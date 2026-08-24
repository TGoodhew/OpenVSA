using System;
using System.Globalization;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 2: find the first complete pulse in the Search Length window (<c>REQ-DEM-041</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Detection is against the noise floor, not against the peak.</strong> The requirement
    /// puts a number on it — a pulse must be at least 15 dB above the noise — and that is a
    /// statement about two levels in the window rather than about a fraction of the largest one. A
    /// threshold placed some decibels below the peak finds an edge in anything, including a signal
    /// that never stops and a window with no signal at all, which is exactly the "silently
    /// mis-locating" the criterion is written against.
    /// </para>
    /// <para>
    /// <strong>Both levels are read as percentiles.</strong> The tenth of the smoothed power is the
    /// floor and the ninetieth is the pulse. That is robust to a pulse occupying anything between a
    /// tenth and nine tenths of the window; outside that range the two percentiles converge, the
    /// window fails the 15 dB test, and nothing is reported — which is the right answer, because a
    /// window that is all pulse or all silence does not contain a pulse to find. Sizing the window
    /// so that it does is what <c>REQ-DEM-033</c>'s <c>2 × MaxOn + MaxOff</c> is for.
    /// </para>
    /// <para>
    /// <strong>The first COMPLETE pulse.</strong> A window very often opens part-way through a
    /// burst, and that burst's start is not in it. Taking the first sample above the threshold would
    /// centre the result on a fragment; this skips a pulse already in progress and takes the first
    /// one whose rising and falling edges are both inside the window.
    /// </para>
    /// <para>
    /// Only the first, per the requirement. A capture holding several bursts is analysed on one of
    /// them, and which one is not left to whichever happened to be brightest.
    /// </para>
    /// </remarks>
    internal sealed class BurstSearchStep : IChainStep
    {
        /// <summary>How far above the noise floor a pulse must be to count, in decibels.</summary>
        /// <remarks><c>REQ-DEM-041</c>'s own number, and the reason this step has a floor to
        /// measure at all.</remarks>
        private const double DetectionDb = 15.0;

        /// <summary>The shortest run of samples that counts as a pulse, in symbols.</summary>
        private const int MinimumSymbols = 8;

        /// <inheritdoc />
        public DemodStep Step => DemodStep.BurstSearch;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] search = DemodContext.Require(
                context.Search, DemodStep.SearchWindow, DemodStep.BurstSearch);

            int samples = Iq.Count(search);
            double perSymbol = context.SampleRateHz / context.Settings.SymbolRateHz;
            int smoothing = Math.Max(1, (int)Math.Round(perSymbol));

            double[] power = Smoothed(search, samples, smoothing);

            double floor = Percentile(power, 0.10);
            double pulse = Percentile(power, 0.90);

            if (pulse <= 0.0)
            {
                context.Note(
                    "Step 2 found no signal in the Search Length window to search for a pulse in.");

                return StepOutcome.Continue;
            }

            double above = floor <= 0.0
                ? double.PositiveInfinity
                : 10.0 * Math.Log10(pulse / floor);

            if (above < DetectionDb)
            {
                // Reported, not guessed at. The criterion's second half is a burst 10 dB above the
                // noise being "reported as not found, rather than silently mis-locating", and the
                // number it fell short by is the useful part of saying so.
                context.Note(
                    "Step 2 found nothing " +
                    DetectionDb.ToString("0", CultureInfo.InvariantCulture) +
                    " dB above the noise floor: the window's strong and quiet levels differ by " +
                    above.ToString("F1", CultureInfo.InvariantCulture) +
                    " dB (REQ-DEM-041). The Result Length window was left where it was rather than " +
                    "centred on something that may not be a pulse.");

                return StepOutcome.Continue;
            }

            // Half way between the two levels in decibels, which is the level a rising edge crosses
            // once and a falling edge once. Taken in the middle rather than near either, so that
            // neither the floor's ripple nor the pulse's own modulation crosses it repeatedly.
            double threshold = Math.Sqrt(floor * pulse);

            int start;
            int length;

            if (!FirstCompletePulse(power, samples, threshold, out start, out length))
            {
                context.Note(
                    "Step 2 saw a signal " + above.ToString("F1", CultureInfo.InvariantCulture) +
                    " dB above the noise but no pulse that both began and ended inside the Search " +
                    "Length window. REQ-DEM-033's 2 x MaxOn + MaxOff is the length that guarantees " +
                    "one; the window was left where it was.");

                return StepOutcome.Continue;
            }

            int shortest = (int)Math.Round(MinimumSymbols * perSymbol);

            if (length < shortest)
            {
                context.Note(
                    "Step 2 found a pulse of " +
                    (length / perSymbol).ToString("F1", CultureInfo.InvariantCulture) +
                    " symbols, which is shorter than the " +
                    MinimumSymbols.ToString(CultureInfo.InvariantCulture) +
                    " a demodulation can be positioned by. The window was left as it was.");

                return StepOutcome.Continue;
            }

            context.BurstFound = true;
            context.BurstStartSample = start;
            context.BurstLengthSamples = length;

            context.Note(
                "Step 2 found a pulse of " +
                (length / perSymbol).ToString("F1", CultureInfo.InvariantCulture) +
                " symbols, " + above.ToString("F1", CultureInfo.InvariantCulture) +
                " dB above the noise floor, beginning " +
                (start / perSymbol).ToString("F1", CultureInfo.InvariantCulture) +
                " symbols into the Search Length window.");

            return StepOutcome.Continue;
        }

        /// <summary>
        /// Finds the first pulse whose both edges are inside the window.
        /// </summary>
        /// <param name="power">The smoothed power.</param>
        /// <param name="samples">How long it is.</param>
        /// <param name="threshold">The level an edge crosses.</param>
        /// <param name="start">Where the pulse begins.</param>
        /// <param name="length">How long it is, in samples.</param>
        /// <returns>Whether one was found.</returns>
        /// <remarks>
        /// A window that opens part-way through a burst is the common case rather than the awkward
        /// one, so a pulse already in progress at sample zero is skipped: its beginning is not in
        /// the window, and a result centred on what is left would be centred on a fragment.
        /// </remarks>
        private static bool FirstCompletePulse(
            double[] power, int samples, double threshold, out int start, out int length)
        {
            start = 0;
            length = 0;

            int sample = 0;

            // Step over a pulse that was already running when the window opened.
            while (sample < samples && power[sample] >= threshold)
            {
                sample++;
            }

            while (sample < samples && power[sample] < threshold)
            {
                sample++;
            }

            if (sample >= samples)
            {
                return false;
            }

            int rising = sample;

            while (sample < samples && power[sample] >= threshold)
            {
                sample++;
            }

            if (sample >= samples)
            {
                // It never came back down inside the window, so its end is not here either.
                return false;
            }

            start = rising;
            length = sample - rising;

            return true;
        }

        /// <summary>A percentile of the power, by sorting a copy of it.</summary>
        /// <param name="power">The smoothed power.</param>
        /// <param name="fraction">Which percentile, from 0 to 1.</param>
        /// <returns>The value at that point of the distribution.</returns>
        /// <remarks>
        /// Sorted rather than histogrammed: a Search Length window is at most a few hundred thousand
        /// samples and this runs once per demodulation, where the sort is a millisecond and a
        /// histogram would be a bin width to choose and defend.
        /// </remarks>
        private static double Percentile(double[] power, double fraction)
        {
            if (power.Length == 0)
            {
                return 0.0;
            }

            var sorted = new double[power.Length];

            Array.Copy(power, sorted, power.Length);
            Array.Sort(sorted);

            int at = (int)Math.Round(fraction * (sorted.Length - 1));

            if (at < 0)
            {
                at = 0;
            }

            if (at >= sorted.Length)
            {
                at = sorted.Length - 1;
            }

            return sorted[at];
        }

        /// <summary>The signal's power, smoothed over a symbol.</summary>
        /// <param name="search">The search window.</param>
        /// <param name="samples">How long it is.</param>
        /// <param name="window">How many samples to average over.</param>
        /// <returns>One value per sample.</returns>
        /// <remarks>
        /// A symbol's worth, because a modulated signal's instantaneous power passes through zero
        /// between symbols whatever its envelope is doing — a threshold applied to it would find an
        /// edge at every symbol of a burst that never stopped.
        /// </remarks>
        private static double[] Smoothed(double[] search, int samples, int window)
        {
            var power = new double[samples];
            double running = 0.0;

            for (int sample = 0; sample < samples; sample++)
            {
                running += Iq.At(search, sample).MagnitudeSquared;

                if (sample >= window)
                {
                    running -= Iq.At(search, sample - window).MagnitudeSquared;
                }

                power[sample] = running / Math.Min(window, sample + 1);
            }

            return power;
        }
    }
}
