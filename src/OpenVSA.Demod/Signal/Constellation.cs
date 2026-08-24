using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using OpenVSA.Demod.Results;

namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// The points a symbol is decided against, and the bits each one carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scope.</strong> This is <c>REQ-DEM-010</c>'s catalogue for every format that <em>is</em>
    /// a point list: the phase-keyed family, quadrature amplitude modulation square and cross, star
    /// and arbitrary-ring APSK, and on-off keying. Those need nothing from the chain that
    /// <c>REQ-DEM-001</c> did not already build, which is why they are here and finished.
    /// </para>
    /// <para>
    /// <strong>What is deliberately not here.</strong> The rest of <c>REQ-DEM-010</c>'s table is not
    /// point lists. Offset formats sample the two axes half a symbol apart; differential ones carry
    /// their bits in the change of phase rather than the phase; EDGE turns the constellation by
    /// 3π/8 every symbol; FSK is not a constellation at all and VSB is barely one. Each needs the
    /// chain to do something different, which is <c>REQ-DEM-012</c>'s and <c>REQ-DEM-021</c>'s work,
    /// and each arrives with that rather than as a name in a list that demodulates to nonsense.
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
    /// <para>
    /// <strong>That the mapping is a convention and not a fact is not theoretical.</strong> On
    /// 24 August 2026 an E4438C's <c>QPSK</c> and its <c>GRAYQPSK</c> were both demodulated against
    /// this natural mapping and compared with a PN9 sequence generated independently: the first
    /// matched 1024 bits of 1024, the second scored 75.10 % — exactly what a Gray transposition of
    /// two symbols predicts — and was rejected. So a wrong mapping is invisible to EVM and to any
    /// round trip through OpenVSA's own generator, and visible only against a transmitter.
    /// <c>evidence/req-e44-007/</c> has it.
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
            bool isOffset,
            bool isDifferential = false,
            double rotationPerSymbolRadians = 0.0)
        {
            Name = name;
            BitsPerSymbol = bitsPerSymbol;
            LevelsPerAxis = levelsPerAxis;
            Family = family;
            IsOffset = isOffset;
            IsDifferential = isDifferential;
            RotationPerSymbolRadians = rotationPerSymbolRadians;
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

        /// <summary>
        /// Whether the bits are carried by the change from one symbol to the next rather than by
        /// the symbol itself (<c>REQ-DEM-012</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Also a property of the format rather than of its points: DQPSK's constellation is QPSK's.
        /// What it says is that the decided symbol is an accumulation and the data is its
        /// difference, which is <c>DemodStep.SymbolDecisions</c>'s to undo.
        /// </para>
        /// <para>
        /// <strong>It is what the format expects, not what will be done.</strong>
        /// <c>REQ-DEM-012</c> requires the reference to be selectable, so
        /// <see cref="OpenVSA.Demod.Chain.DemodSettings.DifferentialReference"/> is what actually decides, and this
        /// is only what it follows when it is left to the format.
        /// </para>
        /// </remarks>
        public bool IsDifferential { get; }

        /// <summary>
        /// How far the constellation is turned between one symbol and the next, in radians.
        /// </summary>
        /// <remarks>
        /// <para>
        /// π/4 for π/4-DQPSK, and zero for everything that does not rotate. Symbol <em>k</em> is
        /// decided against the points turned by <em>k</em> times this, which is why
        /// <see cref="Decide(Iq, int)"/> and <see cref="Ideal(int, int)"/> take a symbol index at
        /// all.
        /// </para>
        /// <para>
        /// <strong>The rotation has to come out before the carrier is fitted, not after.</strong> A
        /// fixed turn per symbol is indistinguishable from a carrier frequency offset of that many
        /// cycles per symbol — π/4 per symbol is exactly Rs/8 — so a chain that left it in would fit
        /// it as frequency error, report Rs/8 of carrier error that is not there, and hand the
        /// decisions a constellation that had been turned by however much of it the fit did not
        /// absorb.
        /// </para>
        /// </remarks>
        public double RotationPerSymbolRadians { get; }

        /// <summary>
        /// Whether the points are one ring with the symbol value running around it, which is what a
        /// difference of symbol values has to be a change of phase.
        /// </summary>
        /// <remarks>
        /// True of the phase-keyed family and of nothing else in this catalogue: a QAM's indices run
        /// along rows and its points are at several radii, so subtracting two of them is arithmetic
        /// on a table index rather than a phase. <c>REQ-DEM-012</c>'s differential decoding is
        /// refused on a constellation this is false of — refused rather than allowed to produce the
        /// well-formed, meaningless bit stream it otherwise would.
        /// </remarks>
        public bool IsIndexedRing
        {
            get
            {
                const double Tolerance = 1e-9;

                double radius = Math.Sqrt(
                    (_points[0].I * _points[0].I) + (_points[0].Q * _points[0].Q));

                if (radius < Tolerance)
                {
                    return false;
                }

                double step = (2.0 * Math.PI) / _points.Count;
                double first = Math.Atan2(_points[0].Q, _points[0].I);

                for (int symbol = 0; symbol < _points.Count; symbol++)
                {
                    ConstellationPoint point = _points[symbol];
                    double here = Math.Sqrt((point.I * point.I) + (point.Q * point.Q));

                    double turned = Math.Atan2(point.Q, point.I) - first - (step * symbol);

                    // Onto one turn, so that the comparison is not defeated by the wrap.
                    turned -= 2.0 * Math.PI * Math.Round(turned / (2.0 * Math.PI));

                    if (Math.Abs(here - radius) > Tolerance || Math.Abs(turned) > Tolerance)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

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
        /// Binary phase shift keying: two points on the I axis.
        /// </summary>
        /// <returns>The constellation.</returns>
        public static Constellation Bpsk() =>
            FromPoints(
                "BPSK",
                new List<ConstellationPoint>
                {
                    new ConstellationPoint(-1.0, 0.0),
                    new ConstellationPoint(1.0, 0.0),
                },
                2,
                ModulationFamily.Psk);

        /// <summary>
        /// On-off keying: a point at the origin and a point away from it.
        /// </summary>
        /// <returns>The constellation.</returns>
        /// <remarks>
        /// The one member of the catalogue with a point at the origin, which is why
        /// <see cref="RotationalSymmetry"/> has to survive one. It is amplitude shift keying with two levels
        /// and the lower level switched off, and <c>REQ-DEM-010</c> lists it under the custom APSK
        /// row because that is where the reference product puts it.
        /// </remarks>
        public static Constellation Ook() =>
            FromPoints(
                "OOK",
                new List<ConstellationPoint>
                {
                    new ConstellationPoint(0.0, 0.0),
                    new ConstellationPoint(1.0, 0.0),
                },
                2,
                ModulationFamily.Ask);

        /// <summary>
        /// Phase shift keying of any order: <paramref name="order"/> points evenly around a circle.
        /// </summary>
        /// <param name="order">How many points; a power of two, at least two.</param>
        /// <returns>The constellation.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The order is not a power of two of at least two.
        /// </exception>
        /// <remarks>
        /// Symbol zero is on the positive I axis for every order except four. QPSK is placed on the
        /// diagonals instead, because that is where every instrument and every diagram puts it and
        /// because the bench measurement of 24 August 2026 confirmed OpenVSA's QPSK agrees with an
        /// E4438C's — a rotation is free to a demodulator but not to somebody reading a
        /// constellation against a manual.
        /// </remarks>
        public static Constellation Psk(int order)
        {
            RequirePowerOfTwo(order, nameof(order));

            if (order == 4)
            {
                return Qpsk();
            }

            var points = new List<ConstellationPoint>(order);

            for (int symbol = 0; symbol < order; symbol++)
            {
                double angle = 2.0 * Math.PI * symbol / order;

                points.Add(new ConstellationPoint(Math.Cos(angle), Math.Sin(angle)));
            }

            return FromPoints(
                order == 2 ? "BPSK" : order + "PSK", points, LevelsIn(points), ModulationFamily.Psk);
        }

        /// <summary>
        /// Quadrature amplitude modulation of any order: square where the order is an even power of
        /// two, cross where it is odd.
        /// </summary>
        /// <param name="order">How many points; a power of two, at least four.</param>
        /// <returns>The constellation.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The order is not a power of two of at least four.
        /// </exception>
        /// <remarks>
        /// <para>
        /// <strong>Why odd powers of two are cross-shaped and not rectangular.</strong> 32, 128, 512
        /// and 2048 points cannot fill a square. A rectangle would do it — 8 × 4 for 32 — at the cost
        /// of a worse peak-to-mean ratio and a smaller minimum distance for the same mean power,
        /// which is why no standard uses one. The cross is built the way the standards build it:
        /// take the square that is one binary step too large and cut equal blocks from its four
        /// corners, which removes the points furthest from the origin.
        /// </para>
        /// <para>
        /// 32 points come from a 6 × 6 square less one point per corner, 128 from 12 × 12 less a
        /// 2 × 2 block per corner, 512 from 24 × 24 less 4 × 4, and 2048 from 48 × 48 less 8 × 8. The
        /// side doubles and the corner block doubles with it, so the same construction serves them
        /// all.
        /// </para>
        /// </remarks>
        public static Constellation Qam(int order)
        {
            RequirePowerOfTwo(order, nameof(order));

            if (order < 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(order), order, "Quadrature amplitude modulation needs at least four " +
                    "points; two points on one axis is BPSK or OOK.");
            }

            int bits = BitsFor(order);
            List<ConstellationPoint> points = (bits % 2) == 0 ? SquareQam(bits) : CrossQam(bits);

            return FromPoints(order + "QAM", points, LevelsIn(points), ModulationFamily.Qam);
        }

        /// <summary>One ring of an amplitude-and-phase constellation.</summary>
        /// <remarks>
        /// A class rather than three loose arrays because the three numbers only mean anything
        /// together, and <c>REQ-DEM-010</c>'s limit — eight arbitrarily spaced rings, 256 points —
        /// is a limit on a list of these.
        /// </remarks>
        public sealed class ApskRing
        {
            /// <summary>Describes one ring.</summary>
            /// <param name="radius">Its radius, in the same arbitrary units as the other rings.</param>
            /// <param name="points">How many points are spaced evenly around it.</param>
            /// <param name="phaseRadians">Where the first point sits, anticlockwise from the I axis.</param>
            /// <exception cref="ArgumentOutOfRangeException">
            /// The radius is not positive and finite, or the ring holds fewer than one point.
            /// </exception>
            public ApskRing(double radius, int points, double phaseRadians = 0.0)
            {
                if (!(radius > 0.0) || double.IsInfinity(radius))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(radius), radius, "A ring's radius must be positive and finite.");
                }

                if (points < 1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(points), points, "A ring carries at least one point.");
                }

                Radius = radius;
                Points = points;
                PhaseRadians = phaseRadians;
            }

            /// <summary>The ring's radius.</summary>
            public double Radius { get; }

            /// <summary>How many points it carries.</summary>
            public int Points { get; }

            /// <summary>Where its first point sits, anticlockwise from the I axis.</summary>
            public double PhaseRadians { get; }

            /// <inheritdoc />
            public override string ToString() =>
                Points + " at " + Radius.ToString("G4", CultureInfo.InvariantCulture);
        }

        /// <summary>The most rings <c>REQ-DEM-010</c> requires an APSK constellation to carry.</summary>
        public const int MaximumApskRings = 8;

        /// <summary>The most points <c>REQ-DEM-010</c> requires an APSK constellation to carry.</summary>
        public const int MaximumApskPoints = 256;

        /// <summary>
        /// Amplitude and phase shift keying: points on arbitrarily spaced rings.
        /// </summary>
        /// <param name="name">What to call it.</param>
        /// <param name="rings">The rings, innermost first is conventional but not required.</param>
        /// <returns>The constellation.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="rings"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// There are no rings, more than <see cref="MaximumApskRings"/>, more than
        /// <see cref="MaximumApskPoints"/> points, or the total is not a power of two.
        /// </exception>
        /// <remarks>
        /// The limits are refused rather than clamped. A constellation quietly reduced to eight rings
        /// would demodulate, report an EVM, and be measuring something the user did not ask for —
        /// which is the failure this whole catalogue is arranged to make impossible.
        /// </remarks>
        public static Constellation Apsk(string name, IList<ApskRing> rings)
        {
            if (rings == null)
            {
                throw new ArgumentNullException(nameof(rings));
            }

            if (rings.Count == 0)
            {
                throw new ArgumentException("An APSK constellation needs a ring.", nameof(rings));
            }

            if (rings.Count > MaximumApskRings)
            {
                throw new ArgumentException(
                    "REQ-DEM-010 requires up to " + MaximumApskRings + " rings and this asks for " +
                    rings.Count + ".",
                    nameof(rings));
            }

            var points = new List<ConstellationPoint>();

            foreach (ApskRing ring in rings)
            {
                for (int point = 0; point < ring.Points; point++)
                {
                    double angle = ring.PhaseRadians + (2.0 * Math.PI * point / ring.Points);

                    points.Add(new ConstellationPoint(
                        ring.Radius * Math.Cos(angle), ring.Radius * Math.Sin(angle)));
                }
            }

            if (points.Count > MaximumApskPoints)
            {
                throw new ArgumentException(
                    "REQ-DEM-010 requires up to " + MaximumApskPoints + " points and these rings " +
                    "carry " + points.Count + ".",
                    nameof(rings));
            }

            return FromPoints(name, points, LevelsIn(points), ModulationFamily.Apsk);
        }

        /// <summary>
        /// Star quadrature amplitude modulation: equal numbers of points on concentric rings, each
        /// ring aligned with the last.
        /// </summary>
        /// <param name="order">How many points in total; a power of two.</param>
        /// <param name="rings">How many rings to spread them over.</param>
        /// <param name="ringRatio">The radius of each ring as a multiple of the one inside it.</param>
        /// <returns>The constellation.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The order is not a power of two, the rings do not divide it evenly, or the ratio is not
        /// greater than one.
        /// </exception>
        /// <remarks>
        /// <para>
        /// A star is APSK with the rings aligned and evenly populated, so it is built as one. It is
        /// listed separately in <c>REQ-DEM-010</c> because it is what the reference product calls it
        /// and what a user will look for.
        /// </para>
        /// <para>
        /// <strong>The ring ratio is a parameter because it is a choice, not a constant.</strong>
        /// Two is the textbook value and is the default here; differential star schemes in the
        /// literature use anything from 1.6 to 2.0, and a constellation built with the wrong one
        /// demodulates perfectly well against itself while disagreeing with the transmitter. The
        /// same trap as the bit mapping, and the same answer: state it, do not bury it.
        /// </para>
        /// </remarks>
        public static Constellation StarQam(int order, int rings = 2, double ringRatio = 2.0)
        {
            RequirePowerOfTwo(order, nameof(order));

            if (rings < 1 || (order % rings) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rings), rings, "The rings have to divide the " + order +
                    " points evenly.");
            }

            if (!(ringRatio > 1.0) || double.IsInfinity(ringRatio))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ringRatio), ringRatio,
                    "Each ring is outside the last, so the ratio is greater than one.");
            }

            var specification = new List<ApskRing>(rings);
            double radius = 1.0;

            for (int ring = 0; ring < rings; ring++)
            {
                specification.Add(new ApskRing(radius, order / rings));
                radius *= ringRatio;
            }

            Constellation star = Apsk(order + "STARQAM", specification);

            return new Constellation(
                star.Name,
                star.BitsPerSymbol,
                star.LevelsPerAxis,
                new List<ConstellationPoint>(star.Points),
                ModulationFamily.Qam,
                false);
        }

        /// <summary>
        /// Offset QPSK: QPSK's four points, with Q sent half a symbol after I
        /// (<c>REQ-DEM-012</c>).
        /// </summary>
        /// <returns>The constellation.</returns>
        /// <remarks>
        /// <para>
        /// <strong>The points are QPSK's, and that is the point.</strong> Offsetting the two axes
        /// changes when the signal passes through each state, not which states it has: it exists to
        /// keep the trajectory away from the origin, so that an amplifier does not have to be linear
        /// through zero. A constellation display of OQPSK and of QPSK are the same four points.
        /// </para>
        /// <para>
        /// What differs is everything about <em>when</em>: the chain samples I at the symbol instant
        /// and Q half a symbol later, which is why <c>REQ-DEM-012</c> requires two points per symbol
        /// internally and why the timing estimator needs its own treatment here — see
        /// <c>JointRefinementStep</c>.
        /// </para>
        /// </remarks>
        public static Constellation Oqpsk() => Qpsk().Staggered("OQPSK");

        /// <summary>
        /// Differential QPSK: QPSK's four points, carrying their bits in the change of phase.
        /// </summary>
        /// <returns>The constellation.</returns>
        public static Constellation Dqpsk() => Qpsk().Differential("DQPSK", 0.0);

        /// <summary>
        /// Differential 8PSK: the eight-point ring, carrying its bits in the change of phase.
        /// </summary>
        /// <returns>The constellation.</returns>
        public static Constellation D8psk() => Psk(8).Differential("D8PSK", 0.0);

        /// <summary>
        /// π/4-DQPSK: differential QPSK whose constellation turns by π/4 every symbol.
        /// </summary>
        /// <returns>The constellation.</returns>
        /// <remarks>
        /// <para>
        /// The transmitted points alternate between two QPSK sets a quarter-turn apart, so a
        /// constellation display of the signal shows eight. It is nonetheless a four-point format:
        /// the eight positions are two views of the same four states, and taking the turn out —
        /// which is what <see cref="RotationPerSymbolRadians"/> makes the chain do — leaves QPSK.
        /// </para>
        /// <para>
        /// <strong>Deciding against eight points instead would lose the ambiguity, not resolve
        /// it.</strong> The phase changes π/4-DQPSK sends are odd multiples of π/4; an eight-point
        /// decision would accept the even ones too and so would decode a signal that could not have
        /// been transmitted, quietly, rather than showing the error as a symbol that missed.
        /// </para>
        /// </remarks>
        public static Constellation Pi4Dqpsk() => Qpsk().Differential("PI4DQPSK", Math.PI / 4.0);

        /// <summary>The same points, sent with Q staggered half a symbol behind I.</summary>
        /// <param name="name">What the offset format is called.</param>
        private Constellation Staggered(string name) =>
            new Constellation(
                name,
                BitsPerSymbol,
                LevelsPerAxis,
                new List<ConstellationPoint>(_points),
                Family,
                true,
                IsDifferential,
                RotationPerSymbolRadians);

        /// <summary>The same points, carrying their bits in the change from symbol to symbol.</summary>
        /// <param name="name">What the differential format is called.</param>
        /// <param name="rotationPerSymbolRadians">How far the points turn between symbols.</param>
        /// <exception cref="InvalidOperationException">
        /// The points are not a ring whose index runs around it, so a difference of indices would
        /// not be a change of phase.
        /// </exception>
        /// <remarks>
        /// <strong>The guard is the whole reason this is a method and not a constructor
        /// argument.</strong> <see cref="DifferenceFrom"/> subtracts indices, which is a phase
        /// change only when the points sit at one radius with the index running around the circle.
        /// A differential variant of a QAM would subtract indices happily and produce a bit stream
        /// that meant nothing, at a perfectly respectable EVM. This refuses to build one.
        /// </remarks>
        private Constellation Differential(string name, double rotationPerSymbolRadians)
        {
            RequireRing(name);

            return new Constellation(
                name,
                BitsPerSymbol,
                LevelsPerAxis,
                new List<ConstellationPoint>(_points),
                Family,
                IsOffset,
                true,
                rotationPerSymbolRadians);
        }

        /// <summary>Checks the points are one ring, indexed around it.</summary>
        /// <param name="name">The name of the format being built, for the message.</param>
        private void RequireRing(string name)
        {
            if (!IsIndexedRing)
            {
                throw new InvalidOperationException(
                    name + " would carry its bits in the change from one symbol to the next, " +
                    "which is a change of phase — so its points have to be one ring with the " +
                    "symbol value running around it, and " + Name + "'s are not.");
            }
        }

        /// <summary>
        /// The constellation a format name asks for.
        /// </summary>
        /// <param name="name">The format's name, compared without regard to case.</param>
        /// <returns>The constellation.</returns>
        /// <exception cref="ArgumentException">No format of that name is supported.</exception>
        /// <remarks>
        /// <para>
        /// The entry point a stored setup uses, because a state file holds a format as text and no
        /// caller should have to know which factory method that text means.
        /// </para>
        /// <para>
        /// <strong>Orders are parsed rather than listed.</strong> "256QAM" is answered by the same
        /// three lines that answer "16QAM", so the catalogue cannot come to hold a format that was
        /// added to a list and never wired to a factory. <see cref="Names"/> is the list a user
        /// interface offers, and a test asserts every name in it resolves here and comes back
        /// calling itself the same thing.
        /// </para>
        /// <para>
        /// An unknown name is refused by name rather than falling back to anything. A demodulation
        /// silently performed against the wrong constellation reports EVM and symbols that look
        /// entirely reasonable and are wrong.
        /// </para>
        /// </remarks>
        public static Constellation ByName(string name)
        {
            string wanted = Canonical(name);

            switch (wanted)
            {
                case "BPSK":
                    return Bpsk();

                case "QPSK":
                    return Qpsk();

                case "OOK":
                    return Ook();

                case "OQPSK":
                    return Oqpsk();

                case "DQPSK":
                    return Dqpsk();

                case "D8PSK":
                    return D8psk();

                case "PI4DQPSK":
                case "P4DQPSK":
                    return Pi4Dqpsk();
            }

            int order;

            if (OrderBefore(wanted, "STARQAM", out order))
            {
                return StarQam(order);
            }

            if (OrderBefore(wanted, "QAM", out order))
            {
                return Qam(order);
            }

            if (OrderBefore(wanted, "PSK", out order))
            {
                return Psk(order);
            }

            throw new ArgumentException(
                "No format called \"" + (name ?? "(none)") + "\" is supported. This build " +
                "demodulates " + string.Join(", ", Names) + "; the frequency-keyed formats of " +
                "REQ-DEM-010, its vestigial-sideband ones and the shaped members of the offset " +
                "family are not point lists and arrive with the chain handling they need.",
                nameof(name));
        }

        /// <summary>A format name in the one spelling <see cref="ByName"/> compares against.</summary>
        /// <param name="name">The name as it was given.</param>
        /// <returns>Upper case, with the separators and the Greek dropped.</returns>
        /// <remarks>
        /// One format in this catalogue is habitually written four ways — π/4-DQPSK, PI/4 DQPSK,
        /// PI4DQPSK and, on the instrument that generates it, <c>P4DQPSK</c>. A state file, a bench
        /// script and a menu should not have to agree on which, so the punctuation and the Greek are
        /// removed before the comparison rather than four names being listed. Nothing else in the
        /// catalogue contains a separator, so this changes no other name.
        /// </remarks>
        private static string Canonical(string name)
        {
            string upper = (name ?? string.Empty).Trim().ToUpperInvariant();
            var canonical = new System.Text.StringBuilder(upper.Length);

            foreach (char letter in upper)
            {
                switch (letter)
                {
                    case '-':
                    case '/':
                    case ' ':
                    case '_':
                        break;

                    case 'Π':
                        canonical.Append("PI");
                        break;

                    default:
                        canonical.Append(letter);
                        break;
                }
            }

            return canonical.ToString();
        }

        /// <summary>Reads the order off the front of a name like <c>256QAM</c>.</summary>
        private static bool OrderBefore(string name, string suffix, out int order)
        {
            order = 0;

            if (!name.EndsWith(suffix, StringComparison.Ordinal) || name.Length == suffix.Length)
            {
                return false;
            }

            return int.TryParse(
                name.Substring(0, name.Length - suffix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out order);
        }

        /// <summary>The names <see cref="ByName"/> answers to.</summary>
        /// <remarks>
        /// <para>
        /// The formats of <c>REQ-DEM-010</c> that the chain can demodulate as it stands: those whose
        /// only requirement is a point list, and — since <c>REQ-DEM-012</c> — the offset and
        /// differential members of the phase-keyed family, whose points are a point list and whose
        /// difference is in when and against what they are decided. The rest of that catalogue —
        /// EDGE's rotation, FSK, VSB, MSK, GMSK and the shaped offset formats — are not point lists
        /// at all. They need a pulse this build does not have or a discriminator rather than a
        /// decision, and that handling is <c>REQ-DEM-021</c>'s. Listing them here without it would
        /// offer a user a format that demodulates to nonsense.
        /// </para>
        /// <para>
        /// APSK is absent from this list and present in the catalogue: <see cref="Apsk"/> builds one
        /// from rings, and which rings is exactly what <c>REQ-DEM-010</c> leaves to the user and
        /// <c>REQ-DEM-011</c> will store. A named APSK would be a ring geometry chosen here and
        /// asserted to be somebody's standard, which is the kind of quiet convention this catalogue
        /// exists to avoid.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<string> Names =>
            new ReadOnlyCollection<string>(new List<string>
            {
                "BPSK",
                "QPSK",
                "8PSK",
                "16PSK",
                "OOK",
                "OQPSK",
                "DQPSK",
                "PI4DQPSK",
                "D8PSK",
                "16QAM",
                "32QAM",
                "64QAM",
                "128QAM",
                "256QAM",
                "512QAM",
                "1024QAM",
                "2048QAM",
                "4096QAM",
                "16STARQAM",
                "32STARQAM",
            });

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

        /// <summary>The distinct in-phase levels a point list takes, which is what an eye shows.</summary>
        /// <remarks>
        /// Counted from the points rather than declared beside them. Declaring it looks easy and is
        /// not: an eight-point ring has five distinct cosines, not eight, and the first version of
        /// <c>ModulationScheme.Psk8</c> said six until a test derived it. Every constellation here
        /// therefore derives it the same way, so the number cannot disagree with the geometry.
        /// </remarks>
        private static int LevelsIn(IList<ConstellationPoint> points)
        {
            var levels = new HashSet<double>();

            foreach (ConstellationPoint point in points)
            {
                // Six decimals: far finer than any two distinct levels of a 4096-point
                // constellation are apart, and coarse enough that a cosine computed two ways is one
                // level rather than two.
                levels.Add(Math.Round(point.I, 6));
            }

            return levels.Count;
        }

        private static void RequirePowerOfTwo(int order, string parameter)
        {
            if (order < 2 || BitsFor(order) < 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameter, order,
                    "A constellation carries a whole number of bits, so its order is a power of " +
                    "two of at least two. " + order + " is not.");
            }
        }

        /// <summary>A square grid of odd coordinates, symbol zero at the bottom left.</summary>
        private static List<ConstellationPoint> SquareQam(int bits)
        {
            int side = 1 << (bits / 2);
            var points = new List<ConstellationPoint>(side * side);

            for (int i = 0; i < side; i++)
            {
                for (int q = 0; q < side; q++)
                {
                    points.Add(new ConstellationPoint((2 * i) - side + 1, (2 * q) - side + 1));
                }
            }

            return points;
        }

        /// <summary>
        /// The square one binary step too large, with equal blocks cut from its four corners.
        /// </summary>
        /// <remarks>
        /// For <c>2^(2k+1)</c> points the side is <c>3 × 2^(k-1) × 2</c> — 6, 12, 24, 48 for 32, 128,
        /// 512 and 2048 — and the block cut from each corner is a quarter of the surplus, so the
        /// four of them remove exactly the points furthest from the origin. Written as arithmetic on
        /// the side rather than as four tables, because four tables is four chances to mistype a
        /// coordinate.
        /// </remarks>
        private static List<ConstellationPoint> CrossQam(int bits)
        {
            int order = 1 << bits;

            // 32 -> 6, 128 -> 12, 512 -> 24, 2048 -> 48: the side grows with the square root of the
            // order, and 3/2 of a power of two is what makes the corners come out square.
            int side = 3 * (1 << ((bits - 3) / 2));
            int surplus = (side * side) - order;
            int corner = (int)Math.Round(Math.Sqrt(surplus / 4.0));

            if ((corner * corner * 4) != surplus)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bits), bits,
                    "A cross of " + order + " points would need " + surplus + " removed from a " +
                    side + " by " + side + " square, which is not four square corners.");
            }

            var points = new List<ConstellationPoint>(order);

            for (int i = 0; i < side; i++)
            {
                for (int q = 0; q < side; q++)
                {
                    bool inCorner =
                        (i < corner || i >= side - corner) && (q < corner || q >= side - corner);

                    if (inCorner)
                    {
                        continue;
                    }

                    points.Add(new ConstellationPoint((2 * i) - side + 1, (2 * q) - side + 1));
                }
            }

            return points;
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

        /// <summary>
        /// The change from one symbol to the next, as a symbol value of this constellation.
        /// </summary>
        /// <param name="symbol">The symbol decided at this instant.</param>
        /// <param name="previous">The symbol decided at the instant before.</param>
        /// <returns>The symbol value the change stands for.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Either symbol is not in the constellation.</exception>
        /// <remarks>
        /// <para>
        /// The difference of the two indices, taken around the ring. For the constellations a
        /// differential format is built from — and <see cref="Differential"/> refuses to build one
        /// from any other — the index runs around the circle, so the index difference <em>is</em> the
        /// phase change, in units of a full turn divided by the order.
        /// </para>
        /// <para>
        /// <strong>This is what makes a differential format immune to a turned
        /// constellation.</strong> Turning every symbol by the same amount adds the same index to
        /// both, and the difference is unchanged — which is why a differentially decoded bit stream
        /// needs no rotation searched to compare it with the sequence that was sent, and why the
        /// rotation ambiguity that <c>evidence/req-e44-007/</c> had to search for QPSK simply does
        /// not arise here.
        /// </para>
        /// </remarks>
        public int DifferenceFrom(int symbol, int previous)
        {
            RequireSymbol(symbol, nameof(symbol));
            RequireSymbol(previous, nameof(previous));

            int difference = (symbol - previous) % _points.Count;

            return difference < 0 ? difference + _points.Count : difference;
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
        /// How far the constellation has turned by a symbol index (<c>REQ-DEM-012</c>).
        /// </summary>
        /// <param name="symbolIndex">Which symbol of the result window, counting from zero.</param>
        /// <returns>The unit vector the points are turned by; unity for a format that does not turn.</returns>
        /// <remarks>
        /// Reduced onto one turn before the trigonometry rather than after: at the far end of a
        /// 40 000-symbol Result Length <c>REQ-DEM-013</c> allows, π/4 per symbol has accumulated to
        /// about 31 000 radians, where a double's spacing is a hundred-millionth of a radian. That is
        /// still far below anything a measurement resolves — but the reduction costs one modulo and
        /// removes the question.
        /// </remarks>
        internal Iq Rotation(int symbolIndex)
        {
            if (RotationPerSymbolRadians == 0.0)
            {
                return new Iq(1.0, 0.0);
            }

            const double Turn = 2.0 * Math.PI;
            double angle = (RotationPerSymbolRadians * symbolIndex) % Turn;

            return Iq.FromPhase(angle);
        }

        /// <summary>The nearest point to a sample, allowing for the format's own rotation.</summary>
        /// <param name="value">The measured sample, already corrected for carrier and gain.</param>
        /// <param name="symbolIndex">Which symbol of the result window it is.</param>
        /// <returns>The symbol value of the nearest point.</returns>
        internal int Decide(Iq value, int symbolIndex)
        {
            if (RotationPerSymbolRadians == 0.0)
            {
                return Decide(value.I, value.Q);
            }

            // Turn the sample back rather than the constellation forward: one rotation instead of
            // one per point, and the decision is the same either way.
            Iq unturned = value * Rotation(symbolIndex).Conjugate();

            return Decide(unturned.I, unturned.Q);
        }

        /// <summary>The point a symbol occupies at a given instant, allowing for the rotation.</summary>
        /// <param name="symbol">The symbol value.</param>
        /// <param name="symbolIndex">Which symbol of the result window it is.</param>
        internal Iq Ideal(int symbol, int symbolIndex) =>
            RotationPerSymbolRadians == 0.0
                ? Ideal(symbol)
                : Ideal(symbol) * Rotation(symbolIndex);

        private void RequireSymbol(int symbol, string parameter)
        {
            if (symbol < 0 || symbol >= _points.Count)
            {
                throw new ArgumentOutOfRangeException(
                    parameter, symbol, "The constellation has " + _points.Count + " points.");
            }
        }

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
                    _symmetry = WithRotation(LargestSymmetry());
                }

                return _symmetry;
            }
        }

        /// <summary>
        /// The power that strips the modulation off a format that turns as well as keys.
        /// </summary>
        /// <param name="points">The symmetry of the point list on its own.</param>
        /// <returns>The symmetry of everything the signal actually visits.</returns>
        /// <remarks>
        /// <para>
        /// Step 3 strips the modulation by raising the signal to this power, which works because
        /// every symbol's phase then lands at the same angle. A format that turns by θ every symbol
        /// visits its points <em>and</em> every rotation of them, so the power has to annihilate the
        /// turn too: it must be a multiple of the point list's own symmetry and of the number of
        /// symbols the turn takes to come back round.
        /// </para>
        /// <para>
        /// π/4-DQPSK is four points and eight positions, so its power is eight and not four.
        /// Raising it to the fourth instead turns the alternation into a line half a symbol rate
        /// away from the carrier, and step 3 would report that as the carrier offset — a confident,
        /// entirely wrong answer of Rs/2 rather than a failure.
        /// </para>
        /// </remarks>
        private int WithRotation(int points)
        {
            if (RotationPerSymbolRadians == 0.0)
            {
                return points;
            }

            double perTurn = (2.0 * Math.PI) / Math.Abs(RotationPerSymbolRadians);
            int rounded = (int)Math.Round(perTurn);

            if (rounded < 2 || Math.Abs(perTurn - rounded) > 1e-9)
            {
                throw new InvalidOperationException(
                    Name + " turns by " + RotationPerSymbolRadians.ToString(
                        "G6", CultureInfo.InvariantCulture) +
                    " radians a symbol, which is not a whole fraction of a turn. No power of the " +
                    "signal strips a rotation like that, so step 3 could not find its carrier.");
            }

            return LeastCommonMultiple(points, rounded);
        }

        private static int LeastCommonMultiple(int a, int b)
        {
            int first = a;
            int second = b;

            while (second != 0)
            {
                int remainder = first % second;

                first = second;
                second = remainder;
            }

            return a / first * b;
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
