using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;

namespace GlobalFront.Core.Snapshot
{
    /// <summary>
    /// Reason carried by a REMOVE (tombstone) record. Values above
    /// <see cref="FogOfWarHidden"/> are reserved in delta protocol version 1 and
    /// are rejected by <see cref="DeltaSnapshotWireCodec"/>.
    /// </summary>
    public enum DeltaRemoveCause : byte
    {
        /// <summary>The entity stopped existing in the authoritative simulation.</summary>
        Destroyed = 0,

        /// <summary>
        /// The entity left the receiving client's area of interest (fog of war).
        /// The entity is still alive on the server; the client drops its local
        /// representation.
        /// </summary>
        FogOfWarHidden = 1
    }

    /// <summary>
    /// Header of one delta snapshot packet (36 bytes on the wire, see
    /// <see cref="DeltaSnapshotWireCodec"/>). Pure value: it carries no Unity
    /// types and never enters deterministic simulation state.
    ///
    /// The header describes an establishing delta covering the interval
    /// <c>(BaseTick, Tick]</c>. Staleness and duplicate gating belong to the
    /// replication consumer, not to the codec; the consumer's keyframe
    /// reference (<c>KeyframeRef</c>, OD-14) travels on the wire since delta
    /// protocol version 1 as used by Phase 2.6.4.
    /// </summary>
    public readonly struct DeltaSnapshotHeader : IEquatable<DeltaSnapshotHeader>
    {
        public DeltaSnapshotHeader(
            byte messageType,
            uint deltaProtocolVersion,
            ulong tick,
            ulong baseTick,
            byte partIndex,
            byte partCount,
            byte flags,
            uint stateChecksum,
            ushort addCount,
            ushort updateCount,
            ushort removeCount,
            ushort keyframeRef = 0)
        {
            MessageType = messageType;
            DeltaProtocolVersion = deltaProtocolVersion;
            Tick = tick;
            BaseTick = baseTick;
            PartIndex = partIndex;
            PartCount = partCount;
            Flags = flags;
            StateChecksum = stateChecksum;
            AddCount = addCount;
            UpdateCount = updateCount;
            RemoveCount = removeCount;
            KeyframeRef = keyframeRef;
        }

        /// <summary>
        /// Builds a header for a single-part establishing delta of version 1,
        /// filling in the protocol-significant constants.
        /// </summary>
        public static DeltaSnapshotHeader CreateDelta(
            ulong tick,
            ulong baseTick,
            DeltaFlags flags,
            uint stateChecksum,
            ushort addCount,
            ushort updateCount,
            ushort removeCount,
            ushort keyframeRef = 0)
        {
            return new DeltaSnapshotHeader(
                DeltaSnapshotProtocol.MessageTypeDelta,
                DeltaSnapshotProtocol.Version,
                tick,
                baseTick,
                0,
                1,
                (byte)flags,
                stateChecksum,
                addCount,
                updateCount,
                removeCount,
                keyframeRef);
        }

        /// <summary>
        /// Builds a header for one part of a tick whose change-set was split
        /// across several packets (<paramref name="partCount"/> &gt; 1).
        /// </summary>
        public static DeltaSnapshotHeader CreateDeltaPart(
            ulong tick,
            ulong baseTick,
            byte partIndex,
            byte partCount,
            DeltaFlags flags,
            uint stateChecksum,
            ushort addCount,
            ushort updateCount,
            ushort removeCount,
            ushort keyframeRef = 0)
        {
            return new DeltaSnapshotHeader(
                DeltaSnapshotProtocol.MessageTypeDelta,
                DeltaSnapshotProtocol.Version,
                tick,
                baseTick,
                partIndex,
                partCount,
                (byte)flags,
                stateChecksum,
                addCount,
                updateCount,
                removeCount,
                keyframeRef);
        }

        /// <summary>C2 message type; <c>0x03</c> for an establishing delta.</summary>
        public byte MessageType { get; }

        /// <summary>Delta protocol version space (independent from v1).</summary>
        public uint DeltaProtocolVersion { get; }

        /// <summary>Tick of the state the receiver holds after applying the packet.</summary>
        public ulong Tick { get; }

        /// <summary>First (exclusive) tick of the covered change-set interval.</summary>
        public ulong BaseTick { get; }

        /// <summary>Zero-based index of this packet inside a split tick.</summary>
        public byte PartIndex { get; }

        /// <summary>Total number of packets the tick was split into (at least 1).</summary>
        public byte PartCount { get; }

        /// <summary>Combination of <see cref="DeltaFlags"/> values.</summary>
        public byte Flags { get; }

        /// <summary>
        /// Integrity fingerprint of the tick state. Meaningful only while
        /// <see cref="HasChecksum"/> is set; always present on the wire.
        /// </summary>
        public uint StateChecksum { get; }

        /// <summary>Number of ADD records in the packet.</summary>
        public ushort AddCount { get; }

        /// <summary>Number of UPDATE records in the packet.</summary>
        public ushort UpdateCount { get; }

        /// <summary>Number of REMOVE records in the packet.</summary>
        public ushort RemoveCount { get; }

        /// <summary>
        /// Keyframe generation the delta extends (OD-14 apply-gating reference):
        /// the sender's <c>KeyframeSeq</c> at the moment the delta was built.
        /// The receiver applies the delta only while this equals the generation
        /// of its installed baseline.
        /// </summary>
        public ushort KeyframeRef { get; }

        public bool HasChecksum => (Flags & (byte)DeltaFlags.HasChecksum) != 0;

        public bool HasTombstoneEcho => (Flags & (byte)DeltaFlags.HasTombstoneEcho) != 0;

        public bool Equals(DeltaSnapshotHeader other) =>
            MessageType == other.MessageType &&
            DeltaProtocolVersion == other.DeltaProtocolVersion &&
            Tick == other.Tick &&
            BaseTick == other.BaseTick &&
            PartIndex == other.PartIndex &&
            PartCount == other.PartCount &&
            Flags == other.Flags &&
            StateChecksum == other.StateChecksum &&
            AddCount == other.AddCount &&
            UpdateCount == other.UpdateCount &&
            RemoveCount == other.RemoveCount &&
            KeyframeRef == other.KeyframeRef;

        public override bool Equals(object obj) =>
            obj is DeltaSnapshotHeader other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(MessageType);
            hash.Add(DeltaProtocolVersion);
            hash.Add(Tick);
            hash.Add(BaseTick);
            hash.Add(PartIndex);
            hash.Add(PartCount);
            hash.Add(Flags);
            hash.Add(StateChecksum);
            hash.Add(AddCount);
            hash.Add(UpdateCount);
            hash.Add(RemoveCount);
            hash.Add(KeyframeRef);
            return hash.ToHashCode();
        }

