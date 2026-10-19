using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Phase 2.5 connection-state lifecycle tests (review P1-4): closed
    /// connections free ALL per-peer state (records, endpoint mappings,
    /// limiters, fragment groups), pre-handshake state is bounded and aged,
    /// spoofed sources cannot grow unbounded state, and repeated
    /// connect/disconnect cycles stay bounded (no leaks).
    /// </summary>
    [TestFixture]
    public sealed class TransportLifecycleCleanupTests
    {
        [Test]
        public void OwnCarrier_ClosedConnection_FreesAllState()
        {
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var clientAddress = pipe.CreateEndpoint();
            var server = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var client = OwnDatagramCarrier.CreateClient(pipe, clientAddress, serverAddress, clock);

            var clientConnection = client.ConnectToServer("virtual", 0);
            Assert.That(client.ConnectionCount, Is.EqualTo(1));

            // First datagram creates the server-side connection.
            var probe = new byte[] { 0x55 };
            Assert.That(
                client.Send(clientConnection, TransportChannel.Command, probe, 0, probe.Length),
                Is.True);

            var serverConnection = -1;
            var elapsed = 0L;
            while (serverConnection < 0 && elapsed < 2000)
            {
                pipe.Advance(5);
                clock.Advance(5);
                server.Pump(clock.NowMs);
                client.Pump(clock.NowMs);
                while (server.TryDequeueEvent(out var transportEvent))
                {
                    if (transportEvent.Type == TransportEventType.Connected)
                    {
                        serverConnection = transportEvent.ConnectionId;
                    }
                }

                elapsed += 5;
            }

            Assert.That(serverConnection, Is.GreaterThanOrEqualTo(0));
            Assert.That(server.ConnectionCount, Is.EqualTo(1));

            // Closing frees the record, endpoint mapping, limiter, reorder
            // buffer and fragment groups on both sides.
            client.CloseConnection(clientConnection, TransportDisconnectReason.ClientRequested);
            client.Pump(clock.NowMs);
            Assert.That(client.ConnectionCount, Is.EqualTo(0),
                "client must free closed connection state");

            server.CloseConnection(serverConnection, TransportDisconnectReason.ClientRequested);
            server.Pump(clock.NowMs);
            Assert.That(server.ConnectionCount, Is.EqualTo(0),
                "server must free closed connection state");
        }

        [Test]
        public void OwnCarrier_SpoofedSourceFlood_StateStaysBounded_AndCleansUp()
        {
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var server = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);

            // 300 spoofed sources each send one valid datagram.
            var message = new byte[] { 0x01 };
            var buffer = new byte[TransportProtocol.MaxDatagramBytes];
            var size = EnvelopeCodec.Encode(
                buffer,
                TransportChannel.Command,
                TransportProtocol.HandshakeToken,
                sequence: 1,
                ackNumber: 0,
                ackBitmap: 0,
                messageId: 0,
                fragmentIndex: 0,
                fragmentCount: 0,
                payload: message,
                payloadOffset: 0,
                payloadLength: message.Length);

            for (var source = 0; source < 300; source++)
            {
                pipe.SendDatagram(5000 + source, serverAddress, buffer, size);
            }

            pipe.Advance(10);
            clock.Advance(10);
            server.Pump(clock.NowMs);

            Assert.That(server.ConnectionCount, Is.LessThanOrEqualTo(TransportProtocol.MaxConnections),
                "spoofed sources must not create unbounded connection state");
            Assert.That(server.Metrics.DroppedConnectionLimit, Is.GreaterThan(0),
                "traffic beyond the bounded state caps must be dropped with a counter");

            // Full silence past the idle timeout: unauthenticated
            // connections and pre-handshake tracking both age out.
            for (var step = 0; step < 100; step++)
            {
                pipe.Advance(100);
                clock.Advance(100);
                server.Pump(clock.NowMs);
            }

            Assert.That(server.ConnectionCount, Is.EqualTo(0),
                "silent unauthenticated connections must be cleaned up");
            Assert.That(server.PreHandshakeTrackedCount, Is.EqualTo(0),
                "pre-handshake tracking must have bounded lifetime");
        }

        [Test]
        public void OwnCarrier_ConnectDisconnectSoak_NoStateAccumulation()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile(), capacity: 8);

            for (var cycle = 0; cycle < 5; cycle++)
            {
                var client = TransportTestSupport.CreateClient(rig);
                Assert.That(
                    TransportTestSupport.PumpUntilEstablished(rig, client), Is.True,
                    $"handshake must complete on cycle {cycle}");
                Assert.That(rig.ServerCarrier.ConnectionCount, Is.EqualTo(1),
                    $"exactly one live connection on cycle {cycle}");
                Assert.That(rig.ServerTransport.AttachedCount, Is.EqualTo(1));

                client.GracefulDisconnect();
                rig.Pump(5);
                client.Pump(rig.Clock.NowMs);
                Assert.That(rig.ServerTransport.AttachedCount, Is.EqualTo(0),
                    $"graceful disconnect must release the binding on cycle {cycle}");

                // Silence past the idle timeout: the server-side carrier
                // connection of the departed client must be freed.
                for (var step = 0; step < 80; step++)
                {
                    rig.Pump(100);
                }

                Assert.That(rig.ServerCarrier.ConnectionCount, Is.EqualTo(0),
                    $"connection state must be freed after cycle {cycle}");
            }

            // Memory boundedness after the whole soak: nothing accumulated.
            Assert.That(rig.ServerCarrier.ConnectionCount, Is.EqualTo(0));
            Assert.That(rig.ServerCarrier.PreHandshakeTrackedCount, Is.EqualTo(0));
            Assert.That(rig.ServerTransport.AttachedCount, Is.EqualTo(0));
        }

        [Test]
        public void ServerTransportHost_ConnectionLost_FreesRateLimiter()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(
                TransportTestSupport.PumpUntilEstablished(rig, client), Is.True,
                "handshake must complete");

            Assert.That(rig.ServerTransport.RateLimiterCount, Is.EqualTo(1),
                "the handshake must have created one per-connection limiter");

            // Server-side transport loss for the client's socket.
            rig.ServerCarrier.CloseConnection(1, TransportDisconnectReason.TransportLost);
            rig.Pump(5);

            Assert.That(rig.ServerTransport.RateLimiterCount, Is.EqualTo(0),
                "a lost connection must not leave its rate limiter behind");
        }

        [Test]
        public void ServerTransportHost_ConnectionLost_PurgesPendingAcks()
        {
            var carrier = new AckFailureCarrier();
            var sessions = new SessionManager();
            var server = new MatchServer();
            var match = sessions.CreateMatch(2);
            var host = new ServerTransportHost(carrier, sessions, server)
            {
                AutoJoinMatch = match
            };

            const int connectionId = 7;
            var handshake = new byte[64];
            var handshakeLength = MessageCodec.WriteHeader(
                handshake, 0, TransportMessageType.ConnectRequest, TransportProtocol.HandshakeToken, 0);
            handshakeLength = MessageCodec.EncodeConnectRequest(
                handshake,
                handshakeLength,
                GlobalFront.Server.Snapshot.SnapshotProtocol.Version,
                false,
                Guid.Empty);
            host.HandleMessage(new TransportEvent
            {
                Type = TransportEventType.Message,
                ConnectionId = connectionId,
                Data = handshake,
                Length = handshakeLength
            });

            Assert.That(
                host.Binder.TryGetByConnection(connectionId, out var binding), Is.True,
                "handshake must bind the session before the command is sent");

            // Forged PlayerId => the gate still answers with a CommandAck,
            // which is exactly the send that fails on the dying socket.
            var command = new byte[128];
            var commandLength = CommandWireCodec.EncodeMove(
                command,
                new CommandHeader(
                    new PlayerId(2),
                    sequence: 1,
                    requestedTick: 1,
                    GameCommandType.Move),
                new[] { new EntityId(1) },
                new WorldPointMm(1000, 0),
                new FormationSpec(1, 2000, CardinalFacing.North));

            var message = new byte[
                MessageCodec.GetHeaderSize(TransportMessageType.Command) + commandLength];
            var messageLength = MessageCodec.WriteHeader(
                message, 0, TransportMessageType.Command, binding.Token, 0);
            Array.Copy(command, 0, message, messageLength, commandLength);
            messageLength += commandLength;

            carrier.AllowSends = false;
            host.HandleMessage(new TransportEvent
            {
                Type = TransportEventType.Message,
                ConnectionId = connectionId,
                Data = message,
                Length = messageLength
            });

            Assert.That(host.PendingAckCount, Is.EqualTo(1),
                "a failed ack send must be queued for the retry pass");

            carrier.Enqueue(new TransportEvent
            {
                Type = TransportEventType.Disconnected,
                ConnectionId = connectionId,
                Reason = TransportDisconnectReason.TransportLost
            });
            host.Pump(0);

            Assert.That(host.PendingAckCount, Is.EqualTo(0),
                "acks queued for a dead socket must be purged, not retried forever");
        }

        [Test]
        public void TransportSessionBinder_SnapshotTargets_ReturnsCachedListWithoutAllocations()
        {
            var binder = new TransportSessionBinder();
            var s1 = new SessionId(Guid.NewGuid());
            var s2 = new SessionId(Guid.NewGuid());

            Assert.That(binder.SnapshotTargets.Count, Is.EqualTo(0));

            binder.Bind(s1, new ConnectionHandle(10), new MatchId(1ul), new PlayerId(1), 100);
            Assert.That(binder.SnapshotTargets.Count, Is.EqualTo(1));
            Assert.That(binder.SnapshotTargets[0], Is.EqualTo(s1));

            var firstReference = binder.SnapshotTargets;

            binder.Bind(s2, new ConnectionHandle(20), new MatchId(1ul), new PlayerId(2), 200);
            Assert.That(binder.SnapshotTargets.Count, Is.EqualTo(2));
            Assert.That(ReferenceEquals(firstReference, binder.SnapshotTargets), Is.True, "Must return the same cached instance (Zero-GC)");

            binder.Release(s1);
            Assert.That(binder.SnapshotTargets.Count, Is.EqualTo(1));
            Assert.That(binder.SnapshotTargets[0], Is.EqualTo(s2));

            binder.ReleaseAll();
            Assert.That(binder.SnapshotTargets.Count, Is.EqualTo(0));
        }

        /// <summary>
        /// Minimal carrier double for failure-path lifecycle tests: lets a
        /// test force every send to fail and inject a Disconnected event.
        /// </summary>
        private sealed class AckFailureCarrier : INetworkCarrier
        {
            private readonly Queue<TransportEvent> _events = new Queue<TransportEvent>();
            private readonly TransportMetrics _metrics = new TransportMetrics();
            private readonly VirtualTransportClock _clock = new VirtualTransportClock();

            public bool AllowSends = true;

            public TransportMetrics Metrics => _metrics;

            public ITransportClock Clock => _clock;

            public int EventQueueDepth => _events.Count;

            public void StartServer()
            {
            }

            public int ConnectToServer(string host, int port) => 1;

            public bool Send(
                int connectionId,
                TransportChannel channel,
                byte[] data,
                int offset,
                int length) => AllowSends;

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
                if (_events.Count > 0)
                {
                    transportEvent = _events.Dequeue();
                    return true;
                }

                transportEvent = default;
                return false;
            }

            public void Enqueue(TransportEvent transportEvent) => _events.Enqueue(transportEvent);

            public void Dispose()
            {
            }
        }
    }
}
