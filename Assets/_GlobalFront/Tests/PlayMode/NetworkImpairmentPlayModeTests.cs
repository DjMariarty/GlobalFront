using System;
using System.Collections;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.DedicatedServer;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using EntityId = GlobalFront.Core.Model.EntityId;
using Object = UnityEngine.Object;
using ReconnectResult = GlobalFront.Core.Reconnect.ReconnectResult;

namespace GlobalFront.Tests.PlayMode
{
    /// <summary>
    /// Phase 4, step 4.6.4 (OD-43): liveness of the client/server contour over
    /// real UDP loopback while LiteNetLib applies packet loss and latency
    /// simulation. Timing is asserted only through coarse wall-clock
    /// timeouts; the acceptance criteria are handshake, identity, command
    /// acknowledgement, replicated deltas and session recovery.
    ///
    /// The shipped LiteNetLib binary is built with SIMULATE_NETWORK so these
    /// flags have actual wire effect; the first test additionally proves it by
    /// a deterministic 100% server-to-client loss pulse, because reading the
    /// flag alone would pass on a Release binary where the simulation calls
    /// are compiled out.
    /// </summary>
    [TestFixture]
    public sealed class NetworkImpairmentPlayModeTests
    {
        private const float HandshakeTimeoutSeconds = 25f;
        private const float BaselineTimeoutSeconds = 25f;
        private const float DeliveryTimeoutSeconds = 20f;
        private const float RttWitnessTimeoutSeconds = 10f;
        private const float ReconnectFsmTimeoutSeconds = 10f;

        private const int LossPercent = 5;
        private const int ReconnectLossPercent = 4;
        private const int MinLatencyMs = 20;
        private const int MaxLatencyMs = 60;
        private const int MinimumSimulatedRttMs = 10;

        private static readonly CombatStats StandardStats =
            new CombatStats(maximumHealth: 100, damage: 25, rangeMm: 7000, cooldownTicks: 10);

        private readonly List<RemoteSliceSession> _sessions = new List<RemoteSliceSession>();
        private GameObject _serverObject;
        private DedicatedServerRunner _server;
        private LiteNetLibCarrier _serverCarrier;
        private int _serverPort;
        private int _savedTargetFrameRate;
        private int _disconnectEvents;
        private bool _receiverFailed;
        private GameObject _sliceRoot;
        private TacticalVerticalSliceRunner _sliceRunner;

        [SetUp]
        public void SetUp()
        {
            _savedTargetFrameRate = Application.targetFrameRate;
            _disconnectEvents = 0;
            _receiverFailed = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (_sliceRunner != null)
            {
                _sliceRunner.Shutdown();
                _sliceRunner = null;
            }

            if (_sliceRoot != null)
            {
                Object.Destroy(_sliceRoot);
                _sliceRoot = null;
            }

            for (var index = 0; index < _sessions.Count; index++)
            {
                _sessions[index]?.Dispose();
            }

            _sessions.Clear();

            if (_server != null)
            {
                _server.Shutdown();
            }

            if (_serverObject != null)
            {
                Object.Destroy(_serverObject);
            }

            _server = null;
            _serverCarrier = null;
            _serverObject = null;
            _serverPort = 0;
            Application.targetFrameRate = _savedTargetFrameRate;
        }

        [UnityTest]
        public IEnumerator LiveUDP_UnderPacketLossAndLatency_HandshakeAndCommandSucceed()
        {
            StartServer(packetLoss: true, LossPercent, latency: true);

            var sessionA = CreateClient(packetLoss: true, LossPercent, latency: true);
            var sessionB = CreateClient(packetLoss: true, LossPercent, latency: true);
            var carrierA = (LiteNetLibCarrier)sessionA.Carrier;
            var carrierB = (LiteNetLibCarrier)sessionB.Carrier;

            Assert.That(carrierA.IsSimulatingPacketLoss, Is.True,
                "client A must have loss simulation active on its manager");
            Assert.That(carrierB.IsSimulatingPacketLoss, Is.True,
                "client B must have loss simulation active on its manager");
            Assert.That(_serverCarrier.IsSimulatingPacketLoss, Is.True,
                "the dedicated server must have loss simulation active on its manager");

            sessionA.Connect("127.0.0.1", _serverPort);
            sessionB.Connect("127.0.0.1", _serverPort);

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return sessionA.IsConnected && sessionB.IsConnected;
                },
                HandshakeTimeoutSeconds,
                "both clients must complete the handshake under 5% loss and 20-60 ms latency");

