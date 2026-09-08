using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.PerformanceGate;
using OpenVSA.Synthesis;

namespace OpenVSA.Benchmarks
{
    /// <summary>
    /// <c>REQ-NFR-022</c> and <c>REQ-NFR-023</c>: how long a flexible demodulation takes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both targets were declared when the gate was built and have sat at <c>AwaitingPhase</c> ever
    /// since, because they need the Phase 2 demodulator. It exists now, so what was owed is the
    /// measurement rather than the target.
    /// </para>
    /// <para>
    /// <strong>Only the demodulation is timed.</strong> Generating the signal costs more than
    /// demodulating it and has nothing to do with either requirement, so it happens once, outside
    /// the clock, and every replicate demodulates the same samples. That also removes the
    /// generator's own variance from the figure, which would otherwise be reported as the
    /// demodulator's.
    /// </para>
    /// <para>
    /// <strong>Warmed up first, and that is not a nicety.</strong> The first call jits the whole
    /// fourteen-step chain, and on <c>REQ-NFR-022</c>'s 50 ms budget the jit is a large fraction of
    /// the answer — an unwarmed first run would report the compiler rather than the code. The
    /// warm-up runs are discarded rather than averaged in.
    /// </para>
    /// <para>
    /// <strong>The settings are the requirements' own words</strong>, not a convenient nearby
    /// configuration: 16-QAM at 4 096 symbols and four points a symbol with the equaliser off, and
    /// 1024-QAM at 4 000 symbols with the equaliser on at 31 symbols. Where a requirement states a
    /// number, it is here as a constant with its name beside it.
    /// </para>
    /// </remarks>
    public static class DemodMeasurements
    {
        /// <summary>Timed replicates per target.</summary>
        /// <remarks>
        /// Eleven, and the median of them is not what is reported — the mean and standard deviation
        /// are, because that is what <see cref="TargetMeasurement"/> carries and what the gate's
        /// resolution check reads. Eleven is enough for the deviation to mean something without
        /// making <c>REQ-NFR-023</c>'s four-hundred-millisecond target take a minute to measure.
        /// </remarks>
        private const int Replicates = 11;

        /// <summary>Discarded runs before the clock is believed.</summary>
        private const int WarmUp = 3;

        /// <summary>Both targets, measured.</summary>
        /// <returns>One measurement per requirement.</returns>
        public static IList<TargetMeasurement> Run()
        {
            return new List<TargetMeasurement>
            {
                Time(
                    "Demod16Qam4096Symbols",
                    Constellation.Qam(16),
                    symbols: 4096,
                    pointsPerSymbol: 4,
                    equaliserSymbols: 0),

                Time(
                    "Demod1024Qam4000SymbolsEqualised",
                    Constellation.Qam(1024),
                    symbols: 4000,
                    pointsPerSymbol: 4,
                    equaliserSymbols: 31),
            };
        }

        private static TargetMeasurement Time(
            string name,
            Constellation format,
            int symbols,
            int pointsPerSymbol,
            int equaliserSymbols)
        {
            const double SymbolRateHz = 1e6;

            double sampleRateHz = SymbolRateHz * 8.0;

            var points = new List<SymbolPoint>(format.Count);

            foreach (ConstellationPoint point in format.Points)
            {
                points.Add(new SymbolPoint(point.I, point.Q));
            }

            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.FromPoints(
                    format.Name, points, format.IsOffset, format.RotationPerSymbolRadians),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = sampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 12,

                // A clean signal. These requirements are about how long the analysis takes, and a
                // noisy one would make the equaliser's iteration count -- and so the time -- a
                // property of the noise realisation rather than of the work being timed.
                Seed = 20260907,
            };

            // Room for the Result Length, the filter transients and REQ-DEM-032's wider window.
            var samples = new float[
                2 * (int)Math.Ceiling((symbols * 1.5) * (sampleRateHz / SymbolRateHz))];

            source.Restart();
            source.Fill(samples);

            var settings = new DemodSettings
            {
                Constellation = format,
                SymbolRateHz = SymbolRateHz,
                PointsPerSymbol = pointsPerSymbol,
                ResultLengthSymbols = symbols,
                FilterSymbolSpan = 12,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
                EqualiserEnabled = equaliserSymbols > 0,
                EqualiserLengthSymbols = equaliserSymbols > 0 ? equaliserSymbols : DemodSettings.DefaultEqualiserLengthSymbols,
            };

            settings.Validate();

            var demodulator = new Demodulator();

            for (int run = 0; run < WarmUp; run++)
            {
                demodulator.Run(samples, sampleRateHz, settings);
            }

            var milliseconds = new double[Replicates];
            var clock = new Stopwatch();

            for (int run = 0; run < Replicates; run++)
            {
                clock.Restart();

                DemodResult result = demodulator.Run(samples, sampleRateHz, settings);

                clock.Stop();

                // THE COUNT, NOT MERELY THAT THERE IS ONE. Step 7 shortens the Result Length
                // window to whatever the record has room for and says so in a notice, so a record
                // slightly too short produces a faster demodulation of fewer symbols -- and the
                // requirement names the symbol count. Asserting only that something came back is
                // how a measurement of 4 096 symbols quietly becomes a measurement of 1 400.
                //
                // It also stops the jit eliding the chain as dead code, which is why the first
                // version read the result at all.
                if (result.Trace == null || result.Trace.SymbolCount != symbols)
                {
                    throw new InvalidOperationException(
                        name + " demodulated " +
                        (result.Trace == null ? 0 : result.Trace.SymbolCount) + " symbols, not " +
                        symbols + ". The record is too short for the Result Length the " +
                        "requirement names, so this would not be measuring it.");
                }

                milliseconds[run] = clock.Elapsed.TotalMilliseconds;
            }

            double mean = 0.0;

            foreach (double value in milliseconds)
            {
                mean += value;
            }

            mean /= Replicates;

            double sum = 0.0;

            foreach (double value in milliseconds)
            {
                sum += (value - mean) * (value - mean);
            }

            return new TargetMeasurement(
                name, mean, Math.Sqrt(sum / (Replicates - 1)), Replicates);
        }
    }
}
