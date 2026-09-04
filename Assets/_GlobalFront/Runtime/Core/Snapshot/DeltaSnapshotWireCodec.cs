using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;

namespace GlobalFront.Core.Snapshot
{
    /// <summary>
    /// Outcome of a delta codec operation. Failures are values, never
    /// exceptions: a corrupt or truncated packet must be droppable inside the
    /// network event loop without unwinding the host thread.
    /// </summary>
    public enum DeltaCodecResult : byte
    {
        /// <summary>Operation completed; all produced values are valid.</summary>
        Ok = 0,

        /// <summary>
        /// Not enough bytes: the destination buffer cannot hold the packet, the
        /// source is truncated, or a destination record span is smaller than the
        /// count declared by the header.
        /// </summary>
        BufferTooSmall = 1,

        /// <summary>
        /// Structurally corrupt data: wrong message type, reserved flag or dirty
        /// mask bits, non-ascending entity ids, illegal field value, counts that
        /// contradict the payload, or trailing bytes.
        /// </summary>
        Malformed = 2,

        /// <summary>Packet exceeds <see cref="DeltaSnapshotProtocol.MaxPayloadBytes"/>.</summary>
        PayloadTooLarge = 3,

        /// <summary>Header carries an unsupported <c>DeltaProtocolVersion</c>.</summary>
        UnsupportedVersion = 4,

        /// <summary>
        /// Caller error detected before any byte was written: an inconsistent
        /// header, a count/span mismatch or an unencodable record.
        /// </summary>
        InvalidArgument = 5
    }

    /// <summary>
    /// Binary wire codec of the establishing delta snapshot (Phase 2.6, ADR-010).
    /// Engine-independent, deterministic, little-endian, byte-aligned and
    /// allocation-free: both directions work on caller-owned
    /// <see cref="Span{T}"/>/<see cref="ReadOnlySpan{T}"/> buffers, so the hot
    /// replication path never touches the GC.
    ///
    /// Snapshot Protocol v1 (<c>SnapshotSerializer</c>, <c>SnapshotProtocol.Version</c>)
    /// is left untouched; this codec lives in an independent version space and is
    /// byte-compatible with v1 only inside the ADD record.
    ///
    /// Packet layout (little-endian):
    /// <code>
    /// Header, 36 bytes:
    ///   0   u8   MessageType          = 0x03 (DeltaSnapshotProtocol.MessageTypeDelta)
    ///   1   u32  DeltaProtocolVersion = DeltaSnapshotProtocol.Version
    ///   5   u64  Tick                 state tick after applying the packet
    ///   13  u64  BaseTick             start of the covered change-set interval
    ///   21  u8   PartIndex
    ///   22  u8   PartCount            >= 1; a tick larger than MaxPayloadBytes is split
    ///   23  u8   Flags                DeltaFlags; bits 2..7 reserved, must be 0
    ///   24  u32  StateChecksum        meaningful only when Flags.HasChecksum
    ///   28  u16  AddCount
    ///   30  u16  UpdateCount
    ///   32  u16  RemoveCount
    ///   34  u16  KeyframeRef          keyframe generation the delta extends
    ///                                 (OD-14 apply-gating reference)
    /// Sections, in this order: ADD | UPDATE | REMOVE
    ///
    /// ADD record, fixed 39 bytes (byte-identical to the v1 unit record):
    ///   u64 Entity | u8 Owner | i32 PosX | i32 PosZ | i32 Health |
    ///   u8 HasMoveTarget | i32 MoveTargetX | i32 MoveTargetZ |
    ///   u64 AttackTarget | u8 AutoAcquire
    ///
    /// UPDATE record, variable:
    ///   varint-zigzag EntityIdDelta   delta from the previous record of the
    ///                                 section; the first record is relative to 0
    ///   u8  DirtyMask                 UnitDirtyMask; bit 7 rejected in v1
    ///   then, in ascending bit order, one field group per set bit:
    ///     bit0 Owner:         u8
    ///     bit1 Position:      i32 PosX, i32 PosZ
    ///     bit2 Health:        i32
    ///     bit3 HasMoveTarget: u8 (0/1)
    ///     bit4 MoveTarget:    i32 MoveTargetX, i32 MoveTargetZ -- written only
    ///                       while the target is not being cleared (OD-14: bit3
    ///                       set with HasMoveTarget = 0 omits the coordinates)
    ///     bit5 AttackTarget:  varint-zigzag (AttackTarget - Entity)
    ///     bit6 AutoAcquire:   u8 (0/1)
    ///
    /// REMOVE record, variable:
    ///   varint-zigzag EntityIdDelta | u8 Cause (DeltaRemoveCause)
    /// </code>
    ///
    /// Invariants enforced in both directions: entity ids are valid (non-zero)
    /// and strictly ascending inside each section, ids stay inside the signed
    /// 63-bit varint space, boolean fields are 0/1, and a packet occupies
    /// exactly the bytes it declares (no trailing data).
    /// </summary>
    public static class DeltaSnapshotWireCodec
    {
        /// <summary>Flag bits defined by delta protocol version 1.</summary>
        private const byte DefinedFlagsMask =
            (byte)((byte)DeltaFlags.HasChecksum | (byte)DeltaFlags.HasTombstoneEcho);

