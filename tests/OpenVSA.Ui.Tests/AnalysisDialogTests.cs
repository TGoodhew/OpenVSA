using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Dialogs;
using OpenVSA.Ui.Dialogs.Pages;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-072</c>: the Analysis (MeasSetup) tab set.
    /// </summary>
    public class AnalysisDialogTests
    {
        [Fact]
        public void TheDialogHasExactlyTheSevenTabsInOrder()
        {
            Sta.Run(() => Assert.Equal(
                new[]
                {
                    "Frequency", "ResBW", "Time", "Detectors", "Conversion", "Average", "Heatmaps",
                },
                new List<string>(Analysis().PageNames)));
        }

        [Fact]
        public void TheTabListIsWhatTheDialogIsBuiltFrom()
        {
            // Not two lists that agree today: an eighth tab added to the dialog without being added
            // to the published list would fail this.
            Sta.Run(() => Assert.Equal(
                new List<string>(AnalysisDialog.TabNames),
                new List<string>(Analysis().PageNames)));
        }

        [Fact]
        public void NoTabIsAPlaceholder()
        {
            // "each is populated — none is a placeholder", which is the half of the criterion that
            // a tab list alone cannot express. Every page reports how many editable rows it has.
            Sta.Run(() =>
            {
                AnalysisDialog dialog = Analysis();
                var counted = new List<string>();

                foreach (AnalysisPage page in dialog.AnalysisPages)
                {
                    Assert.True(
                        page.RowCount >= 2,
                        page.GetType().Name + " has " + page.RowCount +
                        " editable row(s); a tab with fewer is a placeholder.");

                    counted.Add(page.GetType().Name);
                }

                Assert.Equal(AnalysisDialog.TabNames.Count, counted.Count);
            });
        }

        [Fact]
        public void ItObeysTheFrameworkRules()
        {
            // REQ-UI-070 and REQ-UI-071, which the requirement names: modeless, no OK or Apply,
            // and every page reachable in all four layout modes.
            Sta.Run(() =>
            {
                AnalysisDialog dialog = Analysis();

                Assert.Equal(0, dialog.CommitButtonCount);
                Assert.Throws<InvalidOperationException>(() => dialog.ShowDialog());

                foreach (DialogMode mode in DialogModes.All)
                {
                    dialog.Mode = mode;

                    foreach (SettingsPage page in dialog.Pages)
                    {
                        Assert.True(
                            dialog.IsReachable(page.Content),
                            page.Name + " is unreachable under " + DialogModes.NameOf(mode) + ".");
                    }
                }
            });
        }

        [Fact]
        public void AMenuEntryOpensItsOwnTab()
        {
            // The Analysis menu lists the seven individually and each opens this one dialog on its
            // own tab; seven windows editing one measurement is what a tab set exists to avoid.
            Sta.Run(() =>
            {
                AnalysisDialog dialog = Analysis();

                for (int i = 0; i < AnalysisDialog.TabNames.Count; i++)
                {
                    Assert.True(dialog.ShowTab(AnalysisDialog.TabNames[i]));
                    Assert.Equal(i, dialog.SelectedIndex);
                }

                Assert.False(dialog.ShowTab("Nowhere"));
            });
        }

        [Fact]
        public void EveryTabEditsTheSameLiveSettings()
        {
            // One piece of state behind seven tabs: a change made on one is visible on the others
            // without any of them being rebuilt, which is REQ-UI-070 applied within a dialog.
            Sta.Run(() =>
            {
                var settings = new AnalysisSettings();
                AnalysisDialog dialog = Analysis(settings);

                int announced = 0;
                settings.Changed += (sender, e) => announced++;

                settings.SpanHz = 5e6;

                Assert.Equal(1, announced);

                foreach (AnalysisPage page in dialog.AnalysisPages)
                {
                    Assert.Same(settings, page.Settings);
                }
            });
        }

        [Fact]
        public void ARejectedEntryIsReportedAndChangesNothing()
        {
            Sta.Run(() =>
            {
                var settings = new AnalysisSettings();
                var page = new AveragePage(settings);

                int was = settings.AverageCount;

                Assert.Throws<ArgumentOutOfRangeException>(() => settings.AverageCount = 0);
                Assert.Equal(was, settings.AverageCount);
            });
        }

        [Fact]
        public void TheDialogNeedsSettingsToEdit()
        {
            Sta.Run(() =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => new AnalysisDialog(new DialogFrameworkOptions(), null));

                Assert.Throws<ArgumentNullException>(
                    () => new AnalysisDialog(null, new AnalysisSettings()));
            });
        }

        [Fact]
        public void ThePagesRefuseNullSettings()
        {
            Sta.Run(() =>
            {
                Assert.Throws<ArgumentNullException>(() => new FrequencyPage(null));
                Assert.Throws<ArgumentNullException>(() => new ResolutionBandwidthPage(null));
                Assert.Throws<ArgumentNullException>(() => new TimePage(null));
                Assert.Throws<ArgumentNullException>(() => new DetectorPage(null));
                Assert.Throws<ArgumentNullException>(() => new ConversionPage(null));
                Assert.Throws<ArgumentNullException>(() => new AveragePage(null));
                Assert.Throws<ArgumentNullException>(() => new HeatmapPage(null));
            });
        }

        [Fact]
        public void ItRemembersItsOwnModeSeparatelyFromDisplayPreferences()
        {
            // Persist Mode is per dialog. Two dialogs open at once must not share a mode, or the
            // option would be a global with extra steps.
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions { PersistMode = true };

                AnalysisDialog analysis = Analysis(options: options);
                analysis.Mode = DialogMode.ExpandersVertical;

                var preferences = new DisplayPreferencesDialog(
                    options,
                    new ColourPreferences(),
                    new FontPreferences(),
                    new TraceDisplayOptions(),
                    SpectrogramColourMap.Default);

                Assert.Equal(DialogMode.TabsOnTop, preferences.Mode);
                Assert.Equal(DialogMode.ExpandersVertical, options.ModeFor(AnalysisDialog.DialogTitle));
            });
        }

        private static AnalysisDialog Analysis(
            AnalysisSettings settings = null, DialogFrameworkOptions options = null) =>
            new AnalysisDialog(
                options ?? new DialogFrameworkOptions(),
                settings ?? new AnalysisSettings());
    }

    /// <summary>
    /// The live analysis settings the seven tabs of <c>REQ-UI-072</c> edit.
    /// </summary>
    public class AnalysisSettingsTests
    {
        [Fact]
        public void ASettingAnnouncesItselfOnceAndOnlyWhenItMoves()
        {
            var settings = new AnalysisSettings();
            int announced = 0;

            settings.Changed += (sender, e) => announced++;

            settings.SpanHz = 5e6;
            Assert.Equal(1, announced);

            settings.SpanHz = 5e6;
            Assert.Equal(1, announced);
        }

        [Fact]
        public void ABatchCostsOneChangeNotSeven()
        {
            // A tab writing two coupled settings, or a whole state being recalled, must cost one
            // re-plan. Seven would be seven re-arms of the instrument for one user action.
            var settings = new AnalysisSettings();
            int announced = 0;

            settings.Changed += (sender, e) => announced++;

            using (settings.Batch())
            {
                settings.CenterFrequencyHz = 2e9;
                settings.SpanHz = 1e6;
                settings.Window = WindowType.Hann;
                settings.Detector = TraceDetector.Peak;
            }

            Assert.Equal(1, announced);
        }

        [Fact]
        public void ABatchThatChangesNothingAnnouncesNothing()
        {
            var settings = new AnalysisSettings();
            int announced = 0;

            settings.Changed += (sender, e) => announced++;

            using (settings.Batch())
            {
            }

            Assert.Equal(0, announced);
        }

        [Fact]
        public void EverySettingRefusesWhatItCannotHold()
        {
            var settings = new AnalysisSettings();

            Assert.Throws<ArgumentOutOfRangeException>(() => settings.CenterFrequencyHz = 0.0);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.SpanHz = -1.0);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.FrequencyPoints = 777);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.SpanToRatio = 1.0);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Overlap = 1.0);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.GateDelaySeconds = -1e-6);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxTransformLength = 1000);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.AverageCount = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.HeatmapDepth = 1);
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.PersistenceSeconds = 0.0);
        }

        [Fact]
        public void MainTimeIsDerivedFromThePointsAndTheSpan()
        {
            // Three names for two degrees of freedom. Storing all three would let a recalled state
            // disagree with itself.
            var settings = new AnalysisSettings { SpanHz = 10e6, FrequencyPoints = 801 };

            double first = settings.MainTimeSeconds;

            settings.SpanHz = 5e6;

            Assert.Equal(first * 2.0, settings.MainTimeSeconds, 9);
        }

        [Fact]
        public void TheSettingsSurviveAMeasurementState()
        {
            var before = new AnalysisSettings();

            using (before.Batch())
            {
                before.CenterFrequencyHz = 2.4e9;
                before.SpanHz = 1e6;
                before.FrequencyPoints = 1601;
                before.PointsAreAutomatic = false;
                before.ResolutionBandwidthHz = 3e3;
                before.ResolutionBandwidthIsAutomatic = false;
                before.Window = WindowType.Hann;
                before.Overlap = 0.5;
                before.GateEnabled = true;
                before.GateDelaySeconds = 1e-6;
                before.GateLengthSeconds = 5e-6;
                before.Detector = TraceDetector.Average;
                before.MaxTransformLength = 4096;
                before.NoiseCorrection = true;
                before.Averaging = AveragingType.RmsVideo;
                before.AverageCount = 64;
                before.RepeatAverage = true;
                before.Accumulator = TraceAccumulator.Spectrogram;
                before.HeatmapDepth = 512;
                before.HeatmapRangeDb = 60.0;
                before.PersistenceSeconds = 0.25;
            }

            var state = new MeasurementState();
            before.SaveInto(state);

            var after = new AnalysisSettings();
            after.LoadFrom(state);

            Assert.Equal(2.4e9, after.CenterFrequencyHz, 3);
            Assert.Equal(1601, after.FrequencyPoints);
            Assert.Equal(3e3, after.ResolutionBandwidthHz, 6);
            Assert.False(after.ResolutionBandwidthIsAutomatic);
            Assert.Equal(WindowType.Hann, after.Window);
            Assert.Equal(0.5, after.Overlap, 6);
            Assert.True(after.GateEnabled);
            Assert.Equal(TraceDetector.Average, after.Detector);
            Assert.False(after.DetectorIsAutomatic);
            Assert.Equal(4096, after.MaxTransformLength);
            Assert.True(after.NoiseCorrection);
            Assert.Equal(AveragingType.RmsVideo, after.Averaging);
            Assert.Equal(64, after.AverageCount);
            Assert.Equal(TraceAccumulator.Spectrogram, after.Accumulator);
            Assert.Equal(512, after.HeatmapDepth);
            Assert.Equal(60.0, after.HeatmapRangeDb, 6);
            Assert.Equal(0.25, after.PersistenceSeconds, 6);
        }

        [Fact]
        public void ARecallCostsOneChangeNotThirty()
        {
            var settings = new AnalysisSettings();
            var state = new MeasurementState { SpanHz = 1e6, CenterFrequencyHz = 2e9 };

            int announced = 0;
            settings.Changed += (sender, e) => announced++;

            settings.LoadFrom(state);

            Assert.Equal(1, announced);
        }

        [Fact]
        public void AStateThisBuildDisagreesWithCostsThatSettingAndNotTheRecall()
        {
            // A state written by another version. Clamping the setting it disagrees about beats
            // refusing the whole recall, which is what every other loader here does.
            var state = new MeasurementState { SpanHz = -1.0 };

            state.Analysis.AverageCount = 500000;
            state.Analysis.FrequencyPoints = 12345;
            state.Analysis.MaxTransformLength = 999;
            state.Traces[0].SpectrogramDepth = 0;

            var settings = new AnalysisSettings();
            double span = settings.SpanHz;
            int points = settings.FrequencyPoints;

            settings.LoadFrom(state);

            Assert.Equal(span, settings.SpanHz, 6);
            Assert.Equal(points, settings.FrequencyPoints);
            Assert.Equal(AnalysisSettings.MaximumAverageCount, settings.AverageCount);
            Assert.Equal(AnalysisSettings.MinimumHeatmapDepth, settings.HeatmapDepth);
        }

        [Fact]
        public void TheDetectorFollowsTheAveragingUntilItIsChosen()
        {
            // Coupled, as the resolution bandwidth is coupled to the span, and for the same kind of
            // reason: an RMS average is a claim about mean power, and drawing it with a peak
            // detector would put the column's loudest bin on screen beside an annotation claiming a
            // mean.
            var settings = new AnalysisSettings();

            Assert.True(settings.DetectorIsAutomatic);
            Assert.Equal(TraceDetector.Normal, settings.Detector);

            settings.Averaging = AveragingType.RmsVideo;
            Assert.Equal(TraceDetector.Average, settings.Detector);

            settings.Averaging = AveragingType.PeakHold;
            Assert.Equal(TraceDetector.Peak, settings.Detector);

            settings.Averaging = AveragingType.Time;
            Assert.Equal(TraceDetector.Normal, settings.Detector);

            // Choosing one uncouples it, and it then stays chosen through a change of averaging.
            settings.Detector = TraceDetector.NegativePeak;

            Assert.False(settings.DetectorIsAutomatic);

            settings.Averaging = AveragingType.RmsVideo;
            Assert.Equal(TraceDetector.NegativePeak, settings.Detector);

            // And re-coupling takes the averaging's answer back.
            settings.DetectorIsAutomatic = true;
            Assert.Equal(TraceDetector.Average, settings.Detector);
        }

        [Fact]
        public void ChoosingADetectorCostsOneChange()
        {
            // It writes two fields — the detector and the coupling — and a surface that re-planned
            // twice for one click would re-arm the instrument twice.
            var settings = new AnalysisSettings();
            int announced = 0;

            settings.Changed += (sender, e) => announced++;

            settings.Detector = TraceDetector.Peak;

            Assert.Equal(1, announced);
        }

        [Fact]
        public void TheSettingsNeedAStateToReadOrWrite()
        {
            var settings = new AnalysisSettings();

            Assert.Throws<ArgumentNullException>(() => settings.LoadFrom(null));
            Assert.Throws<ArgumentNullException>(() => settings.SaveInto(null));
        }
    }
}
