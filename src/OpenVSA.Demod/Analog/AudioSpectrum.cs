using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Demod.Analog
{
    /// <summary>
    /// The spectrum of the recovered audio, and the metrics read off it (<c>REQ-DEM-010a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>SINAD, distortion and residual are all one question asked three ways</strong> — how
    /// much of the audio power is the fundamental, how much is its harmonics, how much is neither —
    /// so they are computed from one transform rather than three. Computing them apart would let
    /// them disagree about which bins the fundamental occupies, and a SINAD that did not match its
    /// own distortion figure would be two measurements wearing one name.
    /// </para>
    /// <para>
    /// <strong>Blackman-Harris, for a reason the numbers depend on.</strong> The criterion asks for
    /// SINAD above 60 dB, and a window's sidelobes put a floor under any such figure: Hann's
    /// −31.5 dB would report about 40 dB on a perfect signal and the requirement could not be met
    /// with any detector whatever. Blackman-Harris is 92 dB down, which leaves the measurement
    /// describing the signal instead of the window.
    /// </para>
    /// <para>
    /// <strong>Power is summed over a lobe, not taken from a bin.</strong> A windowed tone lands
    /// across the window's main lobe wherever it falls between bins, so the peak bin alone holds
    /// only part of it and how much depends on where the tone happens to sit. Summing the lobe
    /// makes the answer independent of that, which is what stops SINAD changing when the modulation
    /// rate moves by half a bin.
    /// </para>
    /// </remarks>
    public sealed class AudioSpectrum
    {
        private readonly double[] _power;
        private readonly ReadOnlyCollection<double> _decibels;

        internal AudioSpectrum(double[] audio, double sampleRateHz)
        {
            int length = 1;

            while (length * 2 <= audio.Length)
            {
                length *= 2;
            }

            IFftProvider fft = FftProviders.Active;

            if (length < 16 || !fft.SupportsLength(length))
            {
                _power = new double[0];
                _decibels = new ReadOnlyCollection<double>(new List<double>());
                BinWidthHz = 0.0;
                FundamentalHz = double.NaN;
                return;
            }

            Window taper = Window.Get(WindowType.BlackmanHarris, length);
            ReadOnlySpan<double> weights = taper.Coefficients;
            var transform = new double[2 * length];

            // The audio is real, so the transform is symmetric and only the first half carries
            // anything the second does not. Loaded as a real signal rather than as an analytic one
            // because the audio genuinely is real -- an imaginary part would be a claim about a
            // quadrature component that does not exist.
            for (int sample = 0; sample < length; sample++)
            {
                transform[2 * sample] = audio[sample] * weights[sample];
                transform[(2 * sample) + 1] = 0.0;
            }

            fft.Forward(new Span<double>(transform));

            int bins = length / 2;

            _power = new double[bins];

            var decibels = new List<double>(bins);
            double scale = 1.0 / (taper.CoherentGain * length);

            for (int bin = 0; bin < bins; bin++)
            {
                double re = transform[2 * bin] * scale;
                double im = transform[(2 * bin) + 1] * scale;

                // Doubled for every bin but DC: the negative-frequency half carries the other half
                // of a real signal's power, and dropping it without doubling would halve every
                // amplitude. DC has no mirror image to take back.
                double power = ((re * re) + (im * im)) * (bin == 0 ? 1.0 : 2.0);

                _power[bin] = power;
                decibels.Add(10.0 * Math.Log10(Math.Max(power, 1e-40)));
            }

            _decibels = new ReadOnlyCollection<double>(decibels);
            BinWidthHz = sampleRateHz / length;
            FundamentalBin = FindFundamental();
            FundamentalHz = FundamentalBin < 0 ? double.NaN : Interpolated(FundamentalBin);
        }

        /// <summary>The spectrum, in decibels relative to the audio's own units.</summary>
        public IReadOnlyList<double> Decibels => _decibels;

        /// <summary>The frequency step between bins, in hertz.</summary>
        public double BinWidthHz { get; }

        /// <summary>The bin the fundamental sits in, or -1.</summary>
        public int FundamentalBin { get; } = -1;

        /// <summary>The modulation rate, interpolated between bins, in hertz.</summary>
        public double FundamentalHz { get; }

        /// <summary>
        /// The bin the fundamental sits in, ignoring the lowest few.
        /// </summary>
        /// <remarks>
        /// <strong>DC and its neighbours are skipped, and they have to be.</strong> The audio is
        /// taken about its own mean, so bin zero holds only what the window leaked there — but a
        /// record holding a whole number of modulation cycles is the exception rather than the
        /// rule, and the leakage of a large fundamental into the first bins can exceed a small
        /// harmonic. Starting the search past the window's own main lobe means the fundamental is
        /// found by being the largest thing that is not the residue of removing the carrier.
        /// </remarks>
        private int FindFundamental()
        {
            int first = 1 + 4;
            int best = -1;
            double most = 0.0;

            for (int bin = first; bin < _power.Length; bin++)
            {
                if (_power[bin] > most)
                {
                    most = _power[bin];
                    best = bin;
                }
            }

            return best;
        }

        /// <summary>
        /// The peak's frequency, interpolated across its neighbours.
        /// </summary>
        /// <remarks>
        /// A parabola through the three bins in decibels, which is the standard estimator and is
        /// exact for a Gaussian lobe. It matters here: at a bin width of tens of hertz, taking the
        /// peak bin's own frequency would miss a modulation rate by up to half a bin, and the
        /// criterion's tolerance on the rate is 1 %.
        /// </remarks>
        private double Interpolated(int bin)
        {
            if (bin <= 0 || bin + 1 >= _power.Length)
            {
                return bin * BinWidthHz;
            }

            double left = _decibels[bin - 1];
            double centre = _decibels[bin];
            double right = _decibels[bin + 1];

            double denominator = left - (2.0 * centre) + right;
            double offset = denominator == 0.0 ? 0.0 : 0.5 * (left - right) / denominator;

            return (bin + offset) * BinWidthHz;
        }

        /// <summary>Power summed over a lobe centred on a bin.</summary>
        /// <param name="centre">The lobe's centre bin.</param>
        /// <param name="halfWidth">Bins either side to include.</param>
        /// <returns>The power.</returns>
        public double LobePower(int centre, int halfWidth)
        {
            double sum = 0.0;

            for (int bin = Math.Max(1, centre - halfWidth);
                bin <= Math.Min(_power.Length - 1, centre + halfWidth);
                bin++)
            {
                sum += _power[bin];
            }

            return sum;
        }

        /// <summary>Every bin's power except DC and its window lobe.</summary>
        private double TotalPower()
        {
            double sum = 0.0;

            for (int bin = 5; bin < _power.Length; bin++)
            {
                sum += _power[bin];
            }

            return sum;
        }

        /// <summary>Signal to noise and distortion, in decibels.</summary>
        /// <param name="halfWidth">Bins either side of a peak counted as part of it.</param>
        /// <returns>The ratio, in decibels, or <see cref="double.NaN"/>.</returns>
        public double SinadDb(int halfWidth)
        {
            if (FundamentalBin < 0)
            {
                return double.NaN;
            }

            double total = TotalPower();
            double fundamental = LobePower(FundamentalBin, halfWidth);
            double rest = total - fundamental;

            if (rest <= 0.0)
            {
                // Everything measurable is the fundamental. A ratio of the total to nothing is not
                // infinity but the end of what this record can resolve, so it is reported as the
                // dynamic range rather than as a number that would look like a measurement.
                return 10.0 * Math.Log10(total / 1e-40);
            }

            return 10.0 * Math.Log10(total / rest);
        }

        /// <summary>Total harmonic distortion, in per cent of the fundamental.</summary>
        /// <param name="halfWidth">Bins either side of a peak counted as part of it.</param>
        /// <param name="harmonics">The highest harmonic counted.</param>
        /// <returns>The distortion, or <see cref="double.NaN"/>.</returns>
        public double DistortionPercent(int halfWidth, int harmonics)
        {
            if (FundamentalBin < 0)
            {
                return double.NaN;
            }

            double fundamental = LobePower(FundamentalBin, halfWidth);

            if (fundamental <= 0.0)
            {
                return double.NaN;
            }

            double sum = 0.0;

            for (int harmonic = 2; harmonic <= harmonics; harmonic++)
            {
                int bin = FundamentalBin * harmonic;

                if (bin + halfWidth >= _power.Length)
                {
                    break;
                }

                sum += LobePower(bin, halfWidth);
            }

            return Math.Sqrt(sum / fundamental) * 100.0;
        }

        /// <summary>
        /// The fraction of the audio's amplitude that is neither the fundamental nor a harmonic.
        /// </summary>
        /// <param name="halfWidth">Bins either side of a peak counted as part of it.</param>
        /// <param name="harmonics">The highest harmonic removed.</param>
        /// <returns>The residual as a fraction of the total rms.</returns>
        /// <remarks>
        /// What residual FM and residual AM are built from: take out what was deliberately put on
        /// the carrier and the rest is the carrier's own. Returned as a fraction so the caller
        /// applies the units — hertz for FM, per cent of carrier for AM — rather than this having
        /// to know which it is being asked for.
        /// </remarks>
        public double ResidualFraction(int halfWidth, int harmonics)
        {
            if (FundamentalBin < 0)
            {
                return double.NaN;
            }

            double total = TotalPower();

            if (total <= 0.0)
            {
                return 0.0;
            }

            double modulation = LobePower(FundamentalBin, halfWidth);

            for (int harmonic = 2; harmonic <= harmonics; harmonic++)
            {
                int bin = FundamentalBin * harmonic;

                if (bin + halfWidth >= _power.Length)
                {
                    break;
                }

                modulation += LobePower(bin, halfWidth);
            }

            double rest = Math.Max(0.0, total - modulation);

            return Math.Sqrt(rest / total);
        }
    }
}
