using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;

namespace GlobalFront.Core.Catalog
{
    /// <summary>
    /// The authoritative five-faction roster database (Step 5.5.3): every
    /// named entity of the Generals-formula 1.0 roster as a data-backed row,
    /// plus the neutral world objects. The roster was approved by the product
    /// owner; the balance numbers are the accepted 1.0 draft and will be
    /// calibrated in playtests — the schema (categories, matrix typing,
    /// footprints, power, builders, secondary-income kinds) is the contract,
    /// not the numbers.
    ///
    /// Canonical id zoning (Step 5.5.1) with the Step 5.5.3 role layout the
    /// roster validator depends on: x10 HQ, x13 Barracks, x14 War Factory,
    /// x24 builder. The ТЗ-fixed ids are honoured verbatim: 160 USA Supply
    /// Drop Zone, 174 USA Dozer, 215 China Internet Center, 219 China Hacker,
    /// 224 China Dozer, 358 GLA Black Market, 373 GLA Worker, 415 Russia Oil
    /// Drilling Rig, 424 Russia ИМР «Медведь», 515 NATO Coalition Logistics
    /// Hub, 524 NATO CEV «Kodiak»; ids 1/2/10/11 are the neutral rows.
    ///
    /// Faction invariants the data encodes (and
    /// <see cref="FactionRosterCompletenessValidator"/> enforces): every
    /// faction fields a physical builder; at most 4 secondary-income buildings
    /// per player are enforced server-side; USA/China/Russia/NATO run a power
    /// grid (producers and consumers) and field aircraft; GLA strictly runs
    /// zero power and zero aircraft.
    ///
    /// Matrix typing follows the 8x7 ADR-015 table: cannons resolve to
    /// <see cref="DamageType.AntiTankCannon"/>, AA systems and rockets to
    /// <see cref="DamageType.GuidedMissile"/>, machine-gun clusters to
    /// <see cref="DamageType.HeavyMachineGun"/>, artillery rockets to
    /// <see cref="DamageType.ArtilleryExplosive"/>, flame to
    /// <see cref="DamageType.ThermobaricNapalm"/>, toxin to
    /// <see cref="DamageType.ToxinChemical"/>, experimental beams to
    /// <see cref="DamageType.HighTechLaser"/>. Non-combat rows (builders,
    /// harvesters, logistics) carry the legacy inert stat line, like the
    /// construction frames do.
    /// </summary>
    public static class DefaultBlueprintCatalog
    {
        /// <summary>Builds the full roster catalog. Deterministic and allocation-only at load.</summary>
        public static BlueprintCatalog Create()
        {
            return new BlueprintCatalog(BuildNeutralRows()
                .ConcatChecked(BuildUsaRows())
                .ConcatChecked(BuildChinaRows())
                .ConcatChecked(BuildGlaRows())
                .ConcatChecked(BuildRussiaRows())
                .ConcatChecked(BuildNatoRows()));
        }

        private static BlueprintDefinition[] ConcatChecked(
            this BlueprintDefinition[] left, BlueprintDefinition[] right)
        {
            var merged = new BlueprintDefinition[left.Length + right.Length];
            left.CopyTo(merged, 0);
            right.CopyTo(merged, left.Length);
            return merged;
        }

        // ------------------------------------------------------------------
        // Neutral (1..99)
        // ------------------------------------------------------------------

        private static BlueprintDefinition[] BuildNeutralRows()
        {
            return new[]
            {
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
                Building(new BlueprintId(10), FactionId.None,
                    cost: 0, buildTimeTicks: 1, health: 2000, 6, 6, visionRangeMm: 6000),
                Building(new BlueprintId(11), FactionId.None,
                    cost: 0, buildTimeTicks: 1, health: 1500, 4, 4, visionRangeMm: 6000),
            };
        }

        // ------------------------------------------------------------------
        // USA (100..199): 24 rows
        // ------------------------------------------------------------------

