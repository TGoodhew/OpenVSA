using System;
using System.Diagnostics;
using System.Threading;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-NFR-012</c>: with an artificially slowed consumer, memory remains bounded and the
    /// dropped-frame count rises monotonically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this guards against is the comfortable one. A pipeline that queues whatever it
    /// cannot draw looks correct in every functional test — no frame is lost, every number is
    /// right — and then runs a machine out of memory during a long acquisition, or shows a trace
    /// that is thirty seconds behind the instrument. Dropping is the correct behaviour, and the
    /// count is how the user learns it happened.
    /// </para>
    /// <para>
    /// <strong>Monotonic, not merely non-zero.</strong> A counter that resets, or that is
    /// recomputed per frame rather than accumulated, would still report drops — and would make
    /// "is it still dropping?" unanswerable, which is the only question a user actually has.
    /// </para>
    /// </remarks>
    public class BackPressureTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the drop count and memory figures are written.</param>
        public BackPressureTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ASlowConsumerCausesDropsRatherThanGrowth()
        {
            var marshal = new RenderMarshal { Columns = 800 };

            long before = GC.GetTotalMemory(forceFullCollection: true);
            long previousDrops = 0;
            int offered = 0;

            SpectrumFrame frame = Frame();

            // Offer far more than the marshal will accept outstanding. Nothing consumes them: this
            // is the artificially slowed stage, at its limit.
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < TimeSpan.FromSeconds(2.0))
            {
                marshal.Offer(frame);
                offered++;

                long drops = marshal.FramesDropped;

                Assert.True(
                    drops >= previousDrops,
                    "The dropped-frame count went backwards, from " + previousDrops + " to " +
                    drops + ". A count that resets cannot answer 'is it still dropping?'.");

                previousDrops = drops;
            }

            // Settled, not merely collected once. A tight offer loop outruns the collector, and a
            // single forced pass on a small machine leaves garbage the run has already released —
            // which is not retention and is not what "bounded" means.
            for (int pass = 0; pass < 3; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            long after = GC.GetTotalMemory(forceFullCollection: true);
            double grewMib = (after - before) / (1024.0 * 1024.0);

            _output.WriteLine(
                offered + " frames offered, " + marshal.FramesDropped + " dropped, managed heap " +
                (before / 1048576.0).ToString("F1") + " -> " + (after / 1048576.0).ToString("F1") +
                " MiB");

            // Something must have been dropped, or the test proved nothing about back-pressure.
            Assert.True(
                marshal.FramesDropped > 0,
                "Nothing was dropped in " + offered + " offers, so no back-pressure was exercised.");

            // **Bounded means "not proportional to the number offered", not "allocation-free".**
            // Producing garbage that the collector reclaims is fine; retaining a frame per offer is
            // not. The first draft asserted a fixed 32 MiB ceiling and failed in CI at 115 MiB with
            // exactly one frame accepted — which was collectable garbage from 28 701 offers on a
            // two-core runner, not a leak. A fixed ceiling measures the machine's collector.
            //
            // So the comparison is against what queueing would have cost. Each frame carries 8 192
            // levels, so retaining them all would be hundreds of megabytes and rising with the
            // duration of the run; five per cent of that is far below a queue and far above the
            // noise of a busy heap.
            double queuedMib = offered * 8192.0 * 4.0 / (1024.0 * 1024.0);

            _output.WriteLine(
                "queueing every frame would have cost " + queuedMib.ToString("F0") +
                " MiB; the heap moved " + grewMib.ToString("F1") + " MiB");

            Assert.True(
                grewMib < Math.Max(64.0, queuedMib * 0.05),
                "The managed heap grew " + grewMib.ToString("F1") + " MiB while " + offered +
                " frames were offered and only " + (offered - marshal.FramesDropped) +
                " accepted, against " + queuedMib.ToString("F0") +
                " MiB if every frame had been queued. That is proportional to the number offered, " +
                "which is what unbounded buffering looks like.");
        }

        [Fact]
        public void TheOutstandingLimitIsSmallAndStated()
        {
            // The bound is a published constant rather than an emergent property of timing, so a
            // change to it is a visible decision. Queueing more would trade memory for latency and
            // show a trace behind the instrument.
            Assert.True(RenderMarshal.MaximumOutstandingPosts > 0);
            Assert.True(
                RenderMarshal.MaximumOutstandingPosts <= 8,
                "An outstanding limit of " + RenderMarshal.MaximumOutstandingPosts +
                " frames is a queue, not back-pressure.");
        }

        private static SpectrumFrame Frame()
        {
            var levels = new float[8192];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = -90.0f + (i % 40);
            }

            return SpectrumFrame.FromLevels(levels, 999.0e6, 244.0, WindowType.FlatTop, 3.8194);
        }
    }
}
