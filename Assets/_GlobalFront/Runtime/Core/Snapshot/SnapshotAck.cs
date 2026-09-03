using System;

namespace GlobalFront.Core.Snapshot
{
    /// <summary>
    /// Health bits of a <see cref="SnapshotAck"/> (Phase 2.6, ADR-010 client
    /// feedback). Bits 3..7 are reserved in version 1 and MUST be zero: an
    /// unknown bit means the sender used a feature this codec cannot interpret,
    /// so such a record is rejected instead of being guessed at.
    /// </summary>
    [Flags]
    public enum AckHealthFlags : byte
    {
        None = 0,

        /// <summary>
        /// The client is missing at least one replication tick; see
        /// <see cref="SnapshotAck.MissingBase"/> and
        /// <see cref="SnapshotAck.MissingBitmap"/>.
        /// </summary>
        GapPresent = 1 << 0,

        /// <summary>
        /// The gap is longer than the 64-tick bitmap can express: every bitmap
        /// bit is set and the exact length is unknown. The server answers with a
        /// cumulative packet when its window covers it, otherwise with a keyframe.
        /// </summary>
        BitmapOverflow = 1 << 1,

        /// <summary>
        /// The client's base is unusable: it is older than the retained delta
        /// history window, it references another keyframe generation, or the
        /// client could not apply a record. Only a fresh keyframe helps.
        /// </summary>
        BaseStale = 1 << 2,

        /// <summary>Mask of the bits version 1 does not define.</summary>
        ReservedBits = 0xF8
    }

    /// <summary>
    /// Client state feedback sent on C0 (reliable ordered) after every applied
    /// replication tick, at the 10 Hz base cadence (Phase 2.6, ADR-010 /
    /// R&amp;D §4.2-§4.3).
    ///
    /// The server is the authority, but delivery on C2 is unreliable, so the
    /// client is the only source of truth for <c>BaseTick</c>: the confirmed
    /// <see cref="LastAppliedTick"/> becomes the base of the next Establishing
    /// Delta. <see cref="MissingBase"/> and <see cref="MissingBitmap"/> are
    /// advisory NACK/diagnostic signals - they never make the client wait for a
    /// lost intermediate tick when a newer applicable delta is available.
    ///
    /// Pure value type: it carries no Unity types and never enters
    /// deterministic simulation state.
    /// </summary>
    public readonly struct SnapshotAck : IEquatable<SnapshotAck>
    {
        public SnapshotAck(
            byte healthFlags,
            ulong lastAppliedTick,
            ulong baseKeyframeTick,
            ulong missingBase,
            ulong missingBitmap)
        {
            HealthFlags = healthFlags;
            LastAppliedTick = lastAppliedTick;
            BaseKeyframeTick = baseKeyframeTick;
            MissingBase = missingBase;
            MissingBitmap = missingBitmap;
        }

        /// <summary>
        /// Builds the progress acknowledgement of one applied replication tick.
        /// Progress acks carry no gap: the client is exactly where it says it is.
        /// </summary>
        public static SnapshotAck CreateProgress(ulong lastAppliedTick, ulong baseKeyframeTick)
        {
            return new SnapshotAck((byte)AckHealthFlags.None, lastAppliedTick, baseKeyframeTick, 0UL, 0UL);
        }

        /// <summary>
        /// Builds the immediate signal for a tick the client could not apply.
        /// </summary>
        /// <param name="missingBase">First tick of the continuous miss (0 = none).</param>
        /// <param name="missingBitmap">Bit <c>i</c> set = tick <c>missingBase + i</c> was not received.</param>
        /// <param name="bitmapOverflow">The gap is longer than 64 ticks.</param>
        /// <param name="baseStale">The base is older than the retained history window.</param>
        public static SnapshotAck CreateGap(
            ulong lastAppliedTick,
            ulong baseKeyframeTick,
            ulong missingBase,
            ulong missingBitmap,
            bool bitmapOverflow,
            bool baseStale)
        {
            var flags = AckHealthFlags.None;
            if (missingBase != 0UL)
            {
                flags |= AckHealthFlags.GapPresent;
            }

            if (bitmapOverflow)
            {
                flags |= AckHealthFlags.BitmapOverflow;
            }

            if (baseStale)
            {
                flags |= AckHealthFlags.BaseStale;
            }

            return new SnapshotAck(
                (byte)flags, lastAppliedTick, baseKeyframeTick, missingBase, missingBitmap);
        }

        /// <summary>C0 message type; <c>0x05</c> for client state feedback.</summary>
        public byte MessageType => SnapshotAckCodec.MessageType;

        /// <summary>Combination of <see cref="AckHealthFlags"/> values.</summary>
        public byte HealthFlags { get; }

        /// <summary>Last replication tick the client applied completely.</summary>
        public ulong LastAppliedTick { get; }

        /// <summary>
        /// Tick of the client's keyframe base. Zero means "no base" on the wire
        /// (R&amp;D §4.2); a base installed at tick 0 is therefore reported as
        /// "unbased" and the server answers with one more keyframe, which is
        /// idempotent for the client.
        /// </summary>
        public ulong BaseKeyframeTick { get; }

        /// <summary>First tick of the continuous miss; 0 when nothing is missing.</summary>
        public ulong MissingBase { get; }

        /// <summary>
        /// Bit <c>i</c> set = tick <c>MissingBase + i</c> was not received. All
        /// 64 bits set together with <see cref="BitmapOverflow"/> means "the gap
        /// is longer than the bitmap".
        /// </summary>
        public ulong MissingBitmap { get; }

        public bool GapPresent => (HealthFlags & (byte)AckHealthFlags.GapPresent) != 0;

        public bool BitmapOverflow => (HealthFlags & (byte)AckHealthFlags.BitmapOverflow) != 0;

        public bool BaseStale => (HealthFlags & (byte)AckHealthFlags.BaseStale) != 0;

        /// <summary>True when this ack reports missing ticks.</summary>
        public bool HasGap => GapPresent && MissingBase != 0UL;

        public bool Equals(SnapshotAck other) =>
            HealthFlags == other.HealthFlags &&
            LastAppliedTick == other.LastAppliedTick &&
            BaseKeyframeTick == other.BaseKeyframeTick &&
            MissingBase == other.MissingBase &&
            MissingBitmap == other.MissingBitmap;

        public override bool Equals(object obj) => obj is SnapshotAck other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(HealthFlags);
            hash.Add(LastAppliedTick);
            hash.Add(BaseKeyframeTick);
            hash.Add(MissingBase);
            hash.Add(MissingBitmap);
            return hash.ToHashCode();
        }

