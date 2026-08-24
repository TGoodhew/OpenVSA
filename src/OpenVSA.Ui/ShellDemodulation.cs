using System;
using System.Windows.Threading;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Ui
{
    /// <summary>
    /// The shell's half of digital demodulation: choosing it, drawing what it produces, and saying
    /// so when it cannot (<c>REQ-DEM-001</c>, <c>REQ-DEM-080</c>, <c>REQ-UI-061</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The context is where the demodulation happens; this file only shows it.</strong>
    /// <c>MeasurementContext</c> demodulates the blocks it is handed when its setup asks for it, and
    /// raises <c>ResultAnalysed</c> beside <c>FrameAnalysed</c>. So the shell subscribes to every
    /// context it owns, and a measurement type is a property of a context's setup rather than a
    /// mode the window is in — which is what lets one context demodulate while another watches the
    /// spectrum, from the same acquisition, as <c>REQ-DAT-010</c> asks.
    /// </para>
    /// <para>
    /// <strong>Results cross to the UI thread the way frames do.</strong> A result arrives on the
    /// acquisition pump's thread. It is marshalled at <see cref="DispatcherPriority.Render"/>, which
    /// is the priority the spectrum path uses, so a demodulation cannot starve the input queue and
    /// cannot be drawn from the wrong thread.
    /// </para>
    /// </remarks>
    public partial class ShellWindow
    {
        private DemodResult _result;

        /// <summary>The newest demodulation the shell has drawn, or <c>null</c>.</summary>
        internal DemodResult LatestResult => _result;

        /// <summary>
        /// Subscribes to every context's demodulation, now and as contexts are added.
        /// </summary>
        private void WatchForResults()
        {
            foreach (MeasurementContext context in _contextSet.Contexts)
            {
                WatchForResults(context);
            }

            _contextSet.Added += (sender, added) => WatchForResults(added);
        }

        /// <summary>
        /// Subscribes to a context's demodulation, so its results reach the display.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <remarks>
        /// Every context, not only the demodulating ones: a context's kind changes while it is
        /// running, and subscribing at that moment would mean the first result after a change
        /// arrived at nobody. A context that is not demodulating raises nothing.
        /// </remarks>
        private void WatchForResults(MeasurementContext context)
        {
            if (context == null)
            {
                return;
            }

            context.ResultAnalysed += OnResultAnalysed;
            context.DemodulationFaulted += OnDemodulationFaulted;
        }

        private void OnResultAnalysed(object sender, DemodResult result)
        {
            var context = sender as MeasurementContext;

            // A secondary context's result is kept by that context and drawn when it is activated,
            // exactly as its frames are. Drawing every context's result into the active window
            // would put one measurement's constellation on another's trace.
            if (context == null || !ReferenceEquals(context, _contextSet.Active))
            {
                return;
            }

            // Already on the UI thread -- a context analysed from the shell's own thread rather
            // than from the pump -- so show it now. Queueing it would be work for nothing, and it
            // would make the display's timing depend on a dispatcher priority being reached, which
            // is not something a caller can wait for without risking waiting for ever.
            if (Dispatcher.CheckAccess())
            {
                ShowResult(result);

                return;
            }

            Dispatcher.BeginInvoke(
                DispatcherPriority.Render, new Action<DemodResult>(ShowResult), result);
        }

        private void OnDemodulationFaulted(object sender, Exception failure)
        {
            if (failure == null)
            {
                return;
            }

            // On the UI thread, because the event log and the status bar are the shell's.
            if (Dispatcher.CheckAccess())
            {
                ReportDemodulationFault(failure.Message);

                return;
            }

            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action<string>(ReportDemodulationFault),
                failure.Message);
        }

        private void ReportDemodulationFault(string message)
        {
            string said = "Demodulation: " + message;

            StatusText.Content = said;
            _eventLog.Append(said);
        }

        /// <summary>
        /// Puts a demodulated result on the trace windows that are showing one.
        /// </summary>
        /// <param name="result">The result.</param>
        private void ShowResult(DemodResult result)
        {
            _result = result;

            if (result == null)
            {
                return;
            }

            foreach (TracePlot plot in Documents.Plots)
            {
                if (plot.ResultKind == ResultTraceKind.None)
                {
                    continue;
                }

                plot.Result = result.Trace;
            }

            ShowResultReadout(result);
        }

        /// <summary>Says what the demodulation measured, in the status bar.</summary>
        /// <param name="result">The result.</param>
        /// <remarks>
        /// EVM and the symbol count, which are the two numbers that say at a glance whether a
        /// demodulation is working. The whole error summary is a trace of its own
        /// (<c>REQ-DEM-080</c>), reached by putting a trace window into the symbol-table format.
        /// </remarks>
        private void ShowResultReadout(DemodResult result)
        {
            if (result.Trace == null)
            {
                return;
            }

            StatusText.Content =
                result.Trace.Modulation + ": " + result.Trace.SymbolCount + " symbols, EVM " +
                result.EvmPercent.ToString("G4", System.Globalization.CultureInfo.CurrentCulture) +
                " %rms";
        }

        /// <summary>
        /// Applies a measurement type to the active context (<c>REQ-UI-061</c> Analysis &gt; Type).
        /// </summary>
        /// <param name="kind">The type chosen.</param>
        /// <remarks>
        /// <para>
        /// <c>SelectKind</c> rather than an assignment, because <c>REQ-DEM-030</c> puts a default on
        /// the first selection of digital demodulation — the symbol rate starts at half the span —
        /// and that default belongs at the moment of choosing rather than inside the demodulator.
        /// </para>
        /// <para>
        /// Leaving digital demodulation clears the result from the windows showing one. A
        /// constellation left on screen after the measurement that produced it has been turned off
        /// is the worst kind of stale display: it is a real measurement, of a signal that may since
        /// have gone.
        /// </para>
        /// </remarks>
        private void ApplyMeasurementKind(MeasurementKind kind)
        {
            MeasurementContext active = _contextSet.Active;

            active.Setup.SelectKind(kind);

            if (kind == MeasurementKind.DigitalDemodulation)
            {
                StartShowingResults();

                return;
            }

            StopShowingResults();
        }

        private void StartShowingResults()
        {
            TracePlot plot = Documents.ActivePlot;

            if (plot != null && plot.ResultKind == ResultTraceKind.None)
            {
                // A constellation, because it is the display that says at a glance whether a
                // demodulation is working at all. The other result formats are a trace-format
                // choice away.
                plot.ResultKind = ResultTraceKind.Constellation;
            }

            DemodState demod = _contextSet.Active.Setup.Demod;

            string said =
                "Digital demodulation: " + demod.Format + " at " +
                EngineeringText.Frequency(demod.SymbolRateHz) + "sym/s, " +
                demod.ResultLengthSymbols + " symbols.";

            StatusText.Content = said;
            _eventLog.Append(said);
        }

        /// <summary>
        /// Tells a synthetic front end what to transmit (<c>REQ-SIM-001</c>).
        /// </summary>
        /// <param name="synthetic">The connected source.</param>
        /// <param name="modulation">The format's name, or <c>null</c> for a carrier.</param>
        /// <remarks>
        /// <para>
        /// <strong>The symbol rate comes from the measurement, once, at the moment of choosing.</strong>
        /// A source and an analyser that agreed automatically and permanently would be a pair that
        /// could never be made to disagree, and disagreeing is a measurement people make on purpose:
        /// <c>REQ-DEM-030</c>'s whole signature test is what a symbol-rate error looks like. So the
        /// rate is copied from the active measurement's demodulator settings when the signal is
        /// chosen, said out loud, and then the two are independent.
        /// </para>
        /// <para>
        /// Refused rather than clipped when the rate the measurement wants is faster than the
        /// acquisition can carry. The alternative is a source transmitting something other than
        /// what the readout says, which is the one thing a signal source must never do.
        /// </para>
        /// </remarks>
        private void ChooseSyntheticSignal(ISyntheticSource synthetic, string modulation)
        {
            if (synthetic == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(modulation))
            {
                synthetic.Modulation = null;

                Said("Simulated source: unmodulated carrier.");

                return;
            }

            double wanted = _contextSet.Active.Setup.Demod.SymbolRateHz;

            if (wanted <= 0.0)
            {
                wanted = DemodState.DefaultSymbolRateFor(_contextSet.Active.Setup.SpanHz);
            }

            double fastest = FastestSymbolRate(synthetic);

            if (fastest > 0.0 && wanted > fastest)
            {
                Said(
                    "The simulated source cannot transmit " + EngineeringText.Frequency(wanted) +
                    "sym/s: this acquisition carries " + EngineeringText.Frequency(fastest) +
                    "sym/s at " + synthetic.MinimumSamplesPerSymbol +
                    " samples a symbol. Widen the span or lower the symbol rate.");

                return;
            }

            try
            {
                synthetic.Modulation = modulation;
                synthetic.SymbolRateHz = wanted;
            }
            catch (ArgumentException refused)
            {
                Said("Simulated source: " + refused.Message);

                return;
            }

            Said(
                "Simulated source: " + modulation + " at " + EngineeringText.Frequency(wanted) +
                "sym/s, roll-off " +
                synthetic.RollOff.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) +
                ".");
        }

        /// <summary>
        /// The fastest symbol rate the current acquisition can carry, or zero when unknown.
        /// </summary>
        /// <param name="synthetic">The source, which declares the samples a symbol needs.</param>
        /// <remarks>
        /// <c>REQ-HAL-002</c>: ranged from what the source and the plan declare, not from anything
        /// this window assumes about simulators.
        /// </remarks>
        private double FastestSymbolRate(ISyntheticSource synthetic)
        {
            SpectrumEngine engine = _engine;
            AcquisitionPlan plan = engine == null ? null : engine.Plan;

            if (plan == null || synthetic.MinimumSamplesPerSymbol <= 0.0)
            {
                return 0.0;
            }

            return plan.SampleRateHz / synthetic.MinimumSamplesPerSymbol;
        }

        private void Said(string what)
        {
            StatusText.Content = what;
            _eventLog.Append(what);
        }

        private void StopShowingResults()
        {
            _result = null;

            foreach (TracePlot plot in Documents.Plots)
            {
                plot.Result = null;
                plot.ResultKind = ResultTraceKind.None;
            }
        }
    }
}
