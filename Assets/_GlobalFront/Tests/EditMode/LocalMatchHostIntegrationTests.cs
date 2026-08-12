using System.Collections.Generic;
using System.Reflection;
using GlobalFront.Client;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Integration tests for the authoritative <see cref="LocalMatchHost"/>
    /// wiring inside <see cref="PrototypeRtsController"/>. These verify that
    /// the controller initializes the host, registers every client unit with a
    /// matching entity id, advances exactly one server tick per
    /// <see cref="FixedSimulationRunner.TickExecuted"/> and synchronizes
    /// presentation units from authoritative snapshots. The host is accessed
    /// through the public <see cref="PrototypeRtsController.Host"/> property,
    /// replacing the reflection-based access used by the former shadow tests
    /// (see ARCHITECTURE.md DEBT-004).
    /// </summary>
    public sealed class LocalMatchHostIntegrationTests
    {
        private static readonly PlayerId LocalPlayer = new PlayerId(1);
        private static readonly PlayerId EnemyPlayer = new PlayerId(2);

        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private PrototypeRtsController _controller;
        private FixedSimulationRunner _runner;
        private GameObject _controllerObject;

        [SetUp]
        public void SetUp()
        {
            _controllerObject = new GameObject("Test Controller");
            _createdObjects.Add(_controllerObject);
            _runner = _controllerObject.AddComponent<FixedSimulationRunner>();
            _controller = _controllerObject.AddComponent<PrototypeRtsController>();

            // EditMode does not run the MonoBehaviour lifecycle (Awake/Start)
            // for AddComponent, so the controller's Awake must be invoked
            // explicitly to initialize _runner, _selection and _commandQueue
            // before the registration/tick methods are exercised.
            InvokePrivateMethod(_controller, "Awake");

            SpawnUnit(1, LocalPlayer, new Vector3(0f, 1.1f, 0f));
            SpawnUnit(2, EnemyPlayer, new Vector3(5f, 1.1f, 0f));

            InvokePrivateMethod(_controller, "RefreshUnits");
            InvokePrivateMethod(_controller, "UpdateRosterCounts");
            InvokePrivateMethod(_controller, "InitializeLocalHost");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _createdObjects)
            {
                if (obj != null)
                {
                    Object.DestroyImmediate(obj);
                }
            }

            _createdObjects.Clear();
            _controller = null;
            _runner = null;
            _controllerObject = null;
        }

        [Test]
        public void LocalMatchHost_IsActiveAfterInitialization()
        {
            Assert.That(_controller.HostActive, Is.True);
            Assert.That(_controller.Host, Is.Not.Null);
        }

        [Test]
        public void LocalMatchHost_UnitCountMatchesRegistry()
        {
            Assert.That(_controller.HostUnitCount, Is.EqualTo(2));
        }

        [Test]
        public void LocalMatchHost_EveryClientUnitRegisteredWithMatchingEntityId()
        {
            var host = _controller.Host;
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out _), Is.True);
            Assert.That(host.TryGetUnit(new CoreEntityId(2), out _), Is.True);
        }

        [Test]
        public void LocalMatchHost_RegisteredOwnerMatchesClient()
        {
            var host = _controller.Host;
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out var friendly), Is.True);
            Assert.That(friendly.Owner, Is.EqualTo(LocalPlayer));

            Assert.That(host.TryGetUnit(new CoreEntityId(2), out var enemy), Is.True);
            Assert.That(enemy.Owner, Is.EqualTo(EnemyPlayer));
        }

        [Test]
        public void LocalMatchHost_RegisteredPositionMatchesClient()
        {
            var host = _controller.Host;
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out var friendly), Is.True);
            Assert.That(friendly.Position, Is.EqualTo(new WorldPointMm(0, 0)));

            Assert.That(host.TryGetUnit(new CoreEntityId(2), out var enemy), Is.True);
            Assert.That(enemy.Position, Is.EqualTo(new WorldPointMm(5000, 0)));
        }

        [Test]
        public void LocalMatchHost_RegisteredHealthMatchesClient()
        {
            var host = _controller.Host;
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out var friendly), Is.True);
            Assert.That(friendly.CurrentHealth, Is.EqualTo(100));

            Assert.That(host.TryGetUnit(new CoreEntityId(2), out var enemy), Is.True);
            Assert.That(enemy.CurrentHealth, Is.EqualTo(100));
        }

        [Test]
        public void TickExecuted_AdvancesHostByExactlyOneServerTick()
        {
            EnableBattle();
            Assert.That(_controller.Host.CurrentTick, Is.EqualTo(0ul));

            InvokePrivateMethod(_controller, "OnTickExecuted", 1ul);
            Assert.That(_controller.Host.CurrentTick, Is.EqualTo(1ul));

            InvokePrivateMethod(_controller, "OnTickExecuted", 2ul);
            Assert.That(_controller.Host.CurrentTick, Is.EqualTo(2ul));
        }

        [Test]
        public void TickExecuted_SynchronizesPresentationFromServerSnapshots()
        {
            EnableBattle();

            Assert.That(
                _controller.Host.TryEnqueueMove(
                    new CommandHeader(LocalPlayer, 1, 1, GameCommandType.Move),
                    new[] { new CoreEntityId(1) },
                    new WorldPointMm(10000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            var friendly = GetUnit(new CoreEntityId(1));
            Assert.That(friendly.CurrentPosition, Is.EqualTo(new WorldPointMm(0, 0)));

            InvokePrivateMethod(_controller, "OnTickExecuted", 1ul);

            Assert.That(
                _controller.Host.TryGetUnit(new CoreEntityId(1), out var snapshot),
                Is.True);
            Assert.That(friendly.CurrentPosition, Is.EqualTo(snapshot.Position));
            Assert.That(friendly.CurrentPosition, Is.EqualTo(new WorldPointMm(350, 0)));
        }

        private PrototypeUnit SpawnUnit(ulong entityValue, PlayerId owner, Vector3 position)
        {
            var unitObject = new GameObject("Unit " + entityValue);
            _createdObjects.Add(unitObject);
            unitObject.transform.position = position;

            var unit = unitObject.AddComponent<PrototypeUnit>();
            unit.Initialize(new CoreEntityId(entityValue), owner);
            return unit;
        }

        private PrototypeUnit GetUnit(CoreEntityId entity)
        {
            var field = typeof(PrototypeRtsController).GetField(
                "_registry",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var registry = (UnitRegistry)field.GetValue(_controller);
            return registry.TryGetUnit(entity, out var unit) ? unit : null;
        }

        /// <summary>
        /// Sets the private <c>_battleInitialized</c> flag so
        /// <see cref="PrototypeRtsController.OnTickExecuted"/> does not early
        /// return before ticking the host.
        /// </summary>
        private void EnableBattle()
        {
            var field = typeof(PrototypeRtsController).GetField(
                "_battleInitialized",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(_controller, true);
        }

        private static void InvokePrivateMethod(
            object target,
            string methodName,
            params object[] args)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(target, args);
        }
    }
}
