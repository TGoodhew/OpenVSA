using System;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Markers;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-MKR-001</c>: the three marker types and what distinguishes them, plus
    /// <c>REQ-UI-031</c>'s label format and <c>REQ-MKR-002</c>'s per-trace limit.
    /// </summary>
    public class MarkerTests
    {
        // ---- REQ-MKR-001: the three types ------------------------------------------------------

        [Fact]
        public void ANormalMarkersReadoutTracksItsDataPointAsTheTraceUpdates()
        {
            var set = new MarkerSet();
            Marker marker = set.AddNormal(1.0e9 + 100e3);

            Assert.Equal(-40.0, marker.Read(Frame(-40.0)).YDbm, 6);

            // Same axis, new data: the marker has not moved, but what it reads has.
            Assert.Equal(-12.0, marker.Read(Frame(-12.0)).YDbm, 6);
            Assert.Equal(1.0e9 + 100e3, marker.Read(Frame(-12.0)).XHz, 3);
        }

        [Fact]
        public void AFixedMarkerReadsWhatItReadWhenItWasPlaced()
        {
            // The distinguishing property: neither its X nor its Y moves when the trace updates,
            // which is what makes it usable as a reference to measure against.
            var set = new MarkerSet();
            Marker marker = set.AddFixed(1.0e9 + 100e3, -40.0);

            MarkerReading before = marker.Read(Frame(-40.0));
            MarkerReading after = marker.Read(Frame(-12.0));

            Assert.Equal(before.XHz, after.XHz, 6);
            Assert.Equal(-40.0, after.YDbm, 6);
        }

        [Fact]
        public void ADeltaMarkerReadsTheDifferenceFromItsReference()
        {
            var set = new MarkerSet();
            Marker reference = set.AddNormal(1.0e9 + 100e3);
            Marker delta = set.AddDelta(1.0e9 + 300e3, reference);

            SpectrumFrame frame = Frame(-40.0, peakIndex: 30, peakLevel: -10.0);
            MarkerReading reading = delta.Read(frame);

            Assert.True(reading.IsValid);
            Assert.Equal(200e3, reading.XHz, 3);
            Assert.Equal(30.0, reading.YDbm, 6);
        }

        [Fact]
        public void ChangingTheReferenceChangesTheDeltaReadout()
        {
            var set = new MarkerSet();
            Marker first = set.AddNormal(1.0e9 + 100e3);
            Marker second = set.AddFixed(1.0e9 + 200e3, -70.0);
            Marker delta = set.AddDelta(1.0e9 + 300e3, first);

            SpectrumFrame frame = Frame(-40.0);
            double fromFirst = delta.Read(frame).YDbm;

            set.Rehome(delta, second);
            double fromSecond = delta.Read(frame).YDbm;

            Assert.Equal(0.0, fromFirst, 6);
            Assert.Equal(30.0, fromSecond, 6);
        }

        [Fact]
        public void AMarkerOutsideTheTraceReadsNothing()
        {
            var set = new MarkerSet();
            Marker marker = set.AddNormal(2.0e9);

            Assert.False(marker.Read(Frame(-40.0)).IsValid);
        }

        // ---- REQ-UI-031: the delta label -------------------------------------------------------

        [Fact]
        public void ASameTraceDeltaLabelHasNoTraceLetter()
        {
            var set = new MarkerSet('A');
            Marker first = set.AddNormal(1.0e9);
            set.AddNormal(1.0e9);
            Marker third = set.AddDelta(1.0e9, first);

            // Exactly "3Δ1" - emitting the trace letter unconditionally is the obvious
            // implementation and is wrong here, which is why this is asserted as a literal.
            Assert.Equal("3Δ1", third.Label);
            Assert.Equal("Mkr 3Δ1", third.WindowLabel);
        }

        [Fact]
        public void ACrossTraceDeltaLabelCarriesTheReferenceTraceLetter()
        {
            var traceA = new MarkerSet('A');
            var traceB = new MarkerSet('B');

            Marker onB = traceB.AddNormal(1.0e9);
            traceA.AddNormal(1.0e9);
            Marker delta = traceA.AddDelta(1.0e9, onB);

            Assert.Equal("2ΔB1", delta.Label);
            Assert.Equal("Mkr 2ΔB1", delta.WindowLabel);
        }

        [Fact]
        public void ANormalMarkersLabelIsItsNumber()
        {
            var set = new MarkerSet();
            Assert.Equal("1", set.AddNormal(1.0e9).Label);
        }

        // ---- REQ-MKR-001: reference integrity --------------------------------------------------

        [Fact]
        public void AMarkerThatAnotherMeasuresFromCannotBeDeleted()
        {
            var set = new MarkerSet();
            Marker reference = set.AddNormal(1.0e9);
            set.AddDelta(1.0e9, reference);

            InvalidOperationException refusal =
                Assert.Throws<InvalidOperationException>(() => set.Remove(reference));

            // Named, not silent: the message says which marker is in the way and what to do.
            Assert.Contains("marker 2", refusal.Message);
            Assert.Equal(2, set.Markers.Count);
        }

        [Fact]
        public void DeletingTheDependantFirstThenTheReferenceIsAllowed()
        {
            var set = new MarkerSet();
            Marker reference = set.AddNormal(1.0e9);
            Marker delta = set.AddDelta(1.0e9, reference);

            set.Remove(delta);
            set.Remove(reference);

            Assert.Empty(set.Markers);
        }

        [Fact]
        public void ADeltaMarkerMustHaveAReference()
        {
            var set = new MarkerSet();
            Assert.Throws<ArgumentNullException>(() => set.AddDelta(1.0e9, null));
        }

        [Fact]
        public void ReHomingIsRefusedForAMarkerThatIsNotADelta_OrForItself()
        {
            var set = new MarkerSet();
            Marker normal = set.AddNormal(1.0e9);
            Marker delta = set.AddDelta(1.0e9, normal);

            Assert.Throws<ArgumentException>(() => set.Rehome(normal, delta));
            Assert.Throws<ArgumentException>(() => set.Rehome(delta, delta));
        }

        // ---- REQ-MKR-002: twenty per trace -----------------------------------------------------

        [Fact]
        public void TheTwentyFirstMarkerIsRefusedWithTheLimitNamed()
        {
            var set = new MarkerSet();

            for (int i = 0; i < MarkerSet.MaximumPerTrace; i++)
            {
                set.AddNormal(1.0e9);
            }

            InvalidOperationException refusal =
                Assert.Throws<InvalidOperationException>(() => set.AddNormal(1.0e9));

            Assert.Contains("20", refusal.Message);
            Assert.Equal(20, set.Markers.Count);
        }

        [Fact]
        public void TheLimitIsPerTrace()
        {
            var traceA = new MarkerSet('A');
            var traceB = new MarkerSet('B');

            for (int i = 0; i < MarkerSet.MaximumPerTrace; i++)
            {
                traceA.AddNormal(1.0e9);
                traceB.AddNormal(1.0e9);
            }

            Assert.Equal(20, traceA.Markers.Count);
            Assert.Equal(20, traceB.Markers.Count);
        }

        [Fact]
        public void ADeletedNumberIsReusedRatherThanSkipped()
        {
            var set = new MarkerSet();
            set.AddNormal(1.0e9);
            Marker second = set.AddNormal(1.0e9);
            set.AddNormal(1.0e9);

            set.Remove(second);

            Assert.Equal(2, set.AddNormal(1.0e9).Number);
        }

        // ---- REQ-UI-030: selection ------------------------------------------------------------

        [Fact]
        public void TheNewestMarkerIsSelected_AndOnlyOneEverIs()
        {
            var set = new MarkerSet();
            Marker first = set.AddNormal(1.0e9);
            Marker second = set.AddNormal(1.0e9);

            Assert.False(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.Same(second, set.Selected);

            set.Select(first);

            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);
        }

        [Fact]
        public void DeletingTheSelectedMarkerSelectsAnother()
        {
            var set = new MarkerSet();
            Marker first = set.AddNormal(1.0e9);
            Marker second = set.AddNormal(1.0e9);

            set.Remove(second);

            Assert.Same(first, set.Selected);
        }

        [Fact]
        public void AMarkerFromAnotherTraceCannotBeSelectedHere()
        {
            var traceA = new MarkerSet('A');
            var traceB = new MarkerSet('B');
            Marker onB = traceB.AddNormal(1.0e9);

            Assert.Throws<ArgumentException>(() => traceA.Select(onB));
        }

        /// <summary>A 101-point frame from 1 GHz with a flat floor and one peak.</summary>
        private static SpectrumFrame Frame(double floorDbm, int peakIndex = -1, double peakLevel = 0.0)
        {
            var levels = new float[101];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = (float)floorDbm;
            }

            if (peakIndex >= 0)
            {
                levels[peakIndex] = (float)peakLevel;
            }

            return SpectrumFrame.FromLevels(levels, 1.0e9, 10e3, WindowType.FlatTop, 3.8194);
        }
    }
}
