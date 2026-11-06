using GlobalFront.Core.Combat;

namespace GlobalFront.Core.Catalog
{
    /// <summary>
    /// One immutable row of the authoritative blueprint registry (Step 5.5.1).
    ///
    /// The registry is where the five-faction roster becomes data: combat
    /// characteristics ride the existing <see cref="CombatStats"/> (matrix-typed
    /// damage and armor, ADR-015), while the economy fields (cost, build time,
    /// power) and the construction geometry (footprint cells on the 500 mm Snap
    /// Grid) are what later steps wire into construction, production and the
    /// power ledger. A row never mutates after the catalog's load-time
    /// validation, so a definition can be handed out freely without a copy.
    ///
    /// The constructor is deliberately passive: every structural rule lives in
    /// the <see cref="BlueprintCatalog"/> constructor, which validates the whole
    /// table fail-fast at load. <see cref="CombatStats"/> already refuses an
    /// impossible stat line (non-positive health, damage out of range, a
    /// damage/armor byte outside the 8x7 matrix and not the legacy sentinel),
    /// so a bad row cannot even be built before the catalog sees it.
    ///
    /// Power fields are non-negative magnitudes: <c>PowerProduction</c> is what
    /// the structure feeds into the grid, <c>PowerConsumption</c> is what it
    /// draws. A net figure (a factory's -4 in the design notes) is the consumer's
    /// subtraction, not a signed field. GLA rows are all-zero by the
    /// zero-power rule (ADR for the power ledger).
    /// </summary>
    public readonly struct BlueprintDefinition
    {
        public BlueprintDefinition(
            BlueprintId id,
            byte faction,
            BlueprintCategory category,
            byte legacyKind,
            int cost,
            uint buildTimeTicks,
            CombatStats stats,
            int speedMmPerTick,
            int footprintWidthCells,
            int footprintHeightCells,
            int powerProduction,
            int powerConsumption,
            int visionRangeMm,
            BlueprintId prerequisiteId = default)
        {
            Id = id;
            Faction = faction;
            Category = category;
            LegacyKind = legacyKind;
            Cost = cost;
            BuildTimeTicks = buildTimeTicks;
            Stats = stats;
            SpeedMmPerTick = speedMmPerTick;
            FootprintWidthCells = footprintWidthCells;
            FootprintHeightCells = footprintHeightCells;
            PowerProduction = powerProduction;
            PowerConsumption = powerConsumption;
            VisionRangeMm = visionRangeMm;
            PrerequisiteId = prerequisiteId;
        }

        /// <summary>Registry identity; <see cref="BlueprintId.None"/> never enters the catalog.</summary>
        public BlueprintId Id { get; }

        /// <summary>
        /// Owning faction: a <see cref="FactionId"/> value, or
        /// <see cref="FactionId.None"/> for neutral world objects.
        /// </summary>
        public byte Faction { get; }

        /// <summary>Structural class driving footprint and movement invariants.</summary>
        public BlueprintCategory Category { get; }

        /// <summary>
        /// Legacy <c>UnitKinds</c> mapping: the byte the v2 wire protocol and the
        /// client renderers still speak. The ten-kind space is far coarser than
        /// the roster (every infantry row maps to one kind), so presentation
        /// resolves through this bridge until the renderers move to blueprint
        /// ids. Structural rows map to <c>BaseStructure</c>.
        /// </summary>
        public byte LegacyKind { get; }

        /// <summary>Credit price; neutral world objects that are never built carry 0.</summary>
        public int Cost { get; }

        /// <summary>Production duration in ticks at normal power; 0 is refused at load.</summary>
        public uint BuildTimeTicks { get; }

        /// <summary>Combat line: health, damage, range, cooldown, matrix types.</summary>
        public CombatStats Stats { get; }

        /// <summary>Ground speed in mm per tick; 0 for stationary rows.</summary>
        public int SpeedMmPerTick { get; }

        /// <summary>Footprint extent on the 500 mm construction grid, X axis; 0 for mobile rows.</summary>
        public int FootprintWidthCells { get; }

        /// <summary>Footprint extent on the 500 mm construction grid, Y axis; 0 for mobile rows.</summary>
        public int FootprintHeightCells { get; }

        /// <summary>Power fed into the grid per the ledger's unit scale; 0 unless a producer.</summary>
        public int PowerProduction { get; }

        /// <summary>Power drawn from the grid per the ledger's unit scale; 0 for GLA rows.</summary>
        public int PowerConsumption { get; }

        /// <summary>Sight radius in millimetres; strictly positive for every row.</summary>
        public int VisionRangeMm { get; }

        /// <summary>Registry identity that must be owned before this row is buildable; None if free.</summary>
        public BlueprintId PrerequisiteId { get; }

        /// <summary>
        /// True once the row carries a real identity and a real health pool — the
        /// two properties every consumer trusts blindly. A default-initialized
        /// row (an empty catalog slot) reads as unresolved.
        /// </summary>
        public bool IsResolved => Id.IsValid && Stats.MaximumHealth > 0;
    }
}
