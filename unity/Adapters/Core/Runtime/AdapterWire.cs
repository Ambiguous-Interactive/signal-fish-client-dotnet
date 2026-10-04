#nullable enable
namespace SignalFish.Client.Adapters
{
    using System;
    using System.Buffers.Binary;

    /// <summary>
    /// The engine adapters' wire constants and the header that carries
    /// engine facts inside one opaque Signal Fish binary game-data
    /// payload. The Signal Fish relay is a room broadcast — it has no
    /// per-peer addressing and no class metadata on the binary lane — so
    /// an adapter frames its own facts (target, channel, version) ahead
    /// of the engine segment; the segment itself rides verbatim. Layout:
    /// <c>[0] header version, [1] delivery channel byte, [2..17] target
    /// player UUID (16 RFC-4122 network-order bytes, all-zero means
    /// broadcast), [18..] engine segment</c>.
    /// </summary>
    public static class AdapterWire
    {
        /// <summary>The header byte length every encoded frame carries.</summary>
        public const int HeaderLength = 18;

        /// <summary>The current header version; receivers refuse others.</summary>
        public const byte HeaderVersion = 1;

        /// <summary>
        /// The delivery channel byte for reliable traffic. Both pinned
        /// engines spell reliable as zero (FishNet <c>Channel.Reliable</c>,
        /// Mirror <c>Channels.Reliable</c>); the per-adapter pins live in
        /// <c>scripts/lint-unity-adapter.ps1</c>.
        /// </summary>
        public const byte ReliableChannel = 0;

        /// <summary>
        /// The delivery channel byte for unreliable traffic. Both pinned
        /// engines spell unreliable as one (FishNet
        /// <c>Channel.Unreliable</c>, Mirror <c>Channels.Unreliable</c>);
        /// the per-adapter pins live in
        /// <c>scripts/lint-unity-adapter.ps1</c>.
        /// </summary>
        public const byte UnreliableChannel = 1;

        /// <summary>The broadcast target spelling (the all-zero UUID).</summary>
        public static readonly Guid BroadcastTarget = Guid.Empty;

        /// <summary>
        /// Gets whether the channel byte is one the header frames. Unknown
        /// bytes are refused so an engine channel addition can never
        /// silently cross a wire the receiver would misread.
        /// </summary>
        public static bool IsKnownChannel(byte channel)
        {
            return channel == ReliableChannel || channel == UnreliableChannel;
        }

        /// <summary>
        /// Encodes one adapter frame: the header followed by the segment.
        /// Succeeds only for a known channel and a destination that fits
        /// the header plus the segment; the caller sizes the buffer from
        /// <see cref="HeaderLength"/> plus the segment length.
        /// </summary>
        public static bool TryEncode(
            byte channel,
            Guid target,
            ReadOnlySpan<byte> segment,
            Span<byte> destination,
            out int written
        )
        {
            written = 0;
            if (!IsKnownChannel(channel) || destination.Length < HeaderLength + segment.Length)
            {
                return false;
            }

            destination[0] = HeaderVersion;
            destination[1] = channel;
            WriteNetworkUuid(destination.Slice(2, 16), target);
            segment.CopyTo(destination.Slice(HeaderLength));
            written = HeaderLength + segment.Length;
            return true;
        }

        /// <summary>
        /// Decodes one adapter frame into its header facts and the verbatim
        /// segment slice. Refuses short frames, unknown header versions,
        /// and unknown channel bytes — every refusal means the frame can
        /// never be interpreted, not merely that it looks unusual.
        /// </summary>
        public static bool TryDecode(
            ReadOnlySpan<byte> frame,
            out byte channel,
            out Guid target,
            out ReadOnlySpan<byte> segment
        )
        {
            channel = 0;
            target = Guid.Empty;
            segment = default;
            if (frame.Length < HeaderLength || frame[0] != HeaderVersion)
            {
                return false;
            }

            channel = frame[1];
            if (!IsKnownChannel(channel))
            {
                return false;
            }

            target = ReadNetworkUuid(frame.Slice(2, 16));
            segment = frame.Slice(HeaderLength);
            return true;
        }

        /// <summary>
        /// Writes the 16 RFC-4122 network-order UUID bytes — the exact
        /// spelling the library's binary game-data decoder reads
        /// <c>from_player</c> with — so adapter targets and relay sender
        /// stamps round-trip byte-for-byte.
        /// </summary>
        public static void WriteNetworkUuid(Span<byte> destination, Guid value)
        {
            Span<byte> little = stackalloc byte[16];
            if (!value.TryWriteBytes(little))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            /*
                Guid.ToByteArray()/TryWriteBytes order the first three
                groups little-endian; the network form is big-endian for
                those groups and verbatim for the last eight bytes.
            */
            destination[0] = little[3];
            destination[1] = little[2];
            destination[2] = little[1];
            destination[3] = little[0];
            destination[4] = little[5];
            destination[5] = little[4];
            destination[6] = little[7];
            destination[7] = little[6];
            little.Slice(8, 8).CopyTo(destination.Slice(8, 8));
        }

        /// <summary>
        /// Reads the 16 RFC-4122 network-order UUID bytes into a
        /// <see cref="Guid"/> (the library's binary game-data spelling).
        /// </summary>
        public static Guid ReadNetworkUuid(ReadOnlySpan<byte> bytes)
        {
            return new Guid(
                BinaryPrimitives.ReadInt32BigEndian(bytes),
                BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(4)),
                BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(6)),
                bytes[8],
                bytes[9],
                bytes[10],
                bytes[11],
                bytes[12],
                bytes[13],
                bytes[14],
                bytes[15]
            );
        }
    }
}
