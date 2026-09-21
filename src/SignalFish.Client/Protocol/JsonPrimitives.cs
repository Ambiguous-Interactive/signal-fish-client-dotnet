namespace SignalFish.Client.Protocol
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;

    /// <summary>
    /// Stable byte-sequence hashing for payload structs whose fields carry
    /// verbatim JSON slices (netstandard2.1 has no
    /// <c>HashCode.AddBytes</c>). FNV-1a 64-bit, folded to 32 bits.
    /// </summary>
    internal static class ProtocolHash
    {
        internal static int Of(ReadOnlySpan<byte> data)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (byte b in data)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }

                return (int)(hash ^ (hash >> 32));
            }
        }
    }

    /// <summary>
    /// Outcome of one payload member-walk step (<see
    /// cref="JsonScanner.BeginObject"/>, <see cref="JsonScanner.ScanMember"/>,
    /// <see cref="JsonScanner.EndMember"/>).
    /// </summary>
    internal enum JsonMemberState : byte
    {
        /// <summary>Sentinel for <c>default(JsonMemberState)</c>; not a walk outcome.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a walk outcome. Use the walk outcomes defined below."
        )]
        None = 0,

        /// <summary>A member (or the next member) is available.</summary>
        Member = 1,

        /// <summary>The object closed cleanly.</summary>
        EndObject = 2,

        /// <summary>Malformed input (bounded; the walker must stop).</summary>
        Error = 3,
    }

    /// <summary>
    /// Strict, allocation-free UTF-8 JSON scanning primitives: the
    /// structural validation pass of the hand-rolled codec (see the
    /// json-serialization skill — two-pass envelope decode, total on any
    /// input). Tokenizes RFC 8259, validates string escapes and UTF-8
    /// well-formedness, enforces a nesting bound, and reports bounded
    /// <see cref="DecodeError"/> values instead of throwing.
    /// </summary>
    internal ref struct JsonScanner
    {
        /// <summary>
        /// Maximum JSON nesting depth (objects and arrays) accepted; must
        /// match <see cref="EnvelopeReader.MaxDepth"/> so payload decode and
        /// envelope decode enforce the same bound.
        /// </summary>
        internal const int MaxDepth = 64;

        /// <summary>Nesting depth of root-level member values (the root object is level 1).</summary>
        private const int RootMemberDepth = 2;

        private readonly ReadOnlySpan<byte> _buf;
        private int _pos;

        internal JsonScanner(ReadOnlySpan<byte> buffer)
        {
            _buf = buffer;
            _pos = 0;
        }

        /// <summary>Number of bytes consumed so far (the error offset source).</summary>
        internal int Position => _pos;

        /// <summary>True when all input has been consumed.</summary>
        internal bool IsEof => _pos >= _buf.Length;

        /// <summary>Skips JSON whitespace (space, tab, LF, CR).</summary>
        internal void SkipWhitespace()
        {
            while (_pos < _buf.Length)
            {
                byte b = _buf[_pos];
                if (b != 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
                {
                    break;
                }

                _pos++;
            }
        }

        /// <summary>The byte at the current position, or 0 at end of input.</summary>
        internal byte Peek => _pos < _buf.Length ? _buf[_pos] : (byte)0;

        /// <summary>
        /// Consumes the next byte when it equals <paramref name="expected"/>.
        /// EOF maps to <see cref="DecodeError.Truncated"/>, any other byte to
        /// <see cref="DecodeError.InvalidToken"/>.
        /// </summary>
        internal DecodeError Expect(byte expected)
        {
            if (_pos >= _buf.Length)
            {
                return DecodeError.Truncated;
            }

            if (_buf[_pos] != expected)
            {
                return DecodeError.InvalidToken;
            }

            _pos++;
            return default(DecodeError);
        }

        /// <summary>
        /// Scans a JSON string starting at the opening quote and returns its
        /// raw byte range (quotes included) plus whether it contains escape
        /// sequences. Validates escapes, control characters, and UTF-8.
        /// </summary>
        internal DecodeError ScanStringRaw(out Range raw, out bool hasEscapes)
        {
            raw = default;
            hasEscapes = false;

            DecodeError err = Expect((byte)'"');
            if (err != default(DecodeError))
            {
                return err;
            }

            int start = _pos - 1;
            while (true)
            {
                if (_pos >= _buf.Length)
                {
                    return DecodeError.Truncated;
                }

                byte b = _buf[_pos];
                switch (b)
                {
                    case (byte)'"':
                        _pos++;
                        raw = start.._pos;
                        return default(DecodeError);
                    case (byte)'\\':
                        hasEscapes = true;
                        err = ScanEscape();
                        if (err != default(DecodeError))
                        {
                            return err;
                        }

                        break;
                    default:
                        if (b < 0x20)
                        {
                            return DecodeError.InvalidToken;
                        }

                        int width = Utf8SequenceWidth(b, _buf, _pos);
                        if (width <= 0)
                        {
                            return DecodeError.InvalidToken;
                        }

                        _pos += width;
                        break;
                }
            }
        }

        /// <summary>
        /// Validates and consumes any JSON value at the current position,
        /// tracking nesting depth against <paramref name="maxDepth"/>.
        /// </summary>
        internal DecodeError ScanValue(int depth, int maxDepth) =>
            ScanValueRaw(depth, maxDepth, out _);

        /// <summary>
        /// Validates and consumes any JSON value at the current position and
        /// returns its raw byte range.
        /// </summary>
        internal DecodeError ScanValueRaw(int depth, int maxDepth, out Range raw)
        {
            raw = default;
            if (depth > maxDepth)
            {
                return DecodeError.DepthExceeded;
            }

            if (_pos >= _buf.Length)
            {
                return DecodeError.Truncated;
            }

            switch (_buf[_pos])
            {
                case (byte)'{':
                    return ScanObject(depth, maxDepth, out raw);
                case (byte)'[':
                    return ScanArrayRaw(depth, maxDepth, out raw);
                case (byte)'"':
                    return ScanStringRaw(out raw, out _);
                case (byte)'t':
                case (byte)'f':
                case (byte)'n':
                {
                    int start = _pos;
                    DecodeError literal = ScanLiteral(
                        _buf[_pos] == (byte)'t' ? "true"
                        : _buf[_pos] == (byte)'f' ? "false"
                        : "null"
                    );
                    if (literal == default(DecodeError))
                    {
                        raw = start.._pos;
                    }

                    return literal;
                }

                case (byte)'-':
                case >= (byte)'0' and <= (byte)'9':
                {
                    int start = _pos;
                    DecodeError number = ScanNumber();
                    if (number == default(DecodeError))
                    {
                        raw = start.._pos;
                    }

                    return number;
                }

                default:
                    return DecodeError.InvalidToken;
            }
        }

        // --- Payload member walk --------------------------------------------

        /// <summary>
        /// Begins a payload object walk: consumes <c>{</c> and any leading
        /// whitespace. Returns <see cref="JsonMemberState.Member"/> when a
        /// first member follows, <see cref="JsonMemberState.EndObject"/> for
        /// an empty object (consumed), or <see cref="JsonMemberState.Error"/>
        /// for malformed input.
        /// </summary>
        internal JsonMemberState BeginObject()
        {
            SkipWhitespace();
            if (Expect((byte)'{') != default(DecodeError))
            {
                return JsonMemberState.Error;
            }

            SkipWhitespace();
            if (Peek == (byte)'}')
            {
                _pos++;
                return JsonMemberState.EndObject;
            }

            return Peek == (byte)'"' ? JsonMemberState.Member : JsonMemberState.Error;
        }

        /// <summary>
        /// Scans one member (key, colon, value) of a payload object. The
        /// caller processes the ranges, then calls <see cref="EndMember"/>.
        /// Depth accounting matches the envelope reader: member values sit
        /// at root-member depth.
        /// </summary>
        internal JsonMemberState ScanMember(out Range keyRaw, out Range valueRaw)
        {
            keyRaw = default;
            valueRaw = default;
            if (ScanStringRaw(out keyRaw, out _) != default(DecodeError))
            {
                return JsonMemberState.Error;
            }

            SkipWhitespace();
            if (Expect((byte)':') != default(DecodeError))
            {
                return JsonMemberState.Error;
            }

            SkipWhitespace();
            return ScanValueRaw(RootMemberDepth, MaxDepth, out valueRaw) == default(DecodeError)
                ? JsonMemberState.Member
                : JsonMemberState.Error;
        }

        /// <summary>
        /// Consumes the member separator after a scanned member: the next
        /// member, the object close, or an error (truncated or malformed).
        /// </summary>
        internal JsonMemberState EndMember()
        {
            SkipWhitespace();
            switch (Peek)
            {
                case (byte)',':
                    _pos++;
                    SkipWhitespace();
                    return JsonMemberState.Member;
                case (byte)'}':
                    _pos++;
                    return JsonMemberState.EndObject;
                default:
                    return JsonMemberState.Error;
            }
        }

        /// <summary>
        /// Compares a scanned member key (quotes excluded, escapes decoded)
        /// against a plain ASCII wire name. Keys are plain on every frame the
        /// server emits, so the common path is a byte compare. Non-ASCII
        /// UTF-8 bytes (all ≥ 0x80) can never equal ASCII name bytes, so the
        /// byte compare is exact.
        /// </summary>
        internal bool KeyIs(Range keyRaw, string asciiName)
        {
            (int Offset, int Length) s = keyRaw.GetOffsetAndLength(_buf.Length);
            ReadOnlySpan<byte> inner = _buf.Slice(s.Offset + 1, s.Length - 2);
            if (inner.IndexOf((byte)'\\') < 0)
            {
                if (inner.Length != asciiName.Length)
                {
                    return false;
                }

                for (int i = 0; i < inner.Length; i++)
                {
                    if (inner[i] != (byte)asciiName[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            Span<byte> name =
                asciiName.Length <= 64 ? stackalloc byte[64] : new byte[asciiName.Length];
            for (int i = 0; i < asciiName.Length; i++)
            {
                name[i] = (byte)asciiName[i];
            }

            return KeyEquals(inner, hasEscapes: true, name.Slice(0, asciiName.Length));
        }

        /// <summary>Reads a scanned JSON string into a managed string.</summary>
        internal bool TryReadString(Range valueRaw, out string value)
        {
            value = string.Empty;
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(_buf.Length);
            if (s.Length < 2 || _buf[s.Offset] != (byte)'"')
            {
                return false;
            }

            ReadOnlySpan<byte> inner = _buf.Slice(s.Offset + 1, s.Length - 2);
            value = MaterializeString(inner, hasEscapes: inner.IndexOf((byte)'\\') >= 0);
            return true;
        }

        /// <summary>Reads a scanned JSON number that must be an unsigned 32-bit integer in decimal form.</summary>
        internal bool TryReadUInt32(Range valueRaw, out uint value)
        {
            value = 0;
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(_buf.Length);
            ReadOnlySpan<byte> digits = _buf.Slice(s.Offset, s.Length);
            if (digits.IsEmpty || digits.Length > 10)
            {
                return false;
            }

            ulong accumulator = 0;
            foreach (byte b in digits)
            {
                if (!IsDigit(b))
                {
                    return false;
                }

                accumulator = (accumulator * 10) + (uint)(b - (byte)'0');
                if (accumulator > uint.MaxValue)
                {
                    return false;
                }
            }

            value = (uint)accumulator;
            return true;
        }

        /// <summary>Reads a scanned JSON literal that must be <c>true</c> or <c>false</c>.</summary>
        internal bool TryReadBoolean(Range valueRaw, out bool value)
        {
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(_buf.Length);
            ReadOnlySpan<byte> raw = _buf.Slice(s.Offset, s.Length);
            if (raw.SequenceEqual(TrueLiteral))
            {
                value = true;
                return true;
            }

            if (raw.SequenceEqual(FalseLiteral))
            {
                value = false;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>Checks whether a scanned value is the JSON literal <c>null</c>.</summary>
        internal bool TryReadNull(Range valueRaw)
        {
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(_buf.Length);
            return _buf.Slice(s.Offset, s.Length).SequenceEqual(NullLiteral);
        }

        /// <summary>
        /// Reads a scanned value that must be a hyphenated UUID string
        /// (<c>8-4-4-4-12</c> hex). Allocation-free: the text form is parsed
        /// big-endian per field, matching <see cref="Guid.Parse"/> exactly.
        /// Escaped strings are rejected (UUID text never needs escapes).
        /// </summary>
        internal bool TryReadGuid(Range valueRaw, out Guid value)
        {
            value = default;
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(_buf.Length);
            if (s.Length != 38 || _buf[s.Offset] != (byte)'"' || _buf[s.Offset + 37] != (byte)'"')
            {
                return false;
            }

            ReadOnlySpan<byte> text = _buf.Slice(s.Offset + 1, 36);
            if (
                text.IndexOf((byte)'\\') >= 0
                || text[8] != (byte)'-'
                || text[13] != (byte)'-'
                || text[18] != (byte)'-'
                || text[23] != (byte)'-'
            )
            {
                return false;
            }

            // Printed groups are big-endian: the first group's first pair is
            // the integer's most significant byte. Each pair is validated
            // before combining (an all-FFFF field would otherwise alias the
            // -1 error sentinel).
            int a0 = Hex(text, 0);
            int a1 = Hex(text, 2);
            int a2 = Hex(text, 4);
            int a3 = Hex(text, 6);
            int b0 = Hex(text, 9);
            int b1 = Hex(text, 11);
            int c0 = Hex(text, 14);
            int c1 = Hex(text, 16);
            if ((a0 | a1 | a2 | a3 | b0 | b1 | c0 | c1) < 0)
            {
                return false;
            }

            int a = (a0 << 24) | (a1 << 16) | (a2 << 8) | a3;
            int b = (b0 << 8) | b1;
            int c = (c0 << 8) | c1;

            Span<byte> bytes = stackalloc byte[16];

            // Guid's binary layout is the little-endian RFC 4122 encoding:
            // the first three fields serialize least significant byte first.
            bytes[0] = (byte)a;
            bytes[1] = (byte)(a >> 8);
            bytes[2] = (byte)(a >> 16);
            bytes[3] = (byte)(a >> 24);
            bytes[4] = (byte)b;
            bytes[5] = (byte)(b >> 8);
            bytes[6] = (byte)c;
            bytes[7] = (byte)(c >> 8);

            // The trailing 8 bytes print as the last three groups
            // (4-4-12 hex), big-endian, MSB first.
            for (int i = 0; i < 8; i++)
            {
                int index = i < 2 ? 19 + (i * 2) : 24 + ((i - 2) * 2);
                int pair = Hex(text, index);
                if (pair < 0)
                {
                    return false;
                }

                bytes[8 + i] = (byte)pair;
            }

            value = new Guid(bytes);
            return true;
        }

        /// <summary>
        /// Reads one big-endian hex byte (two characters) at
        /// <paramref name="index"/>; -1 when either character is not hex.
        /// </summary>
        private static int Hex(ReadOnlySpan<byte> text, int index)
        {
            int high = HexNibble(text[index]);
            int low = HexNibble(text[index + 1]);
            return (high | low) < 0 ? -1 : (high << 4) | low;
        }

        private static int HexNibble(byte b)
        {
            if ((byte)(b - (byte)'0') <= 9)
            {
                return b - (byte)'0';
            }

            if ((byte)(b - (byte)'a') <= 5)
            {
                return b - (byte)'a' + 10;
            }

            if ((byte)(b - (byte)'A') <= 5)
            {
                return b - (byte)'A' + 10;
            }

            return -1;
        }

        /// <summary>
        /// Returns a scanned value as a verbatim JSON object slice of
        /// <paramref name="data"/> (no re-encode; passthrough contract).
        /// </summary>
        internal static bool TryReadObjectSlice(
            ReadOnlyMemory<byte> data,
            Range valueRaw,
            out ReadOnlyMemory<byte> slice
        )
        {
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(data.Length);
            slice = default;
            if (s.Length < 2 || data.Span[s.Offset] != (byte)'{')
            {
                return false;
            }

            slice = data.Slice(s.Offset, s.Length);
            return true;
        }

        /// <summary>
        /// Reads a scanned value that must be a JSON array of strings into a
        /// list (decode path; allocates the result and its strings).
        /// </summary>
        internal static bool TryReadStringArray(
            ReadOnlyMemory<byte> data,
            Range valueRaw,
            out IReadOnlyList<string>? values
        )
        {
            values = null;
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(data.Length);
            if (s.Length < 2 || data.Span[s.Offset] != (byte)'[')
            {
                return false;
            }

            JsonScanner scanner = new JsonScanner(data.Span.Slice(s.Offset, s.Length));
            scanner.SkipWhitespace();
            if (scanner.Expect((byte)'[') != default(DecodeError))
            {
                return false;
            }

            List<string> list = new List<string>();
            scanner.SkipWhitespace();
            if (scanner.Peek == (byte)']')
            {
                scanner._pos++;
            }
            else
            {
                while (true)
                {
                    scanner.SkipWhitespace();
                    if (
                        scanner.ScanStringRaw(out Range element, out bool hasEscapes)
                        != default(DecodeError)
                    )
                    {
                        return false;
                    }

                    (int EOffset, int ELength) e = element.GetOffsetAndLength(s.Length);
                    list.Add(
                        MaterializeString(
                            data.Span.Slice(s.Offset + e.EOffset + 1, e.ELength - 2),
                            hasEscapes
                        )
                    );
                    scanner.SkipWhitespace();
                    byte next = scanner.Peek;
                    if (next == (byte)',')
                    {
                        scanner._pos++;
                        continue;
                    }

                    if (next == (byte)']')
                    {
                        scanner._pos++;
                        break;
                    }

                    return false;
                }
            }

            scanner.SkipWhitespace();
            if (!scanner.IsEof)
            {
                return false;
            }

            values = list;
            return true;
        }

        // --- Shared escape-aware text helpers --------------------------------

        /// <summary>
        /// Compares a scanned key (quotes stripped) against an ASCII wire
        /// name. Known names are plain ASCII, so only escapes that resolve to
        /// single ASCII bytes can match.
        /// </summary>
        internal static bool KeyEquals(
            ReadOnlySpan<byte> keyInner,
            bool hasEscapes,
            ReadOnlySpan<byte> asciiName
        )
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
        /// Materializes a validated JSON string body (quotes excluded) into a
        /// managed string, decoding escapes. Rare path: only payload decode
        /// and unknown-type routing allocate here.
        /// </summary>
        internal static string MaterializeString(ReadOnlySpan<byte> inner, bool hasEscapes)
        {
            if (!hasEscapes)
            {
                return System.Text.Encoding.UTF8.GetString(inner);
            }

            // UTF-16 chars never exceed raw byte count (4-byte UTF-8 → 2 chars).
            Span<char> chars = inner.Length <= 512 ? stackalloc char[512] : new char[inner.Length];
            int written = 0;
            int segmentStart = 0;
            int i = 0;
            while (i < inner.Length)
            {
                if (inner[i] != (byte)'\\')
                {
                    i++;
                    continue;
                }

                written += AppendUtf8Segment(
                    inner.Slice(segmentStart, i - segmentStart),
                    chars.Slice(written)
                );
                int scalar = DecodeEscapeScalar(inner, ref i);
                chars[written++] = scalar >= 0 ? (char)scalar : '?';
                segmentStart = i;
            }

            written += AppendUtf8Segment(inner.Slice(segmentStart), chars.Slice(written));
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
                case (byte)'"':
                    return (byte)'"';
                case (byte)'\\':
                    return (byte)'\\';
                case (byte)'/':
                    return (byte)'/';
                case (byte)'b':
                    return 0x08;
                case (byte)'f':
                    return 0x0C;
                case (byte)'n':
                    return 0x0A;
                case (byte)'r':
                    return 0x0D;
                case (byte)'t':
                    return 0x09;
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
                case (byte)'"':
                    return '"';
                case (byte)'\\':
                    return '\\';
                case (byte)'/':
                    return '/';
                case (byte)'b':
                    return '\b';
                case (byte)'f':
                    return '\f';
                case (byte)'n':
                    return '\n';
                case (byte)'r':
                    return '\r';
                case (byte)'t':
                    return '\t';
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

        private static readonly byte[] TrueLiteral = { (byte)'t', (byte)'r', (byte)'u', (byte)'e' };
        private static readonly byte[] FalseLiteral =
        {
            (byte)'f',
            (byte)'a',
            (byte)'l',
            (byte)'s',
            (byte)'e',
        };
        private static readonly byte[] NullLiteral = { (byte)'n', (byte)'u', (byte)'l', (byte)'l' };

        /// <summary>
        /// Validates and consumes a JSON array at the current position and
        /// returns its raw byte range (brackets included).
        /// </summary>
        internal DecodeError ScanArrayRaw(int depth, int maxDepth, out Range raw)
        {
            raw = default;
            int start = _pos;
            DecodeError err = Expect((byte)'[');
            if (err != default(DecodeError))
            {
                return err;
            }

            SkipWhitespace();
            if (_pos < _buf.Length && _buf[_pos] == (byte)']')
            {
                _pos++;
                raw = start.._pos;
                return default(DecodeError);
            }

            while (true)
            {
                SkipWhitespace();
                err = ScanValue(depth + 1, maxDepth);
                if (err != default(DecodeError))
                {
                    return err;
                }

                SkipWhitespace();
                if (_pos >= _buf.Length)
                {
                    return DecodeError.Truncated;
                }

                switch (_buf[_pos])
                {
                    case (byte)',':
                        _pos++;
                        break;
                    case (byte)']':
                        _pos++;
                        raw = start.._pos;
                        return default(DecodeError);
                    default:
                        return DecodeError.InvalidToken;
                }
            }
        }

        /// <summary>
        /// Validates and consumes a JSON object at the current position and
        /// returns its raw byte range (braces included).
        /// </summary>
        internal DecodeError ScanObject(int depth, int maxDepth, out Range raw)
        {
            raw = default;
            int start = _pos;
            DecodeError err = Expect((byte)'{');
            if (err != default(DecodeError))
            {
                return err;
            }

            SkipWhitespace();
            if (_pos < _buf.Length && _buf[_pos] == (byte)'}')
            {
                _pos++;
                raw = start.._pos;
                return default(DecodeError);
            }

            while (true)
            {
                // Member key.
                err = ScanStringRaw(out _, out _);
                if (err != default(DecodeError))
                {
                    return err;
                }

                SkipWhitespace();
                err = Expect((byte)':');
                if (err != default(DecodeError))
                {
                    return err;
                }

                SkipWhitespace();
                err = ScanValue(depth + 1, maxDepth);
                if (err != default(DecodeError))
                {
                    return err;
                }

                SkipWhitespace();
                if (_pos >= _buf.Length)
                {
                    return DecodeError.Truncated;
                }

                switch (_buf[_pos])
                {
                    case (byte)',':
                        _pos++;
                        SkipWhitespace();
                        break;
                    case (byte)'}':
                        _pos++;
                        raw = start.._pos;
                        return default(DecodeError);
                    default:
                        return DecodeError.InvalidToken;
                }
            }
        }

        /// <summary>
        /// Consumes an escape sequence positioned just after the backslash.
        /// </summary>
        private DecodeError ScanEscape()
        {
            // _pos points at the backslash.
            if (_pos + 1 >= _buf.Length)
            {
                return DecodeError.Truncated;
            }

            _pos++;
            byte e = _buf[_pos];
            _pos++;
            switch (e)
            {
                case (byte)'"':
                case (byte)'\\':
                case (byte)'/':
                case (byte)'b':
                case (byte)'f':
                case (byte)'n':
                case (byte)'r':
                case (byte)'t':
                    return default(DecodeError);
                case (byte)'u':
                    for (int i = 0; i < 4; i++)
                    {
                        if (_pos >= _buf.Length)
                        {
                            return DecodeError.Truncated;
                        }

                        if (!IsHex(_buf[_pos]))
                        {
                            return DecodeError.InvalidToken;
                        }

                        _pos++;
                    }

                    return default(DecodeError);
                default:
                    return DecodeError.InvalidToken;
            }
        }

        internal DecodeError ScanLiteral(string word)
        {
            // Byte-exact compare; all JSON literals are ASCII.
            for (int i = 0; i < word.Length; i++)
            {
                if (_pos >= _buf.Length)
                {
                    return DecodeError.Truncated;
                }

                if (_buf[_pos] != (byte)word[i])
                {
                    return DecodeError.InvalidToken;
                }

                _pos++;
            }

            return default(DecodeError);
        }

        private DecodeError ScanNumber()
        {
            // -? int frac? exp?  per RFC 8259 grammar.
            if (_pos < _buf.Length && _buf[_pos] == (byte)'-')
            {
                _pos++;
            }

            // Integer part: 0, or [1-9] followed by digits.
            if (_pos >= _buf.Length)
            {
                return DecodeError.Truncated;
            }

            byte b = _buf[_pos];
            if (b == (byte)'0')
            {
                _pos++;
            }
            else if (b >= (byte)'1' && b <= (byte)'9')
            {
                _pos++;
                while (_pos < _buf.Length && IsDigit(_buf[_pos]))
                {
                    _pos++;
                }
            }
            else
            {
                return DecodeError.InvalidToken;
            }

            // Fraction.
            if (_pos < _buf.Length && _buf[_pos] == (byte)'.')
            {
                _pos++;
                if (_pos >= _buf.Length)
                {
                    return DecodeError.Truncated;
                }

                if (!IsDigit(_buf[_pos]))
                {
                    return DecodeError.InvalidToken;
                }

                while (_pos < _buf.Length && IsDigit(_buf[_pos]))
                {
                    _pos++;
                }
            }

            // Exponent.
            if (_pos < _buf.Length && (_buf[_pos] == (byte)'e' || _buf[_pos] == (byte)'E'))
            {
                _pos++;
                if (_pos < _buf.Length && (_buf[_pos] == (byte)'+' || _buf[_pos] == (byte)'-'))
                {
                    _pos++;
                }

                if (_pos >= _buf.Length)
                {
                    return DecodeError.Truncated;
                }

                if (!IsDigit(_buf[_pos]))
                {
                    return DecodeError.InvalidToken;
                }

                while (_pos < _buf.Length && IsDigit(_buf[_pos]))
                {
                    _pos++;
                }
            }

            return default(DecodeError);
        }

        /// <summary>
        /// Validates the UTF-8 sequence starting at <paramref name="offset"/>
        /// and returns its length in bytes, or 0 when malformed (RFC 3629:
        /// surrogates and overlongs rejected).
        /// </summary>
        private static int Utf8SequenceWidth(byte lead, ReadOnlySpan<byte> buf, int offset)
        {
            if (lead < 0x80)
            {
                return 1;
            }

            int required;
            byte lo = 0x80,
                hi = 0xBF;
            switch (lead)
            {
                case >= 0xC2 and <= 0xDF:
                    required = 1;
                    break;
                case 0xE0:
                    required = 2;
                    lo = 0xA0;
                    break;
                case >= 0xE1 and <= 0xEC:
                case >= 0xEE and <= 0xEF:
                    required = 2;
                    break;
                case 0xED:
                    required = 2;
                    hi = 0x9F;
                    break;
                case 0xF0:
                    required = 3;
                    lo = 0x90;
                    break;
                case >= 0xF1 and <= 0xF3:
                    required = 3;
                    break;
                case 0xF4:
                    required = 3;
                    hi = 0x8F;
                    break;
                default:
                    return 0;
            }

            if (offset + required >= buf.Length)
            {
                // The final continuation byte must exist within the buffer.
                return 0;
            }

            byte b1 = buf[offset + 1];
            if (b1 < lo || b1 > hi)
            {
                return 0;
            }

            for (int i = 2; i <= required; i++)
            {
                byte cont = buf[offset + i];
                if (cont < 0x80 || cont > 0xBF)
                {
                    return 0;
                }
            }

            return required + 1;
        }

        internal static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';

        internal static bool IsHex(byte b) =>
            (b >= (byte)'0' && b <= (byte)'9')
            || (b >= (byte)'a' && b <= (byte)'f')
            || (b >= (byte)'A' && b <= (byte)'F');
    }

    /// <summary>
    /// Allocation-free UTF-8 JSON writer over an
    /// <see cref="IBufferWriter{T}"/>: the encode half of the hand-rolled
    /// codec. Strings are escape-aware per RFC 8259 (short escapes for the
    /// named control characters, <c>\u00XX</c> for the rest; non-ASCII bytes
    /// pass through as valid UTF-8). Output may split across any number of
    /// buffer segments — every write renews the span on demand. Finish each
    /// frame with <see cref="Flush"/>.
    /// </summary>
    internal ref struct JsonWriter
    {
        private static readonly byte[] CommaSpace = { (byte)',', (byte)' ' };

        private readonly IBufferWriter<byte> _writer;
        private Span<byte> _span;
        private int _pos;

        internal JsonWriter(IBufferWriter<byte> writer)
        {
            _writer = writer;
            _span = writer.GetSpan();
            _pos = 0;
        }

        internal void WriteBytes(ReadOnlySpan<byte> value)
        {
            Ensure(value.Length);
            value.CopyTo(_span.Slice(_pos));
            _pos += value.Length;
        }

        internal void WriteByte(byte value)
        {
            Ensure(1);
            _span[_pos++] = value;
        }

        /// <summary>
        /// Writes a UTF-8 string as a quoted JSON string with escapes. The
        /// writer cannot validate that arbitrary bytes are well-formed UTF-8
        /// in every segment, so callers pass text produced from managed
        /// strings or previously validated frames.
        /// </summary>
        internal void WriteString(ReadOnlySpan<byte> utf8Value)
        {
            WriteByte((byte)'"');
            int i = 0;
            while (i < utf8Value.Length)
            {
                int runStart = i;
                while (i < utf8Value.Length && !NeedsEscape(utf8Value[i]))
                {
                    i++;
                }

                if (i > runStart)
                {
                    WriteBytes(utf8Value.Slice(runStart, i - runStart));
                }

                if (i < utf8Value.Length)
                {
                    WriteEscaped(utf8Value[i]);
                    i++;
                }
            }

            WriteByte((byte)'"');
        }

        /// <summary>
        /// Writes a managed string as a quoted, escape-aware UTF-8 JSON
        /// string without intermediate allocations: ASCII runs copy
        /// directly, control characters escape, and non-ASCII characters
        /// encode into UTF-8 in place. Unpaired surrogates (never valid
        /// input) are replaced by the UTF-8 encoder rather than corrupting
        /// the frame.
        /// </summary>
        internal void WriteString(string value)
        {
            WriteByte((byte)'"');

            // One scratch slot for the whole loop — stackalloc inside the
            // loop would allocate a fresh frame region per iteration and
            // overflow the stack on long non-ASCII strings.
            Span<char> scratch = stackalloc char[2];
            int i = 0;
            while (i < value.Length)
            {
                int runStart = i;
                while (i < value.Length && !NeedsEscapeChar(value[i]))
                {
                    i++;
                }

                int runLength = i - runStart;
                if (runLength > 0)
                {
                    Ensure(runLength);
                    for (int k = 0; k < runLength; k++)
                    {
                        _span[_pos++] = (byte)value[runStart + k];
                    }
                }

                if (i >= value.Length)
                {
                    break;
                }

                char c = value[i];
                if (c < 0x80)
                {
                    WriteEscaped((byte)c);
                    i++;
                }
                else if (char.IsSurrogatePair(value, i))
                {
                    scratch[0] = value[i];
                    scratch[1] = value[i + 1];
                    Ensure(4);
                    _pos += System.Text.Encoding.UTF8.GetBytes(scratch, _span.Slice(_pos));
                    i += 2;
                }
                else
                {
                    scratch[0] = c;
                    Ensure(3);
                    _pos += System.Text.Encoding.UTF8.GetBytes(
                        scratch.Slice(0, 1),
                        _span.Slice(_pos)
                    );
                    i++;
                }
            }

            WriteByte((byte)'"');
        }

        /// <summary>
        /// Writes a plain ASCII key with the canonical member separator:
        /// <c>"key": </c>. Field keys are compile-time ASCII constants.
        /// </summary>
        internal void WriteKey(ReadOnlySpan<byte> asciiKey)
        {
            WriteByte((byte)'"');
            WriteBytes(asciiKey);
            WriteByte((byte)'"');
            WriteBytes(ColonSpace);
        }

        /// <summary>Writes the canonical member separator (<c>, </c>) before a non-first member.</summary>
        internal void WriteMemberSeparator(ref bool firstMember)
        {
            if (!firstMember)
            {
                WriteBytes(CommaSpace);
            }

            firstMember = false;
        }

        /// <summary>Writes a JSON array of strings with the canonical <c>, </c> separator.</summary>
        internal void WriteStringArray(IReadOnlyList<string> values)
        {
            WriteByte((byte)'[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    WriteBytes(CommaSpace);
                }

                WriteString(
                    values[i] ?? throw new ArgumentException("Array elements must not be null.")
                );
            }

            WriteByte((byte)']');
        }

        internal void WriteUInt32(uint value)
        {
            Ensure(10);
            Span<byte> digits = stackalloc byte[10];
            int len = 0;
            do
            {
                digits[len++] = (byte)('0' + (value % 10));
                value /= 10;
            } while (value != 0);

            while (len > 0)
            {
                _span[_pos++] = digits[--len];
            }
        }

        internal void WriteBoolean(bool value)
        {
            ReadOnlySpan<byte> literal = value ? TrueLiteral : FalseLiteral;
            WriteBytes(literal);
        }

        internal void WriteNull()
        {
            WriteBytes(NullLiteral);
        }

        /// <summary>Commits all buffered bytes to the underlying writer.</summary>
        internal void Flush()
        {
            _writer.Advance(_pos);
            _pos = 0;
            _span = default;
        }

        private static readonly byte[] ColonSpace = { (byte)':', (byte)' ' };
        private static readonly byte[] TrueLiteral = { (byte)'t', (byte)'r', (byte)'u', (byte)'e' };
        private static readonly byte[] FalseLiteral =
        {
            (byte)'f',
            (byte)'a',
            (byte)'l',
            (byte)'s',
            (byte)'e',
        };
        private static readonly byte[] NullLiteral = { (byte)'n', (byte)'u', (byte)'l', (byte)'l' };

        private static bool NeedsEscape(byte b) => b < 0x20 || b == (byte)'"' || b == (byte)'\\';

        private static bool NeedsEscapeChar(char c) =>
            c < 0x20 || c == '"' || c == '\\' || c >= 0x80;

        private void WriteEscaped(byte b)
        {
            Ensure(6);
            _span[_pos++] = (byte)'\\';
            switch (b)
            {
                case (byte)'"':
                    _span[_pos++] = (byte)'"';
                    break;
                case (byte)'\\':
                    _span[_pos++] = (byte)'\\';
                    break;
                case 0x08:
                    _span[_pos++] = (byte)'b';
                    break;
                case 0x0C:
                    _span[_pos++] = (byte)'f';
                    break;
                case 0x0A:
                    _span[_pos++] = (byte)'n';
                    break;
                case 0x0D:
                    _span[_pos++] = (byte)'r';
                    break;
                case 0x09:
                    _span[_pos++] = (byte)'t';
                    break;
                default:
                {
                    _span[_pos++] = (byte)'u';
                    _span[_pos++] = (byte)'0';
                    _span[_pos++] = (byte)'0';
                    byte hi = (byte)(b >> 4);
                    byte lo = (byte)(b & 0xF);
                    _span[_pos++] = (byte)(hi < 10 ? '0' + hi : 'A' + hi - 10);
                    _span[_pos++] = (byte)(lo < 10 ? '0' + lo : 'A' + lo - 10);
                    break;
                }
            }
        }

        private void Ensure(int count)
        {
            if (count > _span.Length - _pos)
            {
                _writer.Advance(_pos);
                _pos = 0;
                _span = _writer.GetSpan(count);
            }
        }
    }
}
