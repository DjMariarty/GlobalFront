using System;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Model
{
    public readonly struct EntityId : IEquatable<EntityId>
    {
        public EntityId(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        public bool IsValid => Value != 0;

        public bool Equals(EntityId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is EntityId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => IsValid ? Value.ToString() : "Invalid";

        public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);

        public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);
    }

    public readonly struct PlayerId : IEquatable<PlayerId>
    {
        public PlayerId(byte value)
        {
            Value = value;
        }

        public byte Value { get; }

        public bool IsValid => Value >= 1 && Value <= SimulationConstants.MaxPlayers;

        public bool Equals(PlayerId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is PlayerId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => IsValid ? Value.ToString() : "Invalid";

        public static bool operator ==(PlayerId left, PlayerId right) => left.Equals(right);

        public static bool operator !=(PlayerId left, PlayerId right) => !left.Equals(right);
    }

    /// <summary>
    /// Opaque server-assigned identity of one client attachment (Phase 2.4,
    /// ADR-008). Guid-backed so the value carries entropy: until
    /// authentication exists, the SessionId is also the reconnect identity
    /// handle (OD-4). Generation happens exclusively in
    /// <c>GlobalFront.Server</c>; this type is a pure value and never enters
    /// deterministic simulation state.
    /// </summary>
    public readonly struct SessionId : IEquatable<SessionId>
    {
        public SessionId(Guid value)
        {
            Value = value;
        }

        public Guid Value { get; }

        public bool IsValid => Value != Guid.Empty;

        public bool Equals(SessionId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is SessionId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => IsValid ? Value.ToString() : "Invalid";

        public static bool operator ==(SessionId left, SessionId right) => left.Equals(right);

        public static bool operator !=(SessionId left, SessionId right) => !left.Equals(right);
    }

    /// <summary>
    /// Opaque server-assigned identity of one match instance (Phase 2.4,
    /// ADR-008). Scoped to the match: full participant addressing is the
    /// tuple (MatchId, PlayerId). Generated exclusively in
    /// <c>GlobalFront.Server</c>; never part of deterministic simulation
    /// state or the Snapshot Protocol v1 payload (OD-5).
    /// </summary>
    public readonly struct MatchId : IEquatable<MatchId>
    {
        public MatchId(Guid value)
        {
            Value = value;
        }

        public MatchId(ulong value)
        {
            Span<byte> bytes = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            Value = new Guid(bytes);
        }

        public ulong AsUInt64()
        {
            Span<byte> bytes = stackalloc byte[16];
            Value.TryWriteBytes(bytes);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }

        public Guid Value { get; }

        public bool IsValid => Value != Guid.Empty;

        public bool Equals(MatchId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is MatchId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => IsValid ? Value.ToString() : "Invalid";

        public static bool operator ==(MatchId left, MatchId right) => left.Equals(right);

        public static bool operator !=(MatchId left, MatchId right) => !left.Equals(right);
    }
}
