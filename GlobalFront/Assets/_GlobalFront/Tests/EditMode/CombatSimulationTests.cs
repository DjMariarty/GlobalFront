using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    public sealed class CombatSimulationTests
    {
        [Test]
        public void CombatStats_RejectsInvalidValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CombatStats(0, 10, 1000, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CombatStats(100, 0, 1000, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CombatStats(100, 10, -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CombatStats(100, 10, 1000, 0));
        }

        [Test]
        public void CombatMath_UsesInclusiveThreeFourFiveRangeBoundary()
        {
            var origin = new WorldPointMm(0, 0);
            var target = new WorldPointMm(3000, 4000);

            Assert.That(CombatMath.IsWithinRange(origin, target, 5000), Is.True);
            Assert.That(CombatMath.IsWithinRange(origin, target, 4999), Is.False);
            Assert.That(CombatMath.IsWithinRange(origin, origin, 0), Is.True);
        }

        [Test]
        public void CombatMath_SaturatesExtremeCoordinateDistance()
        {
            var minimum = new WorldPointMm(int.MinValue, int.MinValue);
            var maximum = new WorldPointMm(int.MaxValue, int.MaxValue);

            Assert.That(
                CombatMath.SquaredDistance(minimum, maximum),
                Is.EqualTo(ulong.MaxValue));
            Assert.That(
                CombatMath.IsWithinRange(minimum, maximum, int.MaxValue),
                Is.False);
        }

        [Test]
        public void ApplyDamage_ClampsAtZeroAndReportsDeathOnce()
        {
            var state = CreateState(1, 1, new CombatStats(100, 10, 1000, 1));

            Assert.That(state.ApplyDamage(30), Is.False);
            Assert.That(state.CurrentHealth, Is.EqualTo(70));
            Assert.That(state.ApplyDamage(long.MaxValue), Is.True);
            Assert.That(state.CurrentHealth, Is.Zero);
            Assert.That(state.ApplyDamage(1), Is.False);
            Assert.That(state.CurrentHealth, Is.Zero);
            Assert.That(state.TryAssignTarget(new EntityId(2)), Is.False);
            Assert.That(state.CanFire(ulong.MaxValue), Is.False);
        }

        [Test]
        public void Resolver_EnforcesExactCooldownTicks()
        {
            var stats = new CombatStats(100, 10, 5000, 3);
            var attacker = CreateState(1, 1, stats);
            var target = CreateState(2, 2, stats);
            attacker.TryAssignTarget(target.Entity);
            var inputs = CreatePair(attacker, target, 1000);

            Assert.That(CombatTickResolver.Resolve(inputs, 10).EventCount, Is.EqualTo(1));
            Assert.That(attacker.NextAttackTick, Is.EqualTo(13));
            Assert.That(CombatTickResolver.Resolve(inputs, 12).EventCount, Is.Zero);
            Assert.That(CombatTickResolver.Resolve(inputs, 13).EventCount, Is.EqualTo(1));
            Assert.That(attacker.NextAttackTick, Is.EqualTo(16));
        }

        [Test]
        public void Resolver_DoesNotFireOutsideRange()
        {
            var stats = new CombatStats(100, 10, 5000, 3);
            var attacker = CreateState(1, 1, stats);
            var target = CreateState(2, 2, stats);
            attacker.TryAssignTarget(target.Entity);

            var result = CombatTickResolver.Resolve(
                CreatePair(attacker, target, 5001),
                10);

            Assert.That(result.EventCount, Is.Zero);
            Assert.That(target.CurrentHealth, Is.EqualTo(100));
            Assert.That(attacker.NextAttackTick, Is.Zero);
        }

        [Test]
        public void Resolver_AggregatesFocusedDamageBeforeApplyingIt()
        {
            var attackerStats = new CombatStats(100, 30, 5000, 3);
            var targetStats = new CombatStats(50, 5, 5000, 3);
            var attackerOne = CreateState(1, 1, attackerStats);
            var attackerTwo = CreateState(2, 1, attackerStats);
            var target = CreateState(10, 2, targetStats);
            attackerOne.TryAssignTarget(target.Entity);
            attackerTwo.TryAssignTarget(target.Entity);

            var result = CombatTickResolver.Resolve(
                new[]
                {
                    new CombatantTickInput(target, new WorldPointMm(0, 0)),
                    new CombatantTickInput(attackerTwo, new WorldPointMm(1000, 0)),
                    new CombatantTickInput(attackerOne, new WorldPointMm(-1000, 0))
                },
                20);

            Assert.That(result.EventCount, Is.EqualTo(2));
            Assert.That(result.GetEvent(0).Attacker, Is.EqualTo(new EntityId(1)));
            Assert.That(result.GetEvent(1).Attacker, Is.EqualTo(new EntityId(2)));
            Assert.That(target.CurrentHealth, Is.Zero);
            Assert.That(attackerOne.HasAttackTarget, Is.False);
            Assert.That(attackerTwo.HasAttackTarget, Is.False);
        }

        [Test]
        public void Resolver_AllowsMutualLethalDamageFromSingleSnapshot()
        {
            var stats = new CombatStats(100, 100, 5000, 3);
            var first = CreateState(1, 1, stats);
            var second = CreateState(2, 2, stats);
            first.TryAssignTarget(second.Entity);
            second.TryAssignTarget(first.Entity);

            var result = CombatTickResolver.Resolve(
                CreatePair(first, second, 1000),
                20);

            Assert.That(result.EventCount, Is.EqualTo(2));
            Assert.That(first.IsAlive, Is.False);
            Assert.That(second.IsAlive, Is.False);
            Assert.That(result.Outcome.Kind, Is.EqualTo(BattleOutcomeKind.Draw));
        }

        [Test]
        public void Resolver_ClearsMissingDeadOrFriendlyTarget()
        {
            var stats = new CombatStats(100, 10, 5000, 3);
            var attacker = CreateState(1, 1, stats);
            var friendly = CreateState(2, 1, stats);
            attacker.TryAssignTarget(friendly.Entity);

            Assert.That(
                CombatTickResolver.Resolve(CreatePair(attacker, friendly, 1000), 1)
                    .EventCount,
                Is.Zero);
            Assert.That(attacker.HasAttackTarget, Is.False);

            attacker.TryAssignTarget(new EntityId(99));
            Assert.That(
                CombatTickResolver.Resolve(
                    new[]
                    {
                        new CombatantTickInput(attacker, new WorldPointMm(0, 0)),
                        new CombatantTickInput(friendly, new WorldPointMm(1000, 0))
                    },
                    2).EventCount,
                Is.Zero);
            Assert.That(attacker.HasAttackTarget, Is.False);
        }

        [Test]
        public void Resolver_IsInvariantToInputOrder()
        {
            var stats = new CombatStats(100, 25, 5000, 3);
            var firstA = CreateState(1, 1, stats);
            var secondA = CreateState(2, 2, stats);
            firstA.TryAssignTarget(secondA.Entity);
            secondA.TryAssignTarget(firstA.Entity);
            var forward = CombatTickResolver.Resolve(
                CreatePair(firstA, secondA, 1000),
                8);

            var firstB = CreateState(1, 1, stats);
            var secondB = CreateState(2, 2, stats);
            firstB.TryAssignTarget(secondB.Entity);
            secondB.TryAssignTarget(firstB.Entity);
            var reverse = CombatTickResolver.Resolve(
                new[]
                {
                    new CombatantTickInput(secondB, new WorldPointMm(1000, 0)),
                    new CombatantTickInput(firstB, new WorldPointMm(0, 0))
                },
                8);

            Assert.That(reverse.EventCount, Is.EqualTo(forward.EventCount));
            Assert.That(reverse.GetEvent(0), Is.EqualTo(forward.GetEvent(0)));
            Assert.That(reverse.GetEvent(1), Is.EqualTo(forward.GetEvent(1)));
            Assert.That(firstB.CurrentHealth, Is.EqualTo(firstA.CurrentHealth));
            Assert.That(secondB.CurrentHealth, Is.EqualTo(secondA.CurrentHealth));
        }

        [Test]
        public void Resolver_DeterminesInProgressVictoryAndDraw()
        {
            var stats = new CombatStats(100, 10, 5000, 3);
            var inProgressFirst = CreateState(1, 1, stats);
            var inProgressSecond = CreateState(2, 2, stats);

            Assert.That(
                CombatTickResolver.Resolve(
                        CreatePair(inProgressFirst, inProgressSecond, 1000),
                        1)
                    .Outcome.Kind,
                Is.EqualTo(BattleOutcomeKind.InProgress));

            var winner = CreateState(11, 1, stats);
            var defeated = CreateState(12, 2, stats);
            defeated.ApplyDamage(100);
            var victory = CombatTickResolver.Resolve(
                    CreatePair(winner, defeated, 1000),
                    2)
                .Outcome;
            Assert.That(victory.Kind, Is.EqualTo(BattleOutcomeKind.Victory));
            Assert.That(victory.Winner, Is.EqualTo(new PlayerId(1)));

            var deadFirst = CreateState(21, 1, stats);
            var deadSecond = CreateState(22, 2, stats);
            deadFirst.ApplyDamage(100);
            deadSecond.ApplyDamage(100);
            Assert.That(
                CombatTickResolver.Resolve(CreatePair(deadFirst, deadSecond, 1000), 3)
                    .Outcome.Kind,
                Is.EqualTo(BattleOutcomeKind.Draw));
        }

        private static CombatantState CreateState(
            ulong entity,
            byte owner,
            CombatStats stats) =>
            new CombatantState(new EntityId(entity), new PlayerId(owner), stats);

        private static CombatantTickInput[] CreatePair(
            CombatantState first,
            CombatantState second,
            int distanceMm) =>
            new[]
            {
                new CombatantTickInput(first, new WorldPointMm(0, 0)),
                new CombatantTickInput(second, new WorldPointMm(distanceMm, 0))
            };
    }
}
