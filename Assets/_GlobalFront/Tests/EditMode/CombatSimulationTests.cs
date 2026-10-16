using System;
using System.Reflection;
using System.Threading;
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

        /// <summary>
        /// Witness for Phase 1.0 sprint 5: Resolve clears the roster scratch on
        /// entry, so a relation implementation that calls back into the same
        /// resolver would silently hand the outer tick a half-cleared roster. The
        /// guard turns that corruption into a throw, and the finally releases the
        /// instance for the next legitimate call.
        /// </summary>
        [Test]
        public void Resolver_WhenRelationReentersResolve_ThrowsAndStaysUsable()
        {
            var stats = new CombatStats(100, 10, 5000, 3);
            var attacker = CreateState(1, 1, stats);
            var target = CreateState(2, 2, stats);
            attacker.TryAssignTarget(target.Entity);
            var pair = CreatePair(attacker, target, 1000);

            var resolver = new CombatTickResolver();
            var error = Assert.Throws<InvalidOperationException>(() =>
                resolver.Resolve(pair, pair.Length, 1, new ReentrantRelation(resolver, pair)));

            Assert.That(error.Message, Does.Contain("not reentrant"));
            Assert.That(
                target.CurrentHealth,
                Is.EqualTo(100),
                "the aborted resolve must not have applied any damage");

            Assert.That(
                resolver.Resolve(pair, pair.Length, 10).EventCount,
                Is.EqualTo(1),
                "a throwing resolve must release the guard, not lock the instance");
            Assert.That(target.CurrentHealth, Is.EqualTo(90));
        }

        /// <summary>
        /// Witness for Phase 1.0 sprint 5.1: the single-threaded test above proves a guard
        /// exists, but nothing proves it holds when two threads share one resolver — which is
        /// the hazard the field comment claimed since sprint 5. Here the owner parks inside
        /// the guarded region (DetermineOutcome runs after the claim is taken and inside the
        /// try), and only once it has signalled that it is there does a second thread try the
        /// same instance. The contender must be refused while the owner holds it, and the
        /// owner's roster must come back uncorrupted.
        ///
        /// What this test does NOT prove: that the claim is atomic rather than a bool
        /// check-then-set. The window between reading and writing a non-atomic flag sits
        /// before any relation callback, so a thread parked inside the guard has already set
        /// it and the contender is refused either way. Reaching that window would need an
        /// injection seam inside Resolve, so the field-type pin below is what witnesses the
        /// Interlocked form.
        ///
        /// Every wait is bounded and the release gate is set from three places, so no
        /// interleaving — including a guard that never engages — can wedge a batchmode run.
        /// </summary>
        [Test]
        public void Resolver_IsNotReentrantAcrossConcurrentThreadsOnOneInstance()
        {
            var claim = typeof(CombatTickResolver).GetField(
                "_isResolving", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(
                claim,
                Is.Not.Null,
                "CombatTickResolver renamed or removed its reentrancy claim");
            Assert.That(
                claim.FieldType,
                Is.EqualTo(typeof(int)),
                "the reentrancy claim is no longer an int, so it cannot be taken with " +
                "Interlocked.CompareExchange and check-then-set races again");

            var hold = TimeSpan.FromSeconds(10);
            var join = TimeSpan.FromSeconds(30);

            // Owner roster: exactly one shot, so a clean resolve reports one event.
            var stats = new CombatStats(100, 10, 5000, 3);
            var attacker = CreateState(1, 1, stats);
            var target = CreateState(2, 2, stats);
            attacker.TryAssignTarget(target.Entity);
            var ownerRoster = CreatePair(attacker, target, 1000);

            // Contender roster: disjoint entities and out of range, so it never fires. A
            // different roster keeps a bypassed guard visible as a corrupted owner result
            // instead of two threads resolving identical data and hiding it.
            var contenderRoster = CreatePair(CreateState(3, 1, stats), CreateState(4, 2, stats), 5001);

            var resolver = new CombatTickResolver();
            var insideGuard = new ManualResetEventSlim(false);
            var releaseGuard = new ManualResetEventSlim(false);
            var errors = new Exception[2];
            var ownerEventCount = -1;

            var owner = new Thread(() =>
            {
                try
                {
                    ownerEventCount = resolver.Resolve(
                        ownerRoster,
                        ownerRoster.Length,
                        10,
                        new ParkingRelation(insideGuard, releaseGuard, hold)).EventCount;
                }
                catch (Exception error)
                {
                    // Swallowed into a slot: an exception escaping a worker thread becomes
                    // Unity's "Unhandled log message: [Exception]" and buries the real assert.
                    errors[0] = error;
                }
                finally
                {
                    releaseGuard.Set();
                }
            })
            { IsBackground = true };

            owner.Start();
            try
            {
                Assert.That(
                    insideGuard.Wait(hold),
                    Is.True,
                    "the test premise is wrong: the owner never reached the guarded region");

                var contender = new Thread(() =>
                {
                    try
                    {
                        resolver.Resolve(contenderRoster, contenderRoster.Length, 10);
                    }
                    catch (Exception error)
                    {
                        errors[1] = error;
                    }
                    finally
                    {
                        // Whether it was refused at the guard or (without one) ran to the
                        // end, the parked owner has to be let go.
                        releaseGuard.Set();
                    }
                })
                { IsBackground = true };

                contender.Start();

                Assert.That(
                    contender.Join(join),
                    Is.True,
                    "the contending resolve wedged instead of being refused");
                Assert.That(
                    owner.Join(join),
                    Is.True,
                    "the owner never finished after the guard was released");

                Assert.That(
                    errors[0],
                    Is.Null,
                    "the owner's resolve threw once it was released, so a second thread " +
                    "entered the same instance and corrupted its scratch");
                Assert.That(
                    ownerEventCount,
                    Is.EqualTo(1),
                    "the owner came back with a corrupted roster, so two threads resolved " +
                    "the same instance at once");
                Assert.That(
                    errors[1],
                    Is.InstanceOf<InvalidOperationException>(),
                    "a concurrent resolve on a held instance was accepted, so the guard does " +
                    "not hold across threads");
                Assert.That(
                    errors[1].Message,
                    Does.Contain("not reentrant"),
                    "the cross-thread refusal must carry the same reason as the reentrant one");

                // The refusal must not have consumed the claim: the instance is still usable.
                Assert.That(
                    resolver.Resolve(ownerRoster, ownerRoster.Length, 20).EventCount,
                    Is.EqualTo(1),
                    "a refused contender left the instance latched");
            }
            finally
            {
                // If a premise assert threw before the contender ever started, the owner is
                // still parked — release it so the suite can never hang on a failed test.
                releaseGuard.Set();
            }
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

        /// <summary>
        /// A relation that calls back into the resolver it was handed, standing in
        /// for the reentrancy the sprint 5 guard exists to refuse.
        /// </summary>
        private sealed class ReentrantRelation : ICombatRelation
        {
            private readonly CombatTickResolver _resolver;
            private readonly CombatantTickInput[] _roster;

            public ReentrantRelation(CombatTickResolver resolver, CombatantTickInput[] roster)
            {
                _resolver = resolver;
                _roster = roster;
            }

            public bool AreEnemies(PlayerId left, PlayerId right)
            {
                _resolver.Resolve(_roster, _roster.Length, 1);
                return true;
            }

            public BattleOutcome DetermineOutcome(
                CombatantTickInput[] canonical,
                int count) =>
                BattleOutcome.InProgress;
        }

        /// <summary>
        /// Parks the calling thread inside the resolver's guarded region. DetermineOutcome is
        /// called once the claim is held and inside the try, so a thread blocked here is
        /// provably the one holding the instance. The wait is bounded, so even a guard that
        /// never releases can only fail an assert, never hang the suite.
        /// </summary>
        private sealed class ParkingRelation : ICombatRelation
        {
            private readonly ManualResetEventSlim _inside;
            private readonly ManualResetEventSlim _release;
            private readonly TimeSpan _hold;

            public ParkingRelation(
                ManualResetEventSlim inside,
                ManualResetEventSlim release,
                TimeSpan hold)
            {
                _inside = inside;
                _release = release;
                _hold = hold;
            }

            public bool AreEnemies(PlayerId left, PlayerId right) => left != right;

            public BattleOutcome DetermineOutcome(
                CombatantTickInput[] canonical,
                int count)
            {
                _inside.Set();
                _release.Wait(_hold);
                return BattleOutcome.InProgress;
            }
        }
    }
}
