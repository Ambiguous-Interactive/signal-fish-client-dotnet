namespace SignalFish.Client.Core
{
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
    }
}
