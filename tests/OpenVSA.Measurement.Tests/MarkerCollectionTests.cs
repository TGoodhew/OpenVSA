using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Markers;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-MKR-002</c> (twenty per trace, traces uncapped), <c>REQ-MKR-004</c> (coupling),
    /// <c>REQ-MKR-005</c> (functions) and <c>REQ-MKR-006</c> (readouts agreeing).
    /// </summary>
    public class MarkerCollectionTests
    {
        private const double StartHz = 1.000e9;

        private readonly ITestOutputHelper _output;

        public MarkerCollectionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ---- REQ-MKR-002: the limit is per trace, and the trace count is not capped ------------

        [Fact]
        public void TwentyMarkersFitOnATraceAndTheTwentyFirstIsRefusedByName()
        {
            var markers = new MarkerCollection();
            MarkerSet set = markers.ForTrace('A');

            for (int i = 0; i < MarkerSet.MaximumPerTrace; i++)
            {
                set.AddNormal(StartHz + i * 1e6);
            }

            Assert.Equal(20, set.Markers.Count);

            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => set.AddNormal(StartHz));

            Assert.Contains("20", error.Message);
            Assert.Contains("REQ-MKR-002", error.Message);

            // Refused, not silently dropped: the set is unchanged.
            Assert.Equal(20, set.Markers.Count);
        }

        [Fact]
        public void TwentyOnEachOfSeveralTracesAllCoexist()
        {
            // The limit is per trace. A collection-wide cap would show up here and nowhere else.
            var markers = new MarkerCollection();

            foreach (char letter in new[] { 'A', 'B', 'C', 'D' })
            {
                MarkerSet set = markers.ForTrace(letter);

                for (int i = 0; i < MarkerSet.MaximumPerTrace; i++)
                {
                    set.AddNormal(StartHz + i * 1e6);
                }
            }

            Assert.Equal(4, markers.TraceCount);

            foreach (char letter in new[] { 'A', 'B', 'C', 'D' })
            {
                Assert.Equal(20, markers.ForTrace(letter).Markers.Count);
            }
        }

        [Fact]
        public void TheTraceCountIsNotCappedByAConstant()
        {
            // The criterion asks for traces to be bounded only by memory. Rather than allocate
            // until the process dies - which tests the machine, not the code - this creates far
            // more traces than any plausible constant and asserts every one took its markers. A
            // fixed ceiling below this would fail; a genuine memory bound is far above it.
            var markers = new MarkerCollection();
            const int traces = 5000;

            for (int i = 0; i < traces; i++)
            {
                markers.ForTrace((char)('A' + i % 26)).GetType();
            }

            // Letters repeat, so count distinctly: what matters is that nothing refused.
            Assert.Equal(26, markers.TraceCount);

            var wide = new MarkerCollection();

            for (int i = 0; i < traces; i++)
            {
                MarkerSet set = wide.ForTrace((char)i);
                set.AddNormal(StartHz);
            }

            Assert.Equal(traces, wide.TraceCount);
            Assert.Equal(traces, wide.Readouts().Count);
        }

        // ---- REQ-MKR-004: coupling ------------------------------------------------------------

        [Fact]
        public void CoupledMarkersMoveToTheSameXNotTheSameIndex()
        {
            // The criterion, with traces of deliberately different point counts over the same span:
            // coupling by sample index passes a test where the lengths match and fails here.
            var markers = new MarkerCollection { Coupled = true };

            markers.Update('A', Comb(801, 12.5e3));
            markers.Update('B', Comb(401, 25e3));

            Marker a3 = Numbered(markers.ForTrace('A'), 3);
            Marker b3 = Numbered(markers.ForTrace('B'), 3);
            Marker b1 = Numbered(markers.ForTrace('B'), 1);

            double target = StartHz + 3.0e6;

            IReadOnlyList<Marker> moved = markers.MoveTo(a3, target);

            Assert.Equal(2, moved.Count);
            Assert.Equal(target, a3.XHz, 3);
            Assert.Equal(target, b3.XHz, 3);

            // Same frequency, different bin: 240 on A, 120 on B.
            Assert.Equal(240, a3.IndexIn(markers.FrameOf('A')));
            Assert.Equal(120, b3.IndexIn(markers.FrameOf('B')));

            // And markers of other numbers stayed where they were.
            Assert.Equal(StartHz + 1.0e6, b1.XHz, 3);
        }

        [Fact]
        public void IncommensurateTracesAreLeftAloneRatherThanCoupled()
        {
            // A time trace has no 1.003 GHz. Moving its marker to that number is arithmetically
            // valid and physically meaningless, which is worse than not moving it.
            var markers = new MarkerCollection { Coupled = true };

            markers.Update('A', Comb(801, 12.5e3));
            markers.Update('T', Comb(801, 12.5e3), MarkerAxis.Time);

            Marker a3 = Numbered(markers.ForTrace('A'), 3);
            Marker t3 = Numbered(markers.ForTrace('T'), 3);

            double before = t3.XHz;

            IReadOnlyList<Marker> moved = markers.MoveTo(a3, StartHz + 3.0e6);

            Assert.Single(moved);
            Assert.Equal(before, t3.XHz, 6);
        }

        [Fact]
        public void ATraceThatDoesNotReachTheNewPositionKeepsItsMarker()
        {
            // A trace zoomed into a narrower span simply does not cover the position. Clamping its
            // marker to an edge would park it on a real value at a place nobody put it.
            var markers = new MarkerCollection { Coupled = true };

            markers.Update('A', Comb(801, 12.5e3));
            markers.Update('Z', Comb(101, 1e3));    // 1.0000 to 1.0001 GHz only

            Marker a3 = Numbered(markers.ForTrace('A'), 3);
            Marker z3 = Numbered(markers.ForTrace('Z'), 3);

            double before = z3.XHz;

            markers.MoveTo(a3, StartHz + 5.0e6);

            Assert.Equal(before, z3.XHz, 6);
            Assert.False(markers.Covers('Z', StartHz + 5.0e6));
        }

        [Fact]
        public void WithCouplingOffNoMarkerButTheDraggedOneMoves()
        {
            var markers = new MarkerCollection { Coupled = false };

            markers.Update('A', Comb(801, 12.5e3));
            markers.Update('B', Comb(801, 12.5e3));

            Marker a3 = Numbered(markers.ForTrace('A'), 3);
            Marker b3 = Numbered(markers.ForTrace('B'), 3);

            double before = b3.XHz;

            IReadOnlyList<Marker> moved = markers.MoveTo(a3, StartHz + 3.0e6);

            Assert.Single(moved);
            Assert.Equal(before, b3.XHz, 6);
        }

        // ---- REQ-MKR-005: peak tracking and the marker-to-parameter functions ------------------

        [Fact]
        public void ATrackingMarkerFollowsADriftingTone()
        {
            var markers = new MarkerCollection();

            SpectrumFrame first = Tones(801, 12.5e3, new[] { 2.0e6, 5.0e6 }, new[] { -20.0, -30.0 });
            markers.Update('A', first);

            MarkerSet set = markers.ForTrace('A');
            Marker marker = set.AddNormal(StartHz + 5.0e6);
            marker.TracksPeak = true;

            Assert.Equal(StartHz + 5.0e6, marker.Read(first).XHz, 3);

            // The second tone drifts up by four bins; the first stays put and is still the larger.
            SpectrumFrame drifted =
                Tones(801, 12.5e3, new[] { 2.0e6, 5.05e6 }, new[] { -20.0, -30.0 });

            markers.Update('A', drifted);

            _output.WriteLine("marker landed at " + ((marker.XHz - StartHz) / 1e6) + " MHz");

            // It followed its own tone rather than jumping to the taller one.
            Assert.Equal(StartHz + 5.05e6, marker.XHz, 3);
        }

        [Fact]
        public void AMarkerThatDoesNotTrackStaysWhereItWasPut()
        {
            var markers = new MarkerCollection();

            markers.Update('A', Tones(801, 12.5e3, new[] { 5.0e6 }, new[] { -20.0 }));

            Marker marker = markers.ForTrace('A').AddNormal(StartHz + 5.0e6);

            markers.Update('A', Tones(801, 12.5e3, new[] { 5.05e6 }, new[] { -20.0 }));

            Assert.Equal(StartHz + 5.0e6, marker.XHz, 3);
        }

        [Fact]
        public void AFixedMarkerDoesNotTrackEvenWhenAskedTo()
        {
            // Locking the value it reads is what a fixed marker is; a locked value that moved
            // would be neither.
            var markers = new MarkerCollection();

            markers.Update('A', Tones(801, 12.5e3, new[] { 5.0e6 }, new[] { -20.0 }));

            Marker marker = markers.ForTrace('A').AddFixed(StartHz + 5.0e6, -20.0);
            marker.TracksPeak = true;

            markers.Update('A', Tones(801, 12.5e3, new[] { 5.05e6 }, new[] { -20.0 }));

            Assert.Equal(StartHz + 5.0e6, marker.XHz, 3);
            Assert.Equal(-20.0, marker.Read(markers.FrameOf('A')).YDbm, 6);
        }

        [Fact]
        public void MarkerToCentreFrequencySetsTheCentreExactly()
        {
            var target = new Target();
            var markers = new MarkerCollection();
            SpectrumFrame frame = Tones(801, 12.5e3, new[] { 3.0e6 }, new[] { -20.0 });

            markers.Update('A', frame);

            Marker marker = markers.ForTrace('A').AddNormal(StartHz + 3.0e6);

            double written = MarkerFunctions.ToCenterFrequency(marker, frame, target);

            Assert.Equal(StartHz + 3.0e6, written, 3);
            Assert.Equal(written, target.CenterHz, 9);
        }

        [Fact]
        public void MarkerToReferenceLevelSetsTheLevel()
        {
            var target = new Target();
            SpectrumFrame frame = Tones(801, 12.5e3, new[] { 3.0e6 }, new[] { -17.5 });

            var markers = new MarkerCollection();
            markers.Update('A', frame);

            Marker marker = markers.ForTrace('A').AddNormal(StartHz + 3.0e6);

            double written = MarkerFunctions.ToReferenceLevel(marker, frame, target);

            Assert.Equal(-17.5, written, 4);
            Assert.Equal(written, target.ReferenceDbm, 9);
        }

        [Fact]
        public void CopyValueToParameterWritesTheValueTheMarkerShows()
        {
            var target = new Target();
            SpectrumFrame frame = Tones(801, 12.5e3, new[] { 3.0e6 }, new[] { -17.5 });

            var markers = new MarkerCollection();
            markers.Update('A', frame);

            Marker marker = markers.ForTrace('A').AddNormal(StartHz + 3.0e6);

            double written = MarkerFunctions.CopyValueToParameter(
                marker, frame, "TriggerLevel", target);

            Assert.Equal(-17.5, written, 4);
            Assert.Equal(-17.5, target.Parameters["TriggerLevel"], 4);
        }

        [Fact]
        public void ADeltaMarkerTunesToItsOwnPositionNotToItsDifference()
        {
            // Tuning to a difference is arithmetically possible and always wrong.
            var target = new Target();
            SpectrumFrame frame =
                Tones(801, 12.5e3, new[] { 1.0e6, 3.0e6 }, new[] { -20.0, -30.0 });

            var markers = new MarkerCollection();
            markers.Update('A', frame);

            MarkerSet set = markers.ForTrace('A');
            Marker reference = set.AddNormal(StartHz + 1.0e6);
            Marker delta = set.AddDelta(StartHz + 3.0e6, reference);

            // Its reading is a 2 MHz difference...
            Assert.Equal(2.0e6, delta.Read(frame).XHz, 3);

            // ...and tuning to it goes to 1.003 GHz, not to 2 MHz.
            Assert.Equal(
                StartHz + 3.0e6, MarkerFunctions.ToCenterFrequency(delta, frame, target), 3);
            Assert.Equal(
                -30.0, MarkerFunctions.ToReferenceLevel(delta, frame, target), 4);

            // But "copy value" copies the value on screen, which for a delta is the difference.
            Assert.Equal(
                -10.0,
                MarkerFunctions.CopyValueToParameter(delta, frame, "Threshold", target),
                4);
        }

        [Fact]
        public void AMarkerWithNoReadingIsRefusedRatherThanCopyingNothing()
        {
            var target = new Target();
            var marker = new MarkerSet('A').AddNormal(StartHz);

            Assert.Throws<InvalidOperationException>(
                () => MarkerFunctions.ToCenterFrequency(marker, null, target));
            Assert.Throws<InvalidOperationException>(
                () => MarkerFunctions.CopyValueToParameter(marker, null, "P", target));
        }

        // ---- REQ-MKR-006: the two surfaces cannot disagree -------------------------------------

        [Fact]
        public void TheAboveGridReadoutAndTheMarkersWindowRowAgree()
        {
            // "Two independently computed readouts drifting apart is the failure this guards
            // against." They agree because there is one readout and both surfaces render it.
            var markers = new MarkerCollection();
            SpectrumFrame frame =
                Tones(801, 12.5e3, new[] { 2.0e6, 6.0e6 }, new[] { -20.0, -35.0 });

            markers.Update('A', frame);
            markers.Update('B', frame);

            MarkerSet a = markers.ForTrace('A');
            Marker one = a.AddNormal(StartHz + 2.0e6);
            markers.ForTrace('B').AddNormal(StartHz + 6.0e6);

            a.Select(one);
            markers.ActiveTrace = 'A';

            MarkerReadout above = markers.ActiveReadout;
            MarkerReadout row = markers.Readouts()
                .Single(r => r.TraceLetter == 'A' && r.Marker.Number == one.Number);

            Assert.Equal(row.Text, above.Text);
            Assert.Equal(row.Reading.XHz, above.Reading.XHz, 9);
            Assert.Equal(row.Reading.YDbm, above.Reading.YDbm, 9);

            // And after a move they still agree.
            markers.MoveTo(one, StartHz + 6.0e6);

            Assert.Equal(
                markers.Readouts()
                    .Single(r => r.TraceLetter == 'A' && r.Marker.Number == one.Number).Text,
                markers.ActiveReadout.Text);
        }

        [Fact]
        public void TheAboveGridReadoutFollowsTheActiveMarker()
        {
            var markers = new MarkerCollection();
            SpectrumFrame frame =
                Tones(801, 12.5e3, new[] { 2.0e6, 6.0e6 }, new[] { -20.0, -35.0 });

            markers.Update('A', frame);
            MarkerSet set = markers.ForTrace('A');

            Marker one = set.AddNormal(StartHz + 2.0e6);
            Marker two = set.AddNormal(StartHz + 6.0e6);

            markers.ActiveTrace = 'A';

            // AddNormal selects what it adds, so the second is active.
            Assert.Equal(two.Number, markers.ActiveReadout.Marker.Number);

            set.Select(one);

            Assert.Equal(one.Number, markers.ActiveReadout.Marker.Number);
            Assert.True(markers.ActiveReadout.IsActive);
        }

        [Fact]
        public void TheMarkersWindowListsEveryMarkerOnEveryTrace()
        {
            // Not only the active trace: a marker on a trace you are not looking at is exactly the
            // one you forget about.
            var markers = new MarkerCollection();
            SpectrumFrame frame = Tones(801, 12.5e3, new[] { 2.0e6 }, new[] { -20.0 });

            markers.Update('A', frame);
            markers.Update('B', frame);
            markers.Update('C', frame);

            markers.ForTrace('A').AddNormal(StartHz + 1.0e6);
            markers.ForTrace('A').AddNormal(StartHz + 2.0e6);
            markers.ForTrace('B').AddNormal(StartHz + 3.0e6);
            markers.ForTrace('C').AddNormal(StartHz + 4.0e6);

            markers.ActiveTrace = 'A';

            IReadOnlyList<MarkerReadout> rows = markers.Readouts();

            Assert.Equal(4, rows.Count);
            Assert.Equal(new[] { 'A', 'A', 'B', 'C' }, rows.Select(r => r.TraceLetter));
            Assert.Single(rows, r => r.IsActive);
        }

        [Fact]
        public void AReadoutSaysSoWhenThereIsNothingToRead()
        {
            var markers = new MarkerCollection();
            Marker marker = markers.ForTrace('A').AddNormal(StartHz);

            Assert.Contains("--", markers.ReadoutFor(marker).Text);
        }

        [Fact]
        public void ATimeAxisReadoutIsInSecondsNotHertz()
        {
            var markers = new MarkerCollection();

            markers.Update('T', Comb(801, 1e-6), MarkerAxis.Time);

            Marker marker = markers.ForTrace('T').AddNormal(StartHz + 100e-6);

            Assert.Contains("us", markers.ReadoutFor(marker).Text);
            Assert.DoesNotContain("MHz", markers.ReadoutFor(marker).Text);
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            var markers = new MarkerCollection();

            Assert.Throws<ArgumentNullException>(() => markers.Update('A', null));
            Assert.Throws<ArgumentNullException>(() => markers.MoveTo(null, StartHz));
            Assert.Throws<ArgumentNullException>(() => markers.ReadoutFor(null));
            Assert.Throws<ArgumentException>(() => markers.ActiveTrace = 'Q');

            Marker marker = markers.ForTrace('A').AddNormal(StartHz);

            Assert.Throws<ArgumentOutOfRangeException>(() => markers.MoveTo(marker, double.NaN));
        }

        // ---- Helpers ---------------------------------------------------------------------------

        /// <summary>The marker of a given number on a set, created with the ones below it.</summary>
        private static Marker Numbered(MarkerSet set, int number)
        {
            while (set.Markers.Count < number)
            {
                set.AddNormal(StartHz + set.Markers.Count * 1e6 + 1e6);
            }

            return set.Markers.Single(m => m.Number == number);
        }

        /// <summary>A flat trace, for tests that only care about the axis.</summary>
        private static SpectrumFrame Comb(int points, double binHz)
        {
            var levels = new float[points];

            for (int i = 0; i < points; i++)
            {
                levels[i] = -90.0f;
            }

            return SpectrumFrame.FromLevels(levels, StartHz, binHz, WindowType.Uniform, 1.0);
        }

        /// <summary>A trace with tones at stated offsets and levels, on a flat floor.</summary>
        private static SpectrumFrame Tones(
            int points, double binHz, double[] offsetsHz, double[] levelsDbm)
        {
            var levels = new float[points];

            for (int i = 0; i < points; i++)
            {
                levels[i] = -90.0f;
            }

            for (int t = 0; t < offsetsHz.Length; t++)
            {
                int index = (int)Math.Round(offsetsHz[t] / binHz);

                if (index >= 0 && index < points)
                {
                    levels[index] = (float)levelsDbm[t];
                }
            }

            return SpectrumFrame.FromLevels(levels, StartHz, binHz, WindowType.Uniform, 1.0);
        }

        private sealed class Target : IMarkerParameterTarget
        {
            public double CenterHz { get; private set; } = double.NaN;

            public double ReferenceDbm { get; private set; } = double.NaN;

            public Dictionary<string, double> Parameters { get; } =
                new Dictionary<string, double>(StringComparer.Ordinal);

            public void SetCenterFrequency(double hz) => CenterHz = hz;

            public void SetReferenceLevel(double dbm) => ReferenceDbm = dbm;

            public void SetParameter(string parameter, double value) =>
                Parameters[parameter] = value;
        }
    }
}
