namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>Classification of a decoded envelope frame event.</summary>
    public enum EnvelopeEventKind : byte
    {
        /// <summary>
        /// Sentinel for <c>default(EnvelopeEventKind)</c>. Never produced by
        /// <see cref="EnvelopeReader.Decode"/>; not a valid event
        /// classification.
        /// </summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a valid classification. Use the members defined by the protocol."
        )]
        None = 0,

        /// <summary>A known message type; see <see cref="EnvelopeEvent.Message"/>.</summary>
        Message = 1,

        /// <summary>An unrecognized (forward-compatible) <c>type</c> discriminator.</summary>
        UnknownMessage = 2,

        /// <summary>The frame is malformed or violates a decode bound.</summary>
        DecodeFailed = 3,
    }
}
