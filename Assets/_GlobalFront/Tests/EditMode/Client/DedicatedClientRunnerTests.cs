using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Client.UI;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;
using EntityId = GlobalFront.Core.Model.EntityId;
using Object = UnityEngine.Object;

namespace GlobalFront.Tests.EditMode.Client
{
    /// <summary>
    /// Phase 4, step 4.2 (OD-38): the tactical slice as a client of a remote
    /// dedicated server.
    ///
    /// The rig is the real Phase 2.5/2.6 stack over the deterministic virtual
    /// pipe: a session-managed host, its replication emitter, a full client
    /// endpoint and the same bridge/receiver pair production uses. Nothing in
    /// these tests reaches into the runner to fake a snapshot, which is what
    /// makes "the network moved the view" a claim about the wired path.
    /// </summary>
    [TestFixture]
    public sealed class DedicatedClientRunnerTests
    {
        private sealed class RemoteRig : IDisposable
        {
            public TransportTestSupport.Rig Server;
            public ServerReplicationEmitter Emitter;
            public RemoteSliceSession Session;

            public static RemoteRig Create(int latencyMs = 10)
            {
                var rig = new RemoteRig
                {
                    Server = TransportTestSupport.CreateRig(
                        new ImpairmentProfile { LatencyMs = latencyMs })
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

            /// <summary>
            /// Starts a two-sided match: the connected client plus a host-side
            /// session that stands in for the opposing player. One owner would
            /// make <see cref="SkirmishCombatRelation"/> declare a victory on
            /// the first tick, and a finished match rejects every command.
            /// </summary>
            public EntityId[] StartMatch()
            {
                var enemySession = Server.Host.CreateSession(
                    Server.Host.CreateConnectionHandle());
                Assert.That(
                    Server.Host.TryJoinMatch(enemySession, Server.Match, out var enemyPlayer),
                    Is.EqualTo(JoinResult.Assigned));

                var specs = new[]
                {
                    new UnitSpawnSpec(
                        Session.Endpoint.Player, new WorldPointMm(-10_000, 0), 350, false),
                    new UnitSpawnSpec(
                        enemyPlayer, new WorldPointMm(10_000, 0), 350, false)
                };
                var config = new MatchConfig(TransportTestSupport.StandardStats, specs);
                Assert.That(
                    Server.Host.TryStartSessionMatch(Server.Match, config, out var entityIds),
                    Is.True,
                    "the session match must start for both joined players");
                Assert.That(entityIds.Length, Is.EqualTo(2));
                return entityIds;
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

            /// <summary>Pumps authoritative ticks until the client installed its baseline.</summary>
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
                    $"base={receiver.BaseKeyframeTick} live={Session.World.LiveCount} " +
                    $"failure={receiver.FailureReason} " +
                    $"deltas={receiver.AppliedDeltaCount} requests={receiver.TotalRequestCount} " +
                    $"emitter deltas={Emitter.EmittedDeltaCount} keyframes={Emitter.EmittedKeyframeCount} " +
                    $"slices={Emitter.EmittedSliceCount} requests={Emitter.RequestCount} " +
                    $"bridge kf={Session.Bridge.AssembledKeyframeCount} " +
                    $"acks={Session.Bridge.SentAckCount} malformed={Session.Bridge.MalformedPayloadCount} " +
                    $"carrier recv={Session.Carrier.Metrics.DatagramsReceived}";
            }

            public void Dispose()
            {
                Session?.Dispose();
                Server?.ServerCarrier?.Dispose();
            }
        }

        private readonly List<TacticalVerticalSliceRunner> _runners =
            new List<TacticalVerticalSliceRunner>();
        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private readonly List<RemoteRig> _rigs = new List<RemoteRig>();

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

            for (var index = 0; index < _createdObjects.Count; index++)
            {
                if (_createdObjects[index] != null)
                {
                    Object.DestroyImmediate(_createdObjects[index]);
                }
            }

            _createdObjects.Clear();
        }

        [Test]
        public void DedicatedClient_DoesNotSpawnLocalHost()
        {
            var rig = CreateRig();
            var runner = BuildRunner(rig.Session);

            Assert.That(runner.NetworkMode, Is.EqualTo(SliceNetworkMode.DedicatedClient));
            Assert.That(runner.Host, Is.Null,
                "a dedicated client must never create a local match host");
            Assert.That(rig.Server.Host.UnitCount, Is.EqualTo(0),
                "initializing a dedicated client must not start anything on the server either");
        }

