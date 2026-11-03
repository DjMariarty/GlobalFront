using System;

namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// Why a placement attempt was rejected. Every way
    /// <see cref="BuildingPlacementRegistry.TryRegister"/> can fail has its own
    /// code, so a false return never carries an ambiguous
    /// <see cref="PlacementRejection.None"/>.
    /// </summary>
    public enum PlacementRejection : byte
    {
        None = 0,
        OutOfBounds = 1,

        /// <summary>Reserved for the Step 5.2.2 terrain gates; never returned yet.</summary>
        TerrainBlocked = 2,
        OverlapsExistingBuilding = 3,
        RegistryFull = 4,
        InvalidEntityId = 5
    }

    /// <summary>
    /// Axis-aligned building footprint in integer millimetres. Both dimensions
    /// must be strictly positive and a multiple of the 500 mm snap step so the
    /// AABB corners stay on the same deterministic lattice as snapped centres.
    /// A constructed footprint is always valid; only
    /// <c>default(BuildingFootprint)</c> bypasses the constructor (0x0) and must
    /// never reach the registry.
    /// </summary>
    public readonly struct BuildingFootprint : IEquatable<BuildingFootprint>
    {
        public const int SnapStepMm = 500;

        public BuildingFootprint(int widthMm, int lengthMm)
        {
            if (widthMm <= 0 || widthMm % SnapStepMm != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(widthMm), widthMm,
                    $"Footprint width must be strictly positive and a multiple of {SnapStepMm} mm.");
            }

            if (lengthMm <= 0 || lengthMm % SnapStepMm != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lengthMm), lengthMm,
                    $"Footprint length must be strictly positive and a multiple of {SnapStepMm} mm.");
            }

            WidthMm = widthMm;
            LengthMm = lengthMm;
        }

        public int WidthMm { get; }

        public int LengthMm { get; }

        public bool Equals(BuildingFootprint other) => WidthMm == other.WidthMm && LengthMm == other.LengthMm;

        public override bool Equals(object obj) => obj is BuildingFootprint other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (WidthMm * 397) ^ LengthMm;
            }
        }

        public override string ToString() => $"{WidthMm} x {LengthMm} mm";

        public static bool operator ==(BuildingFootprint left, BuildingFootprint right) => left.Equals(right);

        public static bool operator !=(BuildingFootprint left, BuildingFootprint right) => !left.Equals(right);
    }
}
