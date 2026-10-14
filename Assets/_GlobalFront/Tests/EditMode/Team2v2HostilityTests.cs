using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Witnesses for audit P1-2: hostility must follow the team relation rather
    /// than PlayerId identity, and a match must end once a team is wiped rather
    /// than once a single player is. Every case builds its server with
    /// <see cref="Team2v2CombatRelation"/> explicitly, so the 1v1 skirmish
    /// default stays covered by <see cref="MatchServerTests"/>.
    /// </summary>
    /// <remarks>
    /// Distances are chosen to sit inside the attacker's weapon range, which
    /// keeps <c>MatchServer.MoveUnits</c> from ordering a pursuit march. Positions
    /// therefore never change and each tick schedule below is closed-form rather
    /// than a race.
    /// </remarks>
    public sealed class Team2v2HostilityTests
    {
        private static readonly PlayerId RedOne = new PlayerId(1);
        private static readonly PlayerId RedTwo = new PlayerId(2);
        private static readonly PlayerId BlueOne = new PlayerId(3);

        /// <summary>Red shooter: 25 damage every 2 ticks, first shot on tick 1.</summary>
        private static readonly CombatStats RedStats = new CombatStats(100, 25, 12000, 2);

        /// <summary>Survives every tick these tests run, so it can act as a witness.</summary>
        private static readonly CombatStats DurableStats = new CombatStats(1000, 10, 12000, 2);

        /// <summary>Dies to two Red volleys, which is what makes a team collapse reachable.</summary>
        private static readonly CombatStats FragileStats = new CombatStats(100, 10, 12000, 2);

        [Test]
        public void Match2v2_AlliedUnits_DoNotAutoAcquireEachOther()
        {
            var server = new MatchServer(Team2v2CombatRelation.Instance);

            // The ally at 2000 mm is by far the closest combatant, so a
            // PlayerId-identity gate locks onto it. The only hostile unit sits at
            // 6000 mm.
            var redOne = server.SpawnUnit(
                RedOne, RedStats, new WorldPointMm(0, 0), 500, autoAcquire: true);
            var redTwo = server.SpawnUnit(
                RedTwo, RedStats, new WorldPointMm(2000, 0), 500, autoAcquire: true);
            var blueOne = server.SpawnUnit(
                BlueOne, DurableStats, new WorldPointMm(6000, 0), 500);

            // Read after exactly one tick: auto-acquire runs before combat
            // resolution, so this is the peak acquisition state and no death has
            // had a chance to clear a target yet.
            server.TickOnce();

            Assert.That(server.TryGetUnit(redOne, out var firstShooter), Is.True);
            Assert.That(
                firstShooter.AttackTarget,
                Is.EqualTo(blueOne),
                "the nearest unit acquired its own ally: the hostility gate still " +
                "compares PlayerId instead of routing through ICombatRelation.AreEnemies");

            Assert.That(server.TryGetUnit(redTwo, out var ally), Is.True);
            Assert.That(
                ally.AttackTarget,
                Is.EqualTo(blueOne),
                "the second allied unit acquired its own ally");

            Assert.That(
                ally.CurrentHealth,
                Is.EqualTo(RedStats.MaximumHealth),
                "an ally took fire on the first tick");

            // Sustained witness: cross-team hostility must survive the fix rather
            // than the gate merely disabling shooting, and the ally must stay
            // untouched as the battle continues.
            server.ExecuteTicks(6);

            Assert.That(server.TryGetUnit(redTwo, out var laterAlly), Is.True);
            Assert.That(
                laterAlly.CurrentHealth,
                Is.EqualTo(RedStats.MaximumHealth),
                "ally damage accumulated over the run");

            Assert.That(server.TryGetUnit(blueOne, out var laterBlue), Is.True);
            Assert.That(
                laterBlue.CurrentHealth,
                Is.LessThan(DurableStats.MaximumHealth),
                "no cross-team fire landed: the fix must not disable shooting at real enemies");
        }

        [Test]
        public void Match2v2_WhenOpposingTeamEliminated_TerminatesWithTeamVictory()
        {
            var server = new MatchServer(Team2v2CombatRelation.Instance);

            var redOne = server.SpawnUnit(
                RedOne, RedStats, new WorldPointMm(0, 0), 500, autoAcquire: true);
            server.SpawnUnit(
                RedTwo, RedStats, new WorldPointMm(1000, 0), 500, autoAcquire: true);
            var blueOne = server.SpawnUnit(
                BlueOne, FragileStats, new WorldPointMm(10000, 0), 500);
            var blueTwo = server.SpawnUnit(
                new PlayerId(4), FragileStats, new WorldPointMm(11000, 0), 500);

            // Both shooters prefer the nearer blue unit, so the schedule is:
            // tick 1 -50, tick 3 kills it, tick 4 both re-acquire the survivor,
            // tick 5 -50, tick 7 kills it. Only Team Red is left standing.
            // The margin is free: TickOnce returns early once the outcome is
            // terminal, so extra ticks cannot undo it.
            server.ExecuteTicks(20);

            // Kind is asserted before IsTerminal so a regression on the player-
            // collapse outcome reports the kind it actually produced.
            Assert.That(
                server.Outcome.Kind,
                Is.EqualTo(BattleOutcomeKind.Victory),
                "the match never terminated as a victory: outcome still collapses on " +
                "player survival, so two living allies with distinct PlayerIds hold " +
                "it InProgress forever");
            Assert.That(
                server.Outcome.IsTerminal,
                Is.True,
                "no terminal outcome was latched on team elimination");
            Assert.That(
                server.Outcome.WinningTeam,
                Is.EqualTo(MatchTopology2v2.TeamRed));
            Assert.That(server.Outcome.IsTeamVictory, Is.True);
            Assert.That(server.Outcome.Winner.IsValid, Is.False);

            Assert.That(server.TryGetUnit(redOne, out var survivor), Is.True);
            Assert.That(
                survivor.CurrentHealth,
                Is.EqualTo(RedStats.MaximumHealth),
                "no allied fire reached the surviving team");

            Assert.That(server.TryGetUnit(blueOne, out var firstCasualty), Is.True);
            Assert.That(firstCasualty.CurrentHealth, Is.Zero);
            Assert.That(server.TryGetUnit(blueTwo, out var secondCasualty), Is.True);
            Assert.That(secondCasualty.CurrentHealth, Is.Zero);
        }

        /// <summary>
        /// Since Phase 1.0 sprint 5 the command path validates hostility itself, so
        /// an order to shoot a teammate is refused before it reaches the schedule and
        /// never becomes an attack target at all.
        /// </summary>
        /// <remarks>
        /// Before the gate landed this test was the only scenario that could hand
        /// <c>MatchServer.ClearInvalidAttackTargets</c> and
        /// <c>CombatTickResolver.ClearInvalidTargets</c> an allied target, because the
        /// command path accepted one. Both gates stay as defence in depth, but with
        /// enqueue-time hostility validation and a hostility-gated auto-acquire scan
        /// there is no longer a supported way to reach either one with an ally — the
        /// resolver-side arm is still witnessed directly at
        /// <c>CombatSimulationTests.Resolver_ClearsMissingDeadOrFriendlyTarget</c>.
        /// </remarks>
        [Test]
        public void Match2v2_ManualAttackOrderOnAlly_NeverDealsDamage()
        {
            var server = new MatchServer(Team2v2CombatRelation.Instance);

            // Nothing auto-acquires: the ally target exists only because a player
            // ordered it, so this test answers for the command-path gate.
            var redOne = server.SpawnUnit(
                RedOne, RedStats, new WorldPointMm(0, 0), 500);
            var ally = server.SpawnUnit(
                RedTwo, DurableStats, new WorldPointMm(3000, 0), 500);

            // Kept out of both auto-acquire (18000) and weapon (12000) range so it
            // survives, holds the match in progress, and cannot be the shooter's
            // substitute target.
            server.SpawnUnit(
                BlueOne, DurableStats, new WorldPointMm(40000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(RedOne, 1, 1, GameCommandType.Attack),
                    new[] { redOne },
                    ally),
                Is.EqualTo(MatchCommandRejection.FriendlyTarget),
                "an order to shoot a teammate must be refused by the command path");

            server.ExecuteTicks(4);

            Assert.That(server.TryGetUnit(ally, out var alliedUnit), Is.True);
            Assert.That(
                alliedUnit.CurrentHealth,
                Is.EqualTo(DurableStats.MaximumHealth),
                "an attack order on an ally dealt damage even though the command " +
                "path was supposed to refuse it");

            Assert.That(
                server.Outcome.IsTerminal,
                Is.False,
                "the match must stay in progress while both teams still have units");
        }

        [Test]
        public void Match2v2_AttackOrderOnTeammate_ReturnsFriendlyTargetAndSchedulesNothing()
        {
            var server = new MatchServer(Team2v2CombatRelation.Instance);

            var redOne = server.SpawnUnit(
                RedOne, RedStats, new WorldPointMm(0, 0), 500);
            var teammate = server.SpawnUnit(
                RedTwo, DurableStats, new WorldPointMm(3000, 0), 500);
            var hostile = server.SpawnUnit(
                BlueOne, DurableStats, new WorldPointMm(40000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(RedOne, 1, 1, GameCommandType.Attack),
                    new[] { redOne },
                    teammate),
                Is.EqualTo(MatchCommandRejection.FriendlyTarget));

            server.ExecuteTicks(4);

            Assert.That(server.TryGetUnit(redOne, out var shooter), Is.True);
            Assert.That(
                shooter.AttackTarget,
                Is.EqualTo(default(EntityId)),
                "the refused order still reached the shooter's attack target");
            Assert.That(server.TryGetUnit(teammate, out var ally), Is.True);
            Assert.That(ally.CurrentHealth, Is.EqualTo(DurableStats.MaximumHealth));

            // CommitSequence runs only on the accepted path, so a refused order must
            // leave the player's sequence window free for a valid order.
            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(RedOne, 2, 5, GameCommandType.Attack),
                    new[] { redOne },
                    hostile),
                Is.EqualTo(MatchCommandRejection.None));
        }

        [Test]
        public void Match2v2_AttackOrderOnEnemyTeam_IsStillAccepted()
        {
            var server = new MatchServer(Team2v2CombatRelation.Instance);

            var redOne = server.SpawnUnit(
                RedOne, RedStats, new WorldPointMm(0, 0), 500);
            var blueOne = server.SpawnUnit(
                BlueOne, DurableStats, new WorldPointMm(6000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(RedOne, 1, 1, GameCommandType.Attack),
                    new[] { redOne },
                    blueOne),
                Is.EqualTo(MatchCommandRejection.None),
                "the hostility gate must not widen into refusing legitimate orders");
        }
    }
}
