namespace GlobalFront.Core.Combat
{
    /// <summary>
    /// The continuous-fire escalation ladder of the Chinese Gatling family (ADR-015 / OD-51):
    /// the multiplier grows while the weapon keeps firing and the ladder restarts once it has
    /// been silent for <see cref="ResetCooldownTicks"/> ticks.
    /// </summary>
    public static class GatlingEscalation
    {
        public const short Tier0MultiplierPermille = 1000; // < 20 ticks of continuous fire
        public const short Tier1MultiplierPermille = 1400; // 20..59 ticks
        public const short Tier2MultiplierPermille = 1800; // 60..119 ticks
        public const short Tier3MultiplierPermille = 2200; // 120+ ticks

        /// <summary>Silence that drops the ladder back to Tier0: 40 ticks at 20 Hz is 2 s.</summary>
        public const int ResetCooldownTicks = 40;

        /// <summary>
        /// Multiplier in permille for a shot fired after <paramref name="continuousFireTicks"/>
        /// of uninterrupted fire. Negative input is treated as the start of the ladder.
        /// </summary>
        public static short GetMultiplierPermille(int continuousFireTicks)
        {
            if (continuousFireTicks >= 120)
            {
                return Tier3MultiplierPermille;
            }

            if (continuousFireTicks >= 60)
            {
                return Tier2MultiplierPermille;
            }

            if (continuousFireTicks >= 20)
            {
                return Tier1MultiplierPermille;
            }

            return Tier0MultiplierPermille;
        }

        /// <summary>
        /// Whether a run of silence this long has expired the ladder.
        /// </summary>
        public static bool ShouldReset(int ticksSinceLastFire)
        {
            return ticksSinceLastFire >= ResetCooldownTicks;
        }
    }
}
