namespace SignalFish.Client.Tests
{
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// M3.3 red-green anchor: every v2 server session-fact wire type routes
    /// to a known <see cref="MessageKind"/> (a wire type falling out as
    /// <c>UnknownMessage</c> would silently strand a session fact), and the
    /// whole routing table round-trips byte-exactly.
    /// </summary>
    [TestFixture]
    public class MessageKindNamesTests
    {
        /*
            The v2 server→client session-fact wire types the mapping layer
            consumes (quick-reference "Mandatory v2 lifecycle" + typed
            failures + spectator confirmations).
        */
        private static readonly string[] ServerSessionWireNames =
        {
            "Authenticated",
            "RoomJoined",
            "SpectatorJoined",
            "Reconnected",
            "RoomLeft",
            "SpectatorLeft",
            "RoomJoinFailed",
            "SpectatorJoinFailed",
            "ReconnectionFailed",
            "Error",
        };

        [Test]
        public void TryRouteServerSessionTypesRouteToKnownKinds()
        {
            foreach (string wireName in ServerSessionWireNames)
            {
                Assert.That(
                    MessageKindNames.TryRoute(
                        Encoding.ASCII.GetBytes(wireName),
                        out MessageKind kind
                    ),
                    Is.True,
                    "server session wire type must route: " + wireName
                );
                Assert.That(
                    MessageKindNames.ToWireName(kind),
                    Is.EqualTo(wireName),
                    "routed kind must round-trip to the exact wire name"
                );
            }
        }

        [Test]
        public void TableRouteRoundtripEveryDefinedKind()
        {
            int definedCount = 0;
            foreach (MessageKind kind in Enum.GetValues<MessageKind>())
            {
                if (kind == default(MessageKind))
                {
                    continue;
                }

                definedCount++;
                string? wireName = MessageKindNames.ToWireName(kind);
                Assert.That(wireName, Is.Not.Null, $"defined kind must have a wire name: {kind}");
                Assert.That(
                    MessageKindNames.TryRoute(
                        Encoding.ASCII.GetBytes(wireName!),
                        out MessageKind routed
                    ),
                    Is.True,
                    "defined kind must route: " + wireName
                );
                Assert.That(routed, Is.EqualTo(kind), wireName);
            }

            /*
                Enum and table must be in exact lockstep: a missing table
                entry strands a kind as unroutable; an extra one phantom-routes.
            */
            int routedCount = 0;
            for (int value = 1; value <= 255; value++)
            {
                if (MessageKindNames.ToWireName((MessageKind)value) is not null)
                {
                    routedCount++;
                }
            }

            Assert.That(routedCount, Is.EqualTo(definedCount));
        }

        [Test]
        public void TryRouteUnknownNamesAreNotRouted()
        {
            Assert.That(
                MessageKindNames.TryRoute(Encoding.ASCII.GetBytes("DefinitelyNotAType"), out _),
                Is.False
            );
            Assert.That(
                MessageKindNames.TryRoute(Encoding.ASCII.GetBytes("roomjoined"), out _),
                Is.False
            );
            Assert.That(
                MessageKindNames.TryRoute(Encoding.ASCII.GetBytes(string.Empty), out _),
                Is.False
            );
        }
    }
}
