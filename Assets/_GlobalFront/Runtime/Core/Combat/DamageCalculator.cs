using System;

namespace GlobalFront.Core.Combat
{
    /// <summary>
    /// The canonical integer damage resolver of ADR-015: flat reduction, then the
    /// <see cref="DamageMatrix"/> permille multiplier, then round-half-up back to whole hit
    /// points. Integer-only and allocation-free, so a replay and a state checksum agree.
    /// </summary>
    public static class DamageCalculator
    {
        /// <summary>
        /// Resolves one hit. Returns 0 when there is no base damage or the matrix cell is 0
        /// (a ground-only weapon firing at an aircraft); otherwise at least 1, because a
        /// nonzero hit cannot be rounded away by the matrix.
        /// </summary>
        /// <param name="bonusMultiplierPermille">
        /// Extra permille composed after the matrix cell (Step 5.5.3 Horde
        /// Bonus: 1250 for a qualifying China horde). 1000 keeps the value
        /// bit-for-bit identical to the unbonused path.
        /// </param>
        public static int CalculateDamage(
            int baseDamage,
            DamageType damageType,
            ArmorType armorType,
            int flatReduction = 0,
            int bonusMultiplierPermille = 1000)
        {
            if (baseDamage <= 0)
            {
                return 0;
            }

            // Optional armor-piercing subtraction (DoTs, heavy tanks) applies to the base,
            // before scaling, and a negative reduction is not a bonus.
            int effectiveBase = flatReduction > 0
                ? Math.Max(baseDamage - flatReduction, 1)
                : baseDamage;

            short multiplierPermille = DamageMatrix.GetMultiplierPermille(damageType, armorType);
            if (multiplierPermille == 0)
            {
                return 0;
            }

            // long holds the widest legal product (int.MaxValue x 2500 x 1250 ≈ 2.5e15)
            // without overflow. The two permille factors compose into one round-half-up
            // at the 1e6 scale: with bonus == 1000 the result is bit-for-bit the
            // unbonused path ((x*m + 500) / 1000).
            long scaled = (long)effectiveBase * multiplierPermille * bonusMultiplierPermille;
            int finalDamage = (int)((scaled + 500_000) / 1_000_000);

            return Math.Max(finalDamage, 1);
        }
    }
}
