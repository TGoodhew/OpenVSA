using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text;

namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// Thrown when the chain executes a step out of the declared order (<c>REQ-DEM-001</c>).
    /// </summary>
    /// <remarks>
    /// A distinct type rather than <see cref="InvalidOperationException"/> because the tests that
    /// prove the order is enforced have to be able to say which failure they provoked. Catching
    /// "something threw" would pass just as well against a chain that fell over for an unrelated
    /// reason.
    /// </remarks>
    [Serializable]
    public class ChainOrderException : InvalidOperationException
    {
        /// <summary>Creates the exception with a message.</summary>
        /// <param name="message">What was executed out of order.</param>
        public ChainOrderException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception with a message and an inner cause.</summary>
        /// <param name="message">What was executed out of order.</param>
        /// <param name="inner">The cause.</param>
        public ChainOrderException(string message, Exception inner)
            : base(message, inner)
        {
        }

        /// <summary>Creates an empty exception.</summary>
        public ChainOrderException()
        {
        }

        /// <summary>Deserialisation constructor.</summary>
        /// <param name="info">The serialised data.</param>
        /// <param name="context">The streaming context.</param>
        protected ChainOrderException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>One step's turn in one pass of the chain.</summary>
    public readonly struct ChainEntry
    {
        internal ChainEntry(int pass, DemodStep step, bool executed)
        {
            Pass = pass;
            Step = step;
            Executed = executed;
        }

        /// <summary>Which pass over the chain this was, counting from one.</summary>
        public int Pass { get; }

        /// <summary>The step.</summary>
        public DemodStep Step { get; }

        /// <summary>
        /// Whether the step ran, as opposed to being skipped because it was optional and off.
        /// </summary>
        public bool Executed { get; }

        /// <inheritdoc />
        public override string ToString() =>
            "pass " + Pass.ToString(CultureInfo.InvariantCulture) + ", step " +
            ProcessingOrder.NumberOf(Step).ToString(CultureInfo.InvariantCulture) + " " + Step +
            (Executed ? string.Empty : " (skipped)");
    }

    /// <summary>
    /// What the chain actually did, and the thing that refuses to record it out of order
    /// (<c>REQ-DEM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the enforcement, not a log.</strong> <c>REQ-DEM-001</c> asks that "a test
    /// fails if any step executes out of declared order", and a check that lives only in a test can
    /// only catch the paths that test walks. Recording through the journal is how every step
    /// announces itself, so the check runs on every demodulation the product ever performs,
    /// including the ones nobody wrote a test for.
    /// </para>
    /// <para>
    /// <strong>The one permitted backward movement.</strong> A pass after the first may begin at
    /// <see cref="ProcessingOrder.ReEntryPoint"/> and nowhere else. That is the specification's
    /// "re-enters at 8 on update" written as a rule: the equaliser's loop is legal, and a step that
    /// re-ran an earlier one for its own convenience is not — which is the distinction a plain
    /// "the numbers must increase" check cannot make, because it would forbid both, and a plain
    /// "record whatever happens" makes neither.
    /// </para>
    /// <para>
    /// <strong>Skips are recorded, not omitted.</strong> An optional step that was off appears with
    /// <see cref="ChainEntry.Executed"/> false. Omitting it would leave a gap indistinguishable
    /// from a step that was forgotten, and the criterion about optional steps is precisely that the
    /// others keep their order around them.
    /// </para>
    /// </remarks>
    public sealed class ChainJournal
    {
        private readonly List<ChainEntry> _entries = new List<ChainEntry>();
        private readonly ReadOnlyCollection<ChainEntry> _readOnly;

        /// <summary>Creates an empty journal.</summary>
        public ChainJournal()
        {
            _readOnly = new ReadOnlyCollection<ChainEntry>(_entries);
        }

        /// <summary>Every step's turn, in the sequence it happened.</summary>
        /// <remarks>
        /// A view of the list rather than a copy of it, so a caller holding this sees the entries
        /// arrive as the chain runs — which is what a journal is for — and reading it costs
        /// nothing.
        /// </remarks>
        public IReadOnlyList<ChainEntry> Entries => _readOnly;

        /// <summary>How many passes over the chain there have been.</summary>
        /// <remarks>Zero before the first step is recorded; one for a chain with no re-entry.</remarks>
        public int PassCount => _entries.Count == 0 ? 0 : _entries[_entries.Count - 1].Pass;

        /// <summary>Records a step's turn, refusing it if it is out of order.</summary>
        /// <param name="pass">Which pass over the chain this is, counting from one.</param>
        /// <param name="step">The step.</param>
        /// <param name="executed">Whether it ran, as opposed to being skipped.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="pass"/> is below one, or <paramref name="step"/> is not a known step.
        /// </exception>
        /// <exception cref="ChainOrderException">
        /// The step comes before one already recorded in this pass, the pass number skipped a
        /// value or went backwards, or a later pass began somewhere other than the declared
        /// re-entry point.
        /// </exception>
        public void Record(int pass, DemodStep step, bool executed)
        {
            if (pass < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pass), pass, "Passes are counted from one.");
            }

            int position = ProcessingOrder.PositionOf(step);

            if (_entries.Count == 0)
            {
                RequireFirstEntry(pass, step);
            }
            else
            {
                ChainEntry previous = _entries[_entries.Count - 1];

                if (pass == previous.Pass)
                {
                    RequireForwardWithinPass(previous, step, position);
                }
                else
                {
                    RequireLegalNewPass(previous, pass, step);
                }
            }

            _entries.Add(new ChainEntry(pass, step, executed));
        }

        /// <summary>The steps that actually ran, in sequence, across every pass.</summary>
        /// <returns>The executed steps; skipped ones are left out.</returns>
        public IReadOnlyList<DemodStep> Executed()
        {
            var executed = new List<DemodStep>(_entries.Count);

            foreach (ChainEntry entry in _entries)
            {
                if (entry.Executed)
                {
                    executed.Add(entry.Step);
                }
            }

            return new ReadOnlyCollection<DemodStep>(executed);
        }

        /// <summary>The steps of one pass, in sequence, whether they ran or were skipped.</summary>
        /// <param name="pass">The pass, counting from one.</param>
        /// <returns>That pass's entries.</returns>
        public IReadOnlyList<ChainEntry> Pass(int pass)
        {
            var entries = new List<ChainEntry>();

            foreach (ChainEntry entry in _entries)
            {
                if (entry.Pass == pass)
                {
                    entries.Add(entry);
                }
            }

            return new ReadOnlyCollection<ChainEntry>(entries);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var text = new StringBuilder();

            foreach (ChainEntry entry in _entries)
            {
                if (text.Length > 0)
                {
                    text.Append(Environment.NewLine);
                }

                text.Append(entry);
            }

            return text.ToString();
        }

        private static void RequireFirstEntry(int pass, DemodStep step)
        {
            if (pass != 1)
            {
                throw new ChainOrderException(
                    "The first step recorded was in pass " +
                    pass.ToString(CultureInfo.InvariantCulture) +
                    ". A chain starts at pass 1.");
            }

            if (step != ProcessingOrder.Steps[0])
            {
                throw new ChainOrderException(
                    "The chain started at " + Name(step) + ". The declared order starts at " +
                    Name(ProcessingOrder.Steps[0]) + ".");
            }
        }

        private void RequireForwardWithinPass(ChainEntry previous, DemodStep step, int position)
        {
            if (position <= ProcessingOrder.PositionOf(previous.Step))
            {
                throw new ChainOrderException(
                    Name(step) + " was executed after " + Name(previous.Step) +
                    " within one pass, but the declared order of REQ-DEM-001 puts it " +
                    (position == ProcessingOrder.PositionOf(previous.Step) ? "at" : "before") +
                    " that step. Only the equaliser's re-entry at " +
                    Name(ProcessingOrder.ReEntryPoint) + " may go backwards, and it does so by " +
                    "starting a new pass.");
            }
        }

        private static void RequireLegalNewPass(ChainEntry previous, int pass, DemodStep step)
        {
            if (pass != previous.Pass + 1)
            {
                throw new ChainOrderException(
                    "Pass " + pass.ToString(CultureInfo.InvariantCulture) + " followed pass " +
                    previous.Pass.ToString(CultureInfo.InvariantCulture) +
                    ". Passes are consecutive.");
            }

            if (step != ProcessingOrder.ReEntryPoint)
            {
                throw new ChainOrderException(
                    "Pass " + pass.ToString(CultureInfo.InvariantCulture) + " began at " +
                    Name(step) + ". A later pass exists only because the equaliser updated its " +
                    "coefficients, and REQ-DEM-001 has it re-enter at " +
                    Name(ProcessingOrder.ReEntryPoint) + ".");
            }

            // The equaliser is the only thing that asks for a re-entry, so a pass that stopped
            // before reaching it cannot have asked. Comparing against the re-entry point instead
            // would let a pass that ended at step 9 open a second one, which is the case this
            // check exists for.
            if (ProcessingOrder.IsAfter(DemodStep.Equaliser, previous.Step))
            {
                throw new ChainOrderException(
                    "Pass " + previous.Pass.ToString(CultureInfo.InvariantCulture) +
                    " ended at " + Name(previous.Step) + ", before the equaliser had run. " +
                    "Nothing in that pass could have asked for a re-entry.");
            }
        }

        private static string Name(DemodStep step) =>
            "step " + ProcessingOrder.NumberOf(step).ToString(CultureInfo.InvariantCulture) +
            " (" + step + ")";
    }
}
