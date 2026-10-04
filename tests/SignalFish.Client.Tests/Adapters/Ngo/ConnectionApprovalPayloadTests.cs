namespace SignalFish.Client.Tests.Adapters.Ngo
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Adapters;
    using SignalFish.Client.Adapters.Ngo;

    /// <summary>
    /// Contract coverage for the approval payload: exactly 16 bytes in
    /// the relay's RFC-4122 network-order UUID spelling (byte-identical
    /// to <see cref="AdapterWire"/>), with every other length and the
    /// empty id refused.
    /// </summary>
    [TestFixture]
    public class ConnectionApprovalPayloadTests
    {
        private static readonly Guid PlayerA = new Guid("01234567-89ab-cdef-0123-456789abcdef");

        [Test]
        public void WriteThenReadRoundTripsThePlayerId()
        {
            byte[] payload = ConnectionApprovalPayload.Write(PlayerA);

            Assert.That(payload, Has.Length.EqualTo(ConnectionApprovalPayload.Length));
            Assert.That(ConnectionApprovalPayload.TryRead(payload, out Guid playerId), Is.True);
            Assert.That(playerId, Is.EqualTo(PlayerA));
        }

        [Test]
        public void PayloadBytesMatchTheSharedAdapterWireUuid()
        {
            byte[] payload = ConnectionApprovalPayload.Write(PlayerA);
            byte[] wire = new byte[AdapterWire.HeaderLength];
            AdapterWire.WriteNetworkUuid(wire, PlayerA);

            Assert.That(payload[0], Is.EqualTo(wire[0]));
            Assert.That(payload[15], Is.EqualTo(wire[15]));
        }

        [Test]
        public void ReadsOfOtherLengthsAndTheEmptyIdAreRefused()
        {
            Assert.That(
                ConnectionApprovalPayload.TryRead(
                    new byte[ConnectionApprovalPayload.Length - 1],
                    out Guid shortRead
                ),
                Is.False
            );
            Assert.That(shortRead, Is.EqualTo(Guid.Empty));
            Assert.That(
                ConnectionApprovalPayload.TryRead(
                    new byte[ConnectionApprovalPayload.Length + 1],
                    out Guid longRead
                ),
                Is.False
            );
            Assert.That(longRead, Is.EqualTo(Guid.Empty));
            Assert.That(
                ConnectionApprovalPayload.TryRead(
                    new byte[ConnectionApprovalPayload.Length],
                    out Guid emptyRead
                ),
                Is.False
            );
            Assert.That(emptyRead, Is.EqualTo(Guid.Empty));
        }
    }
}
