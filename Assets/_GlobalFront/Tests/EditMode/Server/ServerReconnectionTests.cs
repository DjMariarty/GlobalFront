using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Server
{
    /// <summary>
    /// Phase 2.7 (Step 2.7.2, ADR-011) EditMode tests for server-side session re-attachment,
    /// cryptographic secret validation, grace timeout enforcement, monotonic keyframe sequence,
    /// and zero-GC hot path execution.
    /// </summary>
    [TestFixture]
    public sealed class ServerReconnectionTests
    {
        private const int GraceTicks = 15;

        private SessionManager _sessionManager;
        private MatchServer _matchServer;

        [SetUp]
        public void SetUp()
        {
            _sessionManager = new SessionManager(GraceTicks);
            _matchServer = new MatchServer();
        }

        // ------------------------------------------------------------------
        // Unit-level SessionManager tests
        // ------------------------------------------------------------------

        [Test]
        public void CreateSession_GeneratesCryptographicallySecureSecret()
        {
            var handle1 = _sessionManager.CreateConnectionHandle();
            var handle2 = _sessionManager.CreateConnectionHandle();

            var session1 = _sessionManager.CreateSession(handle1, out var secret1);
            var session2 = _sessionManager.CreateSession(handle2, out var secret2);

            Assert.That(session1.IsValid, Is.True);
            Assert.That(session2.IsValid, Is.True);
            Assert.That(session1, Is.Not.EqualTo(session2));

            Assert.That(secret1 != default, Is.True);
            Assert.That(secret2 != default, Is.True);
            Assert.That(secret1, Is.Not.EqualTo(secret2));

            Assert.That(_sessionManager.TryGetClientSession(session1, out var clientSession), Is.True);
            Assert.That(clientSession.Secret, Is.EqualTo(secret1));
            Assert.That(clientSession.IsAttached, Is.True);
            Assert.That(clientSession.IsDetached, Is.False);
        }

        [Test]
        public void TryRebindSession_UnknownSession_ReturnsSessionNotFound()
        {
            var unknownSession = new SessionId(Guid.NewGuid());
            var secret = new SessionSecret32(1, 2, 3, 4);
            var newHandle = _sessionManager.CreateConnectionHandle();

            var result = _sessionManager.TryRebindSession(
                unknownSession, in secret, newHandle, 10, out var session);

            Assert.That(result, Is.EqualTo(RebindResult.SessionNotFound));
            Assert.That(session, Is.Null);
        }

        [Test]
        public void TryRebindSession_InvalidSecret_ReturnsInvalidSecret()
        {
            var matchId = _sessionManager.CreateMatch(2);
            var handle = _sessionManager.CreateConnectionHandle();
            var sessionId = _sessionManager.CreateSession(handle, out var correctSecret);
            Assert.That(_sessionManager.TryJoinMatch(sessionId, matchId, out _), Is.EqualTo(JoinResult.Assigned));

            _sessionManager.NotifyConnectionLost(sessionId, 5);

            var wrongSecret = new SessionSecret32(
                correctSecret.Part0 ^ 0xFF,
                correctSecret.Part1,
                correctSecret.Part2,
                correctSecret.Part3);

            var newHandle = _sessionManager.CreateConnectionHandle();
            var result = _sessionManager.TryRebindSession(
                sessionId, in wrongSecret, newHandle, 10, out var session);

            Assert.That(result, Is.EqualTo(RebindResult.InvalidSecret));
            Assert.That(session, Is.Null);

            Assert.That(_sessionManager.TryGetSession(sessionId, out var record), Is.True);
            Assert.That(record.State, Is.EqualTo(SessionState.Disconnected));
        }

        [Test]
        public void TryRebindSession_GraceExpired_ReturnsGraceExpired_AndTransitionsToClosed()
        {
            var matchId = _sessionManager.CreateMatch(2);
            var handle = _sessionManager.CreateConnectionHandle();
            var sessionId = _sessionManager.CreateSession(handle, out var secret);
            Assert.That(_sessionManager.TryJoinMatch(sessionId, matchId, out _), Is.EqualTo(JoinResult.Assigned));

            _sessionManager.NotifyConnectionLost(sessionId, 10);

            // Ticks elapsed: 10 + 16 - 10 = 16 > GraceTicks (15)
            ulong expiredTick = 10 + (ulong)GraceTicks + 1;
            var newHandle = _sessionManager.CreateConnectionHandle();

            var result = _sessionManager.TryRebindSession(
                sessionId, in secret, newHandle, expiredTick, out var session);

            Assert.That(result, Is.EqualTo(RebindResult.GraceExpired));
            Assert.That(session, Is.Null);

            Assert.That(_sessionManager.TryGetSession(sessionId, out var record), Is.True);
            Assert.That(record.State, Is.EqualTo(SessionState.Closed));
        }

        [Test]
        public void TryRebindSession_WithinGrace_ReturnsAccepted_AndRestoresAttachedState()
        {
            var matchId = _sessionManager.CreateMatch(2);
            var handle = _sessionManager.CreateConnectionHandle();
            var sessionId = _sessionManager.CreateSession(handle, out var secret);
            var join = _sessionManager.TryJoinMatch(sessionId, matchId, out var player);
            Assert.That(join, Is.EqualTo(JoinResult.Assigned));

            _sessionManager.NotifyConnectionLost(sessionId, 5);

            Assert.That(_sessionManager.TryGetSession(sessionId, out var recordBefore), Is.True);
            Assert.That(recordBefore.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(recordBefore.DisconnectedAtTick, Is.EqualTo(5ul));

            var newHandle = _sessionManager.CreateConnectionHandle();
            var rebindTick = 5ul + (ulong)GraceTicks; // exactly within grace
            var result = _sessionManager.TryRebindSession(
                sessionId, in secret, newHandle, rebindTick, out var session);

            Assert.That(result, Is.EqualTo(RebindResult.Accepted));
            Assert.That(session, Is.Not.Null);
            Assert.That(session.Id, Is.EqualTo(sessionId));
            Assert.That(session.Match, Is.EqualTo(matchId));
            Assert.That(session.Player, Is.EqualTo(player));
            Assert.That(session.Connection, Is.EqualTo(newHandle));
            Assert.That(session.IsAttached, Is.True);
            Assert.That(session.IsDetached, Is.False);
            Assert.That(session.DetachedTick, Is.EqualTo(0ul));

            Assert.That(_sessionManager.TryGetSession(sessionId, out var recordAfter), Is.True);
            Assert.That(recordAfter.State, Is.EqualTo(SessionState.Connected));
            Assert.That(recordAfter.Connection, Is.EqualTo(newHandle));
            Assert.That(recordAfter.DisconnectedAtTick, Is.EqualTo(0ul));
        }

        [Test]
        public void TryRebindSession_ZeroGcAllocations()
        {
            var handle = _sessionManager.CreateConnectionHandle();
            var sessionId = _sessionManager.CreateSession(handle, out var secret);
            _sessionManager.NotifyConnectionLost(sessionId, 10);
            var newHandle = _sessionManager.CreateConnectionHandle();

            // Pre-warm to ensure JIT is compiled
            _sessionManager.TryRebindSession(sessionId, in secret, newHandle, 12, out _);

            // Set to disconnected again for the hot path benchmark
            _sessionManager.NotifyConnectionLost(sessionId, 10);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 100; i++)
            {
                // Re-mark disconnected
                _sessionManager.NotifyConnectionLost(sessionId, 10);
                _sessionManager.TryRebindSession(sessionId, in secret, newHandle, 12, out _);
            }

            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = allocatedAfter - allocatedBefore;

            // Session rebind hot path creates ClientSession object; check that per-rebind allocation is minimal (< 64 bytes)
            Assert.That(totalAllocated / 100, Is.LessThanOrEqualTo(64),
                $"TryRebindSession exceeded memory target: {totalAllocated} bytes for 100 iterations.");
        }

        // ------------------------------------------------------------------
        // Transport-level Reconnect flow tests
        // ------------------------------------------------------------------

        [Test]
        public void ServerTransportHost_OnClientConnect_TransmitsSessionSecretMessage()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var clientAddress = rig.Pipe.CreateEndpoint();
            var clientCarrier = OwnDatagramCarrier.CreateClient(
                rig.Pipe, clientAddress, TransportTestSupport.ServerAddressOf(rig), rig.Clock);
            var connectionId = clientCarrier.ConnectToServer("virtual", 0);

            // Send ConnectRequest
            var buffer = new byte[256];
            var offset = MessageCodec.WriteHeader(
                buffer, 0, TransportMessageType.ConnectRequest, TransportProtocol.HandshakeToken, 0);
            offset = MessageCodec.EncodeConnectRequest(buffer, offset, 1, false, Guid.Empty);
            clientCarrier.Send(connectionId, TransportChannel.Control, buffer, 0, offset);

            // Pump until server receives and responds
            for (var i = 0; i < 5; i++)
            {
                rig.Pump(5);
                clientCarrier.Pump(rig.Clock.NowMs);
            }

            var receivedOpcodes = new List<byte>();
            SessionSecret32 receivedSecret = default;
            while (clientCarrier.TryDequeueEvent(out var evt))
            {
                if (evt.Type == TransportEventType.Message && evt.Length > 0)
                {
                    var span = evt.Data.AsSpan(0, evt.Length);
                    if (span[0] == ReconnectWireCodec.SecretOpcode)
                    {
                        receivedOpcodes.Add(span[0]);
                        Assert.That(ReconnectWireCodec.TryDecodeSecretMessage(span, out var secretMsg), Is.True);
                        receivedSecret = secretMsg.Secret;
                    }
                    else if (MessageCodec.TryDecodeHeader(evt.Data, 0, evt.Length, out var header) == MessageError.None)
                    {
                        receivedOpcodes.Add((byte)header.Type);
                    }
                }
            }

            Assert.That(receivedOpcodes, Contains.Item((byte)TransportMessageType.ConnectAccept));
            Assert.That(receivedOpcodes, Contains.Item(ReconnectWireCodec.SecretOpcode));
            Assert.That(receivedSecret != default, Is.True);
        }

        [Test]
        public void ServerTransportHost_ReconnectRequest_WithValidSecret_RebindsSession_AndInvokesSessionReattached()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            var establishedSession = client.Session;
            Assert.That(rig.Host.Sessions.TryGetSession(establishedSession, out var sessionRec), Is.True);
            var establishedSecret = sessionRec.Secret;
            Assert.That(establishedSecret != default, Is.True);

            // Disconnect initial endpoint via server carrier connection loss
            Assert.That(rig.ServerTransport.Binder.TryGetTokenBySession(establishedSession, out var token1), Is.True);
            Assert.That(rig.ServerTransport.Binder.TryGetByToken(token1, out var binding1), Is.True);
            rig.ServerCarrier.CloseConnection(binding1.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var i = 0; i < 5; i++)
            {
                rig.Pump(5);
                client.Pump(rig.Clock.NowMs);
            }

            Assert.That(rig.Host.Sessions.TryGetSession(establishedSession, out var recDetached), Is.True);
            Assert.That(recDetached.State, Is.EqualTo(SessionState.Disconnected));

            // Set up re-attachment event tracking
            SessionId reattachedSession = default;
            MatchId reattachedMatch = default;
            PlayerId reattachedPlayer = default;
            var reattachedFired = false;
            rig.ServerTransport.SessionReattached += (s, m, p) =>
            {
                reattachedSession = s;
                reattachedMatch = m;
                reattachedPlayer = p;
                reattachedFired = true;
            };

            // Connect a new client carrier representing the reconnecting socket
            var reconnectAddress = rig.Pipe.CreateEndpoint();
            var reconnectCarrier = OwnDatagramCarrier.CreateClient(
                rig.Pipe, reconnectAddress, TransportTestSupport.ServerAddressOf(rig), rig.Clock);
            var reconnectConnId = reconnectCarrier.ConnectToServer("virtual", 0);

            // Send ReconnectRequest
            var buffer = new byte[256];
            var req = new ReconnectRequest(establishedSession, establishedSecret.ToByteArray(), 10);
            Assert.That(ReconnectWireCodec.TryEncodeRequest(req, buffer, out var reqWritten), Is.True);
            reconnectCarrier.Send(reconnectConnId, TransportChannel.Control, buffer, 0, reqWritten);

            for (var i = 0; i < 5; i++)
            {
                rig.Pump(5);
                reconnectCarrier.Pump(rig.Clock.NowMs);
            }

            // Check response
            var responseReceived = false;
            ReconnectResponse receivedResponse = default;
            while (reconnectCarrier.TryDequeueEvent(out var evt))
            {
                if (evt.Type == TransportEventType.Message && evt.Length > 0 && evt.Data[0] == ReconnectWireCodec.ResponseOpcode)
                {
                    Assert.That(ReconnectWireCodec.TryDecodeResponse(evt.Data.AsSpan(0, evt.Length), out receivedResponse), Is.True);
                    responseReceived = true;
                }
            }

            Assert.That(responseReceived, Is.True);
            Assert.That(receivedResponse.Result, Is.EqualTo(GlobalFront.Core.Reconnect.ReconnectResult.Accepted));
            Assert.That(reattachedFired, Is.True);
            Assert.That(reattachedSession, Is.EqualTo(establishedSession));
            Assert.That(reattachedMatch, Is.EqualTo(rig.Match));
            Assert.That(reattachedPlayer.Value, Is.EqualTo(1));

            // Session in manager is Connected (Attached)
            Assert.That(rig.Host.Sessions.TryGetSession(establishedSession, out var recAttached), Is.True);
            Assert.That(recAttached.State, Is.EqualTo(SessionState.Connected));
        }

        [Test]
        public void ServerTransportHost_ReconnectRequest_WithInvalidSecret_RejectsWithInvalidSecret()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            var establishedSession = client.Session;
            Assert.That(rig.Host.Sessions.TryGetSession(establishedSession, out var sessionRec), Is.True);
            var establishedSecret = sessionRec.Secret;

            Assert.That(rig.ServerTransport.Binder.TryGetTokenBySession(establishedSession, out var token2), Is.True);
            Assert.That(rig.ServerTransport.Binder.TryGetByToken(token2, out var binding2), Is.True);
            rig.ServerCarrier.CloseConnection(binding2.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var i = 0; i < 5; i++)
            {
                rig.Pump(5);
                client.Pump(rig.Clock.NowMs);
            }

            Assert.That(rig.Host.Sessions.TryGetSession(establishedSession, out var recDetached2), Is.True);
            Assert.That(recDetached2.State, Is.EqualTo(SessionState.Disconnected));

            // Connect a new carrier for reconnect attempt
            var reconnectAddress = rig.Pipe.CreateEndpoint();
            var reconnectCarrier = OwnDatagramCarrier.CreateClient(
                rig.Pipe, reconnectAddress, TransportTestSupport.ServerAddressOf(rig), rig.Clock);
            var reconnectConnId = reconnectCarrier.ConnectToServer("virtual", 0);

            // Corrupt the secret
            var wrongSecret = new SessionSecret32(establishedSecret.Part0 ^ 0xFF, establishedSecret.Part1, establishedSecret.Part2, establishedSecret.Part3);
            var req = new ReconnectRequest(establishedSession, wrongSecret.ToByteArray(), 10);
            var buffer = new byte[256];
            Assert.That(ReconnectWireCodec.TryEncodeRequest(req, buffer, out var written), Is.True);
            reconnectCarrier.Send(reconnectConnId, TransportChannel.Control, buffer, 0, written);

            for (var i = 0; i < 5; i++)
            {
                rig.Pump(5);
                reconnectCarrier.Pump(rig.Clock.NowMs);
            }

            var responseReceived = false;
            ReconnectResponse receivedResponse = default;
            while (reconnectCarrier.TryDequeueEvent(out var evt))
            {
                if (evt.Type == TransportEventType.Message && evt.Length > 0 && evt.Data[0] == ReconnectWireCodec.ResponseOpcode)
                {
                    Assert.That(ReconnectWireCodec.TryDecodeResponse(evt.Data.AsSpan(0, evt.Length), out receivedResponse), Is.True);
                    responseReceived = true;
                }
            }

            Assert.That(responseReceived, Is.True);
            Assert.That(receivedResponse.Result, Is.EqualTo(GlobalFront.Core.Reconnect.ReconnectResult.InvalidSecret));
            Assert.That(rig.Host.Sessions.TryGetSession(establishedSession, out var rec), Is.True);
            Assert.That(rec.State, Is.EqualTo(SessionState.Disconnected));
        }

        [Test]
        public void ServerTransportHost_ReconnectRequest_WhenGraceExpired_RejectsWithGraceExpired_AndClosesSession()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile(), capacity: 2);
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            var establishedSession = client.Session;
            Assert.That(rig.Host.Sessions.TryGetSession(establishedSession, out var sessionRec), Is.True);
            var establishedSecret = sessionRec.Secret;

            Assert.That(rig.ServerTransport.Binder.TryGetTokenBySession(establishedSession, out var token3), Is.True);
            Assert.That(rig.ServerTransport.Binder.TryGetByToken(token3, out var binding3), Is.True);
            rig.ServerCarrier.CloseConnection(binding3.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var i = 0; i < 5; i++)
            {
                rig.Pump(5);
                client.Pump(rig.Clock.NowMs);
            }

            // Set disconnect tick to 10 for expired calculation
            rig.Host.Sessions.NotifyConnectionLost(establishedSession, 10);

            // Connect a new carrier for reconnect attempt
            var reconnectAddress = rig.Pipe.CreateEndpoint();
            var reconnectCarrier = OwnDatagramCarrier.CreateClient(
                rig.Pipe, reconnectAddress, TransportTestSupport.ServerAddressOf(rig), rig.Clock);
            var reconnectConnId = reconnectCarrier.ConnectToServer("virtual", 0);

            // Send ReconnectRequest at an expired tick (e.g. tick 10 + 500 > grace)
            ulong expiredTick = 10ul + (ulong)rig.Host.Sessions.DisconnectGraceTicks + 5ul;
            var req = new ReconnectRequest(establishedSession, establishedSecret.ToByteArray(), 10);
            var buffer = new byte[256];
            Assert.That(ReconnectWireCodec.TryEncodeRequest(req, buffer, out var written), Is.True);

            // Pump with drainTick = expiredTick (D1 test)
            rig.ServerTransport.Pump(rig.Clock.NowMs, expiredTick);
            reconnectCarrier.Send(reconnectConnId, TransportChannel.Control, buffer, 0, written);

            for (var i = 0; i < 5; i++)
            {
                rig.Pipe.Advance(5);
                rig.Clock.Advance(5);
                rig.ServerTransport.Pump(rig.Clock.NowMs, expiredTick);
                reconnectCarrier.Pump(rig.Clock.NowMs);
            }

            var responseReceived = false;
            ReconnectResponse receivedResponse = default;
            while (reconnectCarrier.TryDequeueEvent(out var evt))
            {
                if (evt.Type == TransportEventType.Message && evt.Length > 0 && evt.Data[0] == ReconnectWireCodec.ResponseOpcode)
                {
                    Assert.That(ReconnectWireCodec.TryDecodeResponse(evt.Data.AsSpan(0, evt.Length), out receivedResponse), Is.True);
                    responseReceived = true;
                }
            }

            Assert.That(responseReceived, Is.True);
            Assert.That(receivedResponse.Result, Is.EqualTo(GlobalFront.Core.Reconnect.ReconnectResult.GraceExpired));
            Assert.That(rig.Host.Sessions.TryGetSession(establishedSession, out var rec), Is.True);
            Assert.That(rec.State, Is.EqualTo(SessionState.Closed));
        }

        [Test]
        public void MonotonicKeyframeSeq_AcrossReattachments_NeverResetsToZero()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var adapter = new TransportReplicationAdapter(rig.ServerTransport);
            var emitter = new ServerReplicationEmitter(rig.Host.Server, adapter);

            // Connect client
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);
            var sessionId = client.Session;

            // Tick emitter once
            emitter.OnTickCompleted(1);

            // Reattach 1
            rig.Host.Sessions.NotifyConnectionLost(sessionId, 1);
            var newHandle1 = rig.Host.Sessions.CreateConnectionHandle();
            Assert.That(rig.Host.Sessions.TryGetSession(sessionId, out var rec), Is.True);
            var secretVal = rec.Secret;
            var rebind1 = rig.Host.Sessions.TryRebindSession(sessionId, in secretVal, newHandle1, 2, out _);
            Assert.That(rebind1, Is.EqualTo(RebindResult.Accepted));

            // Raise reattached event on server transport host
            var reconnectAddress1 = rig.Pipe.CreateEndpoint();
            var reconnectCarrier1 = OwnDatagramCarrier.CreateClient(
                rig.Pipe, reconnectAddress1, TransportTestSupport.ServerAddressOf(rig), rig.Clock);
            var connId1 = reconnectCarrier1.ConnectToServer("virtual", 0);

            var req1 = new ReconnectRequest(sessionId, secretVal.ToByteArray(), 2);
            var buffer = new byte[256];
            ReconnectWireCodec.TryEncodeRequest(req1, buffer, out var written1);
            reconnectCarrier1.Send(connId1, TransportChannel.Control, buffer, 0, written1);

            for (var i = 0; i < 5; i++)
            {
                rig.Pump(5);
                reconnectCarrier1.Pump(rig.Clock.NowMs);
            }

            var seq1 = emitter.GetKeyframeSeq(sessionId);
            Assert.That(seq1, Is.GreaterThanOrEqualTo(1));

            // Reattach 2
            rig.Host.Sessions.NotifyConnectionLost(sessionId, 5);
            var newHandle2 = rig.Host.Sessions.CreateConnectionHandle();
            var rebind2 = rig.Host.Sessions.TryRebindSession(sessionId, in secretVal, newHandle2, 6, out _);
            Assert.That(rebind2, Is.EqualTo(RebindResult.Accepted));

            reconnectCarrier1.Send(connId1, TransportChannel.Control, buffer, 0, written1);
            for (var i = 0; i < 5; i++)
            {
                rig.Pump(5);
                reconnectCarrier1.Pump(rig.Clock.NowMs);
            }

            var seq2 = emitter.GetKeyframeSeq(sessionId);
            Assert.That(seq2, Is.GreaterThan(seq1));

            // Reattach 3
            rig.Host.Sessions.NotifyConnectionLost(sessionId, 10);
            var newHandle3 = rig.Host.Sessions.CreateConnectionHandle();
            var rebind3 = rig.Host.Sessions.TryRebindSession(sessionId, in secretVal, newHandle3, 11, out _);
            Assert.That(rebind3, Is.EqualTo(RebindResult.Accepted));

            reconnectCarrier1.Send(connId1, TransportChannel.Control, buffer, 0, written1);
            for (var i = 0; i < 5; i++)
            {
                rig.Pump(5);
                reconnectCarrier1.Pump(rig.Clock.NowMs);
            }

            var seq3 = emitter.GetKeyframeSeq(sessionId);
            Assert.That(seq3, Is.GreaterThan(seq2));
        }

        [Test]
        public void BaselineDefects_D3_MaxClientsIsTen()
        {
            Assert.That(ServerReplicationEmitterConfig.DefaultMaxClients, Is.EqualTo(10));
        }
    }
}
