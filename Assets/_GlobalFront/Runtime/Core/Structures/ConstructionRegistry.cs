using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;

namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// Pre-allocated registry of active construction sites (building frames).
    /// Zero-GC hot path: fixed backing arrays, for-loops only, O(1) swap-and-pop
    /// removal. Composes the <see cref="BuildingPlacementRegistry"/> so a frame
    /// occupies its footprint from tick 0 and a cancelled frame is un-placed
    /// atomically with its refund.
    ///
    /// Progress rules (Generals / Contra X canon):
    /// - Modular sites self-assemble at the base rate while the owner's
    ///   <see cref="PowerLedger"/> reports them powered; a blackout (LowPower)
    ///   freezes modular frames. Passing no ledger (null) means "always powered".
    /// - DozerWorker sites advance only while at least one living assigned builder
    ///   stands inside the work radius (measured from the snapped site centre,
    ///   inclusive). Damage never resets progress — it only freezes it. Builders
    ///   are autonomous: a blackout never slows them.
    /// - GLA multi-build: 1 worker = 1000‰, 2 = 1600‰, 3+ = 2000‰ (diminishing
    ///   returns, capped at three). Dozer sites take exactly one builder.
    /// </summary>
    public sealed class ConstructionRegistry
    {
        public const int MaxActiveConstructions = 128;

        /// <summary>Default work radius from the snapped site centre (inclusive).</summary>
        public const int DefaultBuildWorkRadiusMm = 15_000;

        /// <summary>GLA worker cap per site; a 4th worker is rejected, not queued.</summary>
        public const int MaxGlaWorkers = 3;

        /// <summary>Default per-mille gain per tick: 1000 / 25 = 40 ticks = 2.0 s at 20 Hz.</summary>
        public const int DefaultWorkPerTick = 25;

        public const int CompletedMilli = 1000;

        private const int MaxWorkPerTick = 1000;

        private readonly BuildingPlacementRegistry _placementRegistry;

        private readonly PowerLedger _powerLedger;

        private readonly BuilderFsm _fsm;

        private readonly SiteRecord[] _sites;

        /// <summary>Parallel per-slot present-builder counters, reset every tick.</summary>
        private readonly int[] _presentBuilders;

        private readonly ulong[] _completedScratch;

        private readonly int _maxActiveConstructions;

        private readonly int _buildWorkRadiusMm;

        private int _count;

        private int _completedCount;

        public ConstructionRegistry(
            BuildingPlacementRegistry placementRegistry,
            int maxActiveConstructions = MaxActiveConstructions,
            int buildWorkRadiusMm = DefaultBuildWorkRadiusMm,
            PowerLedger powerLedger = null)
        {
            if (placementRegistry is null)
            {
                throw new ArgumentNullException(nameof(placementRegistry));
            }

            if (maxActiveConstructions <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxActiveConstructions), maxActiveConstructions, "Construction capacity must be strictly positive.");
            }

            if (buildWorkRadiusMm <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(buildWorkRadiusMm), buildWorkRadiusMm, "Work radius must be strictly positive.");
            }

            _placementRegistry = placementRegistry;
            _powerLedger = powerLedger;
            _fsm = new BuilderFsm();
            _sites = new SiteRecord[maxActiveConstructions];
            _presentBuilders = new int[maxActiveConstructions];
            _completedScratch = new ulong[maxActiveConstructions];
            _maxActiveConstructions = maxActiveConstructions;
            _buildWorkRadiusMm = buildWorkRadiusMm;
        }

        /// <summary>Active (not yet completed or cancelled) construction sites.</summary>
        public int Count => _count;

        /// <summary>
        /// GLA presence ladder: exact per-mille multipliers with diminishing
        /// returns. 0 builders yields 0 — that zero IS the DozerWorker freeze
        /// (single mechanism, no separate branch). Modular sites bypass this
        /// function and always run at 1000‰.
        /// </summary>
        public static int WorkMultiplierPermille(int presentBuilders)
        {
            if (presentBuilders <= 0)
            {
                return 0;
            }

            if (presentBuilders == 1)
            {
                return 1000;
            }

            if (presentBuilders == 2)
            {
                return 1600;
            }

            return 2000;
        }

        /// <summary>
        /// Starts a construction: snaps the centre, registers the frame in the
        /// placement registry (the frame blocks its footprint from tick 0) and
        /// stores the site at 0‰. Capacity is checked before the placement call so
        /// a rejected start never leaves an orphaned placement record.
        /// </summary>
        public bool TryStartConstruction(
            ulong buildingEntityId,
            PlayerId owner,
            WorldPointMm center,
            BuildingFootprint footprint,
            ConstructionMethod method,
            int baseWorkPerTick,
            int paidCost,
            out ConstructionStartRejection rejection)
        {
            if (baseWorkPerTick <= 0 || baseWorkPerTick > MaxWorkPerTick)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(baseWorkPerTick), baseWorkPerTick,
                    $"Base work per tick must be within (0, {MaxWorkPerTick}].");
            }

            if (paidCost < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(paidCost), paidCost, "Paid cost must not be negative.");
            }

            if (!owner.IsValid)
            {
                rejection = ConstructionStartRejection.InvalidOwner;
                return false;
            }

            if (buildingEntityId == 0)
            {
                rejection = ConstructionStartRejection.InvalidEntityId;
                return false;
            }

            if (IndexOfSite(buildingEntityId) >= 0)
            {
                rejection = ConstructionStartRejection.DuplicateSiteEntity;
                return false;
            }

            if (_count >= _maxActiveConstructions)
            {
                rejection = ConstructionStartRejection.ConstructionRegistryFull;
                return false;
            }

            WorldPointMm snappedCenter = SnapGrid.SnapPosition(center);
            _placementRegistry.TryRegister(buildingEntityId, snappedCenter, footprint, out PlacementRejection placementRejection);
            switch (placementRejection)
            {
                case PlacementRejection.None:
                    break;
                case PlacementRejection.OutOfBounds:
                    rejection = ConstructionStartRejection.MapBounds;
                    return false;
                case PlacementRejection.OverlapsExistingBuilding:
                    rejection = ConstructionStartRejection.OverlapsExistingBuilding;
                    return false;
                case PlacementRejection.RegistryFull:
                    rejection = ConstructionStartRejection.PlacementRegistryFull;
                    return false;
                default:
                    // TerrainBlocked is reserved for Step 5.2.2 terrain gates; the
                    // remaining codes are impossible for a non-zero entity id.
                    rejection = ConstructionStartRejection.InvalidEntityId;
                    return false;
            }

            _sites[_count] = new SiteRecord(
                buildingEntityId, owner, snappedCenter, footprint, 0, baseWorkPerTick, paidCost, method, 0, true);
            _count++;
            rejection = ConstructionStartRejection.None;
            return true;
        }

        /// <summary>
        /// Assigns a builder to an active site. The first builder fixes the site's
        /// kind; Dozer sites cap at 1 builder, Worker (GLA) sites at 3.
        /// </summary>
        public BuilderAssignmentRejection AssignBuilder(ulong builderEntityId, BuilderKind kind, ulong siteEntityId)
        {
            if (IndexOfSite(siteEntityId) < 0)
            {
                return BuilderAssignmentRejection.SiteUnknown;
            }

            return _fsm.Assign(builderEntityId, kind, siteEntityId);
        }

        /// <summary>
        /// Releases a builder (recall / new orders). The site freezes until another
        /// builder arrives; progress never decays.
        /// </summary>
        public bool UnassignBuilder(ulong builderEntityId)
        {
            return _fsm.Unassign(builderEntityId);
        }

        /// <summary>
        /// Advances every active site one tick: frees dead builders, counts
        /// living assigned builders inside the work radius, flips FSM activities
        /// and applies progress (half-up integer rounding, clamped at 1000‰).
        /// Completed sites leave the registry the same tick and are reported in
        /// the result; their building stays registered in the placement registry
        /// and their builders go Idle.
        /// </summary>
        public ConstructionTickResult AdvanceTick(BuilderTickInput[] builders, int count)
        {
            if (builders is null)
            {
                throw new ArgumentNullException(nameof(builders));
            }

            if (count < 0 || count > builders.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be within [0, builders.Length].");
            }

            _completedCount = 0;
            for (int i = 0; i < _count; i++)
            {
                _presentBuilders[i] = 0;
            }

            _fsm.RemoveDead(builders, count);

            for (int b = 0; b < count; b++)
            {
                BuilderTickInput input = builders[b];
                if (!input.IsAlive || !_fsm.TryGet(input.EntityId, out BuilderRecord record))
                {
                    continue;
                }

                int siteIndex = IndexOfSite(record.SiteEntityId);
                if (siteIndex < 0)
                {
                    continue;
                }

                if (CombatMath.IsWithinRange(input.Position, _sites[siteIndex].Center, _buildWorkRadiusMm))
                {
                    _presentBuilders[siteIndex]++;
                    _fsm.SetActivity(input.EntityId, BuilderActivity.Building);
                }
                else
                {
                    _fsm.SetActivity(input.EntityId, BuilderActivity.MovingToSite);
                }
            }

            for (int i = 0; i < _count; )
            {
                SiteRecord site = _sites[i];
                int present = _presentBuilders[i];

                // Modular frames ignore presence and self-assemble at the base
                // rate while the owner is powered — a blackout (LowPower) freezes
                // them. DozerWorker frames run the presence ladder, whose zero
                // entry IS the freeze; builders are autonomous and never consult
                // the ledger, so their sites always report powered.
                bool isPowered;
                int multiplier;
                if (site.Method == ConstructionMethod.Modular)
                {
                    isPowered = _powerLedger is null || _powerLedger.IsModularConstructionPowered(site.Owner);
                    multiplier = isPowered ? 1000 : 0;
                }
                else
                {
                    isPowered = true;
                    multiplier = WorkMultiplierPermille(present);
                }

                long scaled = (long)site.BaseWorkPerTick * multiplier;
                int progress = site.BuildProgressMilli + (int)((scaled + 500) / 1000);
                if (progress > CompletedMilli)
                {
                    progress = CompletedMilli;
                }

                site = site.WithTickOutcome(progress, present, isPowered);
                if (progress >= CompletedMilli)
                {
                    _completedScratch[_completedCount] = site.BuildingEntityId;
                    _completedCount++;
                    _fsm.UnassignSite(site.BuildingEntityId);
                    RemoveSiteAt(i);
                }
                else
                {
                    _sites[i] = site;
                    i++;
                }
            }

            return new ConstructionTickResult(_completedCount);
        }

        /// <summary>
        /// Cancels a site: full (100%) refund of the paid cost, un-placement of the
        /// frame, all its builders to Idle, O(1) removal. Only an incomplete site
        /// can be cancelled.
        /// </summary>
        public bool TryCancel(ulong buildingEntityId, out int refundCost)
        {
            refundCost = 0;
            int index = IndexOfSite(buildingEntityId);
            if (index < 0)
            {
                return false;
            }

            SiteRecord site = _sites[index];
            if (site.BuildProgressMilli >= CompletedMilli)
            {
                return false;
            }

            refundCost = site.PaidCost;
            _placementRegistry.Unregister(buildingEntityId);
            _fsm.UnassignSite(buildingEntityId);
            RemoveSiteAt(index);
            return true;
        }

        /// <summary>Entity id of the <paramref name="index"/>-th site completed by the last AdvanceTick.</summary>
        public ulong GetCompletedBuildingEntityId(int index)
        {
            if (index < 0 || index >= _completedCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, $"Index must be within [0, {_completedCount}).");
            }

            return _completedScratch[index];
        }

        public bool TryGet(ulong buildingEntityId, out ConstructionSiteState site)
        {
            int index = IndexOfSite(buildingEntityId);
            if (index < 0)
            {
                site = default;
                return false;
            }

            site = _sites[index].ToState();
            return true;
        }

        /// <summary>Direct slot access in start order (until a removal shuffles the tail).</summary>
        public ConstructionSiteState GetAt(int index)
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, $"Index must be within [0, {_count}).");
            }

            return _sites[index].ToState();
        }

        public int AssignedBuilderCount(ulong siteEntityId)
        {
            int assigned = 0;
            for (int i = 0; i < _fsm.Count; i++)
            {
                if (_fsm.GetAt(i).SiteEntityId == siteEntityId)
                {
                    assigned++;
                }
            }

            return assigned;
        }

        public bool TryGetBuilderActivity(ulong builderEntityId, out BuilderActivity activity)
        {
            if (_fsm.TryGet(builderEntityId, out BuilderRecord record))
            {
                activity = record.Activity;
                return true;
            }

            activity = BuilderActivity.Idle;
            return false;
        }

        /// <summary>
        /// Full reset: every active frame is un-placed, every builder released.
        /// Match-level teardown — not a per-tick path.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                _placementRegistry.Unregister(_sites[i].BuildingEntityId);
            }

            _count = 0;
            _fsm.Clear();
            _completedCount = 0;
        }

        private int IndexOfSite(ulong buildingEntityId)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_sites[i].BuildingEntityId == buildingEntityId)
                {
                    return i;
                }
            }

            return -1;
        }

        private void RemoveSiteAt(int index)
        {
            _count--;
            _sites[index] = _sites[_count];
            _presentBuilders[index] = _presentBuilders[_count];
            _sites[_count] = default;
            _presentBuilders[_count] = 0;
        }

        /// <summary>Internal mutable-style site row; every update writes an immutable copy back.</summary>
        private readonly struct SiteRecord
        {
            public SiteRecord(
                ulong buildingEntityId,
                PlayerId owner,
                WorldPointMm center,
                BuildingFootprint footprint,
                int buildProgressMilli,
                int baseWorkPerTick,
                int paidCost,
                ConstructionMethod method,
                int presentBuilders,
                bool isPowered)
            {
                BuildingEntityId = buildingEntityId;
                Owner = owner;
                Center = center;
                Footprint = footprint;
                BuildProgressMilli = buildProgressMilli;
                BaseWorkPerTick = baseWorkPerTick;
                PaidCost = paidCost;
                Method = method;
                PresentBuilders = presentBuilders;
                IsPowered = isPowered;
            }

            public ulong BuildingEntityId { get; }

            public PlayerId Owner { get; }

            public WorldPointMm Center { get; }

            public BuildingFootprint Footprint { get; }

            public int BuildProgressMilli { get; }

            public int BaseWorkPerTick { get; }

            public int PaidCost { get; }

            public ConstructionMethod Method { get; }

            public int PresentBuilders { get; }

            public bool IsPowered { get; }

            public SiteRecord WithTickOutcome(int buildProgressMilli, int presentBuilders, bool isPowered)
            {
                return new SiteRecord(
                    BuildingEntityId, Owner, Center, Footprint,
                    buildProgressMilli, BaseWorkPerTick, PaidCost, Method, presentBuilders, isPowered);
            }

            public ConstructionSiteState ToState()
            {
                return new ConstructionSiteState(
                    BuildingEntityId, Owner, Center, Footprint,
                    BuildProgressMilli, BaseWorkPerTick, PaidCost, Method, PresentBuilders, IsPowered);
            }
        }
    }
}
