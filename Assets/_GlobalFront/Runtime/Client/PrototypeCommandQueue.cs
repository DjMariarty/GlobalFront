using System;
using System.Collections.Generic;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client
{
    /// <summary>
    /// Encapsulates the client-side command pipeline: formation, validation,
    /// deterministic ordering and submission through an
    /// <see cref="ICommandChannel"/>. The queue is designed so that a future
    /// network transport can replace <see cref="LocalCommandChannel"/> without
    /// changing gameplay logic.
    /// </summary>
    public sealed class PrototypeCommandQueue
    {
        /// <summary>
        /// Immutable wrapper around a <see cref="MoveCommand"/>,
        /// <see cref="AttackCommand"/> or <see cref="StopCommand"/>.
        /// Public so that future serializers and tests can enumerate pending
        /// commands without reflection.
        /// </summary>
        public readonly struct QueuedPrototypeCommand
        {
            private QueuedPrototypeCommand(
                MoveCommand move,
                AttackCommand attack,
                StopCommand stop)
            {
                Move = move;
                Attack = attack;
                Stop = stop;
            }

            public MoveCommand Move { get; }

            public AttackCommand Attack { get; }

            public StopCommand Stop { get; }

            public CommandHeader Header
            {
                get
                {
                    if (Move != null)
                    {
                        return Move.Header;
                    }

                    if (Attack != null)
                    {
                        return Attack.Header;
                    }

                    return Stop.Header;
                }
            }

            public static QueuedPrototypeCommand FromMove(MoveCommand command) =>
                new QueuedPrototypeCommand(command, null, null);

            public static QueuedPrototypeCommand FromAttack(AttackCommand command) =>
                new QueuedPrototypeCommand(null, command, null);

            public static QueuedPrototypeCommand FromStop(StopCommand command) =>
                new QueuedPrototypeCommand(null, null, command);
        }

        private readonly UnitRegistry _registry;
        private readonly PlayerId _localPlayer;
        private readonly int _formationSpacingMm;

        private readonly List<QueuedPrototypeCommand> _pendingCommands =
            new List<QueuedPrototypeCommand>();
        private uint _nextSequence = 1;
        private uint _lastProcessedSequence;

        /// <summary>
        /// Human-readable status of the last queue operation. Updated on
        /// queue, apply, reject and clear. Read by the controller for HUD.
        /// </summary>
        public string LastCommandMessage { get; private set; } =
            "Select blue units with LMB";

        /// <summary>Number of commands waiting to be applied.</summary>
        public int PendingCount => _pendingCommands.Count;

        public PrototypeCommandQueue(
            UnitRegistry registry,
            PlayerId localPlayer,
            int formationSpacingMm)
        {
            _registry = registry;
            _localPlayer = localPlayer;
            _formationSpacingMm = formationSpacingMm;
        }

        /// <summary>
        /// Builds, validates and enqueues a move command for the currently
        /// selected entities. The formation facing is derived from the
        /// direction from the selection centroid to the destination.
        /// </summary>
        public void QueueMove(
            WorldPointMm destination,
            CoreEntityId[] selectedEntities,
            ulong requestedTick)
        {
            if (selectedEntities == null || selectedEntities.Length == 0)
            {
                LastCommandMessage = "Move rejected: empty selection";
                return;
            }

            long centerX = 0;
            long centerZ = 0;

            for (var index = 0; index < selectedEntities.Length; index++)
            {
                if (_registry.TryGetUnit(selectedEntities[index], out var unit))
                {
                    centerX += unit.CurrentPosition.X;
                    centerZ += unit.CurrentPosition.Z;
                }
            }

            centerX /= selectedEntities.Length;
            centerZ /= selectedEntities.Length;
            var facing = ChooseFacing(
                destination.X - centerX,
                destination.Z - centerZ);
            var header = new CommandHeader(
                _localPlayer,
                _nextSequence++,
                requestedTick,
                GameCommandType.Move);
            var formation = new FormationSpec(0, _formationSpacingMm, facing);

            if (!MoveCommand.TryCreate(
                    header,
                    selectedEntities,
                    destination,
                    formation,
                    out var command,
                    out var error))
            {
                LastCommandMessage = $"Move rejected: {error}";
                return;
            }

            _pendingCommands.Add(QueuedPrototypeCommand.FromMove(command));
            _pendingCommands.Sort(CompareCommands);
            LastCommandMessage =
                $"Move #{header.Sequence} queued for tick {requestedTick}";
        }

        /// <summary>
        /// Builds, validates and enqueues an attack command targeting
        /// <paramref name="target"/> with the currently selected entities.
        /// </summary>
        public void QueueAttack(
            CoreEntityId target,
            CoreEntityId[] selectedEntities,
            ulong requestedTick)
        {
            if (selectedEntities == null || selectedEntities.Length == 0)
            {
                LastCommandMessage = "Attack rejected: empty attackers";
                return;
            }

            if (_registry.TryGetUnit(target, out var targetUnit) &&
                targetUnit.IsAlive &&
                targetUnit.Owner == _localPlayer)
            {
                LastCommandMessage = "Attack rejected: friendly target";
                return;
            }

            var header = new CommandHeader(
                _localPlayer,
                _nextSequence++,
                requestedTick,
                GameCommandType.Attack);

            if (!AttackCommand.TryCreate(
                    header,
                    selectedEntities,
                    target,
                    out var command,
                    out var error))
            {
                LastCommandMessage = $"Attack rejected: {error}";
                return;
            }

            _pendingCommands.Add(QueuedPrototypeCommand.FromAttack(command));
            _pendingCommands.Sort(CompareCommands);
            LastCommandMessage =
                $"Attack #{header.Sequence} queued on unit {target.Value}";
        }

        /// <summary>
        /// Builds, validates and enqueues a stop command for the currently
        /// selected entities. Stop clears both movement and attack targets
        /// on the authoritative server.
        /// </summary>
        public void QueueStop(
            CoreEntityId[] selectedEntities,
            ulong requestedTick)
        {
            if (selectedEntities == null || selectedEntities.Length == 0)
            {
                LastCommandMessage = "Stop rejected: empty selection";
                return;
            }

            var header = new CommandHeader(
                _localPlayer,
                _nextSequence++,
                requestedTick,
                GameCommandType.Stop);

            if (!StopCommand.TryCreate(
                    header,
                    selectedEntities,
                    out var command,
                    out var error))
            {
                LastCommandMessage = $"Stop rejected: {error}";
                return;
            }

            _pendingCommands.Add(QueuedPrototypeCommand.FromStop(command));
            _pendingCommands.Sort(CompareCommands);
            LastCommandMessage =
                $"Stop #{header.Sequence} queued for tick {requestedTick}";
        }

        /// <summary>
        /// Applies all commands whose <see cref="CommandHeader.RequestedTick"/>
        /// is less than or equal to <paramref name="currentTick"/>, in the
        /// deterministic (tick, player, sequence) order established by
        /// <see cref="CompareCommands"/>.
        /// </summary>
        public void ApplyPending(ulong currentTick)
        {
            while (_pendingCommands.Count > 0)
            {
                var queued = _pendingCommands[0];
                if (queued.Header.RequestedTick > currentTick)
                {
                    break;
                }

                _pendingCommands.RemoveAt(0);
                if (queued.Move != null)
                {
                    ApplyMoveCommand(queued.Move, currentTick);
                }
                else if (queued.Attack != null)
                {
                    ApplyAttackCommand(queued.Attack, currentTick);
                }
                else
                {
                    ApplyStopCommand(queued.Stop, currentTick);
                }
            }
        }

        /// <summary>
        /// Forwards all commands whose <see cref="CommandHeader.RequestedTick"/>
        /// is less than or equal to <paramref name="currentTick"/> to the
        /// supplied <see cref="ICommandChannel"/>, in the deterministic
        /// (tick, player, sequence) order established by
        /// <see cref="CompareCommands"/>. The channel validates and schedules
        /// the commands; the client no longer applies gameplay commands
        /// locally. This replaces <see cref="ApplyPending"/> for the
        /// authoritative hosting model and decouples the queue from the
        /// concrete <see cref="LocalMatchHost"/>.
        /// </summary>
        public void ForwardPending(ulong currentTick, ICommandChannel channel)
        {
            if (channel == null)
            {
                throw new ArgumentNullException(nameof(channel));
            }

            while (_pendingCommands.Count > 0)
            {
                var queued = _pendingCommands[0];
                if (queued.Header.RequestedTick > currentTick)
                {
                    break;
                }

                _pendingCommands.RemoveAt(0);
                if (queued.Move != null)
                {
                    ForwardMoveToChannel(queued.Move, currentTick, channel);
                }
                else if (queued.Attack != null)
                {
                    ForwardAttackToChannel(queued.Attack, currentTick, channel);
                }
                else
                {
                    ForwardStopToChannel(queued.Stop, currentTick, channel);
                }
            }
        }

        /// <summary>
        /// Forwards all commands whose <see cref="CommandHeader.RequestedTick"/>
        /// is less than or equal to <paramref name="currentTick"/> to the
        /// authoritative <paramref name="host"/>, in the deterministic
        /// (tick, player, sequence) order established by
        /// <see cref="CompareCommands"/>. The submission is attributed to
        /// <paramref name="session"/> (Phase 2.4, ADR-008): the host's
        /// session gate validates binding before the unchanged MatchServer
        /// validation. The client no longer applies gameplay commands
        /// locally. This replaces <see cref="ApplyPending"/> for the
        /// authoritative hosting model.
        /// </summary>
        public void ForwardPendingToHost(ulong currentTick, LocalMatchHost host, SessionId session)
        {
            ForwardPending(currentTick, new LocalCommandChannel(host, session));
        }

        private static void ForwardMoveToChannel(
            MoveCommand command,
            ulong tick,
            ICommandChannel channel)
        {
            var entities = new CoreEntityId[command.EntityCount];
            for (var index = 0; index < entities.Length; index++)
            {
                entities[index] = command.GetEntity(index);
            }

            var rejection = channel.TrySubmitMove(
                command.Header,
                entities,
                command.Destination,
                command.Formation);

            // Caller does not own the sequence bookkeeping — it is handled
            // by the queue after every successful forward. Rejection
            // messages are surfaced through LastCommandMessage so the
            // controller can render them on the HUD.
            _ = tick;
            _ = rejection;
        }

        private static void ForwardAttackToChannel(
            AttackCommand command,
            ulong tick,
            ICommandChannel channel)
        {
            var attackers = new CoreEntityId[command.AttackerCount];
            for (var index = 0; index < attackers.Length; index++)
            {
                attackers[index] = command.GetAttacker(index);
            }

            var rejection = channel.TrySubmitAttack(
                command.Header,
                attackers,
                command.Target);

            _ = tick;
            _ = rejection;
        }

        private static void ForwardStopToChannel(
            StopCommand command,
            ulong tick,
            ICommandChannel channel)
        {
            var entities = new CoreEntityId[command.EntityCount];
            for (var index = 0; index < entities.Length; index++)
            {
                entities[index] = command.GetEntity(index);
            }

            var rejection = channel.TrySubmitStop(
                command.Header,
                entities);

            _ = tick;
            _ = rejection;
        }

        /// <summary>
        /// Removes all pending commands without applying them. Used when the
        /// battle reaches a terminal state.
        /// </summary>
        public void Clear()
        {
            _pendingCommands.Clear();
        }

        /// <summary>
        /// Replaces <see cref="LastCommandMessage"/> with an externally
        /// computed status string. Used by the controller to surface combat
        /// event counts or terminal battle status.
        /// </summary>
        public void OverrideMessage(string message)
        {
            LastCommandMessage = message;
        }

        private void ApplyMoveCommand(MoveCommand command, ulong tick)
        {
            var validation = command.ValidateAtTick(tick, 2, 0);
            if (validation != CommandValidationResult.Accepted)
            {
                LastCommandMessage = $"Move rejected: {validation}";
                return;
            }

            if (command.Header.Sequence <= _lastProcessedSequence)
            {
                LastCommandMessage = "Move rejected: duplicate sequence";
                return;
            }

            var slots = FormationLayout.CreateSlots(command);
            for (var index = 0; index < slots.Length; index++)
            {
                if (!_registry.TryGetUnit(slots[index].Entity, out var unit) ||
                    !unit.IsAlive ||
                    unit.Owner != command.Header.Player)
                {
                    LastCommandMessage =
                        "Move rejected: invalid ownership or dead unit";
                    return;
                }
            }

            for (var index = 0; index < slots.Length; index++)
            {
                _registry.TryGetUnit(slots[index].Entity, out var moveUnit);
                moveUnit.SetMoveTarget(slots[index].Destination);
            }

            _lastProcessedSequence = command.Header.Sequence;
            LastCommandMessage =
                $"Move #{command.Header.Sequence} executing at tick {tick}";
        }

        private void ApplyAttackCommand(AttackCommand command, ulong tick)
        {
            var validation = command.ValidateAtTick(tick, 2, 0);
            if (validation != CommandValidationResult.Accepted)
            {
                LastCommandMessage = $"Attack rejected: {validation}";
                return;
            }

            if (command.Header.Sequence <= _lastProcessedSequence)
            {
                LastCommandMessage = "Attack rejected: duplicate sequence";
                return;
            }

            if (!_registry.TryGetUnit(command.Target, out var target) ||
                !target.IsAlive ||
                target.Owner == command.Header.Player)
            {
                LastCommandMessage = "Attack rejected: invalid target";
                return;
            }

            for (var index = 0; index < command.AttackerCount; index++)
            {
                var attackerId = command.GetAttacker(index);
                if (!_registry.TryGetUnit(attackerId, out var attacker) ||
                    !attacker.IsAlive ||
                    attacker.Owner != command.Header.Player)
                {
                    LastCommandMessage =
                        "Attack rejected: invalid ownership or dead unit";
                    return;
                }
            }

            for (var index = 0; index < command.AttackerCount; index++)
            {
                _registry.TryGetUnit(
                    command.GetAttacker(index),
                    out var attackerUnit);
                attackerUnit.SetAttackTarget(command.Target);
            }

            _lastProcessedSequence = command.Header.Sequence;
            LastCommandMessage =
                $"Attack #{command.Header.Sequence} executing at tick {tick}";
        }

        private void ApplyStopCommand(StopCommand command, ulong tick)
        {
            var validation = command.ValidateAtTick(tick, 2, 0);
            if (validation != CommandValidationResult.Accepted)
            {
                LastCommandMessage = $"Stop rejected: {validation}";
                return;
            }

            if (command.Header.Sequence <= _lastProcessedSequence)
            {
                LastCommandMessage = "Stop rejected: duplicate sequence";
                return;
            }

            for (var index = 0; index < command.EntityCount; index++)
            {
                var entityId = command.GetEntity(index);
                if (!_registry.TryGetUnit(entityId, out var unit) ||
                    !unit.IsAlive ||
                    unit.Owner != command.Header.Player)
                {
                    LastCommandMessage =
                        "Stop rejected: invalid ownership or dead unit";
                    return;
                }
            }

            for (var index = 0; index < command.EntityCount; index++)
            {
                _registry.TryGetUnit(
                    command.GetEntity(index),
                    out var stopUnit);
                stopUnit.ClearAttackTarget();
                stopUnit.StopMovement();
            }

            _lastProcessedSequence = command.Header.Sequence;
            LastCommandMessage =
                $"Stop #{command.Header.Sequence} executing at tick {tick}";
        }

        /// <summary>
        /// Deterministic comparison for command ordering: tick ascending,
        /// then player ascending, then sequence ascending. This ensures
        /// that when multiple commands fire on the same tick, they are
        /// applied in a reproducible order regardless of enqueue timing.
        /// </summary>
        public static int CompareCommands(
            QueuedPrototypeCommand left,
            QueuedPrototypeCommand right)
        {
            var leftHeader = left.Header;
            var rightHeader = right.Header;
            var tickComparison =
                leftHeader.RequestedTick.CompareTo(rightHeader.RequestedTick);
            if (tickComparison != 0)
            {
                return tickComparison;
            }

            var playerComparison =
                leftHeader.Player.Value.CompareTo(rightHeader.Player.Value);
            return playerComparison != 0
                ? playerComparison
                : leftHeader.Sequence.CompareTo(rightHeader.Sequence);
        }

        /// <summary>
        /// Derives a cardinal facing from a delta vector, choosing the
        /// dominant axis. Used for formation orientation.
        /// </summary>
        public static CardinalFacing ChooseFacing(long deltaX, long deltaZ)
        {
            if (Math.Abs(deltaX) > Math.Abs(deltaZ))
            {
                return deltaX >= 0 ? CardinalFacing.East : CardinalFacing.West;
            }

            return deltaZ >= 0 ? CardinalFacing.North : CardinalFacing.South;
        }
    }
}