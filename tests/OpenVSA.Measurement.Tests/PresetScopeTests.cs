using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Hal;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Limits;
using OpenVSA.Measurement.State;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-UI-061</c>'s nine presets, and the one thing none of them may touch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The criterion: "a test alters hardware configuration, invokes each Preset variant, and
    /// asserts the hardware setup is unchanged while the targeted settings are reset, since that
    /// separation is called out explicitly and is easy to lose."
    /// </para>
    /// <para>
    /// <strong>Easy to lose because the two live in the same object.</strong> The frequency
    /// reference and the source configuration are fields of the same measurement state as the span,
    /// the trigger and the trace formats. Nothing about the shape of the data keeps them apart —
    /// only <see cref="Presets.Apply"/> does, and only these tests say whether it still does.
    /// </para>
    /// </remarks>
    public class PresetScopeTests
    {
        [Fact]
        public void NoVariantResetsTheHardware()
        {
            // Structural, before any behaviour: PresetCategory.Hardware exists so that the
            // separation can be named, and no variant may name it.
            foreach (PresetVariant variant in Presets.Variants)
            {
                Assert.False(
                    Presets.Has(Presets.CategoriesOf(variant), PresetCategory.Hardware),
                    Presets.NameOf(variant) + " claims to reset the hardware setup, and " +
                    "REQ-UI-061 says no preset does.");
            }
        }

        [Fact]
        public void EveryVariantLeavesTheHardwareSetupAlone()
        {
            // The behavioural half, over all nine: alter the hardware configuration a state can
            // carry, apply the preset, and it has to come back untouched.
            foreach (PresetVariant variant in Presets.Variants)
            {
                ApplicationState altered = Altered();
                MeasurementState after = Presets.Apply(variant, altered).Measurements[0];

                string where = Presets.NameOf(variant);

                Assert.True(after.Input.ExternalReference, where + " changed the frequency reference.");
                Assert.True(after.Source.IsEnabled, where + " turned the source off.");
                Assert.Equal(2.5e9, after.Source.FrequencyHz);
                Assert.Equal(-7.5, after.Source.LevelDbm);
                Assert.Equal("Two tone", after.Source.Waveform);
            }
        }

        [Fact]
        public void EachVariantResetsWhatItNamesAndNothingElse()
        {
            foreach (PresetVariant variant in Presets.Variants)
            {
                PresetCategory scope = Presets.CategoriesOf(variant);
                MeasurementState after = Presets.Apply(variant, Altered()).Measurements[0];
                var defaults = new MeasurementState();

                string where = Presets.NameOf(variant) + ": ";

                if (Presets.Has(scope, PresetCategory.Measurement))
                {
                    Assert.Equal(defaults.CenterFrequencyHz, after.CenterFrequencyHz);
                    Assert.Equal(defaults.SpanHz, after.SpanHz);
                    Assert.Equal(defaults.ResolutionBandwidthHz, after.ResolutionBandwidthHz);
                    Assert.Equal(defaults.Analysis.Window, after.Analysis.Window);
                    Assert.Equal(defaults.Analysis.Averaging, after.Analysis.Averaging);
                    Assert.Equal(defaults.Trigger.Style, after.Trigger.Style);
                    Assert.Equal(defaults.Input.RangeDbm, after.Input.RangeDbm);
                }
                else
                {
                    Assert.True(Math.Abs(after.CenterFrequencyHz - 2.4e9) < 1.0, where + "centre");
                    Assert.True(Math.Abs(after.SpanHz - 5e6) < 1.0, where + "span");
                    Assert.Equal(WindowType.Hann, after.Analysis.Window);
                    Assert.Equal(TriggerStyle.External, after.Trigger.Style);
                    Assert.Equal(-13.0, after.Input.RangeDbm);
                }

                if (Presets.Has(scope, PresetCategory.Kind))
                {
                    Assert.Equal(defaults.Kind, after.Kind);
                }
                else
                {
                    Assert.Equal(MeasurementKind.VectorAnalysis, after.Kind);
                }

                if (Presets.Has(scope, PresetCategory.Traces))
                {
                    Assert.Single(after.Traces);
                    Assert.Equal(defaults.Traces[0].Format, after.Traces[0].Format);
                    Assert.Equal(defaults.Traces[0].DecibelsPerDivision, after.Traces[0].DecibelsPerDivision);
                }
                else
                {
                    Assert.Equal(2, after.Traces.Count);
                    Assert.Equal(TraceFormat.WrappedPhase, after.Traces[0].Format);
                }

                if (Presets.Has(scope, PresetCategory.Markers))
                {
                    Assert.Empty(after.Markers);
                }
                else
                {
                    Assert.Equal(2, after.Markers.Count);
                }

                if (Presets.Has(scope, PresetCategory.Limits))
                {
                    Assert.Empty(after.LimitTests);
                }
                else
                {
                    Assert.Single(after.LimitTests);
                }
            }
        }

        [Fact]
        public void SetupTakesTheSessionBackToOneMeasurement()
        {
            ApplicationState altered = Altered();
            altered.Measurements.Add(new MeasurementState { ContextName = "Measurement 2" });

            Assert.Equal(2, altered.Measurements.Count);

            Assert.Single(Presets.Apply(PresetVariant.Setup, altered).Measurements);
            Assert.Single(Presets.Apply(PresetVariant.FactoryDefaults, altered).Measurements);

            // And the ones that are not about the session leave the second measurement alone.
            Assert.Equal(2, Presets.Apply(PresetVariant.Traces, altered).Measurements.Count);
            Assert.Equal(2, Presets.Apply(PresetVariant.Measurement, altered).Measurements.Count);
        }

        [Fact]
        public void TheContextNameSurvivesEveryVariant()
        {
            // REQ-STA-004 matches a recalled state to a context by name, not by position. A preset
            // that renamed the measurement would make the next recall fail to find it.
            foreach (PresetVariant variant in Presets.Variants)
            {
                ApplicationState altered = Altered();
                altered.Measurements[0].ContextName = "Channel power";

                Assert.Equal(
                    "Channel power",
                    Presets.Apply(variant, altered).Measurements[0].ContextName);
            }
        }

        [Fact]
        public void FactoryDefaultsResetsEverythingExceptTheHardware()
        {
            MeasurementState after =
                Presets.Apply(PresetVariant.FactoryDefaults, Altered()).Measurements[0];

            var defaults = new MeasurementState();

            Assert.Equal(defaults.CenterFrequencyHz, after.CenterFrequencyHz);
            Assert.Equal(defaults.Kind, after.Kind);
            Assert.Single(after.Traces);
            Assert.Empty(after.Markers);
            Assert.Empty(after.LimitTests);

            Assert.True(after.Input.ExternalReference);
            Assert.True(after.Source.IsEnabled);
        }

        [Fact]
        public void TheThreeMeasurementVariantsAreNotThreeNamesForOneThing()
        {
            // Measurement and Measurement to Standard keep the measurement kind; Measurement to
            // Defaults does not. If all three had the same scope, two of them would be items a user
            // could not tell apart, which is worse than one item.
            PresetCategory measurement = Presets.CategoriesOf(PresetVariant.Measurement);
            PresetCategory defaults = Presets.CategoriesOf(PresetVariant.MeasurementToDefaults);

            Assert.False(Presets.Has(measurement, PresetCategory.Kind));
            Assert.True(Presets.Has(defaults, PresetCategory.Kind));

            Assert.Equal(
                MeasurementKind.VectorAnalysis,
                Presets.Apply(PresetVariant.Measurement, Altered()).Measurements[0].Kind);

            Assert.Equal(
                MeasurementKind.Spectrum,
                Presets.Apply(PresetVariant.MeasurementToDefaults, Altered()).Measurements[0].Kind);
        }

        [Fact]
        public void EveryVariantIsNamedAsTheRequirementNamesIt()
        {
            var expected = new List<string>
            {
                "Measurement", "Measurement to Standard", "Measurement to Defaults", "Setup",
                "Traces", "Application and Traces", "Display Preferences", "Toolbars",
                "Factory Defaults",
            };

            Assert.Equal(expected, Presets.Variants.Select(Presets.NameOf).ToList());
        }

        [Fact]
        public void AnUnknownVariantIsRefusedRatherThanFiledUnderSomething()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Presets.NameOf((PresetVariant)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => Presets.CategoriesOf((PresetVariant)99));
            Assert.Throws<ArgumentNullException>(() => Presets.Apply(PresetVariant.Setup, null));
        }

        /// <summary>
        /// A state with everything moved off its default, hardware included.
        /// </summary>
        private static ApplicationState Altered()
        {
            var state = new ApplicationState();

            var measurement = new MeasurementState
            {
                ContextName = "Measurement 1",
                Kind = MeasurementKind.VectorAnalysis,
                CenterFrequencyHz = 2.4e9,
                SpanHz = 5e6,
                ResolutionBandwidthHz = 3e3,
                ResolutionBandwidthIsAutomatic = false,
            };

            measurement.Analysis.Window = WindowType.Hann;
            measurement.Analysis.Averaging = AveragingType.RmsVideo;
            measurement.Analysis.AverageCount = 64;

            measurement.Trigger.Style = TriggerStyle.External;
            measurement.Trigger.LevelDbm = -12.0;

            measurement.Input.RangeDbm = -13.0;
            measurement.Input.RangeIsAutomatic = false;

            // The hardware setup: the two parts of it a state carries at all.
            measurement.Input.ExternalReference = true;
            measurement.Source.IsEnabled = true;
            measurement.Source.FrequencyHz = 2.5e9;
            measurement.Source.LevelDbm = -7.5;
            measurement.Source.Waveform = "Two tone";

            measurement.Traces.Clear();
            measurement.Traces.Add(new TraceDisplayState
            {
                Trace = "A",
                Format = TraceFormat.WrappedPhase,
                DecibelsPerDivision = 2.0,
                TopDbm = -20.0,
            });

            measurement.Traces.Add(new TraceDisplayState { Trace = "B" });

            measurement.Windows.Clear();
            measurement.Windows.Add(new TraceWindowState { Trace = "A" });
            measurement.Windows.Add(new TraceWindowState { Trace = "B", Row = 1 });

            measurement.Markers.Add(new MarkerState { Number = 1, XHz = 2.4e9 });
            measurement.Markers.Add(new MarkerState { Number = 2, XHz = 2.401e9, Type = "Delta" });

            measurement.LimitTests.Add(new LimitTestState
            {
                Name = "Mask",
                Lines = { new LimitLineState { Name = "Upper", Side = LimitSide.Upper } },
            });

            state.Measurements.Add(measurement);

            return state;
        }
    }
}
