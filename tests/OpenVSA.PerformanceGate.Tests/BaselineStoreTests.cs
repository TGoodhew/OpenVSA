using System;
using System.Linq;
using OpenVSA.PerformanceGate;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.PerformanceGate.Tests
{
    /// <summary>
    /// The stored baselines: a round trip, and the machine-class identity they are keyed by.
    /// </summary>
    public class BaselineStoreTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the rendered store is written.</param>
        public BaselineStoreTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AStoreSurvivesARoundTripThroughItsText()
        {
            var store = new BaselineStore();
            var reference = new MachineClass("AMD Ryzen 9 7950X", 32, 64);
            var runner = new MachineClass("Intel Xeon Platinum 8370C", 4, 16);
            var recorded = new DateTime(2026, 7, 28, 9, 15, 0, DateTimeKind.Utc);

            store.Set(new BaselineEntry(reference, "Spectrum8192Rendered", 84.25, 0.011, recorded, "ec61c39"));
            store.Set(new BaselineEntry(reference, "Spectrum1MRenderedDecimated", 12.5, 0.02, recorded, "ec61c39"));
            store.Set(new BaselineEntry(runner, "Spectrum8192Rendered", 41.0, 0.08, recorded, "ec61c39"));

            string text = store.Write();
            _output.WriteLine(text);

            BaselineStore again = BaselineStore.Read(text);

            Assert.Equal(3, again.Count);
            Assert.True(again.Recognises(reference));
            Assert.True(again.Recognises(runner));

            BaselineEntry entry = again.Find(reference, "Spectrum8192Rendered");

            Assert.NotNull(entry);
            Assert.Equal(84.25, entry.Mean, 6);
            Assert.Equal(0.011, entry.RelativeResolution, 6);
            Assert.Equal(recorded, entry.Recorded);
            Assert.Equal("ec61c39", entry.Commit);
        }

        [Fact]
        public void TheTextIsOrderedSoADiffShowsWhatChanged()
        {
            // A baseline is a claim about how fast the product is, reviewed in a pull request like
            // any other claim. A re-write that reorders the rows hides the one line that moved.
            var store = new BaselineStore();
            var machine = new MachineClass("AMD Ryzen 9 7950X", 32, 64);
            var when = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);

            foreach (string name in new[] { "Zulu", "Alpha", "Mike" })
            {
                store.Set(new BaselineEntry(machine, name, 1.0, 0.01, when, "x"));
            }

            string[] rows = store.Write()
                .Split('\n')
                .Where(l => l.Length > 0 && l[0] != '#' && !l.StartsWith("machine\t", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(3, rows.Length);
            Assert.Contains("Alpha", rows[0]);
            Assert.Contains("Mike", rows[1]);
            Assert.Contains("Zulu", rows[2]);
        }

        [Fact]
        public void AMachineClassIsCoarseEnoughToShareAndFineEnoughToMatter()
        {
            // Too fine and every machine is its own class, which is the same as no baselines at
            // all; too coarse and two machines of different speed share one, which looks like a
            // comparison and is not.
            var a = new MachineClass("AMD  Ryzen 9   7950X", 32, 64);
            var b = new MachineClass("amd ryzen 9 7950x", 32, 64);
            var fewerCores = new MachineClass("AMD Ryzen 9 7950X", 16, 64);
            var lessMemory = new MachineClass("AMD Ryzen 9 7950X", 32, 32);

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, fewerCores);
            Assert.NotEqual(a, lessMemory);
        }

        [Fact]
        public void AProcessorNameCannotSplitTheKeyItIsStoredIn()
        {
            // The separator is the one character a processor name must not contain, or Parse
            // would cut the name in half and produce a different class that looks plausible.
            var awkward = new MachineClass("Weird|Vendor CPU", 8, 32);

            Assert.DoesNotContain("|", awkward.Processor);
            Assert.Equal(awkward, MachineClass.Parse(awkward.Key));
        }

        [Fact]
        public void AMalformedStoreIsRefusedRatherThanPartlyRead()
        {
            // A half-read baseline file compares against whichever rows happened to parse.
            Assert.Throws<FormatException>(() => BaselineStore.Read("only\ttwo\tcolumns\n"));
            Assert.Throws<FormatException>(
                () => BaselineStore.Read("not-a-machine-key\tName\t1.0\t0.01\t2026-07-28T00:00:00Z\tx\n"));
            Assert.Throws<FormatException>(
                () => BaselineStore.Read("CPU | 8c | 32GiB\tName\tnot-a-number\t0.01\t2026-07-28T00:00:00Z\tx\n"));

            // An absent file is not an error: it is the state before the first baseline is taken.
            Assert.Equal(0, BaselineStore.Read(null).Count);
            Assert.Equal(0, BaselineStore.Read(string.Empty).Count);
        }

        [Fact]
        public void EveryCatalogueEntryIsDistinctAndComplete()
        {
            Assert.Equal(7, TargetCatalogue.All.Count);
            Assert.Equal(7, TargetCatalogue.All.Select(t => t.Name).Distinct().Count());
            Assert.Equal(7, TargetCatalogue.All.Select(t => t.Requirement).Distinct().Count());

            foreach (PerformanceTarget target in TargetCatalogue.All)
            {
                Assert.Same(target, TargetCatalogue.ByName(target.Name));
                Assert.True(target.Stated > 0.0, target.Requirement + " states no figure.");
                Assert.False(string.IsNullOrEmpty(target.Unit), target.Requirement + " has no unit.");
            }

            Assert.Null(TargetCatalogue.ByName("NoSuchBenchmark"));
        }
    }
}
