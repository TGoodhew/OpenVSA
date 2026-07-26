using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Core;
using OpenVSA.Hal;

namespace OpenVSA.Capture.Triggering
{
    /// <summary>
    /// Finds trigger instants in a record and cuts the triggered records out of it
    /// (<c>REQ-TRG-002</c>, <c>REQ-TRG-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Works on the magnitude, not on I or Q.</strong> A level trigger on an IF or zoom
    /// record has to fire on the envelope: the in-phase component of a signal well above the level
    /// passes through zero twice a cycle, so a trigger on <c>I</c> would fire on the carrier rather
    /// than on the burst.
    /// </para>
    /// <para>
    /// <strong>Pre-trigger is not a special case here.</strong> A negative delay moves the record
    /// start earlier, and the only question is whether the samples are in hand — which they are for
    /// a recording, a simulated source, or a front end with capture memory. What the front end can
    /// do is a capability (<see cref="IFrontEndCapabilities.MaxPreTriggerSamples"/>); what the
    /// arithmetic does is the same either way.
    /// </para>
    /// </remarks>
    public static class TriggerSearch
    {
        /// <summary>
        /// The sample indices at which a trigger fires.
        /// </summary>
        /// <param name="block">The record to search.</param>
        /// <param name="settings">Trigger settings.</param>
        /// <returns>Indices into <paramref name="block"/>, ascending.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static IReadOnlyList<int> Instants(IqBlock block, TriggerSettings settings)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            switch (settings.Style)
            {
                case TriggerStyle.Immediate:
                    // Free run: the record begins where it begins.
                    return new[] { 0 };

                case TriggerStyle.Periodic:
                    return Periodic(block, settings);

                case TriggerStyle.Level:
                case TriggerStyle.External:
                    // External is modelled the same way here: a level crossing on whatever the
                    // front end delivered as the trigger channel. The difference is which signal
                    // is looked at, which is the front end's business, not this search's.
                    return Crossings(block, settings);

                default:
                    // A style this search cannot reproduce - a frequency mask needs the spectrum,
                    // not the time record - fires once at the start rather than pretending.
                    return new[] { 0 };
            }
        }

