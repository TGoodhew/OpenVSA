using System;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// Draws an accumulated history as a time–frequency map (<c>REQ-UI-054</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Frequency across, time up, newest at the top.</strong> The frequency axis is the
    /// same one the spectrum trace uses, so a feature sits in the same column whichever of the two
    /// a user is looking at. Time ascends into the past going down — <c>REQ-UI-063</c>'s own
    /// description of the control is "draw each sweep as a row, oldest at the bottom", so the
    /// newest sweep is at the top, where the spectrum trace's own row would be.
    /// </para>
    /// <para>
    /// <strong>A column takes the largest level under it, not the mean.</strong> A row holds more
    /// bins than the graticule has pixels, and a spectrogram is read to find signals: averaging a
    /// narrow carrier with the noise either side of it buries the thing the display exists to show.
    /// The column boundaries are <see cref="TraceEnvelope"/>'s own, so a carrier lands in the same
    /// column on the map as it does on the trace.
    /// </para>
    /// <para>
    /// <strong>Rows and columns are nearest-neighbour in both directions.</strong> A history deeper
    /// than the display shows one row in every few; a shallower one draws each row as a band. No
    /// interpolation either way: an interpolated spectrogram invents cells between two sweeps that
    /// were never measured, and a user counting rows to time an event would be counting invented
    /// ones.
    /// </para>
    /// </remarks>
    public static class SpectrogramRasterizer
    {
        /// <summary>
        /// Draws the history into a rectangle.
        /// </summary>
        /// <param name="surface">The surface to draw on.</param>
        /// <param name="area">The rectangle to fill; usually the graticule.</param>
        /// <param name="history">The accumulated rows.</param>
        /// <param name="map">The colour map (<c>REQ-UI-024</c>).</param>
        /// <param name="levels">What the map's ends stand for.</param>
        /// <param name="thresholdDbm">Cells at or below this are left as background.</param>
        /// <param name="background">What an undrawn cell shows.</param>
        /// <returns>How many pixels were given a cell's colour.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <remarks>
        /// The returned count is what makes "raising Threshold removes cells below it" assertable
        /// against the rendering rather than only against the model: a threshold that was honoured
        /// in the scaling and ignored here would still pass a test that only counted drawable
        /// cells.
        /// </remarks>
        public static int Render(
            PixelSurface surface,
            PixelRect area,
            Spectrogram history,
            SpectrogramColourMap map,
            SpectrogramLevels levels,
            double thresholdDbm,
            PlotColor background)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            int rows = history.RowCount;

            if (rows == 0 || area.Width <= 0 || area.Height <= 0)
            {
                surface.Fill(area, background);
                return 0;
            }

            // No Fill first. Every row of the area is written whole below, with the background
            // colour standing in for the cells a threshold removed, so filling would be half a
            // million pixel writes discarded a moment later — and on this path that is a quarter of
            // the frame's whole cost.

            // One line of colours per history row, reused for every screen row that shows it, and
            // the column-to-bin boundaries worked out once for the whole render.
            //
            // Both matter. The first implementation computed the boundaries inside the pixel loop,
            // and TraceEnvelope's mapping rounds in floating point when there are fewer points than
            // columns — which a full-width graticule always is at 801 points. A full-depth history
            // took 59 ms a frame; the shell stopped answering UI Automation, which is how it was
            // found. The screen rows that share a history row are contiguous, so remembering the
            // last one is the whole cache.
            var line = new PlotColor[area.Width];
            PlotColor[] entries = Entries(map);
            int[] edges = null;
            int edgesFor = -1;
            int builtRow = -1;
            int paintedPerLine = 0;
            int painted = 0;

            for (int y = 0; y < area.Height; y++)
            {
                int row = RowForY(y, area.Height, rows);

                if (row != builtRow)
                {
                    ReadOnlySpan<float> levelsRow = history.Row(row).LevelsDbm;

                    if (levelsRow.Length != edgesFor)
                    {
                        // A re-plan mid-capture leaves rows of different lengths in one history, so
                        // the boundaries are rebuilt when the length changes rather than once.
                        edges = Edges(levelsRow.Length, area.Width);
                        edgesFor = levelsRow.Length;
                    }

                    paintedPerLine = BuildLine(
                        levelsRow, edges, line, entries, levels, thresholdDbm, background);

                    builtRow = row;
                }

                surface.SetRow(area.X, area.Y + y, line);
                painted += paintedPerLine;
            }

            return painted;
        }

        /// <summary>The first bin of each column, with the bin count as a final boundary.</summary>
        private static int[] Edges(int binCount, int width)
        {
            var edges = new int[width + 1];

            for (int column = 0; column < width; column++)
            {
                edges[column] = TraceEnvelope.IndexFor(column, binCount, width);
            }

            edges[width] = binCount;

            return edges;
        }

        /// <summary>
        /// The colour map's entries as an array.
        /// </summary>
        /// <remarks>
        /// Copied once per render rather than indexed through <c>IReadOnlyList</c> once per column.
        /// The map is immutable, so the copy cannot go stale within a frame, and the interface
        /// indexer is a virtual call in the innermost loop of the display.
        /// </remarks>
        private static PlotColor[] Entries(SpectrogramColourMap map)
        {
            var entries = new PlotColor[map.Count];

            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = map.Entries[i];
            }

            return entries;
        }

        /// <summary>Colours one history row across the whole width, and counts what is drawn.</summary>
        private static int BuildLine(
            ReadOnlySpan<float> levelsRow,
            int[] edges,
            PlotColor[] line,
            PlotColor[] entries,
            SpectrogramLevels levels,
            double thresholdDbm,
            PlotColor background)
        {
            int painted = 0;

            double low = levels.LowDbm;
            double scale = entries.Length / levels.RangeDb;

            for (int column = 0; column < line.Length; column++)
            {
                int first = edges[column];
                int next = edges[column + 1];

                if (next <= first)
                {
                    // One column, one point: interpolation rather than decimation, so this column
                    // stands for a single bin instead of a range of them.
                    next = first + 1;
                }

                double peak = double.NaN;

                for (int bin = first; bin < next && bin < levelsRow.Length; bin++)
                {
                    float level = levelsRow[bin];

                    if (!float.IsNaN(level) && (double.IsNaN(peak) || level > peak))
                    {
                        peak = level;
                    }
                }

                if (SpectrogramScaling.IsDrawn(peak, thresholdDbm))
                {
                    // The same arithmetic SpectrogramColourMap.At does, with the division hoisted
                    // out of the loop. Asserted against At by a test, because two ways of choosing
                    // a colour is exactly the kind of duplication that drifts.
                    int index = (int)((peak - low) * scale);

                    line[column] = entries[
                        index < 0 ? 0 : (index >= entries.Length ? entries.Length - 1 : index)];

                    painted++;
                }
                else
                {
                    // The background rather than a skip: the row is written whole, so an undrawn
                    // cell has to carry the colour it would have been left as.
                    line[column] = background;
                }
            }

            return painted;
        }

        /// <summary>
        /// Which history row a screen row shows.
        /// </summary>
        /// <param name="y">Screen row, 0 at the top of the area.</param>
        /// <param name="height">How many screen rows the area has.</param>
        /// <param name="rowCount">How many rows the history holds.</param>
        /// <returns>A row index, 0 for the oldest.</returns>
        /// <remarks>
        /// Public because it is the half of the geometry a marker has to agree with: the
        /// trace-select marker is drawn at the screen row its selected history row occupies, and a
        /// display and a marker that each did this arithmetic their own way would disagree by a
        /// pixel at some depths and by a row at others.
        /// </remarks>
        public static int RowForY(int y, int height, int rowCount)
        {
            if (rowCount <= 0 || height <= 0)
            {
                return -1;
            }

            // Newest at the top, so the screen runs backwards through the history.
            int fromTop = (int)((long)y * rowCount / height);

            if (fromTop >= rowCount)
            {
                fromTop = rowCount - 1;
            }

            return rowCount - 1 - fromTop;
        }

        /// <summary>
        /// Which screen row a history row is drawn on.
        /// </summary>
        /// <param name="rowIndex">History row, 0 for the oldest.</param>
        /// <param name="height">How many screen rows the area has.</param>
        /// <param name="rowCount">How many rows the history holds.</param>
        /// <returns>A screen row, 0 at the top of the area, or −1 if there is nothing to draw.</returns>
        /// <remarks>
        /// The inverse of <see cref="RowForY"/>, and asserted to be one: a marker drawn where
        /// <see cref="RowForY"/> would not put its row back is a marker sitting beside the data it
        /// claims to select.
        /// </remarks>
        public static int YForRow(int rowIndex, int height, int rowCount)
        {
            if (rowCount <= 0 || height <= 0 || rowIndex < 0 || rowIndex >= rowCount)
            {
                return -1;
            }

            int fromTop = rowCount - 1 - rowIndex;

            // The middle of the band this row occupies, taken from the band's own edges rather than
            // from a proportion of the height. The obvious form — (fromTop·height + height/2)/rows —
            // is not an exact inverse: with a history nearly as deep as the display is tall the
            // bands are a pixel or two high, and the half-band offset rounds into the band above.
            // A trace-select marker one row out is a marker selecting a different sweep from the one
            // it is drawn on.
            int first = (int)(((long)fromTop * height + rowCount - 1) / rowCount);
            int last = (int)((((long)fromTop + 1) * height + rowCount - 1) / rowCount) - 1;

            if (last < first)
            {
                last = first;
            }

            int y = (first + last) / 2;

            return y >= height ? height - 1 : (y < 0 ? 0 : y);
        }

        /// <summary>
        /// Which screen column a bin is drawn in.
        /// </summary>
        /// <param name="binIndex">The bin.</param>
        /// <param name="width">How many screen columns the area has.</param>
        /// <param name="binCount">How many bins a row holds.</param>
        /// <returns>A column, or −1 if there is nothing to draw.</returns>
        /// <remarks>
        /// <para>
        /// <strong>The inverse of the mapping the paint actually uses, found by asking it.</strong>
        /// <see cref="TraceEnvelope.ColumnFor"/> is not that inverse: it and
        /// <see cref="TraceEnvelope.IndexFor"/> disagree by a column at the boundaries — over 401
        /// bins in 64 columns, <c>ColumnFor</c> puts bin 200 in column 31 while column 31's own
        /// range from <c>IndexFor</c> is bins 194 to 199 and bin 200 belongs to column 32.
        /// </para>
        /// <para>
        /// A linear scan, once per redraw, over a few hundred columns. A closed form exists for the
        /// decimating case and does not hold for the interpolating one, and two formulas that have
        /// to agree with a third are how the off-by-one above happened in the first place.
        /// </para>
        /// </remarks>
        public static int ColumnForBin(int binIndex, int width, int binCount)
        {
            if (width <= 0 || binCount <= 0 || binIndex < 0 || binIndex >= binCount)
            {
                return -1;
            }

            for (int column = width - 1; column >= 0; column--)
            {
                if (TraceEnvelope.IndexFor(column, binCount, width) <= binIndex)
                {
                    return column;
                }
            }

            return 0;
        }

    }
}
