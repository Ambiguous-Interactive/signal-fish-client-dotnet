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
        // The v2 server→client session-fact wire types the mapping layer
        // consumes (quick-reference "Mandatory v2 lifecycle" + typed
        // failures + spectator confirmations).
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
        public void TryRoute_ServerSessionTypes_RouteToKnownKinds()
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
        public void Table_RouteRoundtrip_EveryDefinedKind()
        {
            for (int value = 1; ; value++)
            {
                string? wireName = MessageKindNames.ToWireName((MessageKind)value);
                if (wireName is null)
                {
                    Assert.That(
                        value > 1,
                        Is.True,
                        "the routing table must define at least one kind"
                    );
                    break;
                }

                Assert.That(
                    MessageKindNames.TryRoute(
                        Encoding.ASCII.GetBytes(wireName),
                        out MessageKind kind
                    ),
                    Is.True,
                    "defined kind must route: " + wireName
                );
                Assert.That((int)kind, Is.EqualTo(value), wireName);
            }
        }

        [Test]
        public void TryRoute_UnknownNames_AreNotRouted()
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
