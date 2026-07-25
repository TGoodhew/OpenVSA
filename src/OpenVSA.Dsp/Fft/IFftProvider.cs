using System;

namespace OpenVSA.Dsp.Fft
{
    /// <summary>
    /// A fast Fourier transform implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-NFR-004</c>. The FFT sits behind this interface so the choice between a managed
    /// transform and a native one is made at deployment time rather than design time. The reason
    /// is licensing as much as performance: FFTW is GPL without a purchased commercial licence and
    /// cannot be linked in, whereas Intel oneMKL and IPP are redistributable under the Intel
    /// Simplified Software Licence. The shipped default is managed and therefore carries no
    /// copyleft obligation, which <c>REQ-NFR-008</c> enforces.
    /// </para>
    /// <para>
    /// <strong>Normalisation is part of the contract, not an implementation detail.</strong>
    /// <see cref="Forward"/> is unnormalised and <see cref="Inverse"/> scales by <c>1/N</c>, so a
    /// forward followed by an inverse is the identity and Parseval's theorem reads
    /// <c>Σ|x[n]|² = (1/N)·Σ|X[k]|²</c>. A provider that split the scaling as <c>1/√N</c> across
    /// both directions would round-trip correctly and still put every absolute amplitude readout
    /// out by <c>√N</c>, which is exactly the kind of defect that survives a round-trip test.
    /// </para>
    /// <para>
    /// The sign convention is <c>X[k] = Σ x[n]·e^(−2πikn/N)</c>.
    /// </para>
    /// </remarks>
    public interface IFftProvider
    {
        /// <summary>Name shown in configuration and in test output.</summary>
        string Name { get; }

        /// <summary>Whether this provider uses a native library.</summary>
        bool IsNativeAccelerated { get; }

        /// <summary>Precision of the provider's internal arithmetic, in bits of significand.</summary>
        /// <remarks>
        /// <c>REQ-NFR-004a</c> requires cross-provider agreement to be asserted at the tolerance of
        /// the <em>less</em> precise provider. That rule needs the precision to be discoverable
        /// rather than inferred from the provider's name.
        /// </remarks>
        int SignificandBits { get; }

        /// <summary>Whether this provider can transform a block of the given length.</summary>
        /// <param name="length">Transform length, in complex points.</param>
        bool SupportsLength(int length);

        /// <summary>
        /// Transforms interleaved complex data in place, unnormalised.
        /// </summary>
        /// <param name="interleaved">Interleaved I/Q; its length must be twice a supported transform length.</param>
        /// <exception cref="ArgumentException">The length is not supported.</exception>
        void Forward(Span<double> interleaved);

        /// <summary>
        /// Inverse-transforms interleaved complex data in place, scaled by <c>1/N</c>.
        /// </summary>
        /// <param name="interleaved">Interleaved I/Q; its length must be twice a supported transform length.</param>
        /// <exception cref="ArgumentException">The length is not supported.</exception>
        void Inverse(Span<double> interleaved);
    }

    /// <summary>
    /// Marks a type as an FFT provider, for discovery at start-up.
    /// </summary>
    /// <remarks>
    /// Discovery is by attribute, as it is for front ends (<c>REQ-HAL-003</c>), so that adding a
    /// provider — in particular a native one shipped as a separate assembly — never means editing
    /// a registry in core code.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class FftProviderAttribute : Attribute
    {
        /// <summary>Marks a provider.</summary>
        /// <param name="name">Name used to select this provider in configuration.</param>
        public FftProviderAttribute(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A provider needs a name.", nameof(name));
            }

            Name = name;
        }

        /// <summary>Name used to select this provider in configuration.</summary>
        public string Name { get; }
    }
}