        [Test]
        public void DedicatedClient_SelectionMove_RoutesThroughNetworkCommandChannel()
        {
            var rig = CreateRig();
            var entities = rig.StartMatch();
            Assert.That(rig.PumpUntilStreaming(2), Is.True,
                "the client must install its baseline: " + rig.Describe());

            var runner = BuildRunner(rig.Session);
            runner.Binder.Render(0.0);
            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(2),
                "the pool must be warm for the replicated roster before the first frame");

            var entity = entities[0];
            var slot = FindSlot(runner.World, entity);
            Assert.That(slot, Is.GreaterThanOrEqualTo(0), "the replicated unit must occupy a slot");
            Assert.That(runner.World.TryGet(entity, out var start), Is.True);
            Assert.That(runner.World.TryGetSlotState(slot, out var selectionState), Is.True);
            Assert.That(selectionState.Owner, Is.EqualTo(runner.LocalPlayer),
                "the client must own the unit the command test is about to select");

            runner.Selection.SelectSingle(entity, slot);
            Assert.That(runner.Selection.SelectedCount, Is.EqualTo(1),
                "the network-owned unit must be selectable by the local player");

            var acknowledgements = new List<CommandAckPayload>();
            rig.Session.CommandChannel.CommandResultReceived += acknowledgements.Add;

            var accepted = runner.Selection.IssueMove(
                new WorldPointMm(start.Position.X + 5000, start.Position.Z),
                rig.Server.Host.CurrentTick + 1,
                runner.CommandSink);
            Assert.That(accepted, Is.True,
                "the selection path must hand the move to the command sink");

            var received = rig.PumpUntil(
                () => rig.Server.Host.Server.TryGetLastAcceptedSequence(
                    rig.Session.Endpoint.Player, out _),
                5000);
            Assert.That(received, Is.True,
                "the authoritative server must receive the move over the network command " +
                "channel (received 0 commands): " + rig.Describe());

            Assert.That(
                rig.Server.Host.Server.TryGetLastAcceptedSequence(
                    rig.Session.Endpoint.Player, out var acceptedSequence),
                Is.True);
            Assert.That(acceptedSequence, Is.EqualTo(1u),
                "exactly one command must have reached the authoritative server");

            Assert.That(
                rig.PumpUntil(() => acknowledgements.Count > 0, 5000),
                Is.True,
                "the server must acknowledge a move sent over the network command channel: " +
                rig.Describe());

            Assert.That(acknowledgements[0].IsAccepted, Is.True,
                $"the authoritative server rejected the move: {acknowledgements[0].Session}/" +
                $"{acknowledgements[0].Command}");

            rig.Server.Host.TickOnce();
            Assert.That(rig.Server.Host.Server.TryGetUnit(entity, out var applied), Is.True);
            Assert.That(applied.HasMoveTarget, Is.True,
                "the accepted move must be applied by the authoritative tick");
        }

        [Test]
        public void DedicatedClient_NetworkSnapshots_AdvanceRenderTickAndBindViews()
        {
            var rig = CreateRig();
            var entities = rig.StartMatch();
            Assert.That(rig.PumpUntilStreaming(2), Is.True,
                "the client must install its baseline: " + rig.Describe());

            var runner = BuildRunner(rig.Session);
            runner.PumpNetwork(rig.Server.Clock.NowMs);
            runner.StepPresentationFrame(0.1f, 0.1);

            var entity = entities[0];
            var slot = FindSlot(runner.World, entity);
            Assert.That(slot, Is.GreaterThanOrEqualTo(0));
            Assert.That(runner.World.TryGet(entity, out var state), Is.True);

            Assert.That(runner.RenderTick, Is.GreaterThan(0.0),
                "a presentation frame must advance the render clock");
            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(2),
                "every replicated unit must be presented by the binder");
            Assert.That(runner.Binder.TryGetView(slot, out var view), Is.True);
            Assert.That(view.Hull.position.x,
                Is.EqualTo(state.Position.X / 1000f).Within(0.001f),
                "the view must sit on the position the network delivered");
            Assert.That(view.Hull.position.z,
                Is.EqualTo(state.Position.Z / 1000f).Within(0.001f));
        }

