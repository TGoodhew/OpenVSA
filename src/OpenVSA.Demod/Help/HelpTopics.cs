using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;

namespace OpenVSA.Demod.Help
{
    /// <summary>
    /// The user help that ships with the demodulator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why help text is compiled into the assembly.</strong> <c>REQ-DEM-001</c> requires the
    /// processing order to be documented "in code and in user help", and a help file that is
    /// installed alongside the program is a file that can go missing, be edited, or be left behind
    /// by an installer that was changed for another reason. Compiled in, the help a build carries is
    /// the help that build was written against, and the test that compares it with the declaration
    /// is testing the thing that ships.
    /// </para>
    /// <para>
    /// <strong>What this is not.</strong> It is not a help system. There is no viewer, no index and
    /// no context sensitivity; the Help menu of <c>REQ-UI-061</c> still says that this build carries
    /// no help content, and it is right, because one topic is not content. What this is, is the
    /// topic <c>REQ-DEM-001</c> asks for, shipped where a viewer will be able to find it.
    /// </para>
    /// </remarks>
    public static class HelpTopics
    {
        /// <summary>The topic describing the demodulation chain (<c>REQ-DEM-001</c>).</summary>
        public const string ProcessingOrder = "demodulation-processing-order";

        /// <summary>
        /// The two filters, the catalogue, and what the span costs (<c>REQ-DEM-020</c>,
        /// <c>REQ-DEM-021</c>, <c>REQ-DEM-023</c>).
        /// </summary>
        /// <remarks>
        /// Three requirements ask for something to be in the user help rather than only in the
        /// code: <c>REQ-DEM-020</c> wants the transmitter/receiver split explained,
        /// <c>REQ-DEM-021</c> wants the catalogue listed, and <c>REQ-DEM-023</c> wants the
        /// filter-span/accuracy trade reproduced "so the default is an informed choice". Tests
        /// assert all three are here, because a help page is the easiest thing in a product to let
        /// drift away from what the product does.
        /// </remarks>
        public const string Filters = "demodulation-filters";

        private static readonly ReadOnlyCollection<string> Topics =
            new ReadOnlyCollection<string>(new List<string> { ProcessingOrder, Filters });

        /// <summary>Every topic that ships, by name.</summary>
        public static IReadOnlyList<string> Names => Topics;

        /// <summary>The topic's text, as Markdown.</summary>
        /// <param name="name">One of <see cref="Names"/>.</param>
        /// <returns>The text.</returns>
        /// <exception cref="ArgumentException">There is no such topic.</exception>
        public static string Read(string name)
        {
            if (!Topics.Contains(name))
            {
                throw new ArgumentException(
                    "There is no help topic called \"" + name + "\". This build carries: " +
                    string.Join(", ", new List<string>(Topics).ToArray()) + ".",
                    nameof(name));
            }

            string resource = "OpenVSA.Demod.Help." + name + ".md";

            using (Stream stream =
                typeof(HelpTopics).GetTypeInfo().Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        "The help topic \"" + name + "\" is listed but was not embedded as " +
                        resource + ". The build is inconsistent with itself.");
                }

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