        /// <summary>Flag bits that version 1 does not define (must be zero).</summary>
        private const byte ReservedFlagsBits = (byte)(0xFF & ~DefinedFlagsMask);

        /// <summary>Dirty-mask bit that version 1 does not define (must be zero).</summary>
        private const byte ExtensionDirtyBit = (byte)UnitDirtyMask.ReservedExtension;

        /// <summary>
        /// Number of bytes a varint-zigzag encoding of <paramref name="value"/> occupies.
        /// </summary>
        public static int GetVarintZigzagSize(long value)
        {
            var raw = ZigzagEncode(value);
            var size = 1;
            while (raw >= 0x80UL)
            {
                raw >>= 7;
                size++;
            }

            return size;
        }

        /// <summary>
        /// Writes <paramref name="value"/> as an LEB128 zigzag varint.
        /// </summary>
        /// <returns>False when the destination cannot hold the encoding; nothing is written then.</returns>
        public static bool TryWriteVarintZigzag(long value, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            var raw = ZigzagEncode(value);
            var offset = 0;
            while (true)
            {
                if (offset >= destination.Length)
                {
                    return false;
                }

                var chunk = (byte)(raw & 0x7FUL);
                raw >>= 7;
                if (raw == 0UL)
                {
                    destination[offset++] = chunk;
                    break;
                }

                destination[offset++] = (byte)(chunk | 0x80);
            }

            bytesWritten = offset;
            return true;
        }

        /// <summary>
        /// Reads an LEB128 zigzag varint at <paramref name="offset"/> and advances
        /// the offset past it. Overlong encodings (more than
        /// <see cref="DeltaSnapshotProtocol.VarintMaxSizeBytes"/> bytes or bits
        /// beyond the 64-bit range) are rejected.
        /// </summary>
        /// <returns>False when the source is truncated or the encoding is invalid; the offset is unchanged then.</returns>
        public static bool TryReadVarintZigzag(ReadOnlySpan<byte> source, ref int offset, out long value)
        {
            value = 0;
            if (offset < 0 || offset >= source.Length)
            {
                return false;
            }

            var cursor = offset;
            ulong raw = 0UL;
            var shift = 0;
            while (true)
            {
                if (cursor >= source.Length)
                {
                    return false;
                }

                var chunk = source[cursor++];
                if (shift == 63 && (chunk & 0x7E) != 0)
                {
                    return false;
                }

                raw |= (ulong)(chunk & 0x7F) << shift;
                if ((chunk & 0x80) == 0)
                {
                    break;
                }

                shift += 7;
                if (shift > 63)
                {
                    return false;
                }
            }

            value = ZigzagDecode(raw);
            offset = cursor;
            return true;
        }

        /// <summary>
        /// Worst-case encoded size for a buffer that must hold any packet with the
        /// given record counts, header included.
        /// </summary>
        /// <returns>-1 when a count is negative or cannot be expressed by the u16 header field.</returns>
        public static int GetMaxEncodedSize(int addCount, int updateCount, int removeCount)
        {
            if (!IsCountInRange(addCount) || !IsCountInRange(updateCount) || !IsCountInRange(removeCount))
            {
                return -1;
            }

            return DeltaSnapshotProtocol.HeaderSizeBytes +
                   (addCount * DeltaSnapshotProtocol.AddRecordSizeBytes) +
                   (updateCount * DeltaSnapshotProtocol.UpdateRecordMaxSizeBytes) +
                   (removeCount * DeltaSnapshotProtocol.RemoveRecordMaxSizeBytes);
        }

        /// <summary>
        /// Validates the record sections and returns the exact encoded packet size
        /// (header included). Use it to budget a packet against
        /// <see cref="DeltaSnapshotProtocol.MaxPayloadBytes"/> before splitting a
        /// tick into parts, or to size a pooled buffer without encoding twice.
        /// </summary>
        public static DeltaCodecResult GetEncodedSize(
            ReadOnlySpan<DeltaAddRecord> adds,
            ReadOnlySpan<DeltaUpdateRecord> updates,
            ReadOnlySpan<DeltaRemoveRecord> removes,
            out int size)
        {
            size = 0;
            var result = Measure(adds, updates, removes, out var payloadSize);
            if (result != DeltaCodecResult.Ok)
            {
                return result;
            }

            size = DeltaSnapshotProtocol.HeaderSizeBytes + payloadSize;
            return DeltaCodecResult.Ok;
        }

