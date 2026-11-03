using GlobalFront.Core.Combat;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Step 5.1.1 — the integer damage-type x armor-type matrix and the canonical resolver
    /// (ADR-015 / OD-51). The matrix is the balance contract of Phase 5, so every cell is
    /// pinned against the table documented in
    /// Documentation/Research/Phase_05_Five_Factions_And_Economy_RND.md §4.2, and the rounding
    /// rule is pinned by cases that separate round-half-up from truncation.
    /// </summary>
    [TestFixture]
    public sealed class DamageMatrixTests
    {
        private const int MeasuredHits = 1000;

        /// <summary>
        /// The documented table, row-major: index = (int)DamageType * 7 + (int)ArmorType.
        /// Columns: Infantry, HeavyInfantry, LightVehicle, HeavyVehicle, Aircraft, Structure,
        /// Fortified.
        /// </summary>
        private static readonly short[] DocumentedMultipliers =
        {
            // SmallArms
            1000, 700, 500, 250, 500, 300, 100,
            // HeavyMachineGun
            700, 1000, 800, 300, 700, 400, 100,
            // AntiTankCannon
            400, 600, 1000, 1000, 200, 800, 600,
            // GuidedMissile
            500, 500, 1000, 900, 1000, 600, 300,
            // ArtilleryExplosive
            1500, 1200, 800, 700, 0, 1200, 1000,
            // ThermobaricNapalm
            1500, 1400, 600, 400, 0, 800, 200,
            // ToxinChemical
            2500, 1800, 400, 150, 0, 300, 150,
            // HighTechLaser
            1000, 1000, 800, 700, 1000, 600, 400
        };

        // ------------------------------------------------------------------
        // 1 — enum shape and the table itself
        // ------------------------------------------------------------------

        [Test]
        public void DamageTypes_And_ArmorTypes_CarryTheDocumentedNumericValues()
        {
            Assert.That((byte)DamageType.SmallArms, Is.EqualTo(0));
            Assert.That((byte)DamageType.HeavyMachineGun, Is.EqualTo(1));
            Assert.That((byte)DamageType.AntiTankCannon, Is.EqualTo(2));
            Assert.That((byte)DamageType.GuidedMissile, Is.EqualTo(3));
            Assert.That((byte)DamageType.ArtilleryExplosive, Is.EqualTo(4));
            Assert.That((byte)DamageType.ThermobaricNapalm, Is.EqualTo(5));
            Assert.That((byte)DamageType.ToxinChemical, Is.EqualTo(6));
            Assert.That((byte)DamageType.HighTechLaser, Is.EqualTo(7));
            Assert.That((byte)DamageType.Count, Is.EqualTo(8));

            Assert.That((byte)ArmorType.Infantry, Is.EqualTo(0));
            Assert.That((byte)ArmorType.HeavyInfantry, Is.EqualTo(1));
            Assert.That((byte)ArmorType.LightVehicle, Is.EqualTo(2));
            Assert.That((byte)ArmorType.HeavyVehicle, Is.EqualTo(3));
            Assert.That((byte)ArmorType.Aircraft, Is.EqualTo(4));
            Assert.That((byte)ArmorType.Structure, Is.EqualTo(5));
            Assert.That((byte)ArmorType.Fortified, Is.EqualTo(6));
            Assert.That((byte)ArmorType.Count, Is.EqualTo(7));

            Assert.That(DocumentedMultipliers.Length, Is.EqualTo((int)DamageType.Count * (int)ArmorType.Count),
                "the documented table must stay an 8x7 grid, otherwise the row-major index drifts");
        }

        [Test]
        public void DamageMatrix_ContainsExact56Multipliers()
        {
            var zeroCells = 0;
            for (var d = 0; d < (int)DamageType.Count; d++)
            {
                for (var a = 0; a < (int)ArmorType.Count; a++)
                {
                    var damageType = (DamageType)d;
                    var armorType = (ArmorType)a;
                    var expected = DocumentedMultipliers[(d * (int)ArmorType.Count) + a];
                    var actual = DamageMatrix.GetMultiplierPermille(damageType, armorType);

                    Assert.That(actual, Is.EqualTo(expected),
                        $"M[{damageType}][{armorType}] must be {expected} permille, found {actual}");
                    Assert.That(actual, Is.GreaterThanOrEqualTo((short)0),
                        $"M[{damageType}][{armorType}] is below the documented range");
                    Assert.That(actual, Is.LessThanOrEqualTo((short)2500),
                        $"M[{damageType}][{armorType}] is above the documented [0..2500] range");

                    if (actual == 0)
                    {
                        zeroCells++;
                    }
                }
            }

            // Only the three ground-only weapons are helpless against Aircraft (R&D §4.2).
            Assert.That(zeroCells, Is.EqualTo(3),
                "exactly Artillery, Thermobaric and Toxin miss aircraft; a fourth zero cell " +
                "silently makes a weapon useless");
        }

        [Test]
        public void DamageMatrix_LegacyCellSmallArmsAgainstInfantry_IsExactly1000()
        {
            // The back-compat anchor of ADR-015: an untyped CombatStats archetypes onto this
            // pair, and only a literal 1000 keeps existing damage values bit-for-bit.
            Assert.That(DamageMatrix.DefaultMultiplierPermille, Is.EqualTo((short)1000));
            Assert.That(
                DamageMatrix.GetMultiplierPermille(DamageType.SmallArms, ArmorType.Infantry),
                Is.EqualTo((short)1000));
        }

        [Test]
        public void DamageMatrix_OutOfBoundsDamageType_ReturnsDefault1000()
        {
            Assert.That(
                DamageMatrix.GetMultiplierPermille((DamageType)99, ArmorType.Infantry),
                Is.EqualTo((short)1000));
            // DamageType.Count itself is the first illegal row.
            Assert.That(
                DamageMatrix.GetMultiplierPermille((DamageType)8, ArmorType.HeavyVehicle),
                Is.EqualTo((short)1000));
            Assert.That(
                DamageMatrix.GetMultiplierPermille((DamageType)255, ArmorType.Structure),
                Is.EqualTo((short)1000));
        }

        [Test]
        public void DamageMatrix_OutOfBoundsArmorType_ReturnsDefault1000()
        {
            Assert.That(
                DamageMatrix.GetMultiplierPermille(DamageType.SmallArms, (ArmorType)99),
                Is.EqualTo((short)1000));
            // ArmorType.Count itself is the first illegal column.
            Assert.That(
                DamageMatrix.GetMultiplierPermille(DamageType.ToxinChemical, (ArmorType)7),
                Is.EqualTo((short)1000));
            Assert.That(
                DamageMatrix.GetMultiplierPermille(DamageType.HighTechLaser, (ArmorType)255),
                Is.EqualTo((short)1000));
        }

        // ------------------------------------------------------------------
        // 2 — the resolver
        // ------------------------------------------------------------------

        [Test]
        public void CalculateDamage_ZeroOrNegativeBase_ReturnsZero()
        {
            Assert.That(
                DamageCalculator.CalculateDamage(0, DamageType.SmallArms, ArmorType.Infantry),
                Is.EqualTo(0));
            Assert.That(
                DamageCalculator.CalculateDamage(-1, DamageType.ToxinChemical, ArmorType.Infantry),
                Is.EqualTo(0));
            Assert.That(
                DamageCalculator.CalculateDamage(-1000, DamageType.ArtilleryExplosive, ArmorType.Structure, 10),
                Is.EqualTo(0));
        }

        [Test]
        public void CalculateDamage_ZeroMultiplier_ReturnsZero()
        {
            // A ground-only volley against an aircraft does no damage, and the min-1 clamp
            // must not resurrect it.
            Assert.That(
                DamageCalculator.CalculateDamage(100, DamageType.ArtilleryExplosive, ArmorType.Aircraft),
                Is.EqualTo(0));
            Assert.That(
                DamageCalculator.CalculateDamage(100, DamageType.ThermobaricNapalm, ArmorType.Aircraft),
                Is.EqualTo(0));
            Assert.That(
                DamageCalculator.CalculateDamage(1000, DamageType.ToxinChemical, ArmorType.Aircraft, 500),
                Is.EqualTo(0));
        }

        [Test]
        public void CalculateDamage_ExactMultiplicationAndRounding()
        {
            Assert.That(
                DamageCalculator.CalculateDamage(100, DamageType.SmallArms, ArmorType.Infantry),
                Is.EqualTo(100));
            Assert.That(
                DamageCalculator.CalculateDamage(100, DamageType.ToxinChemical, ArmorType.Infantry),
                Is.EqualTo(250));
            // 10 x 0.25 = 2.5 -> 3, the half-up witness against truncating division.
            Assert.That(
                DamageCalculator.CalculateDamage(10, DamageType.SmallArms, ArmorType.HeavyVehicle),
                Is.EqualTo(3));
            // 9 x 0.25 = 2.25 -> 2, so the rule is rounding and not a blanket ceiling.
            Assert.That(
                DamageCalculator.CalculateDamage(9, DamageType.SmallArms, ArmorType.HeavyVehicle),
                Is.EqualTo(2));
            Assert.That(
                DamageCalculator.CalculateDamage(11, DamageType.SmallArms, ArmorType.HeavyVehicle),
                Is.EqualTo(3));
            // The widest realistic product still scales through long, not int.
            Assert.That(
                DamageCalculator.CalculateDamage(100000, DamageType.ToxinChemical, ArmorType.Infantry),
                Is.EqualTo(250000));
        }

        [Test]
        public void CalculateDamage_ExactHalfRoundsUp()
        {
            // 15 x 0.7 = 10.5: truncation gives 10, round-half-up gives 11.
            Assert.That(
                DamageCalculator.CalculateDamage(15, DamageType.SmallArms, ArmorType.HeavyInfantry),
                Is.EqualTo(11));
            // 21 x 0.7 = 14.7 -> 15.
            Assert.That(
                DamageCalculator.CalculateDamage(21, DamageType.SmallArms, ArmorType.HeavyInfantry),
                Is.EqualTo(15));
            // 20 x 0.7 = 14.0, untouched by either rule.
            Assert.That(
                DamageCalculator.CalculateDamage(20, DamageType.SmallArms, ArmorType.HeavyInfantry),
                Is.EqualTo(14));
        }

        [Test]
        public void CalculateDamage_SmallBaseNeverBecomesZeroIfMultiplierNonZero()
        {
            // 1 x 0.1 rounds to 0 and clamps back to 1.
            Assert.That(
                DamageCalculator.CalculateDamage(1, DamageType.SmallArms, ArmorType.Fortified),
                Is.EqualTo(1));
            Assert.That(
                DamageCalculator.CalculateDamage(1, DamageType.AntiTankCannon, ArmorType.Aircraft),
                Is.EqualTo(1));
            Assert.That(
                DamageCalculator.CalculateDamage(2, DamageType.HighTechLaser, ArmorType.Fortified),
                Is.EqualTo(1));
        }

        [Test]
        public void CalculateDamage_FlatReduction_IsAppliedBeforeTheMultiplier()
        {
            // (20 - 5) x 0.25 = 3.75 -> 4. Subtracting after scaling, or ignoring the
            // reduction, lands on 1 and 5 instead.
            Assert.That(
                DamageCalculator.CalculateDamage(20, DamageType.SmallArms, ArmorType.HeavyVehicle, 5),
                Is.EqualTo(4));
            Assert.That(
                DamageCalculator.CalculateDamage(20, DamageType.SmallArms, ArmorType.HeavyVehicle, 0),
                Is.EqualTo(5));
        }

        [Test]
        public void CalculateDamage_FlatReduction_CannotReduceBaseBelowOne()
        {
            Assert.That(
                DamageCalculator.CalculateDamage(5, DamageType.SmallArms, ArmorType.Infantry, 10),
                Is.EqualTo(1));
            // An exact kill of the base still leaves 1 hit point of damage.
            Assert.That(
                DamageCalculator.CalculateDamage(10, DamageType.SmallArms, ArmorType.Infantry, 10),
                Is.EqualTo(1));
            Assert.That(
                DamageCalculator.CalculateDamage(11, DamageType.SmallArms, ArmorType.Infantry, 10),
                Is.EqualTo(1));
        }

        [Test]
        public void CalculateDamage_NegativeFlatReduction_IsNotABonus()
        {
            Assert.That(
                DamageCalculator.CalculateDamage(20, DamageType.SmallArms, ArmorType.Infantry, -5),
                Is.EqualTo(20));
            Assert.That(
                DamageCalculator.CalculateDamage(20, DamageType.ToxinChemical, ArmorType.Infantry, -1000),
                Is.EqualTo(50));
        }

        [Test]
        public void CalculateDamage_LegacySentinel255_PreservesBaseDamageBitForBit()
        {
            // ADR-015: an untyped source or target leaves the matrix and keeps flat damage.
            var untypedSource = (DamageType)255;
            var untypedArmor = (ArmorType)255;

            Assert.That(
                DamageCalculator.CalculateDamage(1, untypedSource, ArmorType.Fortified),
                Is.EqualTo(1));
            Assert.That(
                DamageCalculator.CalculateDamage(7, DamageType.ToxinChemical, untypedArmor),
                Is.EqualTo(7));
            Assert.That(
                DamageCalculator.CalculateDamage(100, untypedSource, untypedArmor),
                Is.EqualTo(100));
            Assert.That(
                DamageCalculator.CalculateDamage(1000, untypedSource, ArmorType.Infantry),
                Is.EqualTo(1000));
        }

        // ------------------------------------------------------------------
        // 3 — zero-GC hot path
        // ------------------------------------------------------------------

        [Test]
        public void CalculateDamage_ThousandHits_AllocatesZeroBytes()
        {
            // The table is built by DamageMatrix's static initializer; without this warmup the
            // measured window would report the one-off 56-cell allocation as a hot-path leak.
            var warmup = 0;
            for (var i = 0; i < MeasuredHits; i++)
            {
                warmup += ResolveSweepHit(i);
            }

            Assert.That(warmup, Is.GreaterThan(0), "the warmup must resolve real hits");

            var measured = 0;
            Assert.That(
                () =>
                {
                    for (var i = 0; i < MeasuredHits; i++)
                    {
                        measured += ResolveSweepHit(i);
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "damage resolution is a per-tick hot path and must run at 0 B GC.Alloc");

            Assert.That(measured, Is.EqualTo(warmup),
                "the measured window must have resolved the same hits, or the probe proves " +
                "nothing about work the JIT could have skipped");
        }

        [Test]
        public void GcAllocationProbe_ObservesKnownAllocation_Control()
        {
            // The negated assertion is only evidence once a positive one has been proven live.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory(),
                "the GC.Alloc probe must observe a known allocation");
            Assert.That(ballast, Is.Not.Null);
        }

        /// <summary>
        /// Sweeps every matrix cell, plus the out-of-range guard branch, so the probe covers
        /// the table read and the resolver. Assertion-free on purpose: an NUnit constraint
        /// inside the measured window allocates by itself.
        /// </summary>
        private static int ResolveSweepHit(int index)
        {
            var cell = index % ((int)DamageType.Count * (int)ArmorType.Count);
            var damageType = (DamageType)(cell / (int)ArmorType.Count);
            var armorType = (ArmorType)(cell % (int)ArmorType.Count);
            var baseDamage = 1 + (index % 997);

            if ((index % 10) == 0)
            {
                // The legacy sentinel branch, on top of the table read.
                return DamageCalculator.CalculateDamage(baseDamage, damageType, (ArmorType)255, 3)
                    + DamageCalculator.CalculateDamage(baseDamage, (DamageType)255, armorType, 3);
            }

            return DamageCalculator.CalculateDamage(baseDamage, damageType, armorType, 3);
        }
    }
}
