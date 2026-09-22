using System;
using GlobalFront.Core.Model;

namespace GlobalFront.Core.Snapshot
{
    /// <summary>
    /// Outcome of a keyframe slice codec operation. Failures are values, never
    /// exceptions: a corrupt or truncated slice must be droppable inside the
    /// network event loop without unwinding the host thread.
    /// </summary>
    public enum KeyframeSliceCodecResult : byte
    {
        /// <summary>Operation completed; all produced values are valid.</summary>
        Ok = 0,

        /// <summary>Not enough bytes in the destination or the source.</summary>
        BufferTooSmall = 1,

        /// <summary>
        /// Structurally corrupt data: wrong message type or version, reserved
        /// bits set, impossible part addressing, or trailing bytes.
        /// </summary>
        Malformed = 2,

        /// <summary>Header carries an unsupported slice version.</summary>
        UnsupportedVersion = 3,

        /// <summary>Caller error detected before any byte was written.</summary>
        InvalidArgument = 4
    }

    /// <summary>
    /// One slice header of a sliced keyframe stream (Phase 2.6, step 2.6.4,
    /// ADR-010 / R&amp;D §3.4): a keyframe baseline is cut into fixed-size
    /// slices, each an independent C2 message sharing one
    /// <see cref="KeyframeSeq"/> generation and addressed by
    /// <see cref="PartIndex"/>/<see cref="PartCount"/>.
    ///
    /// Layout (little-endian):
    /// <code>
    /// 0   u8  MessageType     = 0x02 (reserved keyframe-slice space of
    ///                              DeltaSnapshotProtocol; see below)
    /// 1   u8  SliceVersion    = KeyframeSliceCodec.SliceVersion
    /// 2   u64 Tick            state tick of the baseline
    /// 10  u16 KeyframeSeq     generation the base belongs to
    /// 12  u16 PartIndex       0-based, &lt; PartCount
    /// 14  u16 PartCount       &gt;= 1
    /// 16  u32 TotalUnitCount  unit records across all parts
    /// 20  u16 SliceAddCount   unit records in THIS slice
    /// 22  u16 Reserved0       written as 0, must be 0 on decode
    /// </code>
    /// followed by <c>SliceAddCount</c> ADD records in the 40-byte delta layout
    /// (the v1 unit record plus the OD-29 archetype byte; see
    /// <see cref="DeltaSnapshotWireCodec.TryEncodeAddRecords"/>). Because the
    /// framed record grew, <see cref="SliceVersion"/> moved to 2 with
    /// <see cref="DeltaSnapshotProtocol.Version"/>: the size arithmetic rejects a
    /// v1 slice on its own (a declared count would need 39 bytes per record, not
    /// 40), but a framing version that describes the wrong record is a lie worth
    /// avoiding rather than a defence to rely on.
    ///
    /// The message-type byte reuses the 0x02 keyframe-slice slot the delta
    /// protocol reserved for the C2 space, so a payload is classified by its
    /// first byte: 0x02 slice, 0x03 delta, 0x01 legacy full snapshot.
    /// </summary>
    public readonly struct KeyframeSliceHeader
    {
        public const byte MessageTypeValue = 0x02;

        public KeyframeSliceHeader(
            ulong tick,
            ushort keyframeSeq,
            ushort partIndex,
            ushort partCount,
            uint totalUnitCount,
            ushort sliceAddCount)
        {
            Tick = tick;
            KeyframeSeq = keyframeSeq;
            PartIndex = partIndex;
            PartCount = partCount;
            TotalUnitCount = totalUnitCount;
            SliceAddCount = sliceAddCount;
        }

        public byte MessageType => MessageTypeValue;

        public byte SliceVersion => KeyframeSliceCodec.SliceVersion;

        /// <summary>State tick of the baseline; constant across all parts.</summary>
        public ulong Tick { get; }

        /// <summary>Keyframe generation; constant across all parts.</summary>
        public ushort KeyframeSeq { get; }

        public ushort PartIndex { get; }

        public ushort PartCount { get; }

        /// <summary>Unit records across all parts of the keyframe.</summary>
        public uint TotalUnitCount { get; }

        /// <summary>Unit records carried by this slice.</summary>
        public ushort SliceAddCount { get; }
    }

    /// <summary>
    /// Binary wire codec of one keyframe slice (Phase 2.6, step 2.6.4,
    /// ADR-010). Engine-independent, deterministic, little-endian, byte-aligned
    /// and allocation-free: both directions work on caller-owned spans, so the
    /// hot network path never touches the GC.
    ///
    /// Slicing itself is the sender's policy (R&amp;D §11: 8–16 KB slices):
    /// this codec frames and validates one slice, and the receiver assembles
    /// parts by <see cref="KeyframeSliceHeader.KeyframeSeq"/>.
    /// </summary>
    public static class KeyframeSliceCodec
    {
        /// <summary>
        /// Wire version of the slice framing; independent version space. Version 2
        /// frames the 40-byte ADD record that carries the OD-29 archetype byte.
        /// </summary>
        public const byte SliceVersion = 2;

