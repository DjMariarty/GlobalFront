using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using NUnit.Framework;

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
    }
}