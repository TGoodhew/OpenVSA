using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace OpenVSA.Benchmarks
{
    /// <summary>
    /// Entry point for the performance gates of <c>REQ-NFR-020</c> to <c>REQ-NFR-026</c>.
    /// </summary>
    /// <remarks>
    /// Run with no arguments for the full set, or <c>--filter *Spectrum*</c> for one class.
    /// <c>--job short</c> trades precision for a run that fits in a coffee break; the stored
    /// baselines the requirements' regression gate compares against must come from a full run.
    /// </remarks>
    public static class Program
    {
        /// <summary>Runs the benchmarks.</summary>
        /// <param name="args">BenchmarkDotNet command-line arguments.</param>
        /// <returns>Zero on success.</returns>
        public static int Main(string[] args)
        {
            // --gate judges a run rather than taking one. Measuring and deciding are separate
            // processes on purpose: BenchmarkDotNet launches its own optimised child to measure
            // in, so the deciding cannot live inside the measuring.
            foreach (string arg in args)
            {
                if (string.Equals(arg, "--gate", StringComparison.OrdinalIgnoreCase))
                {
                    return GateCommand.Run(args);
                }
            }

            BenchmarkSwitcher
                .FromAssembly(typeof(Program).Assembly)
                .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator));

            return 0;
        }
    }
}