        private static BlueprintDefinition[] BuildUsaRows()
        {
            return new[]
            {
                // Infantry: Ranger, Missile Defender, Pathfinder (hero), Burton (hero).
                Infantry(new BlueprintId(100), FactionId.USA,
                    cost: 225, buildTimeTicks: 150, health: 100, speedMmPerTick: 600, visionRangeMm: 9000),
                Infantry(new BlueprintId(102), FactionId.USA,
                    cost: 300, buildTimeTicks: 175, health: 100, speedMmPerTick: 550, visionRangeMm: 9000,
                    damage: 40, rangeMm: 9000, cooldownTicks: 30, damageType: DamageType.GuidedMissile),
                Infantry(new BlueprintId(103), FactionId.USA,
                    cost: 1500, buildTimeTicks: 300, health: 150, speedMmPerTick: 500, visionRangeMm: 12000,
                    damage: 90, rangeMm: 12000, cooldownTicks: 90),
                Infantry(new BlueprintId(104), FactionId.USA,
                    cost: 1000, buildTimeTicks: 250, health: 200, speedMmPerTick: 550, visionRangeMm: 10000,
                    damage: 95, rangeMm: 8000, cooldownTicks: 30),

                // Vehicles: Humvee, Crusader, Paladin, Avenger, Tomahawk; builder Dozer (174).
                Vehicle(new BlueprintId(105), FactionId.USA,
                    cost: 700, buildTimeTicks: 200, health: 250, damage: 25, rangeMm: 8000, cooldownTicks: 15,
                    speedMmPerTick: 900, visionRangeMm: 10000, lightArmor: true, damageType: DamageType.HeavyMachineGun),
                Vehicle(new BlueprintId(101), FactionId.USA,
                    cost: 900, buildTimeTicks: 400, health: 400, damage: 60, rangeMm: 9000, cooldownTicks: 50,
                    speedMmPerTick: 800, visionRangeMm: 8000),
                Vehicle(new BlueprintId(106), FactionId.USA,
                    cost: 1100, buildTimeTicks: 450, health: 450, damage: 70, rangeMm: 9500, cooldownTicks: 55,
                    speedMmPerTick: 700, visionRangeMm: 9000),
                Vehicle(new BlueprintId(107), FactionId.USA,
                    cost: 1200, buildTimeTicks: 450, health: 350, damage: 60, rangeMm: 12000, cooldownTicks: 40,
                    speedMmPerTick: 750, visionRangeMm: 11000, lightArmor: true, damageType: DamageType.GuidedMissile),
                Vehicle(new BlueprintId(108), FactionId.USA,
                    cost: 1300, buildTimeTicks: 500, health: 300, damage: 110, rangeMm: 14000, cooldownTicks: 120,
                    speedMmPerTick: 550, visionRangeMm: 11000, damageType: DamageType.ArtilleryExplosive),
                Builder(new BlueprintId(174), FactionId.USA,
                    cost: 500, buildTimeTicks: 150, health: 600, speedMmPerTick: 500, visionRangeMm: 8000),

                // Harvester: Chinook.
                Harvester(new BlueprintId(109), FactionId.USA,
                    cost: 1400, buildTimeTicks: 300, health: 400, speedMmPerTick: 700, visionRangeMm: 9000),

                // Aircraft: Comanche, Raptor, Aurora.
                Aircraft(new BlueprintId(120), FactionId.USA,
                    cost: 900, buildTimeTicks: 400, health: 400, damage: 30, rangeMm: 8000, cooldownTicks: 20,
                    speedMmPerTick: 900, visionRangeMm: 11000, damageType: DamageType.HeavyMachineGun),
                Aircraft(new BlueprintId(121), FactionId.USA,
                    cost: 1400, buildTimeTicks: 450, health: 300, damage: 70, rangeMm: 10000, cooldownTicks: 40,
                    speedMmPerTick: 1200, visionRangeMm: 12000),
                Aircraft(new BlueprintId(122), FactionId.USA,
                    cost: 1600, buildTimeTicks: 500, health: 250, damage: 80, rangeMm: 10000, cooldownTicks: 50,
                    speedMmPerTick: 1400, visionRangeMm: 12000, damageType: DamageType.HighTechLaser),

                // Buildings: HQ (110), Reactor, Supply Center, Barracks (113), War Factory (114),
                // Airfield, Patriot, Firebase; secondary 160; superweapon Particle Cannon.
                CommandCenter(new BlueprintId(110), FactionId.USA, powerProduction: 5),
                Building(new BlueprintId(111), FactionId.USA,
                    cost: 800, buildTimeTicks: 300, health: 1500, 4, 4, visionRangeMm: 8000, powerProduction: 8),
                SupplyCenter(new BlueprintId(112), FactionId.USA,
                    cost: 1500, buildTimeTicks: 400, health: 2000, 6, 6, visionRangeMm: 9000),
                Building(new BlueprintId(113), FactionId.USA,
                    cost: 500, buildTimeTicks: 250, health: 1500, 4, 4, visionRangeMm: 8000),
                Building(new BlueprintId(114), FactionId.USA,
                    cost: 2000, buildTimeTicks: 500, health: 2500, 6, 6, visionRangeMm: 8000, powerConsumption: 2),
                Building(new BlueprintId(115), FactionId.USA,
                    cost: 2000, buildTimeTicks: 500, health: 2500, 6, 6, visionRangeMm: 9000, powerConsumption: 2),
                Attacking(new BlueprintId(116), FactionId.USA, BlueprintCategory.Defense,
                    cost: 1000, buildTimeTicks: 300, health: 1200, damage: 70, rangeMm: 12000, cooldownTicks: 60,
                    DamageType.GuidedMissile, ArmorType.HeavyVehicle, 4, 4, powerConsumption: 1, visionRangeMm: 12000),
                Attacking(new BlueprintId(117), FactionId.USA, BlueprintCategory.Defense,
                    cost: 800, buildTimeTicks: 250, health: 1500, damage: 40, rangeMm: 10000, cooldownTicks: 20,
                    DamageType.SmallArms, ArmorType.HeavyVehicle, 4, 4, visionRangeMm: 10000),
                Building(new BlueprintId(160), FactionId.USA,
                    cost: 1200, buildTimeTicks: 300, health: 1500, 4, 4, visionRangeMm: 8000,
                    legacyKind: UnitKinds.DropZone),
                Attacking(new BlueprintId(118), FactionId.USA, BlueprintCategory.Superweapon,
                    cost: 5000, buildTimeTicks: 900, health: 3000, damage: 500, rangeMm: 20000, cooldownTicks: 300,
                    DamageType.HighTechLaser, ArmorType.Fortified, 6, 6, powerConsumption: 10, visionRangeMm: 12000),
            };
        }

        // ------------------------------------------------------------------
        // China (200..299): 25 rows
        // ------------------------------------------------------------------

