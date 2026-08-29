using System.Collections.Generic;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Phase 2.5 own-carrier reliability tests over the deterministic
    /// VirtualNetworkPipe: delivery under loss/duplication/reorder,
    /// retransmission, timeout, max retransmits, wrap-around arithmetic,
    /// receive/reorder windows, keepalive and idle detection — all on the
    /// virtual transport clock (no wall-clock).
    /// </summary>
    [TestFixture]
    public sealed class ReliableCarrierTests
    {
        private sealed class Harness
        {
            public VirtualNetworkPipe Pipe;
            public VirtualTransportClock Clock;
            public OwnDatagramCarrier Server;
            public OwnDatagramCarrier Client;
            public int ClientConnection;
            public int ServerConnection = -1;
            public readonly List<byte[]> ServerReceived = new List<byte[]>();
            public readonly List<TransportEvent> ServerEvents = new List<TransportEvent>();
            public readonly List<TransportEvent> ClientEvents = new List<TransportEvent>();

            public static Harness Create(ImpairmentProfile profile)
            {
                var harness = new Harness
                {
                    Pipe = new VirtualNetworkPipe(profile),
                    Clock = new VirtualTransportClock()
                };
                var serverAddress = harness.Pipe.CreateEndpoint();
                var clientAddress = harness.Pipe.CreateEndpoint();
                harness.Server = OwnDatagramCarrier.CreateServer(
                    harness.Pipe, serverAddress, harness.Clock);
                harness.Client = OwnDatagramCarrier.CreateClient(
                    harness.Pipe, clientAddress, serverAddress, harness.Clock);
                harness.ClientConnection = harness.Client.ConnectToServer("virtual", 0);
                return harness;
            }

            public void Pump(long milliseconds)
            {
                Pipe.Advance(milliseconds);
                Clock.Advance(milliseconds);
                Server.Pump(Clock.NowMs);
                Client.Pump(Clock.NowMs);
                DrainServer();
                DrainClient();
            }

            public void DrainServer()
            {
                while (Server.TryDequeueEvent(out var transportEvent))
                {
                    ServerEvents.Add(transportEvent);
                    if (transportEvent.Type == TransportEventType.Message)
                    {
                        var payload = new byte[transportEvent.Length];
                        System.Array.Copy(transportEvent.Data, payload, transportEvent.Length);
                        ServerReceived.Add(payload);
                    }
                    else if (transportEvent.Type == TransportEventType.Connected)
                    {
                        ServerConnection = transportEvent.ConnectionId;
                    }
                }
            }

            public void DrainClient()
            {
                while (Client.TryDequeueEvent(out var transportEvent))
                {
                    ClientEvents.Add(transportEvent);
                }
            }

            public void PumpUntilServerConnection(long budgetMs = 2000)
            {
                // The server creates a connection on the first datagram it
                // sees: send a probe, then pump until it arrives. The
                // carrier retransmits the probe reliably until the lossy
                // pipe lets it through.
                var probe = new byte[] { 0x55 };
                Client.Send(ClientConnection, TransportChannel.Command, probe, 0, probe.Length);
                var elapsed = 0L;
                while (ServerConnection < 0 && elapsed < budgetMs)
                {
                    Pump(5);
                    elapsed += 5;
                }

                // Drop the probe delivery from the observable streams so the
                // test assertions see only their own traffic.
                DrainServer();
                ServerReceived.Clear();
                ServerEvents.Clear();
            }
        }

        [TestCase(0.05)]
        [TestCase(0.10)]
        [TestCase(0.20)]
        public void ReliableDelivery_CompletesInOrder_UnderLoss(double loss)
        {
            var harness = Harness.Create(new ImpairmentProfile
            {
                LossProbability = loss,
                Seed = 42
            });
            harness.PumpUntilServerConnection();
            Assert.That(harness.ServerConnection, Is.GreaterThanOrEqualTo(0), "server must see the connection");

            const int messageCount = 30;
            for (var index = 0; index < messageCount; index++)
            {
                var payload = new byte[] { (byte)index, 0xAA };
                harness.Client.Send(
                    harness.ClientConnection, TransportChannel.Command, payload, 0, payload.Length);
            }

            var elapsed = 0L;
            while (harness.ServerReceived.Count < messageCount && elapsed < 60000)
            {
                harness.Pump(10);
                elapsed += 10;
            }

            Assert.That(harness.ServerReceived.Count, Is.EqualTo(messageCount),
                $"all messages must arrive through {loss:P0} loss");
            for (var index = 0; index < messageCount; index++)
            {
                Assert.That(harness.ServerReceived[index][0], Is.EqualTo((byte)index),
                    "reliable space must deliver in order");
            }
        }

        [Test]
        public void ReliableDelivery_Deduplicates_DuplicatedDatagrams()
        {
            var harness = Harness.Create(new ImpairmentProfile
            {
                DuplicationProbability = 0.8,
                Seed = 7
            });
            harness.PumpUntilServerConnection();

            var payload = new byte[] { 1, 2, 3 };
            harness.Client.Send(
                harness.ClientConnection, TransportChannel.Command, payload, 0, payload.Length);

            var elapsed = 0L;
            while (elapsed < 3000)
            {
                harness.Pump(10);
                elapsed += 10;
            }

            Assert.That(harness.ServerReceived.Count, Is.EqualTo(1),
                "duplicates must be suppressed by the sequence window");
        }

        [Test]
        public void ReliableDelivery_ReorderedDatagrams_ArriveInOrder()
        {
            var harness = Harness.Create(new ImpairmentProfile
            {
                LatencyMs = 10,
                ReorderJitterMs = 40,
                Seed = 99
            });
            harness.PumpUntilServerConnection();

            for (var index = 0; index < 20; index++)
            {
                var payload = new byte[] { (byte)index };
                harness.Client.Send(
                    harness.ClientConnection, TransportChannel.Command, payload, 0, payload.Length);
            }

            var elapsed = 0L;
            while (harness.ServerReceived.Count < 20 && elapsed < 20000)
            {
                harness.Pump(5);
                elapsed += 5;
            }

            Assert.That(harness.ServerReceived.Count, Is.EqualTo(20));
            for (var index = 0; index < 20; index++)
            {
                Assert.That(harness.ServerReceived[index][0], Is.EqualTo((byte)index));
            }
        }

        [Test]
        public void Retransmission_OccursUnderLoss_AndIsCounted()
        {
            var harness = Harness.Create(new ImpairmentProfile { Seed = 13 });
            harness.PumpUntilServerConnection();

            // Guaranteed loss for the first transmission attempt: partition
            // the pipe, send, pump once, then restore. The only delivery
            // path left is a retransmission.
            harness.Pipe.SetPartitioned(true);
            var payload = new byte[] { 5 };
            harness.Client.Send(
                harness.ClientConnection, TransportChannel.Command, payload, 0, payload.Length);
            harness.Pump(10);
            harness.Pipe.SetPartitioned(false);

            var elapsed = 0L;
            while (harness.ServerReceived.Count < 1 && elapsed < 60000)
            {
                harness.Pump(10);
                elapsed += 10;
            }

            Assert.That(harness.ServerReceived.Count, Is.EqualTo(1));
            Assert.That(harness.Client.Metrics.Retransmissions, Is.GreaterThan(0),
                "loss must be compensated by retransmission");
        }

        [Test]
        public void MaxRetransmits_ProducesDeterministicLossEvent()
        {
            var harness = Harness.Create(new ImpairmentProfile { Seed = 3 });

            // Partition BEFORE the first datagram: nothing can get through,
            // so the sender's ARQ must give up after MaxRetransmits.
            harness.Pipe.SetPartitioned(true);

            var payload = new byte[] { 9 };
            harness.Client.Send(
                harness.ClientConnection, TransportChannel.Command, payload, 0, payload.Length);
            var elapsed = 0L;
            var lost = false;
            while (elapsed < 30000)
            {
                harness.Pump(50);
                elapsed += 50;
                foreach (var transportEvent in harness.ClientEvents)
                {
                    if (transportEvent.Type == TransportEventType.Disconnected &&
                        transportEvent.Reason == TransportDisconnectReason.MaxRetransmits)
                    {
                        lost = true;
                    }
                }

                if (lost)
                {
                    break;
                }
            }

            Assert.That(lost, Is.True, "max retransmits must terminate the connection");
        }

        [Test]
        public void IdleTimeout_FiresAfterSilence_OnTransportClock()
        {
            var harness = Harness.Create(new ImpairmentProfile { Seed = 5 });
            harness.PumpUntilServerConnection();
            harness.ServerEvents.Clear();
            harness.ClientEvents.Clear();

            // Full partition: no keepalive can cross, so both sides must
            // time out purely on the transport clock.
            harness.Pipe.SetPartitioned(true);

            var timedOut = false;
            for (var step = 0; step < 200; step++)
            {
                harness.Pump(100);
                foreach (var transportEvent in harness.ServerEvents)
                {
                    if (transportEvent.Type == TransportEventType.Disconnected &&
                        transportEvent.Reason == TransportDisconnectReason.Timeout)
                    {
                        timedOut = true;
                    }
                }

                if (timedOut)
                {
                    break;
                }
            }

            Assert.That(timedOut, Is.True,
                "idle timeout must fire on the transport clock after full silence");
            Assert.That(harness.Server.Metrics.ConnectionTimeouts, Is.GreaterThan(0));
        }

        [Test]
        public void Keepalive_SustainsIdleConnection_BeyondIdleTimeout()
        {
            var harness = Harness.Create(new ImpairmentProfile { Seed = 11 });
            harness.PumpUntilServerConnection();
            harness.ServerEvents.Clear();

            // No application traffic at all: only carrier keepalive flows.
            for (var step = 0; step < 20; step++)
            {
                harness.Pump(1000);
            }

            foreach (var transportEvent in harness.ServerEvents)
            {
                Assert.That(transportEvent.Type, Is.Not.EqualTo(TransportEventType.Disconnected),
                    "keepalive must keep an idle connection alive");
            }
        }

        [Test]
        public void DeterministicRuns_ProduceIdenticalMetrics()
        {
            var first = RunLossScenario(seed: 777);
            var second = RunLossScenario(seed: 777);

            Assert.That(second.Delivered, Is.EqualTo(first.Delivered));
            Assert.That(second.Retransmissions, Is.EqualTo(first.Retransmissions));
            Assert.That(second.Duplicates, Is.EqualTo(first.Duplicates));
        }

        [Test]
        public void ReceiveWindow_RejectsOutOfWindowSequences()
        {
            var harness = Harness.Create(new ImpairmentProfile { Seed = 2 });
            harness.PumpUntilServerConnection();

            // Inject a forged datagram from a fake peer with a sequence far
            // outside the receive window (AckBase starts at 0).
            var serverAddress = 1;
            var buffer = new byte[TransportProtocol.MaxDatagramBytes];
            var size = EnvelopeCodec.Encode(
                buffer,
                TransportChannel.Command,
                0,
                sequence: 4000,
                ackNumber: 0,
                ackBitmap: 0,
                messageId: 0,
                fragmentIndex: 0,
                fragmentCount: 0,
                payload: new byte[] { 1 },
                payloadOffset: 0,
                payloadLength: 1);
            harness.Pipe.SendDatagram(999, serverAddress, buffer, size);
            harness.Pump(10);

            Assert.That(harness.Server.Metrics.DroppedReplayOrWindow, Is.GreaterThan(0));
        }

        [Test]
        public void ReplayedDatagram_IsSuppressed()
        {
            var harness = Harness.Create(new ImpairmentProfile { Seed = 4 });
            harness.PumpUntilServerConnection();

            var payload = new byte[] { 3 };
            harness.Client.Send(
                harness.ClientConnection, TransportChannel.Command, payload, 0, payload.Length);
            var elapsed = 0L;
            while (harness.ServerReceived.Count < 1 && elapsed < 5000)
            {
                harness.Pump(5);
                elapsed += 5;
            }

            var before = harness.ServerReceived.Count;
            // Pump long enough for any stray duplicate window to settle.
            for (var index = 0; index < 100; index++)
            {
                harness.Pump(10);
            }

            Assert.That(harness.ServerReceived.Count, Is.EqualTo(before),
                "no duplicate delivery must occur after the window passes");
        }

        [Test]
        public void SerialArithmetic_HandlesWrapAround()
        {
            Assert.That(SerialArithmetic.Diff(2, 65534), Is.EqualTo(4));
            Assert.That(SerialArithmetic.Diff(65534, 2), Is.EqualTo(-4));
            Assert.That(SerialArithmetic.IsNewer(1, 65535), Is.True);
            Assert.That(SerialArithmetic.IsNewer(65535, 1), Is.False);
            Assert.That(SerialArithmetic.Diff(100, 100), Is.EqualTo(0));
            Assert.That(SerialArithmetic.Next(65535), Is.EqualTo(0));
            Assert.That(SerialArithmetic.Add(65530, 10), Is.EqualTo(4));
            // Half-space boundary: treated as "older" by convention.
            Assert.That(SerialArithmetic.Diff(32768, 0), Is.LessThan(0));
        }

        private static (int Delivered, long Retransmissions, long Duplicates) RunLossScenario(int seed)
        {
            var harness = Harness.Create(new ImpairmentProfile
            {
                LossProbability = 0.2,
                DuplicationProbability = 0.2,
                Seed = seed
            });
            harness.PumpUntilServerConnection();

            for (var index = 0; index < 50; index++)
            {
                var payload = new byte[] { (byte)index };
                harness.Client.Send(
                    harness.ClientConnection, TransportChannel.Command, payload, 0, payload.Length);
            }

            var elapsed = 0L;
            while (harness.ServerReceived.Count < 50 && elapsed < 60000)
            {
                harness.Pump(10);
                elapsed += 10;
            }

            return (
                harness.ServerReceived.Count,
                harness.Client.Metrics.Retransmissions,
                harness.Client.Metrics.DuplicateSuppressed);
        }

        [Test]
        public void Retransmissions_StayBounded_UnderFivePercentLoss()
        {
            var harness = Harness.Create(new ImpairmentProfile
            {
                LossProbability = 0.05,
                LatencyMs = 10,
                ReorderJitterMs = 10,
                Seed = 4242
            });
            harness.PumpUntilServerConnection();

            // Regression bound derivation (no magic threshold): expected
            // first-attempt losses = 0.05 * 2000 = 100; retransmission
            // losses add ~5% of that (~5); the reorder-span throttle keeps
            // window drops near zero. Bound = 500 ~= 5x the expected ~105.
            // The pre-fix amplification produced tens of thousands of
            // retransmissions and fails this by two orders of magnitude.
            const int messageCount = 2000;
            const long retransmitBound = 500;

            var ackBait = new byte[] { 1 };
            var sent = 0;
            var elapsed = 0L;
            while (sent < messageCount && elapsed < 120000)
            {
                var payload = new byte[] { (byte)sent, (byte)(sent >> 8) };
                if (harness.Client.Send(
                        harness.ClientConnection, TransportChannel.Command, payload, 0, payload.Length))
                {
                    sent++;
                }

                // Server ack traffic analogue (like CommandAck messages):
                // carries the server's cumulative ack back to the client.
                if (harness.ServerConnection >= 0 && elapsed % 20 == 0)
                {
                    harness.Server.Send(
                        harness.ServerConnection, TransportChannel.Control, ackBait, 0, ackBait.Length);
                }

                harness.Pump(10);
                elapsed += 10;
            }

            Assert.That(sent, Is.EqualTo(messageCount), "sends must keep succeeding under backpressure");

            while (harness.ServerReceived.Count < messageCount && elapsed < 240000)
            {
                if (harness.ServerConnection >= 0 && elapsed % 20 == 0)
                {
                    harness.Server.Send(
                        harness.ServerConnection, TransportChannel.Control, ackBait, 0, ackBait.Length);
                }

                harness.Pump(10);
                elapsed += 10;
            }

            Assert.That(harness.ServerReceived.Count, Is.EqualTo(messageCount),
                "all messages must arrive under 5% loss");
            for (var index = 0; index < messageCount; index++)
            {
                Assert.That(harness.ServerReceived[index][0], Is.EqualTo((byte)index));
                Assert.That(harness.ServerReceived[index][1], Is.EqualTo((byte)(index >> 8)));
            }

            Assert.That(harness.Client.Metrics.Retransmissions, Is.LessThanOrEqualTo(retransmitBound),
                "retransmissions must stay proportional to losses, not amplify");
            Assert.That(harness.Client.Metrics.DroppedReplayOrWindow, Is.LessThanOrEqualTo(10),
                "the reorder-span throttle must prevent window-drop cascades");
            Assert.That(harness.Client.Metrics.MaxRetransmitLosses, Is.EqualTo(0),
                "no connection may die under 5% loss");
            Assert.That(harness.Client.Metrics.ConnectionTimeouts, Is.EqualTo(0));
        }

        [Test]
        public void Snapshot_MultiFragment_DeliversOneMessagePerSnapshot()
        {
            var harness = Harness.Create(new ImpairmentProfile { Seed = 21 });
            harness.PumpUntilServerConnection();

            // Two multi-fragment snapshots (>1171 payload bytes each).
            var first = new byte[2400];
            var second = new byte[3600];
            for (var index = 0; index < first.Length; index++)
            {
                first[index] = (byte)(index & 0xFF);
            }

            for (var index = 0; index < second.Length; index++)
            {
                second[index] = (byte)((index * 7) & 0xFF);
            }

            Assert.That(
                harness.Client.Send(
                    harness.ClientConnection, TransportChannel.Snapshot, first, 0, first.Length),
                Is.True);
            Assert.That(
                harness.Client.Send(
                    harness.ClientConnection, TransportChannel.Snapshot, second, 0, second.Length),
                Is.True);

            var elapsed = 0L;
            while (harness.ServerReceived.Count < 2 && elapsed < 10000)
            {
                harness.Pump(5);
                elapsed += 5;
            }

            Assert.That(harness.ServerReceived.Count, Is.EqualTo(2),
                "a fragment stream is ONE logical snapshot per message, never one per fragment");
            Assert.That(harness.ServerReceived[0], Is.EqualTo(first),
                "the first snapshot must reassemble byte-identical");
            Assert.That(harness.ServerReceived[1], Is.EqualTo(second),
                "the second snapshot must reassemble byte-identical");
        }

        [Test]
        public void Snapshot_DuplicatedInterleavedFragments_DeliverOnceEach()
        {
            var harness = Harness.Create(new ImpairmentProfile
            {
                DuplicationProbability = 0.7,
                LatencyMs = 5,
                ReorderJitterMs = 15,
                Seed = 33
            });
            harness.PumpUntilServerConnection();

            var first = new byte[2500];
            var second = new byte[3500];
            for (var index = 0; index < first.Length; index++)
            {
                first[index] = (byte)((index * 3) & 0xFF);
            }

            for (var index = 0; index < second.Length; index++)
            {
                second[index] = (byte)((index * 11) & 0xFF);
            }

            harness.Client.Send(
                harness.ClientConnection, TransportChannel.Snapshot, first, 0, first.Length);
            harness.Client.Send(
                harness.ClientConnection, TransportChannel.Snapshot, second, 0, second.Length);

            var elapsed = 0L;
            while (harness.ServerReceived.Count < 2 && elapsed < 10000)
            {
                harness.Pump(5);
                elapsed += 5;
            }

            // Let any stray duplicate fragments settle.
            for (var index = 0; index < 100; index++)
            {
                harness.Pump(10);
            }

            Assert.That(harness.ServerReceived.Count, Is.EqualTo(2),
                "duplicated/reordered fragments must not produce extra snapshots");
            Assert.That(harness.ServerReceived[0], Is.EqualTo(first));
            Assert.That(harness.ServerReceived[1], Is.EqualTo(second));
        }

        [Test]
        public void Snapshot_StaleLateFragment_DroppedAfterNewerDelivered()
        {
            var harness = Harness.Create(new ImpairmentProfile { Seed = 44 });
            harness.PumpUntilServerConnection();

            var first = new byte[2400];
            var second = new byte[1300];
            harness.Client.Send(
                harness.ClientConnection, TransportChannel.Snapshot, first, 0, first.Length);
            harness.Client.Send(
                harness.ClientConnection, TransportChannel.Snapshot, second, 0, second.Length);

            var elapsed = 0L;
            while (harness.ServerReceived.Count < 2 && elapsed < 10000)
            {
                harness.Pump(5);
                elapsed += 5;
            }

            Assert.That(harness.ServerReceived.Count, Is.EqualTo(2));
            var beforeStale = harness.Server.Metrics.DroppedStaleSnapshot;
            var deliveredBefore = harness.ServerReceived.Count;

            // Forge a LATE fragment of the older snapshot (snapshot seq 1 is
            // strictly older than the delivered seq 2): it must be dropped
            // and must not corrupt or extend the delivered stream.
            var serverAddress = 1;
            var buffer = new byte[TransportProtocol.MaxDatagramBytes];
            var size = EnvelopeCodec.Encode(
                buffer,
                TransportChannel.Snapshot,
                0,
                sequence: 1,
                ackNumber: 0,
                ackBitmap: 0,
                messageId: 99,
                fragmentIndex: 0,
                fragmentCount: 2,
                payload: new byte[] { 0xDE, 0xAD },
                payloadOffset: 0,
                payloadLength: 2);
            harness.Pipe.SendDatagram(2, serverAddress, buffer, size);
            harness.Pump(10);

            Assert.That(harness.Server.Metrics.DroppedStaleSnapshot, Is.GreaterThan(beforeStale),
                "late fragments of an older snapshot must be dropped as stale");
            Assert.That(harness.ServerReceived.Count, Is.EqualTo(deliveredBefore),
                "stale fragments must not produce deliveries");
        }
    }
}