        /// <summary>
        /// Encodes a delta packet into <paramref name="destination"/>.
        ///
        /// Validation happens before the first write, so a failing call leaves the
        /// destination untouched: pooled buffers never observe partial packets.
        /// </summary>
        /// <param name="header">Header; its counts must match the section lengths.</param>
        /// <param name="adds">ADD records in canonical ascending entity-id order.</param>
        /// <param name="updates">UPDATE records in canonical ascending entity-id order.</param>
        /// <param name="removes">REMOVE records in canonical ascending entity-id order.</param>
        /// <param name="destination">Caller-owned buffer; <see cref="GetMaxEncodedSize"/> gives a safe capacity.</param>
        /// <param name="bytesWritten">Exact packet size on success, 0 otherwise.</param>
        public static DeltaCodecResult TryEncode(
            in DeltaSnapshotHeader header,
            ReadOnlySpan<DeltaAddRecord> adds,
            ReadOnlySpan<DeltaUpdateRecord> updates,
            ReadOnlySpan<DeltaRemoveRecord> removes,
            Span<byte> destination,
            out int bytesWritten)
        {
            bytesWritten = 0;

            if (!IsHeaderEncodable(in header, adds.Length, updates.Length, removes.Length))
            {
                return DeltaCodecResult.InvalidArgument;
            }

            var measure = Measure(adds, updates, removes, out var payloadSize);
            if (measure != DeltaCodecResult.Ok)
            {
                return measure;
            }

            var total = DeltaSnapshotProtocol.HeaderSizeBytes + payloadSize;
            if (destination.Length < total)
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            var writer = new SpanWriter(destination);
            WriteHeader(ref writer, in header);
            WriteAdds(ref writer, adds);
            WriteUpdates(ref writer, updates);
            WriteRemoves(ref writer, removes);

            // Defensive invariant: measurement and writing must agree exactly.
            if (writer.HasOverflowed || writer.Offset != total)
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            bytesWritten = total;
            return DeltaCodecResult.Ok;
        }

        /// <summary>
        /// Reads and structurally validates only the 36-byte header, so a
        /// replication consumer can inspect counts and flags before committing
        /// record buffers. Section bytes are never interpreted, but the received
        /// payload must be large enough to hold the declared record counts.
        /// </summary>
        public static DeltaCodecResult TryDecodeHeader(
            ReadOnlySpan<byte> source,
            out DeltaSnapshotHeader header)
        {
            return ReadHeader(source, out header);
        }

        /// <summary>
        /// Decodes a whole delta packet into caller-owned record spans.
        ///
        /// Structural validation only: ownership, apply-gating (stale, duplicate,
        /// reordered, keyframe reference) and gameplay authority stay with the
        /// replication consumer and the authoritative server.
        /// </summary>
        /// <param name="source">Exactly one packet; trailing bytes are rejected.</param>
        /// <param name="header">Decoded header; counts describe how many records were written.</param>
        /// <param name="adds">Destination for <see cref="DeltaSnapshotHeader.AddCount"/> records.</param>
        /// <param name="updates">Destination for <see cref="DeltaSnapshotHeader.UpdateCount"/> records.</param>
        /// <param name="removes">Destination for <see cref="DeltaSnapshotHeader.RemoveCount"/> records.</param>
        /// <remarks>
        /// On a non-<see cref="DeltaCodecResult.Ok"/> result the record spans may
        /// hold partially decoded data; the packet must be discarded.
        /// </remarks>
        public static DeltaCodecResult TryDecode(
            ReadOnlySpan<byte> source,
            out DeltaSnapshotHeader header,
            Span<DeltaAddRecord> adds,
            Span<DeltaUpdateRecord> updates,
            Span<DeltaRemoveRecord> removes)
        {
            var result = ReadHeader(source, out header);
            if (result != DeltaCodecResult.Ok)
            {
                return result;
            }

            if (adds.Length < header.AddCount ||
                updates.Length < header.UpdateCount ||
                removes.Length < header.RemoveCount)
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            var reader = new SpanReader(source, DeltaSnapshotProtocol.HeaderSizeBytes);

            result = ReadAdds(ref reader, header.AddCount, adds);
            if (result != DeltaCodecResult.Ok)
            {
                return result;
            }

            result = ReadUpdates(ref reader, header.UpdateCount, updates);
            if (result != DeltaCodecResult.Ok)
            {
                return result;
            }

            result = ReadRemoves(ref reader, header.RemoveCount, removes);
            if (result != DeltaCodecResult.Ok)
            {
                return result;
            }

            if (reader.Offset != source.Length)
            {
                return DeltaCodecResult.Malformed;
            }

            return DeltaCodecResult.Ok;
        }

