using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Visibility;

namespace GlobalFront.Server.Replication
{
    /// <summary>
    /// Retained per-tick visibility masks (ADR-014, Step 4.3.1).
    ///
    /// A receiver that acknowledges a baseline at tick B and a target at tick T
    /// must be able to rebuild the set of units it is allowed to know at every
    /// tick of <c>(B, T]</c>. Recomputing that set from live positions is not
    /// possible after the fact, so the authoritative server stores one
    /// <see cref="BitMask1024"/> per player per tick for the same 120-tick
    /// window as <see cref="ReplicationHistoryRing"/>.
    ///
    /// Storage: <see cref="Capacity"/> x <see cref="PlayerMaskCapacity"/>
    /// masks (120 x 10 x 128 B = 150 KB), allocated once in the constructor.
    /// Eviction matches the delta ring: recording more than 120 ticks drops
    /// the oldest one, after which <see cref="TryGetVisibility"/> for that
    /// tick returns false and the receiver must be re-based.
    ///
    /// Zero-GC: recording copies masks into a preallocated slot and lookup
    /// copies one mask out; neither call allocates.
    /// </summary>
    public sealed class VisibilityHistoryRing
    {
        /// <summary>Retained ticks, fixed by ADR-010 (6 s at 20 Hz).</summary>
        public const int Capacity = 120;

        /// <summary>Masks stored per tick, one per valid <see cref="PlayerId"/>.</summary>
        public const int PlayerMaskCapacity = SimulationConstants.MaxPlayers;

        private readonly BitMask1024[] _masks = new BitMask1024[Capacity * PlayerMaskCapacity];
        private readonly ulong[] _ticks = new ulong[Capacity];

        private int _head;
        private int _count;
        private ulong _newestTick;
        private ulong _evictedThroughTick;

        /// <summary>Number of ticks currently retained.</summary>
        public int Count => _count;

        /// <summary>Oldest retained tick, or 0 while the ring is empty.</summary>
        public ulong OldestTick => _count == 0 ? 0UL : _ticks[_head];

        /// <summary>Newest recorded tick, or 0 while the ring is empty.</summary>
        public ulong NewestTick => _newestTick;

        /// <summary>Highest tick that has been evicted (0 = nothing evicted).</summary>
        public ulong EvictedThroughTick => _evictedThroughTick;

        public bool HasEvicted => _evictedThroughTick != 0UL;

        /// <summary>Records ignored because their tick was not newer than the last one.</summary>
        public int IgnoredRecordCount { get; private set; }

        /// <summary>
        /// Stores one tick's masks. <paramref name="playerMasks"/> is indexed by
        /// <see cref="PlayerId.Value"/> like the
        /// <see cref="VisibilityEngine"/> output: index 0 is ignored and the
        /// span must hold at least <see cref="SimulationConstants.MaxPlayers"/>
        /// + 1 entries.
        ///
        /// The ring is forward-only: a tick that is not newer than the last
        /// recorded one is ignored (counted in
        /// <see cref="IgnoredRecordCount"/>). Recording more than
        /// <see cref="Capacity"/> ticks evicts the oldest one.
        /// </summary>
        public void RecordTick(ulong tick, ReadOnlySpan<BitMask1024> playerMasks)
        {
            if (playerMasks.Length < PlayerMaskCapacity + 1)
            {
                throw new ArgumentException(
                    "Visibility masks must be indexed by PlayerId.Value and hold MaxPlayers + 1 entries.",
                    nameof(playerMasks));
            }

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

            var slot = PhysicalIndex(_count);
            var maskBase = slot * PlayerMaskCapacity;
            for (var playerValue = 1; playerValue <= PlayerMaskCapacity; playerValue++)
            {
                _masks[maskBase + playerValue - 1] = playerMasks[playerValue];
            }

            _ticks[slot] = tick;
            _count++;
            _newestTick = tick;
        }

        /// <summary>
        /// Returns the stored mask of one player at one tick. False when the
        /// tick is not retained (never recorded, evicted) or the player is
        /// invalid.
        /// </summary>
        public bool TryGetVisibility(ulong tick, PlayerId player, out BitMask1024 mask)
        {
            mask = default;
            if (!player.IsValid || !TryFindLogicalIndex(tick, out var logicalIndex))
            {
                return false;
            }

            var slot = PhysicalIndex(logicalIndex);
            mask = _masks[(slot * PlayerMaskCapacity) + player.Value - 1];
            return true;
        }

        public bool ContainsTick(ulong tick) => TryFindLogicalIndex(tick, out _);

        /// <summary>
        /// Drops the retained history and the diagnostics, e.g. between
        /// matches. Storage stays allocated.
        /// </summary>
        public void Clear()
        {
            for (var index = 0; index < _masks.Length; index++)
            {
                _masks[index].ClearAll();
            }

            for (var index = 0; index < Capacity; index++)
            {
                _ticks[index] = 0UL;
            }

            _head = 0;
            _count = 0;
            _newestTick = 0UL;
            _evictedThroughTick = 0UL;
            IgnoredRecordCount = 0;
        }

        public override string ToString() =>
            $"VisibilityRing(count={_count}/{Capacity}, oldest={OldestTick}, newest={NewestTick}, " +
            $"evictedThrough={_evictedThroughTick}, ignored={IgnoredRecordCount})";

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
