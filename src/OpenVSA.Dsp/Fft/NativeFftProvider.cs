using System;
using System.Runtime.InteropServices;

namespace OpenVSA.Dsp.Fft
{
    /// <summary>
    /// The native provider of <c>REQ-NFR-004</c>, over <c>OpenVSA.Fft.Native.dll</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Written, not vendored.</strong> The interface asks for a power-of-two, in-place,
    /// interleaved-double transform and nothing else, so pulling in FFTW (GPL, and forbidden here
    /// by <c>REQ-NFR-008</c>), oneMKL or a BSD library would add a licence row and, in oneMKL's
    /// case, tens of megabytes to every output directory, for three functions. The library is 140 kB.
    /// </para>
    /// <para>
    /// Correctness is not taken on trust for that: the parametrised suite runs the same round-trip
    /// and Parseval checks against every registered provider, and cross-provider agreement is
    /// asserted against the managed reference at <c>REQ-NFR-004a</c>'s tolerance, up to 2²⁰ points.
    /// </para>
    /// <para>
    /// <strong>Absence is not an error.</strong> The managed provider is the default and the native
    /// library is optional, so a deployment without it registers one provider instead of two rather
    /// than failing to start.
    /// </para>
    /// </remarks>
    [FftProvider("Native")]
    public sealed class NativeFftProvider : IFftProvider
    {
        private const string Library = "OpenVSA.Fft.Native.dll";

        [DllImport(Library, EntryPoint = "openvsa_fft_supports", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Supports(int length);

        [DllImport(Library, EntryPoint = "openvsa_fft_forward", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int ForwardNative(double* interleaved, int length);

        [DllImport(Library, EntryPoint = "openvsa_fft_inverse", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int InverseNative(double* interleaved, int length);

        /// <summary>Creates the provider, failing if the native library cannot be called.</summary>
        /// <exception cref="DllNotFoundException">The library is not beside the assembly.</exception>
        /// <remarks>
        /// The probe happens here so an unusable provider never reaches the registry. Discovering
        /// it lazily would mean the failure arrived in the middle of a measurement instead.
        /// </remarks>
        public NativeFftProvider()
        {
            if (Supports(1024) != 1)
            {
                throw new InvalidOperationException(
                    "OpenVSA.Fft.Native.dll answered that it cannot transform 1024 points.");
            }
        }

        /// <inheritdoc />
        public string Name => "Native";

        /// <inheritdoc />
        public bool IsNativeAccelerated => true;

        /// <inheritdoc />
        /// <remarks>
        /// 53: the native side is <c>double</c> throughout, and the project sets
        /// <c>FloatingPointModel=Precise</c> so the compiler may not reassociate the butterflies
        /// into something faster and less accurate than that claim.
        /// </remarks>
        public int SignificandBits => 53;

        /// <inheritdoc />
        public bool SupportsLength(int length) => length > 0 && Supports(length) == 1;

        /// <inheritdoc />
        /// <exception cref="ArgumentException">The length is not a power of two.</exception>
        public void Forward(Span<double> interleaved) => Run(interleaved, forward: true);

        /// <inheritdoc />
        /// <exception cref="ArgumentException">The length is not a power of two.</exception>
        public void Inverse(Span<double> interleaved) => Run(interleaved, forward: false);

        private static unsafe void Run(Span<double> interleaved, bool forward)
        {
            if ((interleaved.Length & 1) != 0)
            {
                throw new ArgumentException(
                    "An interleaved complex buffer has an even length.", nameof(interleaved));
            }

            int points = interleaved.Length / 2;

            if (points == 0)
            {
                return;
            }

            fixed (double* data = &MemoryMarshal.GetReference(interleaved))
            {
                int ok = forward ? ForwardNative(data, points) : InverseNative(data, points);

                if (ok != 1)
                {
                    throw new ArgumentException(
                        "The native provider transforms power-of-two lengths only; " +
                        points + " points were given.",
                        nameof(interleaved));
                }
            }
        }

        /// <inheritdoc />
        public override string ToString() => Name + " (OpenVSA.Fft.Native)";
    }
}
