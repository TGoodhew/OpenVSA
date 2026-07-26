using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenVSA.Measurement.State
{
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
        /// A state with every setting at the default this specification documents for it.
        /// </summary>
        /// <param name="contextName">The context to name the measurement after.</param>
        public static ApplicationState Factory(string contextName = "Measurement 1") =>
            ApplicationState.Default(contextName);
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
