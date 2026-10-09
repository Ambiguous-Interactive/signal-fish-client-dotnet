namespace SignalFish.Client.E2E
{
    using System;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// The live-server reconnection drill: the recovery flow the codec and
    /// state-machine suites pin with fakes, proven against the real
    /// server's reconnection manager. The shape mirrors the server's own
    /// executable spec (tests/reconnection_replay_e2e.rs,
    /// reconnect_succeeds_with_only_the_wire_token): a v3 joiner captures
    /// the token from <c>RoomJoined</c>, an abrupt socket loss arms it, a
    /// fresh authenticated connection reclaims the seat, and the token
    /// rotates.
    /// </summary>
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class ReconnectionConformanceTests
    {
        private static readonly string[] RelayOnlyTransports = { "relay" };

        private static readonly string[] RelayOnlyTopologies = { "relay" };

        /*
            The anchor witnesses the whole drill, and the CI server reaps
            inbound-silent connections after ~3 s. Pings fire only while
            the harness polls the anchor, so every phase boundary waits
            on the anchor, and the 500 ms cadence feeds it through a
            stalled phase.
        */
        private static readonly PollingClientOptions AnchorOptions = new PollingClientOptions(
            heartbeatIntervalMilliseconds: 500,
            heartbeatTimeoutMilliseconds: 10_000
        );

        [OneTimeSetUp]
        public void RequireLiveServer()
        {
            if (!E2EEnvironment.IsConfigured)
            {
                Assert.Ignore(
                    "SIGNALFISH_E2E_URL is not set; the reconnection drill needs a live server "
                        + "(scripts/run-e2e.ps1)."
                );
            }
        }

        /// <summary>
        /// The reclaim path: a dropped v3 seat reclaims its exact membership
        /// through the wire token on a fresh authenticated connection, the
        /// token rotates, the reclaim carries the current roster (not the
        /// join-time one), the anchor witnesses the departure and the
        /// reclaim, and the relay floor flows again — gameplay sent during
        /// the gap never replays.
        /// </summary>
        [Test]
        public async Task DroppedSeatReclaimsItsMembershipWithTheWireToken()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectV3ClientAsync(
                RelayOnlyTransports,
                RelayOnlyTopologies,
                AnchorOptions
            );
            (SignalFishPollingClient bob, PartitionProxy proxy) =
                await E2EHarness.ConnectProxiedV3ClientAsync(
                    RelayOnlyTransports,
                    RelayOnlyTopologies
                );

            try
            {
                string gameName = E2EHarness.GameName();
                RoomMembership bobSeat = await E2EHarness.JoinRoomAsync(bob, gameName, "bob");
                await E2EHarness.JoinRoomAsync(
                    alice,
                    gameName,
                    "alice",
                    roomCode: bobSeat.RoomCode
                );

                string wireToken =
                    bob.Snapshot.ReconnectionToken
                    ?? throw new InvalidOperationException(
                        "a v3 RoomJoined must carry the reconnection token"
                    );

                /*
                    The drop: disposing the proxy closes both relay legs, so
                    the server's read ends in a plain TCP close — no close
                    frame, exactly what a dead connection looks like — and
                    the token's reclaim window opens.
                */
                await proxy.DisposeAsync();
                await E2EHarness.WaitForEventAsync(bob, e => e.Kind == PollEventKind.Disconnected);

                /*
                    The anchor's PlayerLeft broadcast is the sync point the
                    server's own drill uses: it fires only after the
                    reconnection record is registered, so the reclaim below
                    cannot race the registration.
                */
                await E2EHarness.WaitForEventAsync(
                    alice,
                    e => e.Kind == PollEventKind.PlayerLeft && e.LeftPlayerId == bobSeat.PlayerId
                );

                Assert.That(
                    E2EHarness.SendRelayPayload(alice, @"{""phase"": ""away""}").Accepted,
                    Is.True
                );

                SignalFishPollingClient carol = await E2EHarness.ConnectV3ClientAsync(
                    RelayOnlyTransports,
                    RelayOnlyTopologies
                );
                try
                {
                    /*
                        Carol joins during the gap: a control event for the
                        replay ring and a roster fact the reclaim's snapshot
                        must carry.
                    */
                    RoomMembership carolSeat = await E2EHarness.JoinRoomAsync(
                        carol,
                        gameName,
                        "carol",
                        roomCode: bobSeat.RoomCode
                    );

                    /*
                        The anchor's mid-gap join witness: it proves the
                        roster broadcast reaches the surviving seats, and
                        it splits the setup so the anchor is polled between
                        every handshake (see AnchorOptions).
                    */
                    await E2EHarness.WaitForEventAsync(
                        alice,
                        e =>
                            e.Kind == PollEventKind.PlayerJoined
                            && e.PlayerJoined.Player.Id == carolSeat.PlayerId
                    );

                    SignalFishPollingClient bob2 = await E2EHarness.ConnectV3ClientAsync(
                        RelayOnlyTransports,
                        RelayOnlyTopologies
                    );
                    try
                    {
                        Assert.That(
                            bob2.SendReconnect(
                                new ReconnectMessage(
                                    bobSeat.PlayerId.ToString(),
                                    bobSeat.RoomId.ToString(),
                                    wireToken
                                )
                            ).Accepted,
                            Is.True
                        );
                        PollEvent reconnected = await E2EHarness.WaitForEventAsync(
                            bob2,
                            e =>
                                e.Kind == PollEventKind.Reconnected
                                || e.Kind == PollEventKind.ReconnectionFailed
                        );
                        Assert.That(
                            reconnected.Kind,
                            Is.EqualTo(PollEventKind.Reconnected),
                            $"the reclaim must succeed (got {reconnected.Kind}: "
                                + $"{reconnected.Failure.ErrorCode} — {reconnected.Failure.Reason})"
                        );
                        Assert.That(reconnected.Membership, Is.EqualTo(bobSeat));

                        string rotated =
                            bob2.Snapshot.ReconnectionToken
                            ?? throw new InvalidOperationException(
                                "every successful reclaim rotates in a fresh token"
                            );
                        Assert.That(
                            rotated,
                            Is.Not.EqualTo(wireToken),
                            "the consumed token must not be re-issued"
                        );

                        bool carolPresent = false;
                        foreach (PlayerInfo player in reconnected.Snapshot.CurrentPlayers)
                        {
                            if (player.Id == carolSeat.PlayerId)
                            {
                                carolPresent = true;
                                break;
                            }
                        }

                        Assert.That(
                            carolPresent,
                            Is.True,
                            "the reclaim must carry the current roster — carol joined during the gap"
                        );

                        await E2EHarness.WaitForEventAsync(
                            alice,
                            e =>
                                e.Kind == PollEventKind.PlayerReconnected
                                && e.LeftPlayerId == bobSeat.PlayerId
                        );

                        Assert.That(
                            E2EHarness.SendRelayPayload(alice, @"{""phase"": ""live""}").Accepted,
                            Is.True
                        );
                        PollEvent live = await E2EHarness.WaitForEventAsync(
                            bob2,
                            e => e.Kind == PollEventKind.GameData
                        );
                        Assert.That(
                            E2EHarness.PayloadJsonEquals(
                                live.GameData.Payload.Span,
                                @"{""phase"": ""live""}"
                            ),
                            Is.True,
                            "the reclaimed seat's first relayed frame is the fresh one — gameplay never replays"
                        );
                    }
                    finally
                    {
                        await bob2.DisposeAsync();
                    }
                }
                finally
                {
                    await carol.DisposeAsync();
                }
            }
            finally
            {
                await bob.DisposeAsync();
                await alice.DisposeAsync();
                await proxy.DisposeAsync();
            }
        }

        /// <summary>
        /// One token, one reclaim: once the wire token has reclaimed the
        /// seat, the consumed token is refused with the typed
        /// <c>RECONNECTION_TOKEN_INVALID</c>, and the rotated replacement
        /// reclaims again on the same fresh connection — rotation arms the
        /// next window, it does not end the session.
        /// </summary>
        [Test]
        public async Task ConsumedTokenIsRefusedAndTheRotatedTokenReclaimsAgain()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectV3ClientAsync(
                RelayOnlyTransports,
                RelayOnlyTopologies,
                AnchorOptions
            );
            (SignalFishPollingClient bob, PartitionProxy firstProxy) =
                await E2EHarness.ConnectProxiedV3ClientAsync(
                    RelayOnlyTransports,
                    RelayOnlyTopologies
                );

            try
            {
                string gameName = E2EHarness.GameName();
                RoomMembership bobSeat = await E2EHarness.JoinRoomAsync(bob, gameName, "bob");
                await E2EHarness.JoinRoomAsync(
                    alice,
                    gameName,
                    "alice",
                    roomCode: bobSeat.RoomCode
                );
                string wireToken =
                    bob.Snapshot.ReconnectionToken
                    ?? throw new InvalidOperationException(
                        "a v3 RoomJoined must carry the reconnection token"
                    );

                await firstProxy.DisposeAsync();
                await E2EHarness.WaitForEventAsync(bob, e => e.Kind == PollEventKind.Disconnected);
                await E2EHarness.WaitForEventAsync(
                    alice,
                    e => e.Kind == PollEventKind.PlayerLeft && e.LeftPlayerId == bobSeat.PlayerId
                );

                /*
                    The first reclaim consumes the wire token. The
                    replacement rides its own proxy so its socket can be
                    dropped the same way: the re-drop re-arms the record with
                    the rotated token.
                */
                (SignalFishPollingClient bob2, PartitionProxy secondProxy) =
                    await E2EHarness.ConnectProxiedV3ClientAsync(
                        RelayOnlyTransports,
                        RelayOnlyTopologies
                    );
                try
                {
                    Assert.That(
                        bob2.SendReconnect(
                            new ReconnectMessage(
                                bobSeat.PlayerId.ToString(),
                                bobSeat.RoomId.ToString(),
                                wireToken
                            )
                        ).Accepted,
                        Is.True
                    );
                    PollEvent firstReclaim = await E2EHarness.WaitForEventAsync(
                        bob2,
                        e =>
                            e.Kind == PollEventKind.Reconnected
                            || e.Kind == PollEventKind.ReconnectionFailed
                    );
                    Assert.That(
                        firstReclaim.Kind,
                        Is.EqualTo(PollEventKind.Reconnected),
                        $"the first reclaim must succeed (got {firstReclaim.Kind}: "
                            + $"{firstReclaim.Failure.ErrorCode} — {firstReclaim.Failure.Reason})"
                    );
                    Assert.That(firstReclaim.Membership, Is.EqualTo(bobSeat));

                    string rotated =
                        bob2.Snapshot.ReconnectionToken
                        ?? throw new InvalidOperationException(
                            "every successful reclaim rotates in a fresh token"
                        );
                    Assert.That(
                        rotated,
                        Is.Not.EqualTo(wireToken),
                        "the consumed token must not be re-issued"
                    );

                    /*
                        The anchor witnesses the first reclaim, which also
                        keeps it fed before the re-drop and its
                        PlayerLeft wait (see AnchorOptions).
                    */
                    await E2EHarness.WaitForEventAsync(
                        alice,
                        e =>
                            e.Kind == PollEventKind.PlayerReconnected
                            && e.LeftPlayerId == bobSeat.PlayerId
                    );

                    await secondProxy.DisposeAsync();
                    await E2EHarness.WaitForEventAsync(
                        bob2,
                        e => e.Kind == PollEventKind.Disconnected
                    );
                    await E2EHarness.WaitForEventAsync(
                        alice,
                        e =>
                            e.Kind == PollEventKind.PlayerLeft && e.LeftPlayerId == bobSeat.PlayerId
                    );

                    SignalFishPollingClient bob3 = await E2EHarness.ConnectV3ClientAsync(
                        RelayOnlyTransports,
                        RelayOnlyTopologies
                    );
                    try
                    {
                        Assert.That(
                            bob3.SendReconnect(
                                new ReconnectMessage(
                                    bobSeat.PlayerId.ToString(),
                                    bobSeat.RoomId.ToString(),
                                    wireToken
                                )
                            ).Accepted,
                            Is.True
                        );
                        PollEvent rejection = await E2EHarness.WaitForEventAsync(
                            bob3,
                            e =>
                                e.Kind == PollEventKind.Reconnected
                                || e.Kind == PollEventKind.ReconnectionFailed
                        );
                        Assert.That(
                            rejection.Kind,
                            Is.EqualTo(PollEventKind.ReconnectionFailed),
                            "the consumed token must never reclaim the seat again"
                        );
                        Assert.That(
                            rejection.Failure.ErrorCode,
                            Is.EqualTo("RECONNECTION_TOKEN_INVALID")
                        );

                        Assert.That(
                            bob3.SendReconnect(
                                new ReconnectMessage(
                                    bobSeat.PlayerId.ToString(),
                                    bobSeat.RoomId.ToString(),
                                    rotated
                                )
                            ).Accepted,
                            Is.True
                        );
                        PollEvent secondReclaim = await E2EHarness.WaitForEventAsync(
                            bob3,
                            e =>
                                e.Kind == PollEventKind.Reconnected
                                || e.Kind == PollEventKind.ReconnectionFailed
                        );
                        Assert.That(
                            secondReclaim.Kind,
                            Is.EqualTo(PollEventKind.Reconnected),
                            $"the rotated token must reclaim (got {secondReclaim.Kind}: "
                                + $"{secondReclaim.Failure.ErrorCode} — "
                                + $"{secondReclaim.Failure.Reason})"
                        );
                        Assert.That(secondReclaim.Membership, Is.EqualTo(bobSeat));
                    }
                    finally
                    {
                        await bob3.DisposeAsync();
                    }
                }
                finally
                {
                    await bob2.DisposeAsync();
                    await secondProxy.DisposeAsync();
                }
            }
            finally
            {
                await bob.DisposeAsync();
                await alice.DisposeAsync();
                await firstProxy.DisposeAsync();
            }
        }
    }
}
