using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using GlobalFront.Core.Model;

namespace GlobalFront.Core.Reconnect
{
    /// <summary>
    /// Binary wire codec for Reconnect and Resync protocol messages (ADR-011, step 2.7.1).
    /// Pure, deterministic, little-endian, byte-aligned and strictly zero-GC on all Span hot paths.
    /// </summary>
    public static class ReconnectWireCodec
    {
        public const byte RequestOpcode = ReconnectRequest.ExpectedOpcode; // 12
        public const ushort RequestProtocolVersion = ReconnectRequest.ExpectedProtocolVersion; // 1
        public const int RequestSizeBytes = ReconnectRequest.SizeBytes; // 64

        public const byte ResponseOpcode = ReconnectResponse.ExpectedOpcode; // 13
        public const int ResponseSizeBytes = ReconnectResponse.SizeBytes; // 92

        public const byte SecretOpcode = SessionSecretMessage.ExpectedOpcode; // 9
        public const int SecretMessageSizeBytes = SessionSecretMessage.SizeBytes; // 33
        public const int SecretSizeBytes = SessionSecretMessage.SecretSizeBytes; // 32

        /// <summary>
        /// Encodes a ReconnectRequest into destination buffer (64 bytes).
        /// </summary>
        public static bool TryEncodeRequest(in ReconnectRequest request, Span<byte> buffer, out int written)
        {
            if (buffer.Length < RequestSizeBytes)
            {
                written = 0;
                return false;
            }

            buffer[0] = request.Opcode;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(1, 2), request.ProtocolVersion);

            if (!request.SessionId.Value.TryWriteBytes(buffer.Slice(3, 16)))
            {
                written = 0;
                return false;
            }

            request.CopySecretTo(buffer.Slice(19, 32));
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(51, 8), request.LastAppliedTick);

            // Reserved 5 bytes (bytes 59..63) zeroed
            buffer.Slice(59, 5).Clear();

