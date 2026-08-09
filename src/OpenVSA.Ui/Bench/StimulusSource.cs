using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace OpenVSA.Ui.Bench
{
    /// <summary>
    /// What a source says it can produce, as the shell sees it.
    /// </summary>
    /// <remarks>
    /// A limit the source will not answer for is <see cref="double.NaN"/>. The panel shows the
    /// range it was given and says nothing about the range it was not — see
    /// <see cref="SourceControlModel"/> for why an unstated limit is left unstated rather than
    /// filled in with a plausible one.
    /// </remarks>
    public sealed class SourceLimits
    {
        /// <summary>Every limit unknown.</summary>
        public static readonly SourceLimits Unknown =
            new SourceLimits(double.NaN, double.NaN, double.NaN, double.NaN);

        /// <summary>Creates a set of limits.</summary>
        /// <param name="minimumFrequencyHz">Lowest carrier, in hertz, or <c>NaN</c>.</param>
        /// <param name="maximumFrequencyHz">Highest carrier, in hertz, or <c>NaN</c>.</param>
        /// <param name="minimumLevelDbm">Lowest level, in dBm, or <c>NaN</c>.</param>
        /// <param name="maximumLevelDbm">Highest level, in dBm, or <c>NaN</c>.</param>
        public SourceLimits(
            double minimumFrequencyHz,
            double maximumFrequencyHz,
            double minimumLevelDbm,
            double maximumLevelDbm)
        {
            MinimumFrequencyHz = minimumFrequencyHz;
            MaximumFrequencyHz = maximumFrequencyHz;
            MinimumLevelDbm = minimumLevelDbm;
            MaximumLevelDbm = maximumLevelDbm;
        }

        /// <summary>Lowest carrier the source will produce, in hertz.</summary>
        public double MinimumFrequencyHz { get; }

        /// <summary>Highest carrier the source will produce, in hertz.</summary>
        public double MaximumFrequencyHz { get; }

        /// <summary>Lowest output level, in dBm.</summary>
        public double MinimumLevelDbm { get; }

        /// <summary>Highest output level, in dBm.</summary>
        public double MaximumLevelDbm { get; }

        /// <summary>Whether both frequency limits were had from the source.</summary>
        public bool HasFrequencyRange =>
            !double.IsNaN(MinimumFrequencyHz) && !double.IsNaN(MaximumFrequencyHz);

        /// <summary>Whether both level limits were had from the source.</summary>
        public bool HasLevelRange =>
            !double.IsNaN(MinimumLevelDbm) && !double.IsNaN(MaximumLevelDbm);
    }

    /// <summary>
    /// A test signal source the shell drives without referencing the assembly it came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every reflective call in this feature is in this file, and that is the point.</strong>
    /// <see cref="StimulusRegistry"/> explains why there is no interface to call through:
    /// <c>REQ-ARC-001</c> keeps test infrastructure out of the product's references and
    /// <c>REQ-NFR-032</c> keeps VISA off the start-up path. What is left is late binding, and late
    /// binding is worth confining to one place where its failure modes can be dealt with once —
    /// a missing member named rather than swallowed, and an instrument's own error message
    /// unwrapped out of the <see cref="TargetInvocationException"/> that reflection would otherwise
    /// wrap it in.
    /// </para>
    /// <para>
    /// <strong>Bound once, at discovery.</strong> <see cref="FirstUnbindableMember"/> runs before a
    /// source is ever offered, so a member that has been renamed disables that source with its name
    /// in the reason instead of producing a panel whose buttons do nothing.
    /// </para>
    /// <para>
    /// <strong>Three optional capabilities, each all-or-nothing.</strong> A source may or may not
    /// produce a comb, or noise, or say what its limits are — the harness models each as a separate
    /// interface for exactly that reason. Half a capability is treated as none of it: a source that
    /// has <c>SetMultitone</c> but not <c>MaximumTones</c> cannot be offered a tone count to
    /// validate against, and offering the control anyway would put an unchecked number on the wire.
    /// </para>
    /// </remarks>
    public sealed class StimulusSource : IDisposable
    {
        private readonly object _instance;

        private readonly PropertyInfo _displayName;
        private readonly PropertyInfo _isOutputEnabled;
        private readonly PropertyInfo _frequencyHz;
        private readonly PropertyInfo _levelDbm;
        private readonly MethodInfo _connect;
        private readonly MethodInfo _setContinuousWave;
        private readonly MethodInfo _setOutput;
        private readonly MethodInfo _refresh;
        private readonly MethodInfo _dispose;

        private readonly MethodInfo _readLimits;
        private readonly PropertyInfo[] _limitProperties;

        private readonly MethodInfo _setMultitone;
        private readonly PropertyInfo _minimumTones;
        private readonly PropertyInfo _maximumTones;
        private readonly PropertyInfo _toneCount;
        private readonly PropertyInfo _toneSpacingHz;

        private readonly MethodInfo _setNoise;
        private readonly PropertyInfo _minimumNoiseBandwidthHz;
        private readonly PropertyInfo _maximumNoiseBandwidthHz;
        private readonly PropertyInfo _noiseBandwidthHz;

        private bool _disposed;

        private StimulusSource(object instance, string displayName)
        {
            _instance = instance;
            Name = displayName;

            Type type = instance.GetType();

            _displayName = Property(type, "DisplayName", typeof(string));
            _isOutputEnabled = Property(type, "IsOutputEnabled", typeof(bool));
            _frequencyHz = Property(type, "FrequencyHz", typeof(double));
            _levelDbm = Property(type, "LevelDbm", typeof(double));

            _connect = Method(type, "Connect");
            _setContinuousWave = Method(type, "SetContinuousWave", typeof(double), typeof(double));
            _setOutput = Method(type, "SetOutput", typeof(bool));
            _refresh = Method(type, "Refresh");
            _dispose = Method(type, "Dispose");

            _readLimits = Method(type, "ReadLimits");
            _limitProperties = LimitPropertiesOf(_readLimits);

            _setMultitone = Method(
                type, "SetMultitone", typeof(double), typeof(int), typeof(double), typeof(double));
            _minimumTones = Property(type, "MinimumTones", typeof(int));
            _maximumTones = Property(type, "MaximumTones", typeof(int));
            _toneCount = Property(type, "ToneCount", typeof(int));
            _toneSpacingHz = Property(type, "ToneSpacingHz", typeof(double));

            _setNoise = Method(type, "SetNoise", typeof(double), typeof(double), typeof(double));
            _minimumNoiseBandwidthHz = Property(type, "MinimumNoiseBandwidthHz", typeof(double));
            _maximumNoiseBandwidthHz = Property(type, "MaximumNoiseBandwidthHz", typeof(double));
            _noiseBandwidthHz = Property(type, "NoiseBandwidthHz", typeof(double));
        }

        /// <summary>The name the source was discovered under.</summary>
        /// <remarks>
        /// The discovered name, not the instrument's. <see cref="Identity"/> is what the instrument
        /// calls itself once it has been asked, which is only after a connection.
        /// </remarks>
        public string Name { get; }

        /// <summary>What the source calls itself, once connected.</summary>
        public string Identity => (string)Read(_displayName) ?? Name;

        /// <summary>Whether the output is on.</summary>
        public bool IsOutputEnabled => (bool)Read(_isOutputEnabled);

        /// <summary>The carrier the source reports, in hertz.</summary>
        public double FrequencyHz => (double)Read(_frequencyHz);

        /// <summary>The level the source reports, in dBm.</summary>
        public double LevelDbm => (double)Read(_levelDbm);

        /// <summary>Whether this source will state its frequency and level range.</summary>
        public bool CanReportLimits => _readLimits != null && _limitProperties != null;

        /// <summary>Whether this source will produce a comb of equal tones.</summary>
        public bool CanProduceMultitone =>
            _setMultitone != null && _minimumTones != null && _maximumTones != null &&
            _toneCount != null && _toneSpacingHz != null;

        /// <summary>Whether this source will produce band-limited noise.</summary>
        public bool CanProduceNoise =>
            _setNoise != null && _minimumNoiseBandwidthHz != null &&
            _maximumNoiseBandwidthHz != null && _noiseBandwidthHz != null;

        /// <summary>Fewest tones in a comb.</summary>
        public int MinimumTones => CanProduceMultitone ? (int)Read(_minimumTones) : 0;

        /// <summary>Most tones in a comb.</summary>
        public int MaximumTones => CanProduceMultitone ? (int)Read(_maximumTones) : 0;

        /// <summary>Tones the source reports, or zero when the comb is off.</summary>
        public int ToneCount => CanProduceMultitone ? (int)Read(_toneCount) : 0;

        /// <summary>Tone spacing the source reports, in hertz, or zero.</summary>
        public double ToneSpacingHz => CanProduceMultitone ? (double)Read(_toneSpacingHz) : 0.0;

        /// <summary>Narrowest noise band, in hertz.</summary>
        public double MinimumNoiseBandwidthHz =>
            CanProduceNoise ? (double)Read(_minimumNoiseBandwidthHz) : 0.0;

        /// <summary>Widest noise band, in hertz.</summary>
        public double MaximumNoiseBandwidthHz =>
            CanProduceNoise ? (double)Read(_maximumNoiseBandwidthHz) : 0.0;

        /// <summary>Noise bandwidth the source reports, in hertz, or zero when the noise is off.</summary>
        public double NoiseBandwidthHz => CanProduceNoise ? (double)Read(_noiseBandwidthHz) : 0.0;

        /// <summary>
        /// Wraps a discovered source instance.
        /// </summary>
        /// <param name="instance">The source, as discovery created it.</param>
        /// <param name="displayName">The name it was discovered under.</param>
        /// <exception cref="ArgumentNullException"><paramref name="instance"/> is null.</exception>
        /// <exception cref="InvalidOperationException">A member the shell needs is missing.</exception>
        public static StimulusSource Around(object instance, string displayName)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            string missing = FirstUnbindableMember(instance.GetType());

            if (missing != null)
            {
                throw new InvalidOperationException(
                    "'" + displayName + "' does not provide '" + missing + "'.");
            }

            return new StimulusSource(instance, displayName);
        }

        /// <summary>
        /// The first member the shell needs and a candidate source does not have.
        /// </summary>
        /// <param name="type">The candidate.</param>
        /// <returns>The member's name, or <c>null</c> when every one of them is present.</returns>
        /// <remarks>
        /// Only the members every source must have. The three optional capabilities are absent from
        /// this list by design: a source that produces no noise is not broken, and the panel offers
        /// what the source has.
        /// </remarks>
        public static string FirstUnbindableMember(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (Property(type, "DisplayName", typeof(string)) == null) return "DisplayName";
            if (Property(type, "IsOutputEnabled", typeof(bool)) == null) return "IsOutputEnabled";
            if (Property(type, "FrequencyHz", typeof(double)) == null) return "FrequencyHz";
            if (Property(type, "LevelDbm", typeof(double)) == null) return "LevelDbm";

            if (Method(type, "Connect") == null) return "Connect()";
            if (Method(type, "Refresh") == null) return "Refresh()";
            if (Method(type, "Dispose") == null) return "Dispose()";
            if (Method(type, "SetOutput", typeof(bool)) == null) return "SetOutput(bool)";

            if (Method(type, "SetContinuousWave", typeof(double), typeof(double)) == null)
            {
                return "SetContinuousWave(double, double)";
            }

            return null;
        }

        /// <summary>Connects to the source and reads its identity.</summary>
        public void Connect() => Invoke(_connect);

        /// <summary>Reads back what the source says its state is now.</summary>
        public void Refresh() => Invoke(_refresh);

        /// <summary>Sets an unmodulated carrier.</summary>
        /// <param name="frequencyHz">Carrier frequency, in hertz.</param>
        /// <param name="levelDbm">Output level, in dBm.</param>
        public void SetContinuousWave(double frequencyHz, double levelDbm) =>
            Invoke(_setContinuousWave, frequencyHz, levelDbm);

        /// <summary>Sets a comb of equal tones centred on the carrier.</summary>
        /// <param name="centreFrequencyHz">Centre of the comb, in hertz.</param>
        /// <param name="toneCount">How many tones.</param>
        /// <param name="spacingHz">Spacing between adjacent tones, in hertz.</param>
        /// <param name="levelDbm">Total output level of the comb, in dBm.</param>
        /// <exception cref="NotSupportedException">This source produces no comb.</exception>
        public void SetMultitone(
            double centreFrequencyHz, int toneCount, double spacingHz, double levelDbm)
        {
            if (!CanProduceMultitone)
            {
                throw new NotSupportedException(Name + " does not produce a multitone comb.");
            }

            Invoke(_setMultitone, centreFrequencyHz, toneCount, spacingHz, levelDbm);
        }

        /// <summary>Sets band-limited noise of a stated total power.</summary>
        /// <param name="centreFrequencyHz">Centre of the noise band, in hertz.</param>
        /// <param name="bandwidthHz">Noise bandwidth, in hertz.</param>
        /// <param name="levelDbm">Total power in the band, in dBm.</param>
        /// <exception cref="NotSupportedException">This source produces no noise.</exception>
        public void SetNoise(double centreFrequencyHz, double bandwidthHz, double levelDbm)
        {
            if (!CanProduceNoise)
            {
                throw new NotSupportedException(Name + " does not produce band-limited noise.");
            }

            Invoke(_setNoise, centreFrequencyHz, bandwidthHz, levelDbm);
        }

        /// <summary>Enables or disables the output.</summary>
        /// <param name="enabled">Whether RF should be on.</param>
        public void SetOutput(bool enabled) => Invoke(_setOutput, enabled);

        /// <summary>
        /// Asks the source what it can produce.
        /// </summary>
        /// <returns>
        /// The limits, or <see cref="SourceLimits.Unknown"/> for a source that will not say.
        /// </returns>
        public SourceLimits ReadLimits()
        {
            if (!CanReportLimits)
            {
                return SourceLimits.Unknown;
            }

            object limits = Invoke(_readLimits);

            if (limits == null)
            {
                return SourceLimits.Unknown;
            }

            return new SourceLimits(
                (double)_limitProperties[0].GetValue(limits),
                (double)_limitProperties[1].GetValue(limits),
                (double)_limitProperties[2].GetValue(limits),
                (double)_limitProperties[3].GetValue(limits));
        }

        /// <summary>Turns the output off and closes the source.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _dispose.Invoke(_instance, null);
            }
            catch (Exception)
            {
                // Closing must not fail because the instrument has already gone — the source's own
                // Dispose says the same thing about the session underneath it.
            }
        }

        private object Read(PropertyInfo property)
        {
            try
            {
                return property.GetValue(_instance);
            }
            catch (Exception e)
            {
                throw StimulusDescriptor.Unwrap(e);
            }
        }

        private object Invoke(MethodInfo method, params object[] arguments)
        {
            try
            {
                return method.Invoke(_instance, arguments);
            }
            catch (Exception e)
            {
                // Unwrapped, because the wrapper says "Exception has been thrown by the target of
                // an invocation" and the thing worth reading is what the instrument said.
                throw StimulusDescriptor.Unwrap(e);
            }
        }

        private static PropertyInfo Property(Type type, string name, Type valueType)
        {
            PropertyInfo property = type.GetProperty(
                name, BindingFlags.Public | BindingFlags.Instance);

            return property != null && property.PropertyType == valueType && property.CanRead
                ? property
                : null;
        }

        private static MethodInfo Method(Type type, string name, params Type[] parameters)
        {
            return type.GetMethod(
                name, BindingFlags.Public | BindingFlags.Instance, null, parameters, null);
        }

        /// <summary>
        /// The four limit properties on whatever <c>ReadLimits</c> returns, or null.
        /// </summary>
        /// <remarks>
        /// Bound from the method's declared return type rather than from a returned instance, so
        /// that a renamed property disables the capability at discovery rather than throwing at the
        /// moment the panel tries to range a control.
        /// </remarks>
        private static PropertyInfo[] LimitPropertiesOf(MethodInfo readLimits)
        {
            if (readLimits == null)
            {
                return null;
            }

            var names = new[]
            {
                "MinimumFrequencyHz", "MaximumFrequencyHz", "MinimumLevelDbm", "MaximumLevelDbm",
            };

            List<PropertyInfo> found = names
                .Select(name => Property(readLimits.ReturnType, name, typeof(double)))
                .ToList();

            return found.Any(p => p == null) ? null : found.ToArray();
        }
    }
}
