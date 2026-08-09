using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenVSA.Ui.Bench
{
    /// <summary>What the source is being asked to produce.</summary>
    public enum StimulusKind
    {
        /// <summary>An unmodulated carrier.</summary>
        ContinuousWave = 0,

        /// <summary>A comb of equal tones centred on the carrier.</summary>
        Multitone,

        /// <summary>Band-limited noise of a stated total power.</summary>
        Noise,
    }

    /// <summary>
    /// Everything the test signal source panel does, with no window around it (issue #393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Separated for <c>ConnectionListing</c>'s reason.</strong> What the panel is judged on
    /// — that the ranges are the instrument's, that a coercion is reported rather than swallowed,
    /// that an instrument error reaches the event log and not a dialog — is all decided here and
    /// asserted without a visual tree. What is left in the window is showing it.
    /// </para>
    /// <para>
    /// <strong>Nothing here names an instrument, and nothing here knows a number.</strong> Every
    /// bound offered to the user comes from <see cref="StimulusSource.ReadLimits"/> or from a
    /// capability the source declares, which is <c>REQ-HAL-002</c>'s discipline applied to the
    /// generator side of the bench.
    /// </para>
    /// <para>
    /// <strong>A limit the source will not state is not enforced.</strong> The alternative is to
    /// substitute a plausible one, and a plausible one is a limit belonging to some other
    /// instrument: it would refuse settings this source can honour and accept ones it cannot. An
    /// unstated bound is left to the instrument, which clips and says so — and the saying so is
    /// what <see cref="Apply"/> reports.
    /// </para>
    /// </remarks>
    public sealed class SourceControlModel
    {
        /// <summary>
        /// How far a read-back may differ from the request before it is called a coercion.
        /// </summary>
        /// <remarks>
        /// <strong>Small enough to be a coercion rather than an artefact.</strong> A value is sent
        /// as a round-trippable decimal and read back through a parse, so a difference of one
        /// part in 10^12 is arithmetic and anything larger is the instrument. At a 1 GHz carrier
        /// this is a thousandth of a hertz, so a real coercion — the 0.02 dB level step, a spacing
        /// quantised to a sample clock — is reported and floating-point noise is not.
        /// </remarks>
        public const double CoercionTolerance = 1e-12;

        private readonly StimulusRegistry _registry;
        private readonly Action<string> _log;

        private StimulusSource _source;
        private SourceLimits _limits = SourceLimits.Unknown;

        /// <summary>
        /// Creates the model over a registry and the event log to report into.
        /// </summary>
        /// <param name="registry">Discovered sources.</param>
        /// <param name="log">Takes each line; the shell passes the event log's appender.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        /// <remarks>
        /// The log is supplied rather than reached for, because issue #393 requires coercions and
        /// instrument errors to "surface in the event log rather than a dialog" — and a model that
        /// held a dialog could not be asserted without one.
        /// </remarks>
        public SourceControlModel(StimulusRegistry registry, Action<string> log)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>The sources that can be offered.</summary>
        public IReadOnlyList<StimulusDescriptor> Sources => _registry.Sources;

        /// <summary>Why no source can be opened, or an empty string when one can.</summary>
        public string UnavailableReason => _registry.UnavailableReason;

        /// <summary>The open source, or null.</summary>
        public StimulusSource Source => _source;

        /// <summary>Whether a source is open.</summary>
        public bool IsConnected => _source != null;

        /// <summary>What the open source says it can produce.</summary>
        public SourceLimits Limits => _limits;

        /// <summary>
        /// Opens a source and reads its limits.
        /// </summary>
        /// <param name="descriptor">Which source.</param>
        /// <param name="resource">The address, for a source that needs one.</param>
        /// <returns>Whether it opened.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
        /// <remarks>
        /// A failure to open is reported and returns false. It is the ordinary case on a bench —
        /// the instrument is off, or the address has moved — and it is not an application error.
        /// </remarks>
        public bool Connect(StimulusDescriptor descriptor, string resource)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            Disconnect();

            StimulusSource opened;

            try
            {
                opened = descriptor.Create(resource);
                opened.Connect();
            }
            catch (Exception failure)
            {
                _log("The test signal source could not be opened: " + failure.Message);
                return false;
            }

            _source = opened;

            try
            {
                _limits = opened.ReadLimits();
            }
            catch (Exception failure)
            {
                // A source that will not state its limits is still usable; the panel simply cannot
                // range its entry fields, and says so rather than inventing bounds.
                _limits = SourceLimits.Unknown;
                _log("The test signal source did not report its limits: " + failure.Message);
            }

            _log("Test signal source open: " + opened.Identity + ". " + DescribeLimits());

            return true;
        }

        /// <summary>Closes the source, turning its output off on the way.</summary>
        public void Disconnect()
        {
            StimulusSource source = _source;

            _source = null;
            _limits = SourceLimits.Unknown;

            if (source == null)
            {
                return;
            }

            source.Dispose();
            _log("Test signal source closed.");
        }

        /// <summary>How the panel describes the range it was given.</summary>
        /// <remarks>
        /// The range is named in the log at the moment it is read, so that a run recorded in the
        /// event log carries the bounds the settings were checked against — a panel that shows
        /// bounds only while it is open leaves a later reader unable to tell an accepted setting
        /// from an unchecked one.
        /// </remarks>
        public string DescribeLimits()
        {
            string frequency = _limits.HasFrequencyRange
                ? EngineeringText.Frequency(_limits.MinimumFrequencyHz) + " to " +
                  EngineeringText.Frequency(_limits.MaximumFrequencyHz)
                : "an unstated frequency range";

            string level = _limits.HasLevelRange
                ? Decibels(_limits.MinimumLevelDbm) + " to " + Decibels(_limits.MaximumLevelDbm)
                : "an unstated level range";

            return "It reports " + frequency + ", " + level + ".";
        }

        /// <summary>
        /// Whether a carrier is one this source stated it can produce.
        /// </summary>
        /// <param name="frequencyHz">The carrier, in hertz.</param>
        /// <returns>Why not, or <c>null</c> when it is acceptable.</returns>
        public string ValidateFrequency(double frequencyHz)
        {
            if (double.IsNaN(frequencyHz) || double.IsInfinity(frequencyHz))
            {
                return "A carrier frequency is required.";
            }

            if (!_limits.HasFrequencyRange)
            {
                return null;
            }

            return frequencyHz < _limits.MinimumFrequencyHz ||
                   frequencyHz > _limits.MaximumFrequencyHz
                ? "This source produces " + EngineeringText.Frequency(_limits.MinimumFrequencyHz) +
                  " to " + EngineeringText.Frequency(_limits.MaximumFrequencyHz) + "."
                : null;
        }

        /// <summary>
        /// Whether a level is one this source stated it can produce.
        /// </summary>
        /// <param name="levelDbm">The level, in dBm.</param>
        /// <returns>Why not, or <c>null</c> when it is acceptable.</returns>
        public string ValidateLevel(double levelDbm)
        {
            if (double.IsNaN(levelDbm) || double.IsInfinity(levelDbm))
            {
                return "An output level is required.";
            }

            if (!_limits.HasLevelRange)
            {
                return null;
            }

            return levelDbm < _limits.MinimumLevelDbm || levelDbm > _limits.MaximumLevelDbm
                ? "This source produces " + Decibels(_limits.MinimumLevelDbm) + " to " +
                  Decibels(_limits.MaximumLevelDbm) + "."
                : null;
        }

        /// <summary>
        /// Whether a tone count is one this source will produce.
        /// </summary>
        /// <param name="tones">How many tones.</param>
        /// <returns>Why not, or <c>null</c> when it is acceptable.</returns>
        public string ValidateToneCount(int tones)
        {
            if (_source == null || !_source.CanProduceMultitone)
            {
                return "This source does not produce a multitone comb.";
            }

            return tones < _source.MinimumTones || tones > _source.MaximumTones
                ? "This source produces " + _source.MinimumTones + " to " + _source.MaximumTones +
                  " tones."
                : null;
        }

        /// <summary>
        /// Whether a tone spacing can be asked for.
        /// </summary>
        /// <param name="spacingHz">Spacing between adjacent tones, in hertz.</param>
        /// <returns>Why not, or <c>null</c> when it is acceptable.</returns>
        /// <remarks>
        /// Only that it is positive. The sources this shell can find state no spacing limits, and
        /// a bound invented here would be a bound belonging to a different generator.
        /// </remarks>
        public string ValidateToneSpacing(double spacingHz) =>
            double.IsNaN(spacingHz) || spacingHz <= 0.0
                ? "A tone spacing greater than zero is required."
                : null;

        /// <summary>
        /// Whether a noise bandwidth is one this source will produce.
        /// </summary>
        /// <param name="bandwidthHz">Noise bandwidth, in hertz.</param>
        /// <returns>Why not, or <c>null</c> when it is acceptable.</returns>
        public string ValidateNoiseBandwidth(double bandwidthHz)
        {
            if (_source == null || !_source.CanProduceNoise)
            {
                return "This source does not produce band-limited noise.";
            }

            return bandwidthHz < _source.MinimumNoiseBandwidthHz ||
                   bandwidthHz > _source.MaximumNoiseBandwidthHz
                ? "This source produces noise " +
                  EngineeringText.Frequency(_source.MinimumNoiseBandwidthHz) + " to " +
                  EngineeringText.Frequency(_source.MaximumNoiseBandwidthHz) + " wide."
                : null;
        }

        /// <summary>
        /// Drives the source to a stimulus, and reports what it settled on.
        /// </summary>
        /// <param name="kind">Which stimulus.</param>
        /// <param name="frequencyHz">Carrier, or centre of the comb or band, in hertz.</param>
        /// <param name="levelDbm">Output level, in dBm.</param>
        /// <param name="toneCount">Tones, for a comb.</param>
        /// <param name="spacingHz">Tone spacing, for a comb, in hertz.</param>
        /// <param name="bandwidthHz">Noise bandwidth, in hertz.</param>
        /// <returns>Whether the source accepted it.</returns>
        /// <remarks>
        /// <strong>What it settled on, not what it was asked for.</strong> Every difference between
        /// the request and the read-back is reported: the generator quantises its level, and it
        /// clips rather than refusing a setting outside its range, so a panel reporting the request
        /// would leave a user certain of a stimulus the instrument is not producing. That is the
        /// same discipline the headless scenarios keep, and for the same reason.
        /// </remarks>
        public bool Apply(
            StimulusKind kind,
            double frequencyHz,
            double levelDbm,
            int toneCount,
            double spacingHz,
            double bandwidthHz)
        {
            StimulusSource source = _source;

            if (source == null)
            {
                _log("No test signal source is open.");
                return false;
            }

            try
            {
                switch (kind)
                {
                    case StimulusKind.ContinuousWave:
                        source.SetContinuousWave(frequencyHz, levelDbm);
                        break;

                    case StimulusKind.Multitone:
                        source.SetMultitone(frequencyHz, toneCount, spacingHz, levelDbm);
                        break;

                    case StimulusKind.Noise:
                        source.SetNoise(frequencyHz, bandwidthHz, levelDbm);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(kind), kind, "Not a stimulus this panel produces.");
                }
            }
            catch (Exception failure)
            {
                // The event log, not a dialog. An instrument error during a bench run is traffic to
                // be read alongside the rest of it, and a modal box would also stop the run.
                _log("The test signal source refused the " + Describe(kind) + ": " +
                     failure.Message);

                return false;
            }

            ReportCoercions(source, kind, frequencyHz, levelDbm, toneCount, spacingHz, bandwidthHz);

            return true;
        }

        /// <summary>
        /// Turns the output on or off.
        /// </summary>
        /// <param name="enabled">Whether RF should be on.</param>
        /// <returns>Whether the source accepted it.</returns>
        public bool SetOutput(bool enabled)
        {
            StimulusSource source = _source;

            if (source == null)
            {
                _log("No test signal source is open.");
                return false;
            }

            try
            {
                source.SetOutput(enabled);
            }
            catch (Exception failure)
            {
                _log("The test signal source refused to turn its output " +
                     (enabled ? "on" : "off") + ": " + failure.Message);

                return false;
            }

            _log("Test signal source output " + (source.IsOutputEnabled ? "on" : "off") + ".");

            return true;
        }

        /// <summary>Whether a read-back differs from the request by more than arithmetic.</summary>
        /// <param name="requested">What was asked for.</param>
        /// <param name="actual">What the source reported.</param>
        public static bool Differs(double requested, double actual)
        {
            if (double.IsNaN(requested) || double.IsNaN(actual))
            {
                return double.IsNaN(requested) != double.IsNaN(actual);
            }

            double scale = Math.Max(Math.Abs(requested), Math.Abs(actual));

            return Math.Abs(requested - actual) > CoercionTolerance * Math.Max(1.0, scale);
        }

        private void ReportCoercions(
            StimulusSource source,
            StimulusKind kind,
            double frequencyHz,
            double levelDbm,
            int toneCount,
            double spacingHz,
            double bandwidthHz)
        {
            var coercions = new List<string>();

            if (Differs(frequencyHz, source.FrequencyHz))
            {
                coercions.Add(Coercion("the carrier", frequencyHz, source.FrequencyHz, Hertz));
            }

            if (Differs(levelDbm, source.LevelDbm))
            {
                coercions.Add(Coercion("the level", levelDbm, source.LevelDbm, Decibels));
            }

            if (kind == StimulusKind.Multitone)
            {
                if (source.ToneCount != toneCount)
                {
                    coercions.Add(
                        "the tone count from " + toneCount + " to " + source.ToneCount);
                }

                if (Differs(spacingHz, source.ToneSpacingHz))
                {
                    coercions.Add(
                        Coercion("the tone spacing", spacingHz, source.ToneSpacingHz, Hertz));
                }
            }

            if (kind == StimulusKind.Noise && Differs(bandwidthHz, source.NoiseBandwidthHz))
            {
                coercions.Add(Coercion(
                    "the noise bandwidth", bandwidthHz, source.NoiseBandwidthHz, Hertz));
            }

            if (coercions.Count == 0)
            {
                _log("Test signal source set to " + Describe(kind) + " at " +
                     EngineeringText.Frequency(source.FrequencyHz) + ", " +
                     Decibels(source.LevelDbm) + ".");

                return;
            }

            _log("The test signal source coerced " + string.Join(", ", coercions.ToArray()) + ".");
        }

        private static string Describe(StimulusKind kind)
        {
            switch (kind)
            {
                case StimulusKind.ContinuousWave: return "carrier";
                case StimulusKind.Multitone: return "multitone comb";
                case StimulusKind.Noise: return "noise band";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(kind), kind, "Not a stimulus this panel produces.");
            }
        }

        /// <summary>
        /// Says a coercion in a way that shows two different numbers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>The reason this is not one call to a formatter.</strong> Both readings are
        /// rounded for display, and a coercion smaller than the rounding prints as "coerced the
        /// level from −13.78 dBm to −13.78 dBm" — a sentence that reports a difference and then
        /// hides it. That is not a hypothetical margin: the generator's level step is 0.02 dB
        /// against a two-decimal default, and a kilohertz of carrier coercion disappears entirely
        /// inside a gigahertz shown to three figures.
        /// </para>
        /// <para>
        /// So the precision is raised until the two differ, and stops there. The reader gets the
        /// fewest digits that state the fact, rather than every reading padded to the worst case.
        /// </para>
        /// </remarks>
        private static string Coercion(
            string what, double requested, double actual, Func<double, int, string> format)
        {
            string requestedText = format(requested, MinimumDecimals);
            string actualText = format(actual, MinimumDecimals);

            for (int decimals = MinimumDecimals + 1;
                 decimals <= MaximumDecimals &&
                 string.Equals(requestedText, actualText, StringComparison.Ordinal);
                 decimals++)
            {
                requestedText = format(requested, decimals);
                actualText = format(actual, decimals);
            }

            return what + " from " + requestedText + " to " + actualText;
        }

        /// <summary>Decimals a reading is shown to before a coercion needs more of them.</summary>
        private const int MinimumDecimals = 2;

        /// <summary>
        /// Decimals beyond which two readings are treated as indistinguishable on screen.
        /// </summary>
        /// <remarks>
        /// A double carries about 15 significant figures, so a mantissa shown to this many decimals
        /// is at the edge of what the reading itself means. A difference that survives
        /// <see cref="Differs"/> and still prints the same here is smaller than the instrument
        /// could have meant, and padding further would be showing arithmetic rather than a setting.
        /// </remarks>
        private const int MaximumDecimals = 9;

        private static string Hertz(double hertz, int decimals) =>
            EngineeringText.Frequency(hertz, decimals);

        private static string Decibels(double dbm, int decimals) =>
            dbm.ToString("0." + new string('#', decimals), CultureInfo.CurrentCulture) + " dBm";

        private static string Decibels(double dbm) => Decibels(dbm, MinimumDecimals);
    }
}