        private static BlueprintDefinition[] BuildChinaRows()
        {
            return new[]
            {
                // Infantry: Red Guard, Tank Hunter, Black Lotus (hero), Hacker (219).
                Infantry(new BlueprintId(200), FactionId.China,
                    cost: 150, buildTimeTicks: 125, health: 150, speedMmPerTick: 550, visionRangeMm: 9000),
                Infantry(new BlueprintId(202), FactionId.China,
                    cost: 300, buildTimeTicks: 175, health: 100, speedMmPerTick: 550, visionRangeMm: 9000,
                    damage: 65, rangeMm: 9000, cooldownTicks: 40, damageType: DamageType.AntiTankCannon),
                Infantry(new BlueprintId(203), FactionId.China,
                    cost: 1500, buildTimeTicks: 300, health: 150, speedMmPerTick: 500, visionRangeMm: 12000,
                    damage: 95, rangeMm: 10000, cooldownTicks: 60),
                Infantry(new BlueprintId(219), FactionId.China,
                    cost: 600, buildTimeTicks: 200, health: 100, speedMmPerTick: 500, visionRangeMm: 10000,
                    damage: 5, rangeMm: 100, cooldownTicks: 40, legacyKind: UnitKinds.Hacker),

                // Vehicles: Battlemaster, Gatling Tank, Troop Crawler, Overlord, Dragon Tank,
                // Inferno Cannon, Nuke Cannon; harvester Supply Truck (222); builder Dozer (224).
                Vehicle(new BlueprintId(201), FactionId.China,
                    cost: 800, buildTimeTicks: 350, health: 400, damage: 60, rangeMm: 9000, cooldownTicks: 50,
                    speedMmPerTick: 750, visionRangeMm: 8000),
                Vehicle(new BlueprintId(204), FactionId.China,
                    cost: 800, buildTimeTicks: 300, health: 350, damage: 30, rangeMm: 8000, cooldownTicks: 10,
                    speedMmPerTick: 800, visionRangeMm: 10000, lightArmor: true, damageType: DamageType.HeavyMachineGun),
                Vehicle(new BlueprintId(205), FactionId.China,
                    cost: 900, buildTimeTicks: 350, health: 600, damage: 20, rangeMm: 7000, cooldownTicks: 15,
                    speedMmPerTick: 700, visionRangeMm: 9000, damageType: DamageType.HeavyMachineGun),
                Vehicle(new BlueprintId(206), FactionId.China,
                    cost: 2000, buildTimeTicks: 600, health: 1000, damage: 110, rangeMm: 10000, cooldownTicks: 80,
                    speedMmPerTick: 500, visionRangeMm: 10000),
                Vehicle(new BlueprintId(207), FactionId.China,
                    cost: 800, buildTimeTicks: 300, health: 400, damage: 45, rangeMm: 7000, cooldownTicks: 30,
                    speedMmPerTick: 750, visionRangeMm: 9000, lightArmor: true, damageType: DamageType.ThermobaricNapalm),
                Vehicle(new BlueprintId(208), FactionId.China,
                    cost: 1200, buildTimeTicks: 450, health: 400, damage: 90, rangeMm: 11000, cooldownTicks: 80,
                    speedMmPerTick: 600, visionRangeMm: 10000, damageType: DamageType.ThermobaricNapalm),
                Vehicle(new BlueprintId(209), FactionId.China,
                    cost: 1600, buildTimeTicks: 550, health: 400, damage: 150, rangeMm: 14000, cooldownTicks: 150,
                    speedMmPerTick: 550, visionRangeMm: 11000, damageType: DamageType.ArtilleryExplosive),
                Harvester(new BlueprintId(222), FactionId.China,
                    cost: 1000, buildTimeTicks: 250, health: 500, speedMmPerTick: 700, visionRangeMm: 9000),
                Builder(new BlueprintId(224), FactionId.China,
                    cost: 500, buildTimeTicks: 150, health: 600, speedMmPerTick: 500, visionRangeMm: 8000),

                // Aircraft: MiG, Helix.
                Aircraft(new BlueprintId(220), FactionId.China,
                    cost: 1000, buildTimeTicks: 350, health: 300, damage: 60, rangeMm: 9000, cooldownTicks: 35,
                    speedMmPerTick: 1100, visionRangeMm: 11000),
                Aircraft(new BlueprintId(221), FactionId.China,
                    cost: 1500, buildTimeTicks: 500, health: 800, damage: 35, rangeMm: 8000, cooldownTicks: 20,
                    speedMmPerTick: 700, visionRangeMm: 10000, damageType: DamageType.HeavyMachineGun),

                // Buildings: HQ (210), Reactor, Supply Depot, Barracks (213), War Factory (214),
                // Internet Center (215, secondary host), Airfield, Bunker, Propaganda Tower,
                // Gatling Cannon; superweapon Nuclear Missile.
                CommandCenter(new BlueprintId(210), FactionId.China, powerProduction: 0),
                Building(new BlueprintId(211), FactionId.China,
                    cost: 800, buildTimeTicks: 300, health: 1500, 4, 4, visionRangeMm: 8000, powerProduction: 7),
                SupplyCenter(new BlueprintId(212), FactionId.China,
                    cost: 1500, buildTimeTicks: 400, health: 2000, 6, 6, visionRangeMm: 9000),
                Building(new BlueprintId(213), FactionId.China,
                    cost: 500, buildTimeTicks: 250, health: 1500, 4, 4, visionRangeMm: 8000),
                Building(new BlueprintId(214), FactionId.China,
                    cost: 2000, buildTimeTicks: 500, health: 2500, 6, 6, visionRangeMm: 8000, powerConsumption: 2),
                Building(new BlueprintId(215), FactionId.China,
                    cost: 1500, buildTimeTicks: 400, health: 2000, 4, 4, visionRangeMm: 9000,
                    powerConsumption: 1, legacyKind: UnitKinds.InternetCenter),
                Building(new BlueprintId(216), FactionId.China,
                    cost: 2000, buildTimeTicks: 500, health: 2500, 6, 6, visionRangeMm: 9000, powerConsumption: 2),
                Attacking(new BlueprintId(217), FactionId.China, BlueprintCategory.Defense,
                    cost: 600, buildTimeTicks: 200, health: 1500, damage: 35, rangeMm: 9000, cooldownTicks: 15,
                    DamageType.SmallArms, ArmorType.HeavyVehicle, 3, 3, visionRangeMm: 9000),
                Attacking(new BlueprintId(218), FactionId.China, BlueprintCategory.Defense,
                    cost: 700, buildTimeTicks: 250, health: 1200, damage: 15, rangeMm: 10000, cooldownTicks: 30,
                    DamageType.SmallArms, ArmorType.HeavyVehicle, 3, 3, visionRangeMm: 10000),
                Attacking(new BlueprintId(223), FactionId.China, BlueprintCategory.Defense,
                    cost: 800, buildTimeTicks: 250, health: 1300, damage: 35, rangeMm: 9000, cooldownTicks: 12,
                    DamageType.HeavyMachineGun, ArmorType.HeavyVehicle, 3, 3, powerConsumption: 1, visionRangeMm: 10000),
                Attacking(new BlueprintId(226), FactionId.China, BlueprintCategory.Superweapon,
                    cost: 5000, buildTimeTicks: 900, health: 2500, damage: 400, rangeMm: 20000, cooldownTicks: 400,
                    DamageType.ArtilleryExplosive, ArmorType.Fortified, 6, 6, powerConsumption: 8, visionRangeMm: 12000),
            };
        }

        // ------------------------------------------------------------------
        // GLA (300..399): 25 rows — strictly zero power, zero aircraft.
        // ------------------------------------------------------------------

