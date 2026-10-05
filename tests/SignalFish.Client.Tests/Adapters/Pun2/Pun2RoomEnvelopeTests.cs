namespace SignalFish.Client.Tests.Adapters.Pun2
{
    using System;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Adapters.Pun2;

    /// <summary>
    /// Contract coverage for the PUN2 room-name envelope: an exact
    /// quoted-property shape that survives a shared game-data lane
    /// (foreign payloads decode as absent, unknown fields are ignored),
    /// the conservative room-name charset, and byte-exact write bounds.
    /// </summary>
    [TestFixture]
    public class Pun2RoomEnvelopeTests
    {
        [Test]
        public void WriteThenReadRoundTripsTheRoomName()
        {
            byte[] buffer = new byte[Pun2RoomEnvelope.MaxEnvelopeLength];

            Assert.That(Pun2RoomEnvelope.TryWrite("9fdK2_x-Z0", buffer, out int written), Is.True);
            Assert.That(written, Is.EqualTo(28 + 10));

            Assert.That(
                Pun2RoomEnvelope.TryRead(buffer.AsSpan(0, written), out string roomName),
                Is.True
            );
            Assert.That(roomName, Is.EqualTo("9fdK2_x-Z0"));
        }

        [Test]
        public void WrittenEnvelopeIsTheDocumentedShape()
        {
            byte[] buffer = new byte[Pun2RoomEnvelope.MaxEnvelopeLength];

            Assert.That(Pun2RoomEnvelope.TryWrite("ABC", buffer, out int written), Is.True);

            Assert.That(
                Encoding.UTF8.GetString(buffer, 0, written),
                Is.EqualTo(
                    "{ \"signal_fish_pun2_room\": \"ABC\" }".Replace(
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

            Assert.That(Pun2RoomEnvelope.TryRead(payload, out string roomName), Is.False);
            Assert.That(roomName, Is.Empty);
        }

        [Test]
        public void ReadToleratesUnknownSurroundingFields()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"region\":\"us-west\",\"signal_fish_pun2_room\":\"AB12\",\"extra\":null}"
            );

            Assert.That(Pun2RoomEnvelope.TryRead(payload, out string roomName), Is.True);
            Assert.That(roomName, Is.EqualTo("AB12"));
        }

        [Test]
        public void ReadAcceptsWhitespaceAroundTheValue()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{ \"signal_fish_pun2_room\" : \"AB12\" }"
            );

            Assert.That(Pun2RoomEnvelope.TryRead(payload, out string roomName), Is.True);
            Assert.That(roomName, Is.EqualTo("AB12"));
        }

        [Test]
        public void ReadRejectsValuesOutsideTheRoomNameCharset()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_pun2_room\":\"AB\\\"CD\"}"
            );

            Assert.That(Pun2RoomEnvelope.TryRead(payload, out _), Is.False);
        }

        [Test]
        public void ReadDoesNotMatchALongerKeyWithTheSamePrefix()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_pun2_room_v2\":\"AB12\"}"
            );

            Assert.That(Pun2RoomEnvelope.TryRead(payload, out _), Is.False);
        }

        [Test]
        public void WriteRefusesRoomNamesOutsideTheContract()
        {
            byte[] buffer = new byte[Pun2RoomEnvelope.MaxEnvelopeLength];

            Assert.That(Pun2RoomEnvelope.TryWrite(string.Empty, buffer, out _), Is.False);
            Assert.That(Pun2RoomEnvelope.TryWrite(null!, buffer, out _), Is.False);
            Assert.That(Pun2RoomEnvelope.TryWrite("has space", buffer, out _), Is.False);
            Assert.That(Pun2RoomEnvelope.TryWrite("quote\"name", buffer, out _), Is.False);
            Assert.That(
                Pun2RoomEnvelope.TryWrite(
                    new string('a', Pun2RoomEnvelope.MaxRoomNameLength + 1),
                    buffer,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void WriteRefusesABufferBelowTheEnvelopeLength()
        {
            string roomName = new string('a', Pun2RoomEnvelope.MaxRoomNameLength);
            byte[] tooSmall = new byte[Pun2RoomEnvelope.MaxEnvelopeLength - 1];

            Assert.That(Pun2RoomEnvelope.TryWrite(roomName, tooSmall, out int written), Is.False);
            Assert.That(written, Is.EqualTo(0));
        }

        [Test]
        public void IsValidRoomNameTracksTheCharsetAndLengthBound()
        {
            Assert.That(Pun2RoomEnvelope.IsValidRoomName("A-z_9"), Is.True);
            Assert.That(
                Pun2RoomEnvelope.IsValidRoomName(
                    new string('A', Pun2RoomEnvelope.MaxRoomNameLength)
                ),
                Is.True
            );
            Assert.That(Pun2RoomEnvelope.IsValidRoomName("A.B"), Is.False);
            Assert.That(Pun2RoomEnvelope.IsValidRoomName(string.Empty), Is.False);
            Assert.That(Pun2RoomEnvelope.IsValidRoomName(null), Is.False);
        }

        [Test]
        public void ReadRescansAfterAMalformedValue()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_pun2_room\":\"A.B\",\"second\":{\"signal_fish_pun2_room\":\"OK12\"}}"
            );

            Assert.That(Pun2RoomEnvelope.TryRead(payload, out string roomName), Is.True);
            Assert.That(roomName, Is.EqualTo("OK12"));
        }
    }
}
