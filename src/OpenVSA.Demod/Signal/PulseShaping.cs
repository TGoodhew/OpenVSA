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
        internal static double[] RootRaisedCosine(
            double alpha, int samplesPerSymbol, int symbolSpan)
        {
            if (alpha < 0.0 || alpha > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(alpha), alpha, "Roll-off runs from 0 to 1.");
            }

            if (samplesPerSymbol < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samplesPerSymbol), samplesPerSymbol,
                    "A pulse needs at least two samples per symbol to have a shape.");
            }

            if (symbolSpan < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(symbolSpan), symbolSpan, "A pulse spans at least one symbol either side.");
            }

            int half = samplesPerSymbol * symbolSpan;
            var taps = new double[(2 * half) + 1];

            for (int tap = 0; tap < taps.Length; tap++)
            {
                double t = (tap - half) / (double)samplesPerSymbol;

                taps[tap] = Impulse(t, alpha);
            }

            double energy = 0.0;

            foreach (double tap in taps)
            {
                energy += tap * tap;
            }

            double scale = 1.0 / Math.Sqrt(energy);

            for (int tap = 0; tap < taps.Length; tap++)
            {
                taps[tap] *= scale;
            }

            return taps;
        }

        /// <summary>
        /// Convolves an interleaved signal with real taps, keeping the input's length and its
        /// timing.
        /// </summary>
        /// <param name="interleaved">The signal, real and imaginary alternating.</param>
        /// <param name="taps">The taps; an odd count, so the centre is a sample and not a gap.</param>
        /// <returns>A new buffer of the same length as the input.</returns>
        /// <remarks>
        /// The output is aligned on the taps' centre, so a symmetric filter leaves the signal's
        /// timing alone. Anything else would put a delay of half the filter into the chain that
        /// step 8 would then have to estimate away as a timing offset, and a timing estimate that
        /// is really a filter delay is the kind of thing that works until the filter length
        /// changes.
        /// </remarks>
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
        internal static double RaisedCosineAt(double symbols, double alpha)
        {
            const double Tiny = 1e-9;

            double sinc;

            if (Math.Abs(symbols) < Tiny)
            {
                sinc = 1.0;
            }
            else
            {
                sinc = Math.Sin(Math.PI * symbols) / (Math.PI * symbols);
            }

            if (alpha < Tiny)
            {
                return sinc;
            }

            double denominator = 1.0 - ((2.0 * alpha * symbols) * (2.0 * alpha * symbols));

            if (Math.Abs(denominator) < 1e-7)
            {
                // The removable singularity at t = ±1/2α. The limit is πsin(π/2α)/(4·π/2α)·…, and
                // rather than write that out it is evaluated as the average of two points either
                // side, which is exact to the precision this pulse is used at and cannot be got
                // subtly wrong.
                return 0.5 *
                    (RaisedCosineAt(symbols - 1e-5, alpha) + RaisedCosineAt(symbols + 1e-5, alpha));
            }

            return sinc * Math.Cos(Math.PI * alpha * symbols) / denominator;
        }

        private static double Impulse(double t, double alpha)
        {
            const double Tiny = 1e-9;

            if (Math.Abs(t) < Tiny)
            {
                return 1.0 + (alpha * ((4.0 / Math.PI) - 1.0));
            }

            if (alpha > Tiny && Math.Abs(Math.Abs(t) - (1.0 / (4.0 * alpha))) < Tiny)
            {
                // The removable singularity at t = ±T/4α, where the denominator's
                // 1 − (4αt)² term vanishes. Evaluated by its limit rather than by the
                // general form, which would divide by something near zero.
                double angle = Math.PI / (4.0 * alpha);

                return (alpha / Math.Sqrt(2.0)) *
                    (((1.0 + (2.0 / Math.PI)) * Math.Sin(angle)) +
                     ((1.0 - (2.0 / Math.PI)) * Math.Cos(angle)));
            }

            double numerator =
                Math.Sin(Math.PI * t * (1.0 - alpha)) +
                (4.0 * alpha * t * Math.Cos(Math.PI * t * (1.0 + alpha)));

            // πt(1 − (4αt)²), written in that form so it can be read against the standard
            // definition rather than against an algebraically equivalent rearrangement of it.
            double denominator =
                Math.PI * t * (1.0 - ((4.0 * alpha * t) * (4.0 * alpha * t)));

            return numerator / denominator;
        }
    }
}
