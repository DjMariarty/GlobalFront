using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;

namespace GlobalFront.Server.Replication
{
    /// <summary>Reason the last <see cref="ReplicationChangeSetBuilder.TryMerge"/> failed.</summary>
    public enum ReplicationMergeFailure : byte
    {
        None = 0,

        /// <summary>The merged change-set does not fit into the builder capacity.</summary>
        CapacityExceeded = 1,

        /// <summary>An incoming section was not in canonical ascending entity-id order.</summary>
        UnorderedInput = 2
    }

    /// <summary>
    /// Accumulates change-sets of consecutive ticks into one cumulative
    /// (establishing) change-set, as required by ADR-010: the receiver must be
    /// able to move from the state at <c>BaseTick</c> to the state at
    /// <c>TargetTick</c> with a single packet, even when intermediate ticks were
    /// lost.
    ///
    /// Merge rules (later tick wins):
    /// <list type="bullet">
    /// <item>UPDATE + UPDATE: dirty masks are OR-ed, every field takes its last
    /// value (<c>last-wins per field</c>).</item>
    /// <item>ADD + UPDATE: the update is folded into the ADD record, because the
    /// receiver does not know the entity yet and an ADD carries absolute state.</item>
    /// <item>ADD + REMOVE: the entity was born and died inside the merged window,
    /// so both records disappear.</item>
    /// <item>UPDATE + REMOVE: only the tombstone survives.</item>
    /// <item>REMOVE + REMOVE: idempotent, a single tombstone with the last cause.</item>
    /// <item>OD-14: a cleared move target never carries coordinates, so the
    /// <see cref="UnitDirtyMask.MoveTarget"/> bit is dropped when the merged
    /// <see cref="UnitDirtyMask.HasMoveTarget"/> value is 0.</item>
    /// </list>
    ///
    /// Zero-GC: the builder owns exactly two preallocated buffer sides and
    /// ping-pongs between them, so merging never allocates and a failed merge
    /// leaves the previous accumulator untouched.
    /// </summary>
    public sealed class ReplicationChangeSetBuilder
    {
        private readonly ReplicationChangeSetBuffer[] _sides;

        private int _activeSide;
        private int _addCount;
        private int _updateCount;
        private int _removeCount;

        public ReplicationChangeSetBuilder(int addCapacity, int updateCapacity, int removeCapacity)
        {
            _sides = new[]
            {
                new ReplicationChangeSetBuffer(addCapacity, updateCapacity, removeCapacity),
                new ReplicationChangeSetBuffer(addCapacity, updateCapacity, removeCapacity)
            };
            LastFailure = ReplicationMergeFailure.None;
        }

        public int AddCapacity => _sides[0].AddCapacity;

        public int UpdateCapacity => _sides[0].UpdateCapacity;

        public int RemoveCapacity => _sides[0].RemoveCapacity;

        /// <summary>Why the last merge failed; <see cref="ReplicationMergeFailure.None"/> on success.</summary>
        public ReplicationMergeFailure LastFailure { get; private set; }

        /// <summary>The cumulative change-set merged so far, ascending per section.</summary>
        public ReplicationChangeSet ChangeSet => _sides[_activeSide].AsChangeSet(_addCount, _updateCount, _removeCount);

        /// <summary>Discards the accumulated change-set; buffers stay allocated.</summary>
        public void Clear()
        {
            _addCount = 0;
            _updateCount = 0;
            _removeCount = 0;
            LastFailure = ReplicationMergeFailure.None;
        }

