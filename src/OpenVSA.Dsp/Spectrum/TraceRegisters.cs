using System;
using System.Globalization;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The data registers trace math stores and recalls traces through (<c>REQ-DSP-046</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A register holds the frame, not a copy of it.</strong> A
    /// <see cref="SpectrumFrame"/> is immutable (<c>REQ-NFR-011</c>), so nothing can alter what a
    /// register holds after it is stored, and keeping the reference is what makes recall
    /// bit-identical by construction rather than by careful copying. Copying would additionally
    /// mean an array per store, which at a 2²⁰-point trace is 8 MB for no gain.
    /// </para>
    /// <para>
    /// Registers are numbered from 1 and named <c>D1</c> upwards, which is what the operator will
    /// look for. Storing into an empty register and recalling from one are different situations
    /// and are distinguished: recalling an empty register returns <c>null</c> rather than throwing,
    /// because "nothing has been stored there yet" is a state the UI has to show, not an error.
    /// </para>
    /// </remarks>
    public sealed class TraceRegisters
    {
        /// <summary>Registers provided by default.</summary>
        public const int DefaultCount = 8;

        private readonly SpectrumFrame[] _registers;

        /// <summary>Creates a set of registers.</summary>
        /// <param name="count">How many; must be at least one.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than one.</exception>
        public TraceRegisters(int count = DefaultCount)
        {
            if (count < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count), count, "A register set holds at least one register.");
            }

            _registers = new SpectrumFrame[count];
        }

        /// <summary>How many registers there are.</summary>
        public int Count => _registers.Length;

        /// <summary>The name of a register, as it is shown and selected by.</summary>
        /// <param name="register">Register number, from 1.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such register.</exception>
        public string NameOf(int register)
        {
            Validate(register);
            return "D" + register.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Stores a trace, replacing whatever was there.
        /// </summary>
        /// <param name="register">Register number, from 1.</param>
        /// <param name="frame">The trace, or <c>null</c> to empty the register.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such register.</exception>
        public void Store(int register, SpectrumFrame frame)
        {
            Validate(register);
            // REQ-NFR-002: a register holds a frame indefinitely -- that is what a register is --
            // so it takes its own share, and the frame it displaces gives one back.
            frame?.Retain();

            SpectrumFrame displaced = _registers[register - 1];

            _registers[register - 1] = frame;

            displaced?.Release();
        }

        /// <summary>
        /// Recalls a trace.
        /// </summary>
        /// <param name="register">Register number, from 1.</param>
        /// <returns>The stored trace, or <c>null</c> if the register is empty.</returns>
        /// <exception cref="ArgumentOutOfRangeException">There is no such register.</exception>
        public SpectrumFrame Recall(int register)
        {
            Validate(register);
            return _registers[register - 1];
        }

        /// <summary>Whether a register holds a trace.</summary>
        /// <param name="register">Register number, from 1.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such register.</exception>
        public bool IsOccupied(int register)
        {
            Validate(register);
            return _registers[register - 1] != null;
        }

        /// <summary>Empties every register.</summary>
        public void Clear()
        {
            for (int i = 0; i < _registers.Length; i++)
            {
                _registers[i]?.Release();
                _registers[i] = null;
            }
        }

        private void Validate(int register)
        {
            if (register < 1 || register > _registers.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(register), register,
                    "Registers are numbered 1 to " +
                    _registers.Length.ToString(CultureInfo.CurrentCulture) + ".");
            }
        }
    }
}
