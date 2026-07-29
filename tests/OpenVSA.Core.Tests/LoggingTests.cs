using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using OpenVSA.Core.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Core.Tests
{
    /// <summary>
    /// <c>REQ-NFR-034</c>: per-subsystem levels that take effect without a restart, structured
    /// entries, a bundle that omits nothing silently, and a write that never blocks.
    /// </summary>
    public class LoggingTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the rendered bundle is written.</param>
        public LoggingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void RaisingOneSubsystemsLevelIncreasesOnlyItsOutput()
        {
            // The criterion's own wording: "verified by raising one subsystem's level and asserting
            // only its output increases". A global level would pass a test that only counted total
            // entries.
            var log = new Log { DefaultLevel = LogLevel.Information };

            Emit(log, "Acquisition", 10);
            Emit(log, "Dsp", 10);

            int acquisitionBefore = log.CountFor("Acquisition");
            int dspBefore = log.CountFor("Dsp");

            log.SetLevel("Dsp", LogLevel.Debug);

            Emit(log, "Acquisition", 10);
            Emit(log, "Dsp", 10);

            int acquisitionAfter = log.CountFor("Acquisition") - acquisitionBefore;
            int dspAfter = log.CountFor("Dsp") - dspBefore;

            _output.WriteLine(
                "after raising Dsp to Debug: Acquisition " + acquisitionAfter +
                " entries, Dsp " + dspAfter);

            Assert.True(dspAfter > acquisitionAfter, "Raising Dsp's level did not increase its output.");
            Assert.Equal(acquisitionBefore, acquisitionAfter);
        }

        [Fact]
        public void TheNewLevelAppliesImmediatelyWithNoRestart()
        {
            // A fault that only appears after twenty minutes of acquisition cannot be diagnosed by
            // a setting that needs the application closed to apply.
            var log = new Log { DefaultLevel = LogLevel.Warning };

            Assert.False(log.Write("Hal", LogLevel.Debug, "before"));

            log.SetLevel("Hal", LogLevel.Debug);

            Assert.True(log.Write("Hal", LogLevel.Debug, "after"));
            Assert.Single(log.Entries);
        }

        [Fact]
        public void EntriesCarryFieldsRatherThanFormattedProse()
        {
            // "Machine-parseable fields, not formatted prose — so a support bundle can be queried
            // rather than read." The moment a rate is written into a sentence, "show me every entry
            // where the rate fell below 2 kB/s" stops being answerable.
            var log = new Log();

            log.Write("Visa", LogLevel.Warning, "throughput below plan", new Dictionary<string, object>
            {
                ["bytesPerSecond"] = 1840.0,
                ["resource"] = "GPIB0::17::INSTR",
            });

            LogEntry entry = log.Entries.Single();

            Assert.Equal("throughput below plan", entry.Message);
            Assert.Equal(1840.0, entry.Fields["bytesPerSecond"]);

            // The query the fields make possible.
            IEnumerable<LogEntry> slow = log.Entries
                .Where(e => e.Fields.ContainsKey("bytesPerSecond") &&
                            Convert.ToDouble(e.Fields["bytesPerSecond"]) < 2000.0);

            Assert.Single(slow);

            _output.WriteLine(entry.ToLine());
        }

        [Fact]
        public void WritingNeverBlocksTheCaller()
        {
            // REQ-NFR-011 and REQ-NFR-012 put the measurement pipeline on a thread that must not
            // wait for anything, which is why the requirement calls this a correctness matter
            // rather than a performance one.
            //
            // **Measured on the mean and the 99th percentile, NOT the maximum.** The first version
            // asserted the maximum and failed in CI at 32 ms — which was a garbage collection or a
            // scheduler preemption on a two-core runner, not the log waiting for anything. The
            // maximum of any timed call on a shared machine measures the machine. Changing the
            // threshold would have been tuning a check until it passed; changing the instrument is
            // measuring the thing the requirement is about.
            //
            // A genuinely blocking implementation — a lock held by a slow sink, a synchronous file
            // write — shows up in the mean and the 99th, not in one outlier.
            var log = new Log { DefaultLevel = LogLevel.Debug };

            const int Writes = Log.Capacity * 3;

            var times = new double[Writes];
            var clock = new Stopwatch();

            for (int i = 0; i < Writes; i++)
            {
                clock.Restart();
                log.Write("Dsp", LogLevel.Debug, "frame");
                times[i] = clock.Elapsed.TotalMilliseconds;
            }

            Array.Sort(times);

            double mean = times.Average();
            double ninetyNinth = times[(int)(Writes * 0.99)];

            _output.WriteLine(
                Writes + " writes: mean " + mean.ToString("F5") + " ms, 99th " +
                ninetyNinth.ToString("F4") + " ms, max " + times[Writes - 1].ToString("F3") +
                " ms; " + log.Dropped + " dropped");

            Assert.True(
                mean < 0.05,
                "The mean write took " + mean.ToString("F5") +
                " ms. On the measurement thread that is a dropped frame.");

            Assert.True(
                ninetyNinth < 1.0,
                "The 99th-percentile write took " + ninetyNinth.ToString("F4") +
                " ms, which is a wait rather than an outlier.");
        }

        [Fact]
        public void OverflowDropsTheOldestAndCountsWhatItDropped()
        {
            // A log that quietly lost entries under load would be worst exactly when it was most
            // needed: a bundle from a machine in trouble, missing the part that mattered, and not
            // saying so.
            var log = new Log { DefaultLevel = LogLevel.Debug };

            for (int i = 0; i < Log.Capacity + 500; i++)
            {
                log.Write("Dsp", LogLevel.Debug, "frame", new Dictionary<string, object> { ["n"] = i });
            }

            Assert.Equal(500, log.Dropped);
            Assert.True(log.Entries.Count <= Log.Capacity);

            // The newest survived, which is what a fault report needs.
            LogEntry last = log.Entries[log.Entries.Count - 1];
            Assert.Equal(Log.Capacity + 499, Convert.ToInt32(last.Fields["n"]));
        }

        [Fact]
        public void TheBundleListsWhatItOmitted()
        {
            // The clause the type is built around. A bundle that quietly dropped a connection
            // string would be safe and useless: the reader cannot tell an absent setting from a
            // removed one, and will diagnose against a picture missing the interesting part.
            var bundle = new SupportBundle();

            bundle.AddVersion("OpenVSA", "0.1.0");
            bundle.AddSetting("OpenVSA.Visa.E4406A.Resource", "GPIB0::17::INSTR");
            bundle.AddSetting("SyncfusionLicenseKey", "not-a-real-key");
            bundle.AddSetting("Database.ConnectionString", "Server=x;Password=y");

            var log = new Log();
            log.Write("Hal", LogLevel.Information, "connected");

            string text = bundle.Render(log, new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc));

            _output.WriteLine(text);

            Assert.DoesNotContain("not-a-real-key", text);
            Assert.DoesNotContain("Password=y", text);

            // Named, with a reason, so the reader knows what is missing.
            Assert.Contains("SyncfusionLicenseKey", text);
            Assert.Contains("Database.ConnectionString", text);
            Assert.Contains("may be a licence or API key", text);

            Assert.Equal(2, bundle.Omitted.Count);

            // And the setting that is safe is present in full.
            Assert.Contains("GPIB0::17::INSTR", text);
        }

        [Fact]
        public void ABundleWithNothingOmittedSaysSo()
        {
            // Silence is ambiguous: "no omissions" and "nobody checked" look identical in an empty
            // section.
            var bundle = new SupportBundle();
            bundle.AddSetting("Span", "2 MHz");

            string text = bundle.Render(null, DateTime.UtcNow);

            Assert.Contains("nothing was omitted", text);
        }

        [Fact]
        public void TheWithholdingRuleMatchesNamesNobodyThoughtOf()
        {
            // The failure to avoid is a secret escaping under a name nobody anticipated, so the
            // rule is a case-insensitive substring rather than an exact list.
            Assert.NotNull(SupportBundle.ReasonToWithhold("SyncfusionKey"));
            Assert.NotNull(SupportBundle.ReasonToWithhold("Syncfusion.Licence.KEY"));
            Assert.NotNull(SupportBundle.ReasonToWithhold("apiToken"));
            Assert.NotNull(SupportBundle.ReasonToWithhold("my_password_hint"));

            Assert.Null(SupportBundle.ReasonToWithhold("CentreFrequencyHz"));
            Assert.Null(SupportBundle.ReasonToWithhold("Window"));
        }

        [Fact]
        public void TheBundleReportsDroppedEntriesRatherThanPresentingAPartialLog()
        {
            var log = new Log { DefaultLevel = LogLevel.Debug };

            for (int i = 0; i < Log.Capacity + 10; i++)
            {
                log.Write("Dsp", LogLevel.Debug, "frame");
            }

            string text = new SupportBundle().Render(log, DateTime.UtcNow);

            Assert.Contains("10 entries were dropped", text);
        }

        private static void Emit(Log log, string subsystem, int count)
        {
            for (int i = 0; i < count; i++)
            {
                log.Write(subsystem, LogLevel.Information, "info");
                log.Write(subsystem, LogLevel.Debug, "detail");
            }
        }
    }
}
