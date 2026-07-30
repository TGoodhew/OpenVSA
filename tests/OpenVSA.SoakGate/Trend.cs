using System;
using System.Collections.Generic;

namespace OpenVSA.SoakGate
{
    /// <summary>
    /// A least-squares line through a series, with the uncertainty of its own slope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a trend rather than a ceiling.</strong> <c>REQ-TST-009</c> says memory is to be
    /// judged "against a trend line over the run, not merely against a ceiling, so a slow leak that
    /// stays under the cap still fails". A ceiling cannot see the difference between a process that
    /// settled at 300 MB and one that is on its way through 300 MB to a gigabyte; a slope can.
    /// </para>
    /// <para>
    /// <strong>And why the uncertainty matters.</strong> A managed heap sawtooths, so any finite
    /// series has a non-zero slope. Reporting that as a leak would fail every healthy run. The
    /// standard error of the slope is what separates "rising" from "noisy": a slope smaller than its
    /// own uncertainty is a run that has not shown a rise, and it is reported as such rather than
    /// passed — the same distinction <c>RegressionGate</c> draws with <c>Inconclusive</c>.
    /// </para>
    /// </remarks>
    public sealed class Trend
    {
        private Trend(
            int count, double slope, double intercept, double standardError, double first, double last)
        {
            Count = count;
            Slope = slope;
            Intercept = intercept;
            StandardError = standardError;
            First = first;
            Last = last;
        }

        /// <summary>Points the line was fitted through.</summary>
        public int Count { get; }

        /// <summary>Rise per unit of x — bytes per hour, where x is hours.</summary>
        public double Slope { get; }

        /// <summary>Value the line takes at x = 0.</summary>
        public double Intercept { get; }

        /// <summary>
        /// The standard error of <see cref="Slope"/>, or <see cref="double.PositiveInfinity"/> when
        /// there are too few points to have one.
        /// </summary>
        public double StandardError { get; }

        /// <summary>The first y value.</summary>
        public double First { get; }

        /// <summary>The last y value.</summary>
        public double Last { get; }

        /// <summary>Whether there were enough points, spread widely enough, to fit a line at all.</summary>
        /// <remarks>
        /// Three, not two: two points always fit a line exactly, with no residual and so no
        /// uncertainty, and a run that sampled twice would report a perfectly determined slope from
        /// no evidence. That is the shape of the mistake behind the sampling-shortfall flake in
        /// <c>NoNetworkEgressTests</c> — a run that measured almost nothing reading as a clean pass.
        /// </remarks>
        public bool IsDetermined => Count >= 3 && !double.IsInfinity(StandardError);

        /// <summary>
        /// Whether the slope is positive by more than <paramref name="sigma"/> times its own error.
        /// </summary>
        /// <param name="sigma">How many standard errors to require. Two by convention.</param>
        public bool RisesSignificantly(double sigma = 2.0) =>
            IsDetermined && Slope > sigma * StandardError;

        /// <summary>
        /// Fits a line through a series.
        /// </summary>
        /// <param name="xs">The x values.</param>
        /// <param name="ys">The y values; must be the same length as <paramref name="xs"/>.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The two series differ in length.</exception>
        public static Trend Fit(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
        {
            if (xs == null)
            {
                throw new ArgumentNullException(nameof(xs));
            }

            if (ys == null)
            {
                throw new ArgumentNullException(nameof(ys));
            }

            if (xs.Count != ys.Count)
            {
                throw new ArgumentException(
                    "A trend needs one y for each x; got " + xs.Count + " and " + ys.Count + ".",
                    nameof(ys));
            }

            int n = xs.Count;

            if (n == 0)
            {
                return new Trend(0, double.NaN, double.NaN, double.PositiveInfinity, double.NaN, double.NaN);
            }

            double meanX = 0.0;
            double meanY = 0.0;

            for (int i = 0; i < n; i++)
            {
                meanX += xs[i];
                meanY += ys[i];
            }

            meanX /= n;
            meanY /= n;

            double sxx = 0.0;
            double sxy = 0.0;

            for (int i = 0; i < n; i++)
            {
                double dx = xs[i] - meanX;

                sxx += dx * dx;
                sxy += dx * (ys[i] - meanY);
            }

            if (n < 3 || sxx <= 0.0)
            {
                // Every sample at the same instant, or too few to have a residual: a slope could be
                // computed and would mean nothing.
                return new Trend(
                    n, double.NaN, double.NaN, double.PositiveInfinity, ys[0], ys[n - 1]);
            }

            double slope = sxy / sxx;
            double intercept = meanY - (slope * meanX);
            double residual = 0.0;

            for (int i = 0; i < n; i++)
            {
                double predicted = intercept + (slope * xs[i]);
                double error = ys[i] - predicted;

                residual += error * error;
            }

            // The textbook standard error of a fitted slope: residual variance over the spread in x,
            // with n - 2 degrees of freedom because the fit consumed two.
            double variance = residual / (n - 2);
            double error2 = Math.Sqrt(variance / sxx);

            return new Trend(n, slope, intercept, error2, ys[0], ys[n - 1]);
        }
    }
}
