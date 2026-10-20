using System;
using System.Collections.Generic;
using System.Reflection;
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
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

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

        [Test]
        public void GcAllocationRecorder_ObservesKnownAllocation_Control()
        {
            // The editor's Mono runtime reports 0 from
            // GC.GetAllocatedBytesForCurrentThread() (established project
            // fact), so zero-GC evidence uses Unity's GC.Alloc recorder.
            // Prove the recorder is live before trusting an empty sample set.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                UnityEngine.TestTools.Constraints.Is.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");
            Assert.That(ballast, Is.Not.Null);
        }

        [Test]
        public void SendFragmentedSnapshots_SteadyState_ZeroHeapAllocations()
        {
            // D-4/P1-1: the server simulation thread sends fragmented C2
            // snapshots every tick. Any per-fragment byte[] allocation is a
            // GC.Alloc sample on this thread and fails the recorder window.
            var clock = new SystemTransportClock();
            var serverCarrier = LiteNetLibCarrier.CreateServer(clock);
            serverCarrier.StartServer(0);
            var port = serverCarrier.LocalPort;
            Assert.That(port, Is.GreaterThan(0));

            // Freeze MTU discovery, keepalive pings and the idle timeout:
            // pings recycle small LiteNetLib control packets whose buffers
            // are resized on reuse, and the test intentionally stops polling
            // the raw client while it measures. Both are test-harness
            // concerns, not the project send path.
            var serverManager = GetManagerForTests(serverCarrier);
            serverManager.PingInterval = 3600000;
            serverManager.MtuDiscovery = false;
            serverManager.DisconnectTimeout = 3600000;

            var listener = new FloodClientListener();
            var client = new NetManager(listener)
            {
                IPv6Enabled = false,
                PingInterval = 3600000,
                MtuDiscovery = false,
                DisconnectTimeout = 3600000
            };
            client.Start();
            try
            {
                client.Connect("127.0.0.1", port, "GlobalFront-Phase-2.5");
                var connectionId = WaitForCarrierConnection(serverCarrier, client, 10000);
                Assert.That(connectionId, Is.GreaterThan(0),
                    $"carrier connection must be established; " +
                    $"clientState={client.FirstPeer?.ConnectionState} " +
                    $"serverWorkerError={serverCarrier.LastWorkerError}");

                // 3000 bytes is above any single Sequenced packet, so every
                // message takes three fragments at both the initial and the
                // discovered MTU.
                var payload = new byte[3000];
                for (var index = 0; index < payload.Length; index++)
                {
                    payload[index] = (byte)index;
                }

                // Burst warm-up: fill LiteNetLib's packet pool with buffers of
                // the measured size (small handshake buffers get resized on
                // reuse) and finish MTU discovery. Pool growth is LiteNetLib
                // internals, not the project send path, so it must happen here.
                var warmupSent = 0;
                for (var index = 0; index < 256; index++)
                {
                    if (serverCarrier.Send(
                            connectionId, TransportChannel.Snapshot, payload, 0, payload.Length))
                    {
                        warmupSent++;
                    }
                }

                Assert.That(warmupSent, Is.EqualTo(256),
                    "every warm-up fragmented snapshot must be accepted");

                Assert.That(WaitForQuiescence(client, listener, 200, 15000), Is.True,
                    "warm-up snapshot burst must drain before measuring");

                // LiteNetLib may resize a pooled buffer once while its pool
                // settles; that is library-internal, not the project send
                // path. Establish steady state with two consecutive clean
                // 100-snapshot windows before the evidence window.
                var observed = new int[6];
                var zeroStreak = 0;
                for (var attempt = 0; attempt < observed.Length && zeroStreak < 2; attempt++)
                {
                    observed[attempt] = CountCurrentThreadGcAllocations(
                        () =>
                        {
                            for (var index = 0; index < 100; index++)
                            {
                                serverCarrier.Send(
                                    connectionId,
                                    TransportChannel.Snapshot,
                                    payload,
                                    0,
                                    payload.Length);
                                Thread.Sleep(2);
                            }
                        });
                    zeroStreak = observed[attempt] == 0 ? zeroStreak + 1 : 0;
                }

                var sent = 0;
                var measured = CountCurrentThreadGcAllocations(
                    () =>
                    {
                        for (var index = 0; index < 100; index++)
                        {
                            if (serverCarrier.Send(
                                    connectionId,
                                    TransportChannel.Snapshot,
                                    payload,
                                    0,
                                    payload.Length))
                            {
                                sent++;
                            }

                            Thread.Sleep(2);
                        }
                    });

                Assert.That(measured, Is.EqualTo(0),
                    "100 fragmented snapshots must make 0 GC.Alloc samples " +
                    $"(settle windows [{string.Join(",", observed)}])");
                Assert.That(zeroStreak, Is.GreaterThanOrEqualTo(2),
                    "LiteNetLib's packet pool must reach steady state first; " +
                    $"observed [{string.Join(",", observed)}]");
                Assert.That(sent, Is.EqualTo(100),
                    "every measured fragmented snapshot must be accepted; " +
                    $"disconnect={serverCarrier.LastDisconnectReason} " +
                    $"sendError={serverCarrier.LastSendError} " +
                    $"peers={serverCarrier.PeerMappingCount}");
            }
            finally
            {
                client.Stop();
                serverCarrier.Dispose();
            }
        }

        [Test]
        public void SnapshotCompletion_StaleScan_ReusesPerPeerScratch_NoListAllocation()
        {
            // D-4/P1-1: the latest-wins stale scan used to allocate a local
            // List<ushort> on every delivered snapshot. HandleSnapshotReceive
            // is invoked directly on this thread so the allocation window is
            // deterministic (the real receive path runs on the worker).
            var carrier = LiteNetLibCarrier.CreateServer(new SystemTransportClock());
            var handle = BindSnapshotHandler(carrier);
            const int connectionId = 4242;
            const int payloadLength = 64;

            // Warm-up: per-peer state, sequence map, assembler and event
            // queue must be past their first-use allocations.
            for (var index = 1; index <= 8; index++)
            {
                var id = (ushort)index;
                var sequence = (ushort)index;
                var first = BuildSnapshotDatagram(id, sequence, 0, 2, payloadLength);
                var last = BuildSnapshotDatagram(id, sequence, 1, 2, payloadLength);
                handle(connectionId, first, 0, first.Length);
                handle(connectionId, last, 0, last.Length);
                DrainEvents(carrier);
            }

            const ushort measuredId = 100;
            const ushort measuredSequence = 100;
            var seed = BuildSnapshotDatagram(measuredId, measuredSequence, 0, 2, payloadLength);
            var completion = BuildSnapshotDatagram(measuredId, measuredSequence, 1, 2, payloadLength);
            handle(connectionId, seed, 0, seed.Length);
            DrainEvents(carrier);

            // The completion's only unavoidable allocations: the second
            // fragment slice and the assembled message.
            var baseline = CountCurrentThreadGcAllocations(
                () =>
                {
                    var sliceProbe = new byte[payloadLength];
                    var messageProbe = new byte[payloadLength * 2];
                    System.GC.KeepAlive(sliceProbe);
                    System.GC.KeepAlive(messageProbe);
                });

            var measured = CountCurrentThreadGcAllocations(
                () => handle(connectionId, completion, 0, completion.Length));
            DrainEvents(carrier);

            Assert.That(baseline, Is.EqualTo(2),
                "control: the two payload arrays must be visible as two GC.Alloc samples");
            Assert.That(measured - baseline, Is.EqualTo(0),
                "the latest-wins stale scan must reuse PeerSnapshotState.StaleIdsScratch");
            carrier.Dispose();
        }

        [Test]
        public void SnapshotIncompleteFragmentFlood_TrackedGroupsBounded()
        {
            // D-9: unique incomplete messageIds used to grow SeqByMessageId
            // without limit even though the assembler itself stayed capped.
            var carrier = LiteNetLibCarrier.CreateServer(new SystemTransportClock());
            var handle = BindSnapshotHandler(carrier);
            const int connectionId = 8080;
            const int floodCount = 10;
            var staleBefore = carrier.Metrics.DroppedStaleSnapshot;

            for (var index = 1; index <= floodCount; index++)
            {
                var incomplete = BuildSnapshotDatagram(
                    (ushort)index, (ushort)index, 0, 2, 32);
                handle(connectionId, incomplete, 0, incomplete.Length);
            }

            var delivered = 0;
            while (carrier.TryDequeueEvent(out _))
            {
                delivered++;
            }

            Assert.That(delivered, Is.Zero,
                "fragment 0 of 2 must never complete a snapshot");
            var tracked = TrackedFragmentGroupCount(carrier, connectionId);
            Assert.That(tracked, Is.LessThanOrEqualTo(TransportProtocol.MaxFragmentGroups),
                "unique incomplete messageIds must not grow the tracked-group map past the cap");
            Assert.That(carrier.Metrics.DroppedStaleSnapshot - staleBefore,
                Is.EqualTo(floodCount - TransportProtocol.MaxFragmentGroups),
                "each evicted incomplete group must be counted as a stale drop");
            carrier.Dispose();
        }

        [Test]
        public void SingleFragmentSnapshotReceive_OwnsExactlyOnePayloadCopy()
        {
            // D-4/P1-1: a single-fragment snapshot must hand the event queue
            // one owned payload array and nothing more. The receive scratch
            // is only valid until reader.Recycle().
            var carrier = LiteNetLibCarrier.CreateServer(new SystemTransportClock());
            var handle = BindSnapshotHandler(carrier);
            const int connectionId = 777;
            const int payloadLength = 256;

            // Warm-up: the second delivery is the first one that runs the
            // fully warmed delivery path (first-use bookkeeping leaves the
            // measured steady state).
            var warmup1 = BuildSnapshotDatagram(1, 1, 0, 1, payloadLength);
            handle(connectionId, warmup1, 0, warmup1.Length);
            DrainEvents(carrier);
            var warmup2 = BuildSnapshotDatagram(2, 2, 0, 1, payloadLength);
            handle(connectionId, warmup2, 0, warmup2.Length);
            DrainEvents(carrier);
            var warmup3 = BuildSnapshotDatagram(3, 3, 0, 1, payloadLength);
            handle(connectionId, warmup3, 0, warmup3.Length);
            DrainEvents(carrier);

            // Two delivered snapshots must own exactly two payload arrays.
            var baseline = CountCurrentThreadGcAllocations(
                () =>
                {
                    var probeA = new byte[payloadLength];
                    var probeB = new byte[payloadLength];
                    System.GC.KeepAlive(probeA);
                    System.GC.KeepAlive(probeB);
                });

            var measuredA = BuildSnapshotDatagram(4, 4, 0, 1, payloadLength);
            var measuredB = BuildSnapshotDatagram(5, 5, 0, 1, payloadLength);
            var measured = CountCurrentThreadGcAllocations(
                () =>
                {
                    handle(connectionId, measuredA, 0, measuredA.Length);
                    handle(connectionId, measuredB, 0, measuredB.Length);
                });
            DrainEvents(carrier);

            Assert.That(baseline, Is.EqualTo(2),
                "control: the owned payloads must be visible as two GC.Alloc samples");
            Assert.That(measured - baseline, Is.EqualTo(0),
                "single-fragment receive must allocate exactly one owned payload per message");
            carrier.Dispose();
        }

        private static Action<int, byte[], int, int> BindSnapshotHandler(
            LiteNetLibCarrier carrier)
        {
            var method = typeof(LiteNetLibCarrier).GetMethod(
                "HandleSnapshotReceive",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null,
                "HandleSnapshotReceive must remain the snapshot receive entry point");
            return (Action<int, byte[], int, int>)Delegate.CreateDelegate(
                typeof(Action<int, byte[], int, int>),
                carrier,
                method);
        }

        private static int TrackedFragmentGroupCount(
            LiteNetLibCarrier carrier,
            int connectionId)
        {
            var statesField = typeof(LiteNetLibCarrier).GetField(
                "_snapshotStates",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(statesField, Is.Not.Null,
                "LiteNetLibCarrier must keep per-peer snapshot state");
            var states = (System.Collections.IDictionary)statesField.GetValue(carrier);
            var state = states[connectionId];
            Assert.That(state, Is.Not.Null,
                "receiving a fragment must create the per-peer snapshot state");
            var mapField = state.GetType().GetField(
                "SeqByMessageId",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(mapField, Is.Not.Null,
                "PeerSnapshotState must keep the tracked-group map");
            var map = (System.Collections.IDictionary)mapField.GetValue(state);
            return map.Count;
        }

        private static NetManager GetManagerForTests(LiteNetLibCarrier carrier)
        {
            var field = typeof(LiteNetLibCarrier).GetField(
                "_manager",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "LiteNetLibCarrier must own a NetManager");
            var manager = (NetManager)field.GetValue(carrier);
            Assert.That(manager, Is.Not.Null, "StartServer must have created the NetManager");
            return manager;
        }

        private static byte[] BuildSnapshotDatagram(
            ushort messageId,
            ushort sequence,
            ushort fragmentIndex,
            ushort fragmentCount,
            int payloadLength)
        {
            var datagram = new byte[8 + payloadLength];
            datagram[0] = (byte)(messageId & 0xFF);
            datagram[1] = (byte)((messageId >> 8) & 0xFF);
            datagram[2] = (byte)(sequence & 0xFF);
            datagram[3] = (byte)((sequence >> 8) & 0xFF);
            datagram[4] = (byte)(fragmentIndex & 0xFF);
            datagram[5] = (byte)((fragmentIndex >> 8) & 0xFF);
            datagram[6] = (byte)(fragmentCount & 0xFF);
            datagram[7] = (byte)((fragmentCount >> 8) & 0xFF);
            for (var index = 0; index < payloadLength; index++)
            {
                datagram[8 + index] = (byte)((fragmentIndex * 31 + index) & 0xFF);
            }

            return datagram;
        }

        private static void DrainEvents(LiteNetLibCarrier carrier)
        {
            while (carrier.TryDequeueEvent(out _))
            {
            }
        }

        private static int WaitForCarrierConnection(
            LiteNetLibCarrier carrier,
            NetManager client,
            int timeoutMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                client.PollEvents();
                while (carrier.TryDequeueEvent(out var transportEvent))
                {
                    if (transportEvent.Type == TransportEventType.Connected)
                    {
                        return transportEvent.ConnectionId;
                    }
                }

                Thread.Sleep(2);
            }

            return 0;
        }

        private static int CountCurrentThreadGcAllocations(Action action)
        {
            // Same recorder contract as UnityEngine.TestTools.Constraints:
            // disable to flush, filter to this thread, then sample only the
            // action. The editor's Mono runtime reports 0 from
            // GC.GetAllocatedBytesForCurrentThread(), so this is the live
            // allocation meter.
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

        private static bool WaitForQuiescence(
            NetManager client,
            FloodClientListener listener,
            int quietMs,
            int timeoutMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var lastCount = -1;
            var lastChangeMs = 0L;
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                client.PollEvents();
                if (listener.ReceivedDatagrams != lastCount)
                {
                    lastCount = listener.ReceivedDatagrams;
                    lastChangeMs = watch.ElapsedMilliseconds;
                }
                else if (lastCount > 0 && watch.ElapsedMilliseconds - lastChangeMs >= quietMs)
                {
                    return true;
                }

                Thread.Sleep(1);
            }

            return false;
        }

        /// <summary>Minimal raw-LiteNetLib client listener for the flood test.</summary>
        private sealed class FloodClientListener : INetEventListener
        {
            public bool Connected;
            public bool Settled;
            public int ReceivedDatagrams;

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
                ReceivedDatagrams++;
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