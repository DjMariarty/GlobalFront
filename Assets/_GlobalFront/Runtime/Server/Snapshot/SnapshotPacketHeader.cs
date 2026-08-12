using System;

namespace GlobalFront.Server.Snapshot
{
    /// <summary>
    /// Immutable packet header for snapshot serialization. Contains protocol
    /// version, simulation tick, and unit count. The header is always serialized
    /// in little-endian byte order.
    ///
    /// Binary layout (16 bytes):
    /// - Offset 0: ProtocolVersion (uint32, 4 bytes, little-endian)
    /// - Offset 4: Tick (uint64, 8 bytes, little-endian)
    /// - Offset 12: UnitCount (uint32, 4 bytes, little-endian)
    /// </summary>
    public readonly struct SnapshotPacketHeader : IEquatable<SnapshotPacketHeader>
    {
        public SnapshotPacketHeader(uint protocolVersion, ulong tick, uint unitCount)
        {
            ProtocolVersion = protocolVersion;
            Tick = tick;
            UnitCount = unitCount;
        }

        /// <summary>Protocol version of this packet.</summary>
        public uint ProtocolVersion { get; }

        /// <summary>Simulation tick when this snapshot was captured.</summary>
        public ulong Tick { get; }

        /// <summary>Number of unit snapshots in the packet payload.</summary>
        public uint UnitCount { get; }

        public bool Equals(SnapshotPacketHeader other) =>
            ProtocolVersion == other.ProtocolVersion &&
            Tick == other.Tick &&
            UnitCount == other.UnitCount;

        public override bool Equals(object obj) =>
            obj is SnapshotPacketHeader other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(ProtocolVersion, Tick, UnitCount);

        public override string ToString() =>
            $"SnapshotPacket(v{ProtocolVersion}, tick={Tick}, units={UnitCount})";

        public static bool operator ==(SnapshotPacketHeader left, SnapshotPacketHeader right) =>
            left.Equals(right);

        public static bool operator !=(SnapshotPacketHeader left, SnapshotPacketHeader right) =>
            !left.Equals(right);
    }
}