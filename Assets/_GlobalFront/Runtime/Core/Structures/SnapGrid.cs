using System;
using GlobalFront.Core.Movement;

namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// Deterministic integer snapping of placement coordinates onto a millimetre
    /// lattice. Snapping is a network-desync guard, not a movement restriction —
    /// buildings may be ordered anywhere on the map (Generals / Contra X style).
    /// Half-away-from-zero rounding keeps positive and negative coordinates
    /// symmetric: 250 lands on 500, -250 lands on -500.
    /// </summary>
    public static class SnapGrid
    {
        public const int DefaultSnapStepMm = 500;

        /// <summary>
        /// Rounds <paramref name="coordMm"/> to the nearest multiple of
        /// <paramref name="stepMm"/>; exact halves round away from zero. The
        /// <c>long</c> intermediates keep the half-step shift exact for every
        /// coordinate inside simulation bounds (|coord| &lt; int.MaxValue - step).
        /// </summary>
        public static int SnapCoordinate(int coordMm, int stepMm = DefaultSnapStepMm)
        {
            if (stepMm <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stepMm), stepMm, "Snap step must be strictly positive.");
            }

            return coordMm >= 0
                ? (int)(((long)coordMm + stepMm / 2) / stepMm * stepMm)
                : (int)(((long)coordMm - stepMm / 2) / stepMm * stepMm);
        }

        /// <summary>Snaps both axes of <paramref name="point"/> onto the lattice.</summary>
        public static WorldPointMm SnapPosition(WorldPointMm point, int stepMm = DefaultSnapStepMm)
        {
            return new WorldPointMm(SnapCoordinate(point.X, stepMm), SnapCoordinate(point.Z, stepMm));
        }
    }
}
