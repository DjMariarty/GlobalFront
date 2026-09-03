using System;
using System.Collections.Generic;
using System.Linq;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Server.Replication
{
    /// <summary>
    /// End-to-end and zero-GC evidence for the server replication producers
    /// (Phase 2.6, step 2.6.2, ADR-010).
    ///
    /// The end-to-end tests run a ten-tick simulation through
    /// diff -&gt; history ring -&gt; cumulative merge -&gt; wire codec and replay the
    /// result with a reference applier that mirrors the client behaviour planned
    /// for step 2.6.3. The property under test is the ADR-010 establishing-delta
    /// contract: one merged packet must move a receiver from <c>BaseTick</c> to
    /// <c>TargetTick</c> exactly, even when intermediate ticks were never sent.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationEndToEndTests
    {
        private const int UnitCount = 32;
        private const int WarmupIterations = 200;
        private const int MeasuredIterations = 1000;

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

        // ---------------------------------------------------------------
        // End to end: diff -> ring -> merge -> codec -> replay
        // ---------------------------------------------------------------

        private sealed class Scenario
        {
            public ServerUnitSnapshot[] Baseline;
            public ServerUnitSnapshot[] Final;
            public ReplicationHistoryRing Ring;
        }

        /// <summary>
        /// Ten ticks of churn: movement, damage, an order, an OD-14 target
        /// clear, a retarget, one death, one spawn-and-die pair and one unit that
        /// never changes at all.
        /// </summary>
        private static Scenario RunTenTickScenario()
        {
            var engine = new ServerSnapshotDiffEngine(16, 64, 16);
            var ring = new ReplicationHistoryRing(16, 64, 16);
            var world = new WorldSimulation(engine, ring,
                Unit(1, posX: 0), Unit(2, posX: 10), Unit(3, posX: 20));

            var baseline = world.Snapshot();

            world.Advance(1, () =>
            {
                world.Set(1, Unit(1, posX: 100));
                world.Spawn(Unit(4, owner: 2, posX: 500));
            });
            world.Advance(2, () => world.Set(2, Unit(2, posX: 11, health: 55)));
            world.Advance(3, () => world.Kill(4));
            world.Advance(4, () =>
                world.Set(1, Unit(1, posX: 200, hasMoveTarget: true, moveTargetX: 900, moveTargetZ: 900)));
            world.Advance(5, () => world.Set(1, Unit(1, posX: 300, hasMoveTarget: true,
                moveTargetX: 900, moveTargetZ: 900, attackTarget: 2)));
            world.Advance(6, () => world.Set(1, Unit(1, posX: 400, hasMoveTarget: false)));
            world.Advance(7, () => world.Spawn(Unit(9, owner: 3, posX: 42)));
            world.Advance(8, () => world.Set(2, Unit(2, posX: 12, health: 10, autoAcquire: true)));
            world.Advance(9, () => world.Kill(9));
            world.Advance(10, () => world.Set(1, Unit(1, posX: 500, attackTarget: 0)));

            return new Scenario { Baseline = baseline, Final = world.Snapshot(), Ring = ring };
        }

        [Test]
        public void EndToEnd_MergedEstablishingDelta_ReproducesTheAuthoritativeWorld()
        {
            var scenario = RunTenTickScenario();
            var builder = new ReplicationChangeSetBuilder(64, 256, 64);

            Assert.That(scenario.Ring.Count, Is.EqualTo(10), "one change-set per simulated tick");
            Assert.That(scenario.Ring.TryMergeRange(0, 10, builder), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.AddCount, Is.Zero,
                "units 4 and 9 were born and died inside the window and must disappear");
            Assert.That(merged.RemoveCount, Is.Zero, "no tombstone survives the cancellation");
            Assert.That(merged.UpdateCount, Is.EqualTo(2), "only the two surviving changed units are replicated");

            var unitOne = merged.Updates[0];
            Assert.That(unitOne.Entity.Value, Is.EqualTo(1UL));
            Assert.That(unitOne.DirtyMask,
                Is.EqualTo((byte)(UnitDirtyMask.Position | UnitDirtyMask.HasMoveTarget | UnitDirtyMask.AttackTarget)),
                "ten ticks of unit 1 collapse into one OR-ed mask");
            Assert.That(unitOne.Position, Is.EqualTo(new WorldPointMm(500, 200)), "last position wins");
            Assert.That(unitOne.HasMoveTarget, Is.False, "the OD-14 clear is the last move-target event");
            Assert.That(unitOne.MoveTarget, Is.EqualTo(new WorldPointMm(0, 0)),
                "a cleared target carries no coordinates");
            Assert.That(unitOne.AttackTarget, Is.EqualTo(new EntityId(0)), "last attack target wins");

            var unitTwo = merged.Updates[1];
            Assert.That(unitTwo.Entity.Value, Is.EqualTo(2UL));
            Assert.That(unitTwo.DirtyMask,
                Is.EqualTo((byte)(UnitDirtyMask.Position | UnitDirtyMask.Health | UnitDirtyMask.AutoAcquire)));
            Assert.That(unitTwo.Position, Is.EqualTo(new WorldPointMm(12, 200)));
            Assert.That(unitTwo.CurrentHealth, Is.EqualTo(10));
            Assert.That(unitTwo.AutoAcquireEnemies, Is.True);

            Assert.That(Apply(scenario.Baseline, merged), Is.EqualTo(scenario.Final),
                "replaying the cumulative delta must reproduce the authoritative state exactly");
            Assert.That(scenario.Baseline, Is.Not.EqualTo(scenario.Final),
                "the scenario must actually change the world, otherwise the assertion is vacuous");
        }

        [Test]
        public void EndToEnd_ReceiverResumingMidWindow_Converges()
        {
            var scenario = RunTenTickScenario();
            var builder = new ReplicationChangeSetBuilder(64, 256, 64);

            Assert.That(scenario.Ring.TryMergeRange(0, 5, builder), Is.True);
            var atTickFive = Apply(scenario.Baseline, builder.ChangeSet);

            Assert.That(scenario.Ring.TryMergeRange(5, 10, builder), Is.True,
                "the receiver only confirms tick 5, so ticks 6..10 must be merged");
            var resumed = Apply(atTickFive, builder.ChangeSet);

            Assert.That(resumed, Is.EqualTo(scenario.Final),
                "skipping intermediate ticks is safe: the establishing delta converges");
            Assert.That(atTickFive, Is.Not.EqualTo(scenario.Final),
                "tick 5 is genuinely behind the final state");
        }

        [Test]
        public void EndToEnd_MergedDelta_SurvivesTheWireCodec()
        {
            var scenario = RunTenTickScenario();
            var builder = new ReplicationChangeSetBuilder(64, 256, 64);

            Assert.That(scenario.Ring.TryMergeRange(0, 10, builder), Is.True);
            var merged = builder.ChangeSet;

            var header = DeltaSnapshotHeader.CreateDelta(
                10, 0, DeltaFlags.None, 0,
                (ushort)merged.AddCount, (ushort)merged.UpdateCount, (ushort)merged.RemoveCount);
            var packet = new byte[DeltaSnapshotWireCodec.GetMaxEncodedSize(
                merged.AddCount, merged.UpdateCount, merged.RemoveCount)];

            Assert.That(
                DeltaSnapshotWireCodec.TryEncode(
                    header, merged.Adds, merged.Updates, merged.Removes, packet, out var written),
                Is.EqualTo(DeltaCodecResult.Ok),
                "the merged change-set must be directly encodable");

            var addSink = new DeltaAddRecord[merged.AddCount];
            var updateSink = new DeltaUpdateRecord[merged.UpdateCount];
            var removeSink = new DeltaRemoveRecord[merged.RemoveCount];

            Assert.That(
                DeltaSnapshotWireCodec.TryDecode(
                    packet.AsSpan(0, written), out var decodedHeader, addSink, updateSink, removeSink),
                Is.EqualTo(DeltaCodecResult.Ok));

            Assert.That(decodedHeader.Tick, Is.EqualTo(10UL));
            Assert.That(decodedHeader.BaseTick, Is.Zero);
            Assert.That(updateSink, Is.EqualTo(merged.Updates.ToArray()));

            var received = new ReplicationChangeSet(
                addSink, addSink.Length, updateSink, updateSink.Length, removeSink, removeSink.Length);
            Assert.That(Apply(scenario.Baseline, received), Is.EqualTo(scenario.Final),
                "the decoded packet reproduces the authoritative state");
        }

        [Test]
        public void EndToEnd_LossyReceiver_ReconvergesAfterDroppedTicks()
        {
            var scenario = RunTenTickScenario();
            var builder = new ReplicationChangeSetBuilder(64, 256, 64);

            // The receiver applied ticks 1..2, then lost everything up to tick 8.
            Assert.That(scenario.Ring.TryMergeRange(0, 2, builder), Is.True);
            var applied = Apply(scenario.Baseline, builder.ChangeSet);

            Assert.That(scenario.Ring.TryMergeRange(2, 10, builder), Is.True,
                "the retained window still covers the gap");
            applied = Apply(applied, builder.ChangeSet);

            Assert.That(applied, Is.EqualTo(scenario.Final),
                "a cumulative delta across lost ticks reconverges without a keyframe");
        }

        /// <summary>
        /// Reference applier: the client-side semantics planned for step 2.6.3,
        /// used here to prove the server produces a self-consistent delta.
        /// ADD first, then UPDATE, then REMOVE, matching the wire section order.
        /// </summary>
        private static ServerUnitSnapshot[] Apply(
            ServerUnitSnapshot[] baseState,
            ReplicationChangeSet changeSet)
        {
            var byId = new SortedDictionary<ulong, ServerUnitSnapshot>();
            foreach (var unit in baseState)
            {
                byId[unit.Entity.Value] = unit;
            }

            foreach (var add in changeSet.Adds)
            {
                byId[add.Entity.Value] = new ServerUnitSnapshot(
                    add.Entity, add.Owner, add.Position, add.CurrentHealth,
                    add.HasMoveTarget, add.MoveTarget, add.AttackTarget, add.AutoAcquireEnemies);
            }

            foreach (var update in changeSet.Updates)
            {
                if (!byId.TryGetValue(update.Entity.Value, out var existing))
                {
                    Assert.Fail($"update for unknown entity {update.Entity.Value}: the delta is not establishing");
                }

                byId[update.Entity.Value] = ApplyUpdate(existing, update);
            }

            foreach (var remove in changeSet.Removes)
            {
                byId.Remove(remove.Entity.Value);
            }

            var result = new ServerUnitSnapshot[byId.Count];
            var index = 0;
            foreach (var pair in byId)
            {
                result[index++] = pair.Value;
            }

            return result;
        }

        private static ServerUnitSnapshot ApplyUpdate(ServerUnitSnapshot existing, in DeltaUpdateRecord update)
        {
            var mask = update.DirtyMask;
            var moveTarget = existing.MoveTarget;

            if (HasFlag(mask, UnitDirtyMask.HasMoveTarget) && !update.HasMoveTarget)
            {
                moveTarget = default(WorldPointMm);
            }
            else if (HasFlag(mask, UnitDirtyMask.MoveTarget))
            {
                moveTarget = update.MoveTarget;
            }

            return new ServerUnitSnapshot(
                existing.Entity,
                HasFlag(mask, UnitDirtyMask.Owner) ? update.Owner : existing.Owner,
                HasFlag(mask, UnitDirtyMask.Position) ? update.Position : existing.Position,
                HasFlag(mask, UnitDirtyMask.Health) ? update.CurrentHealth : existing.CurrentHealth,
                HasFlag(mask, UnitDirtyMask.HasMoveTarget) ? update.HasMoveTarget : existing.HasMoveTarget,
                moveTarget,
                HasFlag(mask, UnitDirtyMask.AttackTarget) ? update.AttackTarget : existing.AttackTarget,
                HasFlag(mask, UnitDirtyMask.AutoAcquire)
                    ? update.AutoAcquireEnemies
                    : existing.AutoAcquireEnemies);
        }

        private static bool HasFlag(byte mask, UnitDirtyMask flag) => (mask & (byte)flag) == (byte)flag;

        /// <summary>Minimal authoritative world used to drive the diff engine.</summary>
        private sealed class WorldSimulation
        {
            private readonly List<ServerUnitSnapshot> _units = new List<ServerUnitSnapshot>();
            private readonly ServerSnapshotDiffEngine _engine;
            private readonly ReplicationHistoryRing _ring;

            public WorldSimulation(
                ServerSnapshotDiffEngine engine,
                ReplicationHistoryRing ring,
                params ServerUnitSnapshot[] units)
            {
                _engine = engine;
                _ring = ring;
                _units.AddRange(units);
            }

            /// <summary>Diffs the world before/after the mutation and records the tick.</summary>
            public void Advance(ulong tick, Action mutate)
            {
                var previous = Snapshot();
                mutate();
                var current = Snapshot();

                var result = _engine.Diff(previous, current, out var changeSet);
                Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok), $"the diff of tick {tick} must succeed");
                _ring.RecordTick(tick, in changeSet);
            }

            public void Set(ulong id, ServerUnitSnapshot unit)
            {
                var index = _units.FindIndex(existing => existing.Entity.Value == id);
                Assert.That(index, Is.GreaterThanOrEqualTo(0), $"entity {id} must exist before it is updated");
                _units[index] = unit;
            }

            public void Spawn(ServerUnitSnapshot unit) => _units.Add(unit);

            public void Kill(ulong id)
            {
                var index = _units.FindIndex(existing => existing.Entity.Value == id);
                Assert.That(index, Is.GreaterThanOrEqualTo(0), $"entity {id} must exist before it is removed");
                _units.RemoveAt(index);
            }

            public ServerUnitSnapshot[] Snapshot() =>
                _units.OrderBy(unit => unit.Entity.Value).ToArray();
        }

        // ---------------------------------------------------------------
        // Zero-GC hot path
        // ---------------------------------------------------------------

        [Test]
        public void GcAllocationRecorder_ObservesKnownAllocation_Control()
        {
            // The editor's Mono runtime reports 0 from
            // GC.GetAllocatedBytesForCurrentThread(), so zero-GC evidence uses
            // Unity's GC.Alloc recorder. This control proves the recorder works.
            byte[] ballast = null;

            Assert.That(
                () =>
                {
                    ballast = new byte[4096];
                },
                UnityEngine.TestTools.Constraints.Is.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");

            Assert.That(ballast, Is.Not.Null);
            Assert.That(ballast.Length, Is.EqualTo(4096));
        }

        [Test]
        public void DiffEngine_HotPath_DoesNotAllocate()
        {
            var engine = new ServerSnapshotDiffEngine(16, 64, 16);
            var previous = new ServerUnitSnapshot[UnitCount];
            var current = new ServerUnitSnapshot[UnitCount];
            Fill(previous, 0);
            Fill(current, 1);

            var succeeded = 0;
            var records = 0;

            for (var iteration = 0; iteration < WarmupIterations; iteration++)
            {
                engine.Diff(previous, current, out _);
            }

            // Block-bodied lambda on purpose: the negated constraint only receives
            // the delegate itself when NUnit binds Assert.That(TestDelegate, ...).
            Assert.That(
                () =>
                {
                    // Starts at 1: iteration 0 would rebuild the previous slice and
                    // legitimately produce an empty change-set.
                    for (var iteration = 1; iteration <= MeasuredIterations; iteration++)
                    {
                        Fill(current, iteration);
                        if (engine.Diff(previous, current, out var changeSet) == ReplicationDiffResult.Ok)
                        {
                            succeeded++;
                            records += changeSet.RecordCount;
                        }
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "diffing two world slices must be allocation-free (zero-GC hot path)");

            Assert.That(succeeded, Is.EqualTo(MeasuredIterations));
            Assert.That(records, Is.EqualTo(MeasuredIterations * UnitCount),
                "every unit changed position in every iteration");
        }

        [Test]
        public void HistoryRing_RecordAndMerge_HotPath_DoesNotAllocate()
        {
            var engine = new ServerSnapshotDiffEngine(16, 64, 16);
            var ring = new ReplicationHistoryRing(16, 64, 16);
            var builder = new ReplicationChangeSetBuilder(64, 256, 64);
            var previous = new ServerUnitSnapshot[UnitCount];
            var current = new ServerUnitSnapshot[UnitCount];
            Fill(previous, 0);
            Fill(current, 1);

            var merged = 0;
            var mergedRecords = 0;

            for (var iteration = 1; iteration <= WarmupIterations; iteration++)
            {
                Fill(current, iteration);
                engine.Diff(previous, current, out var changeSet);
                ring.RecordTick((ulong)iteration, in changeSet);
                ring.TryMergeRange((ulong)Math.Max(1, iteration - 100), (ulong)iteration, builder);
            }

            Assert.That(
                () =>
                {
                    for (var iteration = WarmupIterations + 1;
                        iteration <= WarmupIterations + MeasuredIterations;
                        iteration++)
                    {
                        Fill(current, iteration);
                        if (engine.Diff(previous, current, out var changeSet) != ReplicationDiffResult.Ok)
                        {
                            continue;
                        }

                        ring.RecordTick((ulong)iteration, in changeSet);
                        if (ring.TryMergeRange((ulong)(iteration - 100), (ulong)iteration, builder) &&
                            builder.ChangeSet.UpdateCount == UnitCount)
                        {
                            merged++;
                            mergedRecords += builder.ChangeSet.RecordCount;
                        }
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "recording into the ring and merging a 100-tick window must be allocation-free");

            Assert.That(merged, Is.EqualTo(MeasuredIterations), "every iteration produced a full cumulative delta");
            Assert.That(mergedRecords, Is.EqualTo(MeasuredIterations * UnitCount));
            Assert.That(ring.Count, Is.EqualTo(ReplicationHistoryRing.Capacity), "the window stayed bounded");
            Assert.That(ring.OverflowCount, Is.Zero);
        }

        /// <summary>Writes a distinct world slice into a preallocated array.</summary>
        private static void Fill(ServerUnitSnapshot[] slice, int iteration)
        {
            for (var index = 0; index < slice.Length; index++)
            {
                slice[index] = Unit(
                    (ulong)(index + 1),
                    posX: (index * 10) + iteration,
                    health: index % 2 == 0 ? 100 - (iteration % 50) : 100);
            }
        }
    }
}
