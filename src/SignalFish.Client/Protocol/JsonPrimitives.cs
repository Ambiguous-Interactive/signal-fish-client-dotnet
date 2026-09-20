namespace SignalFish.Client.Protocol
{
    using System;

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
            return DecodeError.None;
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
            if (err != DecodeError.None)
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
                        return DecodeError.None;
                    case (byte)'\\':
                        hasEscapes = true;
                        err = ScanEscape();
                        if (err != DecodeError.None)
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
        internal DecodeError ScanValue(int depth, int maxDepth)
        {
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
                    return ScanObject(depth, maxDepth, out _);
                case (byte)'[':
                    return ScanArray(depth, maxDepth);
                case (byte)'"':
                    return ScanStringRaw(out _, out _);
                case (byte)'t':
                    return ScanLiteral("true");
                case (byte)'f':
                    return ScanLiteral("false");
                case (byte)'n':
                    return ScanLiteral("null");
                case (byte)'-':
                case >= (byte)'0' and <= (byte)'9':
                    return ScanNumber();
                default:
                    return DecodeError.InvalidToken;
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
            if (err != DecodeError.None)
            {
                return err;
            }

            SkipWhitespace();
            if (_pos < _buf.Length && _buf[_pos] == (byte)'}')
            {
                _pos++;
                raw = start.._pos;
                return DecodeError.None;
            }

            while (true)
            {
                // Member key.
                err = ScanStringRaw(out _, out _);
                if (err != DecodeError.None)
                {
                    return err;
                }

                SkipWhitespace();
                err = Expect((byte)':');
                if (err != DecodeError.None)
                {
                    return err;
                }

                SkipWhitespace();
                err = ScanValue(depth + 1, maxDepth);
                if (err != DecodeError.None)
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
                        return DecodeError.None;
                    default:
                        return DecodeError.InvalidToken;
                }
            }
        }

        private DecodeError ScanArray(int depth, int maxDepth)
        {
            DecodeError err = Expect((byte)'[');
            if (err != DecodeError.None)
            {
                return err;
            }

            SkipWhitespace();
            if (_pos < _buf.Length && _buf[_pos] == (byte)']')
            {
                _pos++;
                return DecodeError.None;
            }

            while (true)
            {
                SkipWhitespace();
                err = ScanValue(depth + 1, maxDepth);
                if (err != DecodeError.None)
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
                        return DecodeError.None;
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
                    return DecodeError.None;
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

                    return DecodeError.None;
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

            return DecodeError.None;
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

            return DecodeError.None;
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
            byte lo = 0x80, hi = 0xBF;
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
            (b >= (byte)'0' && b <= (byte)'9') ||
            (b >= (byte)'a' && b <= (byte)'f') ||
            (b >= (byte)'A' && b <= (byte)'F');
    }
}
