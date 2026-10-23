#pragma warning disable CS0067
using System;
using GlobalFront.Client;
using GlobalFront.Client.Presentation;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.DedicatedServer;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode;
using GlobalFront.Tests.EditMode.Integration.Replication;
using NUnit.Framework;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;
using Object = UnityEngine.Object;
using ReconnectResult = GlobalFront.Core.Reconnect.ReconnectResult;

namespace GlobalFront.Tests.EditMode.Integration
{
    /// <summary>
    /// Step 4.5.4 (Runtime Loop Closure): the three gaps between the green
    /// component tests and the production runtime are closed here.
    /// <list type="number">
    /// <item>C0 ReconnectResponse carries the fresh rebind token, and the
    /// endpoint adopts it so a reconnected client can sign commands;</item>
    /// <item><see cref="ClientTransportEndpoint.Reconnect"/> and
    /// <see cref="RemoteSliceSession.TryReconnect"/> open the replacement
    /// socket and send the cached-secret request;</item>
    /// <item>the dedicated server installs an active fog-of-war filter (the
    /// local slice wires it too, but keeps it disabled — see the runner).</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public sealed class RuntimeLoopClosureTests
    {
        [Test]
        public void ReconnectResponse_TransmitsSessionToken_AndUpdatesEndpointToken()
        {
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));
            for (var tick = 0; tick < 3; tick++)
            {
                world.PumpTick();
            }

            var client0 = world.Clients[0];
            var player1Unit = client0.FirstEntityOf(new PlayerId(1));
            var previousToken = client0.Endpoint.Token;
            var lastAppliedTick = client0.Receiver.LastAppliedTick;
            Assert.That(previousToken, Is.Not.Zero, "the initial handshake must issue a token");

