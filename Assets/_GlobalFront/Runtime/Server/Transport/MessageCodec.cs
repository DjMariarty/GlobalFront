using System;
using GlobalFront.Core.Model;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Decoded project message header (carrier-agnostic level).
    /// </summary>
    public struct TransportMessageHeader
    {
        public TransportMessageType Type;
        public ulong SessionToken;

        /// <summary>C2 only: opaque-payload ordering key (latest-wins).</summary>
        public ulong SnapshotTick;

        public int PayloadOffset;
        public int PayloadLength;
    }

    public enum MessageError : byte
    {
        None = 0,
        TooShort = 1,
        BadMagic = 2,
        BadMessageVersion = 3,
        UnknownMessageType = 4,
        BadPayloadLength = 5
    }

    /// <summary>
    /// Connect accept payload: server-issued attribution token plus the
    /// identity Phase 2.4 assigned to this attachment.
    /// </summary>
    public struct ConnectAcceptPayload
    {
        public ulong SessionToken;
        public SessionId Session;
        public MatchId Match;
        public PlayerId Player;
    }

    /// <summary>
    /// Authoritative command result (ADR-009, P2-3). Carries BOTH Phase 2.4
    /// rejection enums so the reason of an authoritative rejection is never
    /// lost: session-gate verdict in <see cref="Session"/>, unchanged
    /// <c>MatchServer</c> verdict in <see cref="Command"/>.
    /// </summary>
    public struct CommandAckPayload
    {
        public PlayerId Player;
        public uint CommandSequence;
        public SessionRejection Session;
        public MatchCommandRejection Command;

        public bool IsAccepted =>
            Session == SessionRejection.None && Command == MatchCommandRejection.None;
    }

    /// <summary>
    /// Project-owned message model codec (ADR-009, rev.3). This level is
    /// identical for every carrier; <see cref="TransportProtocol.MessageVersion"/>
    /// versions it, strictly separated from
    /// <see cref="TransportProtocol.CarrierVersion"/> (own carrier framing)
    /// and from <c>SnapshotProtocol.Version</c> (snapshot payload).
    ///
    /// Message layout (little-endian):
    /// <code>
    /// 0   2  Magic
    /// 2   2  MessageVersion
    /// 4   1  MessageType
    /// 5   8  SessionToken
    /// [C2 Snapshot only: +8 SnapshotTick]
    /// payload
    /// </code>
    /// </summary>
    public static class MessageCodec
    {
        public static int GetHeaderSize(TransportMessageType type) =>
            TransportProtocol.MessageHeaderSize +
            (type == TransportMessageType.Snapshot ? TransportProtocol.SnapshotTickSize : 0);

        public static int WriteHeader(
            byte[] buffer,
            int offset,
            TransportMessageType type,
            ulong sessionToken,
            ulong snapshotTick)
        {
            buffer[offset++] = (byte)(TransportProtocol.Magic & 0xFF);
            buffer[offset++] = (byte)((TransportProtocol.Magic >> 8) & 0xFF);
            buffer[offset++] = (byte)(TransportProtocol.MessageVersion & 0xFF);
            buffer[offset++] = (byte)((TransportProtocol.MessageVersion >> 8) & 0xFF);
            buffer[offset++] = (byte)type;
            buffer[offset++] = (byte)(sessionToken & 0xFF);
            buffer[offset++] = (byte)((sessionToken >> 8) & 0xFF);
            buffer[offset++] = (byte)((sessionToken >> 16) & 0xFF);
            buffer[offset++] = (byte)((sessionToken >> 24) & 0xFF);
            buffer[offset++] = (byte)((sessionToken >> 32) & 0xFF);
            buffer[offset++] = (byte)((sessionToken >> 40) & 0xFF);
            buffer[offset++] = (byte)((sessionToken >> 48) & 0xFF);
            buffer[offset++] = (byte)((sessionToken >> 56) & 0xFF);
            if (type == TransportMessageType.Snapshot)
            {
                buffer[offset++] = (byte)(snapshotTick & 0xFF);
                buffer[offset++] = (byte)((snapshotTick >> 8) & 0xFF);
                buffer[offset++] = (byte)((snapshotTick >> 16) & 0xFF);
                buffer[offset++] = (byte)((snapshotTick >> 24) & 0xFF);
                buffer[offset++] = (byte)((snapshotTick >> 32) & 0xFF);
                buffer[offset++] = (byte)((snapshotTick >> 40) & 0xFF);
                buffer[offset++] = (byte)((snapshotTick >> 48) & 0xFF);
                buffer[offset++] = (byte)((snapshotTick >> 56) & 0xFF);
            }

            return offset;
        }

        public static MessageError TryDecodeHeader(
            byte[] data,
            int offset,
            int length,
            out TransportMessageHeader header)
        {
            header = default;
            var remaining = length - offset;
            if (remaining < TransportProtocol.MessageHeaderSize)
            {
                return MessageError.TooShort;
            }

            var magic = (ushort)(data[offset] | (data[offset + 1] << 8));
            if (magic != TransportProtocol.Magic)
            {
                return MessageError.BadMagic;
            }

            var version = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
            if (version != TransportProtocol.MessageVersion)
            {
                return MessageError.BadMessageVersion;
            }

            var type = (TransportMessageType)data[offset + 4];
            if (type == TransportMessageType.None || type > TransportMessageType.ReplicationRequest)
            {
                return MessageError.UnknownMessageType;
            }

            var headerSize = GetHeaderSize(type);
            if (remaining < headerSize)
            {
                return MessageError.TooShort;
            }

            header.Type = type;
            header.SessionToken =
                (ulong)data[offset + 5] |
                ((ulong)data[offset + 6] << 8) |
                ((ulong)data[offset + 7] << 16) |
                ((ulong)data[offset + 8] << 24) |
                ((ulong)data[offset + 9] << 32) |
                ((ulong)data[offset + 10] << 40) |
                ((ulong)data[offset + 11] << 48) |
                ((ulong)data[offset + 12] << 56);

            if (type == TransportMessageType.Snapshot)
            {
                header.SnapshotTick =
                    (ulong)data[offset + 13] |
                    ((ulong)data[offset + 14] << 8) |
                    ((ulong)data[offset + 15] << 16) |
                    ((ulong)data[offset + 16] << 24) |
                    ((ulong)data[offset + 17] << 32) |
                    ((ulong)data[offset + 18] << 40) |
                    ((ulong)data[offset + 19] << 48) |
                    ((ulong)data[offset + 20] << 56);
            }

            header.PayloadOffset = offset + headerSize;
            header.PayloadLength = length - headerSize;
            return MessageError.None;
        }

        // ----- Control payloads -----

        public const int ConnectRequestSize = 4 + 1 + 16;

        public static int EncodeConnectRequest(
            byte[] buffer,
            int offset,
            uint snapshotProtocolVersion,
            bool hasResumeSession,
            Guid resumeSession)
        {
            buffer[offset++] = (byte)(snapshotProtocolVersion & 0xFF);
            buffer[offset++] = (byte)((snapshotProtocolVersion >> 8) & 0xFF);
            buffer[offset++] = (byte)((snapshotProtocolVersion >> 16) & 0xFF);
            buffer[offset++] = (byte)((snapshotProtocolVersion >> 24) & 0xFF);
            buffer[offset++] = (byte)(hasResumeSession ? 1 : 0);
            if (hasResumeSession)
            {
                Array.Copy(resumeSession.ToByteArray(), 0, buffer, offset, 16);
                offset += 16;
            }
            else
            {
                for (var index = 0; index < 16; index++)
                {
                    buffer[offset++] = 0;
                }
            }

            return offset;
        }

        public static bool TryDecodeConnectRequest(
            byte[] data,
            int offset,
            int length,
            out uint snapshotProtocolVersion,
            out Guid resumeSession)
        {
            snapshotProtocolVersion = 0;
            resumeSession = Guid.Empty;
            if (length - offset < ConnectRequestSize)
            {
                return false;
            }

            snapshotProtocolVersion = (uint)(data[offset] |
                (data[offset + 1] << 8) |
                (data[offset + 2] << 16) |
                (data[offset + 3] << 24));
            var hasResume = data[offset + 4] == 1;
            if (hasResume)
            {
                var bytes = new byte[16];
                Array.Copy(data, offset + 5, bytes, 0, 16);
                resumeSession = new Guid(bytes);
            }

            return true;
        }

        public const int ConnectAcceptSize = 8 + 16 + 16 + 1;

        public static int EncodeConnectAccept(
            byte[] buffer,
            int offset,
            ConnectAcceptPayload payload)
        {
            var token = payload.SessionToken;
            buffer[offset++] = (byte)(token & 0xFF);
            buffer[offset++] = (byte)((token >> 8) & 0xFF);
            buffer[offset++] = (byte)((token >> 16) & 0xFF);
            buffer[offset++] = (byte)((token >> 24) & 0xFF);
            buffer[offset++] = (byte)((token >> 32) & 0xFF);
            buffer[offset++] = (byte)((token >> 40) & 0xFF);
            buffer[offset++] = (byte)((token >> 48) & 0xFF);
            buffer[offset++] = (byte)((token >> 56) & 0xFF);
            Array.Copy(payload.Session.Value.ToByteArray(), 0, buffer, offset, 16);
            offset += 16;
            Array.Copy(payload.Match.Value.ToByteArray(), 0, buffer, offset, 16);
            offset += 16;
            buffer[offset++] = payload.Player.Value;
            return offset;
        }

        public static bool TryDecodeConnectAccept(
            byte[] data,
            int offset,
            int length,
            out ConnectAcceptPayload payload)
        {
            payload = default;
            if (length - offset < ConnectAcceptSize)
            {
                return false;
            }

            payload.SessionToken =
                (ulong)data[offset] |
                ((ulong)data[offset + 1] << 8) |
                ((ulong)data[offset + 2] << 16) |
                ((ulong)data[offset + 3] << 24) |
                ((ulong)data[offset + 4] << 32) |
                ((ulong)data[offset + 5] << 40) |
                ((ulong)data[offset + 6] << 48) |
                ((ulong)data[offset + 7] << 56);
            var sessionBytes = new byte[16];
            Array.Copy(data, offset + 8, sessionBytes, 0, 16);
            payload.Session = new SessionId(new Guid(sessionBytes));
            var matchBytes = new byte[16];
            Array.Copy(data, offset + 24, matchBytes, 0, 16);
            payload.Match = new MatchId(new Guid(matchBytes));
            payload.Player = new PlayerId(data[offset + 40]);
            return true;
        }

        public static int EncodeConnectDenied(byte[] buffer, int offset, ConnectDenyReason reason)
        {
            buffer[offset] = (byte)reason;
            return offset + 1;
        }

        public static int EncodeDisconnect(byte[] buffer, int offset, TransportDisconnectReason reason)
        {
            buffer[offset] = (byte)reason;
            return offset + 1;
        }

        public static int EncodePingPong(byte[] buffer, int offset, long timestampMs)
        {
            var value = (ulong)timestampMs;
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

        public static bool TryDecodePingPong(byte[] data, int offset, int length, out long timestampMs)
        {
            timestampMs = 0;
            if (length - offset < 8)
            {
                return false;
            }

            timestampMs = (long)((ulong)data[offset] |
                ((ulong)data[offset + 1] << 8) |
                ((ulong)data[offset + 2] << 16) |
                ((ulong)data[offset + 3] << 24) |
                ((ulong)data[offset + 4] << 32) |
                ((ulong)data[offset + 5] << 40) |
                ((ulong)data[offset + 6] << 48) |
                ((ulong)data[offset + 7] << 56));
            return true;
        }

        public const int CommandAckSize = 1 + 4 + 1 + 1;

        public static int EncodeCommandAck(byte[] buffer, int offset, CommandAckPayload payload)
        {
            buffer[offset++] = payload.Player.Value;
            var sequence = payload.CommandSequence;
            buffer[offset++] = (byte)(sequence & 0xFF);
            buffer[offset++] = (byte)((sequence >> 8) & 0xFF);
            buffer[offset++] = (byte)((sequence >> 16) & 0xFF);
            buffer[offset++] = (byte)((sequence >> 24) & 0xFF);
            buffer[offset++] = (byte)payload.Session;
            buffer[offset++] = (byte)payload.Command;
            return offset;
        }

        public static bool TryDecodeCommandAck(
            byte[] data,
            int offset,
            int length,
            out CommandAckPayload payload)
        {
            payload = default;
            if (length - offset < CommandAckSize)
            {
                return false;
            }

            payload.Player = new PlayerId(data[offset]);
            payload.CommandSequence = (uint)(data[offset + 1] |
                (data[offset + 2] << 8) |
                (data[offset + 3] << 16) |
                (data[offset + 4] << 24));
            payload.Session = (SessionRejection)data[offset + 5];
            payload.Command = (MatchCommandRejection)data[offset + 6];
            return true;
        }
    }
}
