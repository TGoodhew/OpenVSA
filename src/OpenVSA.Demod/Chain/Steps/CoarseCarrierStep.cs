using System;
using System.Globalization;
using OpenVSA.Dsp.Fft;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 3: estimate the carrier offset over the block, and take it out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Block estimation, not a loop.</strong> <c>REQ-DEM-002</c> makes this a design
    /// choice with a stated rationale: steps 3 and 8 fit one solution across the whole block rather
    /// than tracking, so there is no settling transient and every sample contributes. What is
    /// implemented here is the non-data-aided half of that — the modulation is removed by raising
    /// the signal to the power of the constellation's rotational symmetry, which leaves a tone at
    /// that multiple of the carrier offset, and the tone's frequency is read from where it lands in
    /// a transform of the whole window.
    /// </para>
    /// <para>
    /// <strong>A transform, not an average phase advance.</strong> The first form of this step
    /// averaged the phase advance between neighbouring samples of the raised signal, which is the
    /// textbook delay-and-multiply estimator and is a great deal shorter to write. On this signal
    /// it was wrong by 10 kHz in 8 kHz. The reason is that a pulse-shaped signal raised to a power
    /// carries a large self-noise term away from the symbol instants, and averaging a phase mixes
    /// that noise into the answer instead of rejecting it; a transform puts the wanted component in
    /// one bin and the self-noise across all of them, which is what rejecting it looks like. The
    /// peak's position is then interpolated across its neighbours, because a bin is 2 to 3 kHz wide
    /// here and the estimate is worth more than that.
    /// </para>
    /// <para>
    /// <strong>The magnitudes are kept.</strong> Raising the signal to the fourth power weights it
    /// by the fourth power of its envelope, which concentrates the estimate on the samples nearest
    /// the symbol instants — where the phase is a constellation phase and not a transition. That is
    /// a feature of the estimator rather than a defect to normalise away: an earlier version divided
    /// each sample by its magnitude first and gave the noise between symbols an equal vote.
    /// </para>
    /// <para>
    /// <strong>A strong unmodulated spur captures it.</strong> Measured while writing
    /// <c>REQ-DEM-080</c>'s error-vector-spectrum test: a continuous-wave tone 5 % of the signal's
    /// amplitude, offset by a twentieth of the symbol rate, moved this estimate from the carrier to
    /// the spur — 46.8 kHz reported for a signal with no offset at all — and the demodulation that
    /// followed reported 66 % EVM. At 2 % the estimate held to 55 Hz. The mechanism is that the
    /// signal's own contribution to the raised spectrum is spread by its modulation while an
    /// unmodulated tone's is not, so a spur competes on concentration rather than on power.
    /// <c>REQ-DEM-036</c> is where carrier lock tolerance and its diagnostics belong, and this is
    /// the case it will want: the failure is silent, and the number it reports is precise, stable
    /// and wrong.
    /// </para>
    /// <para>
    /// <strong>The unambiguous range.</strong> The transform is of the signal raised to the power of
    /// the symmetry, so an offset is only distinguishable up to the sample rate divided by twice
    /// that power. At the rates this chain works with that is a large fraction of the symbol rate,
    /// and far more than step 8 could then refine. <c>REQ-DEM-036</c> owns what the analyser does
    /// when the offset is larger than the search can reach.
    /// </para>
    /// </remarks>
    internal sealed class CoarseCarrierStep : IChainStep
    {
        /// <summary>
        /// How much of a constellation must survive being raised to its symmetry for this step to
        /// believe the line it finds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Measured, and it sits in a gap two orders of magnitude wide.</strong> Every
        /// format in the catalogue was computed
        /// (<c>evidence/req-dem-011/stripping-quality.txt</c>): the phase-keyed family and the stars
        /// are exactly 1, square QAM 0.43 to 0.52, cross QAM 0.13 to 0.15 — and the multi-ring APSK
        /// constellations of <c>REQ-DEM-011</c> are 0.006 and 0.0005, because their rings' raised
        /// points cancel one another and only the innermost ring survives.
        /// </para>
        /// <para>
        /// Three hundredths is the geometric middle of that gap: a factor of four below the weakest
        /// format this step works for, and a factor of five above the strongest one it does not.
        /// Nothing in the catalogue lies between, which is what makes the threshold a reading of the
        /// formats rather than a number chosen to make a test pass.
        /// </para>
        /// <para>
        /// <strong>Below it, declining beats guessing.</strong> On a 32-APSK with no carrier offset
        /// at all this step reported 64 481 Hz — the tallest line in a spectrum whose carrier line
        /// was five hundred times smaller — and the demodulation that followed recovered 43 symbols
        /// of 512 while reporting that it had converged. Reporting nothing leaves step 8 to find a
        /// carrier it can find, and says so out loud.
        /// </para>
        /// </remarks>
        internal const double MinimumStrippingQuality = 0.03;


        /// <inheritdoc />
        public DemodStep Step => DemodStep.CoarseCarrier;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] search = DemodContext.Require(
                context.Search, DemodStep.SearchWindow, DemodStep.CoarseCarrier);

            int samples = Iq.Count(search);

            int from = context.BurstFound ? context.BurstStartSample : 0;
            int to = context.BurstFound
                ? Math.Min(samples, context.BurstStartSample + context.BurstLengthSamples)
                : samples;

            Constellation constellation = context.Settings.Constellation;

            int order = constellation.RotationalSymmetry;
            int span = to - from;

            double symbolCycles = context.SampleRateHz <= 0.0
                ? 0.0
                : context.Settings.SymbolRateHz / context.SampleRateHz;

            // 🔴 A CONSTANT-ENVELOPE FORMAT CANNOT BE STRIPPED THIS WAY AT ALL, AND ITS POINTS
            // DO NOT SAY SO. StrippingQuality asks what happens to the constellation's POINTS when
            // they are raised to a power, and MSK's points are two: they strip perfectly, and the
            // quality gate below waves the format through. What the estimator actually raises is
            // the WAVEFORM, and between the decision instants an MSK waveform's phase sweeps
            // continuously through a quarter turn a symbol -- it is a frequency modulation, and the
            // fourth power of it is not a carrier line but a pair of lines at plus and minus the
            // symbol rate, which is exactly where this step looks for the envelope's own.
            //
            // Measured on a 1 Msym/s MSK signal with no carrier offset at all: the estimate came
            // back as 250 028.7 Hz -- a quarter of the symbol rate, which is MSK's own deviation --
            // and the demodulation that followed reported 99.9 %rms. A confident, entirely wrong
            // answer, which is the failure this whole step is written to avoid.
            //
            // So the family declines rather than competes. Step 8 estimates the carrier jointly
            // with everything else and can pull in what it starts near; what it cannot do is
            // recover from being handed a quarter of the symbol rate as a starting point.
            //
            // 🔴 TWO ESTIMATORS WERE TRIED IN ITS PLACE AND BOTH ARE WORSE, which is why this
            // declines rather than substituting something. A constant-envelope spectrum is
            // symmetric about its carrier, so its power CENTROID looks like the answer -- and it
            // is the mean instantaneous frequency, which for MSK is a quarter of the symbol rate
            // times the data's own imbalance. Measured on a 4000-symbol block with no carrier
            // offset at all, that came to -2.483 kHz on a 1 Msym/s signal, consistently, and the
            // demodulation that followed read 105 %rms. An estimator that measures the DATA is
            // worse than none: it is wrong on every block rather than on one in twenty-four, and
            // it is wrong in a way that looks like an answer.
            //
            // What would work is the MIDPOINT OF THE TWO DEVIATION HUMPS -- their positions are
            // fixed at plus and minus a quarter of the symbol rate whatever the data does, and only
            // their heights move. #439 carries that, with the block this leaves failing.
            // 🔴 AND A SINGLE-SIDEBAND ONE CANNOT BE STRIPPED EITHER, for a reason of its own.
            // The spectrum of a vestigial-sideband signal is not symmetric about its carrier -- that
            // is the definition of it -- so raising it to a power puts a line where the energy is
            // rather than where the carrier is, and the energy is deliberately all on one side.
            // Measured on a generated 8VSB signal with no carrier offset at all, this step returned
            // 263 248.7 Hz on a 1 Msym/s signal, a quarter of the symbol rate, and the demodulation
            // that followed read 13.2 %rms with 114 of 512 symbols right.
            //
            // A piloted VSB signal does carry a proper answer -- the pilot is a carrier, at the band
            // edge, and finding it is a tone search rather than a power of anything -- but a pilot
            // is optional and an unpiloted signal offers nothing. So this declines for the family
            // and step 8, which fits a frequency on the axis that carries the data, pulls it in.
            if (constellation.Family == ModulationFamily.Vsb)
            {
                context.CoarseFrequencyHz = 0.0;
                context.Search = search;

                context.Note(
                    "Step 3 did not estimate a carrier offset. " + constellation.Name +
                    " suppresses one of its sidebands, so its spectrum is not symmetric about its " +
                    "carrier and raising it to a power finds where the energy is rather than where " +
                    "the carrier is. Step 8 fits the offset on the axis that carries the symbols " +
                    "instead, so a real one must be inside what it can pull in (REQ-DEM-036).");

                return StepOutcome.Continue;
            }

            if (constellation.Family == ModulationFamily.Msk)
            {
                context.CoarseFrequencyHz = 0.0;
                context.Search = search;

                context.Note(
                    "Step 3 did not estimate a carrier offset. " + constellation.Name +
                    " has a constant envelope and a phase that sweeps continuously between symbol " +
                    "instants, so raising it to a power leaves lines at multiples of the symbol " +
                    "rate rather than a carrier line -- the tallest of them is the modulation's " +
                    "own deviation, and reporting it would put the offset a quarter of a symbol " +
                    "rate out. Step 8 starts from no offset instead, so a real one must be inside " +
                    "what it can pull in (REQ-DEM-036).");

                return StepOutcome.Continue;
            }

            double quality = constellation.StrippingQuality;

            if (quality < MinimumStrippingQuality)
            {
                // Nothing to find, so nothing is reported. See MinimumStrippingQuality.
                context.CoarseFrequencyHz = 0.0;
                context.Search = search;

                context.Note(
                    "Step 3 did not estimate a carrier offset. Raising " + constellation.Name +
                    " to the power of " + order.ToString(CultureInfo.InvariantCulture) +
                    " leaves " + quality.ToString("G3", CultureInfo.InvariantCulture) +
                    " of it standing, against the " +
                    MinimumStrippingQuality.ToString("G3", CultureInfo.InvariantCulture) +
                    " this step needs: its rings cancel one another and the carrier's line is not " +
                    "the tallest one in that spectrum. Step 8 therefore starts from no offset, so " +
                    "any real one must be inside what it can pull in (REQ-DEM-036).");

                return StepOutcome.Continue;
            }

            double cyclesPerSample = span < 8
                ? 0.0
                : Estimate(search, from, span, order, symbolCycles);

            context.CoarseFrequencyHz = cyclesPerSample * context.SampleRateHz;
            context.Search = Derotate(search, samples, -cyclesPerSample);

            return StepOutcome.Continue;
        }

        /// <summary>
        /// Reads the carrier offset off the raised signal's spectrum.
        /// </summary>
        /// <param name="search">The search window, interleaved.</param>
        /// <param name="from">Where in it to start.</param>
        /// <param name="span">How many samples to use.</param>
        /// <param name="order">The power the modulation is stripped with.</param>
        /// <param name="symbolCycles">
        /// The symbol rate in cycles per sample, or zero when it is not usable.
        /// </param>
        /// <returns>The offset, in cycles per sample.</returns>
        /// <remarks>
        /// <para>
        /// <strong>The modulation puts lines at every multiple of the symbol rate, and they are not
        /// the carrier.</strong> Raising the signal to a power annihilates the symbols' phases and
        /// leaves the carrier's line at <em>order</em> times the offset — and it also raises the
        /// signal's envelope to that power, and a pulse-shaped envelope is periodic at the symbol
        /// rate. So the transform contains the wanted line near zero and unwanted ones at ±Rs, ±2Rs
        /// and so on, which move with nothing.
        /// </para>
        /// <para>
        /// <strong>Which of them is the tallest depends on the format, and for one of them the
        /// wrong one wins.</strong> Measured on 24 August 2026, as a fraction of the tallest line
        /// in the eighth power: 8PSK's carrier line is 1.00 with its envelope lines at 0.57;
        /// π/4-DQPSK's envelope lines are 1.00 and 0.98 with its carrier line at 0.57. The
        /// alternation between two QPSK sets makes that format's envelope far more strongly
        /// periodic than an eight-point ring's, and the peak search took ±Rs and divided it by
        /// eight — reporting 125.4 kHz of carrier offset on a 1 Msym/s signal that had none, and
        /// demodulating it at 47 % EVM.
        /// </para>
        /// <para>
        /// <strong>So they are excluded by name rather than competed with.</strong> The symbol rate
        /// is supplied exactly (<c>REQ-DEM-030</c>), the envelope's lines are impulses at its
        /// multiples, and the transform's own resolution says how wide an impulse can be. What is
        /// lost is a carrier offset that lands a raised line exactly on one of them — an offset of
        /// Rs/<em>order</em>, which no estimator could have told from the envelope in any case, and
        /// which <c>REQ-DEM-036</c> owns.
        /// </para>
        /// </remarks>
        private static double Estimate(
            double[] search, int from, int span, int order, double symbolCycles)
        {
            int length = TransformLength(span);
            var raised = new double[2 * length];

            for (int sample = 0; sample < span; sample++)
            {
                Iq value = Power(Iq.At(search, from + sample), order);

                Iq.Set(raised, sample, value);
            }

            IFftProvider fft = FftProviders.Active;

            if (!fft.SupportsLength(length))
            {
                return 0.0;
            }

            fft.Forward(new Span<double>(raised));

            // Three times the transform's own resolution either side of a line: the envelope's
            // contribution at a multiple of the symbol rate is an impulse, and an impulse is as
            // wide as the window that measured it. The guard is in cycles per sample, and it is
            // taken from the record's length rather than the padded transform's, because zero
            // padding interpolates a line without narrowing it.
            double guard = 3.0 / span;
            bool notch = symbolCycles > 8.0 * guard;

            int peak = 0;
            double best = -1.0;

            for (int bin = 0; bin < length; bin++)
            {
                if (notch && IsEnvelopeLine(bin, length, symbolCycles, guard))
                {
                    continue;
                }

                double magnitude = Iq.At(raised, bin).MagnitudeSquared;

                if (magnitude > best)
                {
                    best = magnitude;
                    peak = bin;
                }
            }

            double offset = Interpolate(raised, length, peak);

            // Bins above the halfway point are negative frequencies. Reading them as positive ones
            // would turn a small negative offset into an enormous positive one, and the chain would
            // then derotate a clean signal into nonsense.
            double bins = peak + offset;

            if (bins > length / 2.0)
            {
                bins -= length;
            }

            return bins / (length * (double)order);
        }

        /// <summary>Whether a bin sits on one of the envelope's lines rather than the carrier's.</summary>
        /// <param name="bin">Which bin of the transform.</param>
        /// <param name="length">How many bins there are.</param>
        /// <param name="symbolCycles">The symbol rate, in cycles per sample.</param>
        /// <param name="guard">How near a line counts as on it, in cycles per sample.</param>
        /// <returns><c>true</c> at a non-zero multiple of the symbol rate.</returns>
        private static bool IsEnvelopeLine(
            int bin, int length, double symbolCycles, double guard)
        {
            double cycles = bin > length / 2 ? (bin - length) / (double)length : bin / (double)length;
            double multiple = Math.Round(cycles / symbolCycles);

            // Zero is the carrier's own place, and is never excluded.
            return multiple != 0.0 &&
                Math.Abs(cycles - (multiple * symbolCycles)) < guard;
        }

        private static double Interpolate(double[] transform, int length, int peak)
        {
            if (peak <= 0 || peak >= length - 1)
            {
                return 0.0;
            }

            double left = Iq.At(transform, peak - 1).Magnitude;
            double centre = Iq.At(transform, peak).Magnitude;
            double right = Iq.At(transform, peak + 1).Magnitude;

            double denominator = left - (2.0 * centre) + right;

            if (Math.Abs(denominator) < 1e-18)
            {
                return 0.0;
            }

            double offset = 0.5 * (left - right) / denominator;

            return offset < -0.5 || offset > 0.5 ? 0.0 : offset;
        }

        private static int TransformLength(int span)
        {
            // At least twice the data, so the zero padding interpolates the transform and the peak
            // is found on a finer grid than the record length alone would give.
            int length = 16;

            while (length < span * 2)
            {
                length *= 2;
            }

            return length;
        }

        private static Iq Power(Iq value, int order)
        {
            Iq raised = value;

            for (int step = 1; step < order; step++)
            {
                raised = raised * value;
            }

            return raised;
        }

        private static double[] Derotate(double[] interleaved, int samples, double cyclesPerSample)
        {
            var rotated = new double[interleaved.Length];

            for (int sample = 0; sample < samples; sample++)
            {
                Iq value = Iq.At(interleaved, sample);
                Iq turn = Iq.FromPhase(2.0 * Math.PI * cyclesPerSample * sample);

                Iq.Set(rotated, sample, value * turn);
            }

            return rotated;
        }
    }
}