        /// <summary>
        /// Encodes a run of ADD records without a packet header (39 bytes each,
        /// byte-identical to the v1 unit record). This is the record framing of
        /// the keyframe slice codec (step 2.6.4): a baseline streams as one
        /// slice header followed by fixed-size record runs, so no second
        /// decoder exists beside the delta one.
        /// </summary>
        /// <param name="adds">Records in canonical ascending entity-id order.</param>
        /// <param name="destination">Caller-owned buffer; needs exactly
        /// <c>adds.Length * <see cref="DeltaSnapshotProtocol.AddRecordSizeBytes"/></c> bytes.</param>
        /// <param name="bytesWritten">Exact run size on success, 0 otherwise.</param>
        public static DeltaCodecResult TryEncodeAddRecords(
            ReadOnlySpan<DeltaAddRecord> adds,
            Span<byte> destination,
            out int bytesWritten)
        {
            bytesWritten = 0;

            var total = adds.Length * DeltaSnapshotProtocol.AddRecordSizeBytes;
            if (destination.Length < total)
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            // ReadAdds enforces strictly ascending non-zero ids; reject the same
            // violations on the encode side so a failure can never pass silently.
            var previous = 0L;
            for (var index = 0; index < adds.Length; index++)
            {
                if (!TryGetEncodableId(adds[index].Entity, out var id) || id <= previous)
                {
                    return DeltaCodecResult.Malformed;
                }

                previous = id;
            }

            var writer = new SpanWriter(destination);
            WriteAdds(ref writer, adds);
            if (writer.HasOverflowed || writer.Offset != total)
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            bytesWritten = total;
            return DeltaCodecResult.Ok;
        }

        /// <summary>
        /// Decodes a run of ADD records written by
        /// <see cref="TryEncodeAddRecords"/>. The source must hold exactly
        /// <paramref name="recordCount"/> records: the run length is implied by
        /// the framing (slice header or reassembly), so shorter input is
        /// truncated and longer input carries bytes the framing cannot account
        /// for — both are rejected instead of being guessed at.
        /// </summary>
        public static DeltaCodecResult TryDecodeAddRecords(
            ReadOnlySpan<byte> source,
            int recordCount,
            Span<DeltaAddRecord> adds,
            out int recordsRead)
        {
            recordsRead = 0;

            if (recordCount < 0)
            {
                return DeltaCodecResult.InvalidArgument;
            }

            if (adds.Length < recordCount)
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            var total = recordCount * DeltaSnapshotProtocol.AddRecordSizeBytes;
            if (source.Length < total)
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            if (source.Length > total)
            {
                return DeltaCodecResult.Malformed;
            }

            var reader = new SpanReader(source, 0);
            var result = ReadAdds(ref reader, recordCount, adds);
            if (result != DeltaCodecResult.Ok)
            {
                return result;
            }

            recordsRead = recordCount;
            return DeltaCodecResult.Ok;
        }

        private static DeltaCodecResult ReadHeader(
            ReadOnlySpan<byte> source,
            out DeltaSnapshotHeader header)
        {
            header = default;

            if (source.Length < DeltaSnapshotProtocol.HeaderSizeBytes)
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            if (source.Length > DeltaSnapshotProtocol.MaxPacketBytes)
            {
                return DeltaCodecResult.PayloadTooLarge;
            }

            var reader = new SpanReader(source, 0);
            if (!reader.TryReadUInt8(out var messageType) ||
                !reader.TryReadUInt32(out var version) ||
                !reader.TryReadUInt64(out var tick) ||
                !reader.TryReadUInt64(out var baseTick) ||
                !reader.TryReadUInt8(out var partIndex) ||
                !reader.TryReadUInt8(out var partCount) ||
                !reader.TryReadUInt8(out var flags) ||
                !reader.TryReadUInt32(out var stateChecksum) ||
                !reader.TryReadUInt16(out var addCount) ||
                !reader.TryReadUInt16(out var updateCount) ||
                !reader.TryReadUInt16(out var removeCount) ||
                !reader.TryReadUInt16(out var keyframeRef))
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            header = new DeltaSnapshotHeader(
                messageType,
                version,
                tick,
                baseTick,
                partIndex,
                partCount,
                flags,
                stateChecksum,
                addCount,
                updateCount,
                removeCount,
                keyframeRef);

            return ValidateHeader(in header, source.Length - DeltaSnapshotProtocol.HeaderSizeBytes);
        }

        private static DeltaCodecResult ValidateHeader(in DeltaSnapshotHeader header, int payloadLength)
        {
            if (header.MessageType != DeltaSnapshotProtocol.MessageTypeDelta)
            {
                return DeltaCodecResult.Malformed;
            }

            if (header.DeltaProtocolVersion != DeltaSnapshotProtocol.Version)
            {
                return DeltaCodecResult.UnsupportedVersion;
            }

            if ((header.Flags & ReservedFlagsBits) != 0)
            {
                return DeltaCodecResult.Malformed;
            }

            if (header.PartCount == 0 || header.PartIndex >= header.PartCount)
            {
                return DeltaCodecResult.Malformed;
            }

            if (header.BaseTick > header.Tick)
            {
                return DeltaCodecResult.Malformed;
            }

            // The declared counts must at least fit into the received payload;
            // this bounds every decoding loop before it starts.
            var minimumPayload =
                (header.AddCount * DeltaSnapshotProtocol.AddRecordSizeBytes) +
                (header.UpdateCount * DeltaSnapshotProtocol.UpdateRecordMinSizeBytes) +
                (header.RemoveCount * DeltaSnapshotProtocol.RemoveRecordMinSizeBytes);
            if (payloadLength < minimumPayload)
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            return DeltaCodecResult.Ok;
        }