            DropEndpoint(world, client0.Endpoint);
            Assert.That(client0.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected));

            client0.Endpoint.Reconnect(lastAppliedTick);
            var elapsed = 0;
            while (client0.Endpoint.State != ClientTransportState.Established && elapsed < 5_000)
            {
                world.Pump(5);
                elapsed += 5;
            }

            Assert.That(client0.Endpoint.State, Is.EqualTo(ClientTransportState.Established),
                "the endpoint must adopt the accepted reconnect response");
            Assert.That(client0.Endpoint.Token, Is.Not.Zero);
            Assert.That(client0.Endpoint.Token, Is.Not.EqualTo(previousToken),
                "a reconnect must issue a fresh transport token");
            Assert.That(
                world.Rig.ServerTransport.Binder.TryGetTokenBySession(client0.Endpoint.Session, out var reboundToken),
                Is.True);
            Assert.That(client0.Endpoint.Token, Is.EqualTo(reboundToken),
                "the endpoint must mirror the token the server rebound to");

            var submission = client0.Commands.TrySubmitMove(
                new CommandHeader(
                    new PlayerId(1), 1u, world.Rig.Host.CurrentTick + 1, GameCommandType.Move),
                new[] { player1Unit },
                new WorldPointMm(50_000, 0),
                new FormationSpec(1, 2000, CardinalFacing.North));
            Assert.That(submission, Is.EqualTo(MatchCommandRejection.None),
                "the command must pass the local pre-flight with the adopted token");

            elapsed = 0;
            while (client0.CommandAcks.Count == 0 && elapsed < 5_000)
            {
                world.Pump(5);
                elapsed += 5;
            }

            Assert.That(client0.CommandAcks.Count, Is.GreaterThan(0),
                "the server must acknowledge a command signed with the rebound token");
            Assert.That(client0.CommandAcks[0].Session, Is.EqualTo(SessionRejection.None),
                "the rebound token must pass the carrier and session gate");
            Assert.That(client0.CommandAcks[0].Command, Is.EqualTo(MatchCommandRejection.None));
        }

        [Test]
        public void RemoteSliceSession_TryReconnect_DrivesFullReconnect()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            rig.Host.AttachReplication(
                new TransportReplicationAdapter(rig.ServerTransport),
                ServerReplicationEmitterConfig.Default);
            var address = rig.Pipe.CreateEndpoint();
            var carrier = OwnDatagramCarrier.CreateClient(
                rig.Pipe, address, TransportTestSupport.ServerAddressOf(rig), rig.Clock);
            var session = new RemoteSliceSession(carrier, rig.Clock);

            try
            {
                session.Connect("virtual", 0);
                PumpUntil(rig, session, () => session.IsConnected, 5_000, "the client must complete the handshake");

                // A one-player match is terminal after its first tick, and a
                // finished match rejects every rebind. The second slot is a
                // server-side placeholder, exactly like the slice's enemy
                // session: it keeps the match Running without a second socket.
                var enemySession = rig.Host.CreateSession(rig.Host.CreateConnectionHandle());
                Assert.That(
                    rig.Host.TryJoinMatch(enemySession, rig.Match, out _),
                    Is.EqualTo(JoinResult.Assigned),
                    "the placeholder player must take the second slot");
                Assert.That(
                    rig.Host.TryStartSessionMatch(
                        rig.Match,
                        TransportTestSupport.BuildMatchConfig(new PlayerId(1), new PlayerId(2)),
                        out _),
                    Is.True,
                    "the session match must start for both joined players");
                PumpUntil(
                    rig, session,
                    () => session.World.LiveCount == 2 && session.Receiver.IsWorldUsable,
                    5_000,
                    "the baseline must install before the loss",
                    tickHost: true);

                var lastAppliedTick = session.Receiver.LastAppliedTick;
                DropEndpoint(rig, session.Endpoint);
                Assert.That(session.ReconnectCoordinator.State, Is.EqualTo(ClientReconnectState.Reconnecting));
                Assert.That(rig.Host.IsPaused, Is.True, "the server must pause while the client is gone");

                Assert.That(session.TryReconnect(), Is.True,
                    "a lost session in the Reconnecting state must start an attempt");
                Assert.That(session.TryReconnect(), Is.False,
                    "a second attempt while the socket is connecting must be refused");

                var reconnectResponses = new System.Collections.Generic.List<ReconnectResult>();
                session.Endpoint.ReconnectResponseReceived += response =>
                    reconnectResponses.Add(response.Result);
                var lostEvents = 0;
                session.Endpoint.Lost += _ => lostEvents++;
                var reconnectElapsed = 0;
                var reconnectEstablished = false;
                while ((!session.IsConnected ||
                        session.Endpoint.Token == 0 ||
                        !session.Receiver.IsWorldUsable) &&
                       reconnectElapsed < 10_000)
                {
                    rig.Pump(5);
                    session.Pump(rig.Clock.NowMs);
                    reconnectEstablished |= session.Endpoint.State == ClientTransportState.Established;
                    reconnectElapsed += 5;
                }

                Assert.That(
                    session.IsConnected &&
                    session.Endpoint.Token != 0 &&
                    session.Receiver.IsWorldUsable,
                    Is.True,
                    "the reconnect must restore the endpoint and the world\n" +
                    $"coordinator={session.ReconnectCoordinator.State} losses={lostEvents} " +
                    $"responses={string.Join(",", reconnectResponses)} " +
                    $"everEstablished={reconnectEstablished} elapsedMs={reconnectElapsed} " +
                    $"endpoint={session.Endpoint.State} token={session.Endpoint.Token} " +
                    $"receiver={session.Receiver.State} last={session.Receiver.LastAppliedTick} " +
                    $"live={session.World.LiveCount} usable={session.Receiver.IsWorldUsable} " +
                    $"failure={session.Receiver.FailureReason} " +
                    $"serverTick={rig.Host.CurrentTick} paused={rig.Host.IsPaused}");
                Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Established));
                Assert.That(session.ReconnectCoordinator.State, Is.EqualTo(ClientReconnectState.Countdown),
                    "KeyframeInstalled must start the OD-20 countdown");
                Assert.That(session.Receiver.World.LiveCount, Is.EqualTo(2));
                Assert.That(
                    rig.ServerTransport.Binder.TryGetTokenBySession(session.Endpoint.Session, out var token),
                    Is.True);
                Assert.That(session.Endpoint.Token, Is.EqualTo(token));
                Assert.That(session.ReconnectCoordinator.LastAppliedTick,
                    Is.GreaterThanOrEqualTo(lastAppliedTick));
            }
            finally
            {
                rig.Host.Resume();
                session.Dispose();
            }
        }

        [Test]
        public void RemoteSliceSession_TryReconnect_ProjectCodeZeroGC()
        {
            var clock = new VirtualTransportClock();
            var session = new RemoteSliceSession(new NoAllocCarrier(clock), clock);
            try
            {
                var match = new MatchId(1);
                var secret = default(SessionSecret32);
                session.ReconnectCoordinator.CacheSession(
                    new SessionId(Guid.NewGuid()), match, new PlayerId(1), in secret);
                session.ReconnectCoordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
                Assert.That(session.ReconnectCoordinator.State,
                    Is.EqualTo(ClientReconnectState.Reconnecting));

                var reconnecting = false;
                var projectSamples = CountGcAllocations(() => reconnecting = session.TryReconnect());
                var controlSamples = CountGcAllocations(() =>
                {
                    var probe = new byte[4096];
                    probe[0] = 1;
                    Assert.That(probe.Length, Is.EqualTo(4096));
                });

                Assert.That(reconnecting, Is.True);
                Assert.That(controlSamples, Is.GreaterThan(0),
                    "the control allocation must be visible to the recorder");
                Assert.That(projectSamples, Is.EqualTo(0),
                    "the reconnect path itself must not allocate; the production carrier owns its socket state");
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void ProductionRunners_AttachFogOfWarFilter()
        {
            var savedTargetFrameRate = Application.targetFrameRate;
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var carrier = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var serverObject = new GameObject("RuntimeLoopClosureTests_Server");
            var sliceObject = new GameObject("RuntimeLoopClosureTests_Slice");
            DedicatedServerRunner serverRunner = null;
            TacticalVerticalSliceRunner sliceRunner = null;

            try
            {
                serverRunner = serverObject.AddComponent<DedicatedServerRunner>();
                serverRunner.Initialize(carrier, clock);
                Assert.That(serverRunner.Host, Is.Not.Null);
                Assert.That(serverRunner.Host.VisibilityFilter, Is.InstanceOf<FogOfWarReplicationFilter>(),
                    "the dedicated server must install the fog-of-war filter");
                Assert.That(serverRunner.Host.VisibilityFilter.IsEnabled, Is.True,
                    "the dedicated server must run fog of war");

                sliceRunner = sliceObject.AddComponent<TacticalVerticalSliceRunner>();
                sliceRunner.Initialize(
                    unitCount: 2,
                    createVisuals: false,
                    autoStartRealTime: false,
                    enemyAutoAcquire: false);
                Assert.That(sliceRunner.Host, Is.Not.Null);
                Assert.That(sliceRunner.Host.VisibilityFilter, Is.InstanceOf<FogOfWarReplicationFilter>(),
                    "the tactical slice must install the filter");
                Assert.That(sliceRunner.Host.VisibilityFilter.IsEnabled, Is.False,
                    "the 400-unit slice demo keeps the filter disabled: its armies spawn four sight radii apart");
            }
            finally
            {
                sliceRunner?.Shutdown();
                serverRunner?.Shutdown();
                Object.DestroyImmediate(sliceObject);
                Object.DestroyImmediate(serverObject);
                Application.targetFrameRate = savedTargetFrameRate;
            }
        }

        private static int CountGcAllocations(Action action)
        {
            var recorder = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            Assert.That(recorder, Is.Not.Null, "the GC.Alloc recorder must exist in the editor");
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

        private static void DropEndpoint(
            ReplicationIntegrationWorld world,
            ClientTransportEndpoint endpoint)
        {
            Assert.That(
                world.Rig.ServerTransport.Binder.TryGetTokenBySession(endpoint.Session, out var token),
                Is.True);
            Assert.That(world.Rig.ServerTransport.Binder.TryGetByToken(token, out var binding), Is.True);
            endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            world.Rig.ServerCarrier.CloseConnection(binding.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var pump = 0; pump < 5; pump++)
            {
                world.Pump(5);
            }
        }

        private static void DropEndpoint(
            TransportTestSupport.Rig rig,
            ClientTransportEndpoint endpoint)
        {
            Assert.That(rig.ServerTransport.Binder.TryGetTokenBySession(endpoint.Session, out var token), Is.True);
            Assert.That(rig.ServerTransport.Binder.TryGetByToken(token, out var binding), Is.True);
            endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            rig.ServerCarrier.CloseConnection(binding.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var pump = 0; pump < 5; pump++)
            {
                rig.Pump(5);
            }
        }

        private static void PumpUntil(
            TransportTestSupport.Rig rig,
            RemoteSliceSession session,
            Func<bool> condition,
            int budgetMs,
            string message,
            bool tickHost = false)
        {
            var elapsed = 0;
            var everEstablished = false;
            while (!condition() && elapsed < budgetMs)
            {
                if (tickHost)
                {
                    rig.Host.TickOnce();
                }

                rig.Pump(5);
                session.Pump(rig.Clock.NowMs);
                everEstablished |= session.Endpoint.State == ClientTransportState.Established;
                elapsed += 5;
            }

            Assert.That(
                condition(),
                Is.True,
                $"{message}\n" +
                $"receiver: state={session.Receiver.State} last={session.Receiver.LastAppliedTick} " +
                $"base={session.Receiver.BaseKeyframeTick} usable={session.Receiver.IsWorldUsable} " +
                $"live={session.World.LiveCount} failure={session.Receiver.FailureReason} " +
                $"requests={session.Receiver.TotalRequestCount} pending={session.Receiver.PendingRequestCount}\n" +
                $"bridge: kf={session.Bridge.AssembledKeyframeCount} slices={session.Bridge.AssembledSliceCount} " +
                $"deltas={session.Bridge.ForwardedDeltaCount} acks={session.Bridge.SentAckCount} " +
                $"reqs={session.Bridge.SentRequestCount} malformed={session.Bridge.MalformedPayloadCount}\n" +
                $"endpoint: state={session.Endpoint.State} player={session.Endpoint.Player.Value} " +
                $"token={session.Endpoint.Token} session={session.Endpoint.Session.IsValid}\n" +
                $"server: tick={rig.Host.CurrentTick} paused={rig.Host.IsPaused} " +
                $"sessions={rig.Host.Sessions.HasConnectedSessions(rig.Match)} " +
                $"everEstablished={everEstablished} " +
                $"clientTimeouts={session.Carrier.Metrics.ConnectionTimeouts} " +
                $"serverTimeouts={rig.ServerCarrier.Metrics.ConnectionTimeouts} " +
                $"clientStale={session.Carrier.Metrics.DroppedStaleSnapshot} " +
                $"clientBadToken={session.Carrier.Metrics.DroppedBadToken}");
        }

        /// <summary>
        /// Carrier seam whose methods allocate nothing: it exists so the
        /// zero-GC claim measures the project's reconnect path, not the socket
        /// carrier's connection bookkeeping (which a real carrier owns).
        /// </summary>
        private sealed class NoAllocCarrier : INetworkCarrier
        {
            private readonly TransportMetrics _metrics = new TransportMetrics();
            private readonly ITransportClock _clock;
            private int _nextConnection = 1;

            public NoAllocCarrier(ITransportClock clock)
            {
                _clock = clock;
            }

            public TransportMetrics Metrics => _metrics;

            public ITransportClock Clock => _clock;

            public void StartServer()
            {
            }

            public int ConnectToServer(string host, int port) => _nextConnection++;

            public bool Send(int connectionId, TransportChannel channel, byte[] data, int offset, int length) =>
                true;

            public void CloseConnection(int connectionId, TransportDisconnectReason reason)
            {
            }

            public void AssignSessionToken(int connectionId, ulong token)
            {
            }

            public void Shutdown(TransportDisconnectReason reason)
            {
            }

            public void Pump(long nowMs)
            {
            }

            public bool TryDequeueEvent(out TransportEvent transportEvent)
            {
                transportEvent = default;
                return false;
            }

            public int EventQueueDepth => 0;

            public void Dispose()
            {
            }
        }
    }
}
