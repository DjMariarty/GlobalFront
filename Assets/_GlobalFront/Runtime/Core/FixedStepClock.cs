using System;

namespace GlobalFront.Core.Simulation
{
    /// <summary>
    /// Engine-independent fixed-step clock. The authoritative server will own
    /// one instance; presentation frame rate never changes the tick rate.
    /// </summary>
    public sealed class FixedStepClock
    {
        private readonly double _tickDurationSeconds;
        private readonly int _maxCatchUpTicks;
        private double _accumulatorSeconds;

        public FixedStepClock(
            int tickRate = SimulationConstants.ServerTickRate,
            int maxCatchUpTicks = SimulationConstants.MaxCatchUpTicksPerFrame)
        {
            if (tickRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRate));
            }

            if (maxCatchUpTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCatchUpTicks));
            }

            _tickDurationSeconds = 1.0 / tickRate;
            _maxCatchUpTicks = maxCatchUpTicks;
        }

        public ulong Tick { get; private set; }

        public double TickDurationSeconds => _tickDurationSeconds;

        public double BacklogSeconds => _accumulatorSeconds;

        public double InterpolationAlpha =>
            Math.Min(1.0, _accumulatorSeconds / _tickDurationSeconds);

        /// <summary>
        /// Adds elapsed real time and advances at most the configured catch-up
        /// limit. Excess time remains queued instead of silently dropping ticks.
        /// </summary>
        public int Advance(double elapsedSeconds)
        {
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            }

            if (elapsedSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            }

            _accumulatorSeconds += elapsedSeconds;

            var pendingTicks = (int)Math.Floor(
                (_accumulatorSeconds + 1e-12) / _tickDurationSeconds);
            var executedTicks = Math.Min(pendingTicks, _maxCatchUpTicks);

            if (executedTicks <= 0)
            {
                return 0;
            }

            _accumulatorSeconds -= executedTicks * _tickDurationSeconds;
            if (_accumulatorSeconds < 0.0 && _accumulatorSeconds > -1e-9)
            {
                _accumulatorSeconds = 0.0;
            }

            Tick += (ulong)executedTicks;
            return executedTicks;
        }

        public void Reset(ulong tick = 0)
        {
            Tick = tick;
            _accumulatorSeconds = 0.0;
        }
    }
}