        private static bool IsHeaderEncodable(
            in DeltaSnapshotHeader header,
            int addLength,
            int updateLength,
            int removeLength)
        {
            return header.MessageType == DeltaSnapshotProtocol.MessageTypeDelta &&
                   header.DeltaProtocolVersion == DeltaSnapshotProtocol.Version &&
                   header.PartCount >= 1 &&
                   header.PartIndex < header.PartCount &&
                   (header.Flags & ReservedFlagsBits) == 0 &&
                   header.BaseTick <= header.Tick &&
                   header.AddCount == addLength &&
                   header.UpdateCount == updateLength &&
                   header.RemoveCount == removeLength;
        }

        /// <summary>
        /// Validates both directions' shared invariants and measures the exact
        /// section size. Runs before any byte is written.
        /// </summary>
        private static DeltaCodecResult Measure(
            ReadOnlySpan<DeltaAddRecord> adds,
            ReadOnlySpan<DeltaUpdateRecord> updates,
            ReadOnlySpan<DeltaRemoveRecord> removes,
            out int payloadSize)
        {
            payloadSize = 0;
            var previous = 0L;

            for (var index = 0; index < adds.Length; index++)
            {
                ref readonly var record = ref adds[index];
                if (!TryGetEncodableId(record.Entity, out var id) || id <= previous)
                {
                    return DeltaCodecResult.InvalidArgument;
                }

                previous = id;
                payloadSize += DeltaSnapshotProtocol.AddRecordSizeBytes;
            }

            previous = 0L;
            for (var index = 0; index < updates.Length; index++)
            {
                ref readonly var record = ref updates[index];
                if (!TryGetEncodableId(record.Entity, out var id) || id <= previous)
                {
                    return DeltaCodecResult.InvalidArgument;
                }

                var mask = record.DirtyMask;
                if (mask == 0 || (mask & (byte)UnitDirtyMask.ReservedExtension) != 0)
                {
                    return DeltaCodecResult.InvalidArgument;
                }

                var size = GetVarintZigzagSize(id - previous) + 1;
                previous = id;

                if ((mask & (byte)UnitDirtyMask.Owner) != 0)
                {
                    size += 1;
                }

                if ((mask & (byte)UnitDirtyMask.Position) != 0)
                {
                    size += 8;
                }

                if ((mask & (byte)UnitDirtyMask.Health) != 0)
                {
                    size += 4;
                }

                if ((mask & (byte)UnitDirtyMask.HasMoveTarget) != 0)
                {
                    size += 1;
                }

                if (WritesMoveTargetCoordinates(mask, record.HasMoveTarget))
                {
                    size += 8;
                }

                if ((mask & (byte)UnitDirtyMask.AttackTarget) != 0)
                {
                    if (record.AttackTarget.Value > long.MaxValue)
                    {
                        return DeltaCodecResult.InvalidArgument;
                    }

                    size += GetVarintZigzagSize((long)record.AttackTarget.Value - id);
                }

                if ((mask & (byte)UnitDirtyMask.AutoAcquire) != 0)
                {
                    size += 1;
                }

                payloadSize += size;
            }

            previous = 0L;
            for (var index = 0; index < removes.Length; index++)
            {
                ref readonly var record = ref removes[index];
                if (!TryGetEncodableId(record.Entity, out var id) || id <= previous)
                {
                    return DeltaCodecResult.InvalidArgument;
                }

                if (record.Cause > (byte)DeltaRemoveCause.FogOfWarHidden)
                {
                    return DeltaCodecResult.InvalidArgument;
                }

                payloadSize += GetVarintZigzagSize(id - previous) + 1;
                previous = id;
            }

            if (payloadSize > DeltaSnapshotProtocol.MaxPayloadBytes)
            {
                return DeltaCodecResult.PayloadTooLarge;
            }

            return DeltaCodecResult.Ok;
        }

        private static void WriteHeader(ref SpanWriter writer, in DeltaSnapshotHeader header)
        {
            writer.WriteUInt8(header.MessageType);
            writer.WriteUInt32(header.DeltaProtocolVersion);
            writer.WriteUInt64(header.Tick);
            writer.WriteUInt64(header.BaseTick);
            writer.WriteUInt8(header.PartIndex);
            writer.WriteUInt8(header.PartCount);
            writer.WriteUInt8(header.Flags);
            writer.WriteUInt32(header.StateChecksum);
            writer.WriteUInt16(header.AddCount);
            writer.WriteUInt16(header.UpdateCount);
            writer.WriteUInt16(header.RemoveCount);
            writer.WriteUInt16(header.KeyframeRef);
        }

