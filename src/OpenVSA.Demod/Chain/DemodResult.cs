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

        private readonly float[] _reference;
        private readonly ReadOnlyCollection<ConstellationPoint> _equaliser;

        internal DemodResult(
            SymbolTrace trace,
            ErrorSummary summary,
            int[] symbols,
            int[] bits,
            double carrierFrequencyErrorHz,
            ImpairmentEstimate impairments,
            IList<PassResult> passes,
            ChainJournal journal,
            IList<string> notices,
            float[] reference,
            IList<ConstellationPoint> equaliser)
        {
            _reference = reference ?? new float[0];
            _equaliser = equaliser == null
                ? null
                : new ReadOnlyCollection<ConstellationPoint>(equaliser);

            Trace = trace;
            Summary = summary;
            _symbols = new ReadOnlyCollection<int>(symbols ?? new int[0]);
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

        /// <summary>The decided symbol values.</summary>
        public IReadOnlyList<int> Symbols => _symbols;

        /// <summary>The detected bits, most significant first within each symbol.</summary>
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
