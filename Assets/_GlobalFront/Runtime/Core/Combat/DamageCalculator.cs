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
        public static int CalculateDamage(
            int baseDamage,
            DamageType damageType,
            ArmorType armorType,
            int flatReduction = 0)
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

            // long holds the widest legal product (int.MaxValue x 2500) without overflow,
            // and +500 before the truncating divide is the deterministic round-half-up.
            long scaled = (long)effectiveBase * multiplierPermille;
            int finalDamage = (int)((scaled + 500) / 1000);

            return Math.Max(finalDamage, 1);
        }
    }
}
