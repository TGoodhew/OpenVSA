using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Demod.Results
{
    /// <summary>
    /// The context a set of metrics was measured in (<c>REQ-DEM-072</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An EVM figure without this is not a measurement, it is a number.</strong> The
    /// requirement's own rationale, and it is not an abstract worry: two of the settings recorded
    /// here move the reported EVM by more than most real impairments do. The normalisation reference
    /// changes it by a factor of 1.53 on 64-QAM, and a measurement filter that does not match the
    /// transmitter's shaping can move it by an order of magnitude. Disagreements between instruments
    /// almost always turn out to be one of these rather than a difference in the signal.
    /// </para>
    /// <para>
    /// <strong>It is built from the same settings, in the same pass, as the metrics.</strong> That
    /// is what makes "the two can never disagree" structural rather than a matter of remembering to
    /// update both: there is one <see cref="DemodResult"/> per run and it carries both, so a display
    /// that shows the metrics has already been handed the provenance that qualifies them.
    /// </para>
    /// </remarks>
    public sealed class MeasurementProvenance
    {
        private readonly ReadOnlyCollection<string> _lines;

        internal MeasurementProvenance(
            EvmReference normalisation,
            string measurementFilter,
            string referenceFilter,
            int filterSymbolSpan,
            bool equaliserOn,
            bool mirrorSpectrum,
            bool burstSearchOn,
            bool syncSearchOn)
        {
            Normalisation = normalisation;
            MeasurementFilter = measurementFilter ?? string.Empty;
            ReferenceFilter = referenceFilter ?? string.Empty;
            FilterSymbolSpan = filterSymbolSpan;
            EqualiserOn = equaliserOn;
            MirrorSpectrum = mirrorSpectrum;
            BurstSearchOn = burstSearchOn;
            SyncSearchOn = syncSearchOn;

            _lines = new ReadOnlyCollection<string>(Build());
        }

        /// <summary>What the percentages are a percentage of, or <c>null</c>.</summary>
        public EvmReference Normalisation { get; }

        /// <summary>The measurement filter and its parameters, as one phrase.</summary>
        public string MeasurementFilter { get; }

        /// <summary>The reference filter and its parameters, as one phrase.</summary>
        public string ReferenceFilter { get; }

        /// <summary>How many symbols either side of centre both filters span.</summary>
        public int FilterSymbolSpan { get; }

        /// <summary>Whether the equaliser ran.</summary>
        public bool EqualiserOn { get; }

        /// <summary>
        /// Whether the origin offset was taken out of the signal before the metrics were computed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Always false in this build, and that is the honest answer rather than a missing
        /// one.</strong> The chain measures the origin offset (<c>REQ-DEM-066</c>) and reports it;
        /// nothing removes it, because no requirement asks for a compensation that does. So the EVM
        /// reported here includes whatever carrier feedthrough the signal carries, which is a
        /// materially different number from the one an analyser that subtracts it first would show
        /// — and is exactly the sort of difference <c>REQ-DEM-072</c> exists to make visible.
        /// </para>
        /// <para>
        /// It is a property rather than a constant so that the day a compensation is added, the
        /// provenance already has somewhere to say so.
        /// </para>
        /// </remarks>
        public bool IqOffsetRemoved => false;

        /// <summary>Whether the input was conjugated before analysis (<c>REQ-DEM-035</c>).</summary>
        public bool MirrorSpectrum { get; }

        /// <summary>Whether the burst search positioned the window.</summary>
        public bool BurstSearchOn { get; }

        /// <summary>Whether the sync search positioned the window.</summary>
        public bool SyncSearchOn { get; }

        /// <summary>The provenance as the lines a display shows beneath the metrics.</summary>
        /// <remarks>
        /// One statement per line, each naming a setting and its value rather than only the ones
        /// that are on: "equaliser off" is as much a part of an EVM figure's meaning as "equaliser
        /// on", and a display that listed only what was enabled would leave a reader to remember
        /// what the absent lines would have said.
        /// </remarks>
        public IReadOnlyList<string> Lines => _lines;

        /// <inheritdoc />
        public override string ToString() => string.Join("; ", new List<string>(_lines).ToArray());

        private List<string> Build()
        {
            var lines = new List<string>
            {
                Normalisation == null
                    ? "Referenced to the RMS magnitude of the reference constellation."
                    : Normalisation.Describe(),

                "Measurement filter: " + MeasurementFilter + ", spanning " +
                    FilterSymbolSpan.ToString(CultureInfo.InvariantCulture) +
                    " symbols either side of centre.",

                "Reference filter: " + ReferenceFilter + ".",

                "Equaliser " + OnOff(EqualiserOn) + "; IQ origin offset " +
                    (IqOffsetRemoved ? "removed" : "measured but not removed") +
                    "; spectrum " + (MirrorSpectrum ? "mirrored" : "not mirrored") + ".",
            };

            if (BurstSearchOn || SyncSearchOn)
            {
                lines.Add(
                    "Window positioned by " +
                    (BurstSearchOn && SyncSearchOn
                        ? "the burst search and the sync pattern"
                        : BurstSearchOn ? "the burst search" : "the sync pattern") + ".");
            }

            return lines;
        }

        private static string OnOff(bool on) => on ? "on" : "off";
    }
}
