namespace SignalFish.Client.Tests.Adapters.Facepunch
{
    using System;
    using System.Globalization;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Adapters.Facepunch;

    /// <summary>
    /// Contract coverage for the Steam identity envelope: two
    /// role-scoped keys (host and peer) whose values are SteamId64
    /// decimal strings that survive a shared game-data lane (foreign
    /// payloads decode as absent, unknown fields are ignored), the
    /// conservative id charset, and byte-exact write bounds.
    /// </summary>
    [TestFixture]
    public class SteamIdentityEnvelopeTests
    {
        [Test]
        public void WriteThenReadRoundTripsTheHostId()
        {
            byte[] buffer = new byte[SteamIdentityEnvelope.MaxEnvelopeLength];

            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    "76561197960287930",
                    buffer,
                    out int written
                ),
                Is.True
            );
            Assert.That(written, Is.EqualTo(29 + 17));

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    buffer.AsSpan(0, written),
                    SteamIdentityEnvelope.HostPropertyName,
                    out string steamId
                ),
                Is.True
            );
            Assert.That(steamId, Is.EqualTo("76561197960287930"));
        }

        [Test]
        public void WriteThenReadRoundTripsThePeerId()
        {
            byte[] buffer = new byte[SteamIdentityEnvelope.MaxEnvelopeLength];

            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.PeerPropertyName,
                    "76561198000000001",
                    buffer,
                    out int written
                ),
                Is.True
            );

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    buffer.AsSpan(0, written),
                    SteamIdentityEnvelope.PeerPropertyName,
                    out string steamId
                ),
                Is.True
            );
            Assert.That(steamId, Is.EqualTo("76561198000000001"));
        }

        [Test]
        public void WrittenEnvelopeIsTheDocumentedShape()
        {
            byte[] buffer = new byte[SteamIdentityEnvelope.MaxEnvelopeLength];

            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    "123",
                    buffer,
                    out int written
                ),
                Is.True
            );

            Assert.That(
                Encoding.UTF8.GetString(buffer, 0, written),
                Is.EqualTo(
                    "{ \"signal_fish_steam_host\": \"123\" }".Replace(
                        " ",
                        string.Empty,
                        StringComparison.Ordinal
                    )
                )
            );
        }

        [Test]
        public void HostAndPeerKeysDoNotCrossDecode()
        {
            byte[] buffer = new byte[SteamIdentityEnvelope.MaxEnvelopeLength];

            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    "123",
                    buffer,
                    out int written
                ),
                Is.True
            );

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    buffer.AsSpan(0, written),
                    SteamIdentityEnvelope.PeerPropertyName,
                    out string steamId
                ),
                Is.False
            );
            Assert.That(steamId, Is.Empty);
        }

        [Test]
        public void ReadIgnoresForeignGamePayloads()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"player_position\":{\"x\":1.5,\"y\":-2}}"
            );

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    payload,
                    SteamIdentityEnvelope.HostPropertyName,
                    out string steamId
                ),
                Is.False
            );
            Assert.That(steamId, Is.Empty);
        }

        [Test]
        public void ReadToleratesUnknownSurroundingFields()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"region\":\"us-west\",\"signal_fish_steam_host\":\"76561197960287930\",\"extra\":null}"
            );

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    payload,
                    SteamIdentityEnvelope.HostPropertyName,
                    out string steamId
                ),
                Is.True
            );
            Assert.That(steamId, Is.EqualTo("76561197960287930"));
        }

        [Test]
        public void ReadAcceptsWhitespaceAroundTheValue()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{ \"signal_fish_steam_peer\" : \"76561197960287930\" }"
            );

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    payload,
                    SteamIdentityEnvelope.PeerPropertyName,
                    out string steamId
                ),
                Is.True
            );
            Assert.That(steamId, Is.EqualTo("76561197960287930"));
        }

        [Test]
        public void ReadRejectsValuesOutsideTheIdCharset()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_steam_host\":\"7656119796x287930\"}"
            );

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    payload,
                    SteamIdentityEnvelope.HostPropertyName,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void ReadDoesNotMatchALongerKeyWithTheSamePrefix()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_steam_host_v2\":\"123\"}"
            );

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    payload,
                    SteamIdentityEnvelope.HostPropertyName,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void ReadRescansAfterAMalformedValue()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_steam_host\":\"1x3\",\"second\":{\"signal_fish_steam_host\":\"76561197960287930\"}}"
            );

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    payload,
                    SteamIdentityEnvelope.HostPropertyName,
                    out string steamId
                ),
                Is.True
            );
            Assert.That(steamId, Is.EqualTo("76561197960287930"));
        }

        [Test]
        public void ReadRejectsAnEmptyValue()
        {
            ReadOnlySpan<byte> payload = Encoding.UTF8.GetBytes(
                "{\"signal_fish_steam_host\":\"\"}"
            );

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    payload,
                    SteamIdentityEnvelope.HostPropertyName,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void WriteRefusesIdsOutsideTheContract()
        {
            byte[] buffer = new byte[SteamIdentityEnvelope.MaxEnvelopeLength];

            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    string.Empty,
                    buffer,
                    out _
                ),
                Is.False
            );
            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    null!,
                    buffer,
                    out _
                ),
                Is.False
            );
            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    "0",
                    buffer,
                    out _
                ),
                Is.False
            );
            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    "0123",
                    buffer,
                    out _
                ),
                Is.False
            );
            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    "7656119796x287930",
                    buffer,
                    out _
                ),
                Is.False
            );
            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    new string('9', SteamIdentityEnvelope.MaxSteamIdLength + 1),
                    buffer,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void WriteRefusesAnUnknownPropertyName()
        {
            byte[] buffer = new byte[SteamIdentityEnvelope.MaxEnvelopeLength];

            Assert.That(
                SteamIdentityEnvelope.TryWrite("signal_fish_steam_anyone", "123", buffer, out _),
                Is.False
            );
            Assert.That(
                SteamIdentityEnvelope.TryWrite(string.Empty, "123", buffer, out _),
                Is.False
            );
            Assert.That(SteamIdentityEnvelope.TryWrite(null!, "123", buffer, out _), Is.False);
        }

        [Test]
        public void WriteRefusesABufferBelowTheEnvelopeLength()
        {
            string steamId = new string('9', SteamIdentityEnvelope.MaxSteamIdLength);
            byte[] tooSmall = new byte[SteamIdentityEnvelope.MaxEnvelopeLength - 1];

            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.PeerPropertyName,
                    steamId,
                    tooSmall,
                    out int written
                ),
                Is.False
            );
            Assert.That(written, Is.EqualTo(0));
        }

        [Test]
        public void AcceptsTheFullUnsignedLongRange()
        {
            byte[] buffer = new byte[SteamIdentityEnvelope.MaxEnvelopeLength];

            Assert.That(
                SteamIdentityEnvelope.TryWrite(
                    SteamIdentityEnvelope.HostPropertyName,
                    ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
                    buffer,
                    out int written
                ),
                Is.True
            );
            Assert.That(written, Is.EqualTo(29 + 20));

            Assert.That(
                SteamIdentityEnvelope.TryRead(
                    buffer.AsSpan(0, written),
                    SteamIdentityEnvelope.HostPropertyName,
                    out string steamId
                ),
                Is.True
            );
            Assert.That(
                ulong.TryParse(
                    steamId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out ulong parsed
                )
                    && parsed == ulong.MaxValue,
                Is.True
            );
        }

        [Test]
        public void IsValidSteamIdTracksTheCharsetAndLengthBound()
        {
            Assert.That(SteamIdentityEnvelope.IsValidSteamId("76561197960287930"), Is.True);
            Assert.That(
                SteamIdentityEnvelope.IsValidSteamId(
                    new string('9', SteamIdentityEnvelope.MaxSteamIdLength)
                ),
                Is.True
            );
            Assert.That(SteamIdentityEnvelope.IsValidSteamId("0"), Is.False);
            Assert.That(SteamIdentityEnvelope.IsValidSteamId("0123"), Is.False);
            Assert.That(SteamIdentityEnvelope.IsValidSteamId("1x3"), Is.False);
            Assert.That(SteamIdentityEnvelope.IsValidSteamId(string.Empty), Is.False);
            Assert.That(SteamIdentityEnvelope.IsValidSteamId(null), Is.False);
        }
    }
}
