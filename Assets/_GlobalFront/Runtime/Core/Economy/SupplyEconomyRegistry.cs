using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Economy
{
    /// <summary>
    /// Pre-allocated registry of the supply economy: neutral docks with finite
    /// reserves and 4 loading slots each, player-owned supply centers with
    /// instant unloading, and harvesters driven by a deterministic tick FSM
    /// (Idle → ToDock → WaitSlot → Loading(40) → ToCenter → Unload(0) → …).
    ///
    /// Zero-GC hot path: fixed backing arrays, for-loops only. Registry arrays
    /// are kept sorted by EntityId (sorted insert on register, ordered
    /// compaction on unregister — both event-driven paths), so the per-tick
    /// iteration is canonically ordered and distance tie-breaks resolve to the
    /// smallest EntityId for free.
    ///
    /// Architectural accents (Step 5.4.1): distances are squared in `long`
    /// (int overflows past ~46 km of squared range); a transition tick never
    /// also moves the harvester toward its new target — StepTowards begins on
    /// the following tick, guaranteeing at most one movement step per tick.
    /// </summary>
    public sealed class SupplyEconomyRegistry
    {
        private readonly PlayerEconomyState[] _players;

        private readonly DockRecord[] _docks;

        private readonly CenterRecord[] _centers;

        private readonly HarvesterRecord[] _harvesters;

        /// <summary>Flat loading slots: dock i owns [i·4, i·4+4); 0 = free.</summary>
        private readonly ulong[] _dockSlots;

        /// <summary>Flat waiting queues: dock i owns [i·16, i·16+16).</summary>
        private readonly WaitingEntry[] _dockWaiting;

        private readonly int[] _dockWaitingCounts;

        private readonly int _dockSlotStride;

        private readonly int _dockWaitingStride;

        private int _dockCount;

        private int _centerCount;

        private int _harvesterCount;

        /// <summary>Internal tick counter stamping queue arrivals for the (TickArrived, EntityId) order.</summary>
        private ulong _tick;

        public SupplyEconomyRegistry()
        {
            _players = new PlayerEconomyState[SimulationConstants.MaxPlayers + 1];
            for (int value = 0; value <= SimulationConstants.MaxPlayers; value++)
            {
                _players[value] = new PlayerEconomyState(new PlayerId((byte)value), 0, 0);
            }

            _dockSlotStride = SimulationConstants.SupplyDockSlots;
            _dockWaitingStride = SimulationConstants.MaxWaitingHarvestersPerDock;
            _docks = new DockRecord[SimulationConstants.MaxSupplyDocks];
            _centers = new CenterRecord[SimulationConstants.MaxSupplyCenters];
            _harvesters = new HarvesterRecord[SimulationConstants.MaxHarvesters];
            _dockSlots = new ulong[SimulationConstants.MaxSupplyDocks * _dockSlotStride];
            _dockWaiting = new WaitingEntry[SimulationConstants.MaxSupplyDocks * _dockWaitingStride];
            _dockWaitingCounts = new int[SimulationConstants.MaxSupplyDocks];
        }

        public int DockCount => _dockCount;

        public int CenterCount => _centerCount;

        public int HarvesterCount => _harvesterCount;

        // ------------------------------------------------------------------
        // registration
        // ------------------------------------------------------------------

        /// <summary>
        /// Registers a neutral supply dock with a finite reserve. Docks have no
        /// owner — anyone may load from them (Generals canon).
        /// </summary>
        public bool TryRegisterDock(ulong entityId, WorldPointMm position, int initialDollars, out EconomyRegistrationRejection rejection)
        {
            if (initialDollars <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialDollars), initialDollars, "A dock reserve must be strictly positive.");
            }

            if (entityId == 0 || IsKnownEntity(entityId))
            {
                rejection = entityId == 0 ? EconomyRegistrationRejection.InvalidEntityId : EconomyRegistrationRejection.DuplicateEntity;
                return false;
            }

            if (_dockCount >= _docks.Length)
            {
                rejection = EconomyRegistrationRejection.RegistryFull;
                return false;
            }

            int insertIndex = FindDockInsertIndex(entityId);
            for (int i = _dockCount - 1; i >= insertIndex; i--)
            {
                _docks[i + 1] = _docks[i];
                int sourceSlots = i * _dockSlotStride;
                int targetSlots = (i + 1) * _dockSlotStride;
                for (int slot = 0; slot < _dockSlotStride; slot++)
                {
                    _dockSlots[targetSlots + slot] = _dockSlots[sourceSlots + slot];
                    _dockSlots[sourceSlots + slot] = 0;
                }

                int sourceWaiting = i * _dockWaitingStride;
                int targetWaiting = (i + 1) * _dockWaitingStride;
                for (int entry = 0; entry < _dockWaitingStride; entry++)
                {
                    _dockWaiting[targetWaiting + entry] = _dockWaiting[sourceWaiting + entry];
                    _dockWaiting[sourceWaiting + entry] = default;
                }

                _dockWaitingCounts[i + 1] = _dockWaitingCounts[i];
                _dockWaitingCounts[i] = 0;
            }

            _docks[insertIndex] = new DockRecord(entityId, position, initialDollars, initialDollars);
            _dockCount++;
            rejection = EconomyRegistrationRejection.None;
            return true;
        }

        /// <summary>Registers a player-owned supply center (the unload destination).</summary>
        public bool TryRegisterCenter(ulong entityId, PlayerId owner, WorldPointMm position, out EconomyRegistrationRejection rejection)
        {
            if (entityId == 0 || IsKnownEntity(entityId))
            {
                rejection = entityId == 0 ? EconomyRegistrationRejection.InvalidEntityId : EconomyRegistrationRejection.DuplicateEntity;
                return false;
            }

            if (!owner.IsValid)
            {
                rejection = EconomyRegistrationRejection.InvalidOwner;
                return false;
            }

            if (_centerCount >= _centers.Length)
            {
                rejection = EconomyRegistrationRejection.RegistryFull;
                return false;
            }

            int insertIndex = FindCenterInsertIndex(entityId);
            for (int i = _centerCount - 1; i >= insertIndex; i--)
            {
                _centers[i + 1] = _centers[i];
            }

            _centers[insertIndex] = new CenterRecord(entityId, owner, position);
            _centerCount++;
            rejection = EconomyRegistrationRejection.None;
            return true;
        }

        /// <summary>
        /// Registers a harvester; it starts in <see cref="HarvesterState.Idle"/>
        /// and seeks the nearest non-empty dock on the next tick.
        /// </summary>
        public bool TryRegisterHarvester(
            ulong entityId,
            PlayerId owner,
            WorldPointMm position,
            int speedMmPerTick,
            int capacityDollars,
            out EconomyRegistrationRejection rejection)
        {
            if (entityId == 0 || IsKnownEntity(entityId))
            {
                rejection = entityId == 0 ? EconomyRegistrationRejection.InvalidEntityId : EconomyRegistrationRejection.DuplicateEntity;
                return false;
            }

            if (!owner.IsValid)
            {
                rejection = EconomyRegistrationRejection.InvalidOwner;
                return false;
            }

            if (speedMmPerTick <= 0 || capacityDollars <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    speedMmPerTick <= 0 ? nameof(speedMmPerTick) : nameof(capacityDollars),
                    speedMmPerTick <= 0 ? speedMmPerTick : capacityDollars,
                    "Harvester speed and capacity must be strictly positive.");
            }

            if (_harvesterCount >= _harvesters.Length)
            {
                rejection = EconomyRegistrationRejection.RegistryFull;
                return false;
            }

            int insertIndex = FindHarvesterInsertIndex(entityId);
            for (int i = _harvesterCount - 1; i >= insertIndex; i--)
            {
                _harvesters[i + 1] = _harvesters[i];
            }

            _harvesters[insertIndex] = new HarvesterRecord(
                entityId, owner, position, speedMmPerTick, capacityDollars,
                0, HarvesterState.Idle, 0, 0, 0);
            _harvesterCount++;
            rejection = EconomyRegistrationRejection.None;
            return true;
        }

        /// <summary>
        /// Removes a dock, center or harvester. A removed dock releases its
        /// slots and queue (harvesters bound to it fall back to Idle with their
        /// cargo kept); a removed center makes bound harvesters re-seek the
        /// nearest friendly center or fall back to Idle; a removed harvester
        /// frees its slot or queue position.
        /// </summary>
        public bool UnregisterEntity(ulong entityId)
        {
            if (entityId == 0)
            {
                return false;
            }

            int dockIndex = IndexOfDock(entityId);
            if (dockIndex >= 0)
            {
                RemoveDockAt(dockIndex);
                for (int i = 0; i < _harvesterCount; i++)
                {
                    if (_harvesters[i].TargetDockId == entityId &&
                        _harvesters[i].State != HarvesterState.Idle &&
                        _harvesters[i].State != HarvesterState.ToCenter)
                    {
                        _harvesters[i] = _harvesters[i].With(
                            HarvesterState.Idle, _harvesters[i].Position, _harvesters[i].CarriedDollars, 0, 0, 0);
                    }
                }

                return true;
            }

            int centerIndex = IndexOfCenter(entityId);
            if (centerIndex >= 0)
            {
                RemoveCenterAt(centerIndex);
                for (int i = 0; i < _harvesterCount; i++)
                {
                    if (_harvesters[i].TargetCenterId == entityId && _harvesters[i].State == HarvesterState.ToCenter)
                    {
                        _harvesters[i] = RetargetToNearestCenterOrIdle(_harvesters[i]);
                    }
                }

                return true;
            }

            int harvesterIndex = IndexOfHarvester(entityId);
            if (harvesterIndex >= 0)
            {
                ReleaseHarvesterBindings(_harvesters[harvesterIndex]);
                RemoveHarvesterAt(harvesterIndex);
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // credit ledger
        // ------------------------------------------------------------------

        public void SetCredits(PlayerId player, int credits)
        {
            int playerIndex = PlayerIndex(player);
            if (credits < 0)
            {
                credits = 0;
            }

            if (credits > SimulationConstants.MaxCredits)
            {
                credits = SimulationConstants.MaxCredits;
            }

            _players[playerIndex] = new PlayerEconomyState(player, credits, _players[playerIndex].TotalHarvestedCredits);
        }

        public int GetCredits(PlayerId player)
        {
            return _players[PlayerIndex(player)].Credits;
        }

        /// <summary>Full per-player ledger snapshot (credits + harvested statistics).</summary>
        public PlayerEconomyState GetPlayerState(PlayerId player)
        {
            return _players[PlayerIndex(player)];
        }

        /// <summary>Atomically spends credits: succeeds only when the balance covers the cost.</summary>
        public bool TrySpendCredits(PlayerId player, int cost)
        {
            if (cost < 0)
            {
                return false;
            }

            int playerIndex = PlayerIndex(player);
            int credits = _players[playerIndex].Credits;
            if (credits < cost)
            {
                return false;
            }

            _players[playerIndex] = new PlayerEconomyState(player, credits - cost, _players[playerIndex].TotalHarvestedCredits);
            return true;
        }

        /// <summary>Adds income with saturation at <see cref="SimulationConstants.MaxCredits"/>.</summary>
        public void AddCredits(PlayerId player, int amount)
        {
            int playerIndex = PlayerIndex(player);
            _players[playerIndex] = _players[playerIndex].WithCreditDelta(amount);
        }

        // ------------------------------------------------------------------
        // snapshots
        // ------------------------------------------------------------------

        public bool TryGetHarvesterState(ulong entityId, out HarvesterSnapshot snapshot)
        {
            int index = IndexOfHarvester(entityId);
            if (index < 0)
            {
                snapshot = default;
                return false;
            }

            HarvesterRecord record = _harvesters[index];
            snapshot = new HarvesterSnapshot(
                record.EntityId, record.Owner, record.State, record.TargetDockId, record.TargetCenterId,
                record.CarriedDollars, record.CapacityDollars, record.LoadingTicksRemaining);
            return true;
        }

        public bool TryGetDockState(ulong entityId, out SupplyDockSnapshot snapshot)
        {
            int index = IndexOfDock(entityId);
            if (index < 0)
            {
                snapshot = default;
                return false;
            }

            DockRecord record = _docks[index];
            int occupied = 0;
            int slotBase = index * _dockSlotStride;
            for (int slot = 0; slot < _dockSlotStride; slot++)
            {
                if (_dockSlots[slotBase + slot] != 0)
                {
                    occupied++;
                }
            }

            snapshot = new SupplyDockSnapshot(
                record.EntityId, record.Position, record.RemainingDollars, record.InitialDollars,
                occupied, _dockWaitingCounts[index]);
            return true;
        }

        /// <summary>
        /// Copies every registered entity id (docks, centers, harvesters) into
        /// destination in canonical EntityId order. The server uses this for
        /// the per-tick liveness sweep: an id that is present in the roster and
        /// dead is unregistered. Zero-GC; returns the number of ids written.
        /// </summary>
        public int CopyEntityIds(Span<ulong> destination)
        {
            int total = _dockCount + _centerCount + _harvesterCount;
            if (destination.Length < total)
            {
                throw new ArgumentException(
                    $"Destination span holds {destination.Length} ids, registry has {total}.", nameof(destination));
            }

            int offset = 0;
            for (int index = 0; index < _dockCount; index++)
            {
                destination[offset++] = _docks[index].EntityId;
            }

            for (int index = 0; index < _centerCount; index++)
            {
                destination[offset++] = _centers[index].EntityId;
            }

            for (int index = 0; index < _harvesterCount; index++)
            {
                destination[offset++] = _harvesters[index].EntityId;
            }

            return total;
        }

        /// <summary>
        /// Copies the authoritative position of every harvester into
        /// destination. The server mirrors these positions into its roster so
        /// harvester movement replicates to clients (Step 5.4.2). Zero-GC;
        /// returns the number of entries written.
        /// </summary>
        public int CopyHarvesterSync(Span<HarvesterSyncEntry> destination)
        {
            if (destination.Length < _harvesterCount)
            {
                throw new ArgumentException(
                    $"Destination span holds {destination.Length} entries, registry has {_harvesterCount} harvesters.",
                    nameof(destination));
            }

            for (int index = 0; index < _harvesterCount; index++)
            {
                HarvesterRecord record = _harvesters[index];
                destination[index] = new HarvesterSyncEntry(record.EntityId, record.Position);
            }

            return _harvesterCount;
        }

        // ------------------------------------------------------------------
        // per-tick advance
        // ------------------------------------------------------------------

        /// <summary>
        /// Advances every harvester one tick in canonical EntityId order.
        /// Zero-GC: fixed arrays, for-loops, no allocations.
        /// </summary>
        public void AdvanceTick()
        {
            _tick++;
            for (int index = 0; index < _harvesterCount; index++)
            {
                HarvesterRecord harvester = _harvesters[index];
                switch (harvester.State)
                {
                    case HarvesterState.Idle:
                        harvester = AdvanceIdle(harvester);
                        break;
                    case HarvesterState.ToDock:
                        harvester = AdvanceToDock(harvester);
                        break;
                    case HarvesterState.WaitSlot:
                        break; // passive: promotion happens when a slot is released
                    case HarvesterState.Loading:
                        harvester = AdvanceLoading(harvester);
                        break;
                    case HarvesterState.ToCenter:
                        harvester = AdvanceToCenter(harvester);
                        break;
                    case HarvesterState.Unload:
                        break; // reserved: unloading is instant and never persisted
                }

                _harvesters[index] = harvester;
            }
        }

        public void Clear()
        {
            for (int index = 0; index < _dockCount; index++)
            {
                int slotBase = index * _dockSlotStride;
                for (int slot = 0; slot < _dockSlotStride; slot++)
                {
                    _dockSlots[slotBase + slot] = 0;
                }

                int waitingBase = index * _dockWaitingStride;
                for (int entry = 0; entry < _dockWaitingStride; entry++)
                {
                    _dockWaiting[waitingBase + entry] = default;
                }

                _dockWaitingCounts[index] = 0;
            }

            for (int value = 0; value <= SimulationConstants.MaxPlayers; value++)
            {
                _players[value] = new PlayerEconomyState(new PlayerId((byte)value), 0, 0);
            }

            _dockCount = 0;
            _centerCount = 0;
            _harvesterCount = 0;
            _tick = 0;
        }

        // ------------------------------------------------------------------
        // FSM stages
        // ------------------------------------------------------------------

        private HarvesterRecord AdvanceIdle(HarvesterRecord harvester)
        {
            // Cargo first: deliver before topping up.
            if (harvester.CarriedDollars > 0)
            {
                int centerIndex = FindNearestFriendlyCenter(harvester);
                if (centerIndex >= 0)
                {
                    return harvester.With(
                        HarvesterState.ToCenter, harvester.Position, harvester.CarriedDollars,
                        0, _centers[centerIndex].EntityId, 0);
                }
            }

            int dockIndex = FindNearestNonEmptyDock(harvester);
            if (dockIndex >= 0)
            {
                return harvester.With(
                    HarvesterState.ToDock, harvester.Position, harvester.CarriedDollars,
                    _docks[dockIndex].EntityId, 0, 0);
            }

            return harvester; // every dock is depleted (or no center for the cargo)
        }

        private HarvesterRecord AdvanceToDock(HarvesterRecord harvester)
        {
            int dockIndex = IndexOfDock(harvester.TargetDockId);
            if (dockIndex < 0 || _docks[dockIndex].RemainingDollars == 0)
            {
                // The target vanished or depleted mid-path: re-seek (transition
                // tick — no movement).
                dockIndex = FindNearestNonEmptyDock(harvester);
                if (dockIndex < 0)
                {
                    return harvester.With(HarvesterState.Idle, harvester.Position, harvester.CarriedDollars, 0, 0, 0);
                }

                harvester = harvester.With(
                    HarvesterState.ToDock, harvester.Position, harvester.CarriedDollars,
                    _docks[dockIndex].EntityId, 0, 0);
            }

            DockRecord dock = _docks[dockIndex];
            WorldPointMm position = harvester.Position;
            if (position.X != dock.Position.X || position.Z != dock.Position.Z)
            {
                position = PlanarMovement.StepTowards(position, dock.Position, harvester.SpeedMmPerTick);
            }

            if (!CombatMath.IsWithinRange(position, dock.Position, SimulationConstants.DefaultEconomyInteractionRadiusMm))
            {
                return harvester.With(HarvesterState.ToDock, position, harvester.CarriedDollars, dock.EntityId, 0, 0);
            }

            if (dock.RemainingDollars == 0)
            {
                // Arrived to find the dock empty: re-seek without occupying anything.
                dockIndex = FindNearestNonEmptyDock(harvester);
                if (dockIndex < 0)
                {
                    return harvester.With(HarvesterState.Idle, position, harvester.CarriedDollars, 0, 0, 0);
                }

                return harvester.With(
                    HarvesterState.ToDock, position, harvester.CarriedDollars,
                    _docks[dockIndex].EntityId, 0, 0);
            }

            int freeSlot = FindFreeSlot(dockIndex);
            if (freeSlot >= 0)
            {
                _dockSlots[dockIndex * _dockSlotStride + freeSlot] = harvester.EntityId;
                return harvester.With(
                    HarvesterState.Loading, position, harvester.CarriedDollars,
                    dock.EntityId, 0, SimulationConstants.DefaultLoadingDurationTicks);
            }

            int waitingCount = _dockWaitingCounts[dockIndex];
            if (waitingCount < SimulationConstants.MaxWaitingHarvestersPerDock)
            {
                _dockWaiting[dockIndex * _dockWaitingStride + waitingCount] =
                    new WaitingEntry(_tick, harvester.EntityId);
                _dockWaitingCounts[dockIndex] = waitingCount + 1;
            }
            else
            {
                // Queue full (beyond 16 waiters): the harvester holds its ground
                // as an unqueued waiter and is never promoted until a queue
                // position frees up. Deterministic and bounded.
            }

            return harvester.With(HarvesterState.WaitSlot, position, harvester.CarriedDollars, dock.EntityId, 0, 0);
        }

        private HarvesterRecord AdvanceLoading(HarvesterRecord harvester)
        {
            int loadingTicksRemaining = harvester.LoadingTicksRemaining - 1;
            if (loadingTicksRemaining > 0)
            {
                return harvester.With(
                    HarvesterState.Loading, harvester.Position, harvester.CarriedDollars,
                    harvester.TargetDockId, 0, loadingTicksRemaining);
            }

            int dockIndex = IndexOfDock(harvester.TargetDockId);
            if (dockIndex < 0)
            {
                // The dock vanished mid-loading: the cargo is kept, re-seek from Idle.
                return harvester.With(HarvesterState.Idle, harvester.Position, harvester.CarriedDollars, 0, 0, 0);
            }

            DockRecord dock = _docks[dockIndex];
            int space = harvester.CapacityDollars - harvester.CarriedDollars;
            int take = Math.Min(space, dock.RemainingDollars);
            dock = dock.WithRemaining(dock.RemainingDollars - take);
            _docks[dockIndex] = dock;
            int carried = harvester.CarriedDollars + take;

            FreeSlot(dockIndex, harvester.EntityId);
            PromoteQueueHeads(dockIndex);

            if (carried > 0)
            {
                int centerIndex = FindNearestFriendlyCenter(harvester);
                if (centerIndex >= 0)
                {
                    return harvester.With(
                        HarvesterState.ToCenter, harvester.Position, carried,
                        0, _centers[centerIndex].EntityId, 0);
                }

                return harvester.With(HarvesterState.Idle, harvester.Position, carried, 0, 0, 0);
            }

            // Nothing to take (dock emptied between cycles): re-seek from Idle.
            return harvester.With(HarvesterState.Idle, harvester.Position, carried, 0, 0, 0);
        }

        private HarvesterRecord AdvanceToCenter(HarvesterRecord harvester)
        {
            int centerIndex = IndexOfCenter(harvester.TargetCenterId);
            if (centerIndex < 0)
            {
                // The center vanished mid-path: re-seek (transition tick — no movement).
                centerIndex = FindNearestFriendlyCenter(harvester);
                if (centerIndex < 0)
                {
                    return harvester.With(HarvesterState.Idle, harvester.Position, harvester.CarriedDollars, 0, 0, 0);
                }

                harvester = harvester.With(
                    HarvesterState.ToCenter, harvester.Position, harvester.CarriedDollars,
                    0, _centers[centerIndex].EntityId, 0);
            }

            CenterRecord center = _centers[centerIndex];
            WorldPointMm position = harvester.Position;
            if (position.X != center.Position.X || position.Z != center.Position.Z)
            {
                position = PlanarMovement.StepTowards(position, center.Position, harvester.SpeedMmPerTick);
            }

            if (!CombatMath.IsWithinRange(position, center.Position, SimulationConstants.DefaultEconomyInteractionRadiusMm))
            {
                return harvester.With(HarvesterState.ToCenter, position, harvester.CarriedDollars, 0, center.EntityId, 0);
            }

            // Unload: instant (0 ticks). Saturating credit add, income recorded.
            _players[center.Owner.Value] = _players[center.Owner.Value].WithCreditDelta(harvester.CarriedDollars);

            int dockIndex = IndexOfDock(harvester.TargetDockId);
            if (dockIndex >= 0 && _docks[dockIndex].RemainingDollars > 0)
            {
                return harvester.With(HarvesterState.ToDock, position, 0, harvester.TargetDockId, 0, 0);
            }

            dockIndex = FindNearestNonEmptyDock(harvester);
            if (dockIndex >= 0)
            {
                return harvester.With(HarvesterState.ToDock, position, 0, _docks[dockIndex].EntityId, 0, 0);
            }

            return harvester.With(HarvesterState.Idle, position, 0, 0, 0, 0);
        }

        // ------------------------------------------------------------------
        // slot and queue plumbing
        // ------------------------------------------------------------------

        private int FindFreeSlot(int dockIndex)
        {
            int baseIndex = dockIndex * _dockSlotStride;
            for (int slot = 0; slot < _dockSlotStride; slot++)
            {
                if (_dockSlots[baseIndex + slot] == 0)
                {
                    return slot;
                }
            }

            return -1;
        }

        private void FreeSlot(int dockIndex, ulong harvesterEntityId)
        {
            int baseIndex = dockIndex * _dockSlotStride;
            for (int slot = 0; slot < _dockSlotStride; slot++)
            {
                if (_dockSlots[baseIndex + slot] == harvesterEntityId)
                {
                    _dockSlots[baseIndex + slot] = 0;
                    return;
                }
            }
        }

        /// <summary>
        /// Fills free slots of a dock from its waiting queue, promoting heads in
        /// (TickArrived, EntityId) order until no slot or no waiter remains.
        /// </summary>
        private void PromoteQueueHeads(int dockIndex)
        {
            int waitingCount = _dockWaitingCounts[dockIndex];
            while (waitingCount > 0)
            {
                int freeSlot = FindFreeSlot(dockIndex);
                if (freeSlot < 0)
                {
                    return;
                }

                WaitingEntry head = _dockWaiting[dockIndex * _dockWaitingStride];
                int harvesterIndex = IndexOfHarvester(head.EntityId);
                if (harvesterIndex < 0)
                {
                    // Stale entry (its harvester was unregistered): drop it.
                    RemoveWaitingEntryAt(dockIndex, 0);
                    waitingCount = _dockWaitingCounts[dockIndex];
                    continue;
                }

                HarvesterRecord waiting = _harvesters[harvesterIndex];
                _dockSlots[dockIndex * _dockSlotStride + freeSlot] = head.EntityId;
                _harvesters[harvesterIndex] = waiting.With(
                    HarvesterState.Loading, waiting.Position, waiting.CarriedDollars,
                    _docks[dockIndex].EntityId, 0, SimulationConstants.DefaultLoadingDurationTicks);
                RemoveWaitingEntryAt(dockIndex, 0);
                waitingCount = _dockWaitingCounts[dockIndex];
            }
        }

        private void RemoveWaitingEntryAt(int dockIndex, int position)
        {
            int baseIndex = dockIndex * _dockWaitingStride;
            int count = _dockWaitingCounts[dockIndex];
            for (int entry = position + 1; entry < count; entry++)
            {
                _dockWaiting[baseIndex + entry - 1] = _dockWaiting[baseIndex + entry];
            }

            _dockWaiting[baseIndex + count - 1] = default;
            _dockWaitingCounts[dockIndex] = count - 1;
        }

        private void ReleaseHarvesterBindings(HarvesterRecord harvester)
        {
            if (harvester.State == HarvesterState.Loading)
            {
                int dockIndex = IndexOfDock(harvester.TargetDockId);
                if (dockIndex >= 0)
                {
                    FreeSlot(dockIndex, harvester.EntityId);
                    PromoteQueueHeads(dockIndex);
                }
            }
            else if (harvester.State == HarvesterState.WaitSlot)
            {
                int dockIndex = IndexOfDock(harvester.TargetDockId);
                if (dockIndex >= 0)
                {
                    RemoveWaitingEntryFor(dockIndex, harvester.EntityId);
                }
            }
        }

        private void RemoveWaitingEntryFor(int dockIndex, ulong harvesterEntityId)
        {
            int baseIndex = dockIndex * _dockWaitingStride;
            int count = _dockWaitingCounts[dockIndex];
            for (int entry = 0; entry < count; entry++)
            {
                if (_dockWaiting[baseIndex + entry].EntityId == harvesterEntityId)
                {
                    RemoveWaitingEntryAt(dockIndex, entry);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // nearest searches (long-squared distances, ties → smallest EntityId)
        // ------------------------------------------------------------------

        private int FindNearestNonEmptyDock(HarvesterRecord harvester)
        {
            int best = -1;
            long bestDistanceSquared = long.MaxValue;
            for (int i = 0; i < _dockCount; i++)
            {
                if (_docks[i].RemainingDollars <= 0)
                {
                    continue;
                }

                long distanceSquared = DistanceSquaredMm(harvester.Position, _docks[i].Position);
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    best = i;
                }
            }

            return best;
        }

        private int FindNearestFriendlyCenter(HarvesterRecord harvester)
        {
            int best = -1;
            long bestDistanceSquared = long.MaxValue;
            for (int i = 0; i < _centerCount; i++)
            {
                if (_centers[i].Owner.Value != harvester.Owner.Value)
                {
                    continue;
                }

                long distanceSquared = DistanceSquaredMm(harvester.Position, _centers[i].Position);
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    best = i;
                }
            }

            return best;
        }

        private HarvesterRecord RetargetToNearestCenterOrIdle(HarvesterRecord harvester)
        {
            int centerIndex = FindNearestFriendlyCenter(harvester);
            if (centerIndex >= 0)
            {
                return harvester.With(
                    HarvesterState.ToCenter, harvester.Position, harvester.CarriedDollars,
                    0, _centers[centerIndex].EntityId, 0);
            }

            return harvester.With(HarvesterState.Idle, harvester.Position, harvester.CarriedDollars, 0, 0, 0);
        }

        private static long DistanceSquaredMm(WorldPointMm a, WorldPointMm b)
        {
            long dx = (long)a.X - b.X;
            long dz = (long)a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        // ------------------------------------------------------------------
        // internals
        // ------------------------------------------------------------------

        /// <summary>
        /// Whether any dock, center or harvester already holds the id. Public
        /// so the server can enforce one global entity-id space across the
        /// supply and secondary registries (Step 5.4.2).
        /// </summary>
        public bool IsKnownEntity(ulong entityId)
        {
            return IndexOfDock(entityId) >= 0 || IndexOfCenter(entityId) >= 0 || IndexOfHarvester(entityId) >= 0;
        }

        private int IndexOfDock(ulong entityId)
        {
            for (int i = 0; i < _dockCount; i++)
            {
                if (_docks[i].EntityId == entityId)
                {
                    return i;
                }
            }

            return -1;
        }

        private int IndexOfCenter(ulong entityId)
        {
            for (int i = 0; i < _centerCount; i++)
            {
                if (_centers[i].EntityId == entityId)
                {
                    return i;
                }
            }

            return -1;
        }

        private int IndexOfHarvester(ulong entityId)
        {
            for (int i = 0; i < _harvesterCount; i++)
            {
                if (_harvesters[i].EntityId == entityId)
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindDockInsertIndex(ulong entityId)
        {
            int index = 0;
            while (index < _dockCount && _docks[index].EntityId < entityId)
            {
                index++;
            }

            return index;
        }

        private int FindCenterInsertIndex(ulong entityId)
        {
            int index = 0;
            while (index < _centerCount && _centers[index].EntityId < entityId)
            {
                index++;
            }

            return index;
        }

        private int FindHarvesterInsertIndex(ulong entityId)
        {
            int index = 0;
            while (index < _harvesterCount && _harvesters[index].EntityId < entityId)
            {
                index++;
            }

            return index;
        }

        private void RemoveDockAt(int position)
        {
            // Shift the sorted records left, moving every slot block and waiting
            // block with its dock; clear the removed dock's own data first.
            int removedSlots = position * _dockSlotStride;
            for (int slot = 0; slot < _dockSlotStride; slot++)
            {
                _dockSlots[removedSlots + slot] = 0;
            }

            int removedWaiting = position * _dockWaitingStride;
            for (int entry = 0; entry < _dockWaitingStride; entry++)
            {
                _dockWaiting[removedWaiting + entry] = default;
            }

            for (int i = position + 1; i < _dockCount; i++)
            {
                _docks[i - 1] = _docks[i];
                int sourceSlots = i * _dockSlotStride;
                int targetSlots = (i - 1) * _dockSlotStride;
                for (int slot = 0; slot < _dockSlotStride; slot++)
                {
                    _dockSlots[targetSlots + slot] = _dockSlots[sourceSlots + slot];
                    _dockSlots[sourceSlots + slot] = 0;
                }

                int sourceWaiting = i * _dockWaitingStride;
                int targetWaiting = (i - 1) * _dockWaitingStride;
                for (int entry = 0; entry < _dockWaitingStride; entry++)
                {
                    _dockWaiting[targetWaiting + entry] = _dockWaiting[sourceWaiting + entry];
                    _dockWaiting[sourceWaiting + entry] = default;
                }

                _dockWaitingCounts[i - 1] = _dockWaitingCounts[i];
                _dockWaitingCounts[i] = 0;
            }

            _dockCount--;
        }

        private void RemoveCenterAt(int position)
        {
            for (int i = position + 1; i < _centerCount; i++)
            {
                _centers[i - 1] = _centers[i];
            }

            _centerCount--;
        }

        private void RemoveHarvesterAt(int position)
        {
            for (int i = position + 1; i < _harvesterCount; i++)
            {
                _harvesters[i - 1] = _harvesters[i];
            }

            _harvesterCount--;
        }

        private int PlayerIndex(PlayerId player)
        {
            if (player.Value < 1 || player.Value > SimulationConstants.MaxPlayers)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(player), player.Value, $"PlayerId must be within [1, {SimulationConstants.MaxPlayers}].");
            }

            return player.Value;
        }

        // ------------------------------------------------------------------
        // records
        // ------------------------------------------------------------------

        private readonly struct DockRecord
        {
            public DockRecord(ulong entityId, WorldPointMm position, int remainingDollars, int initialDollars)
            {
                EntityId = entityId;
                Position = position;
                RemainingDollars = remainingDollars;
                InitialDollars = initialDollars;
            }

            public ulong EntityId { get; }

            public WorldPointMm Position { get; }

            public int RemainingDollars { get; }

            public int InitialDollars { get; }

            public DockRecord WithRemaining(int remainingDollars)
            {
                return new DockRecord(EntityId, Position, remainingDollars, InitialDollars);
            }
        }

        private readonly struct CenterRecord
        {
            public CenterRecord(ulong entityId, PlayerId owner, WorldPointMm position)
            {
                EntityId = entityId;
                Owner = owner;
                Position = position;
            }

            public ulong EntityId { get; }

            public PlayerId Owner { get; }

            public WorldPointMm Position { get; }
        }

        private readonly struct HarvesterRecord
        {
            public HarvesterRecord(
                ulong entityId,
                PlayerId owner,
                WorldPointMm position,
                int speedMmPerTick,
                int capacityDollars,
                int carriedDollars,
                HarvesterState state,
                ulong targetDockId,
                ulong targetCenterId,
                int loadingTicksRemaining)
            {
                EntityId = entityId;
                Owner = owner;
                Position = position;
                SpeedMmPerTick = speedMmPerTick;
                CapacityDollars = capacityDollars;
                CarriedDollars = carriedDollars;
                State = state;
                TargetDockId = targetDockId;
                TargetCenterId = targetCenterId;
                LoadingTicksRemaining = loadingTicksRemaining;
            }

            public ulong EntityId { get; }

            public PlayerId Owner { get; }

            public WorldPointMm Position { get; }

            public int SpeedMmPerTick { get; }

            public int CapacityDollars { get; }

            public int CarriedDollars { get; }

            public HarvesterState State { get; }

            public ulong TargetDockId { get; }

            public ulong TargetCenterId { get; }

            public int LoadingTicksRemaining { get; }

            public HarvesterRecord With(
                HarvesterState state,
                WorldPointMm position,
                int carriedDollars,
                ulong targetDockId,
                ulong targetCenterId,
                int loadingTicksRemaining)
            {
                return new HarvesterRecord(
                    EntityId, Owner, position, SpeedMmPerTick, CapacityDollars, carriedDollars,
                    state, targetDockId, targetCenterId, loadingTicksRemaining);
            }
        }

        private readonly struct WaitingEntry
        {
            public WaitingEntry(ulong tickArrived, ulong entityId)
            {
                TickArrived = tickArrived;
                EntityId = entityId;
            }

            public ulong TickArrived { get; }

            public ulong EntityId { get; }
        }
    }
}
