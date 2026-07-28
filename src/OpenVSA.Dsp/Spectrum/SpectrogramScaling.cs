using System;
using System.Globalization;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The range of levels a spectrogram's colour map spans (<c>REQ-UI-054</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Here rather than in the renderer, because it is arithmetic over measured levels
    /// rather than over pixels.</strong> Enhance is a statement about the distribution of the
    /// levels in the history — "stretch the map about the busiest ones" — and Threshold is a
    /// statement about which of them are worth drawing at all. Neither needs to know how wide the
    /// display is, and putting them in the rendering layer would have made both untestable without
    /// a window and unreachable from the bench harness.
    /// </para>
    /// <para>
    /// <strong>A level below <see cref="LowDbm"/> is not the same as a level below the
    /// threshold.</strong> The first is drawn in the map's minimum colour; the second is not drawn
    /// at all. That distinction is the whole of the criterion "raising Threshold removes cells
    /// below it" — a threshold that merely darkened them would look like a contrast control.
    /// </para>
    /// </remarks>
    public readonly struct SpectrogramLevels
    {
        /// <summary>
        /// The level a cell must exceed to be drawn at all when no threshold is set.
        /// </summary>
        /// <remarks>
        /// Not zero and not <see cref="double.NegativeInfinity"/>: a spectrogram of a noise floor
        /// holds values down to the floor of the amplitude scale, and a threshold expressed as
        /// "off" has to be a number the comparison can use without a special case at every site.
        /// </remarks>
        public const double NoThresholdDbm = double.NegativeInfinity;

        /// <summary>
        /// The proportion of cells left outside the window at each end by Enhance.
        /// </summary>
        /// <remarks>
        /// Five per cent. The point of Enhance is that a spectrogram of a real signal is nearly all
        /// noise floor with a few very loud cells, so a window taken from the extremes gives almost
        /// the whole map to a range nothing occupies. Clipping the outer twentieth at each end puts
        /// the map over the levels that are actually populated; the loud cells still render, in the
        /// top colour, which is what they would have rendered as anyway.
        /// </remarks>
        public const double EnhanceTailFraction = 0.05;

        /// <summary>Creates a level window.</summary>
        /// <param name="lowDbm">The level the map's first entry stands for.</param>
        /// <param name="highDbm">The level the map's last entry stands for.</param>
        /// <exception cref="ArgumentException">The window has no width, or either end is not finite.</exception>
        public SpectrogramLevels(double lowDbm, double highDbm)
        {
            if (double.IsNaN(lowDbm) || double.IsNaN(highDbm) ||
                double.IsInfinity(lowDbm) || double.IsInfinity(highDbm))
            {
                throw new ArgumentException(
                    "A level window needs two finite levels; it was asked for " +
                    lowDbm.ToString("R", CultureInfo.InvariantCulture) + " to " +
                    highDbm.ToString("R", CultureInfo.InvariantCulture) + " dBm.");
            }

            if (highDbm <= lowDbm)
            {
                throw new ArgumentException(
                    "A level window's top must be above its bottom; it was asked for " +
                    lowDbm.ToString("R", CultureInfo.InvariantCulture) + " to " +
                    highDbm.ToString("R", CultureInfo.InvariantCulture) + " dBm.");
            }

            LowDbm = lowDbm;
            HighDbm = highDbm;
        }

        /// <summary>The level the map's first entry stands for.</summary>
        public double LowDbm { get; }

        /// <summary>The level the map's last entry stands for.</summary>
        public double HighDbm { get; }

        /// <summary>How many decibels the map spans.</summary>
        public double RangeDb => HighDbm - LowDbm;

        /// <summary>
        /// Where a level sits in the window, 0 at the bottom and 1 at the top.
        /// </summary>
        /// <param name="levelDbm">The level.</param>
        /// <returns>A fraction, clamped to 0..1; NaN reads as 0.</returns>
        public double FractionOf(double levelDbm)
        {
            if (double.IsNaN(levelDbm))
            {
                return 0.0;
            }

            double fraction = (levelDbm - LowDbm) / RangeDb;

            return fraction < 0.0 ? 0.0 : (fraction > 1.0 ? 1.0 : fraction);
        }

        /// <inheritdoc />
        public override string ToString() =>
            LowDbm.ToString("0.0", CultureInfo.CurrentCulture) + " to " +
            HighDbm.ToString("0.0", CultureInfo.CurrentCulture) + " dBm";
    }

    /// <summary>
    /// How a spectrogram's levels are mapped onto its colour map (<c>REQ-UI-054</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two controls the requirement names besides the map itself. <strong>Threshold decides
    /// which cells are drawn; Enhance decides what the drawn ones are coloured by.</strong> They
    /// compose: a threshold raised past the bulk of the noise floor changes what Enhance sees, and
    /// so changes the window, which is the behaviour a user manipulating both would expect.
    /// </para>
    /// <para>
    /// <strong>Both are computed over the whole history, not over the newest row.</strong> A window
    /// taken from one row would shift every time a sweep arrived, and a spectrogram whose colours
    /// move under it is unreadable — the same objection that made
    /// <c>SpectrogramColourMap.WithCount</c> discard from the top.
    /// </para>
    /// </remarks>
    public static class SpectrogramScaling
    {
        /// <summary>
        /// The level window for a history, honouring Threshold and Enhance.
        /// </summary>
        /// <param name="history">The accumulated rows.</param>
        /// <param name="thresholdDbm">
        /// Cells at or below this are not drawn; <see cref="SpectrogramLevels.NoThresholdDbm"/>
        /// draws every cell.
        /// </param>
        /// <param name="enhance">Whether to stretch the window about the busiest levels.</param>
        /// <param name="fallback">The window to use when nothing is drawable.</param>
        /// <returns>The window, or <paramref name="fallback"/> when no cell survives the threshold.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="history"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// A history with nothing above the threshold gives the fallback rather than a degenerate
        /// window: raising the threshold above every cell is a legitimate thing to do and it should
        /// leave an empty display, not an exception and not a window of zero width whose reciprocal
        /// is infinite.
        /// </para>
        /// <para>
        /// The same is true of a history whose surviving cells are all at one level — a synthetic
        /// input, or a threshold that has left a single loud carrier. The window is widened to a
        /// decibel around it so that the arithmetic stays finite and the carrier renders as the
        /// top of the map rather than as a division by zero.
        /// </para>
        /// </remarks>
        public static SpectrogramLevels Window(
            Spectrogram history,
            double thresholdDbm,
            bool enhance,
            SpectrogramLevels fallback)
        {
            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            double low;
            double high;
            long count;

            Extremes(history, thresholdDbm, out low, out high, out count);

            if (count == 0)
            {
                return fallback;
            }

            if (enhance && high - low > 0.0)
            {
                Percentiles(history, thresholdDbm, low, high, count, out low, out high);
            }

            if (high - low < 1.0)
            {
                double middle = 0.5 * (low + high);

                low = middle - 0.5;
                high = middle + 0.5;
            }

            return new SpectrogramLevels(low, high);
        }

        /// <summary>
        /// The loudest drawable cell in a history, or NaN when there is none.
        /// </summary>
        /// <param name="history">The accumulated rows.</param>
        /// <exception cref="ArgumentNullException"><paramref name="history"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// What a threshold expressed as "so many decibels below the top" is measured from, and
        /// deliberately <strong>not</strong> the top of the window Enhance produced. Enhance narrows
        /// the window onto the busiest levels, so a threshold relative to <em>that</em> top would
        /// mean something different the moment Enhance was switched on — and on a flat noise floor
        /// the window is a decibel wide, which makes every entry on the ladder a no-op. The
        /// screenshot showed exactly that: Enhance on, threshold at −40 dB, and not one cell
        /// removed.
        /// </para>
        /// <para>
        /// The loudest cell is what a user means by "the top of the map" whatever the display is
        /// currently stretched over.
        /// </para>
        /// </remarks>
        public static double PeakLevelDbm(Spectrogram history)
        {
            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            double low;
            double high;
            long count;

            Extremes(history, SpectrogramLevels.NoThresholdDbm, out low, out high, out count);

            return count == 0 ? double.NaN : high;
        }

        /// <summary>The lowest and highest drawable levels, and how many there are.</summary>
        private static void Extremes(
            Spectrogram history, double thresholdDbm, out double low, out double high, out long count)
        {
            low = double.MaxValue;
            high = double.MinValue;
            count = 0;

            for (int row = 0; row < history.RowCount; row++)
            {
                ReadOnlySpan<float> levels = history.Row(row).LevelsDbm;

                for (int bin = 0; bin < levels.Length; bin++)
                {
                    float level = levels[bin];

                    if (!IsDrawn(level, thresholdDbm))
                    {
                        continue;
                    }

                    if (level < low)
                    {
                        low = level;
                    }

                    if (level > high)
                    {
                        high = level;
                    }

                    count++;
                }
            }
        }

        /// <summary>
        /// The levels the outer twentieth of the cells sit outside, by histogram.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>A histogram rather than a sort, and the reason is measured.</strong> A sorted
        /// copy answers the percentile question exactly, and a bin width is one more number to
        /// defend — but a full-depth history is two hundred rows of eight hundred points, and
        /// sorting a hundred and sixty thousand floats once per acquisition is several milliseconds
        /// of the frame. Two linear passes cost a fraction of that.
        /// </para>
        /// <para>
        /// The bin width follows from the range rather than being chosen: a thousand bins over
        /// whatever the data spans is finer than a decibel on any real display, and the answer is
        /// only used to decide where a colour map starts and stops.
        /// </para>
        /// </remarks>
        private static void Percentiles(
            Spectrogram history,
            double thresholdDbm,
            double low,
            double high,
            long count,
            out double lowerDbm,
            out double upperDbm)
        {
            const int Bins = 1024;

            var histogram = new long[Bins];
            double scale = Bins / (high - low);

            for (int row = 0; row < history.RowCount; row++)
            {
                ReadOnlySpan<float> levels = history.Row(row).LevelsDbm;

                for (int bin = 0; bin < levels.Length; bin++)
                {
                    float level = levels[bin];

                    if (!IsDrawn(level, thresholdDbm))
                    {
                        continue;
                    }

                    int slot = (int)((level - low) * scale);

                    histogram[slot < 0 ? 0 : (slot >= Bins ? Bins - 1 : slot)]++;
                }
            }

            long wantedLow = (long)(count * SpectrogramLevels.EnhanceTailFraction);
            long wantedHigh = (long)(count * (1.0 - SpectrogramLevels.EnhanceTailFraction));

            lowerDbm = low;
            upperDbm = high;

            long running = 0;
            bool foundLow = false;

            for (int slot = 0; slot < Bins; slot++)
            {
                running += histogram[slot];

                if (!foundLow && running >= wantedLow)
                {
                    lowerDbm = low + slot / scale;
                    foundLow = true;
                }

                if (running >= wantedHigh)
                {
                    upperDbm = low + (slot + 1) / scale;
                    break;
                }
            }
        }

        /// <summary>
        /// How many cells a threshold leaves drawable.
        /// </summary>
        /// <param name="history">The accumulated rows.</param>
        /// <param name="thresholdDbm">The threshold.</param>
        /// <exception cref="ArgumentNullException"><paramref name="history"/> is null.</exception>
        /// <remarks>
        /// The criterion "raising Threshold removes cells below it" measured as a number, so a test
        /// and the bench harness can both assert the count falls as the threshold rises without
        /// sampling pixels.
        /// </remarks>
        public static long DrawableCellCount(Spectrogram history, double thresholdDbm)
        {
            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            long drawn = 0;

            for (int row = 0; row < history.RowCount; row++)
            {
                ReadOnlySpan<float> levels = history.Row(row).LevelsDbm;

                for (int bin = 0; bin < levels.Length; bin++)
                {
                    if (IsDrawn(levels[bin], thresholdDbm))
                    {
                        drawn++;
                    }
                }
            }

            return drawn;
        }

        /// <summary>
        /// Whether a cell at this level is drawn.
        /// </summary>
        /// <param name="levelDbm">The cell's level.</param>
        /// <param name="thresholdDbm">The threshold.</param>
        /// <remarks>
        /// <strong>Strictly above</strong>, so that a threshold set to the exact level of a flat
        /// region removes it rather than leaving it drawn at the very bottom of the map — the
        /// boundary a user setting a threshold to "the noise floor" is asking about. NaN is never
        /// drawn: a cell with no value is not a cell at the bottom of the range.
        /// </remarks>
        public static bool IsDrawn(double levelDbm, double thresholdDbm) =>
            !double.IsNaN(levelDbm) && levelDbm > thresholdDbm;

    }
}
