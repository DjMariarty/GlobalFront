using System.Collections;
using System.Reflection;
using GlobalFront.Client;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.PlayMode
{
    /// <summary>
    /// PlayMode validation of the full authoritative runtime pipeline:
    /// Input -> Command -> LocalMatchHost -> MatchServer -> Server Tick ->
    /// ServerUnitSnapshot -> Client Presentation.
    ///
    /// These tests boot the real <c>SampleScene</c>, let
    /// <see cref="GlobalFrontRuntimeBootstrap"/> create the runtime root and
    /// drive the actual host-owned <see cref="TickDriver"/> /
    /// <see cref="PrototypeRtsController"/> loop (Phase 2.3, ADR-007).
    /// Commands are queued through
    /// the real <see cref="PrototypeCommandQueue"/>; no networking,
    /// serialization, prediction or reconciliation is involved.
    ///
    /// All tests share one battle process, so execution order matters and is
    /// fixed with <see cref="OrderAttribute"/>: the movement tests run first
    /// on an untouched unit, the attack test next, and the terminal-outcome
    /// battle runs last because a terminal outcome permanently stops the
    /// authoritative loop.
    /// </summary>
    [TestFixture]
    public sealed class LocalMatchHostPipelinePlayModeTests
    {
        private const string RuntimeRootName = "[GlobalFront Runtime]";
        private const string PrototypeWorldName = "[GlobalFront Prototype World]";
        private const int ExpectedUnitCount = 40;
        private const float BootstrapTimeoutSeconds = 20f;
        private const float TickTimeoutSeconds = 30f;

        /// <summary>
        /// Two-sided battles (enemy auto-acquire restored by the Phase 2.1
        /// ordering bugfix) run a mutual-attrition engagement that terminates
        /// in roughly two minutes of simulation time (measured terminal tick
        /// ≈ 2316 ≈ 116 s); the one-sided calibration (~15 s) no longer
        /// applies. Terminal outcome — not duration — is what this suite
        /// asserts.
        /// </summary>
        private const float BattleTimeoutSeconds = 240f;
        private const float EnemyCounterAttackTimeoutSeconds = 90f;

        private PrototypeRtsController _controller;

        [UnityTest]
        [Order(1)]
        public IEnumerator Bootstrap_CreatesRuntimeRootPrototypeWorldAndAuthoritativeHost()
        {
            yield return WaitForBootstrap();

            Assert.That(GameObject.Find(RuntimeRootName), Is.Not.Null,
                "GlobalFrontRuntimeBootstrap did not create the runtime root.");
            Assert.That(GameObject.Find(PrototypeWorldName), Is.Not.Null,
                "PrototypeWorldBootstrap did not create the prototype world.");
            Assert.That(_controller.HostActive, Is.True,
                "PrototypeRtsController did not initialize the LocalMatchHost.");
            Assert.That(_controller.HostUnitCount, Is.EqualTo(ExpectedUnitCount),
                "Both prototype armies (20 + 20 units) must be registered in the host.");
            Assert.That(_controller.Outcome.IsTerminal, Is.False,
                "The battle must start in progress.");
        }

        [UnityTest]
        [Order(2)]
        public IEnumerator ServerTicks_AdvanceHostAndSynchronizePresentationFromSnapshots()
        {
            yield return WaitForBootstrap();
            var host = _controller.Host;

            // Get the first unit from the host (server-assigned EntityId)
            var snapshots = host.GetAllSnapshots();
            Assert.That(snapshots.Length, Is.GreaterThan(0), "Host must have units after initialization.");
            var entity = snapshots[0].Entity;
            var startPosition = snapshots[0].Position;

            QueueMove(entity, new WorldPointMm(startPosition.X + 5000, startPosition.Z));

            var startTick = host.CurrentTick;
            yield return WaitForHostTick(startTick + 10);

            Assert.That(host.CurrentTick, Is.GreaterThanOrEqualTo(startTick + 10),
                "The authoritative host must keep ticking inside the real runtime loop.");
            Assert.That(host.TryGetUnit(entity, out var snapshot), Is.True);
            Assert.That(snapshot.Position, Is.Not.EqualTo(startPosition),
                "The authoritative server must move the unit.");

            var unit = GetPresentationUnit(entity);
            Assert.That(unit.CurrentPosition, Is.EqualTo(snapshot.Position),
                "Presentation must mirror the authoritative snapshot position.");
        }

        [UnityTest]
        [Order(3)]
        public IEnumerator MoveCommand_DrivesUnitToDestinationThroughAuthoritativePipeline()
        {
            yield return WaitForBootstrap();
            var host = _controller.Host;

            // Get the first unit from the host (server-assigned EntityId)
            var snapshots = host.GetAllSnapshots();
            Assert.That(snapshots.Length, Is.GreaterThan(0), "Host must have units after initialization.");
            var entity = snapshots[0].Entity;
            var initialPosition = snapshots[0].Position;
            var destination = new WorldPointMm(initialPosition.X + 5000, initialPosition.Z);

            QueueMove(entity, destination);

            yield return WaitForSnapshotPosition(entity, destination);

            Assert.That(host.TryGetUnit(entity, out var arrived), Is.True);
            Assert.That(arrived.Position, Is.EqualTo(destination),
                "The authoritative server must land the unit exactly on the destination.");
            Assert.That(arrived.HasMoveTarget, Is.False,
                "The move target must be cleared once the destination is reached.");

            var unit = GetPresentationUnit(entity);
            Assert.That(unit.CurrentPosition, Is.EqualTo(destination),
                "Presentation must end exactly on the authoritative destination.");
            Assert.That(unit.HasTarget, Is.False);
        }

        [UnityTest]
        [Order(4)]
        public IEnumerator AttackCommand_SetsAuthoritativeTargetAndDrivesPursuit()
        {
            yield return WaitForBootstrap();
            var host = _controller.Host;

            // Get units from the host (server-assigned EntityIds)
            // First 20 units are PlayerId(1), next 20 are PlayerId(2)
            var snapshots = host.GetAllSnapshots();
            Assert.That(snapshots.Length, Is.GreaterThanOrEqualTo(40), "Host must have 40 units after initialization.");

            var attackerEntity = snapshots[0].Entity;  // First unit (Player 1)
            var targetEntity = snapshots[20].Entity;   // First enemy unit (Player 2)

            Assert.That(host.TryGetUnit(attackerEntity, out var start), Is.True);
            Assert.That(host.TryGetUnit(targetEntity, out var target), Is.True);

            QueueAttack(attackerEntity, targetEntity);

            var startTick = host.CurrentTick;
            yield return WaitForHostTick(startTick + 5);

            Assert.That(host.TryGetUnit(attackerEntity, out var snapshot), Is.True);
            Assert.That(snapshot.AttackTarget, Is.EqualTo(targetEntity),
                "The authoritative server must hold the attack target.");

            var unit = GetPresentationUnit(attackerEntity);
            Assert.That(unit.AttackTarget, Is.EqualTo(targetEntity),
                "Presentation combat state must mirror the authoritative snapshot.");

            Assert.That(snapshot.Position, Is.Not.EqualTo(start.Position),
                "The attacker must pursue the target through the authoritative movement phase.");
            AssertMovedTowards(start.Position, snapshot.Position, target.Position);
        }

        [UnityTest]
        [Order(5)]
        public IEnumerator EnemyUnits_AutoAcquireAndAttackPlayer_ThroughAuthoritativePipeline()
        {
            // Regression guard for the Phase 2.1 (8178110) initialization
            // ordering bug: enemy units must carry authoritative
            // AutoAcquireEnemies=true at match start, so non-local units
            // detect, acquire and damage player units without any command
            // ever being issued on their behalf.
            yield return WaitForBootstrap();
            var host = _controller.Host;
            var localPlayer = _controller.LocalSession.Player;
            Assert.That(localPlayer.IsValid, Is.True);

            var initial = host.GetAllSnapshots();
            Assert.That(initial.Length, Is.GreaterThanOrEqualTo(ExpectedUnitCount));

            // Move a fresh local unit (index 1 is unused by the other
            // ordered tests) into the enemy army so enemy auto-acquire has
            // a target inside its acquisition range.
            var probeEntity = initial[1].Entity;
            Assert.That(initial[1].Owner, Is.EqualTo(localPlayer));
            QueueMove(probeEntity, new WorldPointMm(20000, 15000));

            // Phase 1: an enemy unit auto-acquires a local unit. No attack
            // command is queued for the enemy side anywhere in this test.
            var elapsed = 0f;
            var enemyEntity = default(CoreEntityId);
            var victimEntity = default(CoreEntityId);
            var victimHealthAtAcquire = 0;
            while (elapsed < EnemyCounterAttackTimeoutSeconds)
            {
                var current = host.GetAllSnapshots();
                for (var index = 0; index < current.Length; index++)
                {
                    var candidate = current[index];
                    if (candidate.Owner == localPlayer || !candidate.AttackTarget.IsValid)
                    {
                        continue;
                    }

                    if (host.TryGetUnit(candidate.AttackTarget, out var target) &&
                        target.Owner == localPlayer)
                    {
                        enemyEntity = candidate.Entity;
                        victimEntity = candidate.AttackTarget;
                        victimHealthAtAcquire = target.CurrentHealth;
                        break;
                    }
                }

                if (enemyEntity.IsValid)
                {
                    break;
                }

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.That(enemyEntity.IsValid, Is.True,
                $"No enemy unit auto-acquired a player unit within {EnemyCounterAttackTimeoutSeconds} seconds; " +
                "authoritative AutoAcquireEnemies for non-local units is likely false.");

            // Phase 2: the acquisition turns into actual combat — the
            // acquired local unit takes damage from enemy fire.
            elapsed = 0f;
            var damaged = false;
            while (elapsed < EnemyCounterAttackTimeoutSeconds)
            {
                if (host.TryGetUnit(victimEntity, out var victim) &&
                    victim.CurrentHealth < victimHealthAtAcquire)
                {
                    damaged = true;
                    break;
                }

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.That(damaged, Is.True,
                "The enemy acquired a player unit but never damaged it; enemy combat is not resolving.");
            Assert.That(host.TryGetUnit(enemyEntity, out var enemy), Is.True);
            Assert.That(enemy.AttackTarget, Is.EqualTo(victimEntity),
                "The enemy must keep its authoritative attack target on the player unit.");
        }

        [UnityTest]
        [Order(6)]
        [Timeout(420000)]
        public IEnumerator FullBattle_ReachesTerminalOutcomeAndStopsPresentation()
        {
            yield return WaitForBootstrap();
            var host = _controller.Host;
            var localPlayer = _controller.LocalSession.Player;

            var snapshots = host.GetAllSnapshots();
            Assert.That(snapshots.Length, Is.GreaterThanOrEqualTo(40), "Host must have 40 units after initialization.");

            // Enemy counter-attacks are live (Phase 2.1 ordering bugfix), so
            // the earlier ordered tests may have cost the local army units;
            // the focus order is issued with the surviving roster only, and
            // the target must be alive to pass authoritative validation.
            var blueEntities = new System.Collections.Generic.List<CoreEntityId>();
            var targetEntity = default(CoreEntityId);
            long rallyX = 0;
            long rallyZ = 0;
            var enemyCount = 0;
            for (var index = 0; index < snapshots.Length; index++)
            {
                var snapshot = snapshots[index];
                if (snapshot.CurrentHealth <= 0)
                {
                    continue;
                }

                if (snapshot.Owner == localPlayer)
                {
                    blueEntities.Add(snapshot.Entity);
                }
                else
                {
                    rallyX += snapshot.Position.X;
                    rallyZ += snapshot.Position.Z;
                    enemyCount++;
                    if (!targetEntity.IsValid)
                    {
                        targetEntity = snapshot.Entity;
                    }
                }
            }

            Assert.That(blueEntities.Count, Is.GreaterThan(0), "The local army must have survivors.");
            Assert.That(targetEntity.IsValid, Is.True, "The enemy army must have survivors.");

            var rallyPoint = new WorldPointMm(
                (int)(rallyX / enemyCount),
                (int)(rallyZ / enemyCount));

            // March the surviving army into the center of the enemy
            // formation: the explicit move order guarantees convergence
            // (two-sided pursuit-only dynamics otherwise produce slow,
            // chaotic engagements), then the delayed focus attack
            // re-enables auto-acquire and turns the contact into a decisive
            // attrition battle. After the focus target dies, auto-acquire
            // keeps the battle running until one side is fully eliminated.
            var queue = GetCommandQueue();
            queue.QueueMove(rallyPoint, blueEntities.ToArray(), NextServerTick());
            queue.QueueAttack(targetEntity, blueEntities.ToArray(), NextServerTick() + 120);

            var elapsed = 0f;
            while (!_controller.Outcome.IsTerminal && elapsed < BattleTimeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.That(_controller.Outcome.IsTerminal, Is.True,
                $"Battle did not reach a terminal outcome within {BattleTimeoutSeconds} seconds.");
            Assert.That(host.Outcome.IsTerminal, Is.True);
            Assert.That(_controller.Outcome, Is.EqualTo(host.Outcome),
                "Controller outcome must come from the authoritative host.");
            Assert.That(_controller.FriendlyAlive == 0 || _controller.EnemyAlive == 0, Is.True,
                "A terminal outcome requires one side to be fully eliminated.");

            var deadFound = false;
            var units = Object.FindObjectsByType<PrototypeUnit>(FindObjectsSortMode.None);
            for (var index = 0; index < units.Length; index++)
            {
                var unit = units[index];
                if (unit.IsAlive)
                {
                    continue;
                }

                deadFound = true;
                var bodyRenderer = unit.GetComponent<Renderer>();
                Assert.That(bodyRenderer.enabled, Is.False,
                    $"Dead unit {unit.Entity.Value} must hide its presentation.");
            }

            Assert.That(deadFound, Is.True,
                "The terminal battle must leave at least one dead presentation unit.");
        }

        private IEnumerator WaitForBootstrap()
        {
            var elapsed = 0f;
            while (elapsed < BootstrapTimeoutSeconds)
            {
                _controller = Object.FindFirstObjectByType<PrototypeRtsController>();
                if (_controller != null && _controller.HostActive)
                {
                    yield break;
                }

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.Fail(
                $"Runtime bootstrap did not produce an active LocalMatchHost within {BootstrapTimeoutSeconds} seconds.");
        }

        private IEnumerator WaitForHostTick(ulong targetTick)
        {
            var elapsed = 0f;
            while (_controller.Host.CurrentTick < targetTick && elapsed < TickTimeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.That(_controller.Host.CurrentTick, Is.GreaterThanOrEqualTo(targetTick),
                $"Host did not reach tick {targetTick} within {TickTimeoutSeconds} seconds.");
        }

        private IEnumerator WaitForSnapshotPosition(CoreEntityId entity, WorldPointMm destination)
        {
            var elapsed = 0f;
            while (elapsed < TickTimeoutSeconds)
            {
                if (_controller.Host.TryGetUnit(entity, out var snapshot) &&
                    snapshot.Position == destination)
                {
                    yield break;
                }

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.Fail(
                $"Unit {entity.Value} did not reach the destination through the authoritative pipeline.");
        }

        private void QueueMove(CoreEntityId entity, WorldPointMm destination) =>
            QueueMove(new[] { entity }, destination);

        private void QueueMove(CoreEntityId[] entities, WorldPointMm destination)
        {
            var queue = GetCommandQueue();
            queue.QueueMove(destination, entities, NextServerTick());
        }

        private void QueueAttack(CoreEntityId attacker, CoreEntityId target) =>
            QueueAttack(new[] { attacker }, target);

        private void QueueAttack(CoreEntityId[] attackers, CoreEntityId target)
        {
            var queue = GetCommandQueue();
            queue.QueueAttack(target, attackers, NextServerTick());
        }

        private PrototypeCommandQueue GetCommandQueue() =>
            (PrototypeCommandQueue)typeof(PrototypeRtsController)
                .GetField("_commandQueue", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_controller);

        /// <summary>
        /// The next authoritative tick a newly issued command can target
        /// (server is at tick N; tick N+1 is the next tick it can take effect).
        /// </summary>
        private ulong NextServerTick() => _controller.Host.CurrentTick + 1ul;

        private static PrototypeUnit GetPresentationUnit(CoreEntityId entity)
        {
            var units = Object.FindObjectsByType<PrototypeUnit>(FindObjectsSortMode.None);
            for (var index = 0; index < units.Length; index++)
            {
                if (units[index].Entity.Equals(entity))
                {
                    return units[index];
                }
            }

            Assert.Fail($"Presentation unit for entity {entity.Value} was not found.");
            return null;
        }

        private static void AssertMovedTowards(
            WorldPointMm from,
            WorldPointMm current,
            WorldPointMm target)
        {
            var before = SquaredDistance(from, target);
            var after = SquaredDistance(current, target);
            Assert.That(after, Is.LessThan(before),
                "The attacker must move closer to the target.");
        }

        private static long SquaredDistance(WorldPointMm left, WorldPointMm right)
        {
            var dx = left.X - right.X;
            var dz = left.Z - right.Z;
            return dx * dx + dz * dz;
        }
    }
}