using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The conversion from sample values to absolute amplitude, stated once (<c>REQ-AMP-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The expression.</strong> Let <c>s[n]</c> be the block's interleaved complex samples,
    /// <c>w[n]</c> the analysis window of length <c>N</c> with coherent gain <c>CG = Σw/N</c>,
    /// <c>V_fs</c> the front end's full scale referred to the input in volts, <c>R</c> the reference
    /// impedance, <c>G</c> any external gain ahead of the input in dB, and
    /// </para>
    /// <code>
    ///     X[k] = Σ s[n]·w[n]·e^(−2πikn/N)          (unnormalised, per IFftProvider)
    /// </code>
    /// <para>
    /// then the amplitude of a discrete tone falling in bin <c>k</c>, in volts peak referred to the
    /// input, and its power into <c>R</c>, are
    /// </para>
    /// <code>
    ///     A[k] = c · V_fs · |X[k]| / (N · CG)
    ///     P[k] = A[k]² / (2R)                       watts
    ///     L[k] = 10·log10(P[k] / 1 mW) − G          dBm
    /// </code>
    /// <para>
    /// where <c>c</c> is 1 on the complex (zoom/IF) path and 2 on the one-sided real-baseband path,
    /// for the reason given at <see cref="OneSidedBinGainDb"/>. Folding the constants together
    /// leaves a single per-configuration offset,
    /// </para>
    /// <code>
    ///     L[k] = 10·log10(|X[k]|²) + K
    ///     K    = 20·log10(V_fs / (N·CG)) − 10·log10(2R) + 30 − G
    /// </code>
    /// <para>
    /// which is what <see cref="AmplitudeScale"/> carries and what every level in OpenVSA is
    /// computed with. <c>REQ-AMP-001</c> requires this to be stated once and implemented once:
    /// there is deliberately no second place where a level is formed from a magnitude.
    /// </para>
    /// <para>
    /// <strong>Sample values are fractions of full scale, not volts.</strong> That is the only
    /// reading under which <c>IqBlock.FullScaleVolts</c> — "ADC full scale referred to the input" —
    /// carries information; a front end that already scaled its samples to volts declares
    /// <c>V_fs = 1</c> and the two coincide.
    /// </para>
    /// <para>
    /// <strong>Complex envelope convention.</strong> Instantaneous power is <c>|x(t)|²/(2R)</c>,
    /// so a complex tone of 1 V peak into 50 Ω reads +10 dBm — the same figure a real sine of 1 V
    /// peak gives, which is what makes the two paths comparable rather than 3 dB apart.
    /// </para>
    /// <para>
    /// <strong>Reference level is a fallback, not a second scaling.</strong> Applying both
    /// <c>V_fs</c> and <c>ReferenceLevelDbm</c> would double-count the front end's range. Where a
    /// front end declares no usable full scale, <see cref="ResolveFullScaleVolts"/> derives one
    /// from the reference level — which for such an instrument is exactly the statement that
    /// full scale is that power.
    /// </para>
    /// </remarks>
    public sealed class AmplitudeChain
    {
        /// <summary>The default reference impedance, 50 Ω (<c>REQ-AMP-002</c>).</summary>
        public const double DefaultReferenceImpedanceOhms = 50.0;

        /// <summary>
        /// The <c>c = 2</c> of the one-sided real path, in dB: <c>20·log10(2)</c>.
        /// </summary>
        /// <remarks>
        /// A real tone of amplitude <c>A</c> puts <c>A/2</c> in each of the bins at <c>±f</c>. A
        /// one-sided display shows only the positive half, so its bins are doubled to keep
        /// <c>A[k]</c> the tone's actual amplitude. DC and Nyquist have no partner and are not
        /// doubled — doubling them is the classic 6 dB error at the two ends of a baseband trace.
        /// </remarks>
        public static readonly double OneSidedBinGainDb = 20.0 * Math.Log10(2.0);

        /// <summary>Creates a chain with the default 50 Ω impedance and no external gain.</summary>
        public AmplitudeChain()
            : this(DefaultReferenceImpedanceOhms, 0.0)
        {
        }

        /// <summary>Creates a chain.</summary>
        /// <param name="referenceImpedanceOhms">Reference impedance; must be positive and finite.</param>
        /// <param name="externalGainDb">
        /// Gain of anything ahead of the front end's input, in dB, positive for amplification. It is
        /// subtracted, so that levels are referred to the plane the user cares about rather than to
        /// the connector.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range or not finite.</exception>
        public AmplitudeChain(double referenceImpedanceOhms, double externalGainDb)
        {
            if (!(referenceImpedanceOhms > 0.0) || double.IsInfinity(referenceImpedanceOhms))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(referenceImpedanceOhms), referenceImpedanceOhms,
                    "Reference impedance must be positive and finite.");
            }

            if (double.IsNaN(externalGainDb) || double.IsInfinity(externalGainDb))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(externalGainDb), externalGainDb, "External gain must be finite.");
            }

            ReferenceImpedanceOhms = referenceImpedanceOhms;
            ExternalGainDb = externalGainDb;
        }

        /// <summary>Reference impedance in ohms.</summary>
        public double ReferenceImpedanceOhms { get; }

        /// <summary>Gain ahead of the front-end input, in dB; subtracted from every level.</summary>
        public double ExternalGainDb { get; }

        /// <summary>
        /// The full scale to use, in volts: the front end's own figure where it has one, otherwise
        /// the equivalent of its reference level.
        /// </summary>
        /// <param name="declaredFullScaleVolts">The front end's declared full scale; may be zero, negative or <see cref="double.NaN"/> to mean "not known".</param>
        /// <param name="referenceLevelDbm">The front end's reference level, used only when no full scale is declared.</param>
        /// <returns>Full scale in volts peak, referred to the input.</returns>
        /// <exception cref="ArgumentException">Neither figure is usable, so no absolute amplitude can be formed.</exception>
        public double ResolveFullScaleVolts(double declaredFullScaleVolts, double referenceLevelDbm)
        {
            if (declaredFullScaleVolts > 0.0 && !double.IsInfinity(declaredFullScaleVolts))
            {
                return declaredFullScaleVolts;
            }

            if (double.IsNaN(referenceLevelDbm) || double.IsInfinity(referenceLevelDbm))
            {
                throw new ArgumentException(
                    "The front end declares neither a usable full scale nor a finite reference " +
                    "level, so no absolute amplitude can be formed (REQ-AMP-001).",
                    nameof(declaredFullScaleVolts));
            }

            // Full scale as the reference level states it: P = 10^((dBm-30)/10) watts, and a tone
            // of that power has peak amplitude sqrt(2RP).
            double watts = Math.Pow(10.0, (referenceLevelDbm - 30.0) / 10.0);
            return Math.Sqrt(2.0 * ReferenceImpedanceOhms * watts);
        }

        /// <summary>
        /// Builds the scale for a block analysed with a given window and transform length.
        /// </summary>
        /// <param name="block">The block whose full scale and reference level apply.</param>
        /// <param name="window">The analysis window; its coherent gain is the amplitude correction of <c>REQ-DSP-011</c>.</param>
        /// <param name="transformLength">Transform length <c>N</c> in complex points; must be positive.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="transformLength"/> is not positive.</exception>
        public AmplitudeScale ScaleFor(IqBlock block, Window window, int transformLength)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            return ScaleFor(
                block.FullScaleVolts, block.ReferenceLevelDbm, transformLength, window.CoherentGain);
        }

        /// <summary>
        /// Builds the scale from its constituents.
        /// </summary>
        /// <param name="declaredFullScaleVolts">Front-end full scale referred to the input; see <see cref="ResolveFullScaleVolts"/>.</param>
        /// <param name="referenceLevelDbm">Front-end reference level, used only as a fallback full scale.</param>
        /// <param name="transformLength">Transform length <c>N</c> in complex points; must be positive.</param>
        /// <param name="coherentGain">Window coherent gain <c>Σw/N</c>; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        /// <exception cref="ArgumentException">No usable full scale can be resolved.</exception>
        public AmplitudeScale ScaleFor(
            double declaredFullScaleVolts,
            double referenceLevelDbm,
            int transformLength,
            double coherentGain)
        {
            if (transformLength <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transformLength), transformLength, "Transform length must be positive.");
            }

            if (!(coherentGain > 0.0) || double.IsInfinity(coherentGain))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(coherentGain), coherentGain, "Coherent gain must be positive and finite.");
            }

            double fullScaleVolts = ResolveFullScaleVolts(declaredFullScaleVolts, referenceLevelDbm);

            // K, exactly as the type-level remarks state it, factored into its linear and
            // logarithmic halves. The product of the two is unchanged - the expression is still
            // stated once - but the linear half is what turns a raw bin into calibrated volts, and
            // REQ-DSP-041's formats need volts rather than decibels to work from.
            double voltsPerUnit = fullScaleVolts / (transformLength * coherentGain);

            double powerOffsetDb =
                -10.0 * Math.Log10(2.0 * ReferenceImpedanceOhms)
                + 30.0
                - ExternalGainDb;

            return new AmplitudeScale(voltsPerUnit, powerOffsetDb);
        }
    }

    /// <summary>
    /// The single scalar of <see cref="AmplitudeChain"/>, ready to apply to FFT magnitudes.
    /// </summary>
    /// <remarks>
    /// A value type carrying one number, so that a per-bin loop adds a constant rather than
    /// re-deriving the chain — and so that the chain cannot be applied twice by accident, which is
    /// the failure mode <c>REQ-AMP-001</c>'s "implemented once" is guarding against.
    /// </remarks>
    public readonly struct AmplitudeScale
    {
        /// <summary>
        /// The level reported for a bin of exactly zero power.
        /// </summary>
        /// <remarks>
        /// Finite by choice. <c>−∞</c> is arithmetically right and propagates through decimation,
        /// axis mapping and averaging as a value nothing else survives contact with; −400 dBm is
        /// some 250 dB below any physical noise floor, so it clamps to the bottom of a graticule
        /// exactly as a true zero would.
        /// </remarks>
        public const double FloorDbm = -400.0;

        /// <summary>Creates a scale.</summary>
        /// <param name="voltsPerUnit">Linear factor from a raw transform bin to volts peak.</param>
        /// <param name="powerOffsetDb">The remaining offset in dB, applied to <c>10·log10(|v|²)</c>.</param>
        public AmplitudeScale(double voltsPerUnit, double powerOffsetDb)
        {
            VoltsPerUnit = voltsPerUnit;
            PowerOffsetDb = powerOffsetDb;
        }

        /// <summary>
        /// Linear factor turning a raw transform bin into volts peak, referred to the input.
        /// </summary>
        /// <remarks>
        /// <c>V_fs / (N · CG)</c>. Applied once when a frame is built, so every format of
        /// <c>REQ-DSP-041</c> works from calibrated volts and none of them re-derives the chain.
        /// </remarks>
        public double VoltsPerUnit { get; }

        /// <summary>The offset in dB from a squared voltage to dBm: <c>−10·log10(2R) + 30 − G</c>.</summary>
        public double PowerOffsetDb { get; }

        /// <summary>
        /// The offset <c>K</c> in dB, added to <c>10·log10(|X[k]|²)</c> of a raw bin.
        /// </summary>
        /// <remarks>
        /// The whole of <c>REQ-AMP-001</c>'s <c>K</c>, for a caller working from raw transform
        /// output rather than from calibrated volts. It is the two halves recombined, not a second
        /// derivation.
        /// </remarks>
        public double OffsetDb => 20.0 * Math.Log10(VoltsPerUnit) + PowerOffsetDb;

        /// <summary>Converts one bin's squared magnitude to dBm.</summary>
        /// <param name="magnitudeSquared">|X[k]|², from the unnormalised forward transform.</param>
        /// <returns>The level in dBm, or <see cref="FloorDbm"/> for a bin of no power.</returns>
        public double PowerToDbm(double magnitudeSquared)
        {
            if (!(magnitudeSquared > 0.0))
            {
                return FloorDbm;
            }

            double level = 10.0 * Math.Log10(magnitudeSquared) + OffsetDb;
            return level < FloorDbm ? FloorDbm : level;
        }

        /// <summary>
        /// Converts a squared magnitude that is already in volts to dBm.
        /// </summary>
        /// <param name="voltsSquared">|v|², where v is in volts peak referred to the input.</param>
        /// <returns>The level in dBm, or <see cref="FloorDbm"/> for no power.</returns>
        public double VoltsSquaredToDbm(double voltsSquared)
        {
            if (!(voltsSquared > 0.0))
            {
                return FloorDbm;
            }

            double level = 10.0 * Math.Log10(voltsSquared) + PowerOffsetDb;
            return level < FloorDbm ? FloorDbm : level;
        }

        /// <summary>Converts one bin's complex value to dBm.</summary>
        /// <param name="real">Real part of <c>X[k]</c>.</param>
        /// <param name="imaginary">Imaginary part of <c>X[k]</c>.</param>
        /// <returns>The level in dBm.</returns>
        public double ToDbm(double real, double imaginary) =>
            PowerToDbm(real * real + imaginary * imaginary);

        /// <summary>
        /// Returns a copy with an additional linear gain.
        /// </summary>
        /// <param name="gain">Linear factor, such as the 2 of the one-sided real path.</param>
        /// <remarks>
        /// Applied to the linear half rather than the logarithmic one, so the volts a frame stores
        /// carry the correction and every format inherits it. Adding it in decibels would correct
        /// the log magnitude and leave real, imaginary and phase reading half amplitude.
        /// </remarks>
        public AmplitudeScale WithLinearGain(double gain) =>
            new AmplitudeScale(VoltsPerUnit * gain, PowerOffsetDb);
    }
}