            // A flag is not evidence that packets are actually delayed: the RTT
            // sample is produced by a live Ping/Pong over the impaired socket.
            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return sessionA.RttSampleCount > 0 && sessionB.RttSampleCount > 0;
                },
                RttWitnessTimeoutSeconds,
                "both clients must obtain an RTT sample over the impaired socket");

            Assert.That(sessionA.SmoothedRttMs, Is.GreaterThanOrEqualTo(MinimumSimulatedRttMs),
                $"latency simulation must be physically active; measured RTT " +
                $"was {sessionA.SmoothedRttMs} ms");
            Assert.That(sessionB.SmoothedRttMs, Is.GreaterThanOrEqualTo(MinimumSimulatedRttMs),
                $"latency simulation must be physically active; measured RTT " +
                $"was {sessionB.SmoothedRttMs} ms");

            var playerA = sessionA.Endpoint.Player;
            var playerB = sessionB.Endpoint.Player;
            Assert.That(playerA.IsValid, Is.True);
            Assert.That(playerB.IsValid, Is.True);
            Assert.That(playerA, Is.Not.EqualTo(playerB),
                "the server must assign distinct player ids");

            var specs = new[]
            {
                new UnitSpawnSpec(playerA, new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(playerB, new WorldPointMm(20_000, 0), 350, false)
            };
            Assert.That(
                _server.Host.TryStartSessionMatch(
                    _server.Match, new MatchConfig(StandardStats, specs), out _),
                Is.True,
                "the dedicated server must start the match for both attached sessions");

            yield return WaitFor(
                () =>
                {
                    _server.Host.TickOnce();
                    PumpClients();
                    return sessionA.World.LiveCount == 2 &&
                        sessionB.World.LiveCount == 2 &&
                        sessionA.Receiver.State == ReplicationReceiverState.Streaming &&
                        sessionB.Receiver.State == ReplicationReceiverState.Streaming;
                },
                BaselineTimeoutSeconds,
                "both clients must install the baseline keyframe under impairment");

            var entityA = FindOwnedUnit(sessionA.World, playerA);
            Assert.That(entityA.IsValid, Is.True,
                "the baseline must describe the unit owned by the first client");
            Assert.That(sessionA.World.TryGet(entityA, out var start), Is.True);

            var acknowledgements = new List<CommandAckPayload>();
            sessionA.CommandChannel.CommandResultReceived += acknowledgements.Add;

            var submit = sessionA.CommandChannel.TrySubmitMove(
                new CommandHeader(
                    playerA, 1, _server.Host.CurrentTick + 1, GameCommandType.Move),
                new[] { entityA },
                new WorldPointMm(start.Position.X + 5000, start.Position.Z),
                new FormationSpec(1, 2000, CardinalFacing.North));
            Assert.That(submit, Is.EqualTo(MatchCommandRejection.None),
                "the move must pass the client's local pre-flight");

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return acknowledgements.Count > 0;
                },
                DeliveryTimeoutSeconds,
                "the server must acknowledge the move over impaired UDP");

            Assert.That(acknowledgements[0].IsAccepted, Is.True,
                $"the authoritative move was rejected: {acknowledgements[0].Session}/" +
                $"{acknowledgements[0].Command}");

            for (var index = 0; index < 3; index++)
            {
                _server.Host.TickOnce();
            }

            var targetTick = _server.Host.CurrentTick;

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return sessionA.Receiver.LastAppliedTick >= targetTick &&
                        sessionB.Receiver.LastAppliedTick >= targetTick &&
                        sessionA.World.TryGet(entityA, out var movedA) &&
                        movedA.Position != start.Position &&
                        sessionB.World.TryGet(entityA, out var movedB) &&
                        movedB.Position != start.Position;
                },
                DeliveryTimeoutSeconds,
                "both clients must receive the delta carrying the move");

            Assert.That(_server.Host.Server.TryGetUnit(entityA, out var serverUnit), Is.True);
            Assert.That(sessionA.World.TryGet(entityA, out var clientA), Is.True);
            Assert.That(sessionB.World.TryGet(entityA, out var clientB), Is.True);
            Assert.That(clientA.Position, Is.EqualTo(serverUnit.Position),
                "client 1 must agree with authority under impairment");
            Assert.That(clientB.Position, Is.EqualTo(serverUnit.Position),
                "client 2 must agree with authority under impairment");

            yield return ProvePacketLossDropsLiveTraffic(sessionA);

            Assert.That(sessionA.IsConnected && sessionB.IsConnected, Is.True,
                "impairment must not disconnect either session");
            Assert.That(_disconnectEvents, Is.Zero,
                "no transport disconnect event may occur during the impairment scenario");
            Assert.That(_receiverFailed, Is.False,
                "no replication receiver may enter ConnectionFailed");
        }

        [UnityTest]
        public IEnumerator LiveUDP_UnderLoss_SocketDisconnectAndReconnect_RecoversSession()
        {
            StartServer(packetLoss: true, ReconnectLossPercent, latency: true);

            var session = CreateClient(packetLoss: true, ReconnectLossPercent, latency: true);
            var sessionB = CreateClient(packetLoss: true, ReconnectLossPercent, latency: true);
            var carrier = (LiteNetLibCarrier)session.Carrier;

            Assert.That(carrier.IsSimulatingPacketLoss, Is.True,
                "the client must have loss simulation active on its manager");

            session.Connect("127.0.0.1", _serverPort);
            sessionB.Connect("127.0.0.1", _serverPort);

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return session.IsConnected && sessionB.IsConnected;
                },
                HandshakeTimeoutSeconds,
                "both clients must complete the handshake under loss before the break");

            var player = session.Endpoint.Player;
            var otherPlayer = sessionB.Endpoint.Player;
            Assert.That(player.IsValid, Is.True);
            Assert.That(otherPlayer.IsValid, Is.True);
            Assert.That(player, Is.Not.EqualTo(otherPlayer));

            // One-owner matches auto-finish when their only player drops, so a
            // second connected player keeps the match Running through the
            // victim's grace window.
            var specs = new[]
            {
                new UnitSpawnSpec(player, new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(otherPlayer, new WorldPointMm(20_000, 0), 350, false)
            };
            Assert.That(
                _server.Host.TryStartSessionMatch(
                    _server.Match, new MatchConfig(StandardStats, specs), out var entityIds),
                Is.True);
            Assert.That(entityIds.Length, Is.EqualTo(2));

            yield return WaitFor(
                () =>
                {
                    _server.Host.TickOnce();
                    PumpClients();
                    return session.World.LiveCount == 2 &&
                        sessionB.World.LiveCount == 2 &&
                        session.Receiver.State == ReplicationReceiverState.Streaming &&
                        sessionB.Receiver.State == ReplicationReceiverState.Streaming;
                },
                BaselineTimeoutSeconds,
                "both clients must install the baseline keyframe before the break");

            var entity = FindOwnedUnit(session.World, player);
            Assert.That(entity.IsValid, Is.True);
            var sessionId = session.Endpoint.Session;
            var otherDisconnected = false;
            sessionB.Disconnected += _ => otherDisconnected = true;

            // PrepareForResync resets the receiver counters, so KeyframeCount
            // cannot witness a post-reconnect baseline; count the install event.
            var keyframeInstalls = 0;
            session.Receiver.KeyframeInstalled += _ => keyframeInstalls++;

            var hasReconnectResponse = false;
            var reconnectResult = ReconnectResult.Accepted;
            session.Endpoint.ReconnectResponseReceived += response =>
            {
                hasReconnectResponse = true;
                reconnectResult = response.Result;
            };

            // Break the socket from the server side. A client-side carrier
            // close is reported by LiteNetLib as RemoteConnectionClose, which
            // the server maps to ClientRequested and treats as a permanent
            // session close; the server-side carrier close feeds the reconnect
            // grace window instead.
            var serverConnectionId = GetServerConnectionId(_server, sessionId);
            _serverCarrier.CloseConnection(
                serverConnectionId, TransportDisconnectReason.TransportLost);

            // Wait for the server to observe the loss before the client is
            // allowed to retry; a rebind that arrives while the server still
            // considers the session Connected is answered with InvalidState,
            // which is terminal for the coordinator.
            yield return WaitFor(
                () => _server.Host.TryGetSession(sessionId, out var record) &&
                    record.State == SessionState.Disconnected,
                ReconnectFsmTimeoutSeconds,
                "the server must move the lost session into reconnect grace");

            // Dropping the client endpoint locally makes the coordinator
            // observe the loss deterministically; its next Pump runs the
            // automatic retry policy.
            session.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return session.ReconnectCoordinator.State == ClientReconnectState.Reconnecting;
                },
                ReconnectFsmTimeoutSeconds,
                "the client coordinator must enter Reconnecting after the forced break");

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return session.ReconnectAttemptCount >= 1;
                },
                ReconnectFsmTimeoutSeconds,
                "the automatic retry policy must open a replacement connection");

            yield return WaitFor(
                () =>
                {
                    _server.Host.TickOnce();
                    PumpClients();
                    return session.IsConnected &&
                        session.ReconnectCoordinator.State != ClientReconnectState.Terminated &&
                        session.Receiver.State == ReplicationReceiverState.Streaming &&
                        keyframeInstalls > 0 &&
                        session.World.TryGet(entity, out _);
                },
                HandshakeTimeoutSeconds,
                () => "the automatic retry must rebind the session and install a fresh keyframe; " +
                    DescribeReconnect(session, keyframeInstalls, hasReconnectResponse, reconnectResult) +
                    DescribeServerSession(sessionId));

            Assert.That(session.ReconnectAttemptCount, Is.GreaterThanOrEqualTo(1),
                "the retry policy must have attempted at least one reconnect");
            Assert.That(session.ReconnectCoordinator.State,
                Is.EqualTo(ClientReconnectState.Countdown)
                    .Or.EqualTo(ClientReconnectState.Resyncing)
                    .Or.EqualTo(ClientReconnectState.Connected),
                "the rebound session must have passed reattachment");
            Assert.That(session.Endpoint.Session, Is.EqualTo(sessionId),
                "the replacement connection must preserve the session id");
            Assert.That(session.Endpoint.Player, Is.EqualTo(player),
                "the replacement connection must preserve the player id");
            Assert.That(session.World.LiveCount, Is.EqualTo(2),
                "the restored world must contain both authoritative units");
            Assert.That(sessionB.IsConnected, Is.True,
                "the other client must remain connected through the recovery");
            Assert.That(otherDisconnected, Is.False,
                "the break must affect only the victim session");
            Assert.That(sessionB.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming),
                "the other client must keep streaming through the recovery");

            // OD-18/OD-20: the host stays paused while the client counts down.
            // RemoteSliceSession.Pump owns the countdown clock; PumpClients
            // advances it with real elapsed time. The wire signal is the only
            // resume path.
            Assert.That(_server.Host.IsPaused, Is.True,
                "the dedicated host must remain paused during the client countdown");

            var sawCountdown = false;
            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    if (session.ReconnectCoordinator.State == ClientReconnectState.Countdown)
                    {
                        sawCountdown = true;
                    }

                    return sawCountdown && !_server.Host.IsPaused;
                },
                ReconnectFsmTimeoutSeconds,
                "the dedicated host must auto-resume after the session-pump " +
                "OD-20 countdown and C0 ClientReadyToResume signal");

            Assert.That(sawCountdown, Is.True,
                "the coordinator must pass through the Countdown state");
            Assert.That(session.ReconnectCoordinator.State,
                Is.EqualTo(ClientReconnectState.Connected),
                "the coordinator must return to Connected after the countdown");
            Assert.That(_server.Host.IsPaused, Is.False,
                "the runtime resume protocol must unpause the dedicated host");

            var acknowledgements = new List<CommandAckPayload>();
            session.CommandChannel.CommandResultReceived += acknowledgements.Add;

            Assert.That(session.World.TryGet(entity, out var restored), Is.True);
            var submit = session.CommandChannel.TrySubmitMove(
                new CommandHeader(
                    player, 1, _server.Host.CurrentTick + 1, GameCommandType.Move),
                new[] { entity },
                new WorldPointMm(restored.Position.X + 5000, restored.Position.Z),
                new FormationSpec(1, 2000, CardinalFacing.North));
            Assert.That(submit, Is.EqualTo(MatchCommandRejection.None),
                "the rebound session must accept commands locally");

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return acknowledgements.Count > 0;
                },
                DeliveryTimeoutSeconds,
                "the server must acknowledge a command sent after reattachment");

            Assert.That(acknowledgements[0].IsAccepted, Is.True,
                $"the post-reconnect move was rejected: {acknowledgements[0].Session}/" +
                $"{acknowledgements[0].Command}");

            for (var index = 0; index < 3; index++)
            {
                _server.Host.TickOnce();
            }

            var targetTick = _server.Host.CurrentTick;

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return session.Receiver.LastAppliedTick >= targetTick &&
                        session.World.TryGet(entity, out var moved) &&
                        moved.Position != restored.Position;
                },
                DeliveryTimeoutSeconds,
                () => "the rebound session must receive post-reconnect deltas; " +
                    $"paused={_server.Host.IsPaused}, tick={_server.Host.CurrentTick}, " +
                    $"target={targetTick}, applied={session.Receiver.LastAppliedTick}, " +
                    DescribeReconnect(session, keyframeInstalls, hasReconnectResponse, reconnectResult));

            Assert.That(_disconnectEvents, Is.GreaterThanOrEqualTo(1),
                "the forced break must surface as a transport disconnect");
            Assert.That(_receiverFailed, Is.False,
                "the receiver must never enter ConnectionFailed during recovery");
            Assert.That(session.ReconnectCoordinator.State,
                Is.Not.EqualTo(ClientReconnectState.Terminated));

            var serverUnitValid = _server.Host.Server.TryGetUnit(entity, out var serverUnit);
            Assert.That(serverUnitValid, Is.True);
            Assert.That(session.World.TryGet(entity, out var mirrored), Is.True);
            Assert.That(mirrored.Position, Is.EqualTo(serverUnit.Position),
                "the recovered mirror must agree with authority");
        }

        [UnityTest]
        public IEnumerator LiveUDP_DedicatedSliceRunner_AdvancesCountdownFromUpdate()
        {
            StartServer(packetLoss: false, 0, latency: false);
            var session = CreateClient(packetLoss: false, 0, latency: false);
            session.Connect("127.0.0.1", _serverPort);

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return session.IsConnected;
                },
                HandshakeTimeoutSeconds,
                "the dedicated client must complete the handshake before the slice runner starts");

            _sliceRoot = new GameObject("NetworkImpairmentPlayModeTests_Slice");
            _sliceRunner = _sliceRoot.AddComponent<TacticalVerticalSliceRunner>();
            _sliceRunner.InitializeDedicatedClient(
                session, createVisuals: false, autoStartRealTime: true);

            var coordinator = session.ReconnectCoordinator;
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            coordinator.OnReconnectResponse(new ReconnectResponse(
                ReconnectResult.Accepted,
                session.Endpoint.Player,
                session.Endpoint.Match,
                0,
                0));
            coordinator.OnKeyframeInstalled();
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown));

            var initial = coordinator.CountdownRemainingSeconds;
            var deadline = Time.realtimeSinceStartup + 1f;
            while (coordinator.CountdownRemainingSeconds >= initial &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown),
                "the observation window must not consume the whole countdown");
            Assert.That(coordinator.CountdownRemainingSeconds, Is.LessThan(initial),
                "TacticalVerticalSliceRunner.Update must advance the OD-20 countdown");
        }

        /// <summary>
        /// Raises server-to-client loss to 100% for a short pulse and proves
        /// the live client stops applying state, then restores the scenario
        /// rate and proves replication resumes. This is the in-test witness
        /// that loss simulation physically drops packets (a flag read cannot
        /// distinguish a working simulator from a compiled-out one).
        /// </summary>
        private IEnumerator ProvePacketLossDropsLiveTraffic(RemoteSliceSession session)
        {
            _serverCarrier.ConfigureSimulation(
                packetLoss: true, 100, latency: true, MinLatencyMs, MaxLatencyMs);

            var drainDeadline = Time.realtimeSinceStartup + 0.35f;
            while (Time.realtimeSinceStartup < drainDeadline)
            {
                PumpClients();
                yield return null;
            }

            var frozenServerTick = _server.Host.CurrentTick;
            var frozenAppliedTick = session.Receiver.LastAppliedTick;

            for (var index = 0; index < 4; index++)
            {
                _server.Host.TickOnce();
            }

            var pulseDeadline = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < pulseDeadline)
            {
                PumpClients();
                yield return null;
            }

            Assert.That(_server.Host.CurrentTick, Is.GreaterThan(frozenServerTick),
                "the server must have advanced ticks during the loss pulse");
            Assert.That(session.Receiver.LastAppliedTick, Is.EqualTo(frozenAppliedTick),
                "100% server-to-client loss must stop the client from applying new state");

            _serverCarrier.ConfigureSimulation(
                packetLoss: true, LossPercent, latency: true, MinLatencyMs, MaxLatencyMs);

            var resumeTarget = _server.Host.CurrentTick + 2;
            yield return WaitFor(
                () =>
                {
                    _server.Host.TickOnce();
                    PumpClients();
                    return session.Receiver.LastAppliedTick >= resumeTarget &&
                        session.Receiver.State == ReplicationReceiverState.Streaming;
                },
                DeliveryTimeoutSeconds,
                "replication must resume after the loss rate is restored to 5%");
        }

        private void StartServer(bool packetLoss, int lossChancePercent, bool latency)
        {
            _serverObject = new GameObject("NetworkImpairmentPlayModeTests_Server");
            _server = _serverObject.AddComponent<DedicatedServerRunner>();
            _server.Initialize(0);

            // Deterministic authority: the test is the only tick source.
            _server.Host.StopRealTime();

            _serverCarrier = _server.Carrier as LiteNetLibCarrier;
            Assert.That(_serverCarrier, Is.Not.Null,
                "DedicatedServerRunner.Initialize(0) must create a LiteNetLib carrier");

            _serverCarrier.ConfigureSimulation(
                packetLoss, lossChancePercent, latency, MinLatencyMs, MaxLatencyMs);

            _serverPort = _serverCarrier.LocalPort;
            Assert.That(_serverPort, Is.GreaterThan(0),
                "the dedicated server must bind a UDP port");
        }

        private RemoteSliceSession CreateClient(bool packetLoss, int lossChancePercent, bool latency)
        {
            var clock = new SystemTransportClock();
            var carrier = LiteNetLibCarrier.CreateClient(clock);
            carrier.ConfigureSimulation(
                packetLoss, lossChancePercent, latency, MinLatencyMs, MaxLatencyMs);

            var session = new RemoteSliceSession(carrier, clock);
            session.Disconnected += OnSessionDisconnected;
            _sessions.Add(session);
            return session;
        }

        private void OnSessionDisconnected(TransportDisconnectReason reason)
        {
            _disconnectEvents++;
        }

        private void PumpClients()
        {
            for (var index = 0; index < _sessions.Count; index++)
            {
                var session = _sessions[index];
                session.Pump(session.Clock.NowMs);

                if (session.Receiver.State == ReplicationReceiverState.ConnectionFailed)
                {
                    _receiverFailed = true;
                }
            }
        }

        private static string DescribeReconnect(
            RemoteSliceSession session,
            int keyframeInstalls,
            bool hasResponse,
            ReconnectResult result)
        {
            return $"endpoint={session.Endpoint.State}, connected={session.IsConnected}, " +
                $"coordinator={session.ReconnectCoordinator.State}, attempts={session.ReconnectAttemptCount}, " +
                $"response={(hasResponse ? result.ToString() : "none")}, " +
                $"secret={(session.Endpoint.Secret == default(SessionSecret32) ? "default" : "cached")}, " +
                $"receiver={session.Receiver.State}, installs={keyframeInstalls}, " +
                $"live={session.World.LiveCount}, applied={session.Receiver.LastAppliedTick}, " +
                $"session={session.Endpoint.Session}, player={session.Endpoint.Player}";
        }

        private string DescribeServerSession(SessionId sessionId)
        {
            if (_server == null || !_server.Host.TryGetSession(sessionId, out var record))
            {
                return ", serverSession=missing";
            }

            return $", serverSession={record.State}";
        }

        private static int GetServerConnectionId(DedicatedServerRunner server, SessionId session)
        {
            var transport = server.TransportHost;
            Assert.That(transport, Is.Not.Null,
                "DedicatedServerRunner must keep its ServerTransportHost");
            Assert.That(transport.Binder.TryGetTokenBySession(session, out var token), Is.True,
                "the victim session must still be bound on the server");
            Assert.That(transport.Binder.TryGetByToken(token, out var binding), Is.True,
                "the victim session token must resolve to a live connection");
            return binding.ConnectionId;
        }

        private static IEnumerator WaitFor(
            Func<bool> step,
            float timeoutSeconds,
            string failureMessage)
        {
            yield return WaitFor(step, timeoutSeconds, () => failureMessage);
        }

        private static IEnumerator WaitFor(
            Func<bool> step,
            float timeoutSeconds,
            Func<string> failureMessage)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!step())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Assert.Fail($"{failureMessage()} (waited {timeoutSeconds:F0} s)");
                }

                yield return null;
            }
        }

        private static EntityId FindOwnedUnit(ClientReplicationWorld world, PlayerId owner)
        {
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (world.TryGetSlotState(slot, out var state) && state.Owner == owner)
                {
                    return state.Entity;
                }
            }

            return default;
        }
    }
}
