using System;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Combat
{
    public enum AttackCommandError : byte
    {
        None = 0,
        InvalidHeader = 1,
        WrongCommandType = 2,
        EmptySelection = 3,
        TooManyEntities = 4,
        InvalidAttacker = 5,
        DuplicateAttacker = 6,
        InvalidTarget = 7,
        TargetIsAttacker = 8
    }

    /// <summary>
    /// Immutable attack payload. Attacker identifiers are copied and sorted so
    /// the authoritative result does not depend on client selection order.
    /// </summary>
    public sealed class AttackCommand
    {
        private readonly EntityId[] _attackers;

        private AttackCommand(
            CommandHeader header,
            EntityId[] attackers,
            EntityId target)
        {
            Header = header;
            _attackers = attackers;
            Target = target;
        }

        public CommandHeader Header { get; }

        public EntityId Target { get; }

        public int AttackerCount => _attackers.Length;

        public EntityId GetAttacker(int index) => _attackers[index];

        public CommandValidationResult ValidateAtTick(
            ulong currentServerTick,
            ulong acceptedPastTicks,
            ulong acceptedFutureTicks) =>
            Header.Validate(currentServerTick, acceptedPastTicks, acceptedFutureTicks);

        public static bool TryCreate(
            CommandHeader header,
            EntityId[] attackers,
            EntityId target,
            out AttackCommand command,
            out AttackCommandError error)
        {
            command = null;

            if (header.Type != GameCommandType.Attack)
            {
                error = AttackCommandError.WrongCommandType;
                return false;
            }

            if (header.Validate(header.RequestedTick, 0, 0) !=
                CommandValidationResult.Accepted)
            {
                error = AttackCommandError.InvalidHeader;
                return false;
            }

            if (attackers == null || attackers.Length == 0)
            {
                error = AttackCommandError.EmptySelection;
                return false;
            }

            if (attackers.Length > SimulationConstants.MaxSelectedEntities)
            {
                error = AttackCommandError.TooManyEntities;
                return false;
            }

            if (!target.IsValid)
            {
                error = AttackCommandError.InvalidTarget;
                return false;
            }

            var canonicalAttackers = new EntityId[attackers.Length];
            Array.Copy(attackers, canonicalAttackers, attackers.Length);
            Array.Sort(canonicalAttackers, CompareEntityIds);

            for (var index = 0; index < canonicalAttackers.Length; index++)
            {
                var attacker = canonicalAttackers[index];
                if (!attacker.IsValid)
                {
                    error = AttackCommandError.InvalidAttacker;
                    return false;
                }

                if (attacker == target)
                {
                    error = AttackCommandError.TargetIsAttacker;
                    return false;
                }

                if (index > 0 && attacker == canonicalAttackers[index - 1])
                {
                    error = AttackCommandError.DuplicateAttacker;
                    return false;
                }
            }

            command = new AttackCommand(header, canonicalAttackers, target);
            error = AttackCommandError.None;
            return true;
        }

        private static int CompareEntityIds(EntityId left, EntityId right) =>
            left.Value.CompareTo(right.Value);
    }
}
