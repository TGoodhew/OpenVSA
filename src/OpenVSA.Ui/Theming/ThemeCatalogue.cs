using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;

namespace OpenVSA.Ui.Theming
{
    /// <summary>
    /// The themes on offer, and the one in force (<c>REQ-UI-083</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two ship, and a third costs a dictionary.</strong> <see cref="Shipped"/> registers
    /// Light and Dark; <see cref="Add"/> takes any other. Nothing here decides a colour from a
    /// name — a name finds a dictionary and the dictionary answers every question after that,
    /// which is the whole of <c>REQ-UI-083</c>'s architectural obligation. The test that proves it
    /// supplies a third dictionary at runtime and asserts the application renders with it, with no
    /// product code changed; every weaker check passes on an implementation that has hard-coded
    /// the two.
    /// </para>
    /// <para>
    /// <strong>Applied by merging, and unmerged before the next.</strong> The theme's dictionary
    /// goes into the target's merged dictionaries and the previous one comes out, so a swap is a
    /// live change to a running application rather than something read once at start-up —
    /// "selectable with no restart" is the criterion, and this is what makes it true.
    /// </para>
    /// <para>
    /// <strong>The plot surface is not here.</strong> <c>REQ-UI-081</c> separates chrome from the
    /// colours of <c>REQ-UI-022</c>, so nothing this class installs can reach the graticule, the
    /// traces or the annotation. A test samples every one of those colours across a theme change
    /// and asserts they are identical.
    /// </para>
    /// </remarks>
    public sealed class ThemeCatalogue
    {
        /// <summary>The name of the light theme this build ships.</summary>
        /// <remarks>
        /// A name to look a dictionary up by, and used for nothing else. It is a constant so that a
        /// caller wanting the shipped default does not have to spell it, not so that code can
        /// branch on it — see the type's remarks and <c>ThemingArchitectureTests</c>.
        /// </remarks>
        public const string LightName = "Light";

        /// <summary>The name of the dark theme this build ships.</summary>
        public const string DarkName = "Dark";

        private readonly List<ChromeTheme> _themes = new List<ChromeTheme>();

        private ResourceDictionary _target;
        private ChromeTheme _installed;

        /// <summary>Raised when the theme in force changes.</summary>
        public event EventHandler Changed;

        /// <summary>The themes on offer, in the order they were registered.</summary>
        public IReadOnlyList<ChromeTheme> Themes => _themes;

        /// <summary>Their names, in the same order.</summary>
        public IReadOnlyList<string> Names
        {
            get
            {
                var names = new List<string>(_themes.Count);

                foreach (ChromeTheme theme in _themes)
                {
                    names.Add(theme.Name);
                }

                return names;
            }
        }

        /// <summary>The theme currently applied, or <c>null</c> before one has been.</summary>
        public ChromeTheme Current => _installed;

        /// <summary>The current theme's name, or an empty string.</summary>
        public string CurrentName => _installed == null ? string.Empty : _installed.Name;

        /// <summary>
        /// The catalogue this build ships: Light and Dark, and exactly those.
        /// </summary>
        /// <exception cref="InvalidOperationException">A shipped dictionary could not be loaded.</exception>
        public static ThemeCatalogue Shipped()
        {
            var catalogue = new ThemeCatalogue();

            catalogue.Add(new ChromeTheme(LightName, Load(LightName)));
            catalogue.Add(new ChromeTheme(DarkName, Load(DarkName)));

            return catalogue;
        }

        /// <summary>
        /// Registers a theme.
        /// </summary>
        /// <param name="theme">The theme.</param>
        /// <exception cref="ArgumentNullException"><paramref name="theme"/> is null.</exception>
        /// <exception cref="ArgumentException">A theme of that name is already registered.</exception>
        /// <remarks>
        /// Public because this is the route a later custom theme takes, and because the test that
        /// verifies the non-foreclosure obligation has to be able to take it without any product
        /// code changing. A theme is accepted whether or not it is complete; what an incomplete one
        /// costs is reported by <see cref="ChromeTheme.MissingKeys"/> rather than refused here,
        /// because refusing would make a theme written against a later version unusable rather than
        /// partly usable.
        /// </remarks>
        public void Add(ChromeTheme theme)
        {
            if (theme == null)
            {
                throw new ArgumentNullException(nameof(theme));
            }

            if (Find(theme.Name) != null)
            {
                throw new ArgumentException(
                    "There is already a theme named '" + theme.Name + "'.", nameof(theme));
            }

            _themes.Add(theme);
        }

        /// <summary>The theme of that name, or <c>null</c>.</summary>
        /// <param name="name">The theme's name; matched without regard to case.</param>
        public ChromeTheme Find(string name)
        {
            foreach (ChromeTheme theme in _themes)
            {
                if (string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return theme;
                }
            }

            return null;
        }

        /// <summary>
        /// Applies a theme to a resource dictionary, replacing whatever this catalogue put there.
        /// </summary>
        /// <param name="name">The theme's name.</param>
        /// <param name="target">
        /// Where to merge it; normally <c>Application.Current.Resources</c>, and a test's own
        /// dictionary when there is no application.
        /// </param>
        /// <returns>Whether a theme of that name was found and applied.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
        public bool Apply(string name, ResourceDictionary target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            ChromeTheme theme = Find(name);

            if (theme == null)
            {
                return false;
            }

            if (_target != null && _installed != null)
            {
                _target.MergedDictionaries.Remove(_installed.Resources);
            }

            target.MergedDictionaries.Add(theme.Resources);

            _target = target;
            _installed = theme;

            EventHandler handler = Changed;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }

            return true;
        }

        /// <summary>
        /// Loads a shipped theme's dictionary from this assembly.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A pack URI, because the dictionaries are XAML compiled into the assembly and that is how
        /// WPF addresses them. <see cref="Application.ResourceAssembly"/> is set when nothing else
        /// has set it: the pack scheme resolves <c>application:,,,</c> against the entry
        /// assembly, and a unit-test host has no entry assembly of ours — without this the shipped
        /// themes load in the application and throw in the tests that check them.
        /// </para>
        /// <para>
        /// The name reaches the URI and nothing else. This is the one place a theme's name is used
        /// to find something, which is the distinction <c>REQ-UI-083</c> draws: finding a
        /// dictionary by name is the mechanism, deciding a colour by name is the thing forbidden.
        /// </para>
        /// </remarks>
        private static ResourceDictionary Load(string name)
        {
            Assembly assembly = typeof(ThemeCatalogue).Assembly;

            if (Application.ResourceAssembly == null)
            {
                Application.ResourceAssembly = assembly;
            }

            var uri = new Uri(
                "pack://application:,,,/" + assembly.GetName().Name + ";component/Themes/" +
                name + ".xaml",
                UriKind.Absolute);

            try
            {
                return new ResourceDictionary { Source = uri };
            }
            catch (Exception failure)
            {
                throw new InvalidOperationException(
                    "The '" + name + "' theme could not be loaded from " + uri + ".", failure);
            }
        }
    }
}
