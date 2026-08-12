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
    }
}
