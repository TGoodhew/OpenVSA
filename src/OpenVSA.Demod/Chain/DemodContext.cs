using System;
using System.Collections.Generic;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain
{
    /// <summary>What one step hands to the next.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Internal, and deliberately so.</strong> The chain's steps are the only things that
    /// read and write this, and every field on it is a partly-demodulated intermediate whose
    /// meaning depends on how far down the order execution has got. Exposing it would publish a
    /// type whose invariants are "step 7 has run" — an API nobody could use correctly and nobody
    /// could change safely. What a caller gets is <see cref="DemodResult"/>, which is what the
    /// chain finished with.
    /// </para>
    /// <para>
    /// <strong>The waveform is replaced, not accumulated.</strong> Each stage of the signal —
    /// search window, working waveform, result window — is a separate field rather than one buffer
    /// overwritten in place, because the equaliser's re-entry re-runs step 8 on the result window
    /// and would otherwise be re-running it on whatever the last step happened to leave behind.
    /// </para>
    /// </remarks>
    internal sealed class DemodContext
    {
        private readonly List<string> _notices = new List<string>();

        internal DemodContext(
            float[] mainTime, double sampleRateHz, DemodSettings settings, ChainJournal journal)
        {
            MainTime = mainTime;
            SampleRateHz = sampleRateHz;
            Settings = settings;
            Journal = journal;
            Gain = 1.0;
        }

        /// <summary>What was asked for.</summary>
        internal DemodSettings Settings { get; }

        /// <summary>What has been done, and the thing that refuses to record it out of order.</summary>
        internal ChainJournal Journal { get; }

        /// <summary>Which pass over the chain is running, counting from one.</summary>
        internal int Pass { get; set; }

        /// <summary>The acquired record, interleaved, as it arrived.</summary>
        /// <remarks>
        /// Still <c>float</c>, and not widened on the way in. The estimators work in double
        /// precision, but they work on the Search Length window, and a record is not that window:
        /// <c>REQ-NFR-001</c>'s worked example is a 30-second capture at 25.6 MS/s, which is 6.1 GB
        /// as it stands. Widening it before step 1 has chosen a window would ask for 12.2 GB to
        /// throw most of it away, and step 1 converts what it keeps.
        /// </remarks>
        internal float[] MainTime { get; }

        /// <summary>The rate <see cref="MainTime"/> was sampled at.</summary>
        internal double SampleRateHz { get; }

        /// <summary>Step 1's window on Main Time.</summary>
        internal double[] Search { get; set; }

        /// <summary>Where <see cref="Search"/> starts in Main Time, in samples.</summary>
        internal int SearchStartSample { get; set; }

        /// <summary>Whether step 2 found a burst.</summary>
        internal bool BurstFound { get; set; }

        /// <summary>Where the burst starts within <see cref="Search"/>, in samples.</summary>
        internal int BurstStartSample { get; set; }

        /// <summary>How long the burst is, in samples.</summary>
        internal int BurstLengthSamples { get; set; }

        /// <summary>Step 3's estimate of the carrier offset, in hertz.</summary>
        internal double CoarseFrequencyHz { get; set; }

        /// <summary>The waveform at the internal processing rate, from step 4 onward.</summary>
        internal double[] Working { get; set; }

        /// <summary>
        /// Output samples per input sample in step 4's resampling, for converting a position found
        /// before it into one after it.
        /// </summary>
        internal double ResampleRatio { get; set; } = 1.0;

        /// <summary>Whether step 6 found the sync pattern.</summary>
        internal bool SyncFound { get; set; }

        /// <summary>
        /// Which sample of <see cref="Working"/> the sync pattern's first symbol falls on, when
        /// step 6 found one.
        /// </summary>
        internal int SyncSampleOffset { get; set; }

        /// <summary>Step 7's Result Length window, and what steps 8 to 14 work on.</summary>
        internal double[] Result { get; set; }

        /// <summary>Where <see cref="Result"/> starts within <see cref="Working"/>, in samples.</summary>
        internal int ResultStartSample { get; set; }

        /// <summary>How many symbols the Result Length window holds.</summary>
        internal int ResultSymbolCount { get; set; }

        /// <summary>
        /// The residual carrier frequency, in hertz, accumulated across the passes and on top of
        /// step 3's coarse correction.
        /// </summary>
        /// <remarks>
        /// Accumulated rather than replaced because the equaliser hands step 8 a waveform it has
        /// already derotated: each pass estimates what is left, and what the caller wants to know
        /// is the total the chain took out.
        /// </remarks>
        internal double ResidualFrequencyHz { get; set; }

        /// <summary>The carrier phase taken out, in radians, accumulated across the passes.</summary>
        internal double PhaseRadians { get; set; }

        /// <summary>The residual carrier frequency this pass alone estimated, in hertz.</summary>
        internal double PassFrequencyHz { get; set; }

        /// <summary>The carrier phase this pass alone estimated, in radians.</summary>
        internal double PassPhaseRadians { get; set; }

        /// <summary>The amplitude this pass alone estimated.</summary>
        internal double PassGain { get; set; } = 1.0;

        /// <summary>
        /// The symbol timing step 8 estimated: where the first symbol's decision instant falls
        /// within <see cref="Result"/>, in samples.
        /// </summary>
        internal double TimingSamples { get; set; }

        /// <summary>The amplitude step 8 estimated.</summary>
        internal double Gain { get; set; }

        /// <summary>What step 8's iteration did, on this pass.</summary>
        internal ConvergenceReport Convergence { get; set; }

        /// <summary>The measured point at each symbol instant, after step 8's corrections.</summary>
        internal Iq[] MeasuredSymbols { get; set; }

        /// <summary>Step 9's decided symbol values.</summary>
        internal int[] Symbols { get; set; }

        /// <summary>
        /// The symbol values the signal carried: the decided ones, or their differences when the
        /// decode is differential (<c>REQ-DEM-012</c>).
        /// </summary>
        internal int[] DataSymbols { get; set; }

        /// <summary>Step 9's detected bits, most significant first within each symbol.</summary>
        internal int[] Bits { get; set; }

        /// <summary>The ideal point for each decided symbol.</summary>
        internal Iq[] IdealSymbols { get; set; }

        /// <summary>Step 10's regenerated ideal waveform, at the internal processing rate.</summary>
        internal double[] IdealWaveform { get; set; }

        /// <summary>The equaliser's coefficients, or <c>null</c> before it has run.</summary>
        internal Iq[] EqualiserCoefficients { get; set; }

        /// <summary>Whether the equaliser changed its coefficients enough to ask for a re-entry.</summary>
        internal bool EqualiserUpdated { get; set; }

        /// <summary>Step 12's estimates.</summary>
        internal ImpairmentEstimate Impairments { get; set; }

        /// <summary>Step 13's error summary.</summary>
        internal ErrorSummary Summary { get; set; }

        /// <summary>Step 13's RMS EVM, as a percentage.</summary>
        internal double EvmPercent { get; set; }

        /// <summary>Step 14's result trace.</summary>
        internal SymbolTrace Trace { get; set; }

        /// <summary>
        /// Step 14's measured waveform, on the grid the symbols define rather than the one the
        /// acquisition happened to use.
        /// </summary>
        internal float[] TraceWaveform { get; set; }

        /// <summary>Step 14's reference waveform, on the same grid as <see cref="TraceWaveform"/>.</summary>
        internal float[] ReferenceWaveform { get; set; }

        /// <summary>How many symbols the result window holds.</summary>
        internal int SymbolCount => Symbols == null ? 0 : Symbols.Length;

        /// <summary>Anything the chain wants the caller to know, in the order it was noticed.</summary>
        internal IReadOnlyList<string> Notices => _notices;

        /// <summary>Records something the caller should be told.</summary>
        /// <param name="notice">What happened.</param>
        internal void Note(string notice)
        {
            if (!string.IsNullOrEmpty(notice))
            {
                _notices.Add(notice);
            }
        }

        /// <summary>The waveform a step is expected to have been given, or an explanatory throw.</summary>
        /// <param name="buffer">The buffer the previous step should have left.</param>
        /// <param name="step">The step that should have left it.</param>
        /// <param name="asker">The step asking for it.</param>
        /// <returns>The buffer.</returns>
        /// <exception cref="ChainOrderException">The buffer is missing.</exception>
        internal static double[] Require(double[] buffer, DemodStep step, DemodStep asker)
        {
            if (buffer == null)
            {
                throw new ChainOrderException(
                    "Step " + ProcessingOrder.NumberOf(asker) + " (" + asker + ") ran with " +
                    "nothing from step " + ProcessingOrder.NumberOf(step) + " (" + step +
                    "). The chain was executed out of order, or a step was left unregistered.");
            }

            return buffer;
        }
    }
}
