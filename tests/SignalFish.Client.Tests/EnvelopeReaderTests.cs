namespace SignalFish.Client.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using NUnit.Framework;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Shared access to the vendored golden protocol fixtures
    /// (tests/Golden, synced by scripts/sync-protocol-fixtures.ps1).
    /// </summary>
    internal static class GoldenFixtures
    {
        internal static readonly string[] PinnedFixtureFiles =
        {
            "v2-client-messages.jsonl",
            "v2-server-messages.jsonl",
            "v3-client-messages.jsonl",
            "v3-server-messages.jsonl",
        };

        internal static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "Golden");

        internal static string ReadLine(string fileName, int lineNumber)
        {
            string[] lines = File.ReadAllLines(Path.Combine(GoldenDirectory, fileName));
            Assert.That(
                lineNumber,
                Is.InRange(1, lines.Length),
                $"Bad fixture line reference: {fileName}:{lineNumber}."
            );
            return lines[lineNumber - 1];
        }

        /// <summary>
        /// First corpus line in the file whose envelope carries the given
        /// wire type; selects samples by meaning so tests survive upstream
        /// line reordering.
        /// </summary>
        internal static string ReadFirstLineOfType(string fileName, string wireType)
        {
            string[] lines = File.ReadAllLines(Path.Combine(GoldenDirectory, fileName));
            foreach (string line in lines)
            {
                if (
                    JsonDocument.Parse(line).RootElement.GetProperty("type").GetString() == wireType
                )
                {
                    return line;
                }
            }

            Assert.Fail($"Fixture {fileName} has no wire sample of type {wireType}.");
            return string.Empty;
        }

        /// <summary>Every (file, line number, wire type) triple in the pinned corpus.</summary>
        internal static IEnumerable<TestCaseData> AllEnvelopeLines()
        {
            foreach (string fileName in PinnedFixtureFiles)
            {
                string[] lines = File.ReadAllLines(Path.Combine(GoldenDirectory, fileName));
                for (int i = 0; i < lines.Length; i++)
                {
                    string type = JsonDocument
                        .Parse(lines[i])
                        .RootElement.GetProperty("type")
                        .GetString()!;
                    yield return new TestCaseData(fileName, i + 1, type).SetArgDisplayNames(
                        $"{fileName}:{i + 1}",
                        type
                    );
                }
            }
        }
    }

    /// <summary>
    /// Red-green anchor for PLAN.md M1.2: the envelope layer decodes every
    /// golden fixture into a typed message event, tolerates unknown types and
    /// unknown fields, and surfaces malformed input as a bounded DecodeFailed
    /// event — never as an exception.
    /// </summary>
    [TestFixture]
    public class EnvelopeReaderTests
    {
        // --- Golden fixture corpus ------------------------------------------

        // --- Allocation gate --------------------------------------------------------

        [Test]
        public void DecodeSteadyStateFullCorpusAllocatesNothing()
        {
            List<byte[]> corpus = new List<byte[]>();
            foreach (string fileName in GoldenFixtures.PinnedFixtureFiles)
            {
                foreach (
                    string line in File.ReadAllLines(
                        Path.Combine(GoldenFixtures.GoldenDirectory, fileName)
                    )
                )
                {
                    corpus.Add(Encoding.UTF8.GetBytes(line));
                }
            }

            foreach (byte[] wire in corpus)
            {
                /*
                    Named guard: UnknownMessage/DecodeFailed paths allocate a
                    type string by design. If an upstream fixture sync adds
                    such a line, this fails here - with a clear reason - long
                    before the allocation gate below sees it.
                */
                EnvelopeEvent warmup = EnvelopeReader.Decode(wire);
                Assert.That(
                    warmup.Kind,
                    Is.EqualTo(EnvelopeEventKind.Message),
                    "Allocation gate requires a corpus of fully-known messages; "
                        + $"a fixture line routed to {warmup.Kind} instead."
                );
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            /*
                Minimum delta across passes: a real regression allocates on
                every pass, while one-time JIT/OSR bookkeeping (a few bytes,
                observed under code-coverage instrumentation) inflates only
                the first measured pass.
            */
            long minDelta = long.MaxValue;
            for (int pass = 0; pass < 8; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                foreach (byte[] wire in corpus)
                {
                    EnvelopeReader.Decode(wire);
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }

            Assert.That(
                minDelta,
                Is.EqualTo(0),
                "Steady-state decoding of the whole golden corpus must not allocate."
            );
        }

        [Test, TestCaseSource(typeof(GoldenFixtures), nameof(GoldenFixtures.AllEnvelopeLines))]
        public void DecodeFixtureLineRoutesToTypedMessageEvent(
            string fileName,
            int lineNumber,
            string expectedType
        )
        {
            byte[] bytes = Encoding.UTF8.GetBytes(GoldenFixtures.ReadLine(fileName, lineNumber));

            EnvelopeEvent ev = EnvelopeReader.Decode(bytes);

            /*
                Enum member names are exactly the wire type names, so this
                asserts the routed kind directly — the ToWireName round-trip
                below alone would be permutation-invariant to table corruption.
            */
            Assert.That(
                Enum.TryParse(expectedType, out MessageKind expectedKind),
                Is.True,
                $"{fileName}:{lineNumber}: fixture type {expectedType} must map to a MessageKind."
            );
            Assert.That(
                ev.Kind,
                Is.EqualTo(EnvelopeEventKind.Message),
                $"{fileName}:{lineNumber} must decode to a typed message event."
            );
            Assert.That(
                ev.Message,
                Is.EqualTo(expectedKind),
                $"{fileName}:{lineNumber}: routed kind must be {expectedKind}."
            );
            Assert.That(
                ev.Raw.Span.SequenceEqual(bytes),
                Is.True,
                $"{fileName}:{lineNumber}: the event must expose the raw envelope bytes."
            );
            Assert.That(
                MessageKindNames.ToWireName(ev.Message),
                Is.EqualTo(expectedType),
                $"{fileName}:{lineNumber}: routed kind must round-trip to the wire type name."
            );
        }

        [Test, TestCaseSource(typeof(GoldenFixtures), nameof(GoldenFixtures.PinnedFixtureFiles))]
        public void DecodeFixturesDataSliceCoversPayloadObjectExactly(string fileName)
        {
            string[] lines = File.ReadAllLines(
                Path.Combine(GoldenFixtures.GoldenDirectory, fileName)
            );
            foreach (string line in lines)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                EnvelopeEvent ev = EnvelopeReader.Decode(bytes);

                using JsonDocument doc = JsonDocument.Parse(line);
                bool hasData =
                    doc.RootElement.TryGetProperty("data", out JsonElement data)
                    && data.ValueKind == JsonValueKind.Object;
                if (!hasData)
                {
                    Assert.That(
                        ev.Data.Length,
                        Is.EqualTo(0),
                        $"{fileName}: payloadless frame must have empty Data."
                    );
                    continue;
                }

                string rawJson = doc.RootElement.GetRawText();
                int dataStart = line.AsSpan()
                    .IndexOf(data.GetRawText().AsSpan(), StringComparison.Ordinal);
                Assert.That(dataStart, Is.GreaterThanOrEqualTo(0));
                byte[] expected = Encoding.UTF8.GetBytes(
                    line.Substring(dataStart, data.GetRawText().Length)
                );

                Assert.That(
                    ev.Data.Span.SequenceEqual(expected),
                    Is.True,
                    $"{fileName}: Data must be the exact \"data\" object bytes for: {line}."
                );
            }
        }

        // --- Forward compatibility ------------------------------------------

        [TestCase("{\"type\":\"FutureThing\",\"data\":{\"x\":1}}", "FutureThing")]
        [TestCase("{\"type\":\"Future\",\"data\":{}}", "Future")]
        [TestCase("{\"type\":\"Fu\\u0074ure\"}", "Future")]
        public void DecodeUnknownTypeEmitsUnknownMessageWithoutThrowing(
            string wire,
            string expectedType
        )
        {
            EnvelopeEvent ev = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(wire));

            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.UnknownMessage));
            Assert.That(ev.TypeText, Is.EqualTo(expectedType));
            Assert.That(ev.Raw.Span.SequenceEqual(Encoding.UTF8.GetBytes(wire)), Is.True);
        }

        [Test]
        public void DecodeTypeValueWithEscapesNeverRoutesToKnownKind()
        {
            /*
                Byte-exact routing policy: an escaped (but semantically equal)
                type value degrades to UnknownMessage with the decoded text,
                never to a known kind.
            */
            EnvelopeEvent ev = EnvelopeReader.Decode(
                Encoding.UTF8.GetBytes("{\"type\":\"P\\u0069ng\"}")
            );

            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.UnknownMessage));
            Assert.That(ev.TypeText, Is.EqualTo("Ping"));
        }

        [Test]
        public void DecodeEscapedMemberKeyRoutesLikePlainKey()
        {
            EnvelopeEvent ev = EnvelopeReader.Decode(
                Encoding.UTF8.GetBytes("{\"\\u0074ype\":\"Ping\"}")
            );

            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.Message));
            Assert.That(ev.Message, Is.EqualTo(MessageKind.Ping));
        }

        [TestCase("{\"v\":2,\"type\":\"Ping\",\"future_root\": {\"a\":[1,2,{\"b\":null}]}}")]
        [TestCase(
            "{\"type\":\"Error\",\"data\":{\"message\":\"m\",\"error_code\":\"X\",\"future_field\":123}}"
        )]
        [TestCase("{\"data\":{},\"type\":\"Ping\"}")] // member order is not significant
        [TestCase("  {  \"type\"  :  \"Pong\"  }  ")] // insignificant whitespace
        [TestCase("{\"type\":\"Pong\",\"data\":null}")] // null data tolerated as absent
        public void DecodeAdditiveWireShapesAreTolerated(string wire)
        {
            EnvelopeEvent ev = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(wire));

            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.Message), wire);
        }

        [Test]
        public void DecodeDuplicateTypeMemberFirstOccurrenceWins()
        {
            EnvelopeEvent ev = EnvelopeReader.Decode(
                Encoding.UTF8.GetBytes("{\"type\":\"Ping\",\"type\":\"Pong\"}")
            );

            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.Message));
            Assert.That(ev.Message, Is.EqualTo(MessageKind.Ping));
        }

        [TestCase(61, EnvelopeEventKind.Message)]
        [TestCase(62, EnvelopeEventKind.Message)]
        [TestCase(63, EnvelopeEventKind.DecodeFailed)]
        public void DecodeDataPayloadNestingEnforcesDepthBound(
            int arrayDepth,
            EnvelopeEventKind expectedKind
        )
        {
            /*
                Root(1) + data(2) + array levels: level k is scanned at depth
                2+k, so the bound accepts 62 array levels and rejects 63.
            */
            string wire =
                "{\"type\":\"Ping\",\"data\":{\"x\":"
                + new string('[', arrayDepth)
                + new string(']', arrayDepth)
                + "}}";

            EnvelopeEvent ev = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(wire));

            Assert.That(ev.Kind, Is.EqualTo(expectedKind), wire);
            if (expectedKind == EnvelopeEventKind.DecodeFailed)
            {
                Assert.That(ev.Error, Is.EqualTo(DecodeError.DepthExceeded));
            }
        }

        [TestCase(62, EnvelopeEventKind.Message)]
        [TestCase(63, EnvelopeEventKind.Message)]
        [TestCase(64, EnvelopeEventKind.DecodeFailed)]
        public void DecodeRootMemberNestingEnforcesDepthBound(
            int arrayDepth,
            EnvelopeEventKind expectedKind
        )
        {
            /*
                Root(1) + direct array levels: level k is scanned at depth
                1+k, so the bound accepts 63 array levels and rejects 64.
            */
            string wire =
                "{\"type\":\"Ping\",\"x\":"
                + new string('[', arrayDepth)
                + new string(']', arrayDepth)
                + "}";

            EnvelopeEvent ev = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(wire));

            Assert.That(ev.Kind, Is.EqualTo(expectedKind), wire);
            if (expectedKind == EnvelopeEventKind.DecodeFailed)
            {
                Assert.That(ev.Error, Is.EqualTo(DecodeError.DepthExceeded));
            }
        }

        // --- Malformed input: bounded DecodeFailed, never a throw -----------

        [Test]
        public void DecodeEmptyTypeFailsAtTheTypeStringOffset()
        {
            EnvelopeEvent ev = EnvelopeReader.Decode(Encoding.UTF8.GetBytes("{\"type\":\"\"}"));

            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.DecodeFailed));
            Assert.That(ev.Error, Is.EqualTo(DecodeError.EmptyType));
            Assert.That(
                ev.ErrorOffset,
                Is.EqualTo(8),
                "the offset must point at the type string, not the frame root."
            );
        }

        [Test, TestCaseSource(nameof(MalformedCorpus))]
        public void DecodeMalformedInputYieldsBoundedDecodeFailedEvent(
            string name,
            byte[] wire,
            DecodeError expectedError
        )
        {
            EnvelopeEvent ev = default;
            Assert.That((Action)(() => ev = EnvelopeReader.Decode(wire)), Throws.Nothing, name);

            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.DecodeFailed), name);
            Assert.That(ev.Error, Is.EqualTo(expectedError), name);
            Assert.That(ev.ErrorOffset, Is.InRange(0, Math.Max(0, wire.Length)), name);
            Assert.That(ev.Message, Is.EqualTo(default(MessageKind)), name);
        }

        private static IEnumerable<TestCaseData> MalformedCorpus()
        {
            IEnumerable<TestCaseData> Case(string name, string wire, DecodeError error)
            {
                yield return new TestCaseData(
                    name,
                    Encoding.UTF8.GetBytes(wire),
                    error
                ).SetArgDisplayNames(name, "wire", error.ToString());
            }

            foreach (TestCaseData c in Case("empty input", "", DecodeError.Truncated))
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "utf8 BOM prefix",
                    "\uFEFF{\"type\":\"Ping\"}",
                    DecodeError.NotAnObject
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case("truncated object", "{\"type\":\"Pi", DecodeError.Truncated)
            )
                yield return c;
            foreach (TestCaseData c in Case("root is array", "[]", DecodeError.NotAnObject))
                yield return c;
            foreach (TestCaseData c in Case("root is string", "\"Ping\"", DecodeError.NotAnObject))
                yield return c;
            foreach (TestCaseData c in Case("root is number", "42", DecodeError.NotAnObject))
                yield return c;
            foreach (
                TestCaseData c in Case("missing type", "{\"data\":{}}", DecodeError.MissingType)
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "type not a string",
                    "{\"type\":7}",
                    DecodeError.TypeNotString
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case("type is null", "{\"type\":null}", DecodeError.TypeNotString)
            )
                yield return c;
            foreach (TestCaseData c in Case("empty type", "{\"type\":\"\"}", DecodeError.EmptyType))
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "data not an object",
                    "{\"type\":\"Ping\",\"data\":7}",
                    DecodeError.DataNotObject
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "data is array",
                    "{\"type\":\"Ping\",\"data\":[]}",
                    DecodeError.DataNotObject
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "trailing content",
                    "{\"type\":\"Ping\"} {}",
                    DecodeError.TrailingContent
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "trailing garbage",
                    "{\"type\":\"Ping\"}x",
                    DecodeError.TrailingContent
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case("unquoted key", "{type:\"Ping\"}", DecodeError.InvalidToken)
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "single-quoted key",
                    "{'type':'Ping'}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "missing colon",
                    "{\"type\" \"Ping\"}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case("missing value", "{\"type\":}", DecodeError.TypeNotString)
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "bare word value",
                    "{\"type\":\"Ping\",\"x\":bare}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "nan literal",
                    "{\"type\":\"Ping\",\"x\":NaN}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "leading zero number",
                    "{\"type\":\"Ping\",\"x\":01}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "dangling decimal",
                    "{\"type\":\"Ping\",\"x\":1.}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "plus-sign number",
                    "{\"type\":\"Ping\",\"x\":+1}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "dangling exponent",
                    "{\"type\":\"Ping\",\"x\":1e}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "bad literal",
                    "{\"type\":\"Ping\",\"x\":tru}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "unterminated object",
                    "{\"type\":\"Ping\"",
                    DecodeError.Truncated
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "unterminated data",
                    "{\"type\":\"Ping\",\"data\":{\"a\":1}",
                    DecodeError.Truncated
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "stray close brace",
                    "{\"type\":\"Ping\"}}",
                    DecodeError.TrailingContent
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "bad escape",
                    "{\"type\":\"Pi\\xng\"}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "bad unicode escape",
                    "{\"type\":\"Pi\\uZZZZ\"}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "truncated unicode escape",
                    "{\"type\":\"Pi\\u00\"}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;
            foreach (
                TestCaseData c in Case(
                    "unescaped control char",
                    "{\"type\":\"Pi\ng\"}",
                    DecodeError.InvalidToken
                )
            )
                yield return c;

            byte[] invalidUtf8 = Encoding.UTF8.GetBytes("{\"type\":\"Ping\"}");
            invalidUtf8[9] = 0xFF; // lone continuation byte inside a string value
            yield return new TestCaseData(
                "invalid utf8 in string",
                invalidUtf8,
                DecodeError.InvalidToken
            ).SetArgDisplayNames(
                "invalid utf8 in string",
                "wire",
                DecodeError.InvalidToken.ToString()
            );

            // Every malformed UTF-8 class, injected as a string value.
            byte[] WithBytes(params byte[] injected)
            {
                byte[] prefix = Encoding.UTF8.GetBytes("{\"type\":\"Ping\",\"x\":\"");
                byte[] suffix = Encoding.UTF8.GetBytes("\"}");
                byte[] wire = new byte[prefix.Length + injected.Length + suffix.Length];
                prefix.CopyTo(wire, 0);
                injected.CopyTo(wire, prefix.Length);
                suffix.CopyTo(wire, prefix.Length + injected.Length);
                return wire;
            }

            yield return new TestCaseData(
                "utf8 overlong 2-byte",
                WithBytes(0xC0, 0x80),
                DecodeError.InvalidToken
            ).SetArgDisplayNames(
                "utf8 overlong 2-byte",
                "wire",
                DecodeError.InvalidToken.ToString()
            );
            yield return new TestCaseData(
                "utf8 overlong 3-byte",
                WithBytes(0xE0, 0x80, 0x80),
                DecodeError.InvalidToken
            ).SetArgDisplayNames(
                "utf8 overlong 3-byte",
                "wire",
                DecodeError.InvalidToken.ToString()
            );
            yield return new TestCaseData(
                "utf8 C1 lead",
                WithBytes(0xC1, 0x80),
                DecodeError.InvalidToken
            ).SetArgDisplayNames("utf8 C1 lead", "wire", DecodeError.InvalidToken.ToString());
            yield return new TestCaseData(
                "utf8 encoded surrogate",
                WithBytes(0xED, 0xA0, 0x80),
                DecodeError.InvalidToken
            ).SetArgDisplayNames(
                "utf8 encoded surrogate",
                "wire",
                DecodeError.InvalidToken.ToString()
            );
            yield return new TestCaseData(
                "utf8 beyond U+10FFFF",
                WithBytes(0xF4, 0x90, 0x80, 0x80),
                DecodeError.InvalidToken
            ).SetArgDisplayNames(
                "utf8 beyond U+10FFFF",
                "wire",
                DecodeError.InvalidToken.ToString()
            );
            yield return new TestCaseData(
                "utf8 F5 lead",
                WithBytes(0xF5, 0x80, 0x80, 0x80),
                DecodeError.InvalidToken
            ).SetArgDisplayNames("utf8 F5 lead", "wire", DecodeError.InvalidToken.ToString());
            yield return new TestCaseData(
                "utf8 FF lead",
                WithBytes(0xFF),
                DecodeError.InvalidToken
            ).SetArgDisplayNames("utf8 FF lead", "wire", DecodeError.InvalidToken.ToString());
            /*
                Sequence truncated by end of frame: rejected at the lead byte
                (strictness decision: any malformed UTF-8 is InvalidToken).
            */
            byte[] truncatedAtEof = Encoding.UTF8.GetBytes("{\"type\":\"Ping\",\"x\":\"");
            byte[] withTruncatedSeq = new byte[truncatedAtEof.Length + 2];
            truncatedAtEof.CopyTo(withTruncatedSeq, 0);
            withTruncatedSeq[truncatedAtEof.Length] = 0xE4;
            withTruncatedSeq[truncatedAtEof.Length + 1] = 0xB8;
            yield return new TestCaseData(
                "utf8 truncated sequence",
                withTruncatedSeq,
                DecodeError.InvalidToken
            ).SetArgDisplayNames(
                "utf8 truncated sequence",
                "wire",
                DecodeError.InvalidToken.ToString()
            );

            yield return new TestCaseData(
                "depth exceeded",
                Encoding.UTF8.GetBytes(
                    "{\"type\":\"Ping\",\"x\":" + new string('[', 100) + new string(']', 100) + "}"
                ),
                DecodeError.DepthExceeded
            ).SetArgDisplayNames("depth exceeded", "wire", DecodeError.DepthExceeded.ToString());
        }
    }
}
