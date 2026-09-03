using System;

namespace GlobalFront.Server.Replication
{
    /// <summary>
    /// Retained history of per-tick change-sets (Phase 2.6, step 2.6.2, ADR-010).
    ///
    /// The ring is the server-side answer to packet loss: a receiver that
    /// confirms <c>BaseTick</c> can be advanced to <c>TargetTick</c> with one
    /// cumulative establishing delta merged from every retained tick of
    /// <c>(BaseTick, TargetTick]</c>, so skipping intermediate ticks is safe.
    /// Once <c>BaseTick</c> falls out of the window, no delta can be built and
    /// the receiver must be re-based with a fresh keyframe.
    ///
    /// Window: <see cref="Capacity"/> = 120 ticks (6 s at 20 Hz), fixed by
    /// ADR-010 as <c>RetainedDeltaHistoryWindow = Tombstone Horizon</c>.
    ///
    /// Zero-GC: all 120 slots are preallocated once in the constructor and then
    /// reused; <see cref="RecordTick"/> copies records into a slot and
    /// <see cref="TryMergeRange"/> writes into a caller-owned
    /// <see cref="ReplicationChangeSetBuilder"/>. No call allocates.
    ///
    /// Per-tick record budgets are constructor parameters. The defaults are
    /// sized for the current prototype; production budgets follow from the
    /// Phase 2.6 bandwidth benchmark (TBD / Owner Decision). A tick that does
    /// not fit is stored truncated and flagged: such a tick can never be
    /// replayed as a delta and forces a re-baseline instead of a silent desync.
    /// </summary>
    public sealed class ReplicationHistoryRing
    {
        /// <summary>Retained ticks, fixed by ADR-010 (6 s at 20 Hz).</summary>
        public const int Capacity = 120;

        public const int DefaultMaxAddsPerTick = ServerSnapshotDiffEngine.DefaultAddCapacity;

        public const int DefaultMaxUpdatesPerTick = ServerSnapshotDiffEngine.DefaultUpdateCapacity;

        public const int DefaultMaxRemovesPerTick = ServerSnapshotDiffEngine.DefaultRemoveCapacity;

        private readonly ReplicationChangeSetBuffer[] _slots;
        private readonly ReplicationChangeSet[] _views;
        private readonly ulong[] _ticks;
        private readonly bool[] _overflowed;

        private int _head;
        private int _count;
        private ulong _newestTick;
        private ulong _evictedThroughTick;

        public ReplicationHistoryRing()
            : this(DefaultMaxAddsPerTick, DefaultMaxUpdatesPerTick, DefaultMaxRemovesPerTick)
        {
        }

        public ReplicationHistoryRing(int maxAddsPerTick, int maxUpdatesPerTick, int maxRemovesPerTick)
        {
            if (maxAddsPerTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAddsPerTick));
            }

