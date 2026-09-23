namespace SignalFish.Client.V3
{
    using System;

    /// <summary>
    /// Whether validated game data still belongs to the application-visible
    /// incarnation or is trailing data overtaken by priority lifecycle
    /// control.
    /// </summary>
    public enum GameDataDisposition : byte
    {
        /// <summary>Sentinel for <c>default(GameDataDisposition)</c>; not a disposition.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a disposition. RecordGameData assigns Apply or Stale whenever it succeeds."
        )]
        None = 0,

        /// <summary>The frame belongs to the application-visible incarnation; apply it.</summary>
        Apply = 1,

        /// <summary>
        /// The frame is trailing data of a departed or superseded
        /// incarnation; the engine still advances its accounting, but the
        /// application must not apply the payload.
        /// </summary>
        Stale = 2,
    }

    /// <summary>
    /// One roster entry of a room/spectator snapshot: a sender with its
    /// epoch/seq delivery baseline, as decoded from the snapshot's
    /// <c>PlayerInfo</c>.
    /// </summary>
    public readonly struct SenderBaseline : IEquatable<SenderBaseline>
    {
        /// <summary>Gets the player's id.</summary>
        public Guid PlayerId { get; }

        /// <summary>Gets the player's incarnation epoch.</summary>
        public uint Epoch { get; }

        /// <summary>Gets the player's baseline sequence (the last sequence already delivered).</summary>
        public ulong Seq { get; }

        /// <summary>Initializes a new sender baseline.</summary>
        public SenderBaseline(Guid playerId, uint epoch, ulong seq)
        {
            PlayerId = playerId;
            Epoch = epoch;
            Seq = seq;
        }

        /// <inheritdoc />
        public bool Equals(SenderBaseline other) =>
            PlayerId == other.PlayerId && Epoch == other.Epoch && Seq == other.Seq;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SenderBaseline other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PlayerId);
            hash.Add(Epoch);
            hash.Add(Seq);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(SenderBaseline left, SenderBaseline right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(SenderBaseline left, SenderBaseline right) =>
            !left.Equals(right);
    }
}
