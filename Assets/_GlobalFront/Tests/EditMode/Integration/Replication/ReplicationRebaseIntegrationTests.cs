using System;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

#pragma warning disable CS0067

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

        // ------------------------------------------------------------------
        // N-03: client sinks must cover the server's merged-delta limits
        // ------------------------------------------------------------------

        [Test]
        public void Delta_With512Removes_DecodesThroughDefaultBridgeWithoutBufferTooSmall()
        {
            const int removeCount = 512;
            var receiver = new ClientReplicationReceiver(removeCount * 2);
            var bridge = new ClientTransportReplicationBridge(new FakeReplicationUplink(), receiver);

            var initial = new DeltaAddRecord[removeCount];
            var removes = new DeltaRemoveRecord[removeCount];
            for (var index = 0; index < removeCount; index++)
            {
                var entity = new EntityId((ulong)(index + 1));
                initial[index] = new DeltaAddRecord(
                    entity,
                    new PlayerId(1),
                    new WorldPointMm(index * 100, 0),
                    100,
                    false,
                    default,
                    default,
                    false);
                removes[index] = new DeltaRemoveRecord(entity, DeltaRemoveCause.Destroyed);
            }

            Assert.That(
                receiver.ReceiveKeyframe(0, keyframeSeq: 1, tick: 100, initial),
                Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(receiver.TryTakeAck(0, out _), Is.True, "drain the baseline ack");

            var header = DeltaSnapshotHeader.CreateDelta(
                101, 100, DeltaFlags.None, 0, 0, 0, removeCount, keyframeRef: 1);
            var payload = new byte[DeltaSnapshotProtocol.MaxPacketBytes];
            Assert.That(
                DeltaSnapshotWireCodec.TryEncode(
                    header,
                    ReadOnlySpan<DeltaAddRecord>.Empty,
                    ReadOnlySpan<DeltaUpdateRecord>.Empty,
                    removes,
                    payload,
                    out var written),
                Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(written, Is.LessThanOrEqualTo(payload.Length));

            bridge.HandleSnapshotPayload(101, payload, written, nowMs: 0);

            Assert.That(
                bridge.BufferTooSmallCount, Is.Zero,
                "the default remove sink must hold a legal 512-remove establishing delta " +
                "(ServerReplicationEmitterConfig.DefaultRemoveCapacity)");
            Assert.That(
                bridge.MalformedPayloadCount, Is.Zero,
                "a sink shortfall is a local capacity problem, not a corrupt packet");
            Assert.That(bridge.ForwardedDeltaCount, Is.EqualTo(1));
            Assert.That(receiver.AppliedRemoveCount, Is.EqualTo(removeCount));
            Assert.That(receiver.World.LiveCount, Is.Zero);
        }

        [Test]
        public void Delta_SinkShortfallAndCorruption_AreCountedApart()
        {
            var receiver = new ClientReplicationReceiver(16);
            var bridge = new ClientTransportReplicationBridge(
                new FakeReplicationUplink(),
                receiver,
                new ClientTransportReplicationBridgeConfig(
                    maxKeyframeUnits: ClientTransportReplicationBridgeConfig.DefaultMaxKeyframeUnits,
                    maxParts: ClientTransportReplicationBridgeConfig.DefaultMaxParts,
                    maxSlicePayloadBytes: ClientTransportReplicationBridgeConfig.DefaultMaxSlicePayloadBytes,
                    addSink: 16,
                    updateSink: 16,
                    removeSink: 1));

            var removes = new[]
            {
                new DeltaRemoveRecord(new EntityId(1), DeltaRemoveCause.Destroyed),
                new DeltaRemoveRecord(new EntityId(2), DeltaRemoveCause.Destroyed)
            };
            var header = DeltaSnapshotHeader.CreateDelta(
                101, 100, DeltaFlags.None, 0, 0, 0, (ushort)removes.Length, keyframeRef: 1);
            var payload = new byte[DeltaSnapshotProtocol.MaxPacketBytes];
            Assert.That(
                DeltaSnapshotWireCodec.TryEncode(
                    header,
                    ReadOnlySpan<DeltaAddRecord>.Empty,
                    ReadOnlySpan<DeltaUpdateRecord>.Empty,
                    removes,
                    payload,
                    out var written),
                Is.EqualTo(DeltaCodecResult.Ok));

            bridge.HandleSnapshotPayload(101, payload, written, nowMs: 0);
            Assert.That(bridge.BufferTooSmallCount, Is.EqualTo(1),
                "two removes cannot fit a one-record sink");
            Assert.That(bridge.MalformedPayloadCount, Is.Zero);
            Assert.That(bridge.ForwardedDeltaCount, Is.Zero);

            // Same packet, corrupt protocol version: structural damage, not a
            // shortfall, and the two counters must not collapse into one.
            payload[1] = 0xFF;
            bridge.HandleSnapshotPayload(102, payload, written, nowMs: 0);
            Assert.That(bridge.BufferTooSmallCount, Is.EqualTo(1));
            Assert.That(bridge.MalformedPayloadCount, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------
        // F-14/F-15: checksum covers owner and auto-acquire, in canonical order
        // ------------------------------------------------------------------

        [Test]
        public void StateChecksum_ServerAndClient_EntityOrderAgreement()
        {
            const int unitCount = 50;
            var server = new MatchServer();
            var records = new DeltaAddRecord[unitCount];
            for (var index = 0; index < unitCount; index++)
            {
                // 37 is coprime with 50, so this is a deterministic permutation
                // of 1..50: ids arrive out of order on the client and in the
                // server's spawn history.
                var id = (ulong)(index * 37 % unitCount + 1);
                var owner = new PlayerId((byte)(index % 2 + 1));
                var position = new WorldPointMm((int)id * 1000, (int)id * 2000);
                var autoAcquire = index % 3 == 0;
                server.SpawnUnitWithEntity(
                    new EntityId(id),
                    owner,
                    TransportTestSupport.StandardStats,
                    position,
                    speedMmPerTick: 350,
                    autoAcquire: autoAcquire);
                records[index] = new DeltaAddRecord(
                    new EntityId(id),
                    owner,
                    position,
                    100,
                    false,
                    default,
                    default,
                    autoAcquire);
            }

            // The authoritative capture walks canonical ascending entity-id order.
            var capture = new ServerUnitSnapshot[unitCount];
            Assert.That(server.CopySnapshots(capture), Is.EqualTo(unitCount));
            for (var index = 1; index < unitCount; index++)
            {
                Assert.That(
                    capture[index].Entity.Value,
                    Is.GreaterThan(capture[index - 1].Entity.Value),
                    "the server capture must be in ascending entity-id order");
            }

            // The client table is filled in shuffled spawn order, so its physical
            // slots are not canonical: the digest can only match if the in-place
            // checksum sort actually runs.
            var client = BuildChecksumClientWorld(records);
            Assert.That(
                IsSlotOrderAscending(client, unitCount), Is.False,
                "the client slots must start in non-canonical order, or this test " +
                "does not exercise the checksum sort");

            var serverChecksum = CaptureServerChecksum(server);
            Assert.That(
                client.ComputeStateChecksum(), Is.EqualTo(serverChecksum),
                "server capture order and the client heapsort must produce one digest");

            // Coverage witnesses: with every other hashed field identical, a
            // divergence in auto-acquire or owner must break parity. These fail
            // if either field is dropped from ComputeStateChecksum.
            Assert.That(
                BuildChecksumClientWorld(records, flipAutoAcquireAt: 0).ComputeStateChecksum(),
                Is.Not.EqualTo(serverChecksum),
                "auto-acquire must be part of the state checksum (F-14)");
            Assert.That(
                BuildChecksumClientWorld(records, flipOwnerAt: 1).ComputeStateChecksum(),
                Is.Not.EqualTo(serverChecksum),
                "the unit owner must be part of the state checksum (F-14)");
        }

        /// <summary>
        /// Drives a real emitter over a capturing transport until the 1 Hz
        /// cadence puts a checksum on a delta, and returns that server digest.
        /// </summary>
        private static uint CaptureServerChecksum(MatchServer server)
        {
            var transport = new ChecksumCapturingTransport();
            var emitter = new ServerReplicationEmitter(
                new MatchServerSnapshotSource(server),
                transport,
                ServerReplicationEmitterConfig.Default);

            transport.RaiseAttached();
            emitter.OnTickCompleted(1);
            transport.RaiseSnapshotRequest();

            for (var tick = 2UL; tick <= 64UL; tick++)
            {
                emitter.OnTickCompleted(tick);
                if (transport.TryTakeChecksumDelta(out var captured))
                {
                    Assert.That(emitter.StateChecksumCount, Is.GreaterThan(0));
                    return captured.StateChecksum;
                }
            }

            Assert.Fail("the emitter never sent a delta carrying a state checksum");
            return 0;
        }

        private static ClientReplicationWorld BuildChecksumClientWorld(
            DeltaAddRecord[] records,
            int flipAutoAcquireAt = -1,
            int flipOwnerAt = -1)
        {
            var world = new ClientReplicationWorld(records.Length * 2);
            for (var index = 0; index < records.Length; index++)
            {
                var record = records[index];
                if (index == flipAutoAcquireAt)
                {
                    record = new DeltaAddRecord(
                        record.Entity,
                        record.Owner,
                        record.Position,
                        record.CurrentHealth,
                        record.HasMoveTarget,
                        record.MoveTarget,
                        record.AttackTarget,
                        !record.AutoAcquireEnemies);
                }

                if (index == flipOwnerAt)
                {
                    record = new DeltaAddRecord(
                        record.Entity,
                        new PlayerId((byte)(record.Owner.Value == 1 ? 2 : 1)),
                        record.Position,
                        record.CurrentHealth,
                        record.HasMoveTarget,
                        record.MoveTarget,
                        record.AttackTarget,
                        record.AutoAcquireEnemies);
                }

                Assert.That(
                    world.ApplyAdd(in record), Is.EqualTo(ClientWorldApplyResult.Ok),
                    $"record {index} must be accepted");
            }

            return world;
        }

        private static bool IsSlotOrderAscending(ClientReplicationWorld world, int count)
        {
            var previous = 0UL;
            var ascending = true;
            for (var slot = 0; slot < count; slot++)
            {
                Assert.That(world.TryGetSlotState(slot, out var state), Is.True);
                if (slot > 0 && state.Entity.Value < previous)
                {
                    ascending = false;
                }

                previous = state.Entity.Value;
            }

            return ascending;
        }

        /// <summary>Fake transport that captures the first delta bearing a checksum.</summary>
        private sealed class ChecksumCapturingTransport : IReplicationTransport
        {
            private static readonly SessionId ClientSession = new SessionId(new Guid(
                0x11121314, 0x1516, 0x1718,
                0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20));

            private DeltaSnapshotHeader _checksumDelta;
            private bool _hasChecksumDelta;

            public event Action<SessionId, MatchId, PlayerId> SessionAttached;
            public event Action<SessionId, MatchId, PlayerId> SessionReattached;
            public event Action<SessionId> PostSessionReattached;
            public event Action<SessionId, TransportDisconnectReason> SessionDetached;
            public event Action<ReplicationFeedbackMessage> FeedbackReceived;

            public void RaiseAttached() =>
                SessionAttached?.Invoke(ClientSession, default, new PlayerId(1));

            public void RaiseSnapshotRequest()
            {
                var request = new ReplicationRequestWire(
                    ReplicationRequestWireKind.SnapshotRequest, attempt: 1, 0, 0, 0);
                var buffer = new byte[ReplicationRequestCodec.SizeBytes];
                Assert.That(
                    ReplicationRequestCodec.TryEncode(request, buffer, out var written),
                    Is.EqualTo(ReplicationRequestCodecResult.Ok));
                FeedbackReceived?.Invoke(new ReplicationFeedbackMessage(
                    ClientSession,
                    TransportMessageType.ReplicationRequest,
                    buffer,
                    0,
                    written));
            }

            public bool TryTakeChecksumDelta(out DeltaSnapshotHeader header)
            {
                header = _checksumDelta;
                return _hasChecksumDelta;
            }

            public bool SendToSession(SessionId session, ulong envelopeTick, byte[] buffer, int length)
            {
                if (buffer != null &&
                    length >= DeltaSnapshotProtocol.HeaderSizeBytes &&
                    buffer[0] == DeltaSnapshotProtocol.MessageTypeDelta &&
                    DeltaSnapshotWireCodec.TryDecodeHeader(
                        buffer.AsSpan(0, length), out var header) == DeltaCodecResult.Ok &&
                    header.HasChecksum)
                {
                    _checksumDelta = header;
                    _hasChecksumDelta = true;
                }

                return true;
            }
        }
    }
}
