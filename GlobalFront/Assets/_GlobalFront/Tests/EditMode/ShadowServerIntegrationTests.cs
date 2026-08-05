using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Model;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.EditMode
{
    public sealed class ShadowServerIntegrationTests
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

            SpawnUnit(1, LocalPlayer, new Vector3(0f, 1.1f, 0f));
            SpawnUnit(2, EnemyPlayer, new Vector3(5f, 1.1f, 0f));

            InvokePrivateMethod(_controller, "RefreshUnits");
            InvokePrivateMethod(_controller, "UpdateRosterCounts");
            InvokePrivateMethod(_controller, "InitializeShadowServer");
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
        public void ShadowServer_IsActiveAfterInitialization()
        {
            Assert.That(_controller.ShadowServerActive, Is.True);
        }

        [Test]
        public void ShadowServer_UnitCountMatchesRegistry()
        {
            Assert.That(_controller.ShadowServerUnitCount, Is.EqualTo(2));
        }

        [Test]
        public void ShadowServer_EveryClientUnitIsRegisteredWithMatchingEntityId()
        {
            var server = GetShadowServer();
            Assert.That(server.TryGetUnit(new EntityId(1), out _), Is.True);
            Assert.That(server.TryGetUnit(new EntityId(2), out _), Is.True);
        }

        [Test]
        public void ShadowServer_RegisteredOwnerMatchesClient()
        {
            var server = GetShadowServer();
            Assert.That(server.TryGetUnit(new EntityId(1), out var friendly), Is.True);
            Assert.That(friendly.Owner, Is.EqualTo(LocalPlayer));

            Assert.That(server.TryGetUnit(new EntityId(2), out var enemy), Is.True);
            Assert.That(enemy.Owner, Is.EqualTo(EnemyPlayer));
        }

        [Test]
        public void ShadowServer_RegisteredPositionMatchesClient()
        {
            var server = GetShadowServer();
            Assert.That(server.TryGetUnit(new EntityId(1), out var friendly), Is.True);
            Assert.That(friendly.Position, Is.EqualTo(new WorldPointMm(0, 0)));

            Assert.That(server.TryGetUnit(new EntityId(2), out var enemy), Is.True);
            Assert.That(enemy.Position, Is.EqualTo(new WorldPointMm(5000, 0)));
        }

        [Test]
        public void ShadowServer_RegisteredHealthMatchesClient()
        {
            var server = GetShadowServer();
            Assert.That(server.TryGetUnit(new EntityId(1), out var friendly), Is.True);
            Assert.That(friendly.CurrentHealth, Is.EqualTo(100));

            Assert.That(server.TryGetUnit(new EntityId(2), out var enemy), Is.True);
            Assert.That(enemy.CurrentHealth, Is.EqualTo(100));
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

        private MatchServer GetShadowServer()
        {
            var field = typeof(PrototypeRtsController).GetField(
                "_shadowServer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (MatchServer)field.GetValue(_controller);
        }

        private static void InvokePrivateMethod(object target, string methodName)
        {
            var method = target.GetType().GetMethod(
                methodName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(target, null);
        }
    }
}
