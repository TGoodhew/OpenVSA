using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-NFR-032</c>: the application starts and offers its sources with no VISA runtime
    /// installed and no instrument attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement calls this an architectural constraint rather than a convenience feature,
    /// and the criterion says why: without the test the capability regresses into <em>"starts, but
    /// only because VISA happened to be present"</em>. That is not hypothetical on a machine like
    /// this one, where VISA <em>is</em> installed — every ordinary run of the product here proves
    /// nothing about the machine the requirement is about.
    /// </para>
    /// <para>
    /// So the check is on what the shell <strong>loads</strong>, not on what it can do: if
    /// <c>Ivi.Visa</c> is not in the running process, then nothing the shell did on the way to a
    /// window needed it, and the same start would work where it is absent.
    /// </para>
    /// </remarks>
    public class RunsWithNoVisaTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45.0);

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the loaded assemblies are written.</param>
        public RunsWithNoVisaTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheShellStartsWithoutLoadingVisa()
        {
            string shell = ShellPath();

            var info = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(shell),
            };

            Process process = Process.Start(info);
            Assert.NotNull(process);

            var loaded = new List<string>();

            try
            {
                Assert.True(
                    process.WaitForInputIdle((int)Patience.TotalMilliseconds),
                    "The shell did not reach an idle message loop.");

                // Let discovery finish: the registry probes the plug-in folder during start-up, and
                // checking before it has would pass for the wrong reason.
                Thread.Sleep(2500);
                process.Refresh();

                Assert.False(process.HasExited, "The shell exited during start-up.");

                foreach (ProcessModule module in process.Modules)
                {
                    loaded.Add(Path.GetFileName(module.FileName));
                }
            }
            finally
            {
                Close(process);
            }

            string[] visa = loaded
                .Where(m => m.IndexOf("Ivi.Visa", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.IndexOf("visa32", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.IndexOf("visa64", StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct()
                .ToArray();

            _output.WriteLine(loaded.Count + " modules loaded; VISA modules: " +
                              (visa.Length == 0 ? "none" : string.Join(", ", visa)));

            Assert.False(
                visa.Any(),
                "The shell loaded " + string.Join(", ", visa) +
                " on the way to a window. REQ-NFR-032 requires it to start on a machine where " +
                "those are absent, and this machine has them — so a start that touches them " +
                "proves nothing about the machine the requirement is about." + Environment.NewLine +
                "The VISA front end is a plug-in discovered at run time; loading its dependency " +
                "eagerly is what this guards against.");
        }

        [Fact]
        public void TheSimulatorAndFilePlaybackPluginsAreDeployedBesideTheShell()
        {
            // "The simulator and file-playback front ends are available." Available means present
            // where the registry probes, which is a property of the build rather than of a run.
            string plugins = Path.Combine(Path.GetDirectoryName(ShellPath()), "FrontEnds");

            Assert.True(Directory.Exists(plugins), "No FrontEnds directory at " + plugins);

            string[] present = Directory.GetFiles(plugins, "*.dll")
                .Select(Path.GetFileName)
                .ToArray();

            _output.WriteLine(string.Join(Environment.NewLine, present));

            Assert.Contains("OpenVSA.Hal.Sim.dll", present);
            Assert.Contains("OpenVSA.Hal.File.dll", present);
        }

        [Fact]
        public void NoAnalysisAssemblyReferencesTheVisaLibrary()
        {
            // The static counterpart: if nothing below the plug-in boundary names Ivi.Visa, the
            // run-time result above cannot regress by accident.
            string[] assemblies =
            {
                "OpenVSA.Core", "OpenVSA.Hal", "OpenVSA.Dsp", "OpenVSA.Capture",
                "OpenVSA.Measurement", "OpenVSA.Demod", "OpenVSA.Personality", "OpenVSA.Api",
                "OpenVSA",
            };

            foreach (string name in assemblies)
            {
                string[] referenced = Assembly.Load(name)
                    .GetReferencedAssemblies()
                    .Select(a => a.Name)
                    .ToArray();

                Assert.DoesNotContain("Ivi.Visa", referenced);
            }
        }

        private static string ShellPath()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpenVSA.slnx")))
                {
                    string project = Path.Combine(directory.FullName, "src", "OpenVSA.Ui");

                    string[] candidates = Directory
                        .GetFiles(project, "OpenVSA.exe", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .ToArray();

                    Assert.True(candidates.Length > 0, "No built shell under " + project);
                    return candidates[0];
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
