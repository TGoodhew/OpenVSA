using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenVSA.PerformanceGate
{
    /// <summary>Identifies the machine this process is running on.</summary>
    /// <remarks>
    /// Read from the registry and <c>GlobalMemoryStatusEx</c> rather than through
    /// <c>System.Management</c>: a WMI query for one string would add an assembly reference and a
    /// second of start-up to a tool whose whole job is measuring how long things take.
    /// </remarks>
    public static class LocalMachine
    {
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        /// <summary>The class of the machine this is running on.</summary>
        /// <remarks>
        /// Falls back to a named-but-unhelpful processor string rather than throwing: a gate that
        /// cannot identify the machine should report an unrecognised machine, which is a state it
        /// already handles, not fail to start.
        /// </remarks>
        public static MachineClass Current()
        {
            return new MachineClass(ProcessorName(), Environment.ProcessorCount, MemoryGib());
        }

        private static string ProcessorName()
        {
            try
            {
                using (RegistryKey key = RegistryKey
                        .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string name = key == null ? null : key.GetValue("ProcessorNameString") as string;

                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }
            catch (Exception)
            {
                // Any failure here means the same thing as an absent value: the machine cannot be
                // identified, so it will be treated as unrecognised and its figures recorded
                // rather than judged.
            }

            return "unidentified processor";
        }

        private static int MemoryGib()
        {
            var status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));

            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0UL)
            {
                return 1;
            }

            // Rounded rather than truncated: a machine reporting 63.9 GiB is a 64 GiB machine, and
            // truncation would put it in a different class from an identical one reporting 64.0.
            return (int)Math.Max(1.0, Math.Round(status.TotalPhys / 1073741824.0));
        }
    }
}
