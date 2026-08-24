using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// One pulse-shaping filter from <c>REQ-DEM-021</c>'s catalogue: its shape, its parameter, and
    /// the taps it makes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Analytic form first, one normalisation afterwards.</strong> <see cref="At"/> is each
    /// filter written the way its own literature writes it — raised cosine and root raised cosine at
    /// unit peak with the customary 1/T omitted, Gaussian at unit area, EDGE as 3GPP TS 45.004
    /// defines it and therefore at a peak of 0.9268. Those are three incompatible conventions sitting
    /// next to each other, which is precisely what <c>REQ-DEM-022a</c> is about; so nothing outside
    /// this class uses <see cref="At"/> raw. <see cref="Taps"/> and <see cref="Shape"/> are the only
    /// ways out, and both go through <see cref="Normalise"/>.
    /// </para>
    /// <para>
    /// <strong>The two roles are normalised differently, and it is stated here because
    /// <c>REQ-DEM-022a</c> requires it to be stated somewhere:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="FilterRole.Measurement"/> — <strong>unit DC gain</strong>. An unmodulated
    /// carrier comes out at exactly the amplitude it went in at, at every span, so
    /// <c>REQ-DEM-023</c>'s "changing the span does not change the measured amplitude of a CW
    /// tone by more than 0.01 dB" is true by construction rather than by luck: it is 0.00 dB,
    /// and a measurement's absolute level does not depend on a filter setting nobody thinks of
    /// as a gain. In the tap domain that is <c>Σh = 1</c>, because the discrete convolution's DC
    /// gain is the sum of its taps; in the continuous domain it is <c>∫h dt = 1</c>. The two
    /// differ by a factor of the sample rate, as a sum and an integral of the same function
    /// always do, and each is unit gain for the convolution it belongs to.
    /// </description></item>
    /// <item><description>
    /// <see cref="FilterRole.Reference"/> — <strong>unit peak</strong>. The reference waveform's
    /// whole purpose is that its value at a symbol instant IS the constellation point that was
    /// decided; anything but unit peak scales the ideal waveform away from the measured one and puts
    /// the difference into EVM. This is why the two roles cannot share one convention.
    /// </description></item>
    /// </list>
    /// <para>
    /// <strong>Truncation is windowed, and the window was chosen by measurement.</strong> See
    /// <see cref="TaperFraction"/>: <c>REQ-DEM-023</c> wants sidelobes below a rectangular
    /// truncation's and <c>REQ-DEM-022a</c> wants the RRC cascade to stay close to the raised
    /// cosine, and those pull in opposite directions. A Hann or Blackman window wins the first and
    /// loses the second by an order of magnitude. The numbers are in
    /// <c>evidence/req-dem-022a/</c>.
    /// </para>
    /// </remarks>
    public sealed class PulseFilter
    {
        /// <summary>
        /// How much of the filter's length the truncation taper occupies, as a fraction.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A quarter — an eighth of the span at each end — raised-cosine shaped, so the taps reach
        /// zero smoothly instead of stopping. <c>REQ-DEM-023</c> requires that; the reason it is a
        /// quarter and not a whole Hann window is that <c>REQ-DEM-022a</c> requires something else,
        /// and the two were measured against each other rather than argued about
        /// (α = 0.35, 16 samples a symbol, <c>evidence/req-dem-022a/cascade-and-window.txt</c>):
        /// </para>
        /// <code>
        /// window          cascade ±8   cascade ±64   worst sidelobe at ±8
        /// rectangular       5.40e-4      3.18e-6           −55.6 dB
        /// tukey 0.25        6.89e-4      1.51e-6           −61.0 dB
        /// hann              4.71e-3      2.77e-5           −95.9 dB
        /// blackman          7.42e-3      4.52e-5          −105.9 dB
        /// </code>
        /// <para>
        /// <c>REQ-DEM-022a</c> demands the cascade stay under 1e-3 at the ±8 default and 5e-6 at
        /// ±64. Hann and Blackman fail both, by four and eight times. The quarter taper meets both
        /// requirements at once: quieter sidelobes than a rectangular truncation at every span, and
        /// a cascade error that is <em>better</em> than rectangular's at ±64 rather than worse.
        /// </para>
        /// <para>
        /// The rectangular row also reproduces the floors <c>REQ-DEM-022a</c> quotes — 5.4e-4,
        /// 1.1e-4, 1.1e-5, 3.2e-6 — which is what says the measurement and the requirement are
        /// talking about the same thing.
        /// </para>
        /// </remarks>
        public const double TaperFraction = 0.25;

        /// <summary>The default roll-off for the raised-cosine pair.</summary>
        public const double DefaultAlpha = 0.35;

        /// <summary>The default bandwidth–time product for the Gaussian; GSM's value.</summary>
        public const double DefaultBandwidthTime = 0.3;

        /// <summary>The default low-pass cutoff, as a fraction of the symbol rate.</summary>
        /// <remarks>
        /// A half, which is the Nyquist bandwidth of the symbol rate — so the default low-pass is
        /// the Nyquist sinc, and a user who has not chosen a cutoff has not chosen a filter that
        /// throws away signal.
        /// </remarks>
        public const double DefaultCutoff = 0.5;

        private readonly ReadOnlyCollection<double> _userTaps;

        private PulseFilter(
            PulseFilterType type,
            double alpha,
            double bandwidthTime,
            double cutoff,
            IList<double> userTaps,
            int userSamplesPerSymbol)
        {
            Type = type;
            Alpha = alpha;
            BandwidthTime = bandwidthTime;
            Cutoff = cutoff;
            UserSamplesPerSymbol = userSamplesPerSymbol;
            _userTaps = userTaps == null
                ? null
                : new ReadOnlyCollection<double>(new List<double>(userTaps));
        }

        /// <summary>Which filter this is.</summary>
        public PulseFilterType Type { get; }

        /// <summary>The roll-off, for the raised-cosine pair; otherwise not used.</summary>
        public double Alpha { get; }

        /// <summary>The bandwidth–time product, for the Gaussian; otherwise not used.</summary>
        public double BandwidthTime { get; }

        /// <summary>The cutoff as a fraction of the symbol rate, for the low-pass.</summary>
        public double Cutoff { get; }

        /// <summary>The taps a user supplied, or <c>null</c>.</summary>
        public IReadOnlyList<double> UserTaps => _userTaps;

        /// <summary>How many samples a symbol the user's taps were given at.</summary>
        public int UserSamplesPerSymbol { get; }

        /// <summary>Root raised cosine.</summary>
        /// <param name="alpha">The roll-off, from 0 to 1.</param>
        /// <exception cref="ArgumentOutOfRangeException">The roll-off is outside 0 to 1.</exception>
        public static PulseFilter RootRaisedCosine(double alpha = DefaultAlpha) =>
            new PulseFilter(
                PulseFilterType.RootRaisedCosine, RequireAlpha(alpha), DefaultBandwidthTime,
                DefaultCutoff, null, 0);

        /// <summary>Raised cosine.</summary>
        /// <param name="alpha">The roll-off, from 0 to 1.</param>
        /// <exception cref="ArgumentOutOfRangeException">The roll-off is outside 0 to 1.</exception>
        public static PulseFilter RaisedCosine(double alpha = DefaultAlpha) =>
            new PulseFilter(
                PulseFilterType.RaisedCosine, RequireAlpha(alpha), DefaultBandwidthTime,
                DefaultCutoff, null, 0);

        /// <summary>Gaussian.</summary>
        /// <param name="bandwidthTime">The bandwidth–time product; positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">The product is not positive and finite.</exception>
        public static PulseFilter Gaussian(double bandwidthTime = DefaultBandwidthTime)
        {
            if (!(bandwidthTime > 0.0) || double.IsInfinity(bandwidthTime))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bandwidthTime),
                    bandwidthTime,
                    "A bandwidth-time product is positive and finite.");
            }

            return new PulseFilter(
                PulseFilterType.Gaussian, DefaultAlpha, bandwidthTime, DefaultCutoff, null, 0);
        }

        /// <summary>The EDGE pulse: the linearised-GMSK main pulse of 3GPP TS 45.004.</summary>
        public static PulseFilter Edge() =>
            new PulseFilter(
                PulseFilterType.Edge, DefaultAlpha, DefaultBandwidthTime, DefaultCutoff, null, 0);

        /// <summary>Half sine.</summary>
        public static PulseFilter HalfSine() =>
            new PulseFilter(
                PulseFilterType.HalfSine, DefaultAlpha, DefaultBandwidthTime, DefaultCutoff,
                null, 0);

        /// <summary>Rectangular.</summary>
        public static PulseFilter Rectangular() =>
            new PulseFilter(
                PulseFilterType.Rectangular, DefaultAlpha, DefaultBandwidthTime, DefaultCutoff,
                null, 0);

        /// <summary>An ideal low-pass.</summary>
        /// <param name="cutoff">The cutoff, as a fraction of the symbol rate; positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">The cutoff is not positive and finite.</exception>
        public static PulseFilter LowPass(double cutoff = DefaultCutoff)
        {
            if (!(cutoff > 0.0) || double.IsInfinity(cutoff))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cutoff),
                    cutoff,
                    "A cutoff is a positive fraction of the symbol rate.");
            }

            return new PulseFilter(
                PulseFilterType.LowPass, DefaultAlpha, DefaultBandwidthTime, cutoff, null, 0);
        }

        /// <summary>No shaping.</summary>
        public static PulseFilter None() =>
            new PulseFilter(
                PulseFilterType.None, DefaultAlpha, DefaultBandwidthTime, DefaultCutoff, null, 0);

        /// <summary>
        /// A filter the user supplied as taps (<c>REQ-DEM-021</c>).
        /// </summary>
        /// <param name="taps">The taps, centre in the middle; an odd count of at least three.</param>
        /// <param name="samplesPerSymbol">How many samples a symbol they were sampled at.</param>
        /// <returns>The filter.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="taps"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// There are fewer than three taps, an even number of them, or they are all zero.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">The sample rate is below two a symbol.</exception>
        /// <remarks>
        /// <para>
        /// <strong>An odd count, because a filter needs a centre.</strong> Every other filter here is
        /// an even function evaluated about zero; a tap list with no middle tap has its centre
        /// between two samples, and every position this class reports would be half a sample out
        /// against the rest of the catalogue.
        /// </para>
        /// <para>
        /// <strong>The rate the taps were sampled at is part of the filter</strong>, because a tap
        /// list is a sampled function and nothing about the numbers says how fast. Asked for at a
        /// different rate, the taps are interpolated — see <see cref="At"/>, where the choice of
        /// interpolation is recorded.
        /// </para>
        /// </remarks>
        public static PulseFilter UserDefined(IList<double> taps, int samplesPerSymbol)
        {
            if (taps == null)
            {
                throw new ArgumentNullException(nameof(taps));
            }

            if (taps.Count < 3)
            {
                throw new ArgumentException(
                    "A filter needs at least three taps to have a shape.", nameof(taps));
            }

            if ((taps.Count % 2) == 0)
            {
                throw new ArgumentException(
                    "A filter needs a centre tap, so the count is odd. This one has " +
                    taps.Count.ToString(CultureInfo.InvariantCulture) + ".",
                    nameof(taps));
            }

            if (samplesPerSymbol < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samplesPerSymbol),
                    samplesPerSymbol,
                    "Taps sampled at fewer than two a symbol do not describe a pulse shape.");
            }

            double magnitude = 0.0;

            foreach (double tap in taps)
            {
                magnitude += Math.Abs(tap);
            }

            if (magnitude <= 0.0)
            {
                throw new ArgumentException(
                    "Every tap is zero, which is not a filter — it is a signal turned off. " +
                    "PulseFilterType.None is how a measurement says no shaping.",
                    nameof(taps));
            }

            return new PulseFilter(
                PulseFilterType.UserDefined,
                DefaultAlpha,
                DefaultBandwidthTime,
                DefaultCutoff,
                taps,
                samplesPerSymbol);
        }

        /// <summary>
        /// The filter's impulse response, in its own conventional analytic form.
        /// </summary>
        /// <param name="symbols">How far from the centre, in symbol periods.</param>
        /// <returns>The value; the conventions differ between filters, deliberately.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Public so that it can be checked against the formulas, and normalised by nothing.
        /// </strong> <c>REQ-DEM-022</c> asks that each filter's coefficients match its published
        /// expression at every tap, and a value that had been through a normalisation could not be
        /// compared with a formula that had not.
        /// </para>
        /// <para>
        /// <strong>The removable singularities return their analytic limits.</strong> t = 0 for all
        /// three of the closed forms, t = ±T/2α for the raised cosine and t = ±T/4α for its root:
        /// each is a 0/0 whose limit is written out here rather than approached with an epsilon or
        /// averaged across, which <c>REQ-DEM-022a</c> asks for by name.
        /// </para>
        /// </remarks>
        public double At(double symbols)
        {
            switch (Type)
            {
                case PulseFilterType.None:
                    return symbols == 0.0 ? 1.0 : 0.0;

                case PulseFilterType.RootRaisedCosine:
                    return RootRaisedCosineAt(symbols, Alpha);

                case PulseFilterType.RaisedCosine:
                    return RaisedCosineAt(symbols, Alpha);

                case PulseFilterType.Gaussian:
                    return GaussianAt(symbols, BandwidthTime);

                case PulseFilterType.Edge:
                    return EdgePulse.At(symbols);

                case PulseFilterType.HalfSine:
                    // One half-period of a cosine across a symbol: unity at the centre, zero at the
                    // symbol's edges, nothing beyond them. MSK's shaping, and the reason it is a
                    // cosine rather than a sine is that this catalogue's filters are all written
                    // about their own centre.
                    return Math.Abs(symbols) <= 0.5
                        ? Math.Cos(Math.PI * symbols)
                        : 0.0;

                case PulseFilterType.Rectangular:
                    return Math.Abs(symbols) <= 0.5 ? 1.0 : 0.0;

                case PulseFilterType.LowPass:
                    // A brick wall of bandwidth ±Cutoff is a sinc of that width in time. At the
                    // default half a symbol rate this is exactly the Nyquist sinc, which is the
                    // raised cosine at zero roll-off — the same filter reached two ways, which is
                    // itself worth a test.
                    return Sinc(2.0 * Cutoff * symbols);

                case PulseFilterType.UserDefined:
                    return UserAt(symbols);

                default:
                    throw new InvalidOperationException(
                        "No impulse response is defined for " + Type + ".");
            }
        }

        /// <summary>
        /// The filter as a shaping function for one role: windowed, truncated and normalised.
        /// </summary>
        /// <param name="symbols">How far from the centre, in symbol periods.</param>
        /// <param name="symbolSpan">How many symbols either side the filter spans.</param>
        /// <param name="role">Which position the filter is in.</param>
        /// <returns>The value to shape with.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The span is below one symbol.</exception>
        /// <remarks>
        /// The continuous counterpart of <see cref="Taps"/>, for the reference waveform of step 10 —
        /// whose symbols sit at a fractional offset, so it needs the pulse evaluated between samples
        /// rather than a tap array. Both go through the same window and the same normalisation, so
        /// the two paths cannot drift apart.
        /// </remarks>
        public double Shape(double symbols, int symbolSpan, FilterRole role)
        {
            RequireSpan(symbolSpan);

            if (Type == PulseFilterType.None)
            {
                return symbols == 0.0 ? 1.0 : 0.0;
            }

            if (Math.Abs(symbols) > symbolSpan)
            {
                return 0.0;
            }

            return At(symbols) * Taper(symbols, symbolSpan) / Scale(role, symbolSpan);
        }

        /// <summary>
        /// The filter as taps, for the convolution of step 5.
        /// </summary>
        /// <param name="samplesPerSymbol">The internal processing rate.</param>
        /// <param name="symbolSpan">How many symbols either side the filter spans.</param>
        /// <param name="role">Which position the filter is in.</param>
        /// <returns>An odd number of taps, centre in the middle.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The rate or the span is too small.</exception>
        public double[] Taps(int samplesPerSymbol, int symbolSpan, FilterRole role)
        {
            if (samplesPerSymbol < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samplesPerSymbol),
                    samplesPerSymbol,
                    "A pulse needs at least two samples a symbol to have a shape.");
            }

            RequireSpan(symbolSpan);

            int half = samplesPerSymbol * symbolSpan;
            var taps = new double[(2 * half) + 1];

            if (Type == PulseFilterType.None)
            {
                // The identity: one tap of one. REQ-DEM-021 asks that None leave the input
                // untouched, and a single unit tap is what does that under either normalisation --
                // its sum is one and its peak is one, so there is nothing for Normalise to decide.
                taps[half] = 1.0;

                return taps;
            }

            for (int tap = 0; tap < taps.Length; tap++)
            {
                double t = (tap - half) / (double)samplesPerSymbol;

                taps[tap] = At(t) * Taper(t, symbolSpan);
            }

            Normalise(taps, role);

            return taps;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            switch (Type)
            {
                case PulseFilterType.RootRaisedCosine:
                case PulseFilterType.RaisedCosine:
                    return Type + ", alpha " + Alpha.ToString("0.###", CultureInfo.InvariantCulture);

                case PulseFilterType.Gaussian:
                    return "Gaussian, BT " +
                        BandwidthTime.ToString("0.###", CultureInfo.InvariantCulture);

                case PulseFilterType.LowPass:
                    return "LowPass, cutoff " +
                        Cutoff.ToString("0.###", CultureInfo.InvariantCulture) + " of the symbol rate";

                case PulseFilterType.UserDefined:
                    return "UserDefined, " + _userTaps.Count + " taps at " +
                        UserSamplesPerSymbol + " samples/symbol";

                default:
                    return Type.ToString();
            }
        }

        /// <summary>The unnormalised sum of the windowed taps, and the peak, in one place.</summary>
        /// <param name="role">Which normalisation is wanted.</param>
        /// <param name="symbolSpan">The span the filter is truncated to.</param>
        /// <returns>What to divide the analytic form by.</returns>
        /// <remarks>
        /// The continuous path's half of <see cref="Normalise"/>. Unit peak needs only the value at
        /// the centre, which is analytic; unit DC gain needs the integral, which is estimated at a
        /// fixed fine rate rather than at the caller's, so that the reference waveform does not
        /// change scale when the internal processing rate does.
        /// </remarks>
        private double Scale(FilterRole role, int symbolSpan)
        {
            if (role == FilterRole.Reference)
            {
                double peak = At(0.0);

                return Math.Abs(peak) < 1e-15 ? 1.0 : peak;
            }

            const int Fine = 64;

            double sum = 0.0;

            for (int tap = -Fine * symbolSpan; tap <= Fine * symbolSpan; tap++)
            {
                double t = tap / (double)Fine;

                sum += At(t) * Taper(t, symbolSpan);
            }

            double gain = sum / Fine;

            return Math.Abs(gain) < 1e-15 ? 1.0 : gain;
        }

        /// <summary>
        /// The one normalisation step <c>REQ-DEM-022a</c> asks for, at the one place it happens.
        /// </summary>
        /// <param name="taps">The windowed taps, in their analytic scale; scaled in place.</param>
        /// <param name="role">Which position the filter is in.</param>
        /// <remarks>
        /// <para>
        /// <strong>Measurement: unit DC gain, which for taps means they sum to one.</strong> The DC
        /// gain of <c>y[n] = Σ h[k] x[n−k]</c> is <c>Σh</c> and nothing else, so an unmodulated
        /// carrier comes out at the amplitude it went in at — at every span, exactly, which is what
        /// <c>REQ-DEM-023</c> asks to within 0.01 dB.
        /// </para>
        /// <para>
        /// <strong>Reference: unit peak.</strong> The reference waveform's value at a symbol instant
        /// has to be the constellation point itself, or the ideal and the measured are on different
        /// scales and the difference lands in EVM.
        /// </para>
        /// </remarks>
        private static void Normalise(double[] taps, FilterRole role)
        {
            double scale;

            if (role == FilterRole.Reference)
            {
                double peak = taps[taps.Length / 2];

                scale = Math.Abs(peak) < 1e-15 ? 1.0 : 1.0 / peak;
            }
            else
            {
                double sum = 0.0;

                foreach (double tap in taps)
                {
                    sum += tap;
                }

                scale = Math.Abs(sum) < 1e-15 ? 1.0 : 1.0 / sum;
            }

            for (int tap = 0; tap < taps.Length; tap++)
            {
                taps[tap] *= scale;
            }
        }

        /// <summary>
        /// The truncation window: unity across the middle, raised-cosine to zero at the ends.
        /// </summary>
        /// <param name="symbols">How far from the centre, in symbol periods.</param>
        /// <param name="symbolSpan">How many symbols either side the filter spans.</param>
        /// <returns>The taper, from 0 to 1.</returns>
        /// <remarks>
        /// A Tukey window of <see cref="TaperFraction"/>. Why that shape and that fraction is
        /// recorded on <see cref="TaperFraction"/>, with the measurements it was chosen from.
        /// </remarks>
        private static double Taper(double symbols, int symbolSpan)
        {
            double distance = Math.Abs(symbols);

            if (distance >= symbolSpan)
            {
                return 0.0;
            }

            double flat = symbolSpan * (1.0 - TaperFraction);

            if (distance <= flat)
            {
                return 1.0;
            }

            double into = (distance - flat) / (symbolSpan - flat);

            return 0.5 * (1.0 + Math.Cos(Math.PI * into));
        }

        private static void RequireSpan(int symbolSpan)
        {
            if (symbolSpan < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(symbolSpan),
                    symbolSpan,
                    "A pulse spans at least one symbol either side of centre.");
            }
        }

        private static double RequireAlpha(double alpha)
        {
            if (!(alpha >= 0.0) || alpha > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(alpha), alpha, "A roll-off runs from 0 to 1.");
            }

            return alpha;
        }

        /// <summary>
        /// How near the raised cosine's singularity the analytic limit is used instead of the
        /// general form.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A millionth, measured on the denominator <c>1 − (2αt)²</c> rather than on t, so the band
        /// means the same thing at every roll-off. In t it is about <c>1e-6/4α</c> — under a
        /// millionth of a symbol at any usable α.
        /// </para>
        /// <para>
        /// <strong>It is not an epsilon standing in for the limit; it is where the general form
        /// stops being able to compute it.</strong> Approaching the singularity, both the numerator
        /// and the denominator go to zero, and a ratio of two quantities each carrying the same
        /// absolute rounding error loses relative accuracy as they shrink. Measured: at a tenth of a
        /// micro-symbol away the general form is already out by about 6e-9, which is enough to fail
        /// <c>REQ-DEM-022a</c>'s own continuity criterion of 1e-9. Inside the band the limit is
        /// returned, which is exact at the singularity and differs from the true curve by less than
        /// the general form's own error at the band's edge.
        /// </para>
        /// <para>
        /// <strong>Both of the raised-cosine pair use it, and the first version of this comment said
        /// otherwise.</strong> The root appeared to hold continuity at a band a thousand times
        /// narrower — until the test that would have caught it stopped at the raised cosine's
        /// assertion first and never reached the root's. With the raised cosine fixed, the root
        /// failed the same way and by more: 1.5e-7 at α = 0.35. Two filters with the same shape of
        /// singularity have the same problem, and one of them passing was a test running out of
        /// assertions rather than a fact about the arithmetic.
        /// </para>
        /// </remarks>
        private const double SingularBand = 1e-6;

        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-12)
            {
                return 1.0;
            }

            return Math.Sin(Math.PI * x) / (Math.PI * x);
        }

        /// <summary>The raised cosine of <c>REQ-DEM-022</c>, at unit peak.</summary>
        internal static double RaisedCosineAt(double symbols, double alpha)
        {
            double sinc = Sinc(symbols);

            if (alpha < 1e-12)
            {
                return sinc;
            }

            double denominator = 1.0 - ((2.0 * alpha * symbols) * (2.0 * alpha * symbols));

            if (Math.Abs(denominator) < SingularBand)
            {
                // The removable singularity at t = ±T/2α. Both factors vanish together, and
                // l'Hôpital in t gives cos'(παt)/(1 − (2αt)²)' → (−πα)/(−4α) = π/4, so the limit is
                // (π/4)·sinc(1/2α). Written out rather than approached with an epsilon: this
                // function used to average two points either side, which REQ-DEM-022a names as the
                // thing not to do.
                return (Math.PI / 4.0) * Sinc(1.0 / (2.0 * alpha));
            }

            return sinc * Math.Cos(Math.PI * alpha * symbols) / denominator;
        }

        /// <summary>The root raised cosine of <c>REQ-DEM-022</c>, in its conventional form.</summary>
        internal static double RootRaisedCosineAt(double symbols, double alpha)
        {
            const double Tiny = 1e-12;

            if (Math.Abs(symbols) < Tiny)
            {
                // h(0) = 1 + α(4/π − 1), the limit of the general form.
                return 1.0 + (alpha * ((4.0 / Math.PI) - 1.0));
            }

            double vanishing = 1.0 - ((4.0 * alpha * symbols) * (4.0 * alpha * symbols));

            if (alpha > Tiny && Math.Abs(vanishing) < SingularBand)
            {
                // The removable singularity at t = ±T/4α, where 1 − (4αt)² vanishes. Detected on
                // that quantity rather than on t, for the reason given on SingularBand: it is the
                // thing whose smallness costs the general form its precision, and measuring the
                // band on it makes the band mean the same at every roll-off.
                double angle = Math.PI / (4.0 * alpha);

                return (alpha / Math.Sqrt(2.0)) *
                    (((1.0 + (2.0 / Math.PI)) * Math.Sin(angle)) +
                     ((1.0 - (2.0 / Math.PI)) * Math.Cos(angle)));
            }

            double numerator =
                Math.Sin(Math.PI * symbols * (1.0 - alpha)) +
                (4.0 * alpha * symbols * Math.Cos(Math.PI * symbols * (1.0 + alpha)));

            // πt(1 − (4αt)²), written in that form so it reads against the standard definition
            // rather than against an algebraically equivalent rearrangement of it.
            double denominator = Math.PI * symbols * vanishing;

            return numerator / denominator;
        }

        /// <summary>The Gaussian of <c>REQ-DEM-022</c>, at unit area.</summary>
        internal static double GaussianAt(double symbols, double bandwidthTime)
        {
            double sigma = Math.Sqrt(Math.Log(2.0)) / (2.0 * Math.PI * bandwidthTime);

            return Math.Exp(-(symbols * symbols) / (2.0 * sigma * sigma)) /
                (Math.Sqrt(2.0 * Math.PI) * sigma);
        }

        /// <summary>The user's taps, read at an arbitrary position.</summary>
        /// <remarks>
        /// <strong>Linearly, and the choice is worth stating.</strong> A tap list is a sampled
        /// function with nothing said about what lies between its samples, so any answer here is an
        /// assumption. Linear interpolation assumes the least — it invents no ringing and cannot
        /// overshoot the taps that were given — where a band-limited reconstruction would assume the
        /// user's filter was sampled above its own Nyquist rate, which nothing checks and a
        /// hand-written tap list often is not.
        /// </remarks>
        private double UserAt(double symbols)
        {
            int centre = _userTaps.Count / 2;
            double position = centre + (symbols * UserSamplesPerSymbol);

            if (position <= -1.0 || position >= _userTaps.Count)
            {
                return 0.0;
            }

            int lower = (int)Math.Floor(position);
            double fraction = position - lower;

            double before = lower < 0 || lower >= _userTaps.Count ? 0.0 : _userTaps[lower];
            double after = lower + 1 < 0 || lower + 1 >= _userTaps.Count
                ? 0.0
                : _userTaps[lower + 1];

            return before + ((after - before) * fraction);
        }
    }
}
