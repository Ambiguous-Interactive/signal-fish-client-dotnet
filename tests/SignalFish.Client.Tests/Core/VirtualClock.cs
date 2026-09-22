namespace SignalFish.Client.Tests.Core
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SignalFish.Client.Core;

    /// <summary>
    /// Virtual time for deterministic tests: no test ever waits on a real
    /// timer; the code under test advances this clock explicitly. Pending
    /// <see cref="DelayAsync"/> waits honor their deadlines — an advance
    /// releases exactly the delays whose deadline the new time reaches, so
    /// cadence and threshold behavior is observable.
    /// </summary>
    public sealed class VirtualClock : ISignalFishClock
    {
        private sealed class PendingDelay
        {
            /// <summary>Gets the clock time at which the delay completes.</summary>
            internal long DeadlineMilliseconds { get; }

            /// <summary>Gets or sets the cancellation hook; disposed on release.</summary>
            internal CancellationTokenRegistration Registration { get; set; }

            internal readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal PendingDelay(long deadlineMilliseconds)
            {
                DeadlineMilliseconds = deadlineMilliseconds;
            }
        }

        public long ElapsedMilliseconds
        {
            get { return Volatile.Read(ref _elapsedMilliseconds); }
        }

        private readonly object _gate = new object();
        private readonly List<PendingDelay> _pendingDelays = new List<PendingDelay>();
        private long _elapsedMilliseconds;

        public void Advance(long milliseconds)
        {
            if (milliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(milliseconds),
                    "The clock is monotonic; advance forwards only."
                );
            }

            List<PendingDelay> released = new List<PendingDelay>();
            lock (_gate)
            {
                _elapsedMilliseconds += milliseconds;
                for (int index = _pendingDelays.Count - 1; index >= 0; index--)
                {
                    PendingDelay delay = _pendingDelays[index];
                    if (delay.DeadlineMilliseconds <= _elapsedMilliseconds)
                    {
                        _pendingDelays.RemoveAt(index);
                        released.Add(delay);
                    }
                }
            }

            foreach (PendingDelay delay in released)
            {
                delay.Registration.Dispose();
                delay.Completion.TrySetResult(true);
            }
        }

        public Task DelayAsync(int milliseconds, CancellationToken ct = default)
        {
            PendingDelay pending;
            lock (_gate)
            {
                if (ct.IsCancellationRequested)
                {
                    return Task.FromCanceled(ct);
                }

                pending = new PendingDelay(_elapsedMilliseconds + milliseconds);
                pending.Registration = ct.Register(
                    static state =>
                    {
                        PendingDelay canceled = (PendingDelay)state!;
                        canceled.Completion.TrySetCanceled();
                    },
                    pending
                );
                _pendingDelays.Add(pending);
            }

            return pending.Completion.Task;
        }
    }
}
