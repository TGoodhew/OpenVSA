using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Limits = OpenVSA.Measurement.Limits;

namespace OpenVSA.Api
{
    /// <summary>
    /// The root of the automation object model (<c>REQ-API-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the beginning of <c>REQ-API-001</c>'s hierarchy, not the whole of it.</strong>
    /// That requirement asks for
    /// <c>Application → Measurement(s) → Input / Trigger / Display → Trace(s) → Marker(s)</c> and
    /// is scoped to its own issue; what is here is the spine plus the limit-test branch that
    /// <c>REQ-LIM-003</c> needs. Members are added as the requirements that need them are built,
    /// rather than declared now and left throwing — an API surface that exists and does not work is
    /// worse than one that does not exist, because it is discoverable.
    /// </para>
    /// <para>
    /// <strong>No UI, ever.</strong> <c>REQ-API-002</c> requires the API to be usable with no UI
    /// loaded, and this assembly's project file says it must never reference <c>OpenVSA.Ui</c>. The
    /// limit verdicts it reports come from <see cref="Limits.LimitEvaluator"/> in the measurement
    /// layer, which is the same object the display reads — not a second evaluation performed for
    /// the API's benefit.
    /// </para>
    /// <para>
    /// Named <c>VsaApplication</c> rather than <c>Application</c> so that a caller can hold it
    /// alongside WPF's <c>Application</c> without an alias, and so that this type never has to be
    /// disambiguated from the <c>OpenVSA.Measurement</c> namespace inside its own assembly.
    /// </para>
    /// </remarks>
    public sealed class VsaApplication
    {
        private readonly List<VsaMeasurement> _measurements = new List<VsaMeasurement>();

        /// <summary>Creates an application with one measurement, as the shell has.</summary>
        public VsaApplication()
            : this("Measurement 1")
        {
        }

        /// <summary>Creates an application with named measurements.</summary>
        /// <param name="names">One name per measurement; at least one.</param>
        /// <exception cref="ArgumentException">No names were given, or one is blank.</exception>
        public VsaApplication(params string[] names)
        {
            if (names == null || names.Length == 0)
            {
                throw new ArgumentException(
                    "An application has at least one measurement.", nameof(names));
            }

            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
                {
                    throw new ArgumentException("A measurement needs a name.", nameof(names));
                }

                _measurements.Add(new VsaMeasurement(name.Trim()));
            }
        }

        /// <summary>The measurements in this session (<c>REQ-STA-004</c>'s contexts).</summary>
        public IReadOnlyList<VsaMeasurement> Measurements =>
            new ReadOnlyCollection<VsaMeasurement>(_measurements);

        /// <summary>
        /// The measurement of that name, or <c>null</c>.
        /// </summary>
        /// <param name="name">The measurement's name; matched without regard to case.</param>
        public VsaMeasurement Measurement(string name)
        {
            foreach (VsaMeasurement measurement in _measurements)
            {
                if (string.Equals(measurement.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return measurement;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public override string ToString() => _measurements.Count + " measurement(s)";
    }

    /// <summary>
    /// One measurement, as the automation API sees it (<c>REQ-API-001</c>).
    /// </summary>
    public sealed class VsaMeasurement
    {
        internal VsaMeasurement(string name)
        {
            Name = name;
            LimitTests = new VsaLimitTests(Evaluator);
        }

        /// <summary>The measurement's name.</summary>
        public string Name { get; }

        /// <summary>
        /// The evaluator this measurement's limit verdicts come from.
        /// </summary>
        /// <remarks>
        /// Exposed so the shell can hand the API the very object its display reads. The alternative
        /// — the API owning its own evaluator — is the "separately computed verdict"
        /// <c>REQ-LIM-003</c> names as the failure.
        /// </remarks>
        public Limits.LimitEvaluator Evaluator { get; } = new Limits.LimitEvaluator();

        /// <summary>The limit tests on this measurement (<c>REQ-LIM-003</c>).</summary>
        public VsaLimitTests LimitTests { get; }

        /// <inheritdoc />
        public override string ToString() => Name;
    }

    /// <summary>
    /// The limit-test surface of the automation API (<c>REQ-LIM-003</c>).
    /// </summary>
    /// <remarks>
    /// The reference product exposes <c>:TRACe:LIMit:COUNt</c> and <c>:TRACe:LIMit:RESult</c>;
    /// these are the same two questions in OpenVSA's own model. Per <c>REQ-API-001</c> the names
    /// here are OpenVSA's and no test asserts them against the reference product's.
    /// </remarks>
    public sealed class VsaLimitTests
    {
        private readonly Limits.LimitEvaluator _evaluator;

        internal VsaLimitTests(Limits.LimitEvaluator evaluator)
        {
            _evaluator = evaluator;
        }

        /// <summary>
        /// How many limit lines are under test.
        /// </summary>
        /// <remarks>
        /// The lines, because they are what pass or fail. A test carrying no lines counts zero and
        /// reports no verdict, which is the honest pair of answers.
        /// </remarks>
        public int Count => _evaluator.LineCount;

        /// <summary>
        /// Whether the last completed evaluation passed, or <c>null</c> if there has been none.
        /// </summary>
        /// <remarks>
        /// <c>null</c> rather than <c>true</c>. "Not evaluated" and "passed" are different answers,
        /// and an API that returned a pass for a measurement that had not started would be
        /// reporting the most dangerous of the two.
        /// </remarks>
        public bool? Passed
        {
            get
            {
                Limits.LimitTestResult result = _evaluator.Latest;

                return result == null ? (bool?)null : result.Passed;
            }
        }

        /// <summary>The last completed verdict, or <c>null</c>.</summary>
        public Limits.LimitTestResult Result => _evaluator.Latest;

        /// <summary>The per-line verdicts of the last completed evaluation.</summary>
        public IReadOnlyList<Limits.LimitLineResult> Lines => _evaluator.Lines;

        /// <summary>
        /// The worst margin of the last completed evaluation, in decibels, or NaN.
        /// </summary>
        /// <remarks>
        /// Positive when passing and negative when failing, which is
        /// <c>LimitTestResult.WorstMarginDb</c>'s convention rather than a second one invented
        /// here.
        /// </remarks>
        public double WorstMarginDb
        {
            get
            {
                Limits.LimitTestResult result = _evaluator.Latest;

                return result == null ? double.NaN : result.WorstMarginDb;
            }
        }

        /// <summary>How many evaluations have completed.</summary>
        public long EvaluationCount => _evaluator.CompletedCount;

        /// <inheritdoc />
        public override string ToString() => _evaluator.ToString();
    }
}
