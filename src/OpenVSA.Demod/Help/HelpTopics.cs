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

        /// <summary>
        /// What the error metrics are relative to, and the ambiguity they cannot resolve
        /// (<c>REQ-DEM-061</c>, <c>REQ-DEM-067a</c>).
        /// </summary>
        /// <remarks>
        /// Two requirements ask for something in the user help rather than only in the code.
        /// <c>REQ-DEM-061</c> wants the EVM normalisation stated rather than inherited silently,
        /// because it is the commonest reason two instruments appear to disagree about the same
        /// signal. <c>REQ-DEM-067a</c> wants the gain-imbalance/quadrature-error ambiguity
        /// documented, because it is a property of the geometry that no estimator can resolve and a
        /// user who does not know about it will look for it in the hardware.
        /// </remarks>
        public const string ErrorMetrics = "demodulation-error-metrics";

        /// <summary>
        /// The equaliser's controls, and what its filter length buys (<c>REQ-DEM-051</c>,
        /// <c>REQ-DEM-052</c>).
        /// </summary>
        /// <remarks>
        /// <c>REQ-DEM-052</c> instructs that the length-to-tap-count relationship be stated where
        /// the user will meet it, calling it "a frequent source of confusion", and
        /// <c>REQ-DEM-051</c>'s three modes are distinctions no control label can carry on its own —
        /// Hold in particular still applies its coefficients, which is not what "hold" suggests to
        /// everyone who reads it.
        /// </remarks>
        public const string Equaliser = "demodulation-equaliser";

        /// <summary>
        /// AM, FM and PM: the detectors, what is measured, and the interpretations
        /// (<c>REQ-DEM-010a</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>REQ-DEM-010a</c> does not itself ask for a help page. This one exists because two of
        /// the numbers it asks for could not be produced without deciding something the
        /// specification left open, and a decision that changes a measurement belongs where the
        /// person reading the measurement will meet it — not only in an issue comment.
        /// </para>
        /// <para>
        /// <strong>The two are the analysis window and the residual definition.</strong> A Hann
        /// window's sidelobes would cap SINAD near 40 dB and no detector whatever could then meet
        /// the requirement's 60; Blackman-Harris was chosen for that reason and the page says so.
        /// And "residual FM/AM" is named as a result without being defined, so the page states what
        /// this build removes to arrive at it.
        /// </para>
        /// </remarks>
        public const string Analog = "demodulation-analog";

        /// <summary>
        /// The Result Length, the 20 % wider pre-demodulation window, and the offset-format limit
        /// (<c>REQ-DEM-032</c>, <c>REQ-DEM-013</c>, <c>REQ-DEM-031</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Same reasoning as <see cref="Analog"/>. <c>REQ-DEM-032</c> gives Spectrum and
        /// Instantaneous Spectrum the SAME window, so what distinguishes them had to be decided —
        /// this build makes it the estimator, averaged against not averaged, and the page says that
        /// is a reading rather than a specification.
        /// </para>
        /// <para>
        /// It also carries two things a user would otherwise have to discover: that displaying a
        /// pre-demodulation trace cannot change a result, and that a short Result Length reads about
        /// 1.4 % higher than a long one because of where the result window starts rather than
        /// because of its length.
        /// </para>
        /// </remarks>
        public const string ResultWindow = "demodulation-result-window";

        private static readonly ReadOnlyCollection<string> Topics =
            new ReadOnlyCollection<string>(
                new List<string>
                {
                    ProcessingOrder,
                    Filters,
                    ErrorMetrics,
                    Equaliser,
                    Analog,
                    ResultWindow,
                });

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
