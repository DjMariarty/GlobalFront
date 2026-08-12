using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    public sealed class FormationLayoutTests
    {
        [Test]
        public void OneEntity_IsPlacedAtFormationCenter()
        {
            var slots = FormationLayout.CreateSlots(CreateCommand(
                new ulong[] { 1 },
                new WorldPointMm(10000, 20000),
                CardinalFacing.North));

            Assert.That(slots.Length, Is.EqualTo(1));
            Assert.That(slots[0].Destination, Is.EqualTo(new WorldPointMm(10000, 20000)));
        }

        [Test]
        public void FourEntities_AreCenteredInTwoByTwoNorthFormation()
        {
            var slots = FormationLayout.CreateSlots(CreateCommand(
                new ulong[] { 1, 2, 3, 4 },
                new WorldPointMm(10000, 20000),
                CardinalFacing.North));

            Assert.That(slots[0].Destination, Is.EqualTo(new WorldPointMm(9000, 21000)));
            Assert.That(slots[1].Destination, Is.EqualTo(new WorldPointMm(11000, 21000)));
            Assert.That(slots[2].Destination, Is.EqualTo(new WorldPointMm(9000, 19000)));
            Assert.That(slots[3].Destination, Is.EqualTo(new WorldPointMm(11000, 19000)));
        }

        [Test]
        public void FiveEntities_CenterTheShortFinalRow()
        {
            var slots = FormationLayout.CreateSlots(CreateCommand(
                new ulong[] { 5, 4, 3, 2, 1 },
                new WorldPointMm(10000, 20000),
                CardinalFacing.North));

            Assert.That(slots[0].Entity, Is.EqualTo(new EntityId(1)));
            Assert.That(slots[0].Destination, Is.EqualTo(new WorldPointMm(8000, 21000)));
            Assert.That(slots[1].Destination, Is.EqualTo(new WorldPointMm(10000, 21000)));
            Assert.That(slots[2].Destination, Is.EqualTo(new WorldPointMm(12000, 21000)));
            Assert.That(slots[3].Destination, Is.EqualTo(new WorldPointMm(9000, 19000)));
            Assert.That(slots[4].Destination, Is.EqualTo(new WorldPointMm(11000, 19000)));
        }

        [Test]
        public void EastFacing_RotatesLateralAxisExactly()
        {
            var slots = FormationLayout.CreateSlots(CreateCommand(
                new ulong[] { 1, 2 },
                new WorldPointMm(10000, 20000),
                CardinalFacing.East));

            Assert.That(slots[0].Destination, Is.EqualTo(new WorldPointMm(10000, 21000)));
            Assert.That(slots[1].Destination, Is.EqualTo(new WorldPointMm(10000, 19000)));
        }

        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(3, 2)]
        [TestCase(4, 2)]
        [TestCase(5, 3)]
        [TestCase(8, 3)]
        [TestCase(9, 3)]
        [TestCase(10, 4)]
        public void AutoColumnCount_UsesIntegerCeilSquareRoot(int units, int expected)
        {
            Assert.That(FormationLayout.GetAutoColumnCount(units), Is.EqualTo(expected));
        }

        private static MoveCommand CreateCommand(
            ulong[] entityValues,
            WorldPointMm destination,
            CardinalFacing facing)
        {
            var entities = new EntityId[entityValues.Length];
            for (var index = 0; index < entityValues.Length; index++)
            {
                entities[index] = new EntityId(entityValues[index]);
            }

            var header = new CommandHeader(
                new PlayerId(1),
                1,
                100,
                GameCommandType.Move);
            Assert.That(
                MoveCommand.TryCreate(
                    header,
                    entities,
                    destination,
                    new FormationSpec(0, 2000, facing),
                    out var command,
                    out var error),
                Is.True,
                error.ToString());
            return command;
        }
    }
}
