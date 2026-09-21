namespace SignalFish.Client.Tests.Polling
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Polling;

    /// <summary>
    /// Red-green anchors for the polling budgets: defaults, validation,
    /// and that every knob is honored by construction.
    /// </summary>
    [TestFixture]
    public class PollingClientOptionsTests
    {
        [Test]
        public void DefaultsMatchProtocolBudgets()
        {
            PollingClientOptions options = new PollingClientOptions();
            Assert.That(options.MaxFramesPerPoll, Is.EqualTo(64));
            Assert.That(options.MaxFrameBytes, Is.EqualTo(64 * 1024));
            Assert.That(options.EventCapacity, Is.EqualTo(256));
            Assert.That(options.HeartbeatIntervalMilliseconds, Is.EqualTo(30_000));
            Assert.That(options.HeartbeatTimeoutMilliseconds, Is.EqualTo(60_000));
        }

        [Test]
        public void CustomBudgetsAreHonored()
        {
            PollingClientOptions options = new PollingClientOptions(
                maxFramesPerPoll: 7,
                maxFrameBytes: 128,
                eventCapacity: 16,
                heartbeatIntervalMilliseconds: 1_000,
                heartbeatTimeoutMilliseconds: 4_000
            );
            Assert.That(options.MaxFramesPerPoll, Is.EqualTo(7));
            Assert.That(options.MaxFrameBytes, Is.EqualTo(128));
            Assert.That(options.EventCapacity, Is.EqualTo(16));
            Assert.That(options.HeartbeatIntervalMilliseconds, Is.EqualTo(1_000));
            Assert.That(options.HeartbeatTimeoutMilliseconds, Is.EqualTo(4_000));
        }

        [Test]
        public void InvalidBudgetsAreRejected(
            [Values] bool badFrames,
            [Values] bool badBytes,
            [Values] bool badCapacity,
            [Values] bool badInterval,
            [Values] bool badTimeout
        )
        {
            /*
                Exactly one knob goes invalid per case; every combination
                must throw (data-driven sweep of the validation table).
            */
            if (!badFrames && !badBytes && !badCapacity && !badInterval && !badTimeout)
            {
                Assert.Pass("No invalid knob in this combination.");
            }

            Action construct = () =>
                _ = new PollingClientOptions(
                    maxFramesPerPoll: badFrames ? 0 : 64,
                    maxFrameBytes: badBytes ? 0 : 64 * 1024,
                    eventCapacity: badCapacity ? 0 : 256,
                    heartbeatIntervalMilliseconds: badInterval ? 0 : 30_000,
                    heartbeatTimeoutMilliseconds: badTimeout ? 0 : 60_000
                );
            Assert.That(construct, Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
}