        private static void WriteAdds(ref SpanWriter writer, ReadOnlySpan<DeltaAddRecord> adds)
        {
            for (var index = 0; index < adds.Length; index++)
            {
                ref readonly var record = ref adds[index];
                writer.WriteUInt64(record.Entity.Value);
                writer.WriteUInt8(record.Owner.Value);
                writer.WriteInt32(record.Position.X);
                writer.WriteInt32(record.Position.Z);
                writer.WriteInt32(record.CurrentHealth);
                writer.WriteUInt8(ToFlagByte(record.HasMoveTarget));
                writer.WriteInt32(record.MoveTarget.X);
                writer.WriteInt32(record.MoveTarget.Z);
                writer.WriteUInt64(record.AttackTarget.Value);
                writer.WriteUInt8(ToFlagByte(record.AutoAcquireEnemies));
            }
        }

        private static void WriteUpdates(ref SpanWriter writer, ReadOnlySpan<DeltaUpdateRecord> updates)
        {
            var previous = 0L;
            for (var index = 0; index < updates.Length; index++)
            {
                ref readonly var record = ref updates[index];
                var id = (long)record.Entity.Value;
                writer.WriteVarint(id - previous);
                previous = id;
                writer.WriteUInt8(record.DirtyMask);

                var mask = record.DirtyMask;
                if ((mask & (byte)UnitDirtyMask.Owner) != 0)
                {
                    writer.WriteUInt8(record.Owner.Value);
                }

                if ((mask & (byte)UnitDirtyMask.Position) != 0)
                {
                    writer.WriteInt32(record.Position.X);
                    writer.WriteInt32(record.Position.Z);
                }

                if ((mask & (byte)UnitDirtyMask.Health) != 0)
                {
                    writer.WriteInt32(record.CurrentHealth);
                }

                if ((mask & (byte)UnitDirtyMask.HasMoveTarget) != 0)
                {
                    writer.WriteUInt8(ToFlagByte(record.HasMoveTarget));
                }

                if (WritesMoveTargetCoordinates(mask, record.HasMoveTarget))
                {
                    writer.WriteInt32(record.MoveTarget.X);
                    writer.WriteInt32(record.MoveTarget.Z);
                }

                if ((mask & (byte)UnitDirtyMask.AttackTarget) != 0)
                {
                    writer.WriteVarint((long)record.AttackTarget.Value - id);
                }

                if ((mask & (byte)UnitDirtyMask.AutoAcquire) != 0)
                {
                    writer.WriteUInt8(ToFlagByte(record.AutoAcquireEnemies));
                }
            }
        }

        private static void WriteRemoves(ref SpanWriter writer, ReadOnlySpan<DeltaRemoveRecord> removes)
        {
            var previous = 0L;
            for (var index = 0; index < removes.Length; index++)
            {
                ref readonly var record = ref removes[index];
                var id = (long)record.Entity.Value;
                writer.WriteVarint(id - previous);
                previous = id;
                writer.WriteUInt8(record.Cause);
            }
        }

        private static DeltaCodecResult ReadAdds(
            ref SpanReader reader,
            int count,
            Span<DeltaAddRecord> adds)
        {
            var previous = 0L;
            for (var index = 0; index < count; index++)
            {
                if (!reader.TryReadUInt64(out var entityValue) ||
                    !TryGetEncodableId(new EntityId(entityValue), out var id) ||
                    id <= previous)
                {
                    return DeltaCodecResult.Malformed;
                }

                previous = id;

                if (!reader.TryReadUInt8(out var owner) ||
                    !reader.TryReadInt32(out var posX) ||
                    !reader.TryReadInt32(out var posZ) ||
                    !reader.TryReadInt32(out var currentHealth))
                {
                    return DeltaCodecResult.BufferTooSmall;
                }

                var result = ReadBool(ref reader, out var hasMoveTarget);
                if (result != DeltaCodecResult.Ok)
                {
                    return result;
                }

                if (!reader.TryReadInt32(out var moveTargetX) ||
                    !reader.TryReadInt32(out var moveTargetZ) ||
                    !reader.TryReadUInt64(out var attackTargetValue))
                {
                    return DeltaCodecResult.BufferTooSmall;
                }

                result = ReadBool(ref reader, out var autoAcquire);
                if (result != DeltaCodecResult.Ok)
                {
                    return result;
                }

                adds[index] = new DeltaAddRecord(
                    new EntityId(entityValue),
                    new PlayerId(owner),
                    new WorldPointMm(posX, posZ),
                    currentHealth,
                    hasMoveTarget,
                    new WorldPointMm(moveTargetX, moveTargetZ),
                    new EntityId(attackTargetValue),
                    autoAcquire);
            }

            return DeltaCodecResult.Ok;
        }

