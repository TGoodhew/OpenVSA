using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Measurement;
using OpenVSA.Synthesis;
using OpenVSA.TestHarness.Synthesis;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-006</c>: the status bar's contents, and the bottom-left placement it quotes.
    /// </summary>
    [Collection("Shell")]
    public class StatusBarTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public StatusBarTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void MeasurementStatusIsAtTheBottomLeftSpecifically()
        {
            // "asserted on position, since that placement is quoted from the reference product".
            // Presence is not enough: a status bar with the message third from the right satisfies
            // every other clause and fails this one.
            _host.Run(() =>
            {
                var shell = Built();

                StatusBar bar = StatusBarOf(shell);

                Assert.True(bar != null, "The shell has no status bar.");

                object first = bar.Items.Cast<object>().First();

                Assert.Same(shell.StatusItem, first);
            });
        }

        [Fact]
        public void EveryFieldTheRequirementListsIsPresent()
        {
            // Measurement status, calibration status, reference lock, spectrum rate, preview
            // features, and OpenVSA's two additions: dropped frames and transfer rate.
            _host.Run(() =>
            {
                var shell = Built();

                var named = StatusBarOf(shell).Items
                    .OfType<StatusBarItem>()
                    .Select(i => i.Name)
                    .ToList();

                foreach (string field in new[]
                {
                    "StatusText", "CalibrationText", "ReferenceText", "RateText",
                    "TransferText", "DroppedText", "PreviewText",
                })
                {
                    Assert.Contains(field, named);
                }
            });
        }

        [Fact]
        public void EachStatusStringAppearsWhenItsConditionHolds()
        {
            // The strings are quoted from the reference product, so they are asserted as literals.
            Assert.Equal("Waiting for Trigger", MeasurementStatusText.TextOf(MeasurementStatus.WaitingForTrigger));
            Assert.Equal("Average Complete", MeasurementStatusText.TextOf(MeasurementStatus.AverageComplete));
            Assert.Equal("Measurement running", MeasurementStatusText.TextOf(MeasurementStatus.Running));
            Assert.Equal("Real-Time Measurement", MeasurementStatusText.TextOf(MeasurementStatus.RealTime));
            Assert.Equal("Filling Time Record", MeasurementStatusText.TextOf(MeasurementStatus.FillingTimeRecord));

            // And each is reached by its own condition rather than assigned.
            Assert.Equal(
                MeasurementStatus.Idle,
                MeasurementStatusText.For(false, false, false, false, 0, 0));

            Assert.Equal(
                MeasurementStatus.WaitingForTrigger,
                MeasurementStatusText.For(true, true, false, false, 0, 0));

            Assert.Equal(
                MeasurementStatus.FillingTimeRecord,
                MeasurementStatusText.For(true, false, true, false, 0, 0));

            Assert.Equal(
                MeasurementStatus.Running,
                MeasurementStatusText.For(true, false, false, false, 0, 0));

            Assert.Equal(
                MeasurementStatus.RealTime,
                MeasurementStatusText.For(true, false, false, true, 0, 0));

            // A completed average outranks everything else that is running, because it is the
            // answer the user was waiting for.
            Assert.Equal(
                MeasurementStatus.AverageComplete,
                MeasurementStatusText.For(true, false, false, true, 10, 10));

            // And it is not reported before it has happened.
            Assert.NotEqual(
                MeasurementStatus.AverageComplete,
                MeasurementStatusText.For(true, false, false, true, 10, 9));
        }

        [Fact]
        public void DrivingTheAcquisitionIntoAPhaseShowsThatPhasesString()
        {
            // The criterion's own exercise, through the seam the acquisition reports on.
            _host.Run(() =>
            {
                var shell = Built();

                shell.ReportAcquisitionPhase(armed: true, fillingRecord: false);

                // Nothing is measuring, so the phase alone does not claim a measurement.
                Assert.Equal(MeasurementStatus.Idle, shell.MeasurementStatus);
                Assert.Equal("Ready", shell.StatusItem.Content);
            });
        }

        [Fact]
        public void CalibrationAndReferenceTrackTheirConditionsRatherThanShowingAFixedValue()
        {
            _host.Run(() =>
            {
                var shell = Built();

                // Nothing connected: neither is claimed either way.
                Assert.Equal("Cal —", shell.CalibrationItem.Content);
                Assert.Equal("Ref —", shell.ReferenceItem.Content);

                shell.SetHardwareIndicators(referenceUnlocked: true, calibrationQuestionable: true);

                Assert.True(shell.IsReferenceUnlocked);
                Assert.True(shell.IsCalibrationQuestionable);

                shell.SetHardwareIndicators(false, false);

                Assert.False(shell.IsReferenceUnlocked);
                Assert.False(shell.IsCalibrationQuestionable);
            });
        }

        [Fact]
        public void ThePreviewFeatureCountTracksWhatIsInUse()
        {
            // REQ-UI-006's "beta features in use", adopted as a preview indicator. OpenVSA gates
            // nothing, so it counts use rather than licence — and reads zero until something says
            // otherwise, which is the honest answer.
            _host.Run(() =>
            {
                var shell = Built();

                Assert.Empty(shell.PreviewFeatures);
                Assert.Equal("No preview features", shell.PreviewItem.Content);

                shell.SetPreviewFeature("Spectrogram markers", true);

                Assert.Single(shell.PreviewFeatures);
                Assert.Contains("1 preview feature(s)", (string)shell.PreviewItem.Content);
                Assert.Contains("Spectrogram markers", (string)shell.PreviewItem.Content);

                shell.SetPreviewFeature("Spectrogram markers", false);

                Assert.Empty(shell.PreviewFeatures);

                Assert.Throws<ArgumentException>(() => shell.SetPreviewFeature("  ", true));
            });
        }

        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };

        private static StatusBar StatusBarOf(ShellWindow shell) =>
            Descendants(shell).OfType<StatusBar>().FirstOrDefault();

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var node = child as DependencyObject;

                if (node == null)
                {
                    continue;
                }

                yield return node;

                foreach (DependencyObject deeper in Descendants(node))
                {
                    yield return deeper;
                }
            }
        }
    }

    /// <summary>
    /// <c>REQ-UI-007</c>: every condition that invalidates a measurement, on the trace.
    /// </summary>
    [Collection("Shell")]
    public class FaultIndicatorTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public FaultIndicatorTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void EveryConditionTheRequirementNamesHasAnIndicatorString()
        {
            // The requirement's own list: ADC overload, unlocked reference, uncalibrated state,
            // demodulation lock failure, sync not found, pulse not found, dropped frames.
            Assert.Equal("OV1", TraceIndicators.TextOf(TraceIndicator.Overload));
            Assert.Equal("REF UNLOCK", TraceIndicators.TextOf(TraceIndicator.ReferenceUnlocked));
            Assert.Equal("CAL?", TraceIndicators.TextOf(TraceIndicator.CalibrationQuestionable));
            Assert.Equal("CARRIER LOCK?", TraceIndicators.TextOf(TraceIndicator.CarrierLock));
            Assert.Equal("SYNC NOT FOUND", TraceIndicators.TextOf(TraceIndicator.SyncNotFound));
            Assert.Equal("PULSE NOT FOUND", TraceIndicators.TextOf(TraceIndicator.PulseNotFound));
            Assert.Equal("DROPPED", TraceIndicators.TextOf(TraceIndicator.DroppedFrames));
        }

        [Fact]
        public void EachConditionRaisesItsStringOnTheTraceAndClearsWhenItClears()
        {
            // "Each listed condition is provoked in turn ... and each raises its REQ-UI-041 string
            // in the trace's upper-right corner ... The indicator clears when the condition clears."
            _host.Run(() =>
            {
                var shell = Built();

                foreach (TraceIndicator condition in new[]
                {
                    TraceIndicator.CarrierLock,
                    TraceIndicator.SyncNotFound,
                    TraceIndicator.PulseNotFound,
                })
                {
                    shell.SetDemodulationIndicator(condition, true);

                    Assert.Contains(
                        TraceIndicators.TextOf(condition), shell.DocumentArea.ActivePlot.IndicatorText);

                    shell.SetDemodulationIndicator(condition, false);

                    Assert.DoesNotContain(
                        TraceIndicators.TextOf(condition), shell.DocumentArea.ActivePlot.IndicatorText);
                }

                shell.SetHardwareIndicators(referenceUnlocked: true, calibrationQuestionable: true);

                Assert.Contains("REF UNLOCK", shell.DocumentArea.ActivePlot.IndicatorText);
                Assert.Contains("CAL?", shell.DocumentArea.ActivePlot.IndicatorText);

                shell.SetHardwareIndicators(false, false);

                Assert.DoesNotContain("REF UNLOCK", shell.DocumentArea.ActivePlot.IndicatorText);
                Assert.DoesNotContain("CAL?", shell.DocumentArea.ActivePlot.IndicatorText);
            });
        }

        [Fact]
        public void OnlyADemodulatorMayReportTheDemodulationConditions()
        {
            // The seam is restricted to the three it owns, so it cannot become a back door for
            // setting conditions the shell is meant to observe for itself.
            _host.Run(() =>
            {
                var shell = Built();

                Assert.Throws<ArgumentOutOfRangeException>(
                    () => shell.SetDemodulationIndicator(TraceIndicator.Overload, true));

                Assert.Throws<ArgumentOutOfRangeException>(
                    () => shell.SetDemodulationIndicator(TraceIndicator.ReferenceUnlocked, true));
            });
        }

        [Fact]
        public void TheIndicatorIsOnTheTraceRatherThanOnlyInTheEventLog()
        {
            // "A test asserts the string is on the trace rather than only in the event log, since
            // 'buried in a log' is precisely the failure this forbids."
            _host.Run(() =>
            {
                var shell = Built();

                shell.SetDemodulationIndicator(TraceIndicator.CarrierLock, true);

                TracePlot plot = shell.DocumentArea.ActivePlot;

                Assert.Contains("CARRIER LOCK?", plot.IndicatorText);

                // In the upper-right corner of the trace, and in the Indicator colour.
                Assert.Equal(HorizontalAlignment.Right, plot.IndicatorElement.HorizontalAlignment);
                Assert.Equal(VerticalAlignment.Top, plot.IndicatorElement.VerticalAlignment);
            });
        }

        [Fact]
        public void TheIndicatorSurvivesShowAnnotationBeingTurnedOff()
        {
            // REQ-UI-007 against REQ-UI-011. Every string here means the number on screen is wrong,
            // so hiding them because a user wanted a clean picture would be a worse burial than the
            // event log the requirement already forbids. This was live: ApplyAnnotationVisibility
            // collapsed the indicator with the rest of the annotation.
            _host.Run(() =>
            {
                var shell = Built();

                shell.SetDemodulationIndicator(TraceIndicator.CarrierLock, true);

                TracePlot plot = shell.DocumentArea.ActivePlot;

                Assert.Equal(Visibility.Visible, plot.IndicatorElement.Visibility);

                plot.ShowAnnotation = false;

                Assert.Equal(Visibility.Visible, plot.IndicatorElement.Visibility);
                Assert.Contains("CARRIER LOCK?", plot.IndicatorText);

                plot.ShowAnnotation = true;

                Assert.Equal(Visibility.Visible, plot.IndicatorElement.Visibility);
            });
        }

        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };
    }

    /// <summary>
    /// <c>REQ-UI-080</c>: the third font slot, now that it has a surface.
    /// </summary>
    public class TabularFontSlotTests
    {
        [Fact]
        public void TheSymbolTableAndErrorSummaryDrawFromTabularNotAnnotation()
        {
            // "The symbol table and error summary of REQ-UI-052 draw from Tabular, not Annotation,
            // which is the whole reason the third slot exists." Until #253's panel there was no
            // surface to judge this on, which is why #276 was left open.
            OnStaThread(() =>
            {
                var fonts = new FontPreferences();

                fonts.Set(FontSlot.Annotation, new FontChoice("Segoe UI", 14.0));
                fonts.Set(FontSlot.Tabular, new FontChoice("Courier New", 9.0));

                var panel = new SymbolTablePanel
                {
                    Result = new SyntheticSymbolSource { Scheme = ModulationScheme.Qam16() }
                        .Generate(32).ToSymbolTrace(),
                };

                panel.ApplyFonts(fonts);

                foreach (TextBlock portion in new[] { panel.SummaryPortion, panel.StreamPortion })
                {
                    Assert.Equal("Courier New", portion.FontFamily.Source);
                    Assert.NotEqual(
                        fonts.Choice(FontSlot.Annotation).SizeDip, portion.FontSize);
                }
            });
        }

        [Fact]
        public void ExactlyThreeSlotsExistAndSettingOneLeavesTheOthers()
        {
            var fonts = new FontPreferences();

            Assert.Equal(
                new[] { FontSlot.Annotation, FontSlot.Marker, FontSlot.Tabular },
                FontPreferences.Slots.ToArray());

            FontChoice marker = fonts.Choice(FontSlot.Marker);
            FontChoice tabular = fonts.Choice(FontSlot.Tabular);

            fonts.Set(FontSlot.Annotation, new FontChoice("Verdana", 12.0));

            Assert.Equal(marker, fonts.Choice(FontSlot.Marker));
            Assert.Equal(tabular, fonts.Choice(FontSlot.Tabular));
        }

        [Fact]
        public void MarkerAndTabularResolveToFixedPitchFacesByDefault()
        {
            // "asserted on the resolved face's pitch" — the face actually in use, not the one asked
            // for, so a machine without Consolas still gives a monospaced answer.
            OnStaThread(() =>
            {
                var fonts = new FontPreferences();

                foreach (FontSlot slot in new[] { FontSlot.Marker, FontSlot.Tabular })
                {
                    Assert.True(FontPreferences.RequiresFixedPitch(slot));
                    Assert.True(
                        FontPreferences.IsFixedPitch(fonts.ResolveFamily(slot)),
                        slot + " resolved to '" + fonts.ResolveFamily(slot) + "', which is not fixed pitch.");
                }

                // Annotation may be proportional, and its default is.
                Assert.False(FontPreferences.RequiresFixedPitch(FontSlot.Annotation));
            });
        }

        [Fact]
        public void TheDefaultsAreTheRecommendedOnes()
        {
            Assert.Equal(new FontChoice("Segoe UI", 9.0), FontPreferences.DefaultFor(FontSlot.Annotation));
            Assert.Equal(new FontChoice("Consolas", 9.0), FontPreferences.DefaultFor(FontSlot.Marker));
            Assert.Equal(new FontChoice("Consolas", 9.0), FontPreferences.DefaultFor(FontSlot.Tabular));

            // And each has a documented fallback chain for a machine without them.
            foreach (FontSlot slot in FontPreferences.Slots)
            {
                Assert.NotEmpty(FontPreferences.Fallbacks(slot));
            }
        }

        private static void OnStaThread(Action action)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo failure = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
                }
            });

            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                failure.Throw();
            }
        }
    }
}
