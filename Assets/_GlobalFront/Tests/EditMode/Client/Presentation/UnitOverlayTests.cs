using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using EntityId = GlobalFront.Core.Model.EntityId;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;
using Object = UnityEngine.Object;

namespace GlobalFront.Tests.EditMode.Client.Presentation
{
    /// <summary>
    /// Phase 3, step 3.4: the instanced overlay batches OD-25 draws selection rings
    /// and health bars with (<see cref="UnitOverlayBatcher"/>).
    ///
    /// The two things a reviewer cannot see from a screenshot are what this suite
    /// defends. First, ownership: an overlay that outlives its unit is a ring sitting
    /// on a corpse or, worse, on the next unit that spawns into the same pooled view,
    /// so every lifecycle edge the binder has (bind, release, slot hand-over, resync,
    /// leaving a match) has to clear the selection along with the identity. Second,
    /// robustness: the batches go straight into a <c>DrawMeshInstanced</c> call, and
    /// one NaN in one instance matrix can take the whole draw apart on some APIs, so a
    /// corrupted position, an archetype with no catalog row, or a zero-length camera
    /// rotation has to cost one missing overlay and nothing else.
    ///
    /// <see cref="UnitOverlayBatcher.BuildBatches"/> is deliberately exercisable
    /// without a camera, a renderer or a frame: everything asserted here is the
    /// visibility rule set and the contents of the buffers the pass would submit.
    /// </summary>
    [TestFixture]
    public sealed class UnitOverlayTests
    {
        private const int SlotCapacity = 64;
        private const int MatchUnits = 400;
        private const int LargeMatchCapacity = 512;
        private const double TickSeconds = 0.05;           // 20 Hz simulation
        private const double FrameSeconds = 1.0 / 60.0;    // 60 fps presentation

        /// <summary>The unit's X position in millimetres, wherever a unit spawns.</summary>
        private const int XMillimetres = 1000;

        private const int ZMillimetres = 2000;
        private const int Health = UnitCatalog.PrototypeMaximumHealth;
        private const int DamagedHealth = 65;

        /// <summary>Diameter of the prototype footprint, in metres.</summary>
        private const float PrototypeDiameter = UnitCatalog.PrototypeRadiusMm * UnitView.MillimetresToMetres * 2f;

        private static readonly Quaternion DefaultCameraRotation = Quaternion.Euler(45f, 0f, 0f);

        private Transform _root;
        private UnitViewPool _pool;
        private ClientReplicationWorld _world;
        private UnitViewTickBuffer _buffer;
        private UnitViewBinder _binder;
        private UnitOverlayBatcher _batcher;
        private ulong _tick;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("UnitOverlayTests_Root").transform;
            _pool = new UnitViewPool(_root);
            CreateMatch(SlotCapacity);
        }

        [TearDown]
        public void TearDown()
        {
            _pool?.Dispose();
            if (_root != null)
            {
                Object.DestroyImmediate(_root.gameObject);
            }
        }

        // ---------------------------------------------------------------- helpers

        private void CreateMatch(int slotCapacity)
        {
            _world = new ClientReplicationWorld(slotCapacity);
            _buffer = new UnitViewTickBuffer(slotCapacity, TickSeconds);
            _binder = new UnitViewBinder(_pool, _world, _buffer);
            _batcher = new UnitOverlayBatcher();
            _tick = 0;
        }

        private void AddUnit(
            ulong entity,
            byte kind = UnitKinds.Scout,
            int posX = XMillimetres,
            int posZ = ZMillimetres,
            int health = Health)
        {
            Assert.That(
                _world.ApplyAdd(new DeltaAddRecord(
                    new EntityId(entity),
                    new PlayerId(1),
                    new WorldPointMm(posX, posZ),
                    health,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false,
                    kind)),
                Is.EqualTo(ClientWorldApplyResult.Ok),
                $"unit {entity} must enter the client world");

            // The pool never instantiates in combat (OD-26), so a binder with an
            // unwarmed archetype silently leaves the unit un-presented — and "no
            // view" is exactly the failure these tests must not confuse with "the
            // overlay pass drew nothing".
            _pool.Warmup(kind, _world.LiveCount);
        }

