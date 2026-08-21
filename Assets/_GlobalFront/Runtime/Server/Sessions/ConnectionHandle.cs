using System;

namespace GlobalFront.Server.Sessions
{
    /// <summary>
    /// Transport-agnostic handle of one live connection (Phase 2.4,
    /// ADR-008). The future transport adapter (Phase 2.5) maps real
    /// connections onto this opaque server-assigned value; a reconnect rebinds
    /// an existing session to a NEW handle. Contains no transport logic.
    /// </summary>
    public readonly struct ConnectionHandle : IEquatable<ConnectionHandle>
    {
        public ConnectionHandle(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        /// <summary>Value 0 is reserved for "no connection".</summary>
        public bool IsValid => Value != 0;

        public bool Equals(ConnectionHandle other) => Value == other.Value;

        public override bool Equals(object obj) => obj is ConnectionHandle other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => IsValid ? Value.ToString() : "Invalid";

        public static bool operator ==(ConnectionHandle left, ConnectionHandle right) => left.Equals(right);

        public static bool operator !=(ConnectionHandle left, ConnectionHandle right) => !left.Equals(right);
    }
}
