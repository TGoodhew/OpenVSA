using System;
using OpenVSA.Core;

namespace OpenVSA.Hal
{
    /// <summary>
    /// What the user asked for, before any front end has had a say.
    /// </summary>
    /// <remarks>
    /// Deliberately unconstrained by any front end's capabilities: a request may ask for a span no
    /// hardware here can deliver. <see cref="IFrontEnd.Negotiate"/> is what reconciles the two, and
    /// <c>REQ-HAL-001</c> requires it to report every reconciliation rather than perform it
    /// silently. Validation here is limited to values that are meaningless in themselves.
    /// </remarks>
    public sealed class AcquisitionRequest
    {
        /// <summary>Creates a request.</summary>
        /// <param name="centerFrequencyHz">Requested centre frequency, in hertz; must be finite.</param>
        /// <param name="spanHz">Requested span, in hertz; must be positive and finite.</param>
        /// <param name="samplesPerBlock">Requested block size, in complex samples; must be positive.</param>
        /// <param name="referenceLevelDbm">Requested reference level, in dBm; must be finite.</param>
        /// <param name="path">
        /// Which acquisition path to use. Optional, and defaulted to
        /// <see cref="AnalysisPath.ComplexZoom"/> because that is the path every front end supports;
        /// <c>IFrontEndCapabilities.SupportsBasebandIq</c> says whether the other one may be asked
        /// for, and a front end that cannot honour it coerces rather than failing.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">A value is not finite or not positive.</exception>
        public AcquisitionRequest(
            double centerFrequencyHz,
            double spanHz,
            int samplesPerBlock,
            double referenceLevelDbm,
            AnalysisPath path = AnalysisPath.ComplexZoom)
        {
            if (double.IsNaN(centerFrequencyHz) || double.IsInfinity(centerFrequencyHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(centerFrequencyHz), centerFrequencyHz, "Centre frequency must be finite.");
            }

            if (!(spanHz > 0.0) || double.IsInfinity(spanHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spanHz), spanHz, "Span must be positive and finite.");
            }

            if (samplesPerBlock <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samplesPerBlock), samplesPerBlock, "Block size must be positive.");
            }

            if (double.IsNaN(referenceLevelDbm) || double.IsInfinity(referenceLevelDbm))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(referenceLevelDbm), referenceLevelDbm, "Reference level must be finite.");
            }

            CenterFrequencyHz = centerFrequencyHz;
            SpanHz = spanHz;
            SamplesPerBlock = samplesPerBlock;
            ReferenceLevelDbm = referenceLevelDbm;
            Path = path;
        }

        /// <summary>Requested centre frequency, in hertz.</summary>
        public double CenterFrequencyHz { get; }

        /// <summary>Requested span, in hertz.</summary>
        public double SpanHz { get; }

        /// <summary>Requested block size, in complex samples.</summary>
        public int SamplesPerBlock { get; }

        /// <summary>Requested reference level, in dBm.</summary>
        public double ReferenceLevelDbm { get; }

        /// <summary>Requested acquisition path (<c>REQ-ACQ-001</c>).</summary>
        public AnalysisPath Path { get; }
    }
}
