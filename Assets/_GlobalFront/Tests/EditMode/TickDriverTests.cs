using System.Collections.Generic;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Unit tests for the server tick driver scheduling boundary (Phase 2.3,
    /// ADR-007). The driver only schedules ticks: no simulation logic, no
    /// Unity API. Verified: 20 Hz pacing, deterministic manual mode,
    /// bounded catch-up, and mode exclusivity.
    /// </summary>
    public sealed class TickDriverTests
    {
        [Test]
        public void DefaultDriver_UsesCanonicalTwentyHzContract()
        {
            var driver = new TickDriver();

            Assert.That(driver.TickDurationSeconds, Is.EqualTo(SimulationConstants.ServerTickDurationSeconds));
        }

        [Test]
        public void Driver_StartsInManualMode()
        {
            var driver = new TickDriver();

            Assert.That(driver.Mode, Is.EqualTo(TickDriverMode.Manual));
            Assert.That(driver.Tick, Is.EqualTo(0ul));
        }

        [Test]
        public void ManualMode_RaisesExactlyOneTickDuePerAdvance()
        {
            var driver = new TickDriver();
            var dueTicks = new List<ulong>();
            driver.TickDue += dueTicks.Add;

            for (var index = 0; index < 5; index++)
            {
                Assert.That(driver.AdvanceManualTick(), Is.True);
            }

            Assert.That(driver.Tick, Is.EqualTo(5ul));
            Assert.That(dueTicks, Is.EqualTo(new List<ulong> { 1, 2, 3, 4, 5 }));
        }

        [Test]
        public void ManualMode_InRealTimeMode_IsRejected()
        {
            var driver = new TickDriver();
            var dueTicks = new List<ulong>();
            driver.TickDue += dueTicks.Add;
            driver.StartRealTime();

            Assert.That(driver.AdvanceManualTick(), Is.False);

            Assert.That(driver.Tick, Is.EqualTo(0ul));
            Assert.That(dueTicks, Is.Empty);
        }

        [Test]
        public void RealTimeMode_PacesExactlyTwentyTicksPerSecond()
        {
            var driver = new TickDriver();
            var dueTicks = new List<ulong>();
            driver.TickDue += dueTicks.Add;
            driver.StartRealTime();

            // One second of wall time equals exactly 20 ticks at 20 Hz.
            // The canonical catch-up limit (4) bounds each call, so the
            // time is pumped in chunks and the queued backlog is drained.
            var scheduled = 0;
            while (driver.Tick < 20ul)
            {
                scheduled += driver.AdvanceRealTime(0.2);
            }

            Assert.That(scheduled, Is.EqualTo(20));
            Assert.That(driver.Tick, Is.EqualTo(20ul));
            Assert.That(dueTicks.Count, Is.EqualTo(20));
            Assert.That(dueTicks[0], Is.EqualTo(1ul));
            Assert.That(dueTicks[19], Is.EqualTo(20ul));
            Assert.That(driver.BacklogSeconds, Is.EqualTo(0.0).Within(1e-9));
        }

        [Test]
        public void RealTimeMode_RaisesTickDueInSequentialOrder()
        {
            var driver = new TickDriver();
            var dueTicks = new List<ulong>();
            driver.TickDue += dueTicks.Add;
            driver.StartRealTime();

            driver.AdvanceRealTime(0.02);
            driver.AdvanceRealTime(0.03);

            // 0.05 s accumulated = exactly one 50 ms tick.
            Assert.That(dueTicks.Count, Is.EqualTo(1));
            Assert.That(dueTicks[0], Is.EqualTo(1ul));

            driver.AdvanceRealTime(0.05);

            Assert.That(dueTicks, Is.EqualTo(new List<ulong> { 1, 2 }));
            Assert.That(driver.Tick, Is.EqualTo(2ul));
        }

        [Test]
        public void RealTimeMode_LimitsCatchUpToConfiguredMaximum()
        {
            var driver = new TickDriver(maxCatchUpTicks: 4);
            var dueTicks = new List<ulong>();
            driver.TickDue += dueTicks.Add;
            driver.StartRealTime();

            var scheduled = driver.AdvanceRealTime(0.25);

            Assert.That(scheduled, Is.EqualTo(4));
            Assert.That(driver.Tick, Is.EqualTo(4ul));
            Assert.That(dueTicks, Is.EqualTo(new List<ulong> { 1, 2, 3, 4 }));

            // The remaining tick stays queued (0.05 s backlog) and is
            // drained on the next call, bounded by the same catch-up limit.
            Assert.That(driver.AdvanceRealTime(0.0), Is.EqualTo(1));
            Assert.That(driver.Tick, Is.EqualTo(5ul));
            Assert.That(dueTicks, Is.EqualTo(new List<ulong> { 1, 2, 3, 4, 5 }));
        }

        [Test]
        public void RealTimeMode_PreservesFractionalTimeBetweenCalls()
        {
            var driver = new TickDriver();
            var dueTicks = new List<ulong>();
            driver.TickDue += dueTicks.Add;
            driver.StartRealTime();

            Assert.That(driver.AdvanceRealTime(0.049), Is.Zero);
            Assert.That(dueTicks, Is.Empty);
            Assert.That(driver.AdvanceRealTime(0.001), Is.EqualTo(1));

            Assert.That(dueTicks, Is.EqualTo(new List<ulong> { 1 }));
            Assert.That(driver.Tick, Is.EqualTo(1ul));
        }

        [Test]
        public void RealTimeMode_WithoutRealTimeStart_SchedulesNothing()
        {
            var driver = new TickDriver();
            var dueTicks = new List<ulong>();
            driver.TickDue += dueTicks.Add;

            Assert.That(driver.AdvanceRealTime(1.0), Is.Zero);

            Assert.That(driver.Tick, Is.EqualTo(0ul));
            Assert.That(dueTicks, Is.Empty);
            Assert.That(driver.BacklogSeconds, Is.EqualTo(0.0));
        }

        [Test]
        public void StopRealTime_PreservesTickAndBacklogAndReturnsToManualMode()
        {
            var driver = new TickDriver();
            driver.StartRealTime();
            driver.AdvanceRealTime(0.045);
            driver.AdvanceRealTime(0.25); // 4 executed, backlog queued

            var tickBefore = driver.Tick;
            var backlogBefore = driver.BacklogSeconds;
            driver.StopRealTime();

            Assert.That(driver.Mode, Is.EqualTo(TickDriverMode.Manual));
            Assert.That(driver.Tick, Is.EqualTo(tickBefore));
            Assert.That(driver.BacklogSeconds, Is.EqualTo(backlogBefore).Within(1e-12));

            Assert.That(driver.AdvanceManualTick(), Is.True);
            Assert.That(driver.Tick, Is.EqualTo(tickBefore + 1));
        }

        [Test]
        public void Reset_RestoresDeterministicManualState()
        {
            var driver = new TickDriver();
            driver.StartRealTime();
            driver.AdvanceRealTime(0.5);
            driver.StopRealTime();

            driver.Reset(100);

            Assert.That(driver.Mode, Is.EqualTo(TickDriverMode.Manual));
            Assert.That(driver.Tick, Is.EqualTo(100ul));
            Assert.That(driver.BacklogSeconds, Is.EqualTo(0.0));

            var dueTicks = new List<ulong>();
            driver.TickDue += dueTicks.Add;
            driver.AdvanceManualTick();

            Assert.That(dueTicks, Is.EqualTo(new List<ulong> { 101 }));
            Assert.That(driver.Tick, Is.EqualTo(101ul));
        }

        [Test]
        public void CustomTickRate_IsUsedForPacing()
        {
            var driver = new TickDriver(tickRate: 100, maxCatchUpTicks: 8);

            Assert.That(driver.TickDurationSeconds, Is.EqualTo(0.01).Within(1e-12));

            driver.StartRealTime();
            Assert.That(driver.AdvanceRealTime(0.05), Is.EqualTo(5));
            Assert.That(driver.Tick, Is.EqualTo(5ul));
        }

        [Test]
        public void Constructor_RejectsNonPositiveTickRateAndCatchUp()
        {
            Assert.That(() => new TickDriver(0), Throws.InstanceOf<System.ArgumentOutOfRangeException>());
            Assert.That(() => new TickDriver(10, 0), Throws.InstanceOf<System.ArgumentOutOfRangeException>());
            Assert.That(() => new TickDriver(-5), Throws.InstanceOf<System.ArgumentOutOfRangeException>());
        }
    }
}
