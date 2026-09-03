using System;

namespace GlobalFront.Core.Snapshot
{
    /// <summary>Kind of a client repair request carried on C0.</summary>
    public enum ReplicationRequestWireKind : byte
    {
        None = 0,

        /// <summary>Ask for a fresh keyframe; the current base is unusable.</summary>
        SnapshotRequest = 1,

        /// <summary>
        /// Ask for the cumulative delta of <c>(LastAppliedTick, now]</c>: the
        /// dependency is inside the retained history window.
        /// </summary>
        DeltaResume = 2
    }

    /// <summary>
    /// Wire form of one client repair request (Phase 2.6, step 2.6.4, ADR-010).
    /// Pure value; the client receiver's internal
    /// <c>ReplicationRequest</c> is mapped onto it by the transport bridge.
    ///
    /// Layout (little-endian), rides as the payload of a C0 message:
    /// <code>
    /// 0   u8  MessageType     = 0x06 (client→server, next to 0x05 SnapshotAck)
    /// 1   u8  Kind            ReplicationRequestWireKind
    /// 2   u8  Attempt         1-based attempt inside the episode (diagnostic)
    /// 3   u8  Reserved0       written as 0, must be 0 on decode
    /// 4   u64 LastAppliedTick   tick the client confirms; base of a DeltaResume
    /// 12  u64 BaseKeyframeTick  tick of the client's keyframe base (0 = no base)
    /// 20  u64 KeyframeTick      requested keyframe tick; 0 = fresh keyframe
    /// </code>
    /// R&amp;D §3.4 also sketches a per-slice retransmission mask; the
    /// implemented receiver FSM (step 2.6.3) only ever asks for fresh
    /// keyframes or cumulative deltas, so the wire carries exactly those.
    /// </summary>
    public readonly struct ReplicationRequestWire
    {
        public ReplicationRequestWire(
            ReplicationRequestWireKind kind,
            byte attempt,
            ulong lastAppliedTick,
            ulong baseKeyframeTick,
            ulong keyframeTick)
        {
            Kind = kind;
            Attempt = attempt;
            LastAppliedTick = lastAppliedTick;
            BaseKeyframeTick = baseKeyframeTick;
            KeyframeTick = keyframeTick;
        }

        public byte MessageType => ReplicationRequestCodec.MessageType;

        public ReplicationRequestWireKind Kind { get; }

        /// <summary>1-based attempt inside the repair episode (diagnostic).</summary>
        public byte Attempt { get; }

        public ulong LastAppliedTick { get; }

        public ulong BaseKeyframeTick { get; }

        public ulong KeyframeTick { get; }

        /// <summary>True when this asks for a fresh keyframe.</summary>
        public bool IsSnapshotRequest => Kind == ReplicationRequestWireKind.SnapshotRequest;
    }

    /// <summary>
    /// Outcome of a replication request codec operation. Failures are values,
    /// never exceptions: a malformed request must be droppable inside the
    /// network event loop without unwinding the host thread.
    /// </summary>
    public enum ReplicationRequestCodecResult : byte
    {
        /// <summary>Operation completed; all produced values are valid.</summary>
        Ok = 0,

        /// <summary>Not enough bytes in the destination or the source.</summary>
        BufferTooSmall = 1,

        /// <summary>Structurally corrupt data (reserved bits set).</summary>
        Malformed = 2,

        /// <summary>Unknown request kind.</summary>
        UnsupportedKind = 3
    }

    /// <summary>
    /// Binary wire codec of the client repair request (Phase 2.6, step 2.6.4).
    /// Engine-independent, deterministic, little-endian and allocation-free:
    /// both directions work on caller-owned spans.
    /// </summary>
    public static class ReplicationRequestCodec
    {
        /// <summary>C0 client→server message type of a repair request.</summary>
        public const byte MessageType = 0x06;

        /// <summary>Fixed wire size of the request record.</summary>
        public const int SizeBytes = 28;

        /// <summary>
        /// Encodes the request record. Validation happens before the first
        /// write, so a failing call leaves the destination untouched.
        /// </summary>
        public static ReplicationRequestCodecResult TryEncode(
            in ReplicationRequestWire request,
            Span<byte> destination,
            out int bytesWritten)
        {
            bytesWritten = 0;

            if (request.Kind == ReplicationRequestWireKind.None)
            {
                return ReplicationRequestCodecResult.UnsupportedKind;
            }

            if (destination.Length < SizeBytes)
            {
                return ReplicationRequestCodecResult.BufferTooSmall;
            }

            destination[0] = MessageType;
            destination[1] = (byte)request.Kind;
            destination[2] = request.Attempt;
            destination[3] = 0;
            WriteUInt64(destination, 4, request.LastAppliedTick);
            WriteUInt64(destination, 12, request.BaseKeyframeTick);
            WriteUInt64(destination, 20, request.KeyframeTick);

            bytesWritten = SizeBytes;
            return ReplicationRequestCodecResult.Ok;
        }

        /// <summary>
        /// Reads the fixed-size record. Trailing bytes are ignored because the
        /// request travels inside a C0 message envelope; an unknown kind or a
        /// non-zero reserved byte makes the record malformed.
        /// </summary>
        public static ReplicationRequestCodecResult TryDecode(
            ReadOnlySpan<byte> source,
            out ReplicationRequestWire request)
        {
            request = default;

            if (source.Length < SizeBytes)
            {
                return ReplicationRequestCodecResult.BufferTooSmall;
            }

            if (source[0] != MessageType)
            {
                return ReplicationRequestCodecResult.Malformed;
            }

            var kind = (ReplicationRequestWireKind)source[1];
            if (kind != ReplicationRequestWireKind.SnapshotRequest &&
                kind != ReplicationRequestWireKind.DeltaResume)
            {
                return ReplicationRequestCodecResult.UnsupportedKind;
            }

            if (source[3] != 0)
            {
                return ReplicationRequestCodecResult.Malformed;
            }

            request = new ReplicationRequestWire(
                kind,
                source[2],
                ReadUInt64(source, 4),
                ReadUInt64(source, 12),
                ReadUInt64(source, 20));
            return ReplicationRequestCodecResult.Ok;
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

        private static ulong ReadUInt64(ReadOnlySpan<byte> source, int offset) =>
            source[offset] |
            ((ulong)source[offset + 1] << 8) |
            ((ulong)source[offset + 2] << 16) |
            ((ulong)source[offset + 3] << 24) |
            ((ulong)source[offset + 4] << 32) |
            ((ulong)source[offset + 5] << 40) |
            ((ulong)source[offset + 6] << 48) |
            ((ulong)source[offset + 7] << 56);
    }
}
