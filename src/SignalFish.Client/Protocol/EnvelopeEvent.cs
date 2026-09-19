using System;

namespace SignalFish.Client.Protocol
{
    /// <summary>
    /// The outcome of decoding one envelope frame. A readonly struct carrying
    /// zero-copy slices of the input frame — the decode hot path allocates
    /// nothing (except the rare <see cref="TypeText"/> of an
    /// <see cref="EnvelopeEventKind.UnknownMessage"/>). Note:
    /// <c>default(EnvelopeEvent)</c> is a degenerate value (<see cref="Kind"/>
    /// = <see cref="EnvelopeEventKind.Message"/> with
    /// <see cref="Message"/> = <see cref="MessageKind.None"/>);
    /// <see cref="EnvelopeReader.Decode"/> never returns it.
    /// </summary>
    public readonly struct EnvelopeEvent
    {
        private readonly ReadOnlyMemory<byte> _raw;
        private readonly ReadOnlyMemory<byte> _data;
        private readonly string? _typeText;

        internal EnvelopeEvent(
            EnvelopeEventKind kind,
            MessageKind message,
            ReadOnlyMemory<byte> raw,
            ReadOnlyMemory<byte> data,
            string? typeText,
            DecodeError error,
            int errorOffset)
        {
            Kind = kind;
            Message = message;
            _raw = raw;
            _data = data;
            _typeText = typeText;
            Error = error;
            ErrorOffset = errorOffset;
        }

        /// <summary>What kind of event this is.</summary>
        public EnvelopeEventKind Kind { get; }

        /// <summary>
        /// The routed known message kind. Valid when
        /// <see cref="Kind"/> is <see cref="EnvelopeEventKind.Message"/>;
        /// <see cref="MessageKind.None"/> otherwise.
        /// </summary>
        public MessageKind Message { get; }

        /// <summary>The complete raw envelope frame, as received.</summary>
        public ReadOnlyMemory<byte> Raw => _raw;

        /// <summary>
        /// The raw bytes of the <c>data</c> object (including braces), sliced
        /// from <see cref="Raw"/>. Empty when the frame carries no payload
        /// object (omitted or JSON <c>null</c>).
        /// </summary>
        public ReadOnlyMemory<byte> Data => _data;

        /// <summary>
        /// The unrecognized <c>type</c> string when <see cref="Kind"/> is
        /// <see cref="EnvelopeEventKind.UnknownMessage"/>;
        /// <see langword="null"/> otherwise. (Rare path; allocates once.)
        /// </summary>
        public string? TypeText => _typeText;

        /// <summary>
        /// The bounded failure reason when <see cref="Kind"/> is
        /// <see cref="EnvelopeEventKind.DecodeFailed"/>;
        /// <see cref="DecodeError.None"/> otherwise.
        /// </summary>
        public DecodeError Error { get; }

        /// <summary>
        /// Byte offset into <see cref="Raw"/> where a decode failure was
        /// detected; 0 when there was no failure.
        /// </summary>
        public int ErrorOffset { get; }
    }
}
