using System;
using OpenVSA.Demod.Results;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>How an accumulated constellation is painted (<c>REQ-DEM-082</c>).</summary>
    public enum DensityRendering
    {
        /// <summary>
        /// The symbol colour, dimmed towards the floor as a cell's share of the peak falls.
        /// </summary>
        /// <remarks>
        /// Digital persistence as an instrument draws it: one colour, and how bright a cell is says
        /// how recently and how often something was there.
        /// </remarks>
        Intensity = 0,

        /// <summary>The colour map, indexed by a cell's share of the peak.</summary>
        /// <remarks>
        /// The heat map. For a hundred thousand symbols the interesting quantity is how many landed
        /// in a cell rather than that one did, and a single colour cannot say it — every cell in
        /// the cloud is the same colour whether it took one symbol or four thousand.
        /// </remarks>
        HeatMap,
    }

    /// <summary>
    /// The accumulated symbol density behind a constellation (<c>REQ-DEM-082</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Persistence and the heat map are one mechanism, not two.</strong>
    /// <c>REQ-DEM-082</c> asks for "digital persistence with configurable decay" and for "optional
    /// density (heat-map) rendering for large symbol counts", and they are the same buffer read two
    /// ways: how much has landed in each cell, and how much of it survives to the next acquisition.
    /// <see cref="Decay"/> is the second and <see cref="Rendering"/> is the first. Building them
    /// separately would mean two accumulators that have to be kept in step, and the first thing to
    /// go out of step would be which cells they light.
    /// </para>
    /// <para>
    /// <strong>Stateful, and therefore not on the rasteriser.</strong> Everything else about
    /// drawing a constellation is a function of one result. Persistence is a function of every
    /// result so far, so it is an object the display owns and hands to the rasteriser, rather than
    /// a flag on a static method. That also makes it clearable: <see cref="Clear"/> is what a
    /// display calls when the measurement changes and the history stops meaning anything.
    /// </para>
    /// <para>
    /// <strong>Float, not int.</strong> A decayed count is not a count — after a decay of 0.8 a
    /// cell that took one symbol holds 0.8 of one — and rounding it back to an integer would make
    /// a slow decay indistinguishable from none at the bottom of the range, where persistence is
    /// read.
    /// </para>
    /// </remarks>
    public sealed class ConstellationDensity
    {
        /// <summary>Below this a cell is treated as empty and stops being drawn.</summary>
        /// <remarks>
        /// A geometric decay never reaches zero, so without a floor every cell the trace has ever
        /// touched stays lit for ever at a value too small to see but large enough to count as
        /// ink — and the display slowly fills in. A thousandth of one symbol is well below the
        /// dimmest colour any map can show.
        /// </remarks>
        public const double Floor = 1.0e-3;

        private float[] _cells;
        private int _width;
        private int _height;
        private double _decay;

        /// <summary>How the accumulated density is painted.</summary>
        public DensityRendering Rendering { get; set; } = DensityRendering.Intensity;

        /// <summary>
        /// The fraction of a cell's intensity that survives into the next acquisition.
        /// </summary>
        /// <remarks>
        /// Zero is no persistence: each acquisition stands alone, which is what a density heat map
        /// of one result wants. Approaching one is a long tail. One itself is refused — nothing
        /// ever fades, so the display accumulates without limit and stops being a picture of the
        /// signal.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The decay is not in the half-open range zero to one.
        /// </exception>
        public double Decay
        {
            get { return _decay; }

            set
            {
                if (double.IsNaN(value) || value < 0.0 || value >= 1.0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        "A decay is the fraction of a cell that survives an acquisition, so it is " +
                        "at least zero and less than one.");
                }

                _decay = value;
            }
        }

        /// <summary>The colour map a heat map is drawn through (<c>REQ-UI-024</c>).</summary>
        /// <remarks>
        /// The product's own map rather than one of this display's invention: a user who has chosen
        /// how a spectrogram reads density has chosen how a constellation reads it too, and two
        /// maps for one idea is two things to keep in step.
        /// </remarks>
        public SpectrogramColourMap Map { get; set; } = SpectrogramColourMap.Default;

        /// <summary>
        /// How bright a cell holding a single symbol is, as a fraction of the symbol colour.
        /// </summary>
        /// <remarks>
        /// The same argument as the eye's (<c>REQ-DEM-081</c>): a cell one symbol reached is rare,
        /// not absent, and a constellation whose outliers are invisible has hidden the thing an
        /// error display is for.
        /// </remarks>
        public double IntensityFloorFraction { get; set; } = 0.25;

        /// <summary>The most any one cell holds, after the last accumulation.</summary>
        public double Peak { get; private set; }

        /// <summary>How many cells hold anything, after the last accumulation.</summary>
        public int OccupiedCells { get; private set; }

        /// <summary>How many acquisitions have been accumulated since the last clear.</summary>
        public int Acquisitions { get; private set; }

        /// <summary>Forgets everything accumulated.</summary>
        public void Clear()
        {
            if (_cells != null)
            {
                Array.Clear(_cells, 0, _cells.Length);
            }

            Peak = 0.0;
            OccupiedCells = 0;
            Acquisitions = 0;
        }

        /// <summary>What a cell holds.</summary>
        /// <param name="column">The column, measured from the area's left edge.</param>
        /// <param name="row">The row, measured from the area's top edge.</param>
        /// <returns>The intensity, or zero when the cell is outside the accumulated area.</returns>
        public double At(int column, int row)
        {
            if (_cells == null || column < 0 || row < 0 || column >= _width || row >= _height)
            {
                return 0.0;
            }

            return _cells[(row * _width) + column];
        }

        /// <summary>
        /// Decays what is held and adds one acquisition's symbols.
        /// </summary>
        /// <param name="trace">The result.</param>
        /// <param name="extent">The value at the edge of the area, in constellation units.</param>
        /// <param name="area">The area drawn in.</param>
        /// <returns>How many symbols landed inside the area.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="trace"/> is null.</exception>
        /// <remarks>
        /// The decay happens here and not in <see cref="Paint"/>, so that redrawing the same
        /// acquisition — a resize, a colour change, a format switch — does not age the display. A
        /// cell fades once per acquisition, which is what "decay" means.
        /// </remarks>
        public int Accumulate(SymbolTrace trace, double extent, PixelRect area)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            Resize(area);

            if (_cells == null)
            {
                return 0;
            }

            if (_decay <= 0.0)
            {
                Array.Clear(_cells, 0, _cells.Length);
            }
            else
            {
                for (int cell = 0; cell < _cells.Length; cell++)
                {
                    float faded = (float)(_cells[cell] * _decay);

                    _cells[cell] = faded < Floor ? 0.0f : faded;
                }
            }

            int landed = 0;

            for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
            {
                ConstellationPoint measured = trace.Measured[symbol];

                int x = ConstellationRasterizer.XFor(measured.I, extent, area);
                int y = ConstellationRasterizer.YFor(measured.Q, extent, area);

                if (!area.Contains(x, y))
                {
                    continue;
                }

                _cells[((y - area.Y) * _width) + (x - area.X)] += 1.0f;
                landed++;
            }

            Acquisitions++;

            Measure();

            return landed;
        }

        /// <summary>
        /// Paints what is held onto a surface.
        /// </summary>
        /// <param name="surface">The surface to draw on.</param>
        /// <param name="area">The area drawn in; the one accumulated into.</param>
        /// <param name="symbol">The symbol colour, for <see cref="DensityRendering.Intensity"/>.</param>
        /// <returns>How many cells were painted.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
        public int Paint(PixelSurface surface, PixelRect area, PlotColor symbol)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            if (_cells == null || Peak <= 0.0)
            {
                return 0;
            }

            int painted = 0;

            for (int row = 0; row < _height; row++)
            {
                for (int column = 0; column < _width; column++)
                {
                    double held = _cells[(row * _width) + column];

                    if (held <= 0.0)
                    {
                        continue;
                    }

                    surface.SetPixel(area.X + column, area.Y + row, ColourFor(held, symbol));
                    painted++;
                }
            }

            return painted;
        }

        /// <summary>The colour a cell holding a given intensity takes.</summary>
        /// <param name="held">What the cell holds.</param>
        /// <param name="symbol">The symbol colour.</param>
        /// <remarks>
        /// Square-rooted for the same reason the eye's shading is (<c>REQ-DEM-081</c>): the centre
        /// of a constellation cloud is reached thousands of times and its tail once, so a linear
        /// ramp puts every symbol that matters against the floor. Monotonic either way — more in a
        /// cell is never a dimmer or a lower-mapped colour.
        /// </remarks>
        public PlotColor ColourFor(double held, PlotColor symbol)
        {
            if (Peak <= 0.0 || held <= 0.0)
            {
                return symbol;
            }

            double share = Math.Sqrt(Math.Min(held, Peak) / Peak);

            if (Rendering == DensityRendering.HeatMap)
            {
                SpectrogramColourMap map = Map ?? SpectrogramColourMap.Default;

                return map.At(share);
            }

            double floor = IntensityFloorFraction;

            if (floor < 0.0)
            {
                floor = 0.0;
            }
            else if (floor > 1.0)
            {
                floor = 1.0;
            }

            double weight = floor + ((1.0 - floor) * share);

            return new PlotColor(
                Channel(symbol.R, weight), Channel(symbol.G, weight), Channel(symbol.B, weight),
                symbol.A);
        }

        private static byte Channel(byte value, double weight)
        {
            int scaled = (int)Math.Round(value * weight);

            return (byte)(scaled < 0 ? 0 : scaled > 255 ? 255 : scaled);
        }

        private void Resize(PixelRect area)
        {
            if (area.Width <= 0 || area.Height <= 0)
            {
                _cells = null;
                _width = 0;
                _height = 0;
                return;
            }

            if (_cells != null && _width == area.Width && _height == area.Height)
            {
                return;
            }

            // The history is in display cells, so it does not survive a change of shape: a cell of
            // the old size is not a cell of the new one, and carrying the numbers across would draw
            // a persistence trail that was never measured at those coordinates.
            _cells = new float[area.Width * area.Height];
            _width = area.Width;
            _height = area.Height;
            Acquisitions = 0;
        }

        private void Measure()
        {
            double peak = 0.0;
            int occupied = 0;

            for (int cell = 0; cell < _cells.Length; cell++)
            {
                double held = _cells[cell];

                if (held <= 0.0)
                {
                    continue;
                }

                occupied++;

                if (held > peak)
                {
                    peak = held;
                }
            }

            Peak = peak;
            OccupiedCells = occupied;
        }
    }
}
