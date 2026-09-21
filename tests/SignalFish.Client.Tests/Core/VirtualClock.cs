namespace SignalFish.Client.Tests.Core
{
    using System;
    using SignalFish.Client.Core;

    /// <summary>
    /// Virtual time for deterministic tests: no test ever waits on a real
    /// timer; the code under test advances this clock explicitly.
    /// </summary>
    public sealed class VirtualClock : ISignalFishClock
    {
        public long ElapsedMilliseconds
        {
            get { return _elapsedMilliseconds; }
        }

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
        }
    }
}
