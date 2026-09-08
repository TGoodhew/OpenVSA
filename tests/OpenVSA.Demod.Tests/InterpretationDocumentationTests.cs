using System;
using System.Collections.Generic;
using OpenVSA.Demod.Help;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// Where this build had to decide something the specification left open, the decision is in the
    /// shipped help.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why these are tested rather than trusted.</strong> Three requirements were
    /// implemented by reading a name the specification did not define — "Instantaneous Spectrum"
    /// against "Spectrum" in <c>REQ-DEM-032</c>, "residual FM/AM" in <c>REQ-DEM-010a</c>, and the
    /// analysis window that decides whether <c>REQ-DEM-010a</c>'s 60 dB SINAD is reachable at all.
    /// Each is defensible and none is the specification's. Tony asked that they be findable in the
    /// documentation and not only in an issue comment, because he may want to change them later —
    /// and a decision recorded only in a comment thread is one that gets rediscovered by whoever
    /// next disagrees with the number it produced.
    /// </para>
    /// <para>
    /// So the pages carry them, and these tests carry the pages. A help page is the easiest thing in
    /// a product to let drift away from what the product does, and an interpretation that quietly
    /// stopped being documented would be worse than one never written down: the reader would have
    /// been told there was nothing to know.
    /// </para>
    /// </remarks>
    public class InterpretationDocumentationTests
    {
        private readonly ITestOutputHelper _output;

        public InterpretationDocumentationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryShippedTopicIsEmbeddedAndReadable()
        {
            foreach (string name in HelpTopics.Names)
            {
                string text = HelpTopics.Read(name);

                Assert.False(string.IsNullOrWhiteSpace(text), name + " is empty.");

                _output.WriteLine(name + ": " + text.Length + " characters");
            }

            Assert.Contains(HelpTopics.Analog, HelpTopics.Names);
            Assert.Contains(HelpTopics.ResultWindow, HelpTopics.Names);
        }

        [Fact]
        public void TheAnalysisWindowThatDecidesTheSinadCeilingIsDocumented()
        {
            // The one whose consequence is not a different number but whether the requirement can
            // be met: a Hann window would cap SINAD near 40 dB against a 60 dB criterion.
            string help = HelpTopics.Read(HelpTopics.Analog);

            Assert.Contains("Blackman-Harris", help, StringComparison.Ordinal);
            Assert.Contains("Hann", help, StringComparison.Ordinal);
            Assert.Contains("60 dB", help, StringComparison.Ordinal);

            // Marked as an interpretation, not buried in prose that reads like settled fact.
            Assert.Contains("not a specification", help, StringComparison.Ordinal);

            // And the ceiling stated, so a reader does not take 88 dB for a property of the signal.
            Assert.Contains("88 dB", help, StringComparison.Ordinal);
        }

        [Fact]
        public void TheResidualDefinitionIsDocumentedAsAReading()
        {
            string help = HelpTopics.Read(HelpTopics.Analog);

            Assert.Contains("Residual FM", help, StringComparison.Ordinal);
            Assert.Contains("does **not** define it", help, StringComparison.Ordinal);
            Assert.Contains("harmonic", help, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheTwoSpectraAreDistinguishedAndTheReadingIsFlagged()
        {
            string help = HelpTopics.Read(HelpTopics.ResultWindow);

            Assert.Contains("Instantaneous Spectrum", help, StringComparison.Ordinal);

            // The distinction itself, in words a user can act on.
            Assert.Contains("averaged", help, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("not a specification", help, StringComparison.Ordinal);
            Assert.Contains("REQ-DEM-032", help, StringComparison.Ordinal);
        }

        [Fact]
        public void TheDeEmphasisTimeConstantsAndTheirCornerAreStated()
        {
            // Not an interpretation -- a fact a bench operator needs, and the reason the control is
            // named in microseconds while the filter is a corner frequency.
            string help = HelpTopics.Read(HelpTopics.Analog);

            Assert.Contains("75 µs", help, StringComparison.Ordinal);
            Assert.Contains("50 µs", help, StringComparison.Ordinal);
            Assert.Contains("2122", help, StringComparison.Ordinal);
            Assert.Contains("3.01 dB", help, StringComparison.Ordinal);
        }

        [Fact]
        public void TheThingsAUserWouldOtherwiseHaveToDiscoverAreWrittenDown()
        {
            string help = HelpTopics.Read(HelpTopics.ResultWindow);

            // That looking at a pre-demodulation trace cannot change a result. Users do not assume
            // this; they assume the opposite, because in most analysers it is not true.
            Assert.Contains("cannot change your result", help, StringComparison.Ordinal);

            // That a short Result Length reads about 1.4 % higher for a reason that is not length.
            Assert.Contains("1.4 %", help, StringComparison.Ordinal);
            Assert.Contains("transient", help, StringComparison.Ordinal);

            // And that the limit is refused rather than shortened, which is the difference between
            // a measurement you can trust and one you cannot.
            Assert.Contains("refused", help, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2 048", help, StringComparison.Ordinal);
            Assert.Contains("40 000", help, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryInterpretationIsMarkedTheSameWayInEveryTopic()
        {
            // One marker, so a reader who has learned to look for it finds all of them. Counted
            // rather than merely found: the count is the number of open readings this build makes,
            // and a new one arriving without its warning would go unnoticed if only presence were
            // asserted.
            var counts = new Dictionary<string, int>();

            foreach (string name in new[] { HelpTopics.Analog, HelpTopics.ResultWindow })
            {
                string help = HelpTopics.Read(name);
                int found = 0;
                int at = 0;

                while ((at = help.IndexOf(
                    "⚠ An interpretation, not a specification", at, StringComparison.Ordinal)) >= 0)
                {
                    found++;
                    at++;
                }

                counts[name] = found;

                _output.WriteLine(name + ": " + found + " interpretation(s) flagged");
            }

            // Two in the analog page -- the window and the residual definition -- and one in the
            // result-window page, for the two spectra. If this number changes, either a reading was
            // added without being flagged or one was settled and the page was not updated.
            Assert.Equal(2, counts[HelpTopics.Analog]);
            Assert.Equal(1, counts[HelpTopics.ResultWindow]);
        }
    }
}
