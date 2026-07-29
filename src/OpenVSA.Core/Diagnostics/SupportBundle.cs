using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenVSA.Core.Diagnostics
{
    /// <summary>
    /// Gathers logs, configuration and version information into one document
    /// (<c>REQ-NFR-034</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It redacts nothing silently.</strong> That is the clause worth building the type
    /// around. A bundle that quietly dropped a connection string would be safe and useless — the
    /// person reading it cannot tell the difference between a setting that was absent and one that
    /// was removed, and will diagnose against a picture that is missing exactly the interesting
    /// part. So everything omitted is listed, by name, with why.
    /// </para>
    /// <para>
    /// Text rather than an archive: one file in a single action is what the requirement asks for,
    /// and a support bundle that needs a tool to open it arrives as an attachment nobody reads.
    /// </para>
    /// </remarks>
    public sealed class SupportBundle
    {
        private readonly List<KeyValuePair<string, string>> _configuration =
            new List<KeyValuePair<string, string>>();

        private readonly List<KeyValuePair<string, string>> _omitted =
            new List<KeyValuePair<string, string>>();

        private readonly List<KeyValuePair<string, string>> _versions =
            new List<KeyValuePair<string, string>>();

        /// <summary>Names that are never included, and the reason each is withheld.</summary>
        /// <remarks>
        /// Matched as a substring and case-insensitively, because the failure to avoid is a secret
        /// escaping under a name nobody thought of — <c>SyncfusionKey</c>, <c>Syncfusion.Licence</c>
        /// and <c>licenseKey</c> should all be caught by one rule.
        /// </remarks>
        public static readonly IReadOnlyDictionary<string, string> Withheld =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["key"] = "may be a licence or API key",
                ["secret"] = "named as a secret",
                ["password"] = "a password",
                ["token"] = "may be an access token",
                ["connectionstring"] = "may embed credentials",
            };

        /// <summary>Adds a configuration setting, withholding it if its name says to.</summary>
        /// <param name="name">The setting's name.</param>
        /// <param name="value">Its value.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        public void AddSetting(string name, string value)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            string reason = ReasonToWithhold(name);

            if (reason != null)
            {
                _omitted.Add(new KeyValuePair<string, string>(name, reason));
                return;
            }

            _configuration.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
        }

        /// <summary>Adds a component's version.</summary>
        /// <param name="component">The component.</param>
        /// <param name="version">Its version.</param>
        /// <exception cref="ArgumentNullException"><paramref name="component"/> is null.</exception>
        public void AddVersion(string component, string version)
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            _versions.Add(new KeyValuePair<string, string>(component, version ?? string.Empty));
        }

        /// <summary>Settings that were withheld, and why.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Omitted => _omitted;

        /// <summary>Why a name would be withheld, or <c>null</c>.</summary>
        /// <param name="name">The setting's name.</param>
        public static string ReasonToWithhold(string name)
        {
            if (name == null)
            {
                return null;
            }

            foreach (KeyValuePair<string, string> rule in Withheld)
            {
                if (name.IndexOf(rule.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return rule.Value;
                }
            }

            return null;
        }

        /// <summary>Renders the bundle.</summary>
        /// <param name="log">The log to include, or <c>null</c> for none.</param>
        /// <param name="generatedUtc">When the bundle was made.</param>
        public string Render(Log log, DateTime generatedUtc)
        {
            var builder = new StringBuilder();

            builder.Append("OpenVSA support bundle\n");
            builder.Append("generated\t")
                .Append(generatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
                .Append('\n');
            builder.Append('\n');

            builder.Append("== versions ==\n");

            foreach (KeyValuePair<string, string> version in _versions)
            {
                builder.Append(version.Key).Append('\t').Append(version.Value).Append('\n');
            }

            builder.Append("\n== configuration ==\n");

            foreach (KeyValuePair<string, string> setting in _configuration)
            {
                builder.Append(setting.Key).Append('\t').Append(setting.Value).Append('\n');
            }

            // Before the log, not after: a reader who stops early should still have seen what is
            // missing, because that is what changes how the rest is read.
            builder.Append("\n== omitted ==\n");

            if (_omitted.Count == 0)
            {
                builder.Append("nothing was omitted\n");
            }
            else
            {
                foreach (KeyValuePair<string, string> omission in _omitted)
                {
                    builder.Append(omission.Key).Append('\t').Append(omission.Value).Append('\n');
                }
            }

            builder.Append("\n== log ==\n");

            if (log == null)
            {
                builder.Append("no log was supplied\n");
                return builder.ToString();
            }

            if (log.Dropped > 0)
            {
                builder.Append("# ").Append(log.Dropped)
                    .Append(" entries were dropped because the queue was full; the oldest go first\n");
            }

            foreach (LogEntry entry in log.Entries)
            {
                builder.Append(entry.ToLine()).Append('\n');
            }

            return builder.ToString();
        }
    }
}
