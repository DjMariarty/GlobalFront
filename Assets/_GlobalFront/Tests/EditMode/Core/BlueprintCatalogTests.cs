using System;
using GlobalFront.Core.Catalog;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Core
{
    /// <summary>
    /// Step 5.5.1: the authoritative blueprint registry, the Core-side table
    /// that turns a <see cref="BlueprintId"/> into the combat line, price,
    /// build time and construction geometry the five-faction roster needs.
    ///
    /// The contract under test mirrors the client catalog's discipline at the
    /// ushort scale: a mistake must surface as an exception while the table is
    /// built (a stored-broken row would read back as truth everywhere the
    /// lookup is trusted), a miss must never be guessed (an empty slot resolves
    /// to None, not to the first real row), and the lookup itself is O(1) and
    /// allocation-free because production and presentation call it per unit.
    /// </summary>
    [TestFixture]
    public sealed class BlueprintCatalogTests
    {
        private const int MeasuredIterations = 2000;

        private static readonly BlueprintCatalog LookupTable =
            new BlueprintCatalog(new[] { VehicleRow(100), VehicleRow(101), BuildingRow(110) });

        [Test]
        public void TryGet_ExistingBlueprint_ReturnsTrueAndCorrectDefinition()
        {
            Assert.That(LookupTable.TryGet(new BlueprintId(101), out var definition), Is.True);
            Assert.That(definition.Id, Is.EqualTo(new BlueprintId(101)), "row indexed by its own id");
            Assert.That(definition.Faction, Is.EqualTo(FactionId.USA));
            Assert.That(definition.Category, Is.EqualTo(BlueprintCategory.Vehicle));
            Assert.That(definition.LegacyKind, Is.EqualTo(UnitKinds.Tank));
            Assert.That(definition.Cost, Is.EqualTo(900));
            Assert.That(definition.BuildTimeTicks, Is.EqualTo(400u));
            Assert.That(definition.Stats.MaximumHealth, Is.EqualTo(400));
            Assert.That(definition.Stats.DamageType, Is.EqualTo((byte)DamageType.AntiTankCannon));
            Assert.That(definition.Stats.ArmorType, Is.EqualTo((byte)ArmorType.HeavyVehicle));
            Assert.That(definition.SpeedMmPerTick, Is.EqualTo(800));
            Assert.That(definition.VisionRangeMm, Is.EqualTo(8000));
            Assert.That(definition.IsResolved, Is.True);

            // The raw-ushort overload is the bridge for wire-era call sites and
            // must resolve exactly the row the typed id resolves.
            Assert.That(LookupTable.TryGet((ushort)101, out var rawIdDefinition), Is.True);
            Assert.That(rawIdDefinition.Id, Is.EqualTo(definition.Id));
        }

        [Test]
        public void TryGet_NonExistingBlueprint_ReturnsFalseAndZeroedDefinition()
        {
            // Beyond the table's ceiling, a hole between rows, and None itself:
            // every miss shape must come back unresolved instead of silently
            // claiming a neighbouring row.
            Assert.That(LookupTable.TryGet(new BlueprintId(600), out var beyondCeiling), Is.False,
                "an id past the highest row is a miss");
            Assert.That(beyondCeiling.Id.IsValid, Is.False);
            Assert.That(beyondCeiling.IsResolved, Is.False);

            Assert.That(LookupTable.TryGet(new BlueprintId(105), out var hole), Is.False,
                "an id inside the table but without a row is a miss");
            Assert.That(hole.Id.IsValid, Is.False);

            Assert.That(LookupTable.TryGet(BlueprintId.None, out _), Is.False,
                "None is never a hit");

            Assert.That(LookupTable.TryGet((ushort)105, out _), Is.False,
                "the raw-id overload shares the miss discipline");
        }

        [Test]
        public void Constructor_DuplicateBlueprintId_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(
                () => new BlueprintCatalog(new[] { VehicleRow(100), VehicleRow(100) }),
                "two rows sharing an id would make one of them unreachable");
        }

        [Test]
        public void Constructor_ZeroHealth_ThrowsArgumentException()
        {
            // The stats line is validated by construction: CombatStats refuses
            // an impossible line before the catalog ever sees it, so the load
            // gate fires one layer earlier (ArgumentOutOfRangeException IS an
            // ArgumentException). The catalog re-checks the health pool as
            // belt-and-braces — unreachable through normal construction, but it
            // keeps the contract local if the struct ever relaxes.
            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => new CombatStats(0, 60, 9000, 50));
            Assert.That(error.ParamName, Is.EqualTo("maximumHealth"));
        }

        [Test]
        public void Constructor_BuildingWithZeroFootprint_ThrowsArgumentException()
        {
            BlueprintDefinition row = BuildingRow(120, 0, 0);
            Assert.Throws<ArgumentException>(
                () => new BlueprintCatalog(new[] { row }),
                "a construction row without a footprint cannot be placed on the 500 mm grid");
        }

        [Test]
        public void Constructor_VehicleWithZeroSpeed_ThrowsArgumentException()
        {
            BlueprintDefinition row = new BlueprintDefinition(
                new BlueprintId(101), FactionId.USA, BlueprintCategory.Vehicle, UnitKinds.Tank,
                cost: 900, buildTimeTicks: 400,
                stats: new CombatStats(400, 60, 9000, 50),
                speedMmPerTick: 0, footprintWidthCells: 0, footprintHeightCells: 0,
                powerProduction: 0, powerConsumption: 0, visionRangeMm: 8000);
            Assert.Throws<ArgumentException>(
                () => new BlueprintCatalog(new[] { row }),
                "a vehicle that cannot move is a data typo, not a turret");
        }

        [Test]
        public void DeduceFaction_CorrectlyIdentifiesFactionsByRange()
        {
            (ushort id, byte faction)[] cases =
            {
                (0, FactionId.None),
                (1, FactionId.None),
                (99, FactionId.None),
                (100, FactionId.USA),
                (199, FactionId.USA),
                (200, FactionId.China),
                (299, FactionId.China),
                (300, FactionId.GLA),
                (399, FactionId.GLA),
                (400, FactionId.Russia),
                (499, FactionId.Russia),
                (500, FactionId.NATO),
                (599, FactionId.NATO),
                (600, FactionId.None),
                (65535, FactionId.None),
            };

            for (var index = 0; index < cases.Length; index++)
            {
                Assert.That(
                    new BlueprintId(cases[index].id).DeduceFaction(),
                    Is.EqualTo(cases[index].faction),
                    $"id {cases[index].id} must deduce to its canonical zone");
            }
        }

        [Test]
        public void Lookup_IsAllocationFree()
        {
            // The recorder must be proven live before it can prove absence.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");
            Assert.That(ballast, Is.Not.Null);

            // Warmup: JIT the lookup path.
            Drive(200);

            // Drive now returns a hit count, so the lambda must be pinned to a
            // TestDelegate — the generic Assert.That overload would accept a
            // Func<int> actual and reject it at runtime.
            TestDelegate measured = () => Drive(MeasuredIterations);
            Assert.That(
                measured,
                GcAssert.Not.AllocatingGCMemory(),
                "TryGet is the per-unit hot path of production and presentation and must be allocation-free");

            // The allocation gate cannot see index corruption: a lookup that
            // reads the wrong slot is still allocation-free. Every seeded
            // lookup must hit, and the miss shape must stay a miss.
            Assert.That(Drive(MeasuredIterations), Is.EqualTo(MeasuredIterations * 3),
                "every seeded lookup must resolve its own row");
            Assert.That(LookupTable.TryGet(new BlueprintId(550), out _), Is.False,
                "a hole between rows stays a miss");
        }

        /// <summary>Returns the number of lookups that hit a real row.</summary>
        private static int Drive(int iterations)
        {
            var hits = 0;
            for (var index = 0; index < iterations; index++)
            {
                if (LookupTable.TryGet(new BlueprintId(101), out _))
                {
                    hits++;
                }

                if (LookupTable.TryGet((ushort)110, out _))
                {
                    hits++;
                }

                if (LookupTable.TryGet(new BlueprintId(100), out _))
                {
                    hits++;
                }
            }

            return hits;
        }

        [Test]
        public void Constructor_NullDefinitions_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new BlueprintCatalog(null));
        }

        [Test]
        public void Constructor_NoneBlueprintId_ThrowsArgumentException()
        {
            BlueprintDefinition row = new BlueprintDefinition(
                BlueprintId.None, FactionId.USA, BlueprintCategory.Vehicle, UnitKinds.Tank,
                cost: 900, buildTimeTicks: 400,
                stats: new CombatStats(400, 60, 9000, 50),
                speedMmPerTick: 800, footprintWidthCells: 0, footprintHeightCells: 0,
                powerProduction: 0, powerConsumption: 0, visionRangeMm: 8000);
            Assert.Throws<ArgumentException>(
                () => new BlueprintCatalog(new[] { row }),
                "a lookup miss is not an id; None must never enter the table");
        }

        [Test]
        public void Constructor_InvalidFaction_ThrowsArgumentException()
        {
            BlueprintDefinition row = VehicleRow(100, 6);
            Assert.Throws<ArgumentException>(
                () => new BlueprintCatalog(new[] { row }),
                "faction 6 is FactionId.Count, not a doctrine");
        }

        [Test]
        public void DefaultBlueprintCatalog_CoversNeutralsAndAllFiveFactions()
        {
            BlueprintCatalog catalog = DefaultBlueprintCatalog.Create();

            (ushort id, byte faction, BlueprintCategory category, byte legacyKind)[] rows =
            {
                (1, FactionId.None, BlueprintCategory.Building, UnitKinds.BaseStructure),
                (2, FactionId.None, BlueprintCategory.Supply, UnitKinds.BaseStructure),
                (100, FactionId.USA, BlueprintCategory.Infantry, UnitKinds.Infantry),
                (101, FactionId.USA, BlueprintCategory.Vehicle, UnitKinds.Tank),
                (110, FactionId.USA, BlueprintCategory.Building, UnitKinds.BaseStructure),
                (200, FactionId.China, BlueprintCategory.Infantry, UnitKinds.Infantry),
                (201, FactionId.China, BlueprintCategory.Vehicle, UnitKinds.Tank),
                (210, FactionId.China, BlueprintCategory.Building, UnitKinds.BaseStructure),
                (300, FactionId.GLA, BlueprintCategory.Infantry, UnitKinds.Infantry),
                (301, FactionId.GLA, BlueprintCategory.Vehicle, UnitKinds.Tank),
                (310, FactionId.GLA, BlueprintCategory.Building, UnitKinds.BaseStructure),
                (400, FactionId.Russia, BlueprintCategory.Infantry, UnitKinds.Infantry),
                (401, FactionId.Russia, BlueprintCategory.Vehicle, UnitKinds.Tank),
                (410, FactionId.Russia, BlueprintCategory.Building, UnitKinds.BaseStructure),
                (500, FactionId.NATO, BlueprintCategory.Infantry, UnitKinds.Infantry),
                (501, FactionId.NATO, BlueprintCategory.Vehicle, UnitKinds.Tank),
                (510, FactionId.NATO, BlueprintCategory.Building, UnitKinds.BaseStructure),
            };

            for (var index = 0; index < rows.Length; index++)
            {
                Assert.That(
                    catalog.TryGet(rows[index].id, out var definition), Is.True,
                    $"starter row {rows[index].id} must resolve");
                Assert.That(definition.IsResolved, Is.True, $"row {rows[index].id} must be usable");
                Assert.That(definition.Faction, Is.EqualTo(rows[index].faction), $"row {rows[index].id} faction");
                Assert.That(definition.Category, Is.EqualTo(rows[index].category), $"row {rows[index].id} category");
                Assert.That(definition.LegacyKind, Is.EqualTo(rows[index].legacyKind), $"row {rows[index].id} legacy kind");

                if (rows[index].faction == FactionId.None)
                {
                    Assert.That(definition.Id.DeduceFaction(), Is.EqualTo(FactionId.None),
                        "neutral ids deduce to no faction");
                }
                else
                {
                    Assert.That(definition.Id.DeduceFaction(), Is.EqualTo(definition.Faction),
                        $"row {rows[index].id}: the id zone and the row must agree on the faction");
                }
            }

            // Costs and speeds the Step 5.5.1 notes fix verbatim.
            Assert.That(Get(catalog, 100).Cost, Is.EqualTo(225), "Ranger");
            Assert.That(Get(catalog, 101).Cost, Is.EqualTo(900), "Crusader");
            Assert.That(Get(catalog, 200).Cost, Is.EqualTo(150), "Red Guard");
            Assert.That(Get(catalog, 201).Cost, Is.EqualTo(800), "Battlemaster");
            Assert.That(Get(catalog, 300).Cost, Is.EqualTo(150), "Rebel");
            Assert.That(Get(catalog, 301).Cost, Is.EqualTo(650), "Scorpion");
            Assert.That(Get(catalog, 400).Cost, Is.EqualTo(100), "Conscript");
            Assert.That(Get(catalog, 401).Cost, Is.EqualTo(1100), "T-90");
            Assert.That(Get(catalog, 500).Cost, Is.EqualTo(200), "Peacekeeper");
            Assert.That(Get(catalog, 501).Cost, Is.EqualTo(1050), "Leopard 2");

            Assert.That(Get(catalog, 100).SpeedMmPerTick, Is.EqualTo(600), "Ranger");
            Assert.That(Get(catalog, 101).SpeedMmPerTick, Is.EqualTo(800), "Crusader");
            Assert.That(Get(catalog, 200).SpeedMmPerTick, Is.EqualTo(550), "Red Guard");
            Assert.That(Get(catalog, 201).SpeedMmPerTick, Is.EqualTo(750), "Battlemaster");
            Assert.That(Get(catalog, 300).SpeedMmPerTick, Is.EqualTo(600), "Rebel");
            Assert.That(Get(catalog, 301).SpeedMmPerTick, Is.EqualTo(850), "Scorpion");
            Assert.That(Get(catalog, 400).SpeedMmPerTick, Is.EqualTo(500), "Conscript");
            Assert.That(Get(catalog, 401).SpeedMmPerTick, Is.EqualTo(700), "T-90");
            Assert.That(Get(catalog, 500).SpeedMmPerTick, Is.EqualTo(580), "Peacekeeper");
            Assert.That(Get(catalog, 501).SpeedMmPerTick, Is.EqualTo(820), "Leopard 2");

            // Construction geometry on the 500 mm grid, and the power deltas.
            Assert.That(Get(catalog, 1).FootprintWidthCells, Is.EqualTo(4), "Oil Derrick width");
            Assert.That(Get(catalog, 1).FootprintHeightCells, Is.EqualTo(4), "Oil Derrick height");
            Assert.That(Get(catalog, 2).FootprintWidthCells, Is.EqualTo(6), "Supply Dock width");
            Assert.That(Get(catalog, 2).FootprintHeightCells, Is.EqualTo(6), "Supply Dock height");
            Assert.That(Get(catalog, 110).PowerProduction, Is.EqualTo(5), "USA command center");
            Assert.That(Get(catalog, 210).PowerProduction, Is.EqualTo(0), "China command center");
            Assert.That(Get(catalog, 310).PowerProduction, Is.EqualTo(0), "GLA command center (zero-power rule)");
            Assert.That(Get(catalog, 410).PowerProduction, Is.EqualTo(3), "Russia command center");
            Assert.That(Get(catalog, 510).PowerProduction, Is.EqualTo(4), "NATO command center");
            for (var index = 110; index <= 510; index += 100)
            {
                Assert.That(Get(catalog, (ushort)index).FootprintWidthCells, Is.EqualTo(8),
                    $"command center {index} width");
                Assert.That(Get(catalog, (ushort)index).FootprintHeightCells, Is.EqualTo(8),
                    $"command center {index} height");
                Assert.That(Get(catalog, (ushort)index).SpeedMmPerTick, Is.EqualTo(0),
                    $"command center {index} does not move");
            }

            // Matrix typing: the notes' infantry kit and the light Scorpion.
            Assert.That(Get(catalog, 500).Stats.ArmorType, Is.EqualTo((byte)ArmorType.HeavyInfantry),
                "Peacekeeper's modern kevlar is heavy infantry");
            Assert.That(Get(catalog, 301).Stats.ArmorType, Is.EqualTo((byte)ArmorType.LightVehicle),
                "the Scorpion stays light armor");
            Assert.That(Get(catalog, 401).Stats.DamageType, Is.EqualTo((byte)DamageType.AntiTankCannon),
                "the T-90's heavy cannon resolves to the matrix's anti-tank row");

            BlueprintDefinition Get(BlueprintCatalog source, ushort id)
            {
                Assert.That(source.TryGet(id, out var definition), Is.True, $"row {id}");
                return definition;
            }
        }

        [Test]
        public void BlueprintId_ValueSemantics()
        {
            var hundred = new BlueprintId(100);
            var hundredAgain = new BlueprintId(100);
            var threeHundred = new BlueprintId(300);

            Assert.That(hundred.Equals(hundredAgain), Is.True);
            Assert.That(hundred.Equals(threeHundred), Is.False);
            Assert.That(hundred.GetHashCode(), Is.EqualTo(hundredAgain.GetHashCode()),
                "equal ids hash equally");
            Assert.That(hundred == hundredAgain, Is.True);
            Assert.That(hundred != threeHundred, Is.True);
            Assert.That(hundred.CompareTo(hundredAgain), Is.EqualTo(0));
            Assert.That(threeHundred.CompareTo(hundred), Is.GreaterThan(0));
            Assert.That(hundred.CompareTo(threeHundred), Is.LessThan(0));

            Assert.That(hundred.IsValid, Is.True);
            Assert.That(BlueprintId.None.IsValid, Is.False);
            Assert.That(hundred.ToString(), Is.EqualTo("Blueprint 100"));
            Assert.That(BlueprintId.None.ToString(), Is.EqualTo("Invalid"));

            BlueprintId[] ids = { new BlueprintId(500), new BlueprintId(100), new BlueprintId(300) };
            Array.Sort(ids);
            Assert.That(ids[0].Value, Is.EqualTo(100), "IComparable orders by the raw value");
            Assert.That(ids[2].Value, Is.EqualTo(500));
        }

        [Test]
        public void FactionId_IsValid_Boundaries()
        {
            Assert.That(FactionId.IsValid(FactionId.None), Is.False,
                "None marks neutral world objects, not a doctrine");
            for (byte faction = 1; faction < FactionId.Count; faction++)
            {
                Assert.That(FactionId.IsValid(faction), Is.True, $"faction {faction} is a real doctrine");
            }

            Assert.That(FactionId.IsValid(FactionId.Count), Is.False, "Count is the ceiling, not a faction");
            Assert.That(FactionId.IsValid(255), Is.False, "stray bytes stay invalid");
        }

        /// <summary>Canonical mobile row for ctor-rejection tests.</summary>
        private static BlueprintDefinition VehicleRow(ushort id, byte faction = FactionId.USA)
        {
            return new BlueprintDefinition(
                new BlueprintId(id), faction, BlueprintCategory.Vehicle, UnitKinds.Tank,
                cost: 900, buildTimeTicks: 400,
                stats: new CombatStats(400, 60, 9000, 50, (byte)DamageType.AntiTankCannon, (byte)ArmorType.HeavyVehicle),
                speedMmPerTick: 800, footprintWidthCells: 0, footprintHeightCells: 0,
                powerProduction: 0, powerConsumption: 0, visionRangeMm: 8000);
        }

        /// <summary>Canonical construction row for ctor-rejection tests.</summary>
        private static BlueprintDefinition BuildingRow(ushort id, int footprintWidthCells = 8, int footprintHeightCells = 8)
        {
            return new BlueprintDefinition(
                new BlueprintId(id), FactionId.USA, BlueprintCategory.Building, UnitKinds.BaseStructure,
                cost: 2000, buildTimeTicks: 600,
                stats: new CombatStats(4000, 1, 0, 1),
                speedMmPerTick: 0, footprintWidthCells: footprintWidthCells, footprintHeightCells: footprintHeightCells,
                powerProduction: 5, powerConsumption: 0, visionRangeMm: 12000);
        }
    }
}
