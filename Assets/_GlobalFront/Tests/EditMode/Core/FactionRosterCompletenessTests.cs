using System.Collections.Generic;
using GlobalFront.Core.Catalog;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using NUnit.Framework;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Core
{
    /// <summary>
    /// Step 5.5.3: the roster completeness gate. The approved five-faction
    /// roster must ship complete — every faction fields its HQ, a physical
    /// builder, the supply/economy chain, an AA structure, a secondary-income
    /// building, a superweapon and a hero, with the power grid and aviation
    /// rules holding per doctrine (four grid factions, GLA strictly none of
    /// either) — and a catalog that breaks any of that fails loudly here.
    /// </summary>
    [TestFixture]
    public sealed class FactionRosterCompletenessTests
    {
        [Test]
        public void Validate_DefaultCatalog_PassesAllFiveFactions()
        {
            Assert.That(
                FactionRosterCompletenessValidator.Validate(DefaultBlueprintCatalog.Create(), out var error),
                Is.True, error ?? "the approved 1.0 roster must pass its own validator");
        }

        [Test]
        public void Validate_RussiaAndNatoHavePhysicalBuilders_Passes()
        {
            var catalog = DefaultBlueprintCatalog.Create();

            Assert.That(catalog.TryGet(new BlueprintId(424), out var imr), Is.True,
                "Russia's ИМР «Медведь» rides the canonical builder id");
            Assert.That(imr.Category, Is.EqualTo(BlueprintCategory.Vehicle),
                "the ИМР is an armoured engineering vehicle, not a building");
            Assert.That(imr.SpeedMmPerTick, Is.GreaterThan(0));

            Assert.That(catalog.TryGet(new BlueprintId(524), out var cev), Is.True,
                "NATO's CEV «Kodiak» rides the canonical builder id");
            Assert.That(cev.Category, Is.EqualTo(BlueprintCategory.Vehicle));
            Assert.That(cev.SpeedMmPerTick, Is.GreaterThan(0));

            Assert.That(
                FactionRosterCompletenessValidator.Validate(catalog, out var error), Is.True, error ?? "");
        }

        [Test]
        public void Validate_MissingBuilder_Fails()
        {
            Assert.That(
                FactionRosterCompletenessValidator.Validate(
                    CatalogWithout(524), out var error),
                Is.False, "a NATO roster without the CEV builder must be refused");
            Assert.That(error, Does.Contain("builder"));
        }

        [Test]
        public void Validate_GlaHasAircraft_Fails()
        {
            var aircraft = new BlueprintDefinition(
                new BlueprintId(350), FactionId.GLA, BlueprintCategory.Aircraft, UnitKinds.Tank,
                cost: 800, buildTimeTicks: 300,
                stats: new CombatStats(300, 40, 8000, 30, (byte)DamageType.GuidedMissile, (byte)ArmorType.Aircraft),
                speedMmPerTick: 800, footprintWidthCells: 0, footprintHeightCells: 0,
                powerProduction: 0, powerConsumption: 0, visionRangeMm: 9000);

            Assert.That(
                FactionRosterCompletenessValidator.Validate(
                    CatalogWith(aircraft), out var error),
                Is.False, "GLA doctrine forbids aircraft outright");
            Assert.That(error, Does.Contain("aircraft"));
        }

        [Test]
        public void Validate_GlaHasPowerConsumption_Fails()
        {
            var poweredBuilding = new BlueprintDefinition(
                new BlueprintId(351), FactionId.GLA, BlueprintCategory.Building, UnitKinds.BaseStructure,
                cost: 800, buildTimeTicks: 300, stats: new CombatStats(1500, 1, 0, 1),
                speedMmPerTick: 0, footprintWidthCells: 4, footprintHeightCells: 4,
                powerProduction: 0, powerConsumption: 3, visionRangeMm: 8000);

            Assert.That(
                FactionRosterCompletenessValidator.Validate(
                    CatalogWith(poweredBuilding), out var error),
                Is.False, "GLA doctrine runs no power grid at all");
            Assert.That(error, Does.Contain("power"));
        }

        // ------------------------------------------------------------------
        // Catalog surgery helpers
        // ------------------------------------------------------------------

        private static BlueprintCatalog CatalogWithout(ushort removedId)
        {
            var catalog = DefaultBlueprintCatalog.Create();
            var definitions = new List<BlueprintDefinition>(catalog.Count - 1);
            var ids = new ushort[catalog.Count];
            Assert.That(catalog.CopyBlueprintIds(ids), Is.EqualTo(catalog.Count));

            for (var index = 0; index < ids.Length; index++)
            {
                if (ids[index] != removedId)
                {
                    definitions.Add(Resolve(catalog, ids[index]));
                }
            }

            return new BlueprintCatalog(definitions.ToArray());
        }

        private static BlueprintCatalog CatalogWith(BlueprintDefinition extra)
        {
            var catalog = DefaultBlueprintCatalog.Create();
            var definitions = new List<BlueprintDefinition>(catalog.Count + 1);
            var ids = new ushort[catalog.Count];
            Assert.That(catalog.CopyBlueprintIds(ids), Is.EqualTo(catalog.Count));

            for (var index = 0; index < ids.Length; index++)
            {
                definitions.Add(Resolve(catalog, ids[index]));
            }

            definitions.Add(extra);
            return new BlueprintCatalog(definitions.ToArray());
        }

        private static BlueprintDefinition Resolve(IBlueprintCatalog catalog, ushort id)
        {
            Assert.That(catalog.TryGet(id, out var definition), Is.True, $"row {id}");
            return definition;
        }
    }
}
