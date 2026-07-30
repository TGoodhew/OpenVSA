using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenVSA.SoakGate
{
    /// <summary>What the gate concluded about one of the soak's claims.</summary>
    public enum SoakVerdict
    {
        /// <summary>The claim holds, and the run could have shown it false.</summary>
        Passed = 0,

        /// <summary>The claim is false.</summary>
        Failed,

        /// <summary>
        /// The run could not resolve the claim — too few samples, too short, or too noisy.
        /// Reported rather than passed, because a run that could not have seen a leak has not shown
        /// there wasn't one.
        /// </summary>
        Inconclusive,
    }

    /// <summary>One judgement, with the figures behind it.</summary>
    public sealed class SoakFinding
    {
        internal SoakFinding(string claim, string requirement, SoakVerdict verdict, string detail)
        {
            Claim = claim;
            Requirement = requirement;
            Verdict = verdict;
            Detail = detail;
        }

        /// <summary>What was claimed, in words.</summary>
        public string Claim { get; }

        /// <summary>The requirement the claim comes from.</summary>
        public string Requirement { get; }

        /// <summary>The conclusion.</summary>
        public SoakVerdict Verdict { get; }

        /// <summary>The numbers, so a verdict can be argued with.</summary>
        public string Detail { get; }

        /// <summary>
        /// Whether this finding fails the run.
        /// </summary>
        /// <remarks>
        /// <see cref="SoakVerdict.Inconclusive"/> fails. A soak whose samples cannot resolve its own
        /// claims has not met the requirement, and treating it as a pass is how a harness that
        /// quietly measured nothing reports success.
        /// </remarks>
        public bool Fails => Verdict != SoakVerdict.Passed;

        /// <inheritdoc />
        public override string ToString() =>
            Verdict.ToString().ToUpperInvariant().PadRight(12) +
            Requirement.PadRight(13) + Claim + " — " + Detail;
    }

    /// <summary>Everything the gate concluded.</summary>
    public sealed class SoakReport
    {
        internal SoakReport(double hours, int samples, IList<SoakFinding> findings)
        {
            Hours = hours;
            Samples = samples;
            Findings = new ReadOnlyCollection<SoakFinding>(findings);
        }

        /// <summary>How long the run lasted.</summary>
        public double Hours { get; }

        /// <summary>How many samples it took.</summary>
        public int Samples { get; }

        /// <summary>One finding per claim.</summary>
        public IReadOnlyList<SoakFinding> Findings { get; }

        /// <summary>Whether the run passed.</summary>
        public bool Passed => !Findings.Any(f => f.Fails);

        /// <summary>The report as text, for a console and for a build log.</summary>
        public string Render()
        {
            var text = new StringBuilder();

            text.Append("OpenVSA soak (REQ-TST-009): ")
                .Append(Hours.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" hours, ")
                .Append(Samples.ToString(CultureInfo.InvariantCulture))
                .Append(" samples")
                .Append('\n')
                .Append('\n');

            foreach (SoakFinding finding in Findings)
            {
                text.Append("  ").Append(finding).Append('\n');
            }

            int failed = Findings.Count(f => f.Fails);

            text.Append('\n')
                .Append(failed == 0
                    ? "  " + Findings.Count + " of " + Findings.Count + " claims hold."
                    : "  " + failed + " of " + Findings.Count + " claims did not hold.")
                .Append('\n');

            return text.ToString();
        }
    }

    /// <summary>
    /// Judges a soak log against <c>REQ-TST-009</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All the deciding, none of the measuring — the same split as <c>OpenVSA.PerformanceGate</c>,
    /// and for the same reason. Every rule here is arithmetic over a list of samples, so the unit
    /// suite drives each one from both sides in milliseconds: a slow leak that stays under any
    /// plausible ceiling, handles that do not come back, a rate that decays by six per cent. None of
    /// that could be provoked on demand inside a real eight-hour run.
    /// </para>
    /// <para>
    /// A log can also be re-judged after the fact, which matters because the run costs a night: if a
    /// rule turns out to be wrong, the evidence is still on disk.
    /// </para>
    /// </remarks>
    public sealed class EnduranceGate
    {
        /// <summary>The duration the requirement states.</summary>
        public const double RequiredHours = 8.0;

        /// <summary>The rate tolerance the requirement states: within 5 %.</summary>
        public const double RateTolerance = 0.05;

        /// <summary>
        /// How far handle and object counts may end outside their opening range.
        /// </summary>
        /// <remarks>
        /// "Return to their starting range" needs a range, and the starting samples supply it: the
        /// span of the counts seen during the first cycles, widened by this fraction. A fixed number
        /// of handles would be a figure invented here rather than measured, and it would be wrong on
        /// the next machine.
        /// </remarks>
        public const double HandleTolerance = 0.10;

        /// <summary>
        /// Opening minutes excluded from the memory trends.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A shell that has just started is still jitting, filling caches and first-touching pages,
        /// and that growth is not a leak. Fitted from t = 0 it tilts the whole line: the two-minute
        /// rehearsal of this harness reported 19 MiB/hour on a heap that had grown 0.66 MiB in
        /// total, because in a two-minute run the warm-up <em>is</em> the run.
        /// </para>
        /// <para>
        /// Two minutes, and stated in the finding, because it is a discarded window rather than a
        /// threshold: it must be small enough that a leak cannot hide inside it. Over the eight hours
        /// the requirement asks for it is four per cent of the samples; a rehearsal shorter than this
        /// has no samples left and is reported inconclusive, which is what a two-minute run deserves
        /// to be told about a claim over hours.
        /// </para>
        /// </remarks>
        public const double WarmUpMinutes = 2.0;

        /// <summary>Creates a gate.</summary>
        /// <param name="requiredHours">
        /// The duration to insist on. Defaults to <see cref="RequiredHours"/>; parameterised so the
        /// harness can be exercised over minutes without the gate pretending that was a soak.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">The duration is not positive.</exception>
        public EnduranceGate(double requiredHours = RequiredHours)
        {
            if (double.IsNaN(requiredHours) || requiredHours <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredHours), requiredHours, "A soak has to last some time.");
            }

            Hours = requiredHours;
        }

        /// <summary>The duration this gate insists on.</summary>
        public double Hours { get; }

        /// <summary>Judges a run.</summary>
        /// <param name="samples">The samples, in the order they were taken.</param>
        /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
        public SoakReport Judge(IEnumerable<SoakSample> samples)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            List<SoakSample> taken = samples.OrderBy(s => s.ElapsedSeconds).ToList();
            double hours = taken.Count == 0 ? 0.0 : taken[taken.Count - 1].ElapsedHours;

            var findings = new List<SoakFinding>
            {
                RanLongEnough(taken, hours),
                ManagedMemoryIsBounded(taken),
                UnmanagedMemoryIsBounded(taken),
                HandlesReturn(taken, "Handles", s => s.Handles),
                HandlesReturn(taken, "GDI objects", s => s.GdiObjects),
                HandlesReturn(taken, "USER objects", s => s.UserObjects),
                RateHeldUp(taken, hours),
                PooledBuffersDoNotGrow(taken),
                DroppedFramesAreReported(taken),
            };

            return new SoakReport(hours, taken.Count, findings);
        }

        private SoakFinding RanLongEnough(List<SoakSample> taken, double hours)
        {
            // One sample an hour would satisfy any duration check and resolve nothing, so the
            // sample count is part of the same claim rather than left implicit.
            int wanted = (int)Math.Ceiling(Hours * 12.0);

            if (hours + 1e-9 < Hours)
            {
                return new SoakFinding(
                    "The soak ran at least " + Duration(),
                    "REQ-TST-009",
                    SoakVerdict.Failed,
                    "it ran " + hours.ToString("0.00", CultureInfo.InvariantCulture) + " hours");
            }

            if (taken.Count < wanted)
            {
                return new SoakFinding(
                    "The soak ran at least " + Duration(),
                    "REQ-TST-009",
                    SoakVerdict.Inconclusive,
                    "it ran " + hours.ToString("0.00", CultureInfo.InvariantCulture) +
                    " hours but took only " + taken.Count + " samples, fewer than the " + wanted +
                    " a trend over that time needs");
            }

            return new SoakFinding(
                "The soak ran at least " + Duration(),
                "REQ-TST-009",
                SoakVerdict.Passed,
                hours.ToString("0.00", CultureInfo.InvariantCulture) + " hours over " +
                taken.Count + " samples, with " + taken[taken.Count - 1].Cycles +
                " create-and-destroy cycles");
        }

        private static SoakFinding ManagedMemoryIsBounded(List<SoakSample> taken)
        {
            // Only the samples that forced a collection. An uncollected heap rises and falls by
            // design, and a trend through it measures the sawtooth rather than the floor.
            List<SoakSample> collected = WarmedUp(taken).Where(s => s.Collected).ToList();

            return MemoryFinding(
                "Managed memory is bounded on a trend, not a ceiling",
                collected,
                s => s.CollectedManagedBytes,
                collected.Count == 0
                    ? "no sample after the first " +
                      WarmUpMinutes.ToString("0.#", CultureInfo.InvariantCulture) +
                      " minutes forced a collection, so there is no floor to fit a line through"
                    : null);
        }

        private static SoakFinding UnmanagedMemoryIsBounded(List<SoakSample> taken) =>
            MemoryFinding(
                "Unmanaged memory is bounded on a trend, not a ceiling",
                WarmedUp(taken),
                s => s.PrivateBytes,
                null);

        /// <summary>The samples after the warm-up window.</summary>
        /// <remarks>See <see cref="WarmUpMinutes"/> for why any are discarded at all.</remarks>
        private static List<SoakSample> WarmedUp(List<SoakSample> taken) =>
            taken.Where(s => s.ElapsedSeconds >= WarmUpMinutes * 60.0).ToList();

        private static SoakFinding MemoryFinding(
            string claim, List<SoakSample> taken, Func<SoakSample, long> read, string cannot)
        {
            if (cannot != null)
            {
                return new SoakFinding(claim, "REQ-TST-009", SoakVerdict.Inconclusive, cannot);
            }

            Trend trend = Trend.Fit(
                taken.Select(s => s.ElapsedHours).ToList(),
                taken.Select(s => (double)read(s)).ToList());

            if (!trend.IsDetermined)
            {
                return new SoakFinding(
                    claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    "only " + trend.Count + " samples after the first " +
                    WarmUpMinutes.ToString("0.#", CultureInfo.InvariantCulture) +
                    " minutes, which cannot determine a slope");
            }

            string figures =
                "slope " + Megabytes(trend.Slope) + "/hour ±" + Megabytes(trend.StandardError) +
                ", from " + Megabytes(trend.First) + " to " + Megabytes(trend.Last) +
                ", over " + trend.Count + " samples after the first " +
                WarmUpMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes";

            return trend.RisesSignificantly()
                ? new SoakFinding(claim, "REQ-TST-009", SoakVerdict.Failed, figures)
                : new SoakFinding(claim, "REQ-TST-009", SoakVerdict.Passed, figures);
        }

        private static SoakFinding HandlesReturn(
            List<SoakSample> taken, string what, Func<SoakSample, int> read)
        {
            string claim = what + " return to their starting range";

            if (taken.Count < 6)
            {
                return new SoakFinding(
                    claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    "only " + taken.Count + " samples");
            }

            if (taken[taken.Count - 1].Cycles < 1)
            {
                // The claim is about repeated creation and destruction. Without a cycle nothing was
                // created or destroyed, and a flat count proves nothing about a leak.
                return new SoakFinding(
                    claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    "no windows or traces were created and destroyed, so nothing was asked of the counts");
            }

            // The opening range: the first tenth of the run, which covers its first cycles.
            int opening = Math.Max(3, taken.Count / 10);
            int low = int.MaxValue;
            int high = int.MinValue;

            for (int i = 0; i < opening; i++)
            {
                int value = read(taken[i]);

                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }

            int allowance = (int)Math.Ceiling(Math.Max(high, 1) * HandleTolerance);
            int ceiling = high + allowance;
            int ending = read(taken[taken.Count - 1]);
            int peak = taken.Max(read);

            string figures =
                "opened " + low + "–" + high + ", ended " + ending + ", peaked " + peak +
                ", allowed up to " + ceiling;

            return ending <= ceiling
                ? new SoakFinding(claim, "REQ-TST-009", SoakVerdict.Passed, figures)
                : new SoakFinding(claim, "REQ-TST-009", SoakVerdict.Failed, figures);
        }

        private SoakFinding RateHeldUp(List<SoakSample> taken, double hours)
        {
            const string Claim = "Update rate over the final hour is within 5 % of the first";

            if (hours < 2.0)
            {
                return new SoakFinding(
                    Claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    "the run lasted " + hours.ToString("0.00", CultureInfo.InvariantCulture) +
                    " hours, so there is no separate first and final hour");
            }

            double first = RateOver(taken, 0.0, 1.0);
            double last = RateOver(taken, hours - 1.0, hours);

            if (double.IsNaN(first) || double.IsNaN(last) || first <= 0.0)
            {
                return new SoakFinding(
                    Claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    "a whole hour's frames could not be counted at one or both ends");
            }

            // Signed so that positive is worse, as the regression gate does: a rate that went up is
            // not a degradation, and collapsing both directions into an absolute difference would
            // fail a run that got faster.
            double change = (first - last) / first;

            string figures =
                "first hour " + first.ToString("0.00", CultureInfo.InvariantCulture) +
                " frames/s, final hour " + last.ToString("0.00", CultureInfo.InvariantCulture) +
                " frames/s, " + (change * 100.0).ToString("0.0", CultureInfo.InvariantCulture) +
                " % slower";

            return change > RateTolerance
                ? new SoakFinding(Claim, "REQ-TST-009", SoakVerdict.Failed, figures)
                : new SoakFinding(Claim, "REQ-TST-009", SoakVerdict.Passed, figures);
        }

        /// <summary>
        /// Frames per second between two elapsed hours, from the cumulative counter.
        /// </summary>
        /// <remarks>
        /// The difference of two readings over the time between them, which is exact. The samples
        /// bounding the window are the ones nearest its edges, and the elapsed time used is theirs
        /// rather than the nominal hour — so a host whose sampling drifted reports the rate it
        /// actually observed.
        /// </remarks>
        private static double RateOver(List<SoakSample> taken, double fromHours, double toHours)
        {
            SoakSample start = null;
            SoakSample end = null;

            foreach (SoakSample sample in taken)
            {
                if (sample.ElapsedHours <= fromHours + 1e-9)
                {
                    start = sample;
                }

                if (sample.ElapsedHours <= toHours + 1e-9)
                {
                    end = sample;
                }
            }

            if (start == null)
            {
                start = taken[0];
            }

            if (end == null || ReferenceEquals(end, start))
            {
                return double.NaN;
            }

            double seconds = end.ElapsedSeconds - start.ElapsedSeconds;

            return seconds <= 0.0 ? double.NaN : (end.FramesDrawn - start.FramesDrawn) / seconds;
        }

        private static SoakFinding PooledBuffersDoNotGrow(List<SoakSample> taken)
        {
            const string Claim = "Pooled buffers show no net growth";

            if (taken.Count < 6)
            {
                return new SoakFinding(
                    Claim, "REQ-NFR-011", SoakVerdict.Inconclusive,
                    "only " + taken.Count + " samples");
            }

            // The second half only, which is what "no net growth" has to mean for this pool.
            // REQ-NFR-011's pool is capped per bucket, so it fills in the first minute of measuring
            // and then stops: fitted over the whole run, that opening step is a positive slope, and
            // failing on it would report the design working as a leak. What must not happen is
            // growth that CONTINUES, and the second half is where that shows.
            List<SoakSample> settled = taken.Skip(taken.Count / 2).ToList();

            Trend trend = Trend.Fit(
                settled.Select(s => s.ElapsedHours).ToList(),
                settled.Select(s => (double)s.PooledBytes).ToList());

            long opening = taken[0].PooledBytes;
            long ending = taken[taken.Count - 1].PooledBytes;
            long peak = taken.Max(s => s.PooledBytes);

            string figures =
                "opened " + Megabytes(opening) + ", ended " + Megabytes(ending) +
                ", peaked " + Megabytes(peak) + "; " + taken[taken.Count - 1].PooledBuffers +
                " buffers retained, slope over the second half " +
                Megabytes(trend.Slope) + "/hour ±" + Megabytes(trend.StandardError);

            if (!trend.IsDetermined)
            {
                return new SoakFinding(
                    Claim, "REQ-NFR-011", SoakVerdict.Inconclusive,
                    figures + " — too few settled samples to determine a slope");
            }

            return trend.RisesSignificantly()
                ? new SoakFinding(Claim, "REQ-NFR-011", SoakVerdict.Failed, figures)
                : new SoakFinding(Claim, "REQ-NFR-011", SoakVerdict.Passed, figures);
        }

        private static SoakFinding DroppedFramesAreReported(List<SoakSample> taken)
        {
            // Reported, not judged: the requirement asks for the counter "at the end rather than
            // checked only for boundedness", and a threshold invented here would be a limit no
            // requirement states. Coalescing is the designed behaviour of REQ-NFR-012.
            const string Claim = "Dropped frames are reported";

            if (taken.Count == 0)
            {
                return new SoakFinding(
                    Claim, "REQ-NFR-012", SoakVerdict.Inconclusive, "no samples");
            }

            SoakSample last = taken[taken.Count - 1];
            long total = last.FramesDrawn + last.FramesDropped;
            double share = total == 0L ? 0.0 : (double)last.FramesDropped / total;

            return new SoakFinding(
                Claim, "REQ-NFR-012", SoakVerdict.Passed,
                last.FramesDropped.ToString("N0", CultureInfo.InvariantCulture) + " dropped of " +
                total.ToString("N0", CultureInfo.InvariantCulture) + " produced (" +
                (share * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + " %), " +
                last.FramesDrawn.ToString("N0", CultureInfo.InvariantCulture) + " drawn");
        }

        /// <summary>The duration this gate insists on, in words.</summary>
        /// <remarks>
        /// Minutes below the hour, because a rehearsal of two minutes read as "at least 0 hours" —
        /// a claim that says nothing and looks like a bug in the gate rather than a short run.
        /// </remarks>
        private string Duration() =>
            Hours >= 1.0
                ? Hours.ToString("0.#", CultureInfo.InvariantCulture) + " hours"
                : (Hours * 60.0).ToString("0.#", CultureInfo.InvariantCulture) + " minutes";

        private static string Megabytes(double bytes) =>
            (bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MiB";
    }
}
