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
    /// drive the actual <see cref="FixedSimulationRunner"/> /
    /// <see cref="PrototypeRtsController"/> loop. Commands are queued through
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
        private const float BattleTimeoutSeconds = 100f;

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
            var entity = new CoreEntityId(1);
            Assert.That(host.TryGetUnit(entity, out var initial), Is.True);
            var startPosition = initial.Position;

            QueueMove(entity, new WorldPointMm(initial.Position.X + 5000, initial.Position.Z));

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
            var entity = new CoreEntityId(1);
            Assert.That(host.TryGetUnit(entity, out var initial), Is.True);
            var destination = new WorldPointMm(initial.Position.X + 5000, initial.Position.Z);

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
            var attackerEntity = new CoreEntityId(1);
            var targetEntity = new CoreEntityId(1001);
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
        public IEnumerator FullBattle_ReachesTerminalOutcomeAndStopsPresentation()
        {
            yield return WaitForBootstrap();
            var host = _controller.Host;
            var targetEntity = new CoreEntityId(1001);

            // Order the whole local army to focus one enemy unit. After the
            // focus target dies, auto-acquire keeps the battle running until
            // one side is fully eliminated.
            var blueEntities = new CoreEntityId[20];
            for (var index = 0; index < blueEntities.Length; index++)
            {
                blueEntities[index] = new CoreEntityId((ulong)(index + 1));
            }

            QueueAttack(blueEntities, targetEntity);

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
            var runner = _controller.GetComponent<FixedSimulationRunner>();
            queue.QueueMove(destination, entities, runner.Tick + 1);
        }

        private void QueueAttack(CoreEntityId attacker, CoreEntityId target) =>
            QueueAttack(new[] { attacker }, target);

        private void QueueAttack(CoreEntityId[] attackers, CoreEntityId target)
        {
            var queue = GetCommandQueue();
            var runner = _controller.GetComponent<FixedSimulationRunner>();
            queue.QueueAttack(target, attackers, runner.Tick + 1);
        }

        private PrototypeCommandQueue GetCommandQueue() =>
            (PrototypeCommandQueue)typeof(PrototypeRtsController)
                .GetField("_commandQueue", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_controller);

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