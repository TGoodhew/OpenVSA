using System;
using BenchmarkDotNet.Attributes;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Benchmarks
{
    /// <summary>
    /// Every stage of one spectrum frame, measured separately, at the two sizes
    /// <c>REQ-NFR-020</c> and <c>REQ-NFR-021</c> name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stages are measured apart rather than as a single "render a frame" number because the
    /// open question is <em>which</em> stage the budget goes to. <c>REQ-NFR-005</c> assumes the
    /// answer is drawing, and mandates a rendering-strategy hierarchy on that basis; if it is
    /// actually the transform, that hierarchy is defending against the wrong cost and
    /// <c>REQ-NFR-003</c>'s SIMD work is the thing that matters.
    /// </para>
    /// <para>
    /// Budgets: <c>REQ-NFR-020</c> wants an 8 192-point frame at ≥60 updates/s — 16.7 ms for
    /// everything. <c>REQ-NFR-021</c> wants a 2²⁰-point frame at ≥10 updates/s — 100 ms.
    /// </para>
    /// </remarks>
    [MemoryDiagnoser]
    public class SpectrumFrameBenchmarks
    {
        /// <summary>Transform sizes: the two the performance requirements name.</summary>
        [Params(8192, 1 << 20)]
        public int TransformSize { get; set; }

        /// <summary>Graticule width in pixels, and so the number of decimated columns.</summary>
        public const int Columns = 800;

        private IFftProvider _double;
        private IFftProvider _single;
        private Window _window;
        private double[] _samples;
        private double[] _scratch;
        private float[] _magnitudes;
        private float[] _minMax;
        private PixelSurface _surface;
        private PlotLayout _layout;

        [GlobalSetup]
        public void Setup()
        {
            _double = new ManagedFftProvider();
            _single = new ManagedSingleFftProvider();
            _window = Window.Get(Window.Default, TransformSize);

            _samples = new double[TransformSize * 2];
            for (int n = 0; n < TransformSize; n++)
            {
                double angle = 2.0 * Math.PI * 0.1 * n;
                _samples[n * 2] = 0.5 * Math.Cos(angle) + 0.01 * Math.Cos(0.7 * n);
                _samples[n * 2 + 1] = 0.5 * Math.Sin(angle) + 0.01 * Math.Sin(0.31 * n);
            }

            _scratch = new double[TransformSize * 2];
            _magnitudes = new float[TransformSize];
            _minMax = new float[Columns * 2];

            // Surface sized so the graticule is exactly Columns wide, so the decimation and the
            // rasterisation are measuring the same frame.
            _layout = new PlotLayout(Columns + 96, 600, margin: 48);
            _surface = new PixelSurface(_layout.Width, _layout.Height);
        }

        /// <summary>Applying the window to the acquired block.</summary>
        [Benchmark(Description = "1. Window")]
        public void ApplyWindow()
        {
            Array.Copy(_samples, _scratch, _samples.Length);
            _window.ApplyTo(new Span<double>(_scratch));
        }

        /// <summary>The forward transform, double precision — the shipped default.</summary>
        [Benchmark(Description = "2. FFT (double)")]
        public void ForwardDouble()
        {
            Array.Copy(_samples, _scratch, _samples.Length);
            _double.Forward(_scratch);
        }

        /// <summary>The forward transform, single precision, for comparison.</summary>
        [Benchmark(Description = "2b. FFT (single)")]
        public void ForwardSingle()
        {
            Array.Copy(_samples, _scratch, _samples.Length);
            _single.Forward(_scratch);
        }

        /// <summary>Magnitude and conversion to dB, one value per bin.</summary>
        [Benchmark(Description = "3. Magnitude to dB")]
        public void MagnitudeToDecibels()
        {
            for (int k = 0; k < TransformSize; k++)
            {
                double re = _scratch[k * 2];
                double im = _scratch[k * 2 + 1];
                double power = re * re + im * im;

                _magnitudes[k] = power > 0.0
                    ? (float)(10.0 * Math.Log10(power))
                    : -400.0f;
            }
        }

        /// <summary>Min/max envelope decimation to the graticule width (<c>REQ-NFR-006</c>).</summary>
        [Benchmark(Description = "4. Decimate")]
        public void Decimate()
        {
            TraceDecimator.Decimate(_magnitudes, Columns, _minMax);
        }

        /// <summary>
        /// Rasterising the whole frame: zones, graticule and trace.
        /// </summary>
        /// <remarks>
        /// Independent of <see cref="TransformSize"/> by construction — decimation has already
        /// reduced the trace to one span per column, so this costs the same at 2²⁰ points as at
        /// 8 192. That invariance is the thing worth confirming, because if it holds then the
        /// point count never reaches the rendering strategy bands of <c>REQ-NFR-005</c>.
        /// </remarks>
        [Benchmark(Description = "5. Rasterise frame")]
        public void Rasterise()
        {
            PlotRasterizer.Render(_surface, _layout, PlotPalette.Dark, _minMax);
        }

        /// <summary>The whole frame end to end, which is what the requirement budgets.</summary>
        [Benchmark(Description = "Whole frame")]
        public void WholeFrame()
        {
            Array.Copy(_samples, _scratch, _samples.Length);
            _window.ApplyTo(new Span<double>(_scratch));
            _double.Forward(_scratch);
            MagnitudeToDecibels();
            TraceDecimator.Decimate(_magnitudes, Columns, _minMax);
            PlotRasterizer.Render(_surface, _layout, PlotPalette.Dark, _minMax);
        }
    }
}
