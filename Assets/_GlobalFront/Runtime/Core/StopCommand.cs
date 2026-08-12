using System;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Movement
{
    public enum StopCommandError : byte
    {
        None = 0,
        InvalidHeader = 1,
        WrongCommandType = 2,
        EmptySelection = 3,
        TooManyEntities = 4,
        InvalidEntity = 5,
        DuplicateEntity = 6
    }

    /// <summary>
    /// Immutable, canonical stop payload. Entity identifiers are copied and
    /// sorted so input order cannot alter authoritative processing. A stop
    /// command clears both movement targets and attack targets for every
    /// specified entity on the authoritative server.
    /// </summary>
    public sealed class StopCommand
    {
        private readonly EntityId[] _entities;

        private StopCommand(CommandHeader header, EntityId[] entities)
        {
            Header = header;
            _entities = entities;
        }

        public CommandHeader Header { get; }

        public int EntityCount => _entities.Length;

        public EntityId GetEntity(int index) => _entities[index];

        public CommandValidationResult ValidateAtTick(
            ulong currentServerTick,
            ulong acceptedPastTicks,
            ulong acceptedFutureTicks) =>
            Header.Validate(currentServerTick, acceptedPastTicks, acceptedFutureTicks);

        public static bool TryCreate(
            CommandHeader header,
            EntityId[] entities,
            out StopCommand command,
            out StopCommandError error)
        {
            command = null;

            if (header.Type != GameCommandType.Stop)
            {
                error = StopCommandError.WrongCommandType;
                return false;
            }

            if (header.Validate(header.RequestedTick, 0, 0) != CommandValidationResult.Accepted)
            {
                error = StopCommandError.InvalidHeader;
                return false;
            }

            if (entities == null || entities.Length == 0)
            {
                error = StopCommandError.EmptySelection;
                return false;
            }

            if (entities.Length > SimulationConstants.MaxSelectedEntities)
            {
                error = StopCommandError.TooManyEntities;
                return false;
            }

            var canonicalEntities = new EntityId[entities.Length];
            Array.Copy(entities, canonicalEntities, entities.Length);
            Array.Sort(canonicalEntities, CompareEntityIds);

            for (var index = 0; index < canonicalEntities.Length; index++)
            {
                if (!canonicalEntities[index].IsValid)
                {
                    error = StopCommandError.InvalidEntity;
                    return false;
                }

                if (index > 0 && canonicalEntities[index] == canonicalEntities[index - 1])
                {
                    error = StopCommandError.DuplicateEntity;
                    return false;
                }
            }

            command = new StopCommand(header, canonicalEntities);
            error = StopCommandError.None;
            return true;
        }

        private static int CompareEntityIds(EntityId left, EntityId right) =>
            left.Value.CompareTo(right.Value);
    }
}