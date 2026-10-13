using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Client.UI;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using UnityEngine.UI;
using EntityId = GlobalFront.Core.Model.EntityId;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;
using Object = UnityEngine.Object;

namespace GlobalFront.Tests.EditMode.Client.Presentation
{
    /// <summary>
    /// Phase 3, step 3.5: selection, screen-space drag box and RTS command issuing
    /// (<see cref="UnitSelectionController"/>, <see cref="UnitPickMath"/>,
    /// <see cref="UnitCommandChannelSink"/>, ADR-012/OD-24).
    ///
    /// Three things these tests exist to defend.
    ///
    /// <b>Picking must work without physics.</b> <see cref="UnitView"/> carries no
    /// collider by design, so every assertion here is made through the same slot table
    /// the overlays are built from, and the fixtures assert that no bound view grew a
    /// collider — a test that quietly passed because something added one would prove
    /// nothing about the picking path.
    ///
    /// <b>Selection must not outlive the unit.</b> Replication slots are recycled, so
    /// the dangerous case is not a stale ring but a ring that moves onto whoever takes
    /// the dead unit's slot. Each lifecycle test names the entity it expects to be
    /// dropped and the entity it expects to stay unselected.
    ///
    /// <b>The click path must be the only thing that is not free.</b> A steady-state
    /// frame over 400 units has to allocate nothing; the commands a click produces are
    /// allowed to, and the budget is stated that way rather than uniformly.
    ///
    /// Screen projection is driven by <see cref="TestProjector"/>, an exact invertible
    /// mapping, so pixel/position assertions are equalities rather than tolerances and
    /// the behind-the-camera rule can be isolated. <see cref="RealCameraProjection"/>
    /// covers the <see cref="CameraUnitPointerProjector"/> path itself.
    /// </summary>
    [TestFixture]
    public sealed class UnitSelectionAndCommandTests
    {
        private const int SlotCapacity = 512;
        private const double TickSeconds = 0.05;
        private const double FrameSeconds = 1.0 / 60.0;
        private const int FullHealth = UnitCatalog.PrototypeMaximumHealth;

        private const byte Local = 1;
        private const byte Enemy = 2;

        // A 2 m grid keeps neighbours outside each other's pick radius, so "which unit
        // did the click land on" never depends on the tie-break.
        private const int Ax = 1000;
        private const int Bx = 3000;
        private const int Cx = 5000;
        private const int Dx = 7000;
        private const int Ex = 9000;
        private const int Fx = 11000;
        private const int Gx = 13000;
        private const int RowZ = 2000;

        /// <summary>
        /// World x of the fixture unit that is inside the marquee geometrically but
        /// behind the lens. Past <see cref="TestProjector.BehindLensX"/> it projects to
        /// a negative depth, and its pixel still lands inside the box.
        /// </summary>
        private const int BehindX = -25000;

        private Transform _root;
        private UnitViewPool _pool;
        private ClientReplicationWorld _world;
        private UnitViewTickBuffer _buffer;
        private UnitViewBinder _binder;
        private UnitSelectionController _selection;
        private TestProjector _projector;
        private RecordingCommandSink _sink;
        private ulong _tick;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("UnitSelectionAndCommandTests_Root").transform;
            _pool = new UnitViewPool(_root);
            _world = new ClientReplicationWorld(SlotCapacity);
            _buffer = new UnitViewTickBuffer(SlotCapacity, TickSeconds);
            _binder = new UnitViewBinder(_pool, _world, _buffer);
            _selection = new UnitSelectionController(
                _binder,
                _world,
                new PlayerId(Local));
            _projector = new TestProjector();
            _sink = new RecordingCommandSink();
            _tick = 10;
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

        // -------------------------------------------------------------------- fixtures

        private static DeltaAddRecord Add(
            ulong entity,
            byte owner,
            int xMillimetres,
            int zMillimetres,
            byte kind)
        {
            return new DeltaAddRecord(
                new EntityId(entity),
                new PlayerId(owner),
                new WorldPointMm(xMillimetres, zMillimetres),
                FullHealth,
                false,
                new WorldPointMm(0, 0),
                new EntityId(0),
                false,
                kind);
        }

        /// <summary>
        /// Puts one unit on screen at an exact millimetre position and returns the slot
        /// it landed in. The pool is warmed one view at a time so no test depends on
        /// OD-26's banned combat growth.
        /// </summary>
        private int Spawn(ulong entity, int xMillimetres, int zMillimetres, byte owner = Local, byte kind = UnitKinds.Scout)
        {
            Assert.That(
                _world.ApplyAdd(Add(entity, owner, xMillimetres, zMillimetres, kind)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            _pool.Warmup(kind, _pool.OwnedCount(kind) + 1);
            BindFrame();
            return SlotOf(entity);
        }

        private void BindFrame()
        {
            _tick += 2;
            _buffer.CaptureTick(_tick, _world);
            _binder.Render(FrameSeconds);
        }

        /// <summary>
        /// Delivers a lethal health update and lets presentation follow it, which is how
        /// a casualty really arrives: the table reports zero before the view stops
        /// drawing a corpse.
        /// </summary>
        private void Kill(ulong entity)
        {
            Assert.That(
                _world.ApplyUpdate(new DeltaUpdateRecord(
                    new EntityId(entity),
                    (byte)UnitDirtyMask.Health,
                    new PlayerId(Local),
                    new WorldPointMm(0, 0),
                    0,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            BindFrame();
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

        private UnitView ViewAt(int slot)
        {
            Assert.That(_binder.TryGetView(slot, out var view), Is.True, $"slot {slot} has no view");
            return view;
        }

        private UnitView ViewOf(ulong entity) => ViewAt(SlotOf(entity));

        /// <summary>
        /// Builds the batches the player would see this frame. Selection rings are the
        /// observable half of the selection: a unit the controller believes is selected
        /// but that draws no ring is selected only in the code that thinks so.
        /// </summary>
        private UnitOverlayBatcher BuildOverlay()
        {
            var batcher = new UnitOverlayBatcher(64);
            batcher.BuildBatches(_binder, Quaternion.identity);
            return batcher;
        }

        /// <summary>
        /// Five live friendly units (three scouts, a tank, and a scout the marquee
        /// reaches), one enemy, one casualty and one unit whose pixel sits inside the
        /// box while the unit itself is behind the lens. Each is excluded for exactly
        /// one reason, so a regression in any single rule shows up as a wrong count.
        /// </summary>
        private void SpawnCast()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            Spawn(3, Cx, RowZ);
            Spawn(4, Dx, RowZ, kind: UnitKinds.Tank);
            Spawn(5, Ex, RowZ, Enemy);
            Spawn(6, Fx, RowZ);
            Spawn(7, BehindX, 0);
            Kill(6);
        }

        private static Vector2 ScreenOfWorld(int xMillimetres, int zMillimetres) =>
            TestProjector.ScreenForMetres(xMillimetres * 0.001f, zMillimetres * 0.001f);

        private void AssertNoCollidersAnywhere()
        {
            for (var slot = 0; slot < _binder.SlotCount; slot++)
            {
                if (_binder.TryGetView(slot, out var view))
                {
                    Assert.That(
                        view.GetComponent<Collider>(),
                        Is.Null,
                        $"OD-24: picking must not depend on colliders, and slot {slot} grew one");
                }
            }
        }

        // -------------------------------------------------------------------- single click

        [Test]
        public void Click_FriendlyUnit_SelectsOnlyItAndTheOverlayDrawsOneRing()
        {
            SpawnCast();

            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), false, 0.0, _projector);

            Assert.That(_selection.SelectedCount, Is.EqualTo(1));
            Assert.That(_selection.GetSelectedEntity(0), Is.EqualTo(new EntityId(1)));
            Assert.That(ViewOf(1).IsSelected, Is.True);
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(1));
            AssertNoCollidersAnywhere();
        }

        [Test]
        public void Click_EmptyGround_ClearsTheSelectionAndTheRing()
        {
            SpawnCast();
            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), false, 0.0, _projector);
            Assert.That(_selection.SelectedCount, Is.EqualTo(1));

            var empty = TestProjector.ScreenForMetres(-30f, -20f);
            _selection.OnPrimaryClick(empty, false, 1.0, _projector);

            Assert.That(_selection.SelectedCount, Is.EqualTo(0));
            Assert.That(ViewOf(1).IsSelected, Is.False, "the ring must not survive the selection");
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(0));
        }

        [Test]
        public void ShiftClick_AddsAUnitAndShiftClickAgain_TogglesItOff()
        {
            SpawnCast();

            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector);
            _selection.OnPrimaryClick(ScreenOfWorld(Bx, RowZ), additive: true, 0.1, _projector);

