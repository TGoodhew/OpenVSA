using System;
using System.Globalization;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Hal;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-ARC-002</c>: each coercion raises a user-visible event-log entry.
    /// </summary>
    /// <remarks>
    /// One entry per coercion, not a summary. The settings pane already says <em>that</em>
    /// something was coerced; a reader who wants to know <em>what</em> should not have to
    /// reconstruct it from a plan readout that changes on the next Apply.
    /// </remarks>
    [Collection("Shell")]
    public class CoercionEventLogTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared shell thread and xunit's output sink.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        /// <param name="output">Where the entries are written.</param>
        public CoercionEventLogTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void EveryCoercionBecomesItsOwnEntry()
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow();

                var coercions = new[]
                {
                    new ParameterCoercion("Span", 40.0e6, 10.0e6, "exceeds this instrument's maximum span"),
                    new ParameterCoercion("SamplesPerBlock", 65536.0, 23050.0, "beyond the capture depth"),
                    new ParameterCoercion("CenterFrequency", 2.4e9, 1.0e9, "a recording cannot be retuned"),
                };

                foreach (ParameterCoercion coercion in coercions)
                {
                    string line = ShellWindow.DescribeCoercionForTest(coercion, "Agilent E4406A");

                    _output.WriteLine(line);

                    // The parameter, both values and the reason: a reader has to be able to act on
                    // it without opening the plan readout.
                    Assert.Contains(coercion.Parameter, line);
                    Assert.Contains(coercion.Reason, line);
                    Assert.Contains("Agilent E4406A", line);
                    Assert.Contains(
                        coercion.Honoured.ToString("G6", CultureInfo.CurrentCulture), line);
                }

                shell.Close();
            });
        }

        [Fact]
        public void TheEntryNamesTheSourceThatImposedIt()
        {
            // Which source coerced it is the whole point on a front-end change: the same setting is
            // honoured by one and rewritten by the next, and an entry that did not say which would
            // leave a user comparing two runs with no way to tell them apart.
            _host.Run(() =>
            {
                var coercion = new ParameterCoercion("Span", 40.0e6, 2.0e6, "wider than the recording");

                string named = ShellWindow.DescribeCoercionForTest(coercion, "File playback — capture.ovsa");
                string anonymous = ShellWindow.DescribeCoercionForTest(coercion, null);

                Assert.StartsWith("File playback", named);
                Assert.StartsWith("The source", anonymous);
            });
        }
    }
}