            if (maxUpdatesPerTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxUpdatesPerTick));
            }

            if (maxRemovesPerTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRemovesPerTick));
            }

            MaxAddsPerTick = maxAddsPerTick;
            MaxUpdatesPerTick = maxUpdatesPerTick;
            MaxRemovesPerTick = maxRemovesPerTick;

            _slots = new ReplicationChangeSetBuffer[Capacity];
            for (var index = 0; index < Capacity; index++)
            {
                _slots[index] = new ReplicationChangeSetBuffer(maxAddsPerTick, maxUpdatesPerTick, maxRemovesPerTick);
            }

            _views = new ReplicationChangeSet[Capacity];
            _ticks = new ulong[Capacity];
            _overflowed = new bool[Capacity];
        }

        /// <summary>Number of ticks currently retained.</summary>
        public int Count => _count;

        /// <summary>Oldest retained tick, or 0 while the ring is empty.</summary>
        public ulong OldestTick => _count == 0 ? 0UL : _ticks[_head];

        /// <summary>Newest recorded tick, or 0 while the ring is empty.</summary>
        public ulong NewestTick => _newestTick;

        /// <summary>
        /// Highest tick whose change-set has been evicted (0 = nothing evicted).
        /// A merge base at or above this tick is still servable by deltas.
        /// </summary>
        public ulong EvictedThroughTick => _evictedThroughTick;

        public bool HasEvicted => _evictedThroughTick != 0UL;

        /// <summary>Ticks whose change-set did not fit into a slot.</summary>
        public int OverflowCount { get; private set; }

        /// <summary>Records ignored because their tick was not newer than the last one.</summary>
        public int IgnoredRecordCount { get; private set; }

        public int MaxAddsPerTick { get; }

        public int MaxUpdatesPerTick { get; }

        public int MaxRemovesPerTick { get; }

        /// <summary>
        /// Stores one tick's change-set, copying the records into a preallocated
        /// slot. The ring is forward-only: a tick that is not newer than the last
        /// recorded one is ignored (counted in <see cref="IgnoredRecordCount"/>).
        /// Recording more than <see cref="Capacity"/> ticks evicts the oldest one.
        ///
        /// Ticks whose change-set is empty are stored as well, so calling this
        /// once per replication tick keeps the retained window equal to
        /// <see cref="Capacity"/> ticks.
        /// </summary>
        public void RecordTick(ulong tick, in ReplicationChangeSet changeSet)
        {
            if (_count > 0 && tick <= _newestTick)
            {
                IgnoredRecordCount++;
                return;
            }

            if (_count == Capacity)
            {
                _evictedThroughTick = _ticks[_head];
                _head = (_head + 1) % Capacity;
                _count--;
            }

            var index = PhysicalIndex(_count);
            _views[index] = _slots[index].CopyFrom(in changeSet, out var truncated);
            _ticks[index] = tick;
            _overflowed[index] = truncated;
            if (truncated)
            {
                OverflowCount++;
            }

            _count++;
            _newestTick = tick;
        }

        /// <summary>
        /// Returns the stored change-set of one tick. False when the tick is not
        /// retained (never recorded, empty-window or evicted) or when it was
        /// stored truncated. The returned view points into the ring slot and
        /// stays valid until that slot is reused by eviction.
        /// </summary>
        public bool TryGetChangeSet(ulong tick, out ReplicationChangeSet changeSet)
        {
            changeSet = ReplicationChangeSet.Empty;

            if (!TryFindLogicalIndex(tick, out var logical))
            {
                return false;
            }

            var index = PhysicalIndex(logical);
            if (_overflowed[index])
            {
                return false;
            }

            changeSet = _views[index];
            return true;
        }

        /// <summary>
        /// Merges every retained tick of <c>(baseTick, targetTick]</c> into
        /// <paramref name="builder"/>, producing the cumulative establishing
        /// delta that carries a receiver from <c>baseTick</c> to
        /// <c>targetTick</c>. The builder is cleared first and keeps its buffers.
        /// </summary>
        /// <returns>
        /// False — meaning "no delta is possible, re-baseline with a keyframe" —
        /// when the interval is inverted, when <paramref name="baseTick"/> is
        /// older than the retained window, when a tick inside the interval was
        /// stored truncated, or when the merged result exceeds the builder
        /// capacity. The builder is always cleared first; on false its content is
        /// either empty or a partial merge and must not be used.
        /// </returns>
        public bool TryMergeRange(ulong baseTick, ulong targetTick, ReplicationChangeSetBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            builder.Clear();

            if (targetTick < baseTick)
            {
                return false;
            }

            if (_evictedThroughTick > baseTick)
            {
                // Change-sets newer than baseTick were already dropped: the
                // receiver cannot be advanced by deltas.
                return false;
            }

            for (var logical = 0; logical < _count; logical++)
            {
                var index = PhysicalIndex(logical);
                var tick = _ticks[index];

                if (tick <= baseTick)
                {
                    continue;
                }

                if (tick > targetTick)
                {
                    break;
                }

                if (_overflowed[index])
                {
                    return false;
                }

                if (!builder.TryMerge(_views[index]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Drops the retained history and the diagnostics, e.g. between matches.
        /// Slot buffers stay allocated.
        /// </summary>
        public void Clear()
        {
            for (var index = 0; index < Capacity; index++)
            {
                _views[index] = ReplicationChangeSet.Empty;
                _ticks[index] = 0UL;
                _overflowed[index] = false;
            }

            _head = 0;
            _count = 0;
            _newestTick = 0UL;
            _evictedThroughTick = 0UL;
            OverflowCount = 0;
            IgnoredRecordCount = 0;
        }

        public override string ToString() =>
            $"HistoryRing(count={_count}/{Capacity}, oldest={OldestTick}, newest={NewestTick}, " +
            $"evictedThrough={_evictedThroughTick}, overflowed={OverflowCount}, ignored={IgnoredRecordCount})";

        private int PhysicalIndex(int logicalIndex) => (_head + logicalIndex) % Capacity;

        private bool TryFindLogicalIndex(ulong tick, out int logicalIndex)
        {
            logicalIndex = -1;
            if (_count == 0)
            {
                return false;
            }

            // Retained ticks are strictly ascending in logical order.
            var low = 0;
            var high = _count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var middleTick = _ticks[PhysicalIndex(middle)];
                if (middleTick == tick)
                {
                    logicalIndex = middle;
                    return true;
                }

                if (middleTick < tick)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return false;
        }
    }
}
