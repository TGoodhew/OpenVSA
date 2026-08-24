using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using OpenVSA.Demod.Results;

namespace OpenVSA.Demod.Chain
{
    /// <summary>What one pass over the chain produced.</summary>
    /// <remarks>
    /// A pass is a complete result, not a partial one: steps 12 to 14 run on every pass, so the
    /// equaliser's second pass can be compared with its first on the quantity that matters. That is
    /// what makes <c>REQ-DEM-001</c>'s "a signal whose EVM improves on the second pass" a reading
    /// of the chain's own output rather than something a test has to instrument the chain to see.
    /// </remarks>
    public sealed class PassResult
    {
        internal PassResult(int pass, ConvergenceReport convergence, double evmPercent, bool equalised)
        {
            Pass = pass;
            Convergence = convergence;
            EvmPercent = evmPercent;
            Equalised = equalised;
        }

        /// <summary>Which pass this was, counting from one.</summary>
        public int Pass { get; }

        /// <summary>What step 8's iteration did on this pass.</summary>
        public ConvergenceReport Convergence { get; }

        /// <summary>The RMS EVM this pass ended with, as a percentage.</summary>
        public double EvmPercent { get; }

        /// <summary>Whether the equaliser ran on this pass.</summary>
        public bool Equalised { get; }

        /// <inheritdoc />
        public override string ToString() =>
            "pass " + Pass.ToString(CultureInfo.InvariantCulture) + ": EVM " +
            EvmPercent.ToString("G4", CultureInfo.InvariantCulture) + " %rms, " + Convergence;
    }

    /// <summary>
    /// What a demodulation finished with: the traces, the metrics, and an account of how it got
    /// there.
    /// </summary>
    /// <remarks>
    /// <strong>The account is part of the result.</strong> <see cref="Journal"/> says which steps
    /// ran, in what order and on which pass; <see cref="Passes"/> says what each pass achieved and
    /// whether its iteration converged; <see cref="Notices"/> carries what the chain wants said out
    /// loud. <c>REQ-DEM-001</c> asks for a bound that is "reported rather than silently accepted",
    /// and a report nobody is handed is not one.
    /// </remarks>
    public sealed class DemodResult
    {
        private readonly ReadOnlyCollection<PassResult> _passes;
        private readonly ReadOnlyCollection<string> _notices;
        private readonly ReadOnlyCollection<int> _bits;
        private readonly ReadOnlyCollection<int> _symbols;
        private readonly ReadOnlyCollection<int> _dataSymbols;

        private readonly float[] _reference;
        private readonly ReadOnlyCollection<ConstellationPoint> _equaliser;

        internal DemodResult(
            SymbolTrace trace,
            ErrorSummary summary,
            int[] symbols,
            int[] dataSymbols,
            int[] bits,
            double carrierFrequencyErrorHz,
            ImpairmentEstimate impairments,
            IList<PassResult> passes,
            ChainJournal journal,
            IList<string> notices,
            float[] reference,
            IList<ConstellationPoint> equaliser,
            LockReport lockReport,
            MeasurementProvenance provenance,
            ChannelResponse channel)
        {
            Lock = lockReport;
            Provenance = provenance;
            ChannelResponse = channel;

            _reference = reference ?? new float[0];
            _equaliser = equaliser == null
                ? null
                : new ReadOnlyCollection<ConstellationPoint>(equaliser);

            Trace = trace;
            Summary = summary;
            _symbols = new ReadOnlyCollection<int>(symbols ?? new int[0]);
            _dataSymbols = new ReadOnlyCollection<int>(dataSymbols ?? symbols ?? new int[0]);
            _bits = new ReadOnlyCollection<int>(bits ?? new int[0]);
            CarrierFrequencyErrorHz = carrierFrequencyErrorHz;
            Impairments = impairments;
            Journal = journal;
            _passes = new ReadOnlyCollection<PassResult>(passes);
            _notices = new ReadOnlyCollection<string>(notices);
        }

        /// <summary>The result trace: the constellation, the symbols and the samples behind them.</summary>
        public SymbolTrace Trace { get; }

        /// <summary>
        /// The waveform quality factor (<c>REQ-DEM-068</c>), or <c>NaN</c> when there was nothing to
        /// compute it from.
        /// </summary>
        /// <remarks>
        /// One for a perfect match and never more. It is not a row of <see cref="Summary"/> because
        /// <c>REQ-UI-053</c> fixes the label set and rho is not in it; where it belongs on screen is
        /// <c>REQ-DEM-070</c>'s question.
        /// </remarks>
        public double Rho => Summary == null ? double.NaN : Summary.Rho;

        /// <summary>
        /// Whether the demodulation locked, and what the signal says about why it did not
        /// (<c>REQ-DEM-036</c>).
        /// </summary>
        /// <remarks>
        /// Present whether or not it locked, because the four quantities it is built on -- the
        /// signal's own symbol rate, the bandwidth it occupies, the bandwidth the filter passes and
        /// how far off centre it sits -- are worth reading on a measurement that worked. When it did
        /// not lock, <see cref="LockReport.Explanation"/> is also among the
        /// <see cref="Notices"/>.
        /// </remarks>
        public LockReport Lock { get; }

