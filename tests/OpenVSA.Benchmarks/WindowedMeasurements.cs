using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using WpfWindow = System.Windows.Window;
using System.Windows;
using System.Windows.Threading;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.PerformanceGate;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Benchmarks
{
    /// <summary>
    /// The rendered targets, measured with a real message loop rather than under BenchmarkDotNet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-NFR-020</c>–<c>REQ-NFR-026</c>'s shared criterion asks for "BenchmarkDotNet plus a
    /// headless measurement driver, <strong>and a windowed harness for the rendered targets</strong>".
    /// This is that windowed harness, and it is separate for a reason that is not organisational:
    /// BenchmarkDotNet measures a method's steady-state cost with the dispatcher idle, and every
    /// one of these targets is about what happens when it is not. "20 windows updating while input
    /// stays under 100 ms" has no meaning inside a benchmark loop.
    /// </para>
    /// <para>
    /// Sustained rates, not peak. Each run measures over a fixed wall-clock window and counts what
    /// actually reached the screen, so a harness that queued a thousand frames and rendered ten
    /// scores ten.
    /// </para>
    /// </remarks>
    public static class WindowedMeasurements
    {
        /// <summary>How long each sustained-rate measurement runs for.</summary>
        /// <remarks>
        /// Long enough that a stray garbage collection is a fraction of the window rather than the
        /// result, short enough that the whole gate is runnable in CI.
        /// </remarks>
        public static readonly TimeSpan MeasurementWindow = TimeSpan.FromSeconds(6.0);

        /// <summary>Measures every rendered target this build can measure.</summary>
        /// <returns>One measurement per target.</returns>
        public static IList<TargetMeasurement> Run()
        {
            var results = new List<TargetMeasurement>();

            results.Add(OnStaThread(() => SpectrumUpdateRate("Spectrum8192Rendered", 8192)));
            results.Add(OnStaThread(() => SpectrumUpdateRate("Spectrum1MRenderedDecimated", 1 << 20)));
            results.Add(OnStaThread(TwentyTraceWindows));

            return results;
        }

        /// <summary>
        /// <c>REQ-NFR-020</c> and <c>REQ-NFR-021</c>: one plot, a transform of a given size,
        /// decimated and drawn, as many times as fit in the window.
        /// </summary>
        /// <param name="name">The benchmark name the gate knows the target by.</param>
        /// <param name="points">The transform size.</param>
        /// <remarks>
        /// End to end — build the frame, decimate it, hand it to the plot, and let the dispatcher
        /// render — because that is what "rendered: ≥60 updates/s" means. Measuring the transform
        /// alone would report a rate the screen never sees.
        /// </remarks>
        private static TargetMeasurement SpectrumUpdateRate(string name, int points)
        {
            var plot = new TracePlot();
            WpfWindow host = Host(plot, 1200.0, 800.0);

            try
            {
                var source = new SpectrumSource(points);
                int columns = Math.Max(2, plot.GraticuleColumns);

                return Sustained(name, () =>
                {
                    // The transform is inside the loop, and it belongs there. Hoisting it out
                    // measures decimation and drawing, which for a 2^20-point frame reported
                    // 223 updates/s against a target of 10 -- a number implausible enough to be
                    // the reason this comment exists. An update is window, transform, magnitude,
                    // decimate and draw; anything less is not what the requirement states.
                    TraceSnapshot snapshot = RenderMarshal.Decimate(
                        source.NextFrame(), columns, new[] { TraceFormat.LogMagnitude },
                        TraceDetector.Normal, TraceFormatOptions.Default);

                    plot.Show(snapshot);
                    Drain();
                });
            }
            finally
            {
                host.Close();
            }
        }

        /// <summary>
        /// <c>REQ-NFR-024</c>: twenty windows updating together, and what that does to input.
        /// </summary>
        /// <remarks>
        /// Twenty real top-level windows, not twenty plots in one. The requirement says windows,
        /// and the costs it is guarding against — per-window layout, per-window render passes,
        /// twenty separate visual trees competing for one dispatcher — are exactly the ones that
        /// disappear if they are collapsed into a single host.
        /// </remarks>
        private static TargetMeasurement TwentyTraceWindows()
        {
            const int Windows = 20;

            var plots = new TracePlot[Windows];
            var hosts = new WpfWindow[Windows];

            for (int i = 0; i < Windows; i++)
            {
                plots[i] = new TracePlot();

                // Tiled small: twenty full-size windows would measure the machine's fill rate
                // rather than OpenVSA, and the requirement is about the update pipeline.
                hosts[i] = Host(plots[i], 420.0, 300.0, (i % 5) * 430.0, (i / 5) * 320.0);
            }

            try
            {
                var source = new SpectrumSource(8192);
                int columns = Math.Max(2, plots[0].GraticuleColumns);
                var latency = new List<double>();

                TargetMeasurement aggregate = Sustained("TwentyTraceWindows", () =>
                {
                    // One acquisition feeding twenty windows, which is the case the requirement
                    // describes -- twenty views of one measurement, not twenty measurements.
                    TraceSnapshot snapshot = RenderMarshal.Decimate(
                        source.NextFrame(), columns, new[] { TraceFormat.LogMagnitude },
                        TraceDetector.Normal, TraceFormatOptions.Default);

                    for (int i = 0; i < Windows; i++)
                    {
                        plots[i].Show(snapshot);
                    }

                    latency.Add(InputLatencyMs());
                    Drain();
                });

                latency.Sort();

                // Reported rather than gated here: REQ-NFR-024 states two figures, and the gate
                // carries one number per target. The latency is printed so a run that meets the
                // rate by starving input is visible rather than silently passing.
                double worst = latency.Count == 0 ? double.NaN : latency[latency.Count - 1];
                double median = latency.Count == 0 ? double.NaN : latency[latency.Count / 2];

                Console.WriteLine(
                    "  REQ-NFR-024 input latency while 20 windows update: median " +
                    median.ToString("F1") + " ms, worst " + worst.ToString("F1") +
                    " ms (target <100 ms)");

                return aggregate;
            }
            finally
            {
                foreach (WpfWindow host in hosts)
                {
                    host.Close();
                }
            }
        }

        /// <summary>
        /// Runs an update repeatedly for <see cref="MeasurementWindow"/> and reports the rate and its spread.
        /// </summary>
        /// <remarks>
        /// The spread comes from per-second buckets rather than per-update timings. An individual
        /// update's cost is dominated by whether a collection happened to land on it; what the
        /// requirement asks about, and what a regression shows up in, is the rate held over a
        /// second. Buckets also give the gate the variance it needs to call a noisy run
        /// inconclusive instead of passing it.
        /// </remarks>
        private static TargetMeasurement Sustained(string name, Action update)
        {
            // A first pass through the whole path, discarded: it pays for JIT, the first layout
            // and the initial bitmap allocation, none of which recur.
            update();

            var overall = Stopwatch.StartNew();
            var bucket = Stopwatch.StartNew();

            var rates = new List<double>();
            int inBucket = 0;

            while (overall.Elapsed < MeasurementWindow)
            {
                update();
                inBucket++;

                if (bucket.Elapsed >= TimeSpan.FromSeconds(1.0))
                {
                    rates.Add(inBucket / bucket.Elapsed.TotalSeconds);
                    inBucket = 0;
                    bucket.Restart();
                }
            }

            if (rates.Count < 2)
            {
                // Fewer than two buckets means the update is slower than half the window, so
                // there is no spread to report and the gate would have to guess at one.
                rates.Add(inBucket / Math.Max(1e-6, bucket.Elapsed.TotalSeconds));
                rates.Add(rates[0]);
            }

            double mean = 0.0;

            foreach (double rate in rates)
            {
                mean += rate;
            }

            mean /= rates.Count;

            double sum = 0.0;

            foreach (double rate in rates)
            {
                sum += (rate - mean) * (rate - mean);
            }

            double deviation = Math.Sqrt(sum / Math.Max(1, rates.Count - 1));

            return new TargetMeasurement(name, mean, deviation, rates.Count);
        }

        /// <summary>How long a fresh input-priority message waits behind the render work.</summary>
        /// <remarks>
        /// Input priority is the point: it is the queue a keystroke or a click joins, so its
        /// turnaround is the latency a user feels. Timing a <c>Background</c> operation instead
        /// would measure something no user waits on.
        /// </remarks>
        private static double InputLatencyMs()
        {
            var clock = Stopwatch.StartNew();
            double elapsed = -1.0;

            Dispatcher.CurrentDispatcher.Invoke(
                new Action(() => elapsed = clock.Elapsed.TotalMilliseconds),
                DispatcherPriority.Input);

            return elapsed;
        }

        /// <summary>Lets the dispatcher render what has been queued.</summary>
        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render);

        /// <summary>A shown window hosting a plot.</summary>
        private static WpfWindow Host(TracePlot plot, double width, double height, double left = 0.0, double top = 0.0)
        {
            var window = new WpfWindow
            {
                Width = width,
                Height = height,
                Left = left,
                Top = top,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Content = plot,
                Title = "OpenVSA performance harness",
            };

            // Shown, not merely constructed. An unshown window's visual tree is never rendered,
            // and the render pass is most of what these targets are measuring.
            window.Show();
            window.UpdateLayout();

            return window;
        }

        /// <summary>
        /// A block of samples, and the whole transform path from it to a spectrum frame.
        /// </summary>
        /// <remarks>
        /// The samples are generated once and the transform runs per update, because that is the
        /// division a live measurement has: acquisition hands over a block, and everything after
        /// it is what the update rate is measuring.
        /// </remarks>
        private sealed class SpectrumSource
        {
            private readonly int _points;
            private readonly OpenVSA.Dsp.Windowing.Window _window;
            private readonly IFftProvider _fft;
            private readonly double[] _samples;
            private readonly double[] _scratch;
            private readonly float[] _levels;

            public SpectrumSource(int points)
            {
                _points = points;
                _window = OpenVSA.Dsp.Windowing.Window.Get(
                    OpenVSA.Dsp.Windowing.Window.Default, points);
                _fft = new ManagedFftProvider();

                _samples = new double[points * 2];
                _scratch = new double[points * 2];
                _levels = new float[points];

                // A carrier off centre with a little noise, so the trace has structure to draw
                // rather than a flat line the rasteriser can skip.
                for (int n = 0; n < points; n++)
                {
                    double angle = 2.0 * Math.PI * 0.1 * n;

                    _samples[n * 2] = 0.5 * Math.Cos(angle) + 0.01 * Math.Cos(0.7 * n);
                    _samples[n * 2 + 1] = 0.5 * Math.Sin(angle) + 0.01 * Math.Sin(0.31 * n);
                }
            }

            /// <summary>Window, transform, magnitude, and a frame — one update's worth.</summary>
            public SpectrumFrame NextFrame()
            {
                Array.Copy(_samples, _scratch, _samples.Length);
                _window.ApplyTo(new Span<double>(_scratch));
                _fft.Forward(_scratch);

                for (int k = 0; k < _points; k++)
                {
                    double re = _scratch[k * 2];
                    double im = _scratch[k * 2 + 1];
                    double power = re * re + im * im;

                    _levels[k] = power > 0.0 ? (float)(10.0 * Math.Log10(power)) : -400.0f;
                }

                return SpectrumFrame.FromLevels(
                    _levels, 999.0e6, 2.0e6 / _points,
                    OpenVSA.Dsp.Windowing.WindowType.FlatTop, 3.8194);
            }
        }

        /// <summary>Runs a measurement on its own STA thread with a live dispatcher.</summary>
        /// <remarks>
        /// One thread per measurement, torn down after: twenty windows left standing would still
        /// be rendering during the next target's window, and the second figure would be measuring
        /// the first target's leftovers.
        /// </remarks>
        private static TargetMeasurement OnStaThread(Func<TargetMeasurement> measure)
        {
            TargetMeasurement result = null;
            Exception failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    result = measure();
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new InvalidOperationException("A windowed measurement failed.", failure);
            }

            return result;
        }
    }
}
