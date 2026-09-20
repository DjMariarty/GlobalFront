using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using NUnit.Framework;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.EditMode.Network
{
    [TestFixture]
    public sealed class Match2v2SessionTests
    {
        private const int FormationSpacingMm = 3200;

        private static readonly CombatStats TestStats = new CombatStats(
            maximumHealth: 100,
            damage: 25,
            rangeMm: 7000,
            cooldownTicks: 10);

        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _createdObjects.Count; index++)
            {
                Object.DestroyImmediate(_createdObjects[index]);
            }
            _createdObjects.Clear();
        }

        [Test]
        public void GraceExpired_WhenNoOtherDisconnectedPlayers_AutomaticallyResumesSimulation()
        {
            // 1. Host with short grace (5 ticks, P2-1)
            var host = new LocalMatchHost(disconnectGraceTicks: 5);
            var match = host.CreateSessionMatch(2);
            var sessionA = host.CreateSession(host.CreateConnectionHandle());
            var sessionB = host.CreateSession(host.CreateConnectionHandle());

            Assert.That(host.TryJoinMatch(sessionA, match, out var playerA), Is.EqualTo(JoinResult.Assigned));
            Assert.That(host.TryJoinMatch(sessionB, match, out var playerB), Is.EqualTo(JoinResult.Assigned));

            var template = BuildTwoPlayerTemplate(playerA, playerB);
            Assert.That(host.TryStartSessionMatch(match, template, out _), Is.True);
            Assert.That(host.IsPaused, Is.False);

            // 2. Player A disconnects -> host automatically auto-pauses
            Assert.That(host.NotifyConnectionLost(sessionA, host.CurrentTick), Is.True);
            Assert.That(host.IsPaused, Is.True, "Host must auto-pause on disconnect");

            // 3. Advance ticks inside grace window (5 ticks): still paused
            host.AdvancePauseTicks(5);
            Assert.That(host.IsPaused, Is.True, "Simulation must remain paused while in grace window");

            // 4. Advance 1 more tick past grace window (tick 6 > 5): grace expires -> Abandoned -> Auto-Resume
            host.AdvancePauseTicks(1);

            Assert.That(host.IsPaused, Is.False, "Simulation must automatically resume when abandoned player has no remaining peers in grace");
            Assert.That(host.Sessions.TryGetPlayerState(match, playerA, out var slotState), Is.True);
            Assert.That(slotState, Is.EqualTo(PlayerConnectionState.Abandoned));
            Assert.That(host.TickOnce(), Is.True, "Simulation must resume ticking forward");
        }

        [Test]
        public void GraceExpired_WhenAnotherPlayerStillInGrace_RemainsPaused()
        {
            var host = new LocalMatchHost(disconnectGraceTicks: 10);
            var match = host.CreateSessionMatch(4);
            var s0 = host.CreateSession(host.CreateConnectionHandle());
            var s1 = host.CreateSession(host.CreateConnectionHandle());
            var s2 = host.CreateSession(host.CreateConnectionHandle());
            var s3 = host.CreateSession(host.CreateConnectionHandle());

            Assert.That(host.TryJoinMatch(s0, match, out var p0), Is.EqualTo(JoinResult.Assigned));
            Assert.That(host.TryJoinMatch(s1, match, out var p1), Is.EqualTo(JoinResult.Assigned));
            Assert.That(host.TryJoinMatch(s2, match, out var p2), Is.EqualTo(JoinResult.Assigned));
            Assert.That(host.TryJoinMatch(s3, match, out var p3), Is.EqualTo(JoinResult.Assigned));

            var template = BuildFourPlayerTemplate(p0, p1, p2, p3);
            Assert.That(host.TryStartSessionMatch(match, template, out _), Is.True);
            Assert.That(host.IsPaused, Is.False);

            // Player 0 disconnects at tick 0
            Assert.That(host.NotifyConnectionLost(s0, 0), Is.True);
            Assert.That(host.IsPaused, Is.True);

            // Advance 5 ticks
            host.AdvancePauseTicks(5);

            // Player 1 disconnects at pause tick 5
            Assert.That(host.NotifyConnectionLost(s1, 5), Is.True);

            // Advance 6 ticks (current tick = 11 > 0 + 10 for Player 0, but 11 <= 5 + 10 for Player 1)
            host.AdvancePauseTicks(6);

            // Player 0 grace has expired (Abandoned), but Player 1 is still in grace (Disconnected)
            Assert.That(host.Sessions.TryGetPlayerState(match, p0, out var state0), Is.True);
            Assert.That(state0, Is.EqualTo(PlayerConnectionState.Abandoned));

            Assert.That(host.Sessions.TryGetPlayerState(match, p1, out var state1), Is.True);
            Assert.That(state1, Is.EqualTo(PlayerConnectionState.Disconnected));

            // Host must remain paused because Player 1 is still in grace
            Assert.That(host.IsPaused, Is.True, "Host must remain paused while another player is still in grace");

            // Advance 5 more ticks (current tick = 16 > 5 + 10 for Player 1)
            host.AdvancePauseTicks(5);

            // Both have now expired
            Assert.That(host.Sessions.TryGetPlayerState(match, p1, out state1), Is.True);
            Assert.That(state1, Is.EqualTo(PlayerConnectionState.Abandoned));

            // No players in grace -> automatically resumed!
            Assert.That(host.IsPaused, Is.False, "Host must automatically resume once all disconnected players have abandoned");
        }

        [Test]
        public void CommandOwnership_RejectsCommandsForForeignUnits()
        {
            var server = new MatchServer();
            var p0 = new PlayerId(1);
            var p1 = new PlayerId(2);

            var unit0 = server.SpawnUnit(
                p0, TestStats, new WorldPointMm(10000, 10000), 500);
            var unit1 = server.SpawnUnit(
                p1, TestStats, new WorldPointMm(20000, 20000), 500);

            // 1. MoveCommand from Player 0 targeting Unit 1 (owned by Player 1)
            var moveHeader = new CommandHeader(p0, 1, 1, GameCommandType.Move);
            var moveRejection = server.TryEnqueueMove(
                moveHeader,
                new[] { unit1 },
                new WorldPointMm(15000, 15000),
                new FormationSpec(0, FormationSpacingMm, CardinalFacing.North));

            Assert.That(moveRejection, Is.EqualTo(MatchCommandRejection.NotEntityOwner));

            // 2. AttackCommand from Player 0 using Unit 1 as attacker
            var attackHeader = new CommandHeader(p0, 2, 1, GameCommandType.Attack);
            var attackRejection = server.TryEnqueueAttack(
                attackHeader,
                new[] { unit1 },
                unit0);

            Assert.That(attackRejection, Is.EqualTo(MatchCommandRejection.NotEntityOwner));

            // 3. StopCommand from Player 0 targeting Unit 1
            var stopHeader = new CommandHeader(p0, 3, 1, GameCommandType.Stop);
            var stopRejection = server.TryEnqueueStop(
                stopHeader,
                new[] { unit1 });

            Assert.That(stopRejection, Is.EqualTo(MatchCommandRejection.NotEntityOwner));

            // 4. Client-side PrototypeCommandQueue ownership validation
            var registry = new UnitRegistry();
            var queue = new PrototypeCommandQueue(registry, p0, FormationSpacingMm);

            var go1 = new GameObject("UnitGo1");
            _createdObjects.Add(go1);
            var protoUnit1 = go1.AddComponent<PrototypeUnit>();
            protoUnit1.Initialize(p1);
            protoUnit1.AssignAuthoritativeEntity(unit1);
            registry.Refresh(p0);

            queue.QueueMove(new WorldPointMm(15000, 15000), new[] { unit1 }, 1);
            Assert.That(queue.PendingCount, Is.Zero);
            Assert.That(queue.LastCommandMessage, Does.Contain("not entity owner"));

            queue.QueueAttack(unit0, new[] { unit1 }, 1);
            Assert.That(queue.PendingCount, Is.Zero);
            Assert.That(queue.LastCommandMessage, Does.Contain("not entity owner"));

            queue.QueueStop(new[] { unit1 }, 1);
            Assert.That(queue.PendingCount, Is.Zero);
            Assert.That(queue.LastCommandMessage, Does.Contain("not entity owner"));
        }

        [Test]
        public void Topology2v2_MapsPlayerSlotsToTeamsCorrectly()
        {
            Assert.That(MatchTopology2v2.GetTeamForSlot(0), Is.EqualTo(MatchTopology2v2.TeamRed));
            Assert.That(MatchTopology2v2.GetTeamForSlot(1), Is.EqualTo(MatchTopology2v2.TeamRed));
            Assert.That(MatchTopology2v2.GetTeamForSlot(2), Is.EqualTo(MatchTopology2v2.TeamBlue));
            Assert.That(MatchTopology2v2.GetTeamForSlot(3), Is.EqualTo(MatchTopology2v2.TeamBlue));

            Assert.That(MatchTopology2v2.GetPlayerForSlot(0), Is.EqualTo(new PlayerId(1)));
            Assert.That(MatchTopology2v2.GetPlayerForSlot(1), Is.EqualTo(new PlayerId(2)));
            Assert.That(MatchTopology2v2.GetPlayerForSlot(2), Is.EqualTo(new PlayerId(3)));
            Assert.That(MatchTopology2v2.GetPlayerForSlot(3), Is.EqualTo(new PlayerId(4)));

            Assert.That(MatchTopology2v2.GetTeamForPlayer(new PlayerId(1)), Is.EqualTo(MatchTopology2v2.TeamRed));
            Assert.That(MatchTopology2v2.GetTeamForPlayer(new PlayerId(2)), Is.EqualTo(MatchTopology2v2.TeamRed));
            Assert.That(MatchTopology2v2.GetTeamForPlayer(new PlayerId(3)), Is.EqualTo(MatchTopology2v2.TeamBlue));
            Assert.That(MatchTopology2v2.GetTeamForPlayer(new PlayerId(4)), Is.EqualTo(MatchTopology2v2.TeamBlue));

            Assert.That(MatchTopology2v2.AreAllies(new PlayerId(1), new PlayerId(2)), Is.True);
            Assert.That(MatchTopology2v2.AreAllies(new PlayerId(3), new PlayerId(4)), Is.True);
            Assert.That(MatchTopology2v2.AreAllies(new PlayerId(1), new PlayerId(3)), Is.False);
            Assert.That(MatchTopology2v2.AreEnemies(new PlayerId(1), new PlayerId(3)), Is.True);
        }

        [Test]
        public void RealTimeAdvance_DuringPause_AccumulatesGraceTicksAndResumes()
        {
            var host = new LocalMatchHost(disconnectGraceTicks: 4);
            var match = host.CreateSessionMatch(2);
            var sessionA = host.CreateSession(host.CreateConnectionHandle());
            var sessionB = host.CreateSession(host.CreateConnectionHandle());

            host.TryJoinMatch(sessionA, match, out var playerA);
            host.TryJoinMatch(sessionB, match, out var playerB);

            var template = BuildTwoPlayerTemplate(playerA, playerB);
            host.TryStartSessionMatch(match, template, out _);

            host.StartRealTime();
            Assert.That(host.NotifyConnectionLost(sessionA, host.CurrentTick), Is.True);
            Assert.That(host.IsPaused, Is.True);

            // 5 ticks * 0.05s = 0.25s (> 4 ticks grace)
            host.AdvanceRealTime(0.25);

            Assert.That(host.IsPaused, Is.False, "Real-time advance during tactical pause must elapse grace window and auto-resume");
        }

        private static MatchConfig BuildTwoPlayerTemplate(PlayerId pA, PlayerId pB)
        {
            var specs = new[]
            {
                new UnitSpawnSpec(pA, new WorldPointMm(10000, 10000), 500, false),
                new UnitSpawnSpec(pB, new WorldPointMm(50000, 50000), 500, false)
            };
            return new MatchConfig(TestStats, specs);
        }

        private static MatchConfig BuildFourPlayerTemplate(PlayerId p0, PlayerId p1, PlayerId p2, PlayerId p3)
        {
            var specs = new[]
            {
                new UnitSpawnSpec(p0, new WorldPointMm(10000, 10000), 500, false),
                new UnitSpawnSpec(p1, new WorldPointMm(15000, 10000), 500, false),
                new UnitSpawnSpec(p2, new WorldPointMm(50000, 50000), 500, false),
                new UnitSpawnSpec(p3, new WorldPointMm(55000, 50000), 500, false)
            };
            return new MatchConfig(TestStats, specs);
        }
    }
}
