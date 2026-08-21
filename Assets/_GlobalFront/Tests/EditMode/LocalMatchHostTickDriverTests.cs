using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using NUnit.Framework;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Phase 2.3 (ADR-007) tests for the host-owned tick lifecycle.
    /// Verifies that the authoritative tick schedule is owned by the
    /// TickDriver inside LocalMatchHost: the two-phase per-tick contract
    /// (TickStarting → server tick → TickCompleted), the command timing
    /// contract (RequestedTick N applied during tick N), manual
    /// deterministic mode, real-time pacing at 20 Hz with bounded catch-up,
    /// and mode exclusivity.
    /// </summary>
    public sealed class LocalMatchHostTickDriverTests
    {
        private static readonly CombatStats StandardStats =
            new CombatStats(100, 10, 12000, 2);
        private const int SpeedMmPerTick = 500;

        [Test]
        public void ManualHost_TickOnce_ExecutesTwoPhaseLifecycleInOrder()
        {
            var host = BuildHost();
            var phaseLog = new List<string>();
            host.TickStarting += tick =>
                phaseLog.Add($"start:{tick}:serverAt{host.CurrentTick}");
            host.TickCompleted += tick =>
                phaseLog.Add($"done:{tick}:serverAt{host.CurrentTick}");

            host.TickOnce();
            host.TickOnce();

            Assert.That(host.CurrentTick, Is.EqualTo(2ul));
            Assert.That(phaseLog, Is.EqualTo(new List<string>
            {
                "start:1:serverAt0",
                "done:1:serverAt1",
                "start:2:serverAt1",
                "done:2:serverAt2"
            }));
        }

        [Test]
        public void ManualHost_CommandRequestedForTickN_IsAppliedDuringTickN()
        {
            var host = BuildHost();
            var queue = BuildQueue(host);

            // The client requests the move for the next authoritative tick,
            // exactly like the production controller does.
            queue.QueueMove(
                new WorldPointMm(10000, 0),
                new[] { new CoreEntityId(1) },
                requestedTick: 1);
            Assert.That(queue.PendingCount, Is.EqualTo(1));

            host.TickStarting += tick =>
                queue.ForwardPending(tick, new LocalCommandChannel(host));

            host.TickOnce();

            Assert.That(queue.PendingCount, Is.EqualTo(0));
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out var snapshot), Is.True);
            Assert.That(snapshot.HasMoveTarget, Is.True);
            Assert.That(snapshot.Position, Is.EqualTo(new WorldPointMm(SpeedMmPerTick, 0)));
        }

        [Test]
        public void ManualHost_MultipleTicks_AdvanceInSequentialDeterministicOrder()
        {
            var host = BuildHost();
            var completed = new List<ulong>();
            host.TickCompleted += completed.Add;

            for (var index = 0; index < 10; index++)
            {
                Assert.That(host.TickOnce(), Is.True);
            }

            Assert.That(host.CurrentTick, Is.EqualTo(10ul));
            var expected = new List<ulong>();
            for (var tick = 1ul; tick <= 10ul; tick++)
            {
                expected.Add(tick);
            }

            Assert.That(completed, Is.EqualTo(expected));
        }

        [Test]
        public void RealTimeHost_AdvanceRealTime_ExecutesAuthoritativeTicksAt20Hz()
        {
            var host = BuildHost();
            var queue = BuildQueue(host);
            var completed = new List<ulong>();
            host.TickCompleted += completed.Add;
            host.TickStarting += tick =>
                queue.ForwardPending(tick, new LocalCommandChannel(host));

            queue.QueueMove(
                new WorldPointMm(10000, 0),
                new[] { new CoreEntityId(1) },
                requestedTick: 3);

            host.StartRealTime();

            // Pump the paced driver until 20 authoritative ticks have run.
            // Each call executes at most the bounded catch-up limit, so the
            // 20 Hz pacing accumulates across calls.
            var executed = 0;
            while (host.CurrentTick < 20ul)
            {
                executed += host.AdvanceRealTime(0.2);
            }

            Assert.That(executed, Is.EqualTo(20));
            Assert.That(host.CurrentTick, Is.EqualTo(20ul));
            Assert.That(completed.Count, Is.EqualTo(20));
            Assert.That(completed[0], Is.EqualTo(1ul));
            Assert.That(completed[19], Is.EqualTo(20ul));
            Assert.That(queue.PendingCount, Is.EqualTo(0));

            // The command was requested for tick 3 and applied during tick 3,
            // so movement covers ticks 3..20 = 18 ticks x 500 mm = 9000 mm.
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out var snapshot), Is.True);
            Assert.That(snapshot.Position, Is.EqualTo(new WorldPointMm(9000, 0)));
            Assert.That(snapshot.HasMoveTarget, Is.True);

            // Two more paced ticks land the unit exactly on the destination.
            while (host.CurrentTick < 22ul)
            {
                host.AdvanceRealTime(0.2);
            }
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out var arrived), Is.True);
            Assert.That(arrived.Position, Is.EqualTo(new WorldPointMm(10000, 0)));
            Assert.That(arrived.HasMoveTarget, Is.False);
        }

        [Test]
        public void RealTimeHost_UsesBoundedCatchUpPerAdvance()
        {
            var host = BuildHost();
            var completed = new List<ulong>();
            host.TickCompleted += completed.Add;

            host.StartRealTime();

            // 0.25 s at 20 Hz = 5 ticks, but the canonical catch-up limit is
            // 4: only 4 ticks run now, the rest stay queued.
            Assert.That(host.AdvanceRealTime(0.25), Is.EqualTo(4));
            Assert.That(host.CurrentTick, Is.EqualTo(4ul));
            Assert.That(completed.Count, Is.EqualTo(4));

            Assert.That(host.AdvanceRealTime(0.0), Is.EqualTo(1));
            Assert.That(host.CurrentTick, Is.EqualTo(5ul));
            Assert.That(completed.Count, Is.EqualTo(5));
        }

        [Test]
        public void RealTimeHost_TickOnce_IsRejectedUntilStopRealTime()
        {
            var host = BuildHost();

            host.StartRealTime();
            Assert.That(host.TickOnce(), Is.False);
            Assert.That(host.CurrentTick, Is.EqualTo(0ul));
            Assert.That(host.TickDriverMode, Is.EqualTo(TickDriverMode.RealTime));

            host.StopRealTime();
            Assert.That(host.TickOnce(), Is.True);
            Assert.That(host.CurrentTick, Is.EqualTo(1ul));
            Assert.That(host.TickDriverMode, Is.EqualTo(TickDriverMode.Manual));
        }

        [Test]
        public void EmptyHost_TicksWithoutUnits_StayInProgress()
        {
            var host = new LocalMatchHost();

            for (var index = 0; index < 5; index++)
            {
                Assert.That(host.TickOnce(), Is.True);
            }

            Assert.That(host.CurrentTick, Is.EqualTo(5ul));
            Assert.That(host.Outcome.IsTerminal, Is.False,
                "An uninitialized server must not report a terminal (draw) outcome.");
            Assert.That(host.Outcome, Is.EqualTo(BattleOutcome.InProgress));
            Assert.That(host.GetAllSnapshots().Length, Is.Zero);
        }

        [Test]
        public void TerminalOutcome_KeepsTickingWithStableSnapshots()
        {
            var host = new LocalMatchHost();
            host.SpawnUnitWithEntity(
                new CoreEntityId(1), new PlayerId(1), StandardStats,
                new WorldPointMm(0, 0), SpeedMmPerTick);
            host.SpawnUnitWithEntity(
                new CoreEntityId(2), new PlayerId(2), StandardStats,
                new WorldPointMm(5000, 0), SpeedMmPerTick);

            Assert.That(
                host.TryEnqueueAttack(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Attack),
                    new[] { new CoreEntityId(1) },
                    new CoreEntityId(2)),
                Is.EqualTo(MatchCommandRejection.None));

            for (var index = 0; index < 60; index++)
            {
                host.TickOnce();
            }

            Assert.That(host.Outcome.IsTerminal, Is.True);
            var terminalTick = host.CurrentTick;
            var terminalSnapshots = host.GetAllSnapshots();

            host.TickOnce();
            host.TickOnce();

            Assert.That(host.CurrentTick, Is.EqualTo(terminalTick + 2));
            Assert.That(host.GetAllSnapshots(), Is.EqualTo(terminalSnapshots));
        }

        [Test]
        public void ManualAndRealTimeHosts_ProduceIdenticalSnapshots()
        {
            // The same simulation core must produce the same state whether
            // the ticks are driven deterministically (tests) or paced in
            // real time (local host / future dedicated server).
            var manual = BuildScriptedHost(isRealTime: false);
            var realtime = BuildScriptedHost(isRealTime: true);

            Assert.That(manual.CurrentTick, Is.EqualTo(realtime.CurrentTick));
            Assert.That(manual.Outcome, Is.EqualTo(realtime.Outcome));

            var manualSnapshots = manual.GetAllSnapshots();
            var realtimeSnapshots = realtime.GetAllSnapshots();
            Assert.That(manualSnapshots.Length, Is.EqualTo(realtimeSnapshots.Length));
            for (var index = 0; index < manualSnapshots.Length; index++)
            {
                Assert.That(
                    realtimeSnapshots[index],
                    Is.EqualTo(manualSnapshots[index]),
                    $"Entity {manualSnapshots[index].Entity} diverged between manual and real-time scheduling.");
            }
        }

        private static LocalMatchHost BuildHost()
        {
            var host = new LocalMatchHost();
            host.SpawnUnitWithEntity(
                new CoreEntityId(1), new PlayerId(1), StandardStats,
                new WorldPointMm(0, 0), SpeedMmPerTick);
            host.SpawnUnitWithEntity(
                new CoreEntityId(2), new PlayerId(2), StandardStats,
                new WorldPointMm(100_000, 0), SpeedMmPerTick);
            return host;
        }

        private static PrototypeCommandQueue BuildQueue(LocalMatchHost host)
        {
            var registry = new UnitRegistry();
            return new PrototypeCommandQueue(
                registry, new PlayerId(1), formationSpacingMm: 2000);
        }

        private static LocalMatchHost BuildScriptedHost(bool isRealTime)
        {
            var host = new LocalMatchHost();
            for (var index = 1; index <= 3; index++)
            {
                host.SpawnUnitWithEntity(
                    new CoreEntityId((ulong)index),
                    new PlayerId(1),
                    StandardStats,
                    new WorldPointMm(-20000 + index * 2000, -20000),
                    SpeedMmPerTick);
            }

            for (var index = 4; index <= 6; index++)
            {
                host.SpawnUnitWithEntity(
                    new CoreEntityId((ulong)index),
                    new PlayerId(2),
                    StandardStats,
                    new WorldPointMm(-20000 + (index - 3) * 2000, 20000),
                    SpeedMmPerTick);
            }

            var formation = new FormationSpec(0, 4000, CardinalFacing.North);
            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                    Entities(1, 2, 3), new WorldPointMm(0, -3000), formation),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(new PlayerId(2), 1, 1, GameCommandType.Move),
                    Entities(4, 5, 6), new WorldPointMm(0, 3000), formation),
                Is.EqualTo(MatchCommandRejection.None));

            if (isRealTime)
            {
                host.StartRealTime();
                while (host.CurrentTick < 60ul)
                {
                    host.AdvanceRealTime(0.05);
                }
            }
            else
            {
                for (var index = 0; index < 60; index++)
                {
                    host.TickOnce();
                }
            }

            return host;
        }

        private static CoreEntityId[] Entities(params ulong[] values)
        {
            var entities = new CoreEntityId[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                entities[index] = new CoreEntityId(values[index]);
            }

            return entities;
        }
    }
}
