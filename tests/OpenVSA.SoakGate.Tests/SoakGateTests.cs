using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.SoakGate.Tests
{
    /// <summary>
    /// <c>REQ-TST-009</c>'s rules, each driven from both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this suite exists at all.</strong> The requirement can only be met by a run that
    /// takes a night, and a run that passes proves nothing about the judging: a gate that returned
    /// "passed" unconditionally would look exactly the same. So every rule is fed a synthetic log
    /// that ought to fail it, and one that ought not — the leak that stays under any ceiling, the
    /// handles that never come back, the rate that decays by six per cent. None of those could be
    /// provoked on demand in a real soak.
    /// </para>
    /// <para>
    /// The logs are built from a healthy baseline and then spoiled one property at a time, so a test
    /// that fails names the rule that broke rather than "something about the log".
    /// </para>
    /// </remarks>
    public class SoakGateTests
    {
        private const double Mib = 1024.0 * 1024.0;
        private const int SamplesPerHour = 60;
        private const double WarmUpHours = EnduranceGate.WarmUpMinutes / 60.0;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes the output helper, so a finding's figures are visible.</summary>
        /// <param name="output">Where findings are written.</param>
        public SoakGateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AHealthyEightHourRunPasses()
        {
            SoakReport report = new EnduranceGate().Judge(Healthy());

            _output.WriteLine(report.Render());

            Assert.True(report.Passed, Failures(report));
            Assert.Equal(8.0, report.Hours, 2);
        }

        // ---- Memory: a trend, not a ceiling ------------------------------------------------------

        [Fact]
        public void ASlowLeakThatStaysUnderAnyCeilingFails()
        {
            // The requirement's own words: "measured against a trend line over the run, not merely
            // against a ceiling, so a slow leak that stays under the cap still fails". 12 MiB an
            // hour over 8 hours is under 100 MiB added -- no plausible ceiling would catch it.
            List<SoakSample> leaking = Spoil(
                Healthy(),
                (s, hours) => With(s, collectedManagedBytes: s.CollectedManagedBytes == 0L
                    ? 0L
                    : s.CollectedManagedBytes + (long)(12.0 * Mib * hours)));

            SoakFinding finding = FindingFor(leaking, "Managed memory");

            _output.WriteLine(finding.ToString());

            Assert.Equal(SoakVerdict.Failed, finding.Verdict);
        }

        [Fact]
        public void ASawtoothWithNoRiseDoesNotCountAsALeak()
        {
            // A managed heap rises and falls by design, so any finite series has a non-zero slope.
            // A gate that failed on that would fail every healthy run -- which is the mistake that
            // makes a leak detector useless rather than merely wrong.
            var sawtooth = new List<SoakSample>();

            for (int i = 0; i <= 8 * SamplesPerHour; i++)
            {
                double hours = i / (double)SamplesPerHour;
                bool collects = i % 10 == 0;

                sawtooth.Add(Sample(
                    hours,
                    managed: (long)((120.0 + (60.0 * (i % 7))) * Mib),
                    collected: collects ? (long)((110.0 + (i % 3 == 0 ? 8.0 : 0.0)) * Mib) : 0L,
                    priv: (long)((400.0 + (i % 5 == 0 ? 20.0 : 0.0)) * Mib),
                    handles: 700 + (i % 9),
                    gdi: 300 + (i % 4),
                    user: 200 + (i % 3),
                    frames: (long)(i * 300),
                    dropped: (long)(i * 40),
                    pooledBuffers: 24,
                    pooledBytes: (long)(48.0 * Mib),
                    traces: 1 + (i % 3),
                    cycles: i / 20));
            }

            SoakReport report = new EnduranceGate().Judge(sawtooth);

            _output.WriteLine(report.Render());

            Assert.True(report.Passed, Failures(report));
        }

        [Fact]
        public void AnUnmanagedLeakFailsEvenWhenTheManagedHeapIsFlat()
        {
            // Two claims, not one. A native handle leak or an unreleased bitmap moves private bytes
            // and leaves GC.GetTotalMemory untouched, so a gate that only watched the managed heap
            // would pass the very failure mode a WPF soak is most likely to have.
            List<SoakSample> leaking = Spoil(
                Healthy(),
                (s, hours) => With(s, privateBytes: s.PrivateBytes + (long)(30.0 * Mib * hours)));

            SoakReport report = new EnduranceGate().Judge(leaking);

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Passed, Verdict(report, "Managed memory"));
            Assert.Equal(SoakVerdict.Failed, Verdict(report, "Unmanaged memory"));
        }

        [Fact]
        public void ARunWhereNothingForcedACollectionCannotAnswerTheManagedClaim()
        {
            // Inconclusive, not passed. Without a collected figure there is no floor to fit a line
            // through, and the sawtooth alone cannot tell a leak from garbage.
            List<SoakSample> uncollected =
                Healthy().Select(s => With(s, collectedManagedBytes: 0L)).ToList();

            SoakFinding finding = FindingFor(uncollected, "Managed memory");

            Assert.Equal(SoakVerdict.Inconclusive, finding.Verdict);
            Assert.True(finding.Fails, "An inconclusive claim has to fail the run.");
        }

        // ---- Handles and objects -----------------------------------------------------------------

        [Theory]
        [InlineData("Handles")]
        [InlineData("GDI objects")]
        [InlineData("USER objects")]
        public void CountsThatNeverComeBackFail(string what)
        {
            List<SoakSample> leaking = Spoil(
                Healthy(),
                (s, hours) =>
                {
                    int extra = (int)(40.0 * hours);

                    switch (what)
                    {
                        case "Handles": return With(s, handles: s.Handles + extra);
                        case "GDI objects": return With(s, gdiObjects: s.GdiObjects + extra);
                        default: return With(s, userObjects: s.UserObjects + extra);
                    }
                });

            SoakFinding finding = FindingFor(leaking, what);

            _output.WriteLine(finding.ToString());

            Assert.Equal(SoakVerdict.Failed, finding.Verdict);
        }

        [Fact]
        public void CountsThatPeakDuringTheRunAndComeBackPass()
        {
            // The claim is that they RETURN to their starting range, not that they never rise.
            // Creating twenty trace windows is supposed to cost handles; keeping them afterwards is
            // the fault. A gate that judged the peak would forbid the very thing being exercised.
            List<SoakSample> spiky = Spoil(
                Healthy(),
                (s, hours) => hours > 3.0 && hours < 4.0
                    ? With(s, handles: s.Handles + 400, gdiObjects: s.GdiObjects + 900)
                    : s);

            SoakReport report = new EnduranceGate().Judge(spiky);

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Passed, Verdict(report, "Handles"));
            Assert.Equal(SoakVerdict.Passed, Verdict(report, "GDI objects"));
        }

        [Fact]
        public void ARunThatCreatedAndDestroyedNothingCannotAnswerTheHandleClaim()
        {
            // "after windows and traces are created and destroyed repeatedly" -- with no cycles,
            // a flat handle count is a count nothing was ever asked of.
            List<SoakSample> idle = Healthy().Select(s => With(s, cycles: 0)).ToList();

            SoakFinding finding = FindingFor(idle, "Handles");

            Assert.Equal(SoakVerdict.Inconclusive, finding.Verdict);
            Assert.Contains("created and destroyed", finding.Detail, StringComparison.Ordinal);
        }

        // ---- Update rate -------------------------------------------------------------------------

        [Theory]
        [InlineData(0.02, SoakVerdict.Passed)]
        [InlineData(0.049, SoakVerdict.Passed)]
        [InlineData(0.08, SoakVerdict.Failed)]
        [InlineData(0.30, SoakVerdict.Failed)]
        public void TheRateToleranceIsDrivenFromBothSidesOfFivePerCent(
            double decay, SoakVerdict expected)
        {
            SoakFinding finding = FindingFor(Decaying(decay), "Update rate");

            _output.WriteLine(finding.ToString());

            Assert.Equal(expected, finding.Verdict);
        }

        [Fact]
        public void ARunThatGotFasterIsNotADegradation()
        {
            // Signed, not absolute. A run whose final hour beat its first has not degraded, and
            // failing it would be a gate that only knows the rate changed.
            SoakFinding finding = FindingFor(Decaying(-0.20), "Update rate");

            _output.WriteLine(finding.ToString());

            Assert.Equal(SoakVerdict.Passed, finding.Verdict);
        }

        [Fact]
        public void TheRateIsMeasuredOverWholeHoursRatherThanBetweenTwoSamples()
        {
            // A start-and-end sample is what the requirement says "would miss" degradation. Here the
            // instantaneous rate at both ends is identical and the middle sags for six hours; the
            // hourly figures must still be equal, because they are, and a gate that had latched onto
            // two readings somewhere else would report something different.
            var sagging = new List<SoakSample>();
            long frames = 0L;

            for (int i = 0; i <= 8 * SamplesPerHour; i++)
            {
                double hours = i / (double)SamplesPerHour;
                double perSample = hours > 1.0 && hours < 7.0 ? 60.0 : 600.0;

                frames += (long)perSample;

                sagging.Add(Sample(
                    hours, (long)(150.0 * Mib), i % 10 == 0 ? (long)(120.0 * Mib) : 0L,
                    (long)(420.0 * Mib), 700, 300, 200, frames, i * 5L, 24, (long)(48.0 * Mib),
                    2, i / 20));
            }

            SoakFinding finding = FindingFor(sagging, "Update rate");

            _output.WriteLine(finding.ToString());

            Assert.Equal(SoakVerdict.Passed, finding.Verdict);
        }

        // ---- Pooled buffers and dropped frames ---------------------------------------------------

        [Fact]
        public void APoolThatFillsEarlyAndThenStaysPutPasses()
        {
            // REQ-NFR-011's pool is capped per bucket, so it fills in the first minute and stops.
            // "No net growth" has to mean no continuing rise, or the design reads as a leak.
            List<SoakSample> filling = Spoil(
                Healthy(),
                (s, hours) => With(s, pooledBytes: hours < 0.1
                    ? (long)(4.0 * Mib)
                    : (long)(48.0 * Mib)));

            SoakFinding finding = FindingFor(filling, "Pooled buffers");

            _output.WriteLine(finding.ToString());

            Assert.Equal(SoakVerdict.Passed, finding.Verdict);
        }

        [Fact]
        public void APoolThatKeepsGrowingFails()
        {
            List<SoakSample> growing = Spoil(
                Healthy(),
                (s, hours) => With(
                    s,
                    pooledBytes: s.PooledBytes + (long)(6.0 * Mib * hours),
                    pooledBuffers: s.PooledBuffers + (int)(4.0 * hours)));

            SoakFinding finding = FindingFor(growing, "Pooled buffers");

            _output.WriteLine(finding.ToString());

            Assert.Equal(SoakVerdict.Failed, finding.Verdict);
        }

        [Fact]
        public void DroppedFramesAreReportedRatherThanJudged()
        {
            // "reported at the end rather than checked only for boundedness". Coalescing is the
            // designed behaviour of REQ-NFR-012, so a threshold here would be a limit no
            // requirement states -- but the figure has to appear, or the clause is unmet.
            List<SoakSample> dropping = Spoil(
                Healthy(), (s, hours) => With(s, framesDropped: s.FramesDropped * 20L));

            SoakFinding finding = FindingFor(dropping, "Dropped frames");

            _output.WriteLine(finding.ToString());

            Assert.Equal(SoakVerdict.Passed, finding.Verdict);
            Assert.Contains("dropped of", finding.Detail, StringComparison.Ordinal);
            Assert.Contains("drawn", finding.Detail, StringComparison.Ordinal);
        }

        // ---- Duration and sampling ---------------------------------------------------------------

        [Fact]
        public void ARunShorterThanEightHoursFails()
        {
            List<SoakSample> shortRun =
                Healthy().Where(s => s.ElapsedHours <= 7.5).ToList();

            SoakFinding finding = FindingFor(shortRun, "The soak ran");

            _output.WriteLine(finding.ToString());

            Assert.Equal(SoakVerdict.Failed, finding.Verdict);
            Assert.Contains("7.5", finding.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void ARunOfTheRightLengthThatSampledTwiceIsInconclusive()
        {
            // The lesson of the NoNetworkEgressTests flake, applied before it can bite: a run that
            // measured almost nothing must say so rather than read as a clean pass.
            var sparse = new List<SoakSample>
            {
                Sample(0.0, (long)(150.0 * Mib), (long)(120.0 * Mib), (long)(420.0 * Mib),
                    700, 300, 200, 0L, 0L, 24, (long)(48.0 * Mib), 1, 0),
                Sample(8.0, (long)(150.0 * Mib), (long)(120.0 * Mib), (long)(420.0 * Mib),
                    700, 300, 200, 288000L, 4000L, 24, (long)(48.0 * Mib), 1, 40),
            };

            SoakReport report = new EnduranceGate().Judge(sparse);

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Inconclusive, Verdict(report, "The soak ran"));
            Assert.False(report.Passed, "Two samples over eight hours must not pass.");
        }

        [Fact]
        public void AnEmptyLogFailsRatherThanPassingVacuously()
        {
            SoakReport report = new EnduranceGate().Judge(new SoakSample[0]);

            Assert.False(report.Passed);
            Assert.Equal(0, report.Samples);
            Assert.All(report.Findings, f => Assert.True(f.Fails));
        }

        [Fact]
        public void EveryClaimTheRequirementMakesIsJudged()
        {
            // The criterion names five things plus the duration. A gate that quietly dropped one
            // would pass a run that never showed it, so the list is asserted rather than trusted.
            SoakReport report = new EnduranceGate().Judge(Healthy());

            string[] claims = report.Findings.Select(f => f.Claim).ToArray();

            Assert.Contains(claims, c => c.StartsWith("The soak ran", StringComparison.Ordinal));
            Assert.Contains(claims, c => c.StartsWith("Managed memory", StringComparison.Ordinal));
            Assert.Contains(claims, c => c.StartsWith("Unmanaged memory", StringComparison.Ordinal));
            Assert.Contains(claims, c => c.StartsWith("Handles", StringComparison.Ordinal));
            Assert.Contains(claims, c => c.StartsWith("GDI objects", StringComparison.Ordinal));
            Assert.Contains(claims, c => c.StartsWith("Update rate", StringComparison.Ordinal));
            Assert.Contains(claims, c => c.StartsWith("Pooled buffers", StringComparison.Ordinal));
            Assert.Contains(claims, c => c.StartsWith("Dropped frames", StringComparison.Ordinal));

            // And each names where it comes from, so a failure can be traced to a requirement.
            Assert.All(report.Findings, f => Assert.StartsWith("REQ-", f.Requirement));
        }

        [Fact]
        public void TheGateCanBeToldAShorterDurationSoTheHarnessItselfCanBeExercised()
        {
            // A two-minute rehearsal has to be judgeable, or the only way to test the host is to
            // wait a night to find out it wrote the wrong column.
            var brief = new List<SoakSample>();

            // 41 samples three seconds apart: exactly the two minutes the gate is told to expect.
            for (int i = 0; i <= 40; i++)
            {
                double hours = i * (3.0 / 3600.0);

                brief.Add(Sample(
                    hours, (long)(150.0 * Mib), i % 5 == 0 ? (long)(120.0 * Mib) : 0L,
                    (long)(420.0 * Mib), 700, 300, 200, i * 30L, i * 4L, 24,
                    (long)(48.0 * Mib), 2, i / 10));
            }

            SoakReport report = new EnduranceGate(120.0 / 3600.0).Judge(brief);

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Passed, Verdict(report, "The soak ran"));

            // But the hourly comparison still says it could not be made, rather than inventing one.
            Assert.Equal(SoakVerdict.Inconclusive, Verdict(report, "Update rate"));
        }

        [Fact]
        public void GrowthConfinedToTheWarmUpIsNotALeak()
        {
            // A shell that has just started is jitting and first-touching pages. 40 MiB of that in
            // the opening minute, then flat for eight hours, is a healthy run -- and fitted from
            // t = 0 it is a slope. See EnduranceGate.WarmUpMinutes.
            List<SoakSample> warming = Spoil(
                Healthy(),
                (s, hours) => hours < WarmUpHours
                    ? With(
                        s,
                        collectedManagedBytes: s.CollectedManagedBytes == 0L
                            ? 0L
                            : s.CollectedManagedBytes - (long)(40.0 * Mib * (1.0 - (hours / WarmUpHours))),
                        privateBytes: s.PrivateBytes - (long)(40.0 * Mib * (1.0 - (hours / WarmUpHours))))
                    : s);

            SoakReport report = new EnduranceGate().Judge(warming);

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Passed, Verdict(report, "Managed memory"));
            Assert.Equal(SoakVerdict.Passed, Verdict(report, "Unmanaged memory"));
        }

        [Fact]
        public void ALeakIsStillFoundWhenAWarmUpPrecedesIt()
        {
            // The other half, and the one that matters: discarding the opening minutes must not
            // become a place a leak can hide behind. The same warm-up, then a rise that continues.
            List<SoakSample> both = Spoil(
                Healthy(),
                (s, hours) => With(
                    s,
                    privateBytes: s.PrivateBytes
                        - (hours < WarmUpHours
                            ? (long)(40.0 * Mib * (1.0 - (hours / WarmUpHours)))
                            : 0L)
                        + (long)(20.0 * Mib * hours)));

            SoakFinding finding = FindingFor(both, "Unmanaged memory");

            _output.WriteLine(finding.ToString());

            Assert.Equal(SoakVerdict.Failed, finding.Verdict);
        }

        [Fact]
        public void ARunShorterThanTheWarmUpCannotAnswerTheMemoryClaims()
        {
            List<SoakSample> tiny = Healthy().Where(s => s.ElapsedSeconds < 60.0).ToList();

            SoakReport report = new EnduranceGate(1.0 / 60.0).Judge(tiny);

            Assert.Equal(SoakVerdict.Inconclusive, Verdict(report, "Managed memory"));
            Assert.Equal(SoakVerdict.Inconclusive, Verdict(report, "Unmanaged memory"));
        }

        [Fact]
        public void AGateCannotBeAskedForANonPositiveDuration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EnduranceGate(0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EnduranceGate(-1.0));
        }

        // ---- Fixtures ----------------------------------------------------------------------------

        /// <summary>An eight-hour run that ought to pass every claim.</summary>
        private static List<SoakSample> Healthy() => Decaying(0.0);

        /// <summary>
        /// An eight-hour run whose frame rate falls by a given fraction between the first hour and
        /// the last, and which is otherwise healthy.
        /// </summary>
        /// <param name="decay">The fractional fall; negative means it got faster.</param>
        private static List<SoakSample> Decaying(double decay)
        {
            var samples = new List<SoakSample>();
            long frames = 0L;
            long dropped = 0L;
            // Inclusive of both ends: 8*60 samples spaced a minute apart end at 7.98 hours,
            // which is a fixture that fails the duration claim for arithmetic reasons rather
            // than for anything the test is about.
            int total = (8 * SamplesPerHour) + 1;

            for (int i = 0; i < total; i++)
                    {
                double hours = i / (double)SamplesPerHour;

                // A rate that falls linearly across the run, so the first and final hours differ by
                // the fraction asked for.
                double perSecond = 30.0 * (1.0 - (decay * (hours / 7.0)));

                frames += (long)(perSecond * 60.0);
                dropped += 12L;

                samples.Add(Sample(
                    hours,
                    managed: (long)((150.0 + (i % 6 == 0 ? 40.0 : 0.0)) * Mib),
                    collected: i % 10 == 0 ? (long)((120.0 + (i % 3)) * Mib) : 0L,
                    priv: (long)((420.0 + (i % 4 == 0 ? 6.0 : 0.0)) * Mib),
                    handles: 700 + (i % 11),
                    gdi: 300 + (i % 7),
                    user: 200 + (i % 5),
                    frames: frames,
                    dropped: dropped,
                    pooledBuffers: 24,
                    pooledBytes: (long)(48.0 * Mib),
                    traces: 1 + (i % 4),
                    cycles: i / 15));
            }

            return samples;
        }

        private static SoakSample Sample(
            double hours, long managed, long collected, long priv, int handles, int gdi, int user,
            long frames, long dropped, int pooledBuffers, long pooledBytes, int traces, int cycles) =>
            new SoakSample(
                hours * 3600.0, managed, collected, priv, handles, gdi, user, frames, dropped,
                pooledBuffers, pooledBytes, traces, cycles);

        /// <summary>Rewrites every sample of a log, given its elapsed hours.</summary>
        private static List<SoakSample> Spoil(
            List<SoakSample> samples, Func<SoakSample, double, SoakSample> change) =>
            samples.Select(s => change(s, s.ElapsedHours)).ToList();

        /// <summary>A copy of a sample with some fields replaced.</summary>
        private static SoakSample With(
            SoakSample s,
            long? managedBytes = null,
            long? collectedManagedBytes = null,
            long? privateBytes = null,
            int? handles = null,
            int? gdiObjects = null,
            int? userObjects = null,
            long? framesDrawn = null,
            long? framesDropped = null,
            int? pooledBuffers = null,
            long? pooledBytes = null,
            int? tracesOpen = null,
            int? cycles = null) =>
            new SoakSample(
                s.ElapsedSeconds,
                managedBytes ?? s.ManagedBytes,
                collectedManagedBytes ?? s.CollectedManagedBytes,
                privateBytes ?? s.PrivateBytes,
                handles ?? s.Handles,
                gdiObjects ?? s.GdiObjects,
                userObjects ?? s.UserObjects,
                framesDrawn ?? s.FramesDrawn,
                framesDropped ?? s.FramesDropped,
                pooledBuffers ?? s.PooledBuffers,
                pooledBytes ?? s.PooledBytes,
                tracesOpen ?? s.TracesOpen,
                cycles ?? s.Cycles);

        private static SoakFinding FindingFor(IEnumerable<SoakSample> samples, string claimPrefix)
        {
            SoakReport report = new EnduranceGate().Judge(samples);

            return report.Findings.First(
                f => f.Claim.StartsWith(claimPrefix, StringComparison.Ordinal));
        }

        private static SoakVerdict Verdict(SoakReport report, string claimPrefix) =>
            report.Findings
                .First(f => f.Claim.StartsWith(claimPrefix, StringComparison.Ordinal))
                .Verdict;

        private static string Failures(SoakReport report) =>
            string.Join("; ", report.Findings.Where(f => f.Fails).Select(f => f.ToString()));
    }
}
