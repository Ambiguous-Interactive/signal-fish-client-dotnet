#nullable enable
namespace SignalFish.Client.Adapters.Mirror
{
    using System;

    /// <summary>
    /// The Mirror-visible MTU the adapter reports. The binding constraint
    /// is the adapter's own inbound contract: <c>maxFrameBytes</c> (the
    /// client's inbound physical-frame bound, 64 KiB by default) must hold
    /// for the relayed frame whose payload is the adapter header plus the
    /// Mirror segment. The reserve covers the strict MessagePack map the
    /// server wraps around every relayed payload — <c>from_player</c>,
    /// <c>encoding</c>, <c>seq</c>, <c>epoch</c>, and the payload bin
    /// headers — with margin, so a segment at the reported MTU can never
    /// produce an inbound frame above the bound on any recipient.
    /// </summary>
    public static class MirrorAdapterMtu
    {
        /// <summary>
        /// The per-frame bytes held back from the client's inbound frame
        /// bound for the relay map, the adapter header, and margin.
        /// </summary>
        public const int WireReserve = 256;

        /// <summary>
        /// Computes the largest Mirror segment the adapter can carry for
        /// one frame. Throws <see cref="ArgumentOutOfRangeException"/> when
        /// the frame bound cannot even fit the reserve — a misconfigured
        /// client fails at construction instead of reporting a zero MTU.
        /// </summary>
        public static int MaxSegmentBytes(int maxFrameBytes)
        {
            if (maxFrameBytes <= WireReserve)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxFrameBytes),
                    maxFrameBytes,
                    $"The frame bound must exceed the adapter wire reserve ({WireReserve} bytes)."
                );
            }

            return maxFrameBytes - WireReserve;
        }
    }
}
