using GlobalFront.Core.Catalog;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Step 5.5.3: the China Horde Bonus calculator. A group of at least five
    /// same-category China units inside the horde radius fights 25% harder and
    /// marches 10% faster; the calculator itself is pure integer permille math
    /// in the GatlingEscalation family — the neighbor counting is the server's
    /// canonical per-tick walk, and both paths must stay deterministic and
    /// allocation-free.
    /// </summary>
    [TestFixture]
    public sealed class HordeBonusCalculatorTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);
        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        // ------------------------------------------------------------------
        // Pure functions
        // ------------------------------------------------------------------

        [Test]
        public void Threshold_CountsTheUnitItself()
        {
            Assert.That(HordeBonusCalculator.IsHorde(SimulationConstants.HordeThreshold - 1), Is.False);
            Assert.That(HordeBonusCalculator.IsHorde(SimulationConstants.HordeThreshold), Is.True,
                "a horde of 5 is the unit plus four allies");
        }

        [Test]
        public void Multipliers_FlipExactlyAtTheThreshold()
        {
            Assert.That(
                HordeBonusCalculator.DamagePermille(SimulationConstants.HordeThreshold - 1),
                Is.EqualTo(1000));
            Assert.That(
                HordeBonusCalculator.DamagePermille(SimulationConstants.HordeThreshold),
                Is.EqualTo(SimulationConstants.HordeDamagePermille));
            Assert.That(
                HordeBonusCalculator.SpeedPermille(SimulationConstants.HordeThreshold - 1),
                Is.EqualTo(1000));
            Assert.That(
                HordeBonusCalculator.SpeedPermille(SimulationConstants.HordeThreshold),
                Is.EqualTo(SimulationConstants.HordeSpeedPermille));
        }

        [Test]
        public void ApplyToSpeed_RoundsHalfUp_AndStaysPositive()
        {
            Assert.That(HordeBonusCalculator.ApplyToSpeed(500, 4), Is.EqualTo(500));
            Assert.That(HordeBonusCalculator.ApplyToSpeed(500, 5), Is.EqualTo(550),
                "500 mm/tick at +10% is 550");
            // 0.9 mm/tick at +10% rounds to 1: StepTowards refuses non-positive steps.
            Assert.That(HordeBonusCalculator.ApplyToSpeed(1, 5), Is.EqualTo(1));
        }

        [Test]
        public void ApplyToSpeed_ThousandReads_AllocatesZeroBytes()
        {
            // Warmup: JIT the calculator.
            for (var index = 0; index < 100; index++)
            {
                HordeBonusCalculator.ApplyToSpeed(500, index % 10);
            }

            TestDelegate measured = () =>
            {
                for (var index = 0; index < 1000; index++)
                {
                    HordeBonusCalculator.ApplyToSpeed(500 + index, index % 10);
                }
            };

            Assert.That(measured, GcAssert.Not.AllocatingGCMemory(),
                "the horde path runs per unit per tick and must be allocation-free");
        }

        // ------------------------------------------------------------------
        // Server integration: damage and movement
        // ------------------------------------------------------------------

        [Test]
        public void ChinaHorde_DealsBonusDamage()
        {
            Assert.That(
                FireOneShot(hordeSize: 5),
                Is.EqualTo(25),
                "20 base damage through the x1.000 (SmallArms x Infantry) cell at the x1.250 horde bonus");
            Assert.That(
                FireOneShot(hordeSize: 4),
                Is.EqualTo(20),
                "four units are not a horde: the matrix cell alone");
        }

        [Test]
        public void ChinaHorde_MarchesFaster()
        {
            Assert.That(StepAfterOneTick(hordeSize: 5), Is.EqualTo(550),
                "the horde's 500 mm/tick march at +10%");
            Assert.That(StepAfterOneTick(hordeSize: 4), Is.EqualTo(500),
                "a four-unit group keeps the base speed");
        }

        /// <summary>
        /// Spawns <paramref name="hordeSize"/> clustered China infantry with
        /// one shooter ordered to fire a single shot, and returns the damage
        /// the enemy actually lost.
        /// </summary>
        private static int FireOneShot(int hordeSize)
        {
            var server = CreateChinaServer();
            var attackers = SpawnHorde(server, hordeSize);
            var enemy = server.SpawnUnit(
                PlayerTwo, new CombatStats(10_000, 1, 7000, 10),
                new WorldPointMm(-5_000, 0), 500, autoAcquire: false, UnitKinds.Infantry);

            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(PlayerOne, 1, 1UL, GameCommandType.Attack),
                    new[] { attackers[0] }, enemy),
                Is.EqualTo(MatchCommandRejection.None));
            server.ExecuteTicks(1);

            Assert.That(server.TryGetUnit(enemy, out var snapshot), Is.True);
            return 10_000 - snapshot.CurrentHealth;
        }

        /// <summary>
        /// Marches the first horde unit one tick toward a far destination and
        /// returns the millimetres it covered.
        /// </summary>
        private static int StepAfterOneTick(int hordeSize)
        {
            var server = CreateChinaServer();
            var attackers = SpawnHorde(server, hordeSize);

            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(PlayerOne, 1, 1UL, GameCommandType.Move),
                    new[] { attackers[0] },
                    new WorldPointMm(100_000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.East)),
                Is.EqualTo(MatchCommandRejection.None));
            server.ExecuteTicks(1);

            Assert.That(server.TryGetUnit(attackers[0], out var snapshot), Is.True);
            return snapshot.Position.X;
        }

        private static MatchServer CreateChinaServer()
        {
            // Player 1 is China; player 2 has no faction. The far-away
            // second player also keeps the victory gate quiet.
            var server = new MatchServer(
                playerFactionsByPlayerId: new byte[] { FactionId.None, FactionId.China });
            server.SpawnUnit(
                PlayerTwo, new CombatStats(100, 1, 0, 1), new WorldPointMm(-150_000, 0), 500);
            return server;
        }

        private static EntityId[] SpawnHorde(MatchServer server, int hordeSize)
        {
            var ids = new EntityId[hordeSize];
            for (var index = 0; index < hordeSize; index++)
            {
                ids[index] = server.SpawnUnitWithEntity(
                    new EntityId(10 + (uint)index),
                    PlayerOne,
                    new CombatStats(100, 20, 7000, 10),
                    new WorldPointMm(index * 1000, 0),
                    500,
                    autoAcquire: false,
                    UnitKinds.Infantry);
            }

            return ids;
        }
    }
}