        /// <summary>
        /// Folds one tick's change-set into the accumulator. Both sides must be
        /// in canonical ascending entity-id order.
        /// </summary>
        /// <returns>
        /// False when the merged result does not fit or the input is unordered;
        /// the accumulator then keeps its previous content and
        /// <see cref="LastFailure"/> explains why. A false result means the
        /// receiver cannot be advanced by deltas and needs a fresh keyframe.
        /// </returns>
        public bool TryMerge(in ReplicationChangeSet incoming)
        {
            var source = _sides[_activeSide];
            var target = _sides[1 - _activeSide];

            var sourceAdds = source.Adds.Slice(0, _addCount);
            var sourceUpdates = source.Updates.Slice(0, _updateCount);
            var sourceRemoves = source.Removes.Slice(0, _removeCount);
            var incomingAdds = incoming.Adds;
            var incomingUpdates = incoming.Updates;
            var incomingRemoves = incoming.Removes;

            var targetAdds = target.Adds;
            var targetUpdates = target.Updates;
            var targetRemoves = target.Removes;

            var sa = 0;
            var su = 0;
            var sr = 0;
            var ia = 0;
            var iu = 0;
            var ir = 0;
            var ta = 0;
            var tu = 0;
            var tr = 0;
            var previousId = 0UL;
            var first = true;

            while (sa < sourceAdds.Length || su < sourceUpdates.Length || sr < sourceRemoves.Length ||
                   ia < incomingAdds.Length || iu < incomingUpdates.Length || ir < incomingRemoves.Length)
            {
                var id = ulong.MaxValue;
                if (sa < sourceAdds.Length)
                {
                    id = Min(id, sourceAdds[sa].Entity.Value);
                }

                if (su < sourceUpdates.Length)
                {
                    id = Min(id, sourceUpdates[su].Entity.Value);
                }

                if (sr < sourceRemoves.Length)
                {
                    id = Min(id, sourceRemoves[sr].Entity.Value);
                }

                if (ia < incomingAdds.Length)
                {
                    id = Min(id, incomingAdds[ia].Entity.Value);
                }

                if (iu < incomingUpdates.Length)
                {
                    id = Min(id, incomingUpdates[iu].Entity.Value);
                }

                if (ir < incomingRemoves.Length)
                {
                    id = Min(id, incomingRemoves[ir].Entity.Value);
                }

                if (!first && id < previousId)
                {
                    LastFailure = ReplicationMergeFailure.UnorderedInput;
                    return false;
                }

                previousId = id;
                first = false;

                var fromSourceAdd = sa < sourceAdds.Length && sourceAdds[sa].Entity.Value == id;
                var fromSourceUpdate = su < sourceUpdates.Length && sourceUpdates[su].Entity.Value == id;
                var fromSourceRemove = sr < sourceRemoves.Length && sourceRemoves[sr].Entity.Value == id;
                var fromIncomingAdd = ia < incomingAdds.Length && incomingAdds[ia].Entity.Value == id;
                var fromIncomingUpdate = iu < incomingUpdates.Length && incomingUpdates[iu].Entity.Value == id;
                var fromIncomingRemove = ir < incomingRemoves.Length && incomingRemoves[ir].Entity.Value == id;

                var state = FoldState.Absent;
                var add = default(DeltaAddRecord);
                var update = default(DeltaUpdateRecord);
                var cause = (byte)0;

                if (fromSourceAdd)
                {
                    state = FoldState.Added;
                    add = sourceAdds[sa];
                }
                else if (fromSourceUpdate)
                {
                    state = FoldState.Updated;
                    update = sourceUpdates[su];
                }
                else if (fromSourceRemove)
                {
                    state = FoldState.Removed;
                    cause = sourceRemoves[sr].Cause;
                }

                if (fromIncomingAdd)
                {
                    ApplyAdd(ref state, ref add, in incomingAdds[ia]);
                }

                if (fromIncomingUpdate)
                {
                    ApplyUpdate(ref state, ref add, ref update, in incomingUpdates[iu]);
                }

                if (fromIncomingRemove)
                {
                    ApplyRemove(ref state, ref add, ref update, ref cause, in incomingRemoves[ir]);
                }

                if (fromSourceAdd)
                {
                    sa++;
                }

                if (fromSourceUpdate)
                {
                    su++;
                }

                if (fromSourceRemove)
                {
                    sr++;
                }

                if (fromIncomingAdd)
                {
                    ia++;
                }

                if (fromIncomingUpdate)
                {
                    iu++;
                }

                if (fromIncomingRemove)
                {
                    ir++;
                }

                switch (state)
                {
                    case FoldState.Added:
                        if (ta >= targetAdds.Length)
                        {
                            LastFailure = ReplicationMergeFailure.CapacityExceeded;
                            return false;
                        }

                        targetAdds[ta++] = add;
                        break;

                    case FoldState.Updated:
                        if (tu >= targetUpdates.Length)
                        {
                            LastFailure = ReplicationMergeFailure.CapacityExceeded;
                            return false;
                        }

                        targetUpdates[tu++] = update;
                        break;

                    case FoldState.Removed:
                        if (tr >= targetRemoves.Length)
                        {
                            LastFailure = ReplicationMergeFailure.CapacityExceeded;
                            return false;
                        }

                        targetRemoves[tr++] = new DeltaRemoveRecord(new EntityId(id), cause);
                        break;
                }
            }

            _activeSide = 1 - _activeSide;
            _addCount = ta;
            _updateCount = tu;
            _removeCount = tr;
            LastFailure = ReplicationMergeFailure.None;
            return true;
        }

        public override string ToString() =>
            $"ChangeSetBuilder(add={_addCount}/{AddCapacity}, update={_updateCount}/{UpdateCapacity}, " +
            $"remove={_removeCount}/{RemoveCapacity}, failure={LastFailure})";

        private static ulong Min(ulong left, ulong right) => left < right ? left : right;

        /// <summary>State of one entity inside the merged window.</summary>
        private enum FoldState : byte
        {
            Absent = 0,
            Added = 1,
            Updated = 2,
            Removed = 3
        }

        private static void ApplyAdd(ref FoldState state, ref DeltaAddRecord add, in DeltaAddRecord incoming)
        {
            // A later ADD carries the absolute state and supersedes whatever the
            // accumulator held. Entity ids are never reused inside a match, so
            // an ADD after an UPDATE/REMOVE of the same id is a defensive path.
            state = FoldState.Added;
            add = incoming;
        }

