using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Client.Replication
{
    /// <summary>
    /// Bit-exact behaviour of the client-side replicated world table
    /// (Phase 2.6, step 2.6.3, ADR-010).
    ///
    /// The property under test is that the client table reproduces the
    /// authoritative record semantics field by field: ADD installs every field,
    /// UPDATE touches exactly the fields selected by <c>dirtyMask</c> (including
    /// the OD-14 move-target clear) and REMOVE drops the unit idempotently.
    /// </summary>
    [TestFixture]
    public sealed class ClientReplicationWorldTests
    {
        internal static DeltaAddRecord Add(
            ulong id,
            byte owner = 1,
            int posX = 100,
            int posZ = 200,
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

        internal static DeltaUpdateRecord Update(
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

        internal static DeltaRemoveRecord Remove(
            ulong id,
            DeltaRemoveCause cause = DeltaRemoveCause.Destroyed)
        {
            return new DeltaRemoveRecord(new EntityId(id), cause);
        }

        private static ClientUnitState Spawned(
            ClientReplicationWorld world,
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
            var result = world.ApplyAdd(Add(
                id, owner, posX, posZ, health, hasMoveTarget,
                moveTargetX, moveTargetZ, attackTarget, autoAcquire));
            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok), $"entity {id} must spawn");
            Assert.That(world.TryGet(new EntityId(id), out var state), Is.True);
            return state;
        }

        // ---------------------------------------------------------------
        // ADD
        // ---------------------------------------------------------------

        [Test]
        public void ApplyAdd_InstallsEveryFieldBitExact()
        {
            var world = new ClientReplicationWorld(16);

            var result = world.ApplyAdd(Add(
                42, owner: 7, posX: -123456, posZ: 987654, health: 55,
                hasMoveTarget: true, moveTargetX: 4000, moveTargetZ: -5000,
                attackTarget: 99, autoAcquire: true));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.LiveCount, Is.EqualTo(1));
            Assert.That(world.TryGet(new EntityId(42), out var state), Is.True);
            Assert.That(state.Entity, Is.EqualTo(new EntityId(42)));
            Assert.That(state.Owner, Is.EqualTo(new PlayerId(7)));
            Assert.That(state.PosX, Is.EqualTo(-123456));
            Assert.That(state.PosZ, Is.EqualTo(987654));
            Assert.That(state.Health, Is.EqualTo(55));
            Assert.That(state.HasMoveTarget, Is.True);
            Assert.That(state.MoveTargetX, Is.EqualTo(4000));
            Assert.That(state.MoveTargetZ, Is.EqualTo(-5000));
            Assert.That(state.AttackTarget, Is.EqualTo(new EntityId(99)));
            Assert.That(state.AutoAcquire, Is.True);
            Assert.That(state.Position, Is.EqualTo(new WorldPointMm(-123456, 987654)));
            Assert.That(state.MoveTarget, Is.EqualTo(new WorldPointMm(4000, -5000)));
        }

        [Test]
        public void ApplyAdd_WithoutMoveTarget_StoresZeroedTargetCoordinates()
        {
            var world = new ClientReplicationWorld(4);

            Spawned(world, 1, hasMoveTarget: false, moveTargetX: 0, moveTargetZ: 0);

            Assert.That(world.TryGet(new EntityId(1), out var state), Is.True);
            Assert.That(state.HasMoveTarget, Is.False);
            Assert.That(state.MoveTarget, Is.EqualTo(new WorldPointMm(0, 0)));
        }

        [Test]
        public void ApplyAdd_SameEntityTwice_IsRejectedAndKeepsTheFirstState()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 5, posX: 10);

            var result = world.ApplyAdd(Add(5, posX: 999));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.DuplicateEntity));
            Assert.That(world.LiveCount, Is.EqualTo(1));
            Assert.That(world.DuplicateAddCount, Is.EqualTo(1));
            Assert.That(world.TryGet(new EntityId(5), out var state), Is.True);
            Assert.That(state.PosX, Is.EqualTo(10), "a duplicate ADD must never overwrite the live unit");
        }

        [Test]
        public void ApplyAdd_InvalidEntityId_IsRejected()
        {
            var world = new ClientReplicationWorld(4);

            Assert.That(world.ApplyAdd(Add(0)), Is.EqualTo(ClientWorldApplyResult.InvalidEntityId));
            Assert.That(world.LiveCount, Is.Zero);
            Assert.That(world.InvalidRecordCount, Is.EqualTo(1));
        }

        [Test]
        public void ApplyAdd_BeyondCapacity_IsRejectedAndCountsTheReject()
        {
            var world = new ClientReplicationWorld(2);
            Spawned(world, 1);
            Spawned(world, 2);

            var result = world.ApplyAdd(Add(3));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.CapacityExceeded));
            Assert.That(world.LiveCount, Is.EqualTo(2));
            Assert.That(world.CapacityRejectCount, Is.EqualTo(1));
            Assert.That(world.HasCapacityFor(1), Is.False);
            Assert.That(world.Contains(new EntityId(3)), Is.False);
        }

        [Test]
        public void ApplyAdd_AfterRemoval_ReusesTheFreedSlotWithoutGrowing()
        {
            var world = new ClientReplicationWorld(2);
            Spawned(world, 1);
            Spawned(world, 2);

            Assert.That(world.ApplyRemove(Remove(1)), Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.ApplyAdd(Add(3)), Is.EqualTo(ClientWorldApplyResult.Ok),
                "the freed slot must be reusable: entity ids are never recycled by the protocol");
            Assert.That(world.LiveCount, Is.EqualTo(2));
            Assert.That(world.Contains(new EntityId(3)), Is.True);
            Assert.That(world.Contains(new EntityId(1)), Is.False);
        }

        // ---------------------------------------------------------------
        // UPDATE: one dirty bit at a time
        // ---------------------------------------------------------------

        [Test]
        public void ApplyUpdate_OwnerBit_ChangesOnlyTheOwner()
        {
            var world = new ClientReplicationWorld(4);
            var before = Spawned(world, 1, owner: 2, posX: 11, posZ: 22, health: 77,
                hasMoveTarget: true, moveTargetX: 33, moveTargetZ: 44, attackTarget: 9, autoAcquire: true);

            var result = world.ApplyUpdate(Update(1, UnitDirtyMask.Owner, owner: 5));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            AssertOnlyOwnerChanged(world, before);
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.Owner, Is.EqualTo(new PlayerId(5)));
        }

        [Test]
        public void ApplyUpdate_PositionBit_ChangesOnlyThePosition()
        {
            var world = new ClientReplicationWorld(4);
            var before = Spawned(world, 1, owner: 2, posX: 11, posZ: 22, health: 77,
                hasMoveTarget: true, moveTargetX: 33, moveTargetZ: 44, attackTarget: 9, autoAcquire: true);

            var result = world.ApplyUpdate(Update(1, UnitDirtyMask.Position, posX: -500, posZ: 600));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.Position, Is.EqualTo(new WorldPointMm(-500, 600)));
            AssertUnrelatedFieldsUnchanged(before, after, UnitDirtyMask.Position);
        }

        [Test]
        public void ApplyUpdate_HealthBit_ChangesOnlyTheHealth()
        {
            var world = new ClientReplicationWorld(4);
            var before = Spawned(world, 1, owner: 2, posX: 11, posZ: 22, health: 77,
                hasMoveTarget: true, moveTargetX: 33, moveTargetZ: 44, attackTarget: 9, autoAcquire: true);

            var result = world.ApplyUpdate(Update(1, UnitDirtyMask.Health, health: 3));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.Health, Is.EqualTo(3));
            AssertUnrelatedFieldsUnchanged(before, after, UnitDirtyMask.Health);
        }

        [Test]
        public void ApplyUpdate_HasMoveTargetBitSetToOne_ChangesOnlyTheFlag()
        {
            var world = new ClientReplicationWorld(4);
            var before = Spawned(world, 1, hasMoveTarget: false, moveTargetX: 0, moveTargetZ: 0);

            var result = world.ApplyUpdate(
                Update(1, UnitDirtyMask.HasMoveTarget, hasMoveTarget: true));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.HasMoveTarget, Is.True);
            Assert.That(after.MoveTarget, Is.EqualTo(new WorldPointMm(0, 0)),
                "the coordinate bit is clear, so the stored target must not move");
            AssertUnrelatedFieldsUnchanged(before, after, UnitDirtyMask.HasMoveTarget);
        }

        [Test]
        public void ApplyUpdate_MoveTargetBit_ChangesOnlyTheCoordinates()
        {
            var world = new ClientReplicationWorld(4);
            var before = Spawned(world, 1, owner: 2, posX: 11, posZ: 22, health: 77,
                hasMoveTarget: true, moveTargetX: 33, moveTargetZ: 44, attackTarget: 9, autoAcquire: true);

            var result = world.ApplyUpdate(
                Update(1, UnitDirtyMask.MoveTarget, moveTargetX: 8000, moveTargetZ: -9000));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.MoveTarget, Is.EqualTo(new WorldPointMm(8000, -9000)));
            Assert.That(after.HasMoveTarget, Is.True);
            AssertUnrelatedFieldsUnchanged(before, after, UnitDirtyMask.MoveTarget);
        }

        [Test]
        public void ApplyUpdate_AttackTargetBit_ChangesOnlyTheAttackTarget()
        {
            var world = new ClientReplicationWorld(4);
            var before = Spawned(world, 1, owner: 2, posX: 11, posZ: 22, health: 77,
                hasMoveTarget: true, moveTargetX: 33, moveTargetZ: 44, attackTarget: 9, autoAcquire: true);

            var result = world.ApplyUpdate(Update(1, UnitDirtyMask.AttackTarget, attackTarget: 0));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.AttackTarget, Is.EqualTo(new EntityId(0)));
            AssertUnrelatedFieldsUnchanged(before, after, UnitDirtyMask.AttackTarget);
        }

        [Test]
        public void ApplyUpdate_AutoAcquireBit_ChangesOnlyTheFlag()
        {
            var world = new ClientReplicationWorld(4);
            var before = Spawned(world, 1, owner: 2, posX: 11, posZ: 22, health: 77,
                hasMoveTarget: true, moveTargetX: 33, moveTargetZ: 44, attackTarget: 9, autoAcquire: false);

            var result = world.ApplyUpdate(Update(1, UnitDirtyMask.AutoAcquire, autoAcquire: true));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.AutoAcquire, Is.True);
            AssertUnrelatedFieldsUnchanged(before, after, UnitDirtyMask.AutoAcquire);
        }

        [Test]
        public void ApplyUpdate_EmptyMask_ChangesNothing()
        {
            var world = new ClientReplicationWorld(4);
            var before = Spawned(world, 1, owner: 2, posX: 11, posZ: 22, health: 77,
                hasMoveTarget: true, moveTargetX: 33, moveTargetZ: 44, attackTarget: 9, autoAcquire: true);

            var result = world.ApplyUpdate(Update(1, UnitDirtyMask.None,
                owner: 8, posX: 1, posZ: 2, health: 3, moveTargetX: 4, moveTargetZ: 5,
                attackTarget: 6, autoAcquire: false));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after, Is.EqualTo(before), "an empty mask transmits no field at all");
        }

        [Test]
        public void ApplyUpdate_FullMask_InstallsEveryTransmittedField()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1);

            const UnitDirtyMask full = UnitDirtyMask.Owner | UnitDirtyMask.Position | UnitDirtyMask.Health |
                UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget | UnitDirtyMask.AttackTarget |
                UnitDirtyMask.AutoAcquire;
            var result = world.ApplyUpdate(Update(1, full,
                owner: 9, posX: -1, posZ: -2, health: 42, hasMoveTarget: true,
                moveTargetX: 700, moveTargetZ: 800, attackTarget: 17, autoAcquire: true));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.Entity, Is.EqualTo(new EntityId(1)), "identity is never dirty");
            Assert.That(after.Owner, Is.EqualTo(new PlayerId(9)));
            Assert.That(after.Position, Is.EqualTo(new WorldPointMm(-1, -2)));
            Assert.That(after.Health, Is.EqualTo(42));
            Assert.That(after.HasMoveTarget, Is.True);
            Assert.That(after.MoveTarget, Is.EqualTo(new WorldPointMm(700, 800)));
            Assert.That(after.AttackTarget, Is.EqualTo(new EntityId(17)));
            Assert.That(after.AutoAcquire, Is.True);
        }

        // ---------------------------------------------------------------
        // UPDATE: OD-14
        // ---------------------------------------------------------------

        [Test]
        public void ApplyUpdate_OD14_ClearingTheMoveTargetZeroesTheCoordinates()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1, hasMoveTarget: true, moveTargetX: 4321, moveTargetZ: 1234);

            // OD-14: the clear sets bit 3 with HasMoveTarget = 0 and transmits no
            // coordinates, even when bit 4 is set as well.
            var result = world.ApplyUpdate(Update(1,
                UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                hasMoveTarget: false, moveTargetX: 0, moveTargetZ: 0));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.HasMoveTarget, Is.False);
            Assert.That(after.MoveTarget, Is.EqualTo(new WorldPointMm(0, 0)),
                "a cleared target carries no coordinates");
        }

        [Test]
        public void ApplyUpdate_OD14_ClearWithoutTheCoordinateBit_StillZeroesTheCoordinates()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1, hasMoveTarget: true, moveTargetX: 4321, moveTargetZ: 1234);

            var result = world.ApplyUpdate(
                Update(1, UnitDirtyMask.HasMoveTarget, hasMoveTarget: false));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.HasMoveTarget, Is.False);
            Assert.That(after.MoveTargetX, Is.Zero);
            Assert.That(after.MoveTargetZ, Is.Zero);
        }

        [Test]
        public void ApplyUpdate_MoveTargetWithoutTheFlagBit_KeepsTheExistingFlag()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1, hasMoveTarget: false);

            var result = world.ApplyUpdate(
                Update(1, UnitDirtyMask.MoveTarget, moveTargetX: 55, moveTargetZ: 66));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.HasMoveTarget, Is.False, "bit 3 was not transmitted, so the flag is untouched");
            Assert.That(after.MoveTarget, Is.EqualTo(new WorldPointMm(55, 66)));
        }

        [Test]
        public void ApplyUpdate_ArmedTargetWithBothBits_SetsFlagAndCoordinates()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1, hasMoveTarget: false);

            var result = world.ApplyUpdate(Update(1,
                UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                hasMoveTarget: true, moveTargetX: 55, moveTargetZ: 66));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after.HasMoveTarget, Is.True);
            Assert.That(after.MoveTarget, Is.EqualTo(new WorldPointMm(55, 66)));
        }

        // ---------------------------------------------------------------
        // UPDATE: rejection paths
        // ---------------------------------------------------------------

        [Test]
        public void ApplyUpdate_UnknownEntity_IsRejectedAndChangesNothing()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1);

            var result = world.ApplyUpdate(Update(77, UnitDirtyMask.Position, posX: 1, posZ: 2));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.UnknownEntity));
            Assert.That(world.UnknownUpdateCount, Is.EqualTo(1));
            Assert.That(world.LiveCount, Is.EqualTo(1));
            Assert.That(world.Contains(new EntityId(77)), Is.False);
        }

        [Test]
        public void ApplyUpdate_InvalidEntityId_IsRejected()
        {
            var world = new ClientReplicationWorld(4);

            Assert.That(
                world.ApplyUpdate(Update(0, UnitDirtyMask.Health, health: 1)),
                Is.EqualTo(ClientWorldApplyResult.InvalidEntityId));
            Assert.That(world.InvalidRecordCount, Is.EqualTo(1));
        }

        [Test]
        public void ApplyUpdate_ReservedDirtyMaskBit_IsRejectedAndLeavesTheUnitUntouched()
        {
            var world = new ClientReplicationWorld(4);
            var before = Spawned(world, 1, health: 100);

            var result = world.ApplyUpdate(
                Update(1, UnitDirtyMask.Health | UnitDirtyMask.ReservedExtension, health: 1));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.ReservedDirtyMaskBit));
            Assert.That(world.InvalidRecordCount, Is.EqualTo(1));
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            Assert.That(after, Is.EqualTo(before), "a v1 table must not guess at an unknown mask bit");
        }

        // ---------------------------------------------------------------
        // REMOVE
        // ---------------------------------------------------------------

        [Test]
        public void ApplyRemove_DestroyedUnit_DropsItFromTheTable()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1);
            Spawned(world, 2);

            var result = world.ApplyRemove(Remove(1, DeltaRemoveCause.Destroyed));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.LiveCount, Is.EqualTo(1));
            Assert.That(world.Contains(new EntityId(1)), Is.False);
            Assert.That(world.TryGet(new EntityId(1), out _), Is.False);
            Assert.That(world.Contains(new EntityId(2)), Is.True, "the neighbour must survive the index shift");
            Assert.That(world.DestroyedRemoveCount, Is.EqualTo(1));
        }

        [Test]
        public void ApplyRemove_FogOfWarHidden_IsCountedSeparately()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1);

            var result = world.ApplyRemove(Remove(1, DeltaRemoveCause.FogOfWarHidden));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.FogOfWarRemoveCount, Is.EqualTo(1));
            Assert.That(world.DestroyedRemoveCount, Is.Zero);
        }

        [Test]
        public void ApplyRemove_UnknownEntity_IsAnIdempotentNoOp()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1);

            var result = world.ApplyRemove(Remove(123));

            Assert.That(result, Is.EqualTo(ClientWorldApplyResult.UnknownEntity),
                "removing an unknown id is a no-op, reported but never a protocol error");
            Assert.That(world.UnknownRemoveCount, Is.EqualTo(1));
            Assert.That(world.LiveCount, Is.EqualTo(1));
            Assert.That(world.Contains(new EntityId(1)), Is.True);
        }

        [Test]
        public void ApplyRemove_Twice_ReportsTheSecondAsUnknown()
        {
            var world = new ClientReplicationWorld(4);
            Spawned(world, 1);

            Assert.That(world.ApplyRemove(Remove(1)), Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.ApplyRemove(Remove(1)), Is.EqualTo(ClientWorldApplyResult.UnknownEntity));
            Assert.That(world.LiveCount, Is.Zero);
        }

        [Test]
        public void ApplyRemove_InvalidEntityId_IsRejected()
        {
            var world = new ClientReplicationWorld(4);

            Assert.That(world.ApplyRemove(Remove(0)), Is.EqualTo(ClientWorldApplyResult.InvalidEntityId));
            Assert.That(world.InvalidRecordCount, Is.EqualTo(1));
        }

        // ---------------------------------------------------------------
        // Reset / churn / enumeration
        // ---------------------------------------------------------------

        [Test]
        public void Reset_DropsEveryUnitAndKeepsTheBuffers()
        {
            var world = new ClientReplicationWorld(8);
            for (ulong id = 1; id <= 8; id++)
            {
                Spawned(world, id);
            }

            world.Reset();

            Assert.That(world.LiveCount, Is.Zero);
            Assert.That(world.Capacity, Is.EqualTo(8));
            Assert.That(world.ResetCount, Is.EqualTo(1));
            for (ulong id = 1; id <= 8; id++)
            {
                Assert.That(world.Contains(new EntityId(id)), Is.False, $"entity {id} must be gone");
            }
        }

        [Test]
        public void Reset_MakesTheWholeCapacityReusable()
        {
            var world = new ClientReplicationWorld(3);
            Spawned(world, 1);
            Spawned(world, 2);
            Spawned(world, 3);
            Assert.That(world.HasCapacityFor(1), Is.False);

            world.Reset();

            Assert.That(world.HasCapacityFor(3), Is.True);
            Assert.That(world.ApplyAdd(Add(1)), Is.EqualTo(ClientWorldApplyResult.Ok),
                "after a re-baseline the same ids may appear again in a fresh keyframe");
            Assert.That(world.ApplyAdd(Add(2)), Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.ApplyAdd(Add(3)), Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(world.LiveCount, Is.EqualTo(3));
        }

        [Test]
        public void Churn_AddRemoveCycles_KeepTheIndexConsistent()
        {
            var world = new ClientReplicationWorld(64);

            for (ulong id = 1; id <= 64; id++)
            {
                Assert.That(world.ApplyAdd(Add(id, posX: (int)id)), Is.EqualTo(ClientWorldApplyResult.Ok));
            }

            for (ulong id = 1; id <= 64; id += 2)
            {
                Assert.That(world.ApplyRemove(Remove(id)), Is.EqualTo(ClientWorldApplyResult.Ok));
            }

            Assert.That(world.LiveCount, Is.EqualTo(32));
            for (ulong id = 1; id <= 64; id++)
            {
                var expected = id % 2 == 0;
                Assert.That(world.Contains(new EntityId(id)), Is.EqualTo(expected), $"entity {id}");
            }

            for (ulong id = 65; id <= 96; id++)
            {
                Assert.That(world.ApplyAdd(Add(id, posX: (int)id)), Is.EqualTo(ClientWorldApplyResult.Ok));
            }

            Assert.That(world.LiveCount, Is.EqualTo(64));
            for (ulong id = 2; id <= 96; id++)
            {
                if (id % 2 == 0 || id >= 65)
                {
                    Assert.That(world.TryGet(new EntityId(id), out var state), Is.True, $"entity {id}");
                    Assert.That(state.PosX, Is.EqualTo((int)id));
                }
            }
        }

        [Test]
        public void CopyLiveStates_FillsTheCallerBufferInSlotOrder()
        {
            var world = new ClientReplicationWorld(8);
            Spawned(world, 3);
            Spawned(world, 1);
            Spawned(world, 2);

            var buffer = new ClientUnitState[8];
            var written = world.CopyLiveStates(buffer);

            Assert.That(written, Is.EqualTo(3));
            var seen = 0;
            for (var index = 0; index < written; index++)
            {
                Assert.That(buffer[index].Entity.IsValid, Is.True);
                Assert.That(world.Contains(buffer[index].Entity), Is.True);
                seen++;
            }

            Assert.That(seen, Is.EqualTo(3));
            Assert.That(world.CopyLiveStates(new ClientUnitState[2]), Is.EqualTo(2),
                "a short destination is filled up to its length and never overflows");
        }

        [Test]
        public void SlotEnumeration_ExposesEveryLiveUnitExactlyOnce()
        {
            var world = new ClientReplicationWorld(16);
            for (ulong id = 1; id <= 10; id++)
            {
                Spawned(world, id);
            }

            world.ApplyRemove(Remove(4));
            world.ApplyRemove(Remove(9));

            var live = 0;
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (!world.IsSlotLive(slot))
                {
                    continue;
                }

                Assert.That(world.TryGetSlotState(slot, out var state), Is.True);
                Assert.That(state.Entity.Value, Is.Not.EqualTo(4UL));
                Assert.That(state.Entity.Value, Is.Not.EqualTo(9UL));
                live++;
            }

            Assert.That(live, Is.EqualTo(8));
            Assert.That(live, Is.EqualTo(world.LiveCount));
        }

        [Test]
        public void SlotAccessors_RejectOutOfRangeIndices()
        {
            var world = new ClientReplicationWorld(4);

            Assert.That(world.IsSlotLive(-1), Is.False);
            Assert.That(world.IsSlotLive(4), Is.False);
            Assert.That(world.TryGetSlotState(-1, out _), Is.False);
            Assert.That(world.TryGetSlotState(4, out _), Is.False);
        }

        [Test]
        public void Constructor_RejectsNonPositiveCapacity()
        {
            Assert.That(() => new ClientReplicationWorld(0),
                Throws.InstanceOf<System.ArgumentOutOfRangeException>());
            Assert.That(() => new ClientReplicationWorld(-1),
                Throws.InstanceOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void DefaultCapacity_CoversThePhase26EntityTarget()
        {
            // ADR-010 targets 3000+ replicated entities; the default table must
            // hold that without resizing (resizing would allocate in the hot path).
            Assert.That(ClientReplicationWorld.DefaultCapacity, Is.GreaterThanOrEqualTo(3000));
        }

        [Test]
        public void ClientUnitState_EqualityIsFieldwise()
        {
            var left = new ClientUnitState(
                new EntityId(1), new PlayerId(2), 3, 4, 5, true, 6, 7, new EntityId(8), true);
            var same = new ClientUnitState(
                new EntityId(1), new PlayerId(2), 3, 4, 5, true, 6, 7, new EntityId(8), true);
            var other = new ClientUnitState(
                new EntityId(1), new PlayerId(2), 3, 4, 5, true, 6, 7, new EntityId(8), false);

            Assert.That(left, Is.EqualTo(same));
            Assert.That(left == same, Is.True);
            Assert.That(left != other, Is.True);
            Assert.That(left.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(left.IsLive, Is.True);
            Assert.That(default(ClientUnitState).IsLive, Is.False);
            Assert.That(left.ToString(), Does.Contain("entity=1"));
        }

        /// <summary>Asserts that only the owner changed.</summary>
        private static void AssertOnlyOwnerChanged(ClientReplicationWorld world, ClientUnitState before)
        {
            Assert.That(world.TryGet(new EntityId(1), out var after), Is.True);
            AssertUnrelatedFieldsUnchanged(before, after, UnitDirtyMask.Owner);
        }

        /// <summary>
        /// Asserts that every field not selected by <paramref name="changed"/>
        /// kept its previous value.
        /// </summary>
        private static void AssertUnrelatedFieldsUnchanged(
            ClientUnitState before,
            ClientUnitState after,
            UnitDirtyMask changed)
        {
            Assert.That(after.Entity, Is.EqualTo(before.Entity));

            if (!HasFlag(changed, UnitDirtyMask.Owner))
            {
                Assert.That(after.Owner, Is.EqualTo(before.Owner), "owner leaked");
            }

            if (!HasFlag(changed, UnitDirtyMask.Position))
            {
                Assert.That(after.Position, Is.EqualTo(before.Position), "position leaked");
            }

            if (!HasFlag(changed, UnitDirtyMask.Health))
            {
                Assert.That(after.Health, Is.EqualTo(before.Health), "health leaked");
            }

            if (!HasFlag(changed, UnitDirtyMask.HasMoveTarget))
            {
                Assert.That(after.HasMoveTarget, Is.EqualTo(before.HasMoveTarget), "hasMoveTarget leaked");
            }

            if (!HasFlag(changed, UnitDirtyMask.MoveTarget) && !HasFlag(changed, UnitDirtyMask.HasMoveTarget))
            {
                Assert.That(after.MoveTarget, Is.EqualTo(before.MoveTarget), "move target leaked");
            }

            if (!HasFlag(changed, UnitDirtyMask.AttackTarget))
            {
                Assert.That(after.AttackTarget, Is.EqualTo(before.AttackTarget), "attack target leaked");
            }

            if (!HasFlag(changed, UnitDirtyMask.AutoAcquire))
            {
                Assert.That(after.AutoAcquire, Is.EqualTo(before.AutoAcquire), "auto-acquire leaked");
            }

            // The archetype has no dirty bit at all, so it must survive every mask
            // combination unchanged: a merge that dropped it would silently strip
            // the client's mesh and health denominator on the next packet.
            Assert.That(after.UnitKind, Is.EqualTo(before.UnitKind), "unit kind leaked");
        }

        [Test]
        public void Add_InstallsUnitKind()
        {
            var world = new ClientReplicationWorld(16);

            Assert.That(
                world.ApplyAdd(Add(11, unitKind: UnitKinds.Tank)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(
                world.ApplyAdd(Add(12)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            Assert.That(world.TryGet(new EntityId(11), out var tank));
            Assert.That(tank.UnitKind, Is.EqualTo(UnitKinds.Tank));

            // A record from before OD-29 (or from the v1 snapshot path) yields
            // Unknown rather than borrowing the first real archetype.
            Assert.That(world.TryGet(new EntityId(12), out var legacy));
            Assert.That(legacy.UnitKind, Is.EqualTo(UnitKinds.Unknown));
        }

        [Test]
        public void Update_KeepsUnitKindAcrossEveryDirtyMask()
        {
            var world = new ClientReplicationWorld(16);
            Assert.That(
                world.ApplyAdd(Add(21, posX: 100, posZ: 200, unitKind: UnitKinds.Scout)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            var masks = new[]
            {
                UnitDirtyMask.Position,
                UnitDirtyMask.Health,
                UnitDirtyMask.Owner | UnitDirtyMask.Position,
                UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                UnitDirtyMask.AttackTarget | UnitDirtyMask.AutoAcquire,
            };

            for (var index = 0; index < masks.Length; index++)
            {
                Assert.That(
                    world.ApplyUpdate(Update(
                        21,
                        masks[index],
                        owner: 2,
                        posX: 900 + index,
                        posZ: 800,
                        health: 42,
                        hasMoveTarget: true,
                        moveTargetX: 10,
                        moveTargetZ: 20,
                        attackTarget: 77,
                        autoAcquire: true)),
                    Is.EqualTo(ClientWorldApplyResult.Ok));

                Assert.That(world.TryGet(new EntityId(21), out var after));
                Assert.That(after.UnitKind, Is.EqualTo(UnitKinds.Scout), $"mask {masks[index]}");
            }
        }

        private static bool HasFlag(UnitDirtyMask mask, UnitDirtyMask flag) =>
            ((byte)mask & (byte)flag) == (byte)flag;
    }
}
