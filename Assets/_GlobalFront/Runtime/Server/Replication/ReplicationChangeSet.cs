using System;
using GlobalFront.Core.Snapshot;

namespace GlobalFront.Server.Replication
{
    /// <summary>
    /// The replicated changes of a single tick: three record sections in
    /// canonical ascending <c>EntityId</c> order, exactly as the delta wire
    /// codec expects them (Phase 2.6, ADR-010).
    ///
    /// A change-set is a pure value type and owns nothing: it is a counted view
    /// over buffers that belong to a <see cref="ReplicationChangeSetBuffer"/>
    /// (diff engine output, one history-ring slot, or a merge builder side).
    /// Creating, copying or reading a change-set therefore never allocates,
    /// which is what keeps the replication hot path zero-GC.
    /// </summary>
    public readonly struct ReplicationChangeSet
    {
        /// <summary>An empty change-set that references no buffer at all.</summary>
        public static readonly ReplicationChangeSet Empty = default;

        public ReplicationChangeSet(
            DeltaAddRecord[] addBuffer,
            int addCount,
            DeltaUpdateRecord[] updateBuffer,
            int updateCount,
            DeltaRemoveRecord[] removeBuffer,
            int removeCount)
        {
            if (addCount < 0 || (addBuffer == null && addCount != 0))
            {
                throw new ArgumentOutOfRangeException(nameof(addCount));
            }

            if (updateCount < 0 || (updateBuffer == null && updateCount != 0))
            {
                throw new ArgumentOutOfRangeException(nameof(updateCount));
            }

            if (removeCount < 0 || (removeBuffer == null && removeCount != 0))
            {
                throw new ArgumentOutOfRangeException(nameof(removeCount));
            }

            AddBuffer = addBuffer;
            AddCount = addCount;
            UpdateBuffer = updateBuffer;
            UpdateCount = updateCount;
            RemoveBuffer = removeBuffer;
            RemoveCount = removeCount;
        }

        /// <summary>Pooled storage behind <see cref="Adds"/>; may be longer than the count.</summary>
        public DeltaAddRecord[] AddBuffer { get; }

        public int AddCount { get; }

        /// <summary>Pooled storage behind <see cref="Updates"/>; may be longer than the count.</summary>
        public DeltaUpdateRecord[] UpdateBuffer { get; }

        public int UpdateCount { get; }

        /// <summary>Pooled storage behind <see cref="Removes"/>; may be longer than the count.</summary>
        public DeltaRemoveRecord[] RemoveBuffer { get; }

        public int RemoveCount { get; }

        /// <summary>New entities of this tick, ascending by entity id.</summary>
        public ReadOnlySpan<DeltaAddRecord> Adds => AddBuffer.AsSpan(0, AddCount);

        /// <summary>Changed entities of this tick, ascending by entity id.</summary>
        public ReadOnlySpan<DeltaUpdateRecord> Updates => UpdateBuffer.AsSpan(0, UpdateCount);

        /// <summary>Tombstones of this tick, ascending by entity id.</summary>
        public ReadOnlySpan<DeltaRemoveRecord> Removes => RemoveBuffer.AsSpan(0, RemoveCount);

        public bool IsEmpty => AddCount == 0 && UpdateCount == 0 && RemoveCount == 0;

        public int RecordCount => AddCount + UpdateCount + RemoveCount;

        public override string ToString() =>
            $"ChangeSet(add={AddCount}, update={UpdateCount}, remove={RemoveCount})";
    }

    /// <summary>
    /// One preallocated triple of record arrays plus the counters that describe
    /// how much of it is live. Buffers are created once (diff engine output,
    /// each <see cref="ReplicationHistoryRing"/> slot, each builder side) and
    /// then reused for every tick, so the replication hot path performs no heap
    /// allocation at all.
    /// </summary>
    public sealed class ReplicationChangeSetBuffer
    {
        private readonly DeltaAddRecord[] _adds;
        private readonly DeltaUpdateRecord[] _updates;
        private readonly DeltaRemoveRecord[] _removes;

        public ReplicationChangeSetBuffer(int addCapacity, int updateCapacity, int removeCapacity)
        {
            if (addCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(addCapacity));
            }

            if (updateCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(updateCapacity));
            }

            if (removeCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(removeCapacity));
            }

            _adds = new DeltaAddRecord[addCapacity];
            _updates = new DeltaUpdateRecord[updateCapacity];
            _removes = new DeltaRemoveRecord[removeCapacity];
        }

        public int AddCapacity => _adds.Length;

        public int UpdateCapacity => _updates.Length;

        public int RemoveCapacity => _removes.Length;

        /// <summary>Writable ADD storage (full capacity).</summary>
        public Span<DeltaAddRecord> Adds => _adds;

        /// <summary>Writable UPDATE storage (full capacity).</summary>
        public Span<DeltaUpdateRecord> Updates => _updates;

        /// <summary>Writable REMOVE storage (full capacity).</summary>
        public Span<DeltaRemoveRecord> Removes => _removes;

        public bool CanHold(int addCount, int updateCount, int removeCount) =>
            addCount >= 0 && updateCount >= 0 && removeCount >= 0 &&
            addCount <= _adds.Length &&
            updateCount <= _updates.Length &&
            removeCount <= _removes.Length;

        /// <summary>
        /// Exposes the first <paramref name="addCount"/>/<paramref name="updateCount"/>/
        /// <paramref name="removeCount"/> records of this buffer as a change-set.
        /// </summary>
        public ReplicationChangeSet AsChangeSet(int addCount, int updateCount, int removeCount)
        {
            if (!CanHold(addCount, updateCount, removeCount))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(addCount),
                    $"counts ({addCount}, {updateCount}, {removeCount}) exceed buffer capacity " +
                    $"({_adds.Length}, {_updates.Length}, {_removes.Length}).");
            }

            return new ReplicationChangeSet(_adds, addCount, _updates, updateCount, _removes, removeCount);
        }

        /// <summary>
        /// Copies the records of <paramref name="source"/> into this buffer,
        /// reusing the preallocated storage. Records that do not fit are dropped
        /// and reported through <paramref name="truncated"/>: a truncated
        /// change-set must never be replayed as a delta, it triggers a
        /// re-baseline instead.
        /// </summary>
        public ReplicationChangeSet CopyFrom(in ReplicationChangeSet source, out bool truncated)
        {
            var addCount = Math.Min(source.AddCount, _adds.Length);
            var updateCount = Math.Min(source.UpdateCount, _updates.Length);
            var removeCount = Math.Min(source.RemoveCount, _removes.Length);

            source.Adds.Slice(0, addCount).CopyTo(_adds);
            source.Updates.Slice(0, updateCount).CopyTo(_updates);
            source.Removes.Slice(0, removeCount).CopyTo(_removes);

            truncated = addCount < source.AddCount ||
                        updateCount < source.UpdateCount ||
                        removeCount < source.RemoveCount;

            return new ReplicationChangeSet(_adds, addCount, _updates, updateCount, _removes, removeCount);
        }

        public override string ToString() =>
            $"ChangeSetBuffer(capacity add={_adds.Length}, update={_updates.Length}, remove={_removes.Length})";
    }
}
