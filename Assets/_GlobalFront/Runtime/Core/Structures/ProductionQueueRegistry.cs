using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// Pre-allocated registry of production buildings with their ring queues
    /// (C&C Generals canon: up to <see cref="SimulationConstants.MaxProductionQueueSlots"/>
    /// pre-orders per building). Zero-GC hot path: fixed backing arrays,
    /// for-loops only, swap-and-pop removal, O(n≤128) lookup by building id.
    ///
    /// Per-tick work accumulates in per-mille (<c>WorkMilliAccumulator</c>):
    /// the LowPower ledger halves the rate to exactly 500‰ (exempt players keep
    /// 1000‰), and the leftover flows to the next queued item — nothing burns.
    /// A finished unit exits through the building's exit point, or — when the
    /// exit is covered by a placed building — through the nearest free 500 mm
    /// lattice node found by a deterministic spiral search.
    /// </summary>
    public sealed class ProductionQueueRegistry
    {
        /// <summary>Default exit: 3 m south of the centre, on the 500 mm lattice.</summary>
        public static readonly WorldPointMm DefaultExitOffsetMm = new WorldPointMm(0, -3000);

        /// <summary>Spiral search cap (32 rings = 16 m); beyond it the exit itself is used.</summary>
        public const int MaxSpawnSearchRings = 32;

        public const int NormalProductionRatePermille = 1000;

        public const int LowPowerProductionRatePermille = 500;

        private readonly BuildingRecord[] _buildings;

        /// <summary>Flat queue storage; building <c>i</c> owns slots [i·9, i·9+9).</summary>
        private readonly ProductionQueueItem[] _queueItems;

        private readonly int _stride;

        private int _count;

        public ProductionQueueRegistry()
        {
            _stride = SimulationConstants.MaxProductionQueueSlots;
            _buildings = new BuildingRecord[SimulationConstants.MaxProductionBuildings];
            _queueItems = new ProductionQueueItem[SimulationConstants.MaxProductionBuildings * SimulationConstants.MaxProductionQueueSlots];
        }

        public int Count => _count;

        /// <summary>
        /// Registers a completed production building. Default rally equals the
        /// exit point with <c>HasRally = false</c> — the first finished unit
        /// leaves through the exit until a rally order arrives.
        /// </summary>
        public bool TryRegisterBuilding(ulong buildingEntityId, PlayerId owner, WorldPointMm center, WorldPointMm? exitPoint = null)
        {
            if (buildingEntityId == 0 || !owner.IsValid)
            {
                return false;
            }

            if (IndexOfBuilding(buildingEntityId) >= 0)
            {
                return false;
            }

            if (_count >= _buildings.Length)
            {
                return false;
            }

            WorldPointMm exit = exitPoint.HasValue
                ? SnapGrid.SnapPosition(exitPoint.Value)
                : new WorldPointMm(center.X + DefaultExitOffsetMm.X, center.Z + DefaultExitOffsetMm.Z);
            _buildings[_count] = new BuildingRecord(buildingEntityId, owner, center, exit, exit, false, 0, 0);
            _count++;
            return true;
        }

        /// <summary>
        /// Removes a destroyed/demolished building: refunds 100% of every queued
        /// order's cost and releases the slot (O(1) swap-and-pop; the moved
        /// building's queue travels with it).
        /// </summary>
        public bool UnregisterBuilding(ulong buildingEntityId, out int totalRefund)
        {
            totalRefund = 0;
            int index = IndexOfBuilding(buildingEntityId);
            if (index < 0)
            {
                return false;
            }

            int baseIndex = index * _stride;
            int queueCount = _buildings[index].QueueCount;
            for (int slot = 0; slot < queueCount; slot++)
            {
                totalRefund += _queueItems[baseIndex + slot].PaidCost;
                _queueItems[baseIndex + slot] = default;
            }

            BuildingRecord moved = _buildings[_count - 1];
            _buildings[index] = moved;
            if (index != _count - 1)
            {
                int movedBase = index * _stride;
                int lastBase = (_count - 1) * _stride;
                for (int slot = 0; slot < moved.QueueCount; slot++)
                {
                    _queueItems[movedBase + slot] = _queueItems[lastBase + slot];
                    _queueItems[lastBase + slot] = default;
                }
            }

            _count--;
            return true;
        }

        /// <summary>
        /// Appends a pre-order. Rejection order: unknown building, foreign
        /// owner, blueprint validity (id ≠ 0, buildTicks ≥ 1, cost ≥ 0), queue
        /// capacity. Enqueueing into an empty queue resets the accumulator.
        /// </summary>
        public ProductionRejection TryEnqueue(ulong buildingEntityId, PlayerId player, ushort blueprintId, ushort buildTicks, int cost)
        {
            int index = IndexOfBuilding(buildingEntityId);
            if (index < 0)
            {
                return ProductionRejection.BuildingNotFound;
            }

            BuildingRecord building = _buildings[index];
            if (building.Owner.Value != player.Value)
            {
                return ProductionRejection.InvalidOwner;
            }

            if (blueprintId == 0 || buildTicks == 0 || cost < 0)
            {
                return ProductionRejection.InvalidBlueprint;
            }

            if (building.QueueCount >= SimulationConstants.MaxProductionQueueSlots)
            {
                return ProductionRejection.QueueFull;
            }

            int accumulator = building.QueueCount == 0 ? 0 : building.WorkMilliAccumulator;
            _queueItems[(index * _stride) + building.QueueCount] =
                new ProductionQueueItem(blueprintId, buildTicks, buildTicks, cost);
            _buildings[index] = building.WithQueue(building.QueueCount + 1, accumulator);
            return ProductionRejection.None;
        }

        /// <summary>
        /// Cancels one queued order with a full (100%) refund. Cancelling the
        /// head (slot 0) discards its accumulated tick progress; cancelling a
        /// middle slot compacts the tail left. Generals canon: never a partial
        /// refund.
        /// </summary>
        public ProductionRejection TryCancel(ulong buildingEntityId, PlayerId player, int slotIndex, out int refundCost)
        {
            refundCost = 0;
            int index = IndexOfBuilding(buildingEntityId);
            if (index < 0)
            {
                return ProductionRejection.BuildingNotFound;
            }

            BuildingRecord building = _buildings[index];
            if (building.Owner.Value != player.Value)
            {
                return ProductionRejection.InvalidOwner;
            }

            if (slotIndex < 0 || slotIndex >= building.QueueCount)
            {
                return ProductionRejection.InvalidSlotIndex;
            }

            int baseIndex = index * _stride;
            refundCost = _queueItems[baseIndex + slotIndex].PaidCost;
            int accumulator = slotIndex == 0 ? 0 : building.WorkMilliAccumulator;
            for (int slot = slotIndex + 1; slot < building.QueueCount; slot++)
            {
                _queueItems[baseIndex + slot - 1] = _queueItems[baseIndex + slot];
            }

            _queueItems[baseIndex + building.QueueCount - 1] = default;
            _buildings[index] = building.WithQueue(building.QueueCount - 1, accumulator);
            return ProductionRejection.None;
        }

        /// <summary>
        /// Sets the rally point: bounds-checked raw, then snapped onto the
        /// lattice. <c>HasRally</c> stays true until the building is destroyed.
        /// </summary>
        public SetRallyRejection TrySetRallyPoint(ulong buildingEntityId, PlayerId player, WorldPointMm rallyPoint)
        {
            int index = IndexOfBuilding(buildingEntityId);
            if (index < 0)
            {
                return SetRallyRejection.BuildingNotFound;
            }

            BuildingRecord building = _buildings[index];
            if (building.Owner.Value != player.Value)
            {
                return SetRallyRejection.InvalidOwner;
            }

            if (!SnapGrid.ValidateInBounds(rallyPoint))
            {
                return SetRallyRejection.OutOfBounds;
            }

            _buildings[index] = building.WithRally(SnapGrid.SnapPosition(rallyPoint), true);
            return SetRallyRejection.None;
        }

        public bool TryGetBuildingState(ulong buildingEntityId, out BuildingProductionState state)
        {
            int index = IndexOfBuilding(buildingEntityId);
            if (index < 0)
            {
                state = default;
                return false;
            }

            BuildingRecord building = _buildings[index];
            state = new BuildingProductionState(
                building.EntityId, building.Owner, building.Center, building.ExitPoint,
                building.RallyPoint, building.HasRally, building.QueueCount, building.WorkMilliAccumulator);
            return true;
        }

        public bool TryGetQueueItem(ulong buildingEntityId, int slotIndex, out ProductionQueueItem item)
        {
            int index = IndexOfBuilding(buildingEntityId);
            if (index < 0 || slotIndex < 0 || slotIndex >= _buildings[index].QueueCount)
            {
                item = default;
                return false;
            }

            item = _queueItems[(index * _stride) + slotIndex];
            return true;
        }

        /// <summary>
        /// Advances every active queue one tick. At most one unit per building
        /// can finish per tick (the accumulator can never reach 2000‰), so a
        /// span of <see cref="SimulationConstants.MaxProductionBuildings"/> is
        /// always sufficient — a smaller span is a caller bug and throws.
        /// </summary>
        /// <returns>Number of units completed this tick.</returns>
        public int AdvanceTick(PowerLedger powerLedger, BuildingPlacementRegistry placement, Span<ProductionCompletedUnit> completedOut)
        {
            if (completedOut.Length < SimulationConstants.MaxProductionBuildings)
            {
                throw new ArgumentException(
                    "The completion span must hold one entry per production building.", nameof(completedOut));
            }

            int completedCount = 0;
            for (int index = 0; index < _count; index++)
            {
                BuildingRecord building = _buildings[index];
                int queueCount = building.QueueCount;
                if (queueCount == 0)
                {
                    continue;
                }

                bool isLowPower = powerLedger != null
                    && powerLedger.GetPowerState(building.Owner).IsLowPower
                    && !powerLedger.IsPowerExempt(building.Owner);
                int rateMilli = isLowPower ? LowPowerProductionRatePermille : NormalProductionRatePermille;
                int accumulator = building.WorkMilliAccumulator + rateMilli;

                int baseIndex = index * _stride;
                while (accumulator >= 1000 && _queueItems[baseIndex].RemainingTicks > 0)
                {
                    ProductionQueueItem head = _queueItems[baseIndex];
                    _queueItems[baseIndex] = head.WithRemainingTicks((ushort)(head.RemainingTicks - 1));
                    accumulator -= 1000;
                }

                if (_queueItems[baseIndex].RemainingTicks == 0)
                {
                    ProductionQueueItem finished = _queueItems[baseIndex];
                    WorldPointMm spawnPosition = ResolveSpawnPosition(building, placement);
                    completedOut[completedCount++] = new ProductionCompletedUnit(
                        building.EntityId, building.Owner, finished.BlueprintId,
                        spawnPosition, building.RallyPoint, building.HasRally);

                    for (int slot = 1; slot < queueCount; slot++)
                    {
                        _queueItems[baseIndex + slot - 1] = _queueItems[baseIndex + slot];
                    }

                    _queueItems[baseIndex + queueCount - 1] = default;
                    queueCount--;
                    if (queueCount == 0)
                    {
                        accumulator = 0;
                    }
                }

                _buildings[index] = building.WithQueue(queueCount, accumulator);
            }

            return completedCount;
        }

        /// <summary>Full reset (match teardown).</summary>
        public void Clear()
        {
            for (int index = 0; index < _count; index++)
            {
                int baseIndex = index * _stride;
                for (int slot = 0; slot < _buildings[index].QueueCount; slot++)
                {
                    _queueItems[baseIndex + slot] = default;
                }
            }

            _count = 0;
        }

        private WorldPointMm ResolveSpawnPosition(BuildingRecord building, BuildingPlacementRegistry placement)
        {
            if (placement is null || !placement.IsPointOccupied(building.ExitPoint))
            {
                return building.ExitPoint;
            }

            // Deterministic spiral over the 500 mm lattice: rings grow outward,
            // each ring scanned clockwise starting from its north edge — the
            // first in-bounds, unoccupied node wins.
            for (int ring = 1; ring <= MaxSpawnSearchRings; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (TryFreeNode(placement, building.ExitPoint, dx, -ring, out WorldPointMm node))
                    {
                        return node;
                    }
                }

                for (int dz = -ring + 1; dz <= ring; dz++)
                {
                    if (TryFreeNode(placement, building.ExitPoint, ring, dz, out WorldPointMm node))
                    {
                        return node;
                    }
                }

                for (int dx = ring - 1; dx >= -ring; dx--)
                {
                    if (TryFreeNode(placement, building.ExitPoint, dx, ring, out WorldPointMm node))
                    {
                        return node;
                    }
                }

                for (int dz = ring - 1; dz >= -ring + 1; dz--)
                {
                    if (TryFreeNode(placement, building.ExitPoint, -ring, dz, out WorldPointMm node))
                    {
                        return node;
                    }
                }
            }

            return building.ExitPoint;
        }

        private static bool TryFreeNode(
            BuildingPlacementRegistry placement,
            WorldPointMm origin,
            int dxSteps,
            int dzSteps,
            out WorldPointMm node)
        {
            node = new WorldPointMm(
                origin.X + (dxSteps * SnapGrid.DefaultSnapStepMm),
                origin.Z + (dzSteps * SnapGrid.DefaultSnapStepMm));
            return SnapGrid.ValidateInBounds(node) && !placement.IsPointOccupied(node);
        }

        private int IndexOfBuilding(ulong buildingEntityId)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_buildings[i].EntityId == buildingEntityId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Internal building row; every update writes an immutable copy back.</summary>
        private readonly struct BuildingRecord
        {
            public BuildingRecord(
                ulong entityId,
                PlayerId owner,
                WorldPointMm center,
                WorldPointMm exitPoint,
                WorldPointMm rallyPoint,
                bool hasRally,
                int queueCount,
                int workMilliAccumulator)
            {
                EntityId = entityId;
                Owner = owner;
                Center = center;
                ExitPoint = exitPoint;
                RallyPoint = rallyPoint;
                HasRally = hasRally;
                QueueCount = queueCount;
                WorkMilliAccumulator = workMilliAccumulator;
            }

            public ulong EntityId { get; }

            public PlayerId Owner { get; }

            public WorldPointMm Center { get; }

            public WorldPointMm ExitPoint { get; }

            public WorldPointMm RallyPoint { get; }

            public bool HasRally { get; }

            public int QueueCount { get; }

            public int WorkMilliAccumulator { get; }

            public BuildingRecord WithQueue(int queueCount, int workMilliAccumulator)
            {
                return new BuildingRecord(
                    EntityId, Owner, Center, ExitPoint, RallyPoint, HasRally,
                    queueCount, workMilliAccumulator);
            }

            public BuildingRecord WithRally(WorldPointMm rallyPoint, bool hasRally)
            {
                return new BuildingRecord(
                    EntityId, Owner, Center, ExitPoint, rallyPoint, hasRally,
                    QueueCount, WorkMilliAccumulator);
            }
        }
    }
}
