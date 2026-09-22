namespace SignalFish.Client.Tests.Reconnection
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Reconnection;

    /// <summary>
    /// Red-green anchors for the manual reconnection support surface: the
    /// Rust client's end-to-end recovery policy as data — the persisted
    /// seat triple captured from a snapshot, and the ReconnectionFailed
    /// decision tree (expired/invalid -&gt; fresh join; seat held -&gt;
    /// wait; anything else -&gt; backoff retry).
    /// </summary>
    [TestFixture]
    public class ReconnectRecoveryTests
    {
        private static readonly Guid PlayerId = new Guid("00000000-0000-0000-0000-00000000000a");

        private static readonly Guid RoomId = new Guid("11111111-1111-1111-1111-111111111111");

        [TestCase("RECONNECTION_EXPIRED", ReconnectAction.FreshJoin)]
        [TestCase("RECONNECTION_TOKEN_INVALID", ReconnectAction.FreshJoin)]
        [TestCase("PLAYER_ALREADY_CONNECTED", ReconnectAction.WaitForSeat)]
        [TestCase("RECONNECTION_FAILED", ReconnectAction.RetryWithBackoff)]
        [TestCase("SERVER_DRAINING", ReconnectAction.RetryWithBackoff)]
        [TestCase("BANNED", ReconnectAction.RetryWithBackoff)]
        [TestCase("SOME_FUTURE_CODE", ReconnectAction.RetryWithBackoff)]
        [TestCase("", ReconnectAction.RetryWithBackoff)]
        [TestCase(null, ReconnectAction.RetryWithBackoff)]
        public void ClassifyImplementsTheRustRecoveryDecisionTree(
            string? errorCode,
            ReconnectAction expected
        )
        {
            Assert.That(ReconnectRecovery.Classify(errorCode), Is.EqualTo(expected));
        }

        [Test]
        public void CapturePersistsTheSeatTripleFromAPlayerBaseline()
        {
            ClientSnapshot snapshot = new ClientSnapshot(
                connected: true,
                transportReady: true,
                authenticated: true,
                role: RoomRole.Player,
                playerId: PlayerId,
                roomId: RoomId,
                roomCode: "ABC123",
                reconnectionToken: "seat-token-1"
            );

            Assert.That(
                ReconnectContext.TryCapture(snapshot, out ReconnectContext context),
                Is.True,
                "a confirmed player baseline with a token is capturable"
            );
            Assert.That(context.PlayerId, Is.EqualTo(PlayerId));
            Assert.That(context.RoomId, Is.EqualTo(RoomId));
            Assert.That(context.Token, Is.EqualTo("seat-token-1"));
        }

        [Test]
        public void CaptureRejectsBaselinesWithoutAUsableSeat()
        {
            // Spectator baselines carry no token (no spectator reconnect).
            Assert.That(
                ReconnectContext.TryCapture(
                    new ClientSnapshot(
                        true,
                        true,
                        true,
                        RoomRole.Spectator,
                        PlayerId,
                        RoomId,
                        "ABC123",
                        null
                    ),
                    out _
                ),
                Is.False
            );

            // Not in a room: nothing to restore.
            Assert.That(
                ReconnectContext.TryCapture(
                    new ClientSnapshot(true, true, true, null, null, null, null, "seat-token-1"),
                    out _
                ),
                Is.False
            );

            // Post-teardown snapshot: the machine cleared the token.
            Assert.That(
                ReconnectContext.TryCapture(
                    new ClientSnapshot(false, false, false, null, null, null, null, null),
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void ToStringRedactsTheToken()
        {
            ReconnectContext context = new ReconnectContext(PlayerId, RoomId, "seat-token-1");

            string text = context.ToString();
            Assert.That(text, Does.Contain(PlayerId.ToString()));
            Assert.That(text, Does.Contain(RoomId.ToString()));
            Assert.That(
                text,
                Does.Not.Contain("seat-token-1"),
                "the token is a bearer seat credential: never render it"
            );
        }

        [Test]
        public void EmptyTokenIsRejected()
        {
            Assert.Throws<ArgumentException>(
                (Action)(() => new ReconnectContext(PlayerId, RoomId, ""))
            );
            Assert.Throws<ArgumentException>(
                (Action)(() => new ReconnectContext(PlayerId, RoomId, null!))
            );
        }

        [Test]
        public void DefaultAndFailedCaptureContextsStayHashableAndInert()
        {
            /*
                default(ReconnectContext) (and the default TryCapture writes
                on failure) carries a null token: hashing must not throw,
                equality must hold, and ToString must stay log-safe.
            */
            ReconnectContext inert = default;
            Assert.DoesNotThrow((Action)(() => inert.GetHashCode()));
            Assert.That(inert, Is.EqualTo(default(ReconnectContext)));
            Assert.That(inert.ToString(), Does.Contain("<none>"));

            Assert.That(
                ReconnectContext.TryCapture(
                    new ClientSnapshot(
                        true,
                        true,
                        true,
                        RoomRole.Spectator,
                        PlayerId,
                        RoomId,
                        "ABC123",
                        null
                    ),
                    out ReconnectContext failed
                ),
                Is.False
            );
            Assert.DoesNotThrow((Action)(() => failed.GetHashCode()));
            Assert.That(failed, Is.EqualTo(inert));
        }
    }
}
