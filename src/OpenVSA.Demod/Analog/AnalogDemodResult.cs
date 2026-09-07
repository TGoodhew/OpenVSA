using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Demod.Analog
{
    /// <summary>The traces an analog demodulation offers (<c>REQ-DEM-010a</c>).</summary>
    public enum AnalogTrace
    {
        /// <summary>The demodulated waveform against time.</summary>
        DemodulatedWaveform = 0,

        /// <summary>The spectrum of that waveform.</summary>
        AudioSpectrum,

        /// <summary>The carrier's instantaneous frequency against time.</summary>
        CarrierFrequency,

        /// <summary>The carrier's envelope against time.</summary>
        CarrierAmplitude,
    }

    /// <summary>What an analog demodulation produced (<c>REQ-DEM-010a</c>).</summary>
    /// <remarks>
    /// <para>
    /// The requirement asks for three traces: the demodulated waveform against time, the
    /// demodulated audio spectrum, and carrier frequency and amplitude against time. The last is
    /// two series rather than one, so it is offered as two traces and the enumeration says four.
    /// </para>
    /// <para>
    /// <strong>Carrier frequency and amplitude are always present, whatever was demodulated.</strong>
    /// They are the two things the detectors compute on the way to any of AM, FM or PM, so
    /// withholding them on an AM measurement would be hiding a trace that had already been
    /// produced. It is also what makes the pair useful: seeing the envelope beside the frequency is
    /// how incidental AM on an FM signal is found.
    /// </para>
    /// </remarks>
    public sealed class AnalogDemodResult
    {
        private readonly ReadOnlyCollection<double> _audio;
        private readonly ReadOnlyCollection<double> _frequency;
        private readonly ReadOnlyCollection<double> _amplitude;

        internal AnalogDemodResult(
            AnalogDemodType type,
            double sampleRateHz,
            double[] audio,
            double[] carrierFrequencyHz,
            double[] carrierAmplitude,
            AudioSpectrum spectrum,
            AnalogMetrics metrics)
        {
            Type = type;
            SampleRateHz = sampleRateHz;
            _audio = new ReadOnlyCollection<double>(audio);
            _frequency = new ReadOnlyCollection<double>(carrierFrequencyHz);
            _amplitude = new ReadOnlyCollection<double>(carrierAmplitude);
            Spectrum = spectrum;
            Metrics = metrics;
        }

        /// <summary>What was demodulated.</summary>
        public AnalogDemodType Type { get; }

        /// <summary>The rate the record and every trace here is sampled at.</summary>
        public double SampleRateHz { get; }

        /// <summary>
        /// The recovered audio: a fraction of the carrier for AM, hertz for FM, radians for PM.
        /// </summary>
        public IReadOnlyList<double> Demodulated => _audio;

        /// <summary>The carrier's instantaneous frequency, in hertz.</summary>
        public IReadOnlyList<double> CarrierFrequencyHz => _frequency;

        /// <summary>The carrier's envelope, in the acquisition's units.</summary>
        public IReadOnlyList<double> CarrierAmplitude => _amplitude;

        /// <summary>The spectrum of the recovered audio.</summary>
        public AudioSpectrum Spectrum { get; }

        /// <summary>What was measured.</summary>
        public AnalogMetrics Metrics { get; }

        /// <summary>The unit a trace's values carry.</summary>
        /// <param name="trace">The trace.</param>
        /// <returns>The unit, or an empty string.</returns>
        public string UnitFor(AnalogTrace trace)
        {
            switch (trace)
            {
                case AnalogTrace.DemodulatedWaveform:
                    return Type == AnalogDemodType.Am
                        ? string.Empty
                        : Type == AnalogDemodType.Fm ? "Hz" : "rad";

                case AnalogTrace.AudioSpectrum:
                    return "dB";

                case AnalogTrace.CarrierFrequency:
                    return "Hz";

                default:
                    return string.Empty;
            }
        }

        /// <summary>A trace's values.</summary>
        /// <param name="trace">The trace.</param>
        /// <returns>The values.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The trace is not one of the four.</exception>
        public IReadOnlyList<double> Take(AnalogTrace trace)
        {
            switch (trace)
            {
                case AnalogTrace.DemodulatedWaveform:
                    return _audio;

                case AnalogTrace.AudioSpectrum:
                    return Spectrum.Decibels;

                case AnalogTrace.CarrierFrequency:
                    return _frequency;

                case AnalogTrace.CarrierAmplitude:
                    return _amplitude;

                default:
                    throw new ArgumentOutOfRangeException(nameof(trace));
            }
        }

        /// <summary>The step between a trace's points on its horizontal axis.</summary>
        /// <param name="trace">The trace.</param>
        /// <returns>Seconds for a time trace, hertz for the spectrum.</returns>
        public double StepFor(AnalogTrace trace) =>
            trace == AnalogTrace.AudioSpectrum
                ? Spectrum.BinWidthHz
                : (SampleRateHz <= 0.0 ? 1.0 : 1.0 / SampleRateHz);

        /// <summary>Every trace an analog demodulation offers.</summary>
        public static IReadOnlyList<AnalogTrace> AllTraces { get; } =
            new ReadOnlyCollection<AnalogTrace>(
                new List<AnalogTrace>
                {
                    AnalogTrace.DemodulatedWaveform,
                    AnalogTrace.AudioSpectrum,
                    AnalogTrace.CarrierFrequency,
                    AnalogTrace.CarrierAmplitude,
                });

        /// <inheritdoc />
        public override string ToString() => Metrics.ToString();
    }
}
