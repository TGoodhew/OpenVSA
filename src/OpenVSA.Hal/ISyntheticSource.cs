using System.Collections.Generic;

namespace OpenVSA.Hal
{
    /// <summary>
    /// A front end that makes its own signal, and can be told what to make (<c>REQ-SIM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Optional, and implemented by one kind of thing.</strong> A front end that acquires
    /// from an instrument, a file or a network has nothing to answer here and does not implement
    /// this. The shell asks for it and offers what it finds; everything else in the product goes on
    /// treating every front end alike, which is <c>REQ-ARC-002</c>'s "the DSP and measurement layers
    /// shall be incapable of distinguishing a live instrument from a file or simulator". Only the
    /// shell ever asks, and only so that a person with no instrument has something to look at.
    /// </para>
    /// <para>
    /// <strong>Synthetic, not source, and the word is doing work.</strong> Driving a real signal
    /// generator is <c>IStimulusSource</c>'s job, in the test harness, and that interface's own
    /// remarks explain why it is deliberately not part of the HAL: a generator reachable through the
    /// measurement path could feed synthesised data straight into the DSP without crossing an
    /// instrument, which is the shortcut the harness exists to prevent. This interface is not that.
    /// It does not drive anything: it tells a front end that is <em>already</em> inventing its
    /// samples what to invent. A front end with an instrument behind it must never implement it, and
    /// a bench generator must never be reached through it.
    /// </para>
    /// <para>
    /// <strong>Ranged from the declaration, per <c>REQ-HAL-002</c>.</strong>
    /// <see cref="Modulations"/> is what a UI offers and <see cref="MinimumSamplesPerSymbol"/> is
    /// what it ranges a symbol rate against, because the fastest symbol rate a synthetic source can
    /// carry is a property of the sample rate the acquisition negotiated rather than of anything the
    /// UI may assume.
    /// </para>
    /// </remarks>
    public interface ISyntheticSource
    {
        /// <summary>
        /// The modulations this source can produce, by name; never null and never empty.
        /// </summary>
        /// <remarks>
        /// The names a UI offers. A source that could produce nothing would not implement this
        /// interface, so an empty list is a defect rather than a state to render.
        /// </remarks>
        IReadOnlyList<string> Modulations { get; }

        /// <summary>
        /// The fewest samples per symbol this source will shape a symbol over.
        /// </summary>
        /// <remarks>
        /// A UI divides the negotiated sample rate by this to know the fastest symbol rate it may
        /// offer. Below it there is no pulse to speak of, and a source that accepted the setting
        /// anyway would be producing something it could not have transmitted.
        /// </remarks>
        double MinimumSamplesPerSymbol { get; }

        /// <summary>
        /// What to transmit, by name, or <c>null</c> for an unmodulated carrier.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// A name that is not one of <see cref="Modulations"/>.
        /// </exception>
        string Modulation { get; set; }

        /// <summary>The symbol rate to transmit at, in hertz.</summary>
        double SymbolRateHz { get; set; }

        /// <summary>The transmit pulse's roll-off, from 0 to 1.</summary>
        double RollOff { get; set; }
    }
}
