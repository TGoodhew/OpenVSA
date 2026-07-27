using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace OpenVSA.Measurement.State
{
    /// <summary>
    /// A trace-math expression, saved apart from the state (<c>REQ-STA-002</c>).
    /// </summary>
    public sealed class TraceMathState
    {
        /// <summary>The trace the result is shown on.</summary>
        public string Trace { get; set; } = "A";

        /// <summary>The operator's name, as <c>TraceMath</c> registers it.</summary>
        public string Operator { get; set; } = "Subtract";

        /// <summary>The left operand: a trace letter, or a register name such as <c>D1</c>.</summary>
        public string Left { get; set; } = "A";

        /// <summary>The right operand, or empty for a constant or a unary operator.</summary>
        public string Right { get; set; } = string.Empty;

        /// <summary>Real part of the constant operand, in volts.</summary>
        public double ConstantReal { get; set; }

        /// <summary>Imaginary part of the constant operand, in volts.</summary>
        public double ConstantImaginary { get; set; }
    }

    /// <summary>
    /// A stored data register, saved apart from the state (<c>REQ-STA-002</c>).
    /// </summary>
    /// <remarks>
    /// The complex values are carried in full rather than as levels, because a register is used as
    /// an operand of trace math and a magnitude cannot be subtracted from a spectrum in any way
    /// that means anything (<c>REQ-DSP-046</c>).
    /// </remarks>
    public sealed class RegisterState
    {
        /// <summary>Register number, from 1.</summary>
        public int Register { get; set; } = 1;

        /// <summary>Frequency of point 0, in hertz.</summary>
        public double StartFrequencyHz { get; set; }

        /// <summary>Spacing between points, in hertz.</summary>
        public double BinWidthHz { get; set; } = 1.0;

        /// <summary>The window the trace was computed with.</summary>
        public string Window { get; set; } = "FlatTop";

        /// <summary>The window's equivalent noise bandwidth, in bins.</summary>
        public double EquivalentNoiseBandwidthBins { get; set; } = 1.0;

        /// <summary>Interleaved real and imaginary values, in volts; two per point.</summary>
        public List<float> Complex { get; set; } = new List<float>();
    }

    /// <summary>
    /// One tool window's docked position, size and open state (<c>REQ-UI-002</c>).
    /// </summary>
    /// <remarks>
    /// Keyed by the window's displayed name — <c>SCPI Log</c>, not an ordinal — so the file says
    /// what it means when read, and so reordering the shell's own enumeration cannot silently
    /// reassign one window's saved geometry to another.
    /// </remarks>
    public sealed class ToolWindowPlacement
    {
        /// <summary>The window's name, exactly as <c>REQ-UI-002</c> writes it.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Which edge it is docked to: <c>Left</c>, <c>Right</c> or <c>Bottom</c>.</summary>
        public string Side { get; set; } = "Right";

        /// <summary>Docked width in device-independent pixels, for a left or right dock.</summary>
        public double Width { get; set; } = 280.0;

        /// <summary>Docked height in device-independent pixels, for a bottom dock.</summary>
        public double Height { get; set; } = 180.0;

        /// <summary>Whether the window is open.</summary>
        public bool IsOpen { get; set; }
    }

    /// <summary>
    /// Display preferences, saved apart from the state (<c>REQ-STA-002</c>).
    /// </summary>
    public sealed class DisplayPreferencesState
    {
        /// <summary>Background behind the graticule, as packed ARGB.</summary>
        public uint TraceBackground { get; set; } = 0xFF101014;

        /// <summary>Graticule lines, as packed ARGB.</summary>
        public uint Grid { get; set; } = 0xFF3C3C46;

        /// <summary>Annotation text, as packed ARGB.</summary>
        public uint Annotation { get; set; } = 0xFFE0E0E6;

        /// <summary>Background outside the graticule, as packed ARGB.</summary>
        public uint AnnotationBackground { get; set; } = 0xFF1E1E24;

        /// <summary>Trace geometry, as packed ARGB.</summary>
        public uint Trace { get; set; } = 0xFFFFD200;

        /// <summary>Trace indicator messages, as packed ARGB.</summary>
        public uint Indicator { get; set; } = 0xFFFF6A3C;

        /// <summary>Annotation font size, in points.</summary>
        public double AnnotationFontSize { get; set; } = 11.0;

        /// <summary>Markers-window font family (<c>REQ-UI-033</c>).</summary>
        public string MarkerFontFamily { get; set; } = "Consolas";

        /// <summary>
        /// Where each tool window is docked, how big it is, and whether it is open
        /// (<c>REQ-UI-002</c>).
        /// </summary>
        /// <remarks>
        /// A display preference rather than part of a setup, for the reason the whole sidecar
        /// exists: recalling a colleague's measurement should not rearrange your windows. The
        /// requirement asks for this to persist "across a restart", and the display sidecar is what
        /// outlives a session.
        /// </remarks>
        public List<ToolWindowPlacement> ToolWindows { get; set; } = new List<ToolWindowPlacement>();
    }

    /// <summary>
    /// A loaded recording, referenced apart from the state (<c>REQ-STA-002</c>).
    /// </summary>
    public sealed class RecordingState
    {
        /// <summary>Path to the recording, or empty if none is loaded.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Playback position, in seconds from the start.</summary>
        public double PositionSeconds { get; set; }

        /// <summary>Whether playback loops at the end.</summary>
        public bool Loops { get; set; }
    }

    /// <summary>
    /// The four things a state deliberately does not contain (<c>REQ-STA-002</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Separate because their lifetimes are separate.</strong> A setup describes how to
    /// measure; a recording is data, a register holds a measurement already taken, math is an
    /// analysis over both, and display preferences belong to the person rather than to the
    /// measurement. Folding them into a state would mean recalling a colleague's setup repainted
    /// your display and threw away the reference trace you were comparing against.
    /// </para>
    /// <para>
    /// Each has its own save and recall, so none of them is lost by being excluded — which is the
    /// distinction between an exclusion and an omission.
    /// </para>
    /// </remarks>
    public sealed class SidecarState
    {
        /// <summary>The schema version this software writes for sidecars.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>Extension for a saved trace-math definition.</summary>
        public const string MathExtension = ".ovsa-math.json";

        /// <summary>Extension for saved data registers.</summary>
        public const string RegistersExtension = ".ovsa-registers.json";

        /// <summary>Extension for saved display preferences.</summary>
        public const string PreferencesExtension = ".ovsa-display.json";

        /// <summary>Extension for a saved recording reference.</summary>
        public const string RecordingExtension = ".ovsa-recording.json";

        /// <summary>Schema version of this sidecar.</summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Trace-math definitions.</summary>
        public List<TraceMathState> Math { get; set; } = new List<TraceMathState>();

        /// <summary>Data registers.</summary>
        public List<RegisterState> Registers { get; set; } = new List<RegisterState>();

        /// <summary>Display preferences.</summary>
        public DisplayPreferencesState Display { get; set; } = new DisplayPreferencesState();

        /// <summary>The loaded recording.</summary>
        public RecordingState Recording { get; set; } = new RecordingState();
    }

    /// <summary>
    /// Reads and writes the sidecars, each through its own command (<c>REQ-STA-002</c>).
    /// </summary>
    public static class SidecarFile
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.Indented,

            // As in StateFile: a collection with a default must be replaced by what the file says,
            // not have the file's contents appended to the default.
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        /// <summary>Writes any sidecar part as JSON.</summary>
        /// <typeparam name="T">The part's type.</typeparam>
        /// <param name="part">The part.</param>
        /// <exception cref="ArgumentNullException"><paramref name="part"/> is null.</exception>
        public static string Write<T>(T part)
        {
            if (part == null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            return JsonConvert.SerializeObject(part, Settings);
        }

        /// <summary>Reads any sidecar part from JSON.</summary>
        /// <typeparam name="T">The part's type.</typeparam>
        /// <param name="json">The JSON text.</param>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null or empty.</exception>
        /// <exception cref="StateFormatException">The text is not that part.</exception>
        public static T Read<T>(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentNullException(nameof(json));
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(json, Settings);
            }
            catch (JsonException failure)
            {
                throw new StateFormatException(
                    "The file is not a " + typeof(T).Name + ": " + failure.Message, failure);
            }
        }

        /// <summary>Saves a sidecar part to a file.</summary>
        /// <typeparam name="T">The part's type.</typeparam>
        /// <param name="part">The part.</param>
        /// <param name="path">Where to write it.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static void Save<T>(T part, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            File.WriteAllText(path, Write(part));
        }

        /// <summary>Loads a sidecar part from a file.</summary>
        /// <typeparam name="T">The part's type.</typeparam>
        /// <param name="path">The file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null or empty.</exception>
        /// <exception cref="StateFormatException">The file is not that part.</exception>
        public static T Load<T>(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            return Read<T>(File.ReadAllText(path));
        }
    }
}