        public override string ToString() =>
            $"SnapshotAck(type=0x{MessageType:X2}, last={LastAppliedTick}, base={BaseKeyframeTick}, " +
            $"missingBase={MissingBase}, bitmap=0x{MissingBitmap:X16}, flags=0x{HealthFlags:X2})";

        public static bool operator ==(SnapshotAck left, SnapshotAck right) => left.Equals(right);

        public static bool operator !=(SnapshotAck left, SnapshotAck right) => !left.Equals(right);
    }

    /// <summary>Outcome of one <see cref="SnapshotAckCodec"/> call.</summary>
    public enum SnapshotAckCodecResult : byte
    {
        Ok = 0,
        BufferTooSmall = 1,
        Malformed = 2,
        InvalidArgument = 3
    }

    /// <summary>
    /// Fixed-size wire codec of <see cref="SnapshotAck"/> (Phase 2.6, ADR-010).
    ///
    /// Layout, little-endian and byte-aligned like the rest of the protocol:
    /// <code>
    ///   0   u8   MessageType      = 0x05 (SnapshotAck on C0)
    ///   1   u8   HealthFlags      = { GapPresent:1, BitmapOverflow:1, BaseStale:1, Reserved:5 }
    ///   2   u64  LastAppliedTick
    ///  10   u64  BaseKeyframeTick
    ///  18   u64  MissingBase
    ///  26   u64  MissingBitmap
    /// </code>
    ///
    /// Size note: those offsets total <see cref="SizeBytes"/> = 34. The prose
    /// figure "33 bytes" in R&amp;D §4.2 (and the derived "33 B x 10 Hz =
    /// 330 B/s") does not match its own offsets; the offsets are normative here,
    /// so the uplink budget is 34 B x 10 Hz = 340 B/s. Recorded as a
    /// documentation conflict for the next R&amp;D revision.
    ///
    /// The record is fixed-size and continuation blocks are not implemented:
    /// <see cref="AckHealthFlags.BitmapOverflow"/> is the honest signal that the
    /// gap does not fit, which is all the server needs to choose between a
    /// cumulative delta and a keyframe (R&amp;D §5.3).
    ///
    /// Zero-GC: both directions work on caller-owned spans and never allocate.
    /// </summary>
    public static class SnapshotAckCodec
    {
        /// <summary>
        /// C0 message type of the client feedback record. The C2 snapshot space
        /// uses 0x01 (full v1 legacy), 0x02 (keyframe slice), 0x03 (delta) and
        /// 0x04 (cumulative delta); 0x05 is the first client-to-server value.
        /// </summary>
        public const byte MessageType = 0x05;

        /// <summary>Exact encoded size of one acknowledgement.</summary>
        public const int SizeBytes = 34;

        /// <summary>Number of ticks the advisory missing-bitmap can express.</summary>
        public const int MissingBitmapBits = 64;

        private const int MessageTypeOffset = 0;
        private const int HealthFlagsOffset = 1;
        private const int LastAppliedTickOffset = 2;
        private const int BaseKeyframeTickOffset = 10;
        private const int MissingBaseOffset = 18;
        private const int MissingBitmapOffset = 26;

        /// <summary>
        /// Writes the fixed-size record. On any non-<see cref="SnapshotAckCodecResult.Ok"/>
        /// result the destination is left untouched, so pooled buffers never
        /// observe a partial acknowledgement.
        /// </summary>
        public static SnapshotAckCodecResult TryEncode(
            in SnapshotAck ack,
            Span<byte> destination,
            out int bytesWritten)
        {
            bytesWritten = 0;

            if ((ack.HealthFlags & (byte)AckHealthFlags.ReservedBits) != 0)
            {
                return SnapshotAckCodecResult.InvalidArgument;
            }

            if (destination.Length < SizeBytes)
            {
                return SnapshotAckCodecResult.BufferTooSmall;
            }

            destination[MessageTypeOffset] = MessageType;
            destination[HealthFlagsOffset] = ack.HealthFlags;
            WriteUInt64(destination, LastAppliedTickOffset, ack.LastAppliedTick);
            WriteUInt64(destination, BaseKeyframeTickOffset, ack.BaseKeyframeTick);
            WriteUInt64(destination, MissingBaseOffset, ack.MissingBase);
            WriteUInt64(destination, MissingBitmapOffset, ack.MissingBitmap);

            bytesWritten = SizeBytes;
            return SnapshotAckCodecResult.Ok;
        }

        /// <summary>
        /// Reads the fixed-size record. Trailing bytes are ignored because the
        /// acknowledgement travels inside a C0 message envelope; a reserved
        /// health bit or an unknown message type makes the record malformed.
        /// </summary>
        public static SnapshotAckCodecResult TryDecode(
            ReadOnlySpan<byte> source,
            out SnapshotAck ack)
        {
            ack = default;

            if (source.Length < SizeBytes)
            {
                return SnapshotAckCodecResult.BufferTooSmall;
            }

            if (source[MessageTypeOffset] != MessageType)
            {
                return SnapshotAckCodecResult.Malformed;
            }

            var healthFlags = source[HealthFlagsOffset];
            if ((healthFlags & (byte)AckHealthFlags.ReservedBits) != 0)
            {
                return SnapshotAckCodecResult.Malformed;
            }

            ack = new SnapshotAck(
                healthFlags,
                ReadUInt64(source, LastAppliedTickOffset),
                ReadUInt64(source, BaseKeyframeTickOffset),
                ReadUInt64(source, MissingBaseOffset),
                ReadUInt64(source, MissingBitmapOffset));

            return SnapshotAckCodecResult.Ok;
        }

        private static void WriteUInt64(Span<byte> destination, int offset, ulong value)
        {
            destination[offset] = (byte)(value & 0xFF);
            destination[offset + 1] = (byte)((value >> 8) & 0xFF);
            destination[offset + 2] = (byte)((value >> 16) & 0xFF);
            destination[offset + 3] = (byte)((value >> 24) & 0xFF);
            destination[offset + 4] = (byte)((value >> 32) & 0xFF);
            destination[offset + 5] = (byte)((value >> 40) & 0xFF);
            destination[offset + 6] = (byte)((value >> 48) & 0xFF);
            destination[offset + 7] = (byte)((value >> 56) & 0xFF);
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> source, int offset)
        {
            return (ulong)source[offset] |
                   ((ulong)source[offset + 1] << 8) |
                   ((ulong)source[offset + 2] << 16) |
                   ((ulong)source[offset + 3] << 24) |
                   ((ulong)source[offset + 4] << 32) |
                   ((ulong)source[offset + 5] << 40) |
                   ((ulong)source[offset + 6] << 48) |
                   ((ulong)source[offset + 7] << 56);
        }
    }
}
