using System;

namespace OpenVSA.Core
{
    /// <summary>
    /// Identifies the front end that produced a block of samples.
    /// </summary>
    /// <remarks>
    /// Deliberately opaque to the DSP and measurement layers. Per <c>REQ-ARC-001</c> those layers
    /// must be incapable of distinguishing a live instrument from a file or a simulator, so this
    /// type carries identity for provenance and logging only — never for behavioural branching.
    /// </remarks>
    public struct FrontEndId : IEquatable<FrontEndId>
    {
        private readonly string _value;

        /// <summary>Creates an identifier from its string form.</summary>
        /// <param name="value">Stable, unique identifier for the front end instance.</param>
        public FrontEndId(string value)
        {
            _value = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>An unset identifier.</summary>
        public static FrontEndId None => default(FrontEndId);

        /// <summary>The string form of this identifier, or an empty string when unset.</summary>
        public string Value => _value ?? string.Empty;

        /// <inheritdoc />
        public bool Equals(FrontEndId other) =>
            string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is FrontEndId other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => Value.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => Value;
    }
}
