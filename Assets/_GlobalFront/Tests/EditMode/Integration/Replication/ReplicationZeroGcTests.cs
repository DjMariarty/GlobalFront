using System;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server.Replication;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Integration.Replication
{
    /// <summary>
    /// Zero-GC evidence for the step 2.6.4 hot paths (ADR-010): the emitter's
    /// per-tick pipeline (capture → diff → ring → merge → encode → send) and
    /// the bridge's per-payload pipeline (decode → assembly → apply → ack)
    /// must run without touching the GC heap.
    ///
    /// Scope note: the editor's Mono runtime reports 0 from
    /// GC.GetAllocatedBytesForCurrentThread(), so zero-GC evidence uses
    /// Unity's GC.Alloc recorder (the established project pattern). The
    /// transport carrier is substituted with non-allocating fakes: per-datagram
    /// receive buffers are owned by the Phase 2.5 carrier, which the
    /// replication layer deliberately does not extend.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationZeroGcTests
    {
        private const int WarmupIterations = 200;
        private const int MeasuredIterations = 1000;

        [Test]
        public void GcAllocationRecorder_ObservesKnownAllocation_Control()
        {
            // The recorder must be proven live before it can prove absence.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                UnityEngine.TestTools.Constraints.Is.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");
            Assert.That(ballast, Is.Not.Null);
        }

        [Test]
        public void Emitter_TickPipeline_DoesNotAllocate()
        {
            var source = new FakeSnapshotSource(128);
            var transport = new FakeReplicationTransport();
            var emitter = new ServerReplicationEmitter(
                source, transport, ServerReplicationEmitterConfig.Default);
            transport.AttachOneClient();
            // The receiver FSM opens its baseline episode with a SnapshotRequest;
            // the emitter is request-driven and streams only after serving it.
            transport.DeliverSnapshotRequest();

            // Warmup: the lazy keyframe staging buffer, the keyframe itself and
            // the baseline bookkeeping leave the measured window.
            var tick = 0UL;
            for (var iteration = 0; iteration < WarmupIterations; iteration++)
            {
                source.Mutate(iteration);
                emitter.OnTickCompleted(++tick);
            }

            Assert.That(transport.DeltasSent, Is.GreaterThan(0), "the warmup must stream deltas");
            Assert.That(transport.SlicesSent, Is.GreaterThan(0), "the warmup must stream the keyframe");
            Assert.That(emitter.SnapshotShortfallCount, Is.EqualTo(0));
            Assert.That(emitter.KeyframeFallbackCount, Is.EqualTo(0));

            var deltasBefore = transport.DeltasSent;
            var ackBuffer = new byte[SnapshotAckCodec.SizeBytes];
            var ack = SnapshotAck.CreateProgress(tick, 1);
            Assert.That(
                SnapshotAckCodec.TryEncode(ack, ackBuffer, out var ackBytes),
                Is.EqualTo(SnapshotAckCodecResult.Ok));

            // Block-bodied lambda on purpose: the negated constraint only
            // receives the delegate itself when NUnit binds Assert.That.
            Assert.That(
                () =>
                {
                    for (var iteration = 0; iteration < MeasuredIterations; iteration++)
                    {
                        source.Mutate(iteration);
                        emitter.OnTickCompleted(++tick);
                        if (iteration % 7 == 0)
                        {
                            ackBuffer[2] = (byte)tick;
                            ackBuffer[3] = (byte)(tick >> 8);
                            transport.DeliverAck(ackBuffer);
                        }
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "the emitter tick pipeline must be allocation-free (zero-GC hot path)");

            Assert.That(transport.DeltasSent - deltasBefore,
                Is.GreaterThanOrEqualTo(MeasuredIterations),
                "every measured tick must have streamed a delta");
            Assert.That(emitter.AckCount, Is.GreaterThan(0),
                "the measured window must have decoded acks");
            Assert.That(emitter.SnapshotShortfallCount, Is.EqualTo(0));
            Assert.That(emitter.KeyframeFallbackCount, Is.EqualTo(0));
            Assert.That(emitter.SendFailureCount, Is.EqualTo(0));
        }

        [Test]
        public void Bridge_PayloadPipeline_DoesNotAllocate()
        {
            var uplink = new FakeReplicationUplink();
            var receiver = new ClientReplicationReceiver(ClientReplicationWorld.DefaultCapacity);
            var bridge = new ClientTransportReplicationBridge(uplink, receiver);

            // One keyframe slice installing a 64-unit baseline, encoded once.
            const int keyframeUnits = 64;
            var baseline = new DeltaAddRecord[keyframeUnits];
            for (var index = 0; index < keyframeUnits; index++)
            {
                baseline[index] = new DeltaAddRecord(
                    new EntityId((ulong)(index + 1)),
                    new PlayerId(1),
                    new WorldPointMm(index * 1000, 0),
                    100,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false);
            }

            var keyframeBuffer = new byte[KeyframeSliceCodec.GetSliceSize(keyframeUnits)];
            var keyframeHeader = new KeyframeSliceHeader(
                0, 1, 0, 1, keyframeUnits, keyframeUnits);
            Assert.That(
                KeyframeSliceCodec.TryEncodeSlice(keyframeHeader, baseline, keyframeBuffer, out var keyframeLength),
                Is.EqualTo(KeyframeSliceCodecResult.Ok));

            // The reusable delta payload: one UPDATE per unit, positions
            // rewritten per iteration before encoding.
            var updates = new DeltaUpdateRecord[keyframeUnits];
            for (var index = 0; index < keyframeUnits; index++)
            {
                updates[index] = new DeltaUpdateRecord(
                    new EntityId((ulong)(index + 1)),
                    (byte)UnitDirtyMask.Position,
                    new PlayerId(1),
                    new WorldPointMm(0, 0),
                    100,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false);
            }

            var deltaBuffer = new byte[DeltaSnapshotProtocol.MaxPacketBytes];

            // Warmup: assembly, baseline installation and the first deltas.
            var tick = 0UL;
            var nowMs = 0L;
            var warmupDeltas = 0;
            for (var iteration = 0; iteration < WarmupIterations; iteration++)
            {
                if (iteration == 0)
                {
                    uplink.Deliver(0, keyframeBuffer);
                    Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
                }

                tick++;
                nowMs += 50;
                warmupDeltas += EncodeAndFeedDelta(
                    bridge, updates, deltaBuffer, tick, nowMs);
            }

            Assert.That(bridge.AssembledKeyframeCount, Is.EqualTo(1));
            Assert.That(warmupDeltas, Is.EqualTo(WarmupIterations),
                "every warmup delta must decode and apply");
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(tick));

            var acksBefore = uplink.AcksSent;

            // Block-bodied lambda on purpose: the negated constraint only
            // receives the delegate itself when NUnit binds Assert.That.
            Assert.That(
                () =>
                {
                    for (var iteration = 0; iteration < MeasuredIterations; iteration++)
                    {
                        tick++;
                        nowMs += 50;
                        EncodeAndFeedDelta(bridge, updates, deltaBuffer, tick, nowMs);
                        bridge.Pump(nowMs);
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "the bridge payload pipeline must be allocation-free (zero-GC hot path)");

            Assert.That(receiver.LastAppliedTick, Is.EqualTo(tick),
                "every measured delta must have been applied");
            Assert.That(uplink.AcksSent - acksBefore, Is.GreaterThan(0),
                "the measured window must have drained acknowledgements");
            Assert.That(bridge.MalformedPayloadCount, Is.EqualTo(0));
            Assert.That(receiver.InconsistentApplyCount, Is.EqualTo(0));
            Assert.That(receiver.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
        }

        /// <summary>
        /// Encodes one establishing delta into the reusable buffer and feeds it
        /// through the bridge. Returns 1 when the bridge forwarded it, 0 when
        /// it dropped the payload, -1 when the encode failed. Assertion-free on
        /// purpose: the measured zero-GC window must not create NUnit
        /// constraints.
        /// </summary>
        private static int EncodeAndFeedDelta(
            ClientTransportReplicationBridge bridge,
            DeltaUpdateRecord[] updates,
            byte[] buffer,
            ulong tick,
            long nowMs)
        {
            for (var index = 0; index < updates.Length; index++)
            {
                updates[index] = new DeltaUpdateRecord(
                    updates[index].Entity,
                    (byte)UnitDirtyMask.Position,
                    updates[index].Owner,
                    new WorldPointMm((int)tick + index, index),
                    updates[index].CurrentHealth,
                    false,
                    new WorldPointMm(0, 0),
                    updates[index].AttackTarget,
                    updates[index].AutoAcquireEnemies);
            }

            var header = DeltaSnapshotHeader.CreateDelta(
                tick,
                tick - 1,
                DeltaFlags.None,
                0,
                0,
                (ushort)updates.Length,
                0);
            if (DeltaSnapshotWireCodec.TryEncode(
                    header,
                    ReadOnlySpan<DeltaAddRecord>.Empty,
                    updates,
                    ReadOnlySpan<DeltaRemoveRecord>.Empty,
                    buffer,
                    out var written) != DeltaCodecResult.Ok)
            {
                return -1;
            }

            var before = bridge.ForwardedDeltaCount;
            bridge.HandleSnapshotPayload(tick, buffer, written, nowMs);
            return bridge.ForwardedDeltaCount - before;
        }
    }
}
