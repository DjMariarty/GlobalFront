using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;
using EntityId = GlobalFront.Core.Model.EntityId;
using Object = UnityEngine.Object;

namespace GlobalFront.Tests.EditMode.Client.Presentation
{
    /// <summary>
    /// Step 4.5.2 (OD-42): view batch recycling across a keyframe re-baseline.
    ///
    /// A keyframe wipes and rebuilds the client table, so every slot index may
    /// describe a different unit afterwards. The presentation layer therefore has
    /// to release all 400 views to the warmed pool and re-acquire them in the next
    /// frame without a single <c>Instantiate</c>/<c>Destroy</c> or managed
    /// allocation, re-prime the interpolation ring at the snapshot tick, and keep
    /// the first post-resync pose snapped rather than blending across the break.
    ///
    /// The runner-level tests drive a real <see cref="RemoteSliceSession"/> over
    /// the deterministic virtual transport: the keyframe is fed to the session's
    /// own receiver, so "the event moved the presentation" is a claim about the
    /// wired path, not about a manual <c>HandleResync</c> call.
    /// </summary>
    [TestFixture]
    public sealed class ViewBatchRecyclingTests
    {
        private const int BatchUnits = 400;
        private const int LocalCapacity = 512;
        private const int RemoteFriendlyUnits = 8;
        private const int RemoteEnemyUnits = 2;
        private const int RemoteUnitCount = RemoteFriendlyUnits + RemoteEnemyUnits;
        private const double TickSeconds = 0.05;
        private const double FrameSeconds = 0.05;

        private Transform _root;
        private UnitViewPool _pool;
        private ClientReplicationWorld _world;
        private UnitViewTickBuffer _buffer;
        private UnitViewBinder _binder;

