namespace SignalFish.Client.Protocol
{
    using System;
    using System.Buffers;

    /// <summary>
    /// Reusable <see cref="IBufferWriter{Byte}"/> over one growable array:
    /// the send-side scratch for encoding wire frames without a dependency
    /// on <c>ArrayBufferWriter</c> (unavailable on netstandard2.1). Not
    /// thread-safe; call <see cref="Reset"/> before each frame. Senders
    /// that hand the written bytes to an async pipeline must copy first
    /// (see <see cref="WrittenSpan"/>).
    /// </summary>
    internal sealed class FrameBufferWriter : IBufferWriter<byte>
    {
        private const int MinGrowBytes = 256;

        /// <summary>Gets the number of bytes written since the last <see cref="Reset"/>.</summary>
        internal int Length => _length;

        /// <summary>
        /// Gets the written bytes. The memory aliases the internal buffer:
        /// copy (e.g. <c>ToArray</c>) before the next <see cref="Reset"/>
        /// when the bytes outlive this call.
        /// </summary>
        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

        private byte[] _buffer;
        private int _length;

        /// <summary>Initializes the writer; the buffer grows on demand.</summary>
        internal FrameBufferWriter()
        {
            _buffer = new byte[MinGrowBytes];
            _length = 0;
        }

        /// <inheritdoc />
        public void Advance(int count)
        {
            if (count < 0 || _length + count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _length += count;
        }

        /// <inheritdoc />
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_length);
        }

        /// <inheritdoc />
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_length);
        }

        /// <summary>Clears the writer, keeping the allocated capacity.</summary>
        internal void Reset()
        {
            _length = 0;
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            int required = _length + (sizeHint == 0 ? MinGrowBytes : sizeHint);
            if (required <= _buffer.Length)
            {
                return;
            }

            int grown = _buffer.Length * 2;
            while (grown < required)
            {
                grown *= 2;
            }

            Array.Resize(ref _buffer, grown);
        }
    }
}
