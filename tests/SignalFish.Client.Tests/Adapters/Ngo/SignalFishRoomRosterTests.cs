namespace SignalFish.Client.Tests.Adapters.Ngo
{
    using System;
    using System.Collections.Generic;
    using NUnit.Framework;
    using SignalFish.Client.Adapters.Ngo;

    /// <summary>
    /// Contract coverage for the room roster: snapshot seeding replaces
    /// membership, join/reconnect/left events keep it current, and the
    /// approval lookup never admits the empty id.
    /// </summary>
    [TestFixture]
    public class SignalFishRoomRosterTests
    {
        private static readonly Guid PlayerA = new Guid("00000000-0000-0000-0000-000000000001");

        private static readonly Guid PlayerB = new Guid("00000000-0000-0000-0000-000000000002");

        private static readonly Guid PlayerC = new Guid("00000000-0000-0000-0000-000000000003");

        [Test]
        public void SeedingReplacesMembershipWithTheSnapshot()
        {
            SignalFishRoomRoster roster = new SignalFishRoomRoster();
            roster.Add(PlayerA);

            roster.Seed(new[] { PlayerB, PlayerC });

            Assert.That(roster.Count, Is.EqualTo(2));
            Assert.That(roster.IsMember(PlayerA), Is.False);
            Assert.That(roster.IsMember(PlayerB), Is.True);
            Assert.That(roster.IsMember(PlayerC), Is.True);
        }

        [Test]
        public void JoinAndReconnectAddAndLeaveRemoves()
        {
            SignalFishRoomRoster roster = new SignalFishRoomRoster();

            Assert.That(roster.Add(PlayerA), Is.True);
            Assert.That(roster.Add(PlayerA), Is.False);
            Assert.That(roster.Add(PlayerB), Is.True);
            Assert.That(roster.Remove(PlayerA), Is.True);
            Assert.That(roster.Remove(PlayerA), Is.False);

            Assert.That(roster.Count, Is.EqualTo(1));
            Assert.That(roster.IsMember(PlayerB), Is.True);
        }

        [Test]
        public void EmptyIdIsNeverAMember()
        {
            SignalFishRoomRoster roster = new SignalFishRoomRoster();

            Assert.That(roster.Add(Guid.Empty), Is.False);
            Assert.That(roster.Remove(Guid.Empty), Is.False);
            Assert.That(roster.IsMember(Guid.Empty), Is.False);
        }

        [Test]
        public void SeedingAnEmptyIdThrows()
        {
            SignalFishRoomRoster roster = new SignalFishRoomRoster();

            Assert.Throws<ArgumentException>(
                (Action)(() => roster.Seed(new[] { PlayerA, Guid.Empty }))
            );
        }

        [Test]
        public void ClearForgetsEveryMember()
        {
            SignalFishRoomRoster roster = new SignalFishRoomRoster();
            roster.Seed(new[] { PlayerA, PlayerB });

            roster.Clear();

            Assert.That(roster.Count, Is.EqualTo(0));
            Assert.That(roster.IsMember(PlayerB), Is.False);
        }

        [Test]
        public void SeedingNullThrows()
        {
            SignalFishRoomRoster roster = new SignalFishRoomRoster();

            Assert.Throws<ArgumentNullException>(
                (Action)(() => roster.Seed((IEnumerable<Guid>)null!))
            );
        }
    }
}
