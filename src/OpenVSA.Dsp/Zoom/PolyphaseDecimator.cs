using System;

namespace OpenVSA.Dsp.Zoom
{
    /// <summary>
    /// Filters and decimates a complex record in one pass (<c>REQ-DSP-023a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The polyphase decimator's arithmetic, written without the commutator.</strong> A
    /// decimator's polyphase decomposition splits the taps into <c>M</c> sub-filters and runs each
    /// against its own phase of the input; the saving over a naive design is that the outputs that
    /// would be thrown away are never computed. Evaluating the filter only at the surviving output
    /// instants gets the same saving — <c>N/M</c> multiply-accumulates per input sample either way
    /// — and does it walking taps and samples contiguously, whereas the sub-filter form strides
    /// the input by <c>M</c> and misses the cache on every tap. The structure is the polyphase
    /// decimator's; the loop order is the one that runs.
    /// </para>
    /// <para>
    /// <strong>Real and complex taps, one engine.</strong> Decimation without a frequency shift
    /// needs real taps; the zoom downconverter needs the same low-pass shifted onto the carrier,
    /// which makes it complex (see <see cref="DigitalDownconverter"/>). Those are two tap sets
    /// through one convolution, not two convolutions, so the real case takes a branch at the top of
    /// <see cref="Decimate"/> and half the multiplies rather than living in its own class.
    /// </para>
    /// <para>
    /// <strong>A record at a time, and only the outputs the record actually supports.</strong>
    /// <c>REQ-DSP-023</c> zooms "using only the captured block", so there is no stream to carry
    /// filter state across. Outputs whose window would reach outside the record are not produced.
    /// Zero-padding to produce them instead would return samples computed from fabricated data,
    /// indistinguishable at the caller from measured ones — and the first <c>N/2</c> of them would
    /// be visibly wrong in exactly the amplitude the whole requirement is about. The caller loses
    /// <see cref="AlignmentOffsetSamples"/> input samples at the start and
    /// <see cref="GroupDelaySamples"/> at the end, and can find out how many outputs remain from
    /// <see cref="OutputCountFor"/> before committing to anything.
    /// </para>
    /// <para>
    /// <strong>Accumulation is in <see cref="double"/> however the samples are stored.</strong>
    /// <c>REQ-DSP-002</c>, and here it is load-bearing rather than routine: a 9 000-tap sum at
    /// single precision would sit near −100 dBc on its own and there would be no stopband left to
    /// measure.
    /// </para>
    /// </remarks>
    public sealed class PolyphaseDecimator
    {
        // Held in correlation order - kernel[t] is taps[N-1-t] - so the inner loop can walk taps
        // and samples in the same direction. For the symmetric real low-pass this is the same array
        // reversed onto itself; for the downconverter's shifted taps it is not, and getting the
        // direction wrong there conjugates the frequency shift rather than failing.
        private readonly double[] _kernelI;
        private readonly double[] _kernelQ;
        private readonly int _decimation;
        private readonly int _groupDelay;
        private readonly int _alignmentOffset;

        private PolyphaseDecimator(double[] kernelI, double[] kernelQ, int decimation)
        {
            _kernelI = kernelI;
            _kernelQ = kernelQ;
            _decimation = decimation;
            _groupDelay = (kernelI.Length - 1) / 2;

            // The output grid is locked to input sample zero, so output m is the filtered value at
            // input index m*M. The first one the record supports is the first multiple of M at or
            // beyond the group delay.
            int firstOutput = (_groupDelay + decimation - 1) / decimation;
            _alignmentOffset = firstOutput * decimation;
        }

        /// <summary>
        /// Builds a decimator from real taps.
        /// </summary>
        /// <param name="taps">Filter taps; must be non-empty and of odd length.</param>
        /// <param name="decimation">Decimation factor; must be at least 1.</param>
        /// <exception cref="ArgumentNullException"><paramref name="taps"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="taps"/> is empty or of even length.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="decimation"/> is less than 1.</exception>
        public static PolyphaseDecimator WithRealTaps(double[] taps, int decimation)
        {
            RequireOddTaps(taps, nameof(taps));
            RequireDecimation(decimation);

            return new PolyphaseDecimator(Reverse(taps), null, decimation);
        }

        /// <summary>
        /// Builds a decimator from complex taps.
        /// </summary>
        /// <param name="tapsI">Real parts of the taps; must be non-empty and of odd length.</param>
        /// <param name="tapsQ">Imaginary parts, the same length as <paramref name="tapsI"/>.</param>
        /// <param name="decimation">Decimation factor; must be at least 1.</param>
        /// <exception cref="ArgumentNullException">A tap array is null.</exception>
        /// <exception cref="ArgumentException">The taps are empty, of even length, or of differing lengths.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="decimation"/> is less than 1.</exception>
        public static PolyphaseDecimator WithComplexTaps(double[] tapsI, double[] tapsQ, int decimation)
        {
            RequireOddTaps(tapsI, nameof(tapsI));

            if (tapsQ == null)
            {
                throw new ArgumentNullException(nameof(tapsQ));
            }

            if (tapsQ.Length != tapsI.Length)
            {
                throw new ArgumentException(
                    "The real and imaginary parts must describe the same number of taps.",
                    nameof(tapsQ));
            }

            RequireDecimation(decimation);

            return new PolyphaseDecimator(Reverse(tapsI), Reverse(tapsQ), decimation);
        }

        /// <summary>Number of taps.</summary>
        public int TapCount => _kernelI.Length;

        /// <summary>Decimation factor.</summary>
        public int Decimation => _decimation;

        /// <summary>Whether the taps are complex.</summary>
        public bool HasComplexTaps => _kernelQ != null;

