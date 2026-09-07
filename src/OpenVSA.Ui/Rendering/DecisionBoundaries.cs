using System;
using System.Collections.Generic;
using OpenVSA.Demod.Results;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The decision-boundary overlay of <c>REQ-DEM-082</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The boundary is where the nearest ideal state changes, and nothing else.</strong>
    /// That is not a way of drawing it — it is what a decision boundary is for the
    /// minimum-distance decision the demodulator actually makes, so the overlay and the decision
    /// cannot disagree. Drawing a rectangular grid instead would be right for QAM and wrong for
    /// every constellation that is not a grid: the DVB rings, an APSK, and any user-defined
    /// constellation (<c>REQ-DEM-014</c>), which is exactly the case a user draws boundaries to
    /// look at.
    /// </para>
    /// <para>
    /// <strong>Cached, because it is the expensive one.</strong> Classifying every cell of the
    /// display against every ideal state is the display's largest piece of arithmetic — a 64-state
    /// constellation on a 400×400 graticule is ten million distance evaluations — and it is also
    /// the one that hardly ever changes: the ideal states are a property of the modulation and the
    /// scale of the display. So it is computed into a mask and reused until one of those moves.
    /// A caller who fixes the display scale computes it once for the life of the measurement;
    /// a caller who lets the scale follow the data pays for it whenever the data's reach changes.
    /// </para>
    /// <para>
    /// <strong>Equidistance, not a change of neighbour.</strong> A boundary cell is one where the
    /// two nearest states are within a cell of each other in distance. The obvious alternative —
    /// mark a cell whose neighbour to the right or below decides differently — was tried first and
    /// is biased: the boundary lies <em>between</em> two cells, so marking one of them puts the
    /// line half a cell to one side, and which side depends on how the tie at the exact boundary
    /// breaks. On QPSK that came out as a vertical arm through the correct column and a horizontal
    /// arm one row above it, from the same code, because the tie broke left for I and downward for
    /// Q. Equidistance has no tie to break: it is the definition of the boundary, it is symmetric
    /// about it, and it needs no neighbour scan.
    /// </para>
    /// <para>
    /// The width follows from the threshold. Moving a cell off a perpendicular bisector changes the
    /// difference of the two distances by twice the distance moved, so admitting a difference of
    /// one cell's width admits half a cell either side: one cell wide, at any angle, closed around
    /// every region, and wider only where three or more states meet — which is a vertex, and is
    /// where the decision really is that uncertain.
    /// </para>
    /// </remarks>
    public sealed class DecisionBoundaries
    {
        private bool[] _mask;
        private int _width;
        private int _height;
        private double _extent;
        private ConstellationPoint[] _states;

        /// <summary>How many cells the last computed boundary occupies.</summary>
        public int Cells { get; private set; }

        /// <summary>How many ideal states the last computed boundary separates.</summary>
        public int States { get; private set; }

        /// <summary>How many times the mask has been computed rather than reused.</summary>
        /// <remarks>
        /// Reported so a display, or a test, can tell a cache that is working from one that is
        /// recomputing every frame and merely looks the same.
        /// </remarks>
        public int Computations { get; private set; }

        /// <summary>
        /// Draws the boundaries, computing them if the display or the constellation has moved.
        /// </summary>
        /// <param name="surface">The surface to draw on.</param>
        /// <param name="area">The rectangle to draw in.</param>
        /// <param name="trace">The result, for its ideal states.</param>
        /// <param name="extent">The value at the edge of the area, in constellation units.</param>
        /// <param name="colour">What to draw the boundaries with.</param>
        /// <returns>How many cells were drawn.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public int Draw(
            PixelSurface surface, PixelRect area, SymbolTrace trace, double extent, PlotColor colour)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            ConstellationPoint[] states = ConstellationRasterizer.IdealStates(trace);

            if (states.Length < 2 || area.Width <= 1 || area.Height <= 1 || extent <= 0.0)
            {
                // One state decides everything, so there is no boundary. Drawing nothing is the
                // honest answer; a grid drawn anyway would be an assertion about a constellation
                // that has no interior edges.
                Cells = 0;
                States = states.Length;
                return 0;
            }

            if (!Reusable(area, extent, states))
            {
                Compute(area, extent, states);
            }

            for (int row = 0; row < _height; row++)
            {
                for (int column = 0; column < _width; column++)
                {
                    if (_mask[(row * _width) + column])
                    {
                        surface.SetPixel(area.X + column, area.Y + row, colour);
                    }
                }
            }

            return Cells;
        }

        /// <summary>Forgets the cached mask.</summary>
        public void Invalidate()
        {
            _mask = null;
            _states = null;
        }

        private bool Reusable(PixelRect area, double extent, ConstellationPoint[] states)
        {
            if (_mask == null || _width != area.Width || _height != area.Height ||
                _states == null || _states.Length != states.Length)
            {
                return false;
            }

            // The scale has to match to better than half a cell at the edge of the display, which
            // is the point at which a re-scaled boundary would land on a different cell from the
            // cached one. Anything tighter recomputes for a difference nobody can see; anything
            // looser draws the boundary in the wrong place.
            double tolerance = _extent / (area.Width - 1);

            if (Math.Abs(_extent - extent) > tolerance)
            {
                return false;
            }

            for (int state = 0; state < states.Length; state++)
            {
                if (_states[state].DistanceTo(states[state]) > 1.0e-12)
                {
                    return false;
                }
            }

            return true;
        }

        private void Compute(PixelRect area, double extent, ConstellationPoint[] states)
        {
            _width = area.Width;
            _height = area.Height;
            _extent = extent;
            _states = states;
            _mask = new bool[_width * _height];

            States = states.Length;
            Computations++;

            // One cell's width in constellation units. Admitting a difference of this much between
            // the two nearest states is what makes the drawn boundary one cell wide.
            double cellWidth = 2.0 * extent / (_width - 1);

            int cells = 0;

            for (int row = 0; row < _height; row++)
            {
                double q = QAt(row, extent, area);

                for (int column = 0; column < _width; column++)
                {
                    if (!OnBoundary(IAt(column, extent, area), q, states, cellWidth))
                    {
                        continue;
                    }

                    _mask[(row * _width) + column] = true;
                    cells++;
                }
            }

            Cells = cells;
        }

        /// <summary>The constellation value a column stands for.</summary>
        /// <remarks>
        /// The inverse of <see cref="ConstellationRasterizer.XFor"/>, so a boundary lands on the
        /// same cell as the symbols it separates. Written out rather than inverted by eye: the two
        /// disagreeing by half a cell is exactly the error this overlay exists to make visible.
        /// </remarks>
        private static double IAt(int column, double extent, PixelRect area) =>
            (((column / (double)(area.Width - 1)) * 2.0) - 1.0) * extent;

        /// <summary>The constellation value a row stands for.</summary>
        private static double QAt(int row, double extent, PixelRect area) =>
            ((((area.Height - 1 - row) / (double)(area.Height - 1)) * 2.0) - 1.0) * extent;

        /// <summary>
        /// Whether a point is within a cell of being equidistant from its two nearest states.
        /// </summary>
        /// <remarks>
        /// The two smallest are tracked in squared distance, which orders the same way and avoids a
        /// root per state, and only the two that survive are rooted — the difference of the roots
        /// is the quantity with a length, and the difference of the squares is not.
        /// </remarks>
        private static bool OnBoundary(
            double i, double q, ConstellationPoint[] states, double cellWidth)
        {
            double closest = double.MaxValue;
            double next = double.MaxValue;

            for (int state = 0; state < states.Length; state++)
            {
                double di = states[state].I - i;
                double dq = states[state].Q - q;
                double distance = (di * di) + (dq * dq);

                if (distance < closest)
                {
                    next = closest;
                    closest = distance;
                }
                else if (distance < next)
                {
                    next = distance;
                }
            }

            if (next == double.MaxValue)
            {
                return false;
            }

            return Math.Sqrt(next) - Math.Sqrt(closest) <= cellWidth;
        }
    }
}
