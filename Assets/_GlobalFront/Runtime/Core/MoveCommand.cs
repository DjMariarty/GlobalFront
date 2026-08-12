using System;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Movement
{
    public enum MoveCommandError : byte
    {
        None = 0,
        InvalidHeader = 1,
        WrongCommandType = 2,
        EmptySelection = 3,
        TooManyEntities = 4,
        InvalidEntity = 5,
        DuplicateEntity = 6,
        InvalidFormation = 7,
        InvalidDestination = 8
    }

    /// <summary>
    /// Immutable, canonical move payload. Entity identifiers are copied and
    /// sorted so input order cannot alter authoritative formation assignment.
    /// </summary>
    public sealed class MoveCommand
    {
        private readonly EntityId[] _entities;

        private MoveCommand(
            CommandHeader header,
            EntityId[] entities,
            WorldPointMm destination,
            FormationSpec formation)
        {
            Header = header;
            _entities = entities;
            Destination = destination;
            Formation = formation;
        }

        public CommandHeader Header { get; }

        public WorldPointMm Destination { get; }

        public FormationSpec Formation { get; }

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
            WorldPointMm destination,
            FormationSpec formation,
            out MoveCommand command,
            out MoveCommandError error)
        {
            command = null;

            if (header.Type != GameCommandType.Move)
            {
                error = MoveCommandError.WrongCommandType;
                return false;
            }

            if (header.Validate(header.RequestedTick, 0, 0) != CommandValidationResult.Accepted)
            {
                error = MoveCommandError.InvalidHeader;
                return false;
            }

            if (entities == null || entities.Length == 0)
            {
                error = MoveCommandError.EmptySelection;
                return false;
            }

            if (entities.Length > SimulationConstants.MaxSelectedEntities)
            {
                error = MoveCommandError.TooManyEntities;
                return false;
            }

            if (!formation.IsValid)
            {
                error = MoveCommandError.InvalidFormation;
                return false;
            }

            if (!destination.IsWithinSimulationBounds)
            {
                error = MoveCommandError.InvalidDestination;
                return false;
            }

            var canonicalEntities = new EntityId[entities.Length];
            Array.Copy(entities, canonicalEntities, entities.Length);
            Array.Sort(canonicalEntities, CompareEntityIds);

            for (var index = 0; index < canonicalEntities.Length; index++)
            {
                if (!canonicalEntities[index].IsValid)
                {
                    error = MoveCommandError.InvalidEntity;
                    return false;
                }

                if (index > 0 && canonicalEntities[index] == canonicalEntities[index - 1])
                {
                    error = MoveCommandError.DuplicateEntity;
                    return false;
                }
            }

            command = new MoveCommand(
                header,
                canonicalEntities,
                destination,
                formation);
            error = MoveCommandError.None;
            return true;
        }

        private static int CompareEntityIds(EntityId left, EntityId right) =>
            left.Value.CompareTo(right.Value);
    }
}
