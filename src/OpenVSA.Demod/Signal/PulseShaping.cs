using System;

namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// The root-raised-cosine pulse, and the convolution that applies it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scope.</strong> The filter catalogue of <c>REQ-DEM-021</c> — nine types, the EDGE
    /// pulse among them — the mathematics of <c>REQ-DEM-022</c> and the truncation rules of
    /// <c>REQ-DEM-023</c> are separate requirements with their own issues. <c>REQ-DEM-001</c> needs
    /// steps 5 and 10 to have a filter to be, and root raised cosine is the one the matched pair of
    /// <c>REQ-DEM-020</c> is stated in terms of. This is that filter and no more; it is internal so
    /// that the catalogue can define the public surface without having to work around a shape
    /// chosen here.
    /// </para>
    /// <para>
    /// <strong>Unit energy, deliberately.</strong> The taps are scaled so their squared sum is one.
    /// A matched pair of unit-energy root-raised-cosine filters then has unity gain at the symbol
    /// centre, because the composite's centre tap is exactly the sum of the squares — which keeps
    /// the amplitude the joint refinement estimates the signal's own rather than the filter's.
    /// <c>REQ-DEM-022a</c> owns the normalisation convention for the catalogue and may state a
    /// different one; this note records what is assumed here so that requirement can find it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Applying a filter to a waveform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a filter <em>is</em> lives in <see cref="PulseFilter"/>; this is what is done with one.
    /// The split matters because of <c>REQ-DEM-022a</c>: there is one place a filter is built and
    /// one place it is normalised, and this class deliberately has neither.
    /// </para>
    /// <para>
    /// <strong>It used to have both.</strong> A root raised cosine was built here and normalised to
    /// unit energy — a third convention alongside the raised cosine's unit peak and the Gaussian's
    /// unit area, which is the exact situation that requirement exists to end. The raised cosine's
    /// removable singularity was also handled here by averaging two points either side of it, which
    /// the same requirement names as the thing not to do. Both are gone; the analytic limits and the
    /// single normalisation are in <see cref="PulseFilter"/>.
    /// </para>
    /// </remarks>
    internal static class PulseShaping
    {
        /// <summary>
        /// Root-raised-cosine taps at a whole number of samples per symbol.
        /// </summary>
        /// <param name="alpha">The roll-off, from zero to one.</param>
        /// <param name="samplesPerSymbol">Samples per symbol; at least two.</param>
        /// <param name="symbolSpan">
        /// How many symbol periods the pulse spans either side of its centre.
        /// </param>
        /// <returns>An odd number of taps, symmetric about the centre, of unit energy.</returns>
        /// <exception cref="ArgumentOutOfRangeException">An argument is outside its range.</exception>
        internal static double[] Convolve(double[] interleaved, double[] taps)
        {
            int samples = Iq.Count(interleaved);
            int centre = taps.Length / 2;
            var output = new double[interleaved.Length];

            for (int sample = 0; sample < samples; sample++)
            {
                double i = 0.0;
                double q = 0.0;

                for (int tap = 0; tap < taps.Length; tap++)
                {
                    int source = sample + centre - tap;

                    if (source < 0 || source >= samples)
                    {
                        continue;
                    }

                    i += taps[tap] * interleaved[2 * source];
                    q += taps[tap] * interleaved[(2 * source) + 1];
                }

                output[2 * sample] = i;
                output[(2 * sample) + 1] = q;
            }

            return output;
        }

        /// <summary>
        /// The raised-cosine pulse at an arbitrary position, in symbols from its centre.
        /// </summary>
        /// <param name="symbols">How far from the centre, in symbol periods.</param>
        /// <param name="alpha">The roll-off.</param>
        /// <returns>The pulse's value, unity at the centre.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Why the reference is this and not the root.</strong> <c>REQ-DEM-020</c> states
        /// the arrangement: the transmitter shapes with a root-raised cosine, the analyser's
        /// measurement filter emulates the receiver's matching half, and the two together make the
        /// full Nyquist filter. So the waveform arriving at step 10 has already been through both
        /// halves, and the ideal waveform it is compared against has to have been through both too.
        /// Regenerating the reference with another root would compare a raised cosine against a
        /// root raised cosine and read the difference as distortion — a fixed several per cent of
        /// EVM, present on a perfect signal, that no impairment put there.
        /// </para>
        /// <para>
        /// Unity at the centre by construction, which matches a matched pair of unit-energy roots:
        /// their composite's centre is the sum of the squares of the taps, and that is one.
        /// </para>
        /// </remarks>
    }
}
