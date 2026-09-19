using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using CoreEntityId = GlobalFront.Core.Model.EntityId;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Client
{
    [TestFixture]
    public sealed class ClientReconnectTests
    {
        private SessionId _sessionId;
        private MatchId _matchId;
        private PlayerId _playerId;
        private SessionSecret32 _secret;
        private List<GameObject> _createdObjects;

        [SetUp]
        public void SetUp()
        {
            _sessionId = new SessionId(Guid.NewGuid());
            _matchId = new MatchId(98765432101234UL);
            _playerId = new PlayerId(3);

            var secretBytes = new byte[32];
            for (var i = 0; i < 32; i++)
            {
                secretBytes[i] = (byte)(0x20 + i);
            }

            _secret = new SessionSecret32(secretBytes);
            _createdObjects = new List<GameObject>();
        }

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _createdObjects.Count; index++)
            {
                if (_createdObjects[index] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_createdObjects[index]);
                }
            }

            _createdObjects.Clear();
        }

        [Test]
        public void Coordinator_InitialState_IsIdle()
        {
            var coordinator = new ClientReconnectCoordinator();
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Idle));
            Assert.That(coordinator.CanSubmitCommands, Is.False);
            Assert.That(coordinator.IsReconnecting, Is.False);
            Assert.That(coordinator.ReconnectAttemptCount, Is.EqualTo(0));
            Assert.That(coordinator.CountdownRemainingSeconds, Is.EqualTo(0f));
        }

        [Test]
        public void Coordinator_CacheSession_TransitionsToConnected()
        {
            var coordinator = new ClientReconnectCoordinator();
            var stateChangedCalled = false;
            coordinator.StateChanged += state =>
            {
                if (state == ClientReconnectState.Connected)
                {
                    stateChangedCalled = true;
                }
            };

            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);

            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Connected));
            Assert.That(coordinator.SessionId, Is.EqualTo(_sessionId));
            Assert.That(coordinator.MatchId, Is.EqualTo(_matchId));
            Assert.That(coordinator.PlayerId, Is.EqualTo(_playerId));
            Assert.That(coordinator.Secret, Is.EqualTo(_secret));
            Assert.That(coordinator.CanSubmitCommands, Is.True);
            Assert.That(coordinator.IsReconnecting, Is.False);
            Assert.That(coordinator.CountdownRemainingSeconds, Is.EqualTo(ClientReconnectCoordinator.DefaultCountdownSeconds));
            Assert.That(stateChangedCalled, Is.True);
        }

        [Test]
        public void Coordinator_OnTransportDisconnected_TransitionsToReconnecting_AndTracksAttempts()
        {
            var coordinator = new ClientReconnectCoordinator();
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);

            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Reconnecting));
            Assert.That(coordinator.ReconnectAttemptCount, Is.EqualTo(1));
            Assert.That(coordinator.CanSubmitCommands, Is.False);
            Assert.That(coordinator.IsReconnecting, Is.True);

            coordinator.OnTransportDisconnected(TransportDisconnectReason.Timeout);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Reconnecting));
            Assert.That(coordinator.ReconnectAttemptCount, Is.EqualTo(2));
        }

        [Test]
        public void Coordinator_TryBuildReconnectRequest_EncodesCorrectWireBytes()
        {
            var coordinator = new ClientReconnectCoordinator();
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);
            coordinator.LastAppliedTick = 420UL;
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);

            Span<byte> buffer = stackalloc byte[128];
            var success = coordinator.TryBuildReconnectRequest(buffer, out var written);

            Assert.That(success, Is.True);
            Assert.That(written, Is.EqualTo(ReconnectRequest.SizeBytes));

            var decoded = ReconnectWireCodec.TryDecodeRequest(buffer.Slice(0, written), out var request);
            Assert.That(decoded, Is.True);
            Assert.That(request.Opcode, Is.EqualTo(ReconnectRequest.ExpectedOpcode));
            Assert.That(request.ProtocolVersion, Is.EqualTo(ReconnectRequest.ExpectedProtocolVersion));
            Assert.That(request.SessionId, Is.EqualTo(_sessionId));
            Assert.That(request.Secret, Is.EqualTo(_secret));
            Assert.That(request.LastAppliedTick, Is.EqualTo(420UL));
        }

        [Test]
        public void Coordinator_TryBuildReconnectRequest_AllocatesZeroGC()
        {
            var coordinator = new ClientReconnectCoordinator();
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);
            coordinator.LastAppliedTick = 100UL;
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);

            var preallocatedBuffer = new byte[ReconnectRequest.SizeBytes];
            Assert.That(() =>
            {
                coordinator.TryBuildReconnectRequest(preallocatedBuffer, out _);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Coordinator_TryBuildReconnectRequest_Fails_WhenInvalidStateOrBufferTooSmall()
        {
            var coordinator = new ClientReconnectCoordinator();
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);

            var buffer = new byte[128];
            // Connected state: not in Reconnecting
            Assert.That(coordinator.TryBuildReconnectRequest(buffer, out var written1), Is.False);
            Assert.That(written1, Is.EqualTo(0));

            // Reconnecting state, but undersized buffer (63 bytes < 64 bytes)
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            var smallBuffer = new byte[ReconnectRequest.SizeBytes - 1];
            Assert.That(coordinator.TryBuildReconnectRequest(smallBuffer, out var written2), Is.False);
            Assert.That(written2, Is.EqualTo(0));
        }

        [Test]
        public void Coordinator_OnReconnectResponse_Accepted_TransitionsToResyncing()
        {
            var coordinator = new ClientReconnectCoordinator();
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);

            ushort notifiedSeq = 0;
            coordinator.ResyncStarted += seq => notifiedSeq = seq;

            var response = new ReconnectResponse(
                ReconnectResult.Accepted,
                _playerId,
                _matchId,
                serverTick: 150UL,
                activeKeyframeSeq: 4);

            coordinator.OnReconnectResponse(in response);

            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Resyncing));
            Assert.That(coordinator.ActiveKeyframeSeq, Is.EqualTo((ushort)4));
            Assert.That(coordinator.ServerTick, Is.EqualTo(150UL));
            Assert.That(notifiedSeq, Is.EqualTo((ushort)4));
            Assert.That(coordinator.IsReconnecting, Is.True);
            Assert.That(coordinator.CanSubmitCommands, Is.False);
        }

        [TestCase(ReconnectResult.GraceExpired)]
        [TestCase(ReconnectResult.InvalidSecret)]
        [TestCase(ReconnectResult.SessionNotFound)]
        [TestCase(ReconnectResult.MatchFinished)]
        public void Coordinator_OnReconnectResponse_Rejections_TransitionsToTerminated(ReconnectResult result)
        {
            var coordinator = new ClientReconnectCoordinator();
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);

            var response = new ReconnectResponse(
                result,
                _playerId,
                _matchId,
                serverTick: 100UL,
                activeKeyframeSeq: 2);

            coordinator.OnReconnectResponse(in response);

            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Terminated));
            Assert.That(coordinator.IsReconnecting, Is.False);
            Assert.That(coordinator.CanSubmitCommands, Is.False);

            // Once terminated, subsequent disconnects do not change state
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Terminated));
        }

        [Test]
        public void Coordinator_OnKeyframeInstalled_TransitionsToCountdown()
        {
            var coordinator = new ClientReconnectCoordinator();
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);

            var response = new ReconnectResponse(
                ReconnectResult.Accepted,
                _playerId,
                _matchId,
                serverTick: 200UL,
                activeKeyframeSeq: 2);
            coordinator.OnReconnectResponse(in response);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Resyncing));

            coordinator.OnKeyframeInstalled();
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown));
            Assert.That(coordinator.CountdownRemainingSeconds, Is.EqualTo(ClientReconnectCoordinator.DefaultCountdownSeconds));
            Assert.That(coordinator.IsReconnecting, Is.True);
            Assert.That(coordinator.CanSubmitCommands, Is.False);
        }

        [Test]
        public void Coordinator_AdvanceCountdown_DecrementsTimer_AndResumesAtZero()
        {
            var coordinator = new ClientReconnectCoordinator();
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);

            var response = new ReconnectResponse(
                ReconnectResult.Accepted,
                _playerId,
                _matchId,
                serverTick: 200UL,
                activeKeyframeSeq: 2);
            coordinator.OnReconnectResponse(in response);
            coordinator.OnKeyframeInstalled();

            var readyFired = false;
            coordinator.ReadyToResume += () => readyFired = true;

            // Advance 2 seconds -> 3 seconds remain
            coordinator.AdvanceCountdown(2.0f, out var ready1);
            Assert.That(ready1, Is.False);
            Assert.That(coordinator.CountdownRemainingSeconds, Is.EqualTo(3.0f).Within(0.001f));
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown));
            Assert.That(readyFired, Is.False);

            // Advance 3 seconds -> countdown finishes, returns to Connected
            coordinator.AdvanceCountdown(3.0f, out var ready2);
            Assert.That(ready2, Is.True);
            Assert.That(coordinator.CountdownRemainingSeconds, Is.EqualTo(0f));
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Connected));
            Assert.That(coordinator.CanSubmitCommands, Is.True);
            Assert.That(readyFired, Is.True);
        }

        [Test]
        public void Coordinator_SuccessfulReconnectCycle_FullFlow()
        {
            var stateHistory = new List<ClientReconnectState>();
            var receiver = new ClientReplicationReceiver(64);
            var coordinator = new ClientReconnectCoordinator(receiver);
            coordinator.StateChanged += s => stateHistory.Add(s);

            // 1. Initial gameplay session
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);
            coordinator.LastAppliedTick = 77UL;
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Connected));

            // 2. Involuntary disconnect
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Reconnecting));

            // 3. Client builds C0 reconnect request
            Span<byte> reqBuffer = stackalloc byte[64];
            Assert.That(coordinator.TryBuildReconnectRequest(reqBuffer, out var written), Is.True);
            Assert.That(written, Is.EqualTo(64));

            // 4. Server accepts reconnect request
            var response = new ReconnectResponse(
                ReconnectResult.Accepted,
                _playerId,
                _matchId,
                serverTick: 120UL,
                activeKeyframeSeq: 3);
            coordinator.OnReconnectResponse(in response);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Resyncing));

            // 5. Keyframe is installed by replication
            coordinator.OnKeyframeInstalled();
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown));
            Assert.That(coordinator.CountdownRemainingSeconds, Is.EqualTo(5.0f));

            // 6. Countdown completes
            coordinator.AdvanceCountdown(5.0f, out var ready);
            Assert.That(ready, Is.True);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Connected));

            // Verify clean state transitions
            Assert.That(stateHistory, Is.EqualTo(new[]
            {
                ClientReconnectState.Connected,
                ClientReconnectState.Reconnecting,
                ClientReconnectState.Resyncing,
                ClientReconnectState.Countdown,
                ClientReconnectState.Connected
            }));
        }

        [Test]
        public void Coordinator_Resyncing_PrimesReplicationReceiver_RejectsStaleDeltas()
        {
            var receiver = new ClientReplicationReceiver(64);
            var coordinator = new ClientReconnectCoordinator(receiver);
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);

            // Install initial baseline keyframe at generation 1, tick 10
            var units = new DeltaAddRecord[]
            {
                new DeltaAddRecord(new CoreEntityId(1), _playerId, new WorldPointMm(1000, 2000), 100, false, default, default, false)
            };
            var outcome1 = receiver.ReceiveKeyframe(100, 1, 10UL, units);
            Assert.That(outcome1, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(receiver.IsWorldUsable, Is.True);
            Assert.That(receiver.CurrentKeyframeSeq, Is.EqualTo((ushort)1));

            // Disconnect and receive ReconnectResponse for generation 2
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            var response = new ReconnectResponse(
                ReconnectResult.Accepted,
                _playerId,
                _matchId,
                serverTick: 50UL,
                activeKeyframeSeq: 2);
            coordinator.OnReconnectResponse(in response);

            // Verify receiver was primed: baseline reset, world unusable until new keyframe
            Assert.That(receiver.IsWorldUsable, Is.False);
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Unbased));

            // An incoming delta from the old generation or pre-baseline tick is rejected
            var staleHeader = new DeltaSnapshotHeader(
                0x03,
                DeltaSnapshotProtocol.Version,
                tick: 15UL,
                baseTick: 10UL,
                0,
                1,
                0,
                0,
                0,
                0,
                0);

            var deltaOutcome = receiver.ReceiveDelta(
                150,
                staleHeader,
                keyframeRef: 1,
                ReadOnlySpan<DeltaAddRecord>.Empty,
                ReadOnlySpan<DeltaUpdateRecord>.Empty,
                ReadOnlySpan<DeltaRemoveRecord>.Empty);

            // Apply-Guard rejects because baseline is missing
            Assert.That(deltaOutcome, Is.EqualTo(ClientReplicationOutcome.RebaseRequested));

            // When new keyframe for generation 2 arrives, it installs cleanly
            var outcome2 = receiver.ReceiveKeyframe(200, 2, 50UL, units);
            Assert.That(outcome2, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(receiver.IsWorldUsable, Is.True);
            Assert.That(receiver.CurrentKeyframeSeq, Is.EqualTo((ushort)2));

            // Signal keyframe installed to coordinator
            coordinator.OnKeyframeInstalled();
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown));
        }

        [Test]
        public void Coordinator_CommandPreservation_PreservesQueueAcrossDisconnectAndPause()
        {
            var registry = new UnitRegistry();
            var queue = new PrototypeCommandQueue(registry, _playerId, 500);

            // Spawn local unit for local command queue validation
            var unit = CreateUnit(10, _playerId, new Vector3(5f, 0f, 5f));
            registry.Refresh(_playerId);

            // Queue a move command
            queue.QueueMove(new WorldPointMm(10000, 10000), new[] { unit.Entity }, requestedTick: 50);
            Assert.That(queue.PendingCount, Is.EqualTo(1));

            // Coordinator goes through disconnect, reconnect, and countdown
            var coordinator = new ClientReconnectCoordinator();
            coordinator.CacheSession(_sessionId, _matchId, _playerId, in _secret);

            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            Assert.That(queue.PendingCount, Is.EqualTo(1), "Commands must not be dropped during disconnect");

            var response = new ReconnectResponse(
                ReconnectResult.Accepted,
                _playerId,
                _matchId,
                serverTick: 60UL,
                activeKeyframeSeq: 2);
            coordinator.OnReconnectResponse(in response);
            Assert.That(queue.PendingCount, Is.EqualTo(1), "Commands must not be dropped during resync");

            coordinator.OnKeyframeInstalled();
            coordinator.AdvanceCountdown(2.5f, out _);
            Assert.That(queue.PendingCount, Is.EqualTo(1), "Commands must not be dropped during 5s countdown");

            coordinator.AdvanceCountdown(2.5f, out var ready);
            Assert.That(ready, Is.True);
            Assert.That(queue.PendingCount, Is.EqualTo(1), "Commands are preserved and ready to be processed post-pause (OD-19)");
        }

        private PrototypeUnit CreateUnit(
            ulong entityValue,
            PlayerId owner,
            Vector3 position)
        {
            var unitObject = new GameObject($"Unit {entityValue}");
            _createdObjects.Add(unitObject);
            unitObject.transform.position = position;
            var unit = unitObject.AddComponent<PrototypeUnit>();
            unit.Initialize(owner);
            unit.AssignAuthoritativeEntity(new CoreEntityId(entityValue));
            return unit;
        }
    }
}
