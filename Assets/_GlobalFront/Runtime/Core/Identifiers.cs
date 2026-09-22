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
    /// Identifies a team in multiplayer matches (Phase 2.8).
    /// Team 0 = Red, Team 1 = Blue in 2v2 topology.
    /// </summary>
    public readonly struct TeamId : IEquatable<TeamId>
    {
        public TeamId(byte value)
        {
            Value = value;
        }

        public byte Value { get; }

        public bool IsValid => Value <= 1;

        public bool Equals(TeamId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is TeamId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => $"Team{Value}";

        public static bool operator ==(TeamId left, TeamId right) => left.Equals(right);

        public static bool operator !=(TeamId left, TeamId right) => !left.Equals(right);
    }

    /// <summary>
    /// Wire identifiers of the unit archetypes the client can present (OD-29).
    ///
    /// A raw <see cref="byte"/> rather than an enum type because the value is
    /// protocol-significant: it is the 40th byte of a delta ADD record and the key
    /// of the client-side <c>UnitCatalog</c>. Unknown is deliberately 0: the
    /// record structs are pooled and folded through <c>default(DeltaAddRecord)</c>
    /// in the change-set builder, so 0 must mean "no kind resolved" instead of
    /// quietly claiming the cheapest real archetype. A record that predates OD-29,
    /// a Snapshot Protocol v1 unit record and a buffer slot the catalog has no row
    /// for all decode to Unknown, and presentation falls back to its
    /// no-stat-resolved behaviour rather than guessing.
    ///
    /// Values are append-only: renumbering an existing kind would silently
    /// re-skin every unit in a recorded stream or an older client build.
    /// </summary>
    public static class UnitKinds
    {
        /// <summary>No archetype resolved: legacy record, or a kind the catalog lacks.</summary>
        public const byte Unknown = 0;

        /// <summary>Fast line unit; the cheapest roster entry.</summary>
        public const byte Scout = 1;

        /// <summary>Slow armoured unit; the turret-bearing presentation case.</summary>
        public const byte Tank = 2;

        /// <summary>Static economy structure; excluded from unit rings by rules later on.</summary>
        public const byte BaseStructure = 3;

        /// <summary>
        /// One past the highest defined kind, so <c>UnitKinds.Count</c> is directly
        /// usable as the O(1) catalog array length.
        /// </summary>
        public const byte Count = 4;

        /// <summary>False for 0 (Unknown) and for anything at or above <see cref="Count"/>.</summary>
        public static bool IsDefined(byte kind) => kind != Unknown && kind < Count;
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
