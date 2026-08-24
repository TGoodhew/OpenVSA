using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Demod.Results
{
    /// <summary>
    /// Something that stops a demodulation locking (<c>REQ-DEM-036</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The order of the members is the requirement's order of likelihood</strong>, and it is
    /// the reason this is an enumeration rather than a set of booleans: the requirement asks for the
    /// causes "in documented order of likelihood", and an enumeration whose values are declared in
    /// that order can be sorted by it without a second table saying what the order is.
    /// </para>
    /// <para>
    /// They are not exclusive. One fault often produces another — a symbol rate that is wrong by
    /// enough will also make the filter look wrong, because the filter is built at the rate that was
    /// supplied — and reporting the second as well as the first is more honest than picking one.
    /// </para>
    /// </remarks>
    public enum LockFault
    {
        /// <summary>The symbol rate supplied is not the rate the signal is running at.</summary>
        /// <remarks>
        /// First, because it is the one quantity the chain takes on trust. <c>REQ-DEM-030</c> makes
        /// the symbol rate an input rather than an estimate, so nothing else in the chain will
        /// notice that it is wrong; every other setting is either measured or checked somewhere.
        /// </remarks>
        SymbolRate,

        /// <summary>The measurement filter does not match the transmitter's shaping.</summary>
        Filter,

        /// <summary>The signal is further off centre than lock allows.</summary>
        CentreFrequency,

        /// <summary>The Result Length is too short for the format to be fitted in.</summary>
        ResultLength,
    }

    /// <summary>
    /// Whether a demodulation locked, and if it did not, what the signal says about why
    /// (<c>REQ-DEM-036</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>"Demodulation failed" is not a diagnosis.</strong> The requirement names four causes
    /// and asks that each of them, injected deliberately, produce "the corresponding diagnostic
    /// rather than a bare 'demodulation failed'". So each of the four is measured rather than
    /// listed: a list of what it might be is what a user can already guess, and which of them the
    /// signal supports is what they cannot.
    /// </para>
    /// <para>
    /// <strong>The measurements are here whether or not it locked.</strong> The four numbers —
    /// the symbol rate read off the signal, the bandwidth it occupies, the bandwidth the chosen
    /// filter passes, and how far off centre it sits — are worth having on a measurement that
    /// worked, and having them either way is what makes a marginal one legible.
    /// </para>
    /// <para>
    /// <strong>They are taken from the acquired window, not from the fit.</strong> The fit is the
    /// thing that failed; a rate or an offset read out of a decision-directed loop that converged
    /// onto wrong decisions would be evidence about the loop rather than about the signal.
    /// </para>
    /// </remarks>
    public sealed class LockReport
    {
        private readonly ReadOnlyCollection<LockFault> _causes;

        internal LockReport(
            bool locked,
            double evmPercent,
            IList<LockFault> causes,
            string explanation,
            double measuredSymbolRateHz,
            double occupiedBandwidthHz,
            double filterBandwidthHz,
            double centreOffsetHz,
            double residualOffsetHz,
            double centreToleranceHz)
        {
            Locked = locked;
            EvmPercent = evmPercent;
            _causes = new ReadOnlyCollection<LockFault>(causes ?? new List<LockFault>());
            Explanation = explanation ?? string.Empty;
            MeasuredSymbolRateHz = measuredSymbolRateHz;
            OccupiedBandwidthHz = occupiedBandwidthHz;
            FilterBandwidthHz = filterBandwidthHz;
            CentreOffsetHz = centreOffsetHz;
            ResidualOffsetHz = residualOffsetHz;
            CentreToleranceHz = centreToleranceHz;
        }

        /// <summary>Whether the demodulation locked.</summary>
        public bool Locked { get; }

        /// <summary>The RMS EVM the judgement was made on, as a percentage.</summary>
        public double EvmPercent { get; }

        /// <summary>
        /// What the signal says is wrong, in <see cref="LockFault"/>'s order of likelihood.
        /// </summary>
        /// <remarks>Empty when it locked, and empty when it did not lock for none of these reasons —
        /// which is itself a finding, and one <see cref="Explanation"/> states.</remarks>
        public IReadOnlyList<LockFault> Causes => _causes;

        /// <summary>What to tell the user, in sentences.</summary>
        /// <remarks>Empty when the demodulation locked.</remarks>
        public string Explanation { get; }

        /// <summary>
        /// The symbol rate read off the signal itself, in hertz, or zero when it could not be read.
        /// </summary>
        /// <remarks>
        /// From the symbol-rate line in the signal's squared envelope — the quantity a square-law
        /// timing estimator uses — rather than from the chain's timing fit. It exists in any format
        /// with excess bandwidth, and it does not care about the carrier, the filter or the
        /// decisions.
        /// </remarks>
        public double MeasuredSymbolRateHz { get; }

        /// <summary>The width the signal occupies, in hertz, or zero when it could not be measured.</summary>
        /// <remarks>
        /// Ninety-nine per cent of its power with the noise floor taken out, measured on the acquired
        /// window before any correction.
        /// </remarks>
        public double OccupiedBandwidthHz { get; }

        /// <summary>The width the configured measurement filter passes, on the same definition.</summary>
        /// <remarks>
        /// Measured from the filter's own taps rather than from a formula per filter type, so that
        /// the comparison with <see cref="OccupiedBandwidthHz"/> is like with like — including the
        /// truncation and the taper the chain actually applies. A matched pair reads the same
        /// number, whatever the filter is.
        /// </remarks>
        public double FilterBandwidthHz { get; }

        /// <summary>How far off centre the signal sat when it arrived, in hertz.</summary>
        /// <remarks>
        /// What step 3 took out plus what it left, which is where the signal was before anything was
        /// done to it. Informational: a signal can arrive a long way off centre and demodulate
        /// perfectly, and <see cref="ResidualOffsetHz"/> is the half of this that decides whether it
        /// does.
        /// </remarks>
        public double CentreOffsetHz { get; }

        /// <summary>What step 3 left behind, in hertz.</summary>
        /// <remarks>
        /// <para>
        /// Read directly: step 3 derotates the search window in place, so the centre of what is left
        /// in it is the offset step 8 was asked to pull in.
        /// </para>
        /// <para>
        /// <strong>This is the quantity <c>REQ-DEM-036</c>'s tolerance applies to</strong>, and not
        /// <see cref="CentreOffsetHz"/>. Step 3 estimates the offset over the whole block and takes
        /// it out before anything else runs, so an acquisition centred a long way off is corrected
        /// and demodulates perfectly well; what step 8 has to pull in is what step 3 left. Judging
        /// the raw offset against ±10 % of the symbol rate would accuse the centre frequency of
        /// every failure on a signal that was tuned loosely and handled fine.
        /// </para>
        /// <para>
        /// It is large when step 3's estimate was captured by a spur, when the offset was outside
        /// the range raising the signal to its symmetry can distinguish, or when step 3 declined to
        /// estimate at all.
        /// </para>
        /// </remarks>
        public double ResidualOffsetHz { get; }

        /// <summary>How much <see cref="ResidualOffsetHz"/> is allowed to be, in hertz.</summary>
        /// <remarks><c>REQ-DEM-036</c>'s "roughly ±10 % of the symbol rate".</remarks>
        public double CentreToleranceHz { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            if (Locked)
            {
                return "locked, EVM " +
                    EvmPercent.ToString("G4", CultureInfo.InvariantCulture) + " %rms";
            }

            if (_causes.Count == 0)
            {
                return "not locked, EVM " +
                    EvmPercent.ToString("G4", CultureInfo.InvariantCulture) +
                    " %rms, none of the four usual causes";
            }

            var named = new string[_causes.Count];

            for (int cause = 0; cause < _causes.Count; cause++)
            {
                named[cause] = _causes[cause].ToString();
            }

            return "not locked, EVM " +
                EvmPercent.ToString("G4", CultureInfo.InvariantCulture) + " %rms: " +
                string.Join(", ", named);
        }
    }
}
