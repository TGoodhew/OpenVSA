using System.Collections.Generic;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// The equaliser's memory between measurements (<c>REQ-DEM-051</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the coefficients cannot live on the settings.</strong> Run and Hold are defined
    /// across successive measurements — Run "updates coefficients from the current measurement and
    /// applies them to the next", Hold freezes them — and a <see cref="DemodSettings"/> is built
    /// afresh for every measurement from the saved state. Coefficients kept there would be born
    /// empty each time and both modes would be indistinguishable from Reset. So the memory is a
    /// separate object with its own lifetime: whoever owns the measurement creates one and hands the
    /// same instance to every settings object it builds.
    /// </para>
    /// <para>
    /// <strong>It is deliberately not a settings value.</strong> Nothing here is a user choice; it is
    /// the result of past measurements. Saving a state file records the mode and the filter length
    /// (which are choices) and not these numbers (which are not) — recalling a setup should not
    /// restore an equaliser fitted to a channel that is no longer connected.
    /// </para>
    /// <para>
    /// This class is not thread-safe. One instance belongs to one measurement, and one measurement
    /// runs on one thread.
    /// </para>
    /// </remarks>
    public sealed class EqualiserState
    {
        private Iq[] _coefficients;

        /// <summary>Whether any coefficients have been fitted and kept.</summary>
        public bool IsAdapted => _coefficients != null;

        /// <summary>How many taps the held coefficients have, or zero when there are none.</summary>
        /// <remarks>
        /// Read by the equaliser to notice that the filter length changed under it: coefficients
        /// fitted for one tap count say nothing about another, so they are dropped rather than
        /// stretched.
        /// </remarks>
        public int Taps => _coefficients == null ? 0 : _coefficients.Length;

        /// <summary>The held coefficients, or <c>null</c> when there are none.</summary>
        public IReadOnlyList<ConstellationPoint> Coefficients
        {
            get
            {
                if (_coefficients == null)
                {
                    return null;
                }

                var points = new List<ConstellationPoint>(_coefficients.Length);

                foreach (Iq tap in _coefficients)
                {
                    points.Add(new ConstellationPoint(tap.I, tap.Q));
                }

                return points;
            }
        }

        /// <summary>Forgets the held coefficients, so the next measurement starts from nothing.</summary>
        public void Clear()
        {
            _coefficients = null;
        }

        /// <summary>The held coefficients when they are the length asked for, else <c>null</c>.</summary>
        /// <param name="taps">How many taps the current settings call for.</param>
        /// <returns>A copy, so the caller cannot write through to the held set.</returns>
        internal Iq[] Held(int taps)
        {
            if (_coefficients == null || _coefficients.Length != taps)
            {
                return null;
            }

            var copy = new Iq[taps];

            _coefficients.CopyTo(copy, 0);

            return copy;
        }

        /// <summary>Keeps a set of coefficients for the next measurement.</summary>
        /// <param name="coefficients">The taps, or <c>null</c> to forget what is held.</param>
        internal void Keep(Iq[] coefficients)
        {
            if (coefficients == null)
            {
                _coefficients = null;

                return;
            }

            var copy = new Iq[coefficients.Length];

            coefficients.CopyTo(copy, 0);

            _coefficients = copy;
        }
    }
}
