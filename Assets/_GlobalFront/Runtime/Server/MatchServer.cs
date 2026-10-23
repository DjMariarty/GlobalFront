using System;
using System.Collections.Generic;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server.Replication;

namespace GlobalFront.Server
{
    /// <summary>
    /// Reason why the authoritative server refused a client command.
    /// </summary>
    public enum MatchCommandRejection : byte
    {
        None = 0,
        MatchOver = 1,
        HeaderRejected = 2,
        DuplicateSequence = 3,
        UnsupportedCommandType = 4,
        InvalidPayload = 5,
        UnknownEntity = 6,
        NotEntityOwner = 7,
        EntityNotAlive = 8,
        FormationOutOfBounds = 9,

        /// <summary>
        /// Phase 2.4 (ADR-008): the session gate refused the command before
        /// it reached match-level validation (unknown/disconnected session,
        /// identity mismatch, or match not running). The detailed reason is
        /// reported by the session layer as SessionRejection.
        /// </summary>
        SessionRejected = 10,

        /// <summary>
        /// Phase 1.0 sprint 5: the attack target belongs to a player this
        /// relation does not consider an enemy, so the order is refused at the
        /// command path instead of relying on the downstream target-clearing
        /// gates to suppress the damage.
        /// </summary>
        FriendlyTarget = 11,

        /// <summary>
        /// Phase 4 (ADR-014, OD-41): The attack target is hidden in the fog of
        /// war for the commanding player at the time of order submission.
        /// </summary>
        TargetInFogOfWar = 12
    }

    /// <summary>
    /// Immutable view of a single authoritative unit, used by tests and by
    /// future snapshot serialization.
    /// </summary>
    public readonly struct ServerUnitSnapshot : IEquatable<ServerUnitSnapshot>
    {
        public ServerUnitSnapshot(
            EntityId entity,
            PlayerId owner,
            WorldPointMm position,
            int currentHealth,
            bool hasMoveTarget,
            WorldPointMm moveTarget,
            EntityId attackTarget,
            bool autoAcquireEnemies,
            byte unitKind = UnitKinds.Unknown)
        {
            Entity = entity;
            Owner = owner;
            Position = position;
            CurrentHealth = currentHealth;
            HasMoveTarget = hasMoveTarget;
            MoveTarget = moveTarget;
            AttackTarget = attackTarget;
            AutoAcquireEnemies = autoAcquireEnemies;
            UnitKind = unitKind;
        }

        public EntityId Entity { get; }

        public PlayerId Owner { get; }

        public WorldPointMm Position { get; }

        public int CurrentHealth { get; }

        public bool HasMoveTarget { get; }

        public WorldPointMm MoveTarget { get; }

        public EntityId AttackTarget { get; }

        public bool AutoAcquireEnemies { get; }

        /// <summary>
        /// OD-29 archetype id (<see cref="UnitKinds"/>) replicated to clients so
        /// presentation can resolve mesh, ring radius and maximum health. Carried
        /// for replication only: it is not part of the deterministic state and not
        /// mixed into <c>StateChecksum</c>.
        /// </summary>
        public byte UnitKind { get; }

        public bool Equals(ServerUnitSnapshot other) =>
            Entity == other.Entity &&
            Owner == other.Owner &&
            Position == other.Position &&
            CurrentHealth == other.CurrentHealth &&
            HasMoveTarget == other.HasMoveTarget &&
            MoveTarget == other.MoveTarget &&
            AttackTarget == other.AttackTarget &&
            AutoAcquireEnemies == other.AutoAcquireEnemies &&
            UnitKind == other.UnitKind;

        public override bool Equals(object obj) =>
            obj is ServerUnitSnapshot other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Entity, Owner, Position, CurrentHealth);