        /// <summary>C2 message type of a keyframe slice (reserved space).</summary>
        public const byte MessageType = KeyframeSliceHeader.MessageTypeValue;

        /// <summary>Fixed size of the slice header in bytes.</summary>
        public const int HeaderSizeBytes = 24;

        /// <summary>
        /// Encoded size of one slice carrying <paramref name="recordCount"/>
        /// unit records. Returns -1 when the count is negative or cannot be
        /// expressed by the u16 <c>SliceAddCount</c> field.
        /// </summary>
        public static int GetSliceSize(int recordCount)
        {
            if (recordCount < 0 || recordCount > ushort.MaxValue)
            {
                return -1;
            }

            return HeaderSizeBytes + (recordCount * DeltaSnapshotProtocol.AddRecordSizeBytes);
        }

        /// <summary>
        /// Largest record count whose slice stays within
        /// <paramref name="budgetBytes"/> — the sizing helper behind the
        /// sender's slice budget (R&amp;D §11).
        /// </summary>
        public static int MaxRecordsForSliceBudget(int budgetBytes)
        {
            if (budgetBytes < HeaderSizeBytes)
            {
                return 0;
            }

            var room = (budgetBytes - HeaderSizeBytes) / DeltaSnapshotProtocol.AddRecordSizeBytes;
            return room > ushort.MaxValue ? ushort.MaxValue : room;
        }

        /// <summary>
        /// Writes only the slice header into <paramref name="destination"/>.
        /// The sender pairs this with a raw copy of ADD-record bytes it has
        /// staged once, so re-sending a slice never re-encodes records.
        /// Returns <see cref="HeaderSizeBytes"/> on success, -1 when the
        /// destination is too small or the header is not encodable.
        /// </summary>
        public static int TryEncodeHeader(Span<byte> destination, in KeyframeSliceHeader header)
        {
            if (destination.Length < HeaderSizeBytes ||
                header.PartCount < 1 ||
                header.PartIndex >= header.PartCount)
            {
                return -1;
            }

            return WriteHeader(destination, in header);
        }

        /// <summary>
        /// Encodes one slice (header plus record run) into
        /// <paramref name="destination"/>. Validation happens before the first
        /// write, so a failing call leaves the destination untouched.
        /// </summary>
        public static KeyframeSliceCodecResult TryEncodeSlice(
            in KeyframeSliceHeader header,
            ReadOnlySpan<DeltaAddRecord> adds,
            Span<byte> destination,
            out int bytesWritten)
        {
            bytesWritten = 0;

            if (header.PartCount < 1 ||
                header.PartIndex >= header.PartCount ||
                header.TotalUnitCount < (uint)header.SliceAddCount)
            {
                return KeyframeSliceCodecResult.InvalidArgument;
            }

            if (adds.Length != header.SliceAddCount)
            {
                return KeyframeSliceCodecResult.InvalidArgument;
            }

            var total = GetSliceSize(header.SliceAddCount);
            if (total < 0)
            {
                return KeyframeSliceCodecResult.InvalidArgument;
            }

            if (destination.Length < total)
            {
                return KeyframeSliceCodecResult.BufferTooSmall;
            }

            var recordResult = DeltaSnapshotWireCodec.TryEncodeAddRecords(
                adds, destination.Slice(HeaderSizeBytes), out var recordBytes);
            if (recordResult != DeltaCodecResult.Ok)
            {
                return RecordResultOf(recordResult);
            }

            var offset = WriteHeader(destination, in header);
            if (offset != HeaderSizeBytes)
            {
                return KeyframeSliceCodecResult.BufferTooSmall;
            }

            bytesWritten = HeaderSizeBytes + recordBytes;
            return KeyframeSliceCodecResult.Ok;
        }

        /// <summary>
        /// Reads and structurally validates only the slice header, so a
        /// receiver can route by <c>(KeyframeSeq, PartIndex)</c> before
        /// committing record buffers. The payload must be large enough to hold
        /// the declared record count, mirroring
        /// <see cref="DeltaSnapshotWireCodec.TryDecodeHeader"/>.
        /// </summary>
        public static KeyframeSliceCodecResult TryDecodeHeader(
            ReadOnlySpan<byte> source,
            out KeyframeSliceHeader header)
        {
            header = default;

            if (source.Length < HeaderSizeBytes)
            {
                return KeyframeSliceCodecResult.BufferTooSmall;
            }

            var offset = 0;
            if (source[offset++] != MessageType)
            {
                return KeyframeSliceCodecResult.Malformed;
            }

            var version = source[offset++];
            if (version != SliceVersion)
            {
                return KeyframeSliceCodecResult.UnsupportedVersion;
            }

            var tick = ReadUInt64(source, ref offset);
            var keyframeSeq = ReadUInt16(source, ref offset);
            var partIndex = ReadUInt16(source, ref offset);
            var partCount = ReadUInt16(source, ref offset);
            var totalUnitCount = ReadUInt32(source, ref offset);
            var sliceAddCount = ReadUInt16(source, ref offset);
            var reserved0 = ReadUInt16(source, ref offset);
            if (offset != HeaderSizeBytes)
            {
                return KeyframeSliceCodecResult.BufferTooSmall;
            }

            if (reserved0 != 0 ||
                partCount < 1 ||
                partIndex >= partCount)
            {
                return KeyframeSliceCodecResult.Malformed;
            }

            var required = HeaderSizeBytes + ((long)sliceAddCount * DeltaSnapshotProtocol.AddRecordSizeBytes);
            if (source.Length < required)
            {
                return KeyframeSliceCodecResult.BufferTooSmall;
            }

            header = new KeyframeSliceHeader(
                tick, keyframeSeq, partIndex, partCount, totalUnitCount, sliceAddCount);
            return KeyframeSliceCodecResult.Ok;
        }

