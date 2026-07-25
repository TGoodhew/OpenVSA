using System;
using System.Buffers;
using System.Collections.Generic;

namespace OpenVSA.Core
{
    /// <summary>
    /// Interleaved single-precision complex sample buffer plus acquisition metadata — the sole
    /// data type crossing the HAL boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-DAT-001</c>. Because the analysis layers see nothing but this type, they cannot tell
    /// a live instrument from a recording or the simulator, which is what <c>REQ-ARC-001</c>
    /// requires.
    /// </para>
    /// <para>
    /// <strong>The buffer is pooled and is never handed out.</strong> <c>REQ-DAT-001a</c> prohibits
    /// exposing a pooled array publicly on an <see cref="IDisposable"/>: a caller holding the
    /// reference past <see cref="Dispose"/> would read whatever the next consumer rented, silently
    /// and with no error. Samples are therefore reachable only through <see cref="GetSamples"/>,
    /// which checks disposal, and only as a <see cref="Span{T}"/>, which cannot be stored in a
    /// field. The check and the span are complementary: the check catches the immediate misuse, the
    /// span makes the deferred one hard to express.
    /// </para>
    /// </remarks>
    public sealed class IqBlock : IDisposable
    {
        private readonly IqBlockMetadata _metadata;
        private float[] _buffer;
        private bool _disposed;

        private IqBlock(in IqBlockMetadata metadata, float[] buffer)
        {
            _metadata = metadata;
            _buffer = buffer;
        }

        /// <summary>
        /// Rents a pooled buffer and returns a block that owns it until disposed.
        /// </summary>
        /// <param name="metadata">Acquisition metadata; every field is required.</param>
        /// <returns>A block whose sample buffer is zero-filled and ready to be written.</returns>
        /// <remarks>
        /// The rented array is normally larger than <c>2 × SampleCount</c> — <see
        /// cref="ArrayPool{T}.Rent"/> returns at least the requested size. That surplus is not
        /// observable through any member of this class, so the extra elements cannot be mistaken
        /// for data.
        /// </remarks>
        public static IqBlock Rent(in IqBlockMetadata metadata)
        {
            int floats = checked(metadata.SampleCount * 2);
            float[] buffer = ArrayPool<float>.Shared.Rent(floats);

            // A pooled array arrives holding the previous tenant's data. Clearing only the region
            // this block exposes is sufficient and avoids touching the surplus.
            Array.Clear(buffer, 0, floats);

            return new IqBlock(metadata, buffer);
        }

        /// <summary>
        /// The interleaved I,Q samples: exactly <c>2 × SampleCount</c> floats.
        /// </summary>
        /// <returns>A mutable view over the block's samples.</returns>
        /// <exception cref="ObjectDisposedException">The block has been disposed.</exception>
        /// <remarks>
        /// Deliberately a method rather than a property, and deliberately returning a span rather
        /// than the array — see the type-level remarks and <c>REQ-DAT-001a</c>.
        /// </remarks>
        public Span<float> GetSamples()
        {
            ThrowIfDisposed();
            return new Span<float>(_buffer, 0, _metadata.SampleCount * 2);
        }

        /// <summary>Number of complex samples in this block.</summary>
        public int SampleCount => _metadata.SampleCount;

        /// <summary>Sample rate in hertz.</summary>
        public double SampleRateHz => _metadata.SampleRateHz;

        /// <summary>Centre frequency in hertz.</summary>
        public double CenterFrequencyHz => _metadata.CenterFrequencyHz;

        /// <summary><c>true</c> for real baseband; <c>false</c> for a complex zoom/IF acquisition.</summary>
        public bool IsBaseband => _metadata.IsBaseband;

        /// <summary>ADC full scale referred to the input, in volts.</summary>
        public double FullScaleVolts => _metadata.FullScaleVolts;

        /// <summary>Reference level in dBm.</summary>
        public double ReferenceLevelDbm => _metadata.ReferenceLevelDbm;

        /// <summary>Monotonic sequence number within an acquisition session.</summary>
        public long SequenceNumber => _metadata.SequenceNumber;

        /// <summary>Acquisition timestamp, UTC.</summary>
        public DateTime AcquiredUtc => _metadata.AcquiredUtc;

        /// <summary>Trigger offset in seconds; negative values denote pre-trigger data.</summary>
        public double TriggerOffsetSeconds => _metadata.TriggerOffsetSeconds;

        /// <summary>
        /// Whether trigger delay/phase corrections have been applied to these samples
        /// (<c>REQ-DAT-002</c>).
        /// </summary>
        public bool TriggerCorrectionsApplied => _metadata.TriggerCorrectionsApplied;

        /// <summary>Identifies the front end that produced this block, for provenance only.</summary>
        public FrontEndId Source => _metadata.Source;

        /// <summary>Per-front-end extras. Never <c>null</c>.</summary>
        public IReadOnlyDictionary<string, object> Extended => _metadata.Extended;

        /// <summary>Whether this block has been disposed and its buffer returned to the pool.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// Reads one complex sample.
        /// </summary>
        /// <param name="index">Zero-based sample index.</param>
        /// <returns>The sample at <paramref name="index"/>.</returns>
        /// <exception cref="ObjectDisposedException">The block has been disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
        /// <remarks>
        /// Interpreted access per <c>REQ-DAT-003</c>. Bulk work uses <see cref="GetSamples"/> and
        /// operates on the raw floats so the loop can vectorise.
        /// </remarks>
        public Complex32 GetSample(int index)
        {
            ThrowIfDisposed();

            if (index < 0 || index >= _metadata.SampleCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, "Sample index is outside the block.");
            }

            int offset = index * 2;
            return new Complex32(_buffer[offset], _buffer[offset + 1]);
        }

        /// <summary>
        /// Returns the sample buffer to the pool. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            float[] buffer = _buffer;

            // Drop the reference before returning the array, so that a racing GetSamples cannot
            // observe a buffer that is already back in the pool.
            _buffer = null;

            if (buffer != null)
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(IqBlock),
                    "The sample buffer has been returned to the pool. Reading it now would return " +
                    "another consumer's live data (REQ-DAT-001a).");
            }
        }
    }
}
