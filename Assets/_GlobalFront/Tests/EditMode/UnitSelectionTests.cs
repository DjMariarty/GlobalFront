using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Model;
using NUnit.Framework;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.EditMode
{
    public sealed class UnitSelectionTests
    {
        private static readonly PlayerId LocalPlayer = new PlayerId(1);
        private static readonly PlayerId EnemyPlayer = new PlayerId(2);

        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        private Camera _camera;
        private UnitRegistry _registry;
        private UnitSelection _selection;

        [SetUp]
        public void SetUp()
        {
            _registry = new UnitRegistry();
            _selection = new UnitSelection(_registry);

            var cameraObject = new GameObject("Selection Test Camera");
            _createdObjects.Add(cameraObject);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.pixelRect = new Rect(0, 0, 800, 600);
            _camera.fieldOfView = 60f;
            _camera.nearClipPlane = 0.3f;
            _camera.farClipPlane = 100f;
            _camera.transform.position = new Vector3(0, 10, -10);
            _camera.transform.rotation = Quaternion.Euler(45, 0, 0);
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
            _camera = null;
            _registry = null;
            _selection = null;
        }

        private PrototypeUnit SpawnUnit(ulong entityValue, PlayerId owner, Vector3 position)
        {
            var unitObject = new GameObject($"Unit {entityValue}");
            _createdObjects.Add(unitObject);
            unitObject.transform.position = position;

            var unit = unitObject.AddComponent<PrototypeUnit>();
            unit.Initialize(owner);
            unit.AssignAuthoritativeEntity(new CoreEntityId(entityValue));
            return unit;
        }

        private void RefreshRegistry()
        {
            _registry.Refresh(LocalPlayer);
        }

        private void SelectUnitByScreenPosition(PrototypeUnit unit, bool additive)
        {
            var screenPoint = _camera.WorldToScreenPoint(unit.transform.position);
            _selection.SelectInsideScreenRect(
                _camera,
                new Vector2(screenPoint.x - 20, screenPoint.y - 20),
                new Vector2(screenPoint.x + 20, screenPoint.y + 20),
                additive,
                LocalPlayer);
        }

        [Test]
        public void SelectInsideScreenRect_SelectsUnitsWithinRect()
        {
            var unit = SpawnUnit(1, LocalPlayer, Vector3.zero);
            RefreshRegistry();

            SelectUnitByScreenPosition(unit, additive: false);

            Assert.That(_selection.Count, Is.EqualTo(1));
            Assert.That(unit.IsSelected, Is.True);
        }

        [Test]
        public void SelectInsideScreenRect_NonAdditive_ReplacesPreviousSelection()
        {
            var first = SpawnUnit(1, LocalPlayer, Vector3.zero);
            var second = SpawnUnit(2, LocalPlayer, new Vector3(3, 0, 0));
            RefreshRegistry();

            SelectUnitByScreenPosition(first, additive: false);
            SelectUnitByScreenPosition(second, additive: false);

            Assert.That(_selection.Count, Is.EqualTo(1));
            Assert.That(first.IsSelected, Is.False);
            Assert.That(second.IsSelected, Is.True);
        }

        [Test]
        public void SelectInsideScreenRect_Additive_KeepsPreviousSelection()
        {
            var first = SpawnUnit(1, LocalPlayer, Vector3.zero);
            var second = SpawnUnit(2, LocalPlayer, new Vector3(3, 0, 0));
            RefreshRegistry();

            SelectUnitByScreenPosition(first, additive: false);
            SelectUnitByScreenPosition(second, additive: true);

            Assert.That(_selection.Count, Is.EqualTo(2));
            Assert.That(first.IsSelected, Is.True);
            Assert.That(second.IsSelected, Is.True);
        }

        [Test]
        public void SelectInsideScreenRect_IgnoresEnemyUnits()
        {
            var enemy = SpawnUnit(1, EnemyPlayer, Vector3.zero);
            RefreshRegistry();

            SelectUnitByScreenPosition(enemy, additive: false);

            Assert.That(_selection.Count, Is.EqualTo(0));
            Assert.That(enemy.IsSelected, Is.False);
        }

        [Test]
        public void SelectInsideScreenRect_IgnoresDeadUnits()
        {
            var dead = SpawnUnit(1, LocalPlayer, Vector3.zero);
            RefreshRegistry();

            dead.CombatState.ApplyDamage(1000);
            SelectUnitByScreenPosition(dead, additive: false);

            Assert.That(_selection.Count, Is.EqualTo(0));
            Assert.That(dead.IsSelected, Is.False);
        }

        [Test]
        public void SelectInsideScreenRect_IgnoresUnitsBehindCamera()
        {
            var behind = SpawnUnit(1, LocalPlayer, new Vector3(0, 10, -30));
            RefreshRegistry();

            var screenPoint = _camera.WorldToScreenPoint(behind.transform.position);
            Assert.That(screenPoint.z, Is.LessThanOrEqualTo(0));

            _selection.SelectInsideScreenRect(
                _camera,
                new Vector2(0, 0),
                new Vector2(800, 600),
                additive: false,
                localPlayer: LocalPlayer);

            Assert.That(_selection.Count, Is.EqualTo(0));
        }

        [Test]
        public void Clear_RemovesAllSelectedUnits_AndResetsFlags()
        {
            var first = SpawnUnit(1, LocalPlayer, Vector3.zero);
            var second = SpawnUnit(2, LocalPlayer, new Vector3(3, 0, 0));
            RefreshRegistry();

            SelectUnitByScreenPosition(first, additive: false);
            SelectUnitByScreenPosition(second, additive: true);
            Assert.That(_selection.Count, Is.EqualTo(2));

            _selection.Clear();

            Assert.That(_selection.Count, Is.EqualTo(0));
            Assert.That(first.IsSelected, Is.False);
            Assert.That(second.IsSelected, Is.False);
        }

        [Test]
        public void PruneDead_RemovesDeadUnits_KeepsAlive()
        {
            var alive = SpawnUnit(1, LocalPlayer, Vector3.zero);
            var dying = SpawnUnit(2, LocalPlayer, new Vector3(3, 0, 0));
            RefreshRegistry();

            SelectUnitByScreenPosition(alive, additive: false);
            SelectUnitByScreenPosition(dying, additive: true);
            Assert.That(_selection.Count, Is.EqualTo(2));

            dying.CombatState.ApplyDamage(1000);
            _selection.PruneDead();

            Assert.That(_selection.Count, Is.EqualTo(1));
            Assert.That(alive.IsSelected, Is.True);
            Assert.That(dying.IsSelected, Is.False);
        }

        [Test]
        public void GetSelectedEntityIds_ReturnsDeterministicallySortedIds()
        {
            // Spawn out of order to prove selection is sorted by entity id.
            SpawnUnit(3, LocalPlayer, new Vector3(6, 0, 0));
            SpawnUnit(1, LocalPlayer, Vector3.zero);
            SpawnUnit(2, LocalPlayer, new Vector3(3, 0, 0));
            RefreshRegistry();

            _selection.SelectInsideScreenRect(
                _camera,
                new Vector2(0, 0),
                new Vector2(800, 600),
                additive: false,
                localPlayer: LocalPlayer);

            var ids = _selection.GetSelectedEntityIds();

            Assert.That(ids.Length, Is.EqualTo(3));
            Assert.That(ids[0], Is.EqualTo(new CoreEntityId(1)));
            Assert.That(ids[1], Is.EqualTo(new CoreEntityId(2)));
            Assert.That(ids[2], Is.EqualTo(new CoreEntityId(3)));
        }
    }
}