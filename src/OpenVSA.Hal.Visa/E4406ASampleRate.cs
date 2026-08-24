using System;

namespace OpenVSA.Hal.Visa
{
    /// <summary>
    /// The E4406A's information-bandwidth-to-sample-rate law, measured rather than assumed
    /// (<c>REQ-E44-002b</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is a class and not two lines inside the front end.</strong> Two previous
    /// attempts at this relationship were wrong in ways that produced plausible numbers instead of
    /// failures — a linear interpolation that under-reported the rate by nearly six times, and a
    /// "1.5× the bandwidth" ratio that was one measured point at the extreme end of the range read
    /// as a law. Both were unfalsifiable where they lived, because nothing could test a private
    /// interpolation against a table of bench readings. This is a pure function so that the 40-point
    /// sweep of 24 August 2026 can be asserted against it, and so the next person who doubts it can
    /// read the evidence beside the arithmetic.
    /// </para>
    /// <para>
    /// <strong>The law.</strong> The instrument decimates a fixed clock, so its sample period is
    /// always a whole number of 1/15 MHz ticks — verified at all 40 points of the sweep, with tick
    /// counts from 1 to 308 805 across five decades. Writing that count <em>n</em>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>the bandwidth it actually uses is <c>W₁ / n</c>,</description></item>
    /// <item><description>the rate it samples at is <c>Fs_max / n</c>,</description></item>
    /// <item><description>so <c>Fs = (Fs_max / W₁) × W_actual</c> — on this instrument 15 MHz over
    /// 3.1 MHz, or 4.83871× the bandwidth in force.</description></item>
    /// </list>
    /// <para>
    /// A commanded bandwidth is rounded <strong>up</strong> to the nearest available step, which is
    /// what makes <em>n</em> a floor rather than a round: every one of the sweep's bisected
    /// boundaries sat just above the coarser step's own actual bandwidth — the 5 to 7.5 MS/s step at
    /// 1.0368 MHz commanded where the coarser step's bandwidth is 1.0333 MHz, and the 7.5 to 15 MS/s
    /// step at 1.5578 MHz where it is 1.55 MHz.
    /// </para>
    /// <para>
    /// <strong>Above <c>W₁</c> the rate clamps and the filter goes on widening alone.</strong> At and
    /// beyond 3.1 MHz of actual bandwidth this instrument stays at 15 MS/s while offering wider
    /// filters — 3.1, 6.7 and 10 MHz were seen — so the oversampling ratio falls to 2.24 and then to
    /// 1.5 at the very top. That top point is the whole of what deviation 3 in
    /// <c>docs/INSTRUMENT-FIRMWARE-DEVIATIONS.md</c> measured, which is why its ratio did not
    /// generalise. <strong>It also means a wide span buys bandwidth and not samples a symbol:</strong>
    /// anything above about 1.56 MHz commanded gets the same 15 MS/s, so the widest span is the right
    /// choice only until the signal fits.
    /// </para>
    /// <para>
    /// <strong>Two claims of different strengths, and it matters which is which.</strong>
    /// <c>Fs = 4.83871 × W_actual</c> is <em>exact</em>: across all 40 points the ratio of rate to
    /// actual bandwidth took three values and no others — 4.83871 wherever the bandwidth was 3.1 MHz
    /// or narrower, then 2.2388 and 1.5 at the two wider filters in the clamped region. Predicting
    /// <c>W_actual</c> from a <em>commanded</em> bandwidth is weaker, because the instrument's own
    /// step list is only approximately <c>W₁/n</c> for integer <em>n</em>.
    /// </para>
    /// <para>
    /// <strong>Measured accuracy of that prediction: exact at and above 17 kHz commanded, and never
    /// worse than 1.40 % below it.</strong> The instrument chose 3 094 ticks at 1 kHz where this says
    /// 3 100, and 308 805 at 10 Hz where this says 310 000. Forty points also cannot show that every
    /// integer <em>n</em> is available — only that the 35 distinct ones the ladder landed on were —
    /// so the residual is a step list nobody has characterised rather than an error to be tuned out.
    /// A per cent does not matter here: this sizes a block before there is an instrument to ask, and
    /// the front end reads the true period back at every configuration and on every block. Nothing
    /// here replaces that. <c>REQ-HAL-001</c>'s negotiation is what makes a measurement right; this
    /// is what makes a <em>plan</em> sensible before there is anything to negotiate with.
    /// </para>
    /// </remarks>
    public static class E4406ASampleRate
    {
        /// <summary>
        /// The sample rate this instrument is expected to use for a commanded bandwidth.
        /// </summary>
        /// <param name="commandedBandwidthHz">The bandwidth about to be commanded, in hertz.</param>
        /// <param name="referenceBandwidthHz">
        /// The widest bandwidth at which the instrument still samples at its maximum rate — <c>W₁</c>
        /// above, 3.1 MHz on the instrument this was measured on. Measured at connect rather than
        /// assumed, because it is a property of one instrument's decimation chain.
        /// </param>
        /// <param name="maximumSampleRateHz">The instrument's maximum sample rate, in hertz.</param>
        /// <returns>The expected sample rate, in hertz.</returns>
        /// <remarks>
        /// Falls back to the maximum rate when either measured constant is missing, which is what the
        /// old linear interpolation did at the top of its range and the only safe answer when there
        /// is nothing to compute from. Accurate to the last digit measured at and above 17 kHz
        /// commanded, and to 1.40 % below it; see the class remarks for why that residual is left
        /// alone.
        /// </remarks>
        public static double For(
            double commandedBandwidthHz, double referenceBandwidthHz, double maximumSampleRateHz)
        {
            if (!(maximumSampleRateHz > 0.0) || double.IsInfinity(maximumSampleRateHz))
            {
                return 0.0;
            }

            if (!(referenceBandwidthHz > 0.0) || double.IsInfinity(referenceBandwidthHz) ||
                !(commandedBandwidthHz > 0.0) || double.IsInfinity(commandedBandwidthHz))
            {
                return maximumSampleRateHz;
            }

            double steps = Math.Floor(referenceBandwidthHz / commandedBandwidthHz);

            // A commanded bandwidth wider than the reference is in the clamped region, where the
            // instrument widens the filter and leaves the rate alone.
            if (steps < 1.0)
            {
                return maximumSampleRateHz;
            }

            return maximumSampleRateHz / steps;
        }

