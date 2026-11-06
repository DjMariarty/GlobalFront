using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Economy
{
    /// <summary>
    /// FSM state of a harvester. Values are protocol-significant — never
    /// renumber. <see cref="HarvesterState.Unload"/> is reserved: unloading is
    /// instant (0 ticks), so the state is applied and immediately replaced by
    /// ToDock/Idle within the same tick and is never observable in a snapshot.
    /// </summary>
    public enum HarvesterState : byte
    {
        Idle = 0,
        ToDock = 1,
        WaitSlot = 2,
        Loading = 3,
        ToCenter = 4,
        Unload = 5
    }

    /// <summary>
    /// Why an economy registration was refused. Values are protocol-significant
    /// — never renumber.
    /// </summary>
    public enum EconomyRegistrationRejection : byte
    {
        None = 0,
        InvalidEntityId = 1,
        InvalidOwner = 2,
        DuplicateEntity = 3,
        RegistryFull = 4,
        EntityNotFound = 5
    }

    /// <summary>
    /// Per-player credit ledger snapshot. Credits saturate at
    /// <see cref="SimulationConstants.MaxCredits"/>; TotalHarvestedCredits
    /// accumulates only income (spending never reduces it).
    /// </summary>
    public readonly struct PlayerEconomyState
    {
        public PlayerEconomyState(PlayerId player, int credits, int totalHarvestedCredits)
        {
            Player = player;
            Credits = credits;
            TotalHarvestedCredits = totalHarvestedCredits;
        }

        public PlayerId Player { get; }

        public int Credits { get; }

        public int TotalHarvestedCredits { get; }

        /// <summary>
        /// Applies a credit delta: Credits saturate into [0, MaxCredits]
        /// (no int overflow at the ceiling), TotalHarvestedCredits grows only by
        /// positive deltas.
        /// </summary>
        public PlayerEconomyState WithCreditDelta(int delta)
        {
            int credits = Credits + delta;
            if (credits < 0)
            {
                credits = 0;
            }

            if (credits > SimulationConstants.MaxCredits)
            {
                credits = SimulationConstants.MaxCredits;
            }

            int totalHarvested = TotalHarvestedCredits + (delta > 0 ? delta : 0);
            return new PlayerEconomyState(Player, credits, totalHarvested);
        }
    }

    /// <summary>
    /// Snapshot of one harvester for observation and tests.
    /// </summary>
    public readonly struct HarvesterSnapshot
    {
        public HarvesterSnapshot(
            ulong entityId,
            PlayerId owner,
            HarvesterState state,
            ulong targetDockId,
            ulong targetCenterId,
            int carriedDollars,
            int capacityDollars,
            int loadingTicksRemaining)
        {
            EntityId = entityId;
            Owner = owner;
            State = state;
            TargetDockId = targetDockId;
            TargetCenterId = targetCenterId;
            CarriedDollars = carriedDollars;
            CapacityDollars = capacityDollars;
            LoadingTicksRemaining = loadingTicksRemaining;
        }

        public ulong EntityId { get; }

        public PlayerId Owner { get; }

        public HarvesterState State { get; }

        public ulong TargetDockId { get; }

        public ulong TargetCenterId { get; }

        public int CarriedDollars { get; }

        public int CapacityDollars { get; }

        public int LoadingTicksRemaining { get; }
    }

    /// <summary>
    /// Snapshot of one supply dock for observation and tests.
    /// </summary>
    public readonly struct SupplyDockSnapshot
    {
        public SupplyDockSnapshot(
            ulong entityId,
            WorldPointMm position,
            int remainingDollars,
            int initialDollars,
            int occupiedSlotCount,
            int waitingQueueCount)
        {
            EntityId = entityId;
            Position = position;
            RemainingDollars = remainingDollars;
            InitialDollars = initialDollars;
            OccupiedSlotCount = occupiedSlotCount;
            WaitingQueueCount = waitingQueueCount;
        }

        public ulong EntityId { get; }

        public WorldPointMm Position { get; }

        public int RemainingDollars { get; }

        public int InitialDollars { get; }

        public int OccupiedSlotCount { get; }

        public int WaitingQueueCount { get; }
    }

    /// <summary>
    /// One harvester's authoritative position, streamed out of
    /// <see cref="SupplyEconomyRegistry.CopyHarvesterSync"/> so the server can
    /// mirror registry-driven movement into its roster (Step 5.4.2).
    /// </summary>
    public readonly struct HarvesterSyncEntry
    {
        public HarvesterSyncEntry(ulong entityId, WorldPointMm position)
        {
            EntityId = entityId;
            Position = position;
        }

        public ulong EntityId { get; }

        public WorldPointMm Position { get; }
    }
}
