using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;

namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// How a building under construction makes progress.
    /// Values are protocol-significant — never renumber.
    /// </summary>
    public enum ConstructionMethod : byte
    {
        /// <summary>USA / China / GLA: a living builder must be present in the work radius.</summary>
        DozerWorker = 0,

        /// <summary>Russia / NATO: modular frame assembles itself at the base rate, no builder required.</summary>
        Modular = 1
    }

    /// <summary>
    /// Kind of a construction builder. Dozers belong to USA/China, Workers to GLA;
    /// the kind fixes the per-site builder cap (1 dozer vs up to 3 workers).
    /// Values are protocol-significant — never renumber.
    /// </summary>
    public enum BuilderKind : byte
    {
        Dozer = 0,
        Worker = 1
    }

    /// <summary>
    /// FSM activity of an assigned builder. Values are protocol-significant —
    /// never renumber.
    /// </summary>
    public enum BuilderActivity : byte
    {
        /// <summary>Unassigned or released; the builder contributes to no site.</summary>
        Idle = 0,

        /// <summary>Assigned to a site but still outside the work radius.</summary>
        MovingToSite = 1,

        /// <summary>Assigned and inside the work radius — contributes work this tick.</summary>
        Building = 2
    }

    /// <summary>
    /// Why a construction could not be started. Every failure of
    /// <see cref="ConstructionRegistry.TryStartConstruction"/> has its own code —
    /// a false return never carries <see cref="ConstructionStartRejection.None"/>.
    /// </summary>
    public enum ConstructionStartRejection : byte
    {
        None = 0,
        InvalidEntityId = 1,
        DuplicateSiteEntity = 2,

        /// <summary>Reserved: an out-of-range work rate throws ArgumentOutOfRangeException instead.</summary>
        InvalidWorkRate = 3,
        MapBounds = 4,
        OverlapsExistingBuilding = 5,
        PlacementRegistryFull = 6,
        ConstructionRegistryFull = 7
    }

    /// <summary>
    /// Why a builder could not be assigned to a construction site.
    /// </summary>
    public enum BuilderAssignmentRejection : byte
    {
        None = 0,

        /// <summary>Zero builder id, or a builder id the caller promised exists but the FSM never saw.</summary>
        BuilderUnknown = 1,
        SiteUnknown = 2,
        AlreadyAssigned = 3,

        /// <summary>Dozer site already has its single builder, or a GLA site already has 3 workers.</summary>
        SiteBuilderLimitReached = 4,
        BuilderKindMismatch = 5,

        /// <summary>The builder table itself is full (never expected at realistic builder counts).</summary>
        BuilderTableFull = 6
    }

    /// <summary>
    /// Public snapshot of one construction site (a building frame). The frame owns
    /// its final archetype and entity id from the first tick; until
    /// <see cref="BuildProgressMilli"/> reaches 1000 the building is inert (no
    /// weapons, production, power or vision — gated by the consumer, not here).
    /// </summary>
    public readonly struct ConstructionSiteState
    {
        public ConstructionSiteState(
            ulong buildingEntityId,
            PlayerId owner,
            WorldPointMm center,
            BuildingFootprint footprint,
            int buildProgressMilli,
            int baseWorkPerTick,
            int paidCost,
            ConstructionMethod method,
            int presentBuilders)
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
        }

        public ulong BuildingEntityId { get; }

        public PlayerId Owner { get; }

        /// <summary>Snapped centre of the frame, identical to the placement-registry record.</summary>
        public WorldPointMm Center { get; }

        public BuildingFootprint Footprint { get; }

        /// <summary>Construction progress in per-mille, 0..1000. Never decreases.</summary>
        public int BuildProgressMilli { get; }

        /// <summary>Base per-mille gain per tick at multiplier 1000 (blueprint parameter).</summary>
        public int BaseWorkPerTick { get; }

        /// <summary>Cost already debited; refunded in full (100%) when the site is cancelled.</summary>
        public int PaidCost { get; }

        public ConstructionMethod Method { get; }

        /// <summary>Builders that were alive and inside the work radius during the last AdvanceTick.</summary>
        public int PresentBuilders { get; }

        public bool IsCompleted => BuildProgressMilli >= ConstructionRegistry.CompletedMilli;

        /// <summary>
        /// A DozerWorker site is frozen while no living builder stands in the work
        /// radius: progress neither grows nor decays. The LowPower freeze for
        /// Modular sites is enforced separately by the PowerLedger (Step 5.2.3)
        /// inside AdvanceTick and is not part of this flag.
        /// </summary>
        public bool IsFrozen => Method == ConstructionMethod.DozerWorker && PresentBuilders == 0;
    }

    /// <summary>
    /// Per-tick view of one builder fed into
    /// <see cref="ConstructionRegistry.AdvanceTick"/> — the same
    /// input-struct pattern as CombatantTickInput. The caller (MatchServer, once
    /// integrated) copies authoritative position and liveness into this struct.
    /// </summary>
    public readonly struct BuilderTickInput
    {
        public BuilderTickInput(ulong entityId, WorldPointMm position, bool isAlive)
        {
            EntityId = entityId;
            Position = position;
            IsAlive = isAlive;
        }

        public ulong EntityId { get; }

        public WorldPointMm Position { get; }

        public bool IsAlive { get; }
    }

    /// <summary>
    /// Result of one construction tick. Completed entity ids are read from the
    /// registry via <see cref="ConstructionRegistry.GetCompletedBuildingEntityId"/>
    /// in the same tick order AdvanceTick finished them.
    /// </summary>
    public readonly struct ConstructionTickResult
    {
        public ConstructionTickResult(int completedCount)
        {
            CompletedCount = completedCount;
        }

        public int CompletedCount { get; }
    }

    /// <summary>
    /// Immutable FSM record of one assigned builder. Only assigned builders occupy
    /// records — an id absent from the table is <see cref="BuilderActivity.Idle"/>.
    /// </summary>
    public readonly struct BuilderRecord
    {
        public BuilderRecord(ulong entityId, BuilderKind kind, BuilderActivity activity, ulong siteEntityId)
        {
            EntityId = entityId;
            Kind = kind;
            Activity = activity;
            SiteEntityId = siteEntityId;
        }

        public ulong EntityId { get; }

        public BuilderKind Kind { get; }

        public BuilderActivity Activity { get; }

        /// <summary>BuildingEntityId of the assigned construction site.</summary>
        public ulong SiteEntityId { get; }
    }
}
