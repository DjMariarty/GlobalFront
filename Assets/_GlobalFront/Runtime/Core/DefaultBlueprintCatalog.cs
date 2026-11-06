using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;

namespace GlobalFront.Core.Catalog
{
    /// <summary>
    /// The Step 5.5.1 starter roster: one data-backed row per faction archetype
    /// named in the design notes, plus the two neutral world objects the
    /// economy already simulates. It exists so the catalog, its invariants and
    /// the tests have a canonical table to stand on — the ~170-blueprint
    /// five-faction roster lands as more rows in later steps, not as a shape
    /// change.
    ///
    /// Numbers explicitly fixed by the Step 5.5.1 notes (costs, speeds,
    /// footprints, command-center power) are honoured verbatim. The rest
    /// (health, damage, ranges, cooldowns, build times, vision) are placeholder
    /// values pegged to the prototypes the sim already runs — the legacy
    /// <c>CombatStats(100, 25, 7000, 10)</c> produced-unit line and the
    /// <c>FrameStats(500, 1, 0, 1)</c> construction line — and are owner
    /// balance decisions, not sim commitments.
    ///
    /// Matrix typing follows the 8x7 ADR-015 table: the design notes' cannon
    /// variants (Cannon/HeavyCannon/PrecisionCannon) all resolve to
    /// <see cref="DamageType.AntiTankCannon"/>, modern infantry kit
    /// (ModernKevlar) to <see cref="ArmorType.HeavyInfantry"/>, main battle
    /// tanks to <see cref="ArmorType.HeavyVehicle"/> while the Scorpion stays
    /// light. Buildings never fire, so they carry the legacy sentinel line
    /// (inert damage 1, cooldown 1) the construction frames already use.
    /// </summary>
    public static class DefaultBlueprintCatalog
    {
        /// <summary>Builds the starter catalog. Deterministic and allocation-only at load.</summary>
        public static BlueprintCatalog Create()
        {
            return new BlueprintCatalog(new[]
            {
                // Neutral (1..99): world objects the economy registers, never builds.
                new BlueprintDefinition(
                    new BlueprintId(1), FactionId.None, BlueprintCategory.Building, UnitKinds.BaseStructure,
                    cost: 0, buildTimeTicks: 1, stats: new CombatStats(1000, 1, 0, 1),
                    speedMmPerTick: 0, footprintWidthCells: 4, footprintHeightCells: 4,
                    powerProduction: 0, powerConsumption: 0, visionRangeMm: 6000),
                new BlueprintDefinition(
                    new BlueprintId(2), FactionId.None, BlueprintCategory.Supply, UnitKinds.BaseStructure,
                    cost: 0, buildTimeTicks: 1, stats: new CombatStats(1500, 1, 0, 1),
                    speedMmPerTick: 0, footprintWidthCells: 6, footprintHeightCells: 6,
                    powerProduction: 0, powerConsumption: 0, visionRangeMm: 6000),

                // USA (100..199): Ranger, Crusader, Command Center.
                Infantry(new BlueprintId(100), FactionId.USA,
                    cost: 225, buildTimeTicks: 150, health: 100, speedMmPerTick: 600, visionRangeMm: 9000),
                Vehicle(new BlueprintId(101), FactionId.USA,
                    cost: 900, buildTimeTicks: 400, health: 400, damage: 60, rangeMm: 9000, cooldownTicks: 50,
                    speedMmPerTick: 800, visionRangeMm: 8000),
                CommandCenter(new BlueprintId(110), FactionId.USA,
                    powerProduction: 5),

                // China (200..299): Red Guard, Battlemaster, Command Center.
                Infantry(new BlueprintId(200), FactionId.China,
                    cost: 150, buildTimeTicks: 125, health: 150, speedMmPerTick: 550, visionRangeMm: 9000),
                Vehicle(new BlueprintId(201), FactionId.China,
                    cost: 800, buildTimeTicks: 350, health: 400, damage: 60, rangeMm: 9000, cooldownTicks: 50,
                    speedMmPerTick: 750, visionRangeMm: 8000),
                CommandCenter(new BlueprintId(210), FactionId.China,
                    powerProduction: 0),

                // GLA (300..399): Rebel, Scorpion Tank, Command Center.
                Infantry(new BlueprintId(300), FactionId.GLA,
                    cost: 150, buildTimeTicks: 125, health: 100, speedMmPerTick: 600, visionRangeMm: 9000),
                Vehicle(new BlueprintId(301), FactionId.GLA,
                    cost: 650, buildTimeTicks: 300, health: 300, damage: 60, rangeMm: 8500, cooldownTicks: 45,
                    speedMmPerTick: 850, visionRangeMm: 8000, lightArmor: true),
                CommandCenter(new BlueprintId(310), FactionId.GLA,
                    powerProduction: 0),

                // Russia (400..499): Conscript, T-90 Heavy Tank, Command Center.
                Infantry(new BlueprintId(400), FactionId.Russia,
                    cost: 100, buildTimeTicks: 100, health: 80, speedMmPerTick: 500, visionRangeMm: 8000),
                Vehicle(new BlueprintId(401), FactionId.Russia,
                    cost: 1100, buildTimeTicks: 500, health: 600, damage: 75, rangeMm: 9500, cooldownTicks: 60,
                    speedMmPerTick: 700, visionRangeMm: 8000),
                CommandCenter(new BlueprintId(410), FactionId.Russia,
                    powerProduction: 3),

                // NATO (500..599): Peacekeeper, Leopard 2, Command Center.
                Infantry(new BlueprintId(500), FactionId.NATO,
                    cost: 200, buildTimeTicks: 150, health: 120, speedMmPerTick: 580, visionRangeMm: 9000,
                    heavyInfantry: true),
                Vehicle(new BlueprintId(501), FactionId.NATO,
                    cost: 1050, buildTimeTicks: 475, health: 500, damage: 70, rangeMm: 9500, cooldownTicks: 55,
                    speedMmPerTick: 820, visionRangeMm: 8000),
                CommandCenter(new BlueprintId(510), FactionId.NATO,
                    powerProduction: 4),
            });
        }

