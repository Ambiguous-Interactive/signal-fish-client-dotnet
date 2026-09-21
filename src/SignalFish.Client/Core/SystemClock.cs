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

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        private SystemClock() { }

        /// <inheritdoc />
        public long ElapsedMilliseconds
        {
            get { return _stopwatch.ElapsedMilliseconds; }
        }
    }
}
