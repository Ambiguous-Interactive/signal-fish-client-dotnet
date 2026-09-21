namespace SignalFish.Client.Tests
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using FsCheck;
    using NUnit.Framework;
    using SignalFish.Client.Protocol;
    using PropertyAttribute = FsCheck.NUnit.PropertyAttribute;

    /// <summary>
    /// FsCheck property suite for the hand-rolled codec (PLAN.md M1.4):
    /// escape-aware string write→materialize roundtrips, value-scan
    /// stability over writer output, envelope decode totality (no input,
    /// however malformed, can throw), and known-type routing under arbitrary
    /// legal payloads. Generators are escape-heavy on purpose: quotes,
    /// backslashes, control characters, and non-ASCII text are the codec's
    /// hard cases.
    /// </summary>
    [TestFixture]
    public class CodecPropertyTests
    {
        private static readonly string[] PinnedFixtureFiles =
        {
            "v2-client-messages.jsonl",
            "v2-server-messages.jsonl",
            "v3-client-messages.jsonl",
            "v3-server-messages.jsonl",
        };

        /// <summary>Structural characters and junk bytes for mutation gens.</summary>
        private static readonly byte[] BytePool =
        {
            (byte)'{',
            (byte)'}',
            (byte)'[',
            (byte)']',
            (byte)':',
            (byte)',',
            (byte)'"',
            (byte)'\\',
            (byte)'a',
            (byte)' ',
            0x00,
            0x7F,
            0x80,
            0xFF,
        };

        /// <summary>Escape- and non-ASCII-heavy characters. Lone surrogates are excluded: the writer replaces them with U+FFFD by design.</summary>
        private static readonly char[] CharPool =
        {
            'a',
            'Z',
            '0',
            '9',
            ' ',
            '_',
            '{',
            '"',
            '\\',
            '/',
            '\b',
            '\f',
            '\n',
            '\r',
            '\t',
            '\0',
            'é',
            '中',
        };

        /// <summary>Complete surrogate pairs the writer encodes as 4-byte UTF-8.</summary>
        private static readonly string[] AstralStrings = { "\uD834\uDD1E", "\U0001F600" };

        private static readonly byte[] OpenEnvelope = Encoding.ASCII.GetBytes("{\"type\": \"");
        private static readonly byte[] OpenEnvelopeNoQuote = Encoding.ASCII.GetBytes("{\"type\": ");
        private static readonly byte[] TypeDataSeparator = Encoding.ASCII.GetBytes(
            "\", \"data\": "
        );
        private static readonly byte[] JsonCommaSpace = { (byte)',', (byte)' ' };
        private static readonly Gen<MessageKind> KnownKindGen = Gen.Elements(DefinedKinds());
        private static byte[][] corpusFrames = Array.Empty<byte[]>();

        [OneTimeSetUp]
        public void LoadCorpus()
        {
            string goldenDirectory = Path.Combine(AppContext.BaseDirectory, "Golden");
            List<byte[]> frames = new List<byte[]>();
            foreach (string fileName in PinnedFixtureFiles)
            {
                string[] lines = File.ReadAllLines(Path.Combine(goldenDirectory, fileName));
                foreach (string line in lines)
                {
                    frames.Add(Encoding.UTF8.GetBytes(line));
                }
            }

            corpusFrames = frames.ToArray();
            Assert.That(corpusFrames.Length, Is.GreaterThan(0), "Golden corpus must be present.");
        }

        // ---- Escape-aware string roundtrip ----------------------------------

        [Property(MaxTest = 400, QuietOnSuccess = true)]
        public Property Materialize_Write_ArbitraryString_Identity()
        {
            return Prop.ForAll(
                Arb.From(TextGen),
                value =>
                {
                    byte[] rendered = RenderString(value);
                    JsonScanner scanner = new JsonScanner(rendered);
                    DecodeError err = scanner.ScanStringRaw(out Range raw, out bool hasEscapes);
                    (int offset, int length) = raw.GetOffsetAndLength(rendered.Length);
                    if (err != default(DecodeError) || offset != 0 || length != rendered.Length)
                    {
                        return false;
                    }

                    string materialized = JsonScanner.MaterializeString(
                        rendered.AsSpan(1, length - 2),
                        hasEscapes
                    );
                    return materialized == value;
                }
            );
        }

        // ---- Value-scan stability over writer output -------------------------

        [Property(MaxTest = 250, QuietOnSuccess = true)]
        public Property Scan_RenderedValue_IsByteStable()
        {
            return Prop.ForAll(
                Arb.From(AnyJson),
                value =>
                {
                    byte[] rendered = RenderValue(value);
                    JsonScanner scanner = new JsonScanner(rendered);
                    DecodeError err = scanner.ScanValueRaw(
                        1,
                        EnvelopeReader.MaxDepth,
                        out Range raw
                    );
                    (int offset, int length) = raw.GetOffsetAndLength(rendered.Length);
                    return err == default(DecodeError) && offset == 0 && length == rendered.Length;
                }
            );
        }

        // ---- Envelope decode totality ----------------------------------------

        [Property(MaxTest = 1000, QuietOnSuccess = true)]
        public Property Decode_ArbitraryBytes_NeverThrows()
        {
            return Prop.ForAll(
                Arb.From(Gen.ArrayOf(Gen.Elements(BytePool))),
                bytes => DecodeIsTotal(bytes)
            );
        }

        [Property(MaxTest = 600, QuietOnSuccess = true)]
        public Property Decode_MutatedCorpusFrame_NeverThrows()
        {
            return Prop.ForAll(
                Arb.From(Gen.Choose(0, corpusFrames.Length - 1)),
                Arb.From(Gen.Choose(0, 63)),
                Arb.From(Gen.Elements(BytePool)),
                (frameIndex, position, value) =>
                {
                    byte[] frame = corpusFrames[frameIndex];
                    byte[] mutated = (byte[])frame.Clone();
                    mutated[position % frame.Length] = value;
                    return DecodeIsTotal(mutated);
                }
            );
        }

        // ---- Known-type routing under arbitrary legal payloads ----------------

        [Property(MaxTest = 150, QuietOnSuccess = true)]
        public Property Decode_KnownType_ArbitraryObjectPayload_Routes()
        {
            return Prop.ForAll(
                Arb.From(KnownKindGen),
                Arb.From(AnyObject),
                (kind, payload) =>
                {
                    string? wireName = MessageKindNames.ToWireName(kind);
                    byte[] data = RenderValue(payload);
                    byte[] frame = ComposeEnvelope(Encoding.ASCII.GetBytes(wireName!), data);
                    EnvelopeEvent ev = EnvelopeReader.Decode(frame);
                    return ev.Kind == EnvelopeEventKind.Message
                        && ev.Message == kind
                        && ev.Error == default(DecodeError)
                        && ev.TypeText is null
                        && ev.Data.Span.SequenceEqual(data);
                }
            );
        }

        [Property(MaxTest = 150, QuietOnSuccess = true)]
        public Property Decode_UnknownType_SurfacesForwardCompatibleEvent()
        {
            return Prop.ForAll(
                Arb.From(TextGen),
                typeName =>
                {
                    ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
                    JsonWriter writer = new JsonWriter(buffer);
                    writer.WriteBytes(OpenEnvelopeNoQuote);
                    writer.WriteString("Zz" + typeName);
                    writer.WriteByte((byte)'}');
                    writer.Flush();
                    byte[] frame = buffer.WrittenSpan.ToArray();
                    EnvelopeEvent ev = EnvelopeReader.Decode(frame);
                    return ev.Kind == EnvelopeEventKind.UnknownMessage
                        && ev.Message == default(MessageKind)
                        && ev.Error == default(DecodeError)
                        && ev.TypeText == "Zz" + typeName
                        && ev.Data.IsEmpty;
                }
            );
        }

        // ---- Helpers -----------------------------------------------------------

        private static bool DecodeIsTotal(byte[] frame)
        {
            EnvelopeEvent ev = EnvelopeReader.Decode(frame);
            return ev.Kind switch
            {
                EnvelopeEventKind.Message => ev.Error == default(DecodeError)
                    && ev.Message != default(MessageKind)
                    && ev.TypeText is null,
                EnvelopeEventKind.UnknownMessage => ev.Error == default(DecodeError)
                    && ev.Message == default(MessageKind)
                    && ev.TypeText is not null,
                EnvelopeEventKind.DecodeFailed => ev.Error != default(DecodeError)
                    && ev.ErrorOffset >= 0
                    && ev.ErrorOffset <= frame.Length,
                _ => false,
            };
        }

        private static byte[] ComposeEnvelope(byte[] typeName, byte[]? data)
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
            JsonWriter writer = new JsonWriter(buffer);
            writer.WriteBytes(OpenEnvelope);
            writer.WriteBytes(typeName);
            if (data is not null)
            {
                writer.WriteBytes(TypeDataSeparator);
                writer.WriteBytes(data);
            }

            writer.WriteByte((byte)'}');
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static byte[] RenderString(string value)
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
            JsonWriter writer = new JsonWriter(buffer);
            writer.WriteString(value);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static byte[] RenderValue(JNode node)
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
            JsonWriter writer = new JsonWriter(buffer);
            Render(ref writer, node);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static void Render(ref JsonWriter writer, JNode node)
        {
            switch (node.Kind)
            {
                case JKind.Null:
                    writer.WriteNull();
                    break;
                case JKind.Bool:
                    writer.WriteBoolean(node.Bool);
                    break;
                case JKind.Number:
                    writer.WriteUInt32(node.Number);
                    break;
                case JKind.Str:
                    writer.WriteString(node.Text);
                    break;
                case JKind.Arr:
                    writer.WriteByte((byte)'[');
                    for (int i = 0; i < node.Items.Count; i++)
                    {
                        if (i > 0)
                        {
                            writer.WriteBytes(JsonCommaSpace);
                        }

                        Render(ref writer, node.Items[i]);
                    }

                    writer.WriteByte((byte)']');
                    break;
                case JKind.Obj:
                    writer.WriteByte((byte)'{');
                    for (int i = 0; i < node.Members.Count; i++)
                    {
                        bool first = i == 0;
                        writer.WriteMemberSeparator(ref first);
                        writer.WriteString(node.Members[i].Key);
                        writer.WriteByte((byte)':');
                        writer.WriteByte((byte)' ');
                        Render(ref writer, node.Members[i].Value);
                    }

                    writer.WriteByte((byte)'}');
                    break;
                default:
                    throw new InvalidOperationException(
                        "Undefined JKind value: " + (byte)node.Kind
                    );
            }
        }

        private static JNode AsObject(JNode node) =>
            node.Kind == JKind.Obj ? node : new JNode { Kind = JKind.Obj };

        private static List<JNode> Bound(JNode[] items)
        {
            List<JNode> list = new List<JNode>();
            for (int i = 0; i < Math.Min(items.Length, 8); i++)
            {
                list.Add(items[i]);
            }

            return list;
        }

        private static List<KeyValuePair<string, JNode>> Pair(string[] keys, JNode[] values)
        {
            List<KeyValuePair<string, JNode>> members = new List<KeyValuePair<string, JNode>>();
            int count = Math.Min(Math.Min(keys.Length, values.Length), 8);
            for (int i = 0; i < count; i++)
            {
                members.Add(new KeyValuePair<string, JNode>(keys[i], values[i]));
            }

            return members;
        }

        private static Gen<string> TextGen =>
            Gen.OneOf(
                Gen.ArrayOf(Gen.Elements(CharPool))
                    .Select(chars => new string(chars, 0, Math.Min(chars.Length, 24))),
                Gen.Elements(AstralStrings)
            );

        private static Gen<JNode> ScalarGen =>
            Gen.OneOf(
                Gen.Constant(new JNode { Kind = JKind.Null }),
                Gen.Choose(0, 1).Select(b => new JNode { Kind = JKind.Bool, Bool = b == 1 }),
                Gen.Choose(0, int.MaxValue)
                    .Select(n => new JNode { Kind = JKind.Number, Number = (uint)n }),
                TextGen.Select(text => new JNode { Kind = JKind.Str, Text = text })
            );

        private static Gen<JNode> AnyJson => Gen.Sized(depth => JsonGen(Math.Min(depth, 3)));

        private static Gen<JNode> AnyObject => AnyJson.Select(AsObject);

        private static Gen<JNode> JsonGen(int depth)
        {
            if (depth <= 0)
            {
                return ScalarGen;
            }

            return Gen.OneOf(
                ScalarGen,
                Gen.ArrayOf(JsonGen(depth - 1))
                    .Select(items => new JNode { Kind = JKind.Arr, Items = Bound(items) }),
                (
                    from keys in Gen.ArrayOf(TextGen)
                    from values in Gen.ArrayOf(JsonGen(depth - 1))
                    select new JNode { Kind = JKind.Obj, Members = Pair(keys, values) }
                )
            );
        }

        private static MessageKind[] DefinedKinds()
        {
            List<MessageKind> kinds = new List<MessageKind>();
            foreach (MessageKind kind in Enum.GetValues<MessageKind>())
            {
                if (kind != default(MessageKind))
                {
                    kinds.Add(kind);
                }
            }

            return kinds.ToArray();
        }

        /// <summary>JSON AST node produced by the codec generators.</summary>
        internal sealed class JNode
        {
            internal JKind Kind { get; set; }

            internal bool Bool { get; set; }

            internal uint Number { get; set; }

            internal string Text { get; set; } = string.Empty;

            internal List<JNode> Items { get; set; } = new List<JNode>();

            internal List<KeyValuePair<string, JNode>> Members { get; set; } =
                new List<KeyValuePair<string, JNode>>();
        }

        internal enum JKind : byte
        {
            /// <summary>Sentinel for <c>default(JKind)</c>; not a valid JSON kind.</summary>
            [Obsolete(
                "This value only exists so the enum default (0) is not a JSON kind. Construct JNode with an explicit Kind."
            )]
            None = 0,

            Null = 1,
            Bool = 2,
            Number = 3,
            Str = 4,
            Arr = 5,
            Obj = 6,
        }
    }
}
