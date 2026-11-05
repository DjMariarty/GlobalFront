using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;

namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// Why a production operation was refused. Values are protocol-significant —
    /// never renumber. <see cref="ProductionRejection.BuildingNotCompleted"/> and
    /// <see cref="ProductionRejection.BuildingDead"/> are reserved for the
    /// Step 5.3.2 server gate: the Core registry only ever holds completed
    /// buildings, so it can never return them (precedent: TerrainBlocked).
    /// </summary>
    public enum ProductionRejection : byte
    {
        None = 0,
        BuildingNotFound = 1,
        InvalidOwner = 2,
        BuildingNotCompleted = 3,
        BuildingDead = 4,
        QueueFull = 5,
        InvalidBlueprint = 6,
        InvalidSlotIndex = 7
    }

    /// <summary>
    /// Why a rally-point order was refused. Values are protocol-significant —
    /// never renumber.
    /// </summary>
    public enum SetRallyRejection : byte
    {
        None = 0,
        BuildingNotFound = 1,
        InvalidOwner = 2,
        OutOfBounds = 3
    }

    /// <summary>
    /// One queued production order (immutable struct). Ticks are whole
    /// simulation ticks at rate 1000‰; the per-tick accumulator lives on the
    /// building, not the item. 16 bytes on a 64-bit runtime.
    /// </summary>
    public readonly struct ProductionQueueItem
    {
        public ProductionQueueItem(ushort blueprintId, ushort totalTicks, ushort remainingTicks, int paidCost)
        {
            BlueprintId = blueprintId;
            TotalTicks = totalTicks;
            RemainingTicks = remainingTicks;
            PaidCost = paidCost;
        }

        public ushort BlueprintId { get; }

        public ushort TotalTicks { get; }

        public ushort RemainingTicks { get; }

        /// <summary>Refunded in full (100%) on cancel or building destruction.</summary>
        public int PaidCost { get; }

        public ProductionQueueItem WithRemainingTicks(ushort remainingTicks) =>
            new ProductionQueueItem(BlueprintId, TotalTicks, remainingTicks, PaidCost);
    }

    /// <summary>
    /// Snapshot of one production building's state for observation and tests.
    /// </summary>
    public readonly struct BuildingProductionState
    {
        public BuildingProductionState(
            ulong buildingEntityId,
            PlayerId owner,
            WorldPointMm center,
            WorldPointMm exitPoint,
            WorldPointMm rallyPoint,
            bool hasRally,
            int queueCount,
            int workMilliAccumulator)
        {
            BuildingEntityId = buildingEntityId;
            Owner = owner;
            Center = center;
            ExitPoint = exitPoint;
            RallyPoint = rallyPoint;
            HasRally = hasRally;
            QueueCount = queueCount;
            WorkMilliAccumulator = workMilliAccumulator;
        }

        public ulong BuildingEntityId { get; }

        public PlayerId Owner { get; }

        public WorldPointMm Center { get; }

        public WorldPointMm ExitPoint { get; }

        /// <summary>Meaningless while <see cref="HasRally"/> is false — it mirrors the exit point.</summary>
        public WorldPointMm RallyPoint { get; }

        public bool HasRally { get; }

        public int QueueCount { get; }

        /// <summary>Per-mille work carried over between ticks; flows to the next item on completion.</summary>
        public int WorkMilliAccumulator { get; }
    }

    /// <summary>
    /// A finished unit leaving production this tick. SpawnPosition is the exit
    /// point or the nearest free lattice node found by the spiral search;
    /// RallyPoint is meaningful only with <see cref="HasRally"/>.
    /// </summary>
    public readonly struct ProductionCompletedUnit
    {
        public ProductionCompletedUnit(
            ulong buildingEntityId,
            PlayerId owner,
            ushort blueprintId,
            WorldPointMm spawnPosition,
            WorldPointMm rallyPoint,
            bool hasRally)
        {
            BuildingEntityId = buildingEntityId;
            Owner = owner;
            BlueprintId = blueprintId;
            SpawnPosition = spawnPosition;
            RallyPoint = rallyPoint;
            HasRally = hasRally;
        }

        public ulong BuildingEntityId { get; }

        public PlayerId Owner { get; }

        public ushort BlueprintId { get; }

        public WorldPointMm SpawnPosition { get; }

        public WorldPointMm RallyPoint { get; }

        public bool HasRally { get; }
    }
}
