using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Personality;
using OpenVSA.Ui;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// What the results panel says, without a panel (<c>REQ-ARC-003</c>).
    /// </summary>
    /// <remarks>
    /// A personality returns a name, a value and a unit and deliberately no formatting, so that the
    /// same reading is not printed differently by every plug-in. These are the decisions that
    /// central formatting then has to make.
    /// </remarks>
    public class PersonalityResultsTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the rendered panel is written.</param>
        public PersonalityResultsTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void WithNothingSelectedItSaysSoRatherThanShowingNothing()
        {
            var results = new PersonalityResults();

            Assert.False(results.HasPersonality);
            Assert.Empty(results.Lines);
            Assert.Contains("No measurement personality", results.Summary);
        }

        [Fact]
        public void SelectedButNotYetRunIsADifferentThingFromNotSelected()
        {
            // A reader looking at an empty panel needs to know which of the two states they are in:
            // one means "choose a measurement type", the other means "it has not measured yet, or
            // it refused this acquisition".
            var results = new PersonalityResults();

            results.Select(new FakePersonality());

            _output.WriteLine(results.Summary);

            Assert.True(results.HasPersonality);
            Assert.Empty(results.Lines);
            Assert.Contains("no readings yet", results.Summary);
        }

        [Fact]
        public void TheStandardAndItsRevisionTravelWithTheName()
        {
            // REQ-PER-011 requires a declared standard revision. A reading that cannot say what it
            // was measured against is not a compliance result, and the panel is where a reader
            // would look for it.
            var results = new PersonalityResults();

            results.Select(new FakePersonality());

            Assert.Equal("Example 2026.1", results.Standard);
            Assert.Contains("Example 2026.1", results.Summary);
        }

        [Fact]
        public void APersonalityWithNoRevisionShowsTheStandardAlone()
        {
            // Rather than "Example " with a trailing space, which reads as a missing value that
            // is not missing.
            var results = new PersonalityResults();

            results.Select(new FakePersonality { StandardRevision = string.Empty });

            Assert.Equal("Example", results.Standard);
        }

        [Fact]
        public void NamesAreAlignedSoTheValuesFormAColumn()
        {
            // A results panel is read by scanning, and a column of numbers whose decimal points
            // wander is markedly harder to scan than one where they do not.
            var results = new PersonalityResults();

            results.Update(new[]
            {
                new PersonalityReading("EVM", 1.234, "%rms"),
                new PersonalityReading("Frequency error", -12.5, "Hz"),
                new PersonalityReading("Power", -20.31, "dBm"),
            });

            foreach (string line in results.Lines)
            {
                _output.WriteLine("[" + line + "]");
            }

            Assert.Equal(3, results.Count);

            // Where each value begins, found as the first digit or minus sign — none of these
            // names contains one. Not the first double space: the padding after a short name is
            // itself a run of spaces, so that would report the end of "EVM" rather than the start
            // of its value, and the first version of this assertion did exactly that.
            char[] numeric = { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '-' };

            int[] valueStarts = results.Lines.Select(l => l.IndexOfAny(numeric)).ToArray();

            _output.WriteLine("values begin at column " + string.Join(", ", valueStarts));

            Assert.Single(valueStarts.Distinct());

            Assert.Contains("%rms", results.Lines[0]);
        }

        [Fact]
        public void ANotMeasuredReadingSaysSoRatherThanShowingNaN()
        {
            // A personality that could not compute a reading is saying something. "NaN" would be
            // read as a fault in the display by anybody who has not met the abbreviation.
            var results = new PersonalityResults();

            results.Update(new[]
            {
                new PersonalityReading("EVM", double.NaN, "%rms"),
                new PersonalityReading("Headroom", double.PositiveInfinity, "dB"),
                new PersonalityReading("Leakage", double.NegativeInfinity, "dBc"),
            });

            foreach (string line in results.Lines)
            {
                _output.WriteLine(line);
            }

            Assert.Contains("not measured", results.Lines[0]);
            Assert.Contains("unbounded", results.Lines[1]);
            Assert.Contains("negligible", results.Lines[2]);

            Assert.DoesNotContain(results.Lines, l => l.Contains("NaN"));
            Assert.DoesNotContain(results.Lines, l => l.Contains("∞"));
        }

        [Fact]
        public void AReadingWithNoUnitDoesNotGetATrailingSpace()
        {
            var results = new PersonalityResults();

            results.Update(new[] { new PersonalityReading("Symbols", 1024.0, string.Empty) });

            Assert.Equal(results.Lines[0], results.Lines[0].TrimEnd());
        }

        [Fact]
        public void SelectingAgainClearsWhatTheLastOneMeasured()
        {
            // Otherwise one personality's readings sit under another's name, which is worse than
            // an empty panel by some distance.
            var results = new PersonalityResults();

            results.Select(new FakePersonality());
            results.Update(new[] { new PersonalityReading("EVM", 1.0, "%rms") });

            Assert.Equal(1, results.Count);

            results.Select(new FakePersonality { DisplayName = "Another" });

            Assert.Equal(0, results.Count);
            Assert.Equal("Another", results.PersonalityName);
        }

        [Fact]
        public void SelectingNothingReturnsToTheSpectrumState()
        {
            var results = new PersonalityResults();

            results.Select(new FakePersonality());
            results.Update(new[] { new PersonalityReading("EVM", 1.0, "%rms") });

            results.Select(null);

            Assert.False(results.HasPersonality);
            Assert.Empty(results.Lines);
            Assert.Equal(string.Empty, results.Standard);
        }

        [Fact]
        public void UpdatingWithNothingClearsRatherThanThrows()
        {
            var results = new PersonalityResults();

            results.Update(new[] { new PersonalityReading("EVM", 1.0, "%rms") });
            results.Update(null);

            Assert.Empty(results.Lines);

            results.Update(new PersonalityReading[0]);

            Assert.Empty(results.Lines);
        }

        [Fact]
        public void SixSignificantFiguresIsWhatIsShown()
        {
            // More than any of these measurements is good to, and fewer than a double will print.
            Assert.Equal("1.23457", PersonalityResults.Format(1.23456789));
            Assert.Equal("0", PersonalityResults.Format(0.0));
            Assert.Equal("-20.3125", PersonalityResults.Format(-20.3125));
        }

        /// <summary>A personality that exists only to be formatted.</summary>
        private sealed class FakePersonality : IMeasurementPersonality
        {
            public string DisplayName { get; set; } = "Example personality";

            public string Standard { get; set; } = "Example";

            public string StandardRevision { get; set; } = "2026.1";

            public bool CanMeasure(IqBlock block) => true;

            public IReadOnlyList<PersonalityReading> Measure(IqBlock block) =>
                new PersonalityReading[0];
        }
    }
}
