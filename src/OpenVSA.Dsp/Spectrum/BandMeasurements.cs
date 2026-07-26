using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>Total and density power over a frequency band.</summary>
    public readonly struct BandPower
    {
        /// <summary>Creates a band-power result.</summary>
        /// <param name="totalDbm">Total power in the band, in dBm.</param>
        /// <param name="densityDbmPerHz">Power spectral density, in dBm/Hz.</param>
        /// <param name="bandwidthHz">Width of the band actually integrated, in hertz.</param>
        /// <param name="binCount">Bins integrated.</param>
        public BandPower(double totalDbm, double densityDbmPerHz, double bandwidthHz, int binCount)
        {
            TotalDbm = totalDbm;
            DensityDbmPerHz = densityDbmPerHz;
            BandwidthHz = bandwidthHz;
            BinCount = binCount;
        }

        /// <summary>Total power in the band, in dBm.</summary>
        public double TotalDbm { get; }

        /// <summary>Power spectral density, in dBm/Hz.</summary>
        public double DensityDbmPerHz { get; }

        /// <summary>Width of the band actually integrated, in hertz.</summary>
        public double BandwidthHz { get; }

        /// <summary>Bins integrated.</summary>
        public int BinCount { get; }

        /// <inheritdoc />
        public override string ToString() =>
            TotalDbm.ToString("F2", CultureInfo.CurrentCulture) + " dBm over " +
            BandwidthHz.ToString("G4", CultureInfo.CurrentCulture) + " Hz (" +
            DensityDbmPerHz.ToString("F2", CultureInfo.CurrentCulture) + " dBm/Hz)";
    }

    /// <summary>One adjacent channel's power, relative to the carrier.</summary>
    public readonly struct AdjacentChannel
    {
        /// <summary>Creates an adjacent-channel result.</summary>
        /// <param name="offsetHz">Channel centre offset from the carrier, in hertz.</param>
        /// <param name="power">Absolute power in the channel.</param>
        /// <param name="ratioDb">Power relative to the carrier channel, in dB — negative below it.</param>
        public AdjacentChannel(double offsetHz, BandPower power, double ratioDb)
        {
            OffsetHz = offsetHz;
            Power = power;
            RatioDb = ratioDb;
        }

        /// <summary>Channel centre offset from the carrier, in hertz.</summary>
        public double OffsetHz { get; }

        /// <summary>Absolute power in the channel.</summary>
        public BandPower Power { get; }

        /// <summary>Power relative to the carrier channel, in dB.</summary>
        public double RatioDb { get; }
    }

    /// <summary>
    /// Band power, occupied bandwidth and adjacent-channel power (<c>REQ-MKR-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Summed bin powers are divided by the window's noise bandwidth.</strong> A window
    /// spreads a tone over several bins, so adding those bins counts it more than once — by
    /// exactly the equivalent noise bandwidth, which is what that quantity means. Dividing by ENBW
    /// recovers the tone's true power, and it does so identically: for a bin-centred tone,
    /// <c>Σ|X_k|² = |X_peak|² · ENBW</c> by Parseval, so the correction is exact rather than
    /// approximate.
    /// </para>
    /// <para>
    /// That single correction is why band power over a band containing one CW tone equals the
    /// tone's power, which is the requirement's first criterion.
    /// </para>
    /// </remarks>
    public static class BandMeasurements
    {
        /// <summary>
        /// Integrates power across a band.
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="startHz">Lower edge of the band, in hertz.</param>
        /// <param name="stopHz">Upper edge, in hertz.</param>
        /// <returns>Total power and density; zero bins give <see cref="AmplitudeScale.FloorDbm"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <exception cref="ArgumentException">The band is inverted.</exception>
        public static BandPower Power(SpectrumFrame frame, double startHz, double stopHz)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (stopHz < startHz)
            {
                throw new ArgumentException(
                    "The band's upper edge is below its lower edge.", nameof(stopHz));
            }

            int first = IndexAtOrAbove(frame, startHz);
            int last = IndexAtOrBelow(frame, stopHz);

            if (first > last)
            {
                return new BandPower(
                    AmplitudeScale.FloorDbm, AmplitudeScale.FloorDbm, 0.0, 0);
            }

            double sum = 0.0;
            ReadOnlySpan<float> complex = frame.Complex;

            for (int i = first; i <= last; i++)
            {
                double re = complex[i * 2];
                double im = complex[i * 2 + 1];

                if (double.IsNaN(re) || double.IsNaN(im))
                {
                    continue;
                }

                sum += re * re + im * im;
            }

            // The ENBW correction, applied once. Without it a flat-top-windowed tone reads nearly
            // 6 dB high, which looks like a plausible calibration error rather than a method error.
            double enbw = frame.EquivalentNoiseBandwidthBins > 0.0
                ? frame.EquivalentNoiseBandwidthBins
                : 1.0;

            double corrected = sum / enbw;

            int bins = last - first + 1;
            double bandwidth = bins * frame.BinWidthHz;

            double totalDbm = frame.Scale.VoltsSquaredToDbm(corrected);
            double densityDbm = bandwidth > 0.0
                ? frame.Scale.VoltsSquaredToDbm(corrected / bandwidth)
                : AmplitudeScale.FloorDbm;

            return new BandPower(totalDbm, densityDbm, bandwidth, bins);
        }

        /// <summary>
        /// The bandwidth containing a given fraction of the total power (<c>REQ-MKR-003</c>).
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="fraction">Fraction of total power to enclose, such as 0.99.</param>
        /// <returns>Occupied bandwidth in hertz, and the band edges.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="fraction"/> is not between 0 and 1.</exception>
        /// <remarks>
        /// <para>
        /// Measured by excluding half the remainder from each edge, which is the standard
        /// definition and the one the 1.167·R_sym figure belongs to.
        /// </para>
        /// <para>
        /// <strong>This is not <c>(1+α)·R_sym</c>.</strong> That is the absolute, null-to-null
        /// bandwidth, and using it as the 99 % figure is wrong by 11–16 % across the usual roll-off
        /// range — a common and quietly damaging error in this kind of code, which is why the
        /// requirement calls it out by name.
        /// </para>
        /// </remarks>
        public static OccupiedBandwidth Occupied(SpectrumFrame frame, double fraction = 0.99)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (!(fraction > 0.0) || fraction >= 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fraction), fraction, "The fraction must be above 0 and below 1.");
            }

            int points = frame.PointCount;
            ReadOnlySpan<float> complex = frame.Complex;

            var power = new double[points];
            double total = 0.0;

            for (int i = 0; i < points; i++)
            {
                double re = complex[i * 2];
                double im = complex[i * 2 + 1];
                power[i] = double.IsNaN(re) || double.IsNaN(im) ? 0.0 : re * re + im * im;
                total += power[i];
            }

            if (!(total > 0.0))
            {
                return new OccupiedBandwidth(0.0, frame.StartFrequencyHz, frame.StartFrequencyHz);
            }

            double excludeEachSide = total * (1.0 - fraction) / 2.0;

            int low = 0;
            double running = 0.0;

            while (low < points - 1 && running + power[low] <= excludeEachSide)
            {
                running += power[low];
                low++;
            }

            int high = points - 1;
            running = 0.0;

            while (high > low && running + power[high] <= excludeEachSide)
            {
                running += power[high];
                high--;
            }

            double bandwidth = (high - low) * frame.BinWidthHz;

            return new OccupiedBandwidth(
                bandwidth, frame.FrequencyAt(low), frame.FrequencyAt(high));
        }

        /// <summary>
        /// The bandwidth between the points a stated number of decibels below the peak
        /// (<c>REQ-CHM-002</c>).
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="decibelsDown">How far below the peak the edges are, such as 3.</param>
        /// <returns>The width and the two edges.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="decibelsDown"/> is not positive.</exception>
        /// <remarks>
        /// <para>
        /// A different criterion from <see cref="Occupied"/>, not a restatement of it: this
        /// measures where the shape falls to a level, while the percentage criterion measures where
        /// the energy is. For the same signal they give different answers, and the requirement asks
        /// for both precisely so that one cannot quietly be aliased onto the other.
        /// </para>
        /// <para>
        /// The crossings are interpolated between bins, so the answer is not quantised to the bin
        /// spacing — which matters because the criterion is stated to within one bin and a
        /// nearest-bin answer would use the whole allowance before the measurement started.
        /// </para>
        /// </remarks>
        public static OccupiedBandwidth XDecibelsDown(SpectrumFrame frame, double decibelsDown = 3.0)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (!(decibelsDown > 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decibelsDown), decibelsDown, "The drop must be positive.");
            }

            int peak = frame.IndexOfPeak();

            if (peak < 0)
            {
                return new OccupiedBandwidth(0.0, frame.StartFrequencyHz, frame.StartFrequencyHz);
            }

            ReadOnlySpan<float> levels = frame.LevelsDbm;
            double threshold = levels[peak] - decibelsDown;

            double lower = Crossing(frame, levels, peak, threshold, downwards: true);
            double upper = Crossing(frame, levels, peak, threshold, downwards: false);

            return new OccupiedBandwidth(upper - lower, lower, upper);
        }

        /// <summary>Walks out from the peak to where the trace crosses a level, interpolating.</summary>
        private static double Crossing(
            SpectrumFrame frame,
            ReadOnlySpan<float> levels,
            int peak,
            double threshold,
            bool downwards)
        {
            int step = downwards ? -1 : 1;
            int i = peak;

            while (true)
            {
                int next = i + step;

                if (next < 0 || next >= levels.Length)
                {
                    // The shape never falls that far inside the span: the edge of the trace is the
                    // best answer available, and it is the honest one.
                    return frame.FrequencyAt(i);
                }

                if (levels[next] <= threshold)
                {
                    double above = levels[i];
                    double below = levels[next];
                    double fraction = Math.Abs(above - below) < 1e-12
                        ? 0.0
                        : (above - threshold) / (above - below);

                    return frame.FrequencyAt(i) + step * fraction * frame.BinWidthHz;
                }

                i = next;
            }
        }

        /// <summary>
        /// Adjacent-channel power relative to a carrier channel (<c>REQ-MKR-003</c>).
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="carrierCenterHz">Centre of the carrier channel, in hertz.</param>
        /// <param name="channelBandwidthHz">Integration bandwidth of every channel, in hertz.</param>
        /// <param name="offsetsHz">Channel centre offsets from the carrier; each is measured on both sides.</param>
        /// <returns>The carrier's power and one entry per offset per side.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelBandwidthHz"/> is not positive.</exception>
        public static AdjacentChannelPower Adjacent(
            SpectrumFrame frame,
            double carrierCenterHz,
            double channelBandwidthHz,
            IReadOnlyList<double> offsetsHz)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (offsetsHz == null)
            {
                throw new ArgumentNullException(nameof(offsetsHz));
            }

            if (!(channelBandwidthHz > 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(channelBandwidthHz), channelBandwidthHz,
                    "A channel needs a positive integration bandwidth.");
            }

            double half = channelBandwidthHz / 2.0;

            BandPower carrier = Power(
                frame, carrierCenterHz - half, carrierCenterHz + half);

            var channels = new List<AdjacentChannel>();

            foreach (double offset in offsetsHz)
            {
                foreach (double signed in new[] { -Math.Abs(offset), Math.Abs(offset) })
                {
                    double centre = carrierCenterHz + signed;
                    BandPower power = Power(frame, centre - half, centre + half);

                    channels.Add(new AdjacentChannel(
                        signed, power, power.TotalDbm - carrier.TotalDbm));
                }
            }

            return new AdjacentChannelPower(carrier, channels);
        }

        private static int IndexAtOrAbove(SpectrumFrame frame, double hertz)
        {
            double offset = (hertz - frame.StartFrequencyHz) / frame.BinWidthHz;
            int index = (int)Math.Ceiling(offset - 1e-9);
            return index < 0 ? 0 : index;
        }

        private static int IndexAtOrBelow(SpectrumFrame frame, double hertz)
        {
            double offset = (hertz - frame.StartFrequencyHz) / frame.BinWidthHz;
            int index = (int)Math.Floor(offset + 1e-9);
            return index > frame.PointCount - 1 ? frame.PointCount - 1 : index;
        }
    }

    /// <summary>The result of an occupied-bandwidth measurement.</summary>
    public readonly struct OccupiedBandwidth
    {
        /// <summary>Creates a result.</summary>
        /// <param name="bandwidthHz">Occupied bandwidth, in hertz.</param>
        /// <param name="lowerEdgeHz">Lower band edge, in hertz.</param>
        /// <param name="upperEdgeHz">Upper band edge, in hertz.</param>
        public OccupiedBandwidth(double bandwidthHz, double lowerEdgeHz, double upperEdgeHz)
        {
            BandwidthHz = bandwidthHz;
            LowerEdgeHz = lowerEdgeHz;
            UpperEdgeHz = upperEdgeHz;
        }

        /// <summary>Occupied bandwidth, in hertz.</summary>
        public double BandwidthHz { get; }

        /// <summary>Lower band edge, in hertz.</summary>
        public double LowerEdgeHz { get; }

        /// <summary>Upper band edge, in hertz.</summary>
        public double UpperEdgeHz { get; }
    }

    /// <summary>The result of an adjacent-channel-power measurement.</summary>
    public sealed class AdjacentChannelPower
    {
        internal AdjacentChannelPower(BandPower carrier, IReadOnlyList<AdjacentChannel> channels)
        {
            Carrier = carrier;
            Channels = channels;
        }

        /// <summary>Power in the carrier channel.</summary>
        public BandPower Carrier { get; }

        /// <summary>The adjacent channels, one per offset per side.</summary>
        public IReadOnlyList<AdjacentChannel> Channels { get; }
    }
}
