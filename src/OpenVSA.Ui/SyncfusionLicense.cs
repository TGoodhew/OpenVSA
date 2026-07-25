using System;
using System.Configuration;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Resolves the Syncfusion licence key, which the controls require before any of them is
    /// constructed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The key never enters source control.</strong> It is a per-developer credential and
    /// this is a public repository, so a key in the tree is a leaked credential regardless of what
    /// it costs. Both sources below are outside the tree: the environment variable belongs to the
    /// machine, and <c>local.secrets.config</c> is git-ignored and merged into
    /// <c>appSettings</c> by the <c>file</c> attribute in <c>App.config</c>.
    /// </para>
    /// <para>
    /// <strong>A missing key is not an error.</strong> Registration is skipped and the application
    /// launches in trial mode. A contributor who has cloned the repository and not yet obtained a
    /// free Community key still gets a working build to develop against. This matters because
    /// OpenVSA ships as one free edition with everything included — the key is a build-time step
    /// for contributors, never a gate on anything a user receives.
    /// </para>
    /// </remarks>
    public static class SyncfusionLicense
    {
        /// <summary>Environment variable checked first for the licence key.</summary>
        public const string EnvironmentVariableName = "SYNCFUSION_LICENSE_KEY";

        /// <summary>The <c>appSettings</c> key checked if the environment variable is unset.</summary>
        public const string AppSettingsKeyName = "SyncfusionLicenseKey";

        /// <summary>The git-ignored file that <c>App.config</c> merges into <c>appSettings</c>.</summary>
        public const string SecretsFileName = "local.secrets.config";

        /// <summary>Whether a key was found and registered.</summary>
        public static bool IsRegistered { get; internal set; }

        /// <summary>
        /// Resolves the licence key from the environment, then from <c>appSettings</c>.
        /// </summary>
        /// <returns>The key, or <c>null</c> if neither source supplies one.</returns>
        /// <remarks>
        /// Never throws. A malformed configuration file must not stop the application launching
        /// over what is at worst a cosmetic banner.
        /// </remarks>
        public static string ResolveKey()
        {
            string fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment.Trim();
            }

            try
            {
                string fromConfiguration = ConfigurationManager.AppSettings[AppSettingsKeyName];
                return string.IsNullOrWhiteSpace(fromConfiguration) ? null : fromConfiguration.Trim();
            }
            catch (ConfigurationErrorsException)
            {
                return null;
            }
        }

        /// <summary>A short description of the licensing state, for the shell.</summary>
        public static string StatusMessage => IsRegistered
            ? "Syncfusion licence registered."
            : "No Syncfusion licence key found — running in trial mode. Set " +
              EnvironmentVariableName + ", or copy " + SecretsFileName + ".example to " +
              SecretsFileName + " and put your key in it.";
    }
}
