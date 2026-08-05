namespace GlobalFront.Core.Simulation
{
    /// <summary>
    /// Constants that form part of the network protocol and may not be changed
    /// without a compatibility review.
    /// </summary>
    public static class SimulationConstants
    {
        public const int ServerTickRate = 20;
        public const double ServerTickDurationSeconds = 1.0 / ServerTickRate;
        public const int MaxCatchUpTicksPerFrame = 4;
        public const int MaxPlayers = 10;
        public const int MaxSelectedEntities = 100;
        public const int MaxFormationSpacingMm = 100_000;
        public const int MaxWorldCoordinateMm = 1_000_000_000;

        /// <summary>
        /// Radius in millimetres within which a unit with auto-acquire enabled
        /// will seek the closest enemy when it has no explicit attack target.
        /// Shared by client (<see cref="GlobalFront.Client.PrototypeRtsController"/>)
        /// and server (<see cref="GlobalFront.Server.MatchServer"/>) to keep
        /// target acquisition behaviour identical on both sides.
        /// </summary>
        public const int AutoAcquireRangeMm = 18000;
    }
}
