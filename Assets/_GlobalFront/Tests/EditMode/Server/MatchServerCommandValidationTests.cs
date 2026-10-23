#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Visibility;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Server
{
    /// <summary>
    /// Step 4.4.1 (ADR-014, OD-41): the authoritative command path refuses an
    /// attack order whose target is hidden from the commanding player, and
    /// leaves the schedule and the sequence state untouched when it does.
    /// Also pins the surrounding ownership/alive/friendly gates so a refusal
    /// for the wrong reason cannot mask the fog gate.
    /// </summary>
    [TestFixture]
    public sealed class MatchServerCommandValidationTests
    {
        private static readonly PlayerId Player1 = new PlayerId(1);
        private static readonly PlayerId Player2 = new PlayerId(2);

        private static readonly CombatStats StandardStats =
            new CombatStats(100, 10, 12000, 2);

        private static readonly CombatStats OneShotStats =
            new CombatStats(100, 100, 12000, 1);

        [Test]
        public void TryEnqueueAttack_TargetInFogOfWar_ReturnsTargetInFogOfWar()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(30000, 0), 500);

            var filter = new TestVisibilityFilter();
            filter.Hide(target);
            server.SetVisibilityFilter(filter);

            var rejection = server.TryEnqueueAttack(
                AttackHeader(Player1, 1, 1), new[] { attacker }, target);

            Assert.That(
                rejection,
                Is.EqualTo(MatchCommandRejection.TargetInFogOfWar));
            Assert.That(
                filter.QueryCount, Is.EqualTo(1), "the gate must consult the installed view");
            Assert.That(filter.LastQueryTick, Is.EqualTo(server.CurrentTick));
            Assert.That(filter.LastQueryPlayer, Is.EqualTo(Player1));
            Assert.That(filter.LastQueryEntity, Is.EqualTo(target));

            server.TickOnce();
            Assert.That(server.TryGetUnit(attacker, out var snapshot), Is.True);
            Assert.That(
                snapshot.AttackTarget.IsValid, Is.False,
                "a refused order must not enter the schedule");

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 2), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.TargetInFogOfWar),
                "reaching the fog gate again proves the refused command did not consume its sequence");
        }

        [Test]
        public void TryEnqueueAttack_TargetVisible_ReturnsNone()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(30000, 0), 500);

            var filter = new TestVisibilityFilter();
            server.SetVisibilityFilter(filter);

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 1), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(filter.QueryCount, Is.EqualTo(1));

            server.TickOnce();
            Assert.That(server.TryGetUnit(attacker, out var snapshot), Is.True);
            Assert.That(
                snapshot.AttackTarget, Is.EqualTo(target),
                "an accepted order must be scheduled and executed");

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 2), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.DuplicateSequence),
                "an accepted order commits its sequence");
        }

        [Test]
        public void TryEnqueueAttack_FilterDisabled_ReturnsNone_Passthrough()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(30000, 0), 500);

            var filter = new FogOfWarReplicationFilter(enabled: false);
            server.SetVisibilityFilter(filter);

            Assert.That(filter.IsEnabled, Is.False);
            Assert.That(
                filter.IsVisible(server.CurrentTick, Player1, target), Is.False,
                "a disabled view reports nothing visible; only the flag can let the order through");

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 1), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.None));
        }

        [Test]
        public void TryEnqueueAttack_FilterNull_ReturnsNone_Passthrough()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(30000, 0), 500);

            Assert.That(server.VisibilityFilter, Is.Null);

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 1), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.None));
        }

        [Test]
        public void TryEnqueueAttack_AttackerNotOwnedByPlayer_ReturnsNotEntityOwner()
        {
            var server = new MatchServer();
            var foreign = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(5000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 1), new[] { foreign }, target),
                Is.EqualTo(MatchCommandRejection.NotEntityOwner));
        }

        [Test]
        public void TryEnqueueAttack_AttackerDead_ReturnsEntityNotAlive()
        {
            var server = new MatchServer();
            var doomed = server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(0, 0), 500);
            server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(1_000_000, 0), 500);
            var killer = server.SpawnUnit(
                Player2, OneShotStats, new WorldPointMm(5000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player2, 1, 1), new[] { killer }, doomed),
                Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();

            Assert.That(server.TryGetUnit(doomed, out var dead), Is.True);
            Assert.That(dead.CurrentHealth, Is.Zero, "the killer must actually kill the attacker");
            Assert.That(
                server.Outcome.IsTerminal, Is.False,
                "the surviving unit keeps the match in progress, so the gate can be observed");

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 2), new[] { doomed }, killer),
                Is.EqualTo(MatchCommandRejection.EntityNotAlive));
        }

        [Test]
        public void TryEnqueueAttack_TargetDead_ReturnsEntityNotAlive()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                Player1, OneShotStats, new WorldPointMm(0, 0), 500);
            var target = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(5000, 0), 500);
            server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(1_000_000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 1), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.None));

            server.TickOnce();

            Assert.That(server.TryGetUnit(target, out var dead), Is.True);
            Assert.That(dead.CurrentHealth, Is.Zero, "the attacker must actually kill the target");
            Assert.That(server.Outcome.IsTerminal, Is.False);

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 2, 2), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.EntityNotAlive));
        }

        [Test]
        public void TryEnqueueAttack_TargetFriendly_ReturnsFriendlyTarget()
        {
            var server = new MatchServer();
            var attacker = server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(0, 0), 500);
            var ally = server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(3000, 0), 500);
            var enemy = server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(6000, 0), 500);

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 1), new[] { attacker }, ally),
                Is.EqualTo(MatchCommandRejection.FriendlyTarget));

            Assert.That(
                server.TryEnqueueAttack(
                    AttackHeader(Player1, 2, 2), new[] { attacker }, enemy),
                Is.EqualTo(MatchCommandRejection.None),
                "the gate must not refuse a genuine enemy");
        }

        [Test]
        public void ServerHost_AttachReplication_PropagatesFilterToMatchServer()
        {
            var host = new ServerHost();
            var filter = new TestVisibilityFilter();
            var emitter = host.AttachReplication(
                new NullReplicationTransport(),
                ServerReplicationEmitterConfig.Default,
                filter);

            Assert.That(emitter.Filter, Is.SameAs(filter));
            Assert.That(host.VisibilityFilter, Is.SameAs(filter));
            Assert.That(host.Server.VisibilityFilter, Is.SameAs(filter));

            var attacker = host.Server.SpawnUnit(
                Player1, StandardStats, new WorldPointMm(0, 0), 500);
            var target = host.Server.SpawnUnit(
                Player2, StandardStats, new WorldPointMm(30000, 0), 500);
            filter.Hide(target);

            Assert.That(
                host.TryEnqueueAttack(
                    AttackHeader(Player1, 1, 1), new[] { attacker }, target),
                Is.EqualTo(MatchCommandRejection.TargetInFogOfWar),
                "the host command path must consult the attached view");
        }

        private static CommandHeader AttackHeader(
            PlayerId player, uint sequence, ulong requestedTick) =>
            new CommandHeader(player, sequence, requestedTick, GameCommandType.Attack);

        /// <summary>
        /// Minimal view: every entity is visible unless explicitly hidden.
        /// Records the queries so the gate's inputs can be witnessed.
        /// </summary>
        private sealed class TestVisibilityFilter : IReplicationFilter
        {
            private readonly HashSet<ulong> _hidden = new HashSet<ulong>();

            public bool IsEnabled { get; set; } = true;

            public int QueryCount { get; private set; }

            public ulong LastQueryTick { get; private set; }

            public PlayerId LastQueryPlayer { get; private set; }

            public EntityId LastQueryEntity { get; private set; }

            public void Hide(EntityId entity) => _hidden.Add(entity.Value);

            public void UpdateTick(
                ulong tick, ReadOnlySpan<ServerUnitSnapshot> world, int unitCount)
            {
            }

            public bool TryGetVisibilityMask(
                ulong tick, PlayerId player, out BitMask1024 mask)
            {
                mask = default;
                return false;
            }

            public bool IsVisible(ulong tick, PlayerId player, EntityId entity)
            {
                QueryCount++;
                LastQueryTick = tick;
                LastQueryPlayer = player;
                LastQueryEntity = entity;
                return !_hidden.Contains(entity.Value);
            }
        }

        /// <summary>Accepts and drops every packet; only the attach seam matters here.</summary>
        private sealed class NullReplicationTransport : IReplicationTransport
        {
            public bool SendToSession(
                SessionId session, ulong envelopeTick, byte[] buffer, int length) => true;

            public event Action<SessionId, MatchId, PlayerId> SessionAttached;

            public event Action<SessionId, MatchId, PlayerId> SessionReattached;

            public event Action<SessionId> PostSessionReattached;

            public event Action<SessionId, TransportDisconnectReason> SessionDetached;

            public event Action<ReplicationFeedbackMessage> FeedbackReceived;
        }
    }
}
