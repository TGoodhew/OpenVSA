using System;
using System.IO;
using System.Linq;
using System.Threading;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Hal.File;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// <c>REQ-REC-003</c>: playback is a first-class front end, and <c>REQ-ARC-002</c>'s middle leg.
    /// </summary>
    /// <remarks>
    /// The design point is that the analysis layers must be incapable of telling a live instrument
    /// from a file (<c>REQ-ARC-001</c>), so a recording arrives through the same negotiation — with
    /// capabilities, a plan and coercions. A playback path that bypassed negotiation would let a
    /// file do things no instrument could, and the difference would surface as a measurement that
    /// only reproduces from a recording.
    /// </remarks>
    public class FilePlaybackTests : IDisposable
    {
        private readonly string _path;
        private readonly ITestOutputHelper _output;

        /// <summary>Writes a recording to play back.</summary>
        /// <param name="output">Where the coercions are written.</param>
        public FilePlaybackTests(ITestOutputHelper output)
        {
            _output = output;
            _path = Path.Combine(Path.GetTempPath(), "openvsa-" + Guid.NewGuid().ToString("N") + ".ovsa");

            var header = new RecordingHeader
            {
                SampleCount = 4096,
                SampleRateHz = 2.0e6,
                CenterFrequencyHz = 1.0e9,
                FullScaleVolts = 1.0,
                ReferenceLevelDbm = -10.0,
                TriggerCorrectionsApplied = true,
            };

            var samples = new float[header.SampleCount * 2];

            // I carries the sample index, so where playback is in the recording is directly
            // observable. The first version used a tone at 0.125 cycles/sample, which repeats
            // every 8 samples — and with a 3 000-sample block, itself a multiple of 8, the
            // wrap-around assertion compared two identical values and could not fail.
            for (int n = 0; n < header.SampleCount; n++)
            {
                samples[n * 2] = n;
                samples[n * 2 + 1] = (float)Math.Sin(0.125 * 2.0 * Math.PI * n);
            }

            FilePlaybackFrontEnd.Write(_path, header, samples);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                System.IO.File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void ItIsDiscoveredAsAFrontEndProvider()
        {
            // The bug this closes: OpenVSA.Hal.File was an empty project, so the registry found two
            // providers where the product claimed three — and a test that looked for the DLL on
            // disk passed anyway. Ask the registry, not the file system.
            var registry = new FrontEndRegistry();

            int added = registry.AddAssembly(typeof(FilePlaybackFrontEnd).Assembly);

            _output.WriteLine(string.Join(", ", registry.Providers.Select(p => p.DisplayName)));

            Assert.Equal(1, added);
            Assert.Contains(registry.Providers, p => p.DisplayName.StartsWith("File playback", StringComparison.Ordinal));
        }

        [Fact]
        public void ARecordingCannotBeRetunedAndSaysSo()
        {
            // Coerced, not refused. A file that rejected a retune would make switching to it from
            // an instrument an error rather than a degradation, which is the opposite of what
            // REQ-ARC-002 asks for.
            using (var playback = new FilePlaybackFrontEnd())
            {
                playback.Open(_path);

                AcquisitionPlan plan = playback.Negotiate(new AcquisitionRequest(
                    centerFrequencyHz: 2.4e9, spanHz: 2.0e6, samplesPerBlock: 1024,
                    referenceLevelDbm: -10.0));

                _output.WriteLine(string.Join("; ", plan.Coercions.Select(c => c.ToString())));

                Assert.Equal(1.0e9, plan.CenterFrequencyHz, 3);
                Assert.Contains(plan.Coercions, c => c.Parameter == "CenterFrequency");
                Assert.Contains(plan.Coercions, c => c.Reason.Contains("cannot be retuned"));
            }
        }

        [Fact]
        public void EveryCoercionIsReported()
        {
            // REQ-ARC-002: "only parameters the new source cannot honour are coerced". Ask for
            // everything the recording cannot give at once.
            using (var playback = new FilePlaybackFrontEnd())
            {
                playback.Open(_path);

                AcquisitionPlan plan = playback.Negotiate(new AcquisitionRequest(
                    centerFrequencyHz: 2.4e9, spanHz: 40.0e6, samplesPerBlock: 65536,
                    referenceLevelDbm: 0.0));

                _output.WriteLine(string.Join(Environment.NewLine, plan.Coercions.Select(c => c.ToString())));

                foreach (string parameter in new[]
                    { "CenterFrequency", "Span", "SamplesPerBlock", "ReferenceLevel" })
                {
                    Assert.Contains(plan.Coercions, c => c.Parameter == parameter);
                }

                // And nothing was quietly changed without a coercion beside it.
                Assert.Equal(4, plan.Coercions.Count);
            }
        }

        [Fact]
        public void WhatItHonoursIsNotCoerced()
        {
            // The other half: a request the recording can meet exactly must come through clean, or
            // "only parameters it cannot honour" is not what is happening.
            using (var playback = new FilePlaybackFrontEnd())
            {
                playback.Open(_path);

                AcquisitionPlan plan = playback.Negotiate(new AcquisitionRequest(
                    centerFrequencyHz: 1.0e9, spanHz: 1.0e6, samplesPerBlock: 1024,
                    referenceLevelDbm: -10.0));

                Assert.Empty(plan.Coercions);
                Assert.True(plan.SupportsGapFreeStreaming);
            }
        }

        [Fact]
        public void ItPlaysTheSamplesBackAndWrapsAtTheEnd()
        {
            // Wrapping rather than stopping: a measurement left running against a file that simply
            // stopped would look like an instrument that had failed.
            using (var playback = new FilePlaybackFrontEnd())
            {
                playback.Open(_path);
                playback.ConnectAsync(CancellationToken.None).Wait();

                AcquisitionPlan plan = playback.Negotiate(new AcquisitionRequest(
                    1.0e9, 1.0e6, 3000, -10.0));

                playback.ConfigureAsync(plan, CancellationToken.None).Wait();
                playback.ArmAsync(CancellationToken.None).Wait();

                IqBlock first = playback.AcquireNextAsync(CancellationToken.None).Result;
                IqBlock second = playback.AcquireNextAsync(CancellationToken.None).Result;

                Assert.Equal(3000, first.SampleCount);
                Assert.Equal(3000, second.SampleCount);

                // I is the sample index, so position is read straight off the samples.
                Assert.Equal(0.0f, first.GetSamples()[0]);
                Assert.Equal(2999.0f, first.GetSamples()[2999 * 2]);

                // 4 096 samples read 3 000 at a time: the second block starts at 3 000 and wraps
                // at its 1 096th sample back to the beginning of the recording.
                Assert.Equal(3000.0f, second.GetSamples()[0]);
                Assert.Equal(4095.0f, second.GetSamples()[1095 * 2]);
                Assert.Equal(0.0f, second.GetSamples()[1096 * 2]);

                // The fidelity flag came from the recording, not a default (REQ-DAT-002).
                Assert.True(first.TriggerCorrectionsApplied);
            }
        }

        [Fact]
        public void ANonRecordingIsRefusedWithItsName()
        {
            string junk = Path.Combine(Path.GetTempPath(), "openvsa-junk-" + Guid.NewGuid().ToString("N"));
            System.IO.File.WriteAllText(junk, "this is not a recording");

            try
            {
                using (var playback = new FilePlaybackFrontEnd())
                {
                    InvalidDataException failure =
                        Assert.Throws<InvalidDataException>(() => playback.Open(junk));

                    Assert.Contains("not an OpenVSA recording", failure.Message);
                }
            }
            finally
            {
                System.IO.File.Delete(junk);
            }
        }

        [Fact]
        public void WithNoRecordingOpenItSaysSoRatherThanPretending()
        {
            using (var playback = new FilePlaybackFrontEnd())
            {
                AcquisitionPlan plan = playback.Negotiate(new AcquisitionRequest(
                    1.0e9, 1.0e6, 1024, 0.0));

                Assert.Contains(plan.Coercions, c => c.Reason.Contains("no recording is open"));
                Assert.False(plan.SupportsGapFreeStreaming);

                Assert.Throws<InvalidOperationException>(
                    () => playback.AcquireNextAsync(CancellationToken.None).GetAwaiter().GetResult());
            }
        }
    }
}
