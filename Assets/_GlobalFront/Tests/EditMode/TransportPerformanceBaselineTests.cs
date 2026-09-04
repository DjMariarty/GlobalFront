using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Snapshot;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Phase 2.5 performance baseline (measurement, not optimization):
    /// packet throughput, allocations, queue depth, retransmissions, CPU
    /// proxy (elapsed), bandwidth and reassembly memory through the full
    /// transport stack under 5% loss. 3000+ entity replication OPTIMIZATION
    /// is Phase 2.6 scope; here the 3000-entity snapshot serves only as a
    /// large opaque payload exercising fragmentation and reassembly bounds.
    /// </summary>
    [TestFixture]
    public sealed class TransportPerformanceBaselineTests
    {
        private const int CommandCount = 2000;
        private const int SnapshotCount = 50;
        private const int SnapshotEntities = 3000;

        [Test]
        public void Baseline_CommandAndSnapshotFlow_UnderLoss()
        {
            var profile = new ImpairmentProfile
            {
                LossProbability = 0.05,
                LatencyMs = 10,
                ReorderJitterMs = 10,
                Seed = 4242
            };
            var rig = TransportTestSupport.CreateRig(profile, capacity: 1);

            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            var config = TransportTestSupport.BuildMatchConfig(client.Player);
            Assert.That(rig.Host.TryStartSessionMatch(rig.Match, config, out _), Is.True);

            var acks = 0;
            var channel = new NetworkCommandChannel(client);
            channel.CommandResultReceived += ack =>
            {
                if (ack.IsAccepted)
                {
                    acks++;
                }
            };

            var largeTick = (ulong)CommandCount + 1;
            var snapshotsReceived = 0;
            var largeDeliveries = new List<byte[]>();
            client.SnapshotReceived += (tick, payload, length) =>
            {
                if (tick == largeTick)
                {
                    // The endpoint hands out reusable buffers: retain a copy.
                    largeDeliveries.Add(payload.AsSpan(0, length).ToArray());
                }
                else
                {
                    snapshotsReceived++;
                }
            };

            // 3000-entity Snapshot Protocol v1 payload (~117 KB), opaque to
            // the transport: exercises fragmentation + reassembly bounds.
            var snapshotPayload = BuildLargeSnapshotPayload(SnapshotEntities);

            // Small snapshots measure unreliable delivery under loss; the
            // large payload measures reassembly (its full delivery is NOT
            // guaranteed on C2 under loss by design).
            var smallSnapshot = new byte[256];
            for (var index = 0; index < smallSnapshot.Length; index++)
            {
                smallSnapshot[index] = (byte)(index & 0xFF);
            }

            var entity = rig.Host.GetAllSnapshots()[0].Entity;
            var allocBefore = System.GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            for (var sequence = 1u; sequence <= CommandCount; sequence++)
            {
                // Respect carrier backpressure (send-side flow control):
                // when the in-flight window is full, pump until it drains.
                // Bounded retries: a dead connection must fail, not hang.
                MatchCommandRejection submit;
                var retries = 0;
                do
                {
                    submit = channel.TrySubmitMove(
                        new CommandHeader(client.Player, sequence, 1, GameCommandType.Move),
                        new[] { entity },
                        new WorldPointMm(5000, 0),
                        new FormationSpec(1, 2000, CardinalFacing.North));
                    if (submit != MatchCommandRejection.None)
                    {
                        rig.Pump(10);
                        client.Pump(rig.Clock.NowMs);
                        retries++;
                        Assert.That(retries, Is.LessThan(600),
                            "backpressure must drain; persistent rejection indicates transport loss");
                    }
                }
                while (submit != MatchCommandRejection.None);

                if (sequence % (CommandCount / SnapshotCount) == 0)
                {
                    rig.ServerTransport.SendSnapshot(
                        client.Session, sequence, smallSnapshot, smallSnapshot.Length);
                }

                // Pacing: ~100 commands/s of virtual time stays inside the
                // 200 pkt/s per-endpoint rate limit including retransmits.
                rig.Pump(10);
                client.Pump(rig.Clock.NowMs);
            }

            var elapsedVirtual = 0L;
            while (acks < CommandCount && elapsedVirtual < 600000)
            {
                rig.Pump(10);
                client.Pump(rig.Clock.NowMs);
                elapsedVirtual += 10;
            }

            watch.Stop();
            var allocAfter = System.GC.GetAllocatedBytesForCurrentThread();

            // Large fragmented C2 delivery check. Under i.i.d. 5% loss a
            // ~100-fragment unreliable message completes with probability
            // ~0.95^100 ≈ 0.6%, so the measurement opens a clean loss
            // window: the baseline proves the snapshot is SENT, fully
            // REASSEMBLED, DELIVERED and ACCEPTED AS LATEST; bandwidth/loss
            // optimization is delta-snapshot territory (Phase 2.6).
            profile.LossProbability = 0;
            Assert.That(
                rig.ServerTransport.SendSnapshot(
                    client.Session, largeTick, snapshotPayload, snapshotPayload.Length),
                Is.True,
                "the large C2 snapshot must be sent");

            var largeElapsed = 0L;
            while (largeDeliveries.Count < 1 && largeElapsed < 10000)
            {
                rig.Pump(10);
                client.Pump(rig.Clock.NowMs);
                largeElapsed += 10;
            }

            profile.LossProbability = 0.05;

            var clientMetrics = client.Carrier.Metrics;
            var serverMetrics = rig.ServerTransport.Metrics;

            TestContext.Out.WriteLine("=== GlobalFront Phase 2.5 transport baseline ===");
            TestContext.Out.WriteLine($"commands delivered: {acks}/{CommandCount} (reliable)");
            TestContext.Out.WriteLine($"snapshots received: {snapshotsReceived}/{SnapshotCount} (unreliable, 5% loss)");
            TestContext.Out.WriteLine($"large C2 snapshot: deliveries={largeDeliveries.Count} size={snapshotPayload.Length} bytes tick={largeTick}");
            TestContext.Out.WriteLine($"virtual time: {rig.Clock.NowMs} ms; wall CPU proxy: {watch.ElapsedMilliseconds} ms");
            TestContext.Out.WriteLine($"throughput: {(CommandCount + SnapshotCount) / System.Math.Max(1, watch.ElapsedMilliseconds / 1000.0):F0} msg/s wall");
            TestContext.Out.WriteLine($"managed allocations: {(allocAfter - allocBefore) / 1024} KB");
            TestContext.Out.WriteLine($"client bytes sent/received: {clientMetrics.BytesSent}/{clientMetrics.BytesReceived}");
            TestContext.Out.WriteLine($"server bytes sent/received: {serverMetrics.BytesSent}/{serverMetrics.BytesReceived}");
            TestContext.Out.WriteLine($"client retransmissions: {clientMetrics.Retransmissions}");
            TestContext.Out.WriteLine($"peak event queue depth: {System.Math.Max(clientMetrics.PeakQueueDepth, serverMetrics.PeakQueueDepth)}");
            TestContext.Out.WriteLine($"peak reassembly memory: client={clientMetrics.ReassemblyBytesPeak} server={serverMetrics.ReassemblyBytesPeak} bytes");
            TestContext.Out.WriteLine($"drops: malformed={serverMetrics.DroppedMalformed} rate={serverMetrics.DroppedRateLimited} window={serverMetrics.DroppedReplayOrWindow} queue={serverMetrics.DroppedQueueOverflow}");

            Assert.That(acks, Is.EqualTo(CommandCount),
                "reliable channel must deliver every command despite 5% loss");
            Assert.That(snapshotsReceived, Is.GreaterThan(SnapshotCount / 2),
                "small unreliable snapshots must mostly arrive under 5% loss");
            Assert.That(largeDeliveries.Count, Is.EqualTo(1),
                "the large fragmented C2 snapshot must be delivered exactly once");
            Assert.That(largeDeliveries[0], Is.EqualTo(snapshotPayload),
                "the large C2 snapshot must reassemble byte-identical (no fake success)");
            Assert.That(largeTick, Is.GreaterThan((ulong)CommandCount),
                "the large snapshot carries the newest SnapshotTick and is accepted as latest");
            Assert.That(clientMetrics.Retransmissions, Is.LessThanOrEqualTo(2000),
                "retransmissions must stay proportional to loss (amplification regression guard)");
            Assert.That(clientMetrics.ReassemblyBytesPeak,
                Is.LessThanOrEqualTo(TransportProtocol.ReassemblyBudgetBytes),
                "client reassembly memory must respect the per-peer budget");
            Assert.That(serverMetrics.ReassemblyBytesPeak,
                Is.LessThanOrEqualTo(TransportProtocol.ReassemblyBudgetBytes),
                "server reassembly memory must respect the per-peer budget");
            Assert.That(serverMetrics.PeakQueueDepth,
                Is.LessThanOrEqualTo(TransportProtocol.MaxQueuedReceiveEvents));
        }

        private static byte[] BuildLargeSnapshotPayload(int entities)
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
                new SnapshotPacketHeader(SnapshotProtocol.Version, 1, (uint)entities),
                snapshots);
        }
    }
}