        /// <summary>Mobile infantry row: SmallArms fire, no footprint, no power.</summary>
        private static BlueprintDefinition Infantry(
            BlueprintId id, byte faction,
            int cost, uint buildTimeTicks, int health, int speedMmPerTick, int visionRangeMm,
            bool heavyInfantry = false)
        {
            return new BlueprintDefinition(
                id, faction, BlueprintCategory.Infantry, UnitKinds.Infantry,
                cost, buildTimeTicks,
                new CombatStats(
                    health, 25, 7000, 10,
                    (byte)DamageType.SmallArms,
                    (byte)(heavyInfantry ? ArmorType.HeavyInfantry : ArmorType.Infantry)),
                speedMmPerTick, 0, 0, 0, 0, visionRangeMm);
        }

        /// <summary>
        /// Ground vehicle row: AntiTankCannon fire against the 8x7 matrix's
        /// heavy or light vehicle armor, no footprint, no power.
        /// </summary>
        private static BlueprintDefinition Vehicle(
            BlueprintId id, byte faction,
            int cost, uint buildTimeTicks, int health, int damage, int rangeMm, uint cooldownTicks,
            int speedMmPerTick, int visionRangeMm, bool lightArmor = false)
        {
            return new BlueprintDefinition(
                id, faction, BlueprintCategory.Vehicle, UnitKinds.Tank,
                cost, buildTimeTicks,
                new CombatStats(
                    health, damage, rangeMm, cooldownTicks,
                    (byte)DamageType.AntiTankCannon,
                    (byte)(lightArmor ? ArmorType.LightVehicle : ArmorType.HeavyVehicle)),
                speedMmPerTick, 0, 0, 0, 0, visionRangeMm);
        }

        /// <summary>
        /// Faction command center: an 8x8 footprint, a legacy inert stat line
        /// (command centers do not fire), and the design note's power delta.
        /// </summary>
        private static BlueprintDefinition CommandCenter(BlueprintId id, byte faction, int powerProduction)
        {
            return new BlueprintDefinition(
                id, faction, BlueprintCategory.Building, UnitKinds.BaseStructure,
                cost: 2000, buildTimeTicks: 600, stats: new CombatStats(4000, 1, 0, 1),
                speedMmPerTick: 0, footprintWidthCells: 8, footprintHeightCells: 8,
                powerProduction: powerProduction, powerConsumption: 0, visionRangeMm: 12000);
        }
    }
}
