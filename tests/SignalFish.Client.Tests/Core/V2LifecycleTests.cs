namespace SignalFish.Client.Tests.Core
{
    using System;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// M3.3 red-green anchor: the v2 lifecycle. Each wire step is admitted
    /// by the state machine (role-gated table), sent as a canonical wire
    /// frame, answered by a server frame mapped through
    /// <see cref="SessionEventMapper"/>, and applied to the machine — the
    /// full Authenticate → JoinRoom → ready/start → relay → LeaveRoom loop
    /// plus the spectator branch.
    /// </summary>
    [TestFixture]
    public class V2LifecycleTests
    {
        private static readonly Guid PlayerId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        private static readonly Guid RoomId = new Guid("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        private const string RoomCode = "ABC123";

        [Test]
        public void PlayerLifecycle_FullV2Loop_ReachesCleanExit()
        {
            SignalFishStateMachine machine = new SignalFishStateMachine();

            // Transport up, then the (optional in open mode) authentication.
            Assert.That(TryAdmit(machine, ClientCommand.Ping), Is.True);
            machine.Apply(SessionEvent.From(SessionEventKind.TransportReady));
            Assert.That(machine.Phase, Is.EqualTo(ConnectionPhase.TransportReady));
            Assert.That(
                ApplyWire(machine, @"{""type"":""Authenticated""}"),
                Is.EqualTo(ConnectionPhase.Authenticated)
            );

            // Directed operations arm their fence; the join confirms it.
            Assert.That(
                AdmitAndArm(machine, ClientCommand.JoinRoom),
                Is.EqualTo(PendingRoomOperation.JoinPlayer)
            );
            Assert.That(
                ApplyWire(
                    machine,
                    @"{""type"":""RoomJoined"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"",""room_code"":""ABC123"",""player_id"":""0f8fad5b-d9cb-469f-a165-70867728950e""}}"
                ),
                Is.EqualTo(ConnectionPhase.InRoom)
            );
            Assert.That(
                machine.Membership,
                Is.EqualTo(new RoomMembership(RoomRole.Player, PlayerId, RoomId, RoomCode))
            );
            Assert.That(machine.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));

            // Readiness and (authority-gated at M5.2) start are in-room player ops.
            Assert.That(TryAdmit(machine, ClientCommand.SetReady), Is.True);
            Assert.That(TryAdmit(machine, ClientCommand.StartGame), Is.True);

            // Gameplay frames are not session facts: they are refused and
            // phase and membership hold.
            RefuseWire(machine, @"{""type"":""GameStarting"",""data"":{""peer_connections"":[]}}");
            Assert.That(machine.Phase, Is.EqualTo(ConnectionPhase.InRoom));
            RefuseWire(
                machine,
                @"{""type"":""GameData"",""data"":{""from_player"":""0f8fad5b-d9cb-469f-a165-70867728950e"",""data"":{}}}"
            );
            Assert.That(machine.Phase, Is.EqualTo(ConnectionPhase.InRoom));
            Assert.That(TryAdmit(machine, ClientCommand.SendGameData), Is.True);

            // The typed leave loop: admit, arm, confirm.
            Assert.That(
                AdmitAndArm(machine, ClientCommand.LeaveRoom),
                Is.EqualTo(PendingRoomOperation.LeavePlayer)
            );
            Assert.That(
                ApplyWire(machine, @"{""type"":""RoomLeft""}"),
                Is.EqualTo(ConnectionPhase.Authenticated)
            );
            Assert.That(machine.Membership.IsPresent, Is.False);

            // A typed failure answers the next fenced operation.
            Assert.That(
                AdmitAndArm(machine, ClientCommand.JoinRoom),
                Is.EqualTo(PendingRoomOperation.JoinPlayer)
            );
            Assert.That(
                ApplyWire(
                    machine,
                    @"{""type"":""RoomJoinFailed"",""data"":{""reason"":""Room is full"",""error_code"":""ROOM_FULL""}}"
                ),
                Is.EqualTo(ConnectionPhase.Authenticated)
            );
            Assert.That(machine.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));

            // A generic server error never releases a fence (fail-closed)…
            Assert.That(
                AdmitAndArm(machine, ClientCommand.JoinRoom),
                Is.EqualTo(PendingRoomOperation.JoinPlayer)
            );
            Assert.That(
                ApplyWire(
                    machine,
                    GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "Error")
                ),
                Is.EqualTo(ConnectionPhase.Authenticated)
            );
            Assert.That(
                machine.PendingOperation,
                Is.EqualTo(PendingRoomOperation.JoinPlayer),
                "a generic server error must not release the fence"
            );

            // …and teardown clears everything.
            machine.Apply(SessionEvent.From(SessionEventKind.Disconnected));
            Assert.That(machine.Phase, Is.EqualTo(ConnectionPhase.Terminal));
        }

        [Test]
        public void SpectatorLifecycle_JoinObserveLeave_HoldsSpectatorRole()
        {
            SignalFishStateMachine machine = new SignalFishStateMachine();
            machine.Apply(SessionEvent.From(SessionEventKind.TransportReady));
            machine.Apply(SessionEvent.Authenticated());

            Assert.That(
                AdmitAndArm(machine, ClientCommand.JoinAsSpectator),
                Is.EqualTo(PendingRoomOperation.JoinSpectator)
            );
            Assert.That(
                ApplyWire(
                    machine,
                    @"{""type"":""SpectatorJoined"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"",""room_code"":""ABC123"",""spectator_id"":""0f8fad5b-d9cb-469f-a165-70867728950e""}}"
                ),
                Is.EqualTo(ConnectionPhase.InRoom)
            );
            Assert.That(
                machine.Membership,
                Is.EqualTo(new RoomMembership(RoomRole.Spectator, PlayerId, RoomId, RoomCode))
            );

            // The role gate holds on the live membership.
            Assert.That(
                machine.TryAdmit(ClientCommand.SetReady, out AdmissionError error),
                Is.False
            );
            Assert.That(error, Is.EqualTo(AdmissionError.WrongRoomRole));

            Assert.That(
                AdmitAndArm(machine, ClientCommand.LeaveSpectator),
                Is.EqualTo(PendingRoomOperation.LeaveSpectator)
            );
            Assert.That(
                ApplyWire(
                    machine,
                    @"{""type"":""SpectatorLeft"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"",""reason"":""voluntary_leave""}}"
                ),
                Is.EqualTo(ConnectionPhase.Authenticated)
            );
            Assert.That(machine.Membership.IsPresent, Is.False);
        }

        private static bool TryAdmit(SignalFishStateMachine machine, ClientCommand command)
        {
            return machine.TryAdmit(command, out _);
        }

        /// <summary>Admits a directed command and arms its fence.</summary>
        private static PendingRoomOperation AdmitAndArm(
            SignalFishStateMachine machine,
            ClientCommand command
        )
        {
            Assert.That(machine.TryAdmit(command, out _), Is.True, command.ToString());
            PendingRoomOperation? fence = SignalFishStateMachine.PendingOperationFor(command);
            Assert.That(fence, Is.Not.Null, command.ToString());
            machine.Arm(fence!.Value);
            return fence.Value;
        }

        /// <summary>
        /// Asserts one server wire frame is not a session fact and leaves
        /// the machine untouched.
        /// </summary>
        private static void RefuseWire(SignalFishStateMachine machine, string wire)
        {
            EnvelopeEvent envelope = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(wire));
            Assert.That(SessionEventMapper.TryMap(envelope, out _), Is.False, wire);
        }

        /// <summary>
        /// Maps one server wire frame to its session event and applies it,
        /// returning the machine's phase afterwards. Fails the test when the
        /// frame is not a session fact (callers assert those separately).
        /// </summary>
        private static ConnectionPhase ApplyWire(SignalFishStateMachine machine, string wire)
        {
            EnvelopeEvent envelope = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(wire));
            Assert.That(
                SessionEventMapper.TryMap(envelope, out SessionEvent sessionEvent),
                Is.True,
                wire
            );
            machine.Apply(sessionEvent);
            return machine.Phase;
        }
    }
}
