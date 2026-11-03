namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// Taxonomy of power consumers, used to ask the ledger which system classes
    /// survive a blackout. Values are protocol-significant — never renumber.
    /// </summary>
    public enum PowerConsumerKind : byte
    {
        None = 0,
        Defense = 1,
        Radar = 2,
        Production = 3,
        ModularCrane = 4
    }

    /// <summary>
    /// Why a building could not be registered in the power ledger.
    /// </summary>
    public enum PowerLedgerRejection : byte
    {
        None = 0,
        InvalidEntityId = 1,
        UnknownPlayer = 2,
        DuplicateBuilding = 3,
        TableFull = 4
    }

    /// <summary>
    /// Precomputed per-player power snapshot. O(1) read — recomputed only on
    /// power-relevant events (building registered / completed / destroyed /
    /// cancelled, exemption toggled), never per tick.
    /// </summary>
    public readonly struct PlayerPowerState
    {
        public PlayerPowerState(
            int produced,
            int consumed,
            int netPower,
            bool isLowPower,
            int productionRatePermille)
        {
            Produced = produced;
            Consumed = consumed;
            NetPower = netPower;
            IsLowPower = isLowPower;
            ProductionRatePermille = productionRatePermille;
        }

        /// <summary>Sum of positive PowerDelta over the player's completed buildings.</summary>
        public int Produced { get; }

        /// <summary>Sum of the magnitudes of negative PowerDelta over completed buildings.</summary>
        public int Consumed { get; }

        /// <summary>Produced - Consumed; forced to 0 for power-exempt players (GLA doctrine).</summary>
        public int NetPower { get; }

        /// <summary>True strictly when NetPower &lt; 0 and the player is not power-exempt.</summary>
        public bool IsLowPower { get; }

        /// <summary>1000‰ normally, exactly 500‰ under blackout.</summary>
        public int ProductionRatePermille { get; }

        public override string ToString() =>
            $"{nameof(Produced)}: {Produced}, {nameof(Consumed)}: {Consumed}, {nameof(NetPower)}: {NetPower}, " +
            $"{nameof(IsLowPower)}: {IsLowPower}, {nameof(ProductionRatePermille)}: {ProductionRatePermille}";
    }
}
