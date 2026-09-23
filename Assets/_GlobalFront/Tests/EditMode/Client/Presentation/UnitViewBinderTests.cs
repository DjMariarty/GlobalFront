using System;
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
    /// Phase 3, step 3.3: the view pool and the slot-to-view binder
    /// (<see cref="UnitViewPool"/>, <see cref="UnitViewBinder"/>, OD-26).
    ///
    /// What these tests defend is the two ways a pooling binder fails quietly in
    /// play instead of loudly in review. The first is identity: a recycled table slot
    /// that keeps its view renders a fresh tank wearing a dead scout's transform, and
    /// a double release puts one object in the free list twice so two live units end
    /// up driving a single hull. The second is cost: one <c>Instantiate</c> per spawn
    /// is an invisible bug at ten units and a frame spike at four hundred, so the
    /// steady-state presentation frame — and the acquire/release churn of a firefight
    /// — both have to be allocation-free, measured with the project's GC.Alloc
    /// recorder because the editor's Mono runtime reports 0 from
    /// <c>GC.GetAllocatedBytesForCurrentThread()</c>.
    /// </summary>
    [TestFixture]
    public sealed class UnitViewBinderTests
    {
        private const int SlotCapacity = 32;
        private const double TickSeconds = 0.05;            // 20 Hz simulation
        private const double FrameSeconds = 1.0 / 60.0;     // 60 fps presentation
        private const double PacketSeconds = 0.1;           // 10 Hz replication
        private const int StepPerPacketMm = 700;

        private const int XMillimetres = 1000;
        private const int ZMillimetres = 2000;
        private const int TurretTargetXMillimetres = 31000;
        private const int MoveTargetZMillimetres = 22000;

        /// <summary>
        /// Matches <see cref="UnitCatalog.PrototypeMaximumHealth"/>, which is what the
        /// replicated archetype resolves to and therefore what a full-health pose
        /// reports as its fraction.
        /// </summary>
        private const int PrototypeHealth = 100;

        private Transform _root;
        private UnitViewPool _pool;
        private ClientReplicationWorld _world;
        private UnitViewTickBuffer _buffer;
        private UnitViewBinder _binder;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("UnitViewBinderTests_Root").transform;
            _pool = new UnitViewPool(_root);
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

        private static DeltaAddRecord Add(
            ulong entity,
            int posX,
            int posZ,
            byte kind,
            int moveTargetX = 0,
            int moveTargetZ = 0,
            ulong attackTarget = 0,
            int health = PrototypeHealth)
        {
            return new DeltaAddRecord(
                new EntityId(entity),
                new PlayerId(1),
                new WorldPointMm(posX, posZ),
                health,
                moveTargetZ != 0 || moveTargetX != 0,
                new WorldPointMm(moveTargetX, moveTargetZ),
                new EntityId(attackTarget),
                false,
                kind);
        }

        private static DeltaUpdateRecord MoveTo(ulong entity, int posX, int posZ)
        {
            return new DeltaUpdateRecord(
                new EntityId(entity),
                (byte)UnitDirtyMask.Position,
                new PlayerId(1),
                new WorldPointMm(posX, posZ),
                0,
                false,
                new WorldPointMm(0, 0),
                new EntityId(0),
                false);
        }

        private static DeltaRemoveRecord Destroy(ulong entity)
        {
            return new DeltaRemoveRecord(new EntityId(entity), DeltaRemoveCause.Destroyed);
        }

        private void AddUnit(ulong entity, byte kind = UnitKinds.Scout)
        {
            Assert.That(
                _world.ApplyAdd(Add(entity, XMillimetres, ZMillimetres, kind)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
        }

        private void CreateMatch(int unitCount)
        {
            _world = new ClientReplicationWorld(SlotCapacity);
            _buffer = new UnitViewTickBuffer(SlotCapacity, TickSeconds);
            _binder = new UnitViewBinder(_pool, _world, _buffer);
            for (var index = 0; index < unitCount; index++)
            {
                AddUnit((ulong)(index + 1), UnitKinds.Scout);
            }
        }

        /// <summary>
        /// Replication hands out freed slots again, so a test must never assume a
        /// slot number — it looks the entity up the same way the binder walks the
        /// table.
        /// </summary>
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

        private void AssertDeactivated(UnitView view, string message)
        {
            Assert.That(view.ViewObject.activeSelf, Is.False, message);
        }

        private int ActiveChildCount()
        {
            var active = 0;
            for (var index = 0; index < _root.childCount; index++)
            {
                if (_root.GetChild(index).gameObject.activeSelf)
                {
                    active++;
                }
            }

            return active;
        }

        /// <summary>
        /// Feeds one 10 Hz packet (with an optional move) plus the frames it covers.
        /// The presentation loop of a real match, and the unit of every assertion
        /// below: the binder needs a capture past its own bind tick before it applies
        /// a pose, so a single frame proves nothing about interpolation.
        /// </summary>
        private void FeedPacket(ulong tick, int entity = 1, int stepX = 0)
        {
            if (stepX != 0)
            {
                Assert.That(
                    _world.ApplyUpdate(MoveTo((ulong)entity, XMillimetres + stepX, ZMillimetres)),
                    Is.EqualTo(ClientWorldApplyResult.Ok));
            }

            _buffer.CaptureTick(tick, _world);
            for (var frame = 0; frame < 6; frame++)
            {
                _binder.Render(FrameSeconds);
            }
        }

        // ---------------------------------------------------------------- UnitView

        [Test]
        public void View_Apply_ConvertsMillimetresAndWritesAbsoluteYaws()
        {
            Assert.That(_pool.Warmup(UnitKinds.Tank, 1), Is.EqualTo(1));
            var view = _pool.Acquire(UnitKinds.Tank);
            Assert.That(view.HasTurret, Is.True, "the placeholder hierarchy carries a turret");

            view.Apply(new UnitViewPose(
                2500f,
                -3000f,
                40,
                0.4f,
                90f,
                45f,
                UnitViewPoseSource.Interpolated));

            Assert.That(view.Hull.position.x, Is.EqualTo(2.5f).Within(1e-4f));
            Assert.That(view.Hull.position.z, Is.EqualTo(-3f).Within(1e-4f));
            Assert.That(view.Hull.position.y, Is.EqualTo(0f), "the playfield is flat by contract (OD-27)");
            Assert.That(view.Hull.eulerAngles.y, Is.EqualTo(90f).Within(0.01f));

            // 45 degrees, not 135: the pose publishes an absolute world heading, so
            // adding the hull yaw through the parent chain would overshoot by exactly
            // the hull's heading.
            Assert.That(view.Turret.eulerAngles.y, Is.EqualTo(45f).Within(0.01f));
            Assert.That(view.Health, Is.EqualTo(40));
            Assert.That(view.HealthFraction, Is.EqualTo(0.4f).Within(1e-5f));
        }

        [Test]
        public void View_Apply_WithoutTurret_IgnoresTurretYaw()
        {
            var hullOnly = new GameObject("Hull only");
            var view = hullOnly.AddComponent<UnitView>();
            Assert.That(view.HasTurret, Is.False);

            view.Apply(new UnitViewPose(
                -500f,
                500f,
                100,
                1f,
                30f,
                120f,
                UnitViewPoseSource.Snapped));

            Assert.That(view.Hull.position.x, Is.EqualTo(-0.5f).Within(1e-4f));
            Assert.That(view.Hull.position.z, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(view.Hull.eulerAngles.y, Is.EqualTo(30f).Within(0.01f));
            Assert.That(view.Turret, Is.Null);

            Object.DestroyImmediate(hullOnly);
        }

        [Test]
        public void View_SnapToMillimetres_PlacesAuthorityBeforeAnySample()
        {
            var hullOnly = new GameObject("Hull only");
            var view = hullOnly.AddComponent<UnitView>();

            view.SnapToMillimetres(-1234, 5678);

            Assert.That(view.Hull.position.x, Is.EqualTo(-1.234f).Within(1e-4f));
            Assert.That(view.Hull.position.z, Is.EqualTo(5.678f).Within(1e-4f));
            Assert.That(view.Hull.position.y, Is.EqualTo(0f));

            Object.DestroyImmediate(hullOnly);
        }

        // ---------------------------------------------------------------- the pool

        [Test]
        public void Pool_Warmup_CreatesExactlyTheRequestedViewsDeactivated()
        {
            Assert.That(_pool.Warmup(UnitKinds.Scout, 4), Is.EqualTo(4));
            Assert.That(_pool.OwnedCount(UnitKinds.Scout), Is.EqualTo(4));
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(4));
            Assert.That(_pool.ActiveCount(UnitKinds.Scout), Is.EqualTo(0));
            Assert.That(_root.childCount, Is.EqualTo(4), "every pooled view stays under the pool root");
            Assert.That(ActiveChildCount(), Is.EqualTo(0), "a warm view is an inactive view");

            // Grow-to, not add-N: a second warm-up at the same or a lower number must
            // not double the allocation it was already given.
            Assert.That(_pool.Warmup(UnitKinds.Scout, 4), Is.EqualTo(0));
            Assert.That(_pool.Warmup(UnitKinds.Scout, 2), Is.EqualTo(0));
            Assert.That(_pool.Warmup(UnitKinds.Scout, 6), Is.EqualTo(2));
            Assert.That(_pool.OwnedCount(UnitKinds.Scout), Is.EqualTo(6));
        }

        [Test]
        public void Pool_AcquireAndRelease_ReuseTheSameInstance()
        {
            Assert.That(_pool.Warmup(UnitKinds.Scout, 2), Is.EqualTo(2));

            var first = _pool.Acquire(UnitKinds.Scout);
            var second = _pool.Acquire(UnitKinds.Scout);
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first.ViewObject.activeSelf, Is.True);
            Assert.That(_pool.InUseCount, Is.EqualTo(2));
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(0));

            Assert.That(_pool.Release(first), Is.True);
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(1));
            Assert.That(_pool.ActiveCount(UnitKinds.Scout), Is.EqualTo(1));
            Assert.That(_pool.InUseCount, Is.EqualTo(1));
            AssertDeactivated(first, "a released view is deactivated, not destroyed");
            Assert.That(_pool.TotalCreated, Is.EqualTo(2), "release must never destroy a view");

            var reused = _pool.Acquire(UnitKinds.Scout);
            Assert.That(reused, Is.SameAs(first), "the whole point of the pool");
            Assert.That(reused.ViewObject.activeSelf, Is.True);
            Assert.That(reused.UnitKind, Is.EqualTo(UnitKinds.Scout));
            Assert.That(reused.IsBound, Is.False, "the pool hands out an unbound view; the binder binds it");
            Assert.That(_pool.GrowCount, Is.EqualTo(0), "a warmed pool must not reallocate");
            Assert.That(_pool.TotalCreated, Is.EqualTo(2));
        }

        [Test]
        public void Pool_KeepsArchetypesSeparated()
        {
            Assert.That(_pool.Warmup(UnitKinds.Scout, 1), Is.EqualTo(1));
            Assert.That(_pool.Warmup(UnitKinds.Tank, 1), Is.EqualTo(1));

            var tank = _pool.Acquire(UnitKinds.Tank);
            Assert.That(tank.UnitKind, Is.EqualTo(UnitKinds.Tank));
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(1));
            Assert.That(_pool.FreeCount(UnitKinds.Tank), Is.EqualTo(0));

            // An archetype the server may add after this client's table was built
            // falls back to the placeholder row instead of throwing mid-frame.
            Assert.That(_pool.Warmup(UnitKinds.Unknown, 1), Is.EqualTo(1));
            var fallback = _pool.Acquire(200);
            Assert.That(fallback, Is.Not.Null);
            Assert.That(fallback.UnitKind, Is.EqualTo(UnitKinds.Unknown), "the fallback row is the unknown archetype");
            Assert.That(_pool.Release(fallback), Is.True);
            Assert.That(_pool.FreeCount(200), Is.EqualTo(1));
            Assert.That(_pool.FreeCount(UnitKinds.Unknown), Is.EqualTo(1), "unknown shares the fallback row");
        }

        [Test]
        public void Pool_Exhaustion_AllocatesOneBlockAndWarns()
        {
            var pool = new UnitViewPool(_root, growBlock: 3);
            Assert.That(pool.Warmup(UnitKinds.Tank, 1), Is.EqualTo(1));

            var first = pool.Acquire(UnitKinds.Tank);
            Assert.That(pool.GrowCount, Is.EqualTo(0));

            var second = pool.Acquire(UnitKinds.Tank);
            Assert.That(second, Is.Not.Null, "an exhausted pool serves the request, it does not hide the unit");
            Assert.That(pool.GrowCount, Is.EqualTo(1));
            Assert.That(pool.OwnedCount(UnitKinds.Tank), Is.EqualTo(4), "one block, not one object at a time");
            Assert.That(second.ViewObject.activeSelf, Is.True);
            Assert.That(first, Is.Not.SameAs(second));

            pool.Dispose();
        }

        [Test]
        public void Pool_Ceiling_RejectsInsteadOfGrowingForever()
        {
            var pool = new UnitViewPool(_root, growBlock: 4, maximumViews: 2);
            Assert.That(pool.Warmup(UnitKinds.Scout, 8), Is.EqualTo(2), "warm-up respects the ceiling too");

            Assert.That(pool.Acquire(UnitKinds.Scout), Is.Not.Null);
            Assert.That(pool.Acquire(UnitKinds.Scout), Is.Not.Null);

            // The ceiling is an error condition (units become invisible), so the log
            // is expected and must not fail the test.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.That(pool.TryAcquire(UnitKinds.Scout, out var rejected), Is.False);
                Assert.That(rejected, Is.Null);
                Assert.That(pool.RejectedCount, Is.EqualTo(1));
                Assert.That(pool.TotalCreated, Is.EqualTo(2));
                Assert.That(pool.InUseCount, Is.EqualTo(2));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            pool.Dispose();
        }

        [Test]
        public void Pool_Release_RejectsRepeatedAndForeignViews()
        {
            Assert.That(_pool.Warmup(UnitKinds.Scout, 2), Is.EqualTo(2));
            var view = _pool.Acquire(UnitKinds.Scout);

            Assert.That(_pool.Release(view), Is.True);
            Assert.That(_pool.Release(view), Is.False, "a double release would put one object on the free list twice");
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(2), "the second release must not duplicate the entry");
            Assert.That(_pool.InUseCount, Is.EqualTo(0));

            var foreign = new GameObject("Foreign").AddComponent<UnitView>();
            Assert.That(_pool.Release(foreign), Is.False);
            Assert.That(_pool.Release(null), Is.False);
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(2));
            Assert.That(_pool.OwnedCount(UnitKinds.Scout), Is.EqualTo(2));

            Object.DestroyImmediate(foreign.gameObject);
        }

        [Test]
        public void Pool_Prototype_IsInstantiatedInPlaceOfThePlaceholder()
        {
            var template = new GameObject("Tank prototype");
            var templateView = template.AddComponent<UnitView>();
            var turret = new GameObject("Turret");
            turret.transform.SetParent(template.transform, false);
            templateView.AssignTurret(turret.transform);

            Assert.That(_pool.SetPrototype(UnitKinds.Tank, template), Is.True);
            Assert.That(_pool.Warmup(UnitKinds.Tank, 2), Is.EqualTo(2));
            Object.DestroyImmediate(template);

            var view = _pool.Acquire(UnitKinds.Tank);
            Assert.That(view, Is.Not.Null);
            Assert.That(view.Hull.parent, Is.EqualTo(_root), "content views live under the pool root");
            Assert.That(view.HasTurret, Is.True, "the prototype's turret reference survived instantiation");
            Assert.That(view.IsPooledView, Is.True);
            Assert.That(view.ViewObject.name, Does.Contain("Tank prototype"));
            Assert.That(_pool.OwnedCount(UnitKinds.Tank), Is.EqualTo(2));
        }

        [Test]
        public void Pool_ReleaseAll_AndDispose_ReturnEveryView()
        {
            Assert.That(_pool.Warmup(UnitKinds.Scout, 3), Is.EqualTo(3));
            var views = new UnitView[3];
            for (var index = 0; index < views.Length; index++)
            {
                views[index] = _pool.Acquire(UnitKinds.Scout);
            }

            Assert.That(_pool.ReleaseAll(), Is.EqualTo(3));
            Assert.That(_pool.InUseCount, Is.EqualTo(0));
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(3));
            Assert.That(_pool.TotalCreated, Is.EqualTo(3));
            Assert.That(ActiveChildCount(), Is.EqualTo(0));

            _pool.Dispose();
            Assert.That(_pool.IsDisposed, Is.True);
            Assert.That(_pool.TotalCreated, Is.EqualTo(0));
            Assert.That(_root.childCount, Is.EqualTo(0), "disposing the pool destroys the views it created");
            Assert.That(_pool.TryAcquire(UnitKinds.Scout, out var after), Is.False);
            Assert.That(after, Is.Null);
            Assert.That(_root, Is.Not.Null, "a caller-supplied root is not the pool's to destroy");
        }

        [Test]
        public void Pool_AcquireAndRelease_DoesNotAllocate()
        {
            // Control first: the recorder has to be proven able to see an allocation
            // before its silence means anything.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            var scoutCount = 8;
            Assert.That(_pool.Warmup(UnitKinds.Scout, scoutCount), Is.EqualTo(scoutCount));
            var tankCount = 4;
            Assert.That(_pool.Warmup(UnitKinds.Tank, tankCount), Is.EqualTo(tankCount));

            var scouts = new UnitView[scoutCount];
            var tanks = new UnitView[tankCount];

            void Churn(int iterations)
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    for (var index = 0; index < scoutCount; index++)
                    {
                        scouts[index] = _pool.Acquire(UnitKinds.Scout);
                    }

                    for (var index = 0; index < tankCount; index++)
                    {
                        tanks[index] = _pool.Acquire(UnitKinds.Tank);
                    }

                    for (var index = 0; index < scoutCount; index++)
                    {
                        _pool.Release(scouts[index]);
                    }

                    for (var index = 0; index < tankCount; index++)
                    {
                        _pool.Release(tanks[index]);
                    }
                }
            }

            Churn(50);
            Assert.That(
                () => Churn(500),
                Is.Not.AllocatingGCMemory(),
                "a firefight's spawn and death churn must come out of the warmed pool");
            Assert.That(_pool.GrowCount, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- the binder

        [Test]
        public void Binder_RejectsMissingDependencies()
        {
            var world = new ClientReplicationWorld(SlotCapacity);
            var buffer = new UnitViewTickBuffer(SlotCapacity, TickSeconds);
            Assert.That(
                () => new UnitViewBinder(null, world, buffer),
                Throws.ArgumentNullException);
            Assert.That(
                () => new UnitViewBinder(_pool, null, buffer),
                Throws.ArgumentNullException);
            Assert.That(
                () => new UnitViewBinder(_pool, world, null),
                Throws.ArgumentNullException);
        }

        [Test]
        public void Binder_SpawnsOneViewPerReplicatedUnit_AndAppliesPosesInMetres()
        {
            CreateMatch(3);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 3), Is.EqualTo(3));

            FeedPacket(10);
            Assert.That(_binder.BoundViewCount, Is.EqualTo(3));
            Assert.That(_binder.SpawnCount, Is.EqualTo(3));
            Assert.That(_pool.InUseCount, Is.EqualTo(3));
            Assert.That(_pool.GrowCount, Is.EqualTo(0), "the warm-up covered the match");
            Assert.That(ActiveChildCount(), Is.EqualTo(3));

            // Only the first unit moves, and only the first unit's pose may have
            // followed: the rest prove the binder addresses views by slot, not by
            // creation order.
            for (var packet = 1; packet < 12; packet++)
            {
                FeedPacket((ulong)(10 + packet * 2), stepX: packet * StepPerPacketMm);
            }

            var slot = SlotOf(1);
            Assert.That(_binder.TryGetView(slot, out var view), Is.True);
            Assert.That(view.EntityValue, Is.EqualTo(1UL));
            Assert.That(view.UnitKind, Is.EqualTo(UnitKinds.Scout));

            // TrySample is idempotent inside one render clock, so the buffer's own
            // answer for this slot is the exact expectation for the view.
            Assert.That(_buffer.TrySample(slot, out var pose), Is.True);
            Assert.That(view.Hull.position.x, Is.EqualTo(pose.XMillimetres * 0.001f).Within(1e-4f));
            Assert.That(view.Hull.position.z, Is.EqualTo(pose.ZMillimetres * 0.001f).Within(1e-4f));
            Assert.That(view.Hull.position.y, Is.EqualTo(0f));
            Assert.That(
                pose.XMillimetres,
                Is.GreaterThan(XMillimetres),
                "the pose under test must be an interpolated one, not the spawn snap");

            var otherSlot = SlotOf(2);
            Assert.That(_binder.TryGetView(otherSlot, out var idle), Is.True);
            Assert.That(idle.Hull.position.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(idle.Hull.position.z, Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void Binder_WritesHullAndTurretYawAsAbsoluteHeadings()
        {
            CreateMatch(0);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 1), Is.EqualTo(1));
            Assert.That(_pool.Warmup(UnitKinds.Tank, 1), Is.EqualTo(1));

            // Heading north at 0 degrees while the turret tracks an enemy due east at
            // 90: two different angles, so a copy-paste of the hull yaw onto the
            // turret cannot pass.
            Assert.That(
                _world.ApplyAdd(new DeltaAddRecord(
                    new EntityId(1),
                    new PlayerId(1),
                    new WorldPointMm(XMillimetres, ZMillimetres),
                    PrototypeHealth,
                    true,
                    new WorldPointMm(XMillimetres, MoveTargetZMillimetres),
                    new EntityId(2),
                    false,
                    UnitKinds.Scout)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(
                _world.ApplyAdd(Add(2, TurretTargetXMillimetres, ZMillimetres, UnitKinds.Tank)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            for (var packet = 0; packet < 20; packet++)
            {
                FeedPacket((ulong)(10 + packet * 2));
            }

            var slot = SlotOf(1);
            Assert.That(_binder.TryGetView(slot, out var view), Is.True);
            Assert.That(view.Hull.eulerAngles.y, Is.EqualTo(0f).Within(0.5f), "hull tracks its move target");
            Assert.That(view.Turret.eulerAngles.y, Is.EqualTo(90f).Within(0.5f), "turret tracks its attack target");

            Assert.That(_buffer.TrySample(slot, out var pose), Is.True);
            Assert.That(pose.TurretYawDegrees, Is.EqualTo(90f).Within(0.5f));
            Assert.That(pose.BodyYawDegrees, Is.EqualTo(0f).Within(0.5f));
        }

        [Test]
        public void Binder_HoldsAuthorityPositionUntilTheRingRecapturesTheSlot()
        {
            CreateMatch(0);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 2), Is.EqualTo(2));

            AddUnit(1);
            _buffer.CaptureTick(10, _world);

            // Binding frame: the ring behind this slot is already tick 10, but the
            // view was created after it was captured, so nothing is applied yet.
            _binder.Render(FrameSeconds);
            Assert.That(_binder.BoundViewCount, Is.EqualTo(1));
            Assert.That(_binder.TryGetView(SlotOf(1), out var view), Is.True);
            Assert.That(view.Hull.position.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(view.Hull.position.z, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(view.Health, Is.EqualTo(0), "the spawn snap carries no pose, and none was applied");

            // A recycled slot is the case the gate exists for: the ring still holds
            // the dead unit's samples until the next capture.
            Assert.That(_world.ApplyRemove(Destroy(1)), Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(
                _world.ApplyAdd(Add(7, -4000, 6000, UnitKinds.Scout)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            _binder.Render(FrameSeconds);

            Assert.That(_binder.RebindCount, Is.EqualTo(1));
            Assert.That(_binder.TryGetView(SlotOf(7), out var rebound), Is.True);
            Assert.That(rebound.EntityValue, Is.EqualTo(7UL));
            Assert.That(rebound.Hull.position.x, Is.EqualTo(-4f).Within(1e-4f));
            Assert.That(rebound.Hull.position.z, Is.EqualTo(6f).Within(1e-4f));

            FeedPacket(12);
            Assert.That(rebound.EntityValue, Is.EqualTo(7UL));
            Assert.That(rebound.Health, Is.EqualTo(PrototypeHealth), "the fresh capture is now applied");
            Assert.That(rebound.HealthFraction, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void Binder_Despawn_ReturnsTheViewToThePoolForReuse()
        {
            CreateMatch(2);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 2), Is.EqualTo(2));
            FeedPacket(10);

            Assert.That(_binder.TryGetView(SlotOf(2), out var doomed), Is.True);
            Assert.That(doomed.ViewObject.activeSelf, Is.True);

            Assert.That(_world.ApplyRemove(Destroy(2)), Is.EqualTo(ClientWorldApplyResult.Ok));
            FeedPacket(12, entity: 1);

            Assert.That(_binder.BoundViewCount, Is.EqualTo(1));
            Assert.That(_binder.ReleaseCount, Is.EqualTo(1));
            Assert.That(_pool.InUseCount, Is.EqualTo(1));
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(1));
            AssertDeactivated(doomed, "a dead unit's view is deactivated, not destroyed");
            Assert.That(doomed.IsBound, Is.False);
            Assert.That(doomed.Health, Is.EqualTo(0), "the readout must not survive the unit");

            // The next spawn in that slot must come back out of the pool.
            AddUnit(5);
            FeedPacket(14, entity: 1);
            Assert.That(_binder.BoundViewCount, Is.EqualTo(2));
            Assert.That(_binder.TryGetView(SlotOf(5), out var respawned), Is.True);
            Assert.That(respawned, Is.SameAs(doomed), "presentation recycles; it never instantiates");
            Assert.That(_pool.TotalCreated, Is.EqualTo(2));
            Assert.That(_pool.GrowCount, Is.EqualTo(0));
        }

        [Test]
        public void Binder_RebindsWhenARecycledSlotChangesHandsMidFrame()
        {
            CreateMatch(2);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 4), Is.EqualTo(4));
            FeedPacket(10);

            var slotA = SlotOf(1);
            Assert.That(_binder.TryGetView(slotA, out var viewA), Is.True);

            // Both the death and the replacement land before the next presentation
            // frame, which is how a table slot is really recycled in a firefight.
            Assert.That(_world.ApplyRemove(Destroy(1)), Is.EqualTo(ClientWorldApplyResult.Ok));
            AddUnit(9);
            Assert.That(SlotOf(9), Is.EqualTo(slotA), "the freed slot is the recycled one");

            for (var packet = 1; packet < 4; packet++)
            {
                FeedPacket((ulong)(10 + packet * 2), entity: 9, stepX: packet * StepPerPacketMm);
            }

            Assert.That(_binder.RebindCount, Is.EqualTo(1));
            Assert.That(_binder.TryGetView(slotA, out var rebound), Is.True);
            Assert.That(rebound, Is.SameAs(viewA), "the same frame that frees a view is the first to take it back");
            Assert.That(rebound.EntityValue, Is.EqualTo(9UL));
            Assert.That(rebound.IsBound, Is.True);
            Assert.That(_pool.InUseCount, Is.EqualTo(2));
            Assert.That(_binder.BoundViewCount, Is.EqualTo(2));
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(2));
            Assert.That(_pool.TotalCreated, Is.EqualTo(4), "a hand-over inside one frame allocates nothing");
        }

        [Test]
        public void Binder_Resync_RebindsEverySlotWithoutInstantiating()
        {
            CreateMatch(3);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 3), Is.EqualTo(3));
            FeedPacket(10);
            var bound = new UnitView[3];
            for (var slot = 0; slot < 3; slot++)
            {
                Assert.That(_binder.TryGetView(slot, out bound[slot]), Is.True);
            }

            var created = _pool.TotalCreated;
            _binder.HandleResync(200);

            Assert.That(_binder.BoundViewCount, Is.EqualTo(0));
            Assert.That(_binder.ReleaseCount, Is.EqualTo(3));
            Assert.That(_pool.InUseCount, Is.EqualTo(0));
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(3));
            Assert.That(_buffer.CapturedTick, Is.EqualTo(0UL), "the ring was re-primed");
            Assert.That(_buffer.RenderTick, Is.EqualTo(200 - _buffer.RenderDelayTicks).Within(0.001));

            // The keyframe comes back with a different set of units, in different
            // slots: presentation has to follow it out of the same objects.
            _world.Reset();
            AddUnit(11);
            AddUnit(12);
            FeedPacket(202, entity: 11);

            Assert.That(_binder.BoundViewCount, Is.EqualTo(2));
            Assert.That(_pool.TotalCreated, Is.EqualTo(created), "a resync costs pops and activations, not instances");
            Assert.That(_pool.GrowCount, Is.EqualTo(0));
            var reacquired = 0;
            for (var slot = 0; slot < 2; slot++)
            {
                Assert.That(_binder.TryGetView(slot, out var view), Is.True);
                for (var index = 0; index < bound.Length; index++)
                {
                    if (ReferenceEquals(view, bound[index]))
                    {
                        reacquired++;
                    }
                }
            }

            Assert.That(reacquired, Is.EqualTo(2));
        }

        [Test]
        public void Binder_LeavesUnitUnpresented_WhenThePoolIsAtItsCeiling()
        {
            _pool.Dispose();
            _pool = new UnitViewPool(_root, growBlock: 1, maximumViews: 1);
            CreateMatch(2);

            LogAssert.ignoreFailingMessages = true;
            try
            {
                _buffer.CaptureTick(10, _world);
                _binder.Render(FrameSeconds);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            Assert.That(_binder.BoundViewCount, Is.EqualTo(1), "one view exists, so one unit is presented");
            Assert.That(_binder.UnpresentedCount, Is.GreaterThan(0));
            Assert.That(_pool.RejectedCount, Is.GreaterThan(0));
            Assert.That(_pool.TotalCreated, Is.EqualTo(1));
            Assert.That(ActiveChildCount(), Is.EqualTo(1));
            Assert.That(_binder.ReleaseAllViews(), Is.EqualTo(1));
        }

        [Test]
        public void Binder_ZeroDelta_HoldsEveryViewInPlace()
        {
            CreateMatch(2);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 2), Is.EqualTo(2));
            FeedPacket(10, entity: 1, stepX: StepPerPacketMm);

            Assert.That(_binder.TryGetView(SlotOf(1), out var view), Is.True);
            var held = view.Hull.position;
            var heldTicks = _buffer.RenderTick;

            for (var frame = 0; frame < 60; frame++)
            {
                _binder.Render(0.0);
            }

            Assert.That(_buffer.RenderTick, Is.EqualTo(heldTicks), "the OD-18 pause must not run the clock");
            Assert.That(view.Hull.position, Is.EqualTo(held));
            Assert.That(_binder.BoundViewCount, Is.EqualTo(2));
            Assert.That(_pool.InUseCount, Is.EqualTo(2));
        }

        [Test]
        public void Binder_ReleaseAllViews_KeepsThePoolWarmForTheNextMatch()
        {
            CreateMatch(3);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 3), Is.EqualTo(3));
            FeedPacket(10);

            Assert.That(_binder.ReleaseAllViews(), Is.EqualTo(3));
            Assert.That(_binder.BoundViewCount, Is.EqualTo(0));
            Assert.That(_pool.InUseCount, Is.EqualTo(0));
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(3));
            Assert.That(_pool.TotalCreated, Is.EqualTo(3));
            Assert.That(ActiveChildCount(), Is.EqualTo(0));

            // Re-entering the same match hands the very same objects back.
            FeedPacket(12);
            Assert.That(_binder.BoundViewCount, Is.EqualTo(3));
            Assert.That(_pool.TotalCreated, Is.EqualTo(3));
            Assert.That(_pool.GrowCount, Is.EqualTo(0));
        }

        [Test]
        public void Binder_PresentationFrame_DoesNotAllocate()
        {
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            const int Units = 8;
            CreateMatch(Units);
            Assert.That(_pool.Warmup(UnitKinds.Scout, Units), Is.EqualTo(Units));

            void Drive(int packets, ulong startTick)
            {
                // No assertions in here: an NUnit constraint allocates, and the whole
                // point of the window is that nothing does.
                var tick = startTick;
                for (var packet = 0; packet < packets; packet++)
                {
                    for (var index = 0; index < Units; index++)
                    {
                        _world.ApplyUpdate(new DeltaUpdateRecord(
                            new EntityId((ulong)(index + 1)),
                            (byte)(UnitDirtyMask.Position | UnitDirtyMask.Health),
                            new PlayerId(1),
                            new WorldPointMm(
                                XMillimetres + (int)tick * StepPerPacketMm,
                                ZMillimetres + index * 10),
                            90,
                            false,
                            new WorldPointMm(0, 0),
                            new EntityId(0),
                            false));
                    }

                    _buffer.CaptureTick(tick, _world);
                    for (var frame = 0; frame < 6; frame++)
                    {
                        _binder.Render(FrameSeconds);
                    }

                    tick += 2;
                }
            }

            Drive(20, 2);
            Assert.That(_binder.BoundViewCount, Is.EqualTo(Units));
            Assert.That(_pool.GrowCount, Is.EqualTo(0), "the measured window must be steady state");

            Assert.That(
                () => Drive(200, 202),
                Is.Not.AllocatingGCMemory(),
                "the per-frame presentation loop must be allocation-free at any unit count");

            Assert.That(_binder.BoundViewCount, Is.EqualTo(Units));
            Assert.That(_binder.PeakViewCount, Is.EqualTo(Units));
            Assert.That(_pool.TotalCreated, Is.EqualTo(Units));
            Assert.That(_buffer.StarvedSampleCount, Is.EqualTo(0), "a 10 Hz feed kept the ring bracketed");
        }

        [Test]
        public void Binder_TracksThePeakItNeedsSoTheLoadScreenCanWarmUpToIt()
        {
            _pool.Dispose();
            _pool = new UnitViewPool(_root, growBlock: 2);
            CreateMatch(1);
            Assert.That(_pool.Warmup(UnitKinds.Scout, 1), Is.EqualTo(1));
            FeedPacket(10);

            AddUnit(2);
            AddUnit(3);
            FeedPacket(12);
            Assert.That(_binder.PeakViewCount, Is.EqualTo(3));
            Assert.That(_pool.GrowCount, Is.EqualTo(1), "the frame that outgrew a one-view warm-up grew by a block");
            Assert.That(_pool.TotalCreated, Is.EqualTo(3));

            Assert.That(_world.ApplyRemove(Destroy(2)), Is.EqualTo(ClientWorldApplyResult.Ok));
            FeedPacket(14);
            Assert.That(_binder.PeakViewCount, Is.EqualTo(3), "the peak is what to warm to, not the current count");
            Assert.That(_binder.BoundViewCount, Is.EqualTo(2));
        }
    }
}
