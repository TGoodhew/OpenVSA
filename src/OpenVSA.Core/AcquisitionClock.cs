using System;
using System.Diagnostics;
using System.Threading;

namespace OpenVSA.Core
{
    /// <summary>
    /// The clock acquisition timestamps come from (<c>REQ-ACQ-010</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not <see cref="DateTime.UtcNow"/>.</strong> Its granularity on Windows is the
    /// system timer tick — between about 1 ms and 15.6 ms depending on what else is running — and
    /// a block of 1024 samples at 25.6 MS/s lasts 40 µs. Timestamping with it would give hundreds
    /// of consecutive blocks the same time, and then a jump; a measurement that needs to know when
    /// something happened would be reading the scheduler rather than the acquisition.
    /// </para>
    /// <para>
    /// <strong>Monotonic, disciplined to UTC once.</strong> <see cref="Stopwatch"/> gives a
    /// high-resolution count that never goes backwards; UTC gives it a meaning. Taking the wall
    /// clock once at session start and counting from there means a clock adjustment mid-run — a
    /// time sync, a leap second, someone changing the time zone — cannot make one block appear to
    /// precede the block before it.
    /// </para>
    /// <para>
    /// The cost is that the absolute time drifts against the wall clock over a long session, by
    /// whatever the two oscillators disagree by. That is the right trade for a measurement
    /// instrument: intervals are what a capture is read for, and an interval that is occasionally
    /// negative is worse than one that is uniformly a few parts per million long.
    /// </para>
    /// </remarks>
    public static class AcquisitionClock
    {
        private static long _baselineTicks;
        private static long _baselineUtcTicks;

        static AcquisitionClock()
        {
            Discipline();
        }

        /// <summary>The clock's resolution, in seconds.</summary>
        public static double ResolutionSeconds => 1.0 / Stopwatch.Frequency;

        /// <summary>Whether the underlying counter is a high-resolution one.</summary>
        /// <remarks>
        /// False on a platform where <see cref="Stopwatch"/> falls back to the system timer, in
        /// which case timestamps are no better than <see cref="DateTime.UtcNow"/> and this says so
        /// rather than implying a precision that is not there.
        /// </remarks>
        public static bool IsHighResolution => Stopwatch.IsHighResolution;

        /// <summary>The current time, in UTC, from the monotonic source.</summary>
        public static DateTime UtcNow
        {
            get
            {
                long baseline = Interlocked.Read(ref _baselineTicks);
                long baselineUtc = Interlocked.Read(ref _baselineUtcTicks);

                double elapsed = (Stopwatch.GetTimestamp() - baseline) / (double)Stopwatch.Frequency;

                return new DateTime(baselineUtc, DateTimeKind.Utc)
                    .AddTicks((long)Math.Round(elapsed * TimeSpan.TicksPerSecond));
            }
        }

