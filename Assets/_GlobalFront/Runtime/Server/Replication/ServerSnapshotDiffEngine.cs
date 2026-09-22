using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;

namespace GlobalFront.Server.Replication
{
    /// <summary>Outcome of one <see cref="ServerSnapshotDiffEngine.Diff"/> pass.</summary>
    public enum ReplicationDiffResult : byte
    {
        /// <summary>The change-set is complete and consistent.</summary>
        Ok = 0,

        /// <summary>An input snapshot was not in canonical ascending entity-id order.</summary>
        UnorderedInput = 1,

        /// <summary>An input snapshot contained entity id 0.</summary>
        InvalidEntityId = 2,

        /// <summary>
        /// The tick produced more records than the engine buffer holds. The
        /// change-set is unusable; the affected receivers need a fresh keyframe.
        /// </summary>
        CapacityExceeded = 3
    }

    /// <summary>
    /// Computes the replicated change-set between two authoritative world slices
    /// (Phase 2.6, step 2.6.2, ADR-010).
    ///
    /// Contract:
    /// <list type="bullet">
    /// <item>Both inputs are <c>ServerUnitSnapshot</c> spans in canonical
    /// ascending <c>EntityId</c> order, exactly as
    /// <c>MatchServer.GetAllSnapshots</c> returns them.</item>
    /// <item>The engine copies no state: it walks the two caller-owned slices
    /// once (O(N + M)) and writes records into one preallocated buffer, so the
    /// server keeps exactly two world slices and the diff itself is zero-GC.</item>
    /// <item>Unchanged entities produce no record at all.</item>
    /// <item>Removed entities become tombstones with
    /// <see cref="DeltaRemoveCause.Destroyed"/>; fog-of-war hiding is a
    /// filtering concern of a later step and is not produced here.</item>
    /// <item>UPDATE fields are absolute values of the current slice, restricted
    /// to the bits of <see cref="ComputeDirtyMask"/>; OD-14 keeps a cleared move
    /// target free of coordinates.</item>
    /// </list>
    ///
    /// The engine owns no simulation state and never touches
    /// <c>MatchServer</c>; integration into the tick loop is step 2.6.4.
    /// </summary>
    public sealed class ServerSnapshotDiffEngine
    {
        /// <summary>
        /// Default per-tick record budgets. Sized for the current prototype;
        /// production budgets follow from the Phase 2.6 bandwidth benchmark
        /// (TBD / Owner Decision), so they are constructor parameters.
        /// </summary>
        public const int DefaultAddCapacity = 64;

        public const int DefaultUpdateCapacity = 512;

        public const int DefaultRemoveCapacity = 64;

        private readonly ReplicationChangeSetBuffer _buffer;

        public ServerSnapshotDiffEngine()
            : this(DefaultAddCapacity, DefaultUpdateCapacity, DefaultRemoveCapacity)
        {
        }

        public ServerSnapshotDiffEngine(int addCapacity, int updateCapacity, int removeCapacity)
        {
            _buffer = new ReplicationChangeSetBuffer(addCapacity, updateCapacity, removeCapacity);
        }

        /// <summary>The single preallocated output buffer, reused on every call.</summary>
        public ReplicationChangeSetBuffer Buffer => _buffer;

        public int AddCapacity => _buffer.AddCapacity;

        public int UpdateCapacity => _buffer.UpdateCapacity;

        public int RemoveCapacity => _buffer.RemoveCapacity;

        /// <summary>
        /// Diffs <paramref name="previous"/> against <paramref name="current"/> in
        /// one ascending pass. The returned change-set is a view over
        /// <see cref="Buffer"/> and stays valid until the next call.
        /// </summary>
        /// <returns>
        /// <see cref="ReplicationDiffResult.Ok"/> on success; on any failure the
        /// change-set is empty and the buffer content must be discarded.
        /// </returns>
        public ReplicationDiffResult Diff(
            ReadOnlySpan<ServerUnitSnapshot> previous,
            ReadOnlySpan<ServerUnitSnapshot> current,
            out ReplicationChangeSet changeSet)
        {
            changeSet = ReplicationChangeSet.Empty;

            var adds = _buffer.Adds;
            var updates = _buffer.Updates;
            var removes = _buffer.Removes;
            var addCount = 0;
            var updateCount = 0;
            var removeCount = 0;

            var p = 0;
            var c = 0;
            var lastPreviousId = 0UL;
            var lastCurrentId = 0UL;

            while (p < previous.Length || c < current.Length)
            {
                var previousExhausted = p >= previous.Length;
                var currentExhausted = c >= current.Length;

                if (!previousExhausted && (currentExhausted || previous[p].Entity.Value < current[c].Entity.Value))
                {
                    // Present before, gone now: tombstone.
                    ref readonly var removed = ref previous[p];
                    if (!removed.Entity.IsValid)
                    {
                        return ReplicationDiffResult.InvalidEntityId;
                    }

                    if (removed.Entity.Value <= lastPreviousId)
                    {
                        return ReplicationDiffResult.UnorderedInput;
                    }

                    if (removeCount >= removes.Length)
                    {
                        return ReplicationDiffResult.CapacityExceeded;
                    }

                    lastPreviousId = removed.Entity.Value;
                    removes[removeCount++] = new DeltaRemoveRecord(removed.Entity, DeltaRemoveCause.Destroyed);
                    p++;
                }
                else if (!currentExhausted && (previousExhausted || current[c].Entity.Value < previous[p].Entity.Value))
                {
                    // Not present before, present now: full state as an ADD record.
                    ref readonly var added = ref current[c];
                    if (!added.Entity.IsValid)
                    {
                        return ReplicationDiffResult.InvalidEntityId;
                    }

                    if (added.Entity.Value <= lastCurrentId)
                    {
                        return ReplicationDiffResult.UnorderedInput;
                    }

                    if (addCount >= adds.Length)
                    {
                        return ReplicationDiffResult.CapacityExceeded;
                    }

                    lastCurrentId = added.Entity.Value;
                    adds[addCount++] = ToAddRecord(in added);
                    c++;
                }
                else
                {
                    // Same entity on both sides: emit only what actually changed.
                    ref readonly var before = ref previous[p];
                    ref readonly var after = ref current[c];

                    if (!after.Entity.IsValid)
                    {
                        return ReplicationDiffResult.InvalidEntityId;
                    }

                    if (before.Entity.Value <= lastPreviousId || after.Entity.Value <= lastCurrentId)
                    {
                        return ReplicationDiffResult.UnorderedInput;
                    }

                    lastPreviousId = before.Entity.Value;
                    lastCurrentId = after.Entity.Value;

                    var dirtyMask = ComputeDirtyMask(in before, in after);
                    p++;
                    c++;

                    if (dirtyMask == (byte)UnitDirtyMask.None)
                    {
                        continue;
                    }

                    if (updateCount >= updates.Length)
                    {
                        return ReplicationDiffResult.CapacityExceeded;
                    }

                    updates[updateCount++] = ToUpdateRecord(in after, dirtyMask);
                }
            }

            changeSet = _buffer.AsChangeSet(addCount, updateCount, removeCount);
            return ReplicationDiffResult.Ok;
        }

