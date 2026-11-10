using System;
using GlobalFront.Core.Model;

namespace GlobalFront.Core.Catalog
{
    /// <summary>
    /// Fail-closed completeness gate of the five-faction roster (Step 5.5.3):
    /// walks a catalog and answers whether every faction fields the minimum
    /// playable set. The authoritative catalog must pass before it ships; a
    /// data-driven or modded catalog that drops a builder or adds GLA aircraft
    /// fails loudly here instead of surfacing as a broken match five phases
    /// later.
    ///
    /// Role identification is canonical-id based for the buildings the roster
    /// layout fixes (HQ = x10, Barracks = x13, War Factory = x14, builder =
    /// x24 of each faction band — the layout DefaultBlueprintCatalog ships and
    /// the ТЗ pins with ids 174/224/373/424/524), and structural for the
    /// classes the data already expresses (Supply centers, harvesters,
    /// defenses, superweapons, aircraft, secondary-income kinds, power fields,
    /// heroes). A hero is an infantry row with at least
    /// <see cref="HeroMinimumDamage"/> damage and
    /// <see cref="HeroMinimumHealth"/> health.
    ///
    /// Walking is allocation-free: the id enumeration rides a stackalloc span
    /// and every row is resolved through <see cref="IBlueprintCatalog.TryGet"/>;
    /// the only allocation is the error string on a failure.
    /// </summary>
    public static class FactionRosterCompletenessValidator
    {
        /// <summary>Minimum damage of a hero infantry row.</summary>
        public const int HeroMinimumDamage = 90;

        /// <summary>Minimum maximum-health of a hero infantry row.</summary>
        public const int HeroMinimumHealth = 150;

        private const int MaxStackallocIds = 512;

        /// <summary>
        /// The ТЗ-fixed canonical ids per faction (indexed by FactionId):
        /// HQ, Barracks, War Factory, physical builder. The builder ids are
        /// deliberately uneven — 174/224/373/424/524 — because the ТЗ pinned
        /// them inside each faction's occupied band.
        /// </summary>
        private static readonly ushort[] HqIds = { 110, 210, 310, 410, 510 };
        private static readonly ushort[] BarracksIds = { 113, 213, 312, 413, 513 };
        private static readonly ushort[] FactoryIds = { 114, 214, 313, 414, 514 };
        private static readonly ushort[] BuilderIds = { 174, 224, 373, 424, 524 };

        /// <summary>
        /// Validates the roster. Returns false and a human-readable
        /// <paramref name="error"/> naming the first broken invariant.
        /// </summary>
        public static bool Validate(IBlueprintCatalog catalog, out string error)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            error = null;

            Span<ushort> ids = catalog.Count <= MaxStackallocIds
                ? stackalloc ushort[MaxStackallocIds]
                : new ushort[catalog.Count];
            var copied = catalog.CopyBlueprintIds(ids);
            if (copied != catalog.Count)
            {
                error = "The catalog enumeration disagreed with its own Count.";
                return false;
            }

            // Per-faction structural tallies indexed by FactionId.
            Span<int> supply = stackalloc int[FactionId.Count];
            Span<int> harvesters = stackalloc int[FactionId.Count];
            Span<int> defenses = stackalloc int[FactionId.Count];
            Span<int> superweapons = stackalloc int[FactionId.Count];
            Span<int> aircraft = stackalloc int[FactionId.Count];
            Span<int> secondary = stackalloc int[FactionId.Count];
            Span<bool> heroes = stackalloc bool[FactionId.Count];
            Span<bool> powerProducers = stackalloc bool[FactionId.Count];
            Span<bool> powerConsumers = stackalloc bool[FactionId.Count];

            for (var index = 0; index < copied; index++)
            {
                if (!catalog.TryGet(new BlueprintId(ids[index]), out var definition))
                {
                    error = $"Blueprint {ids[index]} enumerated but did not resolve.";
                    return false;
                }

                var faction = definition.Faction;
                if (faction == FactionId.None)
                {
                    continue;
                }

                if (definition.Category == BlueprintCategory.Supply)
                {
                    supply[faction]++;
                }

                if (definition.LegacyKind == UnitKinds.Harvester)
                {
                    harvesters[faction]++;
                }

                if (definition.Category == BlueprintCategory.Defense)
                {
                    defenses[faction]++;
                }

                if (definition.Category == BlueprintCategory.Superweapon)
                {
                    superweapons[faction]++;
                }

                if (definition.Category == BlueprintCategory.Aircraft)
                {
                    aircraft[faction]++;
                }

                if (IsSecondaryIncomeKind(definition.LegacyKind))
                {
                    secondary[faction]++;
                }

                if (definition.Category == BlueprintCategory.Infantry &&
                    definition.Stats.Damage >= HeroMinimumDamage &&
                    definition.Stats.MaximumHealth >= HeroMinimumHealth)
                {
                    heroes[faction] = true;
                }

                if (definition.PowerProduction > 0)
                {
                    powerProducers[faction] = true;
                }

                if (definition.PowerConsumption > 0)
                {
                    powerConsumers[faction] = true;
                }
            }