        /// <summary>
        /// Re-anchors the monotonic count to the wall clock.
        /// </summary>
        /// <remarks>
        /// Called once at start-up. Calling it again mid-session re-introduces exactly the
        /// discontinuity the class exists to avoid, so it is for a caller that has decided the
        /// accumulated drift matters more than monotonicity — a very long unattended run, say —
        /// and not for routine use.
        /// </remarks>
        public static void Discipline()
        {
            Interlocked.Exchange(ref _baselineUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _baselineTicks, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Timestamps successive blocks of a gap-free acquisition (<c>REQ-ACQ-010</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The timestamp refers to the first sample of the block</strong>, and the trigger
    /// event lies <c>TriggerOffsetSeconds</c> after it — negative for pre-trigger, where the
    /// trigger is inside the record. That is the relationship <c>REQ-ACQ-010</c> requires be
    /// defined explicitly, and it is defined here, once.
    /// </para>
    /// <para>
    /// <strong>Successive blocks advance by their own duration, not by when they were
    /// noticed.</strong> A gap-free stream has an exact sample-count relationship between one
    /// block and the next; reading the clock per block instead would add the scheduler's jitter to
    /// every timestamp and lose that exactness for nothing. The clock is read once, to place the
    /// first sample of the first block, and the sample count carries the rest.
    /// </para>
    /// </remarks>
    public sealed class BlockTimeline
    {
        private readonly object _gate = new object();

        private DateTime _origin;
        private double _elapsedSeconds;
        private bool _started;

        /// <summary>Creates a timeline that starts at the first block.</summary>
        public BlockTimeline()
        {
        }

        /// <summary>Creates a timeline starting at a given instant.</summary>
        /// <param name="firstSampleUtc">When the first sample of the first block was taken.</param>
        /// <remarks>For a recording, whose timestamps come from the file rather than from now.</remarks>
        public BlockTimeline(DateTime firstSampleUtc)
        {
            _origin = firstSampleUtc.ToUniversalTime();
            _started = true;
        }

        /// <summary>Blocks timestamped so far.</summary>
        public long BlockCount { get; private set; }

        /// <summary>
        /// The timestamp of the next block's first sample, advancing the timeline past it.
        /// </summary>
        /// <param name="sampleCount">Samples in the block; must be positive.</param>
        /// <param name="sampleRateHz">Sample rate, in hertz; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
        /// <remarks>
        /// <strong>Elapsed time is accumulated, and rounded to clock ticks only when a timestamp
        /// is formed.</strong> Advancing a <see cref="DateTime"/> by a rounded block duration each
        /// time feeds the rounding error back in, and it then accumulates linearly: a 1021-sample
        /// block at 15 MS/s lasts 680.667 ticks, which rounds to 681 and gains a third of a tick
        /// per block — 3.3 µs per hundred blocks, and milliseconds over a run. Found by exercising
        /// this against a real acquisition, whose awkward length and rate showed what a
        /// tick-aligned 1024-at-25.6 MS/s unit test could not.
        /// </remarks>
        public DateTime Next(int sampleCount, double sampleRateHz)
        {
            if (sampleCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleCount), sampleCount, "A block holds at least one sample.");
            }

            if (!(sampleRateHz > 0.0) || double.IsInfinity(sampleRateHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleRateHz), sampleRateHz, "A sample rate must be positive and finite.");
            }

            lock (_gate)
            {
                if (!_started)
                {
                    _origin = AcquisitionClock.UtcNow;
                    _elapsedSeconds = 0.0;
                    _started = true;
                }

                DateTime timestamp = _origin.AddTicks(
                    (long)Math.Round(_elapsedSeconds * TimeSpan.TicksPerSecond));

                _elapsedSeconds += sampleCount / sampleRateHz;

                BlockCount++;
                return timestamp;
            }
        }

        /// <summary>
        /// Restarts the timeline, so the next block is placed by the clock again.
        /// </summary>
        /// <remarks>
        /// For a break in the stream — a re-arm, a settings change, a lost trigger. Carrying on
        /// from the old timeline across a gap would claim a continuity the acquisition did not
        /// have, which is a worse error than a small step in the absolute time.
        /// </remarks>
        public void Restart()
        {
            lock (_gate)
            {
                _started = false;
                _elapsedSeconds = 0.0;
                BlockCount = 0;
            }
        }

        /// <summary>
        /// When the trigger event occurred, for a block placed on this timeline.
        /// </summary>
        /// <param name="firstSampleUtc">The block's timestamp.</param>
        /// <param name="triggerOffsetSeconds">
        /// The block's <c>TriggerOffsetSeconds</c>: how far after the first sample the trigger lies.
        /// </param>
        /// <remarks>
        /// The relationship stated as code, so that a caller reasoning about it cannot get the sign
        /// the wrong way round without a test failing.
        /// </remarks>
        public static DateTime TriggerInstant(DateTime firstSampleUtc, double triggerOffsetSeconds) =>
            firstSampleUtc.AddTicks(
                (long)Math.Round(triggerOffsetSeconds * TimeSpan.TicksPerSecond));
    }
}
