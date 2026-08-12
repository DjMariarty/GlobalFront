using System;
using GlobalFront.Core.Simulation;
using UnityEngine;

namespace GlobalFront.Client
{
    /// <summary>
    /// Early integration runner for validating the 20 Hz contract inside the
    /// Editor. The final authoritative loop will live in the server ECS world.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class FixedSimulationRunner : MonoBehaviour
    {
        private FixedStepClock _clock;

        public event Action<ulong> TickExecuted;

        public ulong Tick => _clock?.Tick ?? 0;

        public double BacklogMilliseconds => (_clock?.BacklogSeconds ?? 0.0) * 1000.0;

        public float InterpolationAlpha => (float)(_clock?.InterpolationAlpha ?? 0.0);

        public int TicksExecutedLastFrame { get; private set; }

        private void Awake()
        {
            _clock = new FixedStepClock();
        }

        private void Update()
        {
            var firstTick = _clock.Tick + 1;
            TicksExecutedLastFrame = _clock.Advance(Time.unscaledDeltaTime);

            for (var index = 0; index < TicksExecutedLastFrame; index++)
            {
                TickExecuted?.Invoke(firstTick + (ulong)index);
            }
        }
    }
}
