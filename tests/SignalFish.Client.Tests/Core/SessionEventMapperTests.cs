namespace SignalFish.Client.Tests.Core
{
    using System;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// M3.3 red-green anchor: the wire→session mapping. Data-driven over
    /// canonical v2 server frames (golden-fixture shapes where the corpus
    /// has them; spec-shaped frames where it does not — issue #9), pinning
    /// that every session fact maps to its typed event, non-session facts
    /// stay unmapped, and the mapping hot path allocates nothing.
    /// </summary>
    [TestFixture]
    public class SessionEventMapperTests
    {
        private static readonly Guid PlayerId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        private static readonly Guid RoomId = new Guid("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        private const string RoomCode = "ABC123";

        // Canonical frames. RoomJoined/Reconnected carry the full v2 field
        // set; the decoder consumes the session-critical subset and tolerates
        // the rest (spec: all 12 fields required on the v2 wire).
        private const string RoomJoinedFrame =
            @"{""type"":""RoomJoined"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"","
            + @"""room_code"":""ABC123"",""player_id"":""0f8fad5b-d9cb-469f-a165-70867728950e"","
            + @"""game_name"":""my-game"",""max_players"":8,""supports_authority"":true,"
            + @"""current_players"":[],""is_authority"":false,""lobby_state"":""lobby"","
            + @"""ready_players"":[],""relay_type"":""relay"",""current_spectators"":[]}}";

        private const string SpectatorJoinedFrame =
            @"{""type"":""SpectatorJoined"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"","
            + @"""room_code"":""ABC123"",""spectator_id"":""0f8fad5b-d9cb-469f-a165-70867728950e"","
            + @"""game_name"":""my-game"",""current_players"":[],""current_spectators"":[],"
            + @"""lobby_state"":""lobby""}}";

        private const string ReconnectedFrame =
            @"{""type"":""Reconnected"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"","
            + @"""room_code"":""ABC123"",""player_id"":""0f8fad5b-d9cb-469f-a165-70867728950e"","
            + @"""missed_events"":[]}}";

        [Test]
        public void TryMap_SessionFactFrames_MapToTypedEvents()
        {
            (string Wire, SessionEvent Expected)[] rows =
            {
                (
                    RoomJoinedFrame,
                    SessionEvent.Joined(
                        SessionEventKind.RoomJoined,
                        new RoomMembership(RoomRole.Player, PlayerId, RoomId, RoomCode)
                    )
                ),
                (
                    SpectatorJoinedFrame,
                    SessionEvent.Joined(
                        SessionEventKind.SpectatorJoined,
                        new RoomMembership(RoomRole.Spectator, PlayerId, RoomId, RoomCode)
                    )
                ),
                (
                    ReconnectedFrame,
                    SessionEvent.Joined(
                        SessionEventKind.Reconnected,
                        new RoomMembership(RoomRole.Player, PlayerId, RoomId, RoomCode)
                    )
                ),
                (
                    @"{""type"":""Authenticated""}",
                    SessionEvent.From(SessionEventKind.Authenticated)
                ),
                (@"{""type"":""RoomLeft""}", SessionEvent.From(SessionEventKind.RoomLeft)),
                (
                    @"{""type"":""SpectatorLeft"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"",""reason"":""voluntary_leave""}}",
                    SessionEvent.From(SessionEventKind.SpectatorLeft)
                ),
                (
                    @"{""type"":""RoomJoinFailed"",""data"":{""reason"":""Room is full"",""error_code"":""ROOM_FULL""}}",
                    SessionEvent.From(SessionEventKind.RoomJoinFailed)
                ),
                (
                    @"{""type"":""SpectatorJoinFailed"",""data"":{""reason"":""spectators not allowed""}}",
                    SessionEvent.From(SessionEventKind.SpectatorJoinFailed)
                ),
                (
                    @"{""type"":""ReconnectionFailed"",""data"":{""reason"":""window expired"",""error_code"":""RECONNECTION_EXPIRED""}}",
                    SessionEvent.From(SessionEventKind.ReconnectionFailed)
                ),
                (
                    GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "Error"),
                    SessionEvent.From(SessionEventKind.ServerError)
                ),
            };

            foreach ((string wire, SessionEvent expected) in rows)
            {
                Assert.That(TryMapWire(wire, out SessionEvent mapped), Is.True, wire);
                Assert.That(mapped, Is.EqualTo(expected), wire);
            }
        }

        [Test]
        public void TryMap_NonSessionFacts_AreNotMapped()
        {
            string[] wires =
            {
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "ProtocolInfo"),
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "PlayerJoined"),
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "PlayerLeft"),
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "GameData"),
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "LobbyStateChanged"),
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "AuthorityResponse"),
                GoldenFixtures.ReadFirstLineOfType(
                    "v2-server-messages.jsonl",
                    "AuthenticationError"
                ),
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "AuthorityChanged"),
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "GameStarting"),
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "Pong"),
            };

            foreach (string wire in wires)
            {
                Assert.That(TryMapWire(wire, out _), Is.False, wire);
            }
        }

        [Test]
        public void TryMap_MalformedOrUnknownEnvelope_IsNotMapped()
        {
            // Session-critical field missing from the payload.
            Assert.That(
                TryMapWire(
                    @"{""type"":""RoomJoined"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"",""room_code"":""ABC123""}}",
                    out _
                ),
                Is.False
            );
            // A repeated session-critical key is rejected (fail-closed), not
            // last-wins.
            Assert.That(
                TryMapWire(
                    @"{""type"":""RoomJoined"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"","
                        + @"""room_id"":""0f8fad5b-d9cb-469f-a165-70867728950e"",""room_code"":""ABC123"","
                        + @"""player_id"":""0f8fad5b-d9cb-469f-a165-70867728950e""}}",
                    out _
                ),
                Is.False
            );
            // Payload is not an object.
            Assert.That(TryMapWire(@"{""type"":""RoomJoined""}", out _), Is.False);
            // Unknown wire type: forward-compatible UnknownMessage event.
            Assert.That(TryMapWire(@"{""type"":""FutureThing""}", out _), Is.False);
        }

        [Test]
        public void TryMap_RoomJoined_GuidDecodeMatchesParse(
            [ValueSource(nameof(GuidVariants))] string text
        )
        {
            string wire =
                @"{""type"":""RoomJoined"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"","
                + @"""room_code"":""ABC123"",""player_id"":"""
                + text
                + @"""}}";

            Assert.That(TryMapWire(wire, out SessionEvent mapped), Is.True);
            Assert.That(mapped.Membership.PlayerId, Is.EqualTo(Guid.Parse(text)));
            Assert.That(
                mapped.Membership.RoomId,
                Is.EqualTo(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"))
            );
        }

        [Test]
        public void TryMap_RoomJoined_MalformedUuid_IsRejected()
        {
            string[] badIds =
            {
                "0f8fad5b_d9cb-469f-a165-70867728950e", // wrong separator
                "0f8fad5b-d9cb-469f-a165-70867728950g", // not hex
                "0f8fad5b-d9cb-469f-a165-7086772895", // too short
                "0f8fad5bd9cb469fa16570867728950e11", // unhyphenated
            };

            foreach (string badId in badIds)
            {
                string wire =
                    @"{""type"":""RoomJoined"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"","
                    + @"""room_code"":""ABC123"",""player_id"":"""
                    + badId
                    + @"""}}";
                Assert.That(TryMapWire(wire, out _), Is.False, badId);
            }
        }

        private static readonly string[] GuidVariants =
        {
            "0f8fad5b-d9cb-469f-a165-70867728950e",
            "00000000-0000-0000-0000-000000000000",
            "FFFFFFFF-7425-40de-944b-E07FC1F90AE7",
            "deadbeef-dead-beef-dead-beefdeadbeef",
        };

        [Test]
        public void HotPath_TryMap_SteadyStateAllocatesNothing()
        {
            // Payload-less and failure session facts are per-frame traffic:
            // mapping them must not allocate. Join/reconnect mapping is cold
            // (one string per confirmed membership: the room code) and is
            // excluded by design.
            string[] wires =
            {
                @"{""type"":""Authenticated""}",
                @"{""type"":""RoomLeft""}",
                @"{""type"":""SpectatorLeft"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"",""reason"":""voluntary_leave""}}",
                @"{""type"":""RoomJoinFailed"",""data"":{""reason"":""Room is full""}}",
                @"{""type"":""SpectatorJoinFailed"",""data"":{""reason"":""spectators not allowed""}}",
                @"{""type"":""ReconnectionFailed"",""data"":{""reason"":""window expired"",""error_code"":""RECONNECTION_EXPIRED""}}",
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "Error"),
                @"{""type"":""LobbyStateChanged"",""data"":{""lobby_state"":""lobby"",""all_ready"":false}}",

                // Unknown types are excluded: they take the documented
                // once-per-frame TypeText allocation on the rare path.
            };
            byte[][] frames = new byte[wires.Length][];
            for (int i = 0; i < wires.Length; i++)
            {
                frames[i] = Encoding.UTF8.GetBytes(wires[i]);
            }

            Assert.That(TryMapWire(RoomJoinedFrame, out _), Is.True, "warmup must map");
            // Warm every measured frame once so static/one-time costs land
            // outside the measured passes.
            foreach (string wire in wires)
            {
                TryMapWire(wire, out _);
            }

            long minDelta = long.MaxValue;
            for (int pass = 0; pass < 4; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    foreach (byte[] frame in frames)
                    {
                        TryMapWire(frame, out _);
                    }
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }

            Assert.That(
                minDelta,
                Is.EqualTo(0),
                "Steady-state envelope→session mapping must not allocate."
            );
        }

        private static bool TryMapWire(string wire, out SessionEvent sessionEvent)
        {
            return TryMapWire(Encoding.UTF8.GetBytes(wire), out sessionEvent);
        }

        private static bool TryMapWire(byte[] wire, out SessionEvent sessionEvent)
        {
            EnvelopeEvent envelope = EnvelopeReader.Decode(wire);
            return SessionEventMapper.TryMap(envelope, out sessionEvent);
        }
    }
}
