using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Commands
{
    /// <summary>
    /// Result of decoding a wire command.
    /// </summary>
    public enum CommandWireError : byte
    {
        None = 0,
        TooShort = 1,
        BadVersion = 2,
        UnknownCommandType = 3,
        TruncatedPayload = 4,
        TooManyEntities = 5,
        InvalidEntity = 6,
        InvalidPlayer = 7,
        InvalidFormation = 8,
        TrailingBytes = 9
    }

    /// <summary>
    /// Decoded wire command: the additive transport representation of a
    /// gameplay command (Phase 2.5, ADR-009). The authoritative
    /// <see cref="CommandHeader"/> type itself is unchanged; this codec only
    /// serializes its fields plus the command payload in a versioned,
    /// little-endian, deterministic layout.
    /// </summary>
    public sealed class WireCommand
    {
        public CommandHeader Header;
        public EntityId[] Entities;
        public WorldPointMm Destination;
        public FormationSpec Formation;
        public EntityId Target;
    }

    /// <summary>
    /// Additive, versioned, little-endian wire codec for gameplay commands
    /// (Phase 2.5, ADR-009). Used by both the client
    /// <c>NetworkCommandChannel</c> and the server transport host; the
    /// authoritative validation of decoded commands stays entirely inside
    /// <c>MatchServer</c>.
    ///
    /// Layout:
    /// <code>
    /// [0]      u8   Version (CommandWireCodec.Version)
    /// [1]      u8   GameCommandType
    /// [2]      u8   PlayerId
    /// [3..7]   u32  Sequence
    /// [7..15]  u64  RequestedTick
    /// Move:    u8 count | count * u64 entities | i32 destX | i32 destZ
    ///          | i32 columns | i32 spacingMm | u8 facing
    /// Attack:  u8 count | count * u64 attackers | u64 target
    /// Stop:    u8 count | count * u64 entities
    /// </code>
    /// </summary>
    public static class CommandWireCodec
    {
        /// <summary>Wire format version; bump on any layout change.</summary>
        public const byte Version = 1;

        /// <summary>Fixed size of the common command header on the wire.</summary>
        public const int HeaderSizeBytes = 15;

        public static int GetMoveSize(int entityCount) =>
            HeaderSizeBytes + 1 + entityCount * 8 + 4 + 4 + 4 + 4 + 1;

        public static int GetAttackSize(int attackerCount) =>
            HeaderSizeBytes + 1 + attackerCount * 8 + 8;

        public static int GetStopSize(int entityCount) =>
            HeaderSizeBytes + 1 + entityCount * 8;

        public static int EncodeMove(
            byte[] buffer,
            CommandHeader header,
            EntityId[] entities,
            WorldPointMm destination,
            FormationSpec formation)
        {
            var offset = WriteHeader(buffer, header);
            offset = WriteEntityList(buffer, offset, entities);
            offset = WriteInt32(buffer, offset, destination.X);
            offset = WriteInt32(buffer, offset, destination.Z);
            offset = WriteInt32(buffer, offset, formation.Columns);
            offset = WriteInt32(buffer, offset, formation.SpacingMm);
            buffer[offset++] = (byte)formation.Facing;
            return offset;
        }

        public static int EncodeAttack(
            byte[] buffer,
            CommandHeader header,
            EntityId[] attackers,
            EntityId target)
        {
            var offset = WriteHeader(buffer, header);
            offset = WriteEntityList(buffer, offset, attackers);
            return WriteUInt64(buffer, offset, target.Value);
        }

        public static int EncodeStop(byte[] buffer, CommandHeader header, EntityId[] entities)
        {
            var offset = WriteHeader(buffer, header);
            return WriteEntityList(buffer, offset, entities);
        }

        /// <summary>
        /// Decodes and structurally validates a wire command starting at
        /// <paramref name="offset"/> for <paramref name="length"/> bytes.
        /// Structural validation only: gameplay authority (ownership, tick
        /// windows, entity state) remains with the authoritative server.
        /// </summary>
        public static bool TryDecode(
            byte[] data,
            int offset,
            int length,
            out WireCommand command,
            out CommandWireError error)
        {
            command = null;
            error = CommandWireError.None;

            var end = offset + length;
            if (data == null || length < HeaderSizeBytes || offset < 0 || end > data.Length)
            {
                error = CommandWireError.TooShort;
                return false;
            }

            var version = data[offset++];
            if (version != Version)
            {
                error = CommandWireError.BadVersion;
                return false;
            }

            var type = (GameCommandType)data[offset++];
            if (type != GameCommandType.Move &&
                type != GameCommandType.Attack &&
                type != GameCommandType.Stop)
            {
                error = CommandWireError.UnknownCommandType;
                return false;
            }

            var player = new PlayerId(data[offset++]);
            if (!player.IsValid)
            {
                error = CommandWireError.InvalidPlayer;
                return false;
            }

            var sequence = ReadUInt32(data, ref offset);
            var requestedTick = ReadUInt64(data, ref offset);
            var header = new CommandHeader(player, sequence, requestedTick, type);

            command = new WireCommand { Header = header };

            switch (type)
            {
                case GameCommandType.Move:
                    if (!TryReadEntityList(data, end, ref offset, out command.Entities, out error))
                    {
                        return false;
                    }

                    if (offset + 17 > end)
                    {
                        error = CommandWireError.TruncatedPayload;
                        return false;
                    }

                    var destX = ReadInt32(data, ref offset);
                    var destZ = ReadInt32(data, ref offset);
                    var columns = ReadInt32(data, ref offset);
                    var spacingMm = ReadInt32(data, ref offset);
                    var facing = (CardinalFacing)data[offset++];
                    command.Destination = new WorldPointMm(destX, destZ);
                    command.Formation = new FormationSpec(columns, spacingMm, facing);
                    if (!command.Formation.IsValid)
                    {
                        error = CommandWireError.InvalidFormation;
                        return false;
                    }

                    break;

                case GameCommandType.Attack:
                    if (!TryReadEntityList(data, end, ref offset, out command.Entities, out error))
                    {
                        return false;
                    }

                    if (offset + 8 > end)
                    {
                        error = CommandWireError.TruncatedPayload;
                        return false;
                    }

                    command.Target = new EntityId(ReadUInt64(data, ref offset));
                    break;

                case GameCommandType.Stop:
                    if (!TryReadEntityList(data, end, ref offset, out command.Entities, out error))
                    {
                        return false;
                    }

                    break;
            }

            if (offset != end)
            {
                error = CommandWireError.TrailingBytes;
                return false;
            }

            return true;
        }

        private static int WriteHeader(byte[] buffer, CommandHeader header)
        {
            var offset = 0;
            buffer[offset++] = Version;
            buffer[offset++] = (byte)header.Type;
            buffer[offset++] = header.Player.Value;
            offset = WriteUInt32(buffer, offset, header.Sequence);
            return WriteUInt64(buffer, offset, header.RequestedTick);
        }

        private static int WriteEntityList(byte[] buffer, int offset, EntityId[] entities)
        {
            buffer[offset++] = (byte)entities.Length;
            for (var index = 0; index < entities.Length; index++)
            {
                offset = WriteUInt64(buffer, offset, entities[index].Value);
            }

            return offset;
        }

        private static bool TryReadEntityList(
            byte[] data,
            int end,
            ref int offset,
            out EntityId[] entities,
            out CommandWireError error)
        {
            entities = null;
            error = CommandWireError.None;

            if (offset + 1 > end)
            {
                error = CommandWireError.TruncatedPayload;
                return false;
            }

            var count = data[offset++];
            if (count == 0 || count > SimulationConstants.MaxSelectedEntities)
            {
                error = CommandWireError.TooManyEntities;
                return false;
            }

            if (offset + count * 8 > end)
            {
                error = CommandWireError.TruncatedPayload;
                return false;
            }

            entities = new EntityId[count];
            for (var index = 0; index < count; index++)
            {
                var value = ReadUInt64(data, ref offset);
                if (value == 0)
                {
                    error = CommandWireError.InvalidEntity;
                    return false;
                }

                entities[index] = new EntityId(value);
            }

            return true;
        }

        private static int WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            buffer[offset++] = (byte)((value >> 16) & 0xFF);
            buffer[offset++] = (byte)((value >> 24) & 0xFF);
            return offset;
        }

        private static int WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            buffer[offset++] = (byte)((value >> 16) & 0xFF);
            buffer[offset++] = (byte)((value >> 24) & 0xFF);
            buffer[offset++] = (byte)((value >> 32) & 0xFF);
            buffer[offset++] = (byte)((value >> 40) & 0xFF);
            buffer[offset++] = (byte)((value >> 48) & 0xFF);
            buffer[offset++] = (byte)((value >> 56) & 0xFF);
            return offset;
        }

        private static int WriteInt32(byte[] buffer, int offset, int value) =>
            WriteUInt32(buffer, offset, (uint)value);

        private static uint ReadUInt32(byte[] data, ref int offset)
        {
            var value = (uint)(data[offset] |
                (data[offset + 1] << 8) |
                (data[offset + 2] << 16) |
                (data[offset + 3] << 24));
            offset += 4;
            return value;
        }

        private static ulong ReadUInt64(byte[] data, ref int offset)
        {
            var value = (ulong)data[offset] |
                ((ulong)data[offset + 1] << 8) |
                ((ulong)data[offset + 2] << 16) |
                ((ulong)data[offset + 3] << 24) |
                ((ulong)data[offset + 4] << 32) |
                ((ulong)data[offset + 5] << 40) |
                ((ulong)data[offset + 6] << 48) |
                ((ulong)data[offset + 7] << 56);
            offset += 8;
            return value;
        }

        private static int ReadInt32(byte[] data, ref int offset) =>
            (int)ReadUInt32(data, ref offset);
    }
}
