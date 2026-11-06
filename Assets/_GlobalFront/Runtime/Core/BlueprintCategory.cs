namespace GlobalFront.Core.Catalog
{
    /// <summary>
    /// Presentation-and-rules class of a blueprint row (Step 5.5.1).
    ///
    /// Drives the structural invariants the catalog enforces at load —
    /// Building/Defense rows must declare a footprint on the 500 mm construction
    /// grid, Infantry/Vehicle/Aircraft rows must move — and later phases will
    /// reuse it for roster tabs, build menus and selection ring rules. It is a
    /// byte enum rather than a bare byte because a row's class is simulation
    /// data, not a wire encoding; the protocol never serialises the category.
    ///
    /// Values are append-only: reordering would re-classify stored rows across
    /// a recorded stream or an older client build.
    /// </summary>
    public enum BlueprintCategory : byte
    {
        /// <summary>Unset: a real row never carries this; the catalog rejects it.</summary>
        None = 0,

        /// <summary>Foot soldier; the only class able to occupy an Oil Derrick.</summary>
        Infantry = 1,

        /// <summary>Ground vehicle, from light buggies to heavy tanks.</summary>
        Vehicle = 2,

        /// <summary>Air unit; no air doctrine in the Step 5.5 starter roster.</summary>
        Aircraft = 3,

        /// <summary>Stationary combat structure; turrets, bunkers, stinger sites.</summary>
        Defense = 4,

        /// <summary>Stationary support structure; command centers, production, power.</summary>
        Building = 5,

        /// <summary>Economy structure; docks, supply centers, drop zones.</summary>
        Supply = 6,

        /// <summary>Non-physical upgrade; unlocks rather than exists on the map.</summary>
        Tech = 7,

        /// <summary>Match-shaping superstructure; one per faction, late-game cost.</summary>
        Superweapon = 8,
    }
}
