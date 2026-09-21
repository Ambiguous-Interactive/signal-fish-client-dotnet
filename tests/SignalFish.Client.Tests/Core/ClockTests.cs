namespace SignalFish.Client.Tests.Core
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Core;

    /// <summary>
    /// M3.1 red-green anchor: the clock contract. Monotonicity for the
    /// production clock, explicit-time behavior for the virtual harness.
    /// </summary>
    [TestFixture]
    public class ClockTests
    {
        [Test]
        public void SystemClock_ElapsedMilliseconds_AdvancesMonotonically()
        {
            long first = SystemClock.Instance.ElapsedMilliseconds;
            long last = first;
            bool advanced = false;
            for (int sample = 0; sample < 1 << 20; sample++)
            {
                long current = SystemClock.Instance.ElapsedMilliseconds;
                Assert.That(current, Is.GreaterThanOrEqualTo(last));
                advanced |= current > first;
                last = current;
                if (advanced)
                {
                    break;
                }
            }

            Assert.That(advanced, Is.True, "clock never ticked");
        }

        [Test]
        public void VirtualClock_RejectsBackwardsAdvance()
        {
            VirtualClock clock = new VirtualClock();
            Assert.That(
                (Action)(() => clock.Advance(-1)),
                Throws.TypeOf<ArgumentOutOfRangeException>()
            );
            Assert.That(clock.ElapsedMilliseconds, Is.EqualTo(0));
        }

        [Test]
        public void VirtualClock_StartsAtZero_AndAdvancesExactly()
        {
            VirtualClock clock = new VirtualClock();
            Assert.That(clock.ElapsedMilliseconds, Is.EqualTo(0));

            clock.Advance(30_000);
            clock.Advance(5_000);

            Assert.That(clock.ElapsedMilliseconds, Is.EqualTo(35_000));
        }

        [Test]
        public void VirtualClock_SatisfiesClockContract()
        {
            VirtualClock clock = new VirtualClock();
            clock.Advance(1_000);

            Assert.That(((ISignalFishClock)clock).ElapsedMilliseconds, Is.EqualTo(1_000));
        }
    }
}
