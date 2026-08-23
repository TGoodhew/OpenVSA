using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Demod.Results
{
    /// <summary>
    /// The family a modulation format belongs to, which is what decides whether a metric means
    /// anything for it.
    /// </summary>
    /// <remarks>
    /// Families rather than formats, because the applicability rules are about the shape of the
    /// modulation and not about a particular constellation: magnitude error is meaningless for
    /// every FSK there is, not for 2FSK in particular. <c>REQ-DEM-010</c>'s catalogue is the list of
    /// formats; each of them names one of these.
    /// </remarks>
    public enum ModulationFamily
    {
        /// <summary>Phase-shift keying: BPSK, QPSK, 8PSK and their differential and offset kin.</summary>
        Psk = 0,

        /// <summary>Quadrature amplitude modulation, including DVB-QAM and Star QAM.</summary>
        Qam,

        /// <summary>Amplitude and phase-shift keying on rings.</summary>
        Apsk,

        /// <summary>Frequency-shift keying.</summary>
        Fsk,

        /// <summary>Minimum-shift keying and its Gaussian-filtered relatives.</summary>
        Msk,

        /// <summary>Vestigial sideband.</summary>
        Vsb,

        /// <summary>Amplitude-shift keying, on-off keying included.</summary>
        Ask,

        /// <summary>A constellation the user defined, of no declared family.</summary>
        Custom,
    }

    /// <summary>
    /// Which metrics apply to which formats (<c>REQ-DEM-071</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The rules, and where each comes from.</strong> <c>REQ-DEM-071</c> requires the error
    /// summary's rows to appear and disappear with the format, and gives the failing case in its own
    /// words: "an inapplicable row appearing (magnitude error on FSK, say) fails, as does an
    /// applicable row missing". Each rule below cites the requirement that scopes it, because a
    /// table of applicability with no provenance is a table of opinions.
    /// </para>
    /// <para>
    /// <strong>Three judgements are recorded here rather than hidden.</strong>
    /// </para>
    /// <para>
    /// <em>EVM Pk is not a row.</em> <c>REQ-UI-053</c> lists it among the labels, and the same
    /// requirement's model of the actual on-screen text shows the peak in the EVM row —
    /// <c>EVM = 248.7475 m%rms 732.2379 m% pk at symbol 73</c> — not on a row of its own. The layout
    /// model is the more specific statement, so the peak stays where that shows it. The label
    /// remains in <see cref="ErrorSummary.Labels"/> for a display that does list it separately.
    /// </para>
    /// <para>
    /// <em>SNR (MER) is a row, though <c>REQ-UI-053</c>'s label list omits it.</em> That list is
    /// introduced as the house abbreviation style rather than as a closed set, its own model text
    /// shows <c>SNR = 40.58 dB</c>, and <c>REQ-DEM-069</c> says the label renders exactly
    /// <c>SNR (MER)</c>. Following the requirement that specifies the metric.
    /// </para>
    /// <para>
    /// <em>SNR (MER) is scoped by family, and its requirement scopes it by format.</em>
    /// <c>REQ-DEM-069</c> offers it for "QAM, DVB-QAM, 8PSK, QPSK, APSK and VSB". Read as families
    /// that is PSK, QAM, APSK and VSB, which admits BPSK and 16PSK — formats the requirement did not
    /// name. Generalising is the lesser error: the metric is well defined for them, and the
    /// alternative is a rule keyed on format names that would need editing every time
    /// <c>REQ-DEM-010</c>'s catalogue grew. <c>REQ-DEM-069</c> may narrow it.
    /// </para>
    /// <para>
    /// <strong>What is absent.</strong> FSK error and FSK deviation are <c>REQ-DEM-070</c>'s and
    /// have no label in <c>REQ-UI-053</c>'s list; inventing one here would be inventing house style
    /// for a display nobody has seen. They arrive with the FSK formats, and the FSK family's rules
    /// below are written so that adding them is one line.
    /// </para>
    /// </remarks>
    public static class MetricApplicability
    {
        /// <summary>The signal-to-noise metric's label (<c>REQ-DEM-069</c>).</summary>
        public const string SignalToNoise = "SNR (MER)";

        private static readonly ReadOnlyCollection<string> Order =
            new ReadOnlyCollection<string>(new List<string>
            {
                "EVM",
                "Offset EVM",
                "Mag Err",
                "Phase Err",
                "Freq Err",
                "Carr Ofst",
                "Time Offset",
                "SymClk Err",
                "IQ Offset",
                "IQ Gain Imbalance",
                "IQ Quad. Error",
                "IQ Timing Skew",
                "Amp Droop",
                "Pilot Lvl",
                SignalToNoise,
                "RSSI",
            });

        private static readonly Dictionary<string, string> Units =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "EVM", "%rms" },
                { "Offset EVM", "%rms" },
                { "Mag Err", "%rms" },
                { "Phase Err", "deg" },
                { "Freq Err", "Hz" },
                { "Carr Ofst", "Hz" },
                { "Time Offset", "s" },
                { "SymClk Err", "ppm" },
                { "IQ Offset", "dB" },
                { "IQ Gain Imbalance", "dB" },
                { "IQ Quad. Error", "deg" },
                { "IQ Timing Skew", "s" },
                { "Amp Droop", "dB/sym" },
                { "Pilot Lvl", "dB" },
                { SignalToNoise, "dB" },
                { "RSSI", "dBm" },
            };

        /// <summary>Every label the summary can show, in the order it shows them.</summary>
        public static IReadOnlyList<string> AllLabels => Order;

        /// <summary>The unit a metric is reported in.</summary>
        /// <param name="label">The metric's label.</param>
        /// <returns>The unit, before any engineering prefix.</returns>
        /// <exception cref="ArgumentException">There is no such metric.</exception>
        public static string UnitOf(string label)
        {
            string unit;

            if (!Units.TryGetValue(label ?? string.Empty, out unit))
            {
                throw new ArgumentException(
                    "No metric is called \"" + (label ?? "(none)") + "\".", nameof(label));
            }

            return unit;
        }

        /// <summary>Whether a metric applies to a format.</summary>
        /// <param name="label">The metric's label.</param>
        /// <param name="family">The format's family.</param>
        /// <param name="isOffset">Whether the format staggers I and Q by half a symbol.</param>
        /// <returns>Whether the summary shows a row for it.</returns>
        /// <exception cref="ArgumentException">There is no such metric.</exception>
        public static bool Applies(string label, ModulationFamily family, bool isOffset)
        {
            UnitOf(label);

            switch (label)
            {
                // REQ-DEM-062: an Offset EVM variant exists for offset formats, and only for them.
                case "Offset EVM":
                    return isOffset;

                // REQ-DEM-071's own example of the failing case. A constant-envelope frequency
                // modulation has no amplitude to be in error and no constellation phase to
                // compare, so neither number would mean anything.
                case "Mag Err":
                case "Phase Err":
                    return family != ModulationFamily.Fsk;

                // REQ-DEM-066 and REQ-DEM-067: the origin offset, the axis imbalance and the
                // quadrature error all come from one linear fit of the measured symbols against
                // the ideal ones. FSK and MSK are not linear modulations of a constellation, so
                // there is no such fit to read them out of.
                case "IQ Offset":
                case "IQ Gain Imbalance":
                case "IQ Quad. Error":
                case "IQ Timing Skew":
                    return family != ModulationFamily.Fsk && family != ModulationFamily.Msk;

                // REQ-DEM-070 scopes amplitude droop to the "MSK/GSM class".
                case "Amp Droop":
                    return family == ModulationFamily.Msk;

                // REQ-DEM-070: pilot level is VSB's.
                case "Pilot Lvl":
                    return family == ModulationFamily.Vsb;

                // REQ-DEM-069, generalised from formats to families; see the remarks.
                case SignalToNoise:
                    return family == ModulationFamily.Psk ||
                           family == ModulationFamily.Qam ||
                           family == ModulationFamily.Apsk ||
                           family == ModulationFamily.Vsb;

                default:
                    return true;
            }
        }

        /// <summary>The metrics a format shows, in the order the summary lists them.</summary>
        /// <param name="family">The format's family.</param>
        /// <param name="isOffset">Whether the format staggers I and Q by half a symbol.</param>
        /// <returns>The applicable labels.</returns>
        public static IReadOnlyList<string> LabelsFor(ModulationFamily family, bool isOffset)
        {
            var labels = new List<string>(Order.Count);

            foreach (string label in Order)
            {
                if (Applies(label, family, isOffset))
                {
                    labels.Add(label);
                }
            }

            return new ReadOnlyCollection<string>(labels);
        }
    }
}
