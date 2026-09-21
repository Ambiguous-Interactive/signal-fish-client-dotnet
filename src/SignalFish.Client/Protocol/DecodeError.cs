namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// Bounded reason codes for a <see cref="EnvelopeEventKind.DecodeFailed"/>
    /// event. The decoder is total: malformed input produces one of these
    /// values plus a byte offset — never an exception.
    /// </summary>
    public enum DecodeError : byte
    {
        /// <summary>Sentinel for <c>default(DecodeError)</c>; no failure.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not an error. Compare against default(DecodeError) instead."
        )]
        None = 0,

        /// <summary>The input ended before a complete frame was read.</summary>
        Truncated = 1,

        /// <summary>The frame root is not a JSON object (the envelope contract).</summary>
        NotAnObject = 2,

        /// <summary>The envelope carries no <c>type</c> member.</summary>
        MissingType = 3,

        /// <summary>The <c>type</c> member is not a JSON string.</summary>
        TypeNotString = 4,

        /// <summary>The <c>type</c> member is an empty string.</summary>
        EmptyType = 5,

        /// <summary>The <c>data</c> member is present but is not a JSON object (JSON <c>null</c> is tolerated as absent).</summary>
        DataNotObject = 6,

        /// <summary>A malformed JSON token (bad escape, bad number, invalid UTF-8, wrong delimiter, ...).</summary>
        InvalidToken = 7,

        /// <summary>Nesting exceeded <see cref="EnvelopeReader.MaxDepth"/>.</summary>
        DepthExceeded = 8,

        /// <summary>Non-whitespace content follows the envelope object.</summary>
        TrailingContent = 9,
    }
}
