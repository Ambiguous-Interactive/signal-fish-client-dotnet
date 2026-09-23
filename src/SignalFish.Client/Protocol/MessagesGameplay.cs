namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// Payload of the outbound <c>AuthorityRequest</c> message (C→S): ask to
    /// become or release room authority. <see cref="BecomeAuthority"/> is
    /// required.
    /// </summary>
    public readonly struct AuthorityRequestMessage : IEquatable<AuthorityRequestMessage>
    {
        /// <summary>Gets a value indicating whether the sender requests authority (<c>false</c> releases it).</summary>
        public bool BecomeAuthority { get; }

        /// <summary>Initializes a new <see cref="AuthorityRequestMessage"/> payload.</summary>
        public AuthorityRequestMessage(bool becomeAuthority)
        {
            BecomeAuthority = becomeAuthority;
        }

        /// <inheritdoc />
        public bool Equals(AuthorityRequestMessage other) =>
            BecomeAuthority == other.BecomeAuthority;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is AuthorityRequestMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => BecomeAuthority.GetHashCode();

        /// <inheritdoc />
        public static bool operator ==(
            AuthorityRequestMessage left,
            AuthorityRequestMessage right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            AuthorityRequestMessage left,
            AuthorityRequestMessage right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of an <c>AuthorityRequest</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped. Returns <see langword="false"/> for malformed
        /// input or a missing <c>become_authority</c> field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out AuthorityRequestMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            bool seen = false;
            bool becomeAuthority = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "become_authority"))
                {
                    if (!scanner.TryReadBoolean(valueRaw, out becomeAuthority))
                    {
                        return false;
                    }

                    seen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !seen)
            {
                return false;
            }

            message = new AuthorityRequestMessage(becomeAuthority);
            return true;
        }
    }

    /// <summary>
    /// Payload of the outbound <c>ProvideConnectionInfo</c> message (C→S):
    /// publish legacy, self-declared peer connection metadata. The
    /// <paramref name="connectionInfo"/> value is a verbatim JSON object
    /// (e.g. <c>{"type": "direct", "host": "...", "port": 7777}</c>) that is
    /// relayed to room members without inspection; it must be valid UTF-8
    /// JSON and start with <c>{</c>.
    /// </summary>
    public readonly struct ProvideConnectionInfoMessage : IEquatable<ProvideConnectionInfoMessage>
    {
        /// <summary>Gets the connection-info JSON object, as UTF-8 bytes.</summary>
        public ReadOnlyMemory<byte> ConnectionInfo => _connectionInfo;

        private readonly ReadOnlyMemory<byte> _connectionInfo;

        /// <summary>Initializes a new <see cref="ProvideConnectionInfoMessage"/> payload.</summary>
        /// <param name="connectionInfo">The connection-info JSON object, as UTF-8 bytes.</param>
        public ProvideConnectionInfoMessage(ReadOnlyMemory<byte> connectionInfo)
        {
            EnvelopeWriter.RequireJsonObject(connectionInfo.Span, "connection_info");
            _connectionInfo = connectionInfo;
        }

        /// <inheritdoc />
        public bool Equals(ProvideConnectionInfoMessage other) =>
            _connectionInfo.Span.SequenceEqual(other._connectionInfo.Span);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is ProvideConnectionInfoMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => ProtocolHash.Of(_connectionInfo.Span);

        /// <inheritdoc />
        public static bool operator ==(
            ProvideConnectionInfoMessage left,
            ProvideConnectionInfoMessage right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            ProvideConnectionInfoMessage left,
            ProvideConnectionInfoMessage right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>ProvideConnectionInfo</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice) into its
        /// verbatim <c>connection_info</c> object. Unknown fields are
        /// skipped. Returns <see langword="false"/> for malformed input or a
        /// missing <c>connection_info</c> object.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out ProvideConnectionInfoMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            ReadOnlyMemory<byte> connectionInfo = default;
            bool seen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "connection_info"))
                {
                    if (!JsonScanner.TryReadObjectSlice(data, valueRaw, out connectionInfo))
                    {
                        return false;
                    }

                    seen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !seen)
            {
                return false;
            }

            message = new ProvideConnectionInfoMessage(connectionInfo);
            return true;
        }
    }
}