        private void RemoveUnit(ulong entity)
        {
            Assert.That(
                _world.ApplyRemove(new DeltaRemoveRecord(
                    new EntityId(entity),
                    DeltaRemoveCause.Destroyed)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
        }

        /// <summary>
        /// One replication packet plus the frame that follows it. Binding happens
        /// during the render, and the view keeps its authoritative snap until the ring
        /// recaptures the slot, so this is the shortest path to a bound, filled view.
        /// </summary>
        private void Step(int frames = 1)
        {
            _tick++;
            _buffer.CaptureTick(_tick, _world);
            for (var frame = 0; frame < frames; frame++)
            {
                _binder.Render(FrameSeconds);
            }
        }

        private int SlotOf(ulong entity)
        {
            for (var slot = 0; slot < _world.Capacity; slot++)
            {
                if (_world.TryGetSlotState(slot, out var state) && state.Entity.Value == entity)
                {
                    return slot;
                }
            }

            Assert.Fail($"entity {entity} is not in the client world");
            return -1;
        }

        private UnitView ViewOf(ulong entity)
        {
            var slot = SlotOf(entity);
            Assert.That(_binder.TryGetView(slot, out var view), Is.True, $"entity {entity} has no view");
            return view;
        }

        /// <summary>Spawns one scout, binds its view, and returns the view.</summary>
        private UnitView SpawnUnit(
            ulong entity,
            byte kind = UnitKinds.Scout,
            int health = Health)
        {
            AddUnit(entity, kind, health: health);
            Step();
            return ViewOf(entity);
        }

        private void Build() => _batcher.BuildBatches(_binder, DefaultCameraRotation);

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static void AssertFinite(float value, string field, string because) =>
            Assert.That(IsFinite(value), Is.True, $"{because}: {field} must stay finite");

        private static void AssertAllFinite(Matrix4x4 matrix, string because)
        {
            AssertFinite(matrix.m00, nameof(matrix.m00), because);
            AssertFinite(matrix.m01, nameof(matrix.m01), because);
            AssertFinite(matrix.m02, nameof(matrix.m02), because);
            AssertFinite(matrix.m03, nameof(matrix.m03), because);
            AssertFinite(matrix.m10, nameof(matrix.m10), because);
            AssertFinite(matrix.m11, nameof(matrix.m11), because);
            AssertFinite(matrix.m12, nameof(matrix.m12), because);
            AssertFinite(matrix.m13, nameof(matrix.m13), because);
            AssertFinite(matrix.m20, nameof(matrix.m20), because);
            AssertFinite(matrix.m21, nameof(matrix.m21), because);
            AssertFinite(matrix.m22, nameof(matrix.m22), because);
            AssertFinite(matrix.m23, nameof(matrix.m23), because);
            AssertFinite(matrix.m30, nameof(matrix.m30), because);
            AssertFinite(matrix.m31, nameof(matrix.m31), because);
            AssertFinite(matrix.m32, nameof(matrix.m32), because);
            AssertFinite(matrix.m33, nameof(matrix.m33), because);
        }

        private static void AssertAllFinite(ReadOnlySpan<Matrix4x4> matrices, string because)
        {
            for (var index = 0; index < matrices.Length; index++)
            {
                AssertAllFinite(matrices[index], because);
            }
        }

        private static void AssertAllFinite(ReadOnlySpan<Vector4> properties, string because)
        {
            for (var index = 0; index < properties.Length; index++)
            {
                AssertFinite(properties[index].x, "x", because);
                AssertFinite(properties[index].y, "y", because);
                AssertFinite(properties[index].z, "z", because);
                AssertFinite(properties[index].w, "w", because);
            }
        }

        // ---------------------------------------------------------------- visibility rules

        [Test]
        public void HealthyUnselectedUnits_ProduceNoOverlays()
        {
            SpawnUnit(1);
            SpawnUnit(2);
            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0), "nothing was built yet");

            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0),
                "a full-health bar carries no information and costs a draw");
        }

        [Test]
        public void SelectingAUnit_AddsExactlyOneRingAndOneBar()
        {
            var view = SpawnUnit(1);
            view.SetSelected(true);

            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(1));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(1),
                "the selected unit is the one case a healthy bar is worth drawing");
            Assert.That(_batcher.HealthBarProperties[0].x, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void DeselectingAUnit_ClearsItsOverlaysOnTheNextBuild()
        {
            var view = SpawnUnit(1);
            view.SetSelected(true);
            Build();
            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(1));

            view.SetSelected(false);
            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0),
                "the batch is rebuilt every frame, not accumulated");
        }

        [Test]
        public void DamagedUnselectedUnit_DrawsABarButNoRing()
        {
            SpawnUnit(1, health: DamagedHealth);

            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(1));
            Assert.That(_batcher.HealthBarProperties[0].x, Is.EqualTo(0.65f).Within(1e-5f),
                "the bar publishes the fraction the buffer already computed");
        }

        [Test]
        public void AlwaysShowHealthBars_DrawsABarForEveryLivingBoundUnit()
        {
            SpawnUnit(1);
            SpawnUnit(2);
            SpawnUnit(3, health: DamagedHealth);

            _batcher.AlwaysShowHealthBars = true;
            Build();

            Assert.That(_batcher.HealthBarCount, Is.EqualTo(3));
            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0),
                "the preference is about bars, not about inventing selections");
        }

        [Test]
        public void UnitAtZeroHealth_DrawsNothingEvenWhileSelected()
        {
            var view = SpawnUnit(1);
            view.SetSelected(true);
            view.SnapHealth(0, 0f);

            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0), "a corpse gets no ring");
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0));
        }

        [Test]
        public void DeadUnit_LeavesNoOverlayEvenWhenItWasSelected()
        {
            var view = SpawnUnit(1);
            view.SetSelected(true);
            SpawnUnit(2);

            RemoveUnit(1);
            Step();

            Assert.That(view.IsBound, Is.False, "the binder retired the view with the unit");
            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0));
        }

        [Test]
        public void ReleasedViews_LeaveNoOverlay()
        {
            var view = SpawnUnit(1);
            view.SetSelected(true);
            Assert.That(_binder.ReleaseAllViews(), Is.EqualTo(1));

            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- lifecycle ownership

        [Test]
        public void View_SelectionAndArchetypeState_DoNotSurviveBindOrRelease()
        {
            Assert.That(_pool.Warmup(UnitKinds.Scout, 1), Is.EqualTo(1));
            var view = _pool.Acquire(UnitKinds.Scout);

            view.Bind(new EntityId(5), Health, UnitCatalog.PrototypeRadiusMm);
            Assert.That(view.MaxHealth, Is.EqualTo(Health));
            Assert.That(view.RadiusMillimetres, Is.EqualTo(UnitCatalog.PrototypeRadiusMm));

            view.SetSelected(true);
            Assert.That(view.IsSelected, Is.True);

            view.Bind(new EntityId(6), Health, UnitCatalog.PrototypeRadiusMm);
            Assert.That(view.IsSelected, Is.False, "a rebind is a new unit, not a new label on the old one");
            Assert.That(view.Health, Is.EqualTo(0), "the previous unit's readout must not flash on the next");

            view.SetSelected(true);
            view.Release();
            Assert.That(view.IsSelected, Is.False);
            Assert.That(view.MaxHealth, Is.EqualTo(0), "an unbound view has no archetype");
            Assert.That(view.RadiusMillimetres, Is.EqualTo(0));

            // A catalog row is validated on construction, so a negative could only
            // arrive from a hand-written call; either way it cannot become a geometry.
            view.Bind(new EntityId(7), -5, -1);
            Assert.That(view.MaxHealth, Is.EqualTo(0));
            Assert.That(view.RadiusMillimetres, Is.EqualTo(0));

            Assert.That(_pool.Release(view), Is.True);
        }

        [Test]
        public void RecycledSlot_NewUnitDoesNotInheritTheSelection()
        {
            var first = SpawnUnit(1);
            first.SetSelected(true);
            Build();
            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(1));

            // The hand-over the table actually performs: the slot is freed and taken
            // again before the next presentation frame. The slot has to be named
            // before the death, because after it the entity is gone from the table.
            var recycled = SlotOf(1);
            RemoveUnit(1);
            AddUnit(2);
            Assert.That(SlotOf(2), Is.EqualTo(recycled), "the freed slot is the recycled one");
            Step();

            var replacement = ViewOf(2);
            Assert.That(replacement, Is.SameAs(first), "presentation recycles the same object");
            Assert.That(replacement.IsSelected, Is.False);

            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0),
                "a ring on a unit nobody selected is a lie about the player's order");
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0));
        }

        [Test]
        public void Resync_ClearsEveryOverlayAndSelection()
        {
            SpawnUnit(1).SetSelected(true);
            SpawnUnit(2).SetSelected(true);
            Build();
            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(2));

            _binder.HandleResync(_tick + 50);
            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0));

            // The keyframe may bring the same entities back: they are new views, and
            // OD-20 does not promise the player kept their selection.
            _world.Reset();
            AddUnit(1);
            Step();
            Assert.That(ViewOf(1).IsSelected, Is.False);
        }

        // ---------------------------------------------------------------- geometry

        [Test]
        public void Ring_SitsOnTheGroundPlaneAndSpansTheCatalogDiameter()
        {
            var view = SpawnUnit(1);
            view.SetSelected(true);

            Build();

            var matrix = _batcher.SelectionRingMatrices[0];
            AssertAllFinite(matrix, "a selected, alive unit at a finite position must produce a real matrix");
            Assert.That(
                matrix.m03,
                Is.EqualTo(XMillimetres * UnitView.MillimetresToMetres).Within(1e-5f),
                "the ring is centred on the hull in X");
            Assert.That(
                matrix.m23,
                Is.EqualTo(ZMillimetres * UnitView.MillimetresToMetres).Within(1e-5f),
                "and in Z");
            Assert.That(matrix.m13, Is.EqualTo(UnitOverlayBatcher.RingHeightMetres).Within(1e-6f),
                "lifted off the flat Y = 0 playfield so it cannot z-fight with the terrain (OD-27)");
            Assert.That(matrix.m00, Is.EqualTo(PrototypeDiameter).Within(1e-5f),
                "the quad is unit-sized, so the scale is the ring diameter in metres");
            Assert.That(matrix.m22, Is.EqualTo(PrototypeDiameter).Within(1e-5f));
            Assert.That(matrix.m11, Is.EqualTo(1f).Within(1e-6f), "a flat quad has no height to scale");
            Assert.That(view.RadiusMillimetres, Is.EqualTo(UnitCatalog.PrototypeRadiusMm));
        }

        [Test]
        public void Bar_BillboardsToTheCameraAndCarriesTheHealthFraction()
        {
            var rotation = Quaternion.Euler(30f, 37f, -12f);
            SpawnUnit(1, health: DamagedHealth);

            _batcher.BuildBatches(_binder, rotation);

            var matrix = _batcher.HealthBarMatrices[0];
            AssertAllFinite(matrix, "the bar of an alive, damaged unit must be a real matrix");

            // The bar quad's normal is its local +Z, so a correct billboard is exactly
            // "local +Z lands on the camera's forward" — no per-instance look-at, and
            // no assumption about what that rotation happens to be.
            var normal = matrix.MultiplyVector(Vector3.forward).normalized;
            Assert.That(
                Vector3.Distance(normal, (rotation * Vector3.forward).normalized),
                Is.LessThan(1e-4f));
            Assert.That(
                matrix.MultiplyVector(Vector3.up).magnitude,
                Is.EqualTo(UnitOverlayBatcher.HealthBarThicknessMetres).Within(1e-5f));
            Assert.That(
                matrix.MultiplyVector(Vector3.right).magnitude,
                Is.EqualTo(PrototypeDiameter * UnitOverlayBatcher.HealthBarWidthInDiameters).Within(1e-5f));
            Assert.That(
                matrix.MultiplyPoint3x4(Vector3.zero).y,
                Is.EqualTo(UnitOverlayBatcher.HealthBarHeightMetres).Within(1e-5f),
                "the bar floats above the hull origin by the configured offset");
            Assert.That(_batcher.HealthBarProperties[0].x, Is.EqualTo(0.65f).Within(1e-5f));
        }

        [Test]
        public void BarColor_RampsFromLowToFullHealth()
        {
            SpawnUnit(1, health: DamagedHealth);

            Build();

            var property = _batcher.HealthBarProperties[0];
            var expected = Color.Lerp(_batcher.LowHealthBarColor, _batcher.FullHealthBarColor, 0.65f);
            Assert.That(property.y, Is.EqualTo(expected.r).Within(1e-4f));
            Assert.That(property.z, Is.EqualTo(expected.g).Within(1e-4f));
            Assert.That(property.w, Is.EqualTo(expected.b).Within(1e-4f));
        }

        [Test]
        public void RingColor_IsThePerInstancePropertyOfEveryRing()
        {
            SpawnUnit(1).SetSelected(true);
            SpawnUnit(2).SetSelected(true);

            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(2));
            for (var index = 0; index < _batcher.SelectionRingCount; index++)
            {
                var property = _batcher.SelectionRingProperties[index];
                Assert.That(property.x, Is.EqualTo(_batcher.SelectionRingColor.r).Within(1e-5f));
                Assert.That(property.y, Is.EqualTo(_batcher.SelectionRingColor.g).Within(1e-5f));
                Assert.That(property.z, Is.EqualTo(_batcher.SelectionRingColor.b).Within(1e-5f));
                Assert.That(property.w, Is.EqualTo(_batcher.SelectionRingColor.a).Within(1e-5f));
            }
        }

        [Test]
        public void Overlays_FollowTheUnitAsItMoves()
        {
            var view = SpawnUnit(1);
            view.SetSelected(true);
            Build();
            Assert.That(_batcher.SelectionRingMatrices[0].m03, Is.EqualTo(1f).Within(1e-5f));

            Assert.That(
                _world.ApplyUpdate(new DeltaUpdateRecord(
                    new EntityId(1),
                    (byte)UnitDirtyMask.Position,
                    new PlayerId(1),
                    new WorldPointMm(7000, 9000),
                    0,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            Step(6);

            Build();

            Assert.That(_batcher.SelectionRingMatrices[0].m03, Is.GreaterThan(1.5f),
                "a ring that stayed behind the unit it belongs to would be worse than no ring");
        }

        // ---------------------------------------------------------------- adversarial input

        [Test]
        public void UnresolvedArchetype_DrawsNothingAndProducesNoDegenerateMatrix()
        {
            Assert.That(_pool.Warmup(UnitKinds.Unknown, 1), Is.EqualTo(1));
            var view = SpawnUnit(1, kind: UnitKinds.Unknown);
            view.SetSelected(true);

            Assert.That(view.MaxHealth, Is.EqualTo(0), "the client has no row for this archetype");
            Assert.That(view.RadiusMillimetres, Is.EqualTo(0));

            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0),
                "no denominator means no fraction, and a 0-width bar is not a bar");
            Assert.That(view.HealthFraction, Is.EqualTo(0f), "health * 0 is 0, never 0/0 = NaN");
        }

        [Test]
        public void NonFinitePositions_AreSkippedAndNeverReachTheBatch()
        {
            SpawnUnit(1).SetSelected(true);
            var poisoned = SpawnUnit(2);
            poisoned.SetSelected(true);
            SpawnUnit(3, health: DamagedHealth);

            // A non-finite transform write is refused by the engine, which logs it as
            // an error; that message is the point of the test, not a failure of it.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                foreach (var position in new[]
                {
                    new Vector3(float.NaN, 0f, 0f),
                    new Vector3(0f, float.NaN, 0f),
                    new Vector3(0f, 0f, float.PositiveInfinity),
                    new Vector3(float.NegativeInfinity, 1f, 2f),
                })
                {
                    poisoned.Hull.position = position;

                    // The expectation is derived from what the transform reports rather
                    // than from what was written. The engine keeps the hull at its last
                    // good position, so today the batcher's own position guard cannot be
                    // provoked through a Transform — it is the backstop for a pose whose
                    // millimetre arithmetic overflows before it ever reaches one. Pinning
                    // the batch to the read-back tests the real contract either way: an
                    // overlay for every finite hull, no overlay for a non-finite one, and
                    // never a non-finite number in either batch.
                    var held = IsFinite(poisoned.Hull.position);

                    Build();

                    Assert.That(
                        _batcher.SelectionRingCount,
                        Is.EqualTo(held ? 2 : 1),
                        $"rings must follow the hull positions (read-back {poisoned.Hull.position})");
                    Assert.That(
                        _batcher.HealthBarCount,
                        Is.EqualTo(held ? 3 : 2),
                        $"bars must follow the hull positions (read-back {poisoned.Hull.position})");
                    AssertAllFinite(_batcher.SelectionRingMatrices, $"after a position of {position}");
                    AssertAllFinite(_batcher.HealthBarMatrices, $"after a position of {position}");
                    AssertAllFinite(_batcher.HealthBarProperties, $"after a position of {position}");
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void DegenerateCameraRotation_FallsBackToIdentity()
        {
            SpawnUnit(1, health: DamagedHealth);

            foreach (var rotation in new[]
            {
                new Quaternion(0f, 0f, 0f, 0f),
                new Quaternion(float.NaN, 0f, 0f, 1f),
                new Quaternion(float.PositiveInfinity, 0f, 0f, 1f),
            })
            {
                _batcher.BuildBatches(_binder, rotation);

                Assert.That(_batcher.HealthBarCount, Is.EqualTo(1), "the unit is still there and still hurt");
                var matrix = _batcher.HealthBarMatrices[0];
                AssertAllFinite(_batcher.HealthBarMatrices, $"after rotation {rotation}");
                Assert.That(matrix.m00, Is.EqualTo(PrototypeDiameter * UnitOverlayBatcher.HealthBarWidthInDiameters).Within(1e-4f),
                    "the fallback is an upright bar, not a sheared one");
                Assert.That(matrix.m01, Is.EqualTo(0f).Within(1e-6f));
                Assert.That(matrix.m10, Is.EqualTo(0f).Within(1e-6f));
            }

            // The same rotation, valid this time, has to be the thing that rotates it.
            _batcher.BuildBatches(_binder, Quaternion.Euler(0f, 90f, 0f));
            Assert.That(_batcher.HealthBarMatrices[0].m00, Is.EqualTo(0f).Within(1e-5f),
                "a yaw of 90 degrees turns the bar's width into its Z extent");
        }

        [Test]
        public void NonFiniteHealthFraction_ProducesNoNaNAndOutOfRangeValues()
        {
            var poisoned = SpawnUnit(1);
            poisoned.SetSelected(true);
            poisoned.SnapHealth(DamagedHealth, float.NaN);

            Build();

            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0), "an unknown fraction draws no bar");
            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(1), "and the ring does not depend on it");

            // Infinity does not survive the view's own clamp, and the clamp is
            // Mathf.Clamp01, which takes infinities to the bound but passes NaN
            // straight through. So the batcher's non-finite guard has exactly one
            // shape to catch here, and this asserts which layer caught the other.
            poisoned.SnapHealth(DamagedHealth, float.PositiveInfinity);
            Assert.That(poisoned.HealthFraction, Is.EqualTo(1f),
                "the view absorbs an infinite fraction before the batcher sees it");
            Build();
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(1));
            Assert.That(_batcher.HealthBarProperties[0].x, Is.EqualTo(1f));

            // Over 1 is not poison, it is a stat the server has not caught up with.
            poisoned.SnapHealth(Health * 2, 2f);
            Build();
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(1));
            Assert.That(_batcher.HealthBarProperties[0].x, Is.EqualTo(1f), "clamped, not passed through");

            poisoned.SnapHealth(DamagedHealth, -0.5f);
            Build();
            Assert.That(_batcher.HealthBarProperties[0].x, Is.EqualTo(0f));
        }

        [Test]
        public void BatchStopsAtCapacity_WithoutWritingPastIt()
        {
            CreateMatch(LargeMatchCapacity);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 3), Is.EqualTo(3));
            _batcher = new UnitOverlayBatcher(2);

            SpawnUnit(1).SetSelected(true);
            SpawnUnit(2).SetSelected(true);
            SpawnUnit(3).SetSelected(true);

            Build();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(2));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(2));
            Assert.That(_batcher.ClippedInstanceCount, Is.EqualTo(2), "one ring and one bar did not fit");

            // The written entries stay the contract: nothing half-emitted, nothing
            // from the third unit, and no exception on the way.
            AssertAllFinite(_batcher.SelectionRingMatrices, "with a saturated ring batch");
            AssertAllFinite(_batcher.HealthBarMatrices, "with a saturated bar batch");
        }

        [Test]
        public void Constructor_RejectsUselessCapacities()
        {
            Assert.That(() => new UnitOverlayBatcher(0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new UnitOverlayBatcher(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new UnitOverlayBatcher(UnitOverlayBatcher.MaxCapacity + 1),
                Throws.TypeOf<ArgumentOutOfRangeException>(),
                "more overlays than the replication table can hold units is a misconfiguration, not a buffer");
            Assert.That(new UnitOverlayBatcher().Capacity, Is.EqualTo(UnitOverlayBatcher.DefaultCapacity));
        }

        [Test]
        public void BuildBatches_RequiresABinder()
        {
            Assert.That(
                () => _batcher.BuildBatches(null, DefaultCameraRotation),
                Throws.ArgumentNullException);
        }

        [Test]
        public void Clear_EmptiesTheBatchesWithoutLosingConfiguration()
        {
            SpawnUnit(1).SetSelected(true);
            Build();
            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(1));

            _batcher.AlwaysShowHealthBars = true;
            _batcher.Clear();

            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(0));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(0));
            Assert.That(_batcher.AlwaysShowHealthBars, Is.True, "hiding overlays is not resetting the player's options");

            Build();
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(1));
        }

        // ---------------------------------------------------------------- frame budget

        [Test]
        public void BuildBatches_AtFourHundredUnits_DoesNotAllocate()
        {
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory(),
                "the recorder has to see an allocation before its silence means anything");
            Assert.That(ballast, Is.Not.Null);

            CreateMatch(LargeMatchCapacity);
            Assert.That(_pool.Warmup(UnitKinds.Scout, MatchUnits), Is.EqualTo(MatchUnits));
            for (var entity = 1; entity <= MatchUnits; entity++)
            {
                AddUnit((ulong)entity, health: entity % 2 == 0 ? DamagedHealth : Health);
            }

            Step();
            Assert.That(_binder.BoundViewCount, Is.EqualTo(MatchUnits));

            // Half of them selected, all of them alive: the worst frame the OD-28
            // profile can produce is both batches full at once.
            for (var slot = 0; slot < _binder.SlotCount; slot++)
            {
                if (_binder.TryGetView(slot, out var view) && (view.EntityValue & 1UL) != 0UL)
                {
                    view.SetSelected(true);
                }
            }

            _batcher = new UnitOverlayBatcher(LargeMatchCapacity);
            Assert.That(_batcher.Capacity, Is.GreaterThanOrEqualTo(MatchUnits));

            void Build(int iterations)
            {
                // No assertions and no span reads in here: a boxed Matrix4x4 is an
                // allocation, and the point of the window is that nothing does.
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    _batcher.BuildBatches(_binder, DefaultCameraRotation);
                }
            }

            Build(20);
            Assert.That(_batcher.SelectionRingCount, Is.EqualTo(MatchUnits / 2));
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(MatchUnits),
                "half selected, the other half damaged: every unit earns a bar");

            Assert.That(
                () => Build(200),
                Is.Not.AllocatingGCMemory(),
                "the overlay batch is a per-frame cost at four hundred units, so it must be allocation-free");

            Assert.That(_binder.BoundViewCount, Is.EqualTo(MatchUnits));
            Assert.That(_pool.GrowCount, Is.EqualTo(0), "the measured window must be steady state");
        }

        // ---------------------------------------------------------------- step 3.4 remediation
        //
        // The audit closed one defect per test below, and each of them is written to
        // fail for that defect alone: readiness of the renderer feature (the first
        // Create() used to skip itself), per-chunk instance buffers, ownership of the
        // procedural material and meshes, the colour and rotation inputs a player or a
        // corrupted packet can still reach these buffers with.
        //
        // The meshes and materials the feature builds are native objects that a dropped
        // reference does not free, so these tests count them through
        // Resources.FindObjectsOfTypeAll rather than asking the feature what it thinks
        // it holds — the question the defect is about.

        private const string RingMeshName = "GlobalFront Selection Ring";
        private const string BarMeshName = "GlobalFront Health Bar";
        private const string ProceduralMaterialName = "Unit Overlay";

        /// <summary>
        /// A shader that is always loaded in the editor and definitely has neither
        /// overlay pass: the stand-in for a content author pointing the feature at a
        /// generic material.
        /// </summary>
        private const string ForeignShaderName = "Hidden/InternalErrorShader";

        private static void EnsureOverlayShaderResident()
        {
            Assert.That(
                Shader.Find(UnitOverlayGeometry.ShaderName) == null,
                Is.False,
                $"'{UnitOverlayGeometry.ShaderName}' must be resolvable by name for the procedural resource path to be testable.");
        }

        private static int CountOverlayMeshes()
        {
            var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            var found = 0;
            for (var index = 0; index < meshes.Length; index++)
            {
                var name = meshes[index].name;
                if (name == RingMeshName || name == BarMeshName)
                {
                    found++;
                }
            }

            return found;
        }

        private static Mesh FindOverlayMesh(string name)
        {
            var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            for (var index = 0; index < meshes.Length; index++)
            {
                if (meshes[index].name == name)
                {
                    return meshes[index];
                }
            }

            return null;
        }

        /// <summary>The material the renderer feature built for itself, or null.</summary>
        private static Material FindProceduralOverlayMaterial()
        {
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            for (var index = 0; index < materials.Length; index++)
            {
                var candidate = materials[index];
                if (candidate.name == ProceduralMaterialName && candidate.hideFlags == HideFlags.DontSave)
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Writes the feature's serialized assignment. Through the private field rather
        /// than a <c>SerializedObject</c> on purpose: applying a serialized property runs
        /// <c>OnValidate</c>, which would call <c>Create()</c> behind the test's back and
        /// leave it unable to tell its own rebuild from the pipeline's.
        /// </summary>
        private static void SetOverlayMaterial(UnitOverlayRendererFeature feature, Material material)
        {
            var field = typeof(UnitOverlayRendererFeature)
                .GetField("overlayMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field == null, Is.False, "the feature must still own its serialized material");
            field.SetValue(feature, material);
        }

        [Test]
        public void RendererFeature_Create_WithNullMaterial_BuildsProceduralResourcesAndBecomesReadyWhenAttached()
        {
            EnsureOverlayShaderResident();
            Assert.That(CountOverlayMeshes(), Is.EqualTo(0), "no overlay mesh should exist before a feature is created");

            var feature = ScriptableObject.CreateInstance<UnitOverlayRendererFeature>();
            try
            {
                Assert.That(feature.IsReady, Is.False, "a feature with nothing built yet cannot draw");

                feature.Create();

                Assert.That(feature.Batcher == null, Is.False, "the feature owns the batcher it draws from");

                // P0-1: Create used to test whether it had built the pass rather than
                // whether it had built the resources, and the pass is always there, so the
                // first call — and every later one with no material assigned — took the
                // early exit and the overlays never existed.
                Assert.That(CountOverlayMeshes(), Is.EqualTo(2),
                    "with no material assigned, the first Create() has to build the procedural pair");
                var material = FindProceduralOverlayMaterial();
                Assert.That(material == null, Is.False, "and the material with it");
                Assert.That(material.enableInstancing, Is.True,
                    "URP refuses DrawMeshInstanced for a material that is not instanced");

                Assert.That(feature.IsReady, Is.False, "resources without a match are still nothing to draw");
                feature.Attach(_binder);
                Assert.That(feature.IsReady, Is.True, "resources plus a binder is the whole readiness rule");
                Assert.That(UnitOverlayRendererFeature.Active, Is.SameAs(feature));
            }
            finally
            {
                feature.Dispose();
            }

            Assert.That(feature.IsReady, Is.False, "dispose takes the pass away");
            Assert.That(UnitOverlayRendererFeature.Active, Is.Null, "and uninstalls the feature");
            Assert.That(CountOverlayMeshes(), Is.EqualTo(0),
                "the meshes it built are native objects and must not outlive it");
            Assert.That(FindProceduralOverlayMaterial() == null, Is.True,
                "and so must the material");
        }

        [Test]
        public void RendererFeature_Create_IsIdempotentAndReleasesOldResourcesOnMaterialChange()
        {
            EnsureOverlayShaderResident();
            var feature = ScriptableObject.CreateInstance<UnitOverlayRendererFeature>();
            var callerMaterial = new Material(Shader.Find(UnitOverlayGeometry.ShaderName))
            {
                name = "Caller Overlay Material",
            };
            try
            {
                feature.Create();
                var ringBefore = FindOverlayMesh(RingMeshName);
                var procedural = FindProceduralOverlayMaterial();
                Assert.That(ringBefore == null, Is.False, "the first build produced a ring mesh");
                Assert.That(procedural == null, Is.False, "and its own material");
                Assert.That(CountOverlayMeshes(), Is.EqualTo(2));

                feature.Create();
                feature.Create();

                // Re-running Create is the normal case (script reload, inspector edit), so
                // it must neither rebuild nor strand the previous pair.
                Assert.That(CountOverlayMeshes(), Is.EqualTo(2),
                    "repeating Create with the same assignment must not build a second pair");
                Assert.That(FindOverlayMesh(RingMeshName), Is.SameAs(ringBefore),
                    "and not replace the pair it already has");

                // Now change the assignment to a material somebody else owns. The previous
                // build's objects go with it — including the material the feature created
                // for itself, which is exactly the one it may destroy — and the new ones
                // are built from the caller's material, which is not.
                SetOverlayMaterial(feature, callerMaterial);
                feature.Create();

                Assert.That(ringBefore == null, Is.True,
                    "the ring mesh the superseded build created was destroyed, not just dropped");
                Assert.That(procedural == null, Is.True,
                    "and the material it owned with it");
                Assert.That(CountOverlayMeshes(), Is.EqualTo(2),
                    "and replaced by exactly one new pair");
                Assert.That(callerMaterial == null, Is.False,
                    "an assigned material belongs to the caller: the feature must not destroy it");
                Assert.That(FindProceduralOverlayMaterial() == null, Is.True,
                    "and rebuilding must not quietly create a second one of its own");
            }
            finally
            {
                feature.Dispose();
            }

            Assert.That(callerMaterial == null, Is.False,
                "Dispose drops what it drew with, but never a material it was only handed");
            Assert.That(CountOverlayMeshes(), Is.EqualTo(0), "and it does release the pair it built");
            Assert.That(FindProceduralOverlayMaterial() == null, Is.True,
                "so no overlay material of the feature's own is left for the next test");
            Object.DestroyImmediate(callerMaterial);
        }

        [Test]
        public void Geometry_TryCreateResources_EnablesInstancingOnAssignedMaterial_AndCleansUpOnPassMismatch()
        {
            EnsureOverlayShaderResident();
            var overlayShader = Shader.Find(UnitOverlayGeometry.ShaderName);
            var assigned = new Material(overlayShader) { name = "Test Overlay Material" };
            Mesh ring = null;
            Mesh bar = null;
            var owns = true;
            try
            {
                Assert.That(assigned.enableInstancing, Is.False, "a freshly created material is not instanced");
                Assert.That(
                    UnitOverlayGeometry.TryCreateResources(
                        assigned,
                        out var material,
                        out owns,
                        out ring,
                        out bar,
                        out var ringPassIndex,
                        out var barPassIndex),
                    Is.True,
                    "the overlay shader carries both passes");

                // P1-2: the flag used to be set only on the procedural branch, so a
                // material assigned on the feature reached DrawMeshInstanced uninstanced.
                Assert.That(assigned.enableInstancing, Is.True,
                    "an assigned material is made instanced too, or nothing can draw with it");
                Assert.That(owns, Is.False, "handed in, so handed out: the factory does not own it");
                Assert.That(material, Is.SameAs(assigned));
                Assert.That(ringPassIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(barPassIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(ring == null, Is.False);
                Assert.That(bar == null, Is.False);
            }
            finally
            {
                if (ring != null)
                {
                    Object.DestroyImmediate(ring);
                }

                if (bar != null)
                {
                    Object.DestroyImmediate(bar);
                }
            }

            // The mismatch case: both out indices report unusable, nothing is emitted, and
            // the caller's material survives the failure.
            Assert.That(Shader.Find(ForeignShaderName) == null, Is.False, "the foreign shader must be resolvable");
            var foreign = new Material(Shader.Find(ForeignShaderName));
            Assert.That(
                UnitOverlayGeometry.TryCreateResources(
                    foreign,
                    out var failedMaterial,
                    out var failedOwns,
                    out var failedRing,
                    out var failedBar,
                    out var failedRingPass,
                    out var failedBarPass),
                Is.False,
                "a material without the overlay passes cannot draw overlays");
            Assert.That(failedMaterial == null, Is.True, "and says so with empty outputs");
            Assert.That(failedOwns, Is.False);
            Assert.That(failedRing == null, Is.True);
            Assert.That(failedBar == null, Is.True);
            Assert.That(failedRingPass, Is.EqualTo(-1));
            Assert.That(failedBarPass, Is.EqualTo(-1));
            Assert.That(foreign == null, Is.False, "the caller's material is not the factory's to destroy");

            Object.DestroyImmediate(foreign);
            Object.DestroyImmediate(assigned);
        }

        [Test]
        public void RenderPass_MultiChunk_HasIsolatedChunkBuffersUpToMaxCapacity()
        {
            CreateMatch(LargeMatchCapacity);
            Assert.That(_pool.Warmup(UnitKinds.Scout, MatchUnits), Is.EqualTo(MatchUnits));
            for (var entity = 1; entity <= MatchUnits; entity++)
            {
                // Distinct in X: the whole point is telling the two chunks' instances apart.
                AddUnit((ulong)entity, posX: entity * 10, health: DamagedHealth);
            }

            Step();
            _batcher = new UnitOverlayBatcher(LargeMatchCapacity);
            Build();

            Assert.That(_batcher.HealthBarCount, Is.EqualTo(MatchUnits));
            Assert.That(
                UnitOverlayRenderPass.GetChunkCount(MatchUnits),
                Is.EqualTo(2),
                "four hundred bars is a full first chunk and a part second one");
            Assert.That(
                UnitOverlayRenderPass.GetChunkCount(UnitOverlayBatcher.MaxCapacity),
                Is.EqualTo(UnitOverlayRenderPass.MaxChunksPerBatch),
                "the declared ceiling is the one a maximally sized batcher reaches");

            var pass = new UnitOverlayRenderPass(_batcher);
            Assert.That(
                pass.BarChunks.Length,
                Is.EqualTo(UnitOverlayRenderPass.GetChunkCount(_batcher.Capacity)),
                "a batcher cannot emit past its capacity, so that is the exact number of chunks needed");
            Assert.That(pass.RingChunks.Length, Is.EqualTo(pass.BarChunks.Length));

            var first = pass.BarChunks[0];
            var second = pass.BarChunks[1];
            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(ReferenceEquals(first.Matrices, second.Matrices), Is.False, "P1-1: one array for two draws");
            Assert.That(ReferenceEquals(first.Block, second.Block), Is.False, "P1-1: one block for two draws");

            var sourceMatrices = _batcher.HealthBarMatrixBuffer;
            var sourceProperties = _batcher.HealthBarPropertyBuffer;
            Assert.That(
                UnitOverlayRenderPass.PrepareChunks(
                    sourceMatrices,
                    sourceProperties,
                    MatchUnits,
                    UnitOverlayGeometry.HealthBarParamsProperty,
                    pass.BarChunks),
                Is.EqualTo(2));

            // A command buffer resolves a MaterialPropertyBlock at playback, so the state
            // that matters is what each chunk holds *after the whole batch* has been
            // prepared: the first draw must still read the first 250 instances.
            Assert.That(first.Count, Is.EqualTo(UnitOverlayBatcher.MaxInstancesPerDraw));
            Assert.That(second.Count, Is.EqualTo(MatchUnits - UnitOverlayBatcher.MaxInstancesPerDraw));
            Assert.That(first.Matrices[0].m03, Is.EqualTo(sourceMatrices[0].m03));
            Assert.That(first.Matrices[249].m03, Is.EqualTo(sourceMatrices[249].m03));
            Assert.That(second.Matrices[0].m03, Is.EqualTo(sourceMatrices[250].m03));
            Assert.That(second.Matrices[149].m03, Is.EqualTo(sourceMatrices[399].m03));

            // The block is what the recorded draw resolves at playback, so read it back
            // rather than trusting the copy. GetVectorArray fills a list the caller owns
            // and sized, so it has to be primed first.
            var probe = new List<Vector4>();
            for (var index = 0; index < UnitOverlayBatcher.MaxInstancesPerDraw; index++)
            {
                probe.Add(Vector4.zero);
            }

            first.Block.GetVectorArray(UnitOverlayGeometry.HealthBarParamsProperty, probe);
            Assert.That(probe[0].x, Is.EqualTo(sourceProperties[0].x));
            Assert.That(probe[249].x, Is.EqualTo(sourceProperties[249].x));
            second.Block.GetVectorArray(UnitOverlayGeometry.HealthBarParamsProperty, probe);
            Assert.That(probe[0].x, Is.EqualTo(sourceProperties[250].x), "the second chunk carries its own data");
            Assert.That(first.Matrices[0].m03, Is.Not.EqualTo(sourceMatrices[250].m03),
                "and neither shares its scratch with the other");
        }

        [Test]
        public void Batcher_NonUnitCameraQuaternion_NormalizesRotationBeforeTrs()
        {
            SpawnUnit(1, health: DamagedHealth);
            var inflated = new Quaternion(2f, 0f, 0f, 2f);
            Assert.That(
                inflated.x * inflated.x + inflated.y * inflated.y + inflated.z * inflated.z + inflated.w * inflated.w,
                Is.EqualTo(8f),
                "a valid 90-degree turn that is not of unit length");

            _batcher.BuildBatches(_binder, inflated);
            Assert.That(_batcher.HealthBarCount, Is.EqualTo(1), "the unit is still there and still hurt");
            var bar = _batcher.HealthBarMatrices[0];

            _batcher.BuildBatches(_binder, inflated.normalized);
            var reference = _batcher.HealthBarMatrices[0];

            // P3-1: Matrix4x4.TRS does not normalize its quaternion, so the length used
            // to ride into the instance matrix as extra scale — a bar thicker than
            // HealthBarThicknessMetres says, by the square of the quaternion's length.
            AssertAllFinite(bar, "a non-unit camera rotation");
            Assert.That(
                bar.MultiplyVector(Vector3.up).magnitude,
                Is.EqualTo(UnitOverlayBatcher.HealthBarThicknessMetres).Within(1e-5f),
                "the thickness the batcher configured, not a multiple of it");
            Assert.That(
                bar.MultiplyVector(Vector3.right).magnitude,
                Is.EqualTo(PrototypeDiameter * UnitOverlayBatcher.HealthBarWidthInDiameters).Within(1e-5f));
            Assert.That(
                Vector3.Distance(
                    bar.MultiplyVector(Vector3.forward).normalized,
                    (inflated.normalized * Vector3.forward).normalized),
                Is.LessThan(1e-4f),
                "and it still turns the bar the same way the quaternion turns the camera");

            Assert.That(bar.m00, Is.EqualTo(reference.m00).Within(1e-5f));
            Assert.That(bar.m11, Is.EqualTo(reference.m11).Within(1e-5f));
            Assert.That(bar.m22, Is.EqualTo(reference.m22).Within(1e-5f));
            Assert.That(bar.m12, Is.EqualTo(reference.m12).Within(1e-5f));
            Assert.That(bar.m21, Is.EqualTo(reference.m21).Within(1e-5f));
            Assert.That(bar.m13, Is.EqualTo(reference.m13).Within(1e-6f), "the billboard height is not scaled either");
        }

        [Test]
        public void Batcher_NonFiniteColors_FallbackToFiniteDefaults()
        {
            SpawnUnit(1).SetSelected(true);
            SpawnUnit(2, health: DamagedHealth);

            foreach (var poison in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                // One poisoned channel in each of the three configurable colours, in a
                // different slot each time so the guard cannot pass on a lucky field.
                _batcher.SelectionRingColor = new Color(poison, 1f, 1f, 1f);
                _batcher.LowHealthBarColor = new Color(0f, poison, 0f, 1f);
                _batcher.FullHealthBarColor = new Color(1f, 1f, 1f, poison);

                Assert.That(_batcher.SelectionRingColor.r, Is.EqualTo(UnitOverlayBatcher.DefaultSelectionRingColor.r),
                    "a non-finite channel is refused, not half applied");
                Assert.That(_batcher.LowHealthBarColor.g, Is.EqualTo(UnitOverlayBatcher.DefaultLowHealthBarColor.g));
                Assert.That(_batcher.FullHealthBarColor.a, Is.EqualTo(UnitOverlayBatcher.DefaultFullHealthBarColor.a));

                Build();

                Assert.That(_batcher.SelectionRingCount, Is.EqualTo(1));
                Assert.That(_batcher.HealthBarCount, Is.EqualTo(2));
                AssertAllFinite(_batcher.SelectionRingProperties, $"after a poisoned ring colour ({poison})");
                AssertAllFinite(_batcher.HealthBarProperties, $"after a poisoned bar colour ({poison})");
            }

            // Finite channels are still not free to be anything: the shader alpha-blends
            // what it is given, and negative zero would reach the buffer as a sign bit.
            _batcher.SelectionRingColor = new Color(4f, -0.0f, 0.5f, 9f);
            Assert.That(_batcher.SelectionRingColor.r, Is.EqualTo(1f), "over-range clamps to the bound");
            Assert.That(BitConverter.SingleToInt32Bits(_batcher.SelectionRingColor.g), Is.EqualTo(0),
                "and negative zero is stored as positive zero");
            Assert.That(_batcher.SelectionRingColor.b, Is.EqualTo(0.5f), "a channel in range is left alone");
            Assert.That(BitConverter.SingleToInt32Bits(_batcher.SelectionRingColor.a), Is.EqualTo(0x3f800000));

            Build();
            AssertAllFinite(_batcher.SelectionRingProperties, "after an out-of-range ring colour");
        }

        [Test]
        public void Batcher_NegativeZeroHealthFraction_CanonicalizesToPositiveZero()
        {
            var view = SpawnUnit(1);
            view.SetSelected(true);
            view.SnapHealth(DamagedHealth, -0.0f);

            // The precondition the defect needs: Mathf.Clamp01 tests `value < 0`, which
            // negative zero fails, so the view hands the batcher a -0.0 fraction. Were it
            // already canonical here there would be nothing for the batcher to fix, and
            // the assertion below would be passing for the wrong reason.
            Assert.That(
                BitConverter.SingleToInt32Bits(view.HealthFraction),
                Is.EqualTo(unchecked((int)0x80000000)),
                "the view must still be carrying negative zero for this to test anything");

            Build();

            Assert.That(_batcher.HealthBarCount, Is.EqualTo(1));
            var properties = _batcher.HealthBarProperties;
            Assert.That(
                BitConverter.SingleToInt32Bits(properties[0].x),
                Is.EqualTo(0),
                "P3-2: the instance buffer gets +0.0, never the sign-bit form of the same number");
        }
    }
}
