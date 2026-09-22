using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server.Replication;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Server.Replication
{
    /// <summary>
    /// Isolated EditMode coverage of the cumulative merge rules that turn a
    /// sequence of per-tick change-sets into one establishing delta
    /// (Phase 2.6, step 2.6.2, ADR-010): OR-ed masks with last-wins per field,
    /// ADD/UPDATE folding, ADD+REMOVE cancellation, tombstone idempotence and
    /// the OD-14 move-target canonicalisation.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationChangeSetBuilderTests
    {
        private static DeltaAddRecord Add(
            ulong id,
            byte owner = 1,
            int posX = 0,
            int posZ = 0,
            int health = 100,
            bool hasMoveTarget = false,
            int moveTargetX = 0,
            int moveTargetZ = 0,
            ulong attackTarget = 0,
            bool autoAcquire = false,
            byte unitKind = UnitKinds.Unknown)
        {
            return new DeltaAddRecord(
                new EntityId(id),
                new PlayerId(owner),
                new WorldPointMm(posX, posZ),
                health,
                hasMoveTarget,
                new WorldPointMm(moveTargetX, moveTargetZ),
                new EntityId(attackTarget),
                autoAcquire,
                unitKind);
        }

        private static DeltaUpdateRecord Update(
            ulong id,
            UnitDirtyMask mask,
            byte owner = 0,
            int posX = 0,
            int posZ = 0,
            int health = 0,
            bool hasMoveTarget = false,
            int moveTargetX = 0,
            int moveTargetZ = 0,
            ulong attackTarget = 0,
            bool autoAcquire = false)
        {
            return new DeltaUpdateRecord(
                new EntityId(id),
                (byte)mask,
                new PlayerId(owner),
                new WorldPointMm(posX, posZ),
                health,
                hasMoveTarget,
                new WorldPointMm(moveTargetX, moveTargetZ),
                new EntityId(attackTarget),
                autoAcquire);
        }

        private static DeltaRemoveRecord Remove(
            ulong id,
            DeltaRemoveCause cause = DeltaRemoveCause.Destroyed)
        {
            return new DeltaRemoveRecord(new EntityId(id), cause);
        }

        /// <summary>
        /// Builds a standalone change-set. Every call owns its own buffer so two
        /// change-sets used in the same test never alias each other.
        /// </summary>
        private static ReplicationChangeSet SetOf(
            DeltaAddRecord[] adds,
            DeltaUpdateRecord[] updates,
            DeltaRemoveRecord[] removes)
        {
            var buffer = new ReplicationChangeSetBuffer(
                Math.Max(adds.Length, 1), Math.Max(updates.Length, 1), Math.Max(removes.Length, 1));

            for (var index = 0; index < adds.Length; index++)
            {
                buffer.Adds[index] = adds[index];
            }

            for (var index = 0; index < updates.Length; index++)
            {
                buffer.Updates[index] = updates[index];
            }

            for (var index = 0; index < removes.Length; index++)
            {
                buffer.Removes[index] = removes[index];
            }

            return buffer.AsChangeSet(adds.Length, updates.Length, removes.Length);
        }

        private static readonly DeltaAddRecord[] NoAdds = new DeltaAddRecord[0];
        private static readonly DeltaUpdateRecord[] NoUpdates = new DeltaUpdateRecord[0];
        private static readonly DeltaRemoveRecord[] NoRemoves = new DeltaRemoveRecord[0];

        // ---------------------------------------------------------------
        // Change-set value type and buffers
        // ---------------------------------------------------------------

        [Test]
        public void ChangeSet_Empty_ExposesEmptySpans()
        {
            var empty = ReplicationChangeSet.Empty;

            Assert.That(empty.IsEmpty, Is.True);
            Assert.That(empty.RecordCount, Is.Zero);
            Assert.That(empty.AddBuffer, Is.Null);
            Assert.That(empty.Adds.Length, Is.Zero, "an empty change-set must still expose usable spans");
            Assert.That(empty.Updates.Length, Is.Zero);
            Assert.That(empty.Removes.Length, Is.Zero);
        }

        [Test]
        public void ChangeSet_Constructor_RejectsCountsWithoutABuffer()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _ = new ReplicationChangeSet(null, 1, null, 0, null, 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _ = new ReplicationChangeSet(new DeltaAddRecord[1], -1, null, 0, null, 0));
        }

        [Test]
        public void ChangeSetBuffer_AsChangeSet_RejectsCountsAboveCapacity()
        {
            var buffer = new ReplicationChangeSetBuffer(1, 1, 1);

            Assert.That(buffer.CanHold(1, 1, 1), Is.True);
            Assert.That(buffer.CanHold(2, 0, 0), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = buffer.AsChangeSet(2, 0, 0));
        }

        [Test]
        public void ChangeSetBuffer_CopyFrom_ReusesStorageAndReportsTruncation()
        {
            var source = SetOf(
                new[] { Add(1), Add(2) },
                new[] { Update(3, UnitDirtyMask.Health, health: 5) },
                new[] { Remove(4) });

            var roomy = new ReplicationChangeSetBuffer(4, 4, 4);
            var copy = roomy.CopyFrom(in source, out var truncated);

            Assert.That(truncated, Is.False);
            Assert.That(copy.AddCount, Is.EqualTo(2));
            Assert.That(copy.UpdateCount, Is.EqualTo(1));
            Assert.That(copy.RemoveCount, Is.EqualTo(1));
            Assert.That(copy.Adds.ToArray(), Is.EqualTo(source.Adds.ToArray()), "records are copied by value");

            // Mutating the source buffer afterwards must not change the copy.
            var tight = new ReplicationChangeSetBuffer(1, 1, 0);
            var partial = tight.CopyFrom(in source, out var partialTruncated);

            Assert.That(partialTruncated, Is.True, "records that do not fit must be reported");
            Assert.That(partial.AddCount, Is.EqualTo(1));
            Assert.That(partial.UpdateCount, Is.EqualTo(1));
            Assert.That(partial.RemoveCount, Is.Zero);
        }

        // ---------------------------------------------------------------
        // Merge rules
        // ---------------------------------------------------------------

        [Test]
        public void Builder_FreshAndCleared_AreEmpty()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            Assert.That(builder.ChangeSet.IsEmpty, Is.True);
            Assert.That(builder.LastFailure, Is.EqualTo(ReplicationMergeFailure.None));

            Assert.That(builder.TryMerge(SetOf(NoAdds, new[] { Update(1, UnitDirtyMask.Health, health: 1) }, NoRemoves)),
                Is.True);
            Assert.That(builder.ChangeSet.UpdateCount, Is.EqualTo(1));

            builder.Clear();
            Assert.That(builder.ChangeSet.IsEmpty, Is.True, "clear resets the accumulator without reallocating");
            Assert.That(builder.LastFailure, Is.EqualTo(ReplicationMergeFailure.None));
        }

        [Test]
        public void Merge_SingleChangeSet_IsReproducedExactly()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);
            var tick = SetOf(
                new[] { Add(2) },
                new[] { Update(5, UnitDirtyMask.Position, posX: 10, posZ: 20) },
                new[] { Remove(7, DeltaRemoveCause.FogOfWarHidden) });

            Assert.That(builder.TryMerge(tick), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.AddCount, Is.EqualTo(1));
            Assert.That(merged.UpdateCount, Is.EqualTo(1));
            Assert.That(merged.RemoveCount, Is.EqualTo(1));
            Assert.That(merged.Adds.ToArray(), Is.EqualTo(tick.Adds.ToArray()));
            Assert.That(merged.Updates.ToArray(), Is.EqualTo(tick.Updates.ToArray()));
            Assert.That(merged.Removes.ToArray(), Is.EqualTo(tick.Removes.ToArray()));
        }

        [Test]
        public void Merge_TwoUpdates_OrMasksAndKeepBothFieldValues()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            Assert.That(builder.TryMerge(SetOf(NoAdds,
                new[] { Update(5, UnitDirtyMask.Position, posX: 10, posZ: 20) }, NoRemoves)), Is.True);
            Assert.That(builder.TryMerge(SetOf(NoAdds,
                new[] { Update(5, UnitDirtyMask.Health, health: 42) }, NoRemoves)), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.UpdateCount, Is.EqualTo(1), "one entity produces one cumulative record");
            Assert.That(merged.Updates[0].DirtyMask,
                Is.EqualTo((byte)(UnitDirtyMask.Position | UnitDirtyMask.Health)),
                "masks are OR-ed");
            Assert.That(merged.Updates[0].Position, Is.EqualTo(new WorldPointMm(10, 20)));
            Assert.That(merged.Updates[0].CurrentHealth, Is.EqualTo(42));
        }

        [Test]
        public void Merge_SameFieldAcrossThreeTicks_IsLastWins()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(NoAdds, new[] { Update(5, UnitDirtyMask.Position, posX: 10, posZ: 20) }, NoRemoves));
            builder.TryMerge(SetOf(NoAdds, new[] { Update(5, UnitDirtyMask.Position, posX: 30, posZ: 40) }, NoRemoves));
            Assert.That(builder.TryMerge(SetOf(NoAdds,
                new[] { Update(5, UnitDirtyMask.Position, posX: 50, posZ: 60) }, NoRemoves)), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.UpdateCount, Is.EqualTo(1));
            Assert.That(merged.Updates[0].DirtyMask, Is.EqualTo((byte)UnitDirtyMask.Position));
            Assert.That(merged.Updates[0].Position, Is.EqualTo(new WorldPointMm(50, 60)),
                "the last value of the interval wins per field");
        }

        [Test]
        public void Merge_UpdateAfterAdd_FoldsIntoTheAddRecord()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(
                new[] { Add(7, owner: 1, posX: 1, posZ: 1, health: 100, unitKind: UnitKinds.Tank) },
                NoUpdates,
                NoRemoves));
            Assert.That(builder.TryMerge(SetOf(NoAdds,
                new[] { Update(7, UnitDirtyMask.Position | UnitDirtyMask.Health, posX: 2, posZ: 3, health: 90) },
                NoRemoves)), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.AddCount, Is.EqualTo(1), "the receiver never knew the entity, so it stays an ADD");
            Assert.That(merged.UpdateCount, Is.Zero, "no separate UPDATE may survive next to the ADD");
            Assert.That(merged.Adds[0].Entity, Is.EqualTo(new EntityId(7)));
            Assert.That(merged.Adds[0].Position, Is.EqualTo(new WorldPointMm(2, 3)));
            Assert.That(merged.Adds[0].CurrentHealth, Is.EqualTo(90));
            Assert.That(merged.Adds[0].Owner.Value, Is.EqualTo(1), "untouched fields keep the ADD value");

            // The archetype has no dirty bit, so folding can only carry it over: an
            // ADD that arrived as a Tank must not reach the client as Unknown.
            Assert.That(merged.Adds[0].UnitKind, Is.EqualTo(UnitKinds.Tank),
                "folding an update into an ADD must not lose the replicated archetype");
        }

        [Test]
        public void Merge_AddThenRemove_CancelsBothRecords()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(new[] { Add(9) }, NoUpdates, NoRemoves));
            Assert.That(builder.TryMerge(SetOf(NoAdds, NoUpdates, new[] { Remove(9) })), Is.True);

            Assert.That(builder.ChangeSet.IsEmpty, Is.True,
                "an entity born and destroyed inside the window must disappear completely");
        }

        [Test]
        public void Merge_AddUpdateThenRemove_CancelsEverything()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(new[] { Add(11) }, NoUpdates, NoRemoves));
            builder.TryMerge(SetOf(NoAdds, new[] { Update(11, UnitDirtyMask.Position, posX: 5) }, NoRemoves));
            Assert.That(builder.TryMerge(SetOf(NoAdds, NoUpdates, new[] { Remove(11) })), Is.True);

            Assert.That(builder.ChangeSet.IsEmpty, Is.True);
        }

        [Test]
        public void Merge_UpdateThenRemove_KeepsOnlyTheTombstone()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(NoAdds, new[] { Update(13, UnitDirtyMask.Health, health: 10) }, NoRemoves));
            Assert.That(builder.TryMerge(SetOf(NoAdds, NoUpdates, new[] { Remove(13) })), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.UpdateCount, Is.Zero, "updates of a dead entity are dropped");
            Assert.That(merged.RemoveCount, Is.EqualTo(1));
            Assert.That(merged.Removes[0].Entity, Is.EqualTo(new EntityId(13)));
        }

        [Test]
        public void Merge_RepeatedTombstones_CollapseIntoOne()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(NoAdds, NoUpdates, new[] { Remove(15) }));
            builder.TryMerge(SetOf(NoAdds, NoUpdates, new[] { Remove(15) }));
            Assert.That(builder.TryMerge(SetOf(NoAdds, NoUpdates, new[] { Remove(15) })), Is.True);

            Assert.That(builder.ChangeSet.RemoveCount, Is.EqualTo(1), "removal is idempotent");
        }

        [Test]
        public void Merge_RepeatedTombstones_KeepTheLastCause()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(NoAdds, NoUpdates, new[] { Remove(15, DeltaRemoveCause.Destroyed) }));
            Assert.That(builder.TryMerge(SetOf(NoAdds, NoUpdates,
                new[] { Remove(15, DeltaRemoveCause.FogOfWarHidden) })), Is.True);

            Assert.That(builder.ChangeSet.Removes[0].Cause, Is.EqualTo((byte)DeltaRemoveCause.FogOfWarHidden));
        }

        [Test]
        public void Merge_RemoveAfterRemoveOfKnownEntity_StaysASingleTombstone()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(NoAdds, new[] { Update(21, UnitDirtyMask.Position, posX: 1) }, NoRemoves));
            builder.TryMerge(SetOf(NoAdds, NoUpdates, new[] { Remove(21) }));
            Assert.That(builder.TryMerge(SetOf(NoAdds, NoUpdates, new[] { Remove(21) })), Is.True);

            Assert.That(builder.ChangeSet.RemoveCount, Is.EqualTo(1));
            Assert.That(builder.ChangeSet.UpdateCount, Is.Zero);
        }

        [Test]
        public void Merge_Od14_ClearAfterSet_DropsTheCoordinateBit()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(NoAdds, new[]
            {
                Update(17, UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                    hasMoveTarget: true, moveTargetX: 70, moveTargetZ: 80)
            }, NoRemoves));
            Assert.That(builder.TryMerge(SetOf(NoAdds,
                new[] { Update(17, UnitDirtyMask.HasMoveTarget) }, NoRemoves)), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.UpdateCount, Is.EqualTo(1));
            Assert.That(merged.Updates[0].DirtyMask, Is.EqualTo((byte)UnitDirtyMask.HasMoveTarget),
                "a cleared target must not carry the coordinate bit");
            Assert.That(merged.Updates[0].HasMoveTarget, Is.False);
            Assert.That(merged.Updates[0].MoveTarget, Is.EqualTo(new WorldPointMm(0, 0)),
                "stale coordinates are dropped instead of being replayed");
        }

        [Test]
        public void Merge_Od14_SetAfterClear_RestoresFlagAndCoordinates()
        {
            var builder = new ReplicationChangeSetBuilder(4, 8, 4);

            builder.TryMerge(SetOf(NoAdds, new[] { Update(17, UnitDirtyMask.HasMoveTarget) }, NoRemoves));
            Assert.That(builder.TryMerge(SetOf(NoAdds, new[]
            {
                Update(17, UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                    hasMoveTarget: true, moveTargetX: 91, moveTargetZ: 92)
            }, NoRemoves)), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.Updates[0].DirtyMask,
                Is.EqualTo((byte)(UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget)));
            Assert.That(merged.Updates[0].HasMoveTarget, Is.True);
            Assert.That(merged.Updates[0].MoveTarget, Is.EqualTo(new WorldPointMm(91, 92)));
        }

        [Test]
        public void Merge_MixedEntities_KeepEverySectionAscending()
        {
            var builder = new ReplicationChangeSetBuilder(8, 8, 8);

            builder.TryMerge(SetOf(
                new[] { Add(10) },
                new[] { Update(20, UnitDirtyMask.Health, health: 1) },
                new[] { Remove(30) }));
            Assert.That(builder.TryMerge(SetOf(
                new[] { Add(15) },
                new[] { Update(25, UnitDirtyMask.Health, health: 2) },
                new[] { Remove(35) })), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.AddCount, Is.EqualTo(2));
            Assert.That(merged.Adds[0].Entity.Value, Is.EqualTo(10UL));
            Assert.That(merged.Adds[1].Entity.Value, Is.EqualTo(15UL));
            Assert.That(merged.Updates[0].Entity.Value, Is.EqualTo(20UL));
            Assert.That(merged.Updates[1].Entity.Value, Is.EqualTo(25UL));
            Assert.That(merged.Removes[0].Entity.Value, Is.EqualTo(30UL));
            Assert.That(merged.Removes[1].Entity.Value, Is.EqualTo(35UL));
        }

        [Test]
        public void Merge_InterleavedEntitiesFromBothSides_StayAscending()
        {
            var builder = new ReplicationChangeSetBuilder(8, 8, 8);

            builder.TryMerge(SetOf(NoAdds, new[]
            {
                Update(10, UnitDirtyMask.Position, posX: 1),
                Update(30, UnitDirtyMask.Position, posX: 3)
            }, NoRemoves));
            Assert.That(builder.TryMerge(SetOf(NoAdds, new[]
            {
                Update(20, UnitDirtyMask.Health, health: 2),
                Update(30, UnitDirtyMask.Health, health: 30)
            }, NoRemoves)), Is.True);

            var merged = builder.ChangeSet;
            Assert.That(merged.UpdateCount, Is.EqualTo(3));
            Assert.That(merged.Updates[0].Entity.Value, Is.EqualTo(10UL));
            Assert.That(merged.Updates[1].Entity.Value, Is.EqualTo(20UL));
            Assert.That(merged.Updates[2].Entity.Value, Is.EqualTo(30UL));
            Assert.That(merged.Updates[2].DirtyMask,
                Is.EqualTo((byte)(UnitDirtyMask.Position | UnitDirtyMask.Health)),
                "the entity present on both sides is folded");
            Assert.That(merged.Updates[2].Position, Is.EqualTo(new WorldPointMm(3, 0)));
            Assert.That(merged.Updates[2].CurrentHealth, Is.EqualTo(30));
        }

        [Test]
        public void Merge_CapacityExceeded_FailsAndKeepsThePreviousAccumulator()
        {
            var builder = new ReplicationChangeSetBuilder(2, 2, 2);

            Assert.That(builder.TryMerge(SetOf(NoAdds, new[]
            {
                Update(1, UnitDirtyMask.Health, health: 1),
                Update(2, UnitDirtyMask.Health, health: 2)
            }, NoRemoves)), Is.True);
            Assert.That(builder.ChangeSet.UpdateCount, Is.EqualTo(2));

            Assert.That(builder.TryMerge(SetOf(NoAdds, new[]
            {
                Update(3, UnitDirtyMask.Health, health: 3),
                Update(4, UnitDirtyMask.Health, health: 4)
            }, NoRemoves)), Is.False, "the merged result does not fit");
            Assert.That(builder.LastFailure, Is.EqualTo(ReplicationMergeFailure.CapacityExceeded));
            Assert.That(builder.ChangeSet.UpdateCount, Is.EqualTo(2),
                "a failed merge must not corrupt the accumulator");
            Assert.That(builder.ChangeSet.Updates[0].Entity.Value, Is.EqualTo(1UL));
        }

        [Test]
        public void Merge_UnorderedInput_IsRejected()
        {
            var builder = new ReplicationChangeSetBuilder(8, 8, 8);

            Assert.That(builder.TryMerge(SetOf(NoAdds, new[]
            {
                Update(9, UnitDirtyMask.Health, health: 1),
                Update(4, UnitDirtyMask.Health, health: 2)
            }, NoRemoves)), Is.False, "canonical ascending order is required");
            Assert.That(builder.LastFailure, Is.EqualTo(ReplicationMergeFailure.UnorderedInput));
            Assert.That(builder.ChangeSet.IsEmpty, Is.True);
        }

        [Test]
        public void Merge_ResultIsEncodableByTheCoreWireCodec()
        {
            var builder = new ReplicationChangeSetBuilder(8, 8, 8);

            builder.TryMerge(SetOf(new[] { Add(4, owner: 2, posX: 40) },
                new[] { Update(9, UnitDirtyMask.Position, posX: 90, posZ: 91) }, NoRemoves));
            builder.TryMerge(SetOf(NoAdds,
                new[] { Update(9, UnitDirtyMask.Health, health: 7) },
                new[] { Remove(12, DeltaRemoveCause.FogOfWarHidden) }));

            var merged = builder.ChangeSet;
            var header = DeltaSnapshotHeader.CreateDelta(
                30, 20, DeltaFlags.None, 0,
                (ushort)merged.AddCount, (ushort)merged.UpdateCount, (ushort)merged.RemoveCount);
            var packet = new byte[DeltaSnapshotWireCodec.GetMaxEncodedSize(
                merged.AddCount, merged.UpdateCount, merged.RemoveCount)];

            var encoded = DeltaSnapshotWireCodec.TryEncode(
                header, merged.Adds, merged.Updates, merged.Removes, packet, out var written);

            Assert.That(encoded, Is.EqualTo(DeltaCodecResult.Ok), "a cumulative change-set must be wire-encodable");
            Assert.That(written, Is.GreaterThan(DeltaSnapshotProtocol.HeaderSizeBytes));
        }
    }
}
