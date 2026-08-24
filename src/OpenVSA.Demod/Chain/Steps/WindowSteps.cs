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

            for (int index = 0; index < 2 * length; index++)
            {
                search[index] = context.MainTime[(2 * start) + index];
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

            return StepOutcome.Continue;
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
