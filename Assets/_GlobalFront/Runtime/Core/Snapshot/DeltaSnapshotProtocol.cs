using System;

namespace GlobalFront.Core.Snapshot
{
    /// <summary>
    /// Wire-format constants of the delta snapshot protocol (Phase 2.6, ADR-010).
    ///
    /// This is a version space of its own: <see cref="Version"/> evolves
    /// independently from Snapshot Protocol v1 (<c>SnapshotProtocol.Version</c>),
    /// from the carrier envelope version and from the message version. Only the
    /// ADD record deliberately reuses the byte layout of the v1 unit record so a
    /// keyframe/delta stream can be re-based without a second decoder.
    ///
    /// All values are protocol-significant constants: changing any of them
    /// requires a compatibility review and an ADR (DEVELOPMENT_STANDARD).
    /// </summary>
    public static class DeltaSnapshotProtocol
    {
        /// <summary>
        /// Delta wire-format version carried by every delta packet header.
        /// Bump on any change of the delta layout; never bump Snapshot Protocol v1.
        /// </summary>
        public const uint Version = 1;

        /// <summary>
        /// C2 message type of an establishing delta packet
        /// (0x01 = full v1 legacy, 0x02 = keyframe slice, 0x03 = delta,
        /// 0x04 = cumulative delta).
        /// </summary>
        public const byte MessageTypeDelta = 0x03;

        /// <summary>
        /// Fixed size of the delta packet header in bytes.
        /// See <see cref="DeltaSnapshotWireCodec"/> for the field-by-field layout.
        /// </summary>
        public const int HeaderSizeBytes = 36;

        /// <summary>
        /// Upper bound of the section area (ADD | UPDATE | REMOVE) that follows
        /// the header. A tick whose change-set does not fit is split into several
        /// packets sharing the same <c>Tick</c> and addressed by
        /// <c>PartIndex</c>/<c>PartCount</c>.
        /// </summary>
        public const int MaxPayloadBytes = 8192;

        /// <summary>Largest encodable delta packet: header plus maximal payload.</summary>
        public const int MaxPacketBytes = HeaderSizeBytes + MaxPayloadBytes;

        /// <summary>
        /// Fixed size of one ADD record. Identical to
        /// <c>SnapshotProtocol.SnapshotSizeBytes</c> of Snapshot Protocol v1:
        /// <c>Entity u64, Owner u8, PosX i32, PosZ i32, Health i32,
        /// HasMoveTarget u8, MoveTargetX i32, MoveTargetZ i32, AttackTarget u64,
        /// AutoAcquire u8</c>.
        /// </summary>
        public const int AddRecordSizeBytes = 39;

        /// <summary>Smallest UPDATE record: 1 id varint byte + 1 dirty-mask byte.</summary>
        public const int UpdateRecordMinSizeBytes = 2;

        /// <summary>Smallest REMOVE record: 1 id varint byte + 1 cause byte.</summary>
        public const int RemoveRecordMinSizeBytes = 2;

        /// <summary>
        /// Largest UPDATE record: 10 (id varint) + 1 (mask) + 1 (owner)
        /// + 8 (position) + 4 (health) + 1 (hasMoveTarget) + 8 (move target)
        /// + 10 (attack target varint) + 1 (auto-acquire).
        /// </summary>
        public const int UpdateRecordMaxSizeBytes = 44;

        /// <summary>Largest REMOVE record: 10 (id varint) + 1 (cause).</summary>
        public const int RemoveRecordMaxSizeBytes = 11;

        /// <summary>
        /// Maximum size of one LEB128 zigzag varint over the signed 64-bit range.
        /// Encodings longer than this are rejected as malformed.
        /// </summary>
        public const int VarintMaxSizeBytes = 10;
    }

    /// <summary>
    /// Header flags of a delta packet. Bits 2..7 are reserved in version 1 and
    /// MUST be zero: an unknown flag means the sender used a feature this codec
    /// cannot interpret, so such a packet is rejected instead of being guessed at.
    /// </summary>
    [Flags]
    public enum DeltaFlags : byte
    {
        None = 0,

        /// <summary>
        /// <see cref="DeltaSnapshotHeader.StateChecksum"/> carries a meaningful
        /// integrity fingerprint of the tick state (1 Hz cadence). When the flag
        /// is clear the checksum field is present but MUST be ignored.
        /// </summary>
        HasChecksum = 1 << 0,

        /// <summary>
        /// The REMOVE section also repeats tombstones inside the echo horizon
        /// (20 ticks) so a client that lost one delta does not keep ghost units.
        /// Echoed tombstones use the plain REMOVE record format and are applied
        /// idempotently.
        /// </summary>
        HasTombstoneEcho = 1 << 1
    }

    /// <summary>
    /// Per-unit dirty mask of an UPDATE record. One byte covers the whole
    /// replicated unit state; bit 7 is the reserved extension flag and is
    /// rejected in version 1.
    ///
    /// Fields are written in ascending bit order, one field group per set bit.
    /// Bit semantics are absolute (last-wins per field), never residual.
    /// </summary>
    [Flags]
    public enum UnitDirtyMask : byte
    {
        None = 0,

        /// <summary>Owner (u8).</summary>
        Owner = 1 << 0,

        /// <summary>PosX and PosZ (2 x i32).</summary>
        Position = 1 << 1,

        /// <summary>Current health (i32).</summary>
        Health = 1 << 2,

        /// <summary>
        /// HasMoveTarget (u8, 0/1). OD-14: when this bit is set and the flag
        /// decodes to 0 the move target is cleared and the coordinates are NOT
        /// transmitted, even if <see cref="MoveTarget"/> is set as well.
        /// </summary>
        HasMoveTarget = 1 << 3,

        /// <summary>
        /// MoveTargetX and MoveTargetZ (2 x i32). Written only while the move
        /// target is not being cleared (see <see cref="HasMoveTarget"/>).
        /// </summary>
        MoveTarget = 1 << 4,

        /// <summary>
        /// Attack target entity id, encoded as a zigzag varint relative to the
        /// entity id of the same record (0 = no target).
        /// </summary>
        AttackTarget = 1 << 5,

        /// <summary>Auto-acquire enemies (u8, 0/1).</summary>
        AutoAcquire = 1 << 6,

        /// <summary>
        /// Reserved extension flag. Unused in version 1; encoders must not set it
        /// and decoders reject it so a future wider mask cannot be
        /// misinterpreted as a v1 record.
        /// </summary>
        ReservedExtension = 1 << 7
    }
}
