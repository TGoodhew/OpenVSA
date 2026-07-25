using System;
using System.Runtime.InteropServices;
using OpenVSA.Core;
using Xunit;

namespace OpenVSA.Core.Tests
{
    /// <summary>
    /// Covers <c>REQ-DAT-003</c>: the 8-byte SIMD-friendly complex value type, and the layout
    /// correspondence between a <c>float[2N]</c> and a <c>Complex32[N]</c>.
    /// </summary>
    public class Complex32Tests
    {
        [Fact]
        public void SizeIsExactlyEightBytes()
        {
            // REQ-DAT-003 AC. Anything else breaks the float[2N] <-> Complex32[N] correspondence.
            Assert.Equal(8, Marshal.SizeOf<Complex32>());
        }

        [Fact]
        public void FieldOrderMatchesInterleavedLayout()
        {
            // REQ-DAT-003 AC: reinterpreting a float[2N] as Complex32[N] must yield the same values
            // element for element. Verified through the actual memory, so a swapped field order
            // fails here rather than surfacing later as a conjugated spectrum.
            float[] interleaved = { 1f, 2f, 3f, 4f, 5f, 6f };

            Span<Complex32> reinterpreted =
                MemoryMarshal.Cast<float, Complex32>(interleaved.AsSpan());

            Assert.Equal(3, reinterpreted.Length);
            Assert.Equal(new Complex32(1f, 2f), reinterpreted[0]);
            Assert.Equal(new Complex32(3f, 4f), reinterpreted[1]);
            Assert.Equal(new Complex32(5f, 6f), reinterpreted[2]);
        }

        [Fact]
        public void IOffsetIsZeroAndQOffsetIsFour()
        {
            Assert.Equal(0, (int)Marshal.OffsetOf<Complex32>(nameof(Complex32.I)));
            Assert.Equal(4, (int)Marshal.OffsetOf<Complex32>(nameof(Complex32.Q)));
        }

        [Fact]
        public void MagnitudeMatchesClosedForm()
        {
            // 3-4-5 triangle: exact in binary floating point, so this is an equality not a
            // tolerance (REQ-TST-001 prefers closed-form references).
            var value = new Complex32(3f, 4f);

            Assert.Equal(25.0, value.MagnitudeSquared);
            Assert.Equal(5.0, value.Magnitude);
        }

        [Fact]
        public void MagnitudeSquaredAccumulatesInDouble()
        {
            // REQ-DSP-002: single-precision storage, double-precision accumulation. Stated as a
            // contrast, so the test fails if the implementation ever computes I*I in float.
            const float big = 3e38f;
            var value = new Complex32(big, 0f);

            Assert.True(float.IsInfinity(big * big), "precondition: the float path overflows");
            Assert.False(double.IsInfinity(value.MagnitudeSquared));
            Assert.Equal((double)big * big, value.MagnitudeSquared);
        }

        [Theory]
        [InlineData(1f, 0f, 0.0)]
        [InlineData(0f, 1f, Math.PI / 2)]
        [InlineData(-1f, 0f, Math.PI)]
        [InlineData(0f, -1f, -Math.PI / 2)]
        public void PhaseIsPrincipalValue(float i, float q, double expected)
        {
            // Principal range (-pi, pi], matching REQ-DEM-064's arg() convention.
            Assert.Equal(expected, new Complex32(i, q).Phase, 12);
        }

        [Fact]
        public void EqualityIsByValue()
        {
            var a = new Complex32(1.5f, -2.5f);
            var b = new Complex32(1.5f, -2.5f);
            var c = new Complex32(1.5f, 2.5f);

            Assert.True(a == b);
            Assert.False(a == c);
            Assert.True(a != c);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void ToStringUsesInvariantCulture()
        {
            // REQ-NFR-033's carve-out: number formatting must not depend on machine locale.
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");

                Assert.Equal("(1.5, 2.5)", new Complex32(1.5f, 2.5f).ToString());
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
