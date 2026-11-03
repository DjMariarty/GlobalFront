using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// Pre-allocated per-player power ledger (OD-52 / RND §2.4). Buildings carry
    /// an integer <c>PowerDelta</c>: positive is generation, negative is
    /// consumption; incomplete frames contribute exactly 0 until
    /// <see cref="NotifyBuildingCompleted"/> connects them. The blackout state
    /// (LowPower) triggers strictly at NetPower &lt; 0 and drives the
    /// deterministic degradation rules: defense and radar cut, production rate
    /// halved to exactly 500‰, modular construction frozen. Dozers/workers and
    /// harvesters are autonomous and never affected.
    ///
    /// Recomputation is event-driven (register / complete / destroy / cancel /
    /// exemption toggle), never per tick; <see cref="GetPowerState"/> is an O(1)
    /// read of the precomputed snapshot.
    ///
    /// Power-exempt players (GLA doctrine) never enter LowPower: their reported
    /// state carries NetPower = 0, IsLowPower = false and a full 1000‰ rate no
    /// matter what is registered for them.
    /// </summary>
    public sealed class PowerLedger
    {
        /// <summary>
        /// Sized from the protocol constant, not the design-note's 8: the product
        /// supports up to <see cref="SimulationConstants.MaxPlayers"/> players and
        /// SessionManager assigns ids 1..10.
        /// </summary>
        public const int MaxPlayersPerLedger = SimulationConstants.MaxPlayers;

        public const int MaxBuildingsPerPlayer = 512;

        public const int NormalProductionRatePermille = 1000;

        public const int LowPowerProductionRatePermille = 500;

        private readonly PowerBuildingRecord[] _buildings;

        private readonly int[] _counts;

        private readonly bool[] _exempt;

        private readonly PlayerPowerState[] _states;

        public PowerLedger()
        {
            _buildings = new PowerBuildingRecord[MaxPlayersPerLedger * MaxBuildingsPerPlayer];
            _counts = new int[MaxPlayersPerLedger];
            _exempt = new bool[MaxPlayersPerLedger];
            _states = new PlayerPowerState[MaxPlayersPerLedger];
            for (int i = 0; i < MaxPlayersPerLedger; i++)
            {
                _states[i] = NeutralState;
            }
        }

        /// <summary>
        /// Registers a building. Incomplete frames (<paramref name="isComplete"/>
        /// = false) contribute nothing until <see cref="NotifyBuildingCompleted"/>.
        /// </summary>
        public PowerLedgerRejection RegisterBuilding(PlayerId player, ulong buildingEntityId, int powerDelta, bool isComplete = true)
        {
            if (buildingEntityId == 0)
            {
                return PowerLedgerRejection.InvalidEntityId;
            }

            if (!TryGetPlayerIndex(player, out int playerIndex))
            {
                return PowerLedgerRejection.UnknownPlayer;
            }

            int baseIndex = playerIndex * MaxBuildingsPerPlayer;
            int count = _counts[playerIndex];
            for (int i = 0; i < count; i++)
            {
                if (_buildings[baseIndex + i].EntityId == buildingEntityId)
                {
                    return PowerLedgerRejection.DuplicateBuilding;
                }
            }

            if (count >= MaxBuildingsPerPlayer)
            {
                return PowerLedgerRejection.TableFull;
            }

            _buildings[baseIndex + count] = new PowerBuildingRecord(buildingEntityId, powerDelta, isComplete);
            _counts[playerIndex] = count + 1;
            RecomputePlayer(playerIndex);
            return PowerLedgerRejection.None;
        }

        /// <summary>
        /// Removes a building (destroyed / cancelled). Removing a generating
        /// plant can push the player into LowPower; removing a consumer can lift
        /// them out.
        /// </summary>
        public bool UnregisterBuilding(PlayerId player, ulong buildingEntityId)
        {
            if (!TryGetBuildingSlot(player, buildingEntityId, out int playerIndex, out int slot))
            {
                return false;
            }

            int baseIndex = playerIndex * MaxBuildingsPerPlayer;
            int last = _counts[playerIndex] - 1;
            _buildings[baseIndex + slot] = _buildings[baseIndex + last];
            _buildings[baseIndex + last] = default;
            _counts[playerIndex] = last;
            RecomputePlayer(playerIndex);
            return true;
        }

        /// <summary>
        /// Connects power to a previously registered incomplete frame and sets its
        /// authoritative PowerDelta. A second notification for the same building
        /// returns false (already connected).
        /// </summary>
        public bool NotifyBuildingCompleted(PlayerId player, ulong buildingEntityId, int powerDelta)
        {
            if (!TryGetBuildingSlot(player, buildingEntityId, out int playerIndex, out int slot))
            {
                return false;
            }

            int baseIndex = playerIndex * MaxBuildingsPerPlayer;
            PowerBuildingRecord record = _buildings[baseIndex + slot];
            if (record.IsComplete)
            {
                return false;
            }

            _buildings[baseIndex + slot] = new PowerBuildingRecord(buildingEntityId, powerDelta, true);
            RecomputePlayer(playerIndex);
            return true;
        }

        /// <summary>O(1) read of the precomputed per-player snapshot.</summary>
        public PlayerPowerState GetPowerState(PlayerId player)
        {
            return _states[PlayerIndex(player)];
        }

        /// <summary>
        /// Toggles the power exemption (GLA doctrine: every GLA building has
        /// PowerDelta = 0 and the faction never suffers LowPower). Recomputes the
        /// player's snapshot.
        /// </summary>
        public void SetPlayerPowerExempt(PlayerId player, bool exempt)
        {
            int playerIndex = PlayerIndex(player);
            _exempt[playerIndex] = exempt;
            RecomputePlayer(playerIndex);
        }

        public bool IsPowerExempt(PlayerId player)
        {
            return _exempt[PlayerIndex(player)];
        }

        /// <summary>
        /// Whether a consumer class operates at all. Defense, radar and the
        /// modular crane are cut by a blackout; production always operates (it is
        /// throttled through <see cref="GetProductionRatePermille"/> instead of
        /// being switched off).
        /// </summary>
        public bool IsConsumerPowered(PlayerId player, PowerConsumerKind kind)
        {
            switch (kind)
            {
                case PowerConsumerKind.Defense:
                case PowerConsumerKind.Radar:
                case PowerConsumerKind.ModularCrane:
                    return !_states[PlayerIndex(player)].IsLowPower;
                default:
                    // None has nothing to power; Production throttles, never stops.
                    return true;
            }
        }

        /// <summary>1000‰ normally, exactly 500‰ under blackout.</summary>
        public int GetProductionRatePermille(PlayerId player)
        {
            return _states[PlayerIndex(player)].ProductionRatePermille;
        }

        /// <summary>
        /// Modular construction gate for
        /// <see cref="ConstructionRegistry.AdvanceTick"/>: false freezes modular
        /// frames during a blackout. Dozer/worker construction is autonomous and
        /// never consults this.
        /// </summary>
        public bool IsModularConstructionPowered(PlayerId player)
        {
            return !_states[PlayerIndex(player)].IsLowPower;
        }

        /// <summary>Full reset (match teardown): every player back to the neutral state.</summary>
        public void Clear()
        {
            for (int playerIndex = 0; playerIndex < MaxPlayersPerLedger; playerIndex++)
            {
                int baseIndex = playerIndex * MaxBuildingsPerPlayer;
                for (int i = 0; i < _counts[playerIndex]; i++)
                {
                    _buildings[baseIndex + i] = default;
                }

                _counts[playerIndex] = 0;
                _exempt[playerIndex] = false;
                _states[playerIndex] = NeutralState;
            }
        }

        private static PlayerPowerState NeutralState =>
            new PlayerPowerState(0, 0, 0, false, NormalProductionRatePermille);

        private void RecomputePlayer(int playerIndex)
        {
            int baseIndex = playerIndex * MaxBuildingsPerPlayer;
            int count = _counts[playerIndex];
            int produced = 0;
            int consumed = 0;
            for (int i = 0; i < count; i++)
            {
                PowerBuildingRecord record = _buildings[baseIndex + i];
                if (!record.IsComplete)
                {
                    continue;
                }

                if (record.PowerDelta > 0)
                {
                    produced += record.PowerDelta;
                }
                else if (record.PowerDelta < 0)
                {
                    consumed += -record.PowerDelta;
                }
            }

            if (_exempt[playerIndex])
            {
                // GLA doctrine: the power dimension is irrelevant — never a
                // blackout, never a throttled rate, NetPower pinned to 0.
                _states[playerIndex] = new PlayerPowerState(produced, consumed, 0, false, NormalProductionRatePermille);
                return;
            }

            int netPower = produced - consumed;
            bool isLowPower = netPower < 0;
            _states[playerIndex] = new PlayerPowerState(
                produced,
                consumed,
                netPower,
                isLowPower,
                isLowPower ? LowPowerProductionRatePermille : NormalProductionRatePermille);
        }

        private static bool TryGetPlayerIndex(PlayerId player, out int playerIndex)
        {
            if (player.Value < 1 || player.Value > MaxPlayersPerLedger)
            {
                playerIndex = -1;
                return false;
            }

            playerIndex = player.Value - 1;
            return true;
        }

        private static int PlayerIndex(PlayerId player)
        {
            if (player.Value < 1 || player.Value > MaxPlayersPerLedger)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(player), player.Value, $"PlayerId must be within [1, {MaxPlayersPerLedger}].");
            }

            return player.Value - 1;
        }

        private bool TryGetBuildingSlot(PlayerId player, ulong buildingEntityId, out int playerIndex, out int slot)
        {
            playerIndex = -1;
            slot = -1;
            if (buildingEntityId == 0 || !TryGetPlayerIndex(player, out playerIndex))
            {
                return false;
            }

            int baseIndex = playerIndex * MaxBuildingsPerPlayer;
            int count = _counts[playerIndex];
            for (int i = 0; i < count; i++)
            {
                if (_buildings[baseIndex + i].EntityId == buildingEntityId)
                {
                    slot = i;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Internal building row; immutable snapshots written back on change.</summary>
        private readonly struct PowerBuildingRecord
        {
            public PowerBuildingRecord(ulong entityId, int powerDelta, bool isComplete)
            {
                EntityId = entityId;
                PowerDelta = powerDelta;
                IsComplete = isComplete;
            }

            public ulong EntityId { get; }

            public int PowerDelta { get; }

            public bool IsComplete { get; }
        }
    }
}
