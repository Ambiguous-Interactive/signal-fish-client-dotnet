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

        private static readonly byte[] TypeKeyBytes =
        {
            (byte)'t',
            (byte)'y',
            (byte)'p',
            (byte)'e',
        };
        private static readonly byte[] DataKeyBytes =
        {
            (byte)'d',
            (byte)'a',
            (byte)'t',
            (byte)'a',
        };

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
            while (err == default(DecodeError) && !rootClosed)
            {
                scanner.SkipWhitespace();
                if (firstMember && scanner.Peek == (byte)'}')
                {
                    err = scanner.Expect((byte)'}');
                    rootClosed = true;
                    break;
                }

                err = scanner.ScanStringRaw(out Range keyRaw, out bool keyHasEscapes);
                if (err != default(DecodeError))
                {
                    break;
                }

                scanner.SkipWhitespace();
                err = scanner.Expect((byte)':');
                if (err != default(DecodeError))
                {
                    break;
                }

                scanner.SkipWhitespace();
                ReadOnlySpan<byte> keyInner = KeyInner(span, keyRaw);
                bool isType = !typeSeen && KeyEquals(keyInner, keyHasEscapes, TypeKeyBytes);
                bool isData =
                    !isType && !dataSeen && KeyEquals(keyInner, keyHasEscapes, DataKeyBytes);

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
                    typeSeen = err == default(DecodeError);
                    if (err != default(DecodeError))
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
                        dataSeen = err == default(DecodeError);
                        if (err != default(DecodeError))
                        {
                            break;
                        }
                    }
                    else if (scanner.Peek == (byte)'n')
                    {
                        // JSON null payload: tolerated as absent.
                        err = scanner.ScanLiteral("null");
                        dataSeen = err == default(DecodeError);
                        dataIsNull = err == default(DecodeError);
                        if (err != default(DecodeError))
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
                    if (err != default(DecodeError))
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

            if (err != default(DecodeError))
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
                return Fail(
                    frame,
                    DecodeError.EmptyType,
                    typeRaw.GetOffsetAndLength(span.Length).Offset
                );
            }

            ReadOnlyMemory<byte> data = dataSeen && !dataIsNull ? Slice(frame, dataRaw) : default;

            if (!typeHasEscapes && MessageKindNames.TryRoute(typeInner, out MessageKind kind))
            {
                return new EnvelopeEvent(
                    EnvelopeEventKind.Message,
                    kind,
                    frame,
                    data,
                    typeText: null,
                    default(DecodeError),
                    errorOffset: 0
                );
            }

            // Unknown type: forward-compatible event carrying the raw frame.
            return new EnvelopeEvent(
                EnvelopeEventKind.UnknownMessage,
                default(MessageKind),
                frame,
                data,
                typeText: DecodeTypeText(typeInner, typeHasEscapes),
                default(DecodeError),
                errorOffset: 0
            );
        }

        private static EnvelopeEvent Fail(
            ReadOnlyMemory<byte> frame,
            DecodeError error,
            int offset
        ) =>
            new EnvelopeEvent(
                EnvelopeEventKind.DecodeFailed,
                default(MessageKind),
                frame,
                data: default,
                typeText: null,
                error,
                offset
            );

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
        /// name. Shared with payload decode — see
        /// <see cref="JsonScanner.KeyEquals"/>.
        /// </summary>
        private static bool KeyEquals(
            ReadOnlySpan<byte> keyInner,
            bool hasEscapes,
            ReadOnlySpan<byte> asciiName
        ) => JsonScanner.KeyEquals(keyInner, hasEscapes, asciiName);

        /// <summary>
        /// Materializes an unrecognized <c>type</c> string (rare path; this
        /// is the only decode allocation). Shared with payload decode — see
        /// <see cref="JsonScanner.MaterializeString"/>.
        /// </summary>
        private static string DecodeTypeText(ReadOnlySpan<byte> typeInner, bool hasEscapes) =>
            JsonScanner.MaterializeString(typeInner, hasEscapes);
    }
}
