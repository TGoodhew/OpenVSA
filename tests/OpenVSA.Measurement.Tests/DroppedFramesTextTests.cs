using OpenVSA.Measurement;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-NFR-012</c>: the UI reports a monotonically increasing dropped-frame count.
    /// </summary>
    /// <remarks>
    /// The wording lives below the UI so it can be asserted without a window. The shell binds
    /// <c>DroppedText</c> to this and nothing else, so what is tested here is what is displayed.
    /// </remarks>
    public class DroppedFramesTextTests
    {
        [Fact]
        public void NothingIsSaidUntilSomethingHasBeenDropped()
        {
            // A permanent "0 frames dropped" is noise a reader stops seeing, and the value of this
            // readout is that it appears when something has changed.
            Assert.Equal(string.Empty, MeasurementStatusText.DroppedFramesText(0L));
            Assert.Equal(string.Empty, MeasurementStatusText.DroppedFramesText(-1L));
        }

        [Fact]
        public void TheCountIsShownOnceThereIsOne()
        {
            Assert.Equal("1 frame dropped", MeasurementStatusText.DroppedFramesText(1L));
            Assert.Equal("2 frames dropped", MeasurementStatusText.DroppedFramesText(2L));
            Assert.Equal("4096 frames dropped", MeasurementStatusText.DroppedFramesText(4096L));
        }

        [Fact]
        public void OneFrameIsSingular()
        {
            // Small, and the sort of thing that makes a readout look unconsidered when it is wrong.
            Assert.DoesNotContain("frames", MeasurementStatusText.DroppedFramesText(1L));
            Assert.Contains("frames", MeasurementStatusText.DroppedFramesText(2L));
        }
    }
}
