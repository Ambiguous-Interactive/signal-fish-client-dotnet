namespace SignalFish.Client.Tests.Adapters.Core
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Adapters;

    /// <summary>
    /// Contract coverage for the MTU math: the default-budget result, the
    /// exact reserve arithmetic, and the loud refusal when a frame bound
    /// cannot even fit the wire reserve.
    /// </summary>
    [TestFixture]
    public class AdapterMtuTests
    {
        private static readonly TestCaseData[] ImpossibleBudgets =
        {
            new TestCaseData(AdapterMtu.WireReserve).SetName("ExactlyTheReserve"),
            new TestCaseData(AdapterMtu.WireReserve - 1).SetName("BelowTheReserve"),
            new TestCaseData(0).SetName("Zero"),
            new TestCaseData(-65536).SetName("Negative"),
        };

        [Test]
        public void DefaultFrameBoundYieldsTheDocumentedSegmentBudget()
        {
            Assert.That(
                AdapterMtu.MaxSegmentBytes(64 * 1024),
                Is.EqualTo(64 * 1024 - AdapterMtu.WireReserve)
            );
        }

        [Test]
        public void SmallestPossibleBudgetYieldsASingleByte()
        {
            Assert.That(AdapterMtu.MaxSegmentBytes(AdapterMtu.WireReserve + 1), Is.EqualTo(1));
        }

        [TestCaseSource(nameof(ImpossibleBudgets))]
        public void ImpossibleBudgetsAreRefusedLoudly(int maxFrameBytes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => AdapterMtu.MaxSegmentBytes(maxFrameBytes))
            );
        }
    }
}