        /// <summary>
        /// What was in force when these metrics were computed (<c>REQ-DEM-072</c>).
        /// </summary>
        /// <remarks>
        /// The normalisation reference, both filters with their parameters, and the state of every
        /// compensation. It travels with the result because a number recalled without its context is
        /// the failure the requirement prevents — and because it is built in the same pass as the
        /// metrics, a display that has one has the other.
        /// </remarks>
        public MeasurementProvenance Provenance { get; }

        /// <summary>
        /// The channel the equaliser found, or <c>null</c> when the equaliser did not run
        /// (<c>REQ-DEM-053</c>).
        /// </summary>
        /// <remarks>
        /// The regularised inverse of <see cref="EqualiserCoefficients"/>, which is the impulse
        /// response of the equaliser itself. Null rather than empty when there is no equaliser, for
        /// the same reason the coefficients are: a trace that does not exist is a different thing
        /// from one with no data in it.
        /// </remarks>
        public ChannelResponse ChannelResponse { get; }

        /// <summary>
        /// Step 10's regenerated ideal waveform, on the same grid as <see cref="Trace"/>'s samples.
        /// </summary>
        /// <remarks>
        /// The waveform a perfect transmitter would have sent, given the symbols that were decided:
        /// <c>REQ-DEM-080</c>'s IQ Reference Time, and what the error vector is a difference from.
        /// Interleaved real and imaginary, as bulk samples are carried everywhere else.
        /// </remarks>
        public ReadOnlySpan<float> ReferenceWaveform => new ReadOnlySpan<float>(_reference);

        /// <summary>
        /// The equaliser's coefficients, or <c>null</c> when the equaliser did not run.
        /// </summary>
        /// <remarks>
        /// Null rather than empty, because <c>REQ-DEM-080</c> asks that the traces depending on the
        /// equaliser be "unavailable rather than empty when the equaliser is off". An empty list is
        /// a trace with no data in it; nothing is a trace that does not exist.
        /// </remarks>
        public IReadOnlyList<ConstellationPoint> EqualiserCoefficients => _equaliser;

        /// <summary>The error summary, as <c>REQ-UI-053</c> lays it out.</summary>
        public ErrorSummary Summary { get; }

        /// <summary>The symbol decided at each symbol instant.</summary>
        /// <remarks>
        /// One per point of <see cref="Trace"/>'s constellation, and what a display draws. For a
        /// differentially decoded measurement this is <em>not</em> the data — see
        /// <see cref="DataSymbols"/>, which is shorter by the reference symbol.
        /// </remarks>
        public IReadOnlyList<int> Symbols => _symbols;

        /// <summary>
        /// The symbol values the signal carried (<c>REQ-DEM-012</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same list as <see cref="Symbols"/> for every format that carries its bits in the
        /// symbol itself. For a differential decode it is the change from each symbol to the next,
        /// so it is one shorter: the first symbol of the window is the reference and carries no
        /// data.
        /// </para>
        /// <para>
        /// This is the list to compare against a transmitted sequence, and <see cref="Symbols"/> is
        /// not — they are the same numbers only when the reference is
        /// <see cref="DifferentialReference.None"/>. <see cref="Bits"/> is always this list's bits.
        /// </para>
        /// </remarks>
        public IReadOnlyList<int> DataSymbols => _dataSymbols;

        /// <summary>
        /// The detected bits, most significant first within each symbol, of
        /// <see cref="DataSymbols"/>.
        /// </summary>
        public IReadOnlyList<int> Bits => _bits;

        /// <summary>
        /// The carrier frequency error: step 3's coarse estimate and step 8's residual together.
        /// </summary>
        public double CarrierFrequencyErrorHz { get; }

        /// <summary>Step 12's impairment estimates.</summary>
        public ImpairmentEstimate Impairments { get; }

        /// <summary>What each pass produced, in order.</summary>
        public IReadOnlyList<PassResult> Passes => _passes;

        /// <summary>Which steps ran, in what order, on which pass.</summary>
        public ChainJournal Journal { get; }

        /// <summary>Anything the chain wants the caller to know.</summary>
        public IReadOnlyList<string> Notices => _notices;

        /// <summary>The RMS EVM the last pass ended with, as a percentage.</summary>
        public double EvmPercent =>
            _passes.Count == 0 ? 0.0 : _passes[_passes.Count - 1].EvmPercent;

        /// <summary>What step 8's iteration did on the last pass.</summary>
        public ConvergenceReport Convergence =>
            _passes.Count == 0 ? null : _passes[_passes.Count - 1].Convergence;

        /// <summary>Whether every pass's iteration met the convergence criterion.</summary>
        public bool Converged
        {
            get
            {
                foreach (PassResult pass in _passes)
                {
                    if (pass.Convergence == null || !pass.Convergence.Converged)
                    {
                        return false;
                    }
                }

                return _passes.Count > 0;
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            (Trace == null ? "no trace" : Trace.SymbolCount + " symbols") + ", EVM " +
            EvmPercent.ToString("G4", CultureInfo.InvariantCulture) + " %rms over " +
            _passes.Count.ToString(CultureInfo.InvariantCulture) + " pass(es)";
    }
}