        private static BlueprintDefinition[] BuildGlaRows()
        {
            return new[]
            {
                // Infantry: Rebel, RPG Trooper, Terrorist, Hijacker, Jarmen Kell (hero),
                // builder Worker (373) — the GLA builder is infantry.
                Infantry(new BlueprintId(300), FactionId.GLA,
                    cost: 150, buildTimeTicks: 125, health: 100, speedMmPerTick: 600, visionRangeMm: 9000),
                Infantry(new BlueprintId(302), FactionId.GLA,
                    cost: 250, buildTimeTicks: 150, health: 100, speedMmPerTick: 550, visionRangeMm: 9000,
                    damage: 70, rangeMm: 8500, cooldownTicks: 45, damageType: DamageType.AntiTankCannon),
                Infantry(new BlueprintId(303), FactionId.GLA,
                    cost: 200, buildTimeTicks: 120, health: 80, speedMmPerTick: 650, visionRangeMm: 8000,
                    damage: 120, rangeMm: 1500, cooldownTicks: 60, damageType: DamageType.ThermobaricNapalm),
                Infantry(new BlueprintId(304), FactionId.GLA,
                    cost: 500, buildTimeTicks: 200, health: 120, speedMmPerTick: 550, visionRangeMm: 9000,
                    damage: 10, rangeMm: 500, cooldownTicks: 30),
                Infantry(new BlueprintId(305), FactionId.GLA,
                    cost: 1500, buildTimeTicks: 300, health: 160, speedMmPerTick: 520, visionRangeMm: 12000,
                    damage: 95, rangeMm: 12000, cooldownTicks: 75),
                Builder(new BlueprintId(373), FactionId.GLA,
                    cost: 300, buildTimeTicks: 100, health: 250, speedMmPerTick: 550, visionRangeMm: 8000,
                    isInfantry: true),

                // Vehicles: Technical, Rocket Buggy, Scorpion, Marauder, Quad Cannon,
                // Toxin Tractor, Bomb Truck, Scud Launcher, Battle Bus; harvester Scrap Truck (323).
                Vehicle(new BlueprintId(306), FactionId.GLA,
                    cost: 500, buildTimeTicks: 150, health: 250, damage: 30, rangeMm: 7500, cooldownTicks: 15,
                    speedMmPerTick: 950, visionRangeMm: 9000, lightArmor: true),
                Vehicle(new BlueprintId(307), FactionId.GLA,
                    cost: 600, buildTimeTicks: 200, health: 250, damage: 40, rangeMm: 9000, cooldownTicks: 35,
                    speedMmPerTick: 950, visionRangeMm: 10000, lightArmor: true, damageType: DamageType.GuidedMissile),
                Vehicle(new BlueprintId(301), FactionId.GLA,
                    cost: 650, buildTimeTicks: 300, health: 300, damage: 60, rangeMm: 8500, cooldownTicks: 45,
                    speedMmPerTick: 850, visionRangeMm: 8000, lightArmor: true),
                Vehicle(new BlueprintId(308), FactionId.GLA,
                    cost: 1000, buildTimeTicks: 350, health: 450, damage: 75, rangeMm: 9000, cooldownTicks: 55,
                    speedMmPerTick: 800, visionRangeMm: 9000),
                Vehicle(new BlueprintId(309), FactionId.GLA,
                    cost: 900, buildTimeTicks: 300, health: 350, damage: 30, rangeMm: 8500, cooldownTicks: 10,
                    speedMmPerTick: 850, visionRangeMm: 10000, lightArmor: true, damageType: DamageType.HeavyMachineGun),
                Vehicle(new BlueprintId(319), FactionId.GLA,
                    cost: 900, buildTimeTicks: 300, health: 400, damage: 40, rangeMm: 8500, cooldownTicks: 40,
                    speedMmPerTick: 700, visionRangeMm: 9000, damageType: DamageType.ToxinChemical),
                Vehicle(new BlueprintId(320), FactionId.GLA,
                    cost: 1200, buildTimeTicks: 400, health: 600, damage: 150, rangeMm: 1500, cooldownTicks: 80,
                    speedMmPerTick: 750, visionRangeMm: 9000, damageType: DamageType.ThermobaricNapalm),
                Vehicle(new BlueprintId(321), FactionId.GLA,
                    cost: 1500, buildTimeTicks: 500, health: 350, damage: 130, rangeMm: 14000, cooldownTicks: 140,
                    speedMmPerTick: 550, visionRangeMm: 11000, damageType: DamageType.ArtilleryExplosive),
                Vehicle(new BlueprintId(322), FactionId.GLA,
                    cost: 1000, buildTimeTicks: 350, health: 900, damage: 20, rangeMm: 6000, cooldownTicks: 20,
                    speedMmPerTick: 650, visionRangeMm: 9000, damageType: DamageType.SmallArms),
                Harvester(new BlueprintId(323), FactionId.GLA,
                    cost: 800, buildTimeTicks: 250, health: 450, speedMmPerTick: 700, visionRangeMm: 9000),

                // Buildings: HQ (310), Supply Depot, Barracks (312), Arms Dealer (factory),
                // Black Market (358, secondary), Palace, Tunnel Entrance, Stinger Site,
                // Demo Trap; superweapon Scud Storm. No power rows anywhere.
                CommandCenter(new BlueprintId(310), FactionId.GLA, powerProduction: 0),
                SupplyCenter(new BlueprintId(311), FactionId.GLA,
                    cost: 1300, buildTimeTicks: 350, health: 2000, 6, 6, visionRangeMm: 9000),
                Building(new BlueprintId(312), FactionId.GLA,
                    cost: 400, buildTimeTicks: 220, health: 1500, 4, 4, visionRangeMm: 8000),
                Building(new BlueprintId(313), FactionId.GLA,
                    cost: 1800, buildTimeTicks: 450, health: 2500, 6, 6, visionRangeMm: 8000),
                Building(new BlueprintId(358), FactionId.GLA,
                    cost: 1000, buildTimeTicks: 300, health: 1800, 4, 4, visionRangeMm: 8000,
                    legacyKind: UnitKinds.BlackMarket),
                Building(new BlueprintId(314), FactionId.GLA,
                    cost: 1500, buildTimeTicks: 400, health: 2500, 4, 4, visionRangeMm: 9000),
                Building(new BlueprintId(315), FactionId.GLA,
                    cost: 1000, buildTimeTicks: 300, health: 2500, 4, 4, visionRangeMm: 8000),
                Attacking(new BlueprintId(316), FactionId.GLA, BlueprintCategory.Defense,
                    cost: 900, buildTimeTicks: 300, health: 1200, damage: 55, rangeMm: 10000, cooldownTicks: 50,
                    DamageType.GuidedMissile, ArmorType.HeavyVehicle, 4, 4, visionRangeMm: 11000),
                Attacking(new BlueprintId(317), FactionId.GLA, BlueprintCategory.Defense,
                    cost: 300, buildTimeTicks: 120, health: 500, damage: 200, rangeMm: 100, cooldownTicks: 1,
                    DamageType.ThermobaricNapalm, ArmorType.Fortified, 2, 2, visionRangeMm: 4000),
                Attacking(new BlueprintId(318), FactionId.GLA, BlueprintCategory.Superweapon,
                    cost: 5000, buildTimeTicks: 900, health: 2500, damage: 140, rangeMm: 20000, cooldownTicks: 350,
                    DamageType.ArtilleryExplosive, ArmorType.Fortified, 6, 6, visionRangeMm: 12000),
            };
        }

