using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;

namespace GlobalFront.Client.Replication
{
    /// <summary>
    /// Replicated state of one unit as the client holds it (Phase 2.6, step
    /// 2.6.3, ADR-010). The field set mirrors the 39-byte unit record of the
    /// delta protocol, flattened to the millimetre integers the presentation
    /// layer consumes.
    ///
    /// This is a mirror of authoritative state, never a source of it: the
    /// client neither predicts nor mutates these values locally.
    /// </summary>
    public readonly struct ClientUnitState : IEquatable<ClientUnitState>
    {
        public ClientUnitState(
            EntityId entity,
            PlayerId owner,
            int posX,
            int posZ,
            int health,
            bool hasMoveTarget,
            int moveTargetX,
            int moveTargetZ,
            EntityId attackTarget,
            bool autoAcquire,
            byte unitKind = UnitKinds.Unknown)
        {
            Entity = entity;
            Owner = owner;
            PosX = posX;
            PosZ = posZ;
            Health = health;
            HasMoveTarget = hasMoveTarget;
            MoveTargetX = moveTargetX;
            MoveTargetZ = moveTargetZ;
            AttackTarget = attackTarget;
            AutoAcquire = autoAcquire;
            UnitKind = unitKind;
        }

        /// <summary>Builds the client state of a freshly spawned unit.</summary>
        public static ClientUnitState FromAdd(in DeltaAddRecord add)
        {
            return new ClientUnitState(
                add.Entity,
                add.Owner,
                add.Position.X,
                add.Position.Z,
                add.CurrentHealth,
                add.HasMoveTarget,
                add.MoveTarget.X,
                add.MoveTarget.Z,
                add.AttackTarget,
                add.AutoAcquireEnemies,
                add.UnitKind);
        }

        public EntityId Entity { get; }

        public PlayerId Owner { get; }

        /// <summary>Position X in millimetres.</summary>
        public int PosX { get; }

        /// <summary>Position Z in millimetres.</summary>
        public int PosZ { get; }

        public int Health { get; }

        public bool HasMoveTarget { get; }

        /// <summary>Move target X in millimetres; zero while no target is set.</summary>
        public int MoveTargetX { get; }

        /// <summary>Move target Z in millimetres; zero while no target is set.</summary>
        public int MoveTargetZ { get; }

        /// <summary>Explicit attack target; <c>0</c> when the unit has none.</summary>
        public EntityId AttackTarget { get; }

        public bool AutoAcquire { get; }

        /// <summary>
        /// OD-29 archetype id (<see cref="UnitKinds"/>) as it arrived on the ADD or
        /// keyframe record, unchanged for the life of the entity. The client's
        /// presentation resolves mesh, ring radius and maximum health from it;
        /// <see cref="UnitKinds.Unknown"/> covers a record from before OD-29.
        /// </summary>
        public byte UnitKind { get; }

        public WorldPointMm Position => new WorldPointMm(PosX, PosZ);

        public WorldPointMm MoveTarget => new WorldPointMm(MoveTargetX, MoveTargetZ);

        /// <summary>False for a free table slot (entity id 0 is never valid).</summary>
        public bool IsLive => Entity.IsValid;

        public bool Equals(ClientUnitState other) =>
            Entity == other.Entity &&
            Owner == other.Owner &&
            PosX == other.PosX &&
            PosZ == other.PosZ &&
            Health == other.Health &&
            HasMoveTarget == other.HasMoveTarget &&
            MoveTargetX == other.MoveTargetX &&
            MoveTargetZ == other.MoveTargetZ &&
            AttackTarget == other.AttackTarget &&
            AutoAcquire == other.AutoAcquire &&
            UnitKind == other.UnitKind;

        public override bool Equals(object obj) => obj is ClientUnitState other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Entity);
            hash.Add(Owner);
            hash.Add(PosX);
            hash.Add(PosZ);
            hash.Add(Health);
            hash.Add(HasMoveTarget);
            hash.Add(MoveTargetX);
            hash.Add(MoveTargetZ);
            hash.Add(AttackTarget);
            hash.Add(AutoAcquire);
            hash.Add(UnitKind);
            return hash.ToHashCode();
        }

        public override string ToString() =>
            $"ClientUnit(entity={Entity}, owner={Owner}, pos={Position}, hp={Health}, " +
            $"moveTarget={(HasMoveTarget ? MoveTarget.ToString() : "none")}, attackTarget={AttackTarget}, " +
            $"autoAcquire={AutoAcquire}, kind={UnitKind})";

        public static bool operator ==(ClientUnitState left, ClientUnitState right) => left.Equals(right);

        public static bool operator !=(ClientUnitState left, ClientUnitState right) => !left.Equals(right);
    }

    /// <summary>Outcome of applying one record to the client world table.</summary>
    public enum ClientWorldApplyResult : byte
    {
        /// <summary>The record was applied.</summary>
        Ok = 0,

        /// <summary>The record carried entity id 0, which is never valid.</summary>
        InvalidEntityId = 1,

        /// <summary>ADD for an entity the client already holds.</summary>
        DuplicateEntity = 2,

        /// <summary>
        /// The entity is not in the table. For UPDATE this breaks the
        /// establishing-delta contract and the receiver must re-base; for REMOVE
        /// it is the documented idempotent no-op (tombstone echo).
        /// </summary>
        UnknownEntity = 3,

        /// <summary>The table is full; no record was stored.</summary>
        CapacityExceeded = 4,

        /// <summary>
        /// The dirty mask carries the v1 reserved extension bit. The wire codec
        /// already rejects it; the table refuses to guess at unknown fields.
        /// </summary>
        ReservedDirtyMaskBit = 5
    }

    /// <summary>
    /// Client-side table of the replicated world (Phase 2.6, step 2.6.3,
    /// ADR-010): the state a delta stream is applied to.
    ///
    /// Structure: one preallocated slot array plus an open-addressing
    /// entity-id index with linear probing and backward-shift deletion, and a
    /// free-slot stack. ADD, UPDATE and REMOVE are therefore O(1) and the whole
    /// table is created once - applying a continuous delta stream allocates
    /// nothing (Zero-GC hot path, verified by EditMode tests).
    ///
    /// Enumeration order is slot order, not canonical entity-id order: slots are
    /// handed out from the free stack and reused after a removal. The order is
    /// deterministic for a deterministic sequence of records, which is what the
    /// presentation layer needs; a canonical ascending view is a consumer
    /// concern (the wire invariant "entity ids ascend and are never reused"
    /// still holds inside every packet).
    ///
    /// Capacity is fixed at construction. A table that cannot hold the
    /// authoritative world is a configuration error, reported as
    /// <see cref="ClientWorldApplyResult.CapacityExceeded"/> so the receiver can
    /// re-base instead of silently dropping units.
    /// </summary>
    public sealed class ClientReplicationWorld
    {
        /// <summary>
        /// Default table size. ADR-010 targets 3000+ replicated entities with a
        /// 5000+ stress case in Phase 8; the default leaves headroom for the
        /// target and keeps the index load factor at or below 0.5.
        /// </summary>
        public const int DefaultCapacity = 4096;

        /// <summary>Empty bucket marker: stored values are <c>slot + 1</c>.</summary>
        private const int EmptyBucket = 0;

        private readonly ClientUnitState[] _slots;
        private readonly int[] _freeSlots;
        private readonly int[] _index;
        private readonly int _indexMask;
        private readonly int[] _checksumSlots;

        private int _freeCount;

        public ClientReplicationWorld()
            : this(DefaultCapacity)
        {
        }

        public ClientReplicationWorld(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _slots = new ClientUnitState[capacity];
            _freeSlots = new int[capacity];
            _checksumSlots = new int[capacity];

            var indexSize = 4;
            while (indexSize < capacity * 2)
            {
                indexSize <<= 1;
            }

            _index = new int[indexSize];
            _indexMask = indexSize - 1;

            ClearStorage();
        }

        /// <summary>Number of unit slots; fixed for the lifetime of the table.</summary>
        public int Capacity => _slots.Length;

        /// <summary>Units the client currently holds.</summary>
        public int LiveCount { get; private set; }

        /// <summary>Bucket count of the entity-id index (power of two).</summary>
        public int IndexSize => _index.Length;

        /// <summary>Re-baselines performed through <see cref="Reset"/>.</summary>
        public int ResetCount { get; private set; }

        /// <summary>ADD records refused because the entity was already present.</summary>
        public int DuplicateAddCount { get; private set; }

        /// <summary>UPDATE records refused because the entity is unknown.</summary>
        public int UnknownUpdateCount { get; private set; }

        /// <summary>REMOVE records that hit nothing (idempotent tombstone echo).</summary>
        public int UnknownRemoveCount { get; private set; }

        /// <summary>ADD records refused because the table is full.</summary>
        public int CapacityRejectCount { get; private set; }

        /// <summary>Records refused for an invalid id or a reserved dirty-mask bit.</summary>
        public int InvalidRecordCount { get; private set; }

        /// <summary>Tombstones with cause <see cref="DeltaRemoveCause.Destroyed"/>.</summary>
        public int DestroyedRemoveCount { get; private set; }

        /// <summary>Tombstones with cause <see cref="DeltaRemoveCause.FogOfWarHidden"/>.</summary>
        public int FogOfWarRemoveCount { get; private set; }

        /// <summary>True when <paramref name="additionalUnits"/> more ADDs fit.</summary>
        public bool HasCapacityFor(int additionalUnits) =>
            additionalUnits >= 0 && additionalUnits <= _freeCount;

        /// <summary>
        /// Spawns one unit. All ten replicated fields are installed from the
        /// record, so a keyframe ADD and a delta ADD are applied identically.
        /// </summary>
        public ClientWorldApplyResult ApplyAdd(in DeltaAddRecord add)
        {
            if (!add.Entity.IsValid)
            {
                InvalidRecordCount++;
                return ClientWorldApplyResult.InvalidEntityId;
            }

            if (TryFindSlot(add.Entity, out _))
            {
                DuplicateAddCount++;
                return ClientWorldApplyResult.DuplicateEntity;
            }

            if (_freeCount == 0)
            {
                CapacityRejectCount++;
                return ClientWorldApplyResult.CapacityExceeded;
            }

            var slot = _freeSlots[--_freeCount];
            _slots[slot] = ClientUnitState.FromAdd(in add);
            InsertIndex(add.Entity.Value, slot);
            LiveCount++;
            return ClientWorldApplyResult.Ok;
        }

        /// <summary>
        /// Applies one UPDATE record: exactly the fields selected by its dirty
        /// mask change, every other field keeps its value. Fields whose bit is
        /// clear are not transmitted at all, so their decoded default must never
        /// reach the table.
        ///
        /// OD-14: when the <see cref="UnitDirtyMask.HasMoveTarget"/> bit is set
        /// and the flag decodes to 0, the move target is being cleared and no
        /// coordinates were transmitted - the stored coordinates are zeroed even
        /// if <see cref="UnitDirtyMask.MoveTarget"/> is set as well.
        /// </summary>
        public ClientWorldApplyResult ApplyUpdate(in DeltaUpdateRecord update)
        {
            if (!update.Entity.IsValid)
            {
                InvalidRecordCount++;
                return ClientWorldApplyResult.InvalidEntityId;
            }

            if ((update.DirtyMask & (byte)UnitDirtyMask.ReservedExtension) != 0)
            {
                InvalidRecordCount++;
                return ClientWorldApplyResult.ReservedDirtyMaskBit;
            }

            if (!TryFindSlot(update.Entity, out var slot))
            {
                UnknownUpdateCount++;
                return ClientWorldApplyResult.UnknownEntity;
            }

            var existing = _slots[slot];
            _slots[slot] = Merge(in existing, in update);
            return ClientWorldApplyResult.Ok;
        }

        /// <summary>
        /// Drops one unit. Removal is idempotent: an unknown id is reported as
        /// <see cref="ClientWorldApplyResult.UnknownEntity"/> and changes
        /// nothing, because the tombstone echo horizon repeats removals the
        /// client has already applied.
        /// </summary>
        public ClientWorldApplyResult ApplyRemove(in DeltaRemoveRecord remove)
        {
            if (!remove.Entity.IsValid)
            {
                InvalidRecordCount++;
                return ClientWorldApplyResult.InvalidEntityId;
            }

            if (!TryFindSlot(remove.Entity, out var slot))
            {
                UnknownRemoveCount++;
                return ClientWorldApplyResult.UnknownEntity;
            }

            RemoveFromIndex(remove.Entity.Value, slot);
            _slots[slot] = default;
            _freeSlots[_freeCount++] = slot;
            LiveCount--;

            if (remove.Cause == (byte)DeltaRemoveCause.FogOfWarHidden)
            {
                FogOfWarRemoveCount++;
            }
            else
            {
                DestroyedRemoveCount++;
            }

            return ClientWorldApplyResult.Ok;
        }

        /// <summary>
        /// Drops the whole table at once - the re-baseline path taken before a
        /// keyframe is installed. Slot, index and free-list storage stay
        /// allocated, so a reset never allocates either.
        /// </summary>
        public void Reset()
        {
            ClearStorage();
            ResetCount++;
        }

        public bool Contains(EntityId entity) => TryFindSlot(entity, out _);

        public bool TryGet(EntityId entity, out ClientUnitState state)
        {
            if (!TryFindSlot(entity, out var slot))
            {
                state = default;
                return false;
            }

            state = _slots[slot];
            return true;
        }

        /// <summary>
        /// True when the slot holds a unit. Out-of-range indices report false
        /// instead of throwing, so a presentation loop can walk the table safely.
        /// </summary>
        public bool IsSlotLive(int slot) =>
            (uint)slot < (uint)_slots.Length && _slots[slot].IsLive;

        public bool TryGetSlotState(int slot, out ClientUnitState state)
        {
            if (!IsSlotLive(slot))
            {
                state = default;
                return false;
            }

            state = _slots[slot];
            return true;
        }

        /// <summary>
        /// Copies live units into a caller-owned buffer in slot order and returns
        /// how many were written. Allocation-free: the presentation layer keeps
        /// one buffer for the whole match.
        /// </summary>
        public int CopyLiveStates(Span<ClientUnitState> destination)
        {
            var written = 0;
            for (var slot = 0; slot < _slots.Length && written < destination.Length; slot++)
            {
                if (_slots[slot].IsLive)
                {
                    destination[written++] = _slots[slot];
                }
            }

            return written;
        }

        /// <summary>
        /// Deterministic 32-bit fingerprint of the live replicated world (audit P1-2):
        /// 32-bit FNV-1a over every active unit in canonical ascending entity-id
        /// order, mixing entity id, position (X, Z) and health. Strictly matches
        /// the server's fingerprinting logic in ServerReplicationEmitter.
        ///
        /// Strict zero-GC: operates on the preallocated <see cref="_checksumSlots"/>
        /// scratch array and uses an iterative, in-place heapsort without allocating
        /// memory, LINQ, or interface enumeration.
        /// </summary>
        public uint ComputeStateChecksum()
        {
            var count = 0;
            for (var slot = 0; slot < _slots.Length; slot++)
            {
                if (_slots[slot].IsLive)
                {
                    _checksumSlots[count++] = slot;
                }
            }

            if (count > 1)
            {
                SortChecksumSlots(count);
            }

            unchecked
            {
                var hash = 2166136261u;
                for (var i = 0; i < count; i++)
                {
                    var unit = _slots[_checksumSlots[i]];
                    hash = MixChecksumByte(hash, unit.Entity.Value);
                    hash = MixChecksumByte(hash, (ulong)unit.PosX);
                    hash = MixChecksumByte(hash, (ulong)unit.PosZ);
                    hash = MixChecksumByte(hash, (ulong)unit.Health);
                    hash = MixChecksumByte(hash, unit.HasMoveTarget ? 1UL : 0UL);
                    hash = MixChecksumByte(hash, (ulong)unit.MoveTargetX);
                    hash = MixChecksumByte(hash, (ulong)unit.MoveTargetZ);
                    hash = MixChecksumByte(hash, unit.AttackTarget.Value);
                }

                return hash;
            }
        }

        private void SortChecksumSlots(int count)
        {
            for (var i = (count >> 1) - 1; i >= 0; i--)
            {
                HeapifyChecksumSlots(count, i);
            }

            for (var i = count - 1; i > 0; i--)
            {
                var temp = _checksumSlots[0];
                _checksumSlots[0] = _checksumSlots[i];
                _checksumSlots[i] = temp;

                HeapifyChecksumSlots(i, 0);
            }
        }

        private void HeapifyChecksumSlots(int length, int root)
        {
            while (true)
            {
                var largest = root;
                var left = (root << 1) + 1;
                var right = left + 1;

                if (left < length &&
                    _slots[_checksumSlots[left]].Entity.Value > _slots[_checksumSlots[largest]].Entity.Value)
                {
                    largest = left;
                }

                if (right < length &&
                    _slots[_checksumSlots[right]].Entity.Value > _slots[_checksumSlots[largest]].Entity.Value)
                {
                    largest = right;
                }

                if (largest == root)
                {
                    break;
                }

                var swap = _checksumSlots[root];
                _checksumSlots[root] = _checksumSlots[largest];
                _checksumSlots[largest] = swap;

                root = largest;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static uint MixChecksumByte(uint hash, ulong value)
        {
            unchecked
            {
                for (var shift = 0; shift < sizeof(ulong) * 8; shift += 8)
                {
                    hash = (hash ^ (uint)((value >> shift) & 0xFF)) * 16777619u;
                }

                return hash;
            }
        }

        public override string ToString() =>
            $"ClientReplicationWorld(live={LiveCount}/{Capacity}, index={IndexSize}, resets={ResetCount})";

        /// <summary>Empties slots, index and free stack; storage stays allocated.</summary>
        private void ClearStorage()
        {
            Array.Clear(_slots, 0, _slots.Length);
            Array.Clear(_index, 0, _index.Length);

            // The stack is refilled backwards so that slots are handed out in
            // ascending order again after every re-baseline.
            for (var slot = 0; slot < _slots.Length; slot++)
            {
                _freeSlots[slot] = _slots.Length - 1 - slot;
            }

            _freeCount = _slots.Length;
            LiveCount = 0;
        }

        /// <summary>Field-wise merge of one UPDATE record into the stored state.</summary>
        private static ClientUnitState Merge(in ClientUnitState existing, in DeltaUpdateRecord update)
        {
            var mask = update.DirtyMask;
            var hasMoveTarget = HasFlag(mask, UnitDirtyMask.HasMoveTarget)
                ? update.HasMoveTarget
                : existing.HasMoveTarget;

            int moveTargetX;
            int moveTargetZ;
            if (HasFlag(mask, UnitDirtyMask.HasMoveTarget) && !update.HasMoveTarget)
            {
                // OD-14: the target is being cleared, so no coordinates arrived.
                moveTargetX = 0;
                moveTargetZ = 0;
            }
            else if (HasFlag(mask, UnitDirtyMask.MoveTarget))
            {
                moveTargetX = update.MoveTarget.X;
                moveTargetZ = update.MoveTarget.Z;
            }
            else
            {
                moveTargetX = existing.MoveTargetX;
                moveTargetZ = existing.MoveTargetZ;
            }

            return new ClientUnitState(
                existing.Entity,
                HasFlag(mask, UnitDirtyMask.Owner) ? update.Owner : existing.Owner,
                HasFlag(mask, UnitDirtyMask.Position) ? update.Position.X : existing.PosX,
                HasFlag(mask, UnitDirtyMask.Position) ? update.Position.Z : existing.PosZ,
                HasFlag(mask, UnitDirtyMask.Health) ? update.CurrentHealth : existing.Health,
                hasMoveTarget,
                moveTargetX,
                moveTargetZ,
                HasFlag(mask, UnitDirtyMask.AttackTarget) ? update.AttackTarget : existing.AttackTarget,
                HasFlag(mask, UnitDirtyMask.AutoAcquire)
                    ? update.AutoAcquireEnemies
                    : existing.AutoAcquire,

                // No dirty bit for the archetype: an UPDATE that dropped this line
                // would silently turn a known unit into UnitKinds.Unknown and strip
                // the client's mesh and health denominator on the next packet.
                existing.UnitKind);
        }

        private static bool HasFlag(byte mask, UnitDirtyMask flag) => (mask & (byte)flag) == (byte)flag;

        /// <summary>
        /// Spreads consecutive entity ids apart. Entity ids ascend by small
        /// steps, so the low bits alone would cluster every probe chain.
        /// </summary>
        private static int Hash(ulong entityId)
        {
            unchecked
            {
                var mixed = entityId * 0x9E3779B97F4A7C15UL;
                mixed ^= mixed >> 29;
                return (int)(mixed & 0x7FFFFFFFUL);
            }
        }

        private bool TryFindSlot(EntityId entity, out int slot)
        {
            var bucket = Hash(entity.Value) & _indexMask;
            for (var probes = 0; probes <= _indexMask; probes++)
            {
                var entry = _index[bucket];
                if (entry == EmptyBucket)
                {
                    slot = -1;
                    return false;
                }

                slot = entry - 1;
                if (_slots[slot].Entity == entity)
                {
                    return true;
                }

                bucket = (bucket + 1) & _indexMask;
            }

            // Unreachable while the load factor stays at or below 0.5; the bound
            // keeps a corrupted index from hanging the client thread.
            slot = -1;
            return false;
        }

        private void InsertIndex(ulong entityId, int slot)
        {
            var bucket = Hash(entityId) & _indexMask;
            for (var probes = 0; probes <= _indexMask; probes++)
            {
                if (_index[bucket] == EmptyBucket)
                {
                    _index[bucket] = slot + 1;
                    return;
                }

                bucket = (bucket + 1) & _indexMask;
            }
        }

        /// <summary>
        /// Removes one entry and closes the hole by shifting later entries back,
        /// which keeps every probe chain intact without tombstone markers.
        /// </summary>
        private void RemoveFromIndex(ulong entityId, int slot)
        {
            var hole = Hash(entityId) & _indexMask;
            for (var probes = 0; probes <= _indexMask; probes++)
            {
                if (_index[hole] - 1 == slot)
                {
                    break;
                }

                hole = (hole + 1) & _indexMask;
            }

            _index[hole] = EmptyBucket;

            var candidate = hole;
            for (var probes = 0; probes <= _indexMask; probes++)
            {
                candidate = (candidate + 1) & _indexMask;
                var entry = _index[candidate];
                if (entry == EmptyBucket)
                {
                    return;
                }

                var natural = Hash(_slots[entry - 1].Entity.Value) & _indexMask;
                if (!LiesInCyclicInterval(natural, hole, candidate))
                {
                    _index[hole] = entry;
                    _index[candidate] = EmptyBucket;
                    hole = candidate;
                }
            }
        }

        /// <summary>
        /// True when <paramref name="value"/> lies in the cyclic half-open
        /// interval (<paramref name="from"/>, <paramref name="to"/>]. An entry
        /// whose natural bucket falls inside that interval must stay where it is;
        /// any other entry probed past the hole and may move back into it.
        /// </summary>
        private static bool LiesInCyclicInterval(int value, int from, int to)
        {
            return from <= to
                ? value > from && value <= to
                : value > from || value <= to;
        }
    }
}