        /// <summary>
        /// Recovers the reference bandwidth from one reading taken inside the tracking region.
        /// </summary>
        /// <param name="actualBandwidthHz">The bandwidth the instrument reported using, in hertz.</param>
        /// <param name="apertureSeconds">The sample period the instrument reported, in seconds.</param>
        /// <param name="maximumSampleRateHz">The instrument's maximum sample rate, in hertz.</param>
        /// <returns>
        /// <c>W₁</c> in hertz, or zero if the reading cannot yield it.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <c>W₁ = W_actual × n</c>, and <c>n = Fs_max × T_s</c> because the period is the maximum
        /// rate's period times the decimation. So one reading gives it, whatever step that reading
        /// happened to land on — which is the point: nothing here has to know in advance which
        /// bandwidth the instrument would choose.
        /// </para>
        /// <para>
        /// <strong>The reading must come from inside the tracking region</strong>, meaning a commanded
        /// bandwidth comfortably narrower than <c>W₁</c>. Taken from the clamped region it returns the
        /// filter width in force rather than <c>W₁</c> — a 5 MHz command on the measured instrument
        /// yields 6.7 MHz, which is not the constant wanted. The caller picks the bandwidth; see
        /// <c>ProbeCapabilities</c> for the margin it uses and why.
        /// </para>
        /// </remarks>
        public static double ReferenceBandwidthFrom(
            double actualBandwidthHz, double apertureSeconds, double maximumSampleRateHz)
        {
            if (!(actualBandwidthHz > 0.0) || !(apertureSeconds > 0.0) ||
                !(maximumSampleRateHz > 0.0) || double.IsInfinity(actualBandwidthHz) ||
                double.IsInfinity(apertureSeconds) || double.IsInfinity(maximumSampleRateHz))
            {
                return 0.0;
            }

            double steps = Math.Round(maximumSampleRateHz * apertureSeconds);

            if (steps < 1.0)
            {
                return 0.0;
            }

            return actualBandwidthHz * steps;
        }
    }
}
