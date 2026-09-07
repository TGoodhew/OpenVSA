using System;
using System.Globalization;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 1: extract the Search Length window from Main Time.
    /// </summary>
    /// <remarks>
    /// The first step is a slice, and it is a step of its own because everything after it works on
    /// the window rather than on the record. <c>REQ-DEM-033</c> owns what Search Length may be set
    /// to and how it interacts with the acquisition; this takes the setting as given.
    /// </remarks>
    internal sealed class SearchWindowStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.SearchWindow;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            int total = context.MainTime.Length / 2;
            int start = context.Settings.SearchStartSample;

            if (start >= total)
            {
                throw new ArgumentException(
                    "The Search Length window starts at sample " +
                    start.ToString(CultureInfo.InvariantCulture) + " of a record that holds " +
                    total.ToString(CultureInfo.InvariantCulture) + ".");
            }

            int available = total - start;

            // REQ-DEM-033 states the Search Length in symbols; this is where the sample rate is
            // known, so this is where it becomes a number of samples.
            double perSymbol = context.Settings.SymbolRateHz <= 0.0
                ? 0.0
                : context.SampleRateHz / context.Settings.SymbolRateHz;

            int wanted = context.Settings.SearchLengthSymbols <= 0 || perSymbol <= 0.0
                ? 0
                : (int)Math.Round(context.Settings.SearchLengthSymbols * perSymbol);
            int length = wanted == 0 ? available : Math.Min(wanted, available);

            if (wanted > available)
            {
                context.Note(
                    "The Search Length window asked for " +
                    wanted.ToString(CultureInfo.InvariantCulture) + " samples -- " +
                    context.Settings.SearchLengthSymbols.ToString(CultureInfo.InvariantCulture) +
                    " symbols -- and Main Time had " +
                    available.ToString(CultureInfo.InvariantCulture) +
                    " left. The window was shortened to what was there.");
            }

            // Widened here, where the window's length is known, rather than on the way in.
            var search = new double[2 * length];

            // REQ-DEM-035: mirroring the spectrum is conjugating the waveform, and it happens here
            // because here is before anything has read it. The quadrature part is negated as the
            // samples are widened, which costs nothing over the copy that was happening anyway.
            double sense = context.Settings.MirrorSpectrum ? -1.0 : 1.0;

            for (int sample = 0; sample < length; sample++)
            {
                search[2 * sample] = context.MainTime[(2 * start) + (2 * sample)];
                search[(2 * sample) + 1] =
                    sense * context.MainTime[(2 * start) + (2 * sample) + 1];
            }

            context.Search = search;
            context.SearchStartSample = start;

            return StepOutcome.Continue;
        }
    }

    /// <summary>
    /// Step 7: position the Result Length window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where the window goes is whatever the steps before it found: the sync pattern's position if
    /// step 6 ran and found one, the burst's start if step 2 did, and otherwise far enough into the
    /// waveform to clear the measurement filter's transient. Positioning on that transient is the
    /// classic way to measure an EVM that is really a filter edge, and it costs one filter length
    /// of signal to avoid.
    /// </para>
    /// <para>
    /// The window carries a margin either side of the symbols it holds. Step 8 reads the waveform
    /// at fractional positions through an interpolator with a kernel of its own, and a window cut
    /// exactly to the first and last symbol would have the interpolator reading zeros beyond the
    /// ends — which appears in the result as EVM on the edge symbols and nowhere else.
    /// </para>
    /// </remarks>
    internal sealed class ResultWindowStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.ResultWindow;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] working = DemodContext.Require(
                context.Working, DemodStep.Resample, DemodStep.ResultWindow);

            int perSymbol = context.Settings.PointsPerSymbol;
            int margin = perSymbol + Interpolator.HalfLength + 2;
            int samples = Iq.Count(working);

            int first = FirstSymbolSample(context, perSymbol);
            int begin = Math.Max(0, first - margin);
            int offset = first - begin;

            int room = samples - begin - offset - margin;
            int available = room <= 0 ? 0 : (room / perSymbol) + 1;
            int wanted = context.Settings.ResultLengthSymbols;
            int count = Math.Min(wanted, available);

            if (count < 4)
            {
                throw new ArgumentException(
                    "The Result Length window has room for " +
                    count.ToString(CultureInfo.InvariantCulture) +
                    " symbol(s). A block estimate needs at least four, so this record is too " +
                    "short for these settings.");
            }

            string advice = context.Settings.ResultLengthAdvice;

            if (advice != null)
            {
                // REQ-DEM-031: said once, where a caller will see it, rather than left for a user
                // to infer from a measurement that looks like a bad signal.
                context.Note(advice);
            }

            if (count < wanted)
            {
                context.Note(
                    "The Result Length window asked for " +
                    wanted.ToString(CultureInfo.InvariantCulture) + " symbols and the waveform " +
                    "had room for " + count.ToString(CultureInfo.InvariantCulture) +
                    ". The result is the shorter one.");
            }

            int length = ((count - 1) * perSymbol) + 1 + offset + margin;

            if (begin + length > samples)
            {
                length = samples - begin;
            }

            var result = new double[2 * length];

            Array.Copy(working, 2 * begin, result, 0, 2 * length);

            context.Result = result;
            context.ResultStartSample = begin;
            context.ResultSymbolCount = count;
            context.TimingSamples = offset;

            TakePreDemodWindow(context, working, first, count, perSymbol);

            return StepOutcome.Continue;
        }

        /// <summary>
        /// Cuts the pre-demodulation window: 1.2 x the Result Length (<c>REQ-DEM-032</c>).
        /// </summary>
        /// <param name="context">The chain's state.</param>
        /// <param name="working">The waveform at the internal processing rate.</param>
        /// <param name="first">The first symbol instant, in samples of <paramref name="working"/>.</param>
        /// <param name="count">How many symbols the result window actually holds.</param>
        /// <param name="perSymbol">Samples per symbol at the internal rate.</param>
        /// <remarks>
        /// <para>
        /// <strong>Cut unconditionally, and that is the point.</strong> The criterion asks that the
        /// result be identical "whether or not a pre-demodulation trace is displayed", and the way
        /// to make that true is to leave the display no say in it. There is no flag here and no
        /// flag in the settings: every demodulation carries this window, the displays choose what
        /// to draw from a result that already has both, and the two can no more disagree than a
        /// number can differ from itself. A conditional cut would be cheaper on the runs where
        /// nobody looks, and would put the criterion at the mercy of a code path.
        /// </para>
        /// <para>
        /// <strong>Proportional, not padded.</strong> The extra is 0.2 of the symbols actually
        /// analysed, split evenly either side, so the factor holds at every Result Length rather
        /// than at the one it was tuned on. It is taken from the ACHIEVED count rather than the
        /// setting: where a short record forced the result window down, 1.2 x what was analysed is
        /// the honest statement, and 1.2 x what was asked for would describe a window neither trace
        /// has.
        /// </para>
        /// <para>
        /// <strong>Either side by preference, because the transitions are at both ends.</strong> A
        /// burst has a leading ramp and a trailing one, and a window extended only forwards would
        /// show one and hide the other -- which reads as an asymmetric transmitter rather than an
        /// asymmetric window.
        /// </para>
        /// <para>
        /// <strong>But the SPAN is the requirement and the symmetry is only a preference, so what
        /// one end cannot take the other one does.</strong> This is not a corner case: with no sync
        /// pattern and no burst the result window starts at the measurement filter's transient,
        /// which is a few tens of samples into the waveform, and there is nowhere near a tenth of a
        /// Result Length in front of it. Splitting evenly and clamping gave 1.19 at 64 symbols
        /// falling to 1.12 at 384 -- a factor that drifts with the Result Length, which is exactly
        /// what the criterion checks several lengths to catch. Redistributing holds 1.2 wherever
        /// the record has the samples anywhere.
        /// </para>
        /// <para>
        /// Clamped only when the whole record is too short. Then the window takes what there is: a
        /// shorter pre-demodulation trace is a true statement about a short record, and refusing to
        /// produce one would lose the result over a trace that only decorates it.
        /// <see cref="DemodContext.PreDemodSymbolSpan"/> reports what was actually spanned, so a
        /// display never has to assume the factor it asked for is the factor it got.
        /// </para>
        /// </remarks>
        private static void TakePreDemodWindow(
            DemodContext context, double[] working, int first, int count, int perSymbol)
        {
            int samples = Iq.Count(working);
            int last = first + ((count - 1) * perSymbol);

            // THE WHOLE WINDOW IS SIZED, NOT THE PADDING. The Result Length is count symbols, which
            // is count * perSymbol samples; the result window itself holds one sample fewer than
            // that per end symbol, spanning (count - 1) symbols and a sample. Adding 0.2 of a
            // Result Length to THAT gives 1.2 x count minus three quarters of a symbol -- 1.191 at
            // 64 symbols against 1.199 at 384, a factor that creeps with the Result Length for a
            // reason that has nothing to do with the requirement. Asking for 1.2 x count symbols
            // outright and making the padding the remainder gives 1.200 at every length.
            int wanted = (int)Math.Round(1.2 * count * perSymbol);
            int extra = Math.Max(0, wanted - (last - first + 1));

            int before = Math.Min(extra / 2, first);
            int after = Math.Min(extra - before, Math.Max(0, samples - last - 1));

            // Whatever the trailing end could not take, offer back to the leading end, and vice
            // versa. One pass each way is enough: the second offer can only be limited by a bound
            // the first already respected.
            before = Math.Min(before + (extra - before - after), first);
            after = Math.Min(extra - before, Math.Max(0, samples - last - 1));

            int begin = first - before;
            int end = Math.Min(samples, last + after + 1);
            int length = end - begin;

            if (length <= 0)
            {
                return;
            }

            var window = new double[2 * length];

            Array.Copy(working, 2 * begin, window, 0, 2 * length);

            context.PreDemod = window;
            context.PreDemodStartSample = begin;
            context.PreDemodResultOffsetSamples = first - begin;
            context.PreDemodSymbolSpan = (double)length / perSymbol;
        }

        /// <remarks>
        /// <para>
        /// <strong>Three ways of choosing where to start, in the order <c>REQ-DEM-041</c> gives
        /// them.</strong> With a sync pattern found, the pattern positions the window and nothing
        /// else does. Without one but with a pulse found, the window is <em>auto-centred on the
        /// pulse</em> — that requirement's words — so a burst shorter than the record is analysed
        /// where the signal is rather than where the record happens to begin. With neither, the
        /// window starts after the filter's own transient.
        /// </para>
        /// <para>
        /// Centring is not the same as starting at the pulse. A Result Length shorter than the burst
        /// should sit in the middle of it, away from both edges where the transmitter's own ramps
        /// are; a Result Length longer than the burst will overrun it either way, and centring at
        /// least splits the overrun between the two ends instead of putting all of it after.
        /// </para>
        /// </remarks>
        private static int FirstSymbolSample(DemodContext context, int perSymbol)
        {
            if (context.SyncFound)
            {
                // REQ-DEM-040's Search Offset: where the Result Length window sits relative to the
                // START OF THE PATTERN, in symbols, and negative is allowed -- a measurement is
                // often wanted from before the word it synchronised on.
                return context.SyncSampleOffset +
                    (context.Settings.SearchOffsetSymbols * perSymbol);
            }

            int transient = context.Settings.FilterSymbolSpan * perSymbol;

            if (context.BurstFound)
            {
                // The burst was located in the search window, which is at the acquisition's rate;
                // the working waveform is at the internal processing rate. One multiply, and it is
                // written out because a burst position off by the resampling ratio would put the
                // result window on the wrong part of the signal and still demodulate something.
                int burst = (int)Math.Round(context.BurstStartSample * context.ResampleRatio);
                int length = (int)Math.Round(context.BurstLengthSamples * context.ResampleRatio);

                int wanted = ((context.Settings.ResultLengthSymbols - 1) * perSymbol) + 1;
                int centred = burst + ((length - wanted) / 2);

                // Never before the filter's transient: the first symbols of the working waveform
                // are the measurement filter still filling, and a window centred into them would be
                // measuring the filter.
                return Math.Max(transient, centred);
            }

            return transient;
        }
    }
}
