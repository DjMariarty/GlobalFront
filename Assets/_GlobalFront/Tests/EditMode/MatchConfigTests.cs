#pragma warning disable CS0618
using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Tests for <see cref="MatchConfig"/>, <see cref="UnitSpawnSpec"/>,
    /// and <see cref="MatchServer.InitializeMatch"/>. Validates the
    /// server-owned match initialization pipeline.
    /// </summary>
    [TestFixture]
    public sealed class MatchConfigTests
    {
        private static readonly CombatStats StandardStats =
            new CombatStats(100, 25, 7000, 10);

        #region UnitSpawnSpec Validation

        [Test]
        public void UnitSpawnSpec_InvalidOwner_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new UnitSpawnSpec(
                    new PlayerId(0),
                    new WorldPointMm(0, 0),
                    350,
                    false));
        }

        [Test]
        public void UnitSpawnSpec_OutOfBoundsPosition_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UnitSpawnSpec(
                    new PlayerId(1),
                    new WorldPointMm(int.MaxValue, 0),
                    350,
                    false));
        }

        [Test]
        public void UnitSpawnSpec_ZeroSpeed_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UnitSpawnSpec(
                    new PlayerId(1),
                    new WorldPointMm(0, 0),
                    0,
                    false));
        }

        [Test]
        public void UnitSpawnSpec_NegativeSpeed_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UnitSpawnSpec(
                    new PlayerId(1),
                    new WorldPointMm(0, 0),
                    -1,
                    false));
        }

        [Test]
        public void UnitSpawnSpec_ValidConstruction()
        {
            var spec = new UnitSpawnSpec(
                new PlayerId(1),
                new WorldPointMm(1000, 2000),
                350,
                true);

            Assert.That(spec.Owner, Is.EqualTo(new PlayerId(1)));
            Assert.That(spec.Position, Is.EqualTo(new WorldPointMm(1000, 2000)));
            Assert.That(spec.SpeedMmPerTick, Is.EqualTo(350));
            Assert.That(spec.AutoAcquireEnemies, Is.True);
        }

        [Test]
        public void UnitSpawnSpec_Equality()
        {
            var a = new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false);
            var b = new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false);
            var c = new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(0, 0), 350, false);

            Assert.That(a, Is.EqualTo(b));
            Assert.That(a, Is.Not.EqualTo(c));
        }

        #endregion

        #region MatchConfig Validation

        [Test]
        public void MatchConfig_NullUnits_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MatchConfig(StandardStats, null));
        }

        [Test]
        public void MatchConfig_InvalidStats_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new MatchConfig(default, Array.Empty<UnitSpawnSpec>()));
        }

        [Test]
        public void MatchConfig_EmptyUnits_Valid()
        {
            var config = new MatchConfig(StandardStats, Array.Empty<UnitSpawnSpec>());

            Assert.That(config.UnitCount, Is.EqualTo(0));
            Assert.That(config.UnitStats, Is.EqualTo(StandardStats));
        }

        [Test]
        public void MatchConfig_WithUnits()
        {
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(5000, 5000), 350, true),
            };

            var config = new MatchConfig(StandardStats, specs);

            Assert.That(config.UnitCount, Is.EqualTo(2));
            Assert.That(config.Units[0], Is.EqualTo(specs[0]));
            Assert.That(config.Units[1], Is.EqualTo(specs[1]));
        }

        #endregion

        #region MatchServer.InitializeMatch

        [Test]
        public void InitializeMatch_CreatesUnitsWithSequentialEntityIds()
        {
            var server = new MatchServer();
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(1000, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(5000, 0), 350, true),
            };
            var config = new MatchConfig(StandardStats, specs);

            var entityIds = server.InitializeMatch(config);

            Assert.That(entityIds.Length, Is.EqualTo(3));
            Assert.That(entityIds[0], Is.EqualTo(new EntityId(1)));
            Assert.That(entityIds[1], Is.EqualTo(new EntityId(2)));
            Assert.That(entityIds[2], Is.EqualTo(new EntityId(3)));
            Assert.That(server.UnitCount, Is.EqualTo(3));
        }

        [Test]
        public void InitializeMatch_ServerAssignsEntityIds_Deterministically()
        {
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(5000, 0), 350, true),
            };
            var config = new MatchConfig(StandardStats, specs);

            var server1 = new MatchServer();
            var ids1 = server1.InitializeMatch(config);

            var server2 = new MatchServer();
            var ids2 = server2.InitializeMatch(config);

            Assert.That(ids1, Is.EqualTo(ids2));
        }

        [Test]
        public void InitializeMatch_NullConfig_Throws()
        {
            var server = new MatchServer();
            Assert.Throws<ArgumentNullException>(() => server.InitializeMatch(null));
        }

        [Test]
        public void InitializeMatch_AlreadyInitialized_Throws()
        {
            var server = new MatchServer();
            var config = new MatchConfig(StandardStats, new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
            });

            server.InitializeMatch(config);

            Assert.Throws<InvalidOperationException>(() => server.InitializeMatch(config));
        }

        [Test]
        public void InitializeMatch_AppliesConfigStatsToAllUnits()
        {
            var server = new MatchServer();
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(5000, 0), 350, false),
            };
            var config = new MatchConfig(StandardStats, specs);

            var entityIds = server.InitializeMatch(config);

            Assert.That(server.TryGetUnit(entityIds[0], out var snapshot1), Is.True);
            Assert.That(server.TryGetUnit(entityIds[1], out var snapshot2), Is.True);
            Assert.That(snapshot1.CurrentHealth, Is.EqualTo(StandardStats.MaximumHealth));
            Assert.That(snapshot2.CurrentHealth, Is.EqualTo(StandardStats.MaximumHealth));
        }

        [Test]
        public void InitializeMatch_AppliesOwnerAndPosition()
        {
            var server = new MatchServer();
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(100, 200), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(3000, 4000), 350, true),
            };
            var config = new MatchConfig(StandardStats, specs);

            var entityIds = server.InitializeMatch(config);

            Assert.That(server.TryGetUnit(entityIds[0], out var snapshot1), Is.True);
            Assert.That(snapshot1.Owner, Is.EqualTo(new PlayerId(1)));
            Assert.That(snapshot1.Position, Is.EqualTo(new WorldPointMm(100, 200)));
            Assert.That(snapshot1.AutoAcquireEnemies, Is.False);

            Assert.That(server.TryGetUnit(entityIds[1], out var snapshot2), Is.True);
            Assert.That(snapshot2.Owner, Is.EqualTo(new PlayerId(2)));
            Assert.That(snapshot2.Position, Is.EqualTo(new WorldPointMm(3000, 4000)));
            Assert.That(snapshot2.AutoAcquireEnemies, Is.True);
        }

        [Test]
        public void InitializeMatch_ThenMoveCommand_Works()
        {
            var server = new MatchServer();
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(50000, 0), 350, false),
            };
            var config = new MatchConfig(StandardStats, specs);

            var entityIds = server.InitializeMatch(config);

            var rejection = server.TryEnqueueMove(
                new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                new[] { entityIds[0] },
                new WorldPointMm(5000, 0),
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(rejection, Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();

            Assert.That(server.TryGetUnit(entityIds[0], out var snapshot), Is.True);
            Assert.That(snapshot.Position.X, Is.GreaterThan(0));
        }

        [Test]
        public void InitializeMatch_ThenAttackCommand_Works()
        {
            var server = new MatchServer();
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(3000, 0), 350, true),
            };
            var config = new MatchConfig(StandardStats, specs);

            var entityIds = server.InitializeMatch(config);

            var rejection = server.TryEnqueueAttack(
                new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Attack),
                new[] { entityIds[0] },
                entityIds[1]);

            Assert.That(rejection, Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();

            Assert.That(server.TryGetUnit(entityIds[0], out var snapshot), Is.True);
            Assert.That(snapshot.AttackTarget, Is.EqualTo(entityIds[1]));
        }

        [Test]
        public void InitializeMatch_IdenticalConfigs_ProduceIdenticalSnapshots()
        {
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(-20000, -20000), 500, false),
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(-18000, -20000), 500, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(20000, 20000), 500, true),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(22000, 20000), 500, true),
            };
            var config = new MatchConfig(StandardStats, specs);

            var server1 = new MatchServer();
            var ids1 = server1.InitializeMatch(config);
            server1.ExecuteTicks(20);

            var server2 = new MatchServer();
            var ids2 = server2.InitializeMatch(config);
            server2.ExecuteTicks(20);

            Assert.That(ids1, Is.EqualTo(ids2));

            var snapshots1 = server1.GetAllSnapshots();
            var snapshots2 = server2.GetAllSnapshots();

            Assert.That(snapshots1.Length, Is.EqualTo(snapshots2.Length));
            for (var index = 0; index < snapshots1.Length; index++)
            {
                Assert.That(snapshots1[index], Is.EqualTo(snapshots2[index]),
                    $"Entity {snapshots1[index].Entity} diverged between server instances.");
            }
        }

        [Test]
        public void InitializeMatch_SnapshotsInCanonicalEntityIdOrder()
        {
            var server = new MatchServer();
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(5000, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(10000, 0), 350, false),
            };
            var config = new MatchConfig(StandardStats, specs);

            server.InitializeMatch(config);
            var snapshots = server.GetAllSnapshots();

            Assert.That(snapshots.Length, Is.EqualTo(3));
            Assert.That(snapshots[0].Entity.Value, Is.LessThan(snapshots[1].Entity.Value));
            Assert.That(snapshots[1].Entity.Value, Is.LessThan(snapshots[2].Entity.Value));
        }

        [Test]
        public void InitializeMatch_ForeignOwnerCommand_IsRejected()
        {
            var server = new MatchServer();
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(50000, 0), 350, false),
            };
            var config = new MatchConfig(StandardStats, specs);

            var entityIds = server.InitializeMatch(config);

            // Player 2 tries to move Player 1's unit
            var rejection = server.TryEnqueueMove(
                new CommandHeader(new PlayerId(2), 1, 1, GameCommandType.Move),
                new[] { entityIds[0] },
                new WorldPointMm(1000, 0),
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(rejection, Is.EqualTo(MatchCommandRejection.NotEntityOwner));
        }

        #endregion

        #region LocalMatchHost.InitializeMatch

        [Test]
        public void LocalMatchHost_InitializeMatch_DelegatesToServer()
        {
            // Fully qualified on purpose: GlobalFront.Tests.EditMode.Client.* is a
            // real test namespace since Phase 2.6 step 2.6.3, so the relative
            // "Client.LocalMatchHost" would resolve to it instead of the runtime.
            var host = new GlobalFront.Client.LocalMatchHost();
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(5000, 0), 350, true),
            };
            var config = new MatchConfig(StandardStats, specs);

            var entityIds = host.InitializeMatch(config);

            Assert.That(entityIds.Length, Is.EqualTo(2));
            Assert.That(host.UnitCount, Is.EqualTo(2));
            Assert.That(host.TryGetUnit(entityIds[0], out _), Is.True);
            Assert.That(host.TryGetUnit(entityIds[1], out _), Is.True);
        }

        [Test]
        public void LocalMatchHost_InitializeMatch_ThenTick_Works()
        {
            var host = new GlobalFront.Client.LocalMatchHost();
            var specs = new[]
            {
                new UnitSpawnSpec(new PlayerId(1), new WorldPointMm(0, 0), 500, false),
                new UnitSpawnSpec(new PlayerId(2), new WorldPointMm(50000, 0), 500, false),
            };
            var config = new MatchConfig(StandardStats, specs);

            var entityIds = host.InitializeMatch(config);

            host.TryEnqueueMove(
                new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                new[] { entityIds[0] },
                new WorldPointMm(5000, 0),
                new FormationSpec(1, 2000, CardinalFacing.North));

            host.TickOnce();

            Assert.That(host.CurrentTick, Is.EqualTo(1ul));
            Assert.That(host.TryGetUnit(entityIds[0], out var snapshot), Is.True);
            Assert.That(snapshot.Position.X, Is.EqualTo(500));
        }

        #endregion
    }
}