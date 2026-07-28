using System;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-UI-054</c>: Threshold, Enhance, and the two markers on perpendicular axes.
    /// </summary>
    /// <remarks>
    /// The arithmetic half of the requirement, which is the half that can be stated without a
    /// window. What Threshold and Enhance <em>look</em> like is <c>SpectrogramRasterizerTests</c>;
    /// what they <em>mean</em> is here.
    /// </remarks>
    public class SpectrogramScalingTests
    {
        // Awkward on purpose: 1021 samples at 15 MS/s is the bench block, and tidy powers of two
        // hide the class of error that only shows when nothing divides.
        private const double RateHz = 15e6;
        private const double CenterHz = 1e9;
        private const int Samples = 1021;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public SpectrogramScalingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void RaisingTheThresholdRemovesCellsBelowIt()
        {
            // The criterion, as a monotone count: every step up removes cells and none adds any.
            Spectrogram history = Swept(24);

            long everything = SpectrogramScaling.DrawableCellCount(
                history, SpectrogramLevels.NoThresholdDbm);

            Assert.True(everything > 0, "Nothing was drawable at all.");

            long previous = everything;

            foreach (double threshold in new[] { -120.0, -100.0, -80.0, -60.0, -40.0, -20.0 })
            {
                long drawn = SpectrogramScaling.DrawableCellCount(history, threshold);

                _output.WriteLine(
                    "threshold " + threshold.ToString("0") + " dBm leaves " + drawn + " of " +
                    everything + " cells");

                Assert.True(
                    drawn <= previous,
                    "Raising the threshold to " + threshold + " dBm drew more cells, not fewer.");

                previous = drawn;
            }

            Assert.True(previous < everything, "No threshold removed anything.");
            Assert.Equal(0L, SpectrogramScaling.DrawableCellCount(history, 60.0));
        }

        [Fact]
        public void AThresholdRemovesTheCellsAtItsOwnLevelToo()
        {
            // Strictly above, which is the boundary a user setting the threshold to "the noise
            // floor" is asking about. A test at a level no cell sits exactly on could not tell the
            // two conventions apart.
            Assert.False(SpectrogramScaling.IsDrawn(-70.0, -70.0));
            Assert.True(SpectrogramScaling.IsDrawn(-69.9, -70.0));
            Assert.False(SpectrogramScaling.IsDrawn(double.NaN, SpectrogramLevels.NoThresholdDbm));
        }

        [Fact]
        public void EnhanceNarrowsTheWindowOntoTheLevelsThatArePopulated()
        {
            // The point of Enhance: a spectrogram of a real signal is nearly all noise floor with a
            // few very loud cells, so a window taken from the extremes spends most of the map on a
            // range nothing occupies.
            Spectrogram history = Swept(24);

            SpectrogramLevels plain = SpectrogramScaling.Window(
                history, SpectrogramLevels.NoThresholdDbm, enhance: false, fallback: Fallback);

            SpectrogramLevels enhanced = SpectrogramScaling.Window(
                history, SpectrogramLevels.NoThresholdDbm, enhance: true, fallback: Fallback);

            _output.WriteLine("plain    " + plain);
            _output.WriteLine("enhanced " + enhanced);

            Assert.True(
                enhanced.RangeDb < plain.RangeDb,
                "Enhance widened the window rather than narrowing it: " + enhanced + " against " +
                plain + ".");

            Assert.True(enhanced.LowDbm >= plain.LowDbm);
            Assert.True(enhanced.HighDbm <= plain.HighDbm);

            // And it does what narrowing is for: a level in the populated band lands further up the
            // map than it did, so the detail there is spread over more of the colour entries.
            double middle = 0.5 * (enhanced.LowDbm + enhanced.HighDbm);

            Assert.True(
                enhanced.FractionOf(middle) > plain.FractionOf(middle),
                "A level in the busy band did not move up the map.");
        }

        [Fact]
        public void ThresholdAndEnhanceCompose()
        {
            // Threshold decides which cells Enhance sees, so raising it past the floor lifts the
            // window. Asserted because the two settings are independent controls over one map and
            // the obvious implementation computes the window before applying the threshold.
            Spectrogram history = Swept(24);

            SpectrogramLevels open = SpectrogramScaling.Window(
                history, SpectrogramLevels.NoThresholdDbm, enhance: true, fallback: Fallback);

            SpectrogramLevels raised = SpectrogramScaling.Window(
                history, open.LowDbm + 0.5 * open.RangeDb, enhance: true, fallback: Fallback);

            Assert.True(
                raised.LowDbm > open.LowDbm,
                "The threshold did not lift the enhanced window: " + raised + " against " + open + ".");
        }

        [Fact]
        public void AWindowWithNothingInItFallsBackRatherThanDividingByZero()
        {
            Spectrogram history = Swept(4);

            // Above every cell.
            SpectrogramLevels none = SpectrogramScaling.Window(
                history, 60.0, enhance: false, fallback: Fallback);

            Assert.Equal(Fallback.LowDbm, none.LowDbm);
            Assert.Equal(Fallback.HighDbm, none.HighDbm);

            // And an empty history is not an error either — it is a spectrogram before the first
            // sweep, which is what every spectrogram is for a moment.
            SpectrogramLevels empty = SpectrogramScaling.Window(
                new Spectrogram(8), SpectrogramLevels.NoThresholdDbm, false, Fallback);

            Assert.Equal(Fallback.LowDbm, empty.LowDbm);
        }

        [Fact]
        public void AFlatHistoryStillGivesAUsableWindow()
        {
            // Every cell at one level: the window has to stay finite, because the reciprocal of its
            // width is what colours every cell.
            var history = new Spectrogram(4);
            var start = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

            for (int row = 0; row < 4; row++)
            {
                history.Add(Flat(-50.0, start.AddMilliseconds(row * 10)));
            }

            SpectrogramLevels window = SpectrogramScaling.Window(
                history, SpectrogramLevels.NoThresholdDbm, enhance: true, fallback: Fallback);

            Assert.True(window.RangeDb >= 1.0, "A flat history gave a window of " + window.RangeDb + " dB.");
            Assert.InRange(window.FractionOf(-50.0), 0.0, 1.0);
        }

        [Fact]
        public void AWindowRefusesToBeBuiltUpsideDownOrInfinite()
        {
            Assert.Throws<ArgumentException>(() => new SpectrogramLevels(0.0, -100.0));
            Assert.Throws<ArgumentException>(() => new SpectrogramLevels(-100.0, -100.0));
            Assert.Throws<ArgumentException>(
                () => new SpectrogramLevels(double.NegativeInfinity, 0.0));
        }

        [Fact]
        public void AFractionIsClampedToTheWindow()
        {
            var window = new SpectrogramLevels(-100.0, -20.0);

            Assert.Equal(0.0, window.FractionOf(-140.0));
            Assert.Equal(1.0, window.FractionOf(0.0));
            Assert.Equal(0.5, window.FractionOf(-60.0), 12);
            Assert.Equal(0.0, window.FractionOf(double.NaN));
        }

        // ---- The two markers ---------------------------------------------------------------------

        [Fact]
        public void TheTwoMarkersAreOnPerpendicularAxes()
        {
            // "the spectrogram marker vertical, the trace-select marker horizontal".
            Assert.True(SpectrogramMarkers.IsVertical(SpectrogramMarkerKind.Spectrogram));
            Assert.True(SpectrogramMarkers.IsHorizontal(SpectrogramMarkerKind.TraceSelect));

            Assert.False(SpectrogramMarkers.IsHorizontal(SpectrogramMarkerKind.Spectrogram));
            Assert.False(SpectrogramMarkers.IsVertical(SpectrogramMarkerKind.TraceSelect));
        }

        [Fact]
        public void EachMarkerMovesOnlyAlongItsOwnAxis()
        {
            // Dragged diagonally, both of them. The criterion is that each takes one coordinate of
            // the gesture and ignores the other, so a diagonal drag is the case that tells a correct
            // implementation from one that simply happens never to be given the other coordinate.
            Spectrogram history = Swept(24);
            var markers = new SpectrogramMarkers(history);

            markers.MoveTo(SpectrogramMarkerKind.Spectrogram, 100, 3);
            markers.MoveTo(SpectrogramMarkerKind.TraceSelect, 100, 3);

            int bin = markers.BinIndex;
            int row = markers.RowIndex;

            Assert.Equal(100, bin);
            Assert.Equal(3, row);

            // Drag the frequency marker diagonally: the row must not follow.
            Assert.True(markers.MoveTo(SpectrogramMarkerKind.Spectrogram, 400, 19));
            Assert.Equal(400, markers.BinIndex);
            Assert.Equal(row, markers.RowIndex);

            // And the other way about: the bin must not follow.
            Assert.True(markers.MoveTo(SpectrogramMarkerKind.TraceSelect, 7, 11));
            Assert.Equal(11, markers.RowIndex);
            Assert.Equal(400, markers.BinIndex);
        }

        [Fact]
        public void TheTraceSelectMarkerHandsBackTheRowItSelects()
        {
            // REQ-MKR-007: "moving the trace-select marker to a history row makes the spectrum trace
            // show that row's data". A whole frame, so the trace can draw it in any format.
            Spectrogram history = Swept(24);
            var markers = new SpectrogramMarkers(history);

            for (int row = 0; row < history.RowCount; row++)
            {
                markers.MoveTo(SpectrogramMarkerKind.TraceSelect, 0, row);

                Assert.Same(history.Row(row), markers.SelectedRow);
                Assert.Equal(history.SecondsBeforeNewest(row), markers.SecondsBeforeNewest, 9);
            }
        }

        [Fact]
        public void AMarkerHoldsAnInstantAndAFrequencyRatherThanTwoIndices()
        {
            // The row a marker selects has to stay the same acquisition as the history scrolls under
            // it. Stored as an index it would slide to a different moment once a sweep; stored as an
            // instant it stays put until it ages out.
            var history = new Spectrogram(8);
            var start = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

            for (int row = 0; row < 8; row++)
            {
                history.Add(Flat(-60.0 + row, start.AddMilliseconds(row * 10)));
            }

            var markers = new SpectrogramMarkers(history);

            markers.MoveTo(SpectrogramMarkerKind.TraceSelect, 0, 5);

            SpectrumFrame chosen = markers.SelectedRow;

            Assert.Equal(5, markers.RowIndex);

            // Two more sweeps: the history has scrolled by two, so the same acquisition is now row 3.
            history.Add(Flat(-40.0, start.AddMilliseconds(80.0)));
            history.Add(Flat(-40.0, start.AddMilliseconds(90.0)));

            Assert.Same(chosen, markers.SelectedRow);
            Assert.Equal(3, markers.RowIndex);
        }

        [Fact]
        public void AnUnplacedPairSitsInTheMiddleAndOnTheNewestRow()
        {
            Spectrogram history = Swept(12);
            var markers = new SpectrogramMarkers(history);

            Assert.Equal(history.Newest.PointCount / 2, markers.BinIndex);
            Assert.Equal(history.RowCount - 1, markers.RowIndex);
            Assert.Same(history.Newest, markers.SelectedRow);
            Assert.Equal(0.0, markers.SecondsBeforeNewest, 9);
        }

        [Fact]
        public void MarkersOnAnEmptyHistoryPointAtNothingAndRefuseToMove()
        {
            var markers = new SpectrogramMarkers(new Spectrogram(4));

            Assert.False(markers.HasRows);
            Assert.Equal(-1, markers.BinIndex);
            Assert.Equal(-1, markers.RowIndex);
            Assert.Null(markers.SelectedRow);
            Assert.False(markers.MoveTo(SpectrogramMarkerKind.Spectrogram, 3, 3));
        }

        [Fact]
        public void AGestureOutsideTheDisplayClampsRatherThanBeingRefused()
        {
            // A drag past the edge has an obvious meaning, and a marker that stopped following the
            // pointer at the edge would look like a broken drag — the same argument
            // Spectrogram.RowIndexAt makes for clamping rather than refusing.
            Spectrogram history = Swept(12);
            var markers = new SpectrogramMarkers(history);

            markers.MoveTo(SpectrogramMarkerKind.Spectrogram, -50, 0);
            Assert.Equal(0, markers.BinIndex);

            markers.MoveTo(SpectrogramMarkerKind.Spectrogram, 1_000_000, 0);
            Assert.Equal(history.Newest.PointCount - 1, markers.BinIndex);

            markers.MoveTo(SpectrogramMarkerKind.TraceSelect, 0, -9);
            Assert.Equal(0, markers.RowIndex);

            markers.MoveTo(SpectrogramMarkerKind.TraceSelect, 0, 9999);
            Assert.Equal(history.RowCount - 1, markers.RowIndex);
        }

        [Fact]
        public void ClearingReturnsBothMarkersToWhereAnUnplacedPairSits()
        {
            Spectrogram history = Swept(12);
            var markers = new SpectrogramMarkers(history);

            markers.MoveTo(SpectrogramMarkerKind.Spectrogram, 3, 0);
            markers.MoveTo(SpectrogramMarkerKind.TraceSelect, 0, 1);

            markers.Clear();

            Assert.Equal(history.Newest.PointCount / 2, markers.BinIndex);
            Assert.Equal(history.RowCount - 1, markers.RowIndex);
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new SpectrogramMarkers(null));
            Assert.Throws<ArgumentNullException>(
                () => SpectrogramScaling.Window(null, 0.0, false, Fallback));
            Assert.Throws<ArgumentNullException>(
                () => SpectrogramScaling.DrawableCellCount(null, 0.0));
        }

        // ---- Helpers -----------------------------------------------------------------------------

        private static SpectrogramLevels Fallback => new SpectrogramLevels(-100.0, 0.0);

        /// <summary>A history of a tone stepping up in frequency, one row per step.</summary>
        private static Spectrogram Swept(int rows)
        {
            var history = new Spectrogram(rows);
            var start = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

            for (int row = 0; row < rows; row++)
            {
                double offset = -3e6 + row * (6e6 / Math.Max(1, rows - 1));

                history.Add(Tone(offset, start.AddMilliseconds(row * 10)));
            }

            return history;
        }

        private static SpectrumFrame Tone(double offsetHz, DateTime acquiredUtc) =>
            Compute(acquiredUtc, (n, data) =>
            {
                double angle = 2.0 * Math.PI * (offsetHz / RateHz) * n;

                data[n * 2] = (float)Math.Cos(angle);
                data[n * 2 + 1] = (float)Math.Sin(angle);
            });

        /// <summary>A spectrum whose every bin is at one level, near enough.</summary>
        private static SpectrumFrame Flat(double levelDbm, DateTime acquiredUtc)
        {
            // An impulse in time is flat in frequency, which is the only input that gives a genuinely
            // level spectrum rather than one that merely looks level.
            double amplitude = Math.Pow(10.0, (levelDbm - 10.0) / 20.0);

            return Compute(acquiredUtc, (n, data) =>
            {
                data[n * 2] = n == 0 ? (float)amplitude : 0.0f;
                data[n * 2 + 1] = 0.0f;
            });
        }

        /// <summary>Fills one interleaved sample; a delegate rather than an <c>Action</c>
        /// because a span cannot be a generic argument.</summary>
        private delegate void SampleFiller(int index, Span<float> data);

        private static SpectrumFrame Compute(DateTime acquiredUtc, SampleFiller fill)
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

                for (int n = 0; n < Samples; n++)
                {
                    fill(n, data);
                }

                return new SpectrumComputer(WindowType.FlatTop, null, null).Compute(block);
            }
        }
    }
}
