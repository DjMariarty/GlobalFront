using GlobalFront.Core.Simulation;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    public sealed class FixedStepClockTests
    {
        [Test]
        public void Advance_UsesExactlyTwentyTicksPerSecond()
        {
            var clock = new FixedStepClock(maxCatchUpTicks: 32);

            var executed = clock.Advance(1.0);

            Assert.That(executed, Is.EqualTo(20));
            Assert.That(clock.Tick, Is.EqualTo(20));
            Assert.That(clock.BacklogSeconds, Is.EqualTo(0.0).Within(1e-9));
        }

        [Test]
        public void Advance_PreservesFractionalTimeBetweenFrames()
        {
            var clock = new FixedStepClock();

            Assert.That(clock.Advance(0.049), Is.Zero);
            Assert.That(clock.Advance(0.001), Is.EqualTo(1));
            Assert.That(clock.Tick, Is.EqualTo(1));
        }

        [Test]
        public void Advance_KeepsBacklogWhenCatchUpLimitIsReached()
        {
            var clock = new FixedStepClock(maxCatchUpTicks: 4);

            Assert.That(clock.Advance(0.25), Is.EqualTo(4));
            Assert.That(clock.Tick, Is.EqualTo(4));
            Assert.That(clock.Advance(0.0), Is.EqualTo(1));
            Assert.That(clock.Tick, Is.EqualTo(5));
        }

        [Test]
        public void Reset_ClearsAccumulatedTimeAndSetsTick()
        {
            var clock = new FixedStepClock();
            clock.Advance(0.049);

            clock.Reset(100);

            Assert.That(clock.Tick, Is.EqualTo(100));
            Assert.That(clock.BacklogSeconds, Is.Zero);
        }

        /// <summary>
        /// OD-45: a five-second hitch may not leave five seconds of queued
        /// simulation behind. The catch-up limit runs four ticks now and the
        /// remainder is capped, so the clock recovers within a few frames
        /// instead of lagging the wall clock for the rest of the match.
        /// </summary>
        [Test]
        public void Advance_LongSpike_ClampsQueuedBacklog()
        {
            var clock = new FixedStepClock();

            Assert.That(clock.Advance(5.0), Is.EqualTo(4));
            Assert.That(clock.Tick, Is.EqualTo(4UL));
            Assert.That(
                clock.BacklogSeconds,
                Is.EqualTo(clock.MaxBacklogSeconds).Within(1e-12),
                "the spike must leave exactly the cap queued, not the 4.8 s the catch-up limit refused");

            Assert.That(clock.Advance(0.0), Is.EqualTo(4));
            Assert.That(clock.Advance(0.0), Is.EqualTo(1));
            Assert.That(
                clock.BacklogSeconds,
                Is.EqualTo(0.0).Within(1e-12),
                "the capped backlog has to drain within two zero-length catch-up frames");
        }

        /// <summary>
        /// The clamp only discards time the catch-up limit refused to run. A
        /// clock allowed to execute the whole window must still execute it,
        /// otherwise the cap would silently throttle normal large advances.
        /// </summary>
        [Test]
        public void Advance_BelowCatchUpLimit_IsNotClamped()
        {
            var clock = new FixedStepClock(maxCatchUpTicks: 128);

            Assert.That(clock.Advance(5.0), Is.EqualTo(100));
            Assert.That(clock.Tick, Is.EqualTo(100UL));
            Assert.That(clock.BacklogSeconds, Is.EqualTo(0.0).Within(1e-12));
        }

        [Test]
        public void Advance_UsesConfiguredBacklogCap()
        {
            var clock = new FixedStepClock(
                maxCatchUpTicks: 4,
                maxBacklogSeconds: 0.1);

            Assert.That(clock.Advance(5.0), Is.EqualTo(4));
            Assert.That(clock.BacklogSeconds, Is.EqualTo(0.1).Within(1e-12));
        }

        [Test]
        public void Constructor_RejectsInvalidBacklogCap()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new FixedStepClock(maxBacklogSeconds: -0.1));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new FixedStepClock(maxBacklogSeconds: double.NaN));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new FixedStepClock(maxBacklogSeconds: double.PositiveInfinity));
        }
    }
}
