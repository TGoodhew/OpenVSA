using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenVSA.Measurement.State
{
    /// <summary>
    /// What a preset resets (<c>REQ-UI-061</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The File menu offers nine presets, and what separates them is not how they work but how much
    /// they reach. Naming the reach makes the difference between them checkable, and makes the one
    /// thing none of them may touch — <see cref="Hardware"/> — something a test can look for rather
    /// than something a reviewer has to notice.
    /// </para>
    /// </remarks>
    [Flags]
    public enum PresetCategory
    {
        /// <summary>Nothing.</summary>
        None = 0,

        /// <summary>Frequency, bandwidth, time, trigger, input and analysis settings.</summary>
        Measurement = 1,

        /// <summary>The kind of measurement being made — spectrum, vector, demodulation.</summary>
        Kind = 2,

        /// <summary>Trace formats, scaling and window arrangement.</summary>
        Traces = 4,

        /// <summary>Markers and their calculations.</summary>
        Markers = 8,

        /// <summary>Limit lines and tests.</summary>
        Limits = 16,

        /// <summary>The set of measurement contexts, back to one.</summary>
        Session = 32,

        /// <summary>Colours, typefaces and trace display options — the sidecar, not the state.</summary>
        DisplayPreferences = 64,

        /// <summary>Which toolbars are shown, and where.</summary>
        Toolbars = 128,

        /// <summary>
        /// Which instrument is open, its connection, the frequency reference and the source.
        /// </summary>
        /// <remarks>
        /// <strong>No preset variant includes this, and that is the point of it.</strong>
        /// <c>REQ-UI-061</c> says "Preset never changes the hardware setup", and a user who has
        /// spent ten minutes getting an instrument talking will press Preset expecting to keep it.
        /// </remarks>
        Hardware = 256,
    }

    /// <summary>
    /// The nine presets on <c>REQ-UI-061</c>'s File menu, in the order it lists them.
    /// </summary>
    public enum PresetVariant
    {
        /// <summary>The measurement, from the user's startup preset if they have saved one.</summary>
        Measurement = 0,

        /// <summary>The measurement, from the selected standard's settings.</summary>
        MeasurementToStandard,

        /// <summary>The measurement, from the documented defaults, whatever the user has saved.</summary>
        MeasurementToDefaults,

        /// <summary>Every measurement in the session, back to one at its defaults.</summary>
        Setup,

        /// <summary>Trace formats, scaling and arrangement.</summary>
        Traces,

        /// <summary>Everything above, together.</summary>
        ApplicationAndTraces,

        /// <summary>Colours, typefaces and trace display options.</summary>
        DisplayPreferences,

        /// <summary>Toolbar visibility and arrangement.</summary>
        Toolbars,

        /// <summary>All of it — except the hardware setup.</summary>
        FactoryDefaults,
    }

    /// <summary>
    /// The factory preset and the user's own (<c>REQ-STA-005</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The factory preset is the model's own defaults, not a second list of them.</strong>
    /// A preset assembled by writing out every setting again would be a copy of the defaults that
    /// could drift from them, and the drift would be invisible: a setting whose documented default
    /// changed would keep being preset to the old one. Constructing a fresh state instead means the
    /// two cannot disagree.
    /// </para>
    /// <para>
    /// <strong>It leaves the hardware setup alone</strong> (<c>REQ-UI-061</c>) — structurally,
    /// because a state carries no front end, no resource string and no connection. Preset is about
    /// how to measure, not about what is plugged in, and a preset that disconnected the instrument
    /// would be a preset nobody dared press.
    /// </para>
    /// </remarks>
    public static class Presets
    {
        /// <summary>What a factory preset is called.</summary>
        public const string FactoryName = "Factory preset";

        /// <summary>
        /// The name of the preset <see cref="PresetVariant.Measurement"/> starts from, if the user
        /// has saved one.
        /// </summary>
        /// <remarks>
        /// This is the distinction between <em>Preset Measurement</em> and <em>Preset Measurement
        /// to Defaults</em>: the first returns the measurement to the state the user chose to start
        /// from, the second ignores it and goes to the documented defaults. Without this, the two
        /// items are one item written twice — and the reason a user reaches for the second is
        /// precisely that the first no longer gives them a clean sheet.
        /// </remarks>
        public const string StartupName = "Startup";

        /// <summary>
        /// A state with every setting at the default this specification documents for it.
        /// </summary>
        /// <param name="contextName">The context to name the measurement after.</param>
        public static ApplicationState Factory(string contextName = "Measurement 1") =>
            ApplicationState.Default(contextName);

        /// <summary>
        /// The name of a preset variant, exactly as <c>REQ-UI-061</c>'s File menu writes it.
        /// </summary>
        /// <param name="variant">The variant.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known variant.</exception>
        public static string NameOf(PresetVariant variant)
        {
            switch (variant)
            {
                case PresetVariant.Measurement: return "Measurement";
                case PresetVariant.MeasurementToStandard: return "Measurement to Standard";
                case PresetVariant.MeasurementToDefaults: return "Measurement to Defaults";
                case PresetVariant.Setup: return "Setup";
                case PresetVariant.Traces: return "Traces";
                case PresetVariant.ApplicationAndTraces: return "Application and Traces";
                case PresetVariant.DisplayPreferences: return "Display Preferences";
                case PresetVariant.Toolbars: return "Toolbars";
                case PresetVariant.FactoryDefaults: return "Factory Defaults";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(variant), variant, "Not a known preset variant.");
            }
        }

        /// <summary>
        /// What a preset variant resets.
        /// </summary>
        /// <param name="variant">The variant.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known variant.</exception>
        /// <remarks>
        /// <para>
        /// <strong><see cref="PresetCategory.Hardware"/> is not returned by any of them, and that
        /// is the requirement.</strong> It is a member of the enumeration so that the separation
        /// can be asserted — a category nobody can name is a category nobody can check — and a test
        /// walks every variant looking for it.
        /// </para>
        /// <para>
        /// The three measurement variants differ in where they reset <em>to</em>, not in what they
        /// touch: see <see cref="StartupName"/> and <see cref="PresetCategory.Kind"/>.
        /// </para>
        /// </remarks>
        public static PresetCategory CategoriesOf(PresetVariant variant)
        {
            switch (variant)
            {
                case PresetVariant.Measurement:
                case PresetVariant.MeasurementToStandard:
                    return PresetCategory.Measurement;

                case PresetVariant.MeasurementToDefaults:
                    return PresetCategory.Measurement | PresetCategory.Kind;

                case PresetVariant.Setup:
                    return PresetCategory.Measurement | PresetCategory.Kind |
                           PresetCategory.Markers | PresetCategory.Limits | PresetCategory.Session;

                case PresetVariant.Traces:
                    return PresetCategory.Traces;

                case PresetVariant.ApplicationAndTraces:
                    return PresetCategory.Measurement | PresetCategory.Kind |
                           PresetCategory.Traces | PresetCategory.Markers |
                           PresetCategory.Limits | PresetCategory.Session;

                case PresetVariant.DisplayPreferences:
                    return PresetCategory.DisplayPreferences;

                case PresetVariant.Toolbars:
                    return PresetCategory.Toolbars;

                case PresetVariant.FactoryDefaults:
                    return PresetCategory.Measurement | PresetCategory.Kind |
                           PresetCategory.Traces | PresetCategory.Markers |
                           PresetCategory.Limits | PresetCategory.Session |
                           PresetCategory.DisplayPreferences | PresetCategory.Toolbars;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(variant), variant, "Not a known preset variant.");
            }
        }

        /// <summary>Every variant, in the order <c>REQ-UI-061</c>'s Preset submenu lists them.</summary>
        public static IReadOnlyList<PresetVariant> Variants { get; } =
            new System.Collections.ObjectModel.ReadOnlyCollection<PresetVariant>(
                (PresetVariant[])Enum.GetValues(typeof(PresetVariant)));

        /// <summary>
        /// Applies a preset variant to a state, returning what the state becomes.
        /// </summary>
        /// <param name="variant">Which preset was asked for.</param>
        /// <param name="current">The state as it stands.</param>
        /// <returns>The preset state.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Not a known variant.</exception>
        /// <remarks>
        /// <para>
        /// <strong>Reset from the defaults, preserve what is out of scope</strong> — rather than
        /// walking the state changing the things the variant names. Written the other way round, a
        /// setting added to the state later would keep its old value through every preset until
        /// somebody remembered to add it to the list, and nothing would report the omission.
        /// </para>
        /// <para>
        /// The hardware setup is copied back last and unconditionally. <c>REQ-UI-061</c> calls that
        /// separation out explicitly, and it is easy to lose: the frequency reference and the
        /// source live in the same state object as the settings a preset exists to clear.
        /// </para>
        /// <para>
        /// Sub-objects that are preserved are carried over by reference, not copied. The caller is
        /// replacing the state it passed in, so there is nothing left to alias.
        /// </para>
        /// </remarks>
        public static ApplicationState Apply(PresetVariant variant, ApplicationState current)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            PresetCategory scope = CategoriesOf(variant);

            var next = new ApplicationState
            {
                SchemaVersion = current.SchemaVersion,
                WrittenBy = current.WrittenBy,
                WrittenUtc = current.WrittenUtc,
                UnknownMembersJson = current.UnknownMembersJson,
            };

            // Session: back to the one measurement, keeping its name so that a recall still matches
            // the context it belongs to (REQ-STA-004 matches on the name, not the position).
            IEnumerable<MeasurementState> kept = Has(scope, PresetCategory.Session)
                ? current.Measurements.Take(1)
                : current.Measurements;

            foreach (MeasurementState measurement in kept)
            {
                next.Measurements.Add(Reset(measurement, scope));
            }

            if (next.Measurements.Count == 0)
            {
                next.Measurements.Add(new MeasurementState());
            }

            return next;
        }

        /// <summary>Whether a scope includes a category.</summary>
        /// <param name="scope">The scope.</param>
        /// <param name="category">The category.</param>
        public static bool Has(PresetCategory scope, PresetCategory category) =>
            (scope & category) == category;

        private static MeasurementState Reset(MeasurementState current, PresetCategory scope)
        {
            var next = new MeasurementState { ContextName = current.ContextName };

            if (!Has(scope, PresetCategory.Kind))
            {
                next.Kind = current.Kind;
            }

            if (!Has(scope, PresetCategory.Measurement))
            {
                next.CenterFrequencyHz = current.CenterFrequencyHz;
                next.SpanHz = current.SpanHz;
                next.ResolutionBandwidthHz = current.ResolutionBandwidthHz;
                next.ResolutionBandwidthIsAutomatic = current.ResolutionBandwidthIsAutomatic;
                next.Trigger = current.Trigger;
                next.Input = current.Input;
                next.Analysis = current.Analysis;
            }

            if (!Has(scope, PresetCategory.Traces))
            {
                next.Traces = current.Traces;
                next.Windows = current.Windows;
            }

            if (!Has(scope, PresetCategory.Markers))
            {
                next.Markers = current.Markers;
            }

            if (!Has(scope, PresetCategory.Limits))
            {
                next.LimitTests = current.LimitTests;
            }

            // The hardware setup, whatever the variant: REQ-UI-061's "Preset never changes the
            // hardware setup". The frequency reference and the source are the two parts of it that
            // a state carries at all - which front end is open, and its connection, are not in a
            // state to begin with.
            next.Source = current.Source;
            next.Input.ExternalReference = current.Input.ExternalReference;

            return next;
        }
    }

    /// <summary>
    /// The user's saved presets, held as state files in a directory (<c>REQ-STA-005</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Files rather than a database or a registry key, for the same reason the state format is
    /// text: a preset is something a user should be able to copy to another machine, put in version
    /// control, or send to somebody. Holding them anywhere less ordinary would make all three
    /// harder for no gain.
    /// </para>
    /// <para>
    /// Applying a preset is recalling the state it was captured from — the same code path, not a
    /// parallel one — which is what makes the requirement's equivalence true rather than intended.
    /// </para>
    /// </remarks>
    public sealed class PresetLibrary
    {
        private readonly string _directory;

        /// <summary>Opens a preset library in a directory, creating it if need be.</summary>
        /// <param name="directory">Where presets are kept.</param>
        /// <exception cref="ArgumentNullException"><paramref name="directory"/> is null or empty.</exception>
        public PresetLibrary(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentNullException(nameof(directory));
            }

            _directory = directory;
        }

        /// <summary>The default location: the user's application data.</summary>
        public static string DefaultDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenVSA",
                "Presets");

        /// <summary>Where this library keeps its files.</summary>
        public string Directory => _directory;

        /// <summary>The presets available, in name order.</summary>
        public IReadOnlyList<string> Names
        {
            get
            {
                if (!System.IO.Directory.Exists(_directory))
                {
                    return new string[0];
                }

                return System.IO.Directory
                    .EnumerateFiles(_directory, "*" + StateFile.Extension)
                    .Select(NameOfFile)
                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>
        /// Saves a state as a named preset, replacing one of the same name.
        /// </summary>
        /// <param name="name">The preset's name.</param>
        /// <param name="state">The state to capture.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The name cannot be a file name.</exception>
        public void Save(string name, ApplicationState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            string path = PathOf(name);

            System.IO.Directory.CreateDirectory(_directory);
            StateFile.Save(state, path);
        }

        /// <summary>
        /// Loads a preset.
        /// </summary>
        /// <param name="name">The preset's name.</param>
        /// <returns>The state it holds.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null or empty.</exception>
        /// <exception cref="ArgumentException">The name cannot be a file name.</exception>
        /// <exception cref="FileNotFoundException">There is no such preset.</exception>
        /// <exception cref="StateFormatException">The preset is not a readable state.</exception>
        public ApplicationState Load(string name)
        {
            string path = PathOf(name);

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("There is no preset named '" + name + "'.", path);
            }

            return StateFile.Load(path);
        }

        /// <summary>Whether a preset of that name exists.</summary>
        /// <param name="name">The preset's name.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null or empty.</exception>
        /// <exception cref="ArgumentException">The name cannot be a file name.</exception>
        public bool Contains(string name) => File.Exists(PathOf(name));

        /// <summary>
        /// Deletes a preset.
        /// </summary>
        /// <param name="name">The preset's name.</param>
        /// <returns><c>true</c> if there was one to delete.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null or empty.</exception>
        /// <exception cref="ArgumentException">The name cannot be a file name.</exception>
        public bool Delete(string name)
        {
            string path = PathOf(name);

            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }

        private string PathOf(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(
                    "A preset name cannot contain any of " +
                    new string(Path.GetInvalidFileNameChars().Where(c => !char.IsControl(c)).ToArray()) +
                    ".",
                    nameof(name));
            }

            return Path.Combine(_directory, name + StateFile.Extension);
        }

        private static string NameOfFile(string path)
        {
            string file = Path.GetFileName(path);
            return file.Substring(0, file.Length - StateFile.Extension.Length);
        }
    }
}
