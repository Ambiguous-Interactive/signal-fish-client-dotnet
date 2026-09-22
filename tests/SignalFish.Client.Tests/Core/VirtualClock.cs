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
    /// <see cref="DelayAsync"/> waits complete when <see cref="Advance"/>
    /// runs, so clock time and timer wake-ups move together.
    /// </summary>
    public sealed class VirtualClock : ISignalFishClock
    {
        public long ElapsedMilliseconds
        {
            get { return _elapsedMilliseconds; }
        }

        private readonly object _gate = new object();
        private readonly List<TaskCompletionSource<bool>> _pendingDelays =
            new List<TaskCompletionSource<bool>>();
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

            _elapsedMilliseconds += milliseconds;

            TaskCompletionSource<bool>[] released;
            lock (_gate)
            {
                if (_pendingDelays.Count == 0)
                {
                    return;
                }

                released = _pendingDelays.ToArray();
                _pendingDelays.Clear();
            }

            foreach (TaskCompletionSource<bool> delay in released)
            {
                delay.TrySetResult(true);
            }
        }

        public Task DelayAsync(int milliseconds, CancellationToken ct = default)
        {
            TaskCompletionSource<bool> delay = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            /*
                The registration must outlive this call so a later
                cancellation still completes the pending wait; a completed
                wait simply ignores the (failing) cancel attempt.
            */
            ct.Register(
                static state =>
                {
                    (TaskCompletionSource<bool> Completion, CancellationToken Token) canceled = ((
                        TaskCompletionSource<bool>,
                        CancellationToken
                    ))
                        state!;
                    canceled.Completion.TrySetCanceled(canceled.Token);
                },
                (delay, ct)
            );

            lock (_gate)
            {
                if (ct.IsCancellationRequested)
                {
                    delay.TrySetCanceled(ct);
                }
                else
                {
                    _pendingDelays.Add(delay);
                }
            }

            return delay.Task;
        }
    }
}
