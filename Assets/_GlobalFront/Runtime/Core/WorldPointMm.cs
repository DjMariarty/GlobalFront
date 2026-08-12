using System;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Movement
{
    /// <summary>
    /// Engine-independent planar position quantized to millimetres. Network
    /// commands never carry Unity vectors or frame-rate-dependent values.
    /// </summary>
    public readonly struct WorldPointMm : IEquatable<WorldPointMm>
    {
        public WorldPointMm(int x, int z)
        {
            X = x;
            Z = z;
        }

        public int X { get; }

        public int Z { get; }

        public bool IsWithinSimulationBounds =>
            X >= -SimulationConstants.MaxWorldCoordinateMm &&
            X <= SimulationConstants.MaxWorldCoordinateMm &&
            Z >= -SimulationConstants.MaxWorldCoordinateMm &&
            Z <= SimulationConstants.MaxWorldCoordinateMm;

        public bool Equals(WorldPointMm other) => X == other.X && Z == other.Z;

        public override bool Equals(object obj) => obj is WorldPointMm other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Z;
            }
        }

        public override string ToString() => $"({X}, {Z}) mm";

        public static bool operator ==(WorldPointMm left, WorldPointMm right) => left.Equals(right);

        public static bool operator !=(WorldPointMm left, WorldPointMm right) => !left.Equals(right);
    }

    public enum CardinalFacing : byte
    {
        North = 0,
        East = 1,
        South = 2,
        West = 3
    }

    public readonly struct FormationSpec
    {
        public FormationSpec(int columns, int spacingMm, CardinalFacing facing)
        {
            Columns = columns;
            SpacingMm = spacingMm;
            Facing = facing;
        }

        /// <summary>Zero selects an integer ceil-square-root layout.</summary>
        public int Columns { get; }

        public int SpacingMm { get; }

        public CardinalFacing Facing { get; }

        public bool IsValid =>
            Columns >= 0 &&
            Columns <= SimulationConstants.MaxSelectedEntities &&
            SpacingMm > 0 &&
            SpacingMm <= SimulationConstants.MaxFormationSpacingMm &&
            (SpacingMm & 1) == 0 &&
            (byte)Facing <= (byte)CardinalFacing.West;
    }
}
