using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using OpenVSA.Dsp;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-NFR-011</c>: "Under a 30-minute soak at maximum update rate with the UI actively
    /// resized and markers dragged, zero torn-frame artefacts and zero data races are reported by a
    /// concurrency-checked build."
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What "concurrency-checked" means here, exactly.</strong> .NET has no
    /// ThreadSanitizer, so there is no build that reports data races the way the criterion's wording
    /// implies. Rather than claim one, the check is made specific: every snapshot published to the
    /// UI is content-sealed at publication and the seal is verified after the UI has read it. A
    /// mismatch means a buffer was written after it was handed over — which is the torn frame the
    /// requirement is actually about, and the only failure the ownership rule can produce.
    /// </para>
    /// <para>
    /// It does <em>not</em> detect a torn read of a wider-than-atomic field, a stale value from a
    /// missing barrier, or a lock-ordering bug. Saying so is worth more than a green tick that
    /// implies coverage it does not have. What holds those is the design — a single
    /// <c>Interlocked.Exchange</c> of a reference to an immutable object — and
    /// <see cref="BackPressureTests"/>.
    /// </para>
    /// <para>
    /// The full thirty minutes is not run in CI. Duration comes from
    /// <c>OPENVSA_SOAK_SECONDS</c>, defaulting to a few seconds so the shape of the run is
    /// exercised on every build; the criterion's own run is started explicitly.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class SnapshotSoakTests
    {
        private const string DurationVariable = "OPENVSA_SOAK_SECONDS";

        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared STA host.</summary>
        /// <param name="host">The host whose thread the shell is built on.</param>
        /// <param name="output">Where the soak's counters are written.</param>
        public SnapshotSoakTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void NoPublishedSnapshotIsEverWrittenAgain()
        {
            double seconds = Duration();

            ContentSeal.Enabled = true;

            try
            {
                // On the host's STA thread, not the test's. A WPF Window cannot be created
                // anywhere else, and the resize is only a resize if a dispatcher is there to lay it
                // out — which is the contention the criterion asks for.
                _host.Run(() => Run(seconds));
            }
            finally
            {
                ContentSeal.Enabled = false;
            }
        }

        private void Run(double seconds)
        {
            var marshal = new RenderMarshal { Columns = 1024 };

            long published = 0;
            long rendered = 0;
            long torn = 0;
            long dropped;

            var stop = new ManualResetEventSlim(false);

            // The producer: frames as fast as they can be made, which is what "maximum update rate"
            // means for a soak whose point is contention rather than throughput.
            var producer = new Thread(() =>
            {
                var random = new Random(20260728);
                int points = 4096;

                while (!stop.IsSet)
                {
                    var levels = new float[points];

                    for (int i = 0; i < points; i++)
                    {
                        levels[i] = -100.0f + (float)random.NextDouble() * 80.0f;
                    }

                    marshal.Offer(SpectrumFrame.FromLevels(
                        levels,
                        startFrequencyHz: 1.0e9 - 5.0e6,
                        binWidthHz: 10.0e6 / points,
                        window: WindowType.Hann,
                        equivalentNoiseBandwidthBins: 1.5));

                    Interlocked.Increment(ref published);
                }
            })
            {
                IsBackground = true,
                Name = "soak-producer",
            };

            producer.Start();

            var clock = Stopwatch.StartNew();
            var window = new System.Windows.Window { Width = 800, Height = 600 };

            try
            {
                window.Show();

                var shape = new Random(99);

                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    TraceSnapshot snapshot = marshal.TakeForRender();

                    if (snapshot != null)
                    {
                        // Read it the way the plot does — every value of every envelope — and then
                        // ask whether what was read is still what was published. Reading first
                        // matters: a seal checked before anybody touched the buffer would pass on a
                        // producer that overwrites it a millisecond later.
                        double sum = 0.0;

                        foreach (TraceFormat format in snapshot.Formats)
                        {
                            ReadOnlySpan<float> envelope = snapshot.MinMaxFor(format);

                            for (int i = 0; i < envelope.Length; i++)
                            {
                                sum += envelope[i];
                            }
                        }

                        GC.KeepAlive(sum);

                        if (!snapshot.SealIntact)
                        {
                            Interlocked.Increment(ref torn);
                        }

                        Interlocked.Increment(ref rendered);
                    }

                    // "with the UI actively resized": a resize changes Columns, which is what the
                    // decimation reads on the producer's thread while the UI is reading the result
                    // of the last one. This is the contention the criterion names.
                    marshal.Columns = 200 + shape.Next(1400);
                    window.Width = 400 + shape.Next(800);
                    window.Height = 300 + shape.Next(500);

                    // "and markers dragged": a marker is a read of the published envelope at a
                    // moving index, concurrent with the producer replacing it.
                    if (snapshot != null && snapshot.MinMax.Length > 0)
                    {
                        int at = shape.Next(snapshot.MinMax.Length);
                        GC.KeepAlive(snapshot.MinMax[at]);
                    }

                    // Let WPF actually lay the window out, or the resize is a property write and
                    // nothing more.
                    window.Dispatcher.Invoke(
                        (Action)(() => { }),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            finally
            {
                stop.Set();
                producer.Join(TimeSpan.FromSeconds(5));
                window.Close();
            }

            dropped = marshal.FramesDropped;

            _output.WriteLine(
                "soak " + clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) +
                " s: " + published + " published, " + rendered + " rendered, " + dropped +
                " dropped, " + torn + " torn");

            _output.WriteLine(
                "  " + (published / clock.Elapsed.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture) +
                " frames/s offered, " +
                (rendered / clock.Elapsed.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture) +
                " rendered/s");

            Assert.Equal(0L, torn);

            // The soak has to have soaked. A run that published nothing would assert zero torn
            // frames truthfully and prove nothing at all.
            Assert.True(rendered > 10, "Only " + rendered + " snapshots were rendered.");
            Assert.True(published > rendered, "Nothing was dropped, so there was no contention.");
        }

        /// <summary>How long to soak for, from the environment.</summary>
        /// <remarks>
        /// A few seconds by default so the shape of the run is exercised on every build. The
        /// criterion's thirty minutes is <c>OPENVSA_SOAK_SECONDS=1800</c>, run deliberately.
        /// </remarks>
        private double Duration()
        {
            string setting = Environment.GetEnvironmentVariable(DurationVariable);

            double seconds;

            if (string.IsNullOrEmpty(setting) ||
                !double.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                seconds <= 0.0)
            {
                return 5.0;
            }

            _output.WriteLine(
                DurationVariable + "=" + setting + ": soaking for " +
                (seconds / 60.0).ToString("F1", CultureInfo.InvariantCulture) + " minutes");

            return seconds;
        }
    }
}
