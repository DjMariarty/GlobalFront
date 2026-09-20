#pragma warning disable CS0618
using System;
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
    /// Phase 2.4 (ADR-008) tests for the server-authoritative session /
    /// player identity layer: server-assigned PlayerIds, join ordering,
    /// no-reuse, one-session/one-player binding, the session command gate,
    /// match lifecycle, deterministic MatchConfig owner mapping,
    /// reconnect-ready identity semantics, invalid transitions, capacity and
    /// terminal-match rules.
    /// </summary>
    [TestFixture]
    public sealed class SessionManagerTests
    {
        private const int GraceTicks = 10;
        private const int FormationSpacingMm = 3200;

        private static readonly CombatStats TestStats = new CombatStats(
            maximumHealth: 100,
            damage: 25,
            rangeMm: 7000,
            cooldownTicks: 10);

        private SessionManager _manager;
        private MatchServer _server;

        [SetUp]
        public void SetUp()
        {
            _manager = new SessionManager(GraceTicks);
            _server = new MatchServer();
        }

        // ------------------------------------------------------------------
        // Server-assigned PlayerId + join ordering + no reuse + binding
        // ------------------------------------------------------------------

        [Test]
        public void JoinAssignsServerPlayerId_SessionNeverChooses()
        {
            var match = _manager.CreateMatch(2);
            var session = _manager.CreateSession(new ConnectionHandle(1));

            var join = _manager.TryJoinMatch(session, match, out var assigned);

            Assert.That(join, Is.EqualTo(JoinResult.Assigned));
            Assert.That(assigned.Value, Is.EqualTo(1));
            Assert.That(_manager.TryGetSession(session, out var record), Is.True);
            Assert.That(record.Player, Is.EqualTo(assigned));
            Assert.That(record.Match, Is.EqualTo(match));
            Assert.That(record.State, Is.EqualTo(SessionState.Connected));
        }

        [Test]
        public void JoinOrdering_IsMonotonicByJoinOrder()
        {
            var match = _manager.CreateMatch(4);
            JoinNewSession(match, out var first);
            JoinNewSession(match, out var second);
            JoinNewSession(match, out var third);
            JoinNewSession(match, out var fourth);

            Assert.That(first.Value, Is.EqualTo(1));
            Assert.That(second.Value, Is.EqualTo(2));
            Assert.That(third.Value, Is.EqualTo(3));
            Assert.That(fourth.Value, Is.EqualTo(4));
            Assert.That(_manager.TryGetAssignedCount(match, out var count), Is.True);
            Assert.That(count, Is.EqualTo(4));
        }

        [Test]
        public void ClosedSession_PlayerId_IsNeverReused()
        {
            var match = _manager.CreateMatch(4);
            JoinNewSession(match, out var first);
            var leaverSession = JoinNewSession(match, out var leaver);
            Assert.That(_manager.CloseSession(leaverSession), Is.True);

            JoinNewSession(match, out var replacement);

            Assert.That(replacement.Value, Is.EqualTo(3),
                "A retired PlayerId must never be reassigned within the match.");
            Assert.That(replacement, Is.Not.EqualTo(first));
            Assert.That(replacement, Is.Not.EqualTo(leaver));
        }

        [Test]
        public void AbandonedSession_PlayerId_IsNeverReused_AfterGraceExpiry()
        {
            // Grace expiry while the match is still forming: the abandoned
            // PlayerId is retired before any join could ever reuse it.
            var match = _manager.CreateMatch(4);
            JoinNewSession(match, out var first);
            var leaverSession = JoinNewSession(match, out var leaver);

            Assert.That(_manager.NotifyConnectionLost(leaverSession, 5ul), Is.True);
            _manager.OnTickCompleted(5ul + (ulong)GraceTicks + 1ul);
            Assert.That(
                _manager.TryGetPlayerState(match, leaver, out var slotState),
                Is.True);
            Assert.That(slotState, Is.EqualTo(PlayerConnectionState.Abandoned));

            JoinNewSession(match, out var replacement);
            Assert.That(replacement.Value, Is.EqualTo(3),
                "An abandoned PlayerId must never be reassigned within the match.");
            Assert.That(first.Value, Is.EqualTo(1));
        }

        [Test]
        public void OneSession_OnePlayer_BindingIsExclusive()
        {
            var matchA = _manager.CreateMatch(2);
            var matchB = _manager.CreateMatch(2);
            var session = _manager.CreateSession(new ConnectionHandle(1));
            var otherSession = _manager.CreateSession(new ConnectionHandle(2));

            Assert.That(
                _manager.TryJoinMatch(session, matchA, out var assignedA),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                _manager.TryJoinMatch(session, matchB, out _),
                Is.EqualTo(JoinResult.SessionAlreadyBound));
            Assert.That(
                _manager.TryJoinMatch(otherSession, matchA, out var assignedB),
                Is.EqualTo(JoinResult.Assigned));

            Assert.That(assignedA, Is.Not.EqualTo(assignedB),
                "Two sessions in one match must hold distinct PlayerIds.");
        }

        // ------------------------------------------------------------------
        // Session command gate: identity mismatch / disconnected / phase
        // ------------------------------------------------------------------

        [Test]
        public void ValidateCommand_PlayerMismatch_IsRejected()
        {
            var match = StartRunningMatch(out var local, out _, 2);
            var header = MoveHeader(new PlayerId(2), sequence: 1);

            var rejection = _manager.ValidateCommand(local, header);

            Assert.That(rejection, Is.EqualTo(SessionRejection.PlayerMismatch));
        }

        [Test]
        public void ValidateCommand_DisconnectedSession_IsRejected()
        {
            var match = StartRunningMatch(out var local, out _, 2);
            Assert.That(_manager.NotifyConnectionLost(local, 3ul), Is.True);

            var rejection = _manager.ValidateCommand(local, MoveHeader(new PlayerId(1), 1));

            Assert.That(rejection, Is.EqualTo(SessionRejection.SessionNotConnected));
            Assert.That(match.IsValid, Is.True);
        }

        [Test]
        public void ValidateCommand_UnknownOrUnboundSession_IsRejected()
        {
            var match = StartRunningMatch(out _, out _, 2);
            var unknown = new SessionId(Guid.NewGuid());
            var unbound = _manager.CreateSession(new ConnectionHandle(9));

            Assert.That(
                _manager.ValidateCommand(unknown, MoveHeader(new PlayerId(1), 1)),
                Is.EqualTo(SessionRejection.UnknownSession));
            Assert.That(
                _manager.ValidateCommand(unbound, MoveHeader(new PlayerId(1), 1)),
                Is.EqualTo(SessionRejection.NoBoundMatch));
            Assert.That(match.IsValid, Is.True);
        }

        [Test]
        public void ValidateCommand_FormingOrFinishedMatch_IsRejected()
        {
            // Forming: joined but not started.
            var forming = _manager.CreateMatch(2);
            var formingSession = _manager.CreateSession(new ConnectionHandle(1));
            Assert.That(
                _manager.TryJoinMatch(formingSession, forming, out _),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                _manager.ValidateCommand(formingSession, MoveHeader(new PlayerId(1), 1)),
                Is.EqualTo(SessionRejection.MatchNotRunning));

            // Finished.
            var finished = StartRunningMatch(out var finishedSession, out _, 2);
            Assert.That(_manager.NotifyMatchFinished(finished), Is.True);
            Assert.That(
                _manager.ValidateCommand(finishedSession, MoveHeader(new PlayerId(1), 1)),
                Is.EqualTo(SessionRejection.MatchNotRunning));
        }

        [Test]
        public void ValidateCommand_ConnectedSession_IsAccepted()
        {
            StartRunningMatch(out var local, out _, 2);

            var rejection = _manager.ValidateCommand(local, MoveHeader(new PlayerId(1), 1));

            Assert.That(rejection, Is.EqualTo(SessionRejection.None));
        }

        // ------------------------------------------------------------------
        // Match lifecycle
        // ------------------------------------------------------------------

        [Test]
        public void MatchLifecycle_Forming_Running_Finished()
        {
            var match = _manager.CreateMatch(2);
            Assert.That(_manager.TryGetMatchPhase(match, out var phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Forming));

            JoinNewSession(match, out var firstPlayer);
            JoinNewSession(match, out _);
            Assert.That(
                _manager.TryStartMatch(match, BuildTemplate(new PlayerId(1), new PlayerId(2)), _server, out _),
                Is.True);
            Assert.That(_manager.TryGetMatchPhase(match, out phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Running));
            Assert.That(
                _manager.TryGetPlayerState(match, firstPlayer, out var slotState), Is.True);
            Assert.That(slotState, Is.EqualTo(PlayerConnectionState.Connected));

            Assert.That(_manager.NotifyMatchFinished(match), Is.True);
            Assert.That(_manager.TryGetMatchPhase(match, out phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Finished));
            Assert.That(
                _manager.TryGetPlayerState(match, firstPlayer, out slotState), Is.True);
            Assert.That(slotState, Is.EqualTo(PlayerConnectionState.Abandoned));
        }

        [Test]
        public void GraceExpiry_ClosesSessionAndAbandonsSlot()
        {
            var match = StartRunningMatch(out var local, out _, 2);
            Assert.That(_manager.NotifyConnectionLost(local, 7ul), Is.True);

            // Still inside the grace window.
            _manager.OnTickCompleted(7ul + (ulong)GraceTicks);
            Assert.That(_manager.TryGetSession(local, out var record), Is.True);
            Assert.That(record.State, Is.EqualTo(SessionState.Disconnected));

            // First tick fully past the grace window.
            _manager.OnTickCompleted(7ul + (ulong)GraceTicks + 1ul);
            Assert.That(_manager.TryGetSession(local, out record), Is.True);
            Assert.That(record.State, Is.EqualTo(SessionState.Closed));
            Assert.That(
                _manager.TryGetPlayerState(match, record.Player, out var slotState), Is.True);
            Assert.That(slotState, Is.EqualTo(PlayerConnectionState.Abandoned));
        }

        [Test]
        public void OnTickCompleted_ReentrancySafe_WhenListenerModifiesSessions()
        {
            var match = StartRunningMatch(out var session1, out var session2, 2);
            _manager.NotifyConnectionLost(session1, 1ul);
            _manager.NotifyConnectionLost(session2, 1ul);

            var listenerInvoked = false;
            _manager.SessionGraceExpired += expiredSession =>
            {
                listenerInvoked = true;
                _manager.CreateSession(new ConnectionHandle(999));
            };

            Assert.DoesNotThrow(() => _manager.OnTickCompleted(1ul + (ulong)GraceTicks + 1ul));
            Assert.That(listenerInvoked, Is.True);
        }

        // ------------------------------------------------------------------
        // Deterministic MatchConfig owner mapping (OD-6)
        // ------------------------------------------------------------------

        [Test]
        public void StartMatch_MapsTemplateOwners_OntoAssignedPlayers_Deterministically()
        {
            var match = _manager.CreateMatch(2);
            JoinNewSession(match, out _);
            JoinNewSession(match, out _);

            // Template with non-contiguous owners and repeated slots: slot k
            // (k-th distinct owner ascending) receives the k-th joined player.
            var template = BuildTemplate(
                new PlayerId(5), new PlayerId(3), new PlayerId(5));

            Assert.That(
                _manager.TryStartMatch(match, template, _server, out var entityIds),
                Is.True);

            Assert.That(entityIds.Length, Is.EqualTo(3));
            Assert.That(entityIds[0].Value, Is.EqualTo(1ul),
                "EntityIds stay sequential in spec order: mapping must not reorder specs.");
            Assert.That(entityIds[2].Value, Is.EqualTo(3ul));

            var snapshots = _server.GetAllSnapshots();
            Assert.That(snapshots.Length, Is.EqualTo(3));
            Assert.That(snapshots[0].Owner.Value, Is.EqualTo(2),
                "Spec 0 (template owner 5, second slot) maps to the second joined player.");
            Assert.That(snapshots[1].Owner.Value, Is.EqualTo(1),
                "Spec 1 (template owner 3, lowest slot) maps to the first joined player.");
            Assert.That(snapshots[2].Owner.Value, Is.EqualTo(2));
            Assert.That(snapshots[0].Position, Is.EqualTo(new WorldPointMm(0, 0)));
            Assert.That(snapshots[1].Position, Is.EqualTo(new WorldPointMm(10000, 0)));
            Assert.That(snapshots[2].Position, Is.EqualTo(new WorldPointMm(20000, 0)));
        }

        [Test]
        public void StartMatch_RosterMismatch_LeavesMatchForming()
        {
            var match = _manager.CreateMatch(3);
            JoinNewSession(match, out _);

            // Two template slots but only one joined player.
            var template = BuildTemplate(new PlayerId(1), new PlayerId(2));

            Assert.That(
                _manager.TryStartMatch(match, template, _server, out var entityIds),
                Is.False);
            Assert.That(entityIds, Is.Null);
            Assert.That(_server.UnitCount, Is.EqualTo(0));
            Assert.That(_manager.TryGetMatchPhase(match, out var phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Forming));
        }

        [Test]
        public void StartMatch_NoJoinedPlayers_IsRejected()
        {
            var match = _manager.CreateMatch(2);

            Assert.That(
                _manager.TryStartMatch(match, BuildTemplate(), _server, out var entityIds),
                Is.False,
                "A session-managed match requires at least one participant.");
            Assert.That(entityIds, Is.Null);
            Assert.That(_server.UnitCount, Is.EqualTo(0));
            Assert.That(_manager.TryGetMatchPhase(match, out var phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Forming));
        }

        [Test]
        public void StartMatch_EmptyTemplateWithJoinedPlayers_IsRejected()
        {
            var match = _manager.CreateMatch(2);
            JoinNewSession(match, out _);

            Assert.That(
                _manager.TryStartMatch(match, BuildTemplate(), _server, out _),
                Is.False,
                "An empty template cannot cover a non-empty roster.");
            Assert.That(_manager.TryGetMatchPhase(match, out var phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Forming));
        }

        [Test]
        public void StartMatch_NullArguments_Throw()
        {
            var match = _manager.CreateMatch(2);
            JoinNewSession(match, out _);
            var template = BuildTemplate(new PlayerId(1));

            Assert.That(
                () => _manager.TryStartMatch(match, null, _server, out _),
                Throws.ArgumentNullException);
            Assert.That(
                () => _manager.TryStartMatch(match, template, null, out _),
                Throws.ArgumentNullException);
        }

        [Test]
        public void StartMatch_OnFinishedMatch_IsRejected()
        {
            var match = StartRunningMatch(out _, out _, 2);
            Assert.That(_manager.NotifyMatchFinished(match), Is.True);

            Assert.That(
                _manager.TryStartMatch(
                    match,
                    BuildTemplate(new PlayerId(1), new PlayerId(2)),
                    new MatchServer(),
                    out _),
                Is.False);
        }

        [Test]
        public void CreateMatch_AcceptsBoundaryCapacities()
        {
            Assert.That(_manager.CreateMatch(1).IsValid, Is.True);
            Assert.That(
                _manager.CreateMatch(GlobalFront.Core.Simulation.SimulationConstants.MaxPlayers).IsValid,
                Is.True);
        }

        [Test]
        public void FinishedMatch_NeverTransitionsToClosed_InPhase24()
        {
            // Phase 2.4 lifecycle ends at Finished: MatchPhase.Closed is
            // reserved for the future dedicated/server teardown (ADR-008).
            var match = StartRunningMatch(out _, out _, 2);
            Assert.That(_manager.NotifyMatchFinished(match), Is.True);

            for (ulong tick = 1; tick <= 100; tick++)
            {
                _manager.OnTickCompleted(tick);
            }

            Assert.That(_manager.TryGetMatchPhase(match, out var phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Finished));
        }

        // ------------------------------------------------------------------
        // Reconnect-ready identity semantics (OD-4)
        // ------------------------------------------------------------------

        [Test]
        public void Reconnect_WithinGrace_RebindsIdentityAndIssuesReceipt()
        {
            var match = StartRunningMatch(out var local, out _, 2);
            var gateHeader = MoveHeader(new PlayerId(1), sequence: 7);
            Assert.That(
                _server.TryEnqueueMove(
                    gateHeader,
                    new[] { new EntityId(1) },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, FormationSpacingMm, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));
            _server.TickOnce();

            Assert.That(_manager.NotifyConnectionLost(local, 1ul), Is.True);
            var newHandle = new ConnectionHandle(42);

            var result = _manager.TryReconnect(local, newHandle, 1ul + (ulong)GraceTicks, out var receipt);

            Assert.That(result, Is.EqualTo(ReconnectResult.Reconnected));
            Assert.That(receipt.Match, Is.EqualTo(match));
            Assert.That(receipt.Player.Value, Is.EqualTo(1));
            Assert.That(receipt.CurrentTick, Is.EqualTo(_server.CurrentTick));
            Assert.That(receipt.LastAcceptedSequence, Is.EqualTo(7u),
                "The receipt must carry the last accepted sequence so the client can resume numbering.");
            Assert.That(_manager.TryGetSession(local, out var record), Is.True);
            Assert.That(record.State, Is.EqualTo(SessionState.Connected));
            Assert.That(record.Connection, Is.EqualTo(newHandle),
                "Reconnect rebinds the session to the new connection handle.");
        }

        [Test]
        public void Reconnect_AfterGrace_ReturnsGraceExpired_ThenSessionClosed()
        {
            StartRunningMatch(out var local, out _, 2);
            Assert.That(_manager.NotifyConnectionLost(local, 0ul), Is.True);

            var result = _manager.TryReconnect(
                local, new ConnectionHandle(2), (ulong)GraceTicks + 1ul, out _);
            Assert.That(result, Is.EqualTo(ReconnectResult.GraceExpired));

            _manager.OnTickCompleted((ulong)GraceTicks + 1ul);
            Assert.That(
                _manager.TryReconnect(local, new ConnectionHandle(3), (ulong)GraceTicks + 2ul, out _),
                Is.EqualTo(ReconnectResult.SessionClosed));
        }

        [Test]
        public void Reconnect_InvalidStates_AreRejected()
        {
            StartRunningMatch(out var connected, out _, 2);

            Assert.That(
                _manager.TryReconnect(new SessionId(Guid.NewGuid()), new ConnectionHandle(2), 0ul, out _),
                Is.EqualTo(ReconnectResult.UnknownSession));
            Assert.That(
                _manager.TryReconnect(connected, new ConnectionHandle(2), 0ul, out _),
                Is.EqualTo(ReconnectResult.NotDisconnected));
            Assert.That(
                () => _manager.TryReconnect(connected, default, 0ul, out _),
                Throws.ArgumentException);
        }

        // ------------------------------------------------------------------
        // Invalid transitions + capacity + terminal rules
        // ------------------------------------------------------------------

        [Test]
        public void InvalidTransitions_AreRejected()
        {
            var match = _manager.CreateMatch(2);
            var session = _manager.CreateSession(new ConnectionHandle(1));

            Assert.That(() => _manager.CreateMatch(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => _manager.CreateMatch(SimulationConstantsMaxPlayers() + 1),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => _manager.CreateSession(default), Throws.ArgumentException);
            Assert.That(() => new SessionManager(-1), Throws.InstanceOf<ArgumentOutOfRangeException>());

            Assert.That(
                _manager.TryJoinMatch(new SessionId(Guid.NewGuid()), match, out _),
                Is.EqualTo(JoinResult.UnknownSession));
            Assert.That(
                _manager.TryJoinMatch(session, new MatchId(Guid.NewGuid()), out _),
                Is.EqualTo(JoinResult.UnknownMatch));

            Assert.That(
                _manager.TryStartMatch(new MatchId(Guid.NewGuid()), BuildTemplate(new PlayerId(1)), _server, out _),
                Is.False, "Unknown match cannot start.");
            Assert.That(_manager.NotifyMatchFinished(match), Is.False,
                "A forming match cannot finish.");
            Assert.That(_manager.NotifyConnectionLost(session, 0ul), Is.False,
                "An unbound session cannot lose its connection.");
            Assert.That(_manager.CloseSession(new SessionId(Guid.NewGuid())), Is.False);

            Assert.That(
                _manager.TryJoinMatch(session, match, out _), Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                _manager.TryStartMatch(match, BuildTemplate(new PlayerId(1)), _server, out _),
                Is.True);
            Assert.That(
                _manager.TryStartMatch(match, BuildTemplate(new PlayerId(1)), _server, out _),
                Is.False, "A running match cannot start again.");
            Assert.That(_manager.CloseSession(session), Is.True);
            Assert.That(_manager.CloseSession(session), Is.False,
                "A closed session cannot be closed again.");
            Assert.That(
                _manager.TryJoinMatch(session, match, out _),
                Is.EqualTo(JoinResult.SessionClosed));
        }

        [Test]
        public void FullMatch_RejectsFurtherJoins()
        {
            var match = _manager.CreateMatch(2);
            Assert.That(
                _manager.TryJoinMatch(_manager.CreateSession(new ConnectionHandle(1)), match, out _),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                _manager.TryJoinMatch(_manager.CreateSession(new ConnectionHandle(2)), match, out _),
                Is.EqualTo(JoinResult.Assigned));

            var third = _manager.CreateSession(new ConnectionHandle(3));
            Assert.That(
                _manager.TryJoinMatch(third, match, out var notAssigned),
                Is.EqualTo(JoinResult.MatchFull));
            Assert.That(notAssigned.IsValid, Is.False);
        }

        [Test]
        public void FinishedMatch_RejectsJoinsAndCommands()
        {
            var match = StartRunningMatch(out var local, out _, 2);
            Assert.That(_manager.NotifyMatchFinished(match), Is.True);

            var latecomer = _manager.CreateSession(new ConnectionHandle(5));
            Assert.That(
                _manager.TryJoinMatch(latecomer, match, out _),
                Is.EqualTo(JoinResult.MatchNotForming));
            Assert.That(
                _manager.ValidateCommand(local, MoveHeader(new PlayerId(1), 1)),
                Is.EqualTo(SessionRejection.MatchNotRunning));
        }

        // ------------------------------------------------------------------
        // MatchServer additive accessor (OD-8)
        // ------------------------------------------------------------------

        [Test]
        public void TryGetLastAcceptedSequence_ReflectsAcceptedCommandsOnly()
        {
            var server = new MatchServer();
            var player = new PlayerId(1);

            Assert.That(server.TryGetLastAcceptedSequence(player, out _), Is.False);

            server.InitializeMatch(BuildTemplate(player, new PlayerId(2)));
            Assert.That(server.TryGetLastAcceptedSequence(player, out _), Is.False,
                "No command accepted yet.");

            Assert.That(
                server.TryEnqueueMove(
                    MoveHeader(player, sequence: 7),
                    new[] { new EntityId(1) },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, FormationSpacingMm, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(server.TryGetLastAcceptedSequence(player, out var sequence), Is.True);
            Assert.That(sequence, Is.EqualTo(7u));

            // A rejected duplicate must not move the watermark.
            Assert.That(
                server.TryEnqueueMove(
                    MoveHeader(player, sequence: 7),
                    new[] { new EntityId(1) },
                    new WorldPointMm(2000, 0),
                    new FormationSpec(1, FormationSpacingMm, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.DuplicateSequence));
            Assert.That(server.TryGetLastAcceptedSequence(player, out sequence), Is.True);
            Assert.That(sequence, Is.EqualTo(7u));
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private SessionId JoinNewSession(MatchId match, out PlayerId assigned)
        {
            var session = _manager.CreateSession(_manager.CreateConnectionHandle());
            var join = _manager.TryJoinMatch(session, match, out assigned);
            Assert.That(join, Is.EqualTo(JoinResult.Assigned));
            return session;
        }

        private MatchId StartRunningMatch(out SessionId localSession, out SessionId enemySession, int players)
        {
            var match = _manager.CreateMatch(players);
            var owners = new PlayerId[players];
            var sessions = new SessionId[players];
            for (var index = 0; index < players; index++)
            {
                sessions[index] = _manager.CreateSession(_manager.CreateConnectionHandle());
                Assert.That(
                    _manager.TryJoinMatch(sessions[index], match, out owners[index]),
                    Is.EqualTo(JoinResult.Assigned));
            }

            Assert.That(
                _manager.TryStartMatch(match, BuildTemplate(owners), _server, out _),
                Is.True);

            localSession = sessions[0];
            enemySession = players > 1 ? sessions[1] : default;
            return match;
        }

        private static MatchConfig BuildTemplate(params PlayerId[] slotOwners)
        {
            var specs = new UnitSpawnSpec[slotOwners.Length];
            for (var index = 0; index < slotOwners.Length; index++)
            {
                specs[index] = new UnitSpawnSpec(
                    slotOwners[index],
                    new WorldPointMm(index * 10000, 0),
                    350,
                    false);
            }

            return new MatchConfig(TestStats, specs);
        }

        private static CommandHeader MoveHeader(PlayerId player, uint sequence) =>
            new CommandHeader(player, sequence, requestedTick: 1, GameCommandType.Move);

        private static int SimulationConstantsMaxPlayers() =>
            GlobalFront.Core.Simulation.SimulationConstants.MaxPlayers;
    }
}