            for (byte faction = FactionId.USA; faction < FactionId.Count; faction++)
            {
                var slot = faction - FactionId.USA;

                if (!catalog.TryGet(new BlueprintId(HqIds[slot]), out var hq) ||
                    !hq.IsResolved ||
                    hq.Category != BlueprintCategory.Building ||
                    hq.FootprintWidthCells < 4 ||
                    hq.FootprintHeightCells < 4)
                {
                    error = $"Faction {faction} has no resolved HQ building with a >= 4x4 footprint (id {HqIds[slot]}).";
                    return false;
                }

                if (!catalog.TryGet(new BlueprintId(BuilderIds[slot]), out var builder) ||
                    !builder.IsResolved ||
                    builder.SpeedMmPerTick <= 0 ||
                    (builder.Category != BlueprintCategory.Vehicle &&
                     !(faction == FactionId.GLA && builder.Category == BlueprintCategory.Infantry)))
                {
                    error = $"Faction {faction} has no physical builder (a mobile Vehicle row at id {BuilderIds[slot]}; GLA may field Infantry).";
                    return false;
                }

                if (supply[faction] < 1)
                {
                    error = $"Faction {faction} has no supply drop-off (a Supply-category row).";
                    return false;
                }

                if (harvesters[faction] < 1)
                {
                    error = $"Faction {faction} has no resource harvester (a row with the Harvester legacy kind).";
                    return false;
                }

                if (!catalog.TryGet(new BlueprintId(BarracksIds[slot]), out var barracks) ||
                    barracks.Category != BlueprintCategory.Building ||
                    !barracks.IsResolved)
                {
                    error = $"Faction {faction} has no infantry barracks (id {BarracksIds[slot]}).";
                    return false;
                }

                if (!catalog.TryGet(new BlueprintId(FactoryIds[slot]), out var factory) ||
                    factory.Category != BlueprintCategory.Building ||
                    !factory.IsResolved)
                {
                    error = $"Faction {faction} has no ground-vehicle factory (id {FactoryIds[slot]}).";
                    return false;
                }

                if (defenses[faction] < 1)
                {
                    error = $"Faction {faction} has no defensive structure (a Defense-category row).";
                    return false;
                }

                if (secondary[faction] < 1)
                {
                    error = $"Faction {faction} has no secondary-income building (a DropZone/BlackMarket/OilRig/InternetCenter/LogisticsHub kind).";
                    return false;
                }

                if (superweapons[faction] < 1)
                {
                    error = $"Faction {faction} has no superweapon (a Superweapon-category row).";
                    return false;
                }

                if (!heroes[faction])
                {
                    error = $"Faction {faction} has no hero (an infantry row with >= {HeroMinimumDamage} damage and >= {HeroMinimumHealth} health).";
                    return false;
                }

                if (faction == FactionId.GLA)
                {
                    if (powerProducers[faction] || powerConsumers[faction])
                    {
                        error = "GLA must run zero power production and zero power consumption.";
                        return false;
                    }

                    if (aircraft[faction] != 0)
                    {
                        error = "GLA must field zero aircraft.";
                        return false;
                    }
                }
                else
                {
                    if (!powerProducers[faction])
                    {
                        error = $"Faction {faction} has no power producer (a row with PowerProduction > 0).";
                        return false;
                    }

                    if (aircraft[faction] < 1)
                    {
                        error = $"Faction {faction} has no aircraft (an Aircraft-category row).";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// The five UnitKinds whose per-player count the build gate caps at 4:
        /// every faction's secondary-income building class. Public: the
        /// server's wire build gate enforces the ceiling with it.
        /// </summary>
        public static bool IsSecondaryIncomeKind(byte legacyKind)
        {
            return legacyKind == UnitKinds.DropZone ||
                   legacyKind == UnitKinds.BlackMarket ||
                   legacyKind == UnitKinds.OilRig ||
                   legacyKind == UnitKinds.InternetCenter ||
                   legacyKind == UnitKinds.LogisticsHub;
        }

        /// <summary>
        /// M2 remediation (P1-08): the wire build gate's builder test. Only
        /// the catalog's builder rows (the five canonical ids below) may start
        /// a construction. The legacy kind cannot discriminate — builder rows
        /// share the mobile-armed <see cref="UnitKinds.Tank"/> slot (or
        /// <see cref="UnitKinds.Infantry"/> for the GLA Worker) with combat
        /// units — so the check runs on the blueprint id the unit was spawned
        /// from. Public: the server's wire build gate enforces it.
        /// </summary>
        public static bool IsBuilderBlueprint(ushort blueprintId)
        {
            for (var index = 0; index < BuilderIds.Length; index++)
            {
                if (BuilderIds[index] == blueprintId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// M2 remediation (P1-06): the wire build gate's production-building
        /// test. Factory and barracks rows register in the production registry
        /// on completion and accept Produce orders. The legacy kind cannot
        /// discriminate (every building is a BaseStructure row), so the check
        /// runs on the canonical blueprint ids. Public: the server's wire
        /// build gate drives <c>isProduction</c> with it.
        /// </summary>
        public static bool IsFactoryOrBarracksBlueprint(ushort blueprintId)
        {
            for (var index = 0; index < FactoryIds.Length; index++)
            {
                if (FactoryIds[index] == blueprintId)
                {
                    return true;
                }
            }

            for (var index = 0; index < BarracksIds.Length; index++)
            {
                if (BarracksIds[index] == blueprintId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
