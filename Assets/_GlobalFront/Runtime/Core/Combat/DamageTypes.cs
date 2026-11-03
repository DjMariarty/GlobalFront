namespace GlobalFront.Core.Combat
{
    /// <summary>
    /// Weapon damage families, indexed as rows of the 8x7 <see cref="DamageMatrix"/> (ADR-015).
    /// Values are stable wire/serialization material: never renumber an existing member.
    /// </summary>
    public enum DamageType : byte
    {
        SmallArms = 0,
        HeavyMachineGun = 1,
        AntiTankCannon = 2,
        GuidedMissile = 3,
        ArtilleryExplosive = 4,
        ThermobaricNapalm = 5,
        ToxinChemical = 6,
        HighTechLaser = 7,
        Count = 8
    }

    /// <summary>
    /// Target armor classes, indexed as columns of the 8x7 <see cref="DamageMatrix"/> (ADR-015).
    /// </summary>
    public enum ArmorType : byte
    {
        Infantry = 0,
        HeavyInfantry = 1,
        LightVehicle = 2,
        HeavyVehicle = 3,
        Aircraft = 4,
        Structure = 5,
        Fortified = 6,
        Count = 7
    }
}