        /// <summary>
        /// Exact dirty mask between two states of the same entity: one bit per
        /// replicated field that differs. OD-14 — the move-target coordinate bit
        /// is only considered while a target exists, so clearing a target is
        /// described by <see cref="UnitDirtyMask.HasMoveTarget"/> alone and no
        /// coordinates are queued for transmission.
        /// </summary>
        public static byte ComputeDirtyMask(in ServerUnitSnapshot previous, in ServerUnitSnapshot current)
        {
            var mask = (byte)UnitDirtyMask.None;

            if (previous.Owner != current.Owner)
            {
                mask |= (byte)UnitDirtyMask.Owner;
            }

            if (previous.Position != current.Position)
            {
                mask |= (byte)UnitDirtyMask.Position;
            }

            if (previous.CurrentHealth != current.CurrentHealth)
            {
                mask |= (byte)UnitDirtyMask.Health;
            }

            if (previous.HasMoveTarget != current.HasMoveTarget)
            {
                mask |= (byte)UnitDirtyMask.HasMoveTarget;
            }

            if (current.HasMoveTarget && previous.MoveTarget != current.MoveTarget)
            {
                mask |= (byte)UnitDirtyMask.MoveTarget;
            }

            if (previous.AttackTarget != current.AttackTarget)
            {
                mask |= (byte)UnitDirtyMask.AttackTarget;
            }

            if (previous.AutoAcquireEnemies != current.AutoAcquireEnemies)
            {
                mask |= (byte)UnitDirtyMask.AutoAcquire;
            }

            return mask;
        }

        /// <summary>
        /// Full state of a newly visible entity as an ADD record. Values are passed
        /// through unchanged, including the OD-29 archetype: the ADD section is the
        /// only place a client can learn a unit's kind, so dropping it here would
        /// silently degrade every presentation slot that later depends on it.
        /// </summary>
        public static DeltaAddRecord ToAddRecord(in ServerUnitSnapshot snapshot)
        {
            return new DeltaAddRecord(
                snapshot.Entity,
                snapshot.Owner,
                snapshot.Position,
                snapshot.CurrentHealth,
                snapshot.HasMoveTarget,
                snapshot.MoveTarget,
                snapshot.AttackTarget,
                snapshot.AutoAcquireEnemies,
                snapshot.UnitKind);
        }

        /// <summary>
        /// Absolute current values restricted to <paramref name="dirtyMask"/>;
        /// fields whose bit is clear stay at their default so a record carries
        /// exactly what it transmits.
        /// </summary>
        public static DeltaUpdateRecord ToUpdateRecord(in ServerUnitSnapshot current, byte dirtyMask)
        {
            var hasMoveTargetBit = HasFlag(dirtyMask, UnitDirtyMask.HasMoveTarget);

            // OD-14: a clearing record (bit 3 set, flag 0) never carries coordinates.
            var carriesMoveTarget = HasFlag(dirtyMask, UnitDirtyMask.MoveTarget) &&
                                    (!hasMoveTargetBit || current.HasMoveTarget);

            return new DeltaUpdateRecord(
                current.Entity,
                dirtyMask,
                HasFlag(dirtyMask, UnitDirtyMask.Owner) ? current.Owner : default(PlayerId),
                HasFlag(dirtyMask, UnitDirtyMask.Position) ? current.Position : default(WorldPointMm),
                HasFlag(dirtyMask, UnitDirtyMask.Health) ? current.CurrentHealth : 0,
                hasMoveTargetBit && current.HasMoveTarget,
                carriesMoveTarget ? current.MoveTarget : default(WorldPointMm),
                HasFlag(dirtyMask, UnitDirtyMask.AttackTarget) ? current.AttackTarget : default(EntityId),
                HasFlag(dirtyMask, UnitDirtyMask.AutoAcquire) && current.AutoAcquireEnemies);
        }

        private static bool HasFlag(byte mask, UnitDirtyMask flag) => (mask & (byte)flag) == (byte)flag;
    }
}
