using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Economy
{
    /// <summary>
    /// Why an Oil Derrick capture order was refused. Values are
    /// protocol-significant — never renumber.
    /// </summary>
    public enum DerrickCaptureRejection : byte
    {
        None = 0,
        InvalidPlayer = 1,
        UnknownDerrick = 2,
        UnknownInfantry = 3,
        WrongOwner = 4,
        NotInfantry = 5,
        AlreadyOwned = 6,
        OccupiedByOther = 7
    }

    /// <summary>Income-building archetype of the secondary economy.</summary>
    public enum SecondaryIncomeKind : byte
    {
        DropZone = 0,
        BlackMarket = 1
    }

    /// <summary>
    /// Per-tick read-only view of one roster unit. The secondary economy never
    /// mutates units — it only probes them; CurrentHealth &lt;= 0 or a failed
    /// probe means the unit is gone or dead.
    /// </summary>
    public readonly struct SecondaryEconomyUnitProbe
    {
        public SecondaryEconomyUnitProbe(
            WorldPointMm position,
            int currentHealth,
            bool hasAttackTarget,
            byte unitKind)
        {
            Position = position;
            CurrentHealth = currentHealth;
            HasAttackTarget = hasAttackTarget;
            UnitKind = unitKind;
        }

        public WorldPointMm Position { get; }

        public int CurrentHealth { get; }

        public bool HasAttackTarget { get; }

        public byte UnitKind { get; }
    }

    /// <summary>
    /// Roster adapter the registry probes through. Implemented once by the
    /// server (a single adapter instance, not per tick) so the tick path stays
    /// zero-GC; the Core assembly cannot see the server's private unit records.
    /// </summary>
    public interface ISecondaryEconomyUnitSource
    {
        bool TryProbe(ulong entityId, out SecondaryEconomyUnitProbe probe);
    }

    /// <summary>Snapshot of one Oil Derrick for observation and tests.</summary>
    public readonly struct OilDerrickSnapshot
    {
        public OilDerrickSnapshot(
            ulong entityId,
            WorldPointMm position,
            PlayerId currentOwner,
            ulong occupierEntityId,
            PlayerId occupierPlayer,
            int occupationTicksProgress,
            int ticksSincePayout)
        {
            EntityId = entityId;
            Position = position;
            CurrentOwner = currentOwner;
            OccupierEntityId = occupierEntityId;
            OccupierPlayer = occupierPlayer;
            OccupationTicksProgress = occupationTicksProgress;
            TicksSincePayout = ticksSincePayout;
        }

        public ulong EntityId { get; }

        public WorldPointMm Position { get; }

        /// <summary>Owning player; invalid (0) while the derrick is neutral.</summary>
        public PlayerId CurrentOwner { get; }

        /// <summary>Infantry currently capturing; 0 when no attempt is in progress.</summary>
        public ulong OccupierEntityId { get; }

        public PlayerId OccupierPlayer { get; }

        /// <summary>0..80; any interruption snaps it back to 0.</summary>
        public int OccupationTicksProgress { get; }

        public int TicksSincePayout { get; }
    }

    /// <summary>
    /// Pre-allocated registry of the secondary income sources (Step 5.4.2):
    /// neutral Oil Derricks captured by infantry, USA Drop Zones, GLA Black
    /// Markets and China Hackers. All payouts flow through the
    /// <see cref="SupplyEconomyRegistry"/> saturating ledger, so secondary
    /// income respects the $2B ceiling and feeds TotalHarvestedCredits.
    ///
    /// Zero-GC hot path: fixed backing arrays, for-loops only. Records are
    /// stored in registration order (no distance tie-breaks exist here, unlike
    /// the supply registry) and compact in order on removal.
    ///
    /// Capture discipline: the occupier must stay alive, undamaged and inside
    /// the 6000 mm interaction radius every economy tick. Damage is detected
    /// as CurrentHealth dropping below the probe value of the previous tick
    /// (the server drops combat damage events, so health comparison is the
    /// canonical damaged-this-tick signal). Any violation snaps the occupation
    /// progress and the occupier back to zero in the same tick.
    /// </summary>
    public sealed class SecondaryEconomyRegistry
    {
        private struct DerrickRecord
        {
            public ulong EntityId;
            public WorldPointMm Position;
            public PlayerId CurrentOwner; // invalid (0) = neutral
            public ulong OccupierEntityId; // 0 = no capture in progress
            public PlayerId OccupierPlayer;
            public int OccupationTicksProgress;
            public int TicksSincePayout;
            public int OccupierLastHealth;
        }

        private struct IncomeBuildingRecord
        {
            public ulong EntityId;
            public PlayerId Owner;
            public SecondaryIncomeKind Kind;
            public int TicksSincePayout;
        }

        private struct HackerRecord
        {
            public ulong EntityId;
            public PlayerId Owner;
            public int TicksSincePayout;
        }

        private readonly DerrickRecord[] _derricks;

        private readonly IncomeBuildingRecord[] _incomeBuildings;

        private readonly HackerRecord[] _hackers;

        private int _derrickCount;

        private int _incomeBuildingCount;

        private int _hackerCount;

        public SecondaryEconomyRegistry()
        {
            _derricks = new DerrickRecord[SimulationConstants.MaxOilDerricks];
            _incomeBuildings = new IncomeBuildingRecord[SimulationConstants.MaxSecondaryIncomeBuildings];
            _hackers = new HackerRecord[SimulationConstants.MaxHackers];
        }

        public int DerrickCount => _derrickCount;

        public int IncomeBuildingCount => _incomeBuildingCount;

        public int HackerCount => _hackerCount;

        // ------------------------------------------------------------------
        // registration
        // ------------------------------------------------------------------

        /// <summary>Registers a neutral Oil Derrick; owner 0 until captured.</summary>
        public bool TryRegisterOilDerrick(ulong entityId, WorldPointMm position, out EconomyRegistrationRejection rejection)
        {
            if (entityId == 0 || IsKnownEntity(entityId))
            {
                rejection = entityId == 0 ? EconomyRegistrationRejection.InvalidEntityId : EconomyRegistrationRejection.DuplicateEntity;
                return false;
            }

            if (_derrickCount >= _derricks.Length)
            {
                rejection = EconomyRegistrationRejection.RegistryFull;
                return false;
            }

            _derricks[_derrickCount++] = new DerrickRecord
            {
                EntityId = entityId,
                Position = position
            };

            rejection = EconomyRegistrationRejection.None;
            return true;
        }

        /// <summary>Registers a completed Drop Zone or Black Market building.</summary>
        public bool TryRegisterIncomeBuilding(
            ulong entityId,
            PlayerId owner,
            SecondaryIncomeKind kind,
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

            if (_incomeBuildingCount >= _incomeBuildings.Length)
            {
                rejection = EconomyRegistrationRejection.RegistryFull;
                return false;
            }

            _incomeBuildings[_incomeBuildingCount++] = new IncomeBuildingRecord
            {
                EntityId = entityId,
                Owner = owner,
                Kind = kind
            };

            rejection = EconomyRegistrationRejection.None;
            return true;
        }

        /// <summary>Registers a produced Hacker unit.</summary>
        public bool TryRegisterHacker(ulong entityId, PlayerId owner, out EconomyRegistrationRejection rejection)
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

            if (_hackerCount >= _hackers.Length)
            {
                rejection = EconomyRegistrationRejection.RegistryFull;
                return false;
            }

            _hackers[_hackerCount++] = new HackerRecord
            {
                EntityId = entityId,
                Owner = owner
            };

            rejection = EconomyRegistrationRejection.None;
            return true;
        }

        /// <summary>
        /// Removes any registration of the entity (derrick, income building or
        /// hacker) — used by the server when the roster says the unit died.
        /// </summary>
        public bool UnregisterEntity(ulong entityId)
        {
            int index = IndexOfDerrick(entityId);
            if (index >= 0)
            {
                RemoveDerrickAt(index);
                return true;
            }

            index = IndexOfIncomeBuilding(entityId);
            if (index >= 0)
            {
                RemoveIncomeBuildingAt(index);
                return true;
            }

            index = IndexOfHacker(entityId);
            if (index >= 0)
            {
                RemoveHackerAt(index);
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Oil Derrick capture
        // ------------------------------------------------------------------

        /// <summary>
        /// Orders an infantry unit to occupy a derrick. The server validates
        /// the roster side (unit exists, alive, owned by the player, is an
        /// Infantry kind) and passes the unit's current health so the first
        /// occupation tick can detect damage. Re-issuing the order from the
        /// same occupier refreshes its health baseline without resetting the
        /// progress; a different occupier is refused while a capture is live.
        /// </summary>
        public DerrickCaptureRejection TryOrderCapture(
            ulong derrickEntityId,
            PlayerId player,
            ulong infantryEntityId,
            int infantryCurrentHealth)
        {
            if (!player.IsValid)
            {
                return DerrickCaptureRejection.InvalidPlayer;
            }

            int derrickIndex = IndexOfDerrick(derrickEntityId);
            if (derrickIndex < 0)
            {
                return DerrickCaptureRejection.UnknownDerrick;
            }

            if (infantryEntityId == 0)
            {
                return DerrickCaptureRejection.UnknownInfantry;
            }

            ref DerrickRecord derrick = ref _derricks[derrickIndex];
            if (derrick.CurrentOwner.Equals(player))
            {
                return DerrickCaptureRejection.AlreadyOwned;
            }

            if (derrick.OccupierEntityId != 0 && derrick.OccupierEntityId != infantryEntityId)
            {
                return DerrickCaptureRejection.OccupiedByOther;
            }

            // Same-occupier re-issue refreshes the health baseline without
            // resetting the progress; a fresh attempt starts from progress 0
            // (the reset invariant keeps progress and occupier in lockstep).
            derrick.OccupierEntityId = infantryEntityId;
            derrick.OccupierPlayer = player;
            derrick.OccupierLastHealth = infantryCurrentHealth;
            return DerrickCaptureRejection.None;
        }

        // ------------------------------------------------------------------
        // observation
        // ------------------------------------------------------------------

        public bool TryGetDerrickState(ulong entityId, out OilDerrickSnapshot snapshot)
        {
            int index = IndexOfDerrick(entityId);
            if (index < 0)
            {
                snapshot = default;
                return false;
            }

            DerrickRecord record = _derricks[index];
            snapshot = new OilDerrickSnapshot(
                record.EntityId, record.Position, record.CurrentOwner, record.OccupierEntityId,
                record.OccupierPlayer, record.OccupationTicksProgress, record.TicksSincePayout);
            return true;
        }

        public void Clear()
        {
            _derrickCount = 0;
            _incomeBuildingCount = 0;
            _hackerCount = 0;
        }

        // ------------------------------------------------------------------
        // per-tick advance
        // ------------------------------------------------------------------

        /// <summary>
        /// Advances every secondary source one tick. Zero-GC: fixed arrays,
        /// for-loops, interface probes into the caller-provided source.
        /// </summary>
        public void AdvanceTick(ISecondaryEconomyUnitSource units, SupplyEconomyRegistry economy)
        {
            if (units == null)
            {
                throw new ArgumentNullException(nameof(units));
            }

            if (economy == null)
            {
                throw new ArgumentNullException(nameof(economy));
            }

            AdvanceDerricks(units, economy);
            AdvanceIncomeBuildings(units, economy);
            AdvanceHackers(units, economy);
        }

        private void AdvanceDerricks(ISecondaryEconomyUnitSource units, SupplyEconomyRegistry economy)
        {
            for (int index = 0; index < _derrickCount; index++)
            {
                DerrickRecord derrick = _derricks[index];

                if (derrick.OccupierEntityId != 0)
                {
                    derrick = AdvanceOccupation(derrick, units);
                }

                if (derrick.CurrentOwner.IsValid)
                {
                    derrick.TicksSincePayout++;
                    if (derrick.TicksSincePayout >= SimulationConstants.OilDerrickPayoutIntervalTicks)
                    {
                        derrick.TicksSincePayout = 0;
                        economy.AddCredits(derrick.CurrentOwner, SimulationConstants.OilDerrickPayoutDollars);
                    }
                }

                _derricks[index] = derrick;
            }
        }

        private DerrickRecord AdvanceOccupation(DerrickRecord derrick, ISecondaryEconomyUnitSource units)
        {
            if (!units.TryProbe(derrick.OccupierEntityId, out SecondaryEconomyUnitProbe probe) ||
                probe.CurrentHealth <= 0)
            {
                return ResetOccupation(derrick);
            }

            if (probe.CurrentHealth < derrick.OccupierLastHealth)
            {
                // damaged since the previous economy tick — the attempt collapses
                return ResetOccupation(derrick);
            }

            if (!CombatMath.IsWithinRange(
                    probe.Position,
                    derrick.Position,
                    SimulationConstants.DefaultEconomyInteractionRadiusMm))
            {
                return ResetOccupation(derrick);
            }

            derrick.OccupierLastHealth = probe.CurrentHealth;
            derrick.OccupationTicksProgress++;
            if (derrick.OccupationTicksProgress >= SimulationConstants.OilDerrickCaptureDurationTicks)
            {
                derrick.CurrentOwner = derrick.OccupierPlayer;
                derrick.TicksSincePayout = 0;
                return ResetOccupation(derrick);
            }

            return derrick;
        }

        private static DerrickRecord ResetOccupation(DerrickRecord derrick)
        {
            derrick.OccupierEntityId = 0;
            derrick.OccupierPlayer = default;
            derrick.OccupationTicksProgress = 0;
            derrick.OccupierLastHealth = 0;
            return derrick;
        }

        private void AdvanceIncomeBuildings(ISecondaryEconomyUnitSource units, SupplyEconomyRegistry economy)
        {
            for (int index = _incomeBuildingCount - 1; index >= 0; index--)
            {
                IncomeBuildingRecord building = _incomeBuildings[index];

                // A destroyed building (gone from the roster or dead) deregisters
                // itself; its payouts stop on the same tick.
                if (!units.TryProbe(building.EntityId, out SecondaryEconomyUnitProbe probe) || probe.CurrentHealth <= 0)
                {
                    RemoveIncomeBuildingAt(index);
                    continue;
                }

                building.TicksSincePayout++;
                if (building.Kind == SecondaryIncomeKind.DropZone)
                {
                    if (building.TicksSincePayout >= SimulationConstants.DropZonePayoutIntervalTicks)
                    {
                        building.TicksSincePayout = 0;
                        economy.AddCredits(building.Owner, SimulationConstants.DropZonePayoutDollars);
                    }
                }
                else
                {
                    if (building.TicksSincePayout >= SimulationConstants.BlackMarketPayoutIntervalTicks)
                    {
                        building.TicksSincePayout = 0;
                        economy.AddCredits(building.Owner, SimulationConstants.BlackMarketPayoutDollars);
                    }
                }

                _incomeBuildings[index] = building;
            }
        }

        private void AdvanceHackers(ISecondaryEconomyUnitSource units, SupplyEconomyRegistry economy)
        {
            for (int index = _hackerCount - 1; index >= 0; index--)
            {
                HackerRecord hacker = _hackers[index];

                if (!units.TryProbe(hacker.EntityId, out SecondaryEconomyUnitProbe probe) || probe.CurrentHealth <= 0)
                {
                    RemoveHackerAt(index);
                    continue;
                }

                if (probe.HasAttackTarget)
                {
                    // Attacking: the payout timer freezes, it does not reset.
                    continue;
                }

                hacker.TicksSincePayout++;
                if (hacker.TicksSincePayout >= SimulationConstants.HackerPayoutIntervalTicks)
                {
                    hacker.TicksSincePayout = 0;
                    economy.AddCredits(hacker.Owner, SimulationConstants.HackerPayoutDollars);
                }

                _hackers[index] = hacker;
            }
        }

        // ------------------------------------------------------------------
        // internals
        // ------------------------------------------------------------------

        /// <summary>
        /// Whether any derrick, income building or hacker already holds the
        /// id. Public so the server can enforce one global entity-id space
        /// across the supply and secondary registries (Step 5.4.2).
        /// </summary>
        public bool IsKnownEntity(ulong entityId)
        {
            return IndexOfDerrick(entityId) >= 0 || IndexOfIncomeBuilding(entityId) >= 0 || IndexOfHacker(entityId) >= 0;
        }

        private int IndexOfDerrick(ulong entityId)
        {
            for (int index = 0; index < _derrickCount; index++)
            {
                if (_derricks[index].EntityId == entityId)
                {
                    return index;
                }
            }

            return -1;
        }

        private int IndexOfIncomeBuilding(ulong entityId)
        {
            for (int index = 0; index < _incomeBuildingCount; index++)
            {
                if (_incomeBuildings[index].EntityId == entityId)
                {
                    return index;
                }
            }

            return -1;
        }

        private int IndexOfHacker(ulong entityId)
        {
            for (int index = 0; index < _hackerCount; index++)
            {
                if (_hackers[index].EntityId == entityId)
                {
                    return index;
                }
            }

            return -1;
        }

        private void RemoveDerrickAt(int index)
        {
            for (int i = index + 1; i < _derrickCount; i++)
            {
                _derricks[i - 1] = _derricks[i];
            }

            _derrickCount--;
        }

        private void RemoveIncomeBuildingAt(int index)
        {
            for (int i = index + 1; i < _incomeBuildingCount; i++)
            {
                _incomeBuildings[i - 1] = _incomeBuildings[i];
            }

            _incomeBuildingCount--;
        }

        private void RemoveHackerAt(int index)
        {
            for (int i = index + 1; i < _hackerCount; i++)
            {
                _hackers[i - 1] = _hackers[i];
            }

            _hackerCount--;
        }
    }
}
