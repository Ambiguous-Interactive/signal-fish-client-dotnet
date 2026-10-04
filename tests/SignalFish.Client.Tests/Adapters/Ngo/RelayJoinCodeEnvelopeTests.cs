namespace SignalFish.Client.Tests.Adapters.Ngo
{
    using System;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Adapters.Ngo;

    /// <summary>
    /// Contract coverage for the relay join-code envelope: an exact
    /// quoted-property shape that survives a shared game-data lane
    /// (foreign payloads decode as absent, unknown fields are ignored),
    /// the conservative join-code charset, and byte-exact write bounds.
    /// </summary>
    [TestFixture]
    public class RelayJoinCodeEnvelopeTests
    {
        [Test]
        public void WriteThenReadRoundTripsTheJoinCode()
        {
            byte[] buffer = new byte[RelayJoinCodeEnvelope.MaxEnvelopeLength];

            Assert.That(
                RelayJoinCodeEnvelope.TryWrite("9fdK2_x-Z0", buffer, out int written),
                Is.True
            );
            Assert.That(written, Is.EqualTo(34 + 10));

            Assert.That(
                RelayJoinCodeEnvelope.TryRead(buffer.AsSpan(0, written), out string joinCode),
                Is.True
            );
            Assert.That(joinCode, Is.EqualTo("9fdK2_x-Z0"));
        }

        [Test]
        public void WrittenEnvelopeIsTheDocumentedShape()
        {
            byte[] buffer = new byte[RelayJoinCodeEnvelope.MaxEnvelopeLength];

            Assert.That(RelayJoinCodeEnvelope.TryWrite("ABC", buffer, out int written), Is.True);

            Assert.That(
                Encoding.UTF8.GetString(buffer, 0, written),
                Is.EqualTo(
                    "{ \"signal_fish_relay_join_code\": \"ABC\" }".Replace(
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

            Assert.That(RelayJoinCodeEnvelope.TryRead(payload, out string joinCode), Is.False);
            Assert.That(joinCode, Is.Empty);
        }

        [Test]
        public void ReadToleratesUnknownSurroundingFields()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"region\":\"us-west\",\"signal_fish_relay_join_code\":\"AB12\",\"extra\":null}"
            );

            Assert.That(RelayJoinCodeEnvelope.TryRead(payload, out string joinCode), Is.True);
            Assert.That(joinCode, Is.EqualTo("AB12"));
        }

        [Test]
        public void ReadAcceptsWhitespaceAroundTheValue()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{ \"signal_fish_relay_join_code\" : \"AB12\" }"
            );

            Assert.That(RelayJoinCodeEnvelope.TryRead(payload, out string joinCode), Is.True);
            Assert.That(joinCode, Is.EqualTo("AB12"));
        }

        [Test]
        public void ReadRejectsValuesOutsideTheJoinCodeCharset()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_relay_join_code\":\"AB\\\"CD\"}"
            );

            Assert.That(RelayJoinCodeEnvelope.TryRead(payload, out _), Is.False);
        }

        [Test]
        public void ReadDoesNotMatchALongerKeyWithTheSamePrefix()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_relay_join_code_v2\":\"AB12\"}"
            );

            Assert.That(RelayJoinCodeEnvelope.TryRead(payload, out _), Is.False);
        }

        [Test]
        public void WriteRefusesJoinCodesOutsideTheContract()
        {
            byte[] buffer = new byte[RelayJoinCodeEnvelope.MaxEnvelopeLength];

            Assert.That(RelayJoinCodeEnvelope.TryWrite(string.Empty, buffer, out _), Is.False);
            Assert.That(RelayJoinCodeEnvelope.TryWrite(null!, buffer, out _), Is.False);
            Assert.That(RelayJoinCodeEnvelope.TryWrite("has space", buffer, out _), Is.False);
            Assert.That(RelayJoinCodeEnvelope.TryWrite("quote\"code", buffer, out _), Is.False);
            Assert.That(
                RelayJoinCodeEnvelope.TryWrite(
                    new string('a', RelayJoinCodeEnvelope.MaxJoinCodeLength + 1),
                    buffer,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void WriteRefusesABufferBelowTheEnvelopeLength()
        {
            string joinCode = new string('a', RelayJoinCodeEnvelope.MaxJoinCodeLength);
            byte[] tooSmall = new byte[RelayJoinCodeEnvelope.MaxEnvelopeLength - 1];

            Assert.That(
                RelayJoinCodeEnvelope.TryWrite(joinCode, tooSmall, out int written),
                Is.False
            );
            Assert.That(written, Is.EqualTo(0));
        }

        [Test]
        public void IsValidJoinCodeTracksTheCharsetAndLengthBound()
        {
            Assert.That(RelayJoinCodeEnvelope.IsValidJoinCode("A-z_9"), Is.True);
            Assert.That(
                RelayJoinCodeEnvelope.IsValidJoinCode(
                    new string('A', RelayJoinCodeEnvelope.MaxJoinCodeLength)
                ),
                Is.True
            );
            Assert.That(RelayJoinCodeEnvelope.IsValidJoinCode("A.B"), Is.False);
            Assert.That(RelayJoinCodeEnvelope.IsValidJoinCode(string.Empty), Is.False);
            Assert.That(RelayJoinCodeEnvelope.IsValidJoinCode(null), Is.False);
        }

        [Test]
        public void ReadRescansAfterAMalformedValue()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_relay_join_code\":\"A.B\",\"second\":{\"signal_fish_relay_join_code\":\"OK12\"}}"
            );

            Assert.That(RelayJoinCodeEnvelope.TryRead(payload, out string joinCode), Is.True);
            Assert.That(joinCode, Is.EqualTo("OK12"));
        }
    }
}
