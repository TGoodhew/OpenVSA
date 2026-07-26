using System;
using System.Globalization;

namespace OpenVSA.Measurement.Channels
{
    /// <summary>
    /// The filter a channel's power is integrated through (<c>REQ-CHM-001</c>).
    /// </summary>
    public enum ChannelFilterShape
    {
        /// <summary>
        /// Every bin inside the band counted equally, nothing outside it.
        /// </summary>
        /// <remarks>
        /// What "integration bandwidth" means on its own, and what a band-power marker measures.
        /// </remarks>
        Rectangular = 0,

        /// <summary>
        /// Root-raised-cosine, defined by a symbol rate and a roll-off.
        /// </summary>
        /// <remarks>
        /// The measurement filter every digitally modulated standard specifies, because it is the
        /// receiver's own matched filter: measuring through anything else answers a question no
        /// receiver asks.
        /// </remarks>
        RootRaisedCosine,
    }

    /// <summary>
    /// One channel of an adjacent-channel-power measurement: where it sits, how wide it is, and
    /// what it is measured through (<c>REQ-CHM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Per channel, not per measurement.</strong> The requirement asks for configurable
    /// carrier <em>and offset</em> channel definitions, and they genuinely differ: a standard that
    /// measures its carrier through a matched filter often measures its adjacent channels through a
    /// wider or differently shaped one. A single bandwidth applied to everything would make the
    /// common case unmeasurable.
    /// </para>
    /// <para>
    /// <strong>Integration bandwidth and noise bandwidth are different numbers and both are
    /// exposed.</strong> The first is the span of spectrum the measurement looks at; the second is
    /// what a flat noise input is multiplied by. For a rectangular channel they are equal, which is
    /// exactly why the distinction is so easy to lose — and for a root-raised-cosine channel they
    /// differ by <c>1 + α</c>, which is the whole reason anyone changes the shape.
    /// </para>
    /// </remarks>
    public sealed class ChannelDefinition
    {
        private ChannelDefinition(
            string name,
            double offsetHz,
            ChannelFilterShape shape,
            double bandwidthHz,
            double symbolRateHz,
            double rollOff)
        {
            Name = name;
            OffsetHz = offsetHz;
            Shape = shape;
            SymbolRateHz = symbolRateHz;
            RollOff = rollOff;
            IntegrationBandwidthHz = bandwidthHz;
        }

        /// <summary>User-facing name, such as "Carrier" or "±5 MHz".</summary>
        public string Name { get; }

        /// <summary>
        /// Centre offset from the carrier, in hertz; zero for the carrier itself.
        /// </summary>
        /// <remarks>
        /// Held unsigned. Each offset is measured on both sides, and which side a result came from
        /// is a property of the result rather than of the definition — a definition that carried a
        /// sign would need two of itself for every offset, and the pair could then disagree.
        /// </remarks>
        public double OffsetHz { get; }

        /// <summary>The filter this channel is integrated through.</summary>
        public ChannelFilterShape Shape { get; }

        /// <summary>
        /// The span of spectrum integrated, in hertz.
        /// </summary>
        /// <remarks>
        /// For a root-raised-cosine channel this is <c>(1 + α) · symbol rate</c>, the width outside
        /// which the filter is exactly zero — so no energy is discarded that the filter would have
        /// passed.
        /// </remarks>
        public double IntegrationBandwidthHz { get; }

        /// <summary>Symbol rate, in hertz; zero for a rectangular channel.</summary>
        public double SymbolRateHz { get; }

        /// <summary>Roll-off factor <c>α</c>, from 0 to 1; zero for a rectangular channel.</summary>
        public double RollOff { get; }

        /// <summary>
        /// The filter's equivalent noise bandwidth, in hertz.
        /// </summary>
        /// <remarks>
        /// <c>∫|H(f)|² df</c>. For a rectangular channel it is the integration bandwidth. For a
        /// root-raised-cosine one it is <strong>exactly the symbol rate, whatever the roll-off</strong>
        /// — the defining property of the raised-cosine family, since <c>|H_rrc|²</c> is the
        /// raised-cosine response and that integrates to the symbol rate by the symmetry of its
        /// skirts. It is the figure that predicts what switching to this shape does to a
        /// noise-like signal, which is what <c>REQ-CHM-001</c>'s last criterion asks for.
        /// </remarks>
        public double NoiseBandwidthHz =>
            Shape == ChannelFilterShape.RootRaisedCosine ? SymbolRateHz : IntegrationBandwidthHz;

