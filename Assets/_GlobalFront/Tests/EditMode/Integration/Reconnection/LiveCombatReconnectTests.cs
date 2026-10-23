#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode.Integration.Replication;
using NUnit.Framework;
using CoreEntityId = GlobalFront.Core.Model.EntityId;
using ReconnectResult = GlobalFront.Core.Reconnect.ReconnectResult;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Integration.Reconnection
{
    /// <summary>
    /// Step 4.5.3 / OD-42: end-to-end live-combat reconnect over the real
    /// transport stack (LocalMatchHost → emitter → C2 deltas/keyframes →
    /// bridge → receiver) with 400 units, tactical pause, the OD-20 5-second
    /// countdown, grace expiry, in-flight keyframe interruption and the OD-19
    /// command queue.
    ///
    /// The reconnect socket is a raw <see cref="OwnDatagramCarrier"/> driven by
    /// <see cref="ReconnectLink"/>, exactly like the existing Phase 2.7
    /// integration coverage: the C0 response does not carry the new session
    /// token, so the harness reads it back from the server binder and binds a
    /// fresh uplink. That is a production gap recorded in CURRENT_STATE, not a
    /// test shortcut around any assertion.
    /// </summary>
    [TestFixture]
    public sealed class LiveCombatReconnectTests
    {
        private const int UnitCount = 400;
        private const int GraceTicks = 10;

        [Test]
        public void MidCombat_400Units_Reconnect_PreservesWorldParity()
        {
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatchWithUnits(UnitCount);

            var client0 = world.Clients[0];
            var coordinator = BindCoordinator(world, client0);

            // Two armies keep moving through the disconnect so the post-pause
            // deltas are non-empty and the checksum actually compares a world
            // that changed while the client was away.
            var player1Unit = client0.FirstEntityOf(new PlayerId(1));
            var player2Unit = world.Clients[1].FirstEntityOf(new PlayerId(2));
            client0.SubmitMoveTo(
                new PlayerId(1), player1Unit, new WorldPointMm(400_000, 0), requestedTick: 1);
            world.Clients[1].SubmitMoveTo(
                new PlayerId(2), player2Unit, new WorldPointMm(-400_000, 0), requestedTick: 1);

            for (var tick = 0; tick < 10; tick++)
            {
                world.PumpTick();
            }

            Assert.That(client0.Receiver.LastAppliedTick, Is.EqualTo(10UL),
                "the client must be streaming before the disconnect");
            Assert.That(client0.Receiver.TryGetUnit(player1Unit, out var beforeDrop), Is.True);
            Assert.That(beforeDrop.HasMoveTarget, Is.True, "the player-1 army must be moving");
            var pausedTick = world.Rig.Host.CurrentTick;
            var positionBeforeDrop = beforeDrop.Position;

            DropClient(world, client0);
            Assert.That(client0.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected));
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Reconnecting));
            Assert.That(world.Rig.Host.IsPaused, Is.True,
                "the host must enter tactical pause on the transport loss (OD-18)");
            Assert.That(world.Rig.Host.TickOnce(), Is.False,
                "the simulation must stay frozen during the tactical pause");
            Assert.That(world.Rig.Host.CurrentTick, Is.EqualTo(pausedTick),
                "the paused tick counter must not advance");

            var link = new ReconnectLink(world);
            var response = link.Reconnect(coordinator, client0.Bridge);
            Assert.That(response.Result, Is.EqualTo(ReconnectResult.Accepted),
                "the server must accept the reconnect inside the grace window");
            Assert.That(world.Rig.Host.IsPaused, Is.True,
                "the host must stay paused while the keyframe is streamed");

            link.PumpUntil(() => client0.Receiver.IsWorldUsable, client0.Bridge, budgetMs: 10_000);
            Assert.That(client0.Receiver.IsWorldUsable, Is.True,
                "the 400-unit keyframe must install a usable baseline");
            Assert.That(client0.Receiver.CurrentKeyframeSeq,
                Is.EqualTo(response.ActiveKeyframeSeq));
            Assert.That(client0.Receiver.World.LiveCount, Is.EqualTo(UnitCount),
                "the baseline must restore all 400 units");

            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown),
                "KeyframeInstalled must start the OD-20 countdown automatically");
            Assert.That(coordinator.CountdownRemainingSeconds, Is.EqualTo(5.0f).Within(0.0001f),
                "the OD-20 countdown must start at 5.0 seconds");
            Assert.That(world.Rig.Host.IsPaused, Is.True,
                "the host must remain paused for the whole countdown");

            coordinator.AdvanceCountdown(5.0f, out var ready);
            Assert.That(ready, Is.True);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Connected));
            Assert.That(world.Rig.Host.IsPaused, Is.False,
                "Simulation must resume automatically when countdown finishes");

            var checksumsBefore = world.Emitter.StateChecksumCount;
            var clientChecksumsBefore = link.ChecksumDeltaCount;
            for (var tick = 0; tick < 20; tick++)
            {
                world.PumpTick();
                link.Pump(5);
                link.Feed(client0.Bridge);
                // Extra virtual time for the receive rate limiter: the delta
                // stream keeps the packet bucket honest without ticking.
                link.Pump(5);
                link.Feed(client0.Bridge);
            }

            link.PumpUntil(
                () => client0.Receiver.LastAppliedTick >= world.Rig.Host.CurrentTick &&
                      client0.Receiver.State == ReplicationReceiverState.Streaming &&
                      !client0.Receiver.WorldPartiallyApplied,
                client0.Bridge,
                budgetMs: 10_000);

            Assert.That(world.Emitter.StateChecksumCount, Is.GreaterThan(checksumsBefore),
                "the post-resume window must contain a checksum-bearing delta");
            Assert.That(link.ChecksumDeltaCount, Is.GreaterThan(clientChecksumsBefore),
                "client 0 must itself receive a checksum-bearing delta after the resync");
            Assert.That(client0.Receiver.ChecksumMismatchCount, Is.EqualTo(0),
                "the resynced mirror must match every authoritative checksum");
            Assert.That(client0.Receiver.World.LiveCount, Is.EqualTo(UnitCount),
                "all 400 units must be present after the resume");
            Assert.That(client0.Receiver.TryGetUnit(player1Unit, out var afterResume), Is.True);
            Assert.That(afterResume.Position, Is.Not.EqualTo(positionBeforeDrop),
                "the paused army must have kept moving after the resume");

            world.AssertWorldsMatch(client0);
        }

        [Test]
        public void Reconnect_AfterGraceExpired_RejectsWithGraceExpired_AndPlayerIdNotReused()
        {
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(),
                ServerReplicationEmitterConfig.Default,
                clientCount: 2,
                disconnectGraceTicks: GraceTicks);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client0 = world.Clients[0];
            var coordinator = BindCoordinator(world, client0);

            DropClient(world, client0);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Reconnecting));
            Assert.That(world.Rig.Host.IsPaused, Is.True);

            // The grace window is tick-based (OD-2): resume and advance 15
            // ticks, past the configured 10-tick window.
            world.Rig.Host.Resume();
            for (var tick = 0; tick < 15; tick++)
            {
                Assert.That(world.Rig.Host.TickOnce(), Is.True,
                    "the resumed host must keep ticking through the sweep");
            }

            Assert.That(world.Rig.Host.Sessions.TryGetSession(client0.Endpoint.Session, out var sessionRecord), Is.True);
            Assert.That(sessionRecord.State, Is.EqualTo(SessionState.Closed),
                "the grace sweep must close the session");
            Assert.That(
                world.Rig.Host.Sessions.TryGetPlayerState(world.Rig.Match, new PlayerId(1), out var slotState),
                Is.True);
            Assert.That(slotState, Is.EqualTo(PlayerConnectionState.Abandoned),
                "the abandoned slot must stay retired");

            var link = new ReconnectLink(world);
            var response = link.Reconnect(coordinator, client0.Bridge);
            Assert.That(response.Result, Is.EqualTo(ReconnectResult.GraceExpired),
                "a valid secret must not resurrect a session past its grace window");
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Terminated));

            // A third client cannot take the retired slot: the match is
            // running, so the transport denies the join and no PlayerId is
            // consumed.
            var denyReason = ConnectDenyReason.None;
            var third = world.AddClient();
            third.Endpoint.Denied += reason => denyReason = reason;
            for (var pump = 0; pump < 40 && third.Endpoint.State != ClientTransportState.Disconnected; pump++)
            {
                world.Pump(5);
            }

            Assert.That(third.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected),
                "the third client must be denied");
            Assert.That(denyReason, Is.EqualTo(ConnectDenyReason.Internal));
            Assert.That(world.Rig.Host.Sessions.TryGetAssignedCount(world.Rig.Match, out var assignedCount), Is.True);
            Assert.That(assignedCount, Is.EqualTo(2),
                "a denied client must not consume a PlayerId");
            Assert.That(
                world.Rig.Host.Sessions.TryGetPlayerState(world.Rig.Match, new PlayerId(3), out _),
                Is.False,
                "PlayerId(3) must not exist in the abandoned match");

            // OD-3 registry proof: the retired slot is never handed out again.
            // A running match rejects joins outright, so the monotonic
            // assignment is exercised on a forming match of the same host.
            var forming = world.Rig.Host.CreateSessionMatch(3);
            var firstSession = world.Rig.Host.CreateSession(world.Rig.Host.CreateConnectionHandle());
            var secondSession = world.Rig.Host.CreateSession(world.Rig.Host.CreateConnectionHandle());
            var thirdSession = world.Rig.Host.CreateSession(world.Rig.Host.CreateConnectionHandle());
            Assert.That(world.Rig.Host.TryJoinMatch(firstSession, forming, out var firstPlayer),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(world.Rig.Host.TryJoinMatch(secondSession, forming, out var secondPlayer),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(firstPlayer.Value, Is.EqualTo(1));
            Assert.That(secondPlayer.Value, Is.EqualTo(2));

            Assert.That(world.Rig.Host.CloseSession(firstSession), Is.True);
            Assert.That(world.Rig.Host.TryJoinMatch(thirdSession, forming, out var thirdPlayer),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(thirdPlayer.Value, Is.EqualTo(3),
                "OD-3: a retired PlayerId is never reassigned");
            Assert.That(thirdPlayer.Value, Is.Not.EqualTo(firstPlayer.Value));

            // The E2E path above is closed by the per-tick sweep before
            // TryRebindSession can run. Exercise the guard itself with the
            // sweep deliberately absent: a rebind at tick 11 must still be
            // rejected for a manager whose disconnected session is intact.
            var manager = new SessionManager(GraceTicks);
            var managerMatch = manager.CreateMatch(2);
            var managerA = manager.CreateSession(manager.CreateConnectionHandle(), out var secretA);
            var managerB = manager.CreateSession(manager.CreateConnectionHandle());
            Assert.That(manager.TryJoinMatch(managerA, managerMatch, out _),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(manager.TryJoinMatch(managerB, managerMatch, out _),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(manager.NotifyConnectionLost(managerA, 0UL), Is.True);

            var rebind = manager.TryRebindSession(
                managerA, in secretA, manager.CreateConnectionHandle(), 11UL, out _);
            Assert.That(rebind, Is.EqualTo(RebindResult.GraceExpired),
                "TryRebindSession must enforce the grace window even before the tick sweep");
        }

        [Test]
        public void Disconnect_DuringKeyframeInFlight_PreservesMonotonicSeq()
        {
            // A 512-byte slice budget splits the 400-unit baseline into ~34
            // slices, so the first reconnect can be interrupted mid-assembly.
            var config = new ServerReplicationEmitterConfig(
                ServerReplicationEmitterConfig.DefaultMaxClients,
                ServerReplicationEmitterConfig.DefaultSnapshotCapacity,
                ServerReplicationEmitterConfig.DefaultAddCapacity,
                ServerReplicationEmitterConfig.DefaultUpdateCapacity,
                ServerReplicationEmitterConfig.DefaultRemoveCapacity,
                ServerReplicationEmitterConfig.DefaultPacingRefillBytesPerTick,
                ServerReplicationEmitterConfig.DefaultPacingBurstCapBytes,
                maxSlicePayloadBytes: 512,
                ServerReplicationEmitterConfig.DefaultPeriodicKeyframeTicks);
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), config, clientCount: 2);
            world.StartMatchWithUnits(UnitCount);

            var client0 = world.Clients[0];
            var coordinator = BindCoordinator(world, client0);
            for (var tick = 0; tick < 20 && !client0.Receiver.IsWorldUsable; tick++)
            {
                world.PumpTick();
            }

            Assert.That(client0.Receiver.IsWorldUsable, Is.True,
                "the sliced 400-unit baseline must install");
            Assert.That(client0.Bridge.AssembledKeyframeCount, Is.EqualTo(1));
            Assert.That(client0.Bridge.AssembledSliceCount, Is.GreaterThan(2),
                "the fixture must actually split the keyframe into many slices");
            var installedKeyframes = client0.Bridge.AssembledKeyframeCount;
            var acceptedSlices = client0.Bridge.AssembledSliceCount;
            var duplicateSlices = client0.Bridge.DroppedDuplicateSliceCount;

            DropClient(world, client0);
            Assert.That(world.Rig.Host.IsPaused, Is.True);

            var firstLink = new ReconnectLink(world);
            var firstResponse = firstLink.Reconnect(coordinator, client0.Bridge);
            Assert.That(firstResponse.Result, Is.EqualTo(ReconnectResult.Accepted));

            var elapsed = 0;
            while (firstLink.Captured.Count == 0 && elapsed < 1_000)
            {
                firstLink.Pump(5);
                elapsed += 5;
            }

            Assert.That(firstLink.TryFeedOne(client0.Bridge, out var firstSlice), Is.True,
                "the first reconnected keyframe slice must arrive");
            Assert.That(client0.Bridge.AssembledSliceCount, Is.EqualTo(acceptedSlices + 1));
            Assert.That(client0.Bridge.AssembledKeyframeCount, Is.EqualTo(installedKeyframes),
                "one slice must not complete the keyframe");

            // A duplicate of the accepted part proves the partial assembly is
            // still live and rejects replays.
            client0.Bridge.HandleSnapshotPayload(
                firstSlice.Tick, firstSlice.Payload, firstSlice.Length, world.Rig.Clock.NowMs);
            Assert.That(client0.Bridge.DroppedDuplicateSliceCount, Is.EqualTo(duplicateSlices + 1),
                "the partial assembly must still hold its accepted part");

            // Drop the reconnect socket before the remaining slices arrive.
            firstLink.Drop();
            for (var pump = 0; pump < 5; pump++)
            {
                world.Pump(5);
            }

            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Reconnecting));
            Assert.That(world.Rig.Host.IsPaused, Is.True,
                "the interrupted resync must keep the tactical pause");
            Assert.That(client0.Bridge.AssembledKeyframeCount, Is.EqualTo(installedKeyframes),
                "an incomplete assembly must never install a keyframe");

            // PrepareForResync must discard the partial assembly: re-feeding
            // the same part is accepted again instead of dropped as duplicate.
            client0.Bridge.PrepareForResync(firstResponse.ActiveKeyframeSeq);
            client0.Bridge.HandleSnapshotPayload(
                firstSlice.Tick, firstSlice.Payload, firstSlice.Length, world.Rig.Clock.NowMs);
            Assert.That(client0.Bridge.AssembledSliceCount, Is.EqualTo(acceptedSlices + 2),
                "PrepareForResync must reset the staged slice assembly");

            var secondLink = new ReconnectLink(world);
            var secondResponse = secondLink.Reconnect(coordinator, client0.Bridge);
            Assert.That(secondResponse.Result, Is.EqualTo(ReconnectResult.Accepted));
            Assert.That(secondResponse.ActiveKeyframeSeq,
                Is.GreaterThanOrEqualTo(firstResponse.ActiveKeyframeSeq),
                "the keyframe generation must never go backwards");
            Assert.That(secondResponse.ActiveKeyframeSeq, Is.GreaterThan(firstResponse.ActiveKeyframeSeq),
                "a fresh keyframe must advance the generation");

            secondLink.PumpUntil(() => client0.Receiver.IsWorldUsable, client0.Bridge, budgetMs: 10_000);
            Assert.That(client0.Receiver.IsWorldUsable, Is.True);
            Assert.That(client0.Receiver.CurrentKeyframeSeq,
                Is.EqualTo(secondResponse.ActiveKeyframeSeq));
            Assert.That(client0.Bridge.AssembledKeyframeSeq,
                Is.EqualTo(secondResponse.ActiveKeyframeSeq));
            Assert.That(client0.Receiver.World.LiveCount, Is.EqualTo(UnitCount));
            world.AssertWorldsMatch(client0);
        }

        [Test]
        public void CommandPreservation_AcrossReconnect_DispatchesAfterPause()
        {
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client0 = world.Clients[0];
            var coordinator = BindCoordinator(world, client0);
            for (var tick = 0; tick < 3; tick++)
            {
                world.PumpTick();
            }

            Assert.That(client0.Receiver.IsWorldUsable, Is.True);
            var player1Unit = client0.FirstEntityOf(new PlayerId(1));
            Assert.That(world.Rig.Host.Server.TryGetLastAcceptedSequence(new PlayerId(1), out _), Is.False,
                "the fixture needs a PlayerId 1 with no accepted command yet");

            DropClient(world, client0);
            Assert.That(world.Rig.Host.IsPaused, Is.True);

            // The client-side queue holds the order across the disconnect,
            // the resync and the countdown (OD-19).
            var queue = new PrototypeCommandQueue(new UnitRegistry(), new PlayerId(1), formationSpacingMm: 2000);
            var destination = new WorldPointMm(200_000, 0);
            var requestedTick = world.Rig.Host.CurrentTick + 1;
            queue.QueueMove(destination, new[] { player1Unit }, requestedTick);
            Assert.That(queue.PendingCount, Is.EqualTo(1), "the move must enter the local queue");

            var link = new ReconnectLink(world);
            var response = link.Reconnect(coordinator, client0.Bridge);
            Assert.That(response.Result, Is.EqualTo(ReconnectResult.Accepted));
            link.PumpUntil(() => client0.Receiver.IsWorldUsable, client0.Bridge, budgetMs: 10_000);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown));
            Assert.That(queue.PendingCount, Is.EqualTo(1),
                "the resync and countdown must not drop the queued command");

            coordinator.AdvanceCountdown(5.0f, out var ready);
            Assert.That(ready, Is.True);
            Assert.That(world.Rig.Host.IsPaused, Is.False);
            Assert.That(queue.PendingCount, Is.EqualTo(1),
                "the automatic unpause must not drop the queued command");

            world.PumpTick();
            var channel = new ReconnectedCommandChannel(link);
            queue.ForwardPending(world.Rig.Host.CurrentTick, channel);
            Assert.That(queue.PendingCount, Is.Zero,
                "the preserved command must be dispatched once unpaused");

            var elapsed = 0;
            while (link.CommandAcks.Count == 0 && elapsed < 5_000)
            {
                link.Pump(5);
                elapsed += 5;
            }

            Assert.That(link.CommandAcks.Count, Is.GreaterThan(0),
                "the server must acknowledge the preserved command");
            var ack = link.CommandAcks[0];
            Assert.That(ack.CommandSequence, Is.EqualTo(1U));
            Assert.That(ack.Session, Is.EqualTo(SessionRejection.None));
            Assert.That(ack.Command, Is.EqualTo(MatchCommandRejection.None),
                "the server must not cut the command as a duplicate sequence");
            Assert.That(world.Rig.Host.Server.TryGetLastAcceptedSequence(new PlayerId(1), out var accepted), Is.True);
            Assert.That(accepted, Is.EqualTo(1U));

            for (var tick = 0; tick < 3; tick++)
            {
                world.PumpTick();
                link.Pump(5);
                link.Feed(client0.Bridge);
            }

            Assert.That(world.Rig.Host.Server.TryGetUnit(player1Unit, out var serverUnit), Is.True);
            Assert.That(serverUnit.HasMoveTarget, Is.True,
                "the server must apply the preserved order after the unpause");
            Assert.That(serverUnit.MoveTarget, Is.EqualTo(destination));
            Assert.That(serverUnit.Position.X, Is.GreaterThan(0), "the unit must start marching");
            Assert.That(client0.Receiver.TryGetUnit(player1Unit, out var mirrored), Is.True);
            Assert.That(mirrored.HasMoveTarget, Is.True,
                "the post-pause order must replicate to the client");
        }

        private static ClientReconnectCoordinator BindCoordinator(
            ReplicationIntegrationWorld world,
            ReplicationIntegrationWorld.ClientLeg client)
        {
            var coordinator = new ClientReconnectCoordinator(client.Receiver);
            world.Rig.Host.BindReconnectCoordinator(coordinator);
            coordinator.ResyncStarted += sequence => client.Bridge.PrepareForResync(sequence);
            coordinator.CacheSession(
                client.Endpoint.Session,
                client.Endpoint.Match,
                client.Endpoint.Player,
                client.Endpoint.Secret);
            client.Endpoint.Lost += reason => coordinator.OnTransportDisconnected(reason);
            return coordinator;
        }

        private static void DropClient(
            ReplicationIntegrationWorld world,
            ReplicationIntegrationWorld.ClientLeg client)
        {
            Assert.That(
                world.Rig.ServerTransport.Binder.TryGetTokenBySession(client.Endpoint.Session, out var token),
                Is.True);
            Assert.That(world.Rig.ServerTransport.Binder.TryGetByToken(token, out var binding), Is.True);
            client.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            world.Rig.ServerCarrier.CloseConnection(binding.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var pump = 0; pump < 5; pump++)
            {
                world.Pump(5);
            }
        }

        private readonly struct CapturedSnapshot
        {
            public CapturedSnapshot(ulong tick, byte[] payload, int length)
            {
                Tick = tick;
                Payload = payload;
                Length = length;
            }

            public ulong Tick { get; }
            public byte[] Payload { get; }
            public int Length { get; }
        }

        /// <summary>
        /// One reconnect socket: a fresh client carrier, the C0 request, the
        /// decoded response, the captured C2 stream and an uplink bound to the
        /// server-issued token. Each link owns exactly one connection because
        /// the server binds one socket to one session.
        /// </summary>
        private sealed class ReconnectLink
        {
            private readonly ReplicationIntegrationWorld _world;
            private readonly byte[] _requestBuffer = new byte[ReconnectRequest.SizeBytes];
            private readonly byte[] _messageBuffer = new byte[1_024];
            private readonly List<CapturedSnapshot> _captured = new List<CapturedSnapshot>();
            private readonly List<CommandAckPayload> _commandAcks = new List<CommandAckPayload>();
            private ReconnectResponse _response;
            private bool _hasResponse;
            private SessionId _session;

            public ReconnectLink(ReplicationIntegrationWorld world)
            {
                _world = world;
                var address = world.Rig.Pipe.CreateEndpoint();
                Carrier = OwnDatagramCarrier.CreateClient(
                    world.Rig.Pipe,
                    address,
                    TransportTestSupport.ServerAddressOf(world.Rig),
                    world.Rig.Clock);
                ConnectionId = Carrier.ConnectToServer("virtual", 0);
                for (var pump = 0; pump < 5; pump++)
                {
                    Pump(5);
                }
            }

            public OwnDatagramCarrier Carrier { get; }

            public int ConnectionId { get; }

            public ulong Token { get; private set; }

            public IReadOnlyList<CapturedSnapshot> Captured => _captured;

            public IReadOnlyList<CommandAckPayload> CommandAcks => _commandAcks;

            /// <summary>Captured deltas for this session that carried a state checksum.</summary>
            public int ChecksumDeltaCount { get; private set; }

            public ReconnectResponse Reconnect(
                ClientReconnectCoordinator coordinator,
                ClientTransportReplicationBridge bridge,
                int budgetMs = 5_000)
            {
                Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Reconnecting),
                    "the coordinator must await a reconnect before a request is built");
                coordinator.LastAppliedTick = coordinator.Receiver.LastAppliedTick;
                Assert.That(coordinator.TryBuildReconnectRequest(_requestBuffer, out var written), Is.True);
                Assert.That(
                    Carrier.Send(ConnectionId, TransportChannel.Control, _requestBuffer, 0, written),
                    Is.True);

                var elapsed = 0;
                while (!_hasResponse && elapsed < budgetMs)
                {
                    Pump(5);
                    elapsed += 5;
                }

                Assert.That(_hasResponse, Is.True,
                    "the server must answer the reconnect request within the budget");
                coordinator.OnReconnectResponse(in _response);

                if (_response.Result == ReconnectResult.Accepted)
                {
                    _session = coordinator.SessionId;
                    Assert.That(
                        _world.Rig.ServerTransport.Binder.TryGetTokenBySession(_session, out var token),
                        Is.True,
                        "the rebind must install a fresh transport binding");
                    Token = token;
                    Carrier.AssignSessionToken(ConnectionId, token);
                    bridge.BindUplink(new ReconnectUplink(this));
                }

                return _response;
            }

            public void Pump(int milliseconds = 5)
            {
                _world.Rig.Pump(milliseconds);
                Carrier.Pump(_world.Rig.Clock.NowMs);
                DrainEvents();
            }

            public int Feed(ClientTransportReplicationBridge bridge, int maximum = int.MaxValue)
            {
                var fed = 0;
                while (fed < maximum && _captured.Count > 0)
                {
                    var snapshot = _captured[0];
                    _captured.RemoveAt(0);
                    bridge.HandleSnapshotPayload(
                        snapshot.Tick, snapshot.Payload, snapshot.Length, _world.Rig.Clock.NowMs);
                    fed++;
                }

                return fed;
            }

            public bool TryFeedOne(ClientTransportReplicationBridge bridge, out CapturedSnapshot snapshot)
            {
                if (_captured.Count == 0)
                {
                    snapshot = default;
                    return false;
                }

                snapshot = _captured[0];
                _captured.RemoveAt(0);
                bridge.HandleSnapshotPayload(
                    snapshot.Tick, snapshot.Payload, snapshot.Length, _world.Rig.Clock.NowMs);
                return true;
            }

            public void PumpUntil(
                Func<bool> condition,
                ClientTransportReplicationBridge bridge,
                int budgetMs = 5_000)
            {
                var elapsed = 0;
                while (!condition() && elapsed < budgetMs)
                {
                    Pump(5);
                    Feed(bridge);
                    elapsed += 5;
                }
            }

            public bool SendCommand(byte[] payload, int length)
            {
                if (payload == null || length <= 0 || length > _messageBuffer.Length)
                {
                    return false;
                }

                var offset = MessageCodec.WriteHeader(
                    _messageBuffer, 0, TransportMessageType.Command, Token, 0);
                if (offset + length > _messageBuffer.Length)
                {
                    return false;
                }

                Array.Copy(payload, 0, _messageBuffer, offset, length);
                return Carrier.Send(
                    ConnectionId, TransportChannel.Command, _messageBuffer, 0, offset + length);
            }

            public void Drop()
            {
                Carrier.CloseConnection(ConnectionId, TransportDisconnectReason.TransportLost);
                if (_session.IsValid &&
                    _world.Rig.ServerTransport.Binder.TryGetTokenBySession(_session, out var token) &&
                    _world.Rig.ServerTransport.Binder.TryGetByToken(token, out var binding))
                {
                    _world.Rig.ServerCarrier.CloseConnection(
                        binding.ConnectionId, TransportDisconnectReason.TransportLost);
                }
            }

            private void DrainEvents()
            {
                while (Carrier.TryDequeueEvent(out var transportEvent))
                {
                    if (transportEvent.Type != TransportEventType.Message ||
                        transportEvent.Data == null ||
                        transportEvent.Length <= 0)
                    {
                        continue;
                    }

                    if (transportEvent.Data[0] == ReconnectWireCodec.ResponseOpcode &&
                        ReconnectWireCodec.TryDecodeResponse(
                            transportEvent.Data.AsSpan(0, transportEvent.Length), out var response))
                    {
                        _response = response;
                        _hasResponse = true;
                        continue;
                    }

                    var error = MessageCodec.TryDecodeHeader(
                        transportEvent.Data, 0, transportEvent.Length, out var header);
                    if (error != MessageError.None)
                    {
                        continue;
                    }

                    switch (header.Type)
                    {
                        case TransportMessageType.Snapshot:
                            var payload = new byte[header.PayloadLength];
                            Array.Copy(
                                transportEvent.Data,
                                header.PayloadOffset,
                                payload,
                                0,
                                header.PayloadLength);
                            if (payload.Length > 0 &&
                                payload[0] == DeltaSnapshotProtocol.MessageTypeDelta &&
                                DeltaSnapshotWireCodec.TryDecodeHeader(
                                    payload.AsSpan(0, payload.Length), out var deltaHeader) ==
                                DeltaCodecResult.Ok &&
                                deltaHeader.HasChecksum)
                            {
                                ChecksumDeltaCount++;
                            }

                            _captured.Add(new CapturedSnapshot(
                                header.SnapshotTick, payload, header.PayloadLength));
                            break;
                        case TransportMessageType.CommandAck:
                            if (MessageCodec.TryDecodeCommandAck(
                                    transportEvent.Data,
                                    header.PayloadOffset,
                                    transportEvent.Length,
                                    out var ack))
                            {
                                _commandAcks.Add(ack);
                            }

                            break;
                    }
                }
            }

            private sealed class ReconnectUplink : IReplicationUplink
            {
                private readonly ReconnectLink _link;
                private readonly byte[] _buffer = new byte[256];

                public ReconnectUplink(ReconnectLink link)
                {
                    _link = link;
                }

                public event Action<ulong, byte[], int> SnapshotPayloadReceived;

                public bool TrySendFeedback(ReplicationFeedbackKind kind, byte[] payload, int length)
                {
                    if (payload == null || length <= 0)
                    {
                        return false;
                    }

                    var type = kind == ReplicationFeedbackKind.Ack
                        ? TransportMessageType.SnapshotAck
                        : TransportMessageType.ReplicationRequest;
                    var offset = MessageCodec.WriteHeader(_buffer, 0, type, _link.Token, 0);
                    Array.Copy(payload, 0, _buffer, offset, length);
                    return _link.Carrier.Send(
                        _link.ConnectionId, TransportChannel.Control, _buffer, 0, offset + length);
                }
            }
        }

        /// <summary>
        /// Commands on the reconnected socket. <see cref="NetworkCommandChannel"/>
        /// wraps a <c>ClientTransportEndpoint</c>, which never learns the token
        /// the server issues on rebind (the C0 response omits it), so this
        /// channel sends the same <see cref="CommandWireCodec"/> payload over
        /// the rebound carrier with the token read back from the binder.
        /// </summary>
        private sealed class ReconnectedCommandChannel : ICommandChannel
        {
            private readonly ReconnectLink _link;
            private readonly byte[] _buffer = new byte[1_024];

            public ReconnectedCommandChannel(ReconnectLink link)
            {
                _link = link;
            }

            public MatchCommandRejection TrySubmitMove(
                CommandHeader header,
                CoreEntityId[] entities,
                WorldPointMm destination,
                FormationSpec formation)
            {
                var length = CommandWireCodec.EncodeMove(
                    _buffer, header, entities, destination, formation);
                return _link.SendCommand(_buffer, length)
                    ? MatchCommandRejection.None
                    : MatchCommandRejection.SessionRejected;
            }

            public MatchCommandRejection TrySubmitAttack(
                CommandHeader header,
                CoreEntityId[] attackers,
                CoreEntityId target)
            {
                var length = CommandWireCodec.EncodeAttack(_buffer, header, attackers, target);
                return _link.SendCommand(_buffer, length)
                    ? MatchCommandRejection.None
                    : MatchCommandRejection.SessionRejected;
            }

            public MatchCommandRejection TrySubmitStop(
                CommandHeader header,
                CoreEntityId[] entities)
            {
                var length = CommandWireCodec.EncodeStop(_buffer, header, entities);
                return _link.SendCommand(_buffer, length)
                    ? MatchCommandRejection.None
                    : MatchCommandRejection.SessionRejected;
            }
        }
    }
}
