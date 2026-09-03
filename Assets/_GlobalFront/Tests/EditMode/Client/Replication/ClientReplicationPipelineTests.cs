using System;
using System.Collections.Generic;
using System.Linq;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Client.Replication
{
    /// <summary>
    /// Cross-assembly pipeline evidence for Phase 2.6: the server producers of
    /// step 2.6.2 (diff engine -&gt; history ring -&gt; cumulative merge), the Core
    /// wire codec of step 2.6.1 and the client receiver of step 2.6.3 must fit
    /// together without an adapter.
    ///
    /// Every test drives a real authoritative world, emits real packets over the
    /// real codec and asserts that the client world table ends up bit-exact with
    /// the server world - including across lost ticks, a catch-up cycle and a
    /// unit that was born and died inside the retained window.
    /// </summary>
    [TestFixture]
    public sealed class ClientReplicationPipelineTests
    {
        private const ushort KeyframeSeq = 1;

        private static ServerUnitSnapshot Unit(
            ulong id,
            byte owner = 1,
            int posX = 100,
            int posZ = 200,
            int health = 100,
            bool hasMoveTarget = false,
            int moveTargetX = 0,
            int moveTargetZ = 0,
            ulong attackTarget = 0,
            bool autoAcquire = false)
        {
            return new ServerUnitSnapshot(
                new EntityId(id),
                new PlayerId(owner),
                new WorldPointMm(posX, posZ),
                health,
                hasMoveTarget,
                new WorldPointMm(moveTargetX, moveTargetZ),
                new EntityId(attackTarget),
                autoAcquire);
        }

        /// <summary>Minimal authoritative world used to drive the diff engine.</summary>
        private sealed class ServerWorld
        {
            private readonly List<ServerUnitSnapshot> _units = new List<ServerUnitSnapshot>();

            public ServerWorld(params ServerUnitSnapshot[] units) => _units.AddRange(units);

            public void Set(ServerUnitSnapshot unit)
            {
                var index = _units.FindIndex(existing => existing.Entity.Value == unit.Entity.Value);
                Assert.That(index, Is.GreaterThanOrEqualTo(0), $"entity {unit.Entity.Value} must exist");
                _units[index] = unit;
            }

            public void Spawn(ServerUnitSnapshot unit) => _units.Add(unit);

            public void Kill(ulong id)
            {
                var index = _units.FindIndex(existing => existing.Entity.Value == id);
                Assert.That(index, Is.GreaterThanOrEqualTo(0), $"entity {id} must exist");
                _units.RemoveAt(index);
            }

            public ServerUnitSnapshot[] Snapshot() => _units.OrderBy(unit => unit.Entity.Value).ToArray();
        }

        /// <summary>
        /// Server-side replication endpoint of one client: it owns the diff
        /// engine, the retained history and the encode buffer, and it builds each
        /// Establishing Delta from the tick the client last confirmed.
        /// </summary>
        private sealed class ServerEndpoint
        {
            private readonly ServerSnapshotDiffEngine _engine = new ServerSnapshotDiffEngine(16, 64, 16);
            private readonly ReplicationHistoryRing _ring = new ReplicationHistoryRing(16, 64, 16);
            private readonly ReplicationChangeSetBuilder _builder = new ReplicationChangeSetBuilder(64, 256, 64);

            public void Record(ulong tick, ServerUnitSnapshot[] previous, ServerUnitSnapshot[] current)
            {
                Assert.That(_engine.Diff(previous, current, out var changeSet),
                    Is.EqualTo(ReplicationDiffResult.Ok), $"the diff of tick {tick} must succeed");
                _ring.RecordTick(tick, in changeSet);
            }

            /// <summary>
            /// Encodes the cumulative Establishing Delta of <c>(baseTick, targetTick]</c>,
            /// exactly as the server answers a confirmed <c>LastAppliedTick</c>.
            /// </summary>
            public byte[] Encode(ulong baseTick, ulong targetTick)
            {
                Assert.That(_ring.TryMergeRange(baseTick, targetTick, _builder), Is.True,
                    $"the retained window must cover ({baseTick}, {targetTick}]");
                var merged = _builder.ChangeSet;
                var header = DeltaSnapshotHeader.CreateDelta(
                    targetTick, baseTick, DeltaFlags.None, 0,
                    (ushort)merged.AddCount, (ushort)merged.UpdateCount, (ushort)merged.RemoveCount);
                var packet = new byte[DeltaSnapshotWireCodec.GetMaxEncodedSize(
                    merged.AddCount, merged.UpdateCount, merged.RemoveCount)];
                Assert.That(
                    DeltaSnapshotWireCodec.TryEncode(
                        header, merged.Adds, merged.Updates, merged.Removes, packet, out var written),
                    Is.EqualTo(DeltaCodecResult.Ok));

                return Trim(packet, written);
            }

            public byte[] EncodeKeyframe(ulong tick, ServerUnitSnapshot[] units)
            {
                var adds = new DeltaAddRecord[units.Length];
                for (var index = 0; index < units.Length; index++)
                {
                    adds[index] = ServerSnapshotDiffEngine.ToAddRecord(units[index]);
                }

                var header = DeltaSnapshotHeader.CreateDelta(
                    tick, 0, DeltaFlags.None, 0, (ushort)adds.Length, 0, 0);
                var packet = new byte[DeltaSnapshotWireCodec.GetMaxEncodedSize(adds.Length, 0, 0)];
                Assert.That(
                    DeltaSnapshotWireCodec.TryEncode(
                        header, adds, ReadOnlySpan<DeltaUpdateRecord>.Empty,
                        ReadOnlySpan<DeltaRemoveRecord>.Empty, packet, out var written),
                    Is.EqualTo(DeltaCodecResult.Ok));
                return Trim(packet, written);
            }
        }

        /// <summary>The decoder rejects trailing bytes, so packets are trimmed.</summary>
        private static byte[] Trim(byte[] packet, int written)
        {
            var trimmed = new byte[written];
            Array.Copy(packet, trimmed, written);
            return trimmed;
        }

        /// <summary>Client-side decode sinks, reused for every packet.</summary>
        private sealed class ClientSinks
        {
            public readonly DeltaAddRecord[] Adds = new DeltaAddRecord[64];
            public readonly DeltaUpdateRecord[] Updates = new DeltaUpdateRecord[256];
            public readonly DeltaRemoveRecord[] Removes = new DeltaRemoveRecord[64];

            public DeltaSnapshotHeader Decode(byte[] packet)
            {
                Assert.That(
                    DeltaSnapshotWireCodec.TryDecode(packet, out var header, Adds, Updates, Removes),
                    Is.EqualTo(DeltaCodecResult.Ok),
                    "the client must be able to decode every packet the server emits");
                return header;
            }
        }

        [Test]
        public void Pipeline_KeyframeThenDeltaStream_ReproducesTheAuthoritativeWorld()
        {
            var world = new ServerWorld(Unit(1, posX: 0), Unit(2, posX: 10), Unit(3, posX: 20));
            var server = new ServerEndpoint();
            var sinks = new ClientSinks();
            var receiver = new ClientReplicationReceiver(64);

            var baseline = world.Snapshot();
            receiver.ReceiveKeyframe(0, KeyframeSeq, 0, DecodeAdds(sinks, server.EncodeKeyframe(0, baseline)));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            AssertWorldsMatch(baseline, receiver);

            var nowMs = 100L;
            for (ulong tick = 1; tick <= 10; tick++)
            {
                var previous = world.Snapshot();
                Mutate(world, tick);
                var current = world.Snapshot();
                server.Record(tick, previous, current);

                var header = sinks.Decode(server.Encode(receiver.LastAppliedTick, tick));
                var outcome = receiver.ReceiveDelta(
                    nowMs, header, KeyframeSeq,
                    sinks.Adds.AsSpan(0, header.AddCount),
                    sinks.Updates.AsSpan(0, header.UpdateCount),
                    sinks.Removes.AsSpan(0, header.RemoveCount));

                Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.Applied), $"tick {tick}");
                Assert.That(receiver.LastAppliedTick, Is.EqualTo(tick));
                AssertWorldsMatch(current, receiver);
                nowMs += 100;
            }

            Assert.That(receiver.AppliedDeltaCount, Is.EqualTo(10));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
        }

        [Test]
        public void Pipeline_LostTicks_ReconvergeThroughOneCumulativeDelta()
        {
            var world = new ServerWorld(Unit(1, posX: 0), Unit(2, posX: 10));
            var server = new ServerEndpoint();
            var sinks = new ClientSinks();
            var receiver = new ClientReplicationReceiver(64);

            var baseline = world.Snapshot();
            receiver.ReceiveKeyframe(0, KeyframeSeq, 0, DecodeAdds(sinks, server.EncodeKeyframe(0, baseline)));

            // The client receives ticks 1 and 2, then loses 3..7 and hears again at 8.
            for (ulong tick = 1; tick <= 8; tick++)
            {
                var previous = world.Snapshot();
                Mutate(world, tick);
                var current = world.Snapshot();
                server.Record(tick, previous, current);

                if (tick > 2 && tick < 8)
                {
                    continue;
                }

                var header = sinks.Decode(server.Encode(receiver.LastAppliedTick, tick));
                Assert.That(
                    receiver.ReceiveDelta((long)tick * 100, header, KeyframeSeq,
                        sinks.Adds.AsSpan(0, header.AddCount),
                        sinks.Updates.AsSpan(0, header.UpdateCount),
                        sinks.Removes.AsSpan(0, header.RemoveCount)),
                    Is.EqualTo(ClientReplicationOutcome.Applied),
                    $"the establishing delta of tick {tick} covers everything the client missed");
                AssertWorldsMatch(current, receiver);
            }

            Assert.That(receiver.LastAppliedTick, Is.EqualTo(8UL));
            Assert.That(receiver.PendingRequestCount, Is.Zero, "no repair traffic was needed at all");
        }

        [Test]
        public void Pipeline_DependencyGap_ClientResumesAndTheServerAnswers()
        {
            var world = new ServerWorld(Unit(1, posX: 0), Unit(2, posX: 10));
            var server = new ServerEndpoint();
            var sinks = new ClientSinks();
            var receiver = new ClientReplicationReceiver(64);

            var baseline = world.Snapshot();
            receiver.ReceiveKeyframe(0, KeyframeSeq, 0, DecodeAdds(sinks, server.EncodeKeyframe(0, baseline)));

            ServerUnitSnapshot[] current = baseline;
            for (ulong tick = 1; tick <= 6; tick++)
            {
                var previous = world.Snapshot();
                Mutate(world, tick);
                current = world.Snapshot();
                server.Record(tick, previous, current);
            }

            // The client only ever confirmed the keyframe, but the server (having
            // lost the acks) bases the next delta on tick 4.
            var gapped = sinks.Decode(server.Encode(4, 6));
            Assert.That(
                receiver.ReceiveDelta(1000, gapped, KeyframeSeq,
                    sinks.Adds.AsSpan(0, gapped.AddCount),
                    sinks.Updates.AsSpan(0, gapped.UpdateCount),
                    sinks.Removes.AsSpan(0, gapped.RemoveCount)),
                Is.EqualTo(ClientReplicationOutcome.CatchUpRequested));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.CatchingUp));
            Assert.That(receiver.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.DeltaResume));
            Assert.That(request.LastAppliedTick, Is.Zero);

            // The server answers DeltaResume(0) with the cumulative delta of (0, 6].
            var cumulative = sinks.Decode(server.Encode(receiver.LastAppliedTick, 6));
            Assert.That(
                receiver.ReceiveDelta(1400, cumulative, KeyframeSeq,
                    sinks.Adds.AsSpan(0, cumulative.AddCount),
                    sinks.Updates.AsSpan(0, cumulative.UpdateCount),
                    sinks.Removes.AsSpan(0, cumulative.RemoveCount)),
                Is.EqualTo(ClientReplicationOutcome.Applied));

            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(6UL));
            AssertWorldsMatch(current, receiver);
        }

        [Test]
        public void Pipeline_BaseBeyondTheRetainedWindow_ForcesAKeyframe()
        {
            var world = new ServerWorld(Unit(1, posX: 0));
            var server = new ServerEndpoint();
            var sinks = new ClientSinks();
            var receiver = new ClientReplicationReceiver(64);

            var baseline = world.Snapshot();
            receiver.ReceiveKeyframe(0, KeyframeSeq, 0, DecodeAdds(sinks, server.EncodeKeyframe(0, baseline)));

            // A delta that depends on a tick 200 change-sets ahead of the client:
            // no cumulative packet can cover that, so the client must re-base.
            var header = DeltaSnapshotHeader.CreateDelta(400, 200, DeltaFlags.None, 0, 0, 0, 0);
            Assert.That(
                receiver.ReceiveDelta(1000, header, KeyframeSeq,
                    ReadOnlySpan<DeltaAddRecord>.Empty,
                    ReadOnlySpan<DeltaUpdateRecord>.Empty,
                    ReadOnlySpan<DeltaRemoveRecord>.Empty),
                Is.EqualTo(ClientReplicationOutcome.RebaseRequested));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(receiver.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));

            // A fresh keyframe re-bases the client and restores the stream.
            var fresh = world.Snapshot();
            receiver.ReceiveKeyframe(
                2000, (ushort)(KeyframeSeq + 1), 400,
                DecodeAdds(sinks, server.EncodeKeyframe(400, fresh)));

            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(receiver.CurrentKeyframeSeq, Is.EqualTo(KeyframeSeq + 1));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(400UL));
            AssertWorldsMatch(fresh, receiver);

            // The stream resumes on the new base: an empty Establishing Delta of
            // (400, 401] is a valid "nothing changed" tick.
            var resumed = sinks.Decode(server.Encode(400, 401));
            Assert.That(
                receiver.ReceiveDelta(3000, resumed, (ushort)(KeyframeSeq + 1),
                    sinks.Adds.AsSpan(0, resumed.AddCount),
                    sinks.Updates.AsSpan(0, resumed.UpdateCount),
                    sinks.Removes.AsSpan(0, resumed.RemoveCount)),
                Is.EqualTo(ClientReplicationOutcome.Applied));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(401UL));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
        }

        [Test]
        public void Pipeline_UnitBornAndDiedInsideTheWindow_LeavesNoGhost()
        {
            var world = new ServerWorld(Unit(1, posX: 0));
            var server = new ServerEndpoint();
            var sinks = new ClientSinks();
            var receiver = new ClientReplicationReceiver(64);

            var baseline = world.Snapshot();
            receiver.ReceiveKeyframe(0, KeyframeSeq, 0, DecodeAdds(sinks, server.EncodeKeyframe(0, baseline)));

            // Tick 1 spawns unit 9, tick 2 destroys it again; the client only
            // hears about tick 2 through the cumulative delta of (0, 2].
            var previous = world.Snapshot();
            world.Spawn(Unit(9, owner: 3, posX: 500));
            server.Record(1, previous, world.Snapshot());

            previous = world.Snapshot();
            world.Kill(9);
            var current = world.Snapshot();
            server.Record(2, previous, current);

            var header = sinks.Decode(server.Encode(0, 2));
            Assert.That(header.AddCount, Is.Zero, "the ADD and the tombstone cancel out in the merge");
            Assert.That(header.RemoveCount, Is.Zero);
            Assert.That(
                receiver.ReceiveDelta(1000, header, KeyframeSeq,
                    sinks.Adds.AsSpan(0, header.AddCount),
                    sinks.Updates.AsSpan(0, header.UpdateCount),
                    sinks.Removes.AsSpan(0, header.RemoveCount)),
                Is.EqualTo(ClientReplicationOutcome.Applied));

            Assert.That(receiver.World.Contains(new EntityId(9)), Is.False, "no ghost unit on the client");
            AssertWorldsMatch(current, receiver);
        }

        [Test]
        public void Pipeline_FeedbackCarriesTheConfirmedTickBackToTheServer()
        {
            var world = new ServerWorld(Unit(1, posX: 0), Unit(2, posX: 10));
            var server = new ServerEndpoint();
            var sinks = new ClientSinks();
            var receiver = new ClientReplicationReceiver(64);

            var baseline = world.Snapshot();
            receiver.ReceiveKeyframe(0, KeyframeSeq, 0, DecodeAdds(sinks, server.EncodeKeyframe(0, baseline)));

            var buffer = new byte[SnapshotAckCodec.SizeBytes];
            var nowMs = 100L;
            for (ulong tick = 1; tick <= 5; tick++)
            {
                var previous = world.Snapshot();
                Mutate(world, tick);
                var current = world.Snapshot();
                server.Record(tick, previous, current);

                var header = sinks.Decode(server.Encode(receiver.LastAppliedTick, tick));
                receiver.ReceiveDelta(nowMs, header, KeyframeSeq,
                    sinks.Adds.AsSpan(0, header.AddCount),
                    sinks.Updates.AsSpan(0, header.UpdateCount),
                    sinks.Removes.AsSpan(0, header.RemoveCount));

                Assert.That(receiver.TryTakeAck(nowMs, out var ack), Is.True, $"tick {tick} must be acked");
                Assert.That(SnapshotAckCodec.TryEncode(ack, buffer, out var written),
                    Is.EqualTo(SnapshotAckCodecResult.Ok));
                Assert.That(SnapshotAckCodec.TryDecode(buffer.AsSpan(0, written), out var decoded),
                    Is.EqualTo(SnapshotAckCodecResult.Ok));
                Assert.That(decoded.LastAppliedTick, Is.EqualTo(tick),
                    "the server reads BaseTick for the next delta out of this ack");
                Assert.That(decoded.BaseKeyframeTick, Is.Zero, "the keyframe base of this match is tick 0");
                Assert.That(decoded.HasGap, Is.False);
                nowMs += 100;
            }
        }

        /// <summary>One mutation per tick: movement, damage, orders, spawn and death.</summary>
        private static void Mutate(ServerWorld world, ulong tick)
        {
            switch (tick)
            {
                case 1:
                    world.Set(Unit(1, posX: 100));
                    break;
                case 2:
                    world.Set(Unit(2, posX: 11, health: 55));
                    break;
                case 3:
                    world.Spawn(Unit(4, owner: 2, posX: 500));
                    break;
                case 4:
                    world.Set(Unit(1, posX: 200, hasMoveTarget: true, moveTargetX: 900, moveTargetZ: 900));
                    break;
                case 5:
                    world.Set(Unit(1, posX: 300, hasMoveTarget: true,
                        moveTargetX: 900, moveTargetZ: 900, attackTarget: 2));
                    break;
                case 6:
                    world.Set(Unit(1, posX: 400));
                    break;
                case 7:
                    world.Kill(4);
                    break;
                case 8:
                    world.Set(Unit(2, posX: 12, health: 10, autoAcquire: true));
                    break;
                case 9:
                    world.Set(Unit(1, posX: 450, attackTarget: 0));
                    break;
                case 10:
                    world.Set(Unit(2, posX: 13, health: 5));
                    break;
            }
        }

        /// <summary>Decodes a keyframe packet back into ADD records.</summary>
        private static DeltaAddRecord[] DecodeAdds(ClientSinks sinks, byte[] packet)
        {
            var header = sinks.Decode(packet);
            var adds = new DeltaAddRecord[header.AddCount];
            sinks.Adds.AsSpan(0, header.AddCount).CopyTo(adds);
            return adds;
        }

        /// <summary>Bit-exact comparison of the authoritative and the client world.</summary>
        private static void AssertWorldsMatch(ServerUnitSnapshot[] serverUnits, ClientReplicationReceiver receiver)
        {
            Assert.That(receiver.World.LiveCount, Is.EqualTo(serverUnits.Length),
                "the client table must hold exactly the replicated units");

            foreach (var unit in serverUnits)
            {
                Assert.That(receiver.TryGetUnit(unit.Entity, out var client), Is.True,
                    $"entity {unit.Entity.Value} must exist on the client");
                Assert.That(client.Owner, Is.EqualTo(unit.Owner), $"entity {unit.Entity.Value} owner");
                Assert.That(client.Position, Is.EqualTo(unit.Position), $"entity {unit.Entity.Value} position");
                Assert.That(client.Health, Is.EqualTo(unit.CurrentHealth), $"entity {unit.Entity.Value} health");
                Assert.That(client.HasMoveTarget, Is.EqualTo(unit.HasMoveTarget),
                    $"entity {unit.Entity.Value} hasMoveTarget");
                Assert.That(client.MoveTarget, Is.EqualTo(unit.MoveTarget),
                    $"entity {unit.Entity.Value} moveTarget");
                Assert.That(client.AttackTarget, Is.EqualTo(unit.AttackTarget),
                    $"entity {unit.Entity.Value} attackTarget");
                Assert.That(client.AutoAcquire, Is.EqualTo(unit.AutoAcquireEnemies),
                    $"entity {unit.Entity.Value} autoAcquire");
            }
        }
    }
}
