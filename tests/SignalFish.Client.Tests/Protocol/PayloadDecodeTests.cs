namespace SignalFish.Client.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Golden-fixture and negative decode coverage for the v2 payload
    /// structs (lobby, players, game start, spectators, failures, and the
    /// rich room snapshot). Each golden sample goes through
    /// <see cref="EnvelopeReader"/> first, so payload decode is exercised on
    /// real envelope slices of the pinned
    /// <c>tests/Golden/v2-server-messages.jsonl</c> bytes.
    /// </summary>
    [TestFixture]
    public class PayloadDecodeTests
    {
        [Test]
        public void AuthenticatedGoldenFixtureDecodesIdentityAndRateLimits()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("Authenticated");
            Assert.That(
                AuthenticatedMessage.TryDecode(envelope.Data, out AuthenticatedMessage message),
                Is.True
            );
            Assert.That(message.AppName, Is.EqualTo("my-game"));
            Assert.That(message.Organization, Is.EqualTo("Ambiguous Interactive"));
            Assert.That(message.RateLimits, Is.EqualTo(new RateLimits(60, 3600, 86400)));
        }

        [Test]
        public void ProtocolInfoGoldenFixtureDecodesCapabilityLists()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("ProtocolInfo");
            Assert.That(
                ProtocolInfoMessage.TryDecode(envelope.Data, out ProtocolInfoMessage message),
                Is.True
            );
            Assert.That(message.Capabilities, Has.Count.EqualTo(3));
            Assert.That(message.Capabilities[0], Is.EqualTo("reconnection"));
            Assert.That(message.Capabilities[1], Is.EqualTo("spectators"));
            Assert.That(message.Capabilities[2], Is.EqualTo("authority"));
            Assert.That(message.GameDataFormats, Has.Count.EqualTo(2));
            Assert.That(message.GameDataFormats[0], Is.EqualTo("json"));
            Assert.That(message.GameDataFormats[1], Is.EqualTo("message_pack"));
        }

        [Test]
        public void LobbyStateChangedGoldenFixtureDecodesLobbyState()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("LobbyStateChanged");
            Assert.That(
                LobbyStateChangedMessage.TryDecode(
                    envelope.Data,
                    out LobbyStateChangedMessage message
                ),
                Is.True
            );
            Assert.That(message.LobbyState, Is.EqualTo("lobby"));
            Assert.That(message.ReadyPlayers, Has.Count.EqualTo(1));
            Assert.That(
                message.ReadyPlayers[0],
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000a"))
            );
            Assert.That(message.AllReady, Is.False);
        }

        [Test]
        public void AuthorityResponseGoldenFixtureDecodesGrantWithNullReason()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("AuthorityResponse");
            Assert.That(
                AuthorityResponseMessage.TryDecode(
                    envelope.Data,
                    out AuthorityResponseMessage message
                ),
                Is.True
            );
            Assert.That(message.Granted, Is.True);
            Assert.That(message.Reason, Is.Null);
        }

        [Test]
        public void AuthorityChangedGoldenFixtureDecodesAuthorityHandover()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("AuthorityChanged");
            Assert.That(
                AuthorityChangedMessage.TryDecode(
                    envelope.Data,
                    out AuthorityChangedMessage message
                ),
                Is.True
            );
            Assert.That(
                message.AuthorityPlayer,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000a"))
            );
            Assert.That(message.YouAreAuthority, Is.False);
        }

        // --- Golden fixtures: players and game start ------------------------
        [Test]
        public void PlayerJoinedGoldenFixtureDecodesPlayerInfo()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("PlayerJoined");
            Assert.That(
                PlayerJoinedMessage.TryDecode(envelope.Data, out PlayerJoinedMessage message),
                Is.True
            );
            Assert.That(
                message.Player,
                Is.EqualTo(
                    new PlayerInfo(
                        new Guid("00000000-0000-0000-0000-00000000000b"),
                        "Player 2",
                        false,
                        false,
                        "2026-09-20T12:00:01Z"
                    )
                )
            );
        }

        [Test]
        public void PlayerLeftGoldenFixtureDecodesPlayerId()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("PlayerLeft");
            Assert.That(
                PlayerLeftMessage.TryDecode(envelope.Data, out PlayerLeftMessage message),
                Is.True
            );
            Assert.That(
                message.PlayerId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000b"))
            );
        }

        [Test]
        public void PlayerReconnectedGoldenFixtureDecodesPlayerId()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("PlayerReconnected");
            Assert.That(
                PlayerReconnectedMessage.TryDecode(
                    envelope.Data,
                    out PlayerReconnectedMessage message
                ),
                Is.True
            );
            Assert.That(
                message.PlayerId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000b"))
            );
        }

        [Test]
        public void GameStartingGoldenFixtureDecodesPeerConnectionEndpoints()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("GameStarting");
            Assert.That(
                GameStartingMessage.TryDecode(envelope.Data, out GameStartingMessage message),
                Is.True
            );
            Assert.That(message.PeerConnections, Has.Count.EqualTo(2));

            PeerConnection authority = message.PeerConnections[0];
            Assert.That(
                authority.PlayerId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000a"))
            );
            Assert.That(authority.PlayerName, Is.EqualTo("Player 1"));
            Assert.That(authority.IsAuthority, Is.True);
            Assert.That(authority.RelayType, Is.EqualTo("matchbox"));
            Assert.That(authority.ConnectionInfo, Is.Not.Null);
            Assert.That(
                authority.ConnectionInfo,
                Is.EqualTo(new ConnectionEndpoint("direct", "192.0.2.10", 7777))
            );

            PeerConnection relayed = message.PeerConnections[1];
            Assert.That(
                relayed.PlayerId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000b"))
            );
            Assert.That(relayed.IsAuthority, Is.False);
            Assert.That(relayed.ConnectionInfo, Is.Null);
        }

        // --- Golden fixtures: room snapshots ---------------------------------
        [Test]
        public void RoomJoinedGoldenFixtureDecodesMembershipAndRichSnapshot()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("RoomJoined");
            Assert.That(
                RoomJoinedMessage.TryDecode(envelope.Data, out RoomJoinedMessage message),
                Is.True
            );

            Assert.That(
                message.PlayerId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000a"))
            );
            Assert.That(
                message.RoomId,
                Is.EqualTo(new Guid("11111111-1111-1111-1111-111111111111"))
            );
            Assert.That(message.RoomCode, Is.EqualTo("ABC123"));

            RoomSnapshot snapshot = message.Snapshot;
            Assert.That(snapshot.GameName, Is.EqualTo("my-game"));
            Assert.That(snapshot.MaxPlayers, Is.EqualTo(8));
            Assert.That(snapshot.SupportsAuthority, Is.True);
            Assert.That(snapshot.CurrentPlayers, Has.Count.EqualTo(1));
            Assert.That(
                snapshot.CurrentPlayers![0],
                Is.EqualTo(
                    new PlayerInfo(
                        new Guid("00000000-0000-0000-0000-00000000000a"),
                        "Player 1",
                        true,
                        false,
                        "2026-09-20T12:00:00Z"
                    )
                )
            );
            Assert.That(snapshot.IsAuthority, Is.True);
            Assert.That(snapshot.LobbyState, Is.EqualTo("waiting"));
            Assert.That(snapshot.ReadyPlayers, Is.Empty);
            Assert.That(snapshot.RelayType, Is.EqualTo("matchbox"));
            Assert.That(snapshot.CurrentSpectators, Is.Empty);
        }

        [Test]
        public void SpectatorJoinedGoldenFixtureDecodesPartialSnapshot()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("SpectatorJoined");
            Assert.That(
                SpectatorJoinedMessage.TryDecode(envelope.Data, out SpectatorJoinedMessage message),
                Is.True
            );

            Assert.That(
                message.SpectatorId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000c"))
            );
            Assert.That(
                message.RoomId,
                Is.EqualTo(new Guid("11111111-1111-1111-1111-111111111111"))
            );
            Assert.That(message.RoomCode, Is.EqualTo("ABC123"));

            RoomSnapshot snapshot = message.Snapshot;
            Assert.That(snapshot.GameName, Is.EqualTo("my-game"));
            Assert.That(snapshot.LobbyState, Is.EqualTo("lobby"));
            Assert.That(snapshot.CurrentPlayers, Is.Empty);
            Assert.That(snapshot.CurrentSpectators, Is.Empty);
            Assert.That(snapshot.MaxPlayers, Is.EqualTo(0));
            Assert.That(snapshot.SupportsAuthority, Is.False);
            Assert.That(snapshot.ReadyPlayers, Is.Null);
            Assert.That(snapshot.RelayType, Is.Null);
        }

        [Test]
        public void ReconnectedGoldenFixtureDecodesSnapshotSkippingMissedEvents()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("Reconnected");
            Assert.That(
                ReconnectedMessage.TryDecode(envelope.Data, out ReconnectedMessage message),
                Is.True
            );

            Assert.That(
                message.PlayerId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000a"))
            );
            Assert.That(
                message.RoomId,
                Is.EqualTo(new Guid("11111111-1111-1111-1111-111111111111"))
            );
            Assert.That(message.RoomCode, Is.EqualTo("ABC123"));

            RoomSnapshot snapshot = message.Snapshot;
            Assert.That(snapshot.GameName, Is.EqualTo("my-game"));
            Assert.That(snapshot.MaxPlayers, Is.EqualTo(8));
            Assert.That(snapshot.SupportsAuthority, Is.True);
            Assert.That(snapshot.CurrentPlayers, Is.Empty);
            Assert.That(snapshot.IsAuthority, Is.False);
            Assert.That(snapshot.LobbyState, Is.EqualTo("lobby"));
            Assert.That(snapshot.ReadyPlayers, Is.Empty);
            Assert.That(snapshot.RelayType, Is.EqualTo("matchbox"));
            Assert.That(snapshot.CurrentSpectators, Is.Empty);
        }

        // --- Golden fixtures: spectators --------------------------------------
        [Test]
        public void SpectatorLeftGoldenFixtureDecodesCurrentSpectators()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("SpectatorLeft");
            Assert.That(
                SpectatorLeftMessage.TryDecode(envelope.Data, out SpectatorLeftMessage message),
                Is.True
            );
            Assert.That(
                message.RoomId,
                Is.EqualTo(new Guid("11111111-1111-1111-1111-111111111111"))
            );
            Assert.That(message.RoomCode, Is.EqualTo("ABC123"));
            Assert.That(message.Reason, Is.EqualTo("voluntary_leave"));
            Assert.That(message.CurrentSpectators, Is.Empty);
        }

        [Test]
        public void NewSpectatorJoinedGoldenFixtureDecodesSpectatorInfo()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("NewSpectatorJoined");
            Assert.That(
                NewSpectatorJoinedMessage.TryDecode(
                    envelope.Data,
                    out NewSpectatorJoinedMessage message
                ),
                Is.True
            );
            Assert.That(
                message.Spectator,
                Is.EqualTo(
                    new SpectatorInfo(
                        new Guid("00000000-0000-0000-0000-00000000000c"),
                        "Observer2",
                        "2026-09-20T12:00:02Z"
                    )
                )
            );
            Assert.That(message.CurrentSpectators, Is.Empty);
            Assert.That(message.Reason, Is.EqualTo("joined"));
        }

        [Test]
        public void SpectatorDisconnectedGoldenFixtureDecodesReasonAndSpectators()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("SpectatorDisconnected");
            Assert.That(
                SpectatorDisconnectedMessage.TryDecode(
                    envelope.Data,
                    out SpectatorDisconnectedMessage message
                ),
                Is.True
            );
            Assert.That(
                message.SpectatorId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000c"))
            );
            Assert.That(message.Reason, Is.EqualTo("disconnected"));
            Assert.That(message.CurrentSpectators, Is.Empty);
        }

        // --- Golden fixtures: failure family ---------------------------------
        [Test]
        public void ErrorGoldenFixtureDecodesReasonFromMessageAlias()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("Error");
            Assert.That(
                FailureMessage.TryDecode(envelope.Data, out FailureMessage message),
                Is.True
            );
            Assert.That(message.Reason, Is.EqualTo("Room is full"));
            Assert.That(message.ErrorCode, Is.EqualTo("ROOM_FULL"));
        }

        [Test]
        public void AuthenticationErrorGoldenFixtureDecodesReasonFromErrorAlias()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("AuthenticationError");
            Assert.That(
                FailureMessage.TryDecode(envelope.Data, out FailureMessage message),
                Is.True
            );
            Assert.That(message.Reason, Is.EqualTo("Invalid app_id"));
            Assert.That(message.ErrorCode, Is.EqualTo("INVALID_APP_ID"));
        }

        [Test]
        public void RoomJoinFailedGoldenFixtureDecodesReasonField()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope("RoomJoinFailed");
            Assert.That(
                FailureMessage.TryDecode(envelope.Data, out FailureMessage message),
                Is.True
            );
            Assert.That(message.Reason, Is.EqualTo("Room is full"));
            Assert.That(message.ErrorCode, Is.EqualTo("ROOM_FULL"));
        }

        // --- Negative decode policies ------------------------------------------
        [Test]
        public void RoomJoinedWithMalformedPlayerIdRejectsDecode()
        {
            bool decoded = RoomJoinedMessage.TryDecode(
                Bytes(
                    "{\"player_id\": \"zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz\", "
                        + "\"room_id\": \"11111111-1111-1111-1111-111111111111\", "
                        + "\"room_code\": \"ABC123\"}"
                ),
                out RoomJoinedMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(RoomJoinedMessage)));
        }

        [Test]
        public void ProtocolInfoWithWrongTypedCapabilityElementRejectsDecode()
        {
            bool decoded = ProtocolInfoMessage.TryDecode(
                Bytes(
                    "{\"capabilities\": [\"reconnection\", 5], "
                        + "\"game_data_formats\": [\"json\"]}"
                ),
                out ProtocolInfoMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(ProtocolInfoMessage)));
        }

        [Test]
        public void LobbyStateChangedWithDuplicateLobbyStateKeyRejectsDecode()
        {
            bool decoded = LobbyStateChangedMessage.TryDecode(
                Bytes(
                    "{\"lobby_state\": \"lobby\", \"lobby_state\": \"waiting\", "
                        + "\"ready_players\": [], \"all_ready\": false}"
                ),
                out LobbyStateChangedMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(LobbyStateChangedMessage)));
        }

        [Test]
        public void AuthorityResponseWithNumericReasonRejectsDecode()
        {
            bool decoded = AuthorityResponseMessage.TryDecode(
                Bytes("{\"granted\": true, \"reason\": 3}"),
                out AuthorityResponseMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(AuthorityResponseMessage)));
        }

        [Test]
        public void PlayerJoinedWithPlayerObjectMissingNameRejectsDecode()
        {
            bool decoded = PlayerJoinedMessage.TryDecode(
                Bytes(
                    "{\"player\": {\"id\": \"00000000-0000-0000-0000-00000000000b\", "
                        + "\"is_authority\": false, \"is_ready\": false, "
                        + "\"connected_at\": \"2026-09-20T12:00:01Z\"}}"
                ),
                out PlayerJoinedMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(PlayerJoinedMessage)));
        }

        [Test]
        public void GameStartingWithStringConnectionInfoRejectsDecode()
        {
            bool decoded = GameStartingMessage.TryDecode(
                Bytes(
                    "{\"peer_connections\": [{\"player_id\": "
                        + "\"00000000-0000-0000-0000-00000000000a\", "
                        + "\"player_name\": \"Player 1\", \"is_authority\": true, "
                        + "\"relay_type\": \"matchbox\", \"connection_info\": \"direct\"}]}"
                ),
                out GameStartingMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(GameStartingMessage)));
        }

        [Test]
        public void RoomJoinedSnapshotWithWrongTypedMaxPlayersRejectsDecode()
        {
            bool decoded = RoomJoinedMessage.TryDecode(
                Bytes(
                    "{\"player_id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"room_id\": \"11111111-1111-1111-1111-111111111111\", "
                        + "\"room_code\": \"ABC123\", \"max_players\": \"eight\"}"
                ),
                out RoomJoinedMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(RoomJoinedMessage)));
        }

        [Test]
        public void FailureWithConflictingReasonAliasesRejectsDecode()
        {
            bool decoded = FailureMessage.TryDecode(
                Bytes(
                    "{\"reason\": \"Room is full\", \"message\": \"Room is full\", "
                        + "\"error_code\": \"ROOM_FULL\"}"
                ),
                out FailureMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(FailureMessage)));
        }

        [Test]
        public void AuthenticatedWithMissingRateLimitsRejectsDecode()
        {
            bool decoded = AuthenticatedMessage.TryDecode(
                Bytes("{\"app_name\": \"my-game\", \"organization\": \"Ambiguous Interactive\"}"),
                out AuthenticatedMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(AuthenticatedMessage)));
        }

        [Test]
        public void AuthenticatedWithNullAppNameRejectsDecode()
        {
            bool decoded = AuthenticatedMessage.TryDecode(
                Bytes(
                    "{\"app_name\": null, \"organization\": \"Ambiguous Interactive\", "
                        + "\"rate_limits\": {\"per_minute\": 60, \"per_hour\": 3600, "
                        + "\"per_day\": 86400}}"
                ),
                out AuthenticatedMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(AuthenticatedMessage)));
        }

        private static EnvelopeEvent DecodeFixtureEnvelope(string wireType)
        {
            string line = GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", wireType);
            EnvelopeEvent envelope = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(line));
            Assert.That(envelope.Kind, Is.EqualTo(EnvelopeEventKind.Message));
            return envelope;
        }

        private static ReadOnlyMemory<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);

        // --- Golden fixtures: handshake and lobby ---------------------------
    }
}
