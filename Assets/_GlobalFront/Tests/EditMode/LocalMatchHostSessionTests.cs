using GlobalFront.Client;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Phase 2.4 (ADR-008) host-level integration tests: LocalMatchHost owns
    /// the SessionManager, pumps tick-based grace expiry, transitions the
    /// session-managed match on terminal outcomes, and gates every session
    /// command submission before it reaches the unchanged MatchServer.
    /// </summary>
    [TestFixture]
    public sealed class LocalMatchHostSessionTests
    {
        private const int FormationSpacingMm = 3200;

        private static readonly CombatStats TestStats = new CombatStats(
            maximumHealth: 100,
            damage: 25,
            rangeMm: 7000,
            cooldownTicks: 10);

        private LocalMatchHost _host;

        [SetUp]
        public void SetUp()
        {
            _host = new LocalMatchHost();
        }

        [Test]
        public void GatedSubmission_IdentityMismatch_IsRejectedBeforeMatchValidation()
        {
            StartTwoPlayerMatch(out var local, out _, out _);

            // local is bound to PlayerId 1; submit as PlayerId 2.
            var result = _host.TrySessionMove(
                local,
                new CommandHeader(new PlayerId(2), 1, 1, GameCommandType.Move),
                new[] { new EntityId(1) },
                new WorldPointMm(1000, 0),
                new FormationSpec(1, FormationSpacingMm, CardinalFacing.North));

            Assert.That(result.IsAccepted, Is.False);
            Assert.That(result.Session, Is.EqualTo(SessionRejection.PlayerMismatch));
            Assert.That(_host.Server.TryGetLastAcceptedSequence(new PlayerId(2), out _), Is.False,
                "A mismatched command must never reach MatchServer validation.");
        }

        [Test]
        public void GatedSubmission_DisconnectedSession_IsRejected()
        {
            StartTwoPlayerMatch(out var local, out _, out _);
            Assert.That(_host.NotifyConnectionLost(local, _host.CurrentTick), Is.True);

            var result = _host.TrySessionMove(
                local,
                new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                new[] { new EntityId(1) },
                new WorldPointMm(1000, 0),
                new FormationSpec(1, FormationSpacingMm, CardinalFacing.North));

            Assert.That(result.Session, Is.EqualTo(SessionRejection.SessionNotConnected));
        }

        [Test]
        public void GatedSubmission_ConnectedSession_ReachesMatchServerValidation()
        {
            StartTwoPlayerMatch(out var local, out _, out _);

            var result = _host.TrySessionMove(
                local,
                new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Move),
                new[] { new EntityId(1) },
                new WorldPointMm(1000, 0),
                new FormationSpec(1, FormationSpacingMm, CardinalFacing.North));

            Assert.That(result.IsAccepted, Is.True);
            Assert.That(_host.Server.TryGetLastAcceptedSequence(new PlayerId(1), out var sequence), Is.True);
            Assert.That(sequence, Is.EqualTo(1u));
        }

        [Test]
        public void GatedSubmission_UnknownSession_IsRejected()
        {
            StartTwoPlayerMatch(out _, out _, out _);

            var result = _host.TrySessionStop(
                new SessionId(System.Guid.NewGuid()),
                new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Stop),
                new[] { new EntityId(1) });

            Assert.That(result.Session, Is.EqualTo(SessionRejection.UnknownSession));
        }

        [Test]
        public void SessionLifecycle_ThroughHost_Forming_Running_Finished()
        {
            var match = _host.CreateSessionMatch(2);
            Assert.That(_host.Sessions.TryGetMatchPhase(match, out var phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Forming));
            Assert.That(_host.ActiveSessionMatch.IsValid, Is.False);

            var local = _host.CreateSession(_host.CreateConnectionHandle());
            var enemy = _host.CreateSession(_host.CreateConnectionHandle());
            Assert.That(_host.TryJoinMatch(local, match, out var localPlayer), Is.EqualTo(JoinResult.Assigned));
            Assert.That(_host.TryJoinMatch(enemy, match, out var enemyPlayer), Is.EqualTo(JoinResult.Assigned));

            Assert.That(
                _host.TryStartSessionMatch(match, BuildBattleTemplate(localPlayer, enemyPlayer), out _),
                Is.True);
            Assert.That(_host.ActiveSessionMatch, Is.EqualTo(match));
            Assert.That(_host.Sessions.TryGetMatchPhase(match, out phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Running));

            Assert.That(_host.NotifySessionMatchFinished(match), Is.True);
            Assert.That(_host.Sessions.TryGetMatchPhase(match, out phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Finished));
            Assert.That(
                _host.TrySessionMove(
                    local,
                    new CommandHeader(localPlayer, 1, 1, GameCommandType.Move),
                    new[] { new EntityId(1) },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, FormationSpacingMm, CardinalFacing.North))
                .Session,
                Is.EqualTo(SessionRejection.MatchNotRunning));
        }

        [Test]
        public void TerminalOutcome_TransitionsSessionMatch_ToFinished()
        {
            var match = _host.CreateSessionMatch(2);
            var local = _host.CreateSession(_host.CreateConnectionHandle());
            var enemy = _host.CreateSession(_host.CreateConnectionHandle());
            Assert.That(_host.TryJoinMatch(local, match, out var localPlayer), Is.EqualTo(JoinResult.Assigned));
            Assert.That(_host.TryJoinMatch(enemy, match, out var enemyPlayer), Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                _host.TryStartSessionMatch(
                    match,
                    BuildFightingTemplate(localPlayer, enemyPlayer),
                    out _),
                Is.True);

            // Two auto-acquiring units in range fight to a terminal outcome;
            // the host detects it per tick and retires the session match.
            var ticks = 0;
            while (!_host.Outcome.IsTerminal && ticks < 1000)
            {
                Assert.That(_host.TickOnce(), Is.True);
                ticks++;
            }

            Assert.That(_host.Outcome.IsTerminal, Is.True, "The battle must terminate.");
            Assert.That(_host.Sessions.TryGetMatchPhase(match, out var phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Finished));
            Assert.That(
                _host.TryJoinMatch(_host.CreateSession(_host.CreateConnectionHandle()), match, out _),
                Is.EqualTo(JoinResult.MatchNotForming),
                "A terminal match rejects new joins.");
            Assert.That(
                _host.TrySessionMove(
                    local,
                    new CommandHeader(localPlayer, 99, _host.CurrentTick + 1, GameCommandType.Move),
                    new[] { new EntityId(1) },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, FormationSpacingMm, CardinalFacing.North))
                .Session,
                Is.EqualTo(SessionRejection.MatchNotRunning),
                "A terminal match rejects new commands.");
        }

        [Test]
        public void GraceExpiry_IsDrivenByServerTicks_ThroughTheHost()
        {
            var shortGraceHost = new LocalMatchHost(new MatchServer(), disconnectGraceTicks: 2);
            var match = shortGraceHost.CreateSessionMatch(2);
            var local = shortGraceHost.CreateSession(shortGraceHost.CreateConnectionHandle());
            var enemy = shortGraceHost.CreateSession(shortGraceHost.CreateConnectionHandle());
            Assert.That(shortGraceHost.TryJoinMatch(local, match, out var localPlayer), Is.EqualTo(JoinResult.Assigned));
            Assert.That(shortGraceHost.TryJoinMatch(enemy, match, out var enemyPlayer), Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                shortGraceHost.TryStartSessionMatch(
                    match, BuildBattleTemplate(localPlayer, enemyPlayer), out _),
                Is.True);

            Assert.That(shortGraceHost.NotifyConnectionLost(local, shortGraceHost.CurrentTick), Is.True);

            // Inside the grace window: reconnect works.
            Assert.That(shortGraceHost.TickOnce(), Is.True);
            Assert.That(
                shortGraceHost.TryReconnectSession(
                    local,
                    shortGraceHost.CreateConnectionHandle(),
                    shortGraceHost.CurrentTick,
                    out var receipt),
                Is.EqualTo(ReconnectResult.Reconnected));
            Assert.That(receipt.Player, Is.EqualTo(localPlayer));
            Assert.That(receipt.Match, Is.EqualTo(match));

            // Lose the connection again and run past the grace window.
            var lostAt = shortGraceHost.CurrentTick;
            Assert.That(shortGraceHost.NotifyConnectionLost(local, lostAt), Is.True);
            for (var index = 0; index < 3; index++)
            {
                Assert.That(shortGraceHost.TickOnce(), Is.True);
            }

            Assert.That(shortGraceHost.TryGetSession(local, out var record), Is.True);
            Assert.That(record.State, Is.EqualTo(SessionState.Closed),
                "Grace expiry must be driven by completed server ticks.");
            Assert.That(
                shortGraceHost.Sessions.TryGetPlayerState(match, localPlayer, out var slotState),
                Is.True);
            Assert.That(slotState, Is.EqualTo(PlayerConnectionState.Abandoned));
        }

        [Test]
        public void ReconnectReceipt_LetsTheClientResumeSequenceNumbering()
        {
            StartTwoPlayerMatch(out var local, out _, out var localPlayer);

            // Accept sequence 4 before the drop.
            Assert.That(
                _host.TrySessionMove(
                    local,
                    new CommandHeader(localPlayer, 4, 1, GameCommandType.Move),
                    new[] { new EntityId(1) },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, FormationSpacingMm, CardinalFacing.North))
                .IsAccepted, Is.True);

            var lostAt = _host.CurrentTick;
            Assert.That(_host.NotifyConnectionLost(local, lostAt), Is.True);
            Assert.That(
                _host.TryReconnectSession(local, _host.CreateConnectionHandle(), lostAt, out var receipt),
                Is.EqualTo(ReconnectResult.Reconnected));
            Assert.That(receipt.LastAcceptedSequence, Is.EqualTo(4u));

            // Replayed/old sequences stay rejected by the unchanged MatchServer.
            var replayed = _host.TrySessionMove(
                local,
                new CommandHeader(localPlayer, receipt.LastAcceptedSequence, _host.CurrentTick + 1, GameCommandType.Move),
                new[] { new EntityId(1) },
                new WorldPointMm(2000, 0),
                new FormationSpec(1, FormationSpacingMm, CardinalFacing.North));
            Assert.That(replayed.Session, Is.EqualTo(SessionRejection.None));
            Assert.That(replayed.Command, Is.EqualTo(MatchCommandRejection.DuplicateSequence));

            // Strictly above the watermark: accepted.
            var resumed = _host.TrySessionMove(
                local,
                new CommandHeader(localPlayer, receipt.LastAcceptedSequence + 1, _host.CurrentTick + 1, GameCommandType.Move),
                new[] { new EntityId(1) },
                new WorldPointMm(2000, 0),
                new FormationSpec(1, FormationSpacingMm, CardinalFacing.North));
            Assert.That(resumed.IsAccepted, Is.True);
        }

        [Test]
        public void DeterministicOwnerMapping_ThroughTheHost()
        {
            var match = _host.CreateSessionMatch(2);
            var first = _host.CreateSession(_host.CreateConnectionHandle());
            var second = _host.CreateSession(_host.CreateConnectionHandle());
            Assert.That(_host.TryJoinMatch(first, match, out var firstPlayer), Is.EqualTo(JoinResult.Assigned));
            Assert.That(_host.TryJoinMatch(second, match, out var secondPlayer), Is.EqualTo(JoinResult.Assigned));

            // Template owners intentionally differ from the assigned PlayerIds.
            var template = new MatchConfig(
                TestStats,
                new[]
                {
                    new UnitSpawnSpec(new PlayerId(7), new WorldPointMm(0, 0), 350, false),
                    new UnitSpawnSpec(new PlayerId(9), new WorldPointMm(10000, 0), 350, false)
                });

            Assert.That(_host.TryStartSessionMatch(match, template, out var entityIds), Is.True);

            var snapshots = _host.GetAllSnapshots();
            Assert.That(entityIds.Length, Is.EqualTo(2));
            Assert.That(snapshots[0].Owner, Is.EqualTo(firstPlayer),
                "The lowest template slot must map to the first joined player.");
            Assert.That(snapshots[1].Owner, Is.EqualTo(secondPlayer));
            Assert.That(firstPlayer.Value, Is.EqualTo(1));
            Assert.That(secondPlayer.Value, Is.EqualTo(2));
        }

        [Test]
        public void LocalCommandChannel_RequiresValidSession_AndSurfacesGateRejections()
        {
            Assert.That(() => new LocalCommandChannel(null, new SessionId(System.Guid.NewGuid())),
                Throws.ArgumentNullException);
            Assert.That(() => new LocalCommandChannel(_host, default),
                Throws.ArgumentException);

            StartTwoPlayerMatch(out var local, out var enemy, out var localPlayer);
            var localChannel = new LocalCommandChannel(_host, local);
            var enemyChannel = new LocalCommandChannel(_host, enemy);

            Assert.That(localChannel.Session, Is.EqualTo(local));
            Assert.That(
                localChannel.TrySubmitMove(
                    new CommandHeader(localPlayer, 1, 1, GameCommandType.Move),
                    new[] { new EntityId(1) },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, FormationSpacingMm, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            // Submitting through the enemy channel with the local player's
            // header is an identity mismatch: the gate rejects it and the
            // Phase 2.2 channel contract surfaces SessionRejected.
            Assert.That(
                enemyChannel.TrySubmitMove(
                    new CommandHeader(localPlayer, 1, 1, GameCommandType.Move),
                    new[] { new EntityId(1) },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, FormationSpacingMm, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.SessionRejected));
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void StartTwoPlayerMatch(out SessionId local, out SessionId enemy, out PlayerId localPlayer)
        {
            var match = _host.CreateSessionMatch(2);
            local = _host.CreateSession(_host.CreateConnectionHandle());
            enemy = _host.CreateSession(_host.CreateConnectionHandle());
            Assert.That(_host.TryJoinMatch(local, match, out localPlayer), Is.EqualTo(JoinResult.Assigned));
            Assert.That(_host.TryJoinMatch(enemy, match, out var enemyPlayer), Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                _host.TryStartSessionMatch(match, BuildBattleTemplate(localPlayer, enemyPlayer), out _),
                Is.True);
        }

        /// <summary>Two units far apart: no combat, match stays in progress.</summary>
        private static MatchConfig BuildBattleTemplate(params PlayerId[] owners)
        {
            var specs = new UnitSpawnSpec[owners.Length];
            for (var index = 0; index < owners.Length; index++)
            {
                specs[index] = new UnitSpawnSpec(
                    owners[index],
                    new WorldPointMm(index * 100000, 0),
                    350,
                    false);
            }

            return new MatchConfig(TestStats, specs);
        }

        /// <summary>Two auto-acquiring units in range: they fight to a terminal outcome.</summary>
        private static MatchConfig BuildFightingTemplate(PlayerId first, PlayerId second) =>
            new MatchConfig(
                TestStats,
                new[]
                {
                    new UnitSpawnSpec(first, new WorldPointMm(0, 0), 350, true),
                    new UnitSpawnSpec(second, new WorldPointMm(5000, 0), 350, true)
                });
    }
}
