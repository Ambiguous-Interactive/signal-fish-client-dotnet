namespace SignalFish.Client.Protocol
{
    using System;
    using System.Text;

    /// <summary>
    /// Decodes protocol envelope frames: <c>{ "type": PascalCase, "data": {
    /// snake_case } }</c> JSON over UTF-8 bytes. Two passes per frame —
    /// (1) strict structural validation that records the <c>type</c> and
    /// <c>data</c> spans, (2) routing/extract. The decoder is total: any
    /// input yields an <see cref="EnvelopeEvent"/>; malformed or
    /// bound-violating frames produce a bounded
    /// <see cref="EnvelopeEventKind.DecodeFailed"/> event, never an
    /// exception. Unknown <c>type</c> values and unknown fields are
    /// forward-compatible events, not errors. A leading UTF-8 BOM is
    /// rejected (<see cref="DecodeError.NotAnObject"/>) — wire frames carry
    /// no BOM. Routing is byte-exact: an unescaped <c>type</c> value routes
    /// to <see cref="MessageKind"/>; a <c>type</c> value written with JSON
    /// escapes (legal but never emitted by the server) degrades to
    /// <see cref="EnvelopeEventKind.UnknownMessage"/> with its decoded
    /// <see cref="EnvelopeEvent.TypeText"/>.
    /// </summary>
    public static class EnvelopeReader
    {
        /// <summary>
        /// Maximum JSON nesting depth (objects and arrays) accepted inside a
        /// frame. Deeper frames fail with <see cref="DecodeError.DepthExceeded"/>
        /// instead of risking unbounded recursion.
        /// </summary>
        public const int MaxDepth = 64;

        /// <summary>Nesting depth of root-level member values (the root object is level 1).</summary>
        private const int RootMemberDepth = 2;

        private static readonly byte[] TypeKeyBytes = { (byte)'t', (byte)'y', (byte)'p', (byte)'e' };
        private static readonly byte[] DataKeyBytes = { (byte)'d', (byte)'a', (byte)'t', (byte)'a' };

        /// <summary>
        /// Decodes one envelope frame. Never throws and never allocates on
        /// the known-message path (the only allocation is the
        /// <see cref="EnvelopeEvent.TypeText"/> string of an unknown message).
        /// </summary>
        /// <param name="frame">The raw UTF-8 JSON frame bytes.</param>
        /// <returns>The decoded envelope event; slices of <paramref name="frame"/>.</returns>
        public static EnvelopeEvent Decode(ReadOnlyMemory<byte> frame)
        {
            ReadOnlySpan<byte> span = frame.Span;
            JsonScanner scanner = new JsonScanner(span);

            // ---- Pass 1: structural validation + span recording ------------
            scanner.SkipWhitespace();
            if (scanner.IsEof)
            {
                return Fail(frame, DecodeError.Truncated, scanner.Position);
            }

            if (scanner.Peek != (byte)'{')
            {
                return Fail(frame, DecodeError.NotAnObject, scanner.Position);
            }

            Range typeRaw = default(Range);
            Range dataRaw = default(Range);
            bool typeHasEscapes = false;
            bool typeSeen = false;
            bool dataSeen = false;
            bool dataIsNull = false;

            DecodeError err = scanner.Expect((byte)'{');
            bool firstMember = true;
            bool rootClosed = false;
            while (err == DecodeError.None && !rootClosed)
            {
                scanner.SkipWhitespace();
                if (firstMember && scanner.Peek == (byte)'}')
                {
                    err = scanner.Expect((byte)'}');
                    rootClosed = true;
                    break;
                }

                err = scanner.ScanStringRaw(out Range keyRaw, out bool keyHasEscapes);
                if (err != DecodeError.None)
                {
                    break;
                }

                scanner.SkipWhitespace();
                err = scanner.Expect((byte)':');
                if (err != DecodeError.None)
                {
                    break;
                }

                scanner.SkipWhitespace();
                ReadOnlySpan<byte> keyInner = KeyInner(span, keyRaw);
                bool isType = !typeSeen && KeyEquals(keyInner, keyHasEscapes, TypeKeyBytes);
                bool isData = !isType && !dataSeen && KeyEquals(keyInner, keyHasEscapes, DataKeyBytes);

                if (isType)
                {
                    if (scanner.IsEof)
                    {
                        err = DecodeError.Truncated;
                        break;
                    }

                    if (scanner.Peek != (byte)'"')
                    {
                        err = DecodeError.TypeNotString;
                        break;
                    }

                    err = scanner.ScanStringRaw(out typeRaw, out typeHasEscapes);
                    typeSeen = err == DecodeError.None;
                    if (err != DecodeError.None)
                    {
                        break;
                    }
                }
                else if (isData)
                {
                    if (scanner.IsEof)
                    {
                        err = DecodeError.Truncated;
                        break;
                    }

                    if (scanner.Peek == (byte)'{')
                    {
                        err = scanner.ScanObject(RootMemberDepth, MaxDepth, out dataRaw);
                        dataSeen = err == DecodeError.None;
                        if (err != DecodeError.None)
                        {
                            break;
                        }
                    }
                    else if (scanner.Peek == (byte)'n')
                    {
                        // JSON null payload: tolerated as absent.
                        err = scanner.ScanLiteral("null");
                        dataSeen = err == DecodeError.None;
                        dataIsNull = err == DecodeError.None;
                        if (err != DecodeError.None)
                        {
                            break;
                        }
                    }
                    else
                    {
                        err = DecodeError.DataNotObject;
                        break;
                    }
                }
                else
                {
                    // Unknown field: validate and skip (forward compatibility).
                    err = scanner.ScanValue(RootMemberDepth, MaxDepth);
                    if (err != DecodeError.None)
                    {
                        break;
                    }
                }

                scanner.SkipWhitespace();
                switch (scanner.Peek)
                {
                    case (byte)',':
                        err = scanner.Expect((byte)',');
                        firstMember = false;
                        break;
                    case (byte)'}':
                        err = scanner.Expect((byte)'}');
                        rootClosed = true;
                        break;
                    default:
                        err = scanner.IsEof ? DecodeError.Truncated : DecodeError.InvalidToken;
                        break;
                }
            }

            if (err != DecodeError.None)
            {
                return Fail(frame, err, scanner.Position);
            }

            scanner.SkipWhitespace();
            if (!scanner.IsEof)
            {
                return Fail(frame, DecodeError.TrailingContent, scanner.Position);
            }

            if (!typeSeen)
            {
                return Fail(frame, DecodeError.MissingType, scanner.Position);
            }

            // ---- Pass 2: routing + extraction --------------------------------
            ReadOnlySpan<byte> typeInner = KeyInner(span, typeRaw);
            if (typeInner.Length == 0)
            {
                return Fail(frame, DecodeError.EmptyType, typeRaw.GetOffsetAndLength(span.Length).Offset);
            }

            ReadOnlyMemory<byte> data = dataSeen && !dataIsNull ? Slice(frame, dataRaw) : default;

            if (!typeHasEscapes && MessageKindNames.TryRoute(typeInner, out MessageKind kind))
            {
                return new EnvelopeEvent(
                    EnvelopeEventKind.Message, kind, frame, data, typeText: null,
                    DecodeError.None, errorOffset: 0);
            }

            // Unknown type: forward-compatible event carrying the raw frame.
            return new EnvelopeEvent(
                EnvelopeEventKind.UnknownMessage, MessageKind.None, frame, data,
                typeText: DecodeTypeText(typeInner, typeHasEscapes),
                DecodeError.None, errorOffset: 0);
        }

        private static EnvelopeEvent Fail(ReadOnlyMemory<byte> frame, DecodeError error, int offset) =>
            new EnvelopeEvent(
                EnvelopeEventKind.DecodeFailed, MessageKind.None, frame, data: default,
                typeText: null, error, offset);

        /// <summary>Slices a raw range (netstandard2.1 has no Range-based Slice).</summary>
        private static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> frame, Range range)
        {
            (int Offset, int Length) s = range.GetOffsetAndLength(frame.Length);
            return frame.Slice(s.Offset, s.Length);
        }

        private static ReadOnlySpan<byte> KeyInner(ReadOnlySpan<byte> span, Range keyRaw)
        {
            (int Offset, int Length) s = keyRaw.GetOffsetAndLength(span.Length);
            return span.Slice(s.Offset, s.Length).Slice(1, s.Length - 2);
        }

        /// <summary>
        /// Compares a scanned key (quotes stripped) against an ASCII wire
        /// name. Known names are plain ASCII, so only escapes that resolve to
        /// single ASCII bytes can match.
        /// </summary>
        private static bool KeyEquals(ReadOnlySpan<byte> keyInner, bool hasEscapes, ReadOnlySpan<byte> asciiName)
        {
            if (!hasEscapes)
            {
                return keyInner.SequenceEqual(asciiName);
            }

            if (keyInner.Length > 64)
            {
                return false;
            }

            Span<byte> decoded = stackalloc byte[64];
            int written = 0;
            int i = 0;
            while (i < keyInner.Length)
            {
                if (written >= asciiName.Length)
                {
                    return false;
                }

                if (keyInner[i] != (byte)'\\')
                {
                    decoded[written++] = keyInner[i];
                    i++;
                    continue;
                }

                int ch = DecodeSimpleEscape(keyInner, ref i);
                if (ch < 0)
                {
                    return false;
                }

                decoded[written++] = (byte)ch;
            }

            return decoded.Slice(0, written).SequenceEqual(asciiName);
        }

        /// <summary>
        /// Decodes the escape sequence at <paramref name="index"/> (pointing
        /// at the backslash) to a scalar value, or -1 when it cannot be a
        /// single ASCII byte (non-simple escape, non-ASCII BMP char, or a
        /// surrogate half).
        /// </summary>
        private static int DecodeSimpleEscape(ReadOnlySpan<byte> keyInner, ref int index)
        {
            if (index + 1 >= keyInner.Length)
            {
                return -1;
            }

            byte e = keyInner[index + 1];
            index += 2;
            switch (e)
            {
                case (byte)'"': return (byte)'"';
                case (byte)'\\': return (byte)'\\';
                case (byte)'/': return (byte)'/';
                case (byte)'b': return 0x08;
                case (byte)'f': return 0x0C;
                case (byte)'n': return 0x0A;
                case (byte)'r': return 0x0D;
                case (byte)'t': return 0x09;
                case (byte)'u':
                    if (index + 4 > keyInner.Length)
                    {
                        return -1;
                    }

                    int cp = ParseHex4(keyInner.Slice(index, 4));
                    index += 4;
                    return cp is >= 0x20 and < 0x7F ? cp : -1;
                default:
                    return -1;
            }
        }

        private static int ParseHex4(ReadOnlySpan<byte> hex)
        {
            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                byte b = hex[i];
                int digit = b switch
                {
                    >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                    >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                    >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                    _ => -1,
                };
                if (digit < 0)
                {
                    return -1;
                }

                value = (value << 4) | digit;
            }

            return value;
        }

        /// <summary>
        /// Materializes an unrecognized <c>type</c> string (rare path; this
        /// is the only decode allocation).
        /// </summary>
        private static string DecodeTypeText(ReadOnlySpan<byte> typeInner, bool hasEscapes)
        {
            if (!hasEscapes)
            {
                return Encoding.UTF8.GetString(typeInner);
            }

            // UTF-16 chars never exceed raw byte count (4-byte UTF-8 → 2 chars).
            Span<char> chars = typeInner.Length <= 512 ? stackalloc char[512] : new char[typeInner.Length];
            int written = 0;
            int segmentStart = 0;
            int i = 0;
            while (i < typeInner.Length)
            {
                if (typeInner[i] != (byte)'\\')
                {
                    i++;
                    continue;
                }

                written += AppendUtf8Segment(typeInner.Slice(segmentStart, i - segmentStart), chars.Slice(written));
                int scalar = DecodeEscapeScalar(typeInner, ref i);
                chars[written++] = scalar >= 0 ? (char)scalar : '?';
                segmentStart = i;
            }

            written += AppendUtf8Segment(typeInner.Slice(segmentStart), chars.Slice(written));
            return chars.Slice(0, written).ToString();
        }

        /// <summary>UTF-8 decodes a validated raw segment into <paramref name="dest"/>.</summary>
        private static int AppendUtf8Segment(ReadOnlySpan<byte> segment, Span<char> dest)
        {
            if (segment.IsEmpty)
            {
                return 0;
            }

            return System.Text.Encoding.UTF8.GetChars(segment, dest);
        }

        /// <summary>
        /// Decodes the escape at <paramref name="index"/> (pointing at the
        /// backslash) to a UTF-16 code unit; advances past it. Surrogate
        /// halves pass through so pairs stay pairable.
        /// </summary>
        private static int DecodeEscapeScalar(ReadOnlySpan<byte> inner, ref int index)
        {
            if (index + 1 >= inner.Length)
            {
                return -1;
            }

            byte e = inner[index + 1];
            index += 2;
            switch (e)
            {
                case (byte)'"': return '"';
                case (byte)'\\': return '\\';
                case (byte)'/': return '/';
                case (byte)'b': return '\b';
                case (byte)'f': return '\f';
                case (byte)'n': return '\n';
                case (byte)'r': return '\r';
                case (byte)'t': return '\t';
                case (byte)'u':
                    if (index + 4 > inner.Length)
                    {
                        return -1;
                    }

                    int cp = ParseHex4(inner.Slice(index, 4));
                    index += 4;
                    return cp;
                default:
                    return -1;
            }
        }
    }
}
