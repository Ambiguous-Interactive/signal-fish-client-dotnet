namespace SignalFish.Client.Tests
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Adapters.FishNet;

    /// <summary>
    /// Contract coverage for the MTU math: the default-budget result, the
    /// exact reserve arithmetic, and the loud refusal when a frame bound
    /// cannot even fit the wire reserve.
    /// </summary>
    [TestFixture]
    public class FishNetAdapterMtuTests
    {
        private static readonly TestCaseData[] ImpossibleBudgets =
        {
            new TestCaseData(FishNetAdapterMtu.WireReserve).SetName("ExactlyTheReserve"),
            new TestCaseData(FishNetAdapterMtu.WireReserve - 1).SetName("BelowTheReserve"),
            new TestCaseData(0).SetName("Zero"),
            new TestCaseData(-65536).SetName("Negative"),
        };

        [Test]
        public void DefaultFrameBoundYieldsTheDocumentedSegmentBudget()
        {
            Assert.That(
                FishNetAdapterMtu.MaxSegmentBytes(64 * 1024),
                Is.EqualTo(64 * 1024 - FishNetAdapterMtu.WireReserve)
            );
        }

        [Test]
        public void SmallestPossibleBudgetYieldsASingleByte()
        {
            Assert.That(
                FishNetAdapterMtu.MaxSegmentBytes(FishNetAdapterMtu.WireReserve + 1),
                Is.EqualTo(1)
            );
        }

        [TestCaseSource(nameof(ImpossibleBudgets))]
        public void ImpossibleBudgetsAreRefusedLoudly(int maxFrameBytes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => FishNetAdapterMtu.MaxSegmentBytes(maxFrameBytes))
            );
        }
    }
}
