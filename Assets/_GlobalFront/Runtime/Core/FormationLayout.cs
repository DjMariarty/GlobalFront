using System;
using GlobalFront.Core.Model;

namespace GlobalFront.Core.Movement
{
    public readonly struct FormationSlot
    {
        public FormationSlot(EntityId entity, WorldPointMm destination)
        {
            Entity = entity;
            Destination = destination;
        }

        public EntityId Entity { get; }

        public WorldPointMm Destination { get; }
    }

    public static class FormationLayout
    {
        public static FormationSlot[] CreateSlots(MoveCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            var count = command.EntityCount;
            var columns = command.Formation.Columns == 0
                ? GetAutoColumnCount(count)
                : Math.Min(command.Formation.Columns, count);
            var rows = (count + columns - 1) / columns;
            var slots = new FormationSlot[count];
            var outputIndex = 0;

            for (var row = 0; row < rows; row++)
            {
                var remaining = count - outputIndex;
                var rowCount = Math.Min(columns, remaining);
                var forwardOffset =
                    ((long)(rows - 1 - row * 2) * command.Formation.SpacingMm) / 2;

                for (var column = 0; column < rowCount; column++)
                {
                    var lateralOffset =
                        ((long)(column * 2 - (rowCount - 1)) * command.Formation.SpacingMm) / 2;
                    var destination = RotateAndTranslate(
                        command.Destination,
                        lateralOffset,
                        forwardOffset,
                        command.Formation.Facing);
                    slots[outputIndex] = new FormationSlot(
                        command.GetEntity(outputIndex),
                        destination);
                    outputIndex++;
                }
            }

            return slots;
        }

        public static int GetAutoColumnCount(int unitCount)
        {
            if (unitCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(unitCount));
            }

            var columns = 1;
            while ((long)columns * columns < unitCount)
            {
                columns++;
            }

            return columns;
        }

        private static WorldPointMm RotateAndTranslate(
            WorldPointMm center,
            long lateral,
            long forward,
            CardinalFacing facing)
        {
            long x = center.X;
            long z = center.Z;

            switch (facing)
            {
                case CardinalFacing.North:
                    x += lateral;
                    z += forward;
                    break;
                case CardinalFacing.East:
                    x += forward;
                    z -= lateral;
                    break;
                case CardinalFacing.South:
                    x -= lateral;
                    z -= forward;
                    break;
                case CardinalFacing.West:
                    x -= forward;
                    z += lateral;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(facing));
            }

            return new WorldPointMm(checked((int)x), checked((int)z));
        }
    }
}