            written = RequestSizeBytes;
            return true;
        }

        /// <summary>
        /// Decodes a ReconnectRequest from source buffer (minimum 64 bytes).
        /// Performs strict zero GC allocations.
        /// </summary>
        public static bool TryDecodeRequest(ReadOnlySpan<byte> buffer, out ReconnectRequest request)
        {
            if (buffer.Length < RequestSizeBytes)
            {
                request = default;
                return false;
            }

            byte opcode = buffer[0];
            if (opcode != RequestOpcode)
            {
                request = default;
                return false;
            }

            ushort protocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1, 2));
            if (protocolVersion != RequestProtocolVersion)
            {
                request = default;
                return false;
            }

            var sessionId = new SessionId(new Guid(buffer.Slice(3, 16)));
            var secret = new SessionSecret32(buffer.Slice(19, 32));
            ulong lastAppliedTick = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(51, 8));

            request = new ReconnectRequest(opcode, protocolVersion, sessionId, secret, lastAppliedTick);
            return true;
        }

        /// <summary>
        /// Encodes a ReconnectResponse into destination buffer (92 bytes).
        /// </summary>
        public static bool TryEncodeResponse(in ReconnectResponse response, Span<byte> buffer, out int written)
        {
            if (buffer.Length < ResponseSizeBytes)
            {
                written = 0;
                return false;
            }

            buffer[0] = response.Opcode;
            buffer[1] = (byte)response.Result;
            buffer[2] = response.AssignedPlayer.Value;
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(3, 8), response.Match.AsUInt64());
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(11, 8), response.ServerTick);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(19, 2), response.ActiveKeyframeSeq);

            // Reserved 71 bytes (bytes 21..91) zeroed
            buffer.Slice(21, 71).Clear();

            written = ResponseSizeBytes;
            return true;
        }

        /// <summary>
        /// Decodes a ReconnectResponse from source buffer (minimum 92 bytes).
        /// Performs strict zero GC allocations.
        /// </summary>
        public static bool TryDecodeResponse(ReadOnlySpan<byte> buffer, out ReconnectResponse response)
        {
            if (buffer.Length < ResponseSizeBytes)
            {
                response = default;
                return false;
            }

            byte opcode = buffer[0];
            if (opcode != ResponseOpcode)
            {
                response = default;
                return false;
            }

            byte rawResult = buffer[1];
            if (rawResult > (byte)ReconnectResult.MatchFinished)
            {
                response = default;
                return false;
            }

            var result = (ReconnectResult)rawResult;
            var assignedPlayer = new PlayerId(buffer[2]);
            ulong matchValue = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(3, 8));
            ulong serverTick = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(11, 8));
            ushort activeKeyframeSeq = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(19, 2));

            response = new ReconnectResponse(opcode, result, assignedPlayer, new MatchId(matchValue), serverTick, activeKeyframeSeq);
            return true;
        }

        /// <summary>
        /// Encodes a 32-byte secret into a 33-byte SessionSecretMessage buffer (opcode 9).
        /// </summary>
        public static bool TryEncodeSecret(ReadOnlySpan<byte> secret, Span<byte> buffer, out int written)
        {
            if (secret.Length < SecretSizeBytes || buffer.Length < SecretMessageSizeBytes)
            {
                written = 0;
                return false;
            }

            buffer[0] = SecretOpcode;
            secret.Slice(0, SecretSizeBytes).CopyTo(buffer.Slice(1, SecretSizeBytes));
            written = SecretMessageSizeBytes;
            return true;
        }

        /// <summary>
        /// Decodes a 32-byte secret from a 33-byte SessionSecretMessage buffer into a new byte array.
        /// </summary>
        public static bool TryDecodeSecret(ReadOnlySpan<byte> buffer, out byte[] secret)
        {
            if (buffer.Length < SecretMessageSizeBytes || buffer[0] != SecretOpcode)
            {
                secret = null;
                return false;
            }

            secret = buffer.Slice(1, SecretSizeBytes).ToArray();
            return true;
        }

        /// <summary>
        /// Decodes a 32-byte secret from a 33-byte SessionSecretMessage buffer into a destination span (strictly Zero-GC).
        /// </summary>
        public static bool TryDecodeSecret(ReadOnlySpan<byte> buffer, Span<byte> secretDestination)
        {
            if (buffer.Length < SecretMessageSizeBytes || secretDestination.Length < SecretSizeBytes || buffer[0] != SecretOpcode)
            {
                return false;
            }

            buffer.Slice(1, SecretSizeBytes).CopyTo(secretDestination.Slice(0, SecretSizeBytes));
            return true;
        }

        /// <summary>
        /// Encodes a SessionSecretMessage into destination buffer (33 bytes).
        /// </summary>
        public static bool TryEncodeSecretMessage(in SessionSecretMessage message, Span<byte> buffer, out int written)
        {
            if (buffer.Length < SecretMessageSizeBytes)
            {
                written = 0;
                return false;
            }

            buffer[0] = message.Opcode;
            message.CopySecretTo(buffer.Slice(1, SecretSizeBytes));
            written = SecretMessageSizeBytes;
            return true;
        }

        /// <summary>
        /// Decodes a SessionSecretMessage from source buffer (minimum 33 bytes).
        /// Performs strict zero GC allocations.
        /// </summary>
        public static bool TryDecodeSecretMessage(ReadOnlySpan<byte> buffer, out SessionSecretMessage message)
        {
            if (buffer.Length < SecretMessageSizeBytes)
            {
                message = default;
                return false;
            }

            byte opcode = buffer[0];
            if (opcode != SecretOpcode)
            {
                message = default;
                return false;
            }

            var secret = new SessionSecret32(buffer.Slice(1, SecretSizeBytes));
            message = new SessionSecretMessage(opcode, secret);
            return true;
        }

        /// <summary>
        /// Constant-time buffer comparison to prevent timing attacks.
        /// </summary>
        public static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
    }
}
