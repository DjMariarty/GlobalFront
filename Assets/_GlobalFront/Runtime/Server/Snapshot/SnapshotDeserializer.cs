using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;

namespace GlobalFront.Server.Snapshot
{
    /// <summary>
    /// Deterministic binary deserializer for ServerUnitSnapshot packets.
    /// Validates packet structure and reconstructs ServerUnitSnapshot array.
    ///
    /// Deserialization rules:
    /// - Rejects packets with unsupported protocol version
    /// - Rejects packets shorter than header size
    /// - Rejects packets with insufficient payload for declared UnitCount
    /// - Rejects packets with malformed data
    /// - Assumes little-endian byte order
    /// - Returns snapshots in the same order as they appear in the packet
    /// </summary>
    public static class SnapshotDeserializer
    {
        /// <summary>
        /// Deserializes a snapshot packet from a byte array.
        /// </summary>
        /// <param name="data">Complete packet data including header and payload.</param>
        /// <returns>Tuple of deserialized header and array of ServerUnitSnapshot.</returns>
        /// <exception cref="SnapshotSerializationException">
        /// Thrown when packet is malformed, has unsupported version, or insufficient data.
        /// </exception>
        public static (SnapshotPacketHeader Header, ServerUnitSnapshot[] Snapshots) Deserialize(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            // Validate minimum size for header
            if (data.Length < SnapshotProtocol.HeaderSizeBytes)
            {
                throw new SnapshotSerializationException(
                    $"Packet too short: expected at least {SnapshotProtocol.HeaderSizeBytes} bytes for header, got {data.Length}.");
            }

            var offset = 0;

            // Read header
            var protocolVersion = ReadUInt32(data, ref offset);
            var tick = ReadUInt64(data, ref offset);
            var unitCount = ReadUInt32(data, ref offset);

            // Validate protocol version
            if (protocolVersion != SnapshotProtocol.Version)
            {
                throw new SnapshotSerializationException(
                    $"Unsupported protocol version: {protocolVersion}. Expected {SnapshotProtocol.Version}.");
            }

            // Validate payload size
            var expectedSize = SnapshotProtocol.HeaderSizeBytes +
                              (int)unitCount * SnapshotProtocol.SnapshotSizeBytes;
            if (data.Length < expectedSize)
            {
                throw new SnapshotSerializationException(
                    $"Packet too short: expected {expectedSize} bytes for {unitCount} units, got {data.Length}.");
            }

            // Deserialize snapshots
            var snapshots = new ServerUnitSnapshot[unitCount];
            for (var i = 0; i < unitCount; i++)
            {
                var entityValue = ReadUInt64(data, ref offset);
                var ownerValue = ReadUInt8(data, ref offset);
                var posX = ReadInt32(data, ref offset);
                var posZ = ReadInt32(data, ref offset);
                var currentHealth = ReadInt32(data, ref offset);
                var hasMoveTargetByte = ReadUInt8(data, ref offset);
                var moveTargetX = ReadInt32(data, ref offset);
                var moveTargetZ = ReadInt32(data, ref offset);
                var attackTargetValue = ReadUInt64(data, ref offset);
                var autoAcquireByte = ReadUInt8(data, ref offset);

                // Validate boolean fields
                if (hasMoveTargetByte != 0 && hasMoveTargetByte != 1)
                {
                    throw new SnapshotSerializationException(
                        $"Invalid HasMoveTarget value at unit {i}: {hasMoveTargetByte}. Expected 0 or 1.");
                }

                if (autoAcquireByte != 0 && autoAcquireByte != 1)
                {
                    throw new SnapshotSerializationException(
                        $"Invalid AutoAcquireEnemies value at unit {i}: {autoAcquireByte}. Expected 0 or 1.");
                }

                snapshots[i] = new ServerUnitSnapshot(
                    new EntityId(entityValue),
                    new PlayerId(ownerValue),
                    new WorldPointMm(posX, posZ),
                    currentHealth,
                    hasMoveTargetByte == 1,
                    new WorldPointMm(moveTargetX, moveTargetZ),
                    new EntityId(attackTargetValue),
                    autoAcquireByte == 1
                );
            }

            var header = new SnapshotPacketHeader(protocolVersion, tick, unitCount);
            return (header, snapshots);
        }

        private static byte ReadUInt8(byte[] buffer, ref int offset)
        {
            return buffer[offset++];
        }

        private static uint ReadUInt32(byte[] buffer, ref int offset)
        {
            var b0 = buffer[offset++];
            var b1 = buffer[offset++];
            var b2 = buffer[offset++];
            var b3 = buffer[offset++];
            return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        }

        private static ulong ReadUInt64(byte[] buffer, ref int offset)
        {
            var b0 = buffer[offset++];
            var b1 = buffer[offset++];
            var b2 = buffer[offset++];
            var b3 = buffer[offset++];
            var b4 = buffer[offset++];
            var b5 = buffer[offset++];
            var b6 = buffer[offset++];
            var b7 = buffer[offset++];
            return (ulong)b0 |
                   ((ulong)b1 << 8) |
                   ((ulong)b2 << 16) |
                   ((ulong)b3 << 24) |
                   ((ulong)b4 << 32) |
                   ((ulong)b5 << 40) |
                   ((ulong)b6 << 48) |
                   ((ulong)b7 << 56);
        }

        private static int ReadInt32(byte[] buffer, ref int offset)
        {
            var b0 = buffer[offset++];
            var b1 = buffer[offset++];
            var b2 = buffer[offset++];
            var b3 = buffer[offset++];
            var unsigned = (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
            return (int)unsigned;
        }
    }
}