        /// <summary>
        /// Decodes a whole slice into caller-owned record storage. The source
        /// must hold exactly one slice: a truncated record run is rejected as
        /// truncated, trailing bytes as malformed.
        /// </summary>
        /// <remarks>
        /// On a non-<see cref="KeyframeSliceCodecResult.Ok"/> result the record
        /// span may hold partially decoded data; the slice must be discarded.
        /// </remarks>
        public static KeyframeSliceCodecResult TryDecodeSlice(
            ReadOnlySpan<byte> source,
            Span<DeltaAddRecord> adds,
            out KeyframeSliceHeader header,
            out int recordsRead)
        {
            header = default;
            recordsRead = 0;

            var headerResult = TryDecodeHeader(source, out var decoded);
            if (headerResult != KeyframeSliceCodecResult.Ok)
            {
                return headerResult;
            }

            if (adds.Length < decoded.SliceAddCount)
            {
                return KeyframeSliceCodecResult.BufferTooSmall;
            }

            var runLength = decoded.SliceAddCount * DeltaSnapshotProtocol.AddRecordSizeBytes;
            if (source.Length != HeaderSizeBytes + runLength)
            {
                return KeyframeSliceCodecResult.Malformed;
            }

            var recordResult = DeltaSnapshotWireCodec.TryDecodeAddRecords(
                source.Slice(HeaderSizeBytes, runLength),
                decoded.SliceAddCount,
                adds,
                out var read);
            if (recordResult != DeltaCodecResult.Ok)
            {
                return RecordResultOf(recordResult);
            }

            header = decoded;
            recordsRead = read;
            return KeyframeSliceCodecResult.Ok;
        }

        private static KeyframeSliceCodecResult RecordResultOf(DeltaCodecResult result) =>
            result == DeltaCodecResult.BufferTooSmall
                ? KeyframeSliceCodecResult.BufferTooSmall
                : KeyframeSliceCodecResult.Malformed;

        private static int WriteHeader(Span<byte> destination, in KeyframeSliceHeader header)
        {
            var offset = 0;
            destination[offset++] = MessageType;
            destination[offset++] = SliceVersion;
            WriteUInt64(destination, ref offset, header.Tick);
            WriteUInt16(destination, ref offset, header.KeyframeSeq);
            WriteUInt16(destination, ref offset, header.PartIndex);
            WriteUInt16(destination, ref offset, header.PartCount);
            WriteUInt32(destination, ref offset, header.TotalUnitCount);
            WriteUInt16(destination, ref offset, header.SliceAddCount);
            WriteUInt16(destination, ref offset, 0);
            return offset;
        }

        private static void WriteUInt16(Span<byte> destination, ref int offset, ushort value)
        {
            destination[offset++] = (byte)(value & 0xFF);
            destination[offset++] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteUInt32(Span<byte> destination, ref int offset, uint value)
        {
            destination[offset++] = (byte)(value & 0xFF);
            destination[offset++] = (byte)((value >> 8) & 0xFF);
            destination[offset++] = (byte)((value >> 16) & 0xFF);
            destination[offset++] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteUInt64(Span<byte> destination, ref int offset, ulong value)
        {
            destination[offset++] = (byte)(value & 0xFF);
            destination[offset++] = (byte)((value >> 8) & 0xFF);
            destination[offset++] = (byte)((value >> 16) & 0xFF);
            destination[offset++] = (byte)((value >> 24) & 0xFF);
            destination[offset++] = (byte)((value >> 32) & 0xFF);
            destination[offset++] = (byte)((value >> 40) & 0xFF);
            destination[offset++] = (byte)((value >> 48) & 0xFF);
            destination[offset++] = (byte)((value >> 56) & 0xFF);
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> source, ref int offset)
        {
            var value = (ushort)(source[offset] | (source[offset + 1] << 8));
            offset += 2;
            return value;
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
        {
            var value = (uint)(source[offset] |
                (source[offset + 1] << 8) |
                (source[offset + 2] << 16) |
                (source[offset + 3] << 24));
            offset += 4;
            return value;
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> source, ref int offset)
        {
            var value = source[offset] |
                ((ulong)source[offset + 1] << 8) |
                ((ulong)source[offset + 2] << 16) |
                ((ulong)source[offset + 3] << 24) |
                ((ulong)source[offset + 4] << 32) |
                ((ulong)source[offset + 5] << 40) |
                ((ulong)source[offset + 6] << 48) |
                ((ulong)source[offset + 7] << 56);
            offset += 8;
            return value;
        }
    }
}
