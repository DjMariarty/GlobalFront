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
    /// Isolated EditMode coverage of the server snapshot diff engine
    /// (Phase 2.6, step 2.6.2, ADR-010): ADD/UPDATE/REMOVE detection over two
    /// canonical world slices, the exact per-field dirty mask, OD-14 move-target
    /// clearing, input validation and buffer reuse.
    /// </summary>
    [TestFixture]
    public sealed class ServerSnapshotDiffEngineTests
    {
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

        /// <summary>World slices are always handed over in canonical ascending order.</summary>
        private static ServerUnitSnapshot[] World(params ServerUnitSnapshot[] units)
        {
            Array.Sort(units, (left, right) => left.Entity.Value.CompareTo(right.Entity.Value));
            return units;
        }

        private static ServerSnapshotDiffEngine NewEngine() => new ServerSnapshotDiffEngine(16, 32, 16);

        private static readonly ServerUnitSnapshot[] NoUnits = new ServerUnitSnapshot[0];

        // ---------------------------------------------------------------
        // Change detection
        // ---------------------------------------------------------------

        [Test]
        public void Diff_IdenticalWorlds_ProduceAnEmptyChangeSet()
        {
            var world = World(Unit(1), Unit(2, posX: 500), Unit(3, health: 42));

            var result = NewEngine().Diff(world, world, out var changeSet);

            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));
            Assert.That(changeSet.IsEmpty, Is.True);
            Assert.That(changeSet.RecordCount, Is.Zero);
            Assert.That(changeSet.Adds.Length, Is.Zero);
            Assert.That(changeSet.Updates.Length, Is.Zero);
            Assert.That(changeSet.Removes.Length, Is.Zero);
        }

        [Test]
        public void Diff_DetectsAddsUpdatesAndRemoves_InOnePass()
        {
            var previous = World(Unit(1), Unit(2), Unit(3), Unit(5));
            var current = World(Unit(1), Unit(2, posX: 500), Unit(4, owner: 2), Unit(5, health: 80, autoAcquire: true));

            var result = NewEngine().Diff(previous, current, out var changeSet);

            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));

            Assert.That(changeSet.AddCount, Is.EqualTo(1));
            Assert.That(changeSet.Adds[0].Entity, Is.EqualTo(new EntityId(4)));

            Assert.That(changeSet.UpdateCount, Is.EqualTo(2));
            Assert.That(changeSet.Updates[0].Entity, Is.EqualTo(new EntityId(2)));
            Assert.That(changeSet.Updates[0].DirtyMask, Is.EqualTo((byte)UnitDirtyMask.Position));
            Assert.That(changeSet.Updates[0].Position, Is.EqualTo(new WorldPointMm(500, 200)));
            Assert.That(changeSet.Updates[1].Entity, Is.EqualTo(new EntityId(5)));
            Assert.That(changeSet.Updates[1].DirtyMask,
                Is.EqualTo((byte)(UnitDirtyMask.Health | UnitDirtyMask.AutoAcquire)));
            Assert.That(changeSet.Updates[1].CurrentHealth, Is.EqualTo(80));
            Assert.That(changeSet.Updates[1].AutoAcquireEnemies, Is.True);

            Assert.That(changeSet.RemoveCount, Is.EqualTo(1));
            Assert.That(changeSet.Removes[0].Entity, Is.EqualTo(new EntityId(3)));
            Assert.That(changeSet.Removes[0].Cause, Is.EqualTo((byte)DeltaRemoveCause.Destroyed));
        }

        [Test]
        public void Diff_AddRecordCarriesTheFullAbsoluteState()
        {
            var current = World(Unit(7, owner: 3, posX: -1000, posZ: 2000, health: 55,
                hasMoveTarget: true, moveTargetX: 9000, moveTargetZ: -9000, attackTarget: 11, autoAcquire: true));

            var result = NewEngine().Diff(NoUnits, current, out var changeSet);

            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));
            Assert.That(changeSet.AddCount, Is.EqualTo(1));
            Assert.That(changeSet.UpdateCount, Is.Zero);
            Assert.That(changeSet.RemoveCount, Is.Zero);

            var add = changeSet.Adds[0];
            Assert.That(add.Entity, Is.EqualTo(new EntityId(7)));
            Assert.That(add.Owner.Value, Is.EqualTo(3));
            Assert.That(add.Position, Is.EqualTo(new WorldPointMm(-1000, 2000)));
            Assert.That(add.CurrentHealth, Is.EqualTo(55));
            Assert.That(add.HasMoveTarget, Is.True);
            Assert.That(add.MoveTarget, Is.EqualTo(new WorldPointMm(9000, -9000)));
            Assert.That(add.AttackTarget, Is.EqualTo(new EntityId(11)));
            Assert.That(add.AutoAcquireEnemies, Is.True);
        }

        [Test]
        public void Diff_EveryRemovedEntityBecomesADestroyedTombstone()
        {
            var previous = World(Unit(1), Unit(2), Unit(3));

            var result = NewEngine().Diff(previous, NoUnits, out var changeSet);

            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));
            Assert.That(changeSet.RemoveCount, Is.EqualTo(3));
            Assert.That(changeSet.AddCount, Is.Zero);
            Assert.That(changeSet.UpdateCount, Is.Zero);
            for (var index = 0; index < 3; index++)
            {
                Assert.That(changeSet.Removes[index].Entity.Value, Is.EqualTo((ulong)(index + 1)));
                Assert.That(changeSet.Removes[index].Cause, Is.EqualTo((byte)DeltaRemoveCause.Destroyed),
                    "fog-of-war hiding is a filtering concern of a later step, not of the diff");
            }
        }

        [Test]
        public void Diff_UnchangedEntitiesAmongChangedOnes_ProduceNoRecord()
        {
            var previous = World(Unit(1), Unit(2), Unit(3), Unit(4), Unit(5));
            var current = World(Unit(1), Unit(2, posX: 111), Unit(3), Unit(4), Unit(5, posX: 555));

            var result = NewEngine().Diff(previous, current, out var changeSet);

            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));
            Assert.That(changeSet.UpdateCount, Is.EqualTo(2));
            Assert.That(changeSet.Updates[0].Entity.Value, Is.EqualTo(2UL));
            Assert.That(changeSet.Updates[1].Entity.Value, Is.EqualTo(5UL));
        }

        [Test]
        public void Diff_InterleavedChurn_KeepsEverySectionAscending()
        {
            var previous = World(Unit(10), Unit(20), Unit(30), Unit(40), Unit(50));
            var current = World(Unit(5), Unit(20, posX: 1), Unit(35), Unit(50, health: 1));

            var result = NewEngine().Diff(previous, current, out var changeSet);

            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));
            Assert.That(changeSet.AddCount, Is.EqualTo(2));
            Assert.That(changeSet.Adds[0].Entity.Value, Is.EqualTo(5UL));
            Assert.That(changeSet.Adds[1].Entity.Value, Is.EqualTo(35UL));
            Assert.That(changeSet.UpdateCount, Is.EqualTo(2));
            Assert.That(changeSet.Updates[0].Entity.Value, Is.EqualTo(20UL));
            Assert.That(changeSet.Updates[1].Entity.Value, Is.EqualTo(50UL));
            Assert.That(changeSet.RemoveCount, Is.EqualTo(3));
            Assert.That(changeSet.Removes[0].Entity.Value, Is.EqualTo(10UL));
            Assert.That(changeSet.Removes[1].Entity.Value, Is.EqualTo(30UL));
            Assert.That(changeSet.Removes[2].Entity.Value, Is.EqualTo(40UL));
        }

        [Test]
        public void Diff_EmptyPreviousSlice_TreatsTheWholeWorldAsAdds()
        {
            var current = World(Unit(1), Unit(2), Unit(3));

            var result = NewEngine().Diff(NoUnits, current, out var changeSet);

            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));
            Assert.That(changeSet.AddCount, Is.EqualTo(3));
            Assert.That(changeSet.UpdateCount, Is.Zero);
            Assert.That(changeSet.RemoveCount, Is.Zero);
        }

        [Test]
        public void Diff_BothSlicesEmpty_IsOk()
        {
            var result = NewEngine().Diff(NoUnits, NoUnits, out var changeSet);

            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));
            Assert.That(changeSet.IsEmpty, Is.True);
        }

        [Test]
        public void Diff_UpdateValuesAreAbsolute_CurrentStateNotResiduals()
        {
            var previous = World(Unit(1, posX: 100, posZ: 100, health: 100));
            var current = World(Unit(1, posX: 130, posZ: 100, health: 97));

            NewEngine().Diff(previous, current, out var changeSet);

            Assert.That(changeSet.UpdateCount, Is.EqualTo(1));
            Assert.That(changeSet.Updates[0].Position, Is.EqualTo(new WorldPointMm(130, 100)),
                "positions travel as absolute values, never as offsets");
            Assert.That(changeSet.Updates[0].CurrentHealth, Is.EqualTo(97));
        }

        [Test]
        public void Diff_UpdateRecordCarriesOnlyMaskedFields()
        {
            var previous = World(Unit(1, owner: 4, posX: 100, health: 100, attackTarget: 9, autoAcquire: true));
            var current = World(Unit(1, owner: 4, posX: 250, health: 100, attackTarget: 9, autoAcquire: true));

            NewEngine().Diff(previous, current, out var changeSet);

            var update = changeSet.Updates[0];
            Assert.That(update.DirtyMask, Is.EqualTo((byte)UnitDirtyMask.Position));
            Assert.That(update.Position, Is.EqualTo(new WorldPointMm(250, 200)));
            Assert.That(update.Owner.Value, Is.Zero, "unmasked owner stays default");
            Assert.That(update.CurrentHealth, Is.Zero, "unmasked health stays default");
            Assert.That(update.AttackTarget, Is.EqualTo(new EntityId(0)), "unmasked attack target stays default");
            Assert.That(update.AutoAcquireEnemies, Is.False, "unmasked auto-acquire stays default");
            Assert.That(update.HasMoveTarget, Is.False);
        }

        [Test]
        public void Diff_DoesNotModifyTheInputSlices()
        {
            var previous = World(Unit(1), Unit(2));
            var current = World(Unit(1, posX: 700), Unit(3));
            var previousCopy = (ServerUnitSnapshot[])previous.Clone();
            var currentCopy = (ServerUnitSnapshot[])current.Clone();

            NewEngine().Diff(previous, current, out _);

            Assert.That(previous, Is.EqualTo(previousCopy), "the engine only reads the world slices");
            Assert.That(current, Is.EqualTo(currentCopy));
        }

        // ---------------------------------------------------------------
        // Dirty mask
        // ---------------------------------------------------------------

        [Test]
        public void DirtyMask_EachSingleFieldChange_SetsExactlyOneBit()
        {
            var baseline = Unit(1, owner: 1, posX: 100, posZ: 200, health: 100,
                hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600, attackTarget: 7);

            var cases = new (string Because, ServerUnitSnapshot Current, byte ExpectedMask)[]
            {
                ("owner", Unit(1, owner: 2, posX: 100, posZ: 200, health: 100,
                    hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600, attackTarget: 7), 0x01),
                ("position X", Unit(1, posX: 101, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600,
                    attackTarget: 7), 0x02),
                ("position Z", Unit(1, posZ: 201, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600,
                    attackTarget: 7), 0x02),
                ("health", Unit(1, health: 99, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600,
                    attackTarget: 7), 0x04),
                ("move target coordinates", Unit(1, hasMoveTarget: true, moveTargetX: 501, moveTargetZ: 600,
                    attackTarget: 7), 0x10),
                ("attack target changed", Unit(1, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600,
                    attackTarget: 8), 0x20),
                ("attack target cleared", Unit(1, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600,
                    attackTarget: 0), 0x20),
                ("auto acquire", Unit(1, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600, attackTarget: 7,
                    autoAcquire: true), 0x40)
            };

            foreach (var (because, current, expectedMask) in cases)
            {
                Assert.That(ServerSnapshotDiffEngine.ComputeDirtyMask(in baseline, in current),
                    Is.EqualTo(expectedMask),
                    $"{because}: expected mask 0x{expectedMask:X2}");
            }
        }

        [Test]
        public void DirtyMask_AllFieldsAtOnce_SetsEveryDefinedBit()
        {
            var previous = Unit(1);
            var current = Unit(1, owner: 2, posX: 1, posZ: 2, health: 3, hasMoveTarget: true,
                moveTargetX: 4, moveTargetZ: 5, attackTarget: 6, autoAcquire: true);

            Assert.That(ServerSnapshotDiffEngine.ComputeDirtyMask(in previous, in current), Is.EqualTo(0x7F));
            Assert.That((byte)UnitDirtyMask.ReservedExtension, Is.EqualTo(0x80),
                "the diff must never set the reserved extension bit");
        }

        [Test]
        public void DirtyMask_EveryFieldButTheCoordinates_SkipsTheOd14Bit()
        {
            var previous = Unit(1, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600, attackTarget: 7);
            var current = Unit(1, owner: 2, posX: 1, posZ: 2, health: 3, hasMoveTarget: false,
                attackTarget: 0, autoAcquire: true);

            Assert.That(ServerSnapshotDiffEngine.ComputeDirtyMask(in previous, in current), Is.EqualTo(0x6F),
                "clearing the target sets the flag bit but never the coordinate bit");
        }

        [Test]
        public void DirtyMask_SettingAMoveTarget_SetsFlagAndCoordinates()
        {
            var previous = Unit(1);
            var current = Unit(1, hasMoveTarget: true, moveTargetX: 5, moveTargetZ: 6);

            Assert.That(ServerSnapshotDiffEngine.ComputeDirtyMask(in previous, in current), Is.EqualTo(0x18));
        }

        [Test]
        public void DirtyMask_CoordinatesWithoutATarget_AreNotReplicated()
        {
            // Stale coordinates while no target exists carry no information.
            var previous = Unit(1);
            var current = Unit(1, moveTargetX: 999, moveTargetZ: 999);

            Assert.That(ServerSnapshotDiffEngine.ComputeDirtyMask(in previous, in current), Is.EqualTo(0x00));

            var result = NewEngine().Diff(World(previous), World(current), out var changeSet);
            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));
            Assert.That(changeSet.IsEmpty, Is.True, "no field the receiver cares about changed");
        }

        [Test]
        public void Diff_Od14_ClearingTheMoveTarget_ProducesARecordWithoutCoordinates()
        {
            var previous = World(Unit(1, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600, attackTarget: 7));
            var current = World(Unit(1, hasMoveTarget: false, attackTarget: 7));

            var result = NewEngine().Diff(previous, current, out var changeSet);

            Assert.That(result, Is.EqualTo(ReplicationDiffResult.Ok));
            Assert.That(changeSet.UpdateCount, Is.EqualTo(1));

            var update = changeSet.Updates[0];
            Assert.That(update.DirtyMask, Is.EqualTo((byte)UnitDirtyMask.HasMoveTarget),
                "OD-14: the clear is described by the flag bit alone");
            Assert.That(update.HasMoveTarget, Is.False);
            Assert.That(update.MoveTarget, Is.EqualTo(new WorldPointMm(0, 0)),
                "a cleared target never carries coordinates");

            // The wire form must not contain the two omitted i32 coordinates.
            var cleared = EncodeSingleUpdate(update);
            Assert.That(cleared, Is.EqualTo(DeltaSnapshotProtocol.HeaderSizeBytes + 3),
                "id varint + mask + flag byte");

            var retarget = EncodeSingleUpdate(ServerSnapshotDiffEngine.ToUpdateRecord(
                Unit(1, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600, attackTarget: 7),
                (byte)(UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget)));
            Assert.That(retarget - cleared, Is.EqualTo(8), "setting the target costs exactly the two coordinates");
        }

        [Test]
        public void Diff_Od14_ReTargetingWithoutAFlagChange_SendsCoordinatesOnly()
        {
            var previous = World(Unit(1, hasMoveTarget: true, moveTargetX: 500, moveTargetZ: 600));
            var current = World(Unit(1, hasMoveTarget: true, moveTargetX: 700, moveTargetZ: 800));

            NewEngine().Diff(previous, current, out var changeSet);

            Assert.That(changeSet.Updates[0].DirtyMask, Is.EqualTo((byte)UnitDirtyMask.MoveTarget));
            Assert.That(changeSet.Updates[0].HasMoveTarget, Is.False,
                "the flag is not part of the record when its bit is clear");
            Assert.That(changeSet.Updates[0].MoveTarget, Is.EqualTo(new WorldPointMm(700, 800)));
        }

        [Test]
        public void ToUpdateRecord_MirrorsTheMaskedFieldsOfTheState()
        {
            var state = Unit(3, owner: 2, posX: 11, posZ: 22, health: 33, hasMoveTarget: true,
                moveTargetX: 44, moveTargetZ: 55, attackTarget: 66, autoAcquire: true);

            var record = ServerSnapshotDiffEngine.ToUpdateRecord(in state, 0x7F);

            Assert.That(record.Entity, Is.EqualTo(new EntityId(3)));
            Assert.That(record.DirtyMask, Is.EqualTo(0x7F));
            Assert.That(record.Owner.Value, Is.EqualTo(2));
            Assert.That(record.Position, Is.EqualTo(new WorldPointMm(11, 22)));
            Assert.That(record.CurrentHealth, Is.EqualTo(33));
            Assert.That(record.HasMoveTarget, Is.True);
            Assert.That(record.MoveTarget, Is.EqualTo(new WorldPointMm(44, 55)));
            Assert.That(record.AttackTarget, Is.EqualTo(new EntityId(66)));
            Assert.That(record.AutoAcquireEnemies, Is.True);
        }

        // ---------------------------------------------------------------
        // Input validation and buffer discipline
        // ---------------------------------------------------------------

        [Test]
        public void Diff_RejectsSlicesThatAreNotInCanonicalOrder()
        {
            var engine = NewEngine();

            Assert.That(engine.Diff(new[] { Unit(5), Unit(1) }, NoUnits, out var changeSet),
                Is.EqualTo(ReplicationDiffResult.UnorderedInput), "descending previous slice");
            Assert.That(changeSet.IsEmpty, Is.True, "a rejected diff produces no change-set");

            Assert.That(engine.Diff(NoUnits, new[] { Unit(5), Unit(1) }, out _),
                Is.EqualTo(ReplicationDiffResult.UnorderedInput), "descending current slice");

            Assert.That(engine.Diff(NoUnits, new[] { Unit(5), Unit(5) }, out _),
                Is.EqualTo(ReplicationDiffResult.UnorderedInput), "duplicate ids in the current slice");

            Assert.That(engine.Diff(new[] { Unit(5), Unit(5) }, NoUnits, out _),
                Is.EqualTo(ReplicationDiffResult.UnorderedInput), "duplicate ids in the previous slice");
        }

        [Test]
        public void Diff_RejectsTheReservedEntityIdZero()
        {
            var engine = NewEngine();

            Assert.That(engine.Diff(new[] { Unit(0) }, NoUnits, out _),
                Is.EqualTo(ReplicationDiffResult.InvalidEntityId), "zero id in the previous slice");
            Assert.That(engine.Diff(NoUnits, new[] { Unit(0) }, out _),
                Is.EqualTo(ReplicationDiffResult.InvalidEntityId), "zero id in the current slice");
            Assert.That(engine.Diff(new[] { Unit(1), Unit(0) }, new[] { Unit(1), Unit(2) }, out _),
                Is.EqualTo(ReplicationDiffResult.InvalidEntityId), "zero id in the equal-id branch");
        }

        [Test]
        public void Diff_ReportsCapacityExceeded_PerSection()
        {
            var engine = new ServerSnapshotDiffEngine(1, 1, 1);

            Assert.That(engine.Diff(NoUnits, World(Unit(1), Unit(2)), out var adds),
                Is.EqualTo(ReplicationDiffResult.CapacityExceeded), "too many adds");
            Assert.That(adds.IsEmpty, Is.True);

            Assert.That(engine.Diff(World(Unit(1), Unit(2)), NoUnits, out var removes),
                Is.EqualTo(ReplicationDiffResult.CapacityExceeded), "too many tombstones");
            Assert.That(removes.IsEmpty, Is.True);

            Assert.That(engine.Diff(World(Unit(1, posX: 1), Unit(2, posX: 2)),
                World(Unit(1, posX: 9), Unit(2, posX: 9)), out var updates),
                Is.EqualTo(ReplicationDiffResult.CapacityExceeded), "too many updates");
            Assert.That(updates.IsEmpty, Is.True);
        }

        [Test]
        public void Diff_ReusesItsPreallocatedBuffer_BetweenCalls()
        {
            var engine = NewEngine();
            var previous = World(Unit(1), Unit(2));

            engine.Diff(previous, World(Unit(1, posX: 111), Unit(2, posX: 222)), out var first);
            Assert.That(first.UpdateCount, Is.EqualTo(2));
            var firstBuffer = first.UpdateBuffer;

            engine.Diff(previous, previous, out var second);
            Assert.That(second.IsEmpty, Is.True, "the second diff must not append to the first");
            Assert.That(second.UpdateBuffer, Is.SameAs(firstBuffer),
                "the engine keeps exactly one preallocated output buffer");

            engine.Diff(previous, World(Unit(1, posX: 333)), out var third);
            Assert.That(third.UpdateCount, Is.EqualTo(1));
            Assert.That(third.Updates[0].Position, Is.EqualTo(new WorldPointMm(333, 200)));
        }

        [Test]
        public void Diff_OutputIsAcceptedByTheCoreWireCodec()
        {
            var engine = NewEngine();
            var previous = World(Unit(1), Unit(2), Unit(3));
            var current = World(Unit(1, posX: 111), Unit(3, health: 5), Unit(4, owner: 2));

            Assert.That(engine.Diff(previous, current, out var changeSet), Is.EqualTo(ReplicationDiffResult.Ok));

            var header = DeltaSnapshotHeader.CreateDelta(
                20, 19, DeltaFlags.None, 0,
                (ushort)changeSet.AddCount, (ushort)changeSet.UpdateCount, (ushort)changeSet.RemoveCount);
            var packet = new byte[DeltaSnapshotWireCodec.GetMaxEncodedSize(
                changeSet.AddCount, changeSet.UpdateCount, changeSet.RemoveCount)];

            var encoded = DeltaSnapshotWireCodec.TryEncode(
                header, changeSet.Adds, changeSet.Updates, changeSet.Removes, packet, out var written);

            Assert.That(encoded, Is.EqualTo(DeltaCodecResult.Ok), "diff output must be directly encodable");

            var addSink = new DeltaAddRecord[changeSet.AddCount];
            var updateSink = new DeltaUpdateRecord[changeSet.UpdateCount];
            var removeSink = new DeltaRemoveRecord[changeSet.RemoveCount];
            var decoded = DeltaSnapshotWireCodec.TryDecode(
                packet.AsSpan(0, written), out var decodedHeader, addSink, updateSink, removeSink);

            Assert.That(decoded, Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(decodedHeader.Tick, Is.EqualTo(20UL));
            Assert.That(addSink, Is.EqualTo(changeSet.Adds.ToArray()));
            Assert.That(updateSink, Is.EqualTo(changeSet.Updates.ToArray()));
            Assert.That(removeSink, Is.EqualTo(changeSet.Removes.ToArray()));
        }

        private static int EncodeSingleUpdate(in DeltaUpdateRecord update)
        {
            var updates = new[] { update };
            var header = DeltaSnapshotHeader.CreateDelta(
                2, 1, DeltaFlags.None, 0, 0, 1, 0);
            var packet = new byte[DeltaSnapshotWireCodec.GetMaxEncodedSize(0, 1, 0)];

            var result = DeltaSnapshotWireCodec.TryEncode(
                header,
                Array.Empty<DeltaAddRecord>(),
                updates,
                Array.Empty<DeltaRemoveRecord>(),
                packet,
                out var written);

            Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok), "the update record must encode");
            return written;
        }
    }
}
