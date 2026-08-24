using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Dsp.Fft;

namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// Works out why a demodulation did not lock (<c>REQ-DEM-036</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Each of the four causes is measured, not listed.</strong> The requirement names four
    /// — wrong symbol rate, wrong filter, centre frequency too far off, Result Length too short —
    /// and asks that each of them, injected deliberately, produce "the corresponding diagnostic
    /// rather than a bare 'demodulation failed'". A diagnostic that recited all four every time
    /// would satisfy the words and none of the intent, so each has its own signature here:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// the symbol-rate line in the signal's squared envelope, against the rate that was supplied;
    /// </description></item>
    /// <item><description>
    /// the bandwidth the signal occupies, against the bandwidth the chosen filter passes;
    /// </description></item>
    /// <item><description>
    /// what step 3 left of the offset — the circular first moment of the derotated window's
    /// spectrum — against the requirement's own ±10 % of the symbol rate;
    /// </description></item>
    /// <item><description>
    /// the Result Length, against the format's own recommendation.
    /// </description></item>
    /// </list>
    /// <para>
    /// <strong>All of it from the search window, not from the fit.</strong> The fit is the thing
    /// that failed: a rate or an offset read out of a decision-directed loop that has converged
    /// onto wrong decisions would be evidence about the loop rather than about the signal.
    /// Everything here is measured on <see cref="DemodContext.Search"/>, which is the acquisition
    /// with step 3's coarse carrier estimate taken out of it and nothing else done to it. That is
    /// the right window for all four: the symbol rate and the bandwidth do not care about a
    /// rotation, and for the carrier it is exactly the quantity wanted — what step 3 LEFT.
    /// </para>
    /// <para>
    /// <strong>The causes are not exclusive and are not meant to be.</strong> A symbol rate wrong by
    /// enough will make the filter look wrong too, because the filter is built at the rate that was
    /// supplied. Reporting both, in the requirement's order of likelihood, says more than choosing
    /// one of them would.
    /// </para>
    /// </remarks>
    internal static class LockDiagnosis
    {
        /// <summary>
        /// The error vector above which a demodulation is treated as not locked, as a percentage.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Twenty-five per cent. A locked demodulation of a clean signal reads a fraction of a per
        /// cent, one through two real instruments about one, and a signal at the edge of usefulness
        /// a few. Decisions landing on the wrong constellation points read tens — the
        /// decision-directed fit converging onto wrong decisions is the 48 % case recorded in
        /// <c>JointRefinementStep</c> — and random decisions higher still.
        /// </para>
        /// <para>
        /// So the gap between "poor" and "not locked" is wide, and this sits in the middle of it.
        /// </para>
        /// </remarks>
        internal const double UnlockedEvmPercent = 25.0;

        /// <summary>How far off centre a carrier may be, as a fraction of the symbol rate.</summary>
        /// <remarks><c>REQ-DEM-036</c>'s "roughly ±10 % of the symbol rate".</remarks>
        internal const double CentreToleranceFraction = 0.10;

        /// <summary>
        /// How far the symbol timing may drift across the Result Length window before the symbol
        /// rate is called wrong, in symbols.
        /// </summary>
        /// <remarks>
        /// A quarter of a symbol, which is the physics rather than a threshold: a rate error moves
        /// the decision instant by the error times the number of symbols, and a decision instant a
        /// quarter of a symbol away from the eye's centre is one the far end of the window is being
        /// read at the wrong moment. Below that the fit absorbs it; above it, it cannot.
        /// </remarks>
        private const double DriftSymbols = 0.25;

        /// <summary>
        /// How far the signal's bandwidth and the filter's may differ before the filter is called
        /// wrong, as a fraction.
        /// </summary>
        /// <remarks>
        /// Twelve per cent. A matched pair reads the same number to within the periodogram's own
        /// noise, because a root-raised-cosine transmitter's power spectrum and a root-raised-cosine
        /// filter's energy spectrum are the same function — so the comparison starts at 1.00 and the
        /// only question is how much slack to leave for a finite window. Root-raised-cosine at 0.05
        /// against a signal shaped at 0.35 differs by about 17 %, which is the smallest mismatch
        /// worth naming, and this sits below it.
        /// </remarks>
        private const double BandwidthTolerance = 0.12;

        /// <summary>How many samples the measurements are taken over, at most.</summary>
        /// <remarks>
        /// Sixty-five thousand, which at four samples a symbol is sixteen thousand symbols and a
        /// symbol-rate line resolved to about a quarter of a part in ten thousand. More would buy
        /// resolution nothing here needs and would cost a transform on every failed demodulation.
        /// </remarks>
        private const int MaximumTransformLength = 65536;

        /// <summary>Whether the peak in the searched band is a line rather than the noise in it.</summary>
        private const double LineToMedian = 4.0;

        /// <summary>
        /// Judges a finished demodulation, and explains a failure.
        /// </summary>
        /// <param name="context">The chain's state, after the passes have run.</param>
        /// <returns>The judgement, and the measurements behind it.</returns>
        internal static LockReport For(DemodContext context)
        {
            DemodSettings settings = context.Settings;
            double symbolRateHz = settings.SymbolRateHz;

            bool locked =
                context.Symbols != null &&
                context.Symbols.Length > 0 &&
                context.EvmPercent < UnlockedEvmPercent;

            Periodogram signal = Complex(context.Search, context.SampleRateHz);

            // Step 3 derotates the search window in place, so what is left in it is what step 8 was
            // asked to pull in -- and that, not where the signal arrived, is what the tolerance is
            // about. A signal step 3 centred correctly is not being failed by its centre frequency
            // however far off it was tuned. Where it arrived is that plus what step 3 took out.
            double residualHz = signal == null ? 0.0 : signal.CentreHz();
            double centreHz = residualHz + context.CoarseFrequencyHz;
            double occupied = signal == null ? 0.0 : signal.WidthHz(signal.CentreBin(), 0.99);
            double measuredRate = MeasuredSymbolRateHz(context);
            double filterBandwidth = FilterBandwidthHz(settings);
            double tolerance = symbolRateHz * CentreToleranceFraction;

            var causes = new List<LockFault>();
            var said = new List<string>();

            if (locked)
            {
                return new LockReport(
                    true, context.EvmPercent, causes, string.Empty, measuredRate, occupied,
                    filterBandwidth, centreHz, residualHz, tolerance);
            }

            // 1. The symbol rate. Two conditions, and both are needed: the error has to be large
            //    enough to break the lock, and larger than this measurement can resolve. Reporting
            //    a rate error smaller than the bin it was measured in would be reporting the bin.
            if (measuredRate > 0.0)
            {
                double error = (measuredRate - symbolRateHz) / symbolRateHz;
                double symbols = Math.Max(1, context.ResultSymbolCount);
                double resolvable = signal == null ? 0.0 : signal.BinHz / symbolRateHz;

                if (Math.Abs(error) * symbols > DriftSymbols && Math.Abs(error) > resolvable)
                {
                    causes.Add(LockFault.SymbolRate);
                    said.Add(
                        "THE SYMBOL RATE looks wrong. The signal's own symbol-rate line sits at " +
                        Rate(measuredRate) + ", not at the " + Rate(symbolRateHz) +
                        " supplied, which is " +
                        (Math.Abs(error) * 1e6).ToString("F0", CultureInfo.InvariantCulture) +
                        " parts per million and drifts the symbol timing by " +
                        (Math.Abs(error) * symbols).ToString("F1", CultureInfo.InvariantCulture) +
                        " symbols across the " +
                        symbols.ToString("F0", CultureInfo.InvariantCulture) +
                        "-symbol Result Length. The symbol rate is supplied and never estimated " +
                        "(REQ-DEM-030), so nothing else in the chain will notice.");
                }
            }

            // 2. The filter, compared like with like: the width the signal occupies against the
            //    width the configured filter passes, both measured the same way.
            if (occupied > 0.0 && filterBandwidth > 0.0)
            {
                double ratio = occupied / filterBandwidth;

                if (ratio > 1.0 + BandwidthTolerance || ratio < 1.0 - BandwidthTolerance)
                {
                    causes.Add(LockFault.Filter);
                    said.Add(
                        "THE FILTER looks wrong. The signal occupies " + Rate(occupied) +
                        " and " + settings.MeasurementPulse + " passes " + Rate(filterBandwidth) +
                        ", a factor of " + ratio.ToString("F2", CultureInfo.InvariantCulture) +
                        ". A measurement filter that does not match the transmitter's shaping " +
                        "leaves intersymbol interference the decisions cannot see past " +
                        "(REQ-DEM-020).");
                }
            }

            // 3. The centre frequency -- judged on what step 3 left, not on where the signal
            //    arrived. See LockReport.ResidualOffsetHz for why those are different questions.
            if (signal != null && Math.Abs(residualHz) > tolerance)
            {
                causes.Add(LockFault.CentreFrequency);
                said.Add(
                    "THE CENTRE FREQUENCY is too far off. The signal's power sits " +
                    Rate(Math.Abs(centreHz)) + " " + (centreHz >= 0.0 ? "above" : "below") +
                    " centre and step 3 took out " + Rate(context.CoarseFrequencyHz) +
                    ", leaving " + Rate(Math.Abs(residualHz)) +
                    " for step 8 to pull in -- and lock needs that within " +
                    CentreToleranceFraction.ToString("P0", CultureInfo.InvariantCulture) +
                    " of the symbol rate, which is " + Rate(tolerance) + " (REQ-DEM-036).");
            }

            // 4. The Result Length, which already knows how to say it.
            string advice = settings.ResultLengthAdvice;

            if (advice != null)
            {
                causes.Add(LockFault.ResultLength);
                said.Add("THE RESULT LENGTH is short for this format. " + advice);
            }

            string opening =
                "The demodulation did not lock: EVM " +
                context.EvmPercent.ToString("F1", CultureInfo.InvariantCulture) +
                " %rms, where anything above " +
                UnlockedEvmPercent.ToString("F0", CultureInfo.InvariantCulture) +
                " means the decisions are landing on the wrong points rather than near the right " +
                "ones. ";

            if (causes.Count == 0)
            {
                return new LockReport(
                    false,
                    context.EvmPercent,
                    causes,
                    opening +
                    "None of the four usual causes is present: the signal's symbol-rate line " +
                    "agrees with the rate supplied, its bandwidth matches the filter, it is " +
                    "centred, and the Result Length suits the format. That makes it something " +
                    "else — a signal that is not the format it is thought to be, or an impairment " +
                    "large enough to move the decisions.",
                    measuredRate,
                    occupied,
                    filterBandwidth,
                    centreHz,
                    residualHz,
                    tolerance);
            }

            return new LockReport(
                false,
                context.EvmPercent,
                causes,
                opening + "In the order they are usually the cause: " +
                string.Join(" ", said.ToArray()),
                measuredRate,
                occupied,
                filterBandwidth,
                centreHz,
                residualHz,
                tolerance);
        }

        /// <summary>A rate or a width, in whichever unit reads as a number a person would say.</summary>
        private static string Rate(double hertz)
        {
            double magnitude = Math.Abs(hertz);

            if (magnitude >= 1e6)
            {
                return (hertz / 1e6).ToString("G4", CultureInfo.InvariantCulture) + " MHz";
            }

            if (magnitude >= 1e3)
            {
                return (hertz / 1e3).ToString("G4", CultureInfo.InvariantCulture) + " kHz";
            }

            return hertz.ToString("G4", CultureInfo.InvariantCulture) + " Hz";
        }

        /// <summary>
        /// The symbol rate the signal is actually running at, in hertz, or zero.
        /// </summary>
        /// <param name="context">The chain's state.</param>
        /// <returns>The rate, or zero when the signal carries no symbol-rate line to read.</returns>
        /// <remarks>
        /// <para>
        /// <strong>From the squared envelope.</strong> A pulse-shaped signal with excess bandwidth
        /// carries a line at the symbol rate in <c>|z|²</c> — the quantity a square-law timing
        /// estimator works on — and its position is the symbol rate whatever the carrier, the
        /// filter and the decisions are doing. That independence is the whole point: this runs when
        /// the fit has failed.
        /// </para>
        /// <para>
        /// <strong>Searched over a wide band, then interpolated.</strong> The band is half to twice
        /// the rate supplied, so an error of tens of per cent is read correctly rather than
        /// wrapping; the peak is then fitted with a parabola over its two neighbours, which places
        /// a strong line to a fraction of a bin.
        /// </para>
        /// <para>
        /// <strong>Nothing is returned when there is no line.</strong> A format with no excess
        /// bandwidth has none, and the largest bin in a band of noise is noise. The peak has to
        /// stand a factor of four above the band's median to be called a line at all.
        /// </para>
        /// </remarks>
        private static double MeasuredSymbolRateHz(DemodContext context)
        {
            Periodogram envelope = Envelope(context.Search, context.SampleRateHz);

            if (envelope == null)
            {
                return 0.0;
            }

            double symbolRateHz = context.Settings.SymbolRateHz;

            int from = (int)Math.Floor(0.5 * symbolRateHz / envelope.BinHz);
            int to = (int)Math.Ceiling(2.0 * symbolRateHz / envelope.BinHz);

            if (from < 1)
            {
                from = 1;
            }

            if (to > (envelope.Power.Length / 2) - 2)
            {
                to = (envelope.Power.Length / 2) - 2;
            }

            if (to <= from + 2)
            {
                return 0.0;
            }

            int peak = from;

            for (int bin = from; bin <= to; bin++)
            {
                if (envelope.Power[bin] > envelope.Power[peak])
                {
                    peak = bin;
                }
            }

            var band = new double[to - from + 1];

            Array.Copy(envelope.Power, from, band, 0, band.Length);
            Array.Sort(band);

            double median = band[band.Length / 2];

            if (median <= 0.0 || envelope.Power[peak] < median * LineToMedian)
            {
                return 0.0;
            }

            double below = envelope.Power[peak - 1];
            double at = envelope.Power[peak];
            double above = envelope.Power[peak + 1];

            double curvature = below - (2.0 * at) + above;
            double shift = curvature == 0.0 ? 0.0 : 0.5 * (below - above) / curvature;

            if (shift < -1.0 || shift > 1.0)
            {
                shift = 0.0;
            }

            return (peak + shift) * envelope.BinHz;
        }

        /// <summary>
        /// The width the configured measurement filter passes, in hertz.
        /// </summary>
        /// <param name="settings">What was asked for.</param>
        /// <returns>The width, or zero when it could not be measured.</returns>
        /// <remarks>
        /// <strong>From the filter's own taps, not from a formula per type.</strong> The taps are
        /// the ones the chain convolves with, truncation and taper included, so the comparison with
        /// the signal is like with like and needs no table of expected bandwidths to be kept in step
        /// with the filter catalogue. A root-raised-cosine transmitter's power spectrum and a
        /// root-raised-cosine filter's energy spectrum are the same function, so a matched pair
        /// reads the same number — which is what makes a ratio of one the meaningful baseline.
        /// </remarks>
        private static double FilterBandwidthHz(DemodSettings settings)
        {
            double[] taps = settings.MeasurementPulse.Taps(
                settings.PointsPerSymbol, settings.FilterSymbolSpan, FilterRole.Measurement);

            if (taps == null || taps.Length == 0)
            {
                return 0.0;
            }

            int length = 1;

            while (length < taps.Length * 8 && length < MaximumTransformLength)
            {
                length *= 2;
            }

            IFftProvider fft = FftProviders.Active;

            if (!fft.SupportsLength(length))
            {
                return 0.0;
            }

            var interleaved = new double[2 * length];

            for (int tap = 0; tap < taps.Length && tap < length; tap++)
            {
                Iq.Set(interleaved, tap, new Iq(taps[tap], 0.0));
            }

            fft.Forward(new Span<double>(interleaved));

            var power = new double[length];

            for (int bin = 0; bin < length; bin++)
            {
                power[bin] = Iq.At(interleaved, bin).MagnitudeSquared;
            }

            // The internal processing rate, which is what the taps are spaced at.
            double rateHz = settings.SymbolRateHz * settings.PointsPerSymbol;
            var spectrum = new Periodogram(power, rateHz / length);

            return spectrum.WidthHz(0, 0.99);
        }

        /// <summary>The windowed power spectrum of a complex waveform.</summary>
        private static Periodogram Complex(double[] iq, double sampleRateHz)
        {
            int length = TransformLength(iq);

            if (length == 0)
            {
                return null;
            }

            var interleaved = new double[2 * length];

            for (int sample = 0; sample < length; sample++)
            {
                double taper = Hann(sample, length);
                Iq value = Iq.At(iq, sample);

                Iq.Set(interleaved, sample, new Iq(value.I * taper, value.Q * taper));
            }

            return Transform(interleaved, length, sampleRateHz);
        }

        /// <summary>The windowed power spectrum of a waveform's squared envelope.</summary>
        /// <remarks>
        /// Real, so its spectrum is symmetric and only the first half is looked at. The mean is
        /// taken out first: the squared envelope's average is the signal's power and is far larger
        /// than the line being looked for, and leaving it in would spread it over the low bins
        /// through the window's own skirts.
        /// </remarks>
        private static Periodogram Envelope(double[] iq, double sampleRateHz)
        {
            int length = TransformLength(iq);

            if (length == 0)
            {
                return null;
            }

            var power = new double[length];
            double mean = 0.0;

            for (int sample = 0; sample < length; sample++)
            {
                power[sample] = Iq.At(iq, sample).MagnitudeSquared;
                mean += power[sample];
            }

            mean /= length;

            var interleaved = new double[2 * length];

            for (int sample = 0; sample < length; sample++)
            {
                Iq.Set(
                    interleaved, sample, new Iq((power[sample] - mean) * Hann(sample, length), 0.0));
            }

            return Transform(interleaved, length, sampleRateHz);
        }

        /// <summary>A Hann window, so that a strong line's own skirts do not become a bandwidth.</summary>
        private static double Hann(int sample, int length) =>
            0.5 * (1.0 - Math.Cos(2.0 * Math.PI * sample / (length - 1)));

        /// <summary>The largest power of two that fits, bounded.</summary>
        private static int TransformLength(double[] iq)
        {
            if (iq == null)
            {
                return 0;
            }

            int samples = Math.Min(Iq.Count(iq), MaximumTransformLength);
            int length = 1;

            while (length * 2 <= samples)
            {
                length *= 2;
            }

            return length < 256 ? 0 : length;
        }

        /// <summary>Transforms a prepared buffer into a periodogram.</summary>
        private static Periodogram Transform(double[] interleaved, int length, double sampleRateHz)
        {
            IFftProvider fft = FftProviders.Active;

            if (!fft.SupportsLength(length))
            {
                return null;
            }

            fft.Forward(new Span<double>(interleaved));

            var power = new double[length];

            for (int bin = 0; bin < length; bin++)
            {
                power[bin] = Iq.At(interleaved, bin).MagnitudeSquared;
            }

            return new Periodogram(power, sampleRateHz / length);
        }

        /// <summary>A power spectrum, and the things asked of one here.</summary>
        private sealed class Periodogram
        {
            internal Periodogram(double[] power, double binHz)
            {
                Power = power;
                BinHz = binHz;
            }

            /// <summary>The power in each bin, in the transform's own bin order.</summary>
            internal double[] Power { get; }

            /// <summary>How much frequency one bin covers.</summary>
            internal double BinHz { get; }

            /// <summary>
            /// Which bin the signal is centred on.
            /// </summary>
            /// <returns>The bin, in the transform's own order.</returns>
            /// <remarks>
            /// <strong>The circular first moment, not an ordinary centroid.</strong> Frequency here
            /// wraps, so an average of bin indices would place a signal straddling the edge of the
            /// span in the middle of it. Summing each bin's power as a unit vector at its own
            /// frequency and taking the angle has no edge to straddle — and it is nearly immune to a
            /// flat noise floor, which contributes vectors in every direction and cancels.
            /// </remarks>
            internal int CentreBin()
            {
                double real = 0.0;
                double imaginary = 0.0;

                for (int bin = 0; bin < Power.Length; bin++)
                {
                    double angle = 2.0 * Math.PI * bin / Power.Length;

                    real += Power[bin] * Math.Cos(angle);
                    imaginary += Power[bin] * Math.Sin(angle);
                }

                double turns = Math.Atan2(imaginary, real) / (2.0 * Math.PI);

                if (turns < 0.0)
                {
                    turns += 1.0;
                }

                int centre = (int)Math.Round(turns * Power.Length);

                return centre % Power.Length;
            }

            /// <summary>How far the signal's centre is from zero, in hertz, signed.</summary>
            internal double CentreHz()
            {
                int centre = CentreBin();
                int signed = centre > Power.Length / 2 ? centre - Power.Length : centre;

                return signed * BinHz;
            }

            /// <summary>
            /// The width holding a fraction of the power, grown outwards from a bin.
            /// </summary>
            /// <param name="centre">The bin to grow from.</param>
            /// <param name="fraction">How much of the power to enclose.</param>
            /// <returns>The width in hertz.</returns>
            /// <remarks>
            /// <para>
            /// <strong>With the noise floor taken out first.</strong> A flat floor spread over the
            /// whole span carries more than one per cent of the total whenever the signal-to-noise
            /// ratio is ordinary, so a ninety-nine per cent bandwidth measured without subtracting
            /// it is a measurement of the span rather than of the signal. The floor is the median of
            /// the fifth of the bins furthest from the centre, which is noise for any signal
            /// narrower than four fifths of the span — and a signal wider than that has no room to
            /// be measured in.
            /// </para>
            /// <para>
            /// <strong>Unless the floor turns out to be the signal.</strong> A filter that passes
            /// everything — <c>PulseFilterType.None</c> is one — has a flat spectrum, and the median
            /// of its outer bins is its passband. Subtracting that would leave nothing to measure
            /// and report a bandwidth of zero for the widest filter in the catalogue, so a
            /// subtraction that removes more than half the power is not made: what was taken for a
            /// floor is the thing being measured.
            /// </para>
            /// </remarks>
            internal double WidthHz(int centre, double fraction)
            {
                int length = Power.Length;
                double floor = Floor(centre);
                var clean = new double[length];
                double total = 0.0;
                double raw = 0.0;

                for (int bin = 0; bin < length; bin++)
                {
                    clean[bin] = Math.Max(0.0, Power[bin] - floor);
                    total += clean[bin];
                    raw += Power[bin];
                }

                if (total < raw * 0.5)
                {
                    Array.Copy(Power, clean, length);
                    total = raw;
                }

                if (total <= 0.0)
                {
                    return 0.0;
                }

                double held = clean[centre];
                int width = 1;

                while (held < total * fraction && width < length)
                {
                    int step = (width + 1) / 2;
                    int below = (((centre - step) % length) + length) % length;
                    int above = (centre + step) % length;

                    held += clean[below] + clean[above];
                    width += 2;
                }

                return width * BinHz;
            }

            /// <summary>The noise floor, as the median of the bins furthest from the centre.</summary>
            private double Floor(int centre)
            {
                int length = Power.Length;
                int wanted = Math.Max(1, length / 5);
                var far = new double[wanted];

                // The bins furthest from the centre in circular distance are the ones around the
                // point half a span away from it.
                int opposite = (centre + (length / 2)) % length;

                for (int index = 0; index < wanted; index++)
                {
                    int bin = (opposite - (wanted / 2) + index + length) % length;

                    far[index] = Power[bin];
                }

                Array.Sort(far);

                return far[far.Length / 2];
            }
        }
    }
}
