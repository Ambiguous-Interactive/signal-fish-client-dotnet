namespace SignalFish.Client.Tests.Adapters.Core
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Adapters;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Contract coverage for the FishNet adapter's wire header: the pinned
    /// byte layout, RFC-4122 UUID spelling shared with the library's
    /// binary game-data decoder, round-trip preservation of channel,
    /// target, and segment, and every refusal shape (short frames, unknown
    /// header versions, unknown channels, undersized buffers).
    /// </summary>
    [TestFixture]
    public class AdapterWireTests
    {
        private static readonly Guid TargetPlayer = new Guid(
            "00112233-4455-6677-8899-aabbccddeeff"
        );

        private static readonly TestCaseData[] RoundTripFrames =
        {
            new TestCaseData(
                AdapterWire.ReliableChannel,
                AdapterWire.BroadcastTarget,
                Array.Empty<byte>()
            ).SetName("ReliableBroadcastEmpty"),
            new TestCaseData(
                AdapterWire.ReliableChannel,
                TargetPlayer,
                new byte[] { 0x01, 0x02, 0xfe }
            ).SetName("ReliableTargetedBinary"),
            new TestCaseData(
                AdapterWire.UnreliableChannel,
                AdapterWire.BroadcastTarget,
                Encoding.UTF8.GetBytes("position")
            ).SetName("UnreliableBroadcastText"),
            new TestCaseData(AdapterWire.UnreliableChannel, TargetPlayer, new byte[512]).SetName(
                "UnreliableTargetedLarge"
            ),
        };

        private static readonly TestCaseData[] BrokenFrames =
        {
            new TestCaseData(Array.Empty<byte>()).SetName("Empty"),
            new TestCaseData(new byte[] { AdapterWire.HeaderVersion }).SetName("ShorterThanHeader"),
            new TestCaseData(new byte[] { 0x02, AdapterWire.ReliableChannel }).SetName(
                "UnknownHeaderVersion"
            ),
            new TestCaseData(new byte[] { AdapterWire.HeaderVersion, 0x07, 0x00 }).SetName(
                "UnknownChannel"
            ),
        };

        private static readonly TestCaseData[] UnencodableChannels =
        {
            new TestCaseData((byte)0x07).SetName("UnknownChannelByte"),
            new TestCaseData((byte)0xff).SetName("SentinelChannelByte"),
        };

        [Test]
        public void HeaderLayoutPinsVersionChannelAndNetworkUuid()
        {
            Span<byte> buffer = stackalloc byte[AdapterWire.HeaderLength];
            Assert.That(
                AdapterWire.TryEncode(
                    AdapterWire.ReliableChannel,
                    TargetPlayer,
                    ReadOnlySpan<byte>.Empty,
                    buffer,
                    out int written
                ),
                Is.True
            );

            Assert.That(written, Is.EqualTo(AdapterWire.HeaderLength));
            Assert.That(buffer[0], Is.EqualTo(AdapterWire.HeaderVersion));
            Assert.That(buffer[1], Is.EqualTo(AdapterWire.ReliableChannel));
            Assert.That(
                buffer.Slice(2, 16).ToArray(),
                Is.EqualTo(
                    new byte[]
                    {
                        0x00,
                        0x11,
                        0x22,
                        0x33,
                        0x44,
                        0x55,
                        0x66,
                        0x77,
                        0x88,
                        0x99,
                        0xaa,
                        0xbb,
                        0xcc,
                        0xdd,
                        0xee,
                        0xff,
                    }
                ),
                "The network UUID is the hyphenated display form for an RFC display-shaped GUID."
            );
        }

        [Test]
        public void NetworkUuidRoundTripsTheLibraryBinaryGameDataSpelling()
        {
            Span<byte> wire = stackalloc byte[16];
            AdapterWire.WriteNetworkUuid(wire, TargetPlayer);

            Guid decoded = AdapterWire.ReadNetworkUuid(wire);
            Assert.That(decoded, Is.EqualTo(TargetPlayer));
        }

        [Test]
        public void AdapterEncodedUuidDecodesThroughTheLibraryBinaryGameDataReader()
        {
            /*
                The joint pin: a UUID written by the adapter's encoder feeds
                the library's strict binary game-data decoder as
                from_player, and the adapter frame rides the map's payload
                verbatim. A permutation divergence on either side fails
                here, not in a player.
            */
            Span<byte> wire = stackalloc byte[16];
            AdapterWire.WriteNetworkUuid(wire, TargetPlayer);

            byte[] adapterFrame = new byte[AdapterWire.HeaderLength + 2];
            Assert.That(
                AdapterWire.TryEncode(
                    AdapterWire.UnreliableChannel,
                    TargetPlayer,
                    new byte[] { 0x2a, 0x2b },
                    adapterFrame,
                    out int written
                ),
                Is.True
            );
            Assert.That(written, Is.EqualTo(adapterFrame.Length));

            List<byte> map = new List<byte> { 0x85 };
            WriteFixstr(map, "from_player");
            map.Add(0xc5);
            map.Add(0x00);
            map.Add(0x10);
            foreach (byte wireByte in wire.ToArray())
            {
                map.Add(wireByte);
            }

            WriteFixstr(map, "encoding");
            WriteFixstr(map, "message_pack");
            WriteFixstr(map, "payload");
            map.Add(0xc4);
            map.Add((byte)adapterFrame.Length);
            map.AddRange(adapterFrame);
            WriteFixstr(map, "seq");
            map.Add(0x01);
            WriteFixstr(map, "epoch");
            map.Add(0x01);
            Assert.That(
                BinaryGameDataFrame.TryDecode(
                    map.ToArray(),
                    protocolV3: true,
                    out BinaryGameDataFrame gameData,
                    out DecodeError error,
                    out int errorOffset
                ),
                Is.True,
                $"{error} at {errorOffset}"
            );
            Assert.That(gameData.FromPlayer, Is.EqualTo(TargetPlayer));
            Assert.That(gameData.Payload.ToArray(), Is.EqualTo(adapterFrame));

            Assert.That(
                AdapterWire.TryDecode(
                    gameData.Payload.Span,
                    out byte channel,
                    out Guid target,
                    out ReadOnlySpan<byte> segment
                ),
                Is.True
            );
            Assert.That(channel, Is.EqualTo(AdapterWire.UnreliableChannel));
            Assert.That(target, Is.EqualTo(TargetPlayer));
            Assert.That(segment.ToArray(), Is.EqualTo(new byte[] { 0x2a, 0x2b }));
        }

        [TestCaseSource(nameof(RoundTripFrames))]
        public void EncodeThenDecodePreservesChannelTargetAndSegment(
            byte channel,
            Guid target,
            byte[] segment
        )
        {
            Span<byte> buffer = stackalloc byte[AdapterWire.HeaderLength + segment.Length];
            Assert.That(
                AdapterWire.TryEncode(channel, target, segment, buffer, out int written),
                Is.True
            );
            Assert.That(written, Is.EqualTo(AdapterWire.HeaderLength + segment.Length));

            Assert.That(
                AdapterWire.TryDecode(
                    buffer,
                    out byte decodedChannel,
                    out Guid decodedTarget,
                    out ReadOnlySpan<byte> decodedSegment
                ),
                Is.True
            );
            Assert.That(decodedChannel, Is.EqualTo(channel));
            Assert.That(decodedTarget, Is.EqualTo(target));
            Assert.That(decodedSegment.ToArray(), Is.EqualTo(segment));
        }

        [TestCaseSource(nameof(BrokenFrames))]
        public void BrokenFramesRefuseToDecode(byte[] frame)
        {
            Assert.That(AdapterWire.TryDecode(frame, out _, out _, out _), Is.False);
        }

        [TestCaseSource(nameof(UnencodableChannels))]
        public void UnknownChannelsRefuseToEncode(byte channel)
        {
            Span<byte> buffer = stackalloc byte[AdapterWire.HeaderLength];
            Assert.That(
                AdapterWire.TryEncode(
                    channel,
                    AdapterWire.BroadcastTarget,
                    ReadOnlySpan<byte>.Empty,
                    buffer,
                    out int written
                ),
                Is.False
            );
            Assert.That(written, Is.EqualTo(0));
        }

        [Test]
        public void UndersizedBufferRefusesToEncode()
        {
            byte[] segment = { 0x01 };
            Span<byte> buffer = stackalloc byte[AdapterWire.HeaderLength];
            Assert.That(
                AdapterWire.TryEncode(
                    AdapterWire.ReliableChannel,
                    AdapterWire.BroadcastTarget,
                    segment,
                    buffer,
                    out int written
                ),
                Is.False
            );
            Assert.That(written, Is.EqualTo(0));
        }

        private static void WriteFixstr(List<byte> map, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(0x1f), "the test writer pins fixstr");
            map.Add((byte)(0xa0 | bytes.Length));
            map.AddRange(bytes);
        }
    }
}
