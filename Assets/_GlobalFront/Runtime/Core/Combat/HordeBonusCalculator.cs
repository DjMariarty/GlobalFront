using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Combat
{
    /// <summary>
    /// China Horde Bonus (Step 5.5.3, RND §: horde doctrine): an infantry or
    /// vehicle group of at least <see cref="SimulationConstants.HordeThreshold"/>
    /// same-category, same-owner units within
    /// <see cref="SimulationConstants.HordeRadiusMm"/> fights harder and moves
    /// faster — damage ×<see cref="SimulationConstants.HordeDamagePermille"/>‰,
    /// speed ×<see cref="SimulationConstants.HordeSpeedPermille"/>‰.
    ///
    /// Pure static functions over the neighbor count: the counting itself is
    /// the server's per-tick walk (canonical roster order, squared distances,
    /// zero allocations), and the threshold counts the unit itself — a horde
    /// of 5 is the unit plus four allies. Integer-only and deterministic, in
    /// the <see cref="GatlingEscalation"/> family of aux calculators.
    /// </summary>
    public static class HordeBonusCalculator
    {
        /// <summary>Baseline permille: no bonus.</summary>
        public const int NoBonusPermille = 1000;

        /// <summary>True once the count reaches the horde threshold (the unit itself included).</summary>
        public static bool IsHorde(int neighborCount) =>
            neighborCount >= SimulationConstants.HordeThreshold;

        /// <summary>Damage multiplier, ‰: 1250 for a qualifying horde, 1000 otherwise.</summary>
        public static int DamagePermille(int neighborCount) =>
            IsHorde(neighborCount) ? SimulationConstants.HordeDamagePermille : NoBonusPermille;

        /// <summary>Speed multiplier, ‰: 1100 for a qualifying horde, 1000 otherwise.</summary>
        public static int SpeedPermille(int neighborCount) =>
            IsHorde(neighborCount) ? SimulationConstants.HordeSpeedPermille : NoBonusPermille;

        /// <summary>
        /// Scales a tick speed by the horde bonus with the canonical
        /// round-half-up. Always >= 1, so <see cref="PlanarMovement.StepTowards"/>
        /// never sees a non-positive step.
        /// </summary>
        public static int ApplyToSpeed(int speedMmPerTick, int neighborCount)
        {
            var scaled = ((long)speedMmPerTick * SpeedPermille(neighborCount) + 500) / 1000;
            return scaled >= 1 ? (int)scaled : 1;
        }
    }
}
