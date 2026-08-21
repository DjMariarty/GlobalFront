using GlobalFront.Client;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Pure facade tests for <see cref="LocalMatchHost"/>. These exercise the
    /// host without any Unity scene: command forwarding, server ticks,
    /// snapshot production, rejections, terminal outcome and tick-by-tick
    /// determinism parity. They mirror the scenarios covered by
    /// <see cref="MatchServerTests"/> but go through the hosting layer that
    /// the client presentation consumes.
    /// </summary>
    public sealed class LocalMatchHostTests
    {
        private static readonly CombatStats StandardStats =
            new CombatStats(100, 10, 12000, 2);

        [Test]
        public void MoveCommand_ForwardedToHostAndTickProducesSnapshot()
        {
            var host = new LocalMatchHost();
            host.SpawnUnitWithEntity(
                new EntityId(1), new PlayerId(1), StandardStats,
                new WorldPointMm(0, 0), 500);
            SpawnPassiveOpponent(host);

            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                    Entities(1),
                    new WorldPointMm(10000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            host.TickOnce();

            Assert.That(host.CurrentTick, Is.EqualTo(1ul));
            Assert.That(host.TryGetUnit(new EntityId(1), out var snapshot), Is.True);
            Assert.That(snapshot.Position, Is.EqualTo(new WorldPointMm(500, 0)));
        }

        [Test]
        public void AttackCommand_ForwardedToHostAndResolvesCombatToTerminal()
        {
            var host = new LocalMatchHost();
            host.SpawnUnitWithEntity(
                new EntityId(1), new PlayerId(1), StandardStats,
                new WorldPointMm(0, 0), 500);
            host.SpawnUnitWithEntity(
                new EntityId(2), new PlayerId(2), StandardStats,
                new WorldPointMm(5000, 0), 500);

            Assert.That(
                host.TryEnqueueAttack(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Attack),
                    Entities(1),
                    new EntityId(2)),
                Is.EqualTo(MatchCommandRejection.None));

            RunTicks(host, 60);

            Assert.That(host.Outcome.IsTerminal, Is.True);
            Assert.That(host.Outcome.Kind, Is.EqualTo(BattleOutcomeKind.Victory));
            Assert.That(host.Outcome.Winner, Is.EqualTo(new PlayerId(1)));
            Assert.That(host.TryGetUnit(new EntityId(2), out var target), Is.True);
            Assert.That(target.CurrentHealth, Is.Zero);
        }

        [Test]
        public void StopCommand_ForwardedToHostAndClearsMoveTarget()
        {
            var host = new LocalMatchHost();
            host.SpawnUnitWithEntity(
                new EntityId(1), new PlayerId(1), StandardStats,
                new WorldPointMm(0, 0), 500);
            SpawnPassiveOpponent(host);

            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                    Entities(1),
                    new WorldPointMm(10000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                host.TryEnqueueStop(
                    new CommandHeader(new PlayerId(1), 2, 3, GameCommandType.Stop),
                    Entities(1)),
                Is.EqualTo(MatchCommandRejection.None));

            RunTicks(host, 10);

            Assert.That(host.TryGetUnit(new EntityId(1), out var snapshot), Is.True);
            Assert.That(snapshot.HasMoveTarget, Is.False);
            Assert.That(snapshot.Position, Is.EqualTo(new WorldPointMm(1000, 0)));
        }

        [Test]
        public void ForeignOwnerCommand_IsRejected()
        {
            var host = new LocalMatchHost();
            host.SpawnUnitWithEntity(
                new EntityId(1), new PlayerId(1), StandardStats,
                new WorldPointMm(0, 0), 500);
            host.SpawnUnitWithEntity(
                new EntityId(2), new PlayerId(2), StandardStats,
                new WorldPointMm(10000, 0), 500);

            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(new PlayerId(2), 1, 1, GameCommandType.Move),
                    new[] { new EntityId(1) },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.NotEntityOwner));
        }

        [Test]
        public void DuplicateSequence_IsRejected()
        {
            var host = new LocalMatchHost();
            host.SpawnUnitWithEntity(
                new EntityId(1), new PlayerId(1), StandardStats,
                new WorldPointMm(0, 0), 500);
            SpawnPassiveOpponent(host);

            var destination = new WorldPointMm(1000, 0);
            var formation = new FormationSpec(1, 2000, CardinalFacing.North);

            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 5, 1, GameCommandType.Move),
                    Entities(1), destination, formation),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(new PlayerId(1), 5, 2, GameCommandType.Move),
                    Entities(1), destination, formation),
                Is.EqualTo(MatchCommandRejection.DuplicateSequence));
        }

        [Test]
        public void TerminalOutcome_StopsSimulatingFurther()
        {
            var host = new LocalMatchHost();
            host.SpawnUnitWithEntity(
                new EntityId(1), new PlayerId(1), StandardStats,
                new WorldPointMm(0, 0), 500);
            host.SpawnUnitWithEntity(
                new EntityId(2), new PlayerId(2), StandardStats,
                new WorldPointMm(5000, 0), 500);

            Assert.That(
                host.TryEnqueueAttack(
                    new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Attack),
                    Entities(1), new EntityId(2)),
                Is.EqualTo(MatchCommandRejection.None));

            RunTicks(host, 60);

            Assert.That(host.Outcome.IsTerminal, Is.True);
            var terminalTick = host.CurrentTick;
            var terminalSnapshots = host.GetAllSnapshots();

            host.TickOnce();
            host.TickOnce();

            Assert.That(host.CurrentTick, Is.EqualTo(terminalTick + 2));
            Assert.That(host.GetAllSnapshots(), Is.EqualTo(terminalSnapshots));
        }

        [Test]
        public void GetAllSnapshots_ReturnedInEntityIdOrder()
        {
            var host = new LocalMatchHost();
            host.SpawnUnitWithEntity(
                new EntityId(3), new PlayerId(2), StandardStats,
                new WorldPointMm(9000, 0), 500);
            host.SpawnUnitWithEntity(
                new EntityId(1), new PlayerId(1), StandardStats,
                new WorldPointMm(0, 0), 500);
            host.SpawnUnitWithEntity(
                new EntityId(2), new PlayerId(2), StandardStats,
                new WorldPointMm(5000, 0), 500);

            var snapshots = host.GetAllSnapshots();

            Assert.That(snapshots.Length, Is.EqualTo(3));
            Assert.That(snapshots[0].Entity, Is.EqualTo(new EntityId(1)));
            Assert.That(snapshots[1].Entity, Is.EqualTo(new EntityId(2)));
            Assert.That(snapshots[2].Entity, Is.EqualTo(new EntityId(3)));
        }

        [Test]
        public void IdenticalSetupAndCommands_ProduceIdenticalSnapshots()
        {
            var first = BuildStandardHost();
            var second = BuildStandardHost();

            RunStandardScript(first);
            RunStandardScript(second);

            Assert.That(first.CurrentTick, Is.EqualTo(second.CurrentTick));
            Assert.That(first.Outcome, Is.EqualTo(second.Outcome));
            Assert.That(first.Outcome.IsTerminal, Is.True);

            var firstSnapshots = first.GetAllSnapshots();
            var secondSnapshots = second.GetAllSnapshots();
            Assert.That(firstSnapshots.Length, Is.EqualTo(secondSnapshots.Length));

            for (var index = 0; index < firstSnapshots.Length; index++)
            {
                Assert.That(
                    firstSnapshots[index],
                    Is.EqualTo(secondSnapshots[index]),
                    $"Entity {firstSnapshots[index].Entity} diverged between host instances.");
            }
        }

        [Test]
        public void CommandQueue_ForwardsPendingCommandsToHost()
        {
            // Session-aware host setup (Phase 2.4, ADR-008): the same unit
            // layout as the raw spawn path, with a connected local session
            // bound to PlayerId 1 so the forwarded submission passes the
            // session gate.
            var host = new LocalMatchHost();
            var match = host.CreateSessionMatch(2);
            var localSession = host.CreateSession(host.CreateConnectionHandle());
            var enemySession = host.CreateSession(host.CreateConnectionHandle());
            Assert.That(
                host.TryJoinMatch(localSession, match, out var localPlayer),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                host.TryJoinMatch(enemySession, match, out var enemyPlayer),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                host.TryStartSessionMatch(
                    match,
                    new MatchConfig(
                        StandardStats,
                        new[]
                        {
                            new UnitSpawnSpec(localPlayer, new WorldPointMm(0, 0), 500, false),
                            new UnitSpawnSpec(enemyPlayer, new WorldPointMm(100_000, 0), 500, false)
                        }),
                    out _),
                Is.True);

            var registry = new UnitRegistry();
            var queue = new PrototypeCommandQueue(
                registry, localPlayer, formationSpacingMm: 2000);

            queue.QueueMove(
                new WorldPointMm(10000, 0),
                new[] { new EntityId(1) },
                requestedTick: 1);

            Assert.That(queue.PendingCount, Is.EqualTo(1));

            queue.ForwardPendingToHost(1, host, localSession);

            Assert.That(queue.PendingCount, Is.EqualTo(0));
            host.TickOnce();

            Assert.That(host.TryGetUnit(new EntityId(1), out var snapshot), Is.True);
            Assert.That(snapshot.Position, Is.EqualTo(new WorldPointMm(500, 0)));
        }

        private static void RunTicks(LocalMatchHost host, int count)
        {
            for (var index = 0; index < count; index++)
            {
                host.TickOnce();
            }
        }

        private static LocalMatchHost BuildStandardHost()
        {
            var host = new LocalMatchHost();

            for (var index = 0; index < 3; index++)
            {
                host.SpawnUnitWithEntity(
                    new EntityId((ulong)(index + 1)),
                    new PlayerId(1),
                    StandardStats,
                    new WorldPointMm(-20000 + index * 2000, -20000),
                    500);
            }

            for (var index = 0; index < 3; index++)
            {
                host.SpawnUnitWithEntity(
                    new EntityId((ulong)(index + 4)),
                    new PlayerId(2),
                    StandardStats,
                    new WorldPointMm(-20000 + index * 2000, 20000),
                    500);
            }

            return host;
        }

        private static void RunStandardScript(LocalMatchHost host)
        {
            var playerOne = new PlayerId(1);
            var playerTwo = new PlayerId(2);
            var formation = new FormationSpec(0, 4000, CardinalFacing.North);

            AssertMove(host, playerOne, 1, 1,
                Entities(1, 2, 3), new WorldPointMm(0, -3000), formation);
            AssertMove(host, playerTwo, 1, 1,
                Entities(4, 5, 6), new WorldPointMm(0, 3000), formation);

            RunTicks(host, 60);

            AssertAttack(host, playerOne, 2, 61, Entities(1), new EntityId(4));
            AssertAttack(host, playerOne, 3, 61, Entities(2), new EntityId(5));
            AssertAttack(host, playerOne, 4, 61, Entities(3), new EntityId(6));
            AssertAttack(host, playerTwo, 2, 63, Entities(4), new EntityId(1));
            AssertAttack(host, playerTwo, 3, 63, Entities(5), new EntityId(2));
            AssertAttack(host, playerTwo, 4, 63, Entities(6), new EntityId(3));

            RunTicks(host, 300);
        }

        private static void AssertMove(
            LocalMatchHost host,
            PlayerId player,
            uint sequence,
            ulong requestedTick,
            EntityId[] units,
            WorldPointMm destination,
            FormationSpec formation)
        {
            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(player, sequence, requestedTick, GameCommandType.Move),
                    units, destination, formation),
                Is.EqualTo(MatchCommandRejection.None));
        }

        private static void AssertAttack(
            LocalMatchHost host,
            PlayerId player,
            uint sequence,
            ulong requestedTick,
            EntityId[] attackers,
            EntityId target)
        {
            Assert.That(
                host.TryEnqueueAttack(
                    new CommandHeader(player, sequence, requestedTick, GameCommandType.Attack),
                    attackers, target),
                Is.EqualTo(MatchCommandRejection.None));
        }

        /// <summary>
        /// A second owner keeps the battle outcome in progress, which lets the
        /// tests observe pure movement without the match terminating early.
        /// </summary>
        private static void SpawnPassiveOpponent(LocalMatchHost host) =>
            host.SpawnUnitWithEntity(
                new EntityId(2),
                new PlayerId(2),
                StandardStats,
                new WorldPointMm(100_000, 0),
                500);

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
