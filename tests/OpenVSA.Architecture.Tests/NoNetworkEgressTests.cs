using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-NFR-036</c>: a default installation makes no outbound network connection of any kind,
    /// and opens no listening socket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The criterion is explicit that this must be asserted by watching the process, "not merely by
    /// absence of a telemetry component". An architecture test that searched for an
    /// <c>HttpClient</c> would pass a product that reached the network through a dependency, and
    /// most of the ways this could go wrong are dependencies: a licence check, a font service, an
    /// update ping in a control library.
    /// </para>
    /// <para>
    /// So the shell is launched and its own TCP table is read. That is the whole test: what the
    /// process actually has open, rather than what its source suggests it might.
    /// </para>
    /// </remarks>
    public class NoNetworkEgressTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45.0);

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the observed sockets are written.</param>
        public NoNetworkEgressTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>How many looks at the process's TCP table constitute evidence.</summary>
        /// <remarks>
        /// A count rather than a duration, so the evidence does not depend on machine speed. At
        /// <see cref="SampleInterval"/> apart these span a couple of seconds on any machine, which
        /// covers start-up -- when an update check or a licence call would happen.
        /// </remarks>
        private const int WantedSamples = 8;

        /// <summary>Milliseconds between looks.</summary>
        private const int SampleInterval = 250;

        [Fact]
        public void TheShellOpensNoListeningSocketAndConnectsNowhere()
        {
            string shell = ShellPath();

            var info = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(shell),
            };

            Process process = Process.Start(info);

            Assert.NotNull(process);

            var offenders = new List<string>();
            int samples = 0;
            string stopped = "the sample count was reached";

            try
            {
                process.WaitForInputIdle((int)Patience.TotalMilliseconds);

                // Sampled repeatedly rather than once. A connection opened during start-up and
                // closed again would be invisible to a single look, and start-up is exactly when
                // an update check or a licence call would happen.
                //
                // **A fixed COUNT, not a fixed duration** (#416). Sampling for six seconds and
                // hoping enough fit made the evidence a function of how fast the machine
                // enumerates TCP tables: on a loaded two-core runner it managed four samples and
                // the test failed having found nothing wrong with the shell. The requirement is
                // about what the shell connects to, not about the runner's throughput, so the loop
                // now takes its samples and the deadline is only a backstop against a hang.
                var deadline = Stopwatch.StartNew();

                while (samples < WantedSamples)
                {
                    process.Refresh();

                    if (process.HasExited)
                    {
                        // Said explicitly, because it is a different fault entirely. The old
                        // message reported this as a sampling shortfall -- a symptom two steps
                        // from a shell that died at start-up.
                        stopped = "the shell exited after " + samples + " samples with exit code " +
                                  process.ExitCode;
                        break;
                    }

                    offenders.AddRange(SocketsOf(process.Id));
                    samples++;

                    if (deadline.Elapsed > Patience)
                    {
                        stopped = "the " + Patience.TotalSeconds.ToString("F0") +
                                  " s backstop elapsed after " + samples + " samples";
                        break;
                    }

                    Thread.Sleep(SampleInterval);
                }
            }
            finally
            {
                Close(process);
            }

            _output.WriteLine(
                samples + " samples of the process's TCP table; " + stopped);

            foreach (string offender in offenders.Distinct())
            {
                _output.WriteLine("  " + offender);
            }

            // Kept, and not loosened. It is what stops this passing vacuously: finding no sockets
            // is evidence only if the process was looked at enough times to have seen one.
            Assert.True(
                samples >= WantedSamples,
                "Only " + samples + " of " + WantedSamples + " samples were taken -- " + stopped +
                ". No conclusion about network egress can be drawn from that.");

            Assert.False(
                offenders.Any(),
                "REQ-NFR-036: the shell opened a socket in a default installation." +
                Environment.NewLine + string.Join(Environment.NewLine, offenders.Distinct()));
        }

        /// <summary>
        /// Listening or non-loopback connected sockets belonging to a process.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read from <c>netstat -ano</c>, which reports the owning process id. The managed
        /// <c>IPGlobalProperties</c> equivalents are system-wide and carry no process id at all, so
        /// they would report every other program on the machine.
        /// </para>
        /// <para>
        /// Loopback connections are not egress and are not flagged: WPF and the graphics stack use
        /// them internally, and failing on those would make this test a report about Windows.
        /// </para>
        /// </remarks>
        private static IEnumerable<string> SocketsOf(int processId)
        {
            var start = new ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            string output;

            using (Process netstat = Process.Start(start))
            {
                output = netstat.StandardOutput.ReadToEnd();
                netstat.WaitForExit(15000);
            }

            foreach (string line in output.Split('\n'))
            {
                string[] cells = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                // Proto Local Foreign State Pid — UDP rows have no state and so four cells.
                if (cells.Length < 4 || !cells[0].StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int pid;

                if (!int.TryParse(cells[cells.Length - 1], out pid) || pid != processId)
                {
                    continue;
                }

                string state = cells.Length >= 5 ? cells[3] : string.Empty;
                string foreign = cells[2];

                if (state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    yield return "LISTENING on " + cells[1];
                    continue;
                }

                if (!IsLoopback(foreign))
                {
                    yield return state + " to " + foreign;
                }
            }
        }

        private static bool IsLoopback(string endpoint)
        {
            int colon = endpoint.LastIndexOf(':');
            string address = colon < 0 ? endpoint : endpoint.Substring(0, colon);

            return address.StartsWith("127.", StringComparison.Ordinal) ||
                   address == "0.0.0.0" ||
                   address == "[::1]" ||
                   address == "[::]" ||
                   address == "*";
        }

        private static string ShellPath()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpenVSA.slnx")))
                {
                    string tail = AppDomain.CurrentDomain.BaseDirectory.Substring(
                        AppDomain.CurrentDomain.BaseDirectory.IndexOf(
                            "bin", StringComparison.OrdinalIgnoreCase));

                    string shell = Path.Combine(
                        directory.FullName, "src", "OpenVSA.Ui", tail, "OpenVSA.exe");

                    Assert.True(File.Exists(shell), "No built shell at " + shell);
                    return shell;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not find the repository root.");
        }

        private static void Close(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();

                    if (!process.WaitForExit(5000))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
