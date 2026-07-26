using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Measurement.Channels
{
    /// <summary>Which side of the carrier an offset channel sits on.</summary>
    public enum ChannelSide
    {
        /// <summary>The carrier channel itself.</summary>
        Carrier = 0,

        /// <summary>Below the carrier.</summary>
        Lower,

        /// <summary>Above the carrier.</summary>
        Upper,
    }

    /// <summary>One channel's measured power (<c>REQ-CHM-001</c>).</summary>
    public sealed class ChannelPower
    {
        internal ChannelPower(
            ChannelDefinition definition,
            ChannelSide side,
            double centreHz,
            BandPower power,
            double relativeDb)
        {
            Definition = definition;
            Side = side;
            CentreHz = centreHz;
            Power = power;
            RelativeDb = relativeDb;
        }

        /// <summary>The channel that was measured.</summary>
        public ChannelDefinition Definition { get; }

        /// <summary>Which side of the carrier it sits on.</summary>
        public ChannelSide Side { get; }

        /// <summary>Absolute centre frequency of this channel, in hertz.</summary>
        public double CentreHz { get; }

        /// <summary>Absolute power, in dBm, with the channel's filter applied.</summary>
        public BandPower Power { get; }

        /// <summary>
        /// Power relative to the carrier channel, in dB — negative below it.
        /// </summary>
        /// <remarks>Zero for the carrier itself, by construction rather than by coincidence.</remarks>
        public double RelativeDb { get; }

        /// <summary>Absolute power in dBm, for brevity at a call site.</summary>
        public double AbsoluteDbm => Power.TotalDbm;

        /// <inheritdoc />
        public override string ToString() =>
            Definition.Name + " " + Side + ": " +
            AbsoluteDbm.ToString("F2", CultureInfo.CurrentCulture) + " dBm, " +
            RelativeDb.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + " dBc";
    }

    /// <summary>The result of an adjacent-channel-power measurement.</summary>
    public sealed class AcpResult
    {
        internal AcpResult(ChannelPower carrier, IReadOnlyList<ChannelPower> offsets)
        {
            Carrier = carrier;
            Offsets = offsets;
        }

        /// <summary>The carrier channel.</summary>
        public ChannelPower Carrier { get; }

        /// <summary>
        /// The offset channels, two per definition — lower then upper, in definition order.
        /// </summary>
        public IReadOnlyList<ChannelPower> Offsets { get; }

        /// <summary>
        /// One offset channel by name and side.
        /// </summary>
        /// <param name="name">The channel definition's name.</param>
        /// <param name="side">Which side.</param>
        /// <returns>The result, or <c>null</c> if no such channel was measured.</returns>
        public ChannelPower Find(string name, ChannelSide side)
        {
            foreach (ChannelPower channel in Offsets)
            {
                if (channel.Side == side &&
                    string.Equals(channel.Definition.Name, name, StringComparison.Ordinal))
                {
                    return channel;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public override string ToString() =>
            "Carrier " + Carrier.AbsoluteDbm.ToString("F2", CultureInfo.CurrentCulture) +
            " dBm, " + Offsets.Count + " offset channels";
    }

    /// <summary>
    /// Adjacent channel power: a carrier channel and offset channels either side of it, each with
    /// its own bandwidth and filter (<c>REQ-CHM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every offset is measured on both sides and the two are kept apart.</strong> Not a
    /// convenience: adjacent-channel power is asymmetric whenever the impairment causing it is, and
    /// a measurement that averaged or conflated the sides would hide exactly the case worth
    /// measuring. The tests inject asymmetrically for the same reason — a swapped upper and lower
    /// has to fail rather than average out.
    /// </para>
    /// <para>
    /// <strong>Absolute and relative, both reported.</strong> The ratio is what a standard states a
    /// limit against; the absolute power is what tells you whether the ratio is meaningful, because
    /// a −60 dBc adjacent channel that is at the analyser's own noise floor is a measurement of the
    /// analyser. Reporting only the ratio is how that goes unnoticed.
    /// </para>
    /// <para>
    /// <strong>The integration is <see cref="BandMeasurements.WeightedPower"/>'s.</strong> The
    /// window's noise-bandwidth correction, the bin selection at the band edges and the amplitude
    /// chain are all stated once, in the DSP layer, so an ACP reading and a band-power marker over
    /// the same span cannot disagree — which <c>REQ-CHM-001</c> requires to 0.1 dB and a second
    /// integration loop here would eventually break.
    /// </para>
    /// </remarks>
    public sealed class AcpMeasurement
    {
        private readonly List<ChannelDefinition> _offsets = new List<ChannelDefinition>();

        /// <summary>Creates a measurement with a carrier channel and no offsets.</summary>
        /// <param name="carrier">The carrier channel definition.</param>
        /// <exception cref="ArgumentNullException"><paramref name="carrier"/> is null.</exception>
        public AcpMeasurement(ChannelDefinition carrier)
        {
            if (carrier == null)
            {
                throw new ArgumentNullException(nameof(carrier));
            }

            Carrier = carrier;
        }

        /// <summary>The carrier channel.</summary>
        public ChannelDefinition Carrier { get; }

        /// <summary>The offset channels, each measured on both sides.</summary>
        public IReadOnlyList<ChannelDefinition> Offsets =>
            new ReadOnlyCollection<ChannelDefinition>(_offsets);

        /// <summary>Adds an offset channel.</summary>
        /// <param name="offset">The definition; its offset must not be zero.</param>
        /// <returns>This measurement, so offsets can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="offset"/> is null.</exception>
        /// <exception cref="ArgumentException">The offset is zero.</exception>
        public AcpMeasurement Add(ChannelDefinition offset)
        {
            if (offset == null)
            {
                throw new ArgumentNullException(nameof(offset));
            }

            if (offset.OffsetHz == 0.0)
            {
                throw new ArgumentException(
                    "An offset channel sits away from the carrier; '" + offset.Name +
                    "' is at zero offset, which is the carrier channel.",
                    nameof(offset));
            }

            _offsets.Add(offset);
            return this;
        }

        /// <summary>
        /// Measures a spectrum.
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="carrierCentreHz">Absolute centre frequency of the carrier channel, in hertz.</param>
        /// <returns>The carrier's power and one result per offset per side.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="carrierCentreHz"/> is not finite.</exception>
        public AcpResult Measure(SpectrumFrame frame, double carrierCentreHz)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (double.IsNaN(carrierCentreHz) || double.IsInfinity(carrierCentreHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(carrierCentreHz), carrierCentreHz, "A centre frequency must be finite.");
            }

            BandPower carrierPower = Integrate(frame, Carrier, carrierCentreHz);

            var carrier = new ChannelPower(
                Carrier, ChannelSide.Carrier, carrierCentreHz, carrierPower, 0.0);

            var results = new List<ChannelPower>(_offsets.Count * 2);

            foreach (ChannelDefinition offset in _offsets)
            {
                // Lower first, then upper, so the order on screen matches the order on the axis.
                results.Add(Measure(frame, offset, ChannelSide.Lower, carrierCentreHz, carrierPower));
                results.Add(Measure(frame, offset, ChannelSide.Upper, carrierCentreHz, carrierPower));
            }

            return new AcpResult(carrier, new ReadOnlyCollection<ChannelPower>(results));
        }

        private static ChannelPower Measure(
            SpectrumFrame frame,
            ChannelDefinition definition,
            ChannelSide side,
            double carrierCentreHz,
            BandPower carrierPower)
        {
            double centre = side == ChannelSide.Lower
                ? carrierCentreHz - definition.OffsetHz
                : carrierCentreHz + definition.OffsetHz;

            BandPower power = Integrate(frame, definition, centre);

            return new ChannelPower(
                definition, side, centre, power, power.TotalDbm - carrierPower.TotalDbm);
        }

        private static BandPower Integrate(
            SpectrumFrame frame, ChannelDefinition definition, double centreHz)
        {
            double half = definition.IntegrationBandwidthHz / 2.0;

            if (definition.Shape == ChannelFilterShape.Rectangular)
            {
                // No weight at all rather than a weight of one everywhere: identical arithmetic,
                // and it is the same call a band-power marker makes, which is what keeps the two
                // agreeing.
                return BandMeasurements.Power(frame, centreHz - half, centreHz + half);
            }

            return BandMeasurements.WeightedPower(
                frame,
                centreHz - half,
                centreHz + half,
                f => definition.PowerResponseAt(f - centreHz));
        }
    }
}