        private static DeltaCodecResult ReadUpdates(
            ref SpanReader reader,
            int count,
            Span<DeltaUpdateRecord> updates)
        {
            var previous = 0L;
            for (var index = 0; index < count; index++)
            {
                if (!reader.TryReadVarint(out var idDelta) || idDelta <= 0)
                {
                    return DeltaCodecResult.Malformed;
                }

                if (idDelta > long.MaxValue - previous)
                {
                    return DeltaCodecResult.Malformed;
                }

                var id = previous + idDelta;
                previous = id;

                if (!reader.TryReadUInt8(out var mask))
                {
                    return DeltaCodecResult.BufferTooSmall;
                }

                if (mask == 0 || (mask & ExtensionDirtyBit) != 0)
                {
                    return DeltaCodecResult.Malformed;
                }

                var owner = (byte)0;
                var posX = 0;
                var posZ = 0;
                var currentHealth = 0;
                var hasMoveTarget = false;
                var moveTargetX = 0;
                var moveTargetZ = 0;
                var attackTarget = 0UL;
                var autoAcquire = false;

                if ((mask & (byte)UnitDirtyMask.Owner) != 0 && !reader.TryReadUInt8(out owner))
                {
                    return DeltaCodecResult.BufferTooSmall;
                }

                if ((mask & (byte)UnitDirtyMask.Position) != 0 &&
                    (!reader.TryReadInt32(out posX) || !reader.TryReadInt32(out posZ)))
                {
                    return DeltaCodecResult.BufferTooSmall;
                }

                if ((mask & (byte)UnitDirtyMask.Health) != 0 && !reader.TryReadInt32(out currentHealth))
                {
                    return DeltaCodecResult.BufferTooSmall;
                }

                if ((mask & (byte)UnitDirtyMask.HasMoveTarget) != 0)
                {
                    var result = ReadBool(ref reader, out hasMoveTarget);
                    if (result != DeltaCodecResult.Ok)
                    {
                        return result;
                    }
                }

                // OD-14: clearing the move target omits the coordinates even when
                // the MoveTarget bit is set, so nothing is read in that case.
                if (WritesMoveTargetCoordinates(mask, hasMoveTarget) &&
                    (!reader.TryReadInt32(out moveTargetX) || !reader.TryReadInt32(out moveTargetZ)))
                {
                    return DeltaCodecResult.BufferTooSmall;
                }

                if ((mask & (byte)UnitDirtyMask.AttackTarget) != 0)
                {
                    if (!reader.TryReadVarint(out var targetDelta) || targetDelta < -id)
                    {
                        return DeltaCodecResult.Malformed;
                    }

                    attackTarget = (ulong)(id + targetDelta);
                }

                if ((mask & (byte)UnitDirtyMask.AutoAcquire) != 0)
                {
                    var result = ReadBool(ref reader, out autoAcquire);
                    if (result != DeltaCodecResult.Ok)
                    {
                        return result;
                    }
                }

                updates[index] = new DeltaUpdateRecord(
                    new EntityId((ulong)id),
                    mask,
                    new PlayerId(owner),
                    new WorldPointMm(posX, posZ),
                    currentHealth,
                    hasMoveTarget,
                    new WorldPointMm(moveTargetX, moveTargetZ),
                    new EntityId(attackTarget),
                    autoAcquire);
            }

            return DeltaCodecResult.Ok;
        }

        private static DeltaCodecResult ReadRemoves(
            ref SpanReader reader,
            int count,
            Span<DeltaRemoveRecord> removes)
        {
            var previous = 0L;
            for (var index = 0; index < count; index++)
            {
                if (!reader.TryReadVarint(out var idDelta) || idDelta <= 0)
                {
                    return DeltaCodecResult.Malformed;
                }

                if (idDelta > long.MaxValue - previous)
                {
                    return DeltaCodecResult.Malformed;
                }

                var id = previous + idDelta;
                previous = id;

                if (!reader.TryReadUInt8(out var cause))
                {
                    return DeltaCodecResult.BufferTooSmall;
                }

                if (cause > (byte)DeltaRemoveCause.FogOfWarHidden)
                {
                    return DeltaCodecResult.Malformed;
                }

                removes[index] = new DeltaRemoveRecord(new EntityId((ulong)id), cause);
            }

            return DeltaCodecResult.Ok;
        }

        /// <summary>
        /// OD-14 gate shared by the encoder, the decoder and the size measurement:
        /// move-target coordinates travel only while the target is not being cleared.
        /// </summary>
        private static bool WritesMoveTargetCoordinates(byte mask, bool hasMoveTarget)
        {
            if ((mask & (byte)UnitDirtyMask.MoveTarget) == 0)
            {
                return false;
            }

            var clearing = (mask & (byte)UnitDirtyMask.HasMoveTarget) != 0 && !hasMoveTarget;
            return !clearing;
        }

        private static bool TryGetEncodableId(EntityId entity, out long id)
        {
            id = 0;
            if (!entity.IsValid || entity.Value > long.MaxValue)
            {
                return false;
            }

            id = (long)entity.Value;
            return true;
        }

        private static bool IsCountInRange(int count) => count >= 0 && count <= ushort.MaxValue;

        private static byte ToFlagByte(bool value) => (byte)(value ? 1 : 0);

        private static DeltaCodecResult ReadBool(ref SpanReader reader, out bool value)
        {
            value = false;
            if (!reader.TryReadUInt8(out var raw))
            {
                return DeltaCodecResult.BufferTooSmall;
            }

            if (raw > 1)
            {
                return DeltaCodecResult.Malformed;
            }

            value = raw == 1;
            return DeltaCodecResult.Ok;
        }

