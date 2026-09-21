namespace SignalFish.Client.Tests.Core
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Core;

    /// <summary>
    /// M3.5 red-green anchor: the coherent session snapshot. Tables pin the
    /// Rust-docs phase table (docs/client.md), the membership mirror, the
    /// reconnection-token lifecycle (captured on the player baseline,
    /// rotated on reconnect, cleared by spectator baselines, confirmed
    /// exits, and terminal teardown), and the token redaction in ToString.
    /// </summary>
    [TestFixture]
    public class ClientSnapshotTests
    {
        private const string RoomCode = "ABC123";
        private const string JoinToken = "tok-join-1";
        private const string RotatedToken = "tok-rotated-2";

        private static readonly Guid PlayerId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        private static readonly Guid RoomId = new Guid("7c9e6679-7425-40de-944b-e07fc1f90ae7");

        [Test]
        public void SnapshotPhaseTableMatchesRustDocs()
        {
            (string Label, SignalFishStateMachine Machine, ClientSnapshot Expected)[] rows =
            {
                (
                    "connecting",
                    Fresh(),
                    Snapshot(
                        connected: true,
                        transportReady: false,
                        authenticated: false,
                        role: null
                    )
                ),
                (
                    "transport ready",
                    TransportReady(),
                    Snapshot(
                        connected: true,
                        transportReady: true,
                        authenticated: false,
                        role: null
                    )
                ),
                (
                    "authenticated, outside a room",
                    AuthenticatedAtLeast(),
                    Snapshot(connected: true, transportReady: true, authenticated: true, role: null)
                ),
                (
                    "in a room (player)",
                    InRoom(RoomRole.Player),
                    Snapshot(
                        connected: true,
                        transportReady: true,
                        authenticated: true,
                        role: RoomRole.Player
                    )
                ),
                (
                    "in a room (spectator)",
                    InRoom(RoomRole.Spectator),
                    Snapshot(
                        connected: true,
                        transportReady: true,
                        authenticated: true,
                        role: RoomRole.Spectator
                    )
                ),
                (
                    "terminal",
                    Terminal(),
                    Snapshot(
                        connected: false,
                        transportReady: false,
                        authenticated: false,
                        role: null
                    )
                ),
            };

            foreach (
                (string label, SignalFishStateMachine machine, ClientSnapshot expected) in rows
            )
            {
                Assert.That(machine.CreateSnapshot(), Is.EqualTo(expected), label);
            }
        }

        [Test]
        public void SnapshotMembershipFieldsMirrorMembershipInvariant()
        {
            SignalFishStateMachine machine = InRoom(RoomRole.Player);
            ClientSnapshot joined = machine.CreateSnapshot();
            Assert.That(joined.PlayerId, Is.EqualTo(PlayerId));
            Assert.That(joined.RoomId, Is.EqualTo(RoomId));
            Assert.That(joined.RoomCode, Is.EqualTo(RoomCode));

            machine.Apply(SessionEvent.From(SessionEventKind.RoomLeft));
            ClientSnapshot left = machine.CreateSnapshot();
            Assert.That(left.Role, Is.Null);
            Assert.That(left.PlayerId, Is.Null);
            Assert.That(left.RoomId, Is.Null);
            Assert.That(left.RoomCode, Is.Null);
            Assert.That(left.Connected, Is.True);
        }

        [Test]
        public void PlayerBaselineCapturesReconnectionTokenWhenPresent()
        {
            SignalFishStateMachine machine = AuthenticatedAtLeast();
            machine.Apply(Joined(SessionEventKind.RoomJoined, RoomRole.Player, JoinToken));

            Assert.That(machine.CreateSnapshot().ReconnectionToken, Is.EqualTo(JoinToken));
        }

        [Test]
        public void BaselineWithoutTokenKeepsTokenAbsent()
        {
            /*
                The v2 wire carries no reconnection_token; the v2 floor
                treats the field as optional (tolerant capture, null when
                the frame omits it).
            */
            SignalFishStateMachine machine = AuthenticatedAtLeast();
            machine.Apply(Joined(SessionEventKind.RoomJoined, RoomRole.Player, token: null));

            Assert.That(machine.CreateSnapshot().ReconnectionToken, Is.Null);
        }

        [Test]
        public void ReconnectedRotatesToken()
        {
            SignalFishStateMachine machine = AuthenticatedAtLeast();
            machine.Apply(Joined(SessionEventKind.RoomJoined, RoomRole.Player, JoinToken));
            machine.Apply(Joined(SessionEventKind.Reconnected, RoomRole.Player, RotatedToken));

            Assert.That(machine.CreateSnapshot().ReconnectionToken, Is.EqualTo(RotatedToken));
        }

        [Test]
        public void SpectatorBaselineClearsPlayerToken()
        {
            /*
                Rust parity: "spectator baselines carry no reconnection
                token" — a same-room spectator join discards the pending
                player token (the protocol has no spectator reconnect).
            */
            SignalFishStateMachine machine = AuthenticatedAtLeast();
            machine.Apply(Joined(SessionEventKind.RoomJoined, RoomRole.Player, JoinToken));
            machine.Apply(
                Joined(SessionEventKind.SpectatorJoined, RoomRole.Spectator, token: null)
            );

            ClientSnapshot snapshot = machine.CreateSnapshot();
            Assert.That(snapshot.Role, Is.EqualTo(RoomRole.Spectator));
            Assert.That(snapshot.ReconnectionToken, Is.Null);
        }

        [Test]
        public void ConfirmedExitsClearToken()
        {
            (SessionEventKind Exit, RoomRole Role)[] rows =
            {
                (SessionEventKind.RoomLeft, RoomRole.Player),
                (SessionEventKind.SpectatorLeft, RoomRole.Spectator),
            };

            foreach ((SessionEventKind exit, RoomRole role) in rows)
            {
                SignalFishStateMachine machine = InRoomWithToken(role);
                machine.Apply(SessionEvent.From(exit));
                Assert.That(machine.CreateSnapshot().ReconnectionToken, Is.Null, exit.ToString());
            }
        }

        [Test]
        public void TerminalTeardownClearsToken()
        {
            SignalFishStateMachine machine = InRoomWithToken(RoomRole.Player);
            machine.Apply(SessionEvent.From(SessionEventKind.Disconnected));

            Assert.That(machine.CreateSnapshot().ReconnectionToken, Is.Null);
        }

        [Test]
        public void FenceMismatchedJoinKeepsMembershipAndToken()
        {
            /*
                Fail-closed coherence: a success kind that does not answer
                the fenced operation is ignored whole — membership, fence,
                and token stay put.
            */
            SignalFishStateMachine machine = AuthenticatedAtLeast();
            machine.Arm(PendingRoomOperation.JoinSpectator);
            machine.Apply(Joined(SessionEventKind.RoomJoined, RoomRole.Player, JoinToken));

            ClientSnapshot snapshot = machine.CreateSnapshot();
            Assert.That(snapshot.Role, Is.Null);
            Assert.That(snapshot.ReconnectionToken, Is.Null);
            Assert.That(machine.PendingOperation, Is.EqualTo(PendingRoomOperation.JoinSpectator));
        }

        [Test]
        public void SnapshotEqualityComparesAllFields()
        {
            /*
                Two machines driven through identical facts must produce
                equal snapshots; a differing token must break equality.
            */
            SignalFishStateMachine first = InRoomWithToken(RoomRole.Player);
            SignalFishStateMachine second = InRoomWithToken(RoomRole.Player);
            Assert.That(first.CreateSnapshot(), Is.EqualTo(second.CreateSnapshot()));

            SignalFishStateMachine untstoned = AuthenticatedAtLeast();
            untstoned.Apply(Joined(SessionEventKind.RoomJoined, RoomRole.Player, token: null));
            Assert.That(first.CreateSnapshot(), Is.Not.EqualTo(untstoned.CreateSnapshot()));

            Assert.That(first.CreateSnapshot(), Is.Not.EqualTo(default(ClientSnapshot)));
            Assert.That(default(ClientSnapshot).Connected, Is.False);
        }

        [Test]
        public void ToStringRedactsReconnectionToken()
        {
            SignalFishStateMachine machine = InRoomWithToken(RoomRole.Player);
            string text = machine.CreateSnapshot().ToString();
            Assert.That(text, Does.Contain("<redacted>"));
            Assert.That(text, Does.Not.Contain(JoinToken));

            SignalFishStateMachine anonymous = Fresh();
            Assert.That(anonymous.CreateSnapshot().ToString(), Does.Contain("<none>"));
        }

        // --- Helpers -----------------------------------------------------------------
        private static ClientSnapshot Snapshot(
            bool connected,
            bool transportReady,
            bool authenticated,
            RoomRole? role
        )
        {
            return new ClientSnapshot(
                connected,
                transportReady,
                authenticated,
                role,
                role is null ? null : PlayerId,
                role is null ? null : RoomId,
                role is null ? null : RoomCode,
                null
            );
        }

        private static SessionEvent Joined(SessionEventKind kind, RoomRole role, string? token)
        {
            return SessionEvent.Joined(kind, Membership(role), token);
        }

        private static RoomMembership Membership(RoomRole role)
        {
            return new RoomMembership(role, PlayerId, RoomId, RoomCode);
        }

        private static SignalFishStateMachine Fresh()
        {
            return new SignalFishStateMachine();
        }

        private static SignalFishStateMachine TransportReady()
        {
            SignalFishStateMachine machine = Fresh();
            machine.Apply(SessionEvent.From(SessionEventKind.TransportReady));
            return machine;
        }

        private static SignalFishStateMachine AuthenticatedAtLeast()
        {
            SignalFishStateMachine machine = TransportReady();
            machine.Apply(SessionEvent.Authenticated());
            return machine;
        }

        private static SignalFishStateMachine InRoom(RoomRole role)
        {
            SignalFishStateMachine machine = AuthenticatedAtLeast();
            machine.Apply(Joined(JoinKind(role), role, token: null));
            return machine;
        }

        private static SignalFishStateMachine InRoomWithToken(RoomRole role)
        {
            SignalFishStateMachine machine = AuthenticatedAtLeast();
            machine.Apply(Joined(JoinKind(role), role, JoinToken));
            return machine;
        }

        private static SessionEventKind JoinKind(RoomRole role)
        {
            return role == RoomRole.Player
                ? SessionEventKind.RoomJoined
                : SessionEventKind.SpectatorJoined;
        }

        private static SignalFishStateMachine Terminal()
        {
            SignalFishStateMachine machine = InRoom(RoomRole.Player);
            machine.Apply(SessionEvent.From(SessionEventKind.Disconnected));
            return machine;
        }
    }
}
