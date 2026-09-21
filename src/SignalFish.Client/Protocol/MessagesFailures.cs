namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// Payload of the inbound failure family (S→C): <c>Error</c>,
    /// <c>RoomJoinFailed</c>, <c>SpectatorJoinFailed</c>,
    /// <c>ReconnectionFailed</c>, and any future <c>*Failed</c> message.
    /// The server names the prose reason field inconsistently across this
    /// family — <c>*Failed</c> uses <c>reason</c>, <c>Error</c> uses
    /// <c>message</c>, and <c>AuthenticationError</c> uses <c>error</c> — so
    /// the decoder accepts all three aliases for <see cref="Reason"/>; the
    /// first alias seen wins and a second alias is a repeated known key
    /// (rejected). <see cref="ErrorCode"/> is always <c>error_code</c> and
    /// routing never keys on the prose text. Both fields are required.
    /// </summary>
    public readonly struct FailureMessage : IEquatable<FailureMessage>
    {
        /// <summary>Gets the human-readable failure reason (required).</summary>
        public string Reason { get; }

        /// <summary>Gets the stable machine-readable error code (required).</summary>
        public string ErrorCode { get; }

        /// <summary>Initializes a new <see cref="FailureMessage"/> payload.</summary>
        public FailureMessage(string reason, string errorCode)
        {
            Reason = reason;
            ErrorCode = errorCode;
        }

        /// <inheritdoc />
        public bool Equals(FailureMessage other) =>
            AuthenticateMessage.NullableStringEquals(Reason, other.Reason)
            && AuthenticateMessage.NullableStringEquals(ErrorCode, other.ErrorCode);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is FailureMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Reason);
            hash.Add(ErrorCode);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(FailureMessage left, FailureMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(FailureMessage left, FailureMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a failure envelope (the
        /// <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key (including a second
        /// reason-alias) or a wrong-typed value is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out FailureMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? reason = null;
            string? errorCode = null;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (
                    scanner.KeyIs(keyRaw, "reason")
                    || scanner.KeyIs(keyRaw, "message")
                    || scanner.KeyIs(keyRaw, "error")
                )
                {
                    if (reason is not null || !scanner.TryReadString(valueRaw, out reason))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "error_code"))
                {
                    if (errorCode is not null || !scanner.TryReadString(valueRaw, out errorCode))
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || reason is null || errorCode is null)
            {
                return false;
            }

            message = new FailureMessage(reason, errorCode);
            return true;
        }
    }
}
