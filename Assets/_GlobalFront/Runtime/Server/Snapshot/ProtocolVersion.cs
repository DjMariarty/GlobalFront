namespace GlobalFront.Server.Snapshot
{
    /// <summary>
    /// Snapshot wire-format protocol versioning. The protocol version is a
    /// monotonically increasing identifier embedded in every snapshot packet
    /// header. A deserializer MUST reject packets whose version does not match
    /// the supported version to prevent silent data corruption when the
    /// snapshot format evolves.
    /// </summary>
    public static class SnapshotProtocol
    {
        /// <summary>
        /// Current protocol version. Increment this value whenever the binary
        /// layout of <see cref="Server.ServerUnitSnapshot"/> or
        /// <see cref="SnapshotPacketHeader"/> changes.
        /// </summary>
        public const uint Version = 1;

        /// <summary>
        /// Size of the packet header in bytes. The header contains:
        /// - ProtocolVersion (uint32, 4 bytes)
        /// - Tick (uint64, 8 bytes)
        /// - UnitCount (uint32, 4 bytes)
        /// </summary>
        public const int HeaderSizeBytes = 16;

        /// <summary>
        /// Size of a single serialized ServerUnitSnapshot in bytes:
        /// - Entity.Value (uint64, 8 bytes)
        /// - Owner.Value (uint8, 1 byte)
        /// - Position.X (int32, 4 bytes)
        /// - Position.Z (int32, 4 bytes)
        /// - CurrentHealth (int32, 4 bytes)
        /// - HasMoveTarget (uint8, 1 byte)
        /// - MoveTarget.X (int32, 4 bytes)
        /// - MoveTarget.Z (int32, 4 bytes)
        /// - AttackTarget.Value (uint64, 8 bytes)
        /// - AutoAcquireEnemies (uint8, 1 byte)
        /// Total: 39 bytes per snapshot.
        /// </summary>
        public const int SnapshotSizeBytes = 39;
    }
}