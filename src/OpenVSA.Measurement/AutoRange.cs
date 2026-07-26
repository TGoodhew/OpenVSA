using System;
using System.Globalization;
using OpenVSA.Hal;

namespace OpenVSA.Measurement
{
    /// <summary>
    /// How much room auto-ranging leaves between the signal peak and the reference level
    /// (<c>REQ-ACQ-004</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A band, not a target, and that is what makes it settle.</strong> A rule that aimed
    /// at one headroom figure would move the reference level on every invocation, because no
    /// measured peak lands exactly on it. The band is a dead zone: a peak already inside it is left
    /// alone, so an unchanging signal produces no second adjustment — the criterion
    /// <c>REQ-ACQ-004</c> is explicit about.
    /// </para>
    /// <para>
    /// The band has to be wider than the step the level is moved in, or the very act of adjusting
    /// could land outside it and adjust again. That is checked at construction rather than left to
    /// the caller to get right: with <c>Target ≥ Minimum</c> and <c>Target + Step ≤ Maximum</c>,
    /// quantising upward puts the achieved headroom in <c>[Target, Target + Step)</c>, which is
    /// inside the band by construction.
    /// </para>
    /// </remarks>
    public sealed class HeadroomBand
    {
        /// <summary>Creates a band.</summary>
        /// <param name="minimumDb">Least headroom tolerated, in dB; must not be negative.</param>
        /// <param name="maximumDb">Most headroom tolerated, in dB.</param>
        /// <param name="targetDb">Headroom aimed for when the level is moved, in dB.</param>
        /// <param name="stepDb">Grid the reference level is moved on, in dB; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A value is not finite, or the band is too narrow to settle in.
        /// </exception>
        public HeadroomBand(double minimumDb, double maximumDb, double targetDb, double stepDb)
        {
            Finite(minimumDb, nameof(minimumDb));
            Finite(maximumDb, nameof(maximumDb));
            Finite(targetDb, nameof(targetDb));
            Finite(stepDb, nameof(stepDb));

            if (minimumDb < 0.0)
            {
                // Negative headroom is the peak sitting above the reference level, which is the
                // overload the function exists to escape - not something to aim at.
                throw new ArgumentOutOfRangeException(
                    nameof(minimumDb), minimumDb,
                    "Minimum headroom must not be negative: a peak above the reference level is an overload.");
            }

            if (maximumDb <= minimumDb)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumDb), maximumDb,
                    "The headroom band must have width, or every measurement is out of it.");
            }

            if (!(stepDb > 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stepDb), stepDb, "The reference-level step must be positive.");
            }

            if (targetDb < minimumDb || targetDb + stepDb > maximumDb)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetDb), targetDb,
                    "A target of " + Db(targetDb) + " on a " + Db(stepDb) + " step cannot settle " +
                    "inside " + Db(minimumDb) + " to " + Db(maximumDb) + ": an adjustment lands " +
                    "between the target and one step above it, and that has to be within the band.");
            }

            MinimumDb = minimumDb;
            MaximumDb = maximumDb;
            TargetDb = targetDb;
            StepDb = stepDb;
        }

        /// <summary>
        /// The band used when none is given: 4 dB to 16 dB, aiming at 10 dB, on a 1 dB step.
        /// </summary>
        /// <remarks>
        /// 10 dB of headroom is the working figure for a signal whose peak-to-average is not yet
        /// known — enough for the crest factor of most modulated carriers without throwing away a
        /// decade of dynamic range. The 4 dB floor is deliberately above zero: a peak 1 dB below
        /// the reference level is one drift away from clipping.
        /// </remarks>
        public static HeadroomBand Default { get; } = new HeadroomBand(4.0, 16.0, 10.0, 1.0);

        /// <summary>Least headroom tolerated, in dB.</summary>
        public double MinimumDb { get; }

        /// <summary>Most headroom tolerated, in dB.</summary>
        public double MaximumDb { get; }

        /// <summary>Headroom aimed for when the level is moved, in dB.</summary>
        public double TargetDb { get; }

        /// <summary>Grid the reference level is moved on, in dB.</summary>
        public double StepDb { get; }

        /// <summary>Whether a headroom lies within the band, inclusive.</summary>
        /// <param name="headroomDb">Headroom to test, in dB.</param>
        public bool Contains(double headroomDb) =>
            headroomDb >= MinimumDb && headroomDb <= MaximumDb;

        /// <inheritdoc />
        public override string ToString() => Db(MinimumDb) + " to " + Db(MaximumDb);

        private static string Db(double value) =>
            value.ToString("0.##", CultureInfo.CurrentCulture) + " dB";

        private static void Finite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, value, "A headroom figure must be finite.");
            }
        }
    }

    /// <summary>
    /// Whether auto-ranging can be offered against a front end, and why not if it cannot
    /// (<c>REQ-ACQ-004</c>).
    /// </summary>
    public sealed class AutoRangeAvailability
    {
        private AutoRangeAvailability(bool isAvailable, string explanation)
        {
            IsAvailable = isAvailable;
            Explanation = explanation ?? string.Empty;
        }

        /// <summary>Whether the command can be offered.</summary>
        public bool IsAvailable { get; }

        /// <summary>
        /// Why it cannot be, for the tooltip on the greyed command; empty when it can.
        /// </summary>
        public string Explanation { get; }

        /// <summary>
        /// Whether auto-ranging can be offered against a front end.
        /// </summary>
        /// <param name="capabilities">What the front end declares.</param>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        /// <remarks>
        /// Read from <see cref="IFrontEndCapabilities"/> and nothing else, per <c>REQ-HAL-002</c>.
        /// The second test is not redundant with the first: a front end may control its range and
        /// still declare a single reference level it can sit at, and an auto-range command that can
        /// only ever return the level it was given is the silent no-op the requirement forbids.
        /// </remarks>
        public static AutoRangeAvailability For(IFrontEndCapabilities capabilities)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            if (!capabilities.SupportsInputRangeControl)
            {
                return new AutoRangeAvailability(
                    false,
                    "This source has no input range control, so its reference level cannot be " +
                    "changed to suit the signal.");
            }

            AmplitudeRange range = capabilities.ReferenceLevelRange;

            if (!(range.MaxDbm > range.MinDbm))
            {
                return new AutoRangeAvailability(
                    false,
                    "This source has a single reference level of " +
                    range.MinDbm.ToString("0.##", CultureInfo.CurrentCulture) +
                    " dBm, so there is no other range to move to.");
            }

            return new AutoRangeAvailability(true, string.Empty);
        }

        /// <inheritdoc />
        public override string ToString() =>
            IsAvailable ? "Auto-range available" : "Auto-range unavailable: " + Explanation;
    }

    /// <summary>
    /// What auto-ranging decided, and what to tell the user about it (<c>REQ-ACQ-004</c>).
    /// </summary>
    /// <remarks>
    /// The decision is reported rather than applied. Deciding is arithmetic on a measured peak and
    /// a declared range and can be tested without an instrument; applying means a fresh
    /// <c>Negotiate</c> and <c>ConfigureAsync</c>, which only the caller holding the front end can
    /// do — and which may itself coerce the level, so what was asked for and what arrives are kept
    /// distinct here as everywhere else.
    /// </remarks>
    public sealed class AutoRangeResult
    {
        internal AutoRangeResult(
            double previousReferenceLevelDbm,
            double referenceLevelDbm,
            double peakDbm,
            HeadroomBand band,
            bool limitedByRange,
            string message)
        {
            PreviousReferenceLevelDbm = previousReferenceLevelDbm;
            ReferenceLevelDbm = referenceLevelDbm;
            PeakDbm = peakDbm;
            Band = band;
            LimitedByRange = limitedByRange;
            Message = message;
        }

        /// <summary>The reference level before the decision, in dBm.</summary>
        public double PreviousReferenceLevelDbm { get; }

        /// <summary>The reference level to use, in dBm; unchanged when nothing was needed.</summary>
        public double ReferenceLevelDbm { get; }

        /// <summary>The measured signal peak the decision was made from, in dBm.</summary>
        public double PeakDbm { get; }

        /// <summary>The headroom band that was applied.</summary>
        public HeadroomBand Band { get; }

        /// <summary>Headroom the chosen level leaves, in dB; negative if the peak still overloads.</summary>
        public double HeadroomDb => ReferenceLevelDbm - PeakDbm;

        /// <summary>Whether the level was moved.</summary>
        /// <remarks>
        /// This is what raises the <c>RNG</c> indicator of <c>REQ-UI-007</c>. It is false when the
        /// peak was already inside the band <em>and</em> when the wanted level was not reachable,
        /// so an unchanging signal cannot produce a second indication either way.
        /// </remarks>
        public bool Changed => ReferenceLevelDbm != PreviousReferenceLevelDbm;

        /// <summary>
        /// Whether the front end's reference-level range stopped the wanted level being reached.
        /// </summary>
        public bool LimitedByRange { get; }

        /// <summary>Whether the peak sits above the chosen reference level.</summary>
        public bool IsOverloaded => PeakDbm > ReferenceLevelDbm;

        /// <summary>Whether the chosen level leaves the peak inside the band.</summary>
        public bool IsWithinBand => Band.Contains(HeadroomDb);

        /// <summary>What to tell the user. Never empty.</summary>
        public string Message { get; }

        /// <inheritdoc />
        public override string ToString() => Message;
    }

    /// <summary>
    /// Chooses a reference level that leaves a measured peak within a stated headroom band
    /// (<c>REQ-ACQ-004</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two failure modes are being avoided at once, and they pull in opposite directions. A
    /// reference level below the signal clips it, and the measurement is not merely inaccurate but
    /// wrong in a way that looks plausible — intermodulation products that are not in the signal.
    /// A reference level far above it wastes the converter's range and buries small features in
    /// quantisation noise. Neither is visible on the trace, which is why this is a command rather
    /// than something the user is expected to judge by eye.
    /// </para>
    /// <para>
    /// <strong>It settles.</strong> Three things guarantee it, and all three are needed: the band
    /// is a dead zone so an in-band peak is left alone; the level is quantised upward onto a step
    /// grid so the same peak always yields the same level; and a level that comes back equal to
    /// the current one — because the range clamped it, or the grid rounded to where it already was
    /// — reports no change rather than a change of zero. <see cref="AutoRangeResult.Changed"/> is
    /// therefore false on the second invocation against an unchanging signal, whatever happened on
    /// the first.
    /// </para>
    /// <para>
    /// <strong>One qualification on that.</strong> The level returned is the level to ask for; a
    /// front end may coerce it onto its own grid. Settling then depends on the coerced level still
    /// falling in the band, so the band must be wider than the front end's reference-level
    /// granularity — with <see cref="HeadroomBand.Default"/>, coarser than 6 dB would churn.
    /// No instrument quantises its reference level anywhere near that coarsely, but a caller
    /// narrowing the band should know the constraint exists. Feed the honoured level back in, not
    /// the requested one.
    /// </para>
    /// </remarks>
    public static class AutoRange
    {
        /// <summary>
        /// Chooses the reference level for a measured peak.
        /// </summary>
        /// <param name="capabilities">What the front end declares.</param>
        /// <param name="currentReferenceLevelDbm">The reference level in force, in dBm.</param>
        /// <param name="peakDbm">The measured signal peak, in dBm.</param>
        /// <param name="band">Headroom band; <see cref="HeadroomBand.Default"/> when null.</param>
        /// <returns>The level to use and what to tell the user; never null.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A level is not finite.</exception>
        /// <exception cref="InvalidOperationException">
        /// The front end has no input range control. <c>REQ-ACQ-004</c> requires the function to be
        /// unavailable there rather than silently doing nothing — ask
        /// <see cref="AutoRangeAvailability.For"/> before offering the command, and this is the
        /// backstop for a caller that did not.
        /// </exception>
        public static AutoRangeResult Adjust(
            IFrontEndCapabilities capabilities,
            double currentReferenceLevelDbm,
            double peakDbm,
            HeadroomBand band = null)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            Finite(currentReferenceLevelDbm, nameof(currentReferenceLevelDbm));
            Finite(peakDbm, nameof(peakDbm));

            AutoRangeAvailability availability = AutoRangeAvailability.For(capabilities);

            if (!availability.IsAvailable)
            {
                throw new InvalidOperationException(availability.Explanation);
            }

            HeadroomBand applied = band ?? HeadroomBand.Default;
            double headroom = currentReferenceLevelDbm - peakDbm;

            if (applied.Contains(headroom))
            {
                return new AutoRangeResult(
                    currentReferenceLevelDbm, currentReferenceLevelDbm, peakDbm, applied,
                    limitedByRange: false,
                    message:
                        "Reference level left at " + Dbm(currentReferenceLevelDbm) + ": the peak at " +
                        Dbm(peakDbm) + " is " + Db(headroom) + " below it, inside the " +
                        applied + " headroom band.");
            }

            AmplitudeRange range = capabilities.ReferenceLevelRange;
            double wanted = QuantiseUp(peakDbm + applied.TargetDb, applied.StepDb);
            double chosen = range.Clamp(wanted);
            bool limited = chosen != wanted;

            if (chosen == currentReferenceLevelDbm)
            {
                // Nothing reachable is better than where it already is. Reported as no change, not
                // as a change of zero: REQ-ACQ-004 wants the indication raised when auto-range
                // acts, and moving a level to itself is not acting.
                return new AutoRangeResult(
                    currentReferenceLevelDbm, currentReferenceLevelDbm, peakDbm, applied,
                    limited,
                    message:
                        "Reference level held at " + Dbm(currentReferenceLevelDbm) + ": " +
                        (headroom < applied.MinimumDb
                            ? "the peak at " + Dbm(peakDbm) + " needs " + Db(applied.TargetDb) +
                              " of headroom, and " + Dbm(range.MaxDbm) +
                              " is the highest reference level this source has."
                            : "the peak at " + Dbm(peakDbm) + " leaves " + Db(headroom) +
                              " of headroom, and " + Dbm(range.MinDbm) +
                              " is the lowest reference level this source has."));
            }

            string why = headroom < applied.MinimumDb
                ? "the peak at " + Dbm(peakDbm) + " left only " + Db(headroom) +
                  (headroom < 0.0 ? " — it was over the range" : string.Empty)
                : "the peak at " + Dbm(peakDbm) + " left " + Db(headroom) + " unused";

            string outcome = limited
                ? " That is this source's " +
                  (chosen > currentReferenceLevelDbm ? "highest" : "lowest") +
                  " reference level, leaving " + Db(chosen - peakDbm) + " rather than the " +
                  Db(applied.TargetDb) + " wanted."
                : " The peak now sits " + Db(chosen - peakDbm) + " below it.";

            return new AutoRangeResult(
                currentReferenceLevelDbm, chosen, peakDbm, applied, limited,
                message:
                    "Reference level moved from " + Dbm(currentReferenceLevelDbm) + " to " +
                    Dbm(chosen) + ": " + why + "." + outcome);
        }

        /// <summary>
        /// Rounds a level up onto a step grid.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Upward, not to nearest. Rounding down would spend part of the headroom the band asked
        /// for, and the whole point of the floor is that it is not spent.
        /// </para>
        /// <para>
        /// The tolerance keeps a level already on the grid there. Without it, 1e-16 of
        /// representation error in a value that is arithmetically exactly −10 dBm pushes the
        /// ceiling to −9 dBm, and auto-ranging on a signal that has not moved reports a change.
        /// </para>
        /// </remarks>
        private static double QuantiseUp(double levelDbm, double stepDb)
        {
            double steps = levelDbm / stepDb;
            double rounded = Math.Ceiling(steps - GridTolerance);

            return rounded * stepDb;
        }

        /// <summary>Fraction of a step within which a level counts as already on the grid.</summary>
        private const double GridTolerance = 1e-9;

        private static void Finite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, value, "A level must be finite.");
            }
        }

        private static string Dbm(double value) =>
            value.ToString("0.##", CultureInfo.CurrentCulture) + " dBm";

        private static string Db(double value) =>
            value.ToString("0.##", CultureInfo.CurrentCulture) + " dB";
    }
}
