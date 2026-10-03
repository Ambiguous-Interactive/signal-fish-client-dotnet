namespace SignalFish.Client.Tests.V3
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using SignalFish.Client.V3;

    /// <summary>
    /// Port of the Rust mesh-session test scenarios: authoritative plan
    /// replacement and liveness survival, WebRTC-scoped peer directives,
    /// selected-transport status gating, ICE pre-gather precedence,
    /// membership resets, host departure, and replay idempotency.
    /// </summary>
    [TestFixture]
    public class MeshSessionTests
    {
        [Test]
        public void EmptyDefault()
        {
            MeshSession session = new MeshSession();
            Assert.That(session.Peers, Is.Empty);
            Assert.That(session.Topology, Is.Null);
            Assert.That(session.IsP2p, Is.False);
        }

        [Test]
        public void AppliesPlan()
        {
            MeshSession session = new MeshSession();
            bool changed = session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(1, true), Peer(2, false) },
                    new[] { Ice("stun:a") }
                )
            );
            Assert.That(changed, Is.True);
            Assert.That(session.Topology, Is.EqualTo(SessionTopology.Mesh));
            Assert.That(session.Transport, Is.EqualTo(SessionTransport.WebRtc));
            Assert.That(session.Fallback, Is.EqualTo(SessionTransport.Relay));
            Assert.That(session.IsP2p, Is.True);
            Assert.That(session.Peers.Count, Is.EqualTo(2));
            Assert.That(session.Peer(Uuid(1)), Is.Not.Null);
            Assert.That(PeerOf(session, Uuid(1)).Initiate, Is.True);
            Assert.That(PeerOf(session, Uuid(2)).Initiate, Is.False);
            Assert.That(session.IceServers, Is.EqualTo(new[] { Ice("stun:a") }));
        }

        [Test]
        public void GenerationChangeClearsSurvivingPeerLiveness()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    "gen-12",
                    SessionTopology.Mesh,
                    SessionTransport.WebRtc,
                    null,
                    null,
                    new[] { Peer(1, false) },
                    Array.Empty<IceServerInfo>()
                )
            );
            session.Apply(Status(Uuid(1), "webrtc", true));
            Assert.That(PeerOf(session, Uuid(1)).Connected, Is.True);
            session.Apply(
                Plan(
                    "gen-13",
                    SessionTopology.Mesh,
                    SessionTransport.WebRtc,
                    null,
                    null,
                    new[] { Peer(1, false) },
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(PeerOf(session, Uuid(1)).Connected, Is.False);
        }

        [Test]
        public void ReplanReplacesPeersAndIceNotMerges()
        {
            /*
                Host re-election: plan A then plan B with a new host and a
                different peer set. Peers and ICE are replaced wholesale,
                not merged.
            */
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    SessionTopology.Host,
                    Uuid(1),
                    new[] { Peer(1, false), Peer(2, true) },
                    new[] { Ice("stun:a") }
                )
            );
            session.Apply(Status(Uuid(2), "webrtc", true));
            session.Apply(
                Plan(
                    SessionTopology.Host,
                    Uuid(3),
                    new[] { Peer(2, false), Peer(3, true) },
                    new[] { Ice("stun:b") }
                )
            );
            Assert.That(session.Host, Is.EqualTo(Uuid(3)));
            Assert.That(session.Peer(Uuid(1)), Is.Null, "peer 1 dropped on re-plan");
            Assert.That(session.Peer(Uuid(3)), Is.Not.Null);
            Assert.That(PeerOf(session, Uuid(2)).Connected, Is.False);
            Assert.That(PeerOf(session, Uuid(2)).Initiate, Is.False);
            Assert.That(session.IceServers, Is.EqualTo(new[] { Ice("stun:b") }));
        }

        [Test]
        public void DuplicateNewPeerIsIdempotent()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    Array.Empty<SessionPeerInfo>(),
                    Array.Empty<IceServerInfo>()
                )
            );
            session.Apply(NewPeer(Uuid(5), true));
            session.Apply(NewPeer(Uuid(5), true));
            Assert.That(session.Peers.Count, Is.EqualTo(1));
            Assert.That(PeerOf(session, Uuid(5)).Initiate, Is.True);
        }

        [Test]
        public void NewPeerRequiresSelectedWebRtcTransport()
        {
            /*
                The authority scopes NewPeer to WebRTC peer directives:
                without a plan, and on a relay plan, a peer directive must
                not invent a peer the controller will never connect.
            */
            MeshSession session = new MeshSession();
            Assert.That(session.Apply(NewPeer(Uuid(5), true)), Is.False);
            Assert.That(session.Peers, Is.Empty, "pre-plan NewPeer invented a peer");
            session.Apply(
                Plan(
                    SessionTopology.Relay,
                    SessionTransport.Relay,
                    null,
                    null,
                    Array.Empty<SessionPeerInfo>(),
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.Apply(NewPeer(Uuid(5), true)), Is.False);
            Assert.That(session.Peers, Is.Empty, "relay-plan NewPeer invented a peer");
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    Array.Empty<SessionPeerInfo>(),
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.Apply(NewPeer(Uuid(5), true)), Is.True);
            Assert.That(session.Peers.Count, Is.EqualTo(1));
        }

        [Test]
        public void NewPeerLatestWinsResetsLivenessOnFlagChange()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(2, true) },
                    Array.Empty<IceServerInfo>()
                )
            );
            session.Apply(Status(Uuid(2), "webrtc", true));
            Assert.That(PeerOf(session, Uuid(2)).Connected, Is.True);
            session.Apply(NewPeer(Uuid(2), false));
            Assert.That(session.Peers.Count, Is.EqualTo(1));
            Assert.That(PeerOf(session, Uuid(2)).Initiate, Is.False);
            Assert.That(
                PeerOf(session, Uuid(2)).Connected,
                Is.False,
                "offerer-role changes restart the selected-path handshake"
            );
        }

        [Test]
        public void TransportStatusUnknownPeerIgnored()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(1, true) },
                    Array.Empty<IceServerInfo>()
                )
            );
            bool changed = session.Apply(Status(Uuid(99), "webrtc", true));
            Assert.That(changed, Is.False);
            Assert.That(session.Peers.Count, Is.EqualTo(1));
            Assert.That(session.Peer(Uuid(99)), Is.Null);
        }

        [Test]
        public void TransportStatusUpdatesLivenessNotInitiate()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(1, true) },
                    Array.Empty<IceServerInfo>()
                )
            );
            session.Apply(Status(Uuid(1), "webrtc", true));
            MeshPeer peer = PeerOf(session, Uuid(1));
            Assert.That(peer.Connected, Is.True);
            Assert.That(peer.Initiate, Is.True, "initiate is server-authoritative, untouched");
        }

        [Test]
        public void LivenessTracksOnlySelectedTransportAcrossPlanTransitions()
        {
            MeshSession session = new MeshSession();
            Guid peerId = Uuid(1);
            session.Apply(
                Plan(
                    SessionTopology.Host,
                    peerId,
                    new[] { Peer(1, false) },
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.Apply(Status(peerId, "direct", true)), Is.False);
            Assert.That(PeerOf(session, peerId).Connected, Is.False);
            Assert.That(session.Apply(Status(peerId, "webrtc", true)), Is.True);
            Assert.That(PeerOf(session, peerId).Connected, Is.True);

            session.Apply(
                Plan(
                    SessionTopology.Host,
                    SessionTransport.Direct,
                    peerId,
                    new DirectEndpointInfo("192.0.2.1", 7777u),
                    new[] { Peer(1, false) },
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(PeerOf(session, peerId).Connected, Is.False);
            Assert.That(session.Apply(Status(peerId, "webrtc", true)), Is.False);
            Assert.That(session.Apply(Status(peerId, "direct", true)), Is.True);
            Assert.That(PeerOf(session, peerId).Connected, Is.True);

            session.Apply(
                Plan(
                    SessionTopology.Relay,
                    SessionTransport.Relay,
                    null,
                    null,
                    Array.Empty<SessionPeerInfo>(),
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.Peers, Is.Empty);
            Assert.That(session.IsP2p, Is.False);
        }

        [Test]
        public void PreGatherIceThenPlanPrecedence()
        {
            MeshSession session = new MeshSession();
            session.Apply(RoomJoined(Ice("stun:pre")));
            Assert.That(session.IceServers, Is.EqualTo(new[] { Ice("stun:pre") }));
            Assert.That(session.Topology, Is.Null, "pre-gather creates no plan/peers");
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(1, true) },
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.IceServers, Is.Empty);
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(1, true) },
                    new[] { Ice("stun:plan") }
                )
            );
            Assert.That(session.IceServers, Is.EqualTo(new[] { Ice("stun:plan") }));
        }

        [Test]
        public void PreGatherIdenticalReapplyReportsNoChange()
        {
            /*
                Re-applying a join with an ICE set identical to the one
                already held must report no change; a genuinely different
                set reports a change.
            */
            MeshSession session = new MeshSession();
            Assert.That(session.Apply(RoomJoined(Ice("stun:a"))), Is.True);
            Assert.That(
                session.Apply(RoomJoined(Ice("stun:a"))),
                Is.False,
                "identical pre-gather ICE must not report a change"
            );
            Assert.That(session.Apply(RoomJoined(Ice("stun:b"))), Is.True);
            Assert.That(session.IceServers, Is.EqualTo(new[] { Ice("stun:b") }));
        }

        [Test]
        public void RoomSpectatorAndDisconnectTransitionsResetAuthoritativeState()
        {
            PollEvent[] terminals =
            {
                PollEvent.Disconnected(new TransportClose(1006)),
                PollEvent.From(PollEventKind.RoomLeft),
                SpectatorJoinedReset(),
                PollEvent.From(PollEventKind.SpectatorLeft),
            };
            for (int i = 0; i < terminals.Length; i++)
            {
                MeshSession session = new MeshSession();
                session.Apply(
                    Plan(
                        SessionTopology.Mesh,
                        null,
                        new[] { Peer(1, true) },
                        new[] { Ice("stun:a") }
                    )
                );
                Assert.That(session.IsP2p, Is.True);
                Assert.That(session.Apply(terminals[i]), Is.True, terminals[i].Kind.ToString());
                Assert.That(session.Peers, Is.Empty);
                Assert.That(session.Topology, Is.Null);
                Assert.That(session.IceServers, Is.Empty);
                Assert.That(session.Apply(terminals[i]), Is.False, "reset must be idempotent");
            }
        }

        [Test]
        public void IgnoresUnrelatedEvents()
        {
            MeshSession session = new MeshSession();
            Assert.That(session.Apply(PollEvent.TransportReady()), Is.False);
            Assert.That(session.Apply(PollEvent.From(PollEventKind.Authenticated)), Is.False);
            Assert.That(session.Peers, Is.Empty);
        }

        [Test]
        public void PlayerLeftDropsPeerImmediatelyAndUnknownPeerIsNoOp()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(1, true), Peer(2, false) },
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.Apply(PlayerLeft(Uuid(2))), Is.True);
            Assert.That(session.Peer(Uuid(2)), Is.Null);
            Assert.That(session.Peer(Uuid(1)), Is.Not.Null);
            Assert.That(session.Apply(PlayerLeft(Uuid(2))), Is.False);
            Assert.That(session.Apply(PlayerLeft(Uuid(99))), Is.False);
        }

        [Test]
        public void PlayerLeftClearsDepartedHostAndEndpoint()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    SessionTopology.Host,
                    SessionTransport.Direct,
                    Uuid(1),
                    new DirectEndpointInfo("203.0.113.8", 7000u),
                    Array.Empty<SessionPeerInfo>(),
                    Array.Empty<IceServerInfo>()
                )
            );
            /*
                A departed host must not stay reachable through Host or
                DirectEndpoint; the replacement plan owns re-election. The
                plan carries no peer rows, so the change here reports
                exactly the host/endpoint view change.
            */
            Assert.That(session.Apply(PlayerLeft(Uuid(1))), Is.True);
            Assert.That(session.Host, Is.Null);
            Assert.That(session.DirectEndpoint, Is.Null);
            Assert.That(session.Peers, Is.Empty);

            session.Apply(
                Plan(
                    SessionTopology.Host,
                    Uuid(3),
                    new[] { Peer(3, false), Peer(4, false) },
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.Host, Is.EqualTo(Uuid(3)));
            Assert.That(session.Apply(PlayerLeft(Uuid(4))), Is.True);
            Assert.That(session.Host, Is.EqualTo(Uuid(3)));
        }

        [Test]
        public void TopologyTransitionMeshToHost()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(1, true) },
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.Topology, Is.EqualTo(SessionTopology.Mesh));
            Assert.That(session.Host, Is.Null);
            session.Apply(
                Plan(
                    SessionTopology.Host,
                    Uuid(9),
                    new[] { Peer(9, false) },
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.Topology, Is.EqualTo(SessionTopology.Host));
            Assert.That(session.Host, Is.EqualTo(Uuid(9)));
            Assert.That(session.IsP2p, Is.True);
            Assert.That(session.Peer(Uuid(1)), Is.Null);
        }

        [Test]
        public void RedundantUpdatesReturnFalse()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(1, true) },
                    Array.Empty<IceServerInfo>()
                )
            );
            Assert.That(session.Apply(Status(Uuid(1), "webrtc", true)), Is.True);
            Assert.That(session.Apply(Status(Uuid(1), "webrtc", true)), Is.False);
            session.Apply(NewPeer(Uuid(2), true));
            Assert.That(session.Apply(NewPeer(Uuid(2), true)), Is.False);
        }

        [Test]
        public void ReplayIsIdempotent()
        {
            /*
                Applying a full event sequence twice equals applying it once
                (reconnect replay may overlap with live events).
            */
            PollEvent[] sequence =
            {
                RoomJoined(Ice("stun:pre")),
                Plan(
                    SessionTopology.Mesh,
                    null,
                    new[] { Peer(1, true), Peer(2, false) },
                    new[] { Ice("stun:plan") }
                ),
                NewPeer(Uuid(3), true),
                Status(Uuid(1), "webrtc", true),
            };

            MeshSession once = new MeshSession();
            MeshSession twice = new MeshSession();
            for (int i = 0; i < sequence.Length; i++)
            {
                once.Apply(sequence[i]);
            }

            for (int i = 0; i < sequence.Length * 2; i++)
            {
                twice.Apply(sequence[i % sequence.Length]);
            }

            Assert.That(once.Topology, Is.EqualTo(twice.Topology));
            Assert.That(once.Host, Is.EqualTo(twice.Host));
            Assert.That(
                SessionPlanMessage.SequenceEquals(once.IceServers, twice.IceServers),
                Is.True
            );
            Assert.That(SessionPlanMessage.SequenceEquals(once.Peers, twice.Peers), Is.True);
        }

        [Test]
        public void ReconnectFencesTheOldPlanUntilAFreshLivePlanArrives()
        {
            MeshSession session = new MeshSession();
            session.Apply(
                Plan(SessionTopology.Mesh, null, new[] { Peer(1, true) }, new[] { Ice("stun:old") })
            );
            Assert.That(session.Topology, Is.EqualTo(SessionTopology.Mesh));

            Assert.That(session.Apply(Reconnected(Ice("stun:refreshed"))), Is.True);
            Assert.That(session.Topology, Is.Null);
            Assert.That(session.Generation, Is.Null);
            Assert.That(session.Peers, Is.Empty);
            Assert.That(session.IceServers, Is.EqualTo(new[] { Ice("stun:refreshed") }));
        }

        [Test]
        public void ReconnectWithoutAuthoritativeStateIsPreGatherOnly()
        {
            /*
                The common case (server re-sends a live plan after the
                baseline): the reconnect only seeds pre-gather ICE and
                creates no peers.
            */
            MeshSession session = new MeshSession();
            Assert.That(session.Apply(Reconnected(Ice("stun:pre"))), Is.True);
            Assert.That(session.Topology, Is.Null, "no plan means no topology yet");
            Assert.That(session.Peers, Is.Empty);
            Assert.That(session.IceServers, Is.EqualTo(new[] { Ice("stun:pre") }));
            Assert.That(session.Apply(Reconnected(Ice("stun:pre"))), Is.False);
        }

        private static Guid Uuid(long n)
        {
            return new Guid((int)n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        /// <summary>Mirrors the Rust tests' <c>peer(id).unwrap()</c> accessor.</summary>
        private static MeshPeer PeerOf(MeshSession session, Guid playerId)
        {
            MeshPeer? peer = session.Peer(playerId);
            Assert.That(peer, Is.Not.Null, "peer missing: " + playerId);
            return peer.GetValueOrDefault();
        }

        private static SessionPeerInfo Peer(long id, bool initiate)
        {
            return new SessionPeerInfo(Uuid(id), $"P{id}", false, initiate);
        }

        private static IceServerInfo Ice(string url)
        {
            return new IceServerInfo(new[] { url }, null, null);
        }

        private static PollEvent Plan(
            SessionTopology topology,
            Guid? host,
            SessionPeerInfo[] peers,
            IceServerInfo[] iceServers
        )
        {
            return Plan(null, topology, SessionTransport.WebRtc, host, null, peers, iceServers);
        }

        private static PollEvent Plan(
            SessionTopology topology,
            SessionTransport transport,
            Guid? host,
            DirectEndpointInfo? directEndpoint,
            SessionPeerInfo[] peers,
            IceServerInfo[] iceServers
        )
        {
            return Plan(null, topology, transport, host, directEndpoint, peers, iceServers);
        }

        private static PollEvent Plan(
            string? generation,
            SessionTopology topology,
            SessionTransport transport,
            Guid? host,
            DirectEndpointInfo? directEndpoint,
            SessionPeerInfo[] peers,
            IceServerInfo[] iceServers
        )
        {
            return PollEvent.FromSessionPlan(
                new SessionPlanMessage(
                    generation!,
                    topology,
                    transport,
                    host,
                    directEndpoint,
                    peers,
                    iceServers,
                    SessionTransport.Relay
                ),
                ReadOnlyMemory<byte>.Empty
            );
        }

        private static PollEvent NewPeer(Guid peerId, bool youInitiate)
        {
            return PollEvent.FromNewPeer(
                new NewPeerMessage(peerId, youInitiate),
                ReadOnlyMemory<byte>.Empty
            );
        }

        private static PollEvent Status(Guid peerId, string transport, bool connected)
        {
            return PollEvent.FromPeerTransportStatus(
                new PeerTransportStatusMessage(peerId, transport, connected),
                ReadOnlyMemory<byte>.Empty
            );
        }

        private static PollEvent PlayerLeft(Guid playerId)
        {
            return PollEvent.FromPlayerLeft(playerId, ReadOnlyMemory<byte>.Empty);
        }

        private static PollEvent RoomJoined(params IceServerInfo[] iceServers)
        {
            return MembershipEvent(PollEventKind.RoomJoined, RoomRole.Player, iceServers);
        }

        private static PollEvent Reconnected(params IceServerInfo[] iceServers)
        {
            return MembershipEvent(PollEventKind.Reconnected, RoomRole.Player, iceServers);
        }

        private static PollEvent SpectatorJoinedReset()
        {
            return MembershipEvent(
                PollEventKind.SpectatorJoined,
                RoomRole.Spectator,
                Array.Empty<IceServerInfo>()
            );
        }

        private static PollEvent MembershipEvent(
            PollEventKind kind,
            RoomRole role,
            IceServerInfo[] iceServers
        )
        {
            return PollEvent.FromMembership(
                kind,
                new RoomMembership(role, Uuid(0), Uuid(0), "R"),
                Snapshot(iceServers),
                ReadOnlyMemory<byte>.Empty
            );
        }

        private static RoomSnapshot Snapshot(IceServerInfo[] iceServers)
        {
            return new RoomSnapshot(
                "g",
                4u,
                false,
                false,
                "waiting",
                null,
                null,
                Array.Empty<PlayerInfo>(),
                Array.Empty<SpectatorInfo>(),
                iceServers
            );
        }
    }
}
