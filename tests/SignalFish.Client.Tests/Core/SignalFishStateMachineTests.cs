namespace SignalFish.Client.Tests.Core
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Core;

    /// <summary>
    /// M3.2 red-green anchor: the connection state machine. Tables pin the
    /// Rust-parity phase derivation, the four-field membership invariant,
    /// the role-gated admission table, and the fail-closed membership fence
    /// (released by typed results only — never by a generic server error).
    /// </summary>
    [TestFixture]
    public class SignalFishStateMachineTests
    {
        private const string RoomCode = "ABC123";

        private static readonly Guid PlayerId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        private static readonly Guid RoomId = new Guid("7c9e6679-7425-40de-944b-e07fc1f90ae7");

        [Test]
        public void PhaseProgressionTableMatchesRustPhaseOrdering()
        {
            (SessionEvent Event, ConnectionPhase Expected)[] steps =
            {
                (
                    SessionEvent.From(SessionEventKind.TransportReady),
                    ConnectionPhase.TransportReady
                ),
                (SessionEvent.Authenticated(), ConnectionPhase.Authenticated),
                (
                    SessionEvent.Joined(SessionEventKind.RoomJoined, Membership(RoomRole.Player)),
                    ConnectionPhase.InRoom
                ),
                (SessionEvent.From(SessionEventKind.Disconnected), ConnectionPhase.Terminal),
            };

            SignalFishStateMachine machine = new SignalFishStateMachine();
            Assert.That(machine.Phase, Is.EqualTo(ConnectionPhase.Connecting));
            foreach ((SessionEvent sessionEvent, ConnectionPhase expected) in steps)
            {
                machine.Apply(sessionEvent);
                Assert.That(machine.Phase, Is.EqualTo(expected));
            }
        }

        [Test]
        public void MembershipJoinSetsAllFourFieldsAsOneInvariant()
        {
            SignalFishStateMachine player = InRoom(RoomRole.Player);
            AssertMembership(player, RoomRole.Player);
            Assert.That(player.Phase, Is.EqualTo(ConnectionPhase.InRoom));

            SignalFishStateMachine spectator = InRoom(RoomRole.Spectator);
            AssertMembership(spectator, RoomRole.Spectator);
        }

        [Test]
        public void MembershipConfirmedExitClearsAllFourFields()
        {
            SignalFishStateMachine player = InRoom(RoomRole.Player);
            player.Apply(SessionEvent.From(SessionEventKind.RoomLeft));

            Assert.That(player.Membership.IsPresent, Is.False);
            Assert.That(player.Membership.PlayerId, Is.EqualTo(Guid.Empty));
            Assert.That(player.Membership.RoomId, Is.EqualTo(Guid.Empty));
            Assert.That(player.Membership.RoomCode, Is.Null);
            Assert.That(player.Phase, Is.EqualTo(ConnectionPhase.Authenticated));
            Assert.That(player.IsConnected, Is.True);
        }

        [Test]
        public void MembershipSpectatorExitClearsFieldsKeepsConnection()
        {
            SignalFishStateMachine spectator = InRoom(RoomRole.Spectator);
            spectator.Apply(SessionEvent.From(SessionEventKind.SpectatorLeft));

            Assert.That(spectator.Membership.IsPresent, Is.False);
            Assert.That(spectator.Phase, Is.EqualTo(ConnectionPhase.Authenticated));
        }

        [Test]
        public void AuthenticatedIsSessionFactWithoutIdentity()
        {
            SignalFishStateMachine machine = new SignalFishStateMachine();
            machine.Apply(SessionEvent.Authenticated());

            Assert.That(machine.IsAuthenticated, Is.True);
            /*
                The v2 Authenticated payload carries no player id; the identity
                is confirmed with the membership at join/reconnect time.
            */
            Assert.That(machine.Membership.IsPresent, Is.False);
        }

        [Test]
        public void AdmitAdmissionTableMatchesRustPrecedence()
        {
            (
                string Label,
                SignalFishStateMachine Machine,
                ClientCommand Command,
                AdmissionError Expected
            )[] rows =
            {
                ("fresh+ping", Fresh(), ClientCommand.Ping, default(AdmissionError)),
                (
                    "fresh+joinRoom",
                    Fresh(),
                    ClientCommand.JoinRoom,
                    AdmissionError.NotAuthenticated
                ),
                (
                    "fresh+leaveRoom",
                    Fresh(),
                    ClientCommand.LeaveRoom,
                    AdmissionError.NotAuthenticated
                ),
                (
                    "fresh+reconnect",
                    Fresh(),
                    ClientCommand.Reconnect,
                    AdmissionError.NotAuthenticated
                ),
                ("fresh+setReady", Fresh(), ClientCommand.SetReady, AdmissionError.NotInRoom),
                ("fresh+gameData", Fresh(), ClientCommand.SendGameData, AdmissionError.NotInRoom),
                (
                    "authed+joinRoom",
                    AuthenticatedAtLeast(),
                    ClientCommand.JoinRoom,
                    default(AdmissionError)
                ),
                (
                    "authed+joinSpectator",
                    AuthenticatedAtLeast(),
                    ClientCommand.JoinAsSpectator,
                    default(AdmissionError)
                ),
                (
                    "authed+reconnect",
                    AuthenticatedAtLeast(),
                    ClientCommand.Reconnect,
                    default(AdmissionError)
                ),
                (
                    "authed+leaveRoom",
                    AuthenticatedAtLeast(),
                    ClientCommand.LeaveRoom,
                    AdmissionError.NotInRoom
                ),
                (
                    "authed+leaveSpectator",
                    AuthenticatedAtLeast(),
                    ClientCommand.LeaveSpectator,
                    AdmissionError.NotInRoom
                ),
                (
                    "authed+setReady",
                    AuthenticatedAtLeast(),
                    ClientCommand.SetReady,
                    AdmissionError.NotInRoom
                ),
                (
                    "authed+startGame",
                    AuthenticatedAtLeast(),
                    ClientCommand.StartGame,
                    AdmissionError.NotInRoom
                ),
                (
                    "fenced+ping",
                    Fenced(PendingRoomOperation.JoinPlayer),
                    ClientCommand.Ping,
                    default(AdmissionError)
                ),
                (
                    "fenced+joinRoom",
                    Fenced(PendingRoomOperation.JoinPlayer),
                    ClientCommand.JoinRoom,
                    AdmissionError.RoomOperationPending
                ),
                (
                    "fenced+setReady",
                    Fenced(PendingRoomOperation.JoinPlayer),
                    ClientCommand.SetReady,
                    AdmissionError.RoomOperationPending
                ),
                (
                    "fenced+leaveRoom",
                    Fenced(PendingRoomOperation.JoinPlayer),
                    ClientCommand.LeaveRoom,
                    AdmissionError.RoomOperationPending
                ),
                (
                    "fenced+reconnect",
                    Fenced(PendingRoomOperation.JoinPlayer),
                    ClientCommand.Reconnect,
                    AdmissionError.RoomOperationPending
                ),
                (
                    "fencedInRoom+joinRoom",
                    Fenced(PendingRoomOperation.LeavePlayer),
                    ClientCommand.JoinRoom,
                    AdmissionError.RoomOperationPending
                ),
                (
                    "player+leaveRoom",
                    InRoom(RoomRole.Player),
                    ClientCommand.LeaveRoom,
                    default(AdmissionError)
                ),
                (
                    "player+leaveSpectator",
                    InRoom(RoomRole.Player),
                    ClientCommand.LeaveSpectator,
                    AdmissionError.WrongRoomRole
                ),
                (
                    "player+setReady",
                    InRoom(RoomRole.Player),
                    ClientCommand.SetReady,
                    default(AdmissionError)
                ),
                (
                    "player+startGame",
                    InRoom(RoomRole.Player),
                    ClientCommand.StartGame,
                    default(AdmissionError)
                ),
                (
                    "player+gameData",
                    InRoom(RoomRole.Player),
                    ClientCommand.SendGameData,
                    default(AdmissionError)
                ),
                (
                    "player+joinRoom",
                    InRoom(RoomRole.Player),
                    ClientCommand.JoinRoom,
                    AdmissionError.AlreadyInRoom
                ),
                (
                    "player+joinSpectator",
                    InRoom(RoomRole.Player),
                    ClientCommand.JoinAsSpectator,
                    AdmissionError.AlreadyInRoom
                ),
                (
                    "player+reconnect",
                    InRoom(RoomRole.Player),
                    ClientCommand.Reconnect,
                    AdmissionError.AlreadyInRoom
                ),
                (
                    "spectator+leaveSpectator",
                    InRoom(RoomRole.Spectator),
                    ClientCommand.LeaveSpectator,
                    default(AdmissionError)
                ),
                (
                    "spectator+leaveRoom",
                    InRoom(RoomRole.Spectator),
                    ClientCommand.LeaveRoom,
                    AdmissionError.WrongRoomRole
                ),
                (
                    "spectator+setReady",
                    InRoom(RoomRole.Spectator),
                    ClientCommand.SetReady,
                    AdmissionError.WrongRoomRole
                ),
                (
                    "spectator+startGame",
                    InRoom(RoomRole.Spectator),
                    ClientCommand.StartGame,
                    AdmissionError.WrongRoomRole
                ),
                (
                    "spectator+gameData",
                    InRoom(RoomRole.Spectator),
                    ClientCommand.SendGameData,
                    AdmissionError.WrongRoomRole
                ),
                (
                    "spectator+joinRoom",
                    InRoom(RoomRole.Spectator),
                    ClientCommand.JoinRoom,
                    AdmissionError.AlreadyInRoom
                ),
                ("terminal+ping", Terminal(), ClientCommand.Ping, AdmissionError.NotConnected),
                (
                    "terminal+joinRoom",
                    Terminal(),
                    ClientCommand.JoinRoom,
                    AdmissionError.NotConnected
                ),
                (
                    "terminal+leaveRoom",
                    Terminal(),
                    ClientCommand.LeaveRoom,
                    AdmissionError.NotConnected
                ),
                (
                    "terminal+gameData",
                    Terminal(),
                    ClientCommand.SendGameData,
                    AdmissionError.NotConnected
                ),
            };

            foreach (
                (
                    string label,
                    SignalFishStateMachine machine,
                    ClientCommand command,
                    AdmissionError expected
                ) in rows
            )
            {
                bool admitted = machine.TryAdmit(command, out AdmissionError error);
                Assert.That(
                    admitted,
                    Is.EqualTo(expected == default(AdmissionError)),
                    "admission row failed (admitted): " + label
                );
                Assert.That(error, Is.EqualTo(expected), "admission row failed (error): " + label);
            }
        }

        [Test]
        public void PendingOperationForDirectedOpsFenceOthersDoNot()
        {
            Assert.That(
                SignalFishStateMachine.PendingOperationFor(ClientCommand.JoinRoom),
                Is.EqualTo(PendingRoomOperation.JoinPlayer)
            );
            Assert.That(
                SignalFishStateMachine.PendingOperationFor(ClientCommand.JoinAsSpectator),
                Is.EqualTo(PendingRoomOperation.JoinSpectator)
            );
            Assert.That(
                SignalFishStateMachine.PendingOperationFor(ClientCommand.LeaveRoom),
                Is.EqualTo(PendingRoomOperation.LeavePlayer)
            );
            Assert.That(
                SignalFishStateMachine.PendingOperationFor(ClientCommand.LeaveSpectator),
                Is.EqualTo(PendingRoomOperation.LeaveSpectator)
            );
            Assert.That(
                SignalFishStateMachine.PendingOperationFor(ClientCommand.Reconnect),
                Is.EqualTo(PendingRoomOperation.ReconnectPlayer)
            );
            Assert.That(SignalFishStateMachine.PendingOperationFor(ClientCommand.Ping), Is.Null);
            Assert.That(
                SignalFishStateMachine.PendingOperationFor(ClientCommand.SendGameData),
                Is.Null
            );
        }

        [Test]
        public void FenceReleaseTableTypedResultsOnly()
        {
            (
                string Label,
                PendingRoomOperation Armed,
                SessionEvent Released,
                PendingRoomOperation ExpectedPending,
                bool ExpectedMembership
            )[] rows =
            {
                (
                    "joinConfirmed",
                    PendingRoomOperation.JoinPlayer,
                    SessionEvent.Joined(SessionEventKind.RoomJoined, Membership(RoomRole.Player)),
                    default(PendingRoomOperation),
                    true
                ),
                (
                    "joinFailed",
                    PendingRoomOperation.JoinPlayer,
                    SessionEvent.From(SessionEventKind.RoomJoinFailed),
                    default(PendingRoomOperation),
                    false
                ),
                (
                    "genericErrorKeepsFence",
                    PendingRoomOperation.JoinPlayer,
                    SessionEvent.From(SessionEventKind.ServerError),
                    PendingRoomOperation.JoinPlayer,
                    false
                ),
                (
                    "wrongKindFailureKeepsFence",
                    PendingRoomOperation.JoinPlayer,
                    SessionEvent.From(SessionEventKind.SpectatorJoinFailed),
                    PendingRoomOperation.JoinPlayer,
                    false
                ),
                (
                    "spectatorConfirmed",
                    PendingRoomOperation.JoinSpectator,
                    SessionEvent.Joined(
                        SessionEventKind.SpectatorJoined,
                        Membership(RoomRole.Spectator)
                    ),
                    default(PendingRoomOperation),
                    true
                ),
                (
                    "spectatorFailed",
                    PendingRoomOperation.JoinSpectator,
                    SessionEvent.From(SessionEventKind.SpectatorJoinFailed),
                    default(PendingRoomOperation),
                    false
                ),
                (
                    "leaveConfirmed",
                    PendingRoomOperation.LeavePlayer,
                    SessionEvent.From(SessionEventKind.RoomLeft),
                    default(PendingRoomOperation),
                    false
                ),
                (
                    "wrongKindLeaveKeepsFence",
                    PendingRoomOperation.LeavePlayer,
                    SessionEvent.From(SessionEventKind.SpectatorLeft),
                    PendingRoomOperation.LeavePlayer,
                    false
                ),
                (
                    "spectatorLeaveConfirmed",
                    PendingRoomOperation.LeaveSpectator,
                    SessionEvent.From(SessionEventKind.SpectatorLeft),
                    default(PendingRoomOperation),
                    false
                ),
                (
                    "reconnectConfirmed",
                    PendingRoomOperation.ReconnectPlayer,
                    SessionEvent.Joined(SessionEventKind.Reconnected, Membership(RoomRole.Player)),
                    default(PendingRoomOperation),
                    true
                ),
                (
                    "reconnectFailed",
                    PendingRoomOperation.ReconnectPlayer,
                    SessionEvent.From(SessionEventKind.ReconnectionFailed),
                    default(PendingRoomOperation),
                    false
                ),
                (
                    "teardownReleases",
                    PendingRoomOperation.JoinPlayer,
                    SessionEvent.From(SessionEventKind.Disconnected),
                    default(PendingRoomOperation),
                    false
                ),
            };

            foreach (
                (
                    string label,
                    PendingRoomOperation armed,
                    SessionEvent released,
                    PendingRoomOperation expectedPending,
                    bool expectedMembership
                ) in rows
            )
            {
                SignalFishStateMachine machine = Fenced(armed);
                machine.Apply(released);
                Assert.That(
                    machine.PendingOperation,
                    Is.EqualTo(expectedPending),
                    "fence row failed (pending): " + label
                );
                Assert.That(
                    machine.Membership.IsPresent,
                    Is.EqualTo(expectedMembership),
                    "fence row failed (membership): " + label
                );
            }
        }

        [Test]
        public void FenceMismatchedSuccessIsIgnoredFailClosed()
        {
            SignalFishStateMachine leaving = InRoom(RoomRole.Player);
            leaving.Arm(PendingRoomOperation.LeavePlayer);
            leaving.Apply(
                SessionEvent.Joined(
                    SessionEventKind.SpectatorJoined,
                    Membership(RoomRole.Spectator)
                )
            );
            Assert.That(leaving.PendingOperation, Is.EqualTo(PendingRoomOperation.LeavePlayer));
            Assert.That(leaving.Membership, Is.EqualTo(Membership(RoomRole.Player)));

            SignalFishStateMachine reconnecting = Fenced(PendingRoomOperation.ReconnectPlayer);
            reconnecting.Apply(
                SessionEvent.Joined(SessionEventKind.RoomJoined, Membership(RoomRole.Player))
            );
            Assert.That(
                reconnecting.PendingOperation,
                Is.EqualTo(PendingRoomOperation.ReconnectPlayer)
            );
            Assert.That(reconnecting.Membership.IsPresent, Is.False);
        }

        [Test]
        public void FenceMismatchedLeaveIsIgnoredFailClosed()
        {
            SignalFishStateMachine leaving = InRoom(RoomRole.Player);
            leaving.Arm(PendingRoomOperation.LeavePlayer);
            leaving.Apply(SessionEvent.From(SessionEventKind.SpectatorLeft));

            Assert.That(leaving.PendingOperation, Is.EqualTo(PendingRoomOperation.LeavePlayer));
            Assert.That(leaving.Membership, Is.EqualTo(Membership(RoomRole.Player)));
            Assert.That(leaving.Phase, Is.EqualTo(ConnectionPhase.InRoom));

            SignalFishStateMachine spectatorLeaving = InRoom(RoomRole.Spectator);
            spectatorLeaving.Arm(PendingRoomOperation.LeaveSpectator);
            spectatorLeaving.Apply(SessionEvent.From(SessionEventKind.RoomLeft));

            Assert.That(
                spectatorLeaving.PendingOperation,
                Is.EqualTo(PendingRoomOperation.LeaveSpectator)
            );
            Assert.That(spectatorLeaving.Membership, Is.EqualTo(Membership(RoomRole.Spectator)));
        }

        [Test]
        public void FenceUnfencedLeaveIsAcceptedAsServerRemoval()
        {
            SignalFishStateMachine machine = InRoom(RoomRole.Player);
            machine.Apply(SessionEvent.From(SessionEventKind.SpectatorLeft));

            Assert.That(machine.Membership.IsPresent, Is.False);
            Assert.That(machine.Phase, Is.EqualTo(ConnectionPhase.Authenticated));
        }

        [Test]
        public void ApplyJoinConfirmedWithoutAuthenticationIsIgnored()
        {
            SignalFishStateMachine fresh = Fresh();
            fresh.Apply(
                SessionEvent.Joined(SessionEventKind.RoomJoined, Membership(RoomRole.Player))
            );

            Assert.That(fresh.Phase, Is.EqualTo(ConnectionPhase.Connecting));
            Assert.That(fresh.Membership.IsPresent, Is.False);
        }

        [Test]
        public void ApplyRepeatedAuthenticatedStaysAuthenticated()
        {
            SignalFishStateMachine machine = Fresh();
            machine.Apply(SessionEvent.Authenticated());
            machine.Apply(SessionEvent.Authenticated());

            Assert.That(machine.IsAuthenticated, Is.True);
            Assert.That(machine.Phase, Is.EqualTo(ConnectionPhase.Authenticated));
        }

        [Test]
        public void ApplyDefaultSessionEventIsInert()
        {
            SignalFishStateMachine machine = Fresh();
            machine.Apply(default(SessionEvent));

            Assert.That(machine.Phase, Is.EqualTo(ConnectionPhase.Connecting));
        }

        [Test]
        public void ArmNoneIsMisuse()
        {
            SignalFishStateMachine machine = Fresh();
            Assert.That(
                (Action)(() => machine.Arm(default(PendingRoomOperation))),
                Throws.ArgumentException
            );
        }

        [Test]
        public void StructEqualityMembershipAndEventsFieldWise()
        {
            RoomMembership membership = Membership(RoomRole.Player);
            Assert.That(membership, Is.EqualTo(Membership(RoomRole.Player)));
            Assert.That(membership, Is.Not.EqualTo(Membership(RoomRole.Spectator)));
            Assert.That(
                membership,
                Is.Not.EqualTo(new RoomMembership(RoomRole.Player, PlayerId, RoomId, "abc123"))
            );
            Assert.That(
                membership.GetHashCode(),
                Is.EqualTo(Membership(RoomRole.Player).GetHashCode())
            );
            Assert.That(
                default(RoomMembership).GetHashCode(),
                Is.EqualTo(default(RoomMembership).GetHashCode())
            );

            SessionEvent join = SessionEvent.Joined(
                SessionEventKind.RoomJoined,
                Membership(RoomRole.Player)
            );
            Assert.That(
                join,
                Is.EqualTo(
                    SessionEvent.Joined(SessionEventKind.RoomJoined, Membership(RoomRole.Player))
                )
            );
            Assert.That(join, Is.Not.EqualTo(SessionEvent.From(SessionEventKind.RoomJoined)));
            Assert.That(
                join,
                Is.Not.EqualTo(
                    SessionEvent.Joined(
                        SessionEventKind.SpectatorJoined,
                        Membership(RoomRole.Player)
                    )
                )
            );
            Assert.That(SessionEvent.Authenticated(), Is.Not.EqualTo(default(SessionEvent)));
        }

        [Test]
        public void TerminalAbsorbsAllLaterEvents()
        {
            SignalFishStateMachine machine = Fenced(PendingRoomOperation.JoinPlayer);
            machine.Apply(SessionEvent.From(SessionEventKind.Disconnected));
            machine.Apply(SessionEvent.From(SessionEventKind.TransportReady));
            machine.Apply(SessionEvent.Authenticated());
            machine.Apply(
                SessionEvent.Joined(SessionEventKind.RoomJoined, Membership(RoomRole.Player))
            );

            Assert.That(machine.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            Assert.That(machine.IsConnected, Is.False);
            Assert.That(machine.IsAuthenticated, Is.False);
            Assert.That(machine.Membership.IsPresent, Is.False);
            Assert.That(machine.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));
        }

        [Test]
        public void HotPathAdmitAndApplySteadyStateAllocatesNothing()
        {
            SignalFishStateMachine machine = new SignalFishStateMachine();
            RoomMembership membership = Membership(RoomRole.Player);
            long minDelta = long.MaxValue;
            for (int pass = 0; pass < 4; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    machine.TryAdmit(ClientCommand.Ping, out AdmissionError _);
                    machine.TryAdmit(ClientCommand.JoinRoom, out AdmissionError _);
                    machine.Arm(PendingRoomOperation.JoinPlayer);
                    machine.TryAdmit(ClientCommand.JoinRoom, out AdmissionError _);
                    machine.Apply(SessionEvent.Joined(SessionEventKind.RoomJoined, membership));
                    machine.TryAdmit(ClientCommand.SendGameData, out AdmissionError _);
                    machine.TryAdmit(ClientCommand.LeaveRoom, out AdmissionError _);
                    machine.Apply(SessionEvent.From(SessionEventKind.ServerError));
                    machine.Apply(SessionEvent.From(SessionEventKind.RoomLeft));
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }

            Assert.That(
                minDelta,
                Is.EqualTo(0),
                "Steady-state admission and event application must not allocate."
            );
        }

        // --- Helpers -----------------------------------------------------------------
        private static RoomMembership Membership(RoomRole role)
        {
            return new RoomMembership(role, PlayerId, RoomId, RoomCode);
        }

        private static SignalFishStateMachine Fresh()
        {
            return new SignalFishStateMachine();
        }

        private static SignalFishStateMachine AuthenticatedAtLeast()
        {
            SignalFishStateMachine machine = Fresh();
            machine.Apply(SessionEvent.From(SessionEventKind.TransportReady));
            machine.Apply(SessionEvent.Authenticated());
            return machine;
        }

        private static SignalFishStateMachine Fenced(PendingRoomOperation operation)
        {
            SignalFishStateMachine machine = AuthenticatedAtLeast();
            machine.Arm(operation);
            return machine;
        }

        private static SignalFishStateMachine InRoom(RoomRole role)
        {
            SignalFishStateMachine machine = AuthenticatedAtLeast();
            SessionEventKind kind =
                role == RoomRole.Player
                    ? SessionEventKind.RoomJoined
                    : SessionEventKind.SpectatorJoined;
            machine.Apply(SessionEvent.Joined(kind, Membership(role)));
            return machine;
        }

        private static SignalFishStateMachine Terminal()
        {
            SignalFishStateMachine machine = InRoom(RoomRole.Player);
            machine.Apply(SessionEvent.From(SessionEventKind.Disconnected));
            return machine;
        }

        private static void AssertMembership(SignalFishStateMachine machine, RoomRole expectedRole)
        {
            Assert.That(machine.Membership.IsPresent, Is.True);
            Assert.That(machine.Membership.Role, Is.EqualTo(expectedRole));
            Assert.That(machine.Membership.PlayerId, Is.EqualTo(PlayerId));
            Assert.That(machine.Membership.RoomId, Is.EqualTo(RoomId));
            Assert.That(machine.Membership.RoomCode, Is.EqualTo(RoomCode));
        }
    }
}
