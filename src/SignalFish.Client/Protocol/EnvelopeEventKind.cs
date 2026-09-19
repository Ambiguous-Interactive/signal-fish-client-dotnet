namespace SignalFish.Client.Protocol
{
    /// <summary>Classification of a decoded envelope frame event.</summary>
    public enum EnvelopeEventKind : byte
    {
        /// <summary>A known message type; see <see cref="EnvelopeEvent.Message"/>.</summary>
        Message = 0,

        /// <summary>An unrecognized (forward-compatible) <c>type</c> discriminator.</summary>
        UnknownMessage = 1,

        /// <summary>The frame is malformed or violates a decode bound.</summary>
        DecodeFailed = 2,
    }
}
