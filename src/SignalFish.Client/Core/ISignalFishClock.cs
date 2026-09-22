namespace SignalFish.Client.Core
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Time source for heartbeat scheduling, liveness checks, and backoff.
    /// Production uses <see cref="SystemClock"/>; tests inject a virtual clock
    /// so no test ever waits on a real timer. Monotonic: values never go
    /// backwards within one clock instance.
    /// </summary>
    public interface ISignalFishClock
    {
        /// <summary>Monotonic milliseconds elapsed since this clock started.</summary>
        long ElapsedMilliseconds { get; }

        /// <summary>
        /// Completes after at least <paramref name="milliseconds"/> of this
        /// clock's time passes (a real delay on <see cref="SystemClock"/>; a
        /// test-controlled one on virtual clocks). Cancellation ends the
        /// wait early with an <see cref="OperationCanceledException"/>.
        /// </summary>
        Task DelayAsync(int milliseconds, CancellationToken ct = default);
    }
}
