using System;
using System.Buffers.Binary;
using GlobalFront.Core.Model;

namespace GlobalFront.Core.Reconnect
{
    /// <summary>
    /// Outcome of a reconnect attempt on the wire (ADR-011, step 2.7.1).
    /// </summary>
    public enum ReconnectResult : byte
    {
        Accepted = 0,
        SessionNotFound = 1,
        InvalidSecret = 2,
        GraceExpired = 3,
        MatchFinished = 4,
        InvalidState = 5,
        Denied = 5
    }

    /// <summary>
    /// Immutable 32-byte value container for the session reconnect secret.
    /// Stores the 256 bits across 4 ulong values for strictly zero GC allocation.
    /// </summary>
    public readonly struct SessionSecret32 : IEquatable<SessionSecret32>
    {
        public const int SizeBytes = 32;

        public ulong Part0 { get; }
        public ulong Part1 { get; }
        public ulong Part2 { get; }
        public ulong Part3 { get; }

        public SessionSecret32(ulong part0, ulong part1, ulong part2, ulong part3)
        {
            Part0 = part0;
            Part1 = part1;
            Part2 = part2;
            Part3 = part3;
        }

        public SessionSecret32(ReadOnlySpan<byte> span)
        {
            if (span.Length < SizeBytes)
            {
                throw new ArgumentException($"Buffer must be at least {SizeBytes} bytes.", nameof(span));
            }

            Part0 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(0, 8));
            Part1 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
            Part2 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16, 8));
            Part3 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24, 8));
        }

        public void CopyTo(Span<byte> destination)
        {
            if (destination.Length < SizeBytes)
            {
                throw new ArgumentException($"Destination must be at least {SizeBytes} bytes.", nameof(destination));
            }

            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(0, 8), Part0);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8, 8), Part1);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(16, 8), Part2);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(24, 8), Part3);
        }

        public byte[] ToByteArray()
        {
            var bytes = new byte[SizeBytes];
            CopyTo(bytes);
            return bytes;
        }

        public bool Equals(SessionSecret32 other) =>
            Part0 == other.Part0 &&
            Part1 == other.Part1 &&
            Part2 == other.Part2 &&
            Part3 == other.Part3;

        public override bool Equals(object obj) => obj is SessionSecret32 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Part0, Part1, Part2, Part3);

        public static bool operator ==(SessionSecret32 left, SessionSecret32 right) => left.Equals(right);
        public static bool operator !=(SessionSecret32 left, SessionSecret32 right) => !left.Equals(right);
    }

    /// <summary>
    /// Reconnect request sent by client on channel C0 (opcode = 12, size = 64 bytes).
    /// </summary>
    public readonly struct ReconnectRequest : IEquatable<ReconnectRequest>
    {
        public const byte ExpectedOpcode = 12;
        public const ushort ExpectedProtocolVersion = 1;
        public const int SizeBytes = 64;
        public const int SecretSizeBytes = 32;

        public byte Opcode { get; }
        public ushort ProtocolVersion { get; }
        public SessionId SessionId { get; }
        public SessionSecret32 Secret { get; }
        public ulong LastAppliedTick { get; }

        public byte[] ReconnectSecret => Secret.ToByteArray();

        public ReconnectRequest(
            byte opcode,
            ushort protocolVersion,
            SessionId sessionId,
            SessionSecret32 secret,
            ulong lastAppliedTick)
        {
            Opcode = opcode;
            ProtocolVersion = protocolVersion;
            SessionId = sessionId;
            Secret = secret;
            LastAppliedTick = lastAppliedTick;
        }

        public ReconnectRequest(
            byte opcode,
            ushort protocolVersion,
            SessionId sessionId,
            ReadOnlySpan<byte> secret,
            ulong lastAppliedTick)
            : this(opcode, protocolVersion, sessionId, new SessionSecret32(secret), lastAppliedTick)
        {
        }

        public ReconnectRequest(
            byte opcode,
            ushort protocolVersion,
            SessionId sessionId,
            byte[] secret,
            ulong lastAppliedTick)
            : this(opcode, protocolVersion, sessionId, new SessionSecret32(secret), lastAppliedTick)
        {
        }

        public ReconnectRequest(
            SessionId sessionId,
            in SessionSecret32 secret,
            ulong lastAppliedTick)
            : this(ExpectedOpcode, ExpectedProtocolVersion, sessionId, secret, lastAppliedTick)
        {
        }

        public ReconnectRequest(
            SessionId sessionId,
            ReadOnlySpan<byte> secret,
            ulong lastAppliedTick)
            : this(ExpectedOpcode, ExpectedProtocolVersion, sessionId, new SessionSecret32(secret), lastAppliedTick)
        {
        }

        public ReconnectRequest(
            SessionId sessionId,
            byte[] secret,
            ulong lastAppliedTick)
            : this(ExpectedOpcode, ExpectedProtocolVersion, sessionId, new SessionSecret32(secret), lastAppliedTick)
        {
        }

        public void CopySecretTo(Span<byte> destination) => Secret.CopyTo(destination);

        public bool Equals(ReconnectRequest other) =>
            Opcode == other.Opcode &&
            ProtocolVersion == other.ProtocolVersion &&
            SessionId.Equals(other.SessionId) &&
            Secret.Equals(other.Secret) &&
            LastAppliedTick == other.LastAppliedTick;

        public override bool Equals(object obj) => obj is ReconnectRequest other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Opcode, ProtocolVersion, SessionId, Secret, LastAppliedTick);

        public static bool operator ==(ReconnectRequest left, ReconnectRequest right) => left.Equals(right);
        public static bool operator !=(ReconnectRequest left, ReconnectRequest right) => !left.Equals(right);
    }

    /// <summary>
    /// Reconnect response sent by server on channel C0 (opcode = 13, size = 92 bytes).
    /// </summary>
    public readonly struct ReconnectResponse : IEquatable<ReconnectResponse>
    {
        public const byte ExpectedOpcode = 13;
        public const int SizeBytes = 92;

        public byte Opcode { get; }
        public ReconnectResult Result { get; }
        public PlayerId AssignedPlayer { get; }
        public MatchId Match { get; }
        public ulong ServerTick { get; }
        public ushort ActiveKeyframeSeq { get; }

        public ulong MatchValue => Match.AsUInt64();

        public ReconnectResponse(
            byte opcode,
            ReconnectResult result,
            PlayerId assignedPlayer,
            MatchId match,
            ulong serverTick,
            ushort activeKeyframeSeq)
        {
            Opcode = opcode;
            Result = result;
            AssignedPlayer = assignedPlayer;
            Match = match;
            ServerTick = serverTick;
            ActiveKeyframeSeq = activeKeyframeSeq;
        }

        public ReconnectResponse(
            byte opcode,
            ReconnectResult result,
            PlayerId assignedPlayer,
            ulong match,
            ulong serverTick,
            ushort activeKeyframeSeq)
            : this(opcode, result, assignedPlayer, new MatchId(match), serverTick, activeKeyframeSeq)
        {
        }

        public ReconnectResponse(
            ReconnectResult result,
            PlayerId assignedPlayer,
            MatchId match,
            ulong serverTick,
            ushort activeKeyframeSeq)
            : this(ExpectedOpcode, result, assignedPlayer, match, serverTick, activeKeyframeSeq)
        {
        }

        public ReconnectResponse(
            ReconnectResult result,
            PlayerId assignedPlayer,
            ulong match,
            ulong serverTick,
            ushort activeKeyframeSeq)
            : this(ExpectedOpcode, result, assignedPlayer, new MatchId(match), serverTick, activeKeyframeSeq)
        {
        }

        public bool Equals(ReconnectResponse other) =>
            Opcode == other.Opcode &&
            Result == other.Result &&
            AssignedPlayer.Equals(other.AssignedPlayer) &&
            Match.Equals(other.Match) &&
            ServerTick == other.ServerTick &&
            ActiveKeyframeSeq == other.ActiveKeyframeSeq;

        public override bool Equals(object obj) => obj is ReconnectResponse other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Opcode, Result, AssignedPlayer, Match, ServerTick, ActiveKeyframeSeq);

        public static bool operator ==(ReconnectResponse left, ReconnectResponse right) => left.Equals(right);
        public static bool operator !=(ReconnectResponse left, ReconnectResponse right) => !left.Equals(right);
    }

    /// <summary>
    /// Initial/refreshed session secret message sent on channel C0 (opcode = 9, size = 33 bytes).
    /// </summary>
    public readonly struct SessionSecretMessage : IEquatable<SessionSecretMessage>
    {
        public const byte ExpectedOpcode = 9;
        public const int SizeBytes = 33;
        public const int SecretSizeBytes = 32;

        public byte Opcode { get; }
        public SessionSecret32 Secret { get; }

        public byte[] SecretBytes => Secret.ToByteArray();

        public SessionSecretMessage(byte opcode, SessionSecret32 secret)
        {
            Opcode = opcode;
            Secret = secret;
        }

        public SessionSecretMessage(in SessionSecret32 secret)
            : this(ExpectedOpcode, secret)
        {
        }

        public SessionSecretMessage(byte opcode, ReadOnlySpan<byte> secret)
            : this(opcode, new SessionSecret32(secret))
        {
        }

        public SessionSecretMessage(byte opcode, byte[] secret)
            : this(opcode, new SessionSecret32(secret))
        {
        }

        public SessionSecretMessage(ReadOnlySpan<byte> secret)
            : this(ExpectedOpcode, new SessionSecret32(secret))
        {
        }

        public SessionSecretMessage(byte[] secret)
            : this(ExpectedOpcode, new SessionSecret32(secret))
        {
        }

        public void CopySecretTo(Span<byte> destination) => Secret.CopyTo(destination);

        public bool Equals(SessionSecretMessage other) =>
            Opcode == other.Opcode && Secret.Equals(other.Secret);

        public override bool Equals(object obj) => obj is SessionSecretMessage other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Opcode, Secret);

        public static bool operator ==(SessionSecretMessage left, SessionSecretMessage right) => left.Equals(right);
        public static bool operator !=(SessionSecretMessage left, SessionSecretMessage right) => !left.Equals(right);
    }
}
