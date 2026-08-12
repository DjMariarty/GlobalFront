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
}
