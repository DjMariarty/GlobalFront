using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    public sealed class MatchServerTests
    {
        private static readonly CombatStats StandardStats =
            new CombatStats(100, 10, 12000, 2);

        [Test]
        public void MoveCommand_ExecutesAtRequestedTick_AndReachesDestination()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            SpawnPassiveOpponent(server);

            var rejection = server.TryEnqueueMove(
                new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                Entities(1),
                new WorldPointMm(10000, 0),
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(rejection, Is.EqualTo(MatchCommandRejection.None));

            server.ExecuteTicks(4);
            Assert.That(server.TryGetUnit(unit, out var mid), Is.True);
            Assert.That(mid.Position, Is.EqualTo(new WorldPointMm(2000, 0)));

            server.ExecuteTicks(16);
            Assert.That(server.TryGetUnit(unit, out var final), Is.True);
            Assert.That(final.Position, Is.EqualTo(new WorldPointMm(10000, 0)));
            Assert.That(final.HasMoveTarget, Is.False);
        }

        [Test]
        public void ScheduledCommand_WaitsUntilRequestedTick()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            SpawnPassiveOpponent(server);

            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 1, 5, GameCommandType.Move),
                    Entities(1),
                    new WorldPointMm(10000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            server.ExecuteTicks(4);
            Assert.That(server.TryGetUnit(unit, out var before), Is.True);
            Assert.That(before.Position, Is.EqualTo(new WorldPointMm(0, 0)));

            server.TickOnce();
            Assert.That(server.TryGetUnit(unit, out var after), Is.True);
            Assert.That(after.Position, Is.EqualTo(new WorldPointMm(500, 0)));
        }

        [Test]
        public void AttackCommand_KillsTargetAndReportsVictory()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                new PlayerId(2), StandardStats, new WorldPointMm(5000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Attack),
                    Entities(1),
                    target),
                Is.EqualTo(MatchCommandRejection.None));

            // 100 hp / 10 damage every 2 ticks, first shot on tick 1.
            server.ExecuteTicks(60);

            Assert.That(server.Outcome.IsTerminal, Is.True);
            Assert.That(server.Outcome.Kind, Is.EqualTo(BattleOutcomeKind.Victory));
            Assert.That(server.Outcome.Winner, Is.EqualTo(new PlayerId(1)));
            Assert.That(server.TryGetUnit(target, out var targetSnapshot), Is.True);
            Assert.That(targetSnapshot.CurrentHealth, Is.Zero);
        }

        [Test]
        public void StopCommand_ClearsMoveTargetBeforeMovement()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            SpawnPassiveOpponent(server);

            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                    Entities(1),
                    new WorldPointMm(10000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                server.TryEnqueueStop(
                    new CommandHeader(new PlayerId(1), 2, 3, GameCommandType.Stop),
                    Entities(1)),
                Is.EqualTo(MatchCommandRejection.None));

            server.ExecuteTicks(10);

            Assert.That(server.TryGetUnit(unit, out var snapshot), Is.True);
            Assert.That(snapshot.HasMoveTarget, Is.False);
            Assert.That(snapshot.Position, Is.EqualTo(new WorldPointMm(1000, 0)));
        }

        [Test]
        public void DuplicateOrRegressedSequence_IsRejected()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            var destination = new WorldPointMm(1000, 0);
            var formation = new FormationSpec(1, 2000, CardinalFacing.North);

            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 5, 1, GameCommandType.Move),
                    Entities(1), destination, formation),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 5, 2, GameCommandType.Move),
                    Entities(1), destination, formation),
                Is.EqualTo(MatchCommandRejection.DuplicateSequence));
            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 4, 3, GameCommandType.Move),
                    Entities(1), destination, formation),
                Is.EqualTo(MatchCommandRejection.DuplicateSequence));
        }

        [Test]
        public void ForeignUnitCommand_IsRejected()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            server.SpawnUnit(
                new PlayerId(2), StandardStats, new WorldPointMm(10000, 0), 500);

            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(new PlayerId(2), 1, 1, GameCommandType.Move),
                    new[] { unit },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.NotEntityOwner));
        }

        [Test]
        public void WrongCommandType_IsRejected()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);

            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Stop),
                    new[] { unit },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.UnsupportedCommandType));
        }

        [Test]
        public void FormationExceedingWorldBounds_IsRejected()
        {
            var server = new MatchServer();
            server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(3000, 0), 500);

            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                    Entities(1, 2),
                    new WorldPointMm(1_000_000_000, 0),
                    new FormationSpec(2, 100_000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.FormationOutOfBounds));
        }

        [Test]
        public void IdenticalSetupAndCommands_ProduceIdenticalSimulation()
        {
            var first = BuildStandardServer();
            var second = BuildStandardServer();

            RunStandardScript(first);
            RunStandardScript(second);

            Assert.That(first.CurrentTick, Is.EqualTo(second.CurrentTick));
            Assert.That(first.Outcome, Is.EqualTo(second.Outcome));
            Assert.That(first.Outcome.IsTerminal, Is.True);

            for (ulong value = 1; value <= 6; value++)
            {
                var entity = new EntityId(value);
                Assert.That(first.TryGetUnit(entity, out var firstSnapshot), Is.True);
                Assert.That(second.TryGetUnit(entity, out var secondSnapshot), Is.True);
                Assert.That(
                    firstSnapshot,
                    Is.EqualTo(secondSnapshot),
                    $"Entity {value} diverged between server instances.");
            }
        }

        [Test]
        public void AutoAcquire_FindsClosestEnemyInRange()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500,
                autoAcquire: true);
            var near = server.SpawnUnit(
                new PlayerId(2), StandardStats, new WorldPointMm(5000, 0), 500);
            server.SpawnUnit(
                new PlayerId(2), StandardStats, new WorldPointMm(17000, 0), 500);

            server.TickOnce();

            Assert.That(server.TryGetUnit(unit, out var snapshot), Is.True);
            Assert.That(snapshot.AttackTarget, Is.EqualTo(near));
            Assert.That(snapshot.AutoAcquireEnemies, Is.True);
        }

        [Test]
        public void AutoAcquire_DoesNotAcquireBeyondRange()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500,
                autoAcquire: true);
            server.SpawnUnit(
                new PlayerId(2), StandardStats, new WorldPointMm(100_000, 0), 500);

            server.TickOnce();

            Assert.That(server.TryGetUnit(unit, out var snapshot), Is.True);
            Assert.That(snapshot.AttackTarget.IsValid, Is.False);
        }

        [Test]
        public void AutoAcquire_DisabledByDefault()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            server.SpawnUnit(
                new PlayerId(2), StandardStats, new WorldPointMm(5000, 0), 500);

            server.TickOnce();

            Assert.That(server.TryGetUnit(unit, out var snapshot), Is.True);
            Assert.That(snapshot.AttackTarget.IsValid, Is.False);
            Assert.That(snapshot.AutoAcquireEnemies, Is.False);
        }

        [Test]
        public void AttackTarget_PursuesWhenOutOfRange()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                new PlayerId(2), StandardStats, new WorldPointMm(20000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Attack),
                    Entities(1), target),
                Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();

            Assert.That(server.TryGetUnit(attacker, out var snapshot), Is.True);
            Assert.That(snapshot.Position, Is.EqualTo(new WorldPointMm(500, 0)));
            Assert.That(snapshot.HasMoveTarget, Is.True);
        }

        [Test]
        public void AttackTarget_StopsWhenInRange()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                new PlayerId(2), StandardStats, new WorldPointMm(10000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Attack),
                    Entities(1), target),
                Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();

            Assert.That(server.TryGetUnit(attacker, out var snapshot), Is.True);
            Assert.That(snapshot.Position, Is.EqualTo(new WorldPointMm(0, 0)));
            Assert.That(snapshot.HasMoveTarget, Is.False);
        }

        [Test]
        public void MoveCommand_DisablesAutoAcquire()
        {
            var server = new MatchServer();
            var unit = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500,
                autoAcquire: true);
            SpawnPassiveOpponent(server);

            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                    Entities(1),
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();

            Assert.That(server.TryGetUnit(unit, out var snapshot), Is.True);
            Assert.That(snapshot.AutoAcquireEnemies, Is.False);
            Assert.That(snapshot.AttackTarget.IsValid, Is.False);
        }

        [Test]
        public void ClearInvalidAttackTargets_RemovesDeadTarget()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                new PlayerId(2), StandardStats, new WorldPointMm(1000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Attack),
                    Entities(1), target),
                Is.EqualTo(MatchCommandRejection.None));

            // 100 hp / 10 dmg every 2 ticks = 20 ticks to kill.
            server.ExecuteTicks(40);

            Assert.That(server.TryGetUnit(target, out var targetSnapshot), Is.True);
            Assert.That(targetSnapshot.CurrentHealth, Is.Zero);

            Assert.That(server.TryGetUnit(attacker, out var attackerSnapshot), Is.True);
            Assert.That(attackerSnapshot.AttackTarget.IsValid, Is.False);
        }

        private static MatchServer BuildStandardServer()
        {
            var server = new MatchServer();

            for (var index = 0; index < 3; index++)
            {
                server.SpawnUnit(
                    new PlayerId(1),
                    StandardStats,
                    new WorldPointMm(-20000 + index * 2000, -20000),
                    500);
            }

            for (var index = 0; index < 3; index++)
            {
                server.SpawnUnit(
                    new PlayerId(2),
                    StandardStats,
                    new WorldPointMm(-20000 + index * 2000, 20000),
                    500);
            }

            return server;
        }

        private static void RunStandardScript(MatchServer server)
        {
            var playerOne = new PlayerId(1);
            var playerTwo = new PlayerId(2);
            var formation = new FormationSpec(0, 4000, CardinalFacing.North);

            // Destinations keep every paired duel within the 12 m range
            // (front row 2000 mm apart, back row 10000 mm apart).
            EnqueueMove(
                server, playerOne, 1, 1,
                Entities(1, 2, 3), new WorldPointMm(0, -3000), formation);
            EnqueueMove(
                server, playerTwo, 1, 1,
                Entities(4, 5, 6), new WorldPointMm(0, 3000), formation);

            // Longest march is ~27600 mm at 500 mm/tick, so 60 ticks guarantees
            // every unit has snapped to its formation slot before attacks begin.
            server.ExecuteTicks(60);

            EnqueueAttack(server, playerOne, 2, 61, Entities(1), new EntityId(4));
            EnqueueAttack(server, playerOne, 3, 61, Entities(2), new EntityId(5));
            EnqueueAttack(server, playerOne, 4, 61, Entities(3), new EntityId(6));
            EnqueueAttack(server, playerTwo, 2, 63, Entities(4), new EntityId(1));
            EnqueueAttack(server, playerTwo, 3, 63, Entities(5), new EntityId(2));
            EnqueueAttack(server, playerTwo, 4, 63, Entities(6), new EntityId(3));

            server.ExecuteTicks(300);
        }

        /// <summary>
        /// A second owner keeps the battle outcome in progress, which lets the
        /// tests observe pure movement without the match terminating early.
        /// </summary>
        private static void SpawnPassiveOpponent(MatchServer server) =>
            server.SpawnUnit(
                new PlayerId(2),
                StandardStats,
                new WorldPointMm(100_000, 0),
                500);

        private static void EnqueueMove(
            MatchServer server,
            PlayerId player,
            uint sequence,
            ulong requestedTick,
            EntityId[] units,
            WorldPointMm destination,
            FormationSpec formation)
        {
            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(player, sequence, requestedTick, GameCommandType.Move),
                    units,
                    destination,
                    formation),
                Is.EqualTo(MatchCommandRejection.None));
        }

        private static void EnqueueAttack(
            MatchServer server,
            PlayerId player,
            uint sequence,
            ulong requestedTick,
            EntityId[] attackers,
            EntityId target)
        {
            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(player, sequence, requestedTick, GameCommandType.Attack),
                    attackers,
                    target),
                Is.EqualTo(MatchCommandRejection.None));
        }

        private static EntityId[] Entities(params ulong[] values)
        {
            var entities = new EntityId[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                entities[index] = new EntityId(values[index]);
            }

            return entities;
        }

        [Test]
        public void TickOnce_UnderLoad_IsAllocationFree()
        {
            var server = new MatchServer();
            var p1 = new PlayerId(1);
            var p2 = new PlayerId(2);

            var stats = new CombatStats(
                maximumHealth: 10000,
                damage: 10,
                rangeMm: 5000,
                cooldownTicks: 2);

            for (var i = 0; i < 100; i++)
            {
                var u1 = server.SpawnUnit(p1, stats, new WorldPointMm(1000 * i, 0), 500, autoAcquire: true);
                server.SpawnUnit(p2, stats, new WorldPointMm(1000 * i, 2000), 500, autoAcquire: true);
                server.TryEnqueueMove(
                    new CommandHeader(p1, (uint)(i + 1), 1, GameCommandType.Move),
                    new[] { u1 },
                    new WorldPointMm(1000 * i, 10000),
                    new FormationSpec(0, 1000, CardinalFacing.North));
            }

            for (var tick = 0; tick < 10; tick++)
            {
                server.TickOnce();
            }

            Assert.That(() => server.TickOnce(), UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory());
        }

        /// <summary>
        /// Phase 1.0, P1-4: the tick that *carries* scheduled commands is the one on
        /// the hot path, and it used to build a fresh <c>Comparison</c> from the method
        /// group for its sort. The command batches are issued on three consecutive
        /// ticks so the sort is already warm when the third one is measured.
        /// </summary>
        [Test]
        public void TickOnce_ExecutingScheduledCommands_IsAllocationFree()
        {
            var server = new MatchServer();
            var p1 = new PlayerId(1);
            var p2 = new PlayerId(2);
            var unitA = server.SpawnUnit(p1, StandardStats, new WorldPointMm(0, 0), 500);
            var unitB = server.SpawnUnit(p2, StandardStats, new WorldPointMm(100_000, 0), 500);

            for (var round = 0; round < 3; round++)
            {
                var requestedTick = (ulong)(round + 1);
                Assert.That(
                    server.TryEnqueueStop(
                        new CommandHeader(p1, (uint)(round * 2 + 1), requestedTick, GameCommandType.Stop),
                        new[] { unitA }),
                    Is.EqualTo(MatchCommandRejection.None));
                Assert.That(
                    server.TryEnqueueStop(
                        new CommandHeader(p2, (uint)(round * 2 + 2), requestedTick, GameCommandType.Stop),
                        new[] { unitB }),
                    Is.EqualTo(MatchCommandRejection.None));

                if (round < 2)
                {
                    server.TickOnce();
                    continue;
                }

                Assert.That(
                    () => server.TickOnce(),
                    UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory());
            }
        }

        /// <summary>
        /// Phase 1.0, P1-3: the tick that <em>executes</em> a Move order recomputed the
        /// formation slots TryEnqueueMove had already built and bounds-checked, so
        /// TickOnce allocated a FormationSlot[] per executed command. Exactly one command
        /// is queued per tick on purpose: a single-element batch never enters
        /// List&lt;T&gt;.Sort's comparison path, so the recomputation is the only
        /// allocation this window can produce.
        /// </summary>
        [Test]
        public void TickOnce_ExecutingScheduledMoveCommand_IsAllocationFree()
        {
            var server = new MatchServer();
            var player = new PlayerId(1);
            var first = server.SpawnUnit(
                player, StandardStats, new WorldPointMm(0, 0), 500);
            var second = server.SpawnUnit(
                player, StandardStats, new WorldPointMm(1000, 0), 500);
            SpawnPassiveOpponent(server);

            for (var round = 0; round < 3; round++)
            {
                EnqueueMove(
                    server,
                    player,
                    (uint)(round + 1),
                    (ulong)(round + 1),
                    Entities(1, 2),
                    new WorldPointMm(10000, 0),
                    new FormationSpec(2, 2000, CardinalFacing.North));

                if (round < 2)
                {
                    server.TickOnce();
                    continue;
                }

                Assert.That(
                    () => server.TickOnce(),
                    UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory());
            }

            // TickOnce returns early on a terminal outcome or an empty roster, which would
            // make any allocation claim vacuously true, so prove the measured tick did work.
            Assert.That(server.TryGetUnit(first, out var firstUnit), Is.True);
            Assert.That(server.TryGetUnit(second, out var secondUnit), Is.True);
            Assert.That(
                firstUnit.HasMoveTarget,
                Is.True,
                "the measured tick must have executed a Move command");
            Assert.That(
                secondUnit.HasMoveTarget,
                Is.True,
                "the measured tick must have executed a Move command");
            Assert.That(firstUnit.Position, Is.EqualTo(new WorldPointMm(1500, 0)));
            Assert.That(secondUnit.Position, Is.EqualTo(new WorldPointMm(2500, 0)));

            // Carrying the slots turns the entity-to-slot pairing into an invariant of the
            // stored command rather than a property of a recompute, so pin it: in a 2-column
            // line facing north the lowest entity id takes the -1000 mm lateral slot.
            server.ExecuteTicks(20);
            Assert.That(server.TryGetUnit(first, out firstUnit), Is.True);
            Assert.That(firstUnit.Position, Is.EqualTo(new WorldPointMm(9000, 0)));
            Assert.That(server.TryGetUnit(second, out secondUnit), Is.True);
            Assert.That(secondUnit.Position, Is.EqualTo(new WorldPointMm(11000, 0)));
        }

        /// <summary>
        /// Phase 1.0, P1-5: the selection cap is enforced before the payload is copied
        /// and sorted. Unit 1 exists, so a server that walks the entities instead of
        /// checking the length first answers UnknownEntity for unit 2 and the caller
        /// never learns that the order was simply too big.
        /// </summary>
        [Test]
        public void TryEnqueueStop_WhenEntitiesExceedMaxSelectedEntities_ReturnsInvalidPayload()
        {
            var server = new MatchServer();
            var player = new PlayerId(1);
            server.SpawnUnit(player, StandardStats, new WorldPointMm(0, 0), 500);

            var tooManyEntities = new EntityId[SimulationConstants.MaxSelectedEntities + 1];
            for (var i = 0; i < tooManyEntities.Length; i++)
            {
                tooManyEntities[i] = new EntityId((ulong)(i + 1));
            }

            var rejection = server.TryEnqueueStop(
                new CommandHeader(player, 1, 10, GameCommandType.Stop),
                tooManyEntities);

            Assert.That(rejection, Is.EqualTo(MatchCommandRejection.InvalidPayload));
        }

        // Phase 1.0 sprint 2: SpawnUnitWithEntity used to check only the entity id, so
        // owner and stats were caught late inside CombatantState - after the entity-id
        // high-water mark had already moved - while an out-of-bounds position or a
        // non-positive speed reached the world at all. Each case is asserted against both
        // spawn entries so the contract cannot drift apart again, and against the id
        // counter so a rejected spawn provably leaves the server untouched. One case per
        // test: NUnit reports only the first failure of a method, so a single combined
        // test would witness one guard and imply it covered four.

        [Test]
        public void SpawnUnitWithEntity_InvalidOwner_ThrowsLikeSpawnUnit()
        {
            var server = new MatchServer();
            var invalidOwner = new PlayerId(0);

            Assert.Throws<ArgumentException>(() =>
                server.SpawnUnitWithEntity(
                    new EntityId(9),
                    invalidOwner,
                    StandardStats,
                    new WorldPointMm(0, 0),
                    500));

            Assert.Throws<ArgumentException>(() =>
                server.SpawnUnit(
                    invalidOwner,
                    StandardStats,
                    new WorldPointMm(0, 0),
                    500));

            Assert.That(server.UnitCount, Is.Zero);
            Assert.That(
                server.SpawnUnit(new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500),
                Is.EqualTo(new EntityId(1)),
                "a rejected SpawnUnitWithEntity must not consume the next entity id");
        }

        [Test]
        public void SpawnUnitWithEntity_InvalidStats_ThrowsLikeSpawnUnit()
        {
            var server = new MatchServer();

            // CombatStats' own constructor refuses zeros, so an invalid instance can only
            // arrive as default - which is exactly what a callers' uninitialised field gives.
            var invalidStats = default(CombatStats);

            Assert.Throws<ArgumentException>(() =>
                server.SpawnUnitWithEntity(
                    new EntityId(9),
                    new PlayerId(1),
                    invalidStats,
                    new WorldPointMm(0, 0),
                    500));

            Assert.Throws<ArgumentException>(() =>
                server.SpawnUnit(
                    new PlayerId(1),
                    invalidStats,
                    new WorldPointMm(0, 0),
                    500));

            Assert.That(server.UnitCount, Is.Zero);
            Assert.That(
                server.SpawnUnit(new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500),
                Is.EqualTo(new EntityId(1)),
                "a rejected SpawnUnitWithEntity must not consume the next entity id");
        }

        [Test]
        public void SpawnUnitWithEntity_OutOfBoundsPosition_ThrowsLikeSpawnUnit()
        {
            var server = new MatchServer();
            var outsideWorld = new WorldPointMm(
                SimulationConstants.MaxWorldCoordinateMm + 1, 0);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                server.SpawnUnitWithEntity(
                    new EntityId(9),
                    new PlayerId(1),
                    StandardStats,
                    outsideWorld,
                    500));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                server.SpawnUnit(
                    new PlayerId(1),
                    StandardStats,
                    outsideWorld,
                    500));

            Assert.That(server.UnitCount, Is.Zero);
            Assert.That(
                server.SpawnUnit(new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500),
                Is.EqualTo(new EntityId(1)),
                "a rejected SpawnUnitWithEntity must not consume the next entity id");
        }

        [Test]
        public void SpawnUnitWithEntity_NonPositiveSpeed_ThrowsLikeSpawnUnit()
        {
            var server = new MatchServer();

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                server.SpawnUnitWithEntity(
                    new EntityId(9),
                    new PlayerId(1),
                    StandardStats,
                    new WorldPointMm(0, 0),
                    0));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                server.SpawnUnitWithEntity(
                    new EntityId(9),
                    new PlayerId(1),
                    StandardStats,
                    new WorldPointMm(0, 0),
                    -1));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                server.SpawnUnit(
                    new PlayerId(1),
                    StandardStats,
                    new WorldPointMm(0, 0),
                    0));

            Assert.That(server.UnitCount, Is.Zero);
            Assert.That(
                server.SpawnUnit(new PlayerId(1), StandardStats, new WorldPointMm(0, 0), 500),
                Is.EqualTo(new EntityId(1)),
                "a rejected SpawnUnitWithEntity must not consume the next entity id");
        }
    }
}