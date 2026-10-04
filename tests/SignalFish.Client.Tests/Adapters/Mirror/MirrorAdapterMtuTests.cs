namespace SignalFish.Client.Tests.Adapters.Mirror
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Adapters.Mirror;

    /// <summary>
    /// Contract coverage for the Mirror adapter's MTU math: the
    /// default-budget result, the exact reserve arithmetic, and the loud
    /// refusal when a frame bound cannot even fit the wire reserve.
    /// </summary>
    [TestFixture]
    public class MirrorAdapterMtuTests
    {
        private static readonly TestCaseData[] ImpossibleBudgets =
        {
            new TestCaseData(MirrorAdapterMtu.WireReserve).SetName("ExactlyTheReserve"),
            new TestCaseData(MirrorAdapterMtu.WireReserve - 1).SetName("BelowTheReserve"),
            new TestCaseData(0).SetName("Zero"),
            new TestCaseData(-65536).SetName("Negative"),
        };

        [Test]
        public void DefaultFrameBoundYieldsTheDocumentedSegmentBudget()
        {
            Assert.That(
                MirrorAdapterMtu.MaxSegmentBytes(64 * 1024),
                Is.EqualTo(64 * 1024 - MirrorAdapterMtu.WireReserve)
            );
        }

        [Test]
        public void SmallestPossibleBudgetYieldsASingleByte()
        {
            Assert.That(
                MirrorAdapterMtu.MaxSegmentBytes(MirrorAdapterMtu.WireReserve + 1),
                Is.EqualTo(1)
            );
        }

        [TestCaseSource(nameof(ImpossibleBudgets))]
        public void ImpossibleBudgetsAreRefusedLoudly(int maxFrameBytes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => MirrorAdapterMtu.MaxSegmentBytes(maxFrameBytes))
            );
        }
    }
}
