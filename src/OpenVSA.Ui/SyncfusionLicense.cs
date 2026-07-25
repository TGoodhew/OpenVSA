using System;
using System.IO;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Registers the Syncfusion licence key, which the controls require at start-up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The key is never committed.</strong> It is a per-developer credential, and OpenVSA
    /// is a public repository — a key in source control is a leaked credential regardless of what
    /// it costs. It is read from the <c>SYNCFUSION_LICENSE_KEY</c> environment variable, or from a
    /// <c>syncfusion.license</c> file beside the executable, both of which stay out of the tree.
    /// </para>
    /// <para>
    /// <strong>A missing key is not fatal.</strong> The application starts and runs; the controls
    /// show their unlicensed banner. That is the honest degradation: a contributor who has cloned
    /// the repository and not yet registered for a free Community key still gets a working build
    /// to develop against, and finds out why the banner is there from
    /// <see cref="StatusMessage"/> rather than from a crash on a machine that was fine yesterday.
    /// This matters because OpenVSA ships as one free edition with everything included — the
    /// licence is a build-time step for contributors, never a gate on what a user receives.
    /// </para>
    /// </remarks>
    public static class SyncfusionLicense
    {
        /// <summary>Environment variable holding the licence key.</summary>
        public const string EnvironmentVariableName = "SYNCFUSION_LICENSE_KEY";

        /// <summary>File beside the executable holding the licence key, if the variable is unset.</summary>
        public const string FileName = "syncfusion.license";

        /// <summary>What happened when the licence was registered, for the status bar and logs.</summary>
        public static string StatusMessage { get; private set; } = "Syncfusion licence not yet registered.";

        /// <summary>Whether a key was found and registered.</summary>
        public static bool IsRegistered { get; private set; }

        /// <summary>
        /// Finds and registers the licence key. Safe to call once at start-up; never throws.
        /// </summary>
        public static void Register()
        {
            try
            {
                string key = FindKey();

                if (string.IsNullOrWhiteSpace(key))
                {
                    StatusMessage =
                        "No Syncfusion licence key found — set " + EnvironmentVariableName +
                        " or place a " + FileName + " file beside the executable. " +
                        "The application runs without one; the controls will show a banner.";
                    return;
                }

                Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(key.Trim());
                IsRegistered = true;
                StatusMessage = "Syncfusion licence registered.";
            }
            catch (Exception e)
            {
                // Registration failing must not stop the shell opening. A licensing problem is a
                // cosmetic one; refusing to start over it would be worse than the banner.
                StatusMessage = "Syncfusion licence could not be registered: " + e.Message;
            }
        }

        private static string FindKey()
        {
            string fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
