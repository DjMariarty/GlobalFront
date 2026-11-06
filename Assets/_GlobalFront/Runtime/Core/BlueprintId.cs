using System;

namespace GlobalFront.Core.Catalog
{
    /// <summary>
    /// Authoritative identity of one row in the blueprint registry (Step 5.5.1).
    ///
    /// A <see cref="ushort"/> wrapper rather than a bare number because the value
    /// is protocol-significant: it replaces the ad-hoc <c>ushort blueprintId</c>
    /// fields that production queues (<c>ProductionQueueItem</c>) and the player
    /// state sync (<c>PlayerStateSyncModel.HeadBlueprintId</c>) already carry, and
    /// it will be the key the ADD-record extension resolves once the delta
    /// protocol widens for the blueprint roster (RND §5.5.1, ADD-record v3).
    ///
    /// None is deliberately 0: pooled records fold through
    /// <c>default(...)</c>, so 0 must mean "no blueprint resolved" instead of
    /// quietly claiming the first real row.
    ///
    /// Values are append-only and canonically zoned by faction so a lone id
    /// already tells its doctrine:
    /// <list type="bullet">
    /// <item>1..99 — neutral / shared (Oil Derrick, Supply Dock, civilian tech).</item>
    /// <item>100..199 — USA; 200..299 — China; 300..399 — GLA.</item>
    /// <item>400..499 — Russia; 500..599 — NATO.</item>
    /// <item>600+ — reserved for future factions and shared tech.</item>
    /// </list>
    /// Renumbering an existing id would silently re-arm every produced unit in
    /// a recorded stream or an older client build.
    /// </summary>
    public readonly struct BlueprintId : IEquatable<BlueprintId>, IComparable<BlueprintId>
    {
        /// <summary>No blueprint resolved: an unassigned pooled record or a lookup miss.</summary>
        public static readonly BlueprintId None = new BlueprintId(0);

        public BlueprintId(ushort value)
        {
            Value = value;
        }

        public ushort Value { get; }

        /// <summary>False for 0 (<see cref="None"/>); every other ushort is a usable id.</summary>
        public bool IsValid => Value != 0;

        /// <summary>
        /// Reads the faction out of the canonical id zoning without a catalog
        /// lookup. Neutral ids (1..99), the reserved 600+ band and
        /// <see cref="None"/> deduce to <see cref="FactionId.None"/>; the row's
        /// own <c>Faction</c> field stays the authoritative answer for stored
        /// definitions — this is a routing shortcut, not a substitute.
        /// </summary>
        public byte DeduceFaction()
        {
            if (Value >= 100 && Value < 200)
            {
                return FactionId.USA;
            }

            if (Value >= 200 && Value < 300)
            {
                return FactionId.China;
            }

            if (Value >= 300 && Value < 400)
            {
                return FactionId.GLA;
            }

            if (Value >= 400 && Value < 500)
            {
                return FactionId.Russia;
            }

            if (Value >= 500 && Value < 600)
            {
                return FactionId.NATO;
            }

            return FactionId.None;
        }

        public bool Equals(BlueprintId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is BlueprintId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public int CompareTo(BlueprintId other) => Value.CompareTo(other.Value);

        public override string ToString() => IsValid ? $"Blueprint {Value}" : "Invalid";

        public static bool operator ==(BlueprintId left, BlueprintId right) => left.Equals(right);

        public static bool operator !=(BlueprintId left, BlueprintId right) => !left.Equals(right);
    }
}
