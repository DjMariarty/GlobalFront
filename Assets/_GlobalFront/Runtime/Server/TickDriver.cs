using System;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Server
{
    /// <summary>
    /// Operating mode of a <see cref="TickDriver"/>.
    /// </summary>
    public enum TickDriverMode : byte
    {
        /// <summary>
        /// Deterministic mode for tests: ticks run only through
        /// <see cref="TickDriver.AdvanceManualTick"/>.
        /// </summary>
        Manual = 0,

        /// <summary>
        /// Paced mode: ticks run at the configured rate (20 Hz contract),
        /// driven by <see cref="TickDriver.AdvanceRealTime"/>.
        /// </summary>
        RealTime = 1
    }

    /// <summary>
    /// Scheduling boundary for the authoritative server tick (ADR-007).
    /// Decides <i>when</i> a server tick is due; contains no simulation
    /// logic and no Unity API, so the same driver schedules ticks for the
    /// local host and for a future dedicated server process.
    ///
    /// Real-time mode paces ticks through the engine-independent
    /// <see cref="FixedStepClock"/> at <see cref="SimulationConstants.ServerTickRate"/>
    /// (20 Hz) with the bounded catch-up limit
    /// <see cref="SimulationConstants.MaxCatchUpTicksPerFrame"/>: excess time
    /// stays queued and is drained on subsequent calls instead of silently
    /// dropping ticks. Manual mode executes exactly one tick per
    /// <see cref="TickDriver.AdvanceManualTick"/> call for deterministic
    /// tests.
    ///
    /// The driver raises <see cref="TickDue"/> for every scheduled tick; the
    /// hosting layer (which owns the server lifecycle) performs the
    /// simulation inside that handler.
    /// </summary>
    public sealed class TickDriver
    {
        private readonly FixedStepClock _clock;
        private readonly object _gate = new object();

        private ulong _tick;
        private TickDriverMode _mode = TickDriverMode.Manual;
        private bool _isPaused;

        /// <summary>
        /// Creates a driver for the given tick rate and catch-up limit.
        /// Defaults to the canonical 20 Hz server tick rate and the
        /// canonical catch-up limit. The driver starts in manual mode.
        /// </summary>
        public TickDriver(
            int tickRate = SimulationConstants.ServerTickRate,
            int maxCatchUpTicks = SimulationConstants.MaxCatchUpTicksPerFrame)
        {
            _clock = new FixedStepClock(tickRate, maxCatchUpTicks);
        }

        /// <summary>Index of the last scheduled tick.</summary>
        public ulong Tick => _tick;

        /// <summary>Current scheduling mode.</summary>
        public TickDriverMode Mode
        {
            get
            {
                lock (_gate)
                {
                    return _mode;
                }
            }
        }

        /// <summary>
        /// True when tick calculation is suspended for tactical pause (Phase 2.7, OD-18).
        /// Neither real-time nor manual ticks will execute while paused.
        /// </summary>
        public bool IsPaused
        {
            get
            {
                lock (_gate)
                {
                    return _isPaused;
                }
            }
        }

        /// <summary>Halts tick calculation for tactical pause (OD-18).</summary>
        public void Pause()
        {
            lock (_gate)
            {
                _isPaused = true;
            }
        }

        /// <summary>Resumes tick calculation after tactical pause (OD-18/OD-20).</summary>
        public void Resume()
        {
            lock (_gate)
            {
                _isPaused = false;
            }
        }

        /// <summary>Authoritative tick duration in seconds.</summary>
        public double TickDurationSeconds => _clock.TickDurationSeconds;

        /// <summary>Queued (not yet simulated) time in seconds.</summary>
        public double BacklogSeconds
        {
            get
            {
                lock (_gate)
                {
                    return _clock.BacklogSeconds;
                }
            }
        }

        /// <summary>
        /// Raised when the driver schedules a tick for execution, with the
        /// tick index the hosting layer must simulate. Fired outside the
        /// internal lock, once per scheduled tick.
        /// </summary>
        public event Action<ulong> TickDue;

        /// <summary>
        /// Resets the driver to manual mode at the given tick index, clearing
        /// any queued time. This is the deterministic starting state for
        /// tests.
        /// </summary>
        public void Reset(ulong tick = 0)
        {
            lock (_gate)
            {
                _mode = TickDriverMode.Manual;
                _tick = tick;
                _isPaused = false;
                _clock.Reset(tick);
            }
        }

        /// <summary>
        /// Starts (or resumes) real-time pacing. From this point on the tick
        /// schedule is driven by <see cref="AdvanceRealTime"/> and manual
        /// advances are rejected.
        /// </summary>
        public void StartRealTime()
        {
            lock (_gate)
            {
                _mode = TickDriverMode.RealTime;
            }
        }

        /// <summary>
        /// Stops real-time pacing and returns the driver to manual mode.
        /// The tick index and backlog are preserved.
        /// </summary>
        public void StopRealTime()
        {
            lock (_gate)
            {
                _mode = TickDriverMode.Manual;
            }
        }

        /// <summary>
        /// Feeds elapsed wall time into the real-time accumulator. In
        /// real-time mode this schedules up to the bounded catch-up limit of
        /// ticks per call, raising <see cref="TickDue"/> for each scheduled
        /// tick. Excess time remains queued and is drained on subsequent
        /// calls. Returns the number of ticks scheduled this call; returns 0
        /// when the driver is in manual mode.
        /// </summary>
        public int AdvanceRealTime(double elapsedSeconds)
        {
            int scheduled;
            lock (_gate)
            {
                if (_mode != TickDriverMode.RealTime || _isPaused)
                {
                    return 0;
                }

                scheduled = _clock.Advance(elapsedSeconds);
                _tick = _clock.Tick;
            }

            for (var index = 1; index <= scheduled; index++)
            {
                TickDue?.Invoke(_tick - (ulong)scheduled + (ulong)index);
            }

            return scheduled;
        }

        /// <summary>
        /// Deterministically schedules exactly one tick in manual mode and
        /// raises <see cref="TickDue"/> with the scheduled tick index.
        /// Returns false when the driver is in real-time mode, where the
        /// schedule is owned by <see cref="AdvanceRealTime"/>.
        /// </summary>
        public bool AdvanceManualTick()
        {
            ulong scheduledTick;
            lock (_gate)
            {
                if (_mode == TickDriverMode.RealTime || _isPaused)
                {
                    return false;
                }

                _tick++;
                _clock.Reset(_tick);
                scheduledTick = _tick;
            }

            TickDue?.Invoke(scheduledTick);
            return true;
        }
    }
}