        /// <summary>
        /// Filter group delay, <c>(N − 1) / 2</c> input samples — an exact integer, not a rounded one.
        /// </summary>
        /// <remarks>
        /// The reason the taps are required to be of odd length. An even-length symmetric filter has
        /// a half-sample group delay, which would leave every zoomed record offset from the one it
        /// came from by half a sample and need a fractional-delay interpolator to put back — an
        /// interpolator that would have its own passband ripple, inside a requirement about
        /// passband ripple.
        /// </remarks>
        public int GroupDelaySamples => _groupDelay;

        /// <summary>
        /// Index of the input sample the first output is aligned to.
        /// </summary>
        /// <remarks>
        /// Output <c>j</c> is the filtered value at input index
        /// <c>AlignmentOffsetSamples + j × Decimation</c>, with the group delay already removed. So
        /// a decimated record starts this many input samples into the original, and a caller
        /// carrying timestamps or a trigger offset must advance them by
        /// <c>AlignmentOffsetSamples / Fs_in</c> seconds.
        /// </remarks>
        public int AlignmentOffsetSamples => _alignmentOffset;

        /// <summary>
        /// How many output samples a record of a given length yields.
        /// </summary>
        /// <param name="inputSampleCount">Complex samples available; must not be negative.</param>
        /// <returns>The output count, which is zero for a record too short to support any.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputSampleCount"/> is negative.</exception>
        public int OutputCountFor(int inputSampleCount)
        {
            if (inputSampleCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inputSampleCount), inputSampleCount, "A sample count cannot be negative.");
            }

            // The last supported output sits at input index (L - 1 - D) rounded down to the grid.
            int last = inputSampleCount - 1 - _groupDelay;

            if (last < _alignmentOffset)
            {
                return 0;
            }

            return (last - _alignmentOffset) / _decimation + 1;
        }

        /// <summary>
        /// The shortest record that yields at least one output sample.
        /// </summary>
        public int MinimumInputSamples => _alignmentOffset + _groupDelay + 1;

        /// <summary>
        /// Filters and decimates a record.
        /// </summary>
        /// <param name="input">Interleaved I,Q input; its length must be even.</param>
        /// <param name="output">
        /// Interleaved I,Q output; must hold at least <see cref="OutputCountFor"/> complex samples.
        /// </param>
        /// <returns>The number of complex samples written.</returns>
        /// <exception cref="ArgumentException"><paramref name="input"/> has an odd length.</exception>
        /// <exception cref="ArgumentException"><paramref name="output"/> is too short.</exception>
        public int Decimate(ReadOnlySpan<float> input, Span<float> output)
        {
            if ((input.Length & 1) != 0)
            {
                throw new ArgumentException(
                    "Interleaved I,Q data must have an even number of floats.", nameof(input));
            }

            int count = OutputCountFor(input.Length / 2);

            if (output.Length < count * 2)
            {
                throw new ArgumentException(
                    "The output span holds " + (output.Length / 2).ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " complex samples; this record decimates to " +
                    count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
                    nameof(output));
            }

            if (count == 0)
            {
                return 0;
            }

            if (_kernelQ == null)
            {
                DecimateReal(input, output, count);
            }
            else
            {
                DecimateComplex(input, output, count);
            }

            return count;
        }

        private void DecimateReal(ReadOnlySpan<float> input, Span<float> output, int count)
        {
            double[] kernel = _kernelI;
            int taps = kernel.Length;

            for (int j = 0; j < count; j++)
            {
                // The window runs from (centre - D) to (centre + D); it is the group delay being an
                // exact integer that lets it be a whole-sample window at all.
                int start = (_alignmentOffset + j * _decimation - _groupDelay) * 2;
                double accI = 0.0;
                double accQ = 0.0;

                for (int t = 0, o = start; t < taps; t++, o += 2)
                {
                    double tap = kernel[t];

                    accI += tap * input[o];
                    accQ += tap * input[o + 1];
                }

                output[j * 2] = (float)accI;
                output[j * 2 + 1] = (float)accQ;
            }
        }

        private void DecimateComplex(ReadOnlySpan<float> input, Span<float> output, int count)
        {
            double[] kernelI = _kernelI;
            double[] kernelQ = _kernelQ;
            int taps = kernelI.Length;

            for (int j = 0; j < count; j++)
            {
                int start = (_alignmentOffset + j * _decimation - _groupDelay) * 2;
                double accI = 0.0;
                double accQ = 0.0;

                for (int t = 0, o = start; t < taps; t++, o += 2)
                {
                    double tapI = kernelI[t];
                    double tapQ = kernelQ[t];
                    double sampleI = input[o];
                    double sampleQ = input[o + 1];

                    accI += tapI * sampleI - tapQ * sampleQ;
                    accQ += tapI * sampleQ + tapQ * sampleI;
                }

                output[j * 2] = (float)accI;
                output[j * 2 + 1] = (float)accQ;
            }
        }

        private static double[] Reverse(double[] taps)
        {
            var kernel = new double[taps.Length];

            for (int i = 0; i < taps.Length; i++)
            {
                kernel[i] = taps[taps.Length - 1 - i];
            }

            return kernel;
        }

        private static void RequireOddTaps(double[] taps, string name)
        {
            if (taps == null)
            {
                throw new ArgumentNullException(name);
            }

            if (taps.Length == 0)
            {
                throw new ArgumentException("A filter needs at least one tap.", name);
            }

            if (taps.Length % 2 == 0)
            {
                throw new ArgumentException(
                    "A filter must have an odd number of taps, so that its group delay is a whole " +
                    "number of samples.",
                    name);
            }
        }

        private static void RequireDecimation(int decimation)
        {
            if (decimation < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decimation), decimation, "A decimation factor must be at least 1.");
            }
        }
    }
}
