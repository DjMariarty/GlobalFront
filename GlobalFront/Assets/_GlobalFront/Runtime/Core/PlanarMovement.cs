using System;

namespace GlobalFront.Core.Movement
{
    public static class PlanarMovement
    {
        /// <summary>
        /// Deterministic integer movement used by the prototype simulation.
        /// It never advances farther than maxDistanceMm and snaps at the target.
        /// </summary>
        public static WorldPointMm StepTowards(
            WorldPointMm current,
            WorldPointMm target,
            int maxDistanceMm)
        {
            if (maxDistanceMm <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDistanceMm));
            }

            if (!current.IsWithinSimulationBounds)
            {
                throw new ArgumentOutOfRangeException(nameof(current));
            }

            if (!target.IsWithinSimulationBounds)
            {
                throw new ArgumentOutOfRangeException(nameof(target));
            }

            var deltaX = (long)target.X - current.X;
            var deltaZ = (long)target.Z - current.Z;
            var absoluteX = AbsoluteAsUnsigned(deltaX);
            var absoluteZ = AbsoluteAsUnsigned(deltaZ);
            var squaredDistance = checked(absoluteX * absoluteX + absoluteZ * absoluteZ);
            var maxDistance = (ulong)maxDistanceMm;

            if (squaredDistance <= maxDistance * maxDistance)
            {
                return target;
            }

            var distanceFloor = IntegerSquareRoot(squaredDistance);
            var distanceCeiling = distanceFloor * distanceFloor == squaredDistance
                ? distanceFloor
                : distanceFloor + 1;
            var stepX = checked(deltaX * maxDistanceMm / (long)distanceCeiling);
            var stepZ = checked(deltaZ * maxDistanceMm / (long)distanceCeiling);

            if (stepX == 0 && stepZ == 0)
            {
                if (absoluteX >= absoluteZ)
                {
                    stepX = Math.Sign(deltaX);
                }
                else
                {
                    stepZ = Math.Sign(deltaZ);
                }
            }

            return new WorldPointMm(
                checked(current.X + (int)stepX),
                checked(current.Z + (int)stepZ));
        }

        private static ulong AbsoluteAsUnsigned(long value) =>
            value < 0 ? (ulong)(-value) : (ulong)value;

        private static ulong IntegerSquareRoot(ulong value)
        {
            var remainder = value;
            ulong result = 0;
            var bit = 1UL << 62;

            while (bit > remainder)
            {
                bit >>= 2;
            }

            while (bit != 0)
            {
                if (remainder >= result + bit)
                {
                    remainder -= result + bit;
                    result = (result >> 1) + bit;
                }
                else
                {
                    result >>= 1;
                }

                bit >>= 2;
            }

            return result;
        }
    }
}
