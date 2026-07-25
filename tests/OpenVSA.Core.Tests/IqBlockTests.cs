using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenVSA.Core;
using Xunit;

namespace OpenVSA.Core.Tests
{
    /// <summary>
    /// Covers <c>REQ-DAT-001</c> (block metadata completeness), <c>REQ-DAT-001a</c> (buffer
    /// ownership and use-after-dispose) and <c>REQ-DAT-002</c> (trigger-correction fidelity flag).
    /// </summary>
    public class IqBlockTests
    {
        private static IqBlockMetadata Metadata(
            int sampleCount = 16,
            double sampleRateHz = 1e6,
            bool triggerCorrectionsApplied = false)
        {
            return new IqBlockMetadata(
                sampleCount: sampleCount,
                sampleRateHz: sampleRateHz,
                centerFrequencyHz: 2.4e9,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: -10.0,
                sequenceNumber: 1,
                acquiredUtc: new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc),
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: triggerCorrectionsApplied,
                source: new FrontEndId("test"),
                extended: null);
        }

        // ---- REQ-DAT-001: metadata completeness and self-consistency -------------------------

        [Fact]
        public void GetSamples_LengthIsExactlyTwiceSampleCount()
        {
            // REQ-DAT-001 AC. The rented array is normally larger; the exposed view must not be,
            // or a caller would read pool surplus as though it were data.
            using (var block = IqBlock.Rent(Metadata(sampleCount: 100)))
            {
                Assert.Equal(200, block.GetSamples().Length);
            }
        }

        [Fact]
        public void Rent_ZeroFillsTheExposedRegion()
        {
            // A pooled array arrives holding the previous tenant's data.
            using (var block = IqBlock.Rent(Metadata(sampleCount: 64)))
            {
                Span<float> samples = block.GetSamples();
                for (int i = 0; i < samples.Length; i++)
                {
                    Assert.Equal(0f, samples[i]);
                }
            }
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Metadata_RejectsNonPositiveOrNonFiniteSampleRate(double sampleRateHz)
        {
            // Fs > 0 is REQ-DAT-001's conformance criterion. NaN must fail it too: a front end
            // that could not determine its rate has to say so, not pass NaN down the chain.
            Assert.Throws<ArgumentOutOfRangeException>(() => Metadata(sampleRateHz: sampleRateHz));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Metadata_RejectsNonPositiveSampleCount(int sampleCount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Metadata(sampleCount: sampleCount));
        }

        [Fact]
        public void Metadata_RejectsNonUtcTimestamp()
        {
            Assert.Throws<ArgumentException>(() => new IqBlockMetadata(
                sampleCount: 16,
                sampleRateHz: 1e6,
                centerFrequencyHz: 0.0,
                isBaseband: true,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 0,
                acquiredUtc: new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Local),
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: FrontEndId.None,
                extended: null));
        }

        [Fact]
        public void Extended_IsNeverNull()
        {
            using (var block = IqBlock.Rent(Metadata()))
            {
                Assert.NotNull(block.Extended);
                Assert.Empty(block.Extended);
            }
        }

        // ---- REQ-DAT-001a: no public member returns the pooled array -------------------------

        [Fact]
        public void NoPublicMemberReturnsThePooledArray()
        {
            // REQ-DAT-001a AC, asserted over the public surface rather than by inspection, so that
            // adding a convenient `float[] Samples` property later fails here.
            var offenders = new List<string>();

            foreach (PropertyInfo property in typeof(IqBlock).GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (property.PropertyType == typeof(float[]))
                {
                    offenders.Add("property " + property.Name);
                }
            }

            foreach (MethodInfo method in typeof(IqBlock).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.ReturnType == typeof(float[]))
                {
                    offenders.Add("method " + method.Name);
                }

                if (method.GetParameters().Any(p => p.ParameterType == typeof(float[]).MakeByRefType()))
                {
                    offenders.Add("out/ref parameter on " + method.Name);
                }
            }

            Assert.True(
                offenders.Count == 0,
                "IqBlock must not expose its pooled array (REQ-DAT-001a). Offending members: " +
                string.Join(", ", offenders));
        }

        [Fact]
        public void GetSamples_AfterDispose_Throws()
        {
            var block = IqBlock.Rent(Metadata());
            block.Dispose();

            Assert.Throws<ObjectDisposedException>(() => block.GetSamples().Length);
        }

        [Fact]
        public void GetSample_AfterDispose_Throws()
        {
            var block = IqBlock.Rent(Metadata());
            block.Dispose();

            Assert.Throws<ObjectDisposedException>(() => block.GetSample(0));
        }

        [Fact]
        public void UseAfterDispose_DoesNotReadTheNextTenantsData()
        {
            // REQ-DAT-001a AC, proved rather than assumed: dispose a block, rent again so the pool
            // hands the same buffer to a second block, write distinctive data into it, then read
            // through the first block. Without the disposal check this returns block two's samples
            // silently — which is the whole failure this requirement exists to prevent.
            const int sampleCount = 128;

            var first = IqBlock.Rent(Metadata(sampleCount: sampleCount));
            first.GetSamples().Fill(1.0f);
            first.Dispose();

            var second = IqBlock.Rent(Metadata(sampleCount: sampleCount));
            try
            {
                second.GetSamples().Fill(99.0f);

                ObjectDisposedException caught =
                    Assert.Throws<ObjectDisposedException>(() => first.GetSamples().Length);

                // The message should point at the reason, not just the type name — this is a
                // failure someone will hit at 2am.
                Assert.Contains("REQ-DAT-001a", caught.Message, StringComparison.Ordinal);
            }
            finally
            {
                second.Dispose();
            }
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            // Double-return to an ArrayPool is corruption, not a no-op: the array would then be
            // handed to two consumers at once.
            var block = IqBlock.Rent(Metadata());

            block.Dispose();
            block.Dispose();

            Assert.True(block.IsDisposed);
        }

        [Fact]
        public void IsDisposed_ReflectsState()
        {
            var block = IqBlock.Rent(Metadata());
            Assert.False(block.IsDisposed);

            block.Dispose();
            Assert.True(block.IsDisposed);
        }

        // ---- REQ-DAT-002: trigger-correction fidelity flag -----------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TriggerCorrectionsApplied_RoundTrips(bool applied)
        {
            using (var block = IqBlock.Rent(Metadata(triggerCorrectionsApplied: applied)))
            {
                Assert.Equal(applied, block.TriggerCorrectionsApplied);
            }
        }

        // ---- Interpreted access ---------------------------------------------------------------

        [Fact]
        public void GetSample_ReadsInterleavedPairs()
        {
            using (var block = IqBlock.Rent(Metadata(sampleCount: 3)))
            {
                Span<float> samples = block.GetSamples();
                samples[0] = 1f; samples[1] = 2f;
                samples[2] = 3f; samples[3] = 4f;
                samples[4] = 5f; samples[5] = 6f;

                Assert.Equal(new Complex32(1f, 2f), block.GetSample(0));
                Assert.Equal(new Complex32(3f, 4f), block.GetSample(1));
                Assert.Equal(new Complex32(5f, 6f), block.GetSample(2));
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3)]
        public void GetSample_RejectsOutOfRangeIndex(int index)
        {
            using (var block = IqBlock.Rent(Metadata(sampleCount: 3)))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => block.GetSample(index));
            }
        }
    }
}
