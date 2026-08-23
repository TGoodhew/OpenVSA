using System;
using System.Collections.Generic;
using OpenVSA.Demod.Results;
using OpenVSA.Synthesis;

namespace OpenVSA.TestHarness.Synthesis
{
    /// <summary>
    /// Turns a generated burst into the result type a demodulator would have produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is not on <see cref="SyntheticBurst"/> itself.</strong> It was, until the
    /// generator moved into <c>OpenVSA.Synthesis</c> — an assembly that references
    /// <c>OpenVSA.Core</c> and nothing else, so that a transport may use it without an analysis
    /// assembly appearing underneath it and so that the analysis stack's own tests may use it
    /// without a transport appearing inside it. <c>SymbolTrace</c> lives in <c>OpenVSA.Demod</c>,
    /// and a generator that referenced the demodulator would give up both of those properties for
    /// one convenience method.
    /// </para>
    /// <para>
    /// <strong>This is the bridge the display group stands on.</strong> The burst knows what was
    /// sent and where; a <c>SymbolTrace</c> is what a demodulator produces and what the displays
    /// draw. Every display criterion could therefore be checked against a known signal before there
    /// was a demodulator, and those displays take a real one's output unchanged now that there is.
    /// </para>
    /// </remarks>
    public static class BurstResults
    {
        /// <summary>
        /// The burst as a demodulated result, for the displays of <c>REQ-UI-050</c> onwards.
        /// </summary>
        /// <param name="burst">The generated burst.</param>
        /// <param name="modulationTypes">
        /// Which modulation each symbol belongs to, for exercising <c>REQ-UI-050</c>'s
        /// mixed-modulation colouring, or <c>null</c> when they all agree.
        /// </param>
        /// <returns>The result a demodulator would have produced from this burst.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="burst"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// A modulation-type list is given whose length is not the symbol count.
        /// </exception>
        public static SymbolTrace ToSymbolTrace(
            this SyntheticBurst burst, IList<int> modulationTypes = null)
        {
            if (burst == null)
            {
                throw new ArgumentNullException(nameof(burst));
            }

            var ideal = new List<ConstellationPoint>(burst.Symbols.Count);
            var measured = new List<ConstellationPoint>(burst.Symbols.Count);

            for (int symbol = 0; symbol < burst.Symbols.Count; symbol++)
            {
                SymbolPoint want = burst.Scheme.IdealPoints[burst.Symbols[symbol]];
                SymbolPoint got = burst.MeasuredAt(symbol);

                ideal.Add(new ConstellationPoint(want.I, want.Q));
                measured.Add(new ConstellationPoint(got.I, got.Q));
            }

            var samples = new float[burst.SampleCount * 2];

            burst.Samples.CopyTo(new Span<float>(samples));

            return new SymbolTrace(
                burst.Scheme.Name,
                burst.Scheme.BitsPerSymbol,
                burst.Scheme.LevelsPerAxis,
                new List<int>(burst.Symbols),
                ideal,
                measured,
                new List<int>(burst.DecisionSampleIndices),
                samples,
                burst.SamplesPerSymbol,
                burst.SymbolRateHz,
                modulationTypes);
        }
    }
}