        // ------------------------------------------------------------------
        // Russia (400..499): 31 rows
        // ------------------------------------------------------------------

        private static BlueprintDefinition[] BuildRussiaRows()
        {
            return new[]
            {
                // Infantry: Conscript, RPG Soldier, Flamethrower, Marksman, Spetsnaz (hero).
                Infantry(new BlueprintId(400), FactionId.Russia,
                    cost: 100, buildTimeTicks: 100, health: 80, speedMmPerTick: 500, visionRangeMm: 8000),
                Infantry(new BlueprintId(402), FactionId.Russia,
                    cost: 280, buildTimeTicks: 150, health: 110, speedMmPerTick: 520, visionRangeMm: 9000,
                    damage: 75, rangeMm: 9000, cooldownTicks: 50, damageType: DamageType.AntiTankCannon),
                Infantry(new BlueprintId(403), FactionId.Russia,
                    cost: 400, buildTimeTicks: 150, health: 130, speedMmPerTick: 520, visionRangeMm: 8000,
                    damage: 50, rangeMm: 5000, cooldownTicks: 30, damageType: DamageType.ThermobaricNapalm),
                Infantry(new BlueprintId(404), FactionId.Russia,
                    cost: 700, buildTimeTicks: 200, health: 120, speedMmPerTick: 500, visionRangeMm: 11000,
                    damage: 85, rangeMm: 11000, cooldownTicks: 80),
                Infantry(new BlueprintId(405), FactionId.Russia,
                    cost: 1500, buildTimeTicks: 300, health: 170, speedMmPerTick: 520, visionRangeMm: 12000,
                    damage: 95, rangeMm: 10500, cooldownTicks: 40),

                // Vehicles: T-90M, T-15, BMPT, TOS-1A, 2S19 Msta, Pantsir-S1, Kurganets, Uran-9;
                // builder ИМР «Медведь» (424); harvester КЗКТ-74281 (434).
                Vehicle(new BlueprintId(401), FactionId.Russia,
                    cost: 1100, buildTimeTicks: 500, health: 600, damage: 75, rangeMm: 9500, cooldownTicks: 60,
                    speedMmPerTick: 700, visionRangeMm: 8000),
                Vehicle(new BlueprintId(406), FactionId.Russia,
                    cost: 1800, buildTimeTicks: 550, health: 900, damage: 100, rangeMm: 10000, cooldownTicks: 65,
                    speedMmPerTick: 650, visionRangeMm: 10000),
                Vehicle(new BlueprintId(407), FactionId.Russia,
                    cost: 1500, buildTimeTicks: 500, health: 650, damage: 60, rangeMm: 9000, cooldownTicks: 40,
                    speedMmPerTick: 700, visionRangeMm: 10000),
                Vehicle(new BlueprintId(408), FactionId.Russia,
                    cost: 1600, buildTimeTicks: 500, health: 450, damage: 130, rangeMm: 12000, cooldownTicks: 130,
                    speedMmPerTick: 600, visionRangeMm: 11000, damageType: DamageType.ThermobaricNapalm),
                Vehicle(new BlueprintId(409), FactionId.Russia,
                    cost: 1500, buildTimeTicks: 500, health: 420, damage: 120, rangeMm: 14000, cooldownTicks: 130,
                    speedMmPerTick: 600, visionRangeMm: 11000, damageType: DamageType.ArtilleryExplosive),
                Vehicle(new BlueprintId(425), FactionId.Russia,
                    cost: 1200, buildTimeTicks: 400, health: 500, damage: 65, rangeMm: 12000, cooldownTicks: 45,
                    speedMmPerTick: 750, visionRangeMm: 11000, lightArmor: true, damageType: DamageType.GuidedMissile),
                Vehicle(new BlueprintId(426), FactionId.Russia,
                    cost: 1100, buildTimeTicks: 400, health: 550, damage: 70, rangeMm: 9000, cooldownTicks: 50,
                    speedMmPerTick: 750, visionRangeMm: 10000),
                Vehicle(new BlueprintId(427), FactionId.Russia,
                    cost: 1300, buildTimeTicks: 450, health: 500, damage: 55, rangeMm: 8000, cooldownTicks: 35,
                    speedMmPerTick: 650, visionRangeMm: 9500),
                Builder(new BlueprintId(424), FactionId.Russia,
                    cost: 800, buildTimeTicks: 250, health: 900, speedMmPerTick: 550, visionRangeMm: 9000),
                Harvester(new BlueprintId(434), FactionId.Russia,
                    cost: 1200, buildTimeTicks: 300, health: 600, speedMmPerTick: 650, visionRangeMm: 9000),

                // Aircraft: Mi-26, Ka-52, Su-25, Su-34.
                Aircraft(new BlueprintId(430), FactionId.Russia,
                    cost: 1400, buildTimeTicks: 450, health: 900, damage: 20, rangeMm: 7000, cooldownTicks: 20,
                    speedMmPerTick: 650, visionRangeMm: 10000, damageType: DamageType.HeavyMachineGun),
                Aircraft(new BlueprintId(431), FactionId.Russia,
                    cost: 1300, buildTimeTicks: 450, health: 450, damage: 65, rangeMm: 9000, cooldownTicks: 40,
                    speedMmPerTick: 900, visionRangeMm: 11000),
                Aircraft(new BlueprintId(432), FactionId.Russia,
                    cost: 1300, buildTimeTicks: 450, health: 400, damage: 90, rangeMm: 9000, cooldownTicks: 60,
                    speedMmPerTick: 1000, visionRangeMm: 11000, damageType: DamageType.ArtilleryExplosive),
                Aircraft(new BlueprintId(433), FactionId.Russia,
                    cost: 1700, buildTimeTicks: 550, health: 400, damage: 85, rangeMm: 10000, cooldownTicks: 45,
                    speedMmPerTick: 1100, visionRangeMm: 12000),

                // Buildings: Command Bunker (410), Reactor, Supply Depot, Barracks (413),
                // War Factory (414), Oil Drilling Rig (415, secondary, power-hungry), Airfield,
                // Radar Station, Industrial Plant, Bastion, Pantsir Battery; superweapon Perimeter.
                CommandCenter(new BlueprintId(410), FactionId.Russia, powerProduction: 3),
                Building(new BlueprintId(411), FactionId.Russia,
                    cost: 900, buildTimeTicks: 350, health: 1600, 4, 4, visionRangeMm: 8000, powerProduction: 7),
                SupplyCenter(new BlueprintId(412), FactionId.Russia,
                    cost: 1500, buildTimeTicks: 400, health: 2200, 6, 6, visionRangeMm: 9000),
                Building(new BlueprintId(413), FactionId.Russia,
                    cost: 500, buildTimeTicks: 250, health: 1600, 4, 4, visionRangeMm: 8000),
                Building(new BlueprintId(414), FactionId.Russia,
                    cost: 2000, buildTimeTicks: 500, health: 2600, 6, 6, visionRangeMm: 8000, powerConsumption: 2),
                Building(new BlueprintId(415), FactionId.Russia,
                    cost: 2000, buildTimeTicks: 500, health: 2500, 5, 5, visionRangeMm: 9000,
                    powerConsumption: 2, legacyKind: UnitKinds.OilRig),
                Building(new BlueprintId(416), FactionId.Russia,
                    cost: 2000, buildTimeTicks: 500, health: 2500, 6, 6, visionRangeMm: 9000, powerConsumption: 2),
                Building(new BlueprintId(417), FactionId.Russia,
                    cost: 800, buildTimeTicks: 250, health: 1000, 3, 3, visionRangeMm: 14000, powerConsumption: 1),
                Building(new BlueprintId(418), FactionId.Russia,
                    cost: 1800, buildTimeTicks: 450, health: 2500, 6, 6, visionRangeMm: 8000, powerConsumption: 2),
                Attacking(new BlueprintId(419), FactionId.Russia, BlueprintCategory.Defense,
                    cost: 1100, buildTimeTicks: 350, health: 1800, damage: 90, rangeMm: 11000, cooldownTicks: 70,
                    DamageType.AntiTankCannon, ArmorType.Fortified, 4, 4, visionRangeMm: 11000),
                Attacking(new BlueprintId(423), FactionId.Russia, BlueprintCategory.Defense,
                    cost: 1200, buildTimeTicks: 350, health: 1400, damage: 75, rangeMm: 12500, cooldownTicks: 50,
                    DamageType.GuidedMissile, ArmorType.HeavyVehicle, 4, 4, powerConsumption: 1, visionRangeMm: 12500),
                Attacking(new BlueprintId(421), FactionId.Russia, BlueprintCategory.Superweapon,
                    cost: 5000, buildTimeTicks: 900, health: 2800, damage: 400, rangeMm: 20000, cooldownTicks: 400,
                    DamageType.ArtilleryExplosive, ArmorType.Fortified, 6, 6, powerConsumption: 8, visionRangeMm: 12000),
            };
        }

