using System;
using System.Collections.Generic;
using System.Threading;
using GlobalFront.Client;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Snapshot;
using GlobalFront.Server.Transport;
using LiteNetLib;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// REAL carrier validation (ADR-009): the same transport contract runs
    /// over actual UDP loopback through LiteNetLib 1.3.5 — handshake,
    /// session attribution, command round-trip with authoritative ack,
    /// snapshot delivery and graceful disconnect. The VirtualNetworkPipe
    /// suite covers the identical contract deterministically.
    /// </summary>
    [TestFixture]
    public sealed class LiteNetLibCarrierIntegrationTests
    {
        [Test]
        public void RealUdpLoopback_FullContract_Handshake_Command_Snapshot_Disconnect()
        {
            // Raw UDP loopback probe: proves the environment itself passes
            // UDP datagrams before blaming the carrier.
            var probeDetail = "ok";
            try
            {
                using (var listener = new System.Net.Sockets.UdpClient(
                           new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
                using (var sender = new System.Net.Sockets.UdpClient())
                {
                    var probePort = ((System.Net.IPEndPoint)listener.Client.LocalEndPoint).Port;
                    listener.Client.ReceiveTimeout = 2000;
                    sender.Send(new byte[] { 1, 2, 3 }, 3, "127.0.0.1", probePort);
                    var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                    var received = listener.Receive(ref remote);
                    probeDetail = received.Length == 3 ? "ok" : $"badlen={received.Length}";
                }
            }
            catch (System.Exception ex)
            {
                probeDetail = "fail: " + ex.GetType().Name + " " + ex.Message;
            }

            var clock = new SystemTransportClock();
            var host = new LocalMatchHost();
            var match = host.CreateSessionMatch(2);

            var serverCarrier = LiteNetLibCarrier.CreateServer(clock);
            serverCarrier.StartServer(0);
            var port = serverCarrier.LocalPort;
            Assert.That(port, Is.GreaterThan(0), "server must bind a loopback UDP port");

            var transportHost = new ServerTransportHost(serverCarrier, host.Sessions, host.Server)
            {
                AutoJoinMatch = match
            };
            transportHost.StartServer();

            var clientCarrier = LiteNetLibCarrier.CreateClient(clock);
            var endpoint = new ClientTransportEndpoint(clientCarrier);
            endpoint.Connect("127.0.0.1", port);

            var snapshots = new List<(ulong Tick, byte[] Payload)>();
            endpoint.SnapshotReceived += (tick, payload, length) =>
                // The endpoint hands out reusable buffers: retain a copy.
                snapshots.Add((tick, payload.AsSpan(0, length).ToArray()));

            // Handshake over real UDP.
            var deadline = clock.NowMs + 10000;
            while (endpoint.State != ClientTransportState.Established && clock.NowMs < deadline)
            {
                Thread.Sleep(5);
                transportHost.Pump(clock.NowMs);
                endpoint.Pump(clock.NowMs);
            }

            Assert.That(endpoint.State, Is.EqualTo(ClientTransportState.Established),
                $"handshake must complete over real UDP; " +
                $"clientDisconnect={clientCarrier.LastDisconnectReason} " +
                $"serverDisconnect={serverCarrier.LastDisconnectReason} " +
                $"serverWorkerError={serverCarrier.LastWorkerError} " +
                $"clientWorkerError={clientCarrier.LastWorkerError} " +
                $"serverStarted={serverCarrier.ManagerStarted} " +
                $"clientStarted={clientCarrier.ManagerStarted} " +
                $"serverConnectRequests={serverCarrier.ConnectionRequestsSeen} " +
                $"serverPort={port} " +
                $"udpProbe={probeDetail} " +
                $"serverMgr=[{serverCarrier.ManagerInfo}] clientMgr=[{clientCarrier.ManagerInfo}] " +
                $"serverNetErr={serverCarrier.LastNetworkError} clientNetErr={clientCarrier.LastNetworkError} " +
                $"serverQueue={serverCarrier.EventQueueDepth} clientQueue={clientCarrier.EventQueueDepth}");
            Assert.That(endpoint.Player.IsValid, Is.True);
            Assert.That(endpoint.Match, Is.EqualTo(match));

            // Start the single-player match and run one authoritative command.
            var config = TransportTestSupport.BuildMatchConfig(endpoint.Player);
            Assert.That(host.TryStartSessionMatch(match, config, out var entityIds), Is.True);

            var channel = new NetworkCommandChannel(endpoint);
            var acks = new List<CommandAckPayload>();
            channel.CommandResultReceived += acks.Add;

            var submit = channel.TrySubmitMove(
                new CommandHeader(endpoint.Player, 1, host.CurrentTick + 1, GameCommandType.Move),
                new[] { entityIds[0] },
                new WorldPointMm(5000, 0),
                new FormationSpec(1, 2000, CardinalFacing.North));
            Assert.That(submit, Is.EqualTo(MatchCommandRejection.None));

            deadline = clock.NowMs + 10000;
            while (acks.Count < 1 && clock.NowMs < deadline)
            {
                Thread.Sleep(5);
                transportHost.Pump(clock.NowMs);
                endpoint.Pump(clock.NowMs);
            }

            Assert.That(acks.Count, Is.EqualTo(1), "authoritative ack must arrive over real UDP");
            Assert.That(acks[0].IsAccepted, Is.True);

            host.TickOnce();
            Assert.That(host.Server.TryGetUnit(entityIds[0], out var unit), Is.True);
            Assert.That(unit.Position.X, Is.EqualTo(350),
                "RequestedTick semantics must hold through the real carrier");

            // Snapshot delivery (opaque payload) over the unreliable channel.
            var payload = new byte[] { 10, 20, 30, 40 };
            Assert.That(
                transportHost.SendSnapshot(endpoint.Session, 42, payload, payload.Length),
                Is.True);

            deadline = clock.NowMs + 10000;
            while (snapshots.Count < 1 && clock.NowMs < deadline)
            {
                Thread.Sleep(5);
                transportHost.Pump(clock.NowMs);
                endpoint.Pump(clock.NowMs);
            }

            Assert.That(snapshots.Count, Is.EqualTo(1), "snapshot must arrive over real UDP");
            Assert.That(snapshots[0].Tick, Is.EqualTo(42ul));
            Assert.That(snapshots[0].Payload, Is.EqualTo(payload));

            // Graceful disconnect closes the session.
            endpoint.GracefulDisconnect();
            deadline = clock.NowMs + 10000;
            var closed = false;
            while (!closed && clock.NowMs < deadline)
            {
                Thread.Sleep(5);
                transportHost.Pump(clock.NowMs);
                endpoint.Pump(clock.NowMs);
                closed = host.Sessions.TryGetSession(endpoint.Session, out var record) &&
                    record.State == SessionState.Closed;
            }

            Assert.That(closed, Is.True, "graceful disconnect must close the session");

            // Teardown must free every per-peer record on both carriers.
            deadline = clock.NowMs + 10000;
            while ((serverCarrier.PeerMappingCount > 0 || clientCarrier.PeerMappingCount > 0) &&
                clock.NowMs < deadline)
            {
                Thread.Sleep(5);
                transportHost.Pump(clock.NowMs);
                endpoint.Pump(clock.NowMs);
            }

            Assert.That(serverCarrier.PeerMappingCount, Is.EqualTo(0),
                "server must free peer state after disconnect");
            Assert.That(clientCarrier.PeerMappingCount, Is.EqualTo(0),
                "client must free peer state after disconnect");

            clientCarrier.Dispose();
            serverCarrier.Dispose();
        }

        [Test]
        public void RealUdpLoopback_OversizedSnapshot_FragmentsAndReassembles()
        {
            // A ~117 KB snapshot (3000 entities) is far above any UDP MTU:
            // the LiteNetLib carrier must fragment it at the application
            // layer (Sequenced alone throws TooBigPacketException) and
            // reassemble it losslessly over real UDP.
            var clock = new SystemTransportClock();
            var host = new LocalMatchHost();
            var match = host.CreateSessionMatch(2);

            var serverCarrier = LiteNetLibCarrier.CreateServer(clock);
            serverCarrier.StartServer(0);
            var port = serverCarrier.LocalPort;
            Assert.That(port, Is.GreaterThan(0));

            var transportHost = new ServerTransportHost(serverCarrier, host.Sessions, host.Server)
            {
                AutoJoinMatch = match
            };
            transportHost.StartServer();

            var clientCarrier = LiteNetLibCarrier.CreateClient(clock);
            var endpoint = new ClientTransportEndpoint(clientCarrier);
            endpoint.Connect("127.0.0.1", port);

            var snapshots = new List<(ulong Tick, byte[] Payload)>();
            endpoint.SnapshotReceived += (tick, payload, length) =>
                snapshots.Add((tick, payload.AsSpan(0, length).ToArray()));

            var deadline = clock.NowMs + 10000;
            while (endpoint.State != ClientTransportState.Established && clock.NowMs < deadline)
            {
                Thread.Sleep(5);
                transportHost.Pump(clock.NowMs);
                endpoint.Pump(clock.NowMs);
            }

            Assert.That(endpoint.State, Is.EqualTo(ClientTransportState.Established),
                $"handshake must complete; clientDisconnect={clientCarrier.LastDisconnectReason}");

            var payload = BuildSnapshotPayload(3000);
            Assert.That(payload.Length, Is.GreaterThan(64 * 1024),
                "the test payload must be genuinely oversized");
            Assert.That(
                transportHost.SendSnapshot(endpoint.Session, 4242, payload, payload.Length),
                Is.True);

            deadline = clock.NowMs + 15000;
            while (snapshots.Count < 1 && clock.NowMs < deadline)
            {
                Thread.Sleep(5);
                transportHost.Pump(clock.NowMs);
                endpoint.Pump(clock.NowMs);
            }

            Assert.That(snapshots.Count, Is.EqualTo(1),
                "the oversized snapshot must be delivered exactly once over real UDP");
            Assert.That(snapshots[0].Tick, Is.EqualTo(4242ul));
            Assert.That(snapshots[0].Payload, Is.EqualTo(payload),
                "the oversized snapshot must reassemble byte-identical");

            endpoint.GracefulDisconnect();
            clientCarrier.Dispose();
            serverCarrier.Dispose();
        }

        private static byte[] BuildSnapshotPayload(int entities)
        {
            var snapshots = new ServerUnitSnapshot[entities];
            for (var index = 0; index < entities; index++)
            {
                snapshots[index] = new ServerUnitSnapshot(
                    new EntityId((ulong)index + 1),
                    new PlayerId(1),
                    new WorldPointMm(index * 100, index * 50),
                    100,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false);
            }

            return SnapshotSerializer.Serialize(
                new SnapshotPacketHeader(
                    SnapshotProtocol.Version, 1, (uint)entities),
                snapshots);
        }

        [Test]
        public void RealUdpLoopback_ConnectionFlood_AdmissionBounded_NoPersistentState()
        {
            // Carrier-level admission control regression: a connection flood
            // above MaxConnections must be capped at the carrier, rejected
            // peers must never allocate persistent carrier state, and the
            // teardown of accepted peers must leave the table empty.
            var clock = new SystemTransportClock();
            var serverCarrier = LiteNetLibCarrier.CreateServer(clock);
            serverCarrier.StartServer(0);
            var port = serverCarrier.LocalPort;
            Assert.That(port, Is.GreaterThan(0));

            var floodCount = TransportProtocol.MaxConnections + 4;
            var listeners = new FloodClientListener[floodCount];
            var clients = new NetManager[floodCount];
            for (var index = 0; index < floodCount; index++)
            {
                listeners[index] = new FloodClientListener();
                clients[index] = new NetManager(listeners[index])
                {
                    IPv6Enabled = false
                };
                clients[index].Start();
                clients[index].Connect("127.0.0.1", port, "GlobalFront-Phase-2.5");
            }

            // Poll every client until it reaches a terminal state
            // (connected or rejected/failed).
            var deadline = System.DateTime.UtcNow.AddSeconds(40);
            while (System.DateTime.UtcNow < deadline)
            {
                var settled = 0;
                for (var index = 0; index < floodCount; index++)
                {
                    clients[index].PollEvents();
                    if (listeners[index].Settled)
                    {
                        settled++;
                    }
                }

                if (settled == floodCount)
                {
                    break;
                }

                Thread.Sleep(10);
            }

            var accepted = 0;
            var rejected = 0;
            for (var index = 0; index < floodCount; index++)
            {
                if (listeners[index].Connected)
                {
                    accepted++;
                }
                else
                {
                    rejected++;
                }
            }

            Assert.That(serverCarrier.ConnectionRequestsSeen, Is.GreaterThanOrEqualTo(floodCount),
                "the server must observe every connection request");
            Assert.That(accepted, Is.LessThanOrEqualTo(TransportProtocol.MaxConnections),
                "accepted connections must never exceed the cap");

            // Let the server worker finish dispatching the Connect events of
            // the admitted peers before comparing carrier state.
            var settleDeadline = System.DateTime.UtcNow.AddSeconds(10);
            while (System.DateTime.UtcNow < settleDeadline &&
                serverCarrier.PeerMappingCount < accepted)
            {
                Thread.Sleep(20);
            }

            Assert.That(serverCarrier.PeerMappingCount, Is.EqualTo(accepted),
                "carrier peer state must equal the admitted connections");
            Assert.That(rejected, Is.GreaterThanOrEqualTo(
                    floodCount - TransportProtocol.MaxConnections),
                "requests beyond the cap must be rejected");
            Assert.That(serverCarrier.Metrics.DroppedConnectionLimit, Is.GreaterThanOrEqualTo(
                    floodCount - TransportProtocol.MaxConnections),
                "cap rejections must surface through the counter");

            // Teardown: stop every client; all admitted peer state must be
            // freed and rejected peers must have left nothing behind.
            for (var index = 0; index < floodCount; index++)
            {
                clients[index].Stop();
            }

            var cleanupDeadline = System.DateTime.UtcNow.AddSeconds(30);
            while (System.DateTime.UtcNow < cleanupDeadline &&
                serverCarrier.PeerMappingCount > 0)
            {
                Thread.Sleep(20);
            }

            Assert.That(serverCarrier.PeerMappingCount, Is.EqualTo(0),
                "no persistent carrier state may survive the flood");

            serverCarrier.Dispose();
        }

        [Test]
        public void RealUdpLoopback_AdmissionChurn_ReservationsNeverOvershoot_NoLeak()
        {
            // Per-peer admission reservation regression: concurrent pending
            // admissions and connect → close churn must never transiently
            // overshoot MaxConnections, and every released peer must free
            // exactly its own state (no leaks, no stolen reservations).
            var clock = new SystemTransportClock();
            var serverCarrier = LiteNetLibCarrier.CreateServer(clock);
            serverCarrier.StartServer(0);
            var port = serverCarrier.LocalPort;
            Assert.That(port, Is.GreaterThan(0));

            var maxObserved = 0;

            // Phase A: concurrent burst beyond the cap — exercises many
            // pending reservations at once.
            var burstCount = TransportProtocol.MaxConnections + 6;
            var listeners = new FloodClientListener[burstCount];
            var clients = new NetManager[burstCount];
            for (var index = 0; index < burstCount; index++)
            {
                listeners[index] = new FloodClientListener();
                clients[index] = new NetManager(listeners[index])
                {
                    IPv6Enabled = false
                };
                clients[index].Start();
                clients[index].Connect("127.0.0.1", port, "GlobalFront-Phase-2.5");
            }

            var deadline = System.DateTime.UtcNow.AddSeconds(40);
            while (System.DateTime.UtcNow < deadline)
            {
                var settled = 0;
                for (var index = 0; index < burstCount; index++)
                {
                    clients[index].PollEvents();
                    if (listeners[index].Settled)
                    {
                        settled++;
                    }
                }

                var observed = serverCarrier.PeerMappingCount;
                if (observed > maxObserved)
                {
                    maxObserved = observed;
                }

                if (settled == burstCount)
                {
                    break;
                }

                Thread.Sleep(10);
            }

            Assert.That(maxObserved, Is.LessThanOrEqualTo(TransportProtocol.MaxConnections),
                "the hard cap must hold during concurrent pending admissions");

            for (var index = 0; index < burstCount; index++)
            {
                clients[index].Stop();
            }

            var drainDeadline = System.DateTime.UtcNow.AddSeconds(30);
            while (System.DateTime.UtcNow < drainDeadline &&
                serverCarrier.PeerMappingCount > 0)
            {
                Thread.Sleep(20);
            }

            Assert.That(serverCarrier.PeerMappingCount, Is.EqualTo(0),
                "burst teardown must free every admitted peer");

            // Phase B: connect → close churn — each cycle admits one peer,
            // observes the cap, then closes it before the next cycle.
            for (var cycle = 0; cycle < 8; cycle++)
            {
                var listener = new FloodClientListener();
                var client = new NetManager(listener)
                {
                    IPv6Enabled = false
                };
                client.Start();
                client.Connect("127.0.0.1", port, "GlobalFront-Phase-2.5");

                var connectDeadline = System.DateTime.UtcNow.AddSeconds(15);
                while (System.DateTime.UtcNow < connectDeadline && !listener.Settled)
                {
                    client.PollEvents();
                    var observed = serverCarrier.PeerMappingCount;
                    if (observed > maxObserved)
                    {
                        maxObserved = observed;
                    }

                    Assert.That(observed, Is.LessThanOrEqualTo(TransportProtocol.MaxConnections),
                        $"the hard cap must hold during churn cycle {cycle}");
                    Thread.Sleep(5);
                }

                Assert.That(listener.Connected, Is.True,
                    $"churn cycle {cycle} must be admitted under the cap");

                client.Stop();
                var releaseDeadline = System.DateTime.UtcNow.AddSeconds(15);
                while (System.DateTime.UtcNow < releaseDeadline &&
                    serverCarrier.PeerMappingCount > 0)
                {
                    Thread.Sleep(10);
                }

                Assert.That(serverCarrier.PeerMappingCount, Is.EqualTo(0),
                    $"closed churn peer {cycle} must release exactly its own state");
            }

            Assert.That(maxObserved, Is.LessThanOrEqualTo(TransportProtocol.MaxConnections),
                "MaxConnections must never be exceeded across the whole churn");
            Assert.That(serverCarrier.PeerMappingCount, Is.EqualTo(0),
                "no state may leak after churn");

            serverCarrier.Dispose();
        }

        /// <summary>Minimal raw-LiteNetLib client listener for the flood test.</summary>
        private sealed class FloodClientListener : INetEventListener
        {
            public bool Connected;
            public bool Settled;

            public void OnPeerConnected(NetPeer peer)
            {
                Connected = true;
                Settled = true;
            }

            public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
            {
                Connected = false;
                Settled = true;
            }

            public void OnNetworkReceive(
                NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
            {
                reader.Recycle();
            }

            public void OnNetworkError(
                System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
            {
            }

            public void OnNetworkReceiveUnconnected(
                System.Net.IPEndPoint remoteEndPoint,
                NetPacketReader reader,
                UnconnectedMessageType messageType)
            {
                reader.Recycle();
            }

            public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
            {
            }

            public void OnConnectionRequest(ConnectionRequest request)
            {
                request.Reject();
            }
        }
    }
}