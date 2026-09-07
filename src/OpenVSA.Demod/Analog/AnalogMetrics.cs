using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenVSA.Demod.Analog
{
    /// <summary>
    /// What an analog demodulation measured (<c>REQ-DEM-010a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every field is present whatever was demodulated, and the ones that do not apply are
    /// <see cref="double.NaN"/> rather than zero.</strong> An AM measurement has no FM deviation,
    /// and reporting 0 Hz would be a measurement result rather than the absence of one — the same
    /// distinction <c>REQ-DEM-080</c> draws between a trace that is unavailable and one that is
    /// empty. <see cref="Applies"/> answers it without a caller having to test for NaN.
    /// </para>
    /// </remarks>
    public sealed class AnalogMetrics
    {
        internal AnalogMetrics(
            AnalogDemodType type,
            double amDepthPercent,
            double fmDeviationPeakHz,
            double fmDeviationRmsHz,
            double pmDeviationPeakRadians,
            double pmDeviationRmsRadians,
            double modulationRateHz,
            double sinadDb,
            double distortionPercent,
            double residualFmHz,
            double residualAmPercent,
            double carrierFrequencyHz,
            double carrierAmplitude,
            bool filteredAudio)
        {
            Type = type;
            AmDepthPercent = amDepthPercent;
            FmDeviationPeakHz = fmDeviationPeakHz;
            FmDeviationRmsHz = fmDeviationRmsHz;
            PmDeviationPeakRadians = pmDeviationPeakRadians;
            PmDeviationRmsRadians = pmDeviationRmsRadians;
            ModulationRateHz = modulationRateHz;
            SinadDb = sinadDb;
            DistortionPercent = distortionPercent;
            ResidualFmHz = residualFmHz;
            ResidualAmPercent = residualAmPercent;
            CarrierFrequencyHz = carrierFrequencyHz;
            CarrierAmplitude = carrierAmplitude;
            FilteredAudio = filteredAudio;
        }

        /// <summary>What was demodulated.</summary>
        public AnalogDemodType Type { get; }

        /// <summary>
        /// Modulation depth, in per cent, as <c>(max - min)/(max + min)</c> of the envelope.
        /// </summary>
        /// <remarks>
        /// The textbook definition, and exact for a sinusoidally modulated carrier: an envelope of
        /// <c>A(1 + m·cos)</c> has a maximum of <c>A(1 + m)</c> and a minimum of <c>A(1 - m)</c>,
        /// whose ratio is <c>m</c> with the carrier amplitude cancelled out. That cancellation is
        /// why it is preferred to the peak of the recovered audio, which would have to be divided
        /// by a separately estimated carrier level.
        /// </remarks>
        public double AmDepthPercent { get; }

        /// <summary>Peak frequency deviation, in hertz.</summary>
        public double FmDeviationPeakHz { get; }

        /// <summary>RMS frequency deviation, in hertz.</summary>
        public double FmDeviationRmsHz { get; }

        /// <summary>Peak phase deviation, in radians.</summary>
        public double PmDeviationPeakRadians { get; }

        /// <summary>RMS phase deviation, in radians.</summary>
        public double PmDeviationRmsRadians { get; }

        /// <summary>Peak phase deviation, in degrees.</summary>
        public double PmDeviationPeakDegrees => PmDeviationPeakRadians * 180.0 / Math.PI;

        /// <summary>RMS phase deviation, in degrees.</summary>
        public double PmDeviationRmsDegrees => PmDeviationRmsRadians * 180.0 / Math.PI;

        /// <summary>The modulation's own rate, in hertz.</summary>
        public double ModulationRateHz { get; }

        /// <summary>
        /// Signal-to-noise-and-distortion, in decibels.
        /// </summary>
        /// <remarks>
        /// The whole recovered audio against everything in it that is not the fundamental — noise
        /// and distortion together, which is what the S/(N+D) of the name means. Distinct from
        /// <see cref="DistortionPercent"/>, which counts only the harmonics.
        /// </remarks>
        public double SinadDb { get; }

        /// <summary>Total harmonic distortion, in per cent of the fundamental.</summary>
        public double DistortionPercent { get; }

        /// <summary>
        /// Residual FM: the frequency deviation left when the modulation is taken out, in hertz rms.
        /// </summary>
        /// <remarks>
        /// The carrier's own noise and instability, measured as what remains after the fundamental
        /// and its harmonics are removed. On an unmodulated carrier it is the whole of the
        /// deviation, which is the case the figure is usually quoted for.
        /// </remarks>
        public double ResidualFmHz { get; }

        /// <summary>Residual AM: the envelope variation left when the modulation is taken out.</summary>
        public double ResidualAmPercent { get; }

        /// <summary>The carrier's mean frequency offset from the centre of the record, in hertz.</summary>
        public double CarrierFrequencyHz { get; }

        /// <summary>The carrier's mean amplitude, in the units of the acquisition.</summary>
        public double CarrierAmplitude { get; }

        /// <summary>Whether post-detection filtering was applied to the audio these describe.</summary>
        /// <remarks>
        /// Carried with the numbers rather than left in the settings, because a deviation measured
        /// through a de-emphasis network is a deviation of the filtered audio and comparing it with
        /// an unfiltered one is comparing two different measurements.
        /// </remarks>
        public bool FilteredAudio { get; }

        /// <summary>Whether a metric applies to what was demodulated.</summary>
        /// <param name="name">The metric's label, as <see cref="Render"/> writes it.</param>
        /// <returns>Whether it was measured.</returns>
        public bool Applies(string name)
        {
            switch (name)
            {
                case "AM Depth":
                case "Residual AM":
                    return Type == AnalogDemodType.Am;

                case "FM Deviation (peak)":
                case "FM Deviation (rms)":
                case "Residual FM":
                    return Type == AnalogDemodType.Fm;

                case "PM Deviation (peak)":
                case "PM Deviation (rms)":
                    return Type == AnalogDemodType.Pm;

                default:
                    return true;
            }
        }

        /// <summary>The metrics as a display would list them.</summary>
        /// <returns>One line per metric that applies.</returns>
        public IReadOnlyList<string> Render()
        {
            var lines = new List<string>
            {
                Line("Modulation Rate", ModulationRateHz, "Hz", "F2"),
                Line("SINAD", SinadDb, "dB", "F2"),
                Line("Distortion", DistortionPercent, "%", "F4"),
                Line("Carrier Frequency", CarrierFrequencyHz, "Hz", "F2"),
                Line("Carrier Amplitude", CarrierAmplitude, string.Empty, "G6"),
            };

            switch (Type)
            {
                case AnalogDemodType.Am:
                    lines.Insert(0, Line("AM Depth", AmDepthPercent, "%", "F3"));
                    lines.Add(Line("Residual AM", ResidualAmPercent, "%", "F4"));
                    break;

                case AnalogDemodType.Fm:
                    lines.Insert(0, Line("FM Deviation (peak)", FmDeviationPeakHz, "Hz", "F2"));
                    lines.Insert(1, Line("FM Deviation (rms)", FmDeviationRmsHz, "Hz", "F2"));
                    lines.Add(Line("Residual FM", ResidualFmHz, "Hz", "F3"));
                    break;

                default:
                    lines.Insert(
                        0, Line("PM Deviation (peak)", PmDeviationPeakRadians, "rad", "F4"));
                    lines.Insert(
                        1, Line("PM Deviation (rms)", PmDeviationRmsRadians, "rad", "F4"));
                    lines.Insert(
                        2, Line("PM Deviation (peak)", PmDeviationPeakDegrees, "deg", "F2"));
                    break;
            }

            if (FilteredAudio)
            {
                lines.Add(
                    "  (post-detection filtering was applied: these describe the filtered audio)");
            }

            return lines;
        }

        private static string Line(string label, double value, string unit, string format) =>
            "  " + label.PadRight(22) +
            (double.IsNaN(value)
                ? "n/a"
                : value.ToString(format, CultureInfo.InvariantCulture) +
                  (unit.Length == 0 ? string.Empty : " " + unit));

        /// <inheritdoc />
        public override string ToString() =>
            Type + ": rate " +
            ModulationRateHz.ToString("F1", CultureInfo.InvariantCulture) + " Hz, SINAD " +
            SinadDb.ToString("F1", CultureInfo.InvariantCulture) + " dB";
    }
}