        /// <summary>
        /// Cuts a triggered record out of a longer one.
        /// </summary>
        /// <param name="block">The record to cut from.</param>
        /// <param name="settings">Trigger settings.</param>
        /// <param name="recordSamples">Length of the record to cut, in samples.</param>
        /// <param name="occurrence">Which trigger to use, from 0.</param>
        /// <returns>
        /// A new block starting at the trigger plus the delay, or <c>null</c> if there is no such
        /// trigger or the record does not reach far enough either side of it.
        /// </returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        /// <remarks>
        /// The returned block's <see cref="IqBlock.TriggerOffsetSeconds"/> is where the trigger sits
        /// within it — positive for a pre-trigger record, because the trigger is then inside the
        /// record rather than before it.
        /// </remarks>
        public static IqBlock Extract(
            IqBlock block, TriggerSettings settings, int recordSamples, int occurrence = 0)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (recordSamples < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recordSamples), recordSamples, "A record needs at least one sample.");
            }

            if (occurrence < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(occurrence), occurrence, "Occurrences are numbered from zero.");
            }

            IReadOnlyList<int> instants = Instants(block, settings);

            if (occurrence >= instants.Count)
            {
                return null;
            }

            int trigger = instants[occurrence];
            int first = trigger + settings.DelaySamples(block.SampleRateHz);

            // Not clamped. A pre-trigger record that ran off the front of what was captured would
            // silently be a differently-timed record, and a measurement that quietly moved is worse
            // than one that says it could not be made.
            if (first < 0 || first + recordSamples > block.SampleCount)
            {
                return null;
            }

            var metadata = new IqBlockMetadata(
                sampleCount: recordSamples,
                sampleRateHz: block.SampleRateHz,
                centerFrequencyHz: block.CenterFrequencyHz,
                isBaseband: block.IsBaseband,
                fullScaleVolts: block.FullScaleVolts,
                referenceLevelDbm: block.ReferenceLevelDbm,
                sequenceNumber: block.SequenceNumber,
                acquiredUtc: block.AcquiredUtc,

                // Where the trigger sits relative to the first sample of this record. Positive
                // means the trigger is inside it, which is exactly the pre-trigger case.
                triggerOffsetSeconds: (trigger - first) / block.SampleRateHz,
                triggerCorrectionsApplied: true,
                source: block.Source,
                extended: block.Extended);

            IqBlock record = IqBlock.Rent(metadata);

            try
            {
                block.GetSamples()
                    .Slice(first * 2, recordSamples * 2)
                    .CopyTo(record.GetSamples());
            }
            catch
            {
                record.Dispose();
                throw;
            }

            return record;
        }

        /// <summary>
        /// Whether a front end can serve the pre-trigger a set of settings asks for
        /// (<c>REQ-TRG-002</c>).
        /// </summary>
        /// <param name="capabilities">What the front end declares.</param>
        /// <param name="settings">Trigger settings.</param>
        /// <param name="sampleRateHz">Sample rate, in hertz.</param>
        /// <param name="reason">Receives why it cannot, or empty.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static bool CanServePreTrigger(
            IFrontEndCapabilities capabilities,
            TriggerSettings settings,
            double sampleRateHz,
            out string reason)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            reason = string.Empty;

            if (!settings.IsPreTrigger)
            {
                return true;
            }

            long needed = -(long)settings.DelaySamples(sampleRateHz);

            if (needed <= capabilities.MaxPreTriggerSamples)
            {
                return true;
            }

            reason =
                "This source keeps " +
                capabilities.MaxPreTriggerSamples.ToString(CultureInfo.CurrentCulture) +
                " samples before a trigger, and " +
                needed.ToString(CultureInfo.CurrentCulture) + " were asked for (" +
                (-settings.DelaySeconds * 1e3).ToString("G4", CultureInfo.CurrentCulture) +
                " ms of pre-trigger).";

            return false;
        }

        /// <summary>
        /// Level crossings, with hold-off applied.
        /// </summary>
        /// <remarks>
        /// A crossing, not a level: the signal must have been on the other side of the threshold at
        /// the previous sample. Firing on "above the level" instead would re-trigger on every sample
        /// of a burst, which is not a trigger but a sample counter.
        /// </remarks>
        private static IReadOnlyList<int> Crossings(IqBlock block, TriggerSettings settings)
        {
            ReadOnlySpan<float> samples = block.GetSamples();
            double levelSquared = settings.LevelVolts * settings.LevelVolts;
            int holdoff = settings.HoldoffSamples(block.SampleRateHz);

            var instants = new List<int>();
            int rearmAt = 0;

            bool wasAbove = Above(samples, 0, levelSquared);

            for (int n = 1; n < block.SampleCount; n++)
            {
                bool isAbove = Above(samples, n, levelSquared);
                bool crossed = settings.RisingEdge ? !wasAbove && isAbove : wasAbove && !isAbove;

                wasAbove = isAbove;

                if (!crossed || n < rearmAt)
                {
                    continue;
                }

                instants.Add(n);
                rearmAt = RearmAfter(samples, n, levelSquared, holdoff, settings, block.SampleCount);
            }

            return instants;
        }

        /// <summary>
        /// The earliest sample at which the trigger may fire again, under each hold-off style.
        /// </summary>
        private static int RearmAfter(
            ReadOnlySpan<float> samples,
            int trigger,
            double levelSquared,
            int holdoff,
            TriggerSettings settings,
            int count)
        {
            if (settings.Holdoff == HoldoffStyle.Conventional)
            {
                // A fixed blanking window, whatever the signal does in it.
                return trigger + Math.Max(1, holdoff);
            }

            // Conditional: the signal must hold on one side of the level for the whole hold-off,
            // and the window restarts every time it does not. A run counter rather than a fixed
            // offset, because the interval between qualifying runs is exactly what is not known.
            bool wantAbove = settings.Holdoff == HoldoffStyle.AboveLevel;
            int run = 0;

            for (int n = trigger + 1; n < count; n++)
            {
                if (Above(samples, n, levelSquared) == wantAbove)
                {
                    run++;

                    if (run >= holdoff)
                    {
                        return n + 1;
                    }
                }
                else
                {
                    run = 0;
                }
            }

            // The condition was never satisfied for long enough, so the trigger never re-arms.
            return count;
        }

        private static bool Above(ReadOnlySpan<float> samples, int n, double levelSquared)
        {
            double i = samples[n * 2];
            double q = samples[n * 2 + 1];
            return i * i + q * q > levelSquared;
        }

        private static IReadOnlyList<int> Periodic(IqBlock block, TriggerSettings settings)
        {
            int stride = (int)Math.Round(settings.PeriodSeconds * block.SampleRateHz);

            if (stride < 1)
            {
                stride = 1;
            }

            var instants = new List<int>();

            for (int n = 0; n < block.SampleCount; n += stride)
            {
                instants.Add(n);
            }

            return instants;
        }
    }
}
