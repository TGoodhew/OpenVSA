using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Ui;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-HAL-002</c>'s second clause: switching front ends visibly re-ranges the affected
    /// controls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first clause — no instrument model name anywhere in <c>OpenVSA.Ui</c> — is enforced by
    /// <c>NoInstrumentNamesTests</c>. That one catches a table of models. This one catches the
    /// subtler failure: a settings pane whose limits are constants that happen to suit the
    /// instrument the developer had on the bench. Such a pane names no model, passes the search,
    /// and silently offers a 26.5 GHz centre frequency on a 4 GHz instrument.
    /// </para>
    /// <para>
    /// Two capability sets that differ in every dimension are handed to the shell in turn, and the
    /// displayed ranges must differ. Not "must equal a particular string" — that would pin the
    /// formatting rather than the behaviour — but must change, and must contain the figure the
    /// capabilities declare.
    /// </para>
    /// </remarks>
    public class CapabilityDrivenRangingTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the two sets of displayed ranges are written.</param>
        public CapabilityDrivenRangingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void SwitchingFrontEndsChangesTheDisplayedRanges()
        {
            OnStaThread(() =>
            {
                var shell = new ShellWindow();

                var narrow = new Capabilities(
                    minCentreHz: 10.0e6, maxCentreHz: 4.0e9,
                    minSpanHz: 10.0, maxSpanHz: 10.0e6,
                    minLevelDbm: -60.0, maxLevelDbm: 10.0);

                var wide = new Capabilities(
                    minCentreHz: 0.0, maxCentreHz: 26.5e9,
                    minSpanHz: 1.0, maxSpanHz: 160.0e6,
                    minLevelDbm: -120.0, maxLevelDbm: 30.0);

                shell.RangeSettingsFor(narrow);

                string narrowCentre = shell.CentreRange.Text;
                string narrowSpan = shell.SpanRange.Text;
                string narrowLevel = shell.ReferenceLevelRange.Text;

                shell.RangeSettingsFor(wide);

                string wideCentre = shell.CentreRange.Text;
                string wideSpan = shell.SpanRange.Text;
                string wideLevel = shell.ReferenceLevelRange.Text;

                _output.WriteLine("narrow: " + narrowCentre + " | " + narrowSpan + " | " + narrowLevel);
                _output.WriteLine("wide:   " + wideCentre + " | " + wideSpan + " | " + wideLevel);

                Assert.NotEqual(narrowCentre, wideCentre);
                Assert.NotEqual(narrowSpan, wideSpan);
                Assert.NotEqual(narrowLevel, wideLevel);

                // And the figures shown are the ones the capabilities declared, so a pane that
                // merely redrew something different would not pass.
                Assert.Contains("26.5", wideCentre);
                Assert.Contains("4.0", narrowCentre);
                Assert.Contains("-120", wideLevel);
                Assert.Contains("-60", narrowLevel);

                shell.Close();
            });
        }

        [Fact]
        public void NoCapabilitiesDisablesTheSettingsRatherThanShowingStaleLimits()
        {
            // A pane still showing the previous instrument's limits after a disconnect is worse
            // than a blank one: it invites a setting that nothing can honour.
            OnStaThread(() =>
            {
                var shell = new ShellWindow();

                shell.RangeSettingsFor(new Capabilities(0.0, 26.5e9, 1.0, 160.0e6, -120.0, 30.0));
                Assert.True(shell.SettingsGrid.IsEnabled);

                shell.RangeSettingsFor(null);
                Assert.False(shell.SettingsGrid.IsEnabled);

                shell.Close();
            });
        }

        /// <summary>Capabilities that declare exactly what the test wants ranged.</summary>
        private sealed class Capabilities : IFrontEndCapabilities
        {
            private static readonly IReadOnlyList<TriggerStyle> Styles =
                new ReadOnlyCollection<TriggerStyle>(new[] { TriggerStyle.Immediate });

            public Capabilities(
                double minCentreHz, double maxCentreHz,
                double minSpanHz, double maxSpanHz,
                double minLevelDbm, double maxLevelDbm)
            {
                CenterFrequencyRange = new FrequencyRange(minCentreHz, maxCentreHz);
                MinSpanHz = minSpanHz;
                MaxSpanHz = maxSpanHz;
                ReferenceLevelRange = new AmplitudeRange(minLevelDbm, maxLevelDbm);
            }

            public FrequencyRange CenterFrequencyRange { get; }
            public double MaxSpanHz { get; }
            public double MinSpanHz { get; }
            public double MaxSampleRateHz => MaxSpanHz * 1.28;
            public int MaxSamplesPerBlock => 1 << 20;
            public long MaxCaptureSamples => 1L << 26;
            public bool SupportsBasebandIq => true;
            public int ChannelCount => 1;
            public bool SupportsPhaseCoherentChannels => false;
            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;
            public AmplitudeRange ReferenceLevelRange { get; }
            public bool SupportsExternalRef => false;
            public bool SupportsRealTimeAnalysis => false;
            public bool SupportsInputRangeControl => false;
            public long MaxPreTriggerSamples => 0L;
        }

        private static void OnStaThread(Action action)
        {
            ExceptionDispatchInfo failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                failure.Throw();
            }
        }
    }
}
