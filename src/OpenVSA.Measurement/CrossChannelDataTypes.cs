using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Hal;

namespace OpenVSA.Measurement
{
    /// <summary>
    /// The trace data types that need two phase-coherent channels (<c>REQ-DSP-040a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate enumeration from <see cref="TraceDataType"/>, and deliberately so. Everything in
    /// that one is a function of a single <c>IqBlock</c>; every member of this one is a function of
    /// <em>two</em> blocks acquired on a common timebase with a stated phase relationship. Putting
    /// them together would give a selector one list whose entries had two different availability
    /// rules and two different input shapes, and the difference would have to be rediscovered at
    /// every call site.
    /// </para>
    /// <para>
    /// <strong>These are P2 and unimplemented, which is the honest state to be in.</strong> No
    /// front end in Phase 3 provides phase-coherent two-channel acquisition, so there is nothing to
    /// compute them from. <c>REQ-DSP-040a</c> says as much and asks for one thing that <em>can</em>
    /// be settled now: that they are absent from the selectable set against a single-channel front
    /// end rather than present and erroring. That is what
    /// <see cref="CrossChannelDataTypes.AvailableFor"/> is.
    /// </para>
    /// <para>
    /// When a coherent source exists, <c>IqBlock</c> gains a channel index or the blocks are
    /// grouped into a set with a declared coherence guarantee, and the computations land behind
    /// this same availability rule.
    /// </para>
    /// </remarks>
    public enum CrossChannelDataType
    {
        /// <summary>The cross spectrum of the two channels.</summary>
        CrossSpectrum = 0,

        /// <summary>The cross correlation of the two channels.</summary>
        CrossCorrelation,

        /// <summary>Magnitude-squared coherence, from 0 to 1.</summary>
        Coherence,

        /// <summary>The frequency response from channel one to channel two.</summary>
        FrequencyResponse,

        /// <summary>The impulse response from channel one to channel two.</summary>
        ImpulseResponse,
    }

    /// <summary>
    /// Which cross-channel data types a front end can offer (<c>REQ-DSP-040a</c>).
    /// </summary>
    public static class CrossChannelDataTypes
    {
        /// <summary>Every cross-channel type, in the order a selector would offer them.</summary>
        public static IReadOnlyList<CrossChannelDataType> All { get; } =
            new ReadOnlyCollection<CrossChannelDataType>(
                (CrossChannelDataType[])Enum.GetValues(typeof(CrossChannelDataType)));

        /// <summary>The display name of a cross-channel data type.</summary>
        /// <param name="type">The data type.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known type.</exception>
        public static string Describe(CrossChannelDataType type)
        {
            switch (type)
            {
                case CrossChannelDataType.CrossSpectrum: return "Cross Spectrum";
                case CrossChannelDataType.CrossCorrelation: return "Cross Correlation";
                case CrossChannelDataType.Coherence: return "Coherence";
                case CrossChannelDataType.FrequencyResponse: return "Frequency Response";
                case CrossChannelDataType.ImpulseResponse: return "Impulse Response";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(type), type, "Not a known cross-channel data type.");
            }
        }

        /// <summary>
        /// Whether a front end can supply these at all.
        /// </summary>
        /// <param name="capabilities">What the front end declares.</param>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        /// <remarks>
        /// Two channels and a common timebase, both declared. Either alone is not enough: two
        /// channels digitised independently give a cross spectrum whose phase is whatever the two
        /// clocks happened to be doing, which is a number with no meaning and no obvious symptom.
        /// </remarks>
        public static bool IsSupportedBy(IFrontEndCapabilities capabilities)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            return capabilities.ChannelCount >= 2 && capabilities.SupportsPhaseCoherentChannels;
        }

        /// <summary>
        /// The cross-channel types a front end may be asked for — every one, or none.
        /// </summary>
        /// <param name="capabilities">What the front end declares.</param>
        /// <returns>An empty list against a single-channel or incoherent source.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        /// <remarks>
        /// <c>REQ-DSP-040a</c>'s criterion: "against a single-channel front end they are absent
        /// from the selectable set rather than present and erroring". Absent is the whole point. A
        /// greyed entry says the feature exists and this instrument cannot do it; an entry that
        /// throws when picked says nothing until it is too late. These are all-or-nothing because
        /// they all rest on the same guarantee — a source that can supply one can supply all five.
        /// </remarks>
        public static IReadOnlyList<CrossChannelDataType> AvailableFor(
            IFrontEndCapabilities capabilities) =>
            IsSupportedBy(capabilities)
                ? All
                : new ReadOnlyCollection<CrossChannelDataType>(new CrossChannelDataType[0]);

        /// <summary>
        /// Why a front end cannot offer these, for a user who expected them.
        /// </summary>
        /// <param name="capabilities">What the front end declares.</param>
        /// <returns>An empty string when they are available.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        /// <remarks>
        /// Absent from the list is right for the list, and useless to someone who came looking.
        /// The two failing conditions read very differently to a user — one is "you need a
        /// different instrument", the other is "your instrument has the inputs but will not
        /// promise they share a clock" — so they are not collapsed into one sentence.
        /// </remarks>
        public static string ExplainUnavailability(IFrontEndCapabilities capabilities)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            if (IsSupportedBy(capabilities))
            {
                return string.Empty;
            }

            if (capabilities.ChannelCount < 2)
            {
                return "Cross-channel measurements compare two inputs, and this source has " +
                       (capabilities.ChannelCount == 1 ? "one" : capabilities.ChannelCount.ToString(
                           System.Globalization.CultureInfo.CurrentCulture)) + ".";
            }

            return "This source has " + capabilities.ChannelCount.ToString(
                       System.Globalization.CultureInfo.CurrentCulture) +
                   " inputs but does not declare them phase coherent, so a cross-channel phase " +
                   "would be a measurement of the two clocks rather than of the signal.";
        }
    }
}