        // ------------------------------------------------------------------
        // NATO (500..599): 30 rows
        // ------------------------------------------------------------------

        private static BlueprintDefinition[] BuildNatoRows()
        {
            return new[]
            {
                // Infantry: Marine, Javelin, Sniper, Combat Engineer, Special Forces (hero).
                Infantry(new BlueprintId(500), FactionId.NATO,
                    cost: 200, buildTimeTicks: 150, health: 120, speedMmPerTick: 580, visionRangeMm: 9000,
                    heavyInfantry: true),
                Infantry(new BlueprintId(502), FactionId.NATO,
                    cost: 350, buildTimeTicks: 175, health: 110, speedMmPerTick: 540, visionRangeMm: 10000,
                    damage: 75, rangeMm: 10000, cooldownTicks: 50, damageType: DamageType.GuidedMissile),
                Infantry(new BlueprintId(503), FactionId.NATO,
                    cost: 800, buildTimeTicks: 220, health: 110, speedMmPerTick: 500, visionRangeMm: 12000,
                    damage: 90, rangeMm: 12000, cooldownTicks: 85),
                Infantry(new BlueprintId(504), FactionId.NATO,
                    cost: 450, buildTimeTicks: 150, health: 130, speedMmPerTick: 540, visionRangeMm: 9000,
                    damage: 20, rangeMm: 6000, cooldownTicks: 25),
                Infantry(new BlueprintId(505), FactionId.NATO,
                    cost: 1500, buildTimeTicks: 300, health: 180, speedMmPerTick: 520, visionRangeMm: 12000,
                    damage: 95, rangeMm: 11000, cooldownTicks: 35),

                // Vehicles: Boxer, Leopard 3, PzH 2000, MLRS-E, NASAMS Truck, Recovery Vehicle;
                // builder CEV «Kodiak» (524); harvester Heavy Hauler (534).
                Vehicle(new BlueprintId(506), FactionId.NATO,
                    cost: 900, buildTimeTicks: 350, health: 600, damage: 30, rangeMm: 8000, cooldownTicks: 15,
                    speedMmPerTick: 800, visionRangeMm: 10000, damageType: DamageType.HeavyMachineGun),
                Vehicle(new BlueprintId(501), FactionId.NATO,
                    cost: 1050, buildTimeTicks: 475, health: 500, damage: 70, rangeMm: 9500, cooldownTicks: 55,
                    speedMmPerTick: 820, visionRangeMm: 8000),
                Vehicle(new BlueprintId(507), FactionId.NATO,
                    cost: 1600, buildTimeTicks: 500, health: 450, damage: 125, rangeMm: 14500, cooldownTicks: 130,
                    speedMmPerTick: 600, visionRangeMm: 11000, damageType: DamageType.ArtilleryExplosive),
                Vehicle(new BlueprintId(508), FactionId.NATO,
                    cost: 1500, buildTimeTicks: 500, health: 400, damage: 135, rangeMm: 14000, cooldownTicks: 120,
                    speedMmPerTick: 650, visionRangeMm: 11000, damageType: DamageType.ArtilleryExplosive),
                Vehicle(new BlueprintId(509), FactionId.NATO,
                    cost: 1300, buildTimeTicks: 450, health: 450, damage: 70, rangeMm: 12500, cooldownTicks: 50,
                    speedMmPerTick: 800, visionRangeMm: 11000, lightArmor: true, damageType: DamageType.GuidedMissile),
                Vehicle(new BlueprintId(522), FactionId.NATO,
                    cost: 900, buildTimeTicks: 300, health: 700, damage: 1, rangeMm: 0, cooldownTicks: 1,
                    speedMmPerTick: 650, visionRangeMm: 9000),
                Builder(new BlueprintId(524), FactionId.NATO,
                    cost: 900, buildTimeTicks: 250, health: 950, speedMmPerTick: 580, visionRangeMm: 9000),
                Harvester(new BlueprintId(534), FactionId.NATO,
                    cost: 1300, buildTimeTicks: 300, health: 650, speedMmPerTick: 680, visionRangeMm: 9000),

                // Aircraft: Supply Drone (harvester-kind air), NH-90, Eurofighter, A-400M.
                Aircraft(new BlueprintId(530), FactionId.NATO,
                    cost: 1000, buildTimeTicks: 250, health: 300, damage: 1, rangeMm: 0, cooldownTicks: 1,
                    speedMmPerTick: 800, visionRangeMm: 10000, harvester: true),
                Aircraft(new BlueprintId(531), FactionId.NATO,
                    cost: 1200, buildTimeTicks: 400, health: 550, damage: 25, rangeMm: 7500, cooldownTicks: 20,
                    speedMmPerTick: 800, visionRangeMm: 10500, damageType: DamageType.HeavyMachineGun),
                Aircraft(new BlueprintId(532), FactionId.NATO,
                    cost: 1700, buildTimeTicks: 550, health: 380, damage: 80, rangeMm: 10500, cooldownTicks: 45,
                    speedMmPerTick: 1200, visionRangeMm: 12000),
                Aircraft(new BlueprintId(533), FactionId.NATO,
                    cost: 1500, buildTimeTicks: 500, health: 600, damage: 1, rangeMm: 0, cooldownTicks: 1,
                    speedMmPerTick: 900, visionRangeMm: 11000),

                // Buildings: Coalition HQ (510), Gas Power Station, Logistics Hub (supply),
                // Barracks (513), War Factory (514), Coalition Logistics Hub (515, secondary),
                // Air Base, Satellite Uplink, Sensor Array, Gun Turret, NASAMS Battery;
                // superweapon Kinetic Strike.
                CommandCenter(new BlueprintId(510), FactionId.NATO, powerProduction: 4),
                Building(new BlueprintId(511), FactionId.NATO,
                    cost: 900, buildTimeTicks: 350, health: 1500, 4, 4, visionRangeMm: 8000, powerProduction: 8),
                SupplyCenter(new BlueprintId(512), FactionId.NATO,
                    cost: 1500, buildTimeTicks: 400, health: 2000, 6, 6, visionRangeMm: 9000),
                Building(new BlueprintId(513), FactionId.NATO,
                    cost: 500, buildTimeTicks: 250, health: 1500, 4, 4, visionRangeMm: 8000),
                Building(new BlueprintId(514), FactionId.NATO,
                    cost: 2000, buildTimeTicks: 500, health: 2500, 6, 6, visionRangeMm: 8000, powerConsumption: 2),
                Building(new BlueprintId(515), FactionId.NATO,
                    cost: 1400, buildTimeTicks: 350, health: 1800, 4, 4, visionRangeMm: 9000,
                    powerConsumption: 1, legacyKind: UnitKinds.LogisticsHub),
                Building(new BlueprintId(516), FactionId.NATO,
                    cost: 2000, buildTimeTicks: 500, health: 2500, 6, 6, visionRangeMm: 9000, powerConsumption: 2),
                Building(new BlueprintId(517), FactionId.NATO,
                    cost: 2000, buildTimeTicks: 400, health: 1500, 4, 4, visionRangeMm: 16000, powerConsumption: 2),
                Attacking(new BlueprintId(518), FactionId.NATO, BlueprintCategory.Defense,
                    cost: 700, buildTimeTicks: 250, health: 900, damage: 15, rangeMm: 11000, cooldownTicks: 40,
                    DamageType.SmallArms, ArmorType.HeavyVehicle, 3, 3, powerConsumption: 1, visionRangeMm: 14000),
                Attacking(new BlueprintId(519), FactionId.NATO, BlueprintCategory.Defense,
                    cost: 800, buildTimeTicks: 250, health: 1500, damage: 65, rangeMm: 9000, cooldownTicks: 45,
                    DamageType.AntiTankCannon, ArmorType.Fortified, 3, 3, visionRangeMm: 9000),
                Attacking(new BlueprintId(523), FactionId.NATO, BlueprintCategory.Defense,
                    cost: 1200, buildTimeTicks: 350, health: 1400, damage: 75, rangeMm: 12500, cooldownTicks: 50,
                    DamageType.GuidedMissile, ArmorType.HeavyVehicle, 4, 4, powerConsumption: 1, visionRangeMm: 12500),
                Attacking(new BlueprintId(521), FactionId.NATO, BlueprintCategory.Superweapon,
                    cost: 5000, buildTimeTicks: 900, health: 2800, damage: 450, rangeMm: 20000, cooldownTicks: 350,
                    DamageType.HighTechLaser, ArmorType.Fortified, 6, 6, powerConsumption: 10, visionRangeMm: 12000),
            };
        }

