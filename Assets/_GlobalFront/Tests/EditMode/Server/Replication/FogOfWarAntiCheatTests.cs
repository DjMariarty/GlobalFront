using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Server.Replication
{
    /// <summary>
    /// Milestone M1 remediation (Block 1): the three fog-of-war leaks the
    /// Phase 4 grand audit found in the authoritative path (P1-03, P1-04,
    /// P2-09). Every case runs the production <see cref="MatchServer"/> and
    /// the production <see cref="FogOfWarReplicationFilter"/>; the masks are
    /// fed the same way the replication emitter feeds them (once per completed
    /// tick), and deaths happen through real combat resolution, never through
    /// a test-only despawn.
    /// </summary>
    [TestFixture]
    public sealed class FogOfWarAntiCheatTests
    {
        private static readonly PlayerId Player1 = new PlayerId(1);
        private static readonly PlayerId Player2 = new PlayerId(2);

        private static readonly CombatStats StandardStats =
            new CombatStats(maximumHealth: 100, damage: 10, rangeMm: 12000, cooldownTicks: 1);

        private static readonly CombatStats ShortRangeStats =
            new CombatStats(maximumHealth: 100, damage: 10, rangeMm: 7000, cooldownTicks: 1);

        private static readonly CombatStats OneShotStats =
            new CombatStats(maximumHealth: 100, damage: 100, rangeMm: 12000, cooldownTicks: 1);

        private static readonly CombatStats ScoutStats =
            new CombatStats(maximumHealth: 10, damage: 1, rangeMm: 5000, cooldownTicks: 1);

        [Test]
        public void Corpse_RealCombatDeath_StopsOpeningFogForItsOwner()
        {
            var filter = new FogOfWarReplicationFilter();
            var server = new MatchServer(SkirmishCombatRelation.Instance, filter);
            var scout = server.SpawnUnit(
                Player1, ScoutStats, new WorldPointMm(0, 0), speedMmPerTick: 1);
            server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(900_000, 0), speedMmPerTick: 1);
            var killer = server.SpawnUnit(
                Player2, OneShotStats, new WorldPointMm(4_000, 0), speedMmPerTick: 1);

            RecordTick(server, filter);
            Assert.That(
                filter.IsVisible(server.CurrentTick, Player1, killer), Is.True,
                "the scout's sight must expose the enemy while the scout is alive");

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player2, 1, server.CurrentTick + 1), new[] { killer }, scout),
                Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();
            RecordTick(server, filter);

            Assert.That(server.TryGetUnit(scout, out var corpse), Is.True,
                "the authoritative roster keeps the destroyed unit");
            Assert.That(corpse.CurrentHealth, Is.Zero, "the hit must really kill the scout");
            Assert.That(
                server.Outcome.IsTerminal, Is.False,
                "the distant reserve keeps the match in progress, so the filter is observed mid-match");

            Assert.That(
                filter.IsVisible(server.CurrentTick, Player1, killer), Is.False,
                "a dead scout must not keep opening the fog around its corpse (P1-03)");
            Assert.That(
                filter.IsVisible(server.CurrentTick, Player1, scout), Is.False,
                "the defined policy is that a destroyed unit leaves the replicated view");

            for (var tick = 0; tick < 5; tick++)
            {
                server.TickOnce();
                RecordTick(server, filter);
                Assert.That(
                    filter.IsVisible(server.CurrentTick, Player1, killer), Is.False,
                    $"tick {server.CurrentTick}: corpse vision must stay gone, not decay over time");
            }
        }

        [Test]
        public void Pursuit_HiddenTarget_FreezesMoveTargetAtLastKnownPosition()
        {
            var filter = new FogOfWarReplicationFilter();
            var server = new MatchServer(SkirmishCombatRelation.Instance, filter);
            var pursuer = server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(0, 0), speedMmPerTick: 500);
            var target = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(19_900, 0), speedMmPerTick: 1000);

            RecordTick(server, filter);
            Assert.That(
                filter.IsVisible(server.CurrentTick, Player1, target), Is.True,
                "the target starts inside the pursuer's sight radius");

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, server.CurrentTick + 1), new[] { pursuer }, target),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                server.TryEnqueueMove(
                    MoveHeader(Player2, 1, server.CurrentTick + 1),
                    new[] { target },
                    new WorldPointMm(500_000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();
            RecordTick(server, filter);

            Assert.That(server.TryGetUnit(pursuer, out var firstStep), Is.True);
            Assert.That(server.TryGetUnit(target, out var fled), Is.True);
            Assert.That(
                firstStep.MoveTarget.X, Is.EqualTo(19_900),
                "the first chase step may name the last position the owner was allowed to see");
            Assert.That(fled.Position.X, Is.EqualTo(20_900));
            Assert.That(
                filter.IsVisible(server.CurrentTick, Player1, target), Is.False,
                "one step past the sight radius, the target must be hidden");

            var frozenX = firstStep.MoveTarget.X;
            for (var tick = 0; tick < 4; tick++)
            {
                server.TickOnce();
                RecordTick(server, filter);

                Assert.That(server.TryGetUnit(pursuer, out var chase), Is.True);
                Assert.That(server.TryGetUnit(target, out var hidden), Is.True);
                Assert.That(
                    filter.IsVisible(server.CurrentTick, Player1, target), Is.False,
                    "the target keeps running out of sight");
                Assert.That(
                    chase.MoveTarget.X, Is.EqualTo(frozenX),
                    $"tick {server.CurrentTick}: the replicated move target must stay frozen, " +
                    "not stream the hidden target's new coordinates (P1-04)");
                Assert.That(
                    chase.MoveTarget.X, Is.LessThan(hidden.Position.X),
                    "the frozen point must never catch up with the hidden target's live position");
            }
        }

        [Test]
        public void VisiblePursuit_StillTracksTheTarget()
        {
            var filter = new FogOfWarReplicationFilter();
            var server = new MatchServer(SkirmishCombatRelation.Instance, filter);
            var pursuer = server.SpawnUnit(
                Player1, ShortRangeStats, new WorldPointMm(0, 0), speedMmPerTick: 50);
            var target = server.SpawnUnit(
                Player2, ShortRangeStats, new WorldPointMm(10_000, 0), speedMmPerTick: 100);

            RecordTick(server, filter);
            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, server.CurrentTick + 1), new[] { pursuer }, target),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                server.TryEnqueueMove(
                    MoveHeader(Player2, 1, server.CurrentTick + 1),
                    new[] { target },
                    new WorldPointMm(500_000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            for (var tick = 0; tick < 3; tick++)
            {
                Assert.That(server.TryGetUnit(target, out var before), Is.True);
                server.TickOnce();
                RecordTick(server, filter);

                Assert.That(server.TryGetUnit(pursuer, out var chase), Is.True);
                Assert.That(
                    filter.IsVisible(server.CurrentTick, Player1, target), Is.True,
                    "the control case keeps the target in sight");
                Assert.That(
                    chase.MoveTarget.X, Is.EqualTo(before.Position.X),
                    "a visible target's pursuit keeps naming its live position");
            }
        }

        [Test]
        public void AttackRejection_HiddenMissingAndDeadTargets_AreIndistinguishable()
        {
            var filter = new FogOfWarReplicationFilter();
            var server = new MatchServer(SkirmishCombatRelation.Instance, filter);
            var commander = server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(900_000, 0), speedMmPerTick: 1);
            var killer = server.SpawnUnit(
                Player1, OneShotStats, new WorldPointMm(0, 0), speedMmPerTick: 1);
            var hiddenAlive = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(400_000, 0), speedMmPerTick: 1);
            var doomed = server.SpawnUnit(
                Player2, ScoutStats, new WorldPointMm(4_000, 0), speedMmPerTick: 1);
            var avenger = server.SpawnUnit(
                Player2, OneShotStats, new WorldPointMm(9_000, 0), speedMmPerTick: 1);

            RecordTick(server, filter);
            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, server.CurrentTick + 1), new[] { killer }, doomed),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player2, 1, server.CurrentTick + 1), new[] { avenger }, killer),
                Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();
            RecordTick(server, filter);

            Assert.That(server.TryGetUnit(doomed, out var deadTarget), Is.True);
            Assert.That(deadTarget.CurrentHealth, Is.Zero, "the hidden target must really die");
            Assert.That(server.TryGetUnit(killer, out var deadKiller), Is.True);
            Assert.That(deadKiller.CurrentHealth, Is.Zero, "the last observer of the target must die too");
            Assert.That(server.Outcome.IsTerminal, Is.False, "both sides keep a survivor");

            Assert.That(filter.IsVisible(server.CurrentTick, Player1, hiddenAlive), Is.False);
            Assert.That(filter.IsVisible(server.CurrentTick, Player1, doomed), Is.False);
            Assert.That(filter.IsVisible(server.CurrentTick, Player1, commander), Is.True,
                "the commander itself is always visible to its owner");

            var missing = new EntityId(999);
            var missingResult = server.TryEnqueueAttack(
                AttackHeader(Player1, 2, server.CurrentTick + 1), new[] { commander }, missing);
            var deadResult = server.TryEnqueueAttack(
                AttackHeader(Player1, 2, server.CurrentTick + 1), new[] { commander }, doomed);
            var aliveResult = server.TryEnqueueAttack(
                AttackHeader(Player1, 2, server.CurrentTick + 1), new[] { commander }, hiddenAlive);

            Assert.That(
                missingResult, Is.EqualTo(MatchCommandRejection.TargetInFogOfWar),
                "a nonexistent id must not be distinguishable from a hidden one (P2-09)");
            Assert.That(
                deadResult, Is.EqualTo(MatchCommandRejection.TargetInFogOfWar),
                "a hidden corpse must not answer EntityNotAlive");
            Assert.That(
                aliveResult, Is.EqualTo(MatchCommandRejection.TargetInFogOfWar));
            Assert.That(
                new[] { missingResult, deadResult, aliveResult }, Is.All.EqualTo(missingResult),
                "every target outside the player's view must produce one identical refusal");

            Assert.That(
                server.TryGetLastAcceptedSequence(Player1, out var lastSequence), Is.True);
            Assert.That(
                lastSequence, Is.EqualTo(1u),
                "refused orders must not consume a sequence number");
        }

        [Test]
        public void Passthrough_WithoutFilter_KeepsTheDetailedRejections()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                Player1, OneShotStats, new WorldPointMm(0, 0), speedMmPerTick: 1);
            var target = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(4_000, 0), speedMmPerTick: 1);
            server.SpawnUnit(Player2, StandardStats, new WorldPointMm(900_000, 0), speedMmPerTick: 1);

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 1), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.None));
            server.TickOnce();

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 2, server.CurrentTick + 1),
                    new[] { attacker },
                    new EntityId(999)),
                Is.EqualTo(MatchCommandRejection.UnknownEntity),
                "without a filter the historical passthrough contract is unchanged");
            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 3, server.CurrentTick + 1), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.EntityNotAlive));
        }

        private static CommandHeader AttackHeader(
            PlayerId player, uint sequence, ulong requestedTick) =>
            new CommandHeader(player, sequence, requestedTick, GameCommandType.Attack);

        private static CommandHeader MoveHeader(
            PlayerId player, uint sequence, ulong requestedTick) =>
            new CommandHeader(player, sequence, requestedTick, GameCommandType.Move);

        /// <summary>
        /// Feeds the mask exactly like the emitter does at a completed tick:
        /// one capture of the authoritative roster per tick.
        /// </summary>
        private static void RecordTick(MatchServer server, FogOfWarReplicationFilter filter)
        {
            var snapshots = new ServerUnitSnapshot[SimulationConstants.MaxUnitsPerMatch];
            var count = server.CopySnapshots(snapshots);
            Assert.That(count, Is.GreaterThanOrEqualTo(0));
            filter.UpdateTick(server.CurrentTick, snapshots, count);
        }
    }
}
