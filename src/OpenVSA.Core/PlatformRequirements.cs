using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenVSA.Core
{
    /// <summary>
    /// The platform floor of <c>REQ-NFR-030</c>, and the message shown when it is not met.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement is that the application "refuses to run below Windows 10 21H2 or on a
    /// non-x64 process, <strong>with a message naming the unmet requirement rather than failing
    /// obscurely</strong>". The second half is the part worth code: an unsupported platform
    /// otherwise announces itself as a missing entry point or a <c>BadImageFormatException</c> from
    /// somewhere deep in the load, which tells the person in front of it nothing they can act on.
    /// </para>
    /// <para>
    /// In <c>OpenVSA.Core</c> and not in the shell, so the check is available to every entry point
    /// the product has — the shell, the verification harness, the benchmark host — rather than only
    /// the one that happens to be a window.
    /// </para>
    /// </remarks>
    public static class PlatformRequirements
    {
        /// <summary>Windows 10 21H2 is build 19044.</summary>
        /// <remarks>
        /// The build number, not the version string: Windows reports 10.0 for every release from
        /// Windows 10 through Windows 11, so a major/minor comparison cannot distinguish 21H2 from
        /// anything else and would pass on a build years too old.
        /// </remarks>
        public const int MinimumWindowsBuild = 19044;

        /// <summary>The framework release key for .NET Framework 4.7.2.</summary>
        public const int MinimumFrameworkRelease = 461808;

        /// <summary>
        /// Why the platform is unsuitable, or <c>null</c> when it is suitable.
        /// </summary>
        /// <remarks>
        /// Returns every unmet requirement rather than the first. Someone on a 32-bit build of an
        /// old Windows should learn both facts from one run, not discover the second after fixing
        /// the first.
        /// </remarks>
        public static string Unmet()
        {
            var problems = new StringBuilder();

            if (IntPtr.Size != 8)
            {
                Append(problems,
                    "OpenVSA requires a 64-bit process. This one is 32-bit, and a 32-bit address " +
                    "space cannot hold the capture buffers the product is built around — a " +
                    "30-second capture at 25.6 MS/s is 6.1 GB.");
            }

            if (!IsWindows())
            {
                Append(problems, "OpenVSA requires Windows 10 version 21H2 or later.");
            }
            else
            {
                int build = WindowsBuild();

                if (build > 0 && build < MinimumWindowsBuild)
                {
                    Append(problems,
                        "OpenVSA requires Windows 10 version 21H2 (build " + MinimumWindowsBuild +
                        ") or later. This is build " + build + ".");
                }
            }

            return problems.Length == 0 ? null : problems.ToString();
        }

        /// <summary>Whether the platform meets every requirement.</summary>
        public static bool AreMet() => Unmet() == null;

        /// <summary>The Windows build number, or 0 when it cannot be determined.</summary>
        /// <remarks>
        /// From <c>RtlGetVersion</c> rather than <see cref="Environment.OSVersion"/>. The managed
        /// property is subject to the application-compatibility shim: without a matching
        /// <c>supportedOS</c> entry in the manifest it reports 6.2 on every modern Windows, so a
        /// check built on it would refuse to run on the very systems it is meant to allow. The
        /// manifest here does declare Windows 10, but a check that only works because of a manifest
        /// entry elsewhere is a check waiting to break.
        /// </remarks>
        public static int WindowsBuild()
        {
            if (!IsWindows())
            {
                return 0;
            }

            var version = new OsVersionInfo();
            version.Size = (uint)Marshal.SizeOf(typeof(OsVersionInfo));

            try
            {
                return RtlGetVersion(ref version) == 0 ? (int)version.BuildNumber : 0;
            }
            catch (DllNotFoundException)
            {
                return 0;
            }
            catch (EntryPointNotFoundException)
            {
                return 0;
            }
        }

        private static bool IsWindows() =>
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        private static void Append(StringBuilder problems, string problem)
        {
            if (problems.Length > 0)
            {
                problems.Append(Environment.NewLine).Append(Environment.NewLine);
            }

            problems.Append(problem);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OsVersionInfo
        {
            public uint Size;
            public uint MajorVersion;
            public uint MinorVersion;
            public uint BuildNumber;
            public uint PlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string CsdVersion;
        }

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int RtlGetVersion(ref OsVersionInfo version);
    }
}
