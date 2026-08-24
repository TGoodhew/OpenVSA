using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenVSA.Demod.Results
{
    /// <summary>
    /// What an error metric is expressed as a percentage <em>of</em> (<c>REQ-DEM-061</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the commonest reason two instruments disagree about EVM.</strong> The
    /// requirement says so in as many words, and it is why the choice is a setting rather than a
    /// constant: the same measurement of the same signal reads 1.34 times larger under one
    /// convention than the other on 16-QAM, and neither number is wrong.
    /// </para>
    /// <para>
    /// It has consequences only for variable-envelope formats. On BPSK, QPSK, 8PSK or MSK every
    /// point is the same distance from the origin, the maximum and the RMS are one number, and the
    /// setting does nothing — which <see cref="EvmReference.IsInert"/> reports rather than leaving
    /// a user to wonder why a control had no effect.
    /// </para>
    /// </remarks>
    public enum EvmNormalisation
    {
        /// <summary>The largest magnitude in the reference constellation.</summary>
        /// <remarks>
        /// The outermost corner. Reads lower than <see cref="RmsMagnitude"/> on any format whose
        /// points are not all the same distance out, because the divisor is larger.
        /// </remarks>
        MaximumMagnitude,

        /// <summary>The RMS magnitude — the square root of the mean power — of the reference.</summary>
        /// <remarks>
        /// The default. A signal's average power is what a level measurement reads, so an EVM
        /// referenced to it is a ratio between two quantities a user can both see.
        /// </remarks>
        RmsMagnitude,

        /// <summary>A value the user supplies.</summary>
        /// <remarks>
        /// For comparing against a figure produced under a convention this build does not implement,
        /// which is the situation the other two settings exist to avoid and cannot always.
        /// </remarks>
        UserSpecified,
    }

    /// <summary>
    /// The divisor <c>V_norm</c> that turns an error vector into a percentage, and where it came
    /// from (<c>REQ-DEM-061</c>, <c>REQ-DEM-072</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It carries its own provenance.</strong> <c>REQ-DEM-072</c> asks that no metric be a
    /// number without an account of how it was arrived at, and a percentage whose denominator is
    /// unstated is exactly that. <see cref="Describe"/> is the sentence a display puts next to the
    /// figure.
    /// </para>
    /// <para>
    /// <strong>The reference is the constellation, not the symbols that happened to be
    /// sent.</strong> <c>REQ-DEM-061</c> says "of the reference constellation", and the distinction
    /// is real: a short window of 64-QAM visits a handful of its points, and a divisor computed from
    /// those would make the same signal read differently from one acquisition to the next. Built
    /// from a constellation the reference is a property of the format; built from the decided ideal
    /// points — which is all <see cref="FromPoints"/> has — it converges on the same number as the
    /// window lengthens, and <see cref="FromPoints"/> exists for the callers that hold points and no
    /// format.
    /// </para>
    /// </remarks>
    public sealed class EvmReference
    {
        /// <summary>
        /// How close the maximum and the RMS must be for the choice to be called inert, as a
        /// fraction.
        /// </summary>
        /// <remarks>
        /// A part in ten thousand. A constant-modulus format's two magnitudes are equal in exact
        /// arithmetic and differ in the last bits or two after a constellation has been built by
        /// trigonometry; the smallest real gap in the catalogue is 32-point star QAM, whose maximum
        /// is some tens of per cent above its RMS. There is nothing between.
        /// </remarks>
        private const double InertFraction = 1e-4;

        /// <summary>
        /// Creates a reference from the two magnitudes a format has and the choice between them.
        /// </summary>
        /// <param name="choice">Which of them to use.</param>
        /// <param name="maximumMagnitude">The largest magnitude in the reference constellation.</param>
        /// <param name="rmsMagnitude">The RMS magnitude of the reference constellation.</param>
        /// <param name="userVolts">The value to use when <paramref name="choice"/> asks for one.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A magnitude is not positive, or the choice is not one this understands.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="choice"/> is <see cref="EvmNormalisation.UserSpecified"/> and
        /// <paramref name="userVolts"/> is not positive. A normalisation of zero would report every
        /// error as infinite, which is a worse answer than refusing.
        /// </exception>
        public EvmReference(
            EvmNormalisation choice,
            double maximumMagnitude,
            double rmsMagnitude,
            double userVolts)
        {
            if (maximumMagnitude <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumMagnitude),
                    maximumMagnitude,
                    "A constellation's largest magnitude is positive.");
            }

            if (rmsMagnitude <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rmsMagnitude), rmsMagnitude, "A constellation's RMS magnitude is positive.");
            }

            Choice = choice;
            MaximumMagnitude = maximumMagnitude;
            RmsMagnitude = rmsMagnitude;
            UserVolts = userVolts;

            switch (choice)
            {
                case EvmNormalisation.MaximumMagnitude:
                    Volts = maximumMagnitude;
                    break;

                case EvmNormalisation.RmsMagnitude:
                    Volts = rmsMagnitude;
                    break;

                case EvmNormalisation.UserSpecified:
                    if (userVolts <= 0.0)
                    {
                        throw new ArgumentException(
                            "A user-specified EVM normalisation is a positive magnitude; " +
                            userVolts.ToString("G6", CultureInfo.InvariantCulture) +
                            " would make every error infinite (REQ-DEM-061).",
                            nameof(userVolts));
                    }

                    Volts = userVolts;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(choice), choice, "That is not a normalisation this build has.");
            }
        }

        /// <summary>Which normalisation was asked for.</summary>
        public EvmNormalisation Choice { get; }

        /// <summary>The divisor the metrics were actually referenced to.</summary>
        public double Volts { get; }

        /// <summary>The largest magnitude in the reference constellation.</summary>
        public double MaximumMagnitude { get; }

        /// <summary>The RMS magnitude of the reference constellation.</summary>
        public double RmsMagnitude { get; }

        /// <summary>What was supplied for <see cref="EvmNormalisation.UserSpecified"/>.</summary>
        public double UserVolts { get; }

        /// <summary>
        /// Whether the choice makes no difference to this format.
        /// </summary>
        /// <remarks>
        /// True for a constant-modulus format, where the maximum and the RMS are the same number.
        /// <c>REQ-DEM-061</c> asks that this be visible rather than left to be discovered by
        /// changing the setting and seeing nothing happen.
        /// </remarks>
        public bool IsInert =>
            Math.Abs(MaximumMagnitude - RmsMagnitude) <= RmsMagnitude * InertFraction;

        /// <summary>
        /// The ratio between the two computed normalisations, largest over RMS.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DEM-061</c>'s acceptance criterion in one number: switching from RMS to maximum
        /// divides every reported percentage by exactly this, which for 16-QAM is
        /// <c>sqrt(18/10)</c> = 1.3416. It is here so that a test and a display can both state the
        /// prediction rather than each recomputing it.
        /// </remarks>
        public double MaximumOverRms => MaximumMagnitude / RmsMagnitude;

        /// <summary>
        /// A reference over a list of points.
        /// </summary>
        /// <param name="choice">Which normalisation.</param>
        /// <param name="points">The points to measure, usually a format's ideal points.</param>
        /// <param name="userVolts">The value for <see cref="EvmNormalisation.UserSpecified"/>.</param>
        /// <returns>The reference, or <c>null</c> when there are no points to measure.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="points"/> is null.</exception>
        /// <remarks>
        /// Null rather than a reference of one when the list is empty: a divisor invented for a
        /// measurement with nothing in it would put a percentage on an empty summary.
        /// </remarks>
        public static EvmReference FromPoints(
            EvmNormalisation choice, IReadOnlyList<ConstellationPoint> points, double userVolts)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            if (points.Count == 0)
            {
                return null;
            }

            double largest = 0.0;
            double sum = 0.0;

            foreach (ConstellationPoint point in points)
            {
                double power = (point.I * point.I) + (point.Q * point.Q);

                sum += power;

                if (power > largest)
                {
                    largest = power;
                }
            }

            double maximum = Math.Sqrt(largest);
            double rms = Math.Sqrt(sum / points.Count);

            // A constellation whose points are all at the origin -- which nothing real is, and an
            // empty or degenerate one might be. One volt, so that the metrics read the error itself
            // rather than dividing by nothing.
            if (maximum < 1e-12 || rms < 1e-12)
            {
                return new EvmReference(
                    choice == EvmNormalisation.UserSpecified && userVolts > 0.0
                        ? choice
                        : EvmNormalisation.RmsMagnitude,
                    1.0,
                    1.0,
                    userVolts);
            }

            return new EvmReference(choice, maximum, rms, userVolts);
        }

        /// <summary>
        /// The sentence a display puts beside the figure (<c>REQ-DEM-072</c>).
        /// </summary>
        /// <returns>What the percentages are a percentage of, and whether the choice mattered.</returns>
        public string Describe()
        {
            string what;

            switch (Choice)
            {
                case EvmNormalisation.MaximumMagnitude:
                    what = "the largest magnitude in the reference constellation";
                    break;

                case EvmNormalisation.UserSpecified:
                    what = "a user-specified magnitude";
                    break;

                default:
                    what = "the RMS magnitude of the reference constellation";
                    break;
            }

            string stated =
                "Referenced to " + what + ", " +
                Volts.ToString("G6", CultureInfo.InvariantCulture) + ".";

            if (Choice == EvmNormalisation.UserSpecified)
            {
                return stated;
            }

            if (IsInert)
            {
                return stated +
                    " This format is constant-modulus, so the maximum and the RMS are the same " +
                    "number and the choice makes no difference to it.";
            }

            return stated + " The other setting would read " +
                (Choice == EvmNormalisation.MaximumMagnitude ? "larger" : "smaller") +
                " by a factor of " +
                MaximumOverRms.ToString("G6", CultureInfo.InvariantCulture) + ".";
        }

        /// <inheritdoc />
        public override string ToString() =>
            Choice + " = " + Volts.ToString("G6", CultureInfo.InvariantCulture);
    }
}
