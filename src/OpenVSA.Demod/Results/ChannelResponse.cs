using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Demod.Results
{
    /// <summary>
    /// The channel the equaliser found, as a frequency response (<c>REQ-DEM-053</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The channel is the inverse of the equaliser</strong>, because that is what an
    /// equaliser is: the filter that undoes what the channel did. So a measurement of the one is a
    /// measurement of the other, and this is the equaliser's response inverted.
    /// </para>
    /// <para>
    /// <strong>Regularised, and it has to be.</strong> A pointwise <c>1/W</c> diverges wherever the
    /// equaliser's response has a null — and it will have nulls, at the band edges where there is no
    /// signal to constrain it — producing spikes tens of decibels tall that look exactly like real
    /// channel features and are not. The inversion used is
    /// </para>
    /// <code>
    ///     C = W* / (|W|^2 + epsilon)
    /// </code>
    /// <para>
    /// which is the same expression away from the nulls and bounded at them. <c>epsilon</c> is set
    /// from the measurement's own noise floor rather than chosen: the largest <c>|W|^2</c> in the
    /// response, divided by the signal-to-noise ratio the measurement reported. Below the noise the
    /// channel is not measurable, and the regularisation is what makes the trace say so — a gentle
    /// roll-off instead of a spike — rather than inventing a number there.
    /// </para>
    /// <para>
    /// <see cref="Regularisation"/> is the annotation the requirement asks to be carried on the
    /// trace, so that a reader knows the band edges are bounded rather than measured.
    /// </para>
    /// </remarks>
    public sealed class ChannelResponse
    {
        private readonly ReadOnlyCollection<double> _frequencies;
        private readonly ReadOnlyCollection<double> _magnitudeDb;
        private readonly ReadOnlyCollection<double> _phaseDegrees;
        private readonly ReadOnlyCollection<double> _groupDelaySeconds;

        internal ChannelResponse(
            IList<double> frequenciesHz,
            IList<double> magnitudeDb,
            IList<double> phaseDegrees,
            IList<double> groupDelaySeconds,
            double epsilon,
            double signalToNoiseDb,
            double trustedHalfWidthHz)
        {
            _frequencies = new ReadOnlyCollection<double>(frequenciesHz);
            _magnitudeDb = new ReadOnlyCollection<double>(magnitudeDb);
            _phaseDegrees = new ReadOnlyCollection<double>(phaseDegrees);
            _groupDelaySeconds = new ReadOnlyCollection<double>(groupDelaySeconds);

            Epsilon = epsilon;
            SignalToNoiseDb = signalToNoiseDb;
            TrustedHalfWidthHz = trustedHalfWidthHz;
        }

        /// <summary>The frequency of each point, in hertz, relative to the carrier.</summary>
        /// <remarks>Ordered from the most negative to the most positive, so a plot reads left to
        /// right without the caller sorting it.</remarks>
        public IReadOnlyList<double> FrequenciesHz => _frequencies;

        /// <summary>The channel's magnitude at each point, in decibels.</summary>
        public IReadOnlyList<double> MagnitudeDb => _magnitudeDb;

        /// <summary>The channel's phase at each point, in degrees, unwrapped.</summary>
        public IReadOnlyList<double> PhaseDegrees => _phaseDegrees;

        /// <summary>The group delay at each point, in seconds.</summary>
        /// <remarks>
        /// The negative derivative of the unwrapped phase with respect to angular frequency, by
        /// central difference. Optional in <c>REQ-DEM-053</c> and included because group-delay
        /// distortion is one of the three impairment classes <c>REQ-DEM-050</c> names, and a
        /// response that shows magnitude and phase but not delay makes the reader do the
        /// differentiation.
        /// </remarks>
        public IReadOnlyList<double> GroupDelaySeconds => _groupDelaySeconds;

        /// <summary>The regularisation term added to the squared magnitude before inverting.</summary>
        public double Epsilon { get; }

        /// <summary>The signal-to-noise ratio <see cref="Epsilon"/> was derived from, in decibels.</summary>
        public double SignalToNoiseDb { get; }

        /// <summary>
        /// How far either side of the carrier the response is a measurement rather than a
        /// regularisation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A channel cannot be measured where the signal carries no power, and a pulse-shaped signal
        /// carries almost none at the edge of the band it occupies — the spectrum of a raised cosine
        /// is zero there by construction. Beyond this half-width the trace is held up by its
        /// regularisation, and a display should show that it is: greyed, dashed, or simply not
        /// drawn.
        /// </para>
        /// <para>
        /// Taken as the flat part of the pulse's spectrum — the band where its own power is within
        /// one per cent of its peak, which for a raised cosine is its Nyquist flat region exactly.
        /// Beyond it the channel is recovered by dividing out a MODELLED pulse, and the model's own
        /// error is amplified by however far the pulse has fallen: measured on a two-ray channel,
        /// the recovered response is exact across the flat band, half a decibel out where the pulse
        /// is a decibel down, and meaningless at the band edge where the pulse is zero.
        /// </para>
        /// </remarks>
        public double TrustedHalfWidthHz { get; }

        /// <summary>How many points the response has.</summary>
        public int Count => _frequencies.Count;

        /// <summary>
        /// The annotation that belongs on the trace (<c>REQ-DEM-053</c>).
        /// </summary>
        /// <remarks>
        /// The requirement asks that the regularisation be "documented and annotated on the trace",
        /// and this is the annotation: what was done, why, and the number it was done with.
        /// </remarks>
        public string Regularisation =>
            "Inverted as (WP)*/(|WP|^2 + |W|^2 N/S) with the noise-to-signal ratio taken from the " +
            "measurement's own signal-to-noise ratio of " +
            SignalToNoiseDb.ToString("F1", CultureInfo.InvariantCulture) +
            " dB, which is e = " + Epsilon.ToString("G4", CultureInfo.InvariantCulture) +
            " at the response's peak. The composite pulse P is divided back out because the " +
            "equaliser inverts the channel and the pulse together. Where the signal carries no " +
            "power the inversion is bounded rather than divergent. Past " +
            (TrustedHalfWidthHz / 1e3).ToString("G4", CultureInfo.InvariantCulture) +
            " kHz either side the pulse is no longer flat, and dividing it back out amplifies the " +
            "error in the model of it -- the trace there is an extrapolation, not a measurement.";

        /// <summary>
        /// The magnitude at a frequency, by linear interpolation between points.
        /// </summary>
        /// <param name="hertz">The frequency, relative to the carrier.</param>
        /// <returns>The magnitude in decibels, or <c>NaN</c> outside the response.</returns>
        public double MagnitudeDbAt(double hertz) => At(_magnitudeDb, hertz);

        /// <summary>The phase at a frequency, by linear interpolation between points.</summary>
        /// <param name="hertz">The frequency, relative to the carrier.</param>
        /// <returns>The phase in degrees, or <c>NaN</c> outside the response.</returns>
        public double PhaseDegreesAt(double hertz) => At(_phaseDegrees, hertz);

        /// <inheritdoc />
        public override string ToString() =>
            Count.ToString(CultureInfo.InvariantCulture) + " points from " +
            (Count == 0 ? "nowhere" :
                (_frequencies[0] / 1e6).ToString("G4", CultureInfo.InvariantCulture) + " to " +
                (_frequencies[Count - 1] / 1e6).ToString("G4", CultureInfo.InvariantCulture) +
                " MHz");

        private double At(ReadOnlyCollection<double> values, double hertz)
        {
            if (Count < 2 || hertz < _frequencies[0] || hertz > _frequencies[Count - 1])
            {
                return double.NaN;
            }

            for (int point = 1; point < Count; point++)
            {
                if (_frequencies[point] < hertz)
                {
                    continue;
                }

                double span = _frequencies[point] - _frequencies[point - 1];

                if (span <= 0.0)
                {
                    return values[point];
                }

                double fraction = (hertz - _frequencies[point - 1]) / span;

                return values[point - 1] + (fraction * (values[point] - values[point - 1]));
            }

            return values[Count - 1];
        }
    }
}
