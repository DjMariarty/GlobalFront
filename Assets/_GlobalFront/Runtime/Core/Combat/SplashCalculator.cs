using System;

namespace GlobalFront.Core.Combat
{
    /// <summary>
    /// Area-damage falloff of ADR-015 for artillery, thermobaric and napalm strikes:
    /// whole millimetres in, whole hit points out, truncating toward zero.
    /// </summary>
    public static class SplashCalculator
    {
        /// <summary>
        /// Damage at <paramref name="distanceMm"/> from the epicentre: the full base at the
        /// centre, decreasing linearly to nothing at the rim. A negative distance is the
        /// centre, and a non-positive radius has no area to cover.
        /// </summary>
        public static int CalculateLinearFalloff(int baseDamage, int radiusMm, int distanceMm)
        {
            if (baseDamage <= 0 || radiusMm <= 0 || distanceMm >= radiusMm)
            {
                return 0;
            }

            int clampedDistance = Math.Max(0, distanceMm);
            long remainingRange = (long)(radiusMm - clampedDistance);
            long scaled = (long)baseDamage * remainingRange;

            return (int)(scaled / radiusMm);
        }
    }
}
