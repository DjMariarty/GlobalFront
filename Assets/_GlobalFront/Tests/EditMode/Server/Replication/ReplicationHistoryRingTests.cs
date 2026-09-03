using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Server.Replication
{
    /// <summary>
    /// Isolated EditMode coverage of the retained replication history
    /// (Phase 2.6, step 2.6.2, ADR-010): the 120-tick window, cyclic eviction,
    /// per-tick budgets, and the cumulative merge of <c>(baseTick, targetTick]</c>
    /// including the re-baseline trigger when the base falls out of the window.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationHistoryRingTests
    {
        private static DeltaUpdateRecord Update(ulong id, UnitDirtyMask mask, int posX = 0, int posZ = 0,
            int health = 0, bool hasMoveTarget = false, int moveTargetX = 0, int moveTargetZ = 0)
        {
            return new DeltaUpdateRecord(
                new EntityId(id),
                (byte)mask,
                default(PlayerId),
                new WorldPointMm(posX, posZ),
                health,
                hasMoveTarget,
                new WorldPointMm(moveTargetX, moveTargetZ),
                default(EntityId),
                false);
        }

        private static DeltaAddRecord Add(ulong id, int posX = 0, int health = 100)
        {
            return new DeltaAddRecord(
                new EntityId(id),
                new PlayerId(1),
                new WorldPointMm(posX, 0),
                health,
                false,
                default(WorldPointMm),
                default(EntityId),
                false);
        }

        private static DeltaRemoveRecord Remove(ulong id, DeltaRemoveCause cause = DeltaRemoveCause.Destroyed) =>
            new DeltaRemoveRecord(new EntityId(id), cause);

        /// <summary>
        /// One reusable source buffer: the ring must copy out of it, so writing
        /// the next tick over the same storage is part of the contract under test.
        /// </summary>
        private ReplicationChangeSetBuffer _source;

        [SetUp]
        public void SetUp() => _source = new ReplicationChangeSetBuffer(8, 16, 8);

        private ReplicationChangeSet HealthTick(ulong id, int health)
        {
            _source.Updates[0] = Update(id, UnitDirtyMask.Health, health: health);
            return _source.AsChangeSet(0, 1, 0);
        }

        private ReplicationChangeSet PositionTick(ulong id, int posX)
        {
            _source.Updates[0] = Update(id, UnitDirtyMask.Position, posX: posX);
            return _source.AsChangeSet(0, 1, 0);
        }

        private ReplicationChangeSet MixedTick(DeltaAddRecord[] adds, DeltaUpdateRecord[] updates,
            DeltaRemoveRecord[] removes)
        {
            for (var index = 0; index < adds.Length; index++)
            {
                _source.Adds[index] = adds[index];
            }

            for (var index = 0; index < updates.Length; index++)
            {
                _source.Updates[index] = updates[index];
            }

            for (var index = 0; index < removes.Length; index++)
            {
                _source.Removes[index] = removes[index];
            }

            return _source.AsChangeSet(adds.Length, updates.Length, removes.Length);
        }

        // ---------------------------------------------------------------
        // Window contract
        // ---------------------------------------------------------------

        [Test]
        public void Capacity_IsTheAdr010HistoryWindow()
        {
            Assert.That(ReplicationHistoryRing.Capacity, Is.EqualTo(120),
                "ADR-010 fixes RetainedDeltaHistoryWindow = Tombstone Horizon = 120 ticks (6 s at 20 Hz)");
        }

        [Test]
        public void FreshRing_IsEmptyAndServesEmptyMerges()
        {
            var ring = new ReplicationHistoryRing(4, 4, 4);
            var builder = new ReplicationChangeSetBuilder(8, 8, 8);

            Assert.That(ring.Count, Is.Zero);
            Assert.That(ring.OldestTick, Is.Zero);
            Assert.That(ring.NewestTick, Is.Zero);
            Assert.That(ring.HasEvicted, Is.False);
            Assert.That(ring.EvictedThroughTick, Is.Zero);
            Assert.That(ring.OverflowCount, Is.Zero);
            Assert.That(ring.IgnoredRecordCount, Is.Zero);

            Assert.That(ring.TryGetChangeSet(1, out _), Is.False);
            Assert.That(ring.TryMergeRange(0, 100, builder), Is.True,
                "an empty history still yields a valid (empty) establishing delta");
            Assert.That(builder.ChangeSet.IsEmpty, Is.True);
        }

        [Test]
        public void RecordThenGet_ReturnsTheRecordedChangeSet()
        {
            var ring = new ReplicationHistoryRing(4, 4, 4);

            ring.RecordTick(10, HealthTick(1, 42));

            Assert.That(ring.Count, Is.EqualTo(1));
            Assert.That(ring.OldestTick, Is.EqualTo(10UL));
            Assert.That(ring.NewestTick, Is.EqualTo(10UL));
            Assert.That(ring.TryGetChangeSet(10, out var changeSet), Is.True);
            Assert.That(changeSet.UpdateCount, Is.EqualTo(1));
            Assert.That(changeSet.Updates[0].Entity.Value, Is.EqualTo(1UL));
            Assert.That(changeSet.Updates[0].CurrentHealth, Is.EqualTo(42));
        }

        [Test]
        public void RecordCopiesRecords_ReusingTheSourceBufferDoesNotCorruptHistory()
        {
            var ring = new ReplicationHistoryRing(4, 4, 4);

            ring.RecordTick(10, HealthTick(1, 42));
            ring.RecordTick(20, HealthTick(1, 43));
            ring.RecordTick(30, HealthTick(1, 44));

            Assert.That(ring.TryGetChangeSet(10, out var first), Is.True);
            Assert.That(ring.TryGetChangeSet(20, out var second), Is.True);
            Assert.That(ring.TryGetChangeSet(30, out var third), Is.True);

            Assert.That(first.Updates[0].CurrentHealth, Is.EqualTo(42), "tick 10 kept its own copy");
            Assert.That(second.Updates[0].CurrentHealth, Is.EqualTo(43), "tick 20 kept its own copy");
            Assert.That(third.Updates[0].CurrentHealth, Is.EqualTo(44));
            Assert.That(first.UpdateBuffer, Is.Not.SameAs(second.UpdateBuffer),
                "every retained tick owns a distinct preallocated slot");
        }

        [Test]
        public void RecordAcceptsEmptyChangeSets()
        {
            var ring = new ReplicationHistoryRing(4, 4, 4);
            var builder = new ReplicationChangeSetBuilder(8, 8, 8);

            ring.RecordTick(5, ReplicationChangeSet.Empty);

            Assert.That(ring.Count, Is.EqualTo(1), "an idle tick still occupies the window");
            Assert.That(ring.TryGetChangeSet(5, out var changeSet), Is.True);
            Assert.That(changeSet.IsEmpty, Is.True);
            Assert.That(ring.TryMergeRange(0, 5, builder), Is.True);
            Assert.That(builder.ChangeSet.IsEmpty, Is.True);
        }

        [Test]
        public void TryGetChangeSet_ReturnsFalseForTicksThatWereNeverRecorded()
        {
            var ring = new ReplicationHistoryRing(4, 4, 4);

            ring.RecordTick(10, HealthTick(1, 1));
            ring.RecordTick(30, HealthTick(1, 2));

            Assert.That(ring.TryGetChangeSet(20, out var changeSet), Is.False,
                "a tick inside the window that was never recorded has no change-set");
            Assert.That(changeSet.IsEmpty, Is.True);
            Assert.That(ring.TryGetChangeSet(0, out _), Is.False);
            Assert.That(ring.TryGetChangeSet(31, out _), Is.False);
        }

        [Test]
        public void RecordIgnoresStaleAndDuplicateTicks()
        {
            var ring = new ReplicationHistoryRing(4, 4, 4);

            ring.RecordTick(10, HealthTick(1, 10));
            ring.RecordTick(5, HealthTick(2, 5));
            ring.RecordTick(10, HealthTick(3, 11));

            Assert.That(ring.Count, Is.EqualTo(1), "the ring is a forward-only history");
            Assert.That(ring.IgnoredRecordCount, Is.EqualTo(2));
            Assert.That(ring.NewestTick, Is.EqualTo(10UL));
            Assert.That(ring.TryGetChangeSet(5, out _), Is.False, "a stale tick was never stored");
            Assert.That(ring.TryGetChangeSet(10, out var stored), Is.True);
            Assert.That(stored.Updates[0].Entity.Value, Is.EqualTo(1UL), "the first record was not overwritten");
        }

        // ---------------------------------------------------------------
        // Eviction
        // ---------------------------------------------------------------

        [Test]
        public void Ring_EvictsTheOldestTickOnceCapacityIsExceeded()
        {
            var ring = new ReplicationHistoryRing(2, 2, 2);

            for (ulong tick = 1; tick <= ReplicationHistoryRing.Capacity; tick++)
            {
                ring.RecordTick(tick, HealthTick(tick, (int)tick));
            }

            Assert.That(ring.Count, Is.EqualTo(ReplicationHistoryRing.Capacity));
            Assert.That(ring.OldestTick, Is.EqualTo(1UL));
            Assert.That(ring.NewestTick, Is.EqualTo(120UL));
            Assert.That(ring.HasEvicted, Is.False, "nothing was dropped while the window is exactly full");

            ring.RecordTick(121, HealthTick(121, 121));

            Assert.That(ring.Count, Is.EqualTo(ReplicationHistoryRing.Capacity), "the window stays bounded");
            Assert.That(ring.OldestTick, Is.EqualTo(2UL));
            Assert.That(ring.NewestTick, Is.EqualTo(121UL));
            Assert.That(ring.HasEvicted, Is.True);
            Assert.That(ring.EvictedThroughTick, Is.EqualTo(1UL));
            Assert.That(ring.TryGetChangeSet(1, out _), Is.False, "the evicted tick is gone");
            Assert.That(ring.TryGetChangeSet(2, out var oldest), Is.True);
            Assert.That(oldest.Updates[0].CurrentHealth, Is.EqualTo(2), "the oldest retained tick is intact");
            Assert.That(ring.TryGetChangeSet(121, out var newest), Is.True);
            Assert.That(newest.Updates[0].CurrentHealth, Is.EqualTo(121));
        }

        [Test]
        public void Ring_SurvivesMultipleWraps()
        {
            var ring = new ReplicationHistoryRing(2, 2, 2);

            for (ulong tick = 1; tick <= 300; tick++)
            {
                ring.RecordTick(tick, HealthTick(tick, (int)tick));
            }

            Assert.That(ring.Count, Is.EqualTo(ReplicationHistoryRing.Capacity));
            Assert.That(ring.OldestTick, Is.EqualTo(181UL));
            Assert.That(ring.NewestTick, Is.EqualTo(300UL));
            Assert.That(ring.EvictedThroughTick, Is.EqualTo(180UL));
            Assert.That(ring.TryGetChangeSet(180, out _), Is.False);
            Assert.That(ring.TryGetChangeSet(181, out var oldest), Is.True);
            Assert.That(oldest.Updates[0].CurrentHealth, Is.EqualTo(181));

            // Every retained tick still resolves to its own content after two wraps.
            for (ulong tick = 181; tick <= 300; tick++)
            {
                Assert.That(ring.TryGetChangeSet(tick, out var changeSet), Is.True, $"tick {tick}");
                Assert.That(changeSet.Updates[0].CurrentHealth, Is.EqualTo((int)tick), $"tick {tick} content");
            }
        }

        [Test]
        public void Ring_ReusesEvictedSlotsWithoutAllocating()
        {
            var ring = new ReplicationHistoryRing(2, 2, 2);

            for (ulong tick = 1; tick <= 1000; tick++)
            {
                ring.RecordTick(tick, PositionTick(7, (int)(tick % 500)));
            }

            Assert.That(ring.Count, Is.EqualTo(ReplicationHistoryRing.Capacity));
            Assert.That(ring.OldestTick, Is.EqualTo(881UL));
            Assert.That(ring.NewestTick, Is.EqualTo(1000UL));
            Assert.That(ring.TryGetChangeSet(1000, out var newest), Is.True);
            Assert.That(newest.Updates[0].Position.X, Is.EqualTo(0), "1000 % 500");
            Assert.That(ring.TryGetChangeSet(999, out var previous), Is.True);
            Assert.That(previous.Updates[0].Position.X, Is.EqualTo(499), "999 % 500");
        }

        // ---------------------------------------------------------------
        // Per-tick budget
        // ---------------------------------------------------------------

        [Test]
        public void Ring_FlagsATickWhoseChangeSetDoesNotFit()
        {
            var ring = new ReplicationHistoryRing(1, 1, 1);
            var builder = new ReplicationChangeSetBuilder(8, 8, 8);

            ring.RecordTick(1, MixedTick(
                new DeltaAddRecord[0],
                new[] { Update(1, UnitDirtyMask.Health, health: 1), Update(2, UnitDirtyMask.Health, health: 2) },
                new DeltaRemoveRecord[0]));

            Assert.That(ring.OverflowCount, Is.EqualTo(1));
            Assert.That(ring.Count, Is.EqualTo(1), "the tick is still part of the window");
            Assert.That(ring.TryGetChangeSet(1, out _), Is.False,
                "a truncated tick must never be replayed as a delta");
            Assert.That(ring.TryMergeRange(0, 1, builder), Is.False,
                "a truncated tick inside the interval forces a re-baseline");
        }

        [Test]
        public void Ring_ExposesItsPerTickBudgets()
        {
            var ring = new ReplicationHistoryRing(3, 9, 5);

            Assert.That(ring.MaxAddsPerTick, Is.EqualTo(3));
            Assert.That(ring.MaxUpdatesPerTick, Is.EqualTo(9));
            Assert.That(ring.MaxRemovesPerTick, Is.EqualTo(5));
            Assert.That(ReplicationHistoryRing.DefaultMaxUpdatesPerTick,
                Is.EqualTo(ServerSnapshotDiffEngine.DefaultUpdateCapacity),
                "the ring budget defaults to what the diff engine can produce");

            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ReplicationHistoryRing(-1, 1, 1));
        }

        [Test]
        public void Ring_DiffEngineOutputAlwaysFitsTheDefaultBudgets()
        {
            var engine = new ServerSnapshotDiffEngine();
            var ring = new ReplicationHistoryRing();

            var previous = new ServerUnitSnapshot[0];
            var current = new ServerUnitSnapshot[8];
            for (var index = 0; index < current.Length; index++)
            {
                current[index] = new ServerUnitSnapshot(
                    new EntityId((ulong)(index + 1)), new PlayerId(1), new WorldPointMm(index * 10, 0),
                    100, false, default(WorldPointMm), default(EntityId), false);
            }

            Assert.That(engine.Diff(previous, current, out var changeSet), Is.EqualTo(ReplicationDiffResult.Ok));
            ring.RecordTick(1, in changeSet);

            Assert.That(ring.OverflowCount, Is.Zero);
            Assert.That(ring.TryGetChangeSet(1, out var stored), Is.True);
            Assert.That(stored.AddCount, Is.EqualTo(8));
        }

        // ---------------------------------------------------------------
        // Merge range
        // ---------------------------------------------------------------

        [Test]
        public void MergeRange_MergesFiveToTenTicks_WithLastWinsPerField()
        {
            var ring = new ReplicationHistoryRing(8, 16, 8);
            var builder = new ReplicationChangeSetBuilder(32, 64, 32);

            for (ulong tick = 1; tick <= 10; tick++)
            {
                ring.RecordTick(tick, PositionTick(1, (int)(tick * 100)));
            }

            Assert.That(ring.TryMergeRange(0, 10, builder), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.UpdateCount, Is.EqualTo(1), "ten ticks of one entity collapse into one record");
            Assert.That(merged.Updates[0].DirtyMask, Is.EqualTo((byte)UnitDirtyMask.Position));
            Assert.That(merged.Updates[0].Position.X, Is.EqualTo(1000), "the last position of the interval wins");

            // A five-tick sub-window merges the same way.
            Assert.That(ring.TryMergeRange(5, 10, builder), Is.True);
            Assert.That(builder.ChangeSet.UpdateCount, Is.EqualTo(1));
            Assert.That(builder.ChangeSet.Updates[0].Position.X, Is.EqualTo(1000));
        }

        [Test]
        public void MergeRange_ExcludesTheBaseTickAndIncludesTheTargetTick()
        {
            var ring = new ReplicationHistoryRing(8, 16, 8);
            var builder = new ReplicationChangeSetBuilder(32, 64, 32);

            ring.RecordTick(10, MixedTick(new DeltaAddRecord[0],
                new[] { Update(1, UnitDirtyMask.Health, health: 10) }, new[] { Remove(2) }));
            ring.RecordTick(20, MixedTick(new DeltaAddRecord[0],
                new[] { Update(1, UnitDirtyMask.Health, health: 20) }, NoRemoves()));
            ring.RecordTick(30, MixedTick(new DeltaAddRecord[0],
                new[] { Update(1, UnitDirtyMask.Health, health: 30) }, NoRemoves()));

            Assert.That(ring.TryMergeRange(10, 30, builder), Is.True);
            Assert.That(builder.ChangeSet.UpdateCount, Is.EqualTo(1));
            Assert.That(builder.ChangeSet.Updates[0].CurrentHealth, Is.EqualTo(30));
            Assert.That(builder.ChangeSet.RemoveCount, Is.Zero,
                "the tombstone of the base tick was already applied by the receiver");

            Assert.That(ring.TryMergeRange(0, 30, builder), Is.True);
            Assert.That(builder.ChangeSet.RemoveCount, Is.EqualTo(1), "merging from zero includes every tick");
            Assert.That(builder.ChangeSet.Removes[0].Entity.Value, Is.EqualTo(2UL));

            Assert.That(ring.TryMergeRange(20, 20, builder), Is.True);
            Assert.That(builder.ChangeSet.IsEmpty, Is.True, "an empty interval yields an empty delta");

            Assert.That(ring.TryMergeRange(10, 20, builder), Is.True);
            Assert.That(builder.ChangeSet.Updates[0].CurrentHealth, Is.EqualTo(20));
        }

        [Test]
        public void MergeRange_RejectsAnInvertedInterval()
        {
            var ring = new ReplicationHistoryRing(4, 4, 4);
            var builder = new ReplicationChangeSetBuilder(8, 8, 8);

            ring.RecordTick(10, HealthTick(1, 1));

            Assert.That(ring.TryMergeRange(20, 10, builder), Is.False);
            Assert.That(builder.ChangeSet.IsEmpty, Is.True, "a rejected merge clears the builder");
        }

        [Test]
        public void MergeRange_CollapsesAddAndRemoveAcrossTicks()
        {
            var ring = new ReplicationHistoryRing(8, 16, 8);
            var builder = new ReplicationChangeSetBuilder(32, 64, 32);

            ring.RecordTick(1, MixedTick(new[] { Add(5, posX: 10) }, NoUpdates(), NoRemoves()));
            ring.RecordTick(2, MixedTick(NoAdds(), new[] { Update(5, UnitDirtyMask.Position, posX: 20) }, NoRemoves()));
            ring.RecordTick(3, MixedTick(NoAdds(), NoUpdates(), new[] { Remove(5) }));
            ring.RecordTick(4, MixedTick(NoAdds(), new[] { Update(6, UnitDirtyMask.Health, health: 3) }, NoRemoves()));

            Assert.That(ring.TryMergeRange(0, 4, builder), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.AddCount, Is.Zero, "the entity that appeared and died leaves no ADD");
            Assert.That(merged.RemoveCount, Is.Zero, "... and no tombstone either");
            Assert.That(merged.UpdateCount, Is.EqualTo(1), "only the surviving entity is replicated");
            Assert.That(merged.Updates[0].Entity.Value, Is.EqualTo(6UL));
        }

        [Test]
        public void MergeRange_ReturnsFalseOnceTheBaseTickFellOutOfTheWindow()
        {
            var ring = new ReplicationHistoryRing(2, 2, 2);
            var builder = new ReplicationChangeSetBuilder(256, 512, 256);

            for (ulong tick = 1; tick <= 130; tick++)
            {
                ring.RecordTick(tick, HealthTick(tick, (int)tick));
            }

            Assert.That(ring.EvictedThroughTick, Is.EqualTo(10UL));

            Assert.That(ring.TryMergeRange(9, 130, builder), Is.False,
                "tick 10 was evicted and is newer than the base tick: no delta can be built");
            Assert.That(ring.TryMergeRange(10, 130, builder), Is.True,
                "a base tick at the eviction boundary is still servable");
            Assert.That(builder.ChangeSet.UpdateCount, Is.EqualTo(120), "the whole retained window merged");
            Assert.That(ring.TryMergeRange(129, 130, builder), Is.True);
            Assert.That(builder.ChangeSet.UpdateCount, Is.EqualTo(1));
            Assert.That(builder.ChangeSet.Updates[0].CurrentHealth, Is.EqualTo(130));
        }

        [Test]
        public void MergeRange_FailsWhenTheBuilderCannotHoldTheResult()
        {
            var ring = new ReplicationHistoryRing(8, 16, 8);
            var builder = new ReplicationChangeSetBuilder(2, 2, 2);

            ring.RecordTick(1, MixedTick(NoAdds(), new[]
            {
                Update(1, UnitDirtyMask.Health, health: 1),
                Update(2, UnitDirtyMask.Health, health: 2)
            }, NoRemoves()));
            ring.RecordTick(2, MixedTick(NoAdds(), new[]
            {
                Update(3, UnitDirtyMask.Health, health: 3),
                Update(4, UnitDirtyMask.Health, health: 4)
            }, NoRemoves()));

            Assert.That(ring.TryMergeRange(0, 2, builder), Is.False);
            Assert.That(builder.LastFailure, Is.EqualTo(ReplicationMergeFailure.CapacityExceeded),
                "the caller learns that a keyframe re-baseline is required");
        }

        [Test]
        public void MergeRange_RequiresABuilder()
        {
            var ring = new ReplicationHistoryRing(4, 4, 4);

            Assert.Throws<ArgumentNullException>(() => ring.TryMergeRange(0, 10, null));
        }

        [Test]
        public void Clear_ResetsTheWindowAndTheDiagnostics()
        {
            var ring = new ReplicationHistoryRing(1, 1, 1);
            var builder = new ReplicationChangeSetBuilder(8, 8, 8);

            ring.RecordTick(1, MixedTick(NoAdds(),
                new[] { Update(1, UnitDirtyMask.Health, health: 1), Update(2, UnitDirtyMask.Health, health: 2) },
                NoRemoves()));
            ring.RecordTick(0, HealthTick(3, 3));
            Assert.That(ring.OverflowCount, Is.EqualTo(1));
            Assert.That(ring.IgnoredRecordCount, Is.EqualTo(1));

            ring.Clear();

            Assert.That(ring.Count, Is.Zero);
            Assert.That(ring.HasEvicted, Is.False);
            Assert.That(ring.OverflowCount, Is.Zero);
            Assert.That(ring.IgnoredRecordCount, Is.Zero);
            Assert.That(ring.TryGetChangeSet(1, out _), Is.False);
            Assert.That(ring.TryMergeRange(0, 10, builder), Is.True);
            Assert.That(builder.ChangeSet.IsEmpty, Is.True);
        }

        private static DeltaAddRecord[] NoAdds() => new DeltaAddRecord[0];

        private static DeltaUpdateRecord[] NoUpdates() => new DeltaUpdateRecord[0];

        private static DeltaRemoveRecord[] NoRemoves() => new DeltaRemoveRecord[0];
    }
}
