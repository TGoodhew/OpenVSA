using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenVSA.Measurement.State
{
    /// <summary>
    /// Raised when a state names contexts the application does not have (<c>REQ-STA-004</c>).
    /// </summary>
    [Serializable]
    public class ContextMismatchException : InvalidOperationException
    {
        private static readonly string[] None = new string[0];

        /// <summary>Creates the exception.</summary>
        public ContextMismatchException()
            : base("The state names contexts this application does not have.")
        {
            Missing = None;
            Available = None;
        }

        /// <summary>Creates the exception with a message.</summary>
        /// <param name="message">What did not match.</param>
        public ContextMismatchException(string message)
            : base(message)
        {
            Missing = None;
            Available = None;
        }

        /// <summary>Creates the exception with a message and an inner exception.</summary>
        /// <param name="message">What did not match.</param>
        /// <param name="inner">The underlying cause.</param>
        public ContextMismatchException(string message, Exception inner)
            : base(message, inner)
        {
            Missing = None;
            Available = None;
        }

        /// <summary>Creates the exception naming what did not match.</summary>
        /// <param name="message">What did not match, in words.</param>
        /// <param name="missing">The context names the state asked for and did not find.</param>
        /// <param name="available">The context names the application has.</param>
        public ContextMismatchException(
            string message, IReadOnlyList<string> missing, IReadOnlyList<string> available)
            : base(message)
        {
            Missing = missing ?? None;
            Available = available ?? None;
        }

        /// <summary>Deserialisation constructor.</summary>
        /// <param name="info">Serialisation data.</param>
        /// <param name="context">Streaming context.</param>
        protected ContextMismatchException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
            Missing = None;
            Available = None;
        }

        /// <summary>The context names the state asked for and did not find.</summary>
        public IReadOnlyList<string> Missing { get; }

        /// <summary>The context names the application has.</summary>
        public IReadOnlyList<string> Available { get; }
    }

    /// <summary>
    /// Applies a saved state to the application's measurement contexts (<c>REQ-STA-004</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Matched by name, and all or nothing.</strong> Matching by position would apply one
    /// measurement's settings to another whenever contexts had been reordered, and would do it
    /// silently. Applying what matches and reporting the rest is worse still: it leaves the
    /// instrument in a configuration that was never saved and that nobody chose, which is exactly
    /// the state the requirement exists to prevent — so the whole recall is validated before any
    /// of it is applied.
    /// </para>
    /// <para>
    /// The error names the contexts that did not match and what was expected, because "recall
    /// failed" tells the user nothing they can act on.
    /// </para>
    /// </remarks>
    public static class StateRecall
    {
        /// <summary>
        /// The context names a state asks for that are not in a set of contexts.
        /// </summary>
        /// <param name="state">The saved state.</param>
        /// <param name="contexts">The context names the application has.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static IReadOnlyList<string> Mismatches(
            ApplicationState state, IEnumerable<string> contexts)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (contexts == null)
            {
                throw new ArgumentNullException(nameof(contexts));
            }

            var have = new HashSet<string>(contexts, StringComparer.Ordinal);

            return state.Measurements
                .Select(m => m.ContextName)
                .Where(name => !have.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Applies a state to a set of contexts, or refuses the whole recall.
        /// </summary>
        /// <param name="state">The saved state.</param>
        /// <param name="contexts">
        /// The application's contexts, keyed by name. Every entry the state names must be present;
        /// contexts the state does not name are left alone.
        /// </param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ContextMismatchException">A named context does not exist.</exception>
        /// <remarks>
        /// The mismatch check runs to completion before anything is written, so a refused recall
        /// leaves the configuration exactly as it was.
        /// </remarks>
        public static void Apply(
            ApplicationState state, IDictionary<string, MeasurementState> contexts)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (contexts == null)
            {
                throw new ArgumentNullException(nameof(contexts));
            }

            IReadOnlyList<string> missing = Mismatches(state, contexts.Keys);

            if (missing.Count > 0)
            {
                var available = contexts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

                throw new ContextMismatchException(
                    "The state names " +
                    (missing.Count == 1 ? "a context" : missing.Count + " contexts") +
                    " this application does not have: " +
                    string.Join(", ", missing.Select(Quote)) +
                    ". It has " + string.Join(", ", available.Select(Quote)) +
                    ". Nothing has been changed.",
                    missing,
                    available);
            }

            foreach (MeasurementState measurement in state.Measurements)
            {
                contexts[measurement.ContextName] = measurement;
            }
        }

        private static string Quote(string name) => "'" + name + "'";
    }
}
