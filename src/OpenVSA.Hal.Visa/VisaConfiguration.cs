using System;
using System.Configuration;

namespace OpenVSA.Hal.Visa
{
    /// <summary>
    /// Where a VISA driver gets the resource it should open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Configuration, never a bus scan.</strong> On a bench with HP-IB extenders every
    /// GPIB address answers a scan whether an instrument is there or not — this machine's own
    /// resource manager reports all thirty. Choosing an instrument from that list would connect to
    /// whatever happened to be at an address, so the address is stated rather than discovered.
    /// </para>
    /// <para>
    /// An environment variable overrides the configuration file, so a bench can be pointed at a
    /// different instrument for one run without editing anything that is under source control.
    /// </para>
    /// </remarks>
    public static class VisaConfiguration
    {
        /// <summary>Prefix for the environment-variable form of a setting.</summary>
        public const string EnvironmentPrefix = "OPENVSA_";

        /// <summary>
        /// The resource name for a setting key, or a default.
        /// </summary>
        /// <param name="settingKey">The <c>appSettings</c> key.</param>
        /// <param name="fallback">Used when neither the environment nor configuration names one.</param>
        /// <returns>A VISA resource string.</returns>
        /// <exception cref="ArgumentException"><paramref name="settingKey"/> is missing.</exception>
        public static string ResourceFor(string settingKey, string fallback)
        {
            if (string.IsNullOrEmpty(settingKey))
            {
                throw new ArgumentException("A setting key is required.", nameof(settingKey));
            }

            string fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableFor(settingKey));

            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                return fromEnvironment;
            }

            try
            {
                string configured = ConfigurationManager.AppSettings[settingKey];

                if (!string.IsNullOrEmpty(configured))
                {
                    return configured;
                }
            }
            catch (ConfigurationException)
            {
                // A malformed configuration file must not stop the application starting; the
                // fallback is a documented default rather than a failure (REQ-NFR-032).
            }

            return fallback;
        }

        /// <summary>The environment-variable name a setting key maps to.</summary>
        /// <param name="settingKey">The <c>appSettings</c> key.</param>
        /// <remarks>
        /// <c>OpenVSA.Visa.E4406A.Resource</c> becomes <c>OPENVSA_VISA_E4406A_RESOURCE</c>: dots
        /// are not portable in environment-variable names on every shell.
        /// </remarks>
        public static string EnvironmentVariableFor(string settingKey) =>
            settingKey == null ? null : settingKey.Replace('.', '_').ToUpperInvariant();
    }
}
