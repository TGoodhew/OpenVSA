using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OpenVSA.Hal;
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
            IReadOnlyCollection<string> mapped = new string[0];

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

                // BOTH lists, because neither is enough on its own (#423).
                //
                // Process.Modules lists what was loaded as a MODULE, which on .NET Framework means
                // native DLLs and NGen'd images only: an IL-only assembly is mapped as a section
                // and never appears. Measured — of ours, a running shell's module list contains
                // OpenVSA.exe and nothing else, while OpenVSA.Core.dll is certainly loaded. So the
                // Ivi.Visa half of this test could not fail, and had been passing for that reason
                // rather than for the intended one.
                //
                // The mapped-file list is the one that can. It walks the address space and asks
                // what file backs each mapping, which is how an IL assembly shows itself.
                foreach (ProcessModule module in process.Modules)
                {
                    loaded.Add(Path.GetFileName(module.FileName));
                }

                mapped = MappedFilesOf(process);
            }
            finally
            {
                Close(process);
            }

            // The control, and it is not decoration: without it the new check is exactly as
            // vacuous as the one it replaces. OpenVSA.Core is IL-only, has no native image, and is
            // certainly loaded by a shell that has drawn a window — so an enumeration that cannot
            // see it cannot see Ivi.Visa either, and its silence would mean nothing.
            //
            // The stronger control cannot live in CI, and was run by hand instead: the same
            // enumeration pointed at OpenVSA.Verify driving the bench reported
            //
            //     Ivi.Visa.dll, NationalInstruments.Visa.dll, nivisa64.dll, visaConfMgr.dll,
            //     OpenVSA.Hal.Visa.dll
            //
            // so this check does fail when there is something to fail on. It is not repeated here
            // because it needs VISA installed and an instrument answering, and a CI runner has
            // neither — a control that skips on the machine it matters on is not a control.
            Assert.Contains(
                mapped,
                f => f.Equals("OpenVSA.Core.dll", StringComparison.OrdinalIgnoreCase));

            string[] visa = loaded
                .Concat(mapped)
                .Where(m => m.IndexOf("Ivi.Visa", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.IndexOf("visa32", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.IndexOf("visa64", StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct()
                .ToArray();

            _output.WriteLine(
                loaded.Count + " modules and " + mapped.Count + " mapped files; VISA: " +
                (visa.Length == 0 ? "none" : string.Join(", ", visa)));

            _output.WriteLine("ours, mapped: " + string.Join(", ", mapped
                .Where(f => f.StartsWith("OpenVSA", StringComparison.OrdinalIgnoreCase))));

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
        public void TheSimulatorAndFilePlaybackFrontEndsAreActuallyDiscovered()
        {
            // "The simulator and file-playback front ends are available."
            //
            // **Asked of the registry, not of the file system.** The first version of this test
            // checked that OpenVSA.Hal.File.dll sat in the FrontEnds folder — and it did, while
            // being an empty project containing no front end at all. The registry found two
            // providers where the product claimed three, and this test passed anyway. A
            // present-but-empty assembly is exactly the case the distinction exists to catch.
            string plugins = Path.Combine(Path.GetDirectoryName(ShellPath()), "FrontEnds");

            Assert.True(Directory.Exists(plugins), "No FrontEnds directory at " + plugins);

            var registry = new FrontEndRegistry();
            registry.ProbeDirectory(plugins);

            string[] names = registry.Providers.Select(p => p.DisplayName).ToArray();

            _output.WriteLine("discovered: " + string.Join(", ", names));

            foreach (FrontEndDiscoveryFailure failure in registry.Failures)
            {
                _output.WriteLine("failure: " + failure);
            }

            Assert.Contains(names, n => n.IndexOf("Simulated", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(names, n => n.IndexOf("File playback", StringComparison.OrdinalIgnoreCase) >= 0);
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

        /// <summary>
        /// Every file backing a mapping in a process, by file name (<c>#423</c>).
        /// </summary>
        /// <param name="process">The process, which must still be running.</param>
        /// <returns>Distinct file names, case-insensitively.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Why this and not <see cref="Process.Modules"/>.</strong> On .NET Framework the
        /// CLR maps an IL-only assembly as a section rather than loading it as a module, so it
        /// never reaches the module list — a running shell reports <c>OpenVSA.exe</c> and no other
        /// assembly of ours, while every one of them is loaded. <c>Ivi.Visa</c> is IL-only and
        /// GAC-resident, so the check that mattered here was looking in the one list that could
        /// not contain it.
        /// </para>
        /// <para>
        /// Walking the address space and asking what file backs each region finds them all,
        /// including native images and native DLLs, so this is a superset of the module list
        /// rather than an alternative to it.
        /// </para>
        /// </remarks>
        internal static IReadOnlyCollection<string> MappedFilesOf(Process process)
        {
            var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            IntPtr handle = OpenProcess(
                ProcessQueryInformation | ProcessVmRead, false, process.Id);

            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Could not open the shell process to read its mappings: error " +
                    Marshal.GetLastWin32Error() + ".");
            }

            try
            {
                var name = new StringBuilder(4096);
                var address = IntPtr.Zero;
                int size = Marshal.SizeOf(typeof(MemoryBasicInformation));

                MemoryBasicInformation region;

                while (VirtualQueryEx(handle, address, out region, (IntPtr)size) != IntPtr.Zero)
                {
                    if (region.Type == MemMapped || region.Type == MemImage)
                    {
                        name.Length = 0;

                        if (GetMappedFileName(handle, region.BaseAddress, name, name.Capacity) > 0)
                        {
                            files.Add(Path.GetFileName(name.ToString()));
                        }
                    }

                    long next = region.BaseAddress.ToInt64() + region.RegionSize.ToInt64();

                    // A region that does not advance would spin here for ever, and a hung test is
                    // a worse failure than the one being looked for.
                    if (next <= address.ToInt64())
                    {
                        break;
                    }

                    address = new IntPtr(next);
                }
            }
            finally
            {
                CloseHandle(handle);
            }

            return files;
        }

        private const int ProcessQueryInformation = 0x0400;
        private const int ProcessVmRead = 0x0010;
        private const int MemImage = 0x1000000;
        private const int MemMapped = 0x40000;

        /// <summary>
        /// The 64-bit layout, spelled out because the padding is part of it.
        /// </summary>
        /// <remarks>
        /// <c>REQ-NFR-001</c> makes every build x64, so there is one layout to get right rather
        /// than two — but its two alignment words are not optional: leaving them out shifts
        /// <see cref="Type"/> by eight bytes and every region reads as private.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint Alignment1;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Alignment2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(
            IntPtr process, IntPtr address, out MemoryBasicInformation buffer, IntPtr length);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "GetMappedFileNameW")]
        private static extern int GetMappedFileName(
            IntPtr process, IntPtr address, StringBuilder fileName, int size);

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
