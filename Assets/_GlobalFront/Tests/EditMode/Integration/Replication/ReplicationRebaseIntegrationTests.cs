using System;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Integration.Replication
{
    /// <summary>
    /// Step 2.6.4 re-baseline evidence (ADR-010): when the client falls behind
    /// the retained 120-tick history window, the guard sends it to REBASING,
    /// the emitter answers the SnapshotRequest with a freshly sliced keyframe
    /// and the stream resumes. Also covers the server-side fallback: a
    /// DeltaResume whose base the ring has already evicted must degrade to a
    /// keyframe instead of an unusable cumulative delta.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationRebaseIntegrationTests
    {
        [Test]
        public void Rebase_WindowExceeded_KeyframeRestoresStreaming()
        {
            var profile = new ImpairmentProfile { LatencyMs = 2, Seed = 11 };
            // Two clients keep the match non-terminal (a one-sided roster would
            // end the match instantly and freeze the simulation).
            var world = ReplicationIntegrationWorld.Create(
                profile, ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client = world.Clients[0];
            var entityA = client.FirstEntityOf(new PlayerId(1));
            var entityB = client.FirstEntityOf(new PlayerId(2));
            client.SubmitMoveTo(
                new PlayerId(1), entityA, new WorldPointMm(500_000, 0), world.Rig.Host.CurrentTick + 1);
            client.SubmitMoveTo(
                new PlayerId(2), entityB, new WorldPointMm(-500_000, 0), world.Rig.Host.CurrentTick + 1);

            // Establish the stream.
            for (var tick = 0; tick < 10; tick++)
            {
                world.PumpTick();
            }

            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            var streamingLastApplied = client.Receiver.LastAppliedTick;
            Assert.That(streamingLastApplied, Is.GreaterThan(0UL));

            // Full loss for more than the 120-tick history window. The pump
            // steps are compressed so the virtual clock stays far below the
            // transport idle timeout: the protocol window is tick-based, the
            // transport liveness is millisecond-based, and this test isolates
            // the former.
            world.Profile.LossProbability = 1.0;
            const int blackoutTicks = 130;
            for (var tick = 0; tick < blackoutTicks; tick++)
            {
                world.PumpTick(2);
            }

            Assert.That(world.Rig.Host.CurrentTick,
                Is.GreaterThanOrEqualTo(streamingLastApplied + 120),
                "the blackout must push the server beyond the retained window");

            // Connectivity returns: the next surviving delta depends on a base
            // the client cannot reach, so the receiver must go to REBASING and
            // request a fresh keyframe over the reliable control channel.
            world.Profile.LossProbability = 0.0;
            Assert.That(
                world.PumpUntilCaughtUp(client, budgetMs: 30000, keepTicking: true), Is.True,
                "the keyframe re-baseline must restore streaming");

            world.AssertWorldsMatch(client);
            Assert.That(client.Receiver.RebaseCount, Is.GreaterThanOrEqualTo(1),
                "the window escape must be handled by a re-baseline");
            Assert.That(client.Bridge.AssembledKeyframeCount, Is.GreaterThanOrEqualTo(2),
                "the client must have assembled the initial keyframe and the re-baseline keyframe");
            Assert.That(world.Emitter.RequestCount, Is.GreaterThanOrEqualTo(1),
                "the emitter must have received the SnapshotRequest");
            Assert.That(world.Emitter.EmittedKeyframeCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(client.Receiver.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
            Assert.That(client.Receiver.IsWorldUsable, Is.True);
        }

        [Test]
        public void Rebase_DeltaResumeWithEvictedBase_FallsBackToKeyframe()
        {
            var profile = new ImpairmentProfile { LatencyMs = 2, Seed = 13 };
            // Two clients keep the match non-terminal (see the test above).
            var world = ReplicationIntegrationWorld.Create(
                profile, ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client = world.Clients[0];
            var entityA = client.FirstEntityOf(new PlayerId(1));
            var entityB = client.FirstEntityOf(new PlayerId(2));
            client.SubmitMoveTo(
                new PlayerId(1), entityA, new WorldPointMm(500_000, 0), world.Rig.Host.CurrentTick + 1);
            client.SubmitMoveTo(
                new PlayerId(2), entityB, new WorldPointMm(-500_000, 0), world.Rig.Host.CurrentTick + 1);

            // Stream well past the retention window so tick 1 is evicted.
            for (var tick = 0; tick < 140; tick++)
            {
                world.PumpTick();
            }

            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(world.Emitter.History.HasEvicted, Is.True,
                "the history ring must have evicted early ticks by now");

            var keyframesBefore = world.Emitter.EmittedKeyframeCount;

            // Inject a DeltaResume for a base the ring has already evicted —
            // exactly what a stalled client would ask for. The emitter must
            // detect the impossibility and answer with a fresh keyframe.
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.DeltaResume,
                attempt: 1,
                lastAppliedTick: 1,
                baseKeyframeTick: 1,
                keyframeTick: 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes];
            Assert.That(
                ReplicationRequestCodec.TryEncode(request, buffer, out var written),
                Is.EqualTo(ReplicationRequestCodecResult.Ok));
            Assert.That(
                client.Endpoint.TrySendReplicationFeedback(
                    TransportMessageType.ReplicationRequest, buffer, written), Is.True);
            world.Pump();

            // The fallback keyframe is queued and paced out on the next ticks.
            world.PumpTick();
            world.PumpTick();
            Assert.That(
                world.PumpUntilCaughtUp(client, budgetMs: 20000, keepTicking: true), Is.True,
                "the keyframe fallback must re-baseline the client");

            world.AssertWorldsMatch(client);
            Assert.That(world.Emitter.KeyframeFallbackCount, Is.GreaterThanOrEqualTo(1),
                "the evicted base must be reported as a keyframe fallback");
            Assert.That(world.Emitter.EmittedKeyframeCount,
                Is.GreaterThanOrEqualTo(keyframesBefore + 1));
            Assert.That(client.Receiver.KeyframeCount, Is.GreaterThanOrEqualTo(2),
                "the client must have installed the re-baseline keyframe");
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
        }

        [Test]
        public void Rebase_ConsecutiveChecksumMismatches_TriggersRebasingAndEmitsSnapshotRequest()
        {
            var receiver = new ClientReplicationReceiver(64);
            const ushort keyframeSeq = 1;
            var initialUnits = new[]
            {
                new DeltaAddRecord(new EntityId(1), new PlayerId(1), new WorldPointMm(100, 200), 100, false, default, default, false),
                new DeltaAddRecord(new EntityId(2), new PlayerId(2), new WorldPointMm(300, 400), 100, false, default, default, false)
            };
            var outcome = receiver.ReceiveKeyframe(0, keyframeSeq, 100, initialUnits);
            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));

            // Drain the baseline ack
            Assert.That(receiver.TryTakeAck(0, out _), Is.True);

            // Verify strict zero-GC allocations on ComputeStateChecksum
            Assert.That(
                () => { receiver.World.ComputeStateChecksum(); },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "ComputeStateChecksum must be strictly allocation-free");

            var expectedChecksum = receiver.World.ComputeStateChecksum();
            Assert.That(expectedChecksum, Is.Not.EqualTo(0u));
            Assert.That(receiver.ConsecutiveChecksumMismatches, Is.EqualTo(0));
            Assert.That(receiver.ChecksumMismatchCount, Is.EqualTo(0));

            // 1. Delta with matching checksum -> matches, keeps consecutive mismatches at 0
            var matchingHeader = DeltaSnapshotHeader.CreateDelta(
                101, 100, DeltaFlags.HasChecksum, expectedChecksum, 0, 0, 0, keyframeRef: keyframeSeq);
            var outcome1 = receiver.ReceiveDelta(
                50, in matchingHeader, keyframeSeq,
                ReadOnlySpan<DeltaAddRecord>.Empty,
                ReadOnlySpan<DeltaUpdateRecord>.Empty,
                ReadOnlySpan<DeltaRemoveRecord>.Empty);
            Assert.That(outcome1, Is.EqualTo(ClientReplicationOutcome.Applied));
            Assert.That(receiver.ConsecutiveChecksumMismatches, Is.EqualTo(0));
            Assert.That(receiver.ChecksumMismatchCount, Is.EqualTo(0));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));

            // 2. Delta with corrupted checksum (1st mismatch) -> counter becomes 1, still Streaming, no request
            var corruptChecksum = expectedChecksum ^ 0xDEADBEEFu;
            var corruptHeader1 = DeltaSnapshotHeader.CreateDelta(
                102, 101, DeltaFlags.HasChecksum, corruptChecksum, 0, 0, 0, keyframeRef: keyframeSeq);
            var outcome2 = receiver.ReceiveDelta(
                100, in corruptHeader1, keyframeSeq,
                ReadOnlySpan<DeltaAddRecord>.Empty,
                ReadOnlySpan<DeltaUpdateRecord>.Empty,
                ReadOnlySpan<DeltaRemoveRecord>.Empty);
            Assert.That(outcome2, Is.EqualTo(ClientReplicationOutcome.Applied));
            Assert.That(receiver.ConsecutiveChecksumMismatches, Is.EqualTo(1));
            Assert.That(receiver.ChecksumMismatchCount, Is.EqualTo(1));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(receiver.TryTakeRequest(out _), Is.False, "1st mismatch must not trigger repair");

            // 3. Delta with corrupted checksum (2nd consecutive mismatch) -> triggers Rebasing & SnapshotRequest
            var corruptHeader2 = DeltaSnapshotHeader.CreateDelta(
                103, 102, DeltaFlags.HasChecksum, corruptChecksum, 0, 0, 0, keyframeRef: keyframeSeq);
            var outcome3 = receiver.ReceiveDelta(
                150, in corruptHeader2, keyframeSeq,
                ReadOnlySpan<DeltaAddRecord>.Empty,
                ReadOnlySpan<DeltaUpdateRecord>.Empty,
                ReadOnlySpan<DeltaRemoveRecord>.Empty);
            Assert.That(outcome3, Is.EqualTo(ClientReplicationOutcome.RebaseRequested));
            Assert.That(receiver.ConsecutiveChecksumMismatches, Is.EqualTo(2));
            Assert.That(receiver.ChecksumMismatchCount, Is.EqualTo(2));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(receiver.IsWorldUsable, Is.False, "desynced world must not be usable by presentation");
            Assert.That(receiver.RebaseCount, Is.GreaterThanOrEqualTo(1));

            // Must have queued a SnapshotRequest for a full baseline
            Assert.That(receiver.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));
            Assert.That(request.LastAppliedTick, Is.EqualTo(103UL));
        }
    }
}
