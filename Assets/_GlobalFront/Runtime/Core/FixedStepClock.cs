using System;

namespace GlobalFront.Core.Simulation
{
    /// <summary>
    /// Engine-independent fixed-step clock. The authoritative server will own
    /// one instance; presentation frame rate never changes the tick rate.
    /// </summary>
    public sealed class FixedStepClock
    {
        /// <summary>
        /// Default cap on queued time after a frame: five ticks at the 20 Hz
        /// contract. Once a hitch exceeds it, the simulation deliberately
        /// runs slower than the wall clock instead of accumulating a forever
        /// growing backlog (OD-45).
        /// </summary>
        public const double DefaultMaxBacklogSeconds = 0.25;

        private readonly double _tickDurationSeconds;
        private readonly int _maxCatchUpTicks;
        private readonly double _maxBacklogSeconds;
        private double _accumulatorSeconds;

        public FixedStepClock(
            int tickRate = SimulationConstants.ServerTickRate,
            int maxCatchUpTicks = SimulationConstants.MaxCatchUpTicksPerFrame,
            double maxBacklogSeconds = DefaultMaxBacklogSeconds)
        {
            if (tickRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRate));
            }

            if (maxCatchUpTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCatchUpTicks));
            }

            if (double.IsNaN(maxBacklogSeconds) ||
                double.IsInfinity(maxBacklogSeconds) ||
                maxBacklogSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBacklogSeconds));
            }

            _tickDurationSeconds = 1.0 / tickRate;
            _maxCatchUpTicks = maxCatchUpTicks;
            _maxBacklogSeconds = maxBacklogSeconds;
        }

        public ulong Tick { get; private set; }

        public double TickDurationSeconds => _tickDurationSeconds;

        public double BacklogSeconds => _accumulatorSeconds;

        /// <summary>Queued time cap enforced after every advance.</summary>
        public double MaxBacklogSeconds => _maxBacklogSeconds;

        public double InterpolationAlpha =>
            Math.Min(1.0, _accumulatorSeconds / _tickDurationSeconds);

        /// <summary>
        /// Adds elapsed real time and advances at most the configured catch-up
        /// limit. Time beyond the catch-up limit stays queued, but never more
        /// than <see cref="MaxBacklogSeconds"/>: a sustained hitch must not leave
        /// the simulation permanently behind the wall clock (OD-45). The clamp
        /// is applied after this call's ticks have executed, so it only discards
        /// time the catch-up limit already refused to simulate.
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

            if (executedTicks > 0)
            {
                _accumulatorSeconds -= executedTicks * _tickDurationSeconds;
            }

            if (_accumulatorSeconds < 0.0 && _accumulatorSeconds > -1e-9)
            {
                _accumulatorSeconds = 0.0;
            }

            if (_accumulatorSeconds > _maxBacklogSeconds)
            {
                _accumulatorSeconds = _maxBacklogSeconds;
            }

            if (executedTicks <= 0)
            {
                return 0;
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
