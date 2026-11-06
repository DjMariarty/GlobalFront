namespace GlobalFront.Core.Catalog
{
    /// <summary>
    /// Wire identifiers of the playable factions (Step 5.5.1).
    ///
    /// A raw <see cref="byte"/> rather than an enum type because the value is
    /// protocol-significant: it travels inside <see cref="BlueprintDefinition"/>
    /// rows and will ride along the blueprint roster digests once the server
    /// publishes them. None is deliberately 0: a neutral world object (an Oil
    /// Derrick, a Supply Dock) has no owner faction, and 0 must mean exactly
    /// that instead of quietly claiming the first real faction.
    ///
    /// Values are append-only, mirroring the <c>UnitKinds</c> discipline:
    /// renumbering an existing faction would silently re-skin every blueprint
    /// row in a recorded stream or an older client build.
    /// </summary>
    public static class FactionId
    {
        /// <summary>No faction: neutral world objects and unrouted blueprint ids.</summary>
        public const byte None = 0;

        /// <summary>Air/tech doctrine; Drop Zones, laser tiers, composite armour.</summary>
        public const byte USA = 1;

        /// <summary>Mass doctrine; hackers, nukes, volume armour.</summary>
        public const byte China = 2;

        /// <summary>Guerrilla doctrine; zero power grid, salvage economy.</summary>
        public const byte GLA = 3;

        /// <summary>Heavy armour doctrine; slow, hard-hitting ground forces.</summary>
        public const byte Russia = 4;

        /// <summary>Precision doctrine; fast, expensive, special-operations units.</summary>
        public const byte NATO = 5;

        /// <summary>
        /// One past the highest defined faction, so <see cref="Count"/> is directly
        /// usable as the length of per-faction arrays.
        /// </summary>
        public const byte Count = 6;

        /// <summary>False for 0 (None) and for anything at or above <see cref="Count"/>.</summary>
        public static bool IsValid(byte faction) => faction > None && faction < Count;
    }
}
