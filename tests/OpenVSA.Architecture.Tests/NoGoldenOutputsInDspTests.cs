using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-TST-001</c>: no DSP test compares against a stored output of a previous run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement states the prohibition and then says it must be <strong>enforced, not merely
    /// stated</strong>, and the reason is worth keeping in front of whoever reads this: a
    /// golden-output comparison is exactly how a wrong result becomes the baseline it is later
    /// validated against. The first run produces a number, the number is recorded, and every run
    /// afterwards agrees with it — including every run after the bug was introduced, because the
    /// bug is in the recording too.
    /// </para>
    /// <para>
    /// A DSP primitive's expected value comes from the analytic reference: Parseval, a closed-form
    /// window transform, the known response of a filter. Those can be wrong, but they are wrong in
    /// a way somebody can argue with.
    /// </para>
    /// <para>
    /// This scans sources rather than assemblies because the thing being forbidden is a way of
    /// writing a test, and it has no run-time signature at all — an approval comparison and an
    /// analytic one both come down to <c>Assert.Equal</c>.
    /// </para>
    /// </remarks>
    public class NoGoldenOutputsInDspTests
    {
        /// <summary>
        /// Patterns that mean "compared against something recorded earlier".
        /// </summary>
        /// <remarks>
        /// Named libraries and file conventions rather than a guess at intent. <c>ApprovalTests</c>
        /// and <c>Verify</c> are the two approval frameworks in common use; <c>.approved.</c> and
        /// <c>.verified.</c> are their file conventions; the rest are the names people give a
        /// hand-rolled one. A false positive here costs a rename; a false negative costs the
        /// property the requirement exists to protect.
        /// </remarks>
        private static readonly string[] ForbiddenPatterns =
        {
            @"\bApprovals?\s*\.\s*Verify",
            @"\bVerifier\s*\.\s*Verify",
            @"UseApprovalSubdirectory",
            @"\.approved\.",
            @"\.verified\.",
            @"\bgolden\b",
            @"\bGoldenOutput\b",
            @"ExpectedOutputFile",
            @"StoredExpected",
            @"BaselineFile",
        };

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the scanned file count is written.</param>
        public NoGoldenOutputsInDspTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void NoDspTestComparesAgainstAStoredOutput()
        {
            string suite = DspTestSuiteDirectory();

            var offenders = new List<string>();
            int scanned = 0;

            foreach (string file in Directory.GetFiles(suite, "*.cs", SearchOption.AllDirectories))
            {
                if (file.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    file.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                scanned++;
                string text = File.ReadAllText(file);

                // The comment in this very file names the patterns in order to forbid them, and a
                // scan that flagged its own explanation would be the same mistake as the manifest
                // test that failed on the comment saying "PerMonitorV2 must NOT be declared".
                if (Path.GetFileName(file).Equals("NoGoldenOutputsInDspTests.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string pattern in ForbiddenPatterns)
                {
                    foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
                    {
                        offenders.Add(
                            Path.GetFileName(file) + ": '" + match.Value + "' — REQ-TST-001 forbids " +
                            "comparing a DSP result against a stored output of a previous run.");
                    }
                }
            }

            _output.WriteLine(scanned + " DSP test source files scanned");

            Assert.True(scanned > 10, "Only " + scanned + " files were scanned; the suite was not found.");

            Assert.False(
                offenders.Any(),
                string.Join(Environment.NewLine, offenders.Distinct()));
        }

        [Fact]
        public void TheScanWouldNoticeAnApprovalComparison()
        {
            // A check that cannot fail is not a check. Rather than write a forbidden file into the
            // suite and delete it — which races any other test run — the patterns are applied to a
            // sample of exactly what they exist to catch.
            string[] samples =
            {
                "Approvals.Verify(result);",
                "Verifier.Verify(spectrum);",
                "var expected = File.ReadAllBytes(\"levels.approved.txt\");",
                "// compare against the golden trace",
                "string path = BaselineFile;",
            };

            foreach (string sample in samples)
            {
                Assert.True(
                    ForbiddenPatterns.Any(p => Regex.IsMatch(sample, p, RegexOptions.IgnoreCase)),
                    "The scan would not have caught: " + sample);
            }

            // And it does not fire on an analytic comparison, which is what the requirement wants
            // every DSP test to look like.
            const string Analytic =
                "Assert.Equal(expectedFromParseval, measured, 12); // closed form, REQ-TST-001";

            Assert.False(ForbiddenPatterns.Any(p => Regex.IsMatch(Analytic, p, RegexOptions.IgnoreCase)));
        }

        private static string DspTestSuiteDirectory()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpenVSA.slnx")))
                {
                    string suite = Path.Combine(directory.FullName, "tests", "OpenVSA.Dsp.Tests");

                    Assert.True(Directory.Exists(suite), "No DSP test suite at " + suite);
                    return suite;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not find the repository root.");
        }
    }
}
