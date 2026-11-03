namespace GlobalFront.Core.Combat
{
    /// <summary>
    /// The canonical damage-type x armor-type multiplier table (ADR-015 / OD-51), in permille
    /// where <see cref="DefaultMultiplierPermille"/> is x1.00. Rows are <see cref="DamageType"/>,
    /// columns are <see cref="ArmorType"/>. Lookup allocates nothing: the table is built once at
    /// type initialization and read by index.
    /// </summary>
    public static class DamageMatrix
    {
        /// <summary>Multiplier basis: 1000 permille = x1.00, i.e. the damage is passed through.</summary>
        public const short DefaultMultiplierPermille = 1000;

        // 8 damage types x 7 armor types, row-major: index = damageType * 7 + armorType.
        private static readonly short[] Multipliers = new short[(int)DamageType.Count * (int)ArmorType.Count]
        {
            // SmallArms (0)
            1000, 700, 500, 250, 500, 300, 100,
            // HeavyMachineGun (1)
            700, 1000, 800, 300, 700, 400, 100,
            // AntiTankCannon (2)
            400, 600, 1000, 1000, 200, 800, 600,
            // GuidedMissile (3)
            500, 500, 1000, 900, 1000, 600, 300,
            // ArtilleryExplosive (4)
            1500, 1200, 800, 700, 0, 1200, 1000,
            // ThermobaricNapalm (5)
            1500, 1400, 600, 400, 0, 800, 200,
            // ToxinChemical (6)
            2500, 1800, 400, 150, 0, 300, 150,
            // HighTechLaser (7)
            1000, 1000, 800, 700, 1000, 600, 400
        };

        /// <summary>
        /// Reads one cell. An unknown type or armor class is the legacy sentinel of ADR-015:
        /// it returns <see cref="DefaultMultiplierPermille"/>, so flat base damage survives
        /// the calculator bit-for-bit.
        /// </summary>
        public static short GetMultiplierPermille(DamageType damageType, ArmorType armorType)
        {
            byte d = (byte)damageType;
            byte a = (byte)armorType;
            if (d >= (byte)DamageType.Count || a >= (byte)ArmorType.Count)
            {
                return DefaultMultiplierPermille;
            }

            return Multipliers[(d * (int)ArmorType.Count) + a];
        }
    }
}