        private readonly List<TacticalVerticalSliceRunner> _runners =
            new List<TacticalVerticalSliceRunner>();
        private readonly List<RemoteRig> _rigs = new List<RemoteRig>();
        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("ViewBatchRecyclingTests_Root").transform;
        }

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _runners.Count; index++)
            {
                _runners[index]?.Shutdown();
            }

            _runners.Clear();

            for (var index = 0; index < _rigs.Count; index++)
            {
                _rigs[index]?.Dispose();
            }

            _rigs.Clear();

            _pool?.Dispose();
            _pool = null;
            _binder = null;
            _buffer = null;
            _world = null;

            if (_root != null)
            {
                Object.DestroyImmediate(_root.gameObject);
            }

            for (var index = 0; index < _createdObjects.Count; index++)
            {
                if (_createdObjects[index] != null)
                {
                    Object.DestroyImmediate(_createdObjects[index]);
                }
            }

            _createdObjects.Clear();
        }

        // ---------------------------------------------------------------- runner wiring

        /// <summary>
        /// The brief's invariant: this test never calls
        /// <see cref="UnitViewBinder.HandleResync"/> itself. Only the receiver's
        /// event may release the views, so a missing subscription leaves the ten
        /// old bindings in place and the first assertion fails with
        /// "Expected: 0 But was: 10".
        /// </summary>
        [Test]
        public void Runner_WiresReceiverKeyframeToBinderHandleResync()
        {
            var rig = CreateRemoteRig();
            rig.StartMatch(RemoteFriendlyUnits, RemoteEnemyUnits);
            Assert.That(rig.PumpUntilStreaming(RemoteUnitCount), Is.True,
                "the client must install its baseline: " + rig.Describe());

            var runner = BuildRemoteRunner(rig.Session);
            runner.StepPresentationFrame(0.05f, 0.05);

            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(RemoteUnitCount),
                "every replicated unit must be presented before the keyframe");
            Assert.That(runner.ViewPool.InUseCount, Is.EqualTo(RemoteUnitCount));

            var snapshotTick = runner.CurrentTick + 100UL;
            var outcome = runner.Receiver.ReceiveKeyframe(
                rig.Server.Clock.NowMs,
                keyframeSeq: 2,
                snapshotTick,
                rig.Keyframe);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(0),
                "KeyframeInstalled must drive the binder resync on its own");
            Assert.That(runner.ViewPool.InUseCount, Is.EqualTo(0),
                "every view must be back in the pool, not destroyed");
            Assert.That(
                runner.ViewPool.FreeCount(UnitKinds.Scout),
                Is.EqualTo(RemoteUnitCount));
            Assert.That(runner.ViewPool.TotalCreated, Is.EqualTo(RemoteUnitCount));

            Assert.That(runner.TickBuffer.CapturedTick, Is.EqualTo(0UL),
                "the ring must have been re-primed at the keyframe");
            Assert.That(
                runner.TickBuffer.RenderTick,
                Is.EqualTo(snapshotTick - runner.TickBuffer.RenderDelayTicks).Within(1e-9),
                "the render clock must restart behind the snapshot tick");

            // The next frame must re-acquire the same objects from the warm pool.
            runner.StepPresentationFrame(0.05f, 0.10);
            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(RemoteUnitCount));
            Assert.That(runner.ViewPool.TotalCreated, Is.EqualTo(RemoteUnitCount));
            Assert.That(runner.ViewPool.GrowCount, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- 400-view batch

        [Test]
        public void Resync_400Units_ReturnsAllViewsToPool_WithoutDestroy()
        {
            CreateBinderRig();
            Assert.That(_pool.Warmup(UnitKinds.Scout, BatchUnits), Is.EqualTo(BatchUnits));
            FillWorld(BatchUnits);

            _binder.Render(FrameSeconds);

            Assert.That(_binder.BoundViewCount, Is.EqualTo(BatchUnits));
            Assert.That(_pool.InUseCount, Is.EqualTo(BatchUnits));
            Assert.That(_pool.TotalCreated, Is.EqualTo(BatchUnits));
            Assert.That(_pool.GrowCount, Is.EqualTo(0));

            var views = new UnitView[BatchUnits];
            for (var slot = 0; slot < BatchUnits; slot++)
            {
                Assert.That(_binder.TryGetView(slot, out views[slot]), Is.True);
            }

            _binder.HandleResync(500UL);

            Assert.That(_binder.BoundViewCount, Is.EqualTo(0));
            Assert.That(_binder.ReleaseCount, Is.EqualTo(BatchUnits));
            Assert.That(_pool.InUseCount, Is.EqualTo(0));
            Assert.That(_pool.FreeCount(UnitKinds.Scout), Is.EqualTo(BatchUnits));
            Assert.That(_pool.TotalCreated, Is.EqualTo(BatchUnits));
            Assert.That(_pool.GrowCount, Is.EqualTo(0));
            Assert.That(_binder.DestroyedViewCount, Is.EqualTo(0));

            for (var index = 0; index < BatchUnits; index++)
            {
                Assert.That(views[index], Is.Not.Null,
                    "a released view must return to the pool, never be Destroy()ed");
                Assert.That(views[index].ViewObject, Is.Not.Null);
                Assert.That(views[index].ViewObject.activeSelf, Is.False);
                Assert.That(views[index].IsBound, Is.False);
            }
        }

        [Test]
        public void Resync_400Units_ViewBatchReacquisition_AllocatesZeroGC()
        {
            CreateBinderRig();
            Assert.That(_pool.Warmup(UnitKinds.Scout, BatchUnits), Is.EqualTo(BatchUnits));
            FillWorld(BatchUnits);
            _binder.Render(FrameSeconds);
            _binder.HandleResync(500UL);

            // The keyframe's roster: different entities, same archetype, so every
            // view has to come out of the warmed pool rather than be created.
            _world.Reset();
            FillWorld(BatchUnits, firstEntity: 1000, baseZMillimetres: 1000);

            byte[] control = null;
            var controlSamples = CountGcAllocations(() => control = new byte[4096]);
            Assert.That(control, Is.Not.Null);
            Assert.That(controlSamples, Is.GreaterThan(0),
                "the GC.Alloc recorder must observe a known allocation");

            var acquisitionSamples = CountGcAllocations(() => _binder.Render(FrameSeconds));
            var steadySamples = CountGcAllocations(() => _binder.Render(FrameSeconds));

            Assert.That(acquisitionSamples, Is.EqualTo(0),
                "re-acquiring 400 views from the pool must not allocate");
            Assert.That(steadySamples, Is.EqualTo(0));
            Assert.That(_binder.BoundViewCount, Is.EqualTo(BatchUnits));
            Assert.That(_pool.TotalCreated, Is.EqualTo(BatchUnits),
                "the whole batch must be recycled, not instantiated");
            Assert.That(_pool.GrowCount, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- tick buffer rebase

        [Test]
        public void Resync_ReAnchorsTickBuffer_FirstSampleResolvesToSnapped()
        {
            CreateBinderRig();
            AddUnit(1UL, UnitKinds.Scout, xMillimetres: 12_000, zMillimetres: -4_000);

            _buffer.Resync(1000UL);

            Assert.That(_buffer.CapturedTick, Is.EqualTo(0UL),
                "Flush must drop the pre-resync history");
            Assert.That(_buffer.RenderDelayTicks,
                Is.EqualTo(UnitViewTickBuffer.MinRenderDelayTicks));
            Assert.That(
                _buffer.RenderTick,
                Is.EqualTo(1000.0 - _buffer.RenderDelayTicks).Within(1e-9),
                "the render clock restarts one play-out delay behind the base");

            _buffer.CaptureTick(999UL, _world);
            Assert.That(_buffer.CapturedTick, Is.EqualTo(0UL),
                "pre-resync traffic must be refused");
            Assert.That(_buffer.StalePacketCount, Is.EqualTo(1));

            _buffer.CaptureTick(1001UL, _world);

            Assert.That(_buffer.TrySample(0, out var pose), Is.True);
            Assert.That(pose.Source, Is.EqualTo(UnitViewPoseSource.Snapped),
                "the first pose after a re-base must snap to authority");
            Assert.That(pose.XMillimetres, Is.EqualTo(12_000f));
            Assert.That(pose.ZMillimetres, Is.EqualTo(-4_000f));
        }

        [Test]
        public void Resync_RecycledSlot_DoesNotInheritDeadUnitYaw()
        {
            CreateBinderRig();
            Assert.That(_pool.Warmup(UnitKinds.Scout, 1), Is.EqualTo(1));

            // Slot 0 holds a casualty whose heading points due east (90 degrees).
            AddUnit(1UL, UnitKinds.Scout, xMillimetres: 0, zMillimetres: 0,
                moveTargetXMillimetres: 10_000, moveTargetZMillimetres: 0);
            _buffer.CaptureTick(100UL, _world);
            _binder.Render(FrameSeconds);

            Assert.That(SlotOf(1UL), Is.EqualTo(0));
            Assert.That(_buffer.TrySample(0, out var deadPose), Is.True);
            Assert.That(deadPose.BodyYawDegrees, Is.EqualTo(90f).Within(1f),
                "the casualty's 90 degree heading must be established first");

            _binder.HandleResync(200UL);

            // The keyframe rebuilds the table; the freed slot goes to a new unit
            // that has no heading history of its own (no move target yet).
            _world.Reset();
            AddUnit(2UL, UnitKinds.Scout, xMillimetres: 0, zMillimetres: 0);
            Assert.That(SlotOf(2UL), Is.EqualTo(0), "the new unit takes the freed slot");

            _buffer.CaptureTick(201UL, _world);

            Assert.That(_buffer.TrySample(0, out var replacement), Is.True);
            Assert.That(replacement.Source, Is.EqualTo(UnitViewPoseSource.Snapped));
            Assert.That(replacement.BodyYawDegrees, Is.EqualTo(0f).Within(1f),
                "the replacement must not inherit the dead unit's 90 degree yaw");
        }

        // ---------------------------------------------------------------- teardown

        [Test]
        public void Runner_Teardown_UnsubscribesBinderFromReceiver()
        {
            var rig = CreateRemoteRig();
            rig.StartMatch(RemoteFriendlyUnits, RemoteEnemyUnits);
            Assert.That(rig.PumpUntilStreaming(RemoteUnitCount), Is.True,
                "the client must install its baseline: " + rig.Describe());

            var runner = BuildRemoteRunner(rig.Session);
            runner.StepPresentationFrame(0.05f, 0.05);

            var receiver = runner.Receiver;
            var binder = runner.Binder;
            var buffer = runner.TickBuffer;
            var renderTickBefore = buffer.RenderTick;

            runner.Shutdown();

            Assert.That(binder.BoundViewCount, Is.EqualTo(0),
                "Shutdown detaches and releases every view");
            Assert.That(buffer.RenderTick, Is.EqualTo(renderTickBefore),
                "Shutdown itself must not touch the render clock");

            Assert.DoesNotThrow(() => receiver.ReceiveKeyframe(
                rig.Server.Clock.NowMs,
                keyframeSeq: 3,
                tick: 10_000UL,
                rig.Keyframe));

            Assert.That(buffer.RenderTick, Is.EqualTo(renderTickBefore),
                "a torn-down binder must not receive the keyframe: the subscription leaked");
            Assert.That(buffer.CapturedTick, Is.EqualTo(0UL));
            Assert.That(binder.BoundViewCount, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- local rig helpers

        private void CreateBinderRig()
        {
            _pool = new UnitViewPool(_root);
            _world = new ClientReplicationWorld(LocalCapacity);
            _buffer = new UnitViewTickBuffer(LocalCapacity, TickSeconds);
            _binder = new UnitViewBinder(_pool, _world, _buffer);
        }

        private void FillWorld(int count, int firstEntity = 1, int baseZMillimetres = 0)
        {
            for (var index = 0; index < count; index++)
            {
                AddUnit(
                    (ulong)(firstEntity + index),
                    UnitKinds.Scout,
                    xMillimetres: index * 100,
                    zMillimetres: baseZMillimetres);
            }
        }

        private void AddUnit(
            ulong entity,
            byte kind,
            int xMillimetres = 0,
            int zMillimetres = 0,
            int moveTargetXMillimetres = 0,
            int moveTargetZMillimetres = 0,
            int health = 100)
        {
            var record = new DeltaAddRecord(
                new EntityId(entity),
                new PlayerId(1),
                new WorldPointMm(xMillimetres, zMillimetres),
                health,
                moveTargetXMillimetres != 0 || moveTargetZMillimetres != 0,
                new WorldPointMm(moveTargetXMillimetres, moveTargetZMillimetres),
                new EntityId(0),
                false,
                kind);
            Assert.That(_world.ApplyAdd(in record), Is.EqualTo(ClientWorldApplyResult.Ok));
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

        private static int CountGcAllocations(Action action)
        {
            var recorder = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try
            {
                action();
            }
            finally
            {
                recorder.enabled = false;
                recorder.CollectFromAllThreads();
            }

            return recorder.sampleBlockCount;
        }

        // ---------------------------------------------------------------- remote rig helpers

        private RemoteRig CreateRemoteRig()
        {
            var rig = RemoteRig.Create();
            _rigs.Add(rig);
            return rig;
        }

        private TacticalVerticalSliceRunner BuildRemoteRunner(RemoteSliceSession session)
        {
            var go = new GameObject("ViewBatchRecyclingTests_Slice");
            _createdObjects.Add(go);
            var runner = go.AddComponent<TacticalVerticalSliceRunner>();
            _runners.Add(runner);
            runner.InitializeDedicatedClient(session, createVisuals: false, autoStartRealTime: false);
            return runner;
        }

        /// <summary>
        /// Real Phase 2.5/2.6 stack over the deterministic virtual pipe: a
        /// session-managed host, its replication emitter, and the same
        /// bridge/receiver pair the dedicated client run uses.
        /// </summary>
        private sealed class RemoteRig : IDisposable
        {
            public TransportTestSupport.Rig Server;
            public ServerReplicationEmitter Emitter;
            public RemoteSliceSession Session;
            public DeltaAddRecord[] Keyframe;

            public static RemoteRig Create()
            {
                var rig = new RemoteRig
                {
                    Server = TransportTestSupport.CreateRig(
                        new ImpairmentProfile { LatencyMs = 10 })
                };

                rig.Emitter = rig.Server.Host.AttachReplication(
                    new TransportReplicationAdapter(rig.Server.ServerTransport));

                var carrier = OwnDatagramCarrier.CreateClient(
                    rig.Server.Pipe,
                    rig.Server.Pipe.CreateEndpoint(),
                    TransportTestSupport.ServerAddressOf(rig.Server),
                    rig.Server.Clock);
                rig.Session = new RemoteSliceSession(carrier, rig.Server.Clock);
                rig.Session.Connect("virtual", 0);

                Assert.That(rig.PumpUntil(() => rig.Session.IsConnected, 10000), Is.True,
                    "the remote session must complete the handshake: " + rig.Describe());
                return rig;
            }

            public void StartMatch(int friendlyCount, int enemyCount)
            {
                var enemySession = Server.Host.CreateSession(Server.Host.CreateConnectionHandle());
                Assert.That(
                    Server.Host.TryJoinMatch(enemySession, Server.Match, out var enemyPlayer),
                    Is.EqualTo(JoinResult.Assigned),
                    "the enemy session must join before the match starts");

                var specs = new UnitSpawnSpec[friendlyCount + enemyCount];
                for (var index = 0; index < specs.Length; index++)
                {
                    var friendly = index < friendlyCount;
                    specs[index] = new UnitSpawnSpec(
                        friendly ? Session.Endpoint.Player : enemyPlayer,
                        new WorldPointMm(friendly ? -10_000 + index * 200 : 10_000 + index * 200, 0),
                        350,
                        false,
                        UnitKinds.Scout);
                }

                var config = new MatchConfig(TransportTestSupport.StandardStats, specs);
                Assert.That(
                    Server.Host.TryStartSessionMatch(Server.Match, config, out var entityIds),
                    Is.True,
                    "the session match must start for both joined players");
                Assert.That(entityIds.Length, Is.EqualTo(specs.Length));

                Keyframe = new DeltaAddRecord[entityIds.Length];
                for (var index = 0; index < entityIds.Length; index++)
                {
                    Keyframe[index] = new DeltaAddRecord(
                        entityIds[index],
                        specs[index].Owner,
                        specs[index].Position,
                        TransportTestSupport.StandardStats.MaximumHealth,
                        false,
                        default,
                        new EntityId(0),
                        false,
                        UnitKinds.Scout);
                }
            }

            public void Pump(long milliseconds)
            {
                Server.Pipe.Advance(milliseconds);
                Server.Clock.Advance(milliseconds);
                Server.ServerTransport.Pump(Server.Clock.NowMs);
                Session.Pump(Server.Clock.NowMs);
            }

            public bool PumpUntil(Func<bool> condition, long budgetMs, long stepMs = 5)
            {
                var elapsed = 0L;
                while (elapsed < budgetMs)
                {
                    Pump(stepMs);
                    elapsed += stepMs;
                    if (condition())
                    {
                        return true;
                    }
                }

                return condition();
            }

            public bool PumpUntilStreaming(int expectedUnits, long budgetMs = 15000, long stepMs = 5)
            {
                var elapsed = 0L;
                while (elapsed < budgetMs)
                {
                    Server.Host.TickOnce();
                    Pump(stepMs);
                    elapsed += stepMs;
                    if (Session.World.LiveCount == expectedUnits &&
                        Session.Receiver.State == ReplicationReceiverState.Streaming)
                    {
                        return true;
                    }
                }

                return false;
            }

            public string Describe()
            {
                var receiver = Session.Receiver;
                return
                    $"endpoint={Session.Endpoint.State} tick={Server.Host.CurrentTick} " +
                    $"receiver={receiver.State} last={receiver.LastAppliedTick} " +
                    $"live={Session.World.LiveCount} keyframes={receiver.KeyframeCount} " +
                    $"emitter keyframes={Emitter.EmittedKeyframeCount} " +
                    $"bridge kf={Session.Bridge.AssembledKeyframeCount}";
            }

            public void Dispose()
            {
                Session?.Dispose();
                Server?.ServerCarrier?.Dispose();
            }
        }
    }
}