        private static ulong ZigzagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));

        private static long ZigzagDecode(ulong raw) => (long)(raw >> 1) ^ -(long)(raw & 1UL);

        /// <summary>
        /// Bounds-checked little-endian writer over a caller-owned buffer. A
        /// <see langword="ref struct"/>, so it lives on the stack and never
        /// allocates; overflow is reported instead of throwing.
        /// </summary>
        private ref struct SpanWriter
        {
            private readonly Span<byte> _buffer;
            private int _offset;
            private bool _overflowed;

            public SpanWriter(Span<byte> buffer)
            {
                _buffer = buffer;
                _offset = 0;
                _overflowed = false;
            }

            public int Offset => _offset;

            public bool HasOverflowed => _overflowed;

            public void WriteUInt8(byte value)
            {
                if (_offset + 1 > _buffer.Length)
                {
                    _overflowed = true;
                    return;
                }

                _buffer[_offset++] = value;
            }

            public void WriteUInt16(ushort value)
            {
                if (_offset + 2 > _buffer.Length)
                {
                    _overflowed = true;
                    return;
                }

                _buffer[_offset++] = (byte)(value & 0xFF);
                _buffer[_offset++] = (byte)((value >> 8) & 0xFF);
            }

            public void WriteUInt32(uint value)
            {
                if (_offset + 4 > _buffer.Length)
                {
                    _overflowed = true;
                    return;
                }

                _buffer[_offset++] = (byte)(value & 0xFF);
                _buffer[_offset++] = (byte)((value >> 8) & 0xFF);
                _buffer[_offset++] = (byte)((value >> 16) & 0xFF);
                _buffer[_offset++] = (byte)((value >> 24) & 0xFF);
            }

            public void WriteInt32(int value) => WriteUInt32((uint)value);

            public void WriteUInt64(ulong value)
            {
                if (_offset + 8 > _buffer.Length)
                {
                    _overflowed = true;
                    return;
                }

                _buffer[_offset++] = (byte)(value & 0xFF);
                _buffer[_offset++] = (byte)((value >> 8) & 0xFF);
                _buffer[_offset++] = (byte)((value >> 16) & 0xFF);
                _buffer[_offset++] = (byte)((value >> 24) & 0xFF);
                _buffer[_offset++] = (byte)((value >> 32) & 0xFF);
                _buffer[_offset++] = (byte)((value >> 40) & 0xFF);
                _buffer[_offset++] = (byte)((value >> 48) & 0xFF);
                _buffer[_offset++] = (byte)((value >> 56) & 0xFF);
            }

            public void WriteVarint(long value)
            {
                if (!TryWriteVarintZigzag(value, _buffer.Slice(_offset), out var written))
                {
                    _overflowed = true;
                    return;
                }

                _offset += written;
            }
        }

        /// <summary>
        /// Bounds-checked little-endian reader over a received packet. A
        /// <see langword="ref struct"/>, so it lives on the stack and never
        /// allocates; truncation is reported instead of throwing.
        /// </summary>
        private ref struct SpanReader
        {
            private readonly ReadOnlySpan<byte> _source;
            private int _offset;

            public SpanReader(ReadOnlySpan<byte> source, int offset)
            {
                _source = source;
                _offset = offset;
            }

            public int Offset => _offset;

            public bool TryReadUInt8(out byte value)
            {
                value = 0;
                if (_offset + 1 > _source.Length)
                {
                    return false;
                }

                value = _source[_offset++];
                return true;
            }

            public bool TryReadUInt16(out ushort value)
            {
                value = 0;
                if (_offset + 2 > _source.Length)
                {
                    return false;
                }

                value = (ushort)(_source[_offset] | (_source[_offset + 1] << 8));
                _offset += 2;
                return true;
            }

            public bool TryReadUInt32(out uint value)
            {
                value = 0;
                if (_offset + 4 > _source.Length)
                {
                    return false;
                }

                value = (uint)(_source[_offset] |
                    (_source[_offset + 1] << 8) |
                    (_source[_offset + 2] << 16) |
                    (_source[_offset + 3] << 24));
                _offset += 4;
                return true;
            }

            public bool TryReadInt32(out int value)
            {
                var read = TryReadUInt32(out var raw);
                value = (int)raw;
                return read;
            }

            public bool TryReadUInt64(out ulong value)
            {
                value = 0;
                if (_offset + 8 > _source.Length)
                {
                    return false;
                }

                value = _source[_offset] |
                    ((ulong)_source[_offset + 1] << 8) |
                    ((ulong)_source[_offset + 2] << 16) |
                    ((ulong)_source[_offset + 3] << 24) |
                    ((ulong)_source[_offset + 4] << 32) |
                    ((ulong)_source[_offset + 5] << 40) |
                    ((ulong)_source[_offset + 6] << 48) |
                    ((ulong)_source[_offset + 7] << 56);
                _offset += 8;
                return true;
            }

            public bool TryReadVarint(out long value) =>
                DeltaSnapshotWireCodec.TryReadVarintZigzag(_source, ref _offset, out value);
        }
    }
}
