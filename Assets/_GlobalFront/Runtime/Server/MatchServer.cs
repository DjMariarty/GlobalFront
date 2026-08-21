using System;
using System.Collections.Generic;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;

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
        FormationOutOfBounds = 9
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
            bool autoAcquireEnemies)
        {
            Entity = entity;
            Owner = owner;
            Position = position;
            CurrentHealth = currentHealth;
            HasMoveTarget = hasMoveTarget;
            MoveTarget = moveTarget;
            AttackTarget = attackTarget;
            AutoAcquireEnemies = autoAcquireEnemies;
        }

        public EntityId Entity { get; }

        public PlayerId Owner { get; }

        public WorldPointMm Position { get; }

        public int CurrentHealth { get; }

        public bool HasMoveTarget { get; }

        public WorldPointMm MoveTarget { get; }

        public EntityId AttackTarget { get; }

        public bool AutoAcquireEnemies { get; }

        public bool Equals(ServerUnitSnapshot other) =>
            Entity == other.Entity &&
            Owner == other.Owner &&
            Position == other.Position &&
            CurrentHealth == other.CurrentHealth &&
            HasMoveTarget == other.HasMoveTarget &&
            MoveTarget == other.MoveTarget &&
            AttackTarget == other.AttackTarget &&
            AutoAcquireEnemies == other.AutoAcquireEnemies;

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

        private sealed class UnitRecord
        {
            public CombatantState Combat;

            public WorldPointMm Position;

            public int SpeedMmPerTick;

            public bool HasMoveTarget;

            public WorldPointMm MoveTarget;

            public bool AutoAcquireEnemies;
        }

        private sealed class ScheduledCommand
        {
            public CommandHeader Header;

            public MoveCommand Move;

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
        /// Thrown when the match has already been initialized (units exist).
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

            var entityIds = new EntityId[config.UnitCount];
            for (var index = 0; index < config.UnitCount; index++)
            {
                var spec = config.Units[index];
                var entity = new EntityId(_nextEntityValue++);
                SpawnUnitCore(entity, spec.Owner, config.UnitStats, spec.Position, spec.SpeedMmPerTick, spec.AutoAcquireEnemies);
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
        public EntityId SpawnUnit(
            PlayerId owner,
            CombatStats stats,
            WorldPointMm position,
            int speedMmPerTick,
            bool autoAcquire = false)
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

            var entity = new EntityId(_nextEntityValue++);
            return SpawnUnitCore(entity, owner, stats, position, speedMmPerTick, autoAcquire);
        }

        /// <summary>
        /// Adds a unit with a caller-supplied entity identifier. Intended for
        /// shadow/parity integrations where the authoritative id assignment
        /// must match an external source (e.g. client scene ids). The caller is
        /// responsible for ensuring uniqueness and ordering.
        /// </summary>
        public EntityId SpawnUnitWithEntity(
            EntityId entity,
            PlayerId owner,
            CombatStats stats,
            WorldPointMm position,
            int speedMmPerTick,
            bool autoAcquire = false)
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

            if (_nextEntityValue <= entity.Value)
            {
                _nextEntityValue = entity.Value + 1;
            }

            return SpawnUnitCore(entity, owner, stats, position, speedMmPerTick, autoAcquire);
        }

        private EntityId SpawnUnitCore(
            EntityId entity,
            PlayerId owner,
            CombatStats stats,
            WorldPointMm position,
            int speedMmPerTick,
            bool autoAcquire)
        {
            var record = new UnitRecord
            {
                Combat = new CombatantState(entity, owner, stats),
                Position = position,
                SpeedMmPerTick = speedMmPerTick,
                AutoAcquireEnemies = autoAcquire
            };

            _units.Add(entity, record);
            _orderedUnits.Add(record);
            _orderedUnits.Sort(
                (left, right) =>
                    left.Combat.Entity.Value.CompareTo(right.Combat.Entity.Value));

            return entity;
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

            Schedule(new ScheduledCommand { Header = header, Move = command }, header.RequestedTick);
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

            if (entities == null || entities.Length == 0)
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
        /// Returns a snapshot of every unit in deterministic entity-id order.
        /// Used by the hosting layer to synchronize client presentation after
        /// each server tick.
        /// </summary>
        public ServerUnitSnapshot[] GetAllSnapshots()
        {
            var snapshots = new ServerUnitSnapshot[_orderedUnits.Count];
            for (var index = 0; index < _orderedUnits.Count; index++)
            {
                snapshots[index] = CreateSnapshot(_orderedUnits[index]);
            }

            return snapshots;
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
                record.AutoAcquireEnemies);

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

            commands.Sort(CompareScheduledCommands);

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
            var slots = FormationLayout.CreateSlots(command.Move);

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
            var startPositions =
                new Dictionary<EntityId, WorldPointMm>(_orderedUnits.Count);
            for (var index = 0; index < _orderedUnits.Count; index++)
            {
                var unit = _orderedUnits[index];
                startPositions[unit.Combat.Entity] = unit.Position;
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
                    startPositions.TryGetValue(
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
        /// Clears attack targets that are dead or belong to the same owner.
        /// Run before auto-acquire and before movement on each authoritative
        /// tick.
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
                    target.Combat.Owner == unit.Combat.Owner)
                {
                    unit.Combat.ClearTarget();
                }
            }
        }

        /// <summary>
        /// For each alive unit with <see cref="UnitRecord.AutoAcquireEnemies"/>
        /// set and no current attack target, finds the closest enemy within
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
                        candidate.Combat.Owner == unit.Combat.Owner)
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
            var inputs = new CombatantTickInput[_orderedUnits.Count];
            for (var index = 0; index < _orderedUnits.Count; index++)
            {
                var unit = _orderedUnits[index];
                inputs[index] = new CombatantTickInput(unit.Combat, unit.Position);
            }

            var result = CombatTickResolver.Resolve(inputs, tick);

            if (result.Outcome.IsTerminal)
            {
                Outcome = result.Outcome;
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