        // ------------------------------------------------------------------
        // Row factories
        // ------------------------------------------------------------------

        /// <summary>
        /// Mobile infantry row: SmallArms fire by default; the damage/range/
        /// cooldown overrides turn a row into a rocket, flame or marksman
        /// specialist. No footprint, no power.
        /// </summary>
        private static BlueprintDefinition Infantry(
            BlueprintId id, byte faction,
            int cost, uint buildTimeTicks, int health, int speedMmPerTick, int visionRangeMm,
            int damage = 25, int rangeMm = 7000, uint cooldownTicks = 10,
            DamageType damageType = DamageType.SmallArms,
            bool heavyInfantry = false,
            byte legacyKind = UnitKinds.Infantry)
        {
            return new BlueprintDefinition(
                id, faction, BlueprintCategory.Infantry, legacyKind,
                cost, buildTimeTicks,
                new CombatStats(
                    health, damage, rangeMm, cooldownTicks,
                    (byte)damageType,
                    (byte)(heavyInfantry ? ArmorType.HeavyInfantry : ArmorType.Infantry)),
                speedMmPerTick, 0, 0, 0, 0, visionRangeMm);
        }

        /// <summary>
        /// Ground vehicle row: AntiTankCannon fire by default against heavy
        /// vehicle armor (or light when <paramref name="lightArmor"/>); the
        /// damage-type override re-arms the row as a gun truck, MLRS or flame
        /// vehicle. No footprint, no power.
        /// </summary>
        private static BlueprintDefinition Vehicle(
            BlueprintId id, byte faction,
            int cost, uint buildTimeTicks, int health, int damage, int rangeMm, uint cooldownTicks,
            int speedMmPerTick, int visionRangeMm,
            bool lightArmor = false,
            DamageType damageType = DamageType.AntiTankCannon)
        {
            return new BlueprintDefinition(
                id, faction, BlueprintCategory.Vehicle, UnitKinds.Tank,
                cost, buildTimeTicks,
                new CombatStats(
                    health, damage, rangeMm, cooldownTicks,
                    (byte)damageType,
                    (byte)(lightArmor ? ArmorType.LightVehicle : ArmorType.HeavyVehicle)),
                speedMmPerTick, 0, 0, 0, 0, visionRangeMm);
        }

