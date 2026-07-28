using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace OpenVSA.PerformanceGate
{
    /// <summary>What a whole run concluded, and the text a reader sees.</summary>
    /// <remarks>
    /// The text is built here rather than at the console so it can be asserted. A report that says
    /// "passed" for a run that resolved nothing is the failure mode this whole requirement exists
    /// to prevent, and it is a property of the wording as much as of the arithmetic.
    /// </remarks>
    public sealed class GateReport
    {
        internal GateReport(
            MachineClass machine,
            bool recognised,
            double threshold,
            IList<TargetVerdict> verdicts)
        {
            Machine = machine;
            MachineRecognised = recognised;
            Threshold = threshold;
            Verdicts = new ReadOnlyCollection<TargetVerdict>(verdicts);
        }

        /// <summary>The machine the run was taken on.</summary>
        public MachineClass Machine { get; }

        /// <summary>Whether any baseline exists for that machine class.</summary>
        public bool MachineRecognised { get; }

        /// <summary>The threshold applied.</summary>
        public double Threshold { get; }

        /// <summary>One verdict per target, in requirement order.</summary>
        public IReadOnlyList<TargetVerdict> Verdicts { get; }

        /// <summary>How many verdicts of a kind the run produced.</summary>
        /// <param name="verdict">The kind.</param>
        public int Count(Verdict verdict)
        {
            int n = 0;

            foreach (TargetVerdict v in Verdicts)
            {
                if (v.Verdict == verdict)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>Whether the run should fail the build.</summary>
        public bool Failed
        {
            get
            {
                foreach (TargetVerdict v in Verdicts)
                {
                    if (v.Fails)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>The process exit code: 0 passed, 1 failed.</summary>
        public int ExitCode => Failed ? 1 : 0;

        /// <summary>The whole report as text.</summary>
        public string Render()
        {
            var b = new StringBuilder();

            b.Append("REQ-TST-007 performance gate — ")
             .Append((Threshold * 100.0).ToString("F0", CultureInfo.InvariantCulture))
             .Append("% regression fails\n");
            b.Append("  machine: ").Append(Machine.Key).Append('\n');

            if (!MachineRecognised)
            {
                // Reported, not treated as a pass and not treated as a failure. Comparing a CI
                // runner against the reference machine's baseline would fire on the hardware.
                b.Append("  NO BASELINE for this machine class. Figures below are recorded, not judged;\n")
                 .Append("  the targets are stated for the reference machine and comparing across\n")
                 .Append("  machine classes measures the hardware rather than the change.\n");
            }

            b.Append('\n');

            foreach (TargetVerdict v in Verdicts)
            {
                b.Append("  ").Append(Label(v.Verdict).PadRight(14))
                 .Append(v.Target.Requirement.PadRight(12))
                 .Append(Detail(v)).Append('\n');
            }

            b.Append('\n');
            b.Append("  ").Append(Count(Verdict.Passed)).Append(" passed, ")
             .Append(Count(Verdict.Regressed)).Append(" regressed, ")
             .Append(Count(Verdict.Inconclusive)).Append(" inconclusive, ")
             .Append(Count(Verdict.NoBaseline)).Append(" unbaselined, ")
             .Append(Count(Verdict.AwaitingPhase)).Append(" not yet measurable, ")
             .Append(Count(Verdict.Missing)).Append(" missing\n");

            if (Count(Verdict.Missing) > 0)
            {
                b.Append("\n  A target whose feature exists produced no measurement. The harness may not\n")
                 .Append("  quietly shrink to the targets that happen to be implemented.\n");
            }

            return b.ToString();
        }

        private static string Label(Verdict verdict)
        {
            switch (verdict)
            {
                case Verdict.Passed: return "PASS";
                case Verdict.Regressed: return "REGRESSED";
                case Verdict.Inconclusive: return "INCONCLUSIVE";
                case Verdict.NoBaseline: return "recorded";
                case Verdict.AwaitingPhase: return "not yet";
                case Verdict.Missing: return "MISSING";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(verdict), verdict, "Not a known verdict.");
            }
        }

        private static string Detail(TargetVerdict v)
        {
            PerformanceTarget t = v.Target;

            switch (v.Verdict)
            {
                case Verdict.AwaitingPhase:
                    return t.Description + " — waits on Phase " + t.AwaitingPhase.Value;

                case Verdict.Missing:
                    return t.Description + " — no measurement, and its feature is built";

                case Verdict.NoBaseline:
                    return Value(v) + "   " + t.Description;

                default:
                    return Value(v) + ", baseline " +
                        v.Baseline.Mean.ToString("G6", CultureInfo.InvariantCulture) +
                        ", " + Change(v.RelativeChange) +
                        "   " + t.Description;
            }
        }

        private static string Value(TargetVerdict v) =>
            v.Measurement.Mean.ToString("G6", CultureInfo.InvariantCulture) + " " + v.Target.Unit +
            " ±" + (v.Measurement.RelativeResolution * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";

        private static string Change(double relative)
        {
            if (double.IsNaN(relative))
            {
                return "no comparison";
            }

            // Signed so that positive always reads as worse, whichever direction the target
            // counts as better. "+18% worse" and "-4% better" need no unit to interpret.
            double percent = relative * 100.0;

            return percent >= 0.0
                ? "+" + percent.ToString("F1", CultureInfo.InvariantCulture) + "% worse"
                : (-percent).ToString("F1", CultureInfo.InvariantCulture) + "% better";
        }
    }
}
