using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OpenVSA.Core.Threading;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-NFR-010</c>: the thread-affinity helper is present at every layer boundary, is active
    /// in Debug builds, and the suite fails on a violation.
    /// </summary>
    /// <remarks>
    /// The rule this protects is that the UI thread never performs DSP or I/O, and its failure mode
    /// is silent: a measurement computed on the dispatcher is not wrong, it is merely a shell that
    /// stops answering while it runs. Nothing about the numbers gives it away, so the guard has to
    /// be an assertion at the boundary rather than a review habit.
    /// </remarks>
    public class ThreadAffinityBoundaryTests
    {
        /// <summary>
        /// The boundaries the requirement means, and why each is one.
        /// </summary>
        /// <remarks>
        /// Named rather than discovered. A rule of the form "every public method in these
        /// assemblies" would be wrong — most methods are not boundaries and adding an assertion to
        /// all of them would be noise that gets deleted. These are the four places control actually
        /// crosses between the dispatcher and a worker.
        /// </remarks>
        private static readonly Dictionary<string, string> Boundaries = new Dictionary<string, string>
        {
            ["OpenVSA.Dsp/Spectrum/SpectrumComputer.cs"] = "the DSP entry point: must not be on the UI thread",
            ["OpenVSA.Measurement/SpectrumEngine.cs"] = "drives acquisition and analysis off the dispatcher",
            ["OpenVSA.Ui/Rendering/RenderMarshal.cs"] = "the hand-off from worker to dispatcher",
            ["OpenVSA.Ui/Rendering/TracePlot.cs"] = "draws, so must be on the UI thread",
        };

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the checked boundaries are written.</param>
        public ThreadAffinityBoundaryTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryLayerBoundaryAssertsItsThread()
        {
            string root = RepositoryRoot();
            var missing = new List<string>();

            foreach (KeyValuePair<string, string> boundary in Boundaries)
            {
                string path = Path.Combine(root, "src", boundary.Key.Replace('/', Path.DirectorySeparatorChar));

                Assert.True(File.Exists(path), "No such boundary file: " + path);

                string text = File.ReadAllText(path);

                if (text.IndexOf("AssertOnUiThread", StringComparison.Ordinal) < 0 &&
                    text.IndexOf("AssertNotOnUiThread", StringComparison.Ordinal) < 0)
                {
                    missing.Add(boundary.Key + " — " + boundary.Value);
                }
                else
                {
                    _output.WriteLine("ok  " + boundary.Key);
                }
            }

            Assert.False(
                missing.Any(),
                "REQ-NFR-010: a layer boundary carries no thread-affinity assertion." +
                Environment.NewLine + string.Join(Environment.NewLine, missing));
        }

        [Fact]
        public void TheAssertionIsActiveAndActuallyThrows()
        {
            // "Active in Debug builds; the test suite includes a run that fails on any violation."
            // A helper compiled away, or one that logs instead of throwing, would satisfy a test
            // that only checked the call was present — which is what the test above checks.
            var thread = new Thread(() =>
            {
                ThreadAffinity.MarkUiThread();

                try
                {
                    // Claiming to do DSP on the marked UI thread is the violation.
                    Assert.Throws<InvalidOperationException>(
                        () => ThreadAffinity.AssertNotOnUiThread("spectrum computation"));

                    // And the converse holds on the same thread, so the helper is discriminating
                    // rather than throwing at everything.
                    ThreadAffinity.AssertOnUiThread("drawing");
                }
                finally
                {
                    ThreadAffinity.ClearUiThread();
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(30.0)), "The affinity check did not finish.");
        }

        [Fact]
        public void WithNoUiThreadMarkedBothDirectionsAreSilent()
        {
            // Deliberate, and documented on the helper: silent when no UI thread has been marked,
            // which is the case in the headless test run and in REQ-API-002's automation surface.
            // An assertion that fired when there was no UI at all would be asserting something the
            // requirement does not say.
            //
            // Asserted rather than assumed, because it is the difference between a guard that is
            // lenient by design and one that is broken — and the two look identical from a passing
            // test suite. I wrote this test the other way round first.
            ThreadAffinity.AssertNotOnUiThread("a worker");
            ThreadAffinity.AssertOnUiThread("drawing with no UI at all");
        }

        [Fact]
        public void TheAssertionSymbolIsDefinedForTheTestRun()
        {
            // The helper is [Conditional("OPENVSA_THREAD_ASSERTS")]. If that symbol were not
            // defined, every call would compile away and every test above would pass by doing
            // nothing at all — the most comfortable kind of green.
            var thread = new Thread(() =>
            {
                ThreadAffinity.MarkUiThread();

                try
                {
                    bool threw = false;

                    try
                    {
                        ThreadAffinity.AssertNotOnUiThread("proof the symbol is defined");
                    }
                    catch (InvalidOperationException)
                    {
                        threw = true;
                    }

                    Assert.True(
                        threw,
                        "OPENVSA_THREAD_ASSERTS is not defined for this build, so every affinity " +
                        "assertion in the product compiles away and REQ-NFR-010 is unenforced.");
                }
                finally
                {
                    ThreadAffinity.ClearUiThread();
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(30.0)), "The symbol check did not finish.");
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpenVSA.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not find the repository root.");
        }
    }
}
