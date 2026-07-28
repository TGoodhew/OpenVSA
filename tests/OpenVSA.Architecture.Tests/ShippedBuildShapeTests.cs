using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// Properties of the build that ships, rather than of the code that produced it
    /// (<c>REQ-NFR-001</c>, <c>REQ-NFR-007</c>).
    /// </summary>
    /// <remarks>
    /// These are the requirements a well-meaning edit breaks silently. Declaring PerMonitorV2 looks
    /// like an upgrade and is not one on .NET Framework 4.7.2; switching a project to AnyCPU
    /// removes a ceiling nobody notices until a capture is 8 GB. Neither shows up in a unit test of
    /// any class, so they are asserted against the built artefacts.
    /// </remarks>
    public class ShippedBuildShapeTests
    {
        private const int RtManifest = 24;
        private const int CreateProcessManifestResourceId = 1;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the read manifest and machine types are written.</param>
        public ShippedBuildShapeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheShippedManifestDeclaresPerMonitorV1AndNotV2()
        {
            // REQ-NFR-007. Read out of the built executable rather than from app.manifest in the
            // source tree, because the failure this guards against includes the manifest not being
            // embedded at all — a source file that says the right thing and never reaches the
            // binary satisfies a test that reads the source file.
            string manifest = EmbeddedManifest(ShellPath());

            _output.WriteLine(manifest);

            XDocument document = XDocument.Parse(manifest);
            XNamespace settings = "http://schemas.microsoft.com/SMI/2005/WindowsSettings";

            string[] awareness = document.Descendants(settings + "dpiAware")
                .Select(e => e.Value.Trim())
                .ToArray();

            Assert.Single(awareness);

            // "True/PM" is per-monitor V1. "PerMonitorV2" belongs in <dpiAwareness>, which must be
            // absent entirely: WPF on .NET Framework 4.7.2 does not implement its non-client-area
            // scaling, child-HWND WM_DPICHANGED propagation or dialog scaling, so declaring it
            // promises behaviour the framework will not deliver.
            Assert.Equal("True/PM", awareness[0]);

            Assert.Empty(document.Descendants(settings + "dpiAwareness"));

            // Over element and attribute values, not over the file. The manifest's own comment
            // explains that PerMonitorV2 must not be declared, so a substring search across the
            // raw text fails on the sentence that says the right thing — which is what the first
            // draft of this test did.
            string declared = string.Concat(
                document.Descendants().Select(e => e.Value + " " +
                    string.Concat(e.Attributes().Select(a => a.Value + " "))));

            Assert.DoesNotContain("PerMonitorV2", declared, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheShippedConfigurationKeepsWpfScalingForDpiChanges()
        {
            // The other half of REQ-NFR-007, and it has to be present rather than merely not set
            // to true: the switch defaults differently across framework versions, so an absent
            // entry is a promise about a default rather than a decision.
            string configuration = File.ReadAllText(ShellPath() + ".config");

            XDocument document = XDocument.Parse(configuration);

            XElement[] switches = document
                .Descendants("add")
                .Where(e => (string)e.Attribute("key") == "Switch.System.Windows.DoNotScaleForDpiChanges")
                .ToArray();

            Assert.Single(switches);
            Assert.Equal("false", ((string)switches[0].Attribute("value")).ToLowerInvariant());

            // REQ-NFR-001's other half lives in the same file.
            Assert.Contains("gcAllowVeryLargeObjects", configuration);
        }

        [Fact]
        public void EveryShippedAssemblyIsX64()
        {
            // REQ-NFR-001: "the build produces no AnyCPU or x86 output". Read from the PE header,
            // because that is what the loader reads. An AnyCPU assembly is machine type 0x014C
            // with the IL-only flag, so checking for 0x8664 catches AnyCPU and x86 together.
            var wrong = new List<string>();
            var checkedFiles = new List<string>();

            foreach (string file in ShippedAssemblies())
            {
                ushort machine = MachineTypeOf(file);

                checkedFiles.Add(Path.GetFileName(file) + " 0x" + machine.ToString("X4"));

                if (machine != 0x8664)
                {
                    wrong.Add(Path.GetFileName(file) + " is machine type 0x" + machine.ToString("X4"));
                }
            }

            _output.WriteLine(string.Join(Environment.NewLine, checkedFiles));

            Assert.True(checkedFiles.Count > 5, "Only " + checkedFiles.Count + " assemblies were examined.");
            Assert.Empty(wrong);
        }

        [Fact]
        public void LargeObjectSupportIsConfiguredAndAnEightGigabyteArrayIsAllowed()
        {
            // REQ-NFR-001's ceiling: 2 000 000 000 floats is about 7.5 GiB, which needs 64-bit and
            // gcAllowVeryLargeObjects.
            //
            // **The allocation cannot be demonstrated in this process.** That setting is applied at
            // runtime start-up and per process, not per AppDomain, so the test assembly's own
            // config does not reach the vstest host: the array fails there with "array dimensions
            // exceeded supported range" however the product is built. Asserting only the config
            // file would report a capability nothing had exercised, so the check runs in
            // OpenVSA.Verify, which is configured the way the shipped application is.
            Assert.Equal(8, IntPtr.Size);

            string verify = SiblingTool("OpenVSA.Verify.exe");

            var start = new System.Diagnostics.ProcessStartInfo(verify, "--check-large-array")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();

                Assert.True(process.WaitForExit(120000), "The large-array check did not finish.");

                _output.WriteLine(output + errors);

                // 0 allocated it, 1 was refused, 2 means this machine has not the room — which is
                // the criterion's own "on a machine with adequate RAM" and is reported rather than
                // silently passed.
                if (process.ExitCode == 2)
                {
                    _output.WriteLine("NOT DEMONSTRATED: not enough memory on this machine.");
                    return;
                }

                Assert.Equal(0, process.ExitCode);
            }
        }

        // ---- Helpers -----------------------------------------------------------------------------

        /// <summary>The built shell, from its own output directory.</summary>
        private static string ShellPath()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpenVSA.slnx")))
                {
                    // The test's own output path tail names the configuration, so the shell built
                    // alongside it is the one to read.
                    string tail = AppDomain.CurrentDomain.BaseDirectory
                        .Substring(AppDomain.CurrentDomain.BaseDirectory
                            .IndexOf("bin", StringComparison.OrdinalIgnoreCase));

                    string shell = Path.Combine(
                        directory.FullName, "src", "OpenVSA.Ui", tail, "OpenVSA.exe");

                    Assert.True(File.Exists(shell), "No built shell at " + shell);
                    return shell;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not find the repository root.");
        }

        /// <summary>A built tool in its own output directory, matching this test's configuration.</summary>
        private static string SiblingTool(string fileName)
        {
            string shell = ShellPath();
            string configurationTail = Path.GetDirectoryName(shell);
            int bin = configurationTail.IndexOf("bin", StringComparison.OrdinalIgnoreCase);

            string root = Path.GetFullPath(Path.Combine(configurationTail.Substring(0, bin), "..", ".."));

            string tool = Path.Combine(
                root, "src", Path.GetFileNameWithoutExtension(fileName),
                configurationTail.Substring(bin), fileName);

            Assert.True(File.Exists(tool), "No built tool at " + tool);
            return tool;
        }

        /// <summary>Every OpenVSA assembly beside the built shell.</summary>
        private static IEnumerable<string> ShippedAssemblies()
        {
            string directory = Path.GetDirectoryName(ShellPath());

            return Directory.GetFiles(directory, "OpenVSA*.dll")
                .Concat(new[] { ShellPath() })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The PE header's machine type.</summary>
        private static ushort MachineTypeOf(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                stream.Position = 0x3C;
                stream.Position = reader.ReadUInt32();

                uint signature = reader.ReadUInt32();

                if (signature != 0x00004550u)
                {
                    throw new InvalidDataException(path + " has no PE signature.");
                }

                return reader.ReadUInt16();
            }
        }

        // ---- Win32: reading the manifest the loader would read ------------------------------------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        /// <summary>The RT_MANIFEST resource embedded in a binary.</summary>
        /// <remarks>
        /// <c>LOAD_LIBRARY_AS_DATAFILE</c> (0x02), so a managed executable can be opened for its
        /// resources without being run or its imports resolved.
        /// </remarks>
        private static string EmbeddedManifest(string path)
        {
            IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x02);

            Assert.True(module != IntPtr.Zero, "Could not open " + path + " for resources.");

            try
            {
                IntPtr resource = FindResource(
                    module,
                    new IntPtr(CreateProcessManifestResourceId),
                    new IntPtr(RtManifest));

                Assert.True(
                    resource != IntPtr.Zero,
                    "No RT_MANIFEST in " + Path.GetFileName(path) +
                    ". app.manifest is not reaching the built executable, so nothing it says is in force.");

                uint size = SizeofResource(module, resource);
                IntPtr handle = LoadResource(module, resource);
                IntPtr bytes = LockResource(handle);

                Assert.True(size > 0u && bytes != IntPtr.Zero, "The manifest resource is empty.");

                var buffer = new byte[size];
                Marshal.Copy(bytes, buffer, 0, (int)size);

                return new UTF8Encoding(false).GetString(buffer).TrimStart('﻿');
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        private static ulong PhysicalMemoryBytes()
        {
            var status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));

            return GlobalMemoryStatusEx(ref status) ? status.TotalPhys : 0UL;
        }
    }
}
