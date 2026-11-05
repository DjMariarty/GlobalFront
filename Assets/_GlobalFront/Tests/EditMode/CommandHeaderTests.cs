using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    public sealed class CommandHeaderTests
    {
        [Test]
        public void Validate_AcceptsValidCommandInsideTickWindow()
        {
            var header = new CommandHeader(
                new PlayerId(10),
                sequence: 42,
                requestedTick: 103,
                type: GameCommandType.AttackMove);

            var result = header.Validate(
                currentServerTick: 100,
                acceptedPastTicks: 2,
                acceptedFutureTicks: 5);

            Assert.That(result, Is.EqualTo(CommandValidationResult.Accepted));
        }

        [TestCase(0)]
        [TestCase(11)]
        public void Validate_RejectsPlayerOutsideTenSlots(byte playerValue)
        {
            var header = new CommandHeader(
                new PlayerId(playerValue),
                sequence: 1,
                requestedTick: 100,
                type: GameCommandType.Move);

            var result = header.Validate(100, 2, 5);

            Assert.That(result, Is.EqualTo(CommandValidationResult.InvalidPlayer));
        }

        [Test]
        public void Validate_RejectsStaleCommand()
        {
            var header = new CommandHeader(
                new PlayerId(1),
                sequence: 1,
                requestedTick: 90,
                type: GameCommandType.Move);

            var result = header.Validate(100, 2, 5);

            Assert.That(result, Is.EqualTo(CommandValidationResult.TickTooOld));
        }

        [Test]
        public void Validate_RejectsCommandTooFarInFuture()
        {
            var header = new CommandHeader(
                new PlayerId(1),
                sequence: 1,
                requestedTick: 106,
                type: GameCommandType.Move);

            var result = header.Validate(100, 2, 5);

            Assert.That(result, Is.EqualTo(CommandValidationResult.TickTooFarAhead));
        }

        [Test]
        public void GameCommandType_CarriesTheSetRallyValue()
        {
            Assert.That((byte)GameCommandType.SetRally, Is.EqualTo(9), "Step 5.3.1: append-only addition after UseAbility = 8");
        }

        [Test]
        public void Validate_AcceptsSetRally_AsTheNewUpperBound()
        {
            var header = new CommandHeader(
                new PlayerId(1),
                sequence: 1,
                requestedTick: 100,
                type: GameCommandType.SetRally);

            Assert.That(header.Validate(100, 2, 5), Is.EqualTo(CommandValidationResult.Accepted));
        }

        [Test]
        public void Validate_RejectsTypeBeyondTheSetRallyBound()
        {
            var header = new CommandHeader(
                new PlayerId(1),
                sequence: 1,
                requestedTick: 100,
                type: (GameCommandType)10);

            Assert.That(header.Validate(100, 2, 5), Is.EqualTo(CommandValidationResult.UnknownCommand), "the first byte past SetRally is unknown again");
        }
    }
}
