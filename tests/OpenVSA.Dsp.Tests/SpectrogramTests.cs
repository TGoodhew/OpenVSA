using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-043</c>'s spectrogram and <c>REQ-TRC-001a</c>'s separation of accumulators from
    /// formats.
    /// </summary>
    public class SpectrogramTests
    {
        private const double RateHz = 15e6;
        private const double CenterHz = 1e9;
        private const int Samples = 2048;

        private readonly ITestOutputHelper _output;

        public SpectrogramTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ASweptToneRendersAsADiagonalRidge()
        {
            // The requirement's own criterion, and the reason it is worded as it is: it verifies
            // that the time and frequency axes are oriented and scaled correctly rather than merely
            // that something drew. A ridge that ran the other way, or a history indexed newest
            // first, would fail here and pass any test that only counted rows.
            const int rows = 40;
            const double firstHz = -3e6;
            const double lastHz = 3e6;

            var spectrogram = new Spectrogram(rows);
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int r = 0; r < rows; r++)
            {
                double offsetHz = firstHz + (lastHz - firstHz) * r / (rows - 1);

                spectrogram.Add(Spectrum(offsetHz, start.AddMilliseconds(r * 10)));
            }

            Assert.Equal(rows, spectrogram.RowCount);

            double worstBins = 0.0;

            for (int r = 0; r < rows; r++)
            {
                SpectrumFrame row = spectrogram.Row(r);
                int peak = row.IndexOfPeak();

                Assert.True(peak >= 0, "row " + r + " had no peak.");

                double expected = CenterHz + firstHz + (lastHz - firstHz) * r / (rows - 1);
                double bins = Math.Abs(row.FrequencyAt(peak) - expected) / row.BinWidthHz;

                worstBins = Math.Max(worstBins, bins);
            }

            _output.WriteLine("worst row is " + worstBins.ToString("0.000") + " bins out");

            // Within one bin, which is the criterion.
            Assert.True(worstBins <= 1.0, "worst row was " + worstBins + " bins out.");

            // And the ridge ascends: the oldest row is the lowest frequency.
            Assert.True(
                spectrogram.Row(0).FrequencyAt(spectrogram.Row(0).IndexOfPeak()) <
                spectrogram.Newest.FrequencyAt(spectrogram.Newest.IndexOfPeak()),
                "the ridge runs the wrong way, so row 0 is not the oldest.");
        }

        [Fact]
        public void RowsBeyondTheDepthAreDiscardedOldestFirst()
        {
            var spectrogram = new Spectrogram(5);
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 12; i++)
            {
                spectrogram.Add(Spectrum(i * 100e3, start.AddSeconds(i)));
            }

            Assert.Equal(5, spectrogram.RowCount);
            Assert.Equal(12, spectrogram.AddedCount);

            // Rows 7 to 11 survive; the oldest five are gone.
            Assert.Equal(start.AddSeconds(7), spectrogram.Oldest.AcquiredUtc);
            Assert.Equal(start.AddSeconds(11), spectrogram.Newest.AcquiredUtc);

            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(start.AddSeconds(7 + i), spectrogram.Row(i).AcquiredUtc);
            }
        }

        [Fact]
        public void ReducingTheDepthDiscardsTheOldestAtOnce()
        {
            // Not when they age out. The setting was changed to reclaim the memory, and leaving it
            // held until enough new rows arrived would be doing the opposite of what was asked.
            var spectrogram = new Spectrogram(10);
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 10; i++)
            {
                spectrogram.Add(Spectrum(i * 100e3, start.AddSeconds(i)));
            }

            spectrogram.Depth = 3;

            Assert.Equal(3, spectrogram.RowCount);
            Assert.Equal(start.AddSeconds(7), spectrogram.Oldest.AcquiredUtc);
            Assert.Equal(start.AddSeconds(9), spectrogram.Newest.AcquiredUtc);

            // And it keeps working afterwards: the ring's cursor survived the resize.
            spectrogram.Add(Spectrum(0.0, start.AddSeconds(10)));

            Assert.Equal(3, spectrogram.RowCount);
            Assert.Equal(start.AddSeconds(8), spectrogram.Oldest.AcquiredUtc);
            Assert.Equal(start.AddSeconds(10), spectrogram.Newest.AcquiredUtc);
        }

        [Fact]
        public void DeepeningTheHistoryKeepsWhatWasThere()
        {
            var spectrogram = new Spectrogram(3);
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 5; i++)
            {
                spectrogram.Add(Spectrum(i * 100e3, start.AddSeconds(i)));
            }

            spectrogram.Depth = 8;

            Assert.Equal(3, spectrogram.RowCount);
            Assert.Equal(start.AddSeconds(2), spectrogram.Oldest.AcquiredUtc);
            Assert.Equal(start.AddSeconds(4), spectrogram.Newest.AcquiredUtc);

            spectrogram.Add(Spectrum(0.0, start.AddSeconds(5)));

            Assert.Equal(4, spectrogram.RowCount);
            Assert.Equal(start.AddSeconds(2), spectrogram.Oldest.AcquiredUtc);
        }

        [Fact]
        public void SettingTheDepthToWhatItAlreadyIsChangesNothing()
        {
            var spectrogram = new Spectrogram(4);
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 6; i++)
            {
                spectrogram.Add(Spectrum(i * 100e3, start.AddSeconds(i)));
            }

            spectrogram.Depth = 4;

            Assert.Equal(4, spectrogram.RowCount);
            Assert.Equal(start.AddSeconds(2), spectrogram.Oldest.AcquiredUtc);
            Assert.Equal(start.AddSeconds(5), spectrogram.Newest.AcquiredUtc);
        }

        [Fact]
        public void ATraceSelectMarkerPicksTheRowAtThatTime()
        {
            // "Moving the trace-select marker selects the history row at that time and the spectrum
            // trace updates to that row's data" - and it is that row's own frame, not a rendering
            // of it, so every format is still available from the selection.
            var spectrogram = new Spectrogram(20);
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 20; i++)
            {
                spectrogram.Add(Spectrum(-2e6 + i * 200e3, start.AddMilliseconds(i * 50)));
            }

            int index = spectrogram.RowIndexAt(start.AddMilliseconds(7 * 50 + 12));

            Assert.Equal(7, index);

            SpectrumFrame selected = spectrogram.Row(index);

            Assert.Same(spectrogram.Row(7), selected);
            Assert.Equal(start.AddMilliseconds(350), selected.AcquiredUtc);

            int peak = selected.IndexOfPeak();

            Assert.True(
                Math.Abs(selected.FrequencyAt(peak) - (CenterHz - 2e6 + 7 * 200e3)) <=
                selected.BinWidthHz);
        }

        [Fact]
        public void AMarkerDraggedPastTheEndSelectsTheEndRow()
        {
            // A marker that stopped selecting anything at the edge of the display would look like
            // a broken drag.
            var spectrogram = new Spectrogram(10);
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 10; i++)
            {
                spectrogram.Add(Spectrum(0.0, start.AddSeconds(i)));
            }

            Assert.Equal(0, spectrogram.RowIndexAt(start.AddYears(-1)));
            Assert.Equal(9, spectrogram.RowIndexAt(start.AddYears(1)));
        }

        [Fact]
        public void TheTimeAxisIsAgeAndAscendsWithTheIndex()
        {
            var spectrogram = new Spectrogram(10);
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 5; i++)
            {
                spectrogram.Add(Spectrum(0.0, start.AddMilliseconds(i * 250)));
            }

            Assert.Equal(1.0, spectrogram.SecondsBeforeNewest(0), 6);
            Assert.Equal(0.25, spectrogram.SecondsBeforeNewest(3), 6);
            Assert.Equal(0.0, spectrogram.SecondsBeforeNewest(4), 6);
            Assert.Equal(1.0, spectrogram.HistorySeconds, 6);
        }

        [Fact]
        public void AnEmptySpectrogramSaysSoRatherThanReturningNothing()
        {
            var spectrogram = new Spectrogram(4);

            Assert.True(spectrogram.IsEmpty);
            Assert.Null(spectrogram.Newest);
            Assert.Null(spectrogram.Oldest);
            Assert.Equal(0.0, spectrogram.HistorySeconds, 9);
            Assert.Throws<ArgumentOutOfRangeException>(() => spectrogram.Row(0));
            Assert.Throws<InvalidOperationException>(
                () => spectrogram.RowIndexAt(DateTime.UtcNow));
        }

        [Fact]
        public void ClearingReleasesTheHistory()
        {
            var spectrogram = new Spectrogram(4);
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 6; i++)
            {
                spectrogram.Add(Spectrum(0.0, start.AddSeconds(i)));
            }

            spectrogram.Clear();

            Assert.True(spectrogram.IsEmpty);
            Assert.Equal(0, spectrogram.RowCount);
            Assert.Equal(0, spectrogram.AddedCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(Spectrogram.MaximumDepth + 1)]
        public void ADepthOutsideTheAllowedRangeIsRefused(int depth)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Spectrogram(depth));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Spectrogram(4).Depth = depth);
        }

        [Fact]
        public void AFrameOfNullIsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new Spectrogram(4).Add(null));
        }

        [Fact]
        public void AnInstantThatIsNotUtcIsRefused()
        {
            var spectrogram = new Spectrogram(4);

            spectrogram.Add(Spectrum(0.0, new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc)));

            Assert.Throws<ArgumentException>(
                () => spectrogram.RowIndexAt(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Local)));
        }

        // ---- REQ-TRC-001a: accumulators are not formats ---------------------------------------

        [Fact]
        public void ChangingFormatPreservesTheAccumulatedHistory()
        {
            // The criterion, asserted directly. This is the whole reason the two are separate
            // settings: REQ-TRC-001's no-recomputation rule cannot apply to data accumulated
            // across acquisitions, so a format change must leave it alone.
            var trace = new AccumulatingTrace(50)
            {
                Accumulator = TraceAccumulator.Spectrogram,
            };

            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 12; i++)
            {
                trace.Add(Spectrum(i * 100e3, start.AddSeconds(i)));
            }

            SpectrumFrame oldest = trace.Spectrogram.Oldest;

            trace.Format = TraceFormat.UnwrappedPhase;

            Assert.Equal(12, trace.Spectrogram.RowCount);
            Assert.Same(oldest, trace.Spectrogram.Oldest);

            trace.Format = TraceFormat.LinearMagnitude;

            Assert.Equal(12, trace.Spectrogram.RowCount);
            Assert.Same(oldest, trace.Spectrogram.Oldest);
        }

        [Fact]
        public void ChangingTheAccumulatorDiscardsTheHistory()
        {
            // The other half. Rows of spectra are not rows of a persistence map, and carrying them
            // over would present one mode's data under another mode's name.
            var trace = new AccumulatingTrace(50)
            {
                Accumulator = TraceAccumulator.Spectrogram,
            };

            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 12; i++)
            {
                trace.Add(Spectrum(i * 100e3, start.AddSeconds(i)));
            }

            Assert.Equal(12, trace.Spectrogram.RowCount);

            trace.Accumulator = TraceAccumulator.DigitalPersistence;

            Assert.True(trace.Spectrogram.IsEmpty);
        }

        [Fact]
        public void ReassertingTheSameAccumulatorDiscardsNothing()
        {
            // A control that restates its own state on every repaint would otherwise erase the
            // history it was drawing.
            var trace = new AccumulatingTrace(50)
            {
                Accumulator = TraceAccumulator.Spectrogram,
            };

            trace.Add(Spectrum(0.0, new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc)));
            trace.Accumulator = TraceAccumulator.Spectrogram;

            Assert.Equal(1, trace.Spectrogram.RowCount);
        }

        [Fact]
        public void NothingAccumulatesWithoutAnAccumulator()
        {
            var trace = new AccumulatingTrace(50);

            trace.Add(Spectrum(0.0, new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc)));

            Assert.True(trace.Spectrogram.IsEmpty);
            Assert.NotNull(trace.Latest);
        }

        [Fact]
        public void TheLatestAcquisitionIsKeptWhateverTheAccumulator()
        {
            // A trace always has a current acquisition to draw; the accumulator governs what is
            // drawn as well, not instead.
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            foreach (TraceAccumulator mode in Enum.GetValues(typeof(TraceAccumulator)))
            {
                var trace = new AccumulatingTrace { Accumulator = mode };

                trace.Add(Spectrum(1e6, start));

                Assert.NotNull(trace.Latest);
                Assert.Equal(start, trace.Latest.AcquiredUtc);
            }
        }

        [Fact]
        public void ATraceSelectMarkerNeedsASpectrogramAndSaysSoWhenThereIsNone()
        {
            var trace = new AccumulatingTrace { Accumulator = TraceAccumulator.DigitalPersistence };

            trace.Add(Spectrum(0.0, new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc)));

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => trace.SelectRowAt(DateTime.UtcNow));

            Assert.Contains("DigitalPersistence", error.Message);
        }

        [Fact]
        public void SelectingThroughTheTraceGivesTheRowsOwnFrame()
        {
            var trace = new AccumulatingTrace(20) { Accumulator = TraceAccumulator.Spectrogram };
            var start = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 10; i++)
            {
                trace.Add(Spectrum(i * 200e3, start.AddMilliseconds(i * 40)));
            }

            Assert.Same(
                trace.Spectrogram.Row(4), trace.SelectRowAt(start.AddMilliseconds(160)));
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new AccumulatingTrace().Add(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AccumulatingTrace(0));
        }

        /// <summary>A spectrum of a tone at an offset from centre, stamped at an instant.</summary>
        private static SpectrumFrame Spectrum(double offsetHz, DateTime acquiredUtc)
        {
            IqBlock block = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: Samples,
                sampleRateHz: RateHz,
                centerFrequencyHz: CenterHz,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 1,
                acquiredUtc: acquiredUtc,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: new FrontEndId("test"),
                extended: null));

            using (block)
            {
                Span<float> data = block.GetSamples();
                double cycles = offsetHz / RateHz;

                for (int n = 0; n < Samples; n++)
                {
                    double angle = 2.0 * Math.PI * cycles * n;

                    data[n * 2] = (float)Math.Cos(angle);
                    data[n * 2 + 1] = (float)Math.Sin(angle);
                }

                return new SpectrumComputer(WindowType.FlatTop, null, null).Compute(block);
            }
        }
    }
}
