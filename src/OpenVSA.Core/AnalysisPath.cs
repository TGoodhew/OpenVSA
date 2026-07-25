using System;

namespace OpenVSA.Core
{
    /// <summary>
    /// Which acquisition path a measurement uses.
    /// </summary>
    /// <remarks>
    /// The two rows of <c>REQ-ACQ-001</c>'s table. They differ in one number, and getting that
    /// number from the wrong row is the defect the requirement calls out by name: using the
    /// complex factor on the baseband path breaks the <c>T_max</c> identity.
    /// </remarks>
    public enum AnalysisPath
    {
        /// <summary>Complex zoom / IF: the analytic signal, <c>Fs = 1.28 · Span</c>.</summary>
        ComplexZoom = 0,

        /// <summary>Real baseband: a real signal from 0 Hz, <c>Fs = 2.56 · Span</c>.</summary>
        RealBaseband,
    }

    /// <summary>
    /// The span/sample-rate/FFT-size relationships of <c>REQ-ACQ-001</c>, stated once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Rational, not floating point.</strong> The factors are exactly <c>32/25</c> and
    /// <c>64/25</c>, and the relations are computed with integer arithmetic. Written as
    /// <c>1.28</c>, the round trip <c>1024 / 1.28</c> does not return exactly 800, and a point
    /// count derived from an FFT size would then be one off at some sizes and not at others —
    /// which is precisely the class of defect that survives a test written at one size.
    /// </para>
    /// <para>
    /// <strong>This is a product law, not an instrument's.</strong> It says how much of the
    /// acquired Nyquist band is alias-free and therefore displayable; the 28 % surplus is where the
    /// anti-alias filter rolls off. What varies between front ends is whether the resulting sample
    /// rate can be honoured exactly — some quantise it — and that is settled by negotiating with
    /// the front end, never by assuming a particular instrument's behaviour here.
    /// </para>
    /// </remarks>
    public static class AcquisitionLaw
    {
        /// <summary>Numerator of both path factors: <c>32/25 = 1.28</c>, <c>64/25 = 2.56</c>.</summary>
        private const int Denominator = 25;

        /// <summary>Sample-rate factor for <see cref="AnalysisPath.ComplexZoom"/>, as a double.</summary>
        public const double ComplexZoomFactor = 1.28;

        /// <summary>Sample-rate factor for <see cref="AnalysisPath.RealBaseband"/>, as a double.</summary>
        public const double RealBasebandFactor = 2.56;

        /// <summary>The sample-rate factor for a path.</summary>
        /// <param name="path">The acquisition path.</param>
        /// <exception cref="ArgumentOutOfRangeException">The path is not a known one.</exception>
        public static double FactorFor(AnalysisPath path)
        {
            switch (path)
            {
                case AnalysisPath.ComplexZoom:
                    return ComplexZoomFactor;

                case AnalysisPath.RealBaseband:
                    return RealBasebandFactor;

                default:
                    throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown acquisition path.");
            }
        }

        /// <summary>The sample rate a span implies on a path, in hertz.</summary>
        /// <param name="spanHz">Analysis span in hertz.</param>
        /// <param name="path">The acquisition path.</param>
        public static double SampleRateFor(double spanHz, AnalysisPath path) =>
            spanHz * FactorFor(path);

        /// <summary>The span a sample rate implies on a path, in hertz.</summary>
        /// <param name="sampleRateHz">Sample rate in hertz.</param>
        /// <param name="path">The acquisition path.</param>
        public static double SpanFor(double sampleRateHz, AnalysisPath path) =>
            sampleRateHz / FactorFor(path);

        /// <summary>
        /// The FFT size a displayed point count implies, in complex points.
        /// </summary>
        /// <param name="frequencyPoints">Displayed frequency points; must be at least 2.</param>
        /// <param name="path">The acquisition path.</param>
        /// <returns><c>1.28 (N_f − 1)</c> on the complex path, <c>2.56 (N_f − 1)</c> on the real one.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The point count is too small, or the result is not a whole number of samples.</exception>
        public static int TransformLengthFor(int frequencyPoints, AnalysisPath path)
        {
            if (frequencyPoints < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequencyPoints), frequencyPoints, "A spectrum needs at least two points.");
            }

            long numerator = (long)(frequencyPoints - 1) * Numerator(path);

            if (numerator % Denominator != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequencyPoints), frequencyPoints,
                    "N_f − 1 must be a multiple of 25 for the FFT size to be a whole number of " +
                    "samples (REQ-DSP-022).");
            }

            return (int)(numerator / Denominator);
        }

        /// <summary>
        /// The displayed point count an FFT size implies: the inverse of <see cref="TransformLengthFor"/>.
        /// </summary>
        /// <param name="transformLength">FFT size in complex points.</param>
        /// <param name="path">The acquisition path.</param>
        /// <returns>The point count, or 0 if this FFT size does not correspond to a whole one.</returns>
        /// <remarks>
        /// This is how a display learns how many points it may show: the FFT size comes from the
        /// block the front end actually delivered, so the point count follows what was captured
        /// rather than what was hoped for.
        /// </remarks>
        public static int PointsForTransformLength(int transformLength, AnalysisPath path)
        {
            if (transformLength < 1)
            {
                return 0;
            }

            long numerator = (long)transformLength * Denominator;

            return numerator % Numerator(path) != 0
                ? 0
                : (int)(numerator / Numerator(path)) + 1;
        }

        /// <summary>
        /// Longest analysable time record, in seconds: <c>(N_f − 1) / Span</c>.
        /// </summary>
        /// <param name="frequencyPoints">Displayed frequency points.</param>
        /// <param name="spanHz">Analysis span in hertz; must be positive.</param>
        /// <remarks>
        /// The identity that makes the table consistent: <c>T_max</c> is the same on both paths for
        /// the same span and point count, because the path factor cancels between <c>Fs</c> and
        /// <c>N_FFT</c>. Using the complex factor on the real path breaks it, which is what makes
        /// this the discriminating test rather than a restatement.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The span is not positive, or the point count is too small.</exception>
        public static double MaxTimeSeconds(int frequencyPoints, double spanHz)
        {
            if (frequencyPoints < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequencyPoints), frequencyPoints, "A spectrum needs at least two points.");
            }

            if (!(spanHz > 0.0) || double.IsInfinity(spanHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spanHz), spanHz, "Span must be positive and finite.");
            }

            return (frequencyPoints - 1) / spanHz;
        }

        private static int Numerator(AnalysisPath path)
        {
            switch (path)
            {
                case AnalysisPath.ComplexZoom:
                    return 32;

                case AnalysisPath.RealBaseband:
                    return 64;

                default:
                    throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown acquisition path.");
            }
        }
    }
}
