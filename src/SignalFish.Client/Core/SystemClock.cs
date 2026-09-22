namespace SignalFish.Client.Core
{
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Production clock over <see cref="Stopwatch"/>: monotonic and
    /// unaffected by wall-clock adjustments (NTP, DST).
    /// </summary>
    public sealed class SystemClock : ISignalFishClock
    {
        /// <summary>Shared process-wide instance.</summary>
        public static readonly SystemClock Instance = new SystemClock();

        /// <inheritdoc />
        public long ElapsedMilliseconds
        {
            get { return _stopwatch.ElapsedMilliseconds; }
        }

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        private SystemClock() { }

        /// <inheritdoc />
        public Task DelayAsync(int milliseconds, CancellationToken ct = default)
        {
            return Task.Delay(milliseconds, ct);
        }
    }
}
