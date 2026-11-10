using System;
using GlobalFront.Core.Catalog;
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

        // Step 5.5.2 (v2): the blueprint-era payloads. Only the fields of the
        // command's own type are meaningful; the rest stay default.

        /// <summary>Build and Produce: the registry row the command targets.</summary>
        public BlueprintId Blueprint;

        /// <summary>Build: origin cell on the construction grid, X axis.</summary>
        public int GridCellX;

        /// <summary>Build: origin cell on the construction grid, Z axis.</summary>
        public int GridCellZ;

        /// <summary>
        /// Build: the builder entity (0 = modular/global build). Produce and
        /// SetRally: the factory entity (never 0). Cancel: the entity to
        /// cancel on (never 0) — a construction frame when SlotIndex &lt; 0,
        /// the owning factory when SlotIndex &gt;= 0 (N-05).
        /// </summary>
        public EntityId SubjectEntity;

        /// <summary>SetRally: the rally point in world millimetres.</summary>
        public WorldPointMm RallyPoint;

        /// <summary>
        /// N-05 (Cancel): &lt; 0 cancels the construction frame
        /// <see cref="SubjectEntity"/>; &gt;= 0 cancels that production
        /// queue slot of the factory <see cref="SubjectEntity"/>.
        /// </summary>
        public int SlotIndex;
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
    /// Build:   u16 blueprintId | i32 gridCellX | i32 gridCellZ | u64 builder
    ///          (builder 0 = modular/global build; Step 5.5.2)
    /// Produce: u64 factory | u16 blueprintId (Step 5.5.2)
    /// SetRally: u64 factory | i32 rallyX | i32 rallyZ (Step 5.5.2)
    /// Cancel:  u64 target | i32 slotIndex — slotIndex &lt; 0 = construction
    ///          frame, &gt;= 0 = production queue slot (N-05, ТЗ 6.0-B)
    /// </code>
    /// </summary>
    public static class CommandWireCodec
    {
        /// <summary>
        /// Wire format version; bump on any layout change. Version 2 (Step
        /// 5.5.2) adds the Build/Produce/SetRally payloads; the Move/Attack/Stop
        /// layouts are byte-for-byte those of version 1, but the header carries
        /// the version byte, so a v1 peer and a v2 peer reject each other's
        /// packets with <see cref="CommandWireError.BadVersion"/> rather than
        /// mis-read the payload.
        /// </summary>
        public const byte Version = 2;

        /// <summary>Fixed size of the common command header on the wire.</summary>
        public const int HeaderSizeBytes = 15;

        public static int GetMoveSize(int entityCount) =>
            HeaderSizeBytes + 1 + entityCount * 8 + 4 + 4 + 4 + 4 + 1;

        public static int GetAttackSize(int attackerCount) =>
            HeaderSizeBytes + 1 + attackerCount * 8 + 8;

        public static int GetStopSize(int entityCount) =>
            HeaderSizeBytes + 1 + entityCount * 8;

        /// <summary>Header 15 + blueprint 2 + cells 8 + builder 8.</summary>
        public static int GetBuildSize() => HeaderSizeBytes + 2 + 4 + 4 + 8;

        /// <summary>Header 15 + factory 8 + blueprint 2.</summary>
        public static int GetProduceSize() => HeaderSizeBytes + 8 + 2;

        /// <summary>Header 15 + factory 8 + rally 8.</summary>
        public static int GetSetRallySize() => HeaderSizeBytes + 8 + 4 + 4;

        /// <summary>Header 15 + target 8 + slot 4 (N-05).</summary>
        public static int GetCancelSize() => HeaderSizeBytes + 8 + 4;

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
        /// Step 5.5.2: build placement. <paramref name="builder"/> may be
        /// <c>default</c> (0) — the modular/global build that needs no builder
        /// unit on the wire.
        /// </summary>
        public static int EncodeBuild(
            byte[] buffer,
            CommandHeader header,
            BlueprintId blueprint,
            int gridCellX,
            int gridCellZ,
            EntityId builder)
        {
            var offset = WriteHeader(buffer, header);
            offset = WriteUInt16(buffer, offset, blueprint.Value);
            offset = WriteInt32(buffer, offset, gridCellX);
            offset = WriteInt32(buffer, offset, gridCellZ);
            return WriteUInt64(buffer, offset, builder.Value);
        }

        /// <summary>Step 5.5.2: production queue entry for a completed factory.</summary>
        public static int EncodeProduce(
            byte[] buffer,
            CommandHeader header,
            EntityId factory,
            BlueprintId blueprint)
        {
            var offset = WriteHeader(buffer, header);
            offset = WriteUInt64(buffer, offset, factory.Value);
            return WriteUInt16(buffer, offset, blueprint.Value);
        }

        /// <summary>Step 5.5.2: rally point override for a factory.</summary>
        public static int EncodeSetRally(
            byte[] buffer,
            CommandHeader header,
            EntityId factory,
            WorldPointMm rallyPoint)
        {
            var offset = WriteHeader(buffer, header);
            offset = WriteUInt64(buffer, offset, factory.Value);
            offset = WriteInt32(buffer, offset, rallyPoint.X);
            return WriteInt32(buffer, offset, rallyPoint.Z);
        }

        /// <summary>
        /// N-05 (ТЗ 6.0-B): cancel a construction frame (slotIndex &lt; 0) or
        /// a production queue slot (slotIndex &gt;= 0) of
        /// <paramref name="target"/>. The authoritative refund and
        /// ownership rules stay inside <c>MatchServer.TryOrderCancel</c>.
        /// </summary>
        public static int EncodeCancel(
            byte[] buffer,
            CommandHeader header,
            EntityId target,
            int slotIndex)
        {
            var offset = WriteHeader(buffer, header);
            offset = WriteUInt64(buffer, offset, target.Value);
            return WriteInt32(buffer, offset, slotIndex);
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
                type != GameCommandType.Stop &&
                type != GameCommandType.Build &&
                type != GameCommandType.Produce &&
                type != GameCommandType.SetRally &&
                type != GameCommandType.Cancel)
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

                case GameCommandType.Build:
                    if (offset + 18 > end)
                    {
                        error = CommandWireError.TruncatedPayload;
                        return false;
                    }

                    command.Blueprint = new BlueprintId(ReadUInt16(data, ref offset));
                    command.GridCellX = ReadInt32(data, ref offset);
                    command.GridCellZ = ReadInt32(data, ref offset);
                    // A 0 builder is the modular/global build: legal on the wire,
                    // ownership and geometry stay with the authoritative server.
                    command.SubjectEntity = new EntityId(ReadUInt64(data, ref offset));
                    break;

                case GameCommandType.Produce:
                    if (offset + 10 > end)
                    {
                        error = CommandWireError.TruncatedPayload;
                        return false;
                    }

                    var factoryEntity = ReadUInt64(data, ref offset);
                    if (factoryEntity == 0)
                    {
                        error = CommandWireError.InvalidEntity;
                        return false;
                    }

                    command.SubjectEntity = new EntityId(factoryEntity);
                    command.Blueprint = new BlueprintId(ReadUInt16(data, ref offset));
                    break;

                case GameCommandType.SetRally:
                    if (offset + 16 > end)
                    {
                        error = CommandWireError.TruncatedPayload;
                        return false;
                    }

                    var rallyFactory = ReadUInt64(data, ref offset);
                    if (rallyFactory == 0)
                    {
                        error = CommandWireError.InvalidEntity;
                        return false;
                    }

                    command.SubjectEntity = new EntityId(rallyFactory);
                    var rallyX = ReadInt32(data, ref offset);
                    var rallyZ = ReadInt32(data, ref offset);
                    command.RallyPoint = new WorldPointMm(rallyX, rallyZ);
                    break;

                case GameCommandType.Cancel:
                    if (offset + 12 > end)
                    {
                        error = CommandWireError.TruncatedPayload;
                        return false;
                    }

                    var cancelTarget = ReadUInt64(data, ref offset);
                    if (cancelTarget == 0)
                    {
                        error = CommandWireError.InvalidEntity;
                        return false;
                    }

                    command.SubjectEntity = new EntityId(cancelTarget);
                    command.SlotIndex = ReadInt32(data, ref offset);
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

        private static int WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            return offset;
        }

        private static ushort ReadUInt16(byte[] data, ref int offset)
        {
            var value = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            return value;
        }

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