        private static void ApplyUpdate(
            ref FoldState state,
            ref DeltaAddRecord add,
            ref DeltaUpdateRecord update,
            in DeltaUpdateRecord incoming)
        {
            switch (state)
            {
                case FoldState.Added:
                    add = FoldUpdateIntoAdd(in add, in incoming);
                    break;

                case FoldState.Updated:
                    update = MergeUpdates(in update, in incoming);
                    break;

                case FoldState.Removed:
                    // The entity is already gone; a later update is dropped.
                    break;

                default:
                    state = FoldState.Updated;
                    update = incoming;
                    break;
            }
        }

        private static void ApplyRemove(
            ref FoldState state,
            ref DeltaAddRecord add,
            ref DeltaUpdateRecord update,
            ref byte cause,
            in DeltaRemoveRecord incoming)
        {
            if (state == FoldState.Added)
            {
                // Born and died inside the merged window: the receiver never
                // knew this entity, so both records cancel out.
                state = FoldState.Absent;
                add = default(DeltaAddRecord);
                return;
            }

            state = FoldState.Removed;
            update = default(DeltaUpdateRecord);
            cause = incoming.Cause;
        }

        /// <summary>OR-ed mask, last-wins per field (ADR-010).</summary>
        private static DeltaUpdateRecord MergeUpdates(in DeltaUpdateRecord accumulated, in DeltaUpdateRecord incoming)
        {
            var mask = (byte)(accumulated.DirtyMask | incoming.DirtyMask);
            var owner = HasFlag(incoming.DirtyMask, UnitDirtyMask.Owner) ? incoming.Owner : accumulated.Owner;
            var position = HasFlag(incoming.DirtyMask, UnitDirtyMask.Position) ? incoming.Position : accumulated.Position;
            var health = HasFlag(incoming.DirtyMask, UnitDirtyMask.Health)
                ? incoming.CurrentHealth
                : accumulated.CurrentHealth;
            var hasMoveTarget = HasFlag(incoming.DirtyMask, UnitDirtyMask.HasMoveTarget)
                ? incoming.HasMoveTarget
                : accumulated.HasMoveTarget;
            var moveTarget = HasFlag(incoming.DirtyMask, UnitDirtyMask.MoveTarget)
                ? incoming.MoveTarget
                : accumulated.MoveTarget;
            var attackTarget = HasFlag(incoming.DirtyMask, UnitDirtyMask.AttackTarget)
                ? incoming.AttackTarget
                : accumulated.AttackTarget;
            var autoAcquire = HasFlag(incoming.DirtyMask, UnitDirtyMask.AutoAcquire)
                ? incoming.AutoAcquireEnemies
                : accumulated.AutoAcquireEnemies;

            CanonicalizeMoveTarget(ref mask, ref hasMoveTarget, ref moveTarget);

            return new DeltaUpdateRecord(
                accumulated.Entity,
                mask,
                owner,
                position,
                health,
                hasMoveTarget,
                moveTarget,
                attackTarget,
                autoAcquire);
        }

        /// <summary>Applies masked fields onto the absolute ADD record.</summary>
        private static DeltaAddRecord FoldUpdateIntoAdd(in DeltaAddRecord add, in DeltaUpdateRecord update)
        {
            var mask = update.DirtyMask;
            var owner = HasFlag(mask, UnitDirtyMask.Owner) ? update.Owner : add.Owner;
            var position = HasFlag(mask, UnitDirtyMask.Position) ? update.Position : add.Position;
            var health = HasFlag(mask, UnitDirtyMask.Health) ? update.CurrentHealth : add.CurrentHealth;
            var hasMoveTarget = HasFlag(mask, UnitDirtyMask.HasMoveTarget) ? update.HasMoveTarget : add.HasMoveTarget;

            var moveTarget = add.MoveTarget;
            if (HasFlag(mask, UnitDirtyMask.HasMoveTarget) && !update.HasMoveTarget)
            {
                // OD-14: the target is cleared, so no coordinates may survive.
                moveTarget = default(WorldPointMm);
            }
            else if (HasFlag(mask, UnitDirtyMask.MoveTarget))
            {
                moveTarget = update.MoveTarget;
            }

            var attackTarget = HasFlag(mask, UnitDirtyMask.AttackTarget) ? update.AttackTarget : add.AttackTarget;
            var autoAcquire = HasFlag(mask, UnitDirtyMask.AutoAcquire)
                ? update.AutoAcquireEnemies
                : add.AutoAcquireEnemies;

            return new DeltaAddRecord(
                add.Entity,
                owner,
                position,
                health,
                hasMoveTarget,
                moveTarget,
                attackTarget,
                autoAcquire);
        }

        /// <summary>
        /// OD-14: when the merged record clears the move target, the coordinate
        /// bit and the coordinate values are dropped so a cleared target never
        /// travels with stale coordinates.
        /// </summary>
        private static void CanonicalizeMoveTarget(ref byte mask, ref bool hasMoveTarget, ref WorldPointMm moveTarget)
        {
            if (HasFlag(mask, UnitDirtyMask.HasMoveTarget) && !hasMoveTarget)
            {
                mask = (byte)(mask & ~(byte)UnitDirtyMask.MoveTarget);
                moveTarget = default(WorldPointMm);
            }
        }

        private static bool HasFlag(byte mask, UnitDirtyMask flag) => (mask & (byte)flag) == (byte)flag;
    }
}
