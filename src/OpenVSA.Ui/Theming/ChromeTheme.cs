using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;

namespace OpenVSA.Ui.Theming
{
    /// <summary>
    /// One chrome theme: a name and the resources it defines (<c>REQ-UI-083</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A theme is a name and a dictionary, and deliberately nothing else.</strong>
    /// <c>REQ-UI-083</c>'s obligation is that adding a theme later is a matter of supplying another
    /// resource dictionary rather than reworking the rendering code, and the way that obligation is
    /// broken is by giving a theme <em>identity</em> that code can branch on — an
    /// <c>enum Theme { Light, Dark }</c>, or a boolean "is dark" threaded through. There is no such
    /// type here and an architecture test says there is not: the name is a string, it is used to
    /// find a dictionary, and no code chooses a colour from it.
    /// </para>
    /// <para>
    /// <see cref="MissingKeys"/> is what makes "a theme cannot be partially defined" a fact rather
    /// than a hope. A dictionary that omits a key does not fail loudly at the point of the omission
    /// — WPF simply resolves the reference to nothing and the control keeps its default brush,
    /// which reads as a styling mistake rather than as an incomplete theme.
    /// </para>
    /// </remarks>
    public sealed class ChromeTheme
    {
        private readonly ReadOnlyCollection<string> _missing;

        /// <summary>Creates a theme from a name and a dictionary.</summary>
        /// <param name="name">The theme's name, as the chooser shows it.</param>
        /// <param name="resources">What it defines; held by reference.</param>
        /// <param name="skin">
        /// The Syncfusion skin this theme is drawn with, or <c>null</c> for none. See
        /// <see cref="Skin"/>.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="resources"/> is null.</exception>
        public ChromeTheme(string name, ResourceDictionary resources, string skin = null)
        {
            Skin = string.IsNullOrEmpty(skin) ? null : skin.Trim();

            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
            {
                throw new ArgumentException("A theme needs a name.", nameof(name));
            }

            if (resources == null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            Name = name.Trim();
            Resources = resources;

            var missing = new List<string>();

            foreach (string key in ChromeKeys.All)
            {
                if (!resources.Contains(key))
                {
                    missing.Add(key);
                }
            }

            _missing = new ReadOnlyCollection<string>(missing);
        }

        /// <summary>The theme's name.</summary>
        public string Name { get; }

        /// <summary>What it defines.</summary>
        public ResourceDictionary Resources { get; }

        /// <summary>
        /// The Syncfusion skin this theme is drawn with, or <c>null</c> to leave the stock
        /// templates in place.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>This is a second dictionary, not a second mechanism.</strong> A skin name is
        /// carried by the theme in exactly the way the resource dictionary is: supplied at
        /// construction, applied without being inspected, and never compared against a literal to
        /// decide a colour. Adding a theme is still a matter of supplying resources — a custom
        /// theme may name a skin, or name none and get the stock templates, and no code changes
        /// either way.
        /// </para>
        /// <para>
        /// <strong>Why it exists at all.</strong> Three of the seventeen chrome keys could not
        /// reach the controls that need them, and measurement showed why: a
        /// <c>ListBoxItem</c>, a <c>TabItem</c> and a <c>MenuItem</c> hard-code their selection
        /// colours inside their own templates, so only a full template reaches them — and the
        /// Syncfusion pane captions are drawn by a control library that our dictionaries do not
        /// address at all. Across a Light/Dark swap the caption row moved by <em>zero</em> grey
        /// levels while our own menu strip moved 210. A skin supplies the templates we would
        /// otherwise have to own for ever, for both the stock controls and Syncfusion's own.
        /// </para>
        /// </remarks>
        public string Skin { get; }

        /// <summary>The keys of <see cref="ChromeKeys.All"/> this theme does not define.</summary>
        public IReadOnlyList<string> MissingKeys => _missing;

        /// <summary>Whether it defines every key a chrome theme must.</summary>
        public bool IsComplete => _missing.Count == 0;

        /// <inheritdoc />
        public override string ToString() =>
            Name + (IsComplete ? string.Empty : " (" + _missing.Count + " key(s) missing)");
    }
}