        public override string ToString() =>
            $"DeltaSnapshot(type=0x{MessageType:X2}, v{DeltaProtocolVersion}, tick={Tick}, base={BaseTick}, " +
            $"part={PartIndex}/{PartCount}, flags=0x{Flags:X2}, kfRef={KeyframeRef}, " +
            $"add={AddCount}, update={UpdateCount}, remove={RemoveCount})";

        public static bool operator ==(DeltaSnapshotHeader left, DeltaSnapshotHeader right) =>
            left.Equals(right);

        public static bool operator !=(DeltaSnapshotHeader left, DeltaSnapshotHeader right) =>
            !left.Equals(right);
    }

    /// <summary>
    /// Canonical ADD record: a unit that appeared in the authoritative world.
    /// The wire form is byte-identical to the 39-byte unit record of Snapshot
    /// Protocol v1 (<see cref="DeltaSnapshotProtocol.AddRecordSizeBytes"/>), so a
    /// keyframe and a delta ADD section can be produced by the same writer.
    /// </summary>
    public readonly struct DeltaAddRecord : IEquatable<DeltaAddRecord>
    {
        public DeltaAddRecord(
            EntityId entity,
            PlayerId owner,
            WorldPointMm position,
            int currentHealth,
            bool hasMoveTarget,
            WorldPointMm moveTarget,
            EntityId attackTarget,
            bool autoAcquireEnemies)
        {
            Entity = entity;
            Owner = owner;
            Position = position;
            CurrentHealth = currentHealth;
            HasMoveTarget = hasMoveTarget;
            MoveTarget = moveTarget;
            AttackTarget = attackTarget;
            AutoAcquireEnemies = autoAcquireEnemies;
        }

        /// <summary>Entity identity; strictly ascending inside the ADD section.</summary>
        public EntityId Entity { get; }

        public PlayerId Owner { get; }

        public WorldPointMm Position { get; }

        public int CurrentHealth { get; }

        public bool HasMoveTarget { get; }

        public WorldPointMm MoveTarget { get; }

        public EntityId AttackTarget { get; }

        public bool AutoAcquireEnemies { get; }

        public bool Equals(DeltaAddRecord other) =>
            Entity == other.Entity &&
            Owner == other.Owner &&
            Position == other.Position &&
            CurrentHealth == other.CurrentHealth &&
            HasMoveTarget == other.HasMoveTarget &&
            MoveTarget == other.MoveTarget &&
            AttackTarget == other.AttackTarget &&
            AutoAcquireEnemies == other.AutoAcquireEnemies;

        public override bool Equals(object obj) =>
            obj is DeltaAddRecord other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Entity);
            hash.Add(Owner);
            hash.Add(Position);
            hash.Add(CurrentHealth);
            hash.Add(HasMoveTarget);
            hash.Add(MoveTarget);
            hash.Add(AttackTarget);
            hash.Add(AutoAcquireEnemies);
            return hash.ToHashCode();
        }

        public override string ToString() =>
            $"Add(entity={Entity}, owner={Owner}, pos={Position}, hp={CurrentHealth}, " +
            $"moveTarget={(HasMoveTarget ? MoveTarget.ToString() : "none")}, attackTarget={AttackTarget}, " +
            $"autoAcquire={AutoAcquireEnemies})";

        public static bool operator ==(DeltaAddRecord left, DeltaAddRecord right) => left.Equals(right);

        public static bool operator !=(DeltaAddRecord left, DeltaAddRecord right) => !left.Equals(right);
    }

    /// <summary>
    /// UPDATE record: absolute per-field values of one unit, restricted to the
    /// fields selected by <see cref="DirtyMask"/>. Fields whose bit is clear are
    /// not transmitted and keep their default value in a decoded record, so they
    /// MUST be ignored by the consumer.
    ///
    /// OD-14: when <see cref="UnitDirtyMask.HasMoveTarget"/> is set and
    /// <see cref="HasMoveTarget"/> is false the move target is being cleared and
    /// the coordinates are omitted from the wire even if
    /// <see cref="UnitDirtyMask.MoveTarget"/> is set as well. A decoded record
    /// then reports <see cref="MoveTarget"/> as <c>(0, 0)</c>.
    /// </summary>
    public readonly struct DeltaUpdateRecord : IEquatable<DeltaUpdateRecord>
    {
        public DeltaUpdateRecord(
            EntityId entity,
            byte dirtyMask,
            PlayerId owner,
            WorldPointMm position,
            int currentHealth,
            bool hasMoveTarget,
            WorldPointMm moveTarget,
            EntityId attackTarget,
            bool autoAcquireEnemies)
        {
            Entity = entity;
            DirtyMask = dirtyMask;
            Owner = owner;
            Position = position;
            CurrentHealth = currentHealth;
            HasMoveTarget = hasMoveTarget;
            MoveTarget = moveTarget;
            AttackTarget = attackTarget;
            AutoAcquireEnemies = autoAcquireEnemies;
        }

        /// <summary>Entity identity; strictly ascending inside the UPDATE section.</summary>
        public EntityId Entity { get; }

        /// <summary>Combination of <see cref="UnitDirtyMask"/> bits.</summary>
        public byte DirtyMask { get; }

        public PlayerId Owner { get; }

        public WorldPointMm Position { get; }

        public int CurrentHealth { get; }

        public bool HasMoveTarget { get; }

        public WorldPointMm MoveTarget { get; }

        public EntityId AttackTarget { get; }

        public bool AutoAcquireEnemies { get; }

        public bool HasFlag(UnitDirtyMask flag) => (DirtyMask & (byte)flag) == (byte)flag;

        public bool Equals(DeltaUpdateRecord other) =>
            Entity == other.Entity &&
            DirtyMask == other.DirtyMask &&
            Owner == other.Owner &&
            Position == other.Position &&
            CurrentHealth == other.CurrentHealth &&
            HasMoveTarget == other.HasMoveTarget &&
            MoveTarget == other.MoveTarget &&
            AttackTarget == other.AttackTarget &&
            AutoAcquireEnemies == other.AutoAcquireEnemies;

        public override bool Equals(object obj) =>
            obj is DeltaUpdateRecord other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Entity);
            hash.Add(DirtyMask);
            hash.Add(Owner);
            hash.Add(Position);
            hash.Add(CurrentHealth);
            hash.Add(HasMoveTarget);
            hash.Add(MoveTarget);
            hash.Add(AttackTarget);
            hash.Add(AutoAcquireEnemies);
            return hash.ToHashCode();
        }

        public override string ToString() =>
            $"Update(entity={Entity}, mask=0x{DirtyMask:X2}, owner={Owner}, pos={Position}, hp={CurrentHealth}, " +
            $"hasMoveTarget={HasMoveTarget}, moveTarget={MoveTarget}, attackTarget={AttackTarget}, " +
            $"autoAcquire={AutoAcquireEnemies})";

        public static bool operator ==(DeltaUpdateRecord left, DeltaUpdateRecord right) =>
            left.Equals(right);

        public static bool operator !=(DeltaUpdateRecord left, DeltaUpdateRecord right) =>
            !left.Equals(right);
    }

    /// <summary>
    /// REMOVE (tombstone) record. Removal is applied idempotently: removing an
    /// unknown entity id is a no-op for the consumer, not a protocol error.
    /// </summary>
    public readonly struct DeltaRemoveRecord : IEquatable<DeltaRemoveRecord>
    {
        public DeltaRemoveRecord(EntityId entity, byte cause)
        {
            Entity = entity;
            Cause = cause;
        }

        public DeltaRemoveRecord(EntityId entity, DeltaRemoveCause cause)
        {
            Entity = entity;
            Cause = (byte)cause;
        }

        /// <summary>Entity identity; strictly ascending inside the REMOVE section.</summary>
        public EntityId Entity { get; }

        /// <summary>One of the <see cref="DeltaRemoveCause"/> values.</summary>
        public byte Cause { get; }

        public bool Equals(DeltaRemoveRecord other) =>
            Entity == other.Entity && Cause == other.Cause;

        public override bool Equals(object obj) =>
            obj is DeltaRemoveRecord other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Entity, Cause);

        public override string ToString() => $"Remove(entity={Entity}, cause={Cause})";

        public static bool operator ==(DeltaRemoveRecord left, DeltaRemoveRecord right) =>
            left.Equals(right);

        public static bool operator !=(DeltaRemoveRecord left, DeltaRemoveRecord right) =>
            !left.Equals(right);
    }
}