        [Test]
        public void DedicatedClient_CurrentTick_FollowsTheReplicatedTick()
        {
            var rig = CreateRig();
            rig.StartMatch();
            Assert.That(rig.PumpUntilStreaming(2), Is.True,
                "the client must install its baseline: " + rig.Describe());

            var runner = BuildRunner(rig.Session);

            Assert.That(runner.CurrentTick, Is.GreaterThan(0ul));
            Assert.That(runner.CurrentTick, Is.EqualTo(rig.Session.Receiver.LastAppliedTick),
                "with no local host the slice's clock is the replication receiver's");
        }

        /// <summary>
        /// OD-41 / ADR-014: a dedicated client is behind the server by its RTT
        /// and draws still later by the tick buffer's play-out delay, so a
        /// command dated for the next authoritative tick is already stale on
        /// arrival. The frame loop has to lead by the render delay, the
        /// one-way transit estimate and a two-tick safety margin, and that
        /// total still has to fit the server's +60 future window.
        /// </summary>
        [Test]
        public void DedicatedClient_RequestedTick_LeadsByRenderDelayHalfRttAndMargin()
        {
            var rig = CreateRig();
            rig.StartMatch();
            Assert.That(rig.PumpUntilStreaming(2), Is.True,
                "the client must install its baseline: " + rig.Describe());
            Assert.That(rig.PumpUntil(() => rig.Session.RttSampleCount >= 2, 5000), Is.True,
                "two ping round trips must complete before the lead is judged: " + rig.Describe());

            var runner = BuildRunner(rig.Session);
            runner.Binder.Render(0.0);
            runner.StepPresentationFrame(0.016f, 1.0);

            var currentTick = runner.CurrentTick;
            var rttMilliseconds = rig.Session.SmoothedRttMs;
            var renderDelayTicks = runner.TickBuffer.RenderDelayTicks;
            Assert.That(rttMilliseconds, Is.GreaterThan(0),
                "the lead's transit term must be measured from a real sample");

            runner.StepPresentationFrame(0.016f, 1.032);

            var tickMilliseconds = SimulationConstants.ServerTickDurationSeconds * 1000.0;
            var expected = currentTick + 1ul +
                (ulong)Math.Ceiling(rttMilliseconds * 0.5 / tickMilliseconds) +
                (ulong)Math.Ceiling(renderDelayTicks) +
                2ul;

            Assert.That(runner.SelectionDriver.RequestedTick, Is.EqualTo(expected),
                "the frame loop must date commands by the render play-out delay, one-way " +
                "transit and the two-tick safety margin");
            Assert.That(
                runner.SelectionDriver.RequestedTick - runner.CurrentTick,
                Is.LessThanOrEqualTo(MatchServer.AcceptedFutureTicks),
                "the lead must stay inside the server's accepted future window");
        }

        [Test]
        public void DedicatedClient_RttSample_ReachesTheNetworkHud()
        {
            var rig = CreateRig();
            var runner = BuildRunner(rig.Session);

            Assert.That(rig.PumpUntil(() => rig.Session.RttSampleCount >= 2, 5000), Is.True,
                "two ping round trips must complete within five seconds of virtual time: " +
                rig.Describe());

            Assert.That(
                runner.NetworkHud.State.TryGetSlot(rig.Session.Endpoint.Player, out var slot),
                Is.True,
                "the HUD state describes the local player's slot");
            Assert.That(runner.NetworkHud.State[slot].SlotStatus, Is.EqualTo(SlotStatus.Connected));
            Assert.That(runner.NetworkHud.State[slot].PingMs, Is.GreaterThan(0),
                "the EWMA RTT must reach the network HUD's slot model");
        }

        private RemoteRig CreateRig()
        {
            var rig = RemoteRig.Create();
            _rigs.Add(rig);
            return rig;
        }

        private TacticalVerticalSliceRunner BuildRunner(RemoteSliceSession session)
        {
            var go = new GameObject("DedicatedClientRunnerTests_Slice");
            _createdObjects.Add(go);
            var runner = go.AddComponent<TacticalVerticalSliceRunner>();
            _runners.Add(runner);
            runner.InitializeDedicatedClient(session, createVisuals: false, autoStartRealTime: false);
            return runner;
        }

        private static int FindSlot(ClientReplicationWorld world, EntityId entity)
        {
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (world.TryGetSlotState(slot, out var state) && state.Entity == entity)
                {
                    return slot;
                }
            }

            return -1;
        }
    }
}