            Assert.That(_selection.SelectedCount, Is.EqualTo(2));
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True);
            Assert.That(_selection.IsSelected(new EntityId(2)), Is.True);
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(2));

            _selection.OnPrimaryClick(ScreenOfWorld(Bx, RowZ), additive: true, 0.2, _projector);

            Assert.That(_selection.SelectedCount, Is.EqualTo(1));
            Assert.That(_selection.IsSelected(new EntityId(2)), Is.False);
            Assert.That(ViewOf(2).IsSelected, Is.False);
        }

        [Test]
        public void ShiftClick_OnEmptyGround_KeepsTheSelection()
        {
            SpawnCast();
            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector);

            _selection.OnPrimaryClick(TestProjector.ScreenForMetres(-30f, -20f), additive: true, 0.1, _projector);

            Assert.That(_selection.SelectedCount, Is.EqualTo(1), "shift is the player asking to keep what they have");
        }

        [Test]
        public void Click_EnemyUnit_SelectsNothingAndClears()
        {
            SpawnCast();
            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector);

            _selection.OnPrimaryClick(ScreenOfWorld(Ex, RowZ), additive: false, 0.1, _projector);

            Assert.That(_selection.SelectedCount, Is.EqualTo(0), "another player's units are never selectable");
            Assert.That(ViewOf(5).IsSelected, Is.False);
        }

        [Test]
        public void DoubleClick_FriendlyUnit_SelectsEveryFriendlyUnitOfThatKindOnScreen()
        {
            SpawnCast();

            var scout = ScreenOfWorld(Ax, RowZ);
            _selection.OnPrimaryClick(scout, additive: false, 0.0, _projector);
            Assert.That(_selection.SelectedCount, Is.EqualTo(1));

            _selection.OnPrimaryClick(scout, additive: false, 0.0 + UnitSelectionController.DefaultDoubleClickWindowSeconds - 0.05, _projector);

            Assert.That(_selection.SelectedCount, Is.EqualTo(3), "the three local scouts");
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True);
            Assert.That(_selection.IsSelected(new EntityId(2)), Is.True);
            Assert.That(_selection.IsSelected(new EntityId(3)), Is.True);
            Assert.That(_selection.IsSelected(new EntityId(4)), Is.False, "a different archetype is a different group");
            Assert.That(_selection.IsSelected(new EntityId(5)), Is.False, "an enemy of the same archetype is not the player's group");
            Assert.That(_selection.IsSelected(new EntityId(7)), Is.False, "behind the lens is not on screen");
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(3));
        }

        [Test]
        public void DoubleClick_AfterTheWindow_SelectsASingleUnit()
        {
            SpawnCast();
            var scout = ScreenOfWorld(Ax, RowZ);

            _selection.OnPrimaryClick(scout, additive: false, 0.0, _projector);
            _selection.OnPrimaryClick(ScreenOfWorld(Bx, RowZ), additive: false, 0.1, _projector);
            _selection.OnPrimaryClick(scout, additive: false, 0.1 + UnitSelectionController.DefaultDoubleClickWindowSeconds + 0.05, _projector);

            Assert.That(_selection.SelectedCount, Is.EqualTo(1));
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True);
            Assert.That(_selection.IsSelected(new EntityId(2)), Is.False, "the intervening click broke the pair");
        }

        [Test]
        public void Click_PicksTheClosestUnitRatherThanTheFirstSlotScanned()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);

            // 1.05 m from unit 1 and 0.95 m from unit 2, so a scan that kept the first
            // hit — or one that stopped at the first unit inside the radius — would
            // answer unit 1.
            _selection.OnPrimaryClick(TestProjector.ScreenForMetres(2.05f, 2f), additive: false, 0.0, _projector);
            Assert.That(_selection.SelectedCount, Is.EqualTo(1));
            Assert.That(_selection.GetSelectedEntity(0), Is.EqualTo(new EntityId(2)));
        }

        [Test]
        public void Click_EquidistantUnits_IsDecidedByEntityIdNotSlotOrder()
        {
            // Two units presented at the same spot — the degenerate case a stack of
            // models in one formation slot really does produce. Unit 10 is taken first
            // and holds the lower slot, unit 3 is the lower identity, so slot order and
            // identity order disagree, which is the only way to observe the tie-break.
            Spawn(10, Ax, RowZ);
            Spawn(3, Ax, RowZ);
            Assert.That(SlotOf(10), Is.LessThan(SlotOf(3)));

            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector);

            Assert.That(_selection.SelectedCount, Is.EqualTo(1));
            Assert.That(_selection.GetSelectedEntity(0), Is.EqualTo(new EntityId(3)), "deterministic across clients");
        }

        // -------------------------------------------------------------------- drag box

        [Test]
        public void Drag_InsideTheDeadzone_ResolvesAsASingleClick()
        {
            SpawnCast();
            var onFirst = ScreenOfWorld(Ax, RowZ);

            _selection.BeginDrag(onFirst);
            _selection.UpdatePointer(onFirst + new Vector2(2f, 2f), _projector);

            Assert.That(_selection.IsDragging, Is.False, "2 px is a shaky hand, not a marquee");

            var gesture = _selection.ResolveRelease(onFirst + new Vector2(2f, 2f), additive: false, 0.0, _projector);

            Assert.That(gesture, Is.EqualTo(SelectionGesture.Click));
            Assert.That(_selection.SelectedCount, Is.EqualTo(1));
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True);
        }

        [Test]
        public void Drag_PastTheDeadzone_SelectsTheFriendlyUnitsInsideIt()
        {
            SpawnCast();
            var start = TestProjector.ScreenForMetres(-30f, -20f);
            var end = ScreenOfWorld(Gx, RowZ) + new Vector2(10f, 10f);

            _selection.BeginDrag(start);
            _selection.UpdatePointer(start, _projector);
            Assert.That(_selection.IsDragging, Is.False);

            _selection.UpdatePointer(end, _projector);
            Assert.That(_selection.IsDragging, Is.True, "the HUD needs the state to draw the box");
            Assert.That(_selection.ScreenRectPx.xMin, Is.LessThan(_selection.ScreenRectPx.xMax));
            Assert.That(_selection.ScreenRectPx.yMin, Is.LessThan(_selection.ScreenRectPx.yMax));

            Assert.That(
                _selection.ResolveRelease(end, additive: false, 0.0, _projector),
                Is.EqualTo(SelectionGesture.Marquee));

            Assert.That(_selection.SelectedCount, Is.EqualTo(4), "units 1-4; the enemy, the casualty and the unit behind the lens are not");
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True);
            Assert.That(_selection.IsSelected(new EntityId(4)), Is.True);
            Assert.That(_selection.IsSelected(new EntityId(5)), Is.False, "box selection takes only the player's own units");
            Assert.That(_selection.IsSelected(new EntityId(6)), Is.False, "a casualty cannot be ordered");
            Assert.That(_selection.IsSelected(new EntityId(7)), Is.False, "screen position inside the box is not enough");
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(4));
        }

        [Test]
        public void Drag_BehindLensUnit_IsInsideTheBoxButStillExcluded()
        {
            // Proves the z test is what excludes unit 7, not the rectangle: without the
            // depth check the marquee would select a unit the player cannot see.
            Spawn(7, BehindX, 0);
            var projected = _projector.ProjectToScreen(new Vector3(BehindX * 0.001f, 0f, 0f));
            Assert.That(projected.z, Is.LessThan(0f));

            var rect = new Rect(100f, 100f, 600f, 400f);
            Assert.That(
                UnitPickMath.IsInsideScreenRect(projected, rect.xMin, rect.yMin, rect.xMax, rect.yMax),
                Is.True,
                "the fixture is meaningless unless the unit's pixel is inside the box");

            Assert.That(_selection.SelectInsideScreenRect(rect, false, _projector), Is.EqualTo(0));
        }

        [Test]
        public void ShiftDrag_AddsToTheSelectionInsteadOfReplacingIt()
        {
            SpawnCast();
            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector);
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True);

            // Deliberately excludes unit 1 while including the enemy: an additive box
            // has to both keep what the player already had and still refuse units they
            // do not own.
            _selection.SelectInsideScreenRect(new Rect(420f, 100f, 80f, 230f), additive: true, _projector);

            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True, "a pre-existing selection survives an additive box");
            Assert.That(_selection.SelectedCount, Is.EqualTo(4), "unit 1 plus units 2-4; the enemy inside the box is not the player's");
            Assert.That(_selection.IsSelected(new EntityId(5)), Is.False);
        }

        [Test]
        public void Drag_ClearedByASubsequentEmptyGroundClick()
        {
            SpawnCast();
            _selection.SelectInsideScreenRect(
                new Rect(100f, 100f, 600f, 400f),
                additive: false,
                _projector);
            Assert.That(_selection.SelectedCount, Is.GreaterThan(0));

            _selection.OnPrimaryClick(TestProjector.ScreenForMetres(-60f, -60f), additive: false, 5.0, _projector);
            Assert.That(_selection.SelectedCount, Is.EqualTo(0));
        }

        // -------------------------------------------------------------------- lifecycle

        [Test]
        public void SelectedUnitDeath_IsPrunedAndItsRingGoesAway()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector);
            _selection.OnPrimaryClick(ScreenOfWorld(Bx, RowZ), additive: true, 0.1, _projector);
            Assert.That(_selection.SelectedCount, Is.EqualTo(2));

            Kill(1);

            Assert.That(_selection.PruneStaleSelection(), Is.EqualTo(1));
            Assert.That(_selection.SelectedCount, Is.EqualTo(1));
            Assert.That(_selection.GetSelectedEntity(0), Is.EqualTo(new EntityId(2)));
            Assert.That(ViewOf(2).IsSelected, Is.True, "the survivor keeps its ring");
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(1));
        }

        [Test]
        public void RecycledSlot_DoesNotInheritTheDeadUnitsSelection()
        {
            var slot = Spawn(1, Ax, RowZ);
            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector);
            Assert.That(_selection.SelectedCount, Is.EqualTo(1));

            Assert.That(
                _world.ApplyRemove(new DeltaRemoveRecord(new EntityId(1), DeltaRemoveCause.Destroyed)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(Spawn(50, Bx, RowZ), Is.EqualTo(slot), "the freed slot was recycled");

            Assert.That(ViewAt(slot).IsSelected, Is.False, "a fresh unit never inherits a ring");
            Assert.That(_selection.PruneStaleSelection(), Is.EqualTo(1));
            Assert.That(_selection.SelectedCount, Is.EqualTo(0));
            Assert.That(ViewAt(slot).IsSelected, Is.False, "pruning must not have switched the new unit on by mistake");
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(0));
        }

        [Test]
        public void UnitRemovedFromTheWorld_IsPrunedWithoutThrowing()
        {
            var slot = Spawn(1, Ax, RowZ);
            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector);

            Assert.That(
                _world.ApplyRemove(new DeltaRemoveRecord(new EntityId(1), DeltaRemoveCause.FogOfWarHidden)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            Assert.That(_selection.PruneStaleSelection(), Is.EqualTo(1));
            Assert.That(_selection.SelectedCount, Is.EqualTo(0));
            Assert.That(ViewAt(slot).IsSelected, Is.False);
        }

        [Test]
        public void Prune_IsIdempotentAndLeavesOrderingCanonical()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            Spawn(3, Cx, RowZ);
            _selection.AddSelected(new EntityId(3), SlotOf(3));
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));

            Assert.That(_selection.SelectedCount, Is.EqualTo(3), "insertion order does not decide the order issued to the server");
            Assert.That(_selection.GetSelectedEntity(0).Value, Is.EqualTo(1UL));
            Assert.That(_selection.GetSelectedEntity(2).Value, Is.EqualTo(3UL));

            Kill(2);
            Assert.That(_selection.PruneStaleSelection(), Is.EqualTo(1));
            Assert.That(_selection.PruneStaleSelection(), Is.EqualTo(0), "a second pass has nothing left to drop");
            Assert.That(_selection.GetSelectedEntity(0).Value, Is.EqualTo(1UL));
            Assert.That(_selection.GetSelectedEntity(1).Value, Is.EqualTo(3UL));
        }

        [Test]
        public void AddSelected_WithASlotPresentingADifferentUnit_IsRefused()
        {
            var slotOfOne = Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);

            _selection.AddSelected(new EntityId(99), slotOfOne);

            Assert.That(_selection.SelectedCount, Is.EqualTo(0), "entity 99 is not presented by that slot");
            Assert.That(ViewOf(1).IsSelected, Is.False, "and the unit that is must not take on its ring");
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(0));

            _selection.AddSelected(new EntityId(1), slotOfOne);
            Assert.That(_selection.SelectedCount, Is.EqualTo(1), "the same slot does accept its own unit");
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(1));
        }

        [Test]
        public void UnboundSelectedView_IsPrunedWhenThePoolTakesItBack()
        {
            var slot = Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), slot);

            _binder.ReleaseAllViews();

            Assert.That(_selection.PruneStaleSelection(), Is.EqualTo(1));
            Assert.That(_selection.SelectedCount, Is.EqualTo(0));
        }

        // -------------------------------------------------------------------- commands

        [Test]
        public void RightClickOnGround_IssuesAMoveWithCanonicalEntitiesAndMillimetreDestination()
        {
            SpawnCast();
            Select(1, 2, 3);

            // 1 m = 1000 mm, exactly, on both axes.
            Assert.That(
                _selection.TryIssueCommandAtPointer(
                    TestProjector.ScreenForMetres(4f, 7f),
                    _projector,
                    requestedTick: 500,
                    _sink),
                Is.True);

            var issued = _sink.Only;
            Assert.That(issued.Header.Type, Is.EqualTo(GameCommandType.Move));
            Assert.That(issued.Header.Player, Is.EqualTo(new PlayerId(Local)));
            Assert.That(issued.Header.RequestedTick, Is.EqualTo(500UL));
            Assert.That(issued.Destination, Is.EqualTo(new WorldPointMm(4000, 7000)));
            Assert.That(issued.Entities, Is.EqualTo(new EntityId[]
            {
                new EntityId(1), new EntityId(2), new EntityId(3),
            }).AsCollection, "canonical ascending order");
            Assert.That(issued.Formation.IsValid, Is.True, "a move the server would reject is a bug, not a fallback");
        }

        [Test]
        public void RightClickOnEnemy_IssuesAnAttackOnThatTarget()
        {
            SpawnCast();
            Select(1, 2);

            Assert.That(
                _selection.TryIssueCommandAtPointer(ScreenOfWorld(Ex, RowZ), _projector, 501, _sink),
                Is.True);

            var issued = _sink.Only;
            Assert.That(issued.Header.Type, Is.EqualTo(GameCommandType.Attack));
            Assert.That(issued.Target, Is.EqualTo(new EntityId(5)));
            Assert.That(issued.Entities.Length, Is.EqualTo(2));
        }

        [Test]
        public void RightClickOnFriendlyUnit_IssuesAMovePastIt()
        {
            SpawnCast();
            Select(1);

            Assert.That(
                _selection.TryIssueCommandAtPointer(ScreenOfWorld(Bx, RowZ), _projector, 502, _sink),
                Is.True);

            var issued = _sink.Only;
            Assert.That(issued.Header.Type, Is.EqualTo(GameCommandType.Move));
            Assert.That(issued.Destination, Is.EqualTo(new WorldPointMm(Bx, RowZ)));
        }

        [Test]
        public void RightClickOnDeadEnemy_FallsBackToMovingThere()
        {
            SpawnCast();
            Select(1);
            Kill(5);

            Assert.That(
                _selection.TryIssueCommandAtPointer(ScreenOfWorld(Ex, RowZ), _projector, 503, _sink),
                Is.True);
            Assert.That(_sink.Only.Header.Type, Is.EqualTo(GameCommandType.Move), "a corpse is not a target");
        }

        [Test]
        public void StopWithASelection_IssuesAStopForEverySelectedUnit()
        {
            SpawnCast();
            Select(2, 3);

            Assert.That(_selection.IssueStop(504, _sink), Is.True);

            var issued = _sink.Only;
            Assert.That(issued.Header.Type, Is.EqualTo(GameCommandType.Stop));
            Assert.That(issued.Entities, Is.EqualTo(new[] { new EntityId(2), new EntityId(3) }).AsCollection);
        }

        [Test]
        public void Commands_WithAnEmptySelection_IssueNothing()
        {
            SpawnCast();

            Assert.That(_selection.TryIssueCommandAtPointer(ScreenOfWorld(Ax, RowZ), _projector, 600, _sink), Is.False);
            Assert.That(_selection.IssueStop(601, _sink), Is.False);
            Assert.That(_selection.IssueMove(new WorldPointMm(1000, 1000), 602, _sink), Is.False);
            Assert.That(_sink.Count, Is.EqualTo(0), "an RTS that answers a click on nothing with a command lies about it");
        }

        [Test]
        public void CommandsWithNoLocalPlayerIssuedNothing()
        {
            SpawnCast();
            Select(1);
            _selection.LocalPlayerId = new PlayerId(0);

            Assert.That(_selection.IssueStop(603, _sink), Is.False);
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void SelectionBiggerThanTheCommandCeiling_IsBatchedInCanonicalOrder()
        {
            const int Units = 120;
            for (var index = 0; index < Units; index++)
            {
                Spawn(
                    (ulong)(index + 1),
                    MetresToMillimetres(-11f + (index % 12) * 2f),
                    MetresToMillimetres(-9f + (index / 12) * 2f));
            }

            Assert.That(
                _selection.SelectInsideScreenRect(new Rect(200f, 150f, 400f, 300f), false, _projector),
                Is.EqualTo(Units),
                "the selection itself is not capped, only what a single command may carry");
            Assert.That(_selection.SelectedCount, Is.GreaterThan(SimulationConstants.MaxSelectedEntities));

            Assert.That(_selection.IssueStop(604, _sink), Is.True);

            // The ceiling is per command, so the extra twenty are a second order rather
            // than twenty units that never hear the stop.
            Assert.That(_sink.Count, Is.EqualTo(2));
            Assert.That(_sink.issues[0].Entities.Length, Is.EqualTo(SimulationConstants.MaxSelectedEntities));
            Assert.That(_sink.issues[1].Entities.Length, Is.EqualTo(Units - SimulationConstants.MaxSelectedEntities));

            var expectedEntity = 1UL;
            for (var batch = 0; batch < _sink.Count; batch++)
            {
                Assert.That(_sink.issues[batch].Header.Type, Is.EqualTo(GameCommandType.Stop), $"batch {batch}");
                foreach (var entity in _sink.issues[batch].Entities)
                {
                    Assert.That(
                        entity,
                        Is.EqualTo(new EntityId(expectedEntity)),
                        $"batch {batch} must carry the next hundred in canonical order");
                    expectedEntity++;
                }
            }

            Assert.That(expectedEntity, Is.EqualTo((ulong)Units + 1), "every selected unit is ordered, once");
        }

        /// <summary>
        /// Defect A: a 200-unit drag used to be truncated to its first hundred, so the
        /// second hundred stood still while the player watched the order answered. One
        /// command per block of <see cref="SimulationConstants.MaxSelectedEntities"/>, each
        /// with its own sequence, is what makes the whole selection move.
        /// </summary>
        [Test]
        public void IssueMove_WithMoreThan100Units_BatchesCommandsSequentially()
        {
            const int Units = 200;
            for (var index = 0; index < Units; index++)
            {
                Spawn(
                    (ulong)(index + 1),
                    MetresToMillimetres(-11f + (index % 14) * 2f),
                    MetresToMillimetres(-9f + (index / 14) * 2f));
            }

            for (var entity = 1UL; entity <= Units; entity++)
            {
                _selection.AddSelected(new EntityId(entity), SlotOf(entity));
            }

            Assert.That(_selection.SelectedCount, Is.EqualTo(Units));

            // Straight north of the army's centroid, so the chosen facing is North and
            // the second block's stepped destination is an equality rather than a guess.
            var destination = new WorldPointMm(0, 60_000);
            Assert.That(_selection.IssueMove(destination, 605, _sink), Is.True);

            Assert.That(_sink.Count, Is.EqualTo(2), "two hundreds are two orders, not one truncated one");

            Assert.That(_sink.issues[0].Entities.Length, Is.EqualTo(SimulationConstants.MaxSelectedEntities));
            Assert.That(_sink.issues[1].Entities.Length, Is.EqualTo(SimulationConstants.MaxSelectedEntities));
            Assert.That(_sink.issues[0].Header.Sequence, Is.EqualTo(1u));
            Assert.That(_sink.issues[1].Header.Sequence, Is.EqualTo(2u), "a replayed sequence is a refused order");

            var expectedEntity = 1UL;
            for (var batch = 0; batch < _sink.Count; batch++)
            {
                var issued = _sink.issues[batch];
                Assert.That(issued.Header.Type, Is.EqualTo(GameCommandType.Move), $"batch {batch}");
                Assert.That(issued.Header.Player, Is.EqualTo(new PlayerId(Local)), $"batch {batch}");
                Assert.That(issued.Header.RequestedTick, Is.EqualTo(605UL), $"batch {batch}");
                Assert.That(issued.Formation.IsValid, Is.True, $"batch {batch}");
                Assert.That(
                    issued.Formation.Facing,
                    Is.EqualTo(CardinalFacing.North),
                    $"batch {batch} faces the way the whole army was aimed, not its own way");

                foreach (var entity in issued.Entities)
                {
                    Assert.That(
                        entity,
                        Is.EqualTo(new EntityId(expectedEntity)),
                        $"batch {batch} must carry the next hundred in canonical order");
                    expectedEntity++;
                }
            }

            Assert.That(expectedEntity, Is.EqualTo((ulong)Units + 1), "all two hundred are ordered, exactly once");

            // The clicked point is where the first block forms up, unchanged from a
            // selection that fits one command. The second falls in behind it, one full
            // block depth per batch: FormationLayout hands out positions by index in the
            // payload, so two blocks on one destination would be given the same ground.
            Assert.That(_sink.issues[0].Destination, Is.EqualTo(destination));
            Assert.That(
                _sink.issues[1].Destination,
                Is.EqualTo(new WorldPointMm(
                    destination.X,
                    destination.Z - FormationLayout.GetAutoColumnCount(SimulationConstants.MaxSelectedEntities) *
                        _selection.FormationSpacingMillimetres)));
        }

        /// <summary>
        /// The other half of the ceiling rule: a selection that fits one command must
        /// still be one command, aimed at exactly the point the player clicked.
        /// </summary>
        [Test]
        public void IssueMove_WithExactlyOneBatchUnit_SubmitsOneCommandAtTheClickedPoint()
        {
            const int Units = SimulationConstants.MaxSelectedEntities;
            for (var index = 0; index < Units; index++)
            {
                Spawn(
                    (ulong)(index + 1),
                    MetresToMillimetres(-11f + (index % 10) * 2f),
                    MetresToMillimetres(-9f + (index / 10) * 2f));
            }

            for (var entity = 1UL; entity <= Units; entity++)
            {
                _selection.AddSelected(new EntityId(entity), SlotOf(entity));
            }

            var destination = new WorldPointMm(0, 60_000);
            Assert.That(_selection.IssueMove(destination, 606, _sink), Is.True);

            Assert.That(_sink.Count, Is.EqualTo(1), "batching must not split an army that already fits");
            Assert.That(_sink.issues[0].Destination, Is.EqualTo(destination));
            Assert.That(_sink.issues[0].Entities.Length, Is.EqualTo(Units));
            Assert.That(_sink.issues[0].Header.Sequence, Is.EqualTo(1u));
        }

        [Test]
        public void SequencesIncreasePerCommand_AndTheHeaderPlayerFollowsTheLocalPlayer()
        {
            SpawnCast();
            Select(1);
            _selection.LocalPlayerId = new PlayerId(Enemy);

            // The client has just become player 2, so unit 1 — still in the set from when
            // it was player 1's — is now somebody else's. Selecting unit 5 through the
            // door that is now open (the enemy owner is the local player) gives the two
            // commands something legal to address.
            Select(5);

            Assert.That(_selection.IssueStop(700, _sink), Is.True);
            Assert.That(_selection.IssueStop(701, _sink), Is.True);

            Assert.That(_sink.issues[0].Header.Sequence, Is.EqualTo(1u));
            Assert.That(_sink.issues[1].Header.Sequence, Is.EqualTo(2u));
            Assert.That(_sink.issues[1].Header.Player, Is.EqualTo(new PlayerId(Enemy)));
            Assert.That(
                _sink.issues[0].Entities,
                Is.EqualTo(new[] { new EntityId(5) }).AsCollection,
                "the first command already carries only the new player's unit");
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.False, "and player 1's unit is out of the set");
        }

        [Test]
        public void ChannelSink_ForwardsEveryFieldToTheCommandChannel()
        {
            var channel = new RecordingCommandChannel();
            var controller = new UnitSelectionController(_binder, _world, new PlayerId(Local));
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            controller.AddSelected(new EntityId(1), SlotOf(1));
            controller.AddSelected(new EntityId(2), SlotOf(2));

            Assert.That(controller.IssueMove(new WorldPointMm(1234, 5678), 900, new UnitCommandChannelSink(channel)), Is.True);

            Assert.That(channel.Moves, Is.EqualTo(1));
            Assert.That(channel.LastHeader.Type, Is.EqualTo(GameCommandType.Move));
            Assert.That(channel.LastEntities.Length, Is.EqualTo(2));
            Assert.That(channel.LastEntities[0], Is.EqualTo(new EntityId(1)));
            Assert.That(channel.LastEntities[1], Is.EqualTo(new EntityId(2)));
            Assert.That(channel.LastDestination, Is.EqualTo(new WorldPointMm(1234, 5678)));
        }

        [Test]
        public void IssueMove_RejectsADestinationOutsideTheSimulationBounds()
        {
            Spawn(1, Ax, RowZ);
            Select(1);

            Assert.That(
                _selection.IssueMove(new WorldPointMm(SimulationConstants.MaxWorldCoordinateMm + 1, 0), 901, _sink),
                Is.False);
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        // -------------------------------------------------------------------- adversarial input

        [TestCase(0f, 0f, -1f, TestName = "Horizontal ray parallel to the plane")]
        [TestCase(30f, 1f, 0f, TestName = "Upward ray from above the plane")]
        [TestCase(0f, 0f, 0f, TestName = "Zero direction")]
        public void GroundIntersection_RefusesRaysThatNeverMeetThePlane(
            float originY,
            float directionY,
            float directionZ)
        {
            Assert.That(
                UnitPickMath.TryIntersectGroundPlane(
                    new Vector3(0f, originY, 0f),
                    new Vector3(0f, directionY, directionZ),
                    out var point),
                Is.False);
            Assert.That(point, Is.EqualTo(Vector3.zero), "a miss must not report a position");
        }

        [Test]
        public void GroundIntersection_AcceptsADownwardRay()
        {
            Assert.That(
                UnitPickMath.TryIntersectGroundPlane(
                    new Vector3(0f, 30f, 0f),
                    new Vector3(0f, -1f, 0f),
                    out var point),
                Is.True);
            Assert.That(point, Is.EqualTo(Vector3.zero).Within(1e-4f));

            Assert.That(
                UnitPickMath.TryIntersectGroundPlane(
                    new Vector3(2f, 10f, -3f),
                    new Vector3(0f, -1f, 0f),
                    out var below),
                Is.True);
            Assert.That(below.x, Is.EqualTo(2f));
            Assert.That(below.y, Is.EqualTo(0f));
            Assert.That(below.z, Is.EqualTo(-3f));
        }

        [TestCase(float.NaN, 30f, 0f, -1f, 0f, 0f)]
        [TestCase(0f, float.PositiveInfinity, 0f, -1f, 0f, 0f)]
        [TestCase(0f, 30f, 0f, float.NaN, 0f, 0f)]
        [TestCase(0f, 30f, float.NegativeInfinity, -1f, 0f, 0f)]
        public void GroundIntersection_RefusesNonFiniteRays(
            float originX,
            float originY,
            float directionX,
            float directionY,
            float directionZ,
            float originZ)
        {
            Assert.That(
                UnitPickMath.TryIntersectGroundPlane(
                    new Vector3(originX, originY, originZ),
                    new Vector3(directionX, directionY, directionZ),
                    out _),
                Is.False);
        }

        [TestCase(float.NaN, 1f)]
        [TestCase(1f, float.NegativeInfinity)]
        [TestCase(2_000_000f, 0f)]
        [TestCase(0f, -1_500_000f)]
        [TestCase(1_000_001f, 0f)]
        public void MillimetreConversion_RefusesNonFiniteAndOutOfRangeCoordinates(
            float xMetres,
            float zMetres)
        {
            Assert.That(
                UnitPickMath.TryToMillimetrePoint(new Vector3(xMetres, 0f, zMetres), out var point),
                Is.False);
            Assert.That(
                point,
                Is.EqualTo(default(WorldPointMm)),
                "a refused conversion must not leave a half-written coordinate behind");
        }

        [Test]
        public void MillimetreConversion_RoundsInsideTheBoundsAndStopsAtThem()
        {
            Assert.That(
                UnitPickMath.TryToMillimetrePoint(new Vector3(2.25f, 0f, -1.75f), out var point),
                Is.True);
            Assert.That(point, Is.EqualTo(new WorldPointMm(2250, -1750)), "1 m = 1000 mm on both axes");

            Assert.That(
                UnitPickMath.TryToMillimetrePoint(new Vector3(1_000_000f, 0f, 0f), out var ceiling),
                Is.True,
                "the bound is inclusive, and a coordinate exactly at it is legal");
            Assert.That(ceiling.X, Is.EqualTo(SimulationConstants.MaxWorldCoordinateMm));
        }

        [Test]
        public void NonFinitePointer_ChangesNothingAndIssuesNothing()
        {
            SpawnCast();
            Select(1, 2, 3);

            Assert.That(
                _selection.TryIssueCommandAtPointer(
                    new Vector2(float.NaN, 4f),
                    _projector,
                    1,
                    _sink),
                Is.False);
            Assert.That(
                _selection.TryIssueCommandAtPointer(
                    new Vector2(4f, float.PositiveInfinity),
                    _projector,
                    2,
                    _sink),
                Is.False);
            Assert.That(_sink.Count, Is.EqualTo(0), "a poisoned pointer must not reach the server as a corrupt order");

            _selection.OnPrimaryClick(new Vector2(float.NaN, 4f), additive: false, 3.0, _projector);
            Assert.That(_selection.SelectedCount, Is.EqualTo(3), "nor may it look like a click on empty ground");
        }

        [Test]
        public void ProjectorWithNoGroundHit_IssuesNoCommandAndPreservesTheSelection()
        {
            SpawnCast();
            Select(1, 2, 3);
            _projector.GroundAvailable = false;

            Assert.That(
                _selection.TryIssueCommandAtPointer(ScreenOfWorld(Ax, RowZ), _projector, 4, _sink),
                Is.False,
                "no ground means no destination, which is not a licence to invent one");
            Assert.That(_sink.Count, Is.EqualTo(0));

            _selection.OnPrimaryClick(ScreenOfWorld(Bx, RowZ), additive: false, 5.0, _projector);
            Assert.That(_selection.SelectedCount, Is.EqualTo(3));
        }

        [Test]
        public void ExtremePointerCoordinates_CannotOverflowIntoACorruptMove()
        {
            SpawnCast();
            Select(1);

            // A pointer projected far off the edge of the screen lands metres outside the
            // map: the conversion has to refuse it rather than wrap into an int.
            Assert.That(
                _selection.TryIssueCommandAtPointer(
                    TestProjector.ScreenForMetres(3_000_000f, 0f),
                    _projector,
                    6,
                    _sink),
                Is.False);
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void NullProjectorAndNullSink_AreRefusedRatherThanThrown()
        {
            Spawn(1, Ax, RowZ);
            Select(1);

            Assert.That(_selection.TryIssueCommandAtPointer(ScreenOfWorld(Ax, RowZ), null, 1, _sink), Is.False);
            Assert.That(_selection.TryIssueCommandAtPointer(ScreenOfWorld(Ax, RowZ), _projector, 1, null), Is.False);
            Assert.That(_selection.IssueStop(1, null), Is.False);
            Assert.That(() => _selection.UpdatePointer(Vector2.zero, null), Throws.Nothing);
            Assert.That(_selection.HoveredEntity.IsValid, Is.False);
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        // --------------------------------------------------- audit step 3.5: gesture arithmetic

        /// <summary>
        /// P1-1: the drag threshold is the controller's own decision, and a caller that
        /// sees the press and the release in one frame (a fast click, or a driver polling
        /// the button edges) never gives <see cref="UnitSelectionController.UpdatePointer"/>
        /// the travel to notice. Without re-deriving it at release, a 600 pixel drag is
        /// classified as a click on its ending pixel and wipes the squad.
        /// </summary>
        [Test]
        public void Release_WithoutAnIntermediatePointerUpdate_ResolvesAsAMarquee()
        {
            SpawnCast();
            var start = TestProjector.ScreenForMetres(-30f, -20f);
            var end = ScreenOfWorld(Gx, RowZ) + new Vector2(10f, 10f);

            _selection.BeginDrag(start);
            Assert.That(_selection.IsDragging, Is.False, "a press alone has not become a box");

            Assert.That(
                _selection.ResolveRelease(end, additive: false, 0.0, _projector),
                Is.EqualTo(SelectionGesture.Marquee),
                "the release has to measure the travel the press left behind");

            Assert.That(_selection.SelectedCount, Is.EqualTo(4), "units 1-4, exactly as the sampled drag selects");
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True);
            Assert.That(_selection.ScreenRectPx.xMin, Is.EqualTo(start.x));
            Assert.That(_selection.ScreenRectPx.xMax, Is.EqualTo(end.x));
            Assert.That(_selection.IsDragging, Is.False, "the gesture is over");
        }

        /// <summary>
        /// P1-1: a non-finite sample is not a pointer anywhere. The three entry points
        /// that take one have to refuse it rather than arm a drag from it, cancel one
        /// with it, or resolve a click at it.
        /// </summary>
        [Test]
        public void NonFinitePointer_SamplesNoHoverArmsNoDragAndResolvesNothing()
        {
            SpawnCast();
            Select(1, 2, 3);

            _selection.UpdatePointer(new Vector2(float.NaN, 300f), _projector);
            Assert.That(_selection.HoveredEntity.IsValid, Is.False);

            _selection.BeginDrag(new Vector2(float.NegativeInfinity, 300f));
            Assert.That(_selection.IsDragging, Is.False);
            Assert.That(
                _selection.ResolveRelease(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector),
                Is.EqualTo(SelectionGesture.None),
                "a press that never armed has no release to decide either");
            Assert.That(_selection.SelectedCount, Is.EqualTo(3), "and the selection is nobody's to clear");
        }

        /// <summary>
        /// P1-1: one bad sample inside a live drag is a dropped frame, not an ended
        /// gesture. The box keeps what it last had and the release refuses to guess.
        /// </summary>
        [Test]
        public void NonFiniteSample_MidDrag_LeavesTheBoxAndSelectionAlone()
        {
            SpawnCast();
            Select(1, 2, 3);

            var start = TestProjector.ScreenForMetres(-30f, -20f);
            var end = ScreenOfWorld(Gx, RowZ);
            _selection.BeginDrag(start);
            _selection.UpdatePointer(end, _projector);
            Assert.That(_selection.IsDragging, Is.True);
            var rectBefore = _selection.ScreenRectPx;

            _selection.UpdatePointer(new Vector2(500f, float.NaN), _projector);
            Assert.That(_selection.IsDragging, Is.True, "a poisoned sample does not cancel the drag");
            Assert.That(_selection.ScreenRectPx, Is.EqualTo(rectBefore), "nor redraw it at NaN");

            Assert.That(
                _selection.ResolveRelease(new Vector2(float.NaN, 500f), additive: false, 1.0, _projector),
                Is.EqualTo(SelectionGesture.None));
            Assert.That(_selection.SelectedCount, Is.EqualTo(3), "and above all it must not clear the squad");
            Assert.That(_selection.IsDragging, Is.False);
        }

        /// <summary>
        /// P1-2: a marquee the controller cannot describe is not a request to replace the
        /// selection, so the rectangle is validated before anything is cleared.
        /// </summary>
        [TestCase(float.NaN, 100f, 600f, 400f)]
        [TestCase(100f, float.PositiveInfinity, 600f, 400f)]
        [TestCase(100f, 100f, float.NaN, 400f)]
        [TestCase(100f, 100f, 600f, float.NegativeInfinity)]
        public void SelectInsideScreenRect_ANonFiniteBox_KeepsTheSelection(
            float x,
            float y,
            float width,
            float height)
        {
            SpawnCast();
            Select(1, 2, 3);

            Assert.That(
                _selection.SelectInsideScreenRect(new Rect(x, y, width, height), additive: false, _projector),
                Is.EqualTo(3));
            Assert.That(_selection.SelectedCount, Is.EqualTo(3));
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True);
            Assert.That(ViewOf(1).IsSelected, Is.True, "and its ring is still lit");
        }

        /// <summary>
        /// P1-2: the same ordering rule on the double-click group select — a projector
        /// that cannot state a viewport has no screen to test against, so it must not be
        /// allowed to clear the group on the way to selecting nothing.
        /// </summary>
        [Test]
        public void SelectKindOnScreen_AProjectorWithNoViewport_KeepsTheSelection()
        {
            SpawnCast();
            Select(1, 2, 3);

            var noViewport = new TestProjector();
            noViewport.ScreenSizeOverride = Vector2.zero;

            Assert.That(
                _selection.SelectKindOnScreen(UnitKinds.Scout, additive: false, noViewport),
                Is.EqualTo(3));
            Assert.That(_selection.SelectedCount, Is.EqualTo(3));
        }

        /// <summary>
        /// P1-3: <see cref="UnitSelectionController.AddSelected"/> is the one door into the
        /// selection, so ownership and liveness are decided there rather than trusted from
        /// a caller. An enemy or a casualty in the set is a ring on someone else's unit
        /// and a name in a Move payload the server will refuse.
        /// </summary>
        [Test]
        public void AddSelected_AndSelectSingle_RefuseUnitsThePlayerCannotCommand()
        {
            SpawnCast();
            var enemy = new EntityId(5);
            var casualty = new EntityId(6);

            _selection.AddSelected(enemy, SlotOf(5));
            Assert.That(_selection.SelectedCount, Is.EqualTo(0), "another player's unit is not selectable");
            Assert.That(ViewOf(5).IsSelected, Is.False, "and it must not light up");

            _selection.AddSelected(casualty, SlotOf(6));
            Assert.That(_selection.SelectedCount, Is.EqualTo(0), "a corpse cannot be ordered around");
            Assert.That(ViewOf(6).IsSelected, Is.False);

            _selection.SelectSingle(enemy, SlotOf(5));
            Assert.That(_selection.IsSelected(enemy), Is.False);
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(0), "no ring reaches the batch either");

            _selection.AddSelected(new EntityId(1), SlotOf(1));
            Assert.That(_selection.SelectedCount, Is.EqualTo(1), "the player's own live unit still goes in");
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(1));
        }

        // ------------------------------------------------------- audit step 3.5: rounding

        /// <summary>
        /// P2-4: the conversion is decided in double and stays there. Rounding through a
        /// float reintroduces 24-bit quantisation exactly where the value is largest — a
        /// 64 mm step near the coordinate ceiling — and float's round-half-to-even sends an
        /// exact .5 mm to whichever neighbour happens to be even.
        /// </summary>
        [TestCase(0.0005f, 1)]
        [TestCase(-0.0005f, -1)]
        [TestCase(7.0855f, 7085)]
        [TestCase(10.0045f, 10005)]
        [TestCase(999999.9f, 999999875)]
        [TestCase(-999999.9f, -999999875)]
        public void MillimetreConversion_RoundsInDoubleNotThroughAFloat(float metres, int expectedMillimetres)
        {
            Assert.That(
                UnitPickMath.TryToMillimetrePoint(new Vector3(metres, 0f, metres), out var point),
                Is.True);
            Assert.That(point.X, Is.EqualTo(expectedMillimetres), "x at {0} m", metres);
            Assert.That(point.Z, Is.EqualTo(expectedMillimetres), "z at {0} m", metres);
        }

        /// <summary>
        /// P2-4: an overflowed ray is a miss, and a miss writes nothing. The old path
        /// assigned the product first and tested it afterwards, so the caller was told
        /// "no ground" while being handed an infinite coordinate.
        /// </summary>
        [Test]
        public void GroundIntersection_ANonFiniteHit_ReportsNeitherAPositionNorAHit()
        {
            Assert.That(
                UnitPickMath.TryIntersectGroundPlane(
                    new Vector3(3e38f, 3e38f, 0f),
                    new Vector3(1f, -1f, 0f),
                    out var overflowed),
                Is.False);
            Assert.That(overflowed, Is.EqualTo(Vector3.zero), "a refused intersection must not report a position");
        }

        /// <summary>
        /// P2-4: divide then multiply is not an identity in binary floating point, so the
        /// raw hit of a slanted ray sits a few 1e-7 off a plane that is exactly Y = 0 by
        /// contract (OD-27). A caller that compares the ground point against a unit
        /// position should not have to carry a tolerance for an exact value.
        /// </summary>
        [Test]
        public void GroundIntersection_ASlantedHit_LiesExactlyOnThePlane()
        {
            Assert.That(
                UnitPickMath.TryIntersectGroundPlane(
                    new Vector3(1.5f, 2.82f, -2.25f),
                    new Vector3(0.4f, -0.7f, 0.9f),
                    out var slanted),
                Is.True);
            Assert.That(slanted.y, Is.EqualTo(UnitPickMath.GroundHeightMetres), "no float residual");
            Assert.That(slanted.x, Is.EqualTo(3.1114f).Within(1e-3f));
            Assert.That(slanted.z, Is.EqualTo(1.3757f).Within(1e-3f));
        }

        /// <summary>
        /// P2-4: a ray that starts on the plane and climbs away from it never meets it
        /// ahead of itself. <c>enter</c> is a signed zero there, and no comparison catches
        /// a signed zero, so the side test has to be explicit or the pointer reads the
        /// camera's own height as clicked ground.
        /// </summary>
        [Test]
        public void GroundIntersection_ARayClimbingFromThePlane_IsNotAHit()
        {
            Assert.That(
                UnitPickMath.TryIntersectGroundPlane(
                    new Vector3(4f, UnitPickMath.GroundHeightMetres, -2f),
                    new Vector3(0f, 1f, 0f),
                    out var upwardFromThePlane),
                Is.False);
            Assert.That(upwardFromThePlane, Is.EqualTo(Vector3.zero));
        }

        // -------------------------------------------------------- audit step 3.5: commands

        /// <summary>
        /// P2-5: a command is built from a selection the caller captured earlier, so the
        /// pruning that keeps it honest belongs to the emission. A casualty left in the
        /// payload names a unit the server does not own, and a recycled slot reads a
        /// stranger's position into the centroid — which is what decides the cardinal
        /// facing, so the formation is then laid out the wrong way round.
        /// </summary>
        [Test]
        public void Commands_PruneTheSelectionWithoutAFramePassFromTheCaller()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            var slotOfThree = Spawn(3, Cx, RowZ);
            Select(1, 2, 3);

            Kill(2);
            Assert.That(
                _world.ApplyRemove(new DeltaRemoveRecord(new EntityId(3), DeltaRemoveCause.Destroyed)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(
                Spawn(9, 90_000, RowZ),
                Is.EqualTo(slotOfThree),
                "the new unit took the recycled slot");

            // No PruneStaleSelection here: the issue path has to stand on its own.
            Assert.That(_selection.SelectedCount, Is.EqualTo(3), "as far as the caller knows, all three are selected");
            Assert.That(_selection.IssueMove(new WorldPointMm(2000, RowZ), 800, _sink), Is.True);

            var issued = _sink.Only;
            Assert.That(
                issued.Entities,
                Is.EqualTo(new[] { new EntityId(1) }).AsCollection,
                "the casualty and the stranger are not this player's to order");
            Assert.That(_selection.SelectedCount, Is.EqualTo(1), "and the selection itself was pruned");
            Assert.That(issued.Formation.IsValid, Is.True);

            // Due east of the one surviving unit. Scored against the stale three-unit
            // centroid — (1000 + 3000 + 90000) / 3 — the same command faces West.
            Assert.That(issued.Formation.Facing, Is.EqualTo(CardinalFacing.East));
            Assert.That(ViewOf(1).IsSelected, Is.True, "the survivor keeps its ring");
            Assert.That(ViewAt(SlotOf(2)).IsSelected, Is.False, "the corpse loses its own");
        }

        /// <summary>
        /// P3-10: only a move is spread into a formation. Attack and Stop ignore the
        /// layout, so carrying one means the centroid loop above runs for a command that
        /// never reads it.
        /// </summary>
        [Test]
        public void StopAndAttackCommands_CarryNoFormation()
        {
            SpawnCast();
            Select(1, 2);

            Assert.That(_selection.IssueStop(801, _sink), Is.True);
            var stop = _sink.issues[0];
            Assert.That(stop.Header.Type, Is.EqualTo(GameCommandType.Stop));
            Assert.That(stop.Formation.SpacingMm, Is.EqualTo(0), "spacing 0 is no layout, and a real one is never 0");
            Assert.That(stop.Formation.IsValid, Is.False, "and the server never sees one it would have to validate");

            Assert.That(
                _selection.TryIssueCommandAtPointer(ScreenOfWorld(Ex, RowZ), _projector, 802, _sink),
                Is.True);
            var attack = _sink.issues[1];
            Assert.That(attack.Header.Type, Is.EqualTo(GameCommandType.Attack));
            Assert.That(attack.Target, Is.EqualTo(new EntityId(5)));
            Assert.That(attack.Formation.SpacingMm, Is.EqualTo(0));
            Assert.That(attack.Formation, Is.EqualTo(default(FormationSpec)));

            Assert.That(_selection.IssueMove(new WorldPointMm(4000, 4000), 803, _sink), Is.True);
            Assert.That(_sink.issues[2].Formation.IsValid, Is.True, "a move still does carry one");
        }

        /// <summary>
        /// P1-3 from the other side: switching which player this client is must not leave
        /// the previous player's units in a command.
        /// </summary>
        [Test]
        public void Commands_OnlyEverCarryTheCurrentLocalPlayersUnits()
        {
            SpawnCast();
            Select(1);
            _selection.LocalPlayerId = new PlayerId(Enemy);
            Select(5);

            Assert.That(_selection.IssueStop(804, _sink), Is.True);

            var issued = _sink.Only;
            Assert.That(
                issued.Entities,
                Is.EqualTo(new[] { new EntityId(5) }).AsCollection,
                "unit 1 belongs to the player this client stopped being");
            Assert.That(issued.Header.Player, Is.EqualTo(new PlayerId(Enemy)));
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.False);
        }

        // --------------------------------------------------------- audit step 3.5: tuning

        /// <summary>
        /// P2-6: the formation spacing is the one tuning knob the server validates, so an
        /// illegal value is refused loudly at the assignment rather than quietly turning
        /// every move of the match into an InvalidFormation rejection.
        /// </summary>
        [TestCase(0)]
        [TestCase(-100)]
        [TestCase(3201)]
        [TestCase(1)]
        [TestCase(SimulationConstants.MaxFormationSpacingMm + 2)]
        public void FormationSpacing_OutsideTheServerLegalRange_Throws(int spacing)
        {
            Assert.That(
                () => { _selection.FormationSpacingMillimetres = spacing; },
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                _selection.FormationSpacingMillimetres,
                Is.EqualTo(UnitSelectionController.DefaultFormationSpacingMillimetres),
                "a refused assignment leaves the working value alone");
        }

        [TestCase(2)]
        [TestCase(6400)]
        [TestCase(SimulationConstants.MaxFormationSpacingMm)]
        public void FormationSpacing_AcceptsEveryLegalEvenValue(int spacing)
        {
            _selection.FormationSpacingMillimetres = spacing;
            Assert.That(_selection.FormationSpacingMillimetres, Is.EqualTo(spacing));

            Spawn(1, Ax, RowZ);
            Select(1);
            Assert.That(_selection.IssueMove(new WorldPointMm(4000, 4000), 2, _sink), Is.True);
            Assert.That(_sink.Only.Formation.SpacingMm, Is.EqualTo(spacing));
            Assert.That(_sink.Only.Formation.IsValid, Is.True, "and the server accepts the layout it produces");
        }

        /// <summary>
        /// P3-16: the pointer tuning knobs are compared against, not validated by anyone,
        /// so a NaN reaching one of them silently ends selection instead of throwing. They
        /// fall back to the defaults the same way <see cref="UnitOverlayBatcher"/> sanitises
        /// its colours.
        /// </summary>
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(-1f)]
        public void PointerTuning_NonFiniteOrNegative_FallsBackToTheDefault(float value)
        {
            _selection.DragThresholdPixels = value;
            _selection.MinimumPickRadiusMetres = value;

            Assert.That(_selection.DragThresholdPixels, Is.EqualTo(UnitSelectionController.DefaultDragThresholdPixels));
            Assert.That(
                _selection.MinimumPickRadiusMetres,
                Is.EqualTo(UnitSelectionController.DefaultMinimumPickRadiusMetres));

            // Zero is a real setting: every pixel of travel is a box.
            _selection.DragThresholdPixels = 0f;
            Assert.That(_selection.DragThresholdPixels, Is.EqualTo(0f));
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(-0.5)]
        public void DoubleClickWindow_NonFiniteOrNegative_FallsBackToTheDefault(double value)
        {
            _selection.DoubleClickWindowSeconds = value;

            Assert.That(
                _selection.DoubleClickWindowSeconds,
                Is.EqualTo(UnitSelectionController.DefaultDoubleClickWindowSeconds));

            Spawn(1, Ax, RowZ);
            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.0, _projector);
            _selection.OnPrimaryClick(ScreenOfWorld(Ax, RowZ), additive: false, 0.05, _projector);
            Assert.That(_selection.SelectedCount, Is.EqualTo(1), "a sanitised window still double-clicks");
        }

        // ------------------------------------------------------------ audit step 3.5: wiring

        /// <summary>
        /// P2-8: the presenter writes straight into a RectTransform, which accepts a NaN
        /// without complaint and then keeps reporting the broken layout for every frame
        /// afterwards — including the ones the next, legal, drag draws.
        /// </summary>
        [Test]
        public void MarqueePresenter_ABoxItCannotDraw_IsHiddenAndNeverReachesTheRectTransform()
        {
            var host = new GameObject("Marquee");
            var presenter = host.AddComponent<SelectionMarqueePresenter>();
            Assert.That(presenter.Build(), Is.True);
            var frameRect = (RectTransform)presenter.transform.GetChild(0).GetChild(0);

            try
            {
                presenter.Present(true, new Rect(100f, 120f, 200f, 240f));
                Assert.That(presenter.IsVisible, Is.True);

                foreach (var poison in new[]
                {
                    new Rect(float.NaN, 120f, 200f, 240f),
                    new Rect(100f, float.PositiveInfinity, 200f, 240f),
                    new Rect(100f, 120f, float.NaN, 240f),
                    new Rect(100f, 120f, 200f, float.NegativeInfinity),
                    new Rect(100f, 120f, -200f, -240f),
                })
                {
                    presenter.Present(true, poison);

                    Assert.That(presenter.IsVisible, Is.False, $"a {poison} box is not a box");
                    Assert.That(frameRect.anchoredPosition, Is.EqualTo(new Vector2(100f, 120f)), "and never written");
                    Assert.That(frameRect.sizeDelta, Is.EqualTo(new Vector2(200f, 240f)));
                }

                presenter.Present(true, new Rect(140f, 160f, 60f, 60f));
                Assert.That(presenter.IsVisible, Is.True, "a later legal box still draws");
                Assert.That(frameRect.anchoredPosition, Is.EqualTo(new Vector2(140f, 160f)));
            }
            finally
            {
                presenter.Release();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Playing-capture defect: dragging the left button drew no rubber band, and the
        /// report blamed <c>Image.Type.Sliced</c> over a null sprite for generating no
        /// vertices. uGUI decides otherwise — <c>Image.OnPopulateMesh</c> falls through to
        /// <c>Graphic.OnPopulateMesh</c> when there is no sprite and emits a solid quad — so
        /// a box was being drawn: a 35%-alpha green sheet in the same colour family as the
        /// terrain under it, waiting for a bordered 9-slice that nothing ever supplies.
        ///
        /// The assertion is therefore the property the player needs rather than the
        /// mechanism the report guessed at: a shown marquee carries geometry of its own,
        /// from a sprite it owns, in a colour that separates it from the battlefield, and it
        /// does not depend on the untextured fall-through to be anything.
        /// </summary>
        [Test]
        public void MarqueePresenter_WithNoContent_PresentsABoxThatCarriesItsOwnGeometry()
        {
            var host = new GameObject("Marquee");
            var presenter = host.AddComponent<SelectionMarqueePresenter>();
            Assert.That(presenter.Build(), Is.True);

            var frame = presenter.transform.GetChild(0).GetChild(0).GetComponent<Image>();
            Assert.That(frame, Is.Not.Null, "the marquee canvas has no frame image");

            try
            {
                presenter.Present(true, new Rect(120f, 200f, 340f, 260f));
                Assert.That(presenter.IsVisible, Is.True, "the presenter never showed the box");

                Assert.That(
                    frame.sprite,
                    Is.Not.Null,
                    "with no border art the drag box exists only as Image's null-sprite fall-through, " +
                    "which is a coloured sheet rather than a rubber band");
                Assert.That(
                    frame.type,
                    Is.EqualTo(Image.Type.Simple),
                    "slicing a sprite with no borders costs nine quads for the same stretched fill");
                Assert.That(
                    GeneratedVertexCount(frame),
                    Is.GreaterThanOrEqualTo(4),
                    "the shown marquee generates no geometry, so there is nothing on screen to drag with");

                // Translucent so the squad inside the box stays visible, and opaque enough to
                // read against ground, units and the dark of the HUD behind it.
                Assert.That(
                    frame.color.a,
                    Is.GreaterThanOrEqualTo(0.3f).And.LessThan(1f),
                    $"the marquee's alpha is {frame.color.a:0.00}, which is either an invisible sheet " +
                    "or a blindfold over the units being boxed");
            }
            finally
            {
                presenter.Release();
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// How many vertices a graphic hands the canvas when it rebuilds. uGUI decides
        /// that inside the protected <c>OnPopulateMesh</c>, and EditMode runs no canvas
        /// batch, so the mesh cannot be read back off the <see cref="CanvasRenderer"/>.
        /// </summary>
        private static int GeneratedVertexCount(Graphic graphic)
        {
            var populate = typeof(Graphic).GetMethod(
                "OnPopulateMesh",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(VertexHelper) },
                null);
            Assert.That(
                populate,
                Is.Not.Null,
                "uGUI no longer exposes OnPopulateMesh(VertexHelper), so nothing here can see what a widget draws");

            var helper = new VertexHelper();
            populate.Invoke(graphic, new object[] { helper });
            return helper.currentVertCount;
        }

        /// <summary>
        /// P3-14: a camera swap retargets the projector the frame loop already holds. The
        /// per-frame budget is not the point a test can see; object identity is, and it is
        /// the same claim — no new projection object per swap.
        /// </summary>
        [Test]
        public void Driver_SetCamera_RetargetsTheProjectorItAlreadyOwns()
        {
            var driver = AddHarnessComponent<UnitSelectionDriver>("Driver");
            var first = AddHarnessComponent<Camera>("First Camera");
            var second = AddHarnessComponent<Camera>("Second Camera");

            driver.SetCamera(first);
            var projector = driver.PointerProjector as CameraUnitPointerProjector;
            Assert.That(projector, Is.Not.Null);
            Assert.That(projector.Camera, Is.EqualTo(first));

            driver.SetCamera(second);

            Assert.That(driver.PointerProjector, Is.SameAs(projector), "the projector is reused, not replaced");
            Assert.That(projector.Camera, Is.EqualTo(second), "and now projects from the new camera");
        }



        // -------------------------------------------------------------------- real camera

        /// <summary>
        /// Builds a camera whose pixel geometry is fixed, because a
        /// <see cref="Camera"/> that has never rendered reports the editor window for
        /// <c>pixelWidth</c> and <c>pixelHeight</c> and no test should be written
        /// against whatever size that happens to be.
        /// </summary>
        private static Camera MakeGroundCamera(
            RenderTexture target,
            Vector3 position,
            Quaternion rotation,
            bool orthographic)
        {
            var cameraObject = new GameObject("Step35 Camera");
            var camera = cameraObject.AddComponent<Camera>();
            cameraObject.transform.SetPositionAndRotation(position, rotation);
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 500f;
            camera.orthographic = orthographic;
            camera.orthographicSize = 20f;
            camera.targetTexture = target;
            return camera;
        }

        [Test]
        public void RealCamera_ResolvesTheGroundUnderTheViewportCentre()
        {
            var target = new RenderTexture(800, 600, 24);
            var camera = MakeGroundCamera(
                target,
                new Vector3(0f, 30f, 0f),
                Quaternion.LookRotation(Vector3.down),
                orthographic: false);

            try
            {
                var projector = new CameraUnitPointerProjector(camera);
                Assert.That(camera.pixelWidth, Is.EqualTo(800), "the fixture's viewport must be fixed");
                Assert.That(camera.pixelHeight, Is.EqualTo(600));

                Assert.That(
                    projector.TryGetGroundPoint(
                        new Vector2(camera.pixelWidth * 0.5f, camera.pixelHeight * 0.5f),
                        out var ground),
                    Is.True,
                    "a camera looking down at the flat playfield always meets it");
                Assert.That(ground.x, Is.EqualTo(0f).Within(1e-3f));
                Assert.That(ground.z, Is.EqualTo(0f).Within(1e-3f));
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void RealCamera_RefusesAHorizontalAndAnUpwardView()
        {
            var target = new RenderTexture(800, 600, 24);
            try
            {
                foreach (var pitch in new[] { 0f, -45f })
                {
                    var camera = MakeGroundCamera(
                        target,
                        new Vector3(0f, 30f, 0f),
                        Quaternion.Euler(pitch, 0f, 0f),
                        orthographic: false);
                    var projector = new CameraUnitPointerProjector(camera);

                    // Pitch 0 travels along +Z at a constant height and never reaches
                    // Y = 0; a negative pitch looks up and away from it. Both have to be
                    // misses: a fallback to the world origin reads to the player as a
                    // legitimate order to move to the middle of the map.
                    Assert.That(
                        projector.TryGetGroundPoint(
                            new Vector2(camera.pixelWidth * 0.5f, camera.pixelHeight * 0.5f),
                            out _),
                        Is.False,
                        $"a camera pitched {pitch} degrees has no ground under the pointer");

                    Object.DestroyImmediate(camera.gameObject);
                }
            }
            finally
            {
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void RealCamera_ReportsPointsBehindTheLensWithANonPositiveDepth()
        {
            var target = new RenderTexture(800, 600, 24);
            var camera = MakeGroundCamera(
                target,
                new Vector3(0f, 30f, 0f),
                Quaternion.LookRotation(Vector3.down),
                orthographic: false);

            try
            {
                var projector = new CameraUnitPointerProjector(camera);
                Assert.That(projector.ProjectToScreen(Vector3.zero).z, Is.GreaterThan(0f), "the ground below is in front");
                Assert.That(projector.ProjectToScreen(new Vector3(0f, 60f, 0f)).z, Is.LessThan(0f), "above a downward camera is behind it");
                Assert.That(projector.ScreenSizePixels.x, Is.EqualTo(800f));
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void RealCamera_RoundTripsAUnitFromWorldToGroundAndBack()
        {
            var target = new RenderTexture(800, 600, 24);
            var camera = MakeGroundCamera(
                target,
                new Vector3(0f, 40f, 0f),
                Quaternion.LookRotation(Vector3.down),
                orthographic: true);

            try
            {
                var projector = new CameraUnitPointerProjector(camera);
                Spawn(1, Ax, RowZ);

                // The marquee projects with WorldToScreenPoint and the click resolves
                // through ScreenPointToRay onto the ground. If the two disagreed about
                // the same unit, a box drawn around a unit would not select it.
                var projected = projector.ProjectToScreen(new Vector3(1f, 0f, 2f));
                Assert.That(projected.z, Is.GreaterThan(0f));
                Assert.That(projector.TryGetGroundPoint(projected, out var ground), Is.True);
                Assert.That(ground.x, Is.EqualTo(1f).Within(0.05f));
                Assert.That(ground.z, Is.EqualTo(2f).Within(0.05f));

                _selection.OnPrimaryClick(projected, additive: false, 0.0, projector);
                Assert.That(_selection.IsSelected(new EntityId(1)), Is.True, "the box and the click agree about the same unit");
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        // ------------------------------------------------------- altitude-aware picking (part 5)

        /// <summary>
        /// Playing-capture defect: from the slice's 150 m overview, clicking a unit meant
        /// hitting a couple of pixels, and a click that missed cleared the selection
        /// instead. The pointer's ground point is a ray-plane intersection, so a pixel of
        /// mouse travel buys more ground the higher the camera sits — which is exactly why
        /// a floor stated in metres stops being a floor up there.
        /// </summary>
        [Test]
        public void RealCamera_ClickBesideAUnit_SelectsItFromOverviewAltitude()
        {
            const float OverviewAltitudeMetres = 150f;
            const float MissDistanceMetres = 3f;

            var target = new RenderTexture(800, 600, 24);
            var camera = MakeGroundCamera(
                target,
                new Vector3(0f, OverviewAltitudeMetres, 0f),
                Quaternion.LookRotation(Vector3.down),
                orthographic: false);

            try
            {
                var projector = new CameraUnitPointerProjector(camera);

                // A scout's own footprint reaches 1.5 m, so a click 3 m off its centre is a
                // miss by every rule this fixture had before the altitude reached it.
                Spawn(1, 0, 0);
                Spawn(2, 8000, 0);

                var pixel = projector.ProjectToScreen(new Vector3(MissDistanceMetres, 0f, 0f));
                Assert.That(projector.TryGetGroundPoint(pixel, out var ground), Is.True);
                Assert.That(ground.x, Is.EqualTo(MissDistanceMetres).Within(0.05f), "the click must really land beside the unit");

                _selection.OnPrimaryClick(pixel, additive: false, 0.0, projector);

                Assert.That(
                    _selection.IsSelected(new EntityId(1)),
                    Is.True,
                    $"one metre of reach per {UnitSelectionController.PickRadiusAltitudeStepMetres} m of " +
                    $"altitude puts the floor at " +
                    $"{OverviewAltitudeMetres / UnitSelectionController.PickRadiusAltitudeStepMetres:0.0} m, " +
                    "so a unit 3 m from the clicked ground point is the unit that was clicked");

                // The widening has to stay a reach, not become a rubber band: the second
                // unit is 5 m from the pixel, which is outside the same floor.
                Assert.That(_selection.SelectedCount, Is.EqualTo(1), "a wider pick still selects one unit, not the crowd around it");
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// The same click at the altitude the floor was tuned for. The compensation is a
        /// function of height, not a licence to click anywhere: near the ground a 3 m miss
        /// is a 3 m miss, and the empty-ground rule that clears the selection has to stay
        /// reachable or deselecting becomes impossible.
        /// </summary>
        [Test]
        public void RealCamera_ClickBesideAUnit_DoesNotReachAcrossTheGroundAtLowAltitude()
        {
            const float LowAltitudeMetres = 21f;

            var target = new RenderTexture(800, 600, 24);
            var camera = MakeGroundCamera(
                target,
                new Vector3(0f, LowAltitudeMetres, 0f),
                Quaternion.LookRotation(Vector3.down),
                orthographic: false);

            try
            {
                var projector = new CameraUnitPointerProjector(camera);
                Spawn(1, 0, 0);

                var pixel = projector.ProjectToScreen(new Vector3(3f, 0f, 0f));

                _selection.OnPrimaryClick(pixel, additive: false, 0.0, projector);

                Assert.That(
                    _selection.SelectedCount,
                    Is.EqualTo(0),
                    $"at {LowAltitudeMetres} m a 3 m miss is 74 pixels of open ground, so it has to read " +
                    "as the empty-ground click it is");
                Assert.That(_selection.IsSelected(new EntityId(1)), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        // ------------------------------------------------ command reach (part 6 capture)

        /// <summary>
        /// Playing-capture defect: a right-click on free ground issued an attack order
        /// instead of a move, and the squad kept breaking into the melee it was already
        /// in instead of walking to the clicked point.
        ///
        /// <b>The mechanism.</b> One pick decides an order's type — an enemy under the
        /// cursor means Attack, anything else means Move — and that pick was sharing the
        /// <em>selection's</em> altitude-widened reach. The widening is a forgiveness for
        /// "which unit did I aim at", and at the slice's 150 m overview it is
        /// 150 / 35 = 4.3 m, or 7.4 m at the 260 m ceiling, while the armies' own
        /// formation grid is 8.5 m. Once the two sides were interleaved, every patch of
        /// ground between two files was inside an enemy's reach, so the click the player
        /// made to pull them out of the fight re-armed the fight instead. A command pick
        /// now uses a unit's own footprint, so hull means hull and visible empty ground
        /// means ground.
        /// </summary>
        [Test]
        public void RightClick_BesideAnEnemyAtOverviewAltitude_IssuesMoveNotAttack()
        {
            const float OverviewAltitudeMetres = 150f;
            const float EnemyCentreMetres = 1f;
            const float ClickMetres = 4f;

            var target = new RenderTexture(800, 600, 24);
            var camera = MakeGroundCamera(
                target,
                new Vector3(0f, OverviewAltitudeMetres, 0f),
                Quaternion.LookRotation(Vector3.down),
                orthographic: false);

            try
            {
                var projector = new CameraUnitPointerProjector(camera);

                // The enemy is 3 m from the clicked ground point: far outside its own
                // 0.5 m footprint, well inside the reach this camera's altitude used to
                // report. The player's squad sits on the far side so only the enemy could
                // be misread as the thing under the cursor.
                Spawn(1, -10_000, 0);
                Spawn(2, (int)(EnemyCentreMetres * 1000f), 0, owner: Enemy);
                _selection.SelectSingle(new EntityId(1), SlotOf(1));

                var pixel = projector.ProjectToScreen(new Vector3(ClickMetres, 0f, 0f));
                Assert.That(
                    projector.TryGetGroundPoint(pixel, out var ground),
                    Is.True,
                    "the click has to have ground under it, or this fixture is testing the refusal path");
                Assert.That(
                    ground.x,
                    Is.EqualTo(ClickMetres).Within(0.05f),
                    "and the clicked point really is the open ground beside the enemy");

                Assert.That(
                    _selection.TryIssueCommandAtPointer(pixel, projector, 700, _sink),
                    Is.True);

                var issued = _sink.Only;
                Assert.That(
                    issued.Header.Type,
                    Is.EqualTo(GameCommandType.Move),
                    $"a click {ClickMetres - EnemyCentreMetres:0} m off an enemy is free ground, but the " +
                    $"selection's widened floor at {OverviewAltitudeMetres:0} m reaches " +
                    $"{OverviewAltitudeMetres / UnitSelectionController.PickRadiusAltitudeStepMetres:0.0} m and " +
                    "named it — an attack order, which re-arms acquisition for the whole squad");
                Assert.That(
                    issued.Target.IsValid,
                    Is.False,
                    "a move order that also carries a target is the attack order this click was not");
                Assert.That(issued.Destination.X, Is.EqualTo(4000).Within(1), "the squad walks to the pixel it was clicked on");
                Assert.That(issued.Destination.Z, Is.EqualTo(0).Within(1));
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// The other side of the same rule, and the guard against tightening it too far:
        /// taming the reach must not cost the player the ability to order an attack at
        /// all. A cursor on an enemy's own footprint still names it.
        /// </summary>
        [Test]
        public void RightClick_OnAnEnemyHullAtOverviewAltitude_StillIssuesAttack()
        {
            var target = new RenderTexture(800, 600, 24);
            var camera = MakeGroundCamera(
                target,
                new Vector3(0f, 150f, 0f),
                Quaternion.LookRotation(Vector3.down),
                orthographic: false);

            try
            {
                var projector = new CameraUnitPointerProjector(camera);

                Spawn(1, -10_000, 0);
                Spawn(2, 1000, 0, owner: Enemy);
                _selection.SelectSingle(new EntityId(1), SlotOf(1));

                // 0.4 m off the enemy's centre — inside its footprint, outside the
                // 0.5 m radius only by a hair, so a pick bounded by hulls alone still
                // has to reach it.
                var pixel = projector.ProjectToScreen(new Vector3(1.4f, 0f, 0f));

                Assert.That(
                    _selection.TryIssueCommandAtPointer(pixel, projector, 701, _sink),
                    Is.True);
                Assert.That(_sink.Only.Header.Type, Is.EqualTo(GameCommandType.Attack));
                Assert.That(_sink.Only.Target, Is.EqualTo(new EntityId(2)));
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// The forgiveness half, at the zoom ceiling: uncapped, the altitude widening put
        /// 7.4 m of reach on a 1 m footprint, which is nearly a whole grid cell — a click
        /// on obviously empty ground selected the unit next to it and the player could not
        /// deselect. Capped at <see cref="UnitSelectionController.MaxSelectionPickRadiusMetres"/>
        /// the click reads as the empty ground it is.
        /// </summary>
        [Test]
        public void RealCamera_ClickFiveMetresFromAUnitAtZoomCeiling_ClearsInsteadOfSelecting()
        {
            const float CeilingAltitudeMetres = 260f;
            const float MissDistanceMetres = 5f;

            var target = new RenderTexture(800, 600, 24);
            var camera = MakeGroundCamera(
                target,
                new Vector3(0f, CeilingAltitudeMetres, 0f),
                Quaternion.LookRotation(Vector3.down),
                orthographic: false);

            try
            {
                var projector = new CameraUnitPointerProjector(camera);
                Spawn(1, (int)(MissDistanceMetres * 1000f), 0);

                var pixel = projector.ProjectToScreen(new Vector3(0f, 0f, 0f));

                _selection.OnPrimaryClick(pixel, additive: false, 0.0, projector);

                Assert.That(
                    _selection.SelectedCount,
                    Is.EqualTo(0),
                    $"one metre of reach per " +
                    $"{UnitSelectionController.PickRadiusAltitudeStepMetres} m put the floor at " +
                    $"{CeilingAltitudeMetres / UnitSelectionController.PickRadiusAltitudeStepMetres:0.0} m at this " +
                    $"altitude, so a click {MissDistanceMetres} m from a unit with nothing under it selected that " +
                    "unit and the player lost the ability to deselect");
                Assert.That(
                    _selection.IsSelected(new EntityId(1)),
                    Is.False,
                    $"and it was the unit {MissDistanceMetres} m away that got the ring");
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// The cap has to stay above the pixel a unit occupies on screen, or it is a
        /// regression of the part-5 finding rather than a bound on it: 3 m at 260 m is
        /// about nine pixels of open ground, still the same intended click.
        /// </summary>
        [Test]
        public void RealCamera_ClickThreeMetresFromAUnitAtZoomCeiling_StillSelectsIt()
        {
            const float CeilingAltitudeMetres = 260f;

            var target = new RenderTexture(800, 600, 24);
            var camera = MakeGroundCamera(
                target,
                new Vector3(0f, CeilingAltitudeMetres, 0f),
                Quaternion.LookRotation(Vector3.down),
                orthographic: false);

            try
            {
                var projector = new CameraUnitPointerProjector(camera);
                Spawn(1, 3000, 0);

                var pixel = projector.ProjectToScreen(new Vector3(0f, 0f, 0f));

                _selection.OnPrimaryClick(pixel, additive: false, 0.0, projector);

                Assert.That(
                    _selection.IsSelected(new EntityId(1)),
                    Is.True,
                    $"the cap is {UnitSelectionController.MaxSelectionPickRadiusMetres:0.0} m, so a unit 3 m off " +
                    "the cursor is still the unit that was clicked");
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        // -------------------------------------------------------------------- zero GC

        [Test]
        public void SelectionMaintenance_OverFourHundredUnits_DoesNotAllocate()
        {
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            const int Units = 400;
            for (var index = 0; index < Units; index++)
            {
                Spawn(
                    (ulong)(index + 1),
                    MetresToMillimetres(-9.5f + (index % 20) * 1f),
                    MetresToMillimetres(-9.5f + (index / 20) * 1f));
            }

            Assert.That(_binder.BoundViewCount, Is.EqualTo(Units));
            Assert.That(_pool.GrowCount, Is.EqualTo(0), "the measured window must be steady state");

            var created = _pool.TotalCreated;
            Select(1, 200, 400);

            void Maintenance(int iterations)
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    // Sweep the pointer in a circle with the button held: hover
                    // queries, the drag threshold, the marquee rectangle and the
                    // per-unit screen test all run on every one of these frames.
                    var angle = iteration * 0.07;
                    var pointer = new Vector2(
                        400f + (float)Math.Cos(angle) * 180f,
                        300f + (float)Math.Sin(angle) * 150f);
                    _selection.PruneStaleSelection();
                    _selection.UpdatePointer(pointer, _projector);
                }
            }

            Maintenance(20);
            Assert.That(
                () => Maintenance(200),
                Is.Not.AllocatingGCMemory(),
                "OD-28: per-frame selection maintenance over 400 units must allocate nothing");

            Assert.That(_pool.TotalCreated, Is.EqualTo(created));
            Assert.That(_pool.GrowCount, Is.EqualTo(0));
        }

        [Test]
        public void MarqueePresenter_PresentingADrag_DoesNotAllocate()
        {
            byte[] ballast = null;
            Assert.That(() => { ballast = new byte[4096]; }, GcAssert.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            var host = new GameObject("Marquee");
            var presenter = host.AddComponent<SelectionMarqueePresenter>();
            Assert.That(presenter.Build(), Is.True);
            Assert.That(presenter.Build(), Is.True, "a second build must not stack a second canvas");

            void Drag(int iterations)
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    presenter.Present(
                        true,
                        new Rect(100f + iteration, 200f, 300f + iteration, 150f + iteration));
                }
            }

            Drag(10);
            Assert.That(
                () => Drag(200),
                Is.Not.AllocatingGCMemory(),
                "OD-24: the transient marquee canvas draws with one sliced image and no per-frame allocation");

            presenter.Release();
            Object.DestroyImmediate(host);
        }

        [Test]
        public void OverlayBatchBuild_ForTheCurrentSelection_DoesNotAllocate()
        {
            byte[] ballast = null;
            Assert.That(() => { ballast = new byte[4096]; }, GcAssert.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            for (var index = 0; index < 32; index++)
            {
                Spawn((ulong)(index + 1), MetresToMillimetres(1f + index * 2f), 0);
            }

            _selection.SelectInsideScreenRect(new Rect(0f, 0f, 800f, 600f), false, _projector);
            Assert.That(_selection.SelectedCount, Is.GreaterThan(0));

            var batcher = new UnitOverlayBatcher(64);
            void Build(int iterations)
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    _selection.PruneStaleSelection();
                    batcher.BuildBatches(_binder, Quaternion.identity);
                }
            }

            Build(5);
            Assert.That(
                () => Build(200),
                Is.Not.AllocatingGCMemory(),
                "the selection has to reach the ring batch every frame without a cost");
            Assert.That(batcher.SelectionRingCount, Is.EqualTo(_selection.SelectedCount));
        }

        // -------------------------------------------------------------------- driver

        private T AddHarnessComponent<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root, false);
            return go.AddComponent<T>();
        }

        private UnitSelectionDriver CreateDriver(out RtsInputManager input, out SelectionMarqueePresenter presenter)
        {
            input = AddHarnessComponent<RtsInputManager>("Input");
            input.SetMockMode(true);

            presenter = AddHarnessComponent<SelectionMarqueePresenter>("Marquee");
            presenter.Build();

            var driver = AddHarnessComponent<UnitSelectionDriver>("Selection Driver");
            driver.SetInputManager(input);
            driver.SetMarqueePresenter(presenter);
            driver.PointerProjector = _projector;
            driver.Configure(_selection, _sink);
            return driver;
        }

        [Test]
        public void Driver_LeftPressThenRelease_SelectsThroughTheMockPointer()
        {
            SpawnCast();
            var driver = CreateDriver(out var input, out _);

            input.SetMockMousePosition(ScreenOfWorld(Ax, RowZ));
            input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            driver.ManualUpdate(0.0);
            Assert.That(_selection.SelectedCount, Is.EqualTo(0), "a press alone selects nothing; the release decides");

            input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            driver.ManualUpdate(0.05);

            Assert.That(_selection.SelectedCount, Is.EqualTo(1));
            Assert.That(_selection.IsSelected(new EntityId(1)), Is.True);
            Assert.That(BuildOverlay().SelectionRingCount, Is.EqualTo(1));
        }

        [Test]
        public void Driver_RightClickAndStopHotkey_ReachTheSinkWithTheDriversRequestedTick()
        {
            SpawnCast();
            var driver = CreateDriver(out var input, out _);
            Select(1, 2);

            input.SetMockMousePosition(ScreenOfWorld(Ex, RowZ));
            input.SetMockMouseButton(1, down: true, pressed: true, up: false);
            driver.RequestedTick = 777;
            input.SetMockMouseButton(1, down: false, pressed: false, up: true);
            driver.ManualUpdate(0.1);

            Assert.That(_sink.Count, Is.EqualTo(1));
            Assert.That(_sink.issues[0].Header.Type, Is.EqualTo(GameCommandType.Attack));
            Assert.That(_sink.issues[0].Target, Is.EqualTo(new EntityId(5)));
            Assert.That(_sink.issues[0].Header.RequestedTick, Is.EqualTo(777UL));

            input.SetMockMouseButton(1, down: false, pressed: false, up: false);
            input.SetMockStopRequested(true);
            driver.ManualUpdate(0.2);

            Assert.That(_sink.Count, Is.EqualTo(2));
            Assert.That(_sink.issues[1].Header.Type, Is.EqualTo(GameCommandType.Stop));
        }

        [Test]
        public void Driver_Drag_PutsTheMarqueeOnAndTakesItOff()
        {
            SpawnCast();
            var driver = CreateDriver(out var input, out var presenter);

            input.SetMockMousePosition(TestProjector.ScreenForMetres(-30f, -20f));
            input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            driver.ManualUpdate(0.0);
            Assert.That(presenter.IsVisible, Is.False, "a press has not become a box yet");

            var end = TestProjector.ScreenForMetres(11f, 11f);
            input.SetMockMousePosition(end);
            input.SetMockMouseButton(0, down: false, pressed: true, up: false);
            driver.ManualUpdate(0.02);
            Assert.That(_selection.IsDragging, Is.True);
            Assert.That(presenter.IsVisible, Is.True, "the HUD draws from the controller's own state");

            input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            driver.ManualUpdate(0.04);

            Assert.That(presenter.IsVisible, Is.False, "the box goes away with the gesture");
            Assert.That(_selection.SelectedCount, Is.EqualTo(4));
        }

        // -------------------------------------------------------------------- helpers

        private static int MetresToMillimetres(float metres) => Mathf.RoundToInt(metres * 1000f);

        private void Select(params ulong[] entities)
        {
            foreach (var entity in entities)
            {
                _selection.AddSelected(new EntityId(entity), SlotOf(entity));
            }
        }

        // -------------------------------------------------------------------- doubles

        /// <summary>
        /// Exact, invertible screen mapping. A 10 px per metre scale around an 800x600
        /// viewport keeps every fixture coordinate an integer number of pixels, so the
        /// millimetre assertions are equalities.
        ///
        /// The depth rule is deliberately artificial: a unit west of
        /// <see cref="BehindLensX"/> is reported behind the lens while still projecting
        /// to a pixel inside the marquee. A real camera mirrors behind-the-lens points
        /// to a plausible in-rect pixel too — that is exactly why the screen test has to
        /// check depth at all — but a real camera cannot put a behind-lens unit where a
        /// test asks for it.
        /// </summary>
        private sealed class TestProjector : IUnitPointerProjector
        {
            public const float PixelsPerMetre = 10f;
            public const float Width = 800f;
            public const float Height = 600f;
            public const float BehindLensX = -20f;

            public bool GroundAvailable = true;

            /// <summary>
            /// Overrides the reported viewport, because a projector that has never
            /// rendered has nothing to state. Null keeps the fixture's own 800x600.
            /// </summary>
            public Vector2? ScreenSizeOverride;

            public static Vector2 ScreenForMetres(float xMetres, float zMetres) => new Vector2(
                Width * 0.5f + xMetres * PixelsPerMetre,
                Height * 0.5f + zMetres * PixelsPerMetre);

            public Vector3 ProjectToScreen(Vector3 world) => new Vector3(
                Width * 0.5f + world.x * PixelsPerMetre,
                Height * 0.5f + world.z * PixelsPerMetre,
                world.x < BehindLensX ? -1f : 1f);

            public bool TryGetGroundPoint(Vector2 screenPixels, out Vector3 groundWorld)
            {
                groundWorld = Vector3.zero;
                if (!GroundAvailable ||
                    !float.IsFinite(screenPixels.x) ||
                    !float.IsFinite(screenPixels.y))
                {
                    return false;
                }

                groundWorld = new Vector3(
                    (screenPixels.x - Width * 0.5f) / PixelsPerMetre,
                    0f,
                    (screenPixels.y - Height * 0.5f) / PixelsPerMetre);
                return true;
            }

            public Vector2 ScreenSizePixels => ScreenSizeOverride ?? new Vector2(Width, Height);
        }

        private readonly struct CapturedCommand
        {
            public CapturedCommand(
                CommandHeader header,
                EntityId[] entities,
                WorldPointMm destination,
                EntityId target,
                FormationSpec formation)
            {
                Header = header;
                Entities = entities;
                Destination = destination;
                Target = target;
                Formation = formation;
            }

            public CommandHeader Header { get; }

            public EntityId[] Entities { get; }

            public WorldPointMm Destination { get; }

            public EntityId Target { get; }

            public FormationSpec Formation { get; }
        }

        /// <summary>
        /// Copies each request, because <see cref="IssuedCommand"/> hands over the
        /// controller's shared buffer and a recording test that kept the reference
        /// would be asserting against whatever the next click wrote into it.
        /// </summary>
        private sealed class RecordingCommandSink : IUnitCommandSink
        {
            public readonly List<CapturedCommand> issues = new List<CapturedCommand>();

            public int Count => issues.Count;

            public CapturedCommand Only
            {
                get
                {
                    Assert.That(issues.Count, Is.EqualTo(1), "expected exactly one command");
                    return issues[0];
                }
            }

            public void Submit(in IssuedCommand command)
            {
                var entities = new EntityId[command.EntityCount];
                for (var index = 0; index < entities.Length; index++)
                {
                    entities[index] = command.GetEntity(index);
                }

                issues.Add(new CapturedCommand(
                    command.Header,
                    entities,
                    command.Destination,
                    command.Target,
                    command.Formation));
            }
        }

        private sealed class RecordingCommandChannel : ICommandChannel
        {
            public int Moves { get; private set; }

            public CommandHeader LastHeader { get; private set; }

            public EntityId[] LastEntities { get; private set; }

            public WorldPointMm LastDestination { get; private set; }

            public MatchCommandRejection TrySubmitMove(
                CommandHeader header,
                EntityId[] entities,
                WorldPointMm destination,
                FormationSpec formation)
            {
                Moves++;
                LastHeader = header;
                LastEntities = entities;
                LastDestination = destination;
                return MatchCommandRejection.None;
            }

            public MatchCommandRejection TrySubmitAttack(
                CommandHeader header,
                EntityId[] attackers,
                EntityId target)
            {
                LastHeader = header;
                LastEntities = attackers;
                return MatchCommandRejection.None;
            }

            public MatchCommandRejection TrySubmitStop(CommandHeader header, EntityId[] entities)
            {
                LastHeader = header;
                LastEntities = entities;
                return MatchCommandRejection.None;
            }
        }
    }
}
