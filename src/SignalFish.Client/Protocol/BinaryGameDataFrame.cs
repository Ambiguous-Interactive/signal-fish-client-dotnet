namespace SignalFish.Client.Protocol
{
    using System;
    using System.Buffers.Binary;
    using System.Text;

    /// <summary>
    /// The game-data encoding tokens the binary envelope can name. The
    /// payload itself is never inspected — the token only rides the
    /// representation check against the negotiated format.
    /// </summary>
    internal enum GameDataFormatToken : byte
    {
        /// <summary>JSON text frames (the negotiation default).</summary>
        Json = 1,

        /// <summary>MessagePack payloads.</summary>
        MessagePack = 2,

        /// <summary>Opaque rkyv payloads (server opt-in).</summary>
        Rkyv = 3,

        /// <summary>Opaque protobuf payloads (server opt-in).</summary>
        Protobuf = 4,
    }

    /// <summary>
    /// The server's v3 binary game-data relay frame: a raw WebSocket
    /// binary message — never wrapped in the JSON envelope — carrying a
    /// strict MessagePack map (<c>from_player</c> as the 16 RFC-4122 UUID
    /// bytes, <c>encoding</c>, opaque <c>payload</c>, and on v3 the
    /// server-stamped paired <c>seq</c>/<c>epoch</c>). Binary game data
    /// carries no class metadata: it is always reliable and shares the
    /// sender's per-room seq stream with their JSON frames.
    /// </summary>
    internal readonly struct BinaryGameDataFrame
    {
        private static class FieldNames
        {
            internal static readonly byte[] FromPlayer = Encoding.ASCII.GetBytes("from_player");
            internal static readonly byte[] EncodingToken = Encoding.ASCII.GetBytes("encoding");
            internal static readonly byte[] Payload = Encoding.ASCII.GetBytes("payload");
            internal static readonly byte[] Seq = Encoding.ASCII.GetBytes("seq");
            internal static readonly byte[] Epoch = Encoding.ASCII.GetBytes("epoch");
            internal static readonly byte[] Json = Encoding.ASCII.GetBytes("json");
            internal static readonly byte[] MessagePack = Encoding.ASCII.GetBytes("message_pack");
            internal static readonly byte[] Rkyv = Encoding.ASCII.GetBytes("rkyv");
            internal static readonly byte[] Protobuf = Encoding.ASCII.GetBytes("protobuf");
        }

        /// <summary>
        /// The strict MessagePack cursor: only the map/string/bin/uint
        /// shapes the server's strict encoder emits, at any integer
        /// width. Every read advances or fails.
        /// </summary>
        private ref struct Scanner
        {
            internal int Offset => _offset;

            private readonly ReadOnlySpan<byte> _bytes;
            private int _offset;

            internal Scanner(ReadOnlySpan<byte> bytes)
            {
                _bytes = bytes;
                _offset = 0;
            }

            internal bool ReadMapHeader(out int count, out DecodeError error, out int errorOffset)
            {
                count = 0;
                if (!ReadMarker(out byte marker, out error, out errorOffset))
                {
                    return false;
                }

                if (marker >= 0x80 && marker <= 0x8f)
                {
                    count = marker & 0x0f;
                    return true;
                }

                switch (marker)
                {
                    case 0xde:
                        return ReadLengthBody(out count, 2, out error, out errorOffset);
                    case 0xdf:
                        return ReadLengthBody(out count, 4, out error, out errorOffset);
                    default:
                        return Fail(DecodeError.NotAMap, _offset, out error, out errorOffset);
                }
            }

            internal bool ReadStringSpan(
                out ReadOnlySpan<byte> value,
                out DecodeError error,
                out int errorOffset
            )
            {
                value = default;
                if (!ReadMarker(out byte marker, out error, out errorOffset))
                {
                    return false;
                }

                int length;
                if (marker >= 0xa0 && marker <= 0xbf)
                {
                    length = marker & 0x1f;
                }
                else if (marker == 0xd9 || marker == 0xda || marker == 0xdb)
                {
                    if (!ReadLengthBody(out length, WidthOf(marker), out error, out errorOffset))
                    {
                        return false;
                    }
                }
                else
                {
                    return Fail(DecodeError.InvalidFieldValue, _offset, out error, out errorOffset);
                }

                if (_offset + length > _bytes.Length)
                {
                    return Fail(DecodeError.Truncated, _offset, out error, out errorOffset);
                }

                value = _bytes.Slice(_offset, length);
                _offset += length;
                return true;
            }

            internal bool ReadBin(
                int exactBytes,
                out (int Offset, int Length) value,
                out DecodeError error,
                out int errorOffset
            )
            {
                value = default;
                int start = _offset;
                if (!ReadMarker(out byte marker, out error, out errorOffset))
                {
                    return false;
                }

                int length;
                if (marker == 0xc4 || marker == 0xc5 || marker == 0xc6)
                {
                    if (!ReadLengthBody(out length, WidthOf(marker), out error, out errorOffset))
                    {
                        return false;
                    }
                }
                else
                {
                    return Fail(DecodeError.InvalidFieldValue, _offset, out error, out errorOffset);
                }

                if (exactBytes != 0 && length != exactBytes)
                {
                    _offset = start;
                    return Fail(DecodeError.InvalidFieldValue, _offset, out error, out errorOffset);
                }

                value = (_offset, length);
                _offset += length;
                return true;
            }

            internal bool ReadNonZeroUInt64(
                out ulong value,
                out DecodeError error,
                out int errorOffset
            )
            {
                value = 0;
                if (!ReadMarker(out byte marker, out error, out errorOffset))
                {
                    return false;
                }

                if (marker <= 0x7f)
                {
                    value = marker;
                    return RequireNonZero(value, out error, out errorOffset);
                }

                if (marker == 0xcc || marker == 0xcd || marker == 0xce || marker == 0xcf)
                {
                    int width = WidthOf(marker);
                    if (_offset + width > _bytes.Length)
                    {
                        return Fail(DecodeError.Truncated, _offset, out error, out errorOffset);
                    }

                    ReadOnlySpan<byte> body = _bytes.Slice(_offset, width);
                    value = width switch
                    {
                        1 => body[0],
                        2 => BinaryPrimitives.ReadUInt16BigEndian(body),
                        4 => BinaryPrimitives.ReadUInt32BigEndian(body),
                        _ => BinaryPrimitives.ReadUInt64BigEndian(body),
                    };
                    _offset += width;
                    return RequireNonZero(value, out error, out errorOffset);
                }

                return Fail(DecodeError.InvalidFieldValue, _offset, out error, out errorOffset);
            }

            internal bool ReadNonZeroUInt32(
                out uint value,
                out DecodeError error,
                out int errorOffset
            )
            {
                value = 0;
                if (!ReadNonZeroUInt64(out ulong wide, out error, out errorOffset))
                {
                    return false;
                }

                if (wide > uint.MaxValue)
                {
                    return Fail(DecodeError.InvalidFieldValue, _offset, out error, out errorOffset);
                }

                value = (uint)wide;
                return true;
            }

            internal bool RequireNonZero(ulong value, out DecodeError error, out int errorOffset)
            {
                if (value != 0)
                {
                    error = default;
                    errorOffset = 0;
                    return true;
                }

                return Fail(DecodeError.InvalidFieldValue, _offset, out error, out errorOffset);
            }

            internal bool ReadMarker(out byte marker, out DecodeError error, out int errorOffset)
            {
                marker = 0;
                error = default;
                errorOffset = _offset;
                if (_offset >= _bytes.Length)
                {
                    marker = 0;
                    return Fail(DecodeError.Truncated, _offset, out error, out errorOffset);
                }

                marker = _bytes[_offset++];
                error = default;
                return true;
            }

            internal bool ReadLengthBody(
                out int length,
                int width,
                out DecodeError error,
                out int errorOffset
            )
            {
                error = default;
                errorOffset = _offset;
                if (_offset + width > _bytes.Length)
                {
                    length = 0;
                    return Fail(DecodeError.Truncated, _offset, out error, out errorOffset);
                }

                ReadOnlySpan<byte> body = _bytes.Slice(_offset, width);
                length = width switch
                {
                    1 => body[0],
                    2 => BinaryPrimitives.ReadUInt16BigEndian(body),
                    _ => unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(body)),
                };
                _offset += width;
                /*
                    Wide arithmetic: the length is wire-controlled, so the
                    bounded-int sum can wrap negative and pass the check —
                    a declared 0x7FFFFFFF then walked the cursor out of the
                    buffer and faulted the loop. Compare against the
                    remaining bytes instead.
                */
                long remaining = _bytes.Length - _offset;
                if (length < 0 || length > remaining)
                {
                    return Fail(DecodeError.Truncated, _offset, out error, out errorOffset);
                }

                return true;
            }

            internal static int WidthOf(byte marker)
            {
                return marker switch
                {
                    0xc4 or 0xcc or 0xd9 => 1,
                    0xc5 or 0xcd or 0xda => 2,
                    0xc6 or 0xce or 0xdb => 4,
                    _ => 8,
                };
            }

            internal static bool Fail(
                DecodeError reason,
                int offset,
                out DecodeError error,
                out int errorOffset
            )
            {
                error = reason;
                errorOffset = offset;
                return false;
            }
        }

        /// <summary>Gets the player that sent the payload.</summary>
        internal Guid FromPlayer { get; }

        /// <summary>Gets the envelope's <c>encoding</c> token.</summary>
        internal GameDataFormatToken Format { get; }

        /// <summary>Gets the opaque payload bytes (relay format per the token).</summary>
        internal ReadOnlyMemory<byte> Payload => _payload;

        /// <summary>Gets the server-stamped relay sequence (v3).</summary>
        internal ulong Seq { get; }

        /// <summary>Gets the server-stamped incarnation epoch (v3).</summary>
        internal uint Epoch { get; }

        private readonly ReadOnlyMemory<byte> _payload;

        private BinaryGameDataFrame(
            Guid fromPlayer,
            GameDataFormatToken format,
            ReadOnlyMemory<byte> payload,
            ulong seq,
            uint epoch
        )
        {
            FromPlayer = fromPlayer;
            Format = format;
            _payload = payload;
            Seq = seq;
            Epoch = epoch;
        }

        /// <summary>Maps the token onto its canonical wire spelling.</summary>
        internal static string WireToken(GameDataFormatToken format)
        {
            /*
                The token set is closed; `default` (0) is a sentinel that
                never reaches the wire, and it maps to the last arm only
                to keep the switch total.
            */
            return format switch
            {
                GameDataFormatToken.Json => "json",
                GameDataFormatToken.MessagePack => "message_pack",
                GameDataFormatToken.Rkyv => "rkyv",
                GameDataFormatToken.Protobuf => "protobuf",
                _ => "protobuf",
            };
        }

        /// <summary>
        /// Maps an <c>encoding</c> token onto the format; the server's
        /// four tokens all decode here (the representation check, not the
        /// decode, filters against the negotiation).
        /// </summary>
        internal static bool TryReadToken(ReadOnlySpan<byte> token, out GameDataFormatToken format)
        {
            if (token.SequenceEqual(FieldNames.Json))
            {
                format = GameDataFormatToken.Json;
                return true;
            }

            if (token.SequenceEqual(FieldNames.MessagePack))
            {
                format = GameDataFormatToken.MessagePack;
                return true;
            }

            if (token.SequenceEqual(FieldNames.Rkyv))
            {
                format = GameDataFormatToken.Rkyv;
                return true;
            }

            if (token.SequenceEqual(FieldNames.Protobuf))
            {
                format = GameDataFormatToken.Protobuf;
                return true;
            }

            format = default;
            return false;
        }

        /// <summary>
        /// Strictly decodes one binary game-data frame. The map must carry
        /// exactly the expected key set — no duplicates, no unknown keys,
        /// no trailing bytes; <c>from_player</c> and <c>payload</c> must
        /// be MessagePack <c>bin</c> (the sender id exactly 16 bytes) and
        /// the stamps, mandatory on v3, must be non-zero integers of any
        /// width. Failure reports the structured reason and the byte
        /// offset where it was detected.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> frame,
            bool protocolV3,
            out BinaryGameDataFrame decoded,
            out DecodeError error,
            out int errorOffset
        )
        {
            decoded = default;
            error = default;
            errorOffset = 0;

            ReadOnlySpan<byte> bytes = frame.Span;
            Scanner scanner = new Scanner(bytes);

            if (!scanner.ReadMapHeader(out int count, out error, out errorOffset))
            {
                return false;
            }

            int expectedCount = protocolV3 ? 5 : 3;
            if (count != expectedCount)
            {
                return Fail(DecodeError.UnknownField, scanner.Offset, out error, out errorOffset);
            }

            Guid fromPlayer = default;
            GameDataFormatToken format = default;
            (int Offset, int Length) sender = default;
            (int Offset, int Length) payload = default;
            ulong seq = 0;
            uint epoch = 0;
            bool senderSeen = false;
            bool formatSeen = false;
            bool payloadSeen = false;
            bool seqSeen = false;
            bool epochSeen = false;

            for (int member = 0; member < count; member++)
            {
                if (!scanner.ReadStringSpan(out ReadOnlySpan<byte> key, out error, out errorOffset))
                {
                    return false;
                }

                if (key.SequenceEqual(FieldNames.FromPlayer))
                {
                    if (senderSeen)
                    {
                        return Fail(
                            DecodeError.DuplicateField,
                            scanner.Offset,
                            out error,
                            out errorOffset
                        );
                    }

                    /*
                        The sender id rides its own bin; the map's key order
                        is free, so it must never alias the payload slice.
                    */
                    if (!scanner.ReadBin(16, out sender, out error, out errorOffset))
                    {
                        return false;
                    }

                    fromPlayer = FromNetworkUuid(bytes.Slice(sender.Offset, sender.Length));
                    senderSeen = true;
                }
                else if (key.SequenceEqual(FieldNames.EncodingToken))
                {
                    if (formatSeen)
                    {
                        return Fail(
                            DecodeError.DuplicateField,
                            scanner.Offset,
                            out error,
                            out errorOffset
                        );
                    }

                    if (
                        !scanner.ReadStringSpan(
                            out ReadOnlySpan<byte> token,
                            out error,
                            out errorOffset
                        ) || !TryReadToken(token, out format)
                    )
                    {
                        error = DecodeError.InvalidFieldValue;
                        errorOffset = scanner.Offset;
                        return false;
                    }

                    formatSeen = true;
                }
                else if (key.SequenceEqual(FieldNames.Payload))
                {
                    if (payloadSeen)
                    {
                        return Fail(
                            DecodeError.DuplicateField,
                            scanner.Offset,
                            out error,
                            out errorOffset
                        );
                    }

                    if (!scanner.ReadBin(0, out payload, out error, out errorOffset))
                    {
                        return false;
                    }

                    payloadSeen = true;
                }
                else if (protocolV3 && key.SequenceEqual(FieldNames.Seq))
                {
                    if (seqSeen)
                    {
                        return Fail(
                            DecodeError.DuplicateField,
                            scanner.Offset,
                            out error,
                            out errorOffset
                        );
                    }

                    if (!scanner.ReadNonZeroUInt64(out seq, out error, out errorOffset))
                    {
                        return false;
                    }

                    seqSeen = true;
                }
                else if (protocolV3 && key.SequenceEqual(FieldNames.Epoch))
                {
                    if (epochSeen)
                    {
                        return Fail(
                            DecodeError.DuplicateField,
                            scanner.Offset,
                            out error,
                            out errorOffset
                        );
                    }

                    if (!scanner.ReadNonZeroUInt32(out epoch, out error, out errorOffset))
                    {
                        return false;
                    }

                    epochSeen = true;
                }
                else
                {
                    // Unknown key, or a v3-only key on the v2 shape.
                    return Fail(
                        DecodeError.UnknownField,
                        scanner.Offset,
                        out error,
                        out errorOffset
                    );
                }
            }

            if (scanner.Offset != bytes.Length)
            {
                return Fail(
                    DecodeError.TrailingContent,
                    scanner.Offset,
                    out error,
                    out errorOffset
                );
            }

            if (
                !senderSeen
                || !payloadSeen
                || !formatSeen
                || (protocolV3 && (!seqSeen || !epochSeen))
            )
            {
                /*
                    Defense in depth: the fixed member count plus the
                    duplicate/unknown refusals make this unreachable today;
                    it keeps a corrupted seen-set from constructing a frame.
                */
                return Fail(DecodeError.MissingField, scanner.Offset, out error, out errorOffset);
            }

            decoded = new BinaryGameDataFrame(
                fromPlayer,
                format,
                frame.Slice(payload.Offset, payload.Length),
                seq,
                epoch
            );
            return true;
        }

        internal static bool Fail(
            DecodeError reason,
            int offset,
            out DecodeError error,
            out int errorOffset
        )
        {
            error = reason;
            errorOffset = offset;
            return false;
        }

        /// <summary>
        /// Builds the sender id from the 16 RFC-4122 (network byte order)
        /// UUID bytes, so the value equals the hyphenated spelling the
        /// JSON roster carries.
        /// </summary>
        private static Guid FromNetworkUuid(ReadOnlySpan<byte> bytes)
        {
            return new Guid(
                BinaryPrimitives.ReadInt32BigEndian(bytes),
                BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(4)),
                BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(6)),
                bytes[8],
                bytes[9],
                bytes[10],
                bytes[11],
                bytes[12],
                bytes[13],
                bytes[14],
                bytes[15]
            );
        }
    }
}
