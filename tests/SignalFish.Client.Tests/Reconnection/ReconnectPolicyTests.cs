namespace SignalFish.Client.Tests.Reconnection
{
    using System;
    using System.Linq;
    using NUnit.Framework;
    using SignalFish.Client.Reconnection;
    using SignalFish.Client.Tests.Transport;
    using SignalFish.Client.Transport;

    /// <summary>
    /// Red-green anchors for the opt-in reconnection policy: the
    /// deterministic (no-jitter) exponential backoff table, the terminal
    /// close-code classification, and construction validation.
    /// </summary>
    [TestFixture]
    public class ReconnectPolicyTests
    {
        private static readonly int[] Attempts = { 1, 2, 3, 4, 5, 6 };

        private static readonly long[] DoublingDelays = { 100, 200, 400, 400, 400, 400 };

        private static readonly long[] DefaultDelays = { 500, 1_000, 2_000, 4_000, 8_000, 8_000 };

        [Test]
        public void BackoffDoublesPerAttemptAndCapsAtTheCeiling()
        {
            ReconnectPolicy policy = new ReconnectPolicy(
                Factory,
                initialBackoffMilliseconds: 100,
                maxBackoffMilliseconds: 400
            );

            Assert.That(
                Attempts.Select(policy.BackoffForAttempt),
                Is.EqualTo(DoublingDelays),
                "the delay doubles per consecutive attempt and clamps at the ceiling"
            );
        }

        [Test]
        public void DefaultBackoffMatchesTheRustDefaults()
        {
            ReconnectPolicy policy = new ReconnectPolicy(Factory);

            Assert.That(Attempts.Select(policy.BackoffForAttempt), Is.EqualTo(DefaultDelays));
        }

        [Test]
        public void UnitMultiplierHoldsTheInitialDelay()
        {
            ReconnectPolicy policy = new ReconnectPolicy(
                Factory,
                initialBackoffMilliseconds: 250,
                maxBackoffMilliseconds: 250,
                multiplier: 1.0
            );

            Assert.That(policy.BackoffForAttempt(4), Is.EqualTo(250));
        }

        [Test]
        public void NoCloseCodeIsTerminalByDefault()
        {
            ReconnectPolicy policy = new ReconnectPolicy(Factory);

            Assert.That(policy.TerminalCloseCodes, Is.Empty);
            Assert.That(policy.IsTerminalClose(4007), Is.False);
            Assert.That(policy.IsTerminalClose(1006), Is.False);
            Assert.That(policy.IsTerminalClose(0), Is.False);
        }

        [Test]
        public void TerminalCloseCodesClassifyWithoutTouchingTheBasePolicy()
        {
            ReconnectPolicy unclassified = new ReconnectPolicy(Factory);
            ReconnectPolicy classified = unclassified.WithTerminalCloseCodes(4007, 4005);

            Assert.That(classified.IsTerminalClose(4007), Is.True);
            Assert.That(classified.IsTerminalClose(4005), Is.True);
            Assert.That(classified.IsTerminalClose(4003), Is.False);
            Assert.That(
                unclassified.TerminalCloseCodes,
                Is.Empty,
                "classification derives a policy; the original stays unclassified"
            );
            Assert.That(
                classified.BackoffForAttempt(2),
                Is.EqualTo(unclassified.BackoffForAttempt(2))
            );
            Assert.That(classified.MaxAttempts, Is.EqualTo(unclassified.MaxAttempts));
        }

        [Test]
        public void NullFactoryIsRejected()
        {
            Assert.Throws<ArgumentNullException>(
                (Action)(() => new ReconnectPolicy((Func<ITransport>)(null!)))
            );
        }

        [Test]
        public void InvalidBudgetsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => new ReconnectPolicy(Factory, initialBackoffMilliseconds: -1))
            );
            Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(
                    () =>
                        new ReconnectPolicy(
                            Factory,
                            initialBackoffMilliseconds: 100,
                            maxBackoffMilliseconds: 50
                        )
                )
            );
            Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => new ReconnectPolicy(Factory, multiplier: 0.5))
            );
            Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => new ReconnectPolicy(Factory, maxAttempts: 0))
            );
            Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => new ReconnectPolicy(Factory).BackoffForAttempt(0))
            );
        }

        private static FakeTransport Factory()
        {
            return new FakeTransport();
        }
    }
}
