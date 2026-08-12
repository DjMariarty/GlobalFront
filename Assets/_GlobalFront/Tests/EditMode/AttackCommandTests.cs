using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    public sealed class AttackCommandTests
    {
        [Test]
        public void TryCreate_CopiesAndCanonicalizesAttackerOrder()
        {
            var source = new[]
            {
                new EntityId(9),
                new EntityId(2),
                new EntityId(5)
            };

            var accepted = AttackCommand.TryCreate(
                CreateHeader(GameCommandType.Attack),
                source,
                new EntityId(20),
                out var command,
                out var error);
            source[0] = new EntityId(77);

            Assert.That(accepted, Is.True);
            Assert.That(error, Is.EqualTo(AttackCommandError.None));
            Assert.That(command.GetAttacker(0), Is.EqualTo(new EntityId(2)));
            Assert.That(command.GetAttacker(1), Is.EqualTo(new EntityId(5)));
            Assert.That(command.GetAttacker(2), Is.EqualTo(new EntityId(9)));
        }

        [TestCase(GameCommandType.None)]
        [TestCase(GameCommandType.Move)]
        [TestCase(GameCommandType.AttackMove)]
        public void TryCreate_RejectsNonAttackCommandType(GameCommandType type)
        {
            var accepted = AttackCommand.TryCreate(
                CreateHeader(type),
                new[] { new EntityId(1) },
                new EntityId(20),
                out _,
                out var error);

            Assert.That(accepted, Is.False);
            Assert.That(error, Is.EqualTo(AttackCommandError.WrongCommandType));
        }

        [Test]
        public void TryCreate_RejectsInvalidHeader()
        {
            var invalidPlayer = new CommandHeader(
                new PlayerId(0),
                1,
                10,
                GameCommandType.Attack);
            var missingSequence = new CommandHeader(
                new PlayerId(1),
                0,
                10,
                GameCommandType.Attack);

            Assert.That(
                AttackCommand.TryCreate(
                    invalidPlayer,
                    new[] { new EntityId(1) },
                    new EntityId(20),
                    out _,
                    out var playerError),
                Is.False);
            Assert.That(playerError, Is.EqualTo(AttackCommandError.InvalidHeader));

            Assert.That(
                AttackCommand.TryCreate(
                    missingSequence,
                    new[] { new EntityId(1) },
                    new EntityId(20),
                    out _,
                    out var sequenceError),
                Is.False);
            Assert.That(sequenceError, Is.EqualTo(AttackCommandError.InvalidHeader));
        }

        [Test]
        public void TryCreate_RejectsNullOrEmptySelection()
        {
            Assert.That(
                AttackCommand.TryCreate(
                    CreateHeader(GameCommandType.Attack),
                    null,
                    new EntityId(20),
                    out _,
                    out var nullError),
                Is.False);
            Assert.That(nullError, Is.EqualTo(AttackCommandError.EmptySelection));

            Assert.That(
                AttackCommand.TryCreate(
                    CreateHeader(GameCommandType.Attack),
                    new EntityId[0],
                    new EntityId(20),
                    out _,
                    out var emptyError),
                Is.False);
            Assert.That(emptyError, Is.EqualTo(AttackCommandError.EmptySelection));
        }

        [Test]
        public void TryCreate_RejectsInvalidOrDuplicateAttacker()
        {
            Assert.That(
                AttackCommand.TryCreate(
                    CreateHeader(GameCommandType.Attack),
                    new[] { new EntityId(0), new EntityId(2) },
                    new EntityId(20),
                    out _,
                    out var invalidError),
                Is.False);
            Assert.That(invalidError, Is.EqualTo(AttackCommandError.InvalidAttacker));

            Assert.That(
                AttackCommand.TryCreate(
                    CreateHeader(GameCommandType.Attack),
                    new[] { new EntityId(4), new EntityId(1), new EntityId(4) },
                    new EntityId(20),
                    out _,
                    out var duplicateError),
                Is.False);
            Assert.That(duplicateError, Is.EqualTo(AttackCommandError.DuplicateAttacker));
        }

        [Test]
        public void TryCreate_RejectsInvalidOrSelfTarget()
        {
            Assert.That(
                AttackCommand.TryCreate(
                    CreateHeader(GameCommandType.Attack),
                    new[] { new EntityId(1) },
                    default,
                    out _,
                    out var invalidError),
                Is.False);
            Assert.That(invalidError, Is.EqualTo(AttackCommandError.InvalidTarget));

            Assert.That(
                AttackCommand.TryCreate(
                    CreateHeader(GameCommandType.Attack),
                    new[] { new EntityId(8), new EntityId(3) },
                    new EntityId(8),
                    out _,
                    out var selfError),
                Is.False);
            Assert.That(selfError, Is.EqualTo(AttackCommandError.TargetIsAttacker));
        }

        [Test]
        public void TryCreate_EnforcesSelectionLimit()
        {
            var maximum = new EntityId[SimulationConstants.MaxSelectedEntities];
            for (var index = 0; index < maximum.Length; index++)
            {
                maximum[index] = new EntityId((ulong)index + 1);
            }

            Assert.That(
                AttackCommand.TryCreate(
                    CreateHeader(GameCommandType.Attack),
                    maximum,
                    new EntityId(1000),
                    out _,
                    out var maximumError),
                Is.True);
            Assert.That(maximumError, Is.EqualTo(AttackCommandError.None));

            var tooMany = new EntityId[SimulationConstants.MaxSelectedEntities + 1];
            for (var index = 0; index < tooMany.Length; index++)
            {
                tooMany[index] = new EntityId((ulong)index + 1);
            }

            Assert.That(
                AttackCommand.TryCreate(
                    CreateHeader(GameCommandType.Attack),
                    tooMany,
                    new EntityId(1000),
                    out _,
                    out var tooManyError),
                Is.False);
            Assert.That(tooManyError, Is.EqualTo(AttackCommandError.TooManyEntities));
        }

        private static CommandHeader CreateHeader(GameCommandType type) =>
            new CommandHeader(new PlayerId(1), 1, 100, type);
    }
}
