using System;
using OpenVSA.Core;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The statistical trace data types of <c>REQ-DSP-040</c>: CCDF, CDF and PDF.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three describe the same thing — how often the instantaneous power sits a given
    /// distance above the average — and are computed from one pass over the samples. CCDF is the
    /// one an RF engineer reads, because peak-to-average ratio is what drives amplifier headroom.
    /// </para>
    /// <para>
    /// <strong>The x axis is dB above average power, not absolute.</strong> That is what makes a
    /// CCDF comparable between signals of different levels, and it is why the analytic reference
    /// for Gaussian noise — <c>P(x) = exp(−10^(x/10))</c> — has no level in it at all.
    /// </para>
    /// </remarks>
    public static class PowerStatistics
    {
        /// <summary>Default number of dB-above-average bins the curves are computed over.</summary>
        public const int DefaultBins = 200;

        /// <summary>Default upper edge of the x axis, in dB above average power.</summary>
        public const double DefaultRangeDb = 20.0;

        /// <summary>
        /// Computes the complementary cumulative distribution of instantaneous power.
        /// </summary>
        /// <param name="block">The samples to characterise.</param>
        /// <param name="bins">Points along the dB-above-average axis; must be at least two.</param>
        /// <param name="rangeDb">Upper edge of the axis, in dB above average power.</param>
        /// <returns>The curve, and the statistics that go with it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public static PowerDistribution Ccdf(
            IqBlock block, int bins = DefaultBins, double rangeDb = DefaultRangeDb)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            if (bins < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(bins), bins, "At least two bins.");
            }

            if (!(rangeDb > 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rangeDb), rangeDb, "The range must be positive.");
            }

            ReadOnlySpan<float> samples = block.GetSamples();
            int count = block.SampleCount;

            // Mean power first: everything after is relative to it, so it cannot be computed in
            // the same pass as the histogram.
            double sum = 0.0;

            for (int n = 0; n < count; n++)
            {
                double i = samples[n * 2];
                double q = samples[n * 2 + 1];
                sum += i * i + q * q;
            }

            double mean = count > 0 ? sum / count : 0.0;

            var above = new long[bins];
            double step = rangeDb / (bins - 1);
            double peakRatioDb = double.NegativeInfinity;

            if (mean > 0.0)
            {
                for (int n = 0; n < count; n++)
                {
                    double i = samples[n * 2];
                    double q = samples[n * 2 + 1];
                    double power = i * i + q * q;

                    if (!(power > 0.0))
                    {
                        continue;
                    }

                    double ratioDb = 10.0 * Math.Log10(power / mean);

                    if (ratioDb > peakRatioDb)
                    {
                        peakRatioDb = ratioDb;
                    }

                    // Every bin at or below this sample's ratio counts it: the CCDF at x is the
                    // proportion exceeding x, so a sample contributes to all x below it.
                    int highest = (int)Math.Floor(ratioDb / step);

                    if (highest >= bins)
                    {
                        highest = bins - 1;
                    }

                    for (int b = 0; b <= highest; b++)
                    {
                        above[b]++;
                    }
                }
            }

            var probability = new double[bins];
            var axisDb = new double[bins];

            for (int b = 0; b < bins; b++)
            {
                axisDb[b] = b * step;
                probability[b] = count > 0 ? (double)above[b] / count : 0.0;
            }

            return new PowerDistribution(
                axisDb, probability, mean,
                double.IsNegativeInfinity(peakRatioDb) ? 0.0 : peakRatioDb);
        }

        /// <summary>
        /// The analytic CCDF of complex Gaussian noise: <c>P(x) = exp(−10^(x/10))</c>.
        /// </summary>
        /// <param name="decibelsAboveAverage">How far above average power, in dB.</param>
        /// <remarks>
        /// The reference curve <c>REQ-DSP-042</c> requires be overlaid, and the one its accuracy
        /// criterion is stated against. Its envelope is Rayleigh, so its power is exponential,
        /// which is where this closed form comes from.
        /// </remarks>
        public static double GaussianCcdf(double decibelsAboveAverage) =>
            Math.Exp(-Math.Pow(10.0, decibelsAboveAverage / 10.0));
    }

    /// <summary>A power distribution curve and the statistics that accompany it.</summary>
    public sealed class PowerDistribution
    {
        private readonly double[] _axisDb;
        private readonly double[] _ccdf;

        internal PowerDistribution(
            double[] axisDb, double[] ccdf, double meanPowerVoltsSquared, double peakToAverageDb)
        {
            _axisDb = axisDb;
            _ccdf = ccdf;
            MeanPowerVoltsSquared = meanPowerVoltsSquared;
            PeakToAverageDb = peakToAverageDb;
        }

        /// <summary>The x axis, in dB above average power.</summary>
        public ReadOnlySpan<double> AxisDb => new ReadOnlySpan<double>(_axisDb);

        /// <summary>The complementary cumulative distribution: the proportion exceeding each x.</summary>
        public ReadOnlySpan<double> Ccdf => new ReadOnlySpan<double>(_ccdf);

        /// <summary>Mean power, in volts squared.</summary>
        public double MeanPowerVoltsSquared { get; }

        /// <summary>Peak-to-average ratio of the samples, in dB.</summary>
        public double PeakToAverageDb { get; }

        /// <summary>Points in the curve.</summary>
        public int PointCount => _axisDb.Length;

        /// <summary>The cumulative distribution: the proportion at or below each x.</summary>
        /// <param name="destination">Receives <see cref="PointCount"/> values.</param>
        /// <exception cref="ArgumentException">The destination is the wrong length.</exception>
        /// <remarks>
        /// One minus the CCDF, by definition. Derived rather than accumulated separately, so the
        /// two cannot disagree.
        /// </remarks>
        public void Cdf(Span<double> destination)
        {
            Require(destination.Length == PointCount, nameof(destination));

            for (int i = 0; i < PointCount; i++)
            {
                destination[i] = 1.0 - _ccdf[i];
            }
        }

        /// <summary>The probability density: the difference of the CDF between adjacent points.</summary>
        /// <param name="destination">Receives <see cref="PointCount"/> values.</param>
        /// <exception cref="ArgumentException">The destination is the wrong length.</exception>
        public void Pdf(Span<double> destination)
        {
            Require(destination.Length == PointCount, nameof(destination));

            for (int i = 0; i < PointCount - 1; i++)
            {
                destination[i] = _ccdf[i] - _ccdf[i + 1];
            }

            destination[PointCount - 1] = 0.0;
        }

        /// <summary>
        /// The dB above average at which the CCDF falls to a probability.
        /// </summary>
        /// <param name="probability">The probability, such as 0.0001 for the 0.01 % point.</param>
        /// <returns>The x value, interpolated between points, or <see cref="double.NaN"/>.</returns>
        /// <remarks>
        /// <c>REQ-DSP-042</c> asks for peak-to-average at the 0.01 %, 0.1 % and 1 % points, which
        /// is what this answers.
        /// </remarks>
        public double RatioAt(double probability)
        {
            for (int i = 1; i < PointCount; i++)
            {
                if (_ccdf[i] <= probability && _ccdf[i - 1] > probability)
                {
                    double span = _ccdf[i - 1] - _ccdf[i];
                    double fraction = span <= 0.0 ? 0.0 : (_ccdf[i - 1] - probability) / span;
                    return _axisDb[i - 1] + fraction * (_axisDb[i] - _axisDb[i - 1]);
                }
            }

            return double.NaN;
        }

        private static void Require(bool condition, string name)
        {
            if (!condition)
            {
                throw new ArgumentException("The destination must match the curve's length.", name);
            }
        }
    }
}
