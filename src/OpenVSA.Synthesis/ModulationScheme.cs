using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Synthesis
{
    /// <summary>One point of a constellation, in normalised units.</summary>
    public readonly struct SymbolPoint
    {
        /// <summary>Creates a point.</summary>
        /// <param name="i">In-phase component.</param>
        /// <param name="q">Quadrature component.</param>
        public SymbolPoint(double i, double q)
        {
            I = i;
            Q = q;
        }

        /// <summary>In-phase component.</summary>
        public double I { get; }

        /// <summary>Quadrature component.</summary>
        public double Q { get; }

        /// <summary>Distance from another point.</summary>
        /// <param name="other">The other point.</param>
        public double DistanceTo(SymbolPoint other)
        {
            double di = I - other.I;
            double dq = Q - other.Q;

            return Math.Sqrt(di * di + dq * dq);
        }

        /// <inheritdoc />
        public override string ToString() =>
            "(" + I.ToString("0.000", CultureInfo.InvariantCulture) + ", " +
            Q.ToString("0.000", CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// A modulation and its ideal constellation, for generating signals whose symbols are known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This exists so that the demodulation displays can be exercised before there is a
    /// demodulator.</strong> <c>REQ-UI-050</c> wants one drawn point per symbol at the decision
    /// instants, <c>REQ-UI-051</c> wants an eye folded on the symbol clock with <em>m − 1</em>
    /// openings for an m-level modulation, and <c>REQ-UI-052</c> wants the detected symbol stream
    /// beside its metrics. Every one of those criteria is stated against a signal whose truth is
    /// known — "checked against the generated signal's known symbol clock, so a half-symbol offset
    /// fails" — and this is where that truth comes from.
    /// </para>
    /// <para>
    /// <strong>Unit average power, not unit peak.</strong> Constellations are compared by error
    /// vector magnitude, which is referenced to the average power of the ideal points; normalising
    /// to the outermost point instead would make every EVM figure differ from every instrument's by
    /// a constant that depends on the modulation order.
    /// </para>
    /// <para>
    /// <see cref="LevelsPerAxis"/> is the m of "an m-level modulation shows m − 1 eyes stacked
    /// vertically". It is the number of distinct values the I axis takes, not the number of
    /// constellation points — 16QAM has sixteen points and four levels, and an eye with fifteen
    /// openings would be a display of something else.
    /// </para>
    /// </remarks>
    public sealed class ModulationScheme
    {
        private readonly ReadOnlyCollection<SymbolPoint> _points;

        private ModulationScheme(
            string name,
            int bitsPerSymbol,
            int levelsPerAxis,
            IList<SymbolPoint> points,
            bool isOffset = false,
            double rotationPerSymbolRadians = 0.0)
        {
            Name = name;
            BitsPerSymbol = bitsPerSymbol;
            LevelsPerAxis = levelsPerAxis;
            IsOffset = isOffset;
            RotationPerSymbolRadians = rotationPerSymbolRadians;
            _points = new ReadOnlyCollection<SymbolPoint>(points);
        }

        /// <summary>The modulation's name, as a display would label it.</summary>
        public string Name { get; }

        /// <summary>Bits carried by one symbol.</summary>
        public int BitsPerSymbol { get; }

        /// <summary>
        /// Distinct levels on the I axis — the <em>m</em> of <c>REQ-UI-051</c>'s m − 1 eyes.
        /// </summary>
        public int LevelsPerAxis { get; }

        /// <summary>How many eyes an eye diagram of this modulation shows.</summary>
        public int EyeOpenings => LevelsPerAxis - 1;

        /// <summary>The ideal constellation points, indexed by symbol value.</summary>
        public IReadOnlyList<SymbolPoint> IdealPoints => _points;

        /// <summary>How many symbol values there are.</summary>
        public int Order => _points.Count;

        /// <summary>
        /// Whether the Q axis is sent half a symbol after the I axis, as OQPSK is
        /// (<c>REQ-DEM-012</c>).
        /// </summary>
        public bool IsOffset { get; }

        /// <summary>
        /// How far the constellation is turned between one symbol and the next, in radians.
        /// </summary>
        /// <remarks>
        /// π/4 for π/4-DQPSK, zero for everything that does not turn. A transmitter applies it; a
        /// demodulator takes it out again, which is <c>REQ-DEM-012</c>'s business rather than this
        /// project's.
        /// </remarks>
        public double RotationPerSymbolRadians { get; }

        /// <summary>Binary phase shift keying: two points on the I axis.</summary>
        public static ModulationScheme Bpsk() =>
            Normalised("BPSK", 1, 2, new List<SymbolPoint>
            {
                new SymbolPoint(-1.0, 0.0),
                new SymbolPoint(1.0, 0.0),
            });

        /// <summary>Quadrature phase shift keying: four points on the diagonals.</summary>
        public static ModulationScheme Qpsk() =>
            Normalised("QPSK", 2, 2, new List<SymbolPoint>
            {
                new SymbolPoint(1.0, 1.0),
                new SymbolPoint(-1.0, 1.0),
                new SymbolPoint(-1.0, -1.0),
                new SymbolPoint(1.0, -1.0),
            });

        /// <summary>Eight-point phase shift keying.</summary>
        public static ModulationScheme Psk8()
        {
            var points = new List<SymbolPoint>(8);

            for (int i = 0; i < 8; i++)
            {
                double angle = 2.0 * Math.PI * i / 8.0;

                points.Add(new SymbolPoint(Math.Cos(angle), Math.Sin(angle)));
            }

            // Every point is at the same radius, so the I axis takes as many values as there are
            // distinct cosines: ±1, ±√2/2 and 0 — five for an eight-point ring, not eight. Counted
            // rather than guessed, because guessing gave six and the test that derives it from the
            // constellation said otherwise.
            return Normalised("8PSK", 3, 5, points);
        }

        /// <summary>Sixteen-point quadrature amplitude modulation: a 4 × 4 grid.</summary>
        public static ModulationScheme Qam16() => SquareQam("16QAM", 4, 4);

        /// <summary>Sixty-four-point quadrature amplitude modulation: an 8 × 8 grid.</summary>
        public static ModulationScheme Qam64() => SquareQam("64QAM", 8, 6);

        /// <summary>
        /// A scheme from an explicit point list, normalised to unit mean power.
        /// </summary>
        /// <param name="name">What to call it.</param>
        /// <param name="points">The points, indexed by symbol value.</param>
        /// <param name="isOffset">Whether Q is sent half a symbol after I.</param>
        /// <param name="rotationPerSymbolRadians">
        /// How far the points turn between one symbol and the next.
        /// </param>
        /// <returns>The scheme.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="points"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// There are fewer than two points, or the count is not a power of two.
        /// </exception>
        /// <remarks>
        /// <para>
        /// How a format from <c>REQ-DEM-010</c>'s catalogue is generated without this project having
        /// to know that catalogue exists. <c>OpenVSA.Synthesis</c> sits outside the analysis stack so
        /// that a transport can use it, which means it cannot reference <c>OpenVSA.Demod</c> — so the
        /// demodulator's constellation is handed in as points rather than looked up.
        /// </para>
        /// <para>
        /// <strong>A round trip built this way tests the chain, not the constellation.</strong> Both
        /// ends then share one point list, so it proves that timing, carrier, gain and the decisions
        /// recover what was sent — and proves nothing about whether the geometry matches anybody's
        /// standard. Only a transmitter can say that; see <c>evidence/req-e44-007/</c>, where an
        /// instrument's Gray-coded QPSK scored 75.10 % against this project's natural mapping.
        /// </para>
        /// </remarks>
        public static ModulationScheme FromPoints(
            string name,
            IList<SymbolPoint> points,
            bool isOffset = false,
            double rotationPerSymbolRadians = 0.0)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            if (points.Count < 2)
            {
                throw new ArgumentException(
                    "A modulation needs at least two points to carry a bit.", nameof(points));
            }

            int bits = 0;

            while ((1 << bits) < points.Count)
            {
                bits++;
            }

            if ((1 << bits) != points.Count)
            {
                throw new ArgumentException(
                    points.Count + " points do not carry a whole number of bits per symbol.",
                    nameof(points));
            }

            // Counted from the points, never declared: an eight-point ring has five distinct
            // cosines and not eight, and REQ-UI-051's eye count is derived from this.
            var levels = new HashSet<double>();

            foreach (SymbolPoint point in points)
            {
                levels.Add(Math.Round(point.I, 6));
            }

            return Normalised(
                name,
                bits,
                levels.Count,
                new List<SymbolPoint>(points),
                isOffset,
                rotationPerSymbolRadians);
        }

        /// <summary>Every scheme this harness can generate.</summary>
        public static IReadOnlyList<ModulationScheme> All =>
            new ReadOnlyCollection<ModulationScheme>(new List<ModulationScheme>
            {
                Bpsk(), Qpsk(), Psk8(), Qam16(), Qam64(),
            });

        /// <summary>
        /// The scheme of that name, or <c>null</c>.
        /// </summary>
        /// <param name="name">The name, compared without regard to case.</param>
        public static ModulationScheme ByName(string name)
        {
            foreach (ModulationScheme scheme in All)
            {
                if (string.Equals(scheme.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return scheme;
                }
            }

            return null;
        }

        /// <summary>
        /// The nearest ideal point to a measurement, and how far away it was.
        /// </summary>
        /// <param name="measured">The measured point.</param>
        /// <param name="errorMagnitude">Distance to the nearest ideal point.</param>
        /// <returns>The symbol value that point stands for.</returns>
        /// <remarks>
        /// The decision a demodulator makes, written here so that a generated signal can be checked
        /// against its own truth without one — and so that a display can be handed decided symbols
        /// before <c>REQ-DEM</c> exists to decide them.
        /// </remarks>
        public int Decide(SymbolPoint measured, out double errorMagnitude)
        {
            int best = 0;
            double closest = double.MaxValue;

            for (int symbol = 0; symbol < _points.Count; symbol++)
            {
                double distance = measured.DistanceTo(_points[symbol]);

                if (distance < closest)
                {
                    closest = distance;
                    best = symbol;
                }
            }

            errorMagnitude = closest;
            return best;
        }

        /// <summary>
        /// The bits a symbol value carries, most significant first.
        /// </summary>
        /// <param name="symbol">The symbol value.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a symbol of this scheme.</exception>
        /// <remarks>
        /// <c>REQ-UI-052</c>'s bottom portion is "the detected symbol/bit stream", and its left
        /// gutter numbers the rows by bit. A scheme that could not say how many bits a symbol
        /// carries could not be displayed that way.
        /// </remarks>
        public string BitsOf(int symbol)
        {
            if (symbol < 0 || symbol >= _points.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(symbol), symbol, Name + " has symbols 0 to " + (_points.Count - 1) + ".");
            }

            var bits = new char[BitsPerSymbol];

            for (int bit = 0; bit < BitsPerSymbol; bit++)
            {
                bits[bit] = ((symbol >> (BitsPerSymbol - 1 - bit)) & 1) == 1 ? '1' : '0';
            }

            return new string(bits);
        }

        /// <inheritdoc />
        public override string ToString() =>
            Name + " (" + Order + " points, " + BitsPerSymbol + " bits, " + EyeOpenings + " eyes)";

        private static ModulationScheme SquareQam(string name, int side, int bits)
        {
            var points = new List<SymbolPoint>(side * side);

            for (int q = 0; q < side; q++)
            {
                for (int i = 0; i < side; i++)
                {
                    points.Add(new SymbolPoint(2 * i - (side - 1), 2 * q - (side - 1)));
                }
            }

            return Normalised(name, bits, side, points);
        }

        /// <summary>Scales a constellation to unit average power.</summary>
        private static ModulationScheme Normalised(
            string name,
            int bits,
            int levels,
            List<SymbolPoint> points,
            bool isOffset = false,
            double rotationPerSymbolRadians = 0.0)
        {
            double power = 0.0;

            foreach (SymbolPoint point in points)
            {
                power += point.I * point.I + point.Q * point.Q;
            }

            double scale = 1.0 / Math.Sqrt(power / points.Count);

            for (int i = 0; i < points.Count; i++)
            {
                points[i] = new SymbolPoint(points[i].I * scale, points[i].Q * scale);
            }

            return new ModulationScheme(
                name, bits, levels, points, isOffset, rotationPerSymbolRadians);
        }
    }
}