        public override string ToString() =>
            $"Unit {Entity} owner {Owner} at {Position} hp {CurrentHealth}";
    }

    /// <summary>
    /// Headless authoritative match simulation. Owns every unit, validates and
    /// schedules client commands per tick, and resolves movement plus combat
    /// deterministically. Real-time pacing is applied by the hosting layer via
    /// <see cref="GlobalFront.Core.Simulation.FixedStepClock"/>; the server
    /// itself only knows whole ticks so results never depend on wall clock.
    /// </summary>
    public sealed class MatchServer
    {
        /// <summary>Commands may reference ticks this far behind the current one.</summary>
        public const ulong AcceptedPastTicks = 20;

        /// <summary>Commands may be scheduled this far ahead of the current tick.</summary>
        public const ulong AcceptedFutureTicks = 60;

        private readonly Dictionary<EntityId, UnitRecord> _units =
            new Dictionary<EntityId, UnitRecord>();

        private readonly List<UnitRecord> _orderedUnits = new List<UnitRecord>();

        private readonly Dictionary<ulong, List<ScheduledCommand>> _scheduledCommands =
            new Dictionary<ulong, List<ScheduledCommand>>();

        private readonly Dictionary<PlayerId, uint> _lastSequenceByPlayer =
            new Dictionary<PlayerId, uint>();

        private ulong _nextEntityValue = 1;

        private readonly Dictionary<EntityId, WorldPointMm> _startPositionsCache =
            new Dictionary<EntityId, WorldPointMm>(SimulationConstants.MaxUnitsPerMatch);

        private CombatantTickInput[] _combatantInputsCache =
            new CombatantTickInput[SimulationConstants.MaxUnitsPerMatch];

        /// <summary>
        /// Combat scratch owned by this match, never by the process: two servers
        /// ticking on different threads must not interleave into one roster
        /// (Phase 1.0, P1-1). The ~180 KB of capacity-bound buffers is the accepted
        /// cost of that isolation; the tick itself stays allocation-free.
        /// </summary>
        private readonly CombatTickResolver _combatResolver = new CombatTickResolver();

        // Hoisted so scheduling never allocates a delegate for the sort (Phase 1.0, P1-4).
        private static readonly Comparison<ScheduledCommand> ScheduledCommandComparison =
            CompareScheduledCommands;

        /// <summary>
        /// Decides which owners may shoot each other and when the battle ends.
        /// Read-only and allocated once, because the hostility gate runs for
        /// every unit pair on every tick (Phase 1.0, P1-2).
        /// </summary>
        private readonly ICombatRelation _combatRelation;

        /// <summary>
        /// Optional fog-of-war view used as the anti-maphack gate on orders
        /// (Phase 4, OD-41). Null keeps the command path in passthrough mode,
        /// so a server without replication accepts any valid hostile target,
        /// exactly as before the gate existed.
        /// </summary>
        private IReplicationFilter _visibilityFilter;

        /// <param name="combatRelation">
        /// Hostility and termination policy for this match, defaulting to
        /// <see cref="SkirmishCombatRelation.Instance"/> so every existing
        /// caller keeps its current behaviour. A team match must pass
        /// <see cref="Team2v2CombatRelation.Instance"/>; without it allies
        /// acquire each other and a match whose survivors share a team but not
        /// an owner never terminates.
        /// </param>
        public MatchServer(ICombatRelation combatRelation = null)
        {
            _combatRelation = combatRelation ?? SkirmishCombatRelation.Instance;
        }

        /// <summary>
        /// Creates a match whose command path is gated by the given fog-of-war
        /// view (Phase 4, OD-41). A null filter keeps passthrough behaviour.
        /// </summary>
        public MatchServer(ICombatRelation combatRelation, IReplicationFilter visibilityFilter)
            : this(combatRelation)
        {
            _visibilityFilter = visibilityFilter;
        }

        /// <summary>
        /// The fog-of-war view used to refuse orders against targets hidden
        /// from the commanding player, or null while the gate is bypassed.
        /// </summary>
        public IReplicationFilter VisibilityFilter => _visibilityFilter;

        /// <summary>
        /// Installs or clears the view used by the OD-41 command gate. The
        /// filter is read at submission time, so a host may swap it between
        /// matches; null disables the gate.
        /// </summary>
        public void SetVisibilityFilter(IReplicationFilter filter) => _visibilityFilter = filter;

        private sealed class UnitRecord
        {
            public CombatantState Combat;

            public WorldPointMm Position;

            public int SpeedMmPerTick;

            public bool HasMoveTarget;

            public WorldPointMm MoveTarget;

            public bool AutoAcquireEnemies;

            /// <summary>
            /// OD-29 archetype forwarded to clients for presentation. Fixed at
            /// spawn and never read by movement or combat resolution, so it cannot
            /// perturb deterministic simulation state.
            /// </summary>
            public byte UnitKind;
        }

        private sealed class ScheduledCommand
        {
            public CommandHeader Header;

            public MoveCommand Move;

            /// <summary>
            /// The slots <see cref="MatchServer.TryEnqueueMove"/> already computed and
            /// bounds-checked for <see cref="Move"/>, kept so the executing tick applies
            /// them without recomputing or allocating (Phase 1.0, P1-3).
            /// Per-command rather than one shared scratch buffer because a move can sit
            /// queued for up to <see cref="AcceptedFutureTicks"/> ticks, where two
            /// pending batches would otherwise clobber each other.
            /// </summary>
            public FormationSlot[] MoveSlots;

            public AttackCommand Attack;

            public EntityId[] StopEntities;
        }

        /// <summary>Number of fully simulated ticks.</summary>
        public ulong CurrentTick { get; private set; }

        /// <summary>Terminal battle state; remains in progress until one side wins or all die.</summary>
        public BattleOutcome Outcome { get; private set; } = BattleOutcome.InProgress;

        public int UnitCount => _units.Count;

        /// <summary>
        /// Initializes the match from a <see cref="MatchConfig"/>, creating
        /// all units with server-assigned EntityIds. Returns an array of
        /// assigned EntityIds in the same order as <see cref="MatchConfig.Units"/>.
        /// The client uses these EntityIds to create presentation objects.
        /// This is the authoritative source of EntityId assignment; the client
        /// never determines authoritative EntityIds.
        /// </summary>
        /// <param name="config">
        /// Immutable match configuration describing the initial state.
        /// </param>
        /// <returns>
        /// Array of EntityIds assigned by the server, in the same order as
        /// <see cref="MatchConfig.Units"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="config"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the match has already been initialized (units exist), or when
        /// <see cref="MatchConfig.UnitCount"/> exceeds
        /// <see cref="SimulationConstants.MaxUnitsPerMatch"/>. The capacity check runs
        /// before the first spawn, so a rejected template cannot leave a half-built
        /// roster behind a match that can never be re-initialized.
        /// </exception>
        public EntityId[] InitializeMatch(MatchConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (_units.Count > 0)
            {
                throw new InvalidOperationException(
                    "Match has already been initialized. InitializeMatch can only be called on an empty server.");
            }

            if (config.UnitCount > SimulationConstants.MaxUnitsPerMatch)
            {
                throw new InvalidOperationException(
                    $"Cannot initialize match: template asks for {config.UnitCount} units " +
                    $"and the match capacity is {SimulationConstants.MaxUnitsPerMatch}.");
            }

            var entityIds = new EntityId[config.UnitCount];
            for (var index = 0; index < config.UnitCount; index++)
            {
                var spec = config.Units[index];
                var entity = new EntityId(_nextEntityValue++);
                SpawnUnitCore(
                    entity,
                    spec.Owner,
                    config.UnitStats,
                    spec.Position,
                    spec.SpeedMmPerTick,
                    spec.AutoAcquireEnemies,
                    spec.UnitKind);
                entityIds[index] = entity;
            }

            return entityIds;
        }

        /// <summary>
        /// Adds a unit before or during the match. Entity identifiers are
        /// assigned sequentially so identical setup orders produce identical ids.
        /// </summary>
        /// <param name="autoAcquire">
        /// When true, the unit will automatically acquire the closest enemy
        /// within <see cref="SimulationConstants.AutoAcquireRangeMm"/> whenever
        /// it has no explicit attack target. Mirrors PrototypeUnit.AutoAcquireEnemies.
        /// </param>
        /// <param name="unitKind">
        /// OD-29 archetype replicated to clients. Presentation-only, so callers that
        /// do not care about archetypes leave the <see cref="UnitKinds.Unknown"/>
        /// default and their clients keep the no-stat-resolved fallback.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the match already holds <see cref="SimulationConstants.MaxUnitsPerMatch"/>
        /// units. The capacity check runs before the entity id is consumed, so a spawn
        /// refused for being over capacity leaves the identifier counter untouched.
        /// </exception>
        public EntityId SpawnUnit(
            PlayerId owner,
            CombatStats stats,
            WorldPointMm position,
            int speedMmPerTick,
            bool autoAcquire = false,
            byte unitKind = UnitKinds.Unknown)
        {
            ValidateUnitSpec(owner, stats, position, speedMmPerTick);
            ValidateCapacity();

            var entity = new EntityId(_nextEntityValue++);
            return SpawnUnitCore(
                entity, owner, stats, position, speedMmPerTick, autoAcquire, unitKind);
        }

        /// <summary>
        /// Adds a unit with a caller-supplied entity identifier. Intended for
        /// shadow/parity integrations where the authoritative id assignment
        /// must match an external source (e.g. client scene ids). The caller is
        /// responsible for ensuring uniqueness and ordering.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="entity"/> is invalid or already spawned, or
        /// when <paramref name="owner"/> or <paramref name="stats"/> is unusable.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="position"/> leaves the simulation bounds or
        /// <paramref name="speedMmPerTick"/> is not positive. Every check runs before
        /// the entity-id high-water mark moves, so a rejected spawn leaves the server
        /// exactly as it was.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the match already holds <see cref="SimulationConstants.MaxUnitsPerMatch"/>
        /// units. The capacity check runs before the high-water mark moves, so a spawn
        /// refused for being over capacity leaves the identifier counter untouched.
        /// </exception>
        public EntityId SpawnUnitWithEntity(
            EntityId entity,
            PlayerId owner,
            CombatStats stats,
            WorldPointMm position,
            int speedMmPerTick,
            bool autoAcquire = false,
            byte unitKind = UnitKinds.Unknown)
        {
            if (!entity.IsValid)
            {
                throw new ArgumentException("Entity must be valid.", nameof(entity));
            }

            if (_units.ContainsKey(entity))
            {
                throw new ArgumentException(
                    $"Entity {entity} already exists.", nameof(entity));
            }

            ValidateUnitSpec(owner, stats, position, speedMmPerTick);
            ValidateCapacity();

            if (_nextEntityValue <= entity.Value)
            {
                _nextEntityValue = entity.Value + 1;
            }

            return SpawnUnitCore(
                entity, owner, stats, position, speedMmPerTick, autoAcquire, unitKind);
        }

        /// <summary>
        /// The capacity gate every spawn path shares. Kept separate from
        /// <see cref="ValidateUnitSpec"/> because it is the only check that has to read
        /// the live roster, and callers need it to run before they touch
        /// <c>_nextEntityValue</c>: a spawn refused for being over capacity must leave the
        /// identifier counter exactly as it was.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the match already holds <see cref="SimulationConstants.MaxUnitsPerMatch"/>
        /// units.
        /// </exception>
        private void ValidateCapacity()
        {
            if (_units.Count >= SimulationConstants.MaxUnitsPerMatch)
            {
                throw new InvalidOperationException(
                    $"Cannot spawn unit: match has reached capacity of " +
                    $"{SimulationConstants.MaxUnitsPerMatch} units.");
            }
        }

        private EntityId SpawnUnitCore(
            EntityId entity,
            PlayerId owner,
            CombatStats stats,
            WorldPointMm position,
            int speedMmPerTick,
            bool autoAcquire,
            byte unitKind)
        {
            // Defence in depth: both public entrances already run this gate before they
            // consume an identifier, so the only way to reach a throw here is a future
            // path that forgot to. The chokepoint stays anyway because InitializeMatch
            // funnels through it without passing through either public method.
            ValidateCapacity();

            var record = new UnitRecord
            {
                Combat = new CombatantState(entity, owner, stats),
                Position = position,
                SpeedMmPerTick = speedMmPerTick,
                AutoAcquireEnemies = autoAcquire,
                UnitKind = unitKind
            };

            _units.Add(entity, record);
            _orderedUnits.Add(record);
            _orderedUnits.Sort(
                (left, right) =>
                    left.Combat.Entity.Value.CompareTo(right.Combat.Entity.Value));

            return entity;
        }

        /// <summary>
        /// The unit-spec contract shared by <see cref="SpawnUnit"/> and
        /// <see cref="SpawnUnitWithEntity"/> (Phase 1.0 sprint 2, audit P2
        /// "SpawnUnitWithEntity skips all unit validation"). Both entries call it
        /// before touching <c>_nextEntityValue</c> or the rosters, so a rejected spawn
        /// cannot consume an id or half-add a unit. Parameter names are part of the
        /// contract: they are what callers see in <c>ParamName</c>.
        /// </summary>
        private static void ValidateUnitSpec(
            PlayerId owner,
            CombatStats stats,
            WorldPointMm position,
            int speedMmPerTick)
        {
            if (!owner.IsValid)
            {
                throw new ArgumentException("Owner must be valid.", nameof(owner));
            }

            if (!stats.IsValid)
            {
                throw new ArgumentException("Combat stats must be valid.", nameof(stats));
            }

            if (!position.IsWithinSimulationBounds)
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            if (speedMmPerTick <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(speedMmPerTick));
            }
        }

        public MatchCommandRejection TryEnqueueMove(
            CommandHeader header,
            EntityId[] entities,
            WorldPointMm destination,
            FormationSpec formation)
        {
            var rejection = ValidateCommonHeader(header, GameCommandType.Move);
            if (rejection != MatchCommandRejection.None)
            {
                return rejection;
            }

            if (!MoveCommand.TryCreate(
                    header,
                    entities,
                    destination,
                    formation,
                    out var command,
                    out _))
            {
                return MatchCommandRejection.InvalidPayload;
            }

            for (var index = 0; index < command.EntityCount; index++)
            {
                rejection = ValidateControllable(command.GetEntity(index), header.Player);
                if (rejection != MatchCommandRejection.None)
                {
                    return rejection;
                }
            }

            FormationSlot[] slots;
            try
            {
                slots = FormationLayout.CreateSlots(command);
            }
            catch (OverflowException)
            {
                return MatchCommandRejection.FormationOutOfBounds;
            }

            for (var index = 0; index < slots.Length; index++)
            {
                if (!slots[index].Destination.IsWithinSimulationBounds)
                {
                    return MatchCommandRejection.FormationOutOfBounds;
                }
            }

            Schedule(
                new ScheduledCommand
                {
                    Header = header,
                    Move = command,
                    MoveSlots = slots
                },
                header.RequestedTick);
            CommitSequence(header.Player, header.Sequence);
            return MatchCommandRejection.None;
        }

        public MatchCommandRejection TryEnqueueAttack(
            CommandHeader header,
            EntityId[] attackers,
            EntityId target)
        {
            var rejection = ValidateCommonHeader(header, GameCommandType.Attack);
            if (rejection != MatchCommandRejection.None)
            {
                return rejection;
            }

            if (!AttackCommand.TryCreate(header, attackers, target, out var command, out _))
            {
                return MatchCommandRejection.InvalidPayload;
            }

            for (var index = 0; index < command.AttackerCount; index++)
            {
                rejection = ValidateControllable(command.GetAttacker(index), header.Player);
                if (rejection != MatchCommandRejection.None)
                {
                    return rejection;
                }
            }

            if (!_units.TryGetValue(target, out var targetRecord))
            {
                return MatchCommandRejection.UnknownEntity;
            }

            if (!targetRecord.Combat.IsAlive)
            {
                return MatchCommandRejection.EntityNotAlive;
            }

            // Hostility is validated here, once, so an order against a teammate
            // never enters the schedule at all. Runs after ValidateControllable,
            // which is what guarantees both ids handed to AreEnemies are valid
            // owners — the 2v2 relation throws on an invalid PlayerId. The
            // target-clearing gates downstream stay as defence in depth.
            if (!_combatRelation.AreEnemies(header.Player, targetRecord.Combat.Owner))
            {
                return MatchCommandRejection.FriendlyTarget;
            }

            // Phase 4 (OD-41): Anti-maphack gate. An order against a target currently
            // hidden in the fog of war for this player is refused before entering the
            // schedule. A null or disabled filter keeps the historical passthrough
            // behaviour, so pre-replication hosts are unaffected.
            if (_visibilityFilter != null && _visibilityFilter.IsEnabled &&
                !_visibilityFilter.IsVisible(CurrentTick, header.Player, target))
            {
                return MatchCommandRejection.TargetInFogOfWar;
            }

            Schedule(new ScheduledCommand { Header = header, Attack = command }, header.RequestedTick);
            CommitSequence(header.Player, header.Sequence);
            return MatchCommandRejection.None;
        }

        public MatchCommandRejection TryEnqueueStop(CommandHeader header, EntityId[] entities)
        {
            var rejection = ValidateCommonHeader(header, GameCommandType.Stop);
            if (rejection != MatchCommandRejection.None)
            {
                return rejection;
            }

            if (entities == null || entities.Length == 0 ||
                entities.Length > SimulationConstants.MaxSelectedEntities)
            {
                return MatchCommandRejection.InvalidPayload;
            }

            var canonical = new EntityId[entities.Length];
            Array.Copy(entities, canonical, entities.Length);
            Array.Sort(canonical, (left, right) => left.Value.CompareTo(right.Value));

            for (var index = 0; index < canonical.Length; index++)
            {
                if (index > 0 && canonical[index] == canonical[index - 1])
                {
                    return MatchCommandRejection.InvalidPayload;
                }

                rejection = ValidateControllable(canonical[index], header.Player);
                if (rejection != MatchCommandRejection.None)
                {
                    return rejection;
                }
            }

            Schedule(
                new ScheduledCommand { Header = header, StopEntities = canonical },
                header.RequestedTick);
            CommitSequence(header.Player, header.Sequence);
            return MatchCommandRejection.None;
        }

        /// <summary>
        /// Simulates exactly one tick. The hosting layer (the server tick
        /// driver, ADR-007) decides how often this is called (20 Hz
        /// contract). The per-tick pipeline is the authoritative simulation
        /// contract; the client presentation layer no longer runs these
        /// phases locally and instead consumes
        /// <see cref="GetAllSnapshots"/> after each tick:
        ///   1. Apply scheduled commands (player-ordered).
        ///   2. Clear invalid attack targets (dead or friendly).
        ///   3. Auto-acquire closest enemy for units with the flag set.
        ///   4. Move units, with attack-target pursuit overriding move orders.
        ///   5. Resolve combat via Core.CombatTickResolver.
        ///
        /// A server without units advances the tick counter but simulates
        /// nothing and must not report a terminal (draw) outcome before the
        /// match is initialized — the server tick lifecycle is independent
        /// of match initialization (local host and future dedicated server).
        /// </summary>
        public void TickOnce()
        {
            CurrentTick++;

            if (Outcome.IsTerminal || UnitCount == 0)
            {
                // Orders scheduled for a future tick can never run once this method
                // stops executing them, so their buckets are released instead of
                // sitting in the dictionary for the rest of the match.
                if (Outcome.IsTerminal)
                {
                    _scheduledCommands.Clear();
                }

                return;
            }

            ExecuteScheduledCommands(CurrentTick);
            ClearInvalidAttackTargets();
            AcquireAutomaticTargets();
            MoveUnits();
            ResolveCombat(CurrentTick);
        }

        /// <summary>Convenience for tests and batch hosting.</summary>
        public void ExecuteTicks(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            for (var index = 0; index < count; index++)
            {
                TickOnce();
            }
        }

        public bool TryGetUnit(EntityId entity, out ServerUnitSnapshot snapshot)
        {
            if (_units.TryGetValue(entity, out var record))
            {
                snapshot = CreateSnapshot(record);
                return true;
            }

            snapshot = default;
            return false;
        }

        /// <summary>
        /// Returns the last accepted command sequence for a player (Phase
        /// 2.4, ADR-008 / OD-8). Additive read-only accessor used for
        /// reconnect receipts: a reconnecting client resumes numbering
        /// strictly above this value and the existing duplicate-sequence
        /// validation keeps replays safe. Returns false when no command from
        /// this player has been accepted yet. Simulation semantics are
        /// unchanged.
        /// </summary>
        public bool TryGetLastAcceptedSequence(PlayerId player, out uint sequence)
        {
            if (_lastSequenceByPlayer.TryGetValue(player, out var lastSequence))
            {
                sequence = lastSequence;
                return true;
            }

            sequence = 0;
            return false;
        }

        /// <summary>
        /// Returns a snapshot of every unit in deterministic entity-id order.
        /// Used by the hosting layer to synchronize client presentation after
        /// each server tick.
        /// </summary>
        [Obsolete("Use CopySnapshots(Span<>) for zero-allocation access")]
        public ServerUnitSnapshot[] GetAllSnapshots()
        {
            var snapshots = new ServerUnitSnapshot[_orderedUnits.Count];
            for (var index = 0; index < _orderedUnits.Count; index++)
            {
                snapshots[index] = CreateSnapshot(_orderedUnits[index]);
            }

            return snapshots;
        }

        /// <summary>
        /// Allocation-free variant of <see cref="GetAllSnapshots"/> for the
        /// replication emitter (Phase 2.6, step 2.6.4): copies every unit in
        /// deterministic entity-id order into the caller-owned span. Returns
        /// the number of copied records, or -1 when the span cannot hold the
        /// whole world (nothing is copied then).
        /// </summary>
        public int CopySnapshots(Span<ServerUnitSnapshot> destination)
        {
            if (destination.Length < _orderedUnits.Count)
            {
                return -1;
            }

            for (var index = 0; index < _orderedUnits.Count; index++)
            {
                destination[index] = CreateSnapshot(_orderedUnits[index]);
            }

            return _orderedUnits.Count;
        }

        private static ServerUnitSnapshot CreateSnapshot(UnitRecord record) =>
            new ServerUnitSnapshot(
                record.Combat.Entity,
                record.Combat.Owner,
                record.Position,
                record.Combat.CurrentHealth,
                record.HasMoveTarget,
                record.MoveTarget,
                record.Combat.AttackTarget,
                record.AutoAcquireEnemies,
                record.UnitKind);

        private MatchCommandRejection ValidateCommonHeader(
            CommandHeader header,
            GameCommandType expectedType)
        {
            if (Outcome.IsTerminal)
            {
                return MatchCommandRejection.MatchOver;
            }

            if (header.Validate(CurrentTick, AcceptedPastTicks, AcceptedFutureTicks) !=
                CommandValidationResult.Accepted)
            {
                return MatchCommandRejection.HeaderRejected;
            }

            if (header.Type != expectedType)
            {
                return MatchCommandRejection.UnsupportedCommandType;
            }

            if (_lastSequenceByPlayer.TryGetValue(header.Player, out var lastSequence) &&
                header.Sequence <= lastSequence)
            {
                return MatchCommandRejection.DuplicateSequence;
            }

            return MatchCommandRejection.None;
        }

        private MatchCommandRejection ValidateControllable(EntityId entity, PlayerId player)
        {
            if (!_units.TryGetValue(entity, out var record))
            {
                return MatchCommandRejection.UnknownEntity;
            }

            if (record.Combat.Owner != player)
            {
                return MatchCommandRejection.NotEntityOwner;
            }

            if (!record.Combat.IsAlive)
            {
                return MatchCommandRejection.EntityNotAlive;
            }

            return MatchCommandRejection.None;
        }

        private void CommitSequence(PlayerId player, uint sequence) =>
            _lastSequenceByPlayer[player] = sequence;

        private void Schedule(ScheduledCommand command, ulong requestedTick)
        {
            var executeTick = requestedTick <= CurrentTick ? CurrentTick + 1 : requestedTick;

            if (!_scheduledCommands.TryGetValue(executeTick, out var list))
            {
                list = new List<ScheduledCommand>();
                _scheduledCommands[executeTick] = list;
            }

            list.Add(command);
        }

        private void ExecuteScheduledCommands(ulong tick)
        {
            if (!_scheduledCommands.TryGetValue(tick, out var commands))
            {
                return;
            }

            commands.Sort(ScheduledCommandComparison);

            for (var index = 0; index < commands.Count; index++)
            {
                ExecuteCommand(commands[index]);
            }

            _scheduledCommands.Remove(tick);
        }

        private void ExecuteCommand(ScheduledCommand command)
        {
            if (command.Move != null)
            {
                ExecuteMove(command);
                return;
            }

            if (command.Attack != null)
            {
                ExecuteAttack(command);
                return;
            }

            if (command.StopEntities != null)
            {
                ExecuteStop(command);
            }
        }

        private void ExecuteMove(ScheduledCommand command)
        {
            // Apply the slots TryEnqueueMove already computed and bounds-checked, so this
            // tick allocates nothing (Phase 1.0, P1-3). Recomputing here would also be the
            // unguarded OverflowException path the enqueue try/catch exists to close.
            var slots = command.MoveSlots ?? FormationLayout.CreateSlots(command.Move);

            for (var index = 0; index < slots.Length; index++)
            {
                var slot = slots[index];
                if (!_units.TryGetValue(slot.Entity, out var record) ||
                    record.Combat.Owner != command.Header.Player ||
                    !record.Combat.IsAlive)
                {
                    continue;
                }

                record.HasMoveTarget = true;
                record.MoveTarget = slot.Destination;
                record.Combat.ClearTarget();
                record.AutoAcquireEnemies = false;
            }
        }

        private void ExecuteAttack(ScheduledCommand command)
        {
            for (var index = 0; index < command.Attack.AttackerCount; index++)
            {
                if (!_units.TryGetValue(command.Attack.GetAttacker(index), out var record) ||
                    record.Combat.Owner != command.Header.Player ||
                    !record.Combat.IsAlive)
                {
                    continue;
                }

                record.HasMoveTarget = false;
                record.Combat.TryAssignTarget(command.Attack.Target);
                record.AutoAcquireEnemies = true;
            }
        }

        private void ExecuteStop(ScheduledCommand command)
        {
            for (var index = 0; index < command.StopEntities.Length; index++)
            {
                if (!_units.TryGetValue(command.StopEntities[index], out var record) ||
                    record.Combat.Owner != command.Header.Player ||
                    !record.Combat.IsAlive)
                {
                    continue;
                }

                record.HasMoveTarget = false;
                record.Combat.ClearTarget();
            }
        }

        /// <summary>
        /// Moves every alive unit. Captures start-of-tick positions first so
        /// every unit sees the same snapshot of enemy locations. Units with a
        /// valid attack target pursue it (chase when out of range, stop when in
        /// range), overriding any prior move command.
        /// </summary>
        private void MoveUnits()
        {
            _startPositionsCache.Clear();
            for (var index = 0; index < _orderedUnits.Count; index++)
            {
                var unit = _orderedUnits[index];
                _startPositionsCache[unit.Combat.Entity] = unit.Position;
            }

            for (var index = 0; index < _orderedUnits.Count; index++)
            {
                var unit = _orderedUnits[index];
                if (!unit.Combat.IsAlive)
                {
                    continue;
                }

                if (unit.Combat.HasAttackTarget &&
                    _units.TryGetValue(unit.Combat.AttackTarget, out var targetRecord) &&
                    targetRecord.Combat.IsAlive &&
                    _startPositionsCache.TryGetValue(
                        targetRecord.Combat.Entity,
                        out var targetStart))
                {
                    if (CombatMath.IsWithinRange(
                            unit.Position,
                            targetStart,
                            unit.Combat.Stats.RangeMm))
                    {
                        unit.HasMoveTarget = false;
                    }
                    else
                    {
                        unit.HasMoveTarget = true;
                        unit.MoveTarget = targetStart;
                    }
                }

                if (!unit.HasMoveTarget)
                {
                    continue;
                }

                unit.Position = PlanarMovement.StepTowards(
                    unit.Position,
                    unit.MoveTarget,
                    unit.SpeedMmPerTick);

                if (unit.Position == unit.MoveTarget)
                {
                    unit.HasMoveTarget = false;
                }
            }
        }

        /// <summary>
        /// Clears attack targets that are dead or that the current relation does
        /// not consider hostile. Run before auto-acquire and before movement on
        /// each authoritative tick.
        /// </summary>
        private void ClearInvalidAttackTargets()
        {
            for (var index = 0; index < _orderedUnits.Count; index++)
            {
                var unit = _orderedUnits[index];
                if (!unit.Combat.IsAlive || !unit.Combat.HasAttackTarget)
                {
                    continue;
                }

                if (!_units.TryGetValue(unit.Combat.AttackTarget, out var target) ||
                    !target.Combat.IsAlive ||
                    !_combatRelation.AreEnemies(unit.Combat.Owner, target.Combat.Owner))
                {
                    unit.Combat.ClearTarget();
                }
            }
        }

        /// <summary>
        /// For each alive unit with <see cref="UnitRecord.AutoAcquireEnemies"/>
        /// set and no current attack target, finds the closest unit the match's
        /// <see cref="ICombatRelation"/> treats as an enemy within
        /// <see cref="SimulationConstants.AutoAcquireRangeMm"/> and assigns it.
        /// Ties are broken by the order of iteration (stable first-found-wins).
        /// </summary>
        private void AcquireAutomaticTargets()
        {
            var maxSquared =
                (ulong)SimulationConstants.AutoAcquireRangeMm *
                SimulationConstants.AutoAcquireRangeMm;

            for (var unitIndex = 0; unitIndex < _orderedUnits.Count; unitIndex++)
            {
                var unit = _orderedUnits[unitIndex];
                if (!unit.Combat.IsAlive ||
                    unit.Combat.HasAttackTarget ||
                    !unit.AutoAcquireEnemies)
                {
                    continue;
                }

                var bestDistance = ulong.MaxValue;
                EntityId bestTarget = default;

                for (var targetIndex = 0;
                    targetIndex < _orderedUnits.Count;
                    targetIndex++)
                {
                    var candidate = _orderedUnits[targetIndex];
                    if (!candidate.Combat.IsAlive ||
                        !_combatRelation.AreEnemies(unit.Combat.Owner, candidate.Combat.Owner))
                    {
                        continue;
                    }

                    var distance = CombatMath.SquaredDistance(
                        unit.Position,
                        candidate.Position);

                    if (distance > maxSquared || distance >= bestDistance)
                    {
                        continue;
                    }

                    bestDistance = distance;
                    bestTarget = candidate.Combat.Entity;
                }

                if (bestTarget.IsValid)
                {
                    unit.Combat.TryAssignTarget(bestTarget);
                }
            }
        }

        private void ResolveCombat(ulong tick)
        {
            var count = _orderedUnits.Count;
            if (_combatantInputsCache.Length < count)
            {
                var newCapacity = Math.Max(count, Math.Max(_combatantInputsCache.Length * 2, 64));
                _combatantInputsCache = new CombatantTickInput[newCapacity];
            }

            for (var index = 0; index < count; index++)
            {
                var unit = _orderedUnits[index];
                _combatantInputsCache[index] = new CombatantTickInput(unit.Combat, unit.Position);
            }

            var result = _combatResolver.Resolve(
                _combatantInputsCache,
                count,
                tick,
                _combatRelation);

            if (result.Outcome.IsTerminal)
            {
                Outcome = result.Outcome;

                // Flushed here rather than left for the next TickOnce: a host that stops
                // driving ticks the moment a match ends never runs that guard, and orders
                // scheduled for a future tick can no longer execute.
                _scheduledCommands.Clear();
            }
        }

        private static int CompareScheduledCommands(
            ScheduledCommand left,
            ScheduledCommand right)
        {
            var playerComparison = left.Header.Player.Value.CompareTo(right.Header.Player.Value);
            return playerComparison != 0
                ? playerComparison
                : left.Header.Sequence.CompareTo(right.Header.Sequence);
        }
    }
}