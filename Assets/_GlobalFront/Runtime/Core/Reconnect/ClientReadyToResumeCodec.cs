using System;
using GlobalFront.Core.Model;

namespace GlobalFront.Core.Reconnect
{
    /// <summary>
    /// Binary wire codec for the standalone C0 ClientReadyToResume signal
    /// (opcode 14, OD-20): the client completed its local post-reconnect
    /// countdown and is ready for the server to lift the tactical pause.
    /// Fixed 25-byte frame, little-endian, strictly zero-GC:
    /// <code>
    /// 0      opcode 14
    /// 1      protocol version 1
    /// 2..17  SessionId (16-byte Guid)
    /// 18..24 reserved (zero)
    /// </code>
    /// SessionId is Guid-backed in this codebase, so the frame is eight bytes
    /// wider than a hypothetical 8-byte session handle.
    /// </summary>
    public static class ClientReadyToResumeCodec
    {
        public const byte Opcode = 14;
        public const byte ProtocolVersion = 1;
        public const int SessionIdOffset = 2;
        public const int SessionIdSizeBytes = 16;
        public const int ReservedOffset = 18;
        public const int ReservedSizeBytes = 7;
        public const int SizeBytes = 25;

        /// <summary>Encodes a ready signal; the caller owns the preallocated buffer.</summary>
        public static bool TryEncode(SessionId session, Span<byte> buffer, out int written)
        {
            if (buffer.Length < SizeBytes)
            {
                written = 0;
                return false;
            }

            buffer[0] = Opcode;
            buffer[1] = ProtocolVersion;

            if (!session.Value.TryWriteBytes(buffer.Slice(SessionIdOffset, SessionIdSizeBytes)))
            {
                written = 0;
                return false;
            }

            buffer.Slice(ReservedOffset, ReservedSizeBytes).Clear();
            written = SizeBytes;
            return true;
        }

        /// <summary>
        /// Decodes a ready signal. The frame is fixed-size: any other length,
        /// opcode or protocol version is rejected without allocating.
        /// </summary>
        public static bool TryDecode(ReadOnlySpan<byte> buffer, out SessionId session)
        {
            session = default;
            if (buffer.Length != SizeBytes ||
                buffer[0] != Opcode ||
                buffer[1] != ProtocolVersion)
            {
                return false;
            }

            session = new SessionId(new Guid(buffer.Slice(SessionIdOffset, SessionIdSizeBytes)));
            return true;
        }
    }
}
