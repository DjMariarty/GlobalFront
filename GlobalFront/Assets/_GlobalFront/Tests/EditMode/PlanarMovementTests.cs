using System;
using GlobalFront.Core.Movement;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    public sealed class PlanarMovementTests
    {
        [Test]
        public void StepTowards_MovesByConfiguredAxisDistance()
        {
            var result = PlanarMovement.StepTowards(
                new WorldPointMm(0, 0),
                new WorldPointMm(1000, 0),
                350);

            Assert.That(result, Is.EqualTo(new WorldPointMm(350, 0)));
        }

        [Test]
        public void StepTowards_UsesDeterministicThreeFourFiveRatio()
        {
            var result = PlanarMovement.StepTowards(
                new WorldPointMm(0, 0),
                new WorldPointMm(3000, 4000),
                1000);

            Assert.That(result, Is.EqualTo(new WorldPointMm(600, 800)));
        }

        [Test]
        public void StepTowards_SnapsWithoutOvershooting()
        {
            var target = new WorldPointMm(120, -80);

            Assert.That(
                PlanarMovement.StepTowards(new WorldPointMm(0, 0), target, 350),
                Is.EqualTo(target));
        }

        [Test]
        public void StepTowards_RejectsNonPositiveStep()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PlanarMovement.StepTowards(
                    new WorldPointMm(0, 0),
                    new WorldPointMm(1000, 0),
                    0));
        }

        [Test]
        public void StepTowards_RejectsCoordinatesOutsideProtocolBounds()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PlanarMovement.StepTowards(
                    new WorldPointMm(1_000_000_001, 0),
                    new WorldPointMm(0, 0),
                    100));
        }
    }
}