        /// <summary>
        /// A channel integrated with no shaping.
        /// </summary>
        /// <param name="name">User-facing name.</param>
        /// <param name="offsetHz">Centre offset from the carrier; sign is ignored.</param>
        /// <param name="bandwidthHz">Integration bandwidth, in hertz; must be positive.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is missing.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public static ChannelDefinition Rectangular(
            string name, double offsetHz, double bandwidthHz)
        {
            RequireName(name);
            RequireFinite(offsetHz, nameof(offsetHz));
            RequirePositive(bandwidthHz, nameof(bandwidthHz));

            return new ChannelDefinition(
                name, Math.Abs(offsetHz), ChannelFilterShape.Rectangular, bandwidthHz, 0.0, 0.0);
        }

        /// <summary>
        /// A channel integrated through a root-raised-cosine filter.
        /// </summary>
        /// <param name="name">User-facing name.</param>
        /// <param name="offsetHz">Centre offset from the carrier; sign is ignored.</param>
        /// <param name="symbolRateHz">Symbol rate, in hertz; must be positive.</param>
        /// <param name="rollOff">Roll-off <c>α</c>, from 0 to 1.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is missing.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        /// <remarks>
        /// Defined by symbol rate and roll-off rather than by a bandwidth, because that is how
        /// every standard states it and because the bandwidth follows from the pair. Asking for a
        /// bandwidth as well would let the two disagree.
        /// </remarks>
        public static ChannelDefinition RootRaisedCosine(
            string name, double offsetHz, double symbolRateHz, double rollOff)
        {
            RequireName(name);
            RequireFinite(offsetHz, nameof(offsetHz));
            RequirePositive(symbolRateHz, nameof(symbolRateHz));

            if (!(rollOff >= 0.0) || rollOff > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rollOff), rollOff, "A roll-off factor runs from 0 to 1.");
            }

            return new ChannelDefinition(
                name,
                Math.Abs(offsetHz),
                ChannelFilterShape.RootRaisedCosine,
                (1.0 + rollOff) * symbolRateHz,
                symbolRateHz,
                rollOff);
        }

        /// <summary>
        /// The filter's power response <c>|H(f)|²</c> at an offset from the channel's centre.
        /// </summary>
        /// <param name="offsetFromCentreHz">Distance from the channel centre, in hertz.</param>
        /// <returns>A weight from 0 to 1.</returns>
        /// <remarks>
        /// In power, because that is what multiplies a bin. The root-raised-cosine amplitude
        /// response is flat to <c>(1−α)/2 · Rs</c>, follows a cosine skirt to <c>(1+α)/2 · Rs</c>
        /// and is zero beyond; squaring it gives the raised-cosine response, which is why the
        /// noise bandwidth comes out at the symbol rate exactly.
        /// </remarks>
        public double PowerResponseAt(double offsetFromCentreHz)
        {
            double f = Math.Abs(offsetFromCentreHz);

            if (Shape == ChannelFilterShape.Rectangular)
            {
                return f <= IntegrationBandwidthHz / 2.0 ? 1.0 : 0.0;
            }

            double flat = (1.0 - RollOff) * SymbolRateHz / 2.0;
            double edge = (1.0 + RollOff) * SymbolRateHz / 2.0;

            if (f <= flat)
            {
                return 1.0;
            }

            if (f >= edge)
            {
                return 0.0;
            }

            // Amplitude cos(...), so power cos²(...).
            double amplitude = Math.Cos(
                Math.PI / (2.0 * RollOff * SymbolRateHz) * (f - flat));

            return amplitude * amplitude;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Name + ": " + (OffsetHz == 0.0
                ? "carrier"
                : "±" + (OffsetHz / 1e6).ToString("0.###", CultureInfo.CurrentCulture) + " MHz") +
            ", " + (Shape == ChannelFilterShape.RootRaisedCosine
                ? "RRC " + (SymbolRateHz / 1e6).ToString("0.###", CultureInfo.CurrentCulture) +
                  " MHz α=" + RollOff.ToString("0.##", CultureInfo.CurrentCulture)
                : (IntegrationBandwidthHz / 1e6).ToString("0.###", CultureInfo.CurrentCulture) +
                  " MHz rectangular");

        private static void RequireName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A channel needs a name.", nameof(name));
            }
        }

        private static void RequireFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite.");
            }
        }

        private static void RequirePositive(double value, string name)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    name, value, name + " must be positive and finite.");
            }
        }
    }
}
