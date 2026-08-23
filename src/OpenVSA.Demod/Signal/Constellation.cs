using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Demod.Results;

namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// The points a symbol is decided against, and the bits each one carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scope.</strong> <c>REQ-DEM-001</c> needs a constellation because steps 9 and 10 make
    /// no sense without one, and it needs exactly one format to demonstrate the chain. The
    /// catalogue of <c>REQ-DEM-010</c>, the user-defined constellations of <c>REQ-DEM-011</c> and
    /// the differential and offset handling of <c>REQ-DEM-012</c> are separate requirements and
    /// arrive with their own issues. This type is shaped to be what they extend — an arbitrary
    /// point list with a bit mapping — rather than something they will have to replace.
    /// </para>
    /// <para>
    /// <strong>Unit mean power.</strong> The points are scaled so the mean of their squared
    /// magnitudes is one. That is what lets the joint refinement treat its amplitude parameter as
    /// the signal's gain rather than as the gain times whatever scale the constellation happened to
    /// be written in, and it is the convention EVM's normalisation reference
    /// (<c>REQ-DEM-061</c>) will be stated against.
    /// </para>
    /// <para>
    /// <strong>The mapping is natural, not Gray.</strong> Symbol <em>n</em> carries the bits of
    /// <em>n</em>, most significant first. Gray and explicit mappings belong to
    /// <c>REQ-DEM-011</c>; choosing one here would be deciding that requirement in passing, and the
    /// chain's own criteria do not depend on which mapping is used.
    /// </para>
    /// </remarks>
    public sealed class Constellation
    {
        private readonly ReadOnlyCollection<ConstellationPoint> _points;

        private int _symmetry;

        private Constellation(
            string name,
            int bitsPerSymbol,
            int levelsPerAxis,
            IList<ConstellationPoint> points,
            ModulationFamily family,
            bool isOffset)
        {
            Name = name;
            BitsPerSymbol = bitsPerSymbol;
            LevelsPerAxis = levelsPerAxis;
            Family = family;
            IsOffset = isOffset;
            _points = new ReadOnlyCollection<ConstellationPoint>(points);
        }

        /// <summary>What the format is called, for the result's annotation.</summary>
        public string Name { get; }

        /// <summary>How many bits one symbol carries.</summary>
        public int BitsPerSymbol { get; }

        /// <summary>Distinct levels on the I axis — the <em>m</em> of <c>REQ-UI-051</c>'s eyes.</summary>
        public int LevelsPerAxis { get; }

        /// <summary>
        /// Which family this format belongs to, which is what decides the metrics it shows
        /// (<c>REQ-DEM-071</c>).
        /// </summary>
        public ModulationFamily Family { get; }

        /// <summary>
        /// Whether I and Q are staggered by half a symbol, as OQPSK and its relatives are
        /// (<c>REQ-DEM-012</c>).
        /// </summary>
        /// <remarks>
        /// A property of the format rather than of its points: the constellation of OQPSK is QPSK's,
        /// and what differs is when each axis is sampled. It decides whether Offset EVM is a metric
        /// the summary shows, and <c>REQ-DEM-012</c> is where it decides rather more than that.
        /// </remarks>
        public bool IsOffset { get; }

        /// <summary>The points, indexed by symbol value.</summary>
        public IReadOnlyList<ConstellationPoint> Points => _points;

        /// <summary>How many points there are.</summary>
        public int Count => _points.Count;

        /// <summary>
        /// QPSK: four points on the diagonals, each carrying two bits.
        /// </summary>
        /// <returns>The constellation.</returns>
        public static Constellation Qpsk()
        {
            double unit = 1.0 / Math.Sqrt(2.0);

            var points = new List<ConstellationPoint>
            {
                new ConstellationPoint(unit, unit),
                new ConstellationPoint(-unit, unit),
                new ConstellationPoint(-unit, -unit),
                new ConstellationPoint(unit, -unit),
            };

            return new Constellation("QPSK", 2, 2, points, ModulationFamily.Psk, false);
        }

        /// <summary>
        /// The constellation a format name asks for.
        /// </summary>
        /// <param name="name">The format's name, compared without regard to case.</param>
        /// <returns>The constellation.</returns>
        /// <exception cref="ArgumentException">No format of that name is supported.</exception>
        /// <remarks>
        /// <para>
        /// One format, because <c>REQ-DEM-001</c> needed one and <c>REQ-DEM-010</c>'s catalogue is
        /// its own requirement. This exists so that a measurement's stored setup can name a format
        /// as text — which is what a state file has to hold — without every caller knowing which
        /// factory method to call, and so that the catalogue grows in one place.
        /// </para>
        /// <para>
        /// An unknown name is refused by name rather than falling back to QPSK. A demodulation
        /// silently performed against the wrong constellation reports EVM and symbols that look
        /// entirely reasonable and are wrong.
        /// </para>
        /// </remarks>
        public static Constellation ByName(string name)
        {
            if (string.Equals(name, "QPSK", StringComparison.OrdinalIgnoreCase))
            {
                return Qpsk();
            }

            throw new ArgumentException(
                "No format called \"" + (name ?? "(none)") + "\" is supported. This build " +
                "demodulates QPSK; REQ-DEM-010's catalogue is where the rest arrive.",
                nameof(name));
        }

        /// <summary>The names <see cref="ByName"/> answers to.</summary>
        public static IReadOnlyList<string> Names =>
            new ReadOnlyCollection<string>(new List<string> { "QPSK" });

        /// <summary>
        /// Builds a constellation from an explicit point list, normalising it to unit mean power.
        /// </summary>
        /// <param name="name">What the format is called.</param>
        /// <param name="points">The points, in symbol order.</param>
        /// <param name="levelsPerAxis">Distinct levels on the I axis.</param>
        /// <param name="family">Which family it belongs to; <c>Custom</c> when it is nobody's.</param>
        /// <param name="isOffset">Whether I and Q are staggered by half a symbol.</param>
        /// <returns>The constellation.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="points"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// There are fewer than two points, the count is not a power of two, or every point is at
        /// the origin.
        /// </exception>
        public static Constellation FromPoints(
            string name,
            IList<ConstellationPoint> points,
            int levelsPerAxis,
            ModulationFamily family = ModulationFamily.Custom,
            bool isOffset = false)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            if (points.Count < 2)
            {
                throw new ArgumentException(
                    "A constellation needs at least two points to carry a bit.", nameof(points));
            }

            int bits = BitsFor(points.Count);

            if (bits < 0)
            {
                throw new ArgumentException(
                    "A constellation of " + points.Count + " points does not carry a whole " +
                    "number of bits per symbol. Point lists that are not a power of two belong " +
                    "to REQ-DEM-011's explicit mapping, which is not this constructor.",
                    nameof(points));
            }

            double power = 0.0;

            foreach (ConstellationPoint point in points)
            {
                power += (point.I * point.I) + (point.Q * point.Q);
            }

            power /= points.Count;

            if (power < 1e-18)
            {
                throw new ArgumentException(
                    "Every point is at the origin, so there is nothing to normalise and nothing " +
                    "to decide against.", nameof(points));
            }

            double scale = 1.0 / Math.Sqrt(power);
            var normalised = new List<ConstellationPoint>(points.Count);

            foreach (ConstellationPoint point in points)
            {
                normalised.Add(new ConstellationPoint(point.I * scale, point.Q * scale));
            }

            return new Constellation(
                name ?? string.Empty,
                bits,
                levelsPerAxis < 2 ? 2 : levelsPerAxis,
                normalised,
                family,
                isOffset);
        }

        /// <summary>The nearest point to a measured sample, as a symbol value.</summary>
        /// <param name="i">The in-phase part.</param>
        /// <param name="q">The quadrature part.</param>
        /// <returns>The symbol value of the nearest point.</returns>
        /// <remarks>
        /// A linear scan. For the four to sixteen points a demonstration needs it is faster than
        /// anything cleverer, and the decision regions of <c>REQ-DEM-010</c>'s larger formats are
        /// that requirement's problem to make quick.
        /// </remarks>
        public int Decide(double i, double q)
        {
            int best = 0;
            double bestDistance = double.MaxValue;

            for (int symbol = 0; symbol < _points.Count; symbol++)
            {
                ConstellationPoint point = _points[symbol];
                double di = point.I - i;
                double dq = point.Q - q;
                double distance = (di * di) + (dq * dq);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = symbol;
                }
            }

            return best;
        }

        /// <summary>The bits a symbol value carries, most significant first.</summary>
        /// <param name="symbol">The symbol value.</param>
        /// <returns><see cref="BitsPerSymbol"/> bits, each zero or one.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The symbol is not in the constellation.</exception>
        public int[] BitsOf(int symbol)
        {
            if (symbol < 0 || symbol >= _points.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(symbol), symbol, "The constellation has " + _points.Count + " points.");
            }

            var bits = new int[BitsPerSymbol];

            for (int bit = 0; bit < BitsPerSymbol; bit++)
            {
                bits[bit] = (symbol >> (BitsPerSymbol - 1 - bit)) & 1;
            }

            return bits;
        }

        /// <inheritdoc />
        public override string ToString() => Name + ", " + _points.Count + " points";

        /// <summary>The point for a symbol, in the estimators' working type.</summary>
        /// <param name="symbol">The symbol value.</param>
        internal Iq Ideal(int symbol) => new Iq(_points[symbol].I, _points[symbol].Q);

        /// <summary>
        /// The largest <em>m</em> for which turning the constellation by a full turn divided by
        /// <em>m</em> leaves it looking the same.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Four for QPSK and for square QAM, two for BPSK. Step 3 raises the signal to this power
        /// to strip the modulation off the carrier, which works precisely because the symmetry
        /// makes every symbol's contribution land at the same angle.
        /// </para>
        /// <para>
        /// Computed from the points rather than declared beside them, so a constellation defined by
        /// <see cref="FromPoints"/> — which is how <c>REQ-DEM-011</c>'s user-defined formats will
        /// arrive — gets the right answer without anyone having to remember to state it.
        /// </para>
        /// </remarks>
        internal int RotationalSymmetry
        {
            get
            {
                if (_symmetry == 0)
                {
                    _symmetry = LargestSymmetry();
                }

                return _symmetry;
            }
        }

        private int LargestSymmetry()
        {
            for (int order = _points.Count; order > 1; order--)
            {
                if (IsSymmetricUnder(order))
                {
                    return order;
                }
            }

            return 1;
        }

        private bool IsSymmetricUnder(int order)
        {
            double angle = 2.0 * Math.PI / order;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);

            foreach (ConstellationPoint point in _points)
            {
                double i = (point.I * cos) - (point.Q * sin);
                double q = (point.I * sin) + (point.Q * cos);

                if (!Contains(i, q))
                {
                    return false;
                }
            }

            return true;
        }

        private bool Contains(double i, double q)
        {
            const double Tolerance = 1e-9;

            foreach (ConstellationPoint point in _points)
            {
                if (Math.Abs(point.I - i) < Tolerance && Math.Abs(point.Q - q) < Tolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static int BitsFor(int count)
        {
            int bits = 0;
            int value = count;

            while (value > 1)
            {
                if ((value & 1) != 0)
                {
                    return -1;
                }

                value >>= 1;
                bits++;
            }

            return bits;
        }
    }
}
