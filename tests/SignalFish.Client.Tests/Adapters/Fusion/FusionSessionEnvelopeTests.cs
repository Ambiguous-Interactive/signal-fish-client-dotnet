namespace SignalFish.Client.Tests.Adapters.Fusion
{
    using System;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Adapters.Fusion;

    /// <summary>
    /// Contract coverage for the Fusion session-name envelope: an exact
    /// quoted-property shape that survives a shared game-data lane
    /// (foreign payloads decode as absent, unknown fields are ignored),
    /// the conservative session-name charset, and byte-exact write
    /// bounds.
    /// </summary>
    [TestFixture]
    public class FusionSessionEnvelopeTests
    {
        [Test]
        public void WriteThenReadRoundTripsTheSessionName()
        {
            byte[] buffer = new byte[FusionSessionEnvelope.MaxEnvelopeLength];

            Assert.That(
                FusionSessionEnvelope.TryWrite("9fdK2_x-Z0", buffer, out int written),
                Is.True
            );
            Assert.That(written, Is.EqualTo(33 + 10));

            Assert.That(
                FusionSessionEnvelope.TryRead(buffer.AsSpan(0, written), out string sessionName),
                Is.True
            );
            Assert.That(sessionName, Is.EqualTo("9fdK2_x-Z0"));
        }

        [Test]
        public void WrittenEnvelopeIsTheDocumentedShape()
        {
            byte[] buffer = new byte[FusionSessionEnvelope.MaxEnvelopeLength];

            Assert.That(FusionSessionEnvelope.TryWrite("ABC", buffer, out int written), Is.True);

            Assert.That(
                Encoding.UTF8.GetString(buffer, 0, written),
                Is.EqualTo(
                    "{ \"signal_fish_fusion_session\": \"ABC\" }".Replace(
                        " ",
                        string.Empty,
                        StringComparison.Ordinal
                    )
                )
            );
        }

        [Test]
        public void ReadIgnoresForeignGamePayloads()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"player_position\":{\"x\":1.5,\"y\":-2}}"
            );

            Assert.That(FusionSessionEnvelope.TryRead(payload, out string sessionName), Is.False);
            Assert.That(sessionName, Is.Empty);
        }

        [Test]
        public void ReadToleratesUnknownSurroundingFields()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"region\":\"us-west\",\"signal_fish_fusion_session\":\"AB12\",\"extra\":null}"
            );

            Assert.That(FusionSessionEnvelope.TryRead(payload, out string sessionName), Is.True);
            Assert.That(sessionName, Is.EqualTo("AB12"));
        }

        [Test]
        public void ReadAcceptsWhitespaceAroundTheValue()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{ \"signal_fish_fusion_session\" : \"AB12\" }"
            );

            Assert.That(FusionSessionEnvelope.TryRead(payload, out string sessionName), Is.True);
            Assert.That(sessionName, Is.EqualTo("AB12"));
        }

        [Test]
        public void ReadRejectsValuesOutsideTheSessionNameCharset()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_fusion_session\":\"AB\\\"CD\"}"
            );

            Assert.That(FusionSessionEnvelope.TryRead(payload, out _), Is.False);
        }

        [Test]
        public void ReadDoesNotMatchALongerKeyWithTheSamePrefix()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_fusion_session_v2\":\"AB12\"}"
            );

            Assert.That(FusionSessionEnvelope.TryRead(payload, out _), Is.False);
        }

        [Test]
        public void WriteRefusesSessionNamesOutsideTheContract()
        {
            byte[] buffer = new byte[FusionSessionEnvelope.MaxEnvelopeLength];

            Assert.That(FusionSessionEnvelope.TryWrite(string.Empty, buffer, out _), Is.False);
            Assert.That(FusionSessionEnvelope.TryWrite(null!, buffer, out _), Is.False);
            Assert.That(FusionSessionEnvelope.TryWrite("has space", buffer, out _), Is.False);
            Assert.That(FusionSessionEnvelope.TryWrite("quote\"name", buffer, out _), Is.False);
            Assert.That(
                FusionSessionEnvelope.TryWrite(
                    new string('a', FusionSessionEnvelope.MaxSessionNameLength + 1),
                    buffer,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void WriteAcceptsTheFullContractSurface()
        {
            byte[] buffer = new byte[FusionSessionEnvelope.MaxEnvelopeLength];

            Assert.That(FusionSessionEnvelope.TryWrite("room-1_x", buffer, out _), Is.True);
            Assert.That(
                FusionSessionEnvelope.TryWrite(
                    new string('a', FusionSessionEnvelope.MaxSessionNameLength),
                    buffer,
                    out int written
                ),
                Is.True
            );
            Assert.That(written, Is.EqualTo(FusionSessionEnvelope.MaxEnvelopeLength));
        }

        [Test]
        public void WriteRefusesAShortBuffer()
        {
            Assert.That(
                FusionSessionEnvelope.TryWrite("AB", Span<byte>.Empty, out int written),
                Is.False
            );
            Assert.That(written, Is.EqualTo(0));
        }

        [Test]
        public void ReadAcceptsAFusionGuidSessionName()
        {
            /*
                Fusion generates GUID session names when a host names
                none; the hyphens are inside the envelope's charset.
            */
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_fusion_session\":\"b2f7dd18-4a5e-4c1b-9f3a-7c2e8d901234\"}"
            );

            Assert.That(FusionSessionEnvelope.TryRead(payload, out string sessionName), Is.True);
            Assert.That(sessionName, Is.EqualTo("b2f7dd18-4a5e-4c1b-9f3a-7c2e8d901234"));
        }

        [Test]
        public void ReadReturnsTheFirstCompleteEnvelope()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_fusion_session\":\"broken,\"signal_fish_fusion_session\":\"ok\"}"
            );

            Assert.That(FusionSessionEnvelope.TryRead(payload, out string sessionName), Is.True);
            Assert.That(sessionName, Is.EqualTo("ok"));
        }
    }
}
