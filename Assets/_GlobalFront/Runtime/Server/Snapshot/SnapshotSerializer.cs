using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;

namespace GlobalFront.Server.Snapshot
{
    /// <summary>
    /// Deterministic binary serializer for ServerUnitSnapshot packets.
    /// Produces a fixed-layout byte array suitable for network transport.
    ///
    /// Packet layout (little-endian):
    /// - Header (16 bytes): ProtocolVersion, Tick, UnitCount
    /// - Payload (UnitCount * 39 bytes): array of ServerUnitSnapshot
    ///
    /// Each ServerUnitSnapshot (39 bytes):
    /// - Entity.Value (uint64, 8 bytes)
    /// - Owner.Value (uint8, 1 byte)
    /// - Position.X (int32, 4 bytes)
    /// - Position.Z (int32, 4 bytes)
    /// - CurrentHealth (int32, 4 bytes)
    /// - HasMoveTarget (uint8, 1 byte: 0 or 1)
    /// - MoveTarget.X (int32, 4 bytes)
    /// - MoveTarget.Z (int32, 4 bytes)
    /// - AttackTarget.Value (uint64, 8 bytes)
    /// - AutoAcquireEnemies (uint8, 1 byte: 0 or 1)
    ///
    /// The serializer assumes snapshots are provided in canonical entity-id order
    /// (as returned by MatchServer.GetAllSnapshots). No reordering is performed.
    /// </summary>
    public static class SnapshotSerializer
    {
        /// <summary>
        /// Serializes a snapshot packet header and unit array into a byte array.
        /// The input array MUST be in canonical entity-id order (sorted by Entity.Value).
        /// </summary>
        /// <param name="header">Packet header with protocol version, tick, and unit count.</param>
        /// <param name="snapshots">Array of unit snapshots in canonical order.</param>
        /// <returns>Deterministic byte array representing the complete packet.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when header.UnitCount does not match snapshots.Length.
        /// </exception>
        public static byte[] Serialize(SnapshotPacketHeader header, ServerUnitSnapshot[] snapshots)
        {
            if (snapshots == null)
                throw new ArgumentNullException(nameof(snapshots));

            if (header.UnitCount != snapshots.Length)
                throw new ArgumentException(
                    $"Header UnitCount ({header.UnitCount}) does not match array length ({snapshots.Length}).",
                    nameof(header));

            // Verify canonical entity-id order
            for (var i = 1; i < snapshots.Length; i++)
            {
                if (snapshots[i].Entity.Value < snapshots[i - 1].Entity.Value)
                {
                    throw new ArgumentException(
                        $"Snapshots must be in canonical entity-id order. Entity at index {i} (value {snapshots[i].Entity.Value}) " +
                        $"is less than entity at index {i - 1} (value {snapshots[i - 1].Entity.Value}).",
                        nameof(snapshots));
                }
            }

            var totalSize = SnapshotProtocol.HeaderSizeBytes +
                           (int)header.UnitCount * SnapshotProtocol.SnapshotSizeBytes;
            var buffer = new byte[totalSize];
            var offset = 0;

            // Write header (little-endian)
            WriteUInt32(buffer, ref offset, header.ProtocolVersion);
            WriteUInt64(buffer, ref offset, header.Tick);
            WriteUInt32(buffer, ref offset, header.UnitCount);

            // Write each snapshot
            for (var i = 0; i < snapshots.Length; i++)
            {
                var snapshot = snapshots[i];
                WriteUInt64(buffer, ref offset, snapshot.Entity.Value);
                WriteUInt8(buffer, ref offset, snapshot.Owner.Value);
                WriteInt32(buffer, ref offset, snapshot.Position.X);
                WriteInt32(buffer, ref offset, snapshot.Position.Z);
                WriteInt32(buffer, ref offset, snapshot.CurrentHealth);
                WriteUInt8(buffer, ref offset, (byte)(snapshot.HasMoveTarget ? 1 : 0));
                WriteInt32(buffer, ref offset, snapshot.MoveTarget.X);
                WriteInt32(buffer, ref offset, snapshot.MoveTarget.Z);
                WriteUInt64(buffer, ref offset, snapshot.AttackTarget.Value);
                WriteUInt8(buffer, ref offset, (byte)(snapshot.AutoAcquireEnemies ? 1 : 0));
            }

            return buffer;
        }

        private static void WriteUInt8(byte[] buffer, ref int offset, byte value)
        {
            buffer[offset++] = value;
        }

        private static void WriteUInt32(byte[] buffer, ref int offset, uint value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            buffer[offset++] = (byte)((value >> 16) & 0xFF);
            buffer[offset++] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteUInt64(byte[] buffer, ref int offset, ulong value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            buffer[offset++] = (byte)((value >> 16) & 0xFF);
            buffer[offset++] = (byte)((value >> 24) & 0xFF);
            buffer[offset++] = (byte)((value >> 32) & 0xFF);
            buffer[offset++] = (byte)((value >> 40) & 0xFF);
            buffer[offset++] = (byte)((value >> 48) & 0xFF);
            buffer[offset++] = (byte)((value >> 56) & 0xFF);
        }

        private static void WriteInt32(byte[] buffer, ref int offset, int value)
        {
            var unsigned = (uint)value;
            buffer[offset++] = (byte)(unsigned & 0xFF);
            buffer[offset++] = (byte)((unsigned >> 8) & 0xFF);
            buffer[offset++] = (byte)((unsigned >> 16) & 0xFF);
            buffer[offset++] = (byte)((unsigned >> 24) & 0xFF);
        }
    }
}