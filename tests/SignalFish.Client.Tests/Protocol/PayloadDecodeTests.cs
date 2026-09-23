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
        private static readonly TestCaseData[] SealedRoomFailureWires =
        {
            new TestCaseData(
                @"{""type"":""SpectatorJoinFailed"",""data"":{""reason"":""password required"",""error_code"":""PASSWORD_REQUIRED""}}",
                "passwordless join to a sealed room"
            ).SetName("SealedRoomWithoutPassword"),
            new TestCaseData(
                @"{""type"":""SpectatorJoinFailed"",""data"":{""reason"":""wrong password"",""error_code"":""PASSWORD_REQUIRED""}}",
                "wrong password join to a sealed room"
            ).SetName("SealedRoomWithWrongPassword"),
            new TestCaseData(
                @"{""type"":""SpectatorJoinFailed"",""data"":{""reason"":""password refused"",""error_code"":""PASSWORD_REQUIRED""}}",
                "password presented to an open room"
            ).SetName("OpenRoomWithPassword"),
        };

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
            // The v3 negotiation fields are absent on a v2 negotiation.
            Assert.That(message.ProtocolVersion, Is.Null);
            Assert.That(message.MinProtocolVersion, Is.Null);
            Assert.That(message.MaxProtocolVersion, Is.Null);
            Assert.That(message.Transports, Is.Null);
            Assert.That(message.MaxOutboundMessageSize, Is.Null);
        }

        [Test]
        public void ProtocolInfoV3GoldenFixtureDecodesNegotiatedResult()
        {
            EnvelopeEvent envelope = DecodeFixtureEnvelope(
                "ProtocolInfo",
                "v3-server-messages.jsonl"
            );
            Assert.That(
                ProtocolInfoMessage.TryDecode(envelope.Data, out ProtocolInfoMessage message),
                Is.True
            );
            Assert.That(message.Capabilities, Has.Count.EqualTo(4));
            Assert.That(message.Capabilities[0], Is.EqualTo("reconnection"));
            Assert.That(message.Capabilities[1], Is.EqualTo("spectators"));
            Assert.That(message.Capabilities[2], Is.EqualTo("authority"));
            Assert.That(message.Capabilities[3], Is.EqualTo("room_operation_ids"));
            Assert.That(message.ProtocolVersion, Is.EqualTo(3u));
            Assert.That(message.MinProtocolVersion, Is.EqualTo(2u));
            Assert.That(message.MaxProtocolVersion, Is.EqualTo(3u));
            Assert.That(message.Transports, Has.Count.EqualTo(1));
            Assert.That(message.Transports[0], Is.EqualTo("websocket"));
            Assert.That(message.MaxOutboundMessageSize, Is.EqualTo(8388608u));
        }

        // --- v3 negotiation-field decode policies -------------------------------
        [TestCase(
            "{\"capabilities\": [], \"game_data_formats\": [], \"protocol_version\": \"three\"}",
            TestName = "WrongTypedProtocolVersion"
        )]
        [TestCase(
            "{\"capabilities\": [], \"game_data_formats\": [], \"min_protocol_version\": [2]}",
            TestName = "WrongTypedMinProtocolVersion"
        )]
        [TestCase(
            "{\"capabilities\": [], \"game_data_formats\": [], \"transports\": \"websocket\"}",
            TestName = "WrongTypedTransports"
        )]
        [TestCase(
            "{\"capabilities\": [], \"game_data_formats\": [], \"max_outbound_message_size\": -1}",
            TestName = "NegativeMaxOutboundMessageSize"
        )]
        [TestCase(
            "{\"capabilities\": [], \"game_data_formats\": [], \"protocol_version\": 3, \"protocol_version\": 2}",
            TestName = "RepeatedProtocolVersionKey"
        )]
        [TestCase(
            "{\"capabilities\": [], \"game_data_formats\": [], \"protocol_version\": null, \"protocol_version\": 3}",
            TestName = "RepeatedProtocolVersionKeyAfterNull"
        )]
        [TestCase(
            "{\"capabilities\": [], \"game_data_formats\": [], \"transports\": null, \"transports\": [\"websocket\"]}",
            TestName = "RepeatedTransportsKeyAfterNull"
        )]
        public void ProtocolInfoWithMalformedV3FieldRejectsDecode(string wire)
        {
            bool decoded = ProtocolInfoMessage.TryDecode(
                Bytes(wire),
                out ProtocolInfoMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(ProtocolInfoMessage)));
        }

        [Test]
        public void ProtocolInfoWithExplicitNullV3FieldsTreatsThemAsAbsent()
        {
            bool decoded = ProtocolInfoMessage.TryDecode(
                Bytes(
                    "{\"capabilities\": [\"reconnection\"], \"game_data_formats\": [\"json\"], "
                        + "\"protocol_version\": null, \"min_protocol_version\": null, "
                        + "\"max_protocol_version\": null, \"transports\": null, "
                        + "\"max_outbound_message_size\": null}"
                ),
                out ProtocolInfoMessage message
            );
            Assert.That(decoded, Is.True);
            Assert.That(message.ProtocolVersion, Is.Null);
            Assert.That(message.Transports, Is.Null);
            Assert.That(message.MaxOutboundMessageSize, Is.Null);
        }

        [Test]
        public void ProtocolInfoWithUnknownFieldStillDecodes()
        {
            bool decoded = ProtocolInfoMessage.TryDecode(
                Bytes(
                    "{\"capabilities\": [], \"game_data_formats\": [], "
                        + "\"future_v4_field\": {\"nested\": true}}"
                ),
                out ProtocolInfoMessage message
            );
            Assert.That(decoded, Is.True);
            Assert.That(message.Capabilities, Is.Empty);
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

        [Test]
        public void AuthorityChangedNullableAuthorityPlayerDecodesAsVacated()
        {
            /*
                The spec declares authority_player required-but-nullable: an
                explicit JSON null is a legal "the seat vacated" broadcast,
                never a decode failure (#54).
            */
            byte[] wire = Encoding.UTF8.GetBytes(
                @"{""type"":""AuthorityChanged"",""data"":{""authority_player"":null,""you_are_authority"":false}}"
            );
            EnvelopeEvent envelope = EnvelopeReader.Decode(wire);
            Assert.That(
                AuthorityChangedMessage.TryDecode(
                    envelope.Data,
                    out AuthorityChangedMessage message
                ),
                Is.True
            );
            Assert.That(message.AuthorityPlayer, Is.Null);
            Assert.That(message.YouAreAuthority, Is.False);
        }

        [Test]
        public void AuthorityChangedMissingOrWrongTypedPlayerKeyRejectsDecode()
        {
            byte[] missing = Encoding.UTF8.GetBytes(
                @"{""type"":""AuthorityChanged"",""data"":{""you_are_authority"":false}}"
            );
            Assert.That(
                AuthorityChangedMessage.TryDecode(EnvelopeReader.Decode(missing).Data, out _),
                Is.False
            );

            byte[] wrongType = Encoding.UTF8.GetBytes(
                @"{""type"":""AuthorityChanged"",""data"":{""authority_player"":7,""you_are_authority"":false}}"
            );
            Assert.That(
                AuthorityChangedMessage.TryDecode(EnvelopeReader.Decode(wrongType).Data, out _),
                Is.False
            );
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

        [Test]
        public void RoomSnapshotTreatsExplicitNullFieldsAsAbsent()
        {
            string data =
                @"{""game_name"":null,""max_players"":null,""supports_authority"":null,"
                + @"""is_authority"":null,""lobby_state"":null,""relay_type"":null,"
                + @"""ready_players"":null,""current_players"":null,""current_spectators"":null}";
            Assert.That(RoomSnapshot.TryDecode(Bytes(data), out RoomSnapshot snapshot), Is.True);
            Assert.That(snapshot.GameName, Is.Null);
            Assert.That(snapshot.MaxPlayers, Is.EqualTo(0u));
            Assert.That(snapshot.SupportsAuthority, Is.False);
            Assert.That(snapshot.IsAuthority, Is.False);
            Assert.That(snapshot.LobbyState, Is.Null);
            Assert.That(snapshot.RelayType, Is.Null);
            Assert.That(snapshot.ReadyPlayers, Is.Null);
            Assert.That(snapshot.CurrentPlayers, Is.Empty);
            Assert.That(snapshot.CurrentSpectators, Is.Empty);
        }

        [Test]
        public void RoomSnapshotRejectsRepeatedKeys()
        {
            string data =
                @"{""is_authority"":true,""lobby_state"":""lobby"",""is_authority"":false}";
            Assert.That(RoomSnapshot.TryDecode(Bytes(data), out _), Is.False);
        }

        // --- M5.1: password sealing and join-failure indistinguishability ---
        [Test]
        public void PasswordCarryingMessagesRedactTheSecretInToString()
        {
            JoinRoomMessage join = new JoinRoomMessage(
                "my-game",
                "Alice",
                roomCode: "ABCD",
                password: "hunter2"
            );
            string joinText = join.ToString();
            Assert.That(joinText, Does.Contain("GameName=my-game"));
            Assert.That(joinText, Does.Contain("RoomCode=ABCD"));
            Assert.That(joinText, Does.Contain("Password=<redacted>"));
            Assert.That(joinText, Does.Not.Contain("hunter2"));

            JoinAsSpectatorMessage spectate = new JoinAsSpectatorMessage(
                "my-game",
                "ABCD",
                "Watcher",
                "hunter2"
            );
            string spectateText = spectate.ToString();
            Assert.That(spectateText, Does.Contain("SpectatorName=Watcher"));
            Assert.That(spectateText, Does.Contain("Password=<redacted>"));
            Assert.That(spectateText, Does.Not.Contain("hunter2"));

            Assert.That(
                new JoinRoomMessage("my-game", "Alice").ToString(),
                Does.Contain("Password=<none>")
            );
        }

        [TestCaseSource(nameof(SealedRoomFailureWires))]
        public void PasswordFailuresAreIndistinguishableToTheSender(string wire, string because)
        {
            /*
                Missing password, wrong password, and a password presented
                to an open room all arrive as the same typed code — the
                client surfaces it as data and never distinguishes them.
            */
            EnvelopeEvent envelope = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(wire));
            Assert.That(
                FailureMessage.TryDecode(envelope.Data, out FailureMessage failure),
                Is.True,
                because
            );
            Assert.That(failure.ErrorCode, Is.EqualTo("PASSWORD_REQUIRED"), because);
        }

        private static ReadOnlyMemory<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);

        private static EnvelopeEvent DecodeFixtureEnvelope(
            string wireType,
            string fileName = "v2-server-messages.jsonl"
        )
        {
            string line = GoldenFixtures.ReadFirstLineOfType(fileName, wireType);
            EnvelopeEvent envelope = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(line));
            Assert.That(envelope.Kind, Is.EqualTo(EnvelopeEventKind.Message));
            return envelope;
        }

        // --- Golden fixtures: handshake and lobby ---------------------------
    }
}