        /// <summary>
        /// Aircraft row: armed by default with guided missiles against Aircraft
        /// armor. Aircraft have no legacy kind of their own — the presentation
        /// bridge maps them to the mobile-armed <c>Tank</c> slot until the
        /// kind space grows.
        /// </summary>
        private static BlueprintDefinition Aircraft(
            BlueprintId id, byte faction,
            int cost, uint buildTimeTicks, int health, int damage, int rangeMm, uint cooldownTicks,
            int speedMmPerTick, int visionRangeMm,
            DamageType damageType = DamageType.GuidedMissile,
            bool harvester = false)
        {
            return new BlueprintDefinition(
                id, faction, BlueprintCategory.Aircraft, harvester ? UnitKinds.Harvester : UnitKinds.Tank,
                cost, buildTimeTicks,
                new CombatStats(
                    health, damage, rangeMm, cooldownTicks,
                    (byte)damageType, (byte)ArmorType.Aircraft),
                speedMmPerTick, 0, 0, 0, 0, visionRangeMm);
        }

        /// <summary>
        /// Economy truck row (harvester): the legacy inert stat line, vehicle
        /// mobility, <see cref="UnitKinds.Harvester"/> auto-registration.
        /// </summary>
        private static BlueprintDefinition Harvester(
            BlueprintId id, byte faction,
            int cost, uint buildTimeTicks, int health, int speedMmPerTick, int visionRangeMm)
        {
            return new BlueprintDefinition(
                id, faction, BlueprintCategory.Vehicle, UnitKinds.Harvester,
                cost, buildTimeTicks, new CombatStats(health, 1, 0, 1),
                speedMmPerTick, 0, 0, 0, 0, visionRangeMm);
        }

        /// <summary>
        /// Physical builder row (Step 5.5.3): a mobile engineering unit every
        /// faction must field — a Vehicle, or Infantry for GLA. The legacy kind
        /// is the mobile-armed slot (Tank), or Infantry for the Worker.
        /// </summary>
        private static BlueprintDefinition Builder(
            BlueprintId id, byte faction,
            int cost, uint buildTimeTicks, int health, int speedMmPerTick, int visionRangeMm,
            bool isInfantry = false)
        {
            return new BlueprintDefinition(
                id, faction,
                isInfantry ? BlueprintCategory.Infantry : BlueprintCategory.Vehicle,
                isInfantry ? UnitKinds.Infantry : UnitKinds.Tank,
                cost, buildTimeTicks, new CombatStats(health, 1, 0, 1),
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

        /// <summary>
        /// Non-attacking structure row: legacy inert stat line, a footprint on
        /// the 500 mm grid, and the row's power balance. The legacy kind routes
        /// the economy: SupplyCenter, DropZone, BlackMarket, OilRig,
        /// InternetCenter, LogisticsHub — everything else BaseStructure.
        /// </summary>
        private static BlueprintDefinition Building(
            BlueprintId id, byte faction,
            int cost, uint buildTimeTicks, int health,
            int footprintWidthCells, int footprintHeightCells, int visionRangeMm,
            int powerProduction = 0, int powerConsumption = 0,
            byte legacyKind = UnitKinds.BaseStructure)
        {
            return new BlueprintDefinition(
                id, faction, BlueprintCategory.Building, legacyKind,
                cost, buildTimeTicks, new CombatStats(health, 1, 0, 1),
                speedMmPerTick: 0, footprintWidthCells: footprintWidthCells,
                footprintHeightCells: footprintHeightCells,
                powerProduction: powerProduction, powerConsumption: powerConsumption,
                visionRangeMm: visionRangeMm);
        }

        /// <summary>Supply-center row: the drop-off point a faction's economy unloads at.</summary>
        private static BlueprintDefinition SupplyCenter(
            BlueprintId id, byte faction,
            int cost, uint buildTimeTicks, int health,
            int footprintWidthCells, int footprintHeightCells, int visionRangeMm)
        {
            return new BlueprintDefinition(
                id, faction, BlueprintCategory.Supply, UnitKinds.SupplyCenter,
                cost, buildTimeTicks, new CombatStats(health, 1, 0, 1),
                speedMmPerTick: 0, footprintWidthCells: footprintWidthCells,
                footprintHeightCells: footprintHeightCells,
                powerProduction: 0, powerConsumption: 0, visionRangeMm: visionRangeMm);
        }

        /// <summary>
        /// Attacking structure row (Defense turrets and Superweapons): real
        /// matrix-typed stats on the structural armor, a footprint, and the
        /// row's power balance (always zero for GLA).
        /// </summary>
        private static BlueprintDefinition Attacking(
            BlueprintId id, byte faction, BlueprintCategory category,
            int cost, uint buildTimeTicks, int health, int damage, int rangeMm, uint cooldownTicks,
            DamageType damageType, ArmorType armorType,
            int footprintWidthCells, int footprintHeightCells,
            int powerProduction = 0, int powerConsumption = 0, int visionRangeMm = 9000)
        {
            return new BlueprintDefinition(
                id, faction, category, UnitKinds.BaseStructure,
                cost, buildTimeTicks,
                new CombatStats(
                    health, damage, rangeMm, cooldownTicks,
                    (byte)damageType, (byte)armorType),
                speedMmPerTick: 0, footprintWidthCells: footprintWidthCells,
                footprintHeightCells: footprintHeightCells,
                powerProduction: powerProduction, powerConsumption: powerConsumption,
                visionRangeMm: visionRangeMm);
        }
    }
}
