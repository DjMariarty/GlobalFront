using System;
using System.Collections.Generic;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode;
using NUnit.Framework;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Server.Transport
{
    /// <summary>
    /// Step 4.6.2 (OD-43): the deterministic impairment harness. The presets
    /// must produce the loss rates they advertise, the burst model must drop
    /// consecutive packets, identical seeds must reproduce identical traces,
    /// and the reliable channel must still deliver in order through 5% loss
    /// and jitter.
    /// </summary>
    [TestFixture]
    public sealed class NetworkImpairmentHarnessTests
    {
        private const int PacketCount = 1000;
        private const int PayloadBytes = 4;

        [Test]
        public void WifiProfile_WitnessesOnePercentLoss_AndDeliversWithJitter()
        {
            var pipe = new VirtualNetworkPipe(ImpairmentProfile.Wifi(seed: 42));
            var sender = pipe.CreateEndpoint();
            var receiver = pipe.CreateEndpoint();

            for (var index = 0; index < PacketCount; index++)
            {
                pipe.SendDatagram(sender, receiver, PayloadFor(index), PayloadBytes);
            }

            pipe.Advance(2000);

            Assert.That(pipe.SentCount, Is.EqualTo(PacketCount));
            Assert.That(pipe.DroppedCount, Is.InRange(3, 25),
                "the Wi-Fi preset must witness roughly one percent loss, not zero");
            Assert.That(pipe.DeliveredCount, Is.EqualTo(PacketCount - pipe.DroppedCount));
            Assert.That(pipe.InFlightCount, Is.Zero, "2 s must flush the 15 ms + 5 ms jitter window");
        }

        [Test]
        public void WanProfile_WitnessesFivePercentLoss_AndReordering()
        {
            var pipe = new VirtualNetworkPipe(ImpairmentProfile.Wan(seed: 42));
            var sender = pipe.CreateEndpoint();
            var receiver = pipe.CreateEndpoint();

            for (var index = 0; index < PacketCount; index++)
            {
                pipe.SendDatagram(sender, receiver, PayloadFor(index), PayloadBytes);
            }

            var received = new List<int>();
            for (var step = 0; step < 500; step++)
            {
                pipe.Advance(2);
                while (pipe.TryReceive(receiver, out _, out var datagram))
                {
                    received.Add(BitConverter.ToInt32(datagram, 0));
                }
            }

            Assert.That(pipe.DroppedCount, Is.InRange(30, 75),
                "the WAN preset must witness roughly five percent loss");
            Assert.That(pipe.DeliveredCount, Is.EqualTo(PacketCount - pipe.DroppedCount));
            Assert.That(received.Count, Is.EqualTo(pipe.DeliveredCount));

            var inversions = 0;
            for (var index = 0; index + 1 < received.Count; index++)
            {
                if (received[index] > received[index + 1])
                {
                    inversions++;
                }
            }

            Assert.That(inversions, Is.GreaterThan(0),
                "20 ms jitter must reorder at least one pair of datagrams");
        }

        [Test]
        public void BurstLoss_DropsConsecutivePackets()
        {
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile
            {
                BurstLossProbability = 0.05,
                BurstLossMin = 4,
                BurstLossMax = 4,
                LossProbability = 0,
                Seed = 77
            });
            var sender = pipe.CreateEndpoint();
            var receiver = pipe.CreateEndpoint();

            for (var index = 0; index < 500; index++)
            {
                pipe.SendDatagram(sender, receiver, PayloadFor(index), PayloadBytes);
            }

            pipe.Advance(1);

            var delivered = new HashSet<int>();
            while (pipe.TryReceive(receiver, out _, out var datagram))
            {
                delivered.Add(BitConverter.ToInt32(datagram, 0));
            }

            Assert.That(pipe.DroppedCount, Is.GreaterThan(0),
                "the burst preset must drop packets");
            Assert.That(delivered.Count, Is.EqualTo(500 - pipe.DroppedCount));

            var missing = new List<int>();
            for (var index = 0; index < 500; index++)
            {
                if (!delivered.Contains(index))
                {
                    missing.Add(index);
                }
            }

            var longestRun = 1;
            var currentRun = 1;
            for (var index = 1; index < missing.Count; index++)
            {
                if (missing[index] == missing[index - 1] + 1)
                {
                    currentRun++;
                    longestRun = Math.Max(longestRun, currentRun);
                }
                else
                {
                    currentRun = 1;
                }
            }

            Assert.That(longestRun, Is.GreaterThanOrEqualTo(4),
                "burst loss must drop at least one consecutive run of four packets");
        }

        [Test]
        public void Deterministic_IdenticalSeedProducesIdenticalTrace()
        {
            var pipeA = new VirtualNetworkPipe(ImpairmentProfile.Wan(seed: 99));
            var pipeB = new VirtualNetworkPipe(ImpairmentProfile.Wan(seed: 99));
            var senderA = pipeA.CreateEndpoint();
            var receiverA = pipeA.CreateEndpoint();
            var senderB = pipeB.CreateEndpoint();
            var receiverB = pipeB.CreateEndpoint();

            for (var index = 0; index < 500; index++)
            {
                pipeA.SendDatagram(senderA, receiverA, PayloadFor(index), PayloadBytes);
                pipeB.SendDatagram(senderB, receiverB, PayloadFor(index), PayloadBytes);
            }

            var traceA = new List<byte[]>();
            var traceB = new List<byte[]>();
            for (var step = 0; step < 250; step++)
            {
                pipeA.Advance(2);
                pipeB.Advance(2);
                Drain(pipeA, receiverA, traceA);
                Drain(pipeB, receiverB, traceB);
            }

            Assert.That(pipeA.DroppedCount, Is.EqualTo(pipeB.DroppedCount));
            Assert.That(pipeA.DeliveredCount, Is.EqualTo(pipeB.DeliveredCount));
            Assert.That(traceA.Count, Is.EqualTo(traceB.Count));
            for (var index = 0; index < traceA.Count; index++)
            {
                Assert.That(traceA[index], Is.EqualTo(traceB[index]),
                    $"payload {index} diverged between identical seeds");
            }
        }

        [Test]
        public void Carrier_ReliableDelivery_SurvivesWanLoss()
        {
            var pipe = new VirtualNetworkPipe(ImpairmentProfile.Wan(seed: 7));
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var clientAddress = pipe.CreateEndpoint();
            var server = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var client = OwnDatagramCarrier.CreateClient(pipe, clientAddress, serverAddress, clock);

            try
            {
                server.StartServer();
                var connection = client.ConnectToServer("virtual", 0);
                const int messageCount = 20;
                for (var index = 0; index < messageCount; index++)
                {
                    var payload = new byte[PayloadBytes];
                    payload[0] = (byte)index;
                    payload[1] = 0x5A;
                    Assert.That(
                        client.Send(connection, TransportChannel.Control, payload, 0, PayloadBytes),
                        Is.True,
                        $"message {index} must enter the reliable window");
                }

                var received = new List<byte[]>();
                for (var step = 0; step < 500 && received.Count < messageCount; step++)
                {
                    pipe.Advance(5);
                    clock.Advance(5);
                    server.Pump(clock.NowMs);
                    client.Pump(clock.NowMs);

                    while (server.TryDequeueEvent(out var transportEvent))
                    {
                        if (transportEvent.Type == TransportEventType.Message &&
                            transportEvent.Channel == TransportChannel.Control &&
                            transportEvent.Length == PayloadBytes &&
                            transportEvent.Data[1] == 0x5A)
                        {
                            var copy = new byte[PayloadBytes];
                            Array.Copy(transportEvent.Data, copy, PayloadBytes);
                            received.Add(copy);
                        }
                    }
                }

                Assert.That(received.Count, Is.EqualTo(messageCount),
                    "ARQ must deliver every reliable message through 5% loss and jitter");
                for (var index = 0; index < messageCount; index++)
                {
                    Assert.That(received[index][0], Is.EqualTo((byte)index),
                        "the reliable channel must preserve send order");
                }

                Assert.That(pipe.DroppedCount, Is.GreaterThan(0),
                    "the carrier test must actually exercise a lossy pipe");
            }
            finally
            {
                client.Dispose();
                server.Dispose();
            }
        }

        private static byte[] PayloadFor(int index)
        {
            var payload = new byte[PayloadBytes];
            BitConverter.GetBytes(index).CopyTo(payload, 0);
            return payload;
        }

        private static void Drain(
            VirtualNetworkPipe pipe,
            int receiver,
            List<byte[]> trace)
        {
            while (pipe.TryReceive(receiver, out _, out var datagram))
            {
                trace.Add(datagram);
            }
        }
    }
}
