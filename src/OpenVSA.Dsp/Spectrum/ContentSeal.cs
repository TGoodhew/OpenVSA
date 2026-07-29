using System;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// A cheap content hash used to catch a snapshot being mutated after publication
    /// (<c>REQ-NFR-011</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-NFR-011</c> is a claim about ownership: a buffer handed to the UI is never written
    /// again. The claim is enforced by construction — each snapshot owns its array exclusively and
    /// exposes it only as a <see cref="ReadOnlySpan{T}"/>, which cannot be stored in a field — but
    /// "enforced by construction" is exactly the kind of assurance that survives a refactor by
    /// accident rather than by design.
    /// </para>
    /// <para>
    /// So the soak seals every published snapshot and checks the seal after the UI has read it. A
    /// mismatch means something wrote to a buffer it had already given away, which is the torn frame
    /// the requirement forbids.
    /// </para>
    /// <para>
    /// <strong>Off unless switched on.</strong> Hashing every level array would cost a pass over
    /// the data on the measurement thread, at a corner of <c>REQ-NFR-021</c> where there are 2²⁰
    /// points ten times a second. The soak switches it on; nothing else does.
    /// </para>
    /// <para>
    /// <strong>What this does not detect.</strong> It finds writes to a published buffer. It does
    /// not find a torn read of a wider-than-atomic field, a stale value from a missing barrier, or
    /// a lock-ordering bug — .NET has no ThreadSanitizer, and pretending otherwise would be worse
    /// than saying so. What the soak asserts is stated in those terms and no stronger.
    /// </para>
    /// </remarks>
    public static class ContentSeal
    {
        /// <summary>Whether snapshots are sealed as they are published.</summary>
        /// <remarks>
        /// A process-wide switch rather than a constructor argument, because the things that need
        /// sealing are created in half a dozen places and every one of them would otherwise have to
        /// be threaded through from the soak.
        /// </remarks>
        public static bool Enabled { get; set; }

        /// <summary>Hashes a span of floats.</summary>
        /// <param name="values">What to hash.</param>
        /// <returns>The hash, or zero for an empty span.</returns>
        /// <remarks>
        /// FNV-1a over the raw bits. Chosen for speed and for treating every bit as significant —
        /// a checksum that summed the values would miss two samples swapping places, which is
        /// precisely what a partially-overwritten buffer looks like.
        /// </remarks>
        public static ulong Of(ReadOnlySpan<float> values)
        {
            if (values.Length == 0)
            {
                return 0UL;
            }

            const ulong Offset = 14695981039346656037UL;
            const ulong Prime = 1099511628211UL;

            ulong hash = Offset;

            unsafe
            {
                fixed (float* start = values)
                {
                    // The bits, not the value. NaN compares unequal to itself, so a hash built from
                    // comparisons would make a spectrum with an empty bin in it appear to change
                    // every time it was checked. Reinterpreted through a pointer rather than
                    // BitConverter.GetBytes, which allocates a four-byte array per sample and would
                    // turn the seal into the thing being measured.
                    uint* bits = (uint*)start;

                    for (int i = 0; i < values.Length; i++)
                    {
                        uint word = bits[i];

                        hash = (hash ^ (word & 0xFF)) * Prime;
                        hash = (hash ^ ((word >> 8) & 0xFF)) * Prime;
                        hash = (hash ^ ((word >> 16) & 0xFF)) * Prime;
                        hash = (hash ^ (word >> 24)) * Prime;
                    }
                }
            }

            return hash;
        }
    }
}
