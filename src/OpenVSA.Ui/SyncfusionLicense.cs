using System;
using System.Configuration;
using Syncfusion.Licensing;

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
    /// <strong>This is about the SOURCE, not about the shipped binary.</strong> A release or
    /// installer build does embed the key, it is recoverable from the binary with a regular
    /// expression, and that is a deliberate decision rather than a hole in the one above. The
    /// reasoning, and what would justify revisiting it, is recorded beside the target that does the
    /// embedding in <c>OpenVSA.Ui.csproj</c>. Read that before acting on this paragraph.
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

        /// <summary>Whether this build carries a key embedded at build time.</summary>
        /// <remarks>
        /// <para>
        /// A release or installer build passes <c>/p:EmbedSyncfusionLicenseKey=true</c> and the
        /// build writes the key from its own environment into a generated file under <c>obj\</c>,
        /// which is git-ignored. The key is therefore **injected by the build and never
        /// committed**, and an end user needs no Syncfusion account of their own — which is what
        /// makes Syncfusion a development and build dependency rather than a runtime one for
        /// anybody but a contributor.
        /// </para>
        /// <para>
        /// It is the **last** source tried, behind the environment variable and
        /// <c>local.secrets.config</c>, so a developer's own key still wins on their own machine.
        /// </para>
        /// </remarks>
        public static bool HasEmbeddedKey => !string.IsNullOrWhiteSpace(SyncfusionEmbeddedKey.Value);

        /// <summary>Whether a key was found and registered.</summary>
        public static bool IsRegistered { get; private set; }

        /// <summary>
        /// Whether <see cref="Register"/> has run, whether or not it found a key.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="IsRegistered"/> on purpose, and the two answer different
        /// questions. "Did this process try?" is the one a test can assert on a machine with no key
        /// — CI has none — whereas asserting <see cref="IsRegistered"/> would pass on a developer's
        /// machine and fail on the build server for a reason that has nothing to do with the change.
        /// </remarks>
        public static bool RegistrationAttempted { get; private set; }

        /// <summary>
        /// Registers the licence key with Syncfusion, once per process.
        /// </summary>
        /// <returns>Whether a key was found and registered.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Every path that constructs a Syncfusion control must call this first.</strong>
        /// Registration used to live inline in <c>App</c>'s constructor, which meant it happened
        /// only for paths that went through <c>App</c> — and the test host does not: it starts its
        /// own STA thread and builds a <c>ShellWindow</c> directly. The result was a **modal** trial
        /// dialog inside the test run, which blocked the dispatcher; the snapshot soak rendered 1
        /// frame instead of thousands and failed, 49 s after a run that should take seconds.
        /// A licensing mistake surfacing as a hang in an unrelated test is exactly the sort of
        /// thing worth making structurally impossible, so the policy lives here and
        /// <c>NoUnregisteredSyncfusionHostsTests</c> enforces that every such path calls it.
        /// </para>
        /// <para>
        /// <strong>Idempotent and never throws.</strong> Called from several constructors, on
        /// whichever thread gets there first. A second call is a no-op; a failure to resolve a key
        /// leaves the application in trial mode rather than stopping it, which is the same choice
        /// <see cref="ResolveKey"/> makes and for the same reason.
        /// </para>
        /// </remarks>
        public static bool Register()
        {
            lock (Gate)
            {
                if (RegistrationAttempted)
                {
                    return IsRegistered;
                }

                RegistrationAttempted = true;

                string key = ResolveKey();

                if (string.IsNullOrEmpty(key))
                {
                    return false;
                }

                try
                {
                    SyncfusionLicenseProvider.RegisterLicense(key);
                    IsRegistered = true;
                }
                catch (Exception)
                {
                    // A malformed or wrong-version key must not stop the application launching.
                    // The banner is the consequence, and StatusMessage says what to do about it.
                    IsRegistered = false;
                }

                return IsRegistered;
            }
        }

        private static readonly object Gate = new object();

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

                if (!string.IsNullOrWhiteSpace(fromConfiguration))
                {
                    return fromConfiguration.Trim();
                }
            }
            catch (ConfigurationErrorsException)
            {
                // Fall through to the embedded key: a malformed configuration file must not stop
                // the application launching over what is at worst a cosmetic banner.
            }

            return string.IsNullOrWhiteSpace(SyncfusionEmbeddedKey.Value)
                ? null
                : SyncfusionEmbeddedKey.Value.Trim();
        }

        /// <summary>A short description of the licensing state, for the shell.</summary>
        public static string StatusMessage => IsRegistered
            ? "Syncfusion licence registered."
            : "No Syncfusion licence key found — running in trial mode. Set " +
              EnvironmentVariableName + ", or copy " + SecretsFileName + ".example to " +
              SecretsFileName + " and put your key in it.";
    }
}
