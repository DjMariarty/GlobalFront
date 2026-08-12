using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    public sealed class MoveCommandTests
    {
        [Test]
        public void TryCreate_CopiesAndCanonicalizesEntityOrder()
        {
            var source = new[]
            {
                new EntityId(9),
                new EntityId(2),
                new EntityId(5)
            };

            var accepted = MoveCommand.TryCreate(
                CreateHeader(GameCommandType.Move),
                source,
                new WorldPointMm(1000, 2000),
                new FormationSpec(0, 2000, CardinalFacing.North),
                out var command,
                out var error);
            source[0] = new EntityId(77);

            Assert.That(accepted, Is.True);
            Assert.That(error, Is.EqualTo(MoveCommandError.None));
            Assert.That(command.GetEntity(0), Is.EqualTo(new EntityId(2)));
            Assert.That(command.GetEntity(1), Is.EqualTo(new EntityId(5)));
            Assert.That(command.GetEntity(2), Is.EqualTo(new EntityId(9)));
        }

        [TestCase(GameCommandType.None)]
        [TestCase(GameCommandType.AttackMove)]
        public void TryCreate_RejectsNonMoveCommandType(GameCommandType type)
        {
            var accepted = MoveCommand.TryCreate(
                CreateHeader(type),
                new[] { new EntityId(1) },
                new WorldPointMm(0, 0),
                new FormationSpec(0, 2000, CardinalFacing.North),
                out _,
                out var error);

            Assert.That(accepted, Is.False);
            Assert.That(error, Is.EqualTo(MoveCommandError.WrongCommandType));
        }

        [Test]
        public void TryCreate_RejectsEmptySelection()
        {
            var accepted = MoveCommand.TryCreate(
                CreateHeader(GameCommandType.Move),
                new EntityId[0],
                new WorldPointMm(0, 0),
                new FormationSpec(0, 2000, CardinalFacing.North),
                out _,
                out var error);

            Assert.That(accepted, Is.False);
            Assert.That(error, Is.EqualTo(MoveCommandError.EmptySelection));
        }

        [Test]
        public void TryCreate_RejectsInvalidOrDuplicateEntity()
        {
            Assert.That(
                MoveCommand.TryCreate(
                    CreateHeader(GameCommandType.Move),
                    new[] { new EntityId(0), new EntityId(2) },
                    new WorldPointMm(0, 0),
                    new FormationSpec(0, 2000, CardinalFacing.North),
                    out _,
                    out var invalidError),
                Is.False);
            Assert.That(invalidError, Is.EqualTo(MoveCommandError.InvalidEntity));

            Assert.That(
                MoveCommand.TryCreate(
                    CreateHeader(GameCommandType.Move),
                    new[] { new EntityId(4), new EntityId(1), new EntityId(4) },
                    new WorldPointMm(0, 0),
                    new FormationSpec(0, 2000, CardinalFacing.North),
                    out _,
                    out var duplicateError),
                Is.False);
            Assert.That(duplicateError, Is.EqualTo(MoveCommandError.DuplicateEntity));
        }

        [Test]
        public void TryCreate_RejectsOddFormationSpacing()
        {
            var accepted = MoveCommand.TryCreate(
                CreateHeader(GameCommandType.Move),
                new[] { new EntityId(1) },
                new WorldPointMm(0, 0),
                new FormationSpec(0, 1999, CardinalFacing.North),
                out _,
                out var error);

            Assert.That(accepted, Is.False);
            Assert.That(error, Is.EqualTo(MoveCommandError.InvalidFormation));
        }

        [Test]
        public void TryCreate_RejectsOutOfRangeFormationAndDestination()
        {
            Assert.That(
                MoveCommand.TryCreate(
                    CreateHeader(GameCommandType.Move),
                    new[] { new EntityId(1) },
                    new WorldPointMm(0, 0),
                    new FormationSpec(101, 2000, CardinalFacing.North),
                    out _,
                    out var formationError),
                Is.False);
            Assert.That(formationError, Is.EqualTo(MoveCommandError.InvalidFormation));

            Assert.That(
                MoveCommand.TryCreate(
                    CreateHeader(GameCommandType.Move),
                    new[] { new EntityId(1) },
                    new WorldPointMm(1_000_000_001, 0),
                    new FormationSpec(0, 2000, CardinalFacing.North),
                    out _,
                    out var destinationError),
                Is.False);
            Assert.That(destinationError, Is.EqualTo(MoveCommandError.InvalidDestination));
        }

        private static CommandHeader CreateHeader(GameCommandType type) =>
            new CommandHeader(new PlayerId(1), 1, 100, type);
    }
}
