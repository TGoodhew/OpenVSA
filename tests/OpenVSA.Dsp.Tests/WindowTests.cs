using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-010</c> and <c>REQ-DSP-010a</c>: the window set, its tabulated ENBW and sidelobe
    /// figures, and the periodic definition those figures assume.
    /// </summary>
    /// <remarks>
    /// Every expected value here comes from the specification's table, which is the closed-form
    /// reference <c>REQ-TST-001</c> demands. Nothing is compared against a previous run.
    /// </remarks>
    public class WindowTests
    {
        /// <summary>The specification's table, verbatim. A null sidelobe means none is quoted.</summary>
        public static readonly IReadOnlyDictionary<WindowType, Tabulated> Table =
            new Dictionary<WindowType, Tabulated>
            {
                { WindowType.Uniform, new Tabulated(1.0000, -13.3) },
                { WindowType.Hann, new Tabulated(1.5000, -31.5) },
                { WindowType.GaussianTop, new Tabulated(2.2153, null) },
                { WindowType.FlatTop, new Tabulated(3.8194, null) },
                { WindowType.BlackmanHarris, new Tabulated(2.0044, -92.0) },
                { WindowType.KaiserBessel, new Tabulated(2.0013, -89.1) },
                { WindowType.Gaussian, new Tabulated(2.0212, -73.5) },
            };

        /// <summary>A row of the <c>REQ-DSP-010</c> table.</summary>
        public sealed class Tabulated
        {
            /// <summary>Creates a row.</summary>
            /// <param name="enbw">Normalised equivalent noise bandwidth.</param>
            /// <param name="peakSidelobeDb">Peak sidelobe in dB, or null where none is quoted.</param>
            public Tabulated(double enbw, double? peakSidelobeDb)
            {
                Enbw = enbw;
                PeakSidelobeDb = peakSidelobeDb;
            }

            /// <summary>Normalised equivalent noise bandwidth.</summary>
            public double Enbw { get; }

            /// <summary>Peak sidelobe in dB, or null where none is quoted.</summary>
            public double? PeakSidelobeDb { get; }
        }

        public static IEnumerable<object[]> AllWindows()
        {
            foreach (WindowType type in Enum.GetValues(typeof(WindowType)))
            {
                yield return new object[] { type };
            }
        }

        // ---- REQ-DSP-010a: ENBW at every supported FFT size -----------------------------------

        [Theory]
        [MemberData(nameof(AllWindows))]
        public void Enbw_MatchesTheTable_AtEverySupportedFftSize(WindowType type)
        {
            // "at every supported FFT size from 64 to 2^20" — stated that way in REQ-DSP-010a
            // precisely because the small end is where the symmetric definition fails, so
            // testing only at a comfortable size would miss the defect the requirement exists
            // to prevent.
            double expected = Table[type].Enbw;

            for (int length = 64; length <= 1 << 20; length <<= 1)
            {
                Window window = Window.Get(type, length);
                double error = Math.Abs(window.Enbw - expected) / expected;

                Assert.True(
                    error <= 0.001,
                    type + " at N=" + length + ": ENBW " + window.Enbw.ToString("F6") +
                    " against a tabulated " + expected.ToString("F4") + " — " +
                    (error * 100.0).ToString("F4") + " % error, tolerance 0.1 %.");
            }
        }

        [Fact]
        public void SymmetricDefinition_WouldFailAtTheSmallEnd()
        {
            // The rationale of REQ-DSP-010a, made falsifiable. Symmetric Hann has ENBW
            // 1.5*N/(N-1); this asserts that the difference actually matters at N=64 and that
            // the implementation is not quietly using it. Without this test, switching to the
            // symmetric form would still pass every check at N=4096.
            const int small = 64;

            double periodic = Window.Get(WindowType.Hann, small).Enbw;
            double symmetric = 1.5 * small / (small - 1.0);

            Assert.Equal(1.5, periodic, 9);
            Assert.True(
                Math.Abs(symmetric - 1.5) / 1.5 > 0.001,
                "The symmetric form must be outside tolerance at N=64, or this requirement " +
                "would be making a distinction without a difference.");
        }

        [Theory]
        [MemberData(nameof(AllWindows))]
        public void Coefficients_ArePeriodic_NotSymmetric(WindowType type)
        {
            // A periodic window's coefficients are f(n/N), so w[N/2] is the peak and the two
            // halves are not mirror images about the last sample. The symmetric form would put
            // equal values at n=0 and n=N-1; the periodic form does not.
            const int length = 64;
            Window window = Window.Get(type, length);
            ReadOnlySpan<double> w = window.Coefficients;

            if (type == WindowType.Uniform)
            {
                Assert.Equal(1.0, w[0], 12);
                return;
            }

            Assert.True(
                Math.Abs(w[0] - w[length - 1]) > 1e-9,
                type + " has w[0] == w[N-1], which is the symmetric definition REQ-DSP-010a rules out.");

            Assert.Equal(1.0, w[length / 2], 9);
        }

        // ---- REQ-DSP-010: peak sidelobe --------------------------------------------------------

        [Theory]
        [MemberData(nameof(AllWindows))]
        public void PeakSidelobe_MatchesTheTable(WindowType type)
        {
            double? expected = Table[type].PeakSidelobeDb;
            if (expected == null)
            {
                // No figure is quoted for Gaussian Top or Flat Top, so none is binding. Measuring
                // anyway would be inventing an expectation the specification does not state.
                return;
            }

            double measured = MeasurePeakSidelobeDb(Window.Get(type, 512));

            Assert.True(
                Math.Abs(measured - expected.Value) <= 0.5,
                type + ": peak sidelobe " + measured.ToString("F2") + " dB against a tabulated " +
                expected.Value.ToString("F1") + " dB, tolerance 0.5 dB.");
        }

        /// <summary>
        /// Highest sidelobe relative to the main-lobe peak, from a zero-padded transform.
        /// </summary>
        /// <remarks>
        /// Uses the FFT as an instrument rather than as an oracle: the transform itself is
        /// verified against closed-form pairs in <see cref="FftProviderConformanceTests"/>, so a
        /// broken FFT fails there first and this measurement is not circular.
        /// </remarks>
        private static double MeasurePeakSidelobeDb(Window window)
        {
            const int oversample = 64;
            int padded = window.Length * oversample;

            var buffer = new double[padded * 2];
            ReadOnlySpan<double> w = window.Coefficients;
            for (int n = 0; n < window.Length; n++)
            {
                buffer[n * 2] = w[n];
            }

            new ManagedFftProvider().Forward(buffer);

            var magnitudes = new double[padded / 2];
            for (int k = 0; k < magnitudes.Length; k++)
            {
                magnitudes[k] = Math.Sqrt(
                    buffer[k * 2] * buffer[k * 2] + buffer[k * 2 + 1] * buffer[k * 2 + 1]);
            }

            double peak = magnitudes[0];

            // The main-lobe edge is the first local minimum at least 20 dB down. Taking the first
            // local minimum of any depth would stop inside the main lobe of a flat top, whose
            // response near DC is flat by construction and wobbles at the 0.004 dB level.
            double threshold = peak * 0.1;
            int edge = 1;
            while (edge < magnitudes.Length - 1 &&
                   !(magnitudes[edge] < threshold &&
                     magnitudes[edge] <= magnitudes[edge - 1] &&
                     magnitudes[edge] <= magnitudes[edge + 1]))
            {
                edge++;
            }

            double highest = 0.0;
            for (int k = edge; k < magnitudes.Length; k++)
            {
                if (magnitudes[k] > highest)
                {
                    highest = magnitudes[k];
                }
            }

            return 20.0 * Math.Log10(highest / peak);
        }

        // ---- The set itself --------------------------------------------------------------------

        [Fact]
        public void FlatTopIsTheDefault()
        {
            // REQ-DSP-010a calls this out as deliberate and to be preserved. It surprises users
            // who expect Hann, which is exactly why it needs a test rather than a comment.
            Assert.Equal(WindowType.FlatTop, Window.Default);
        }

        [Fact]
        public void EveryTabulatedWindowIsImplementedAndSelectable()
        {
            var implemented = new HashSet<WindowType>();
            foreach (WindowType type in Enum.GetValues(typeof(WindowType)))
            {
                Window window = Window.Get(type, 256);
                Assert.Equal(256, window.Length);
                implemented.Add(type);
            }

            Assert.Equal(Table.Count, implemented.Count);
            foreach (WindowType type in Table.Keys)
            {
                Assert.Contains(type, implemented);
            }
        }

        [Theory]
        [MemberData(nameof(AllWindows))]
        public void CoherentGain_IsTheMeanOfTheCoefficients(WindowType type)
        {
            Window window = Window.Get(type, 1024);

            double sum = 0.0;
            ReadOnlySpan<double> w = window.Coefficients;
            for (int n = 0; n < w.Length; n++)
            {
                sum += w[n];
            }

            Assert.Equal(sum / w.Length, window.CoherentGain, 12);
            Assert.True(window.CoherentGain > 0.0, "Coherent gain must be positive.");
            Assert.True(window.CoherentGain <= 1.0, "A window peaking at 1 cannot have mean above 1.");
        }

        [Fact]
        public void UniformWindow_IsAllOnes()
        {
            Window window = Window.Get(WindowType.Uniform, 128);
            ReadOnlySpan<double> w = window.Coefficients;

            for (int n = 0; n < w.Length; n++)
            {
                Assert.Equal(1.0, w[n], 12);
            }

            Assert.Equal(1.0, window.CoherentGain, 12);
            Assert.Equal(1.0, window.Enbw, 12);
        }

        [Theory]
        [MemberData(nameof(AllWindows))]
        public void Get_ReturnsTheCachedInstance(WindowType type)
        {
            // Windows are applied per frame and Kaiser-Bessel costs a Bessel series per sample,
            // so rebuilding one per acquisition would show up against REQ-NFR-003.
            Assert.Same(Window.Get(type, 2048), Window.Get(type, 2048));
        }

        [Fact]
        public void Get_RejectsNonPositiveLength()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Window.Get(WindowType.Hann, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Window.Get(WindowType.Hann, -1));
        }

        [Fact]
        public void Get_RejectsAnUnknownWindow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Window.Get((WindowType)999, 64));
        }

        // ---- Application ------------------------------------------------------------------------

        [Fact]
        public void ApplyTo_ScalesBothComponentsOfEachSample()
        {
            Window window = Window.Get(WindowType.Hann, 8);
            var samples = new double[16];
            for (int n = 0; n < 8; n++)
            {
                samples[n * 2] = 2.0;
                samples[n * 2 + 1] = 3.0;
            }

            window.ApplyTo(new Span<double>(samples));

            ReadOnlySpan<double> w = window.Coefficients;
            for (int n = 0; n < 8; n++)
            {
                Assert.Equal(2.0 * w[n], samples[n * 2], 12);
                Assert.Equal(3.0 * w[n], samples[n * 2 + 1], 12);
            }
        }

        [Fact]
        public void ApplyTo_RejectsAMismatchedLength()
        {
            Window window = Window.Get(WindowType.Hann, 8);

            Assert.Throws<ArgumentException>(() => window.ApplyTo(new Span<double>(new double[8])));
            Assert.Throws<ArgumentException>(() => window.ApplyTo(new Span<float>(new float[8])));
        }

        [Fact]
        public void ApplyTo_AgreesBetweenTheSingleAndDoubleOverloads()
        {
            Window window = Window.Get(WindowType.FlatTop, 64);

            var asDouble = new double[128];
            var asFloat = new float[128];
            for (int i = 0; i < 128; i++)
            {
                asDouble[i] = i * 0.001;
                asFloat[i] = (float)(i * 0.001);
            }

            window.ApplyTo(new Span<double>(asDouble));
            window.ApplyTo(new Span<float>(asFloat));

            for (int i = 0; i < 128; i++)
            {
                Assert.Equal(asDouble[i], asFloat[i], 5);
            }
        }
    }
}
