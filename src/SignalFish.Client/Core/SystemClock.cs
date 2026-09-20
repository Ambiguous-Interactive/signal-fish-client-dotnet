namespace SignalFish.Client.Core
{
    using System.Diagnostics;

    /// <summary>
    /// Production clock over <see cref="Stopwatch"/>: monotonic and
    /// unaffected by wall-clock adjustments (NTP, DST).
    /// </summary>
    public sealed class SystemClock : ISignalFishClock
    {
        /// <summary>Shared process-wide instance.</summary>
        public static readonly SystemClock Instance = new SystemClock();

        private readonly long startTimestamp;

        private SystemClock()
        {
            this.startTimestamp = Stopwatch.GetTimestamp();
        }

        /// <inheritdoc />
        public long ElapsedMilliseconds
        {
            get
            {
                long delta = Stopwatch.GetTimestamp() - this.startTimestamp;
                return delta * 1000L / Stopwatch.Frequency;
            }
        }
    }
}
