using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Replication;
using GlobalFront.Client.UI;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode.Integration.Replication;
using NUnit.Framework;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;
using ReconnectResult = GlobalFront.Core.Reconnect.ReconnectResult;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Network
{
    /// <summary>
    /// End-to-end integration playtest harness and scenario test suite for 2v2 matches (Phase 2.8, Step 2.8.3).
    /// Tests simultaneous combat, command ownership, tactical pause, C0 resync during pause,
    /// 5-second countdown resumption, and abandonment forfeit (2v1) auto-resumption.
    /// </summary>
    [TestFixture]
    public sealed class Match2v2PlaytestScenarioTests
    {
        private static readonly CombatStats CombatStats2v2 = new CombatStats(
            maximumHealth: 100,
            damage: 25,
            rangeMm: 7000,
            cooldownTicks: 10);

        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private readonly List<OwnDatagramCarrier> _createdCarriers = new List<OwnDatagramCarrier>();

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _createdCarriers.Count; i++)
            {
                _createdCarriers[i]?.Dispose();
            }
            _createdCarriers.Clear();

            for (var i = 0; i < _createdObjects.Count; i++)
            {
                if (_createdObjects[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
                }
            }
            _createdObjects.Clear();
        }

        private sealed class Match2v2Fixture : IDisposable
        {
            public ReplicationIntegrationWorld World;
            public readonly List<ClientReconnectCoordinator> Coordinators = new List<ClientReconnectCoordinator>();
            public readonly List<TacticalPauseOverlay> Overlays = new List<TacticalPauseOverlay>();
            public readonly List<NetworkHudPresenter> Presenters = new List<NetworkHudPresenter>();

            public PlayerId P1 = new PlayerId(1);
            public PlayerId P2 = new PlayerId(2);
            public PlayerId P3 = new PlayerId(3);
            public PlayerId P4 = new PlayerId(4);

            public CoreEntityId Unit1;
            public CoreEntityId Unit2;
            public CoreEntityId Unit3;
            public CoreEntityId Unit4;

            public void Dispose()
            {
                for (var i = 0; i < Presenters.Count; i++)
                {
                    Presenters[i]?.Dispose();
                }
                Presenters.Clear();
                Coordinators.Clear();
                Overlays.Clear();
            }
        }

        private Match2v2Fixture CreateFixture(
            int disconnectGraceTicks = LocalMatchHost.DefaultDisconnectGraceTicks,
            FogOfWarReplicationFilter fogFilter = null,
            ICombatRelation combatRelation = null)
        {
            var fixture = new Match2v2Fixture();

            fixture.World = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(),
                ServerReplicationEmitterConfig.Default,
                clientCount: 4,
                disconnectGraceTicks: disconnectGraceTicks,
                filter: fogFilter,
                combatRelation: combatRelation);

            for (var i = 0; i < 4; i++)
            {
                var client = fixture.World.Clients[i];

                var go = new GameObject($"Overlay_{i}");
                _createdObjects.Add(go);
                var overlay = go.AddComponent<TacticalPauseOverlay>();
                fixture.Overlays.Add(overlay);

                var coord = new ClientReconnectCoordinator(client.Receiver);
                fixture.Coordinators.Add(coord);
                fixture.World.Rig.Host.BindReconnectCoordinator(coord);

                coord.ResyncStarted += seq => client.Bridge.PrepareForResync(seq);
                coord.CacheSession(
                    client.Endpoint.Session,
                    client.Endpoint.Match,
                    client.Endpoint.Player,
                    client.Endpoint.Secret);
                client.Endpoint.Lost += reason => coord.OnTransportDisconnected(reason);

                var presenter = new NetworkHudPresenter(fixture.World.Rig.Host, coord, overlay);
                fixture.Presenters.Add(presenter);
            }

            // Spawn 2v2 combat units:
            // Team 0 (Red): P1 at (10000, 10000), P2 at (15000, 10000)
            // Team 1 (Blue): P3 at (10000, 15000), P4 at (15000, 15000)
            // Mutual distance = 5000 mm < 7000 mm range!
            var specs = new[]
            {
                new UnitSpawnSpec(fixture.P1, new WorldPointMm(10000, 10000), 500, false),
                new UnitSpawnSpec(fixture.P2, new WorldPointMm(15000, 10000), 500, false),
                new UnitSpawnSpec(fixture.P3, new WorldPointMm(10000, 15000), 500, false),
                new UnitSpawnSpec(fixture.P4, new WorldPointMm(15000, 15000), 500, false)
            };

            var matchConfig = new MatchConfig(CombatStats2v2, specs);
            Assert.That(
                fixture.World.Rig.Host.TryStartSessionMatch(fixture.World.Rig.Match, matchConfig, out var entityIds),
                Is.True,
                "2v2 session match must start");

            // Step 5.6.2: a fog-equipped fixture maps the four slots onto their
            // teams immediately, so shared vision applies from the first tick.
            if (fogFilter != null)
            {
                fogFilter.SetPlayerTeam(fixture.P1, MatchTopology2v2.TeamRed);
                fogFilter.SetPlayerTeam(fixture.P2, MatchTopology2v2.TeamRed);
                fogFilter.SetPlayerTeam(fixture.P3, MatchTopology2v2.TeamBlue);
                fogFilter.SetPlayerTeam(fixture.P4, MatchTopology2v2.TeamBlue);
            }

            fixture.Unit1 = entityIds[0];
            fixture.Unit2 = entityIds[1];
            fixture.Unit3 = entityIds[2];
            fixture.Unit4 = entityIds[3];

            // Pump initial ticks so all 4 clients acquire their baseline keyframe and reach Streaming
            for (var tick = 0; tick < 5; tick++)
            {
                fixture.World.PumpTick();
            }

            for (var i = 0; i < 4; i++)
            {
                Assert.That(fixture.World.Clients[i].Receiver.IsWorldUsable, Is.True, $"Client {i} world must be usable");
            }

            return fixture;
        }

        [Test]
        public void Playtest2v2_SimultaneousCombat_MaintainsIdenticalChecksumsAcrossAllFourClients()
        {
            using var fixture = CreateFixture();
            var world = fixture.World;

            var client0 = world.Clients[0];
            var client1 = world.Clients[1];
            var client2 = world.Clients[2];
            var client3 = world.Clients[3];

            // 1. Verify Command Ownership: Client 0 (P1) attempts to command foreign Unit 3 (owned by P3)
            var foreignMoveRejection = client0.Commands.TrySubmitMove(
                new CommandHeader(fixture.P1, client0.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Move),
                new[] { fixture.Unit3 },
                new WorldPointMm(20000, 20000),
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(foreignMoveRejection, Is.EqualTo(MatchCommandRejection.None), "Command passed client preflight");

            // Server-side authoritative direct ownership validation
            var directRejection = world.Rig.Host.Server.TryEnqueueMove(
                new CommandHeader(fixture.P1, 999, world.Rig.Host.CurrentTick + 1, GameCommandType.Move),
                new[] { fixture.Unit3 },
                new WorldPointMm(20000, 20000),
                new FormationSpec(1, 2000, CardinalFacing.North));
            Assert.That(directRejection, Is.EqualTo(MatchCommandRejection.UnknownEntity),
                "Authoritative server must reject command targeting foreign unit without confirming its existence");

            // Pump network ticks until command ack round-trip arrives
            for (var i = 0; i < 5 && client0.CommandAcks.Count == 0; i++)
            {
                world.PumpTick();
            }

            if (client0.CommandAcks.Count > 0)
            {
                var foreignAck = client0.CommandAcks[client0.CommandAcks.Count - 1];
                Assert.That(foreignAck.Command, Is.EqualTo(MatchCommandRejection.UnknownEntity),
                    "Server command ack must use the fail-closed rejection for foreign ids");
            }

            // 2. Simultaneous Combat: All 4 players issue valid attack orders simultaneously
            // P1 (Red) attacks Unit 3 (P3, Blue)
            client0.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P1, client0.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit1 },
                fixture.Unit3);

            // P2 (Red) attacks Unit 4 (P4, Blue)
            client1.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P2, client1.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit2 },
                fixture.Unit4);

            // P3 (Blue) attacks Unit 1 (P1, Red)
            client2.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P3, client2.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit3 },
                fixture.Unit1);

            // P4 (Blue) attacks Unit 2 (P2, Red)
            client3.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P4, client3.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit4 },
                fixture.Unit2);

            // 3. Advance combat over 50 ticks (2.5 seconds of simulated battle)
            for (var tick = 0; tick < 50; tick++)
            {
                world.PumpTick();
            }

            Assert.That(world.Rig.Host.CurrentTick, Is.GreaterThanOrEqualTo(50UL));

            // Verify units took damage from attacks
            Assert.That(client0.Receiver.TryGetUnit(fixture.Unit1, out var u1State), Is.True);
            Assert.That(u1State.Health, Is.LessThan(100), "Unit 1 must have taken combat damage");

            Assert.That(client0.Receiver.TryGetUnit(fixture.Unit3, out var u3State), Is.True);
            Assert.That(u3State.Health, Is.LessThan(100), "Unit 3 must have taken combat damage");

            // 4. Verify StateChecksum across all 4 clients matches bit-for-bit
            var chk0 = client0.Receiver.World.ComputeStateChecksum();
            var chk1 = client1.Receiver.World.ComputeStateChecksum();
            var chk2 = client2.Receiver.World.ComputeStateChecksum();
            var chk3 = client3.Receiver.World.ComputeStateChecksum();

            Assert.That(chk0, Is.Not.Zero, "Checksum must be non-zero");
            Assert.That(chk1, Is.EqualTo(chk0), "Client 1 checksum must match Client 0");
            Assert.That(chk2, Is.EqualTo(chk0), "Client 2 checksum must match Client 0");
            Assert.That(chk3, Is.EqualTo(chk0), "Client 3 checksum must match Client 0");
        }

        [Test]
        public void Playtest2v2_AllyDisconnectDuringCombat_PausesWorld_ResyncsAndResumesAccurately()
        {
            using var fixture = CreateFixture();
            var world = fixture.World;

            var client0 = world.Clients[0];
            var client1 = world.Clients[1];
            var client2 = world.Clients[2];
            var client3 = world.Clients[3];

            // 1. Advance 10 ticks into the match with initial attacks
            client0.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P1, client0.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit1 },
                fixture.Unit3);
            client2.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P3, client2.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit3 },
                fixture.Unit1);

            for (var tick = 0; tick < 10; tick++)
            {
                world.PumpTick();
            }

            // 2. In mid-combat, Player 2 (Team 0, Client 1) disconnects involuntarily
            Assert.That(world.Rig.ServerTransport.Binder.TryGetTokenBySession(client1.Endpoint.Session, out var token1), Is.True);
            Assert.That(world.Rig.ServerTransport.Binder.TryGetByToken(token1, out var binding1), Is.True);

            client1.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            world.Rig.ServerCarrier.CloseConnection(binding1.ConnectionId, TransportDisconnectReason.TransportLost);

            for (var i = 0; i < 5; i++)
            {
                world.Pump(5);
            }

            // Verify host automatically pauses
            Assert.That(world.Rig.Host.IsPaused, Is.True, "Host must auto-pause on player disconnect");

            // Verify presenters of remaining players activate pause overlay with Player 2 as pausing player
            Assert.That(fixture.Presenters[0].State.IsTacticalPauseActive, Is.True);
            Assert.That(fixture.Presenters[0].State.PausingPlayer, Is.EqualTo(fixture.P2));
            Assert.That(fixture.Overlays[0].IsVisible, Is.True);
            Assert.That(fixture.Overlays[0].Message, Does.Contain("Игрок 2"));

            Assert.That(fixture.Presenters[2].State.IsTacticalPauseActive, Is.True);
            Assert.That(fixture.Presenters[3].State.IsTacticalPauseActive, Is.True);

            // 3. Verify simulation is completely frozen
            var pausedTick = world.Rig.Host.CurrentTick;
            Assert.That(world.Rig.Host.TickOnce(), Is.False, "Simulation must not advance while paused");
            Assert.That(world.Rig.Host.CurrentTick, Is.EqualTo(pausedTick), "Tick must remain frozen");

            Assert.That(client0.Receiver.TryGetUnit(fixture.Unit1, out var u1Before), Is.True);
            Assert.That(client0.Receiver.TryGetUnit(fixture.Unit3, out var u3Before), Is.True);

            for (var i = 0; i < 10; i++)
            {
                world.Pump(5);
            }

            Assert.That(client0.Receiver.TryGetUnit(fixture.Unit1, out var u1After), Is.True);
            Assert.That(client0.Receiver.TryGetUnit(fixture.Unit3, out var u3After), Is.True);
            Assert.That(u1After.Health, Is.EqualTo(u1Before.Health), "No damage dealt during tactical pause");
            Assert.That(u1After.Position, Is.EqualTo(u1Before.Position), "No movement during tactical pause");

            // 4. Player 2 reconnects over C0
            var reconnectAddress = world.Rig.Pipe.CreateEndpoint();
            var reconnectCarrier = OwnDatagramCarrier.CreateClient(
                world.Rig.Pipe, reconnectAddress, TransportTestSupport.ServerAddressOf(world.Rig), world.Rig.Clock);
            _createdCarriers.Add(reconnectCarrier);

            var reconnectConnId = reconnectCarrier.ConnectToServer("virtual", 0);

            for (var i = 0; i < 5; i++)
            {
                world.Rig.Pump(5);
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);
            }

            var coord1 = fixture.Coordinators[1];
            coord1.LastAppliedTick = client1.Receiver.LastAppliedTick;
            var reqBuffer = new byte[ReconnectRequest.SizeBytes];
            Assert.That(coord1.TryBuildReconnectRequest(reqBuffer, out var written), Is.True);
            reconnectCarrier.Send(reconnectConnId, TransportChannel.Control, reqBuffer, 0, written);

            // Pump paused world so server streams keyframe slices of the frozen state
            var responseReceived = false;
            ReconnectResponse reconnectResponse = default;
            var elapsed = 0L;
            while ((!responseReceived || !client1.Receiver.IsWorldUsable) && elapsed < 5000)
            {
                world.Rig.Pump(5);
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);
                client1.Bridge.Pump(world.Rig.Clock.NowMs);

                while (reconnectCarrier.TryDequeueEvent(out var evt))
                {
                    if (evt.Type == TransportEventType.Message)
                    {
                        if (evt.Length > 0 && evt.Data[0] == ReconnectWireCodec.ResponseOpcode)
                        {
                            Assert.That(ReconnectWireCodec.TryDecodeResponse(evt.Data.AsSpan(0, evt.Length), out reconnectResponse), Is.True);
                            responseReceived = true;
                            coord1.OnReconnectResponse(in reconnectResponse);
                        }
                        else
                        {
                            var err = MessageCodec.TryDecodeHeader(evt.Data, 0, evt.Length, out var header);
                            if (err == MessageError.None && header.Type == TransportMessageType.Snapshot)
                            {
                                var payload = new byte[header.PayloadLength];
                                Array.Copy(evt.Data, header.PayloadOffset, payload, 0, header.PayloadLength);
                                client1.Bridge.HandleSnapshotPayload(header.SnapshotTick, payload, header.PayloadLength, world.Rig.Clock.NowMs);
                            }
                        }
                    }
                }
                elapsed += 5;
            }

            Assert.That(responseReceived, Is.True, "Reconnect response must be received");
            Assert.That(reconnectResponse.Result, Is.EqualTo(ReconnectResult.Accepted));
            Assert.That(client1.Receiver.IsWorldUsable, Is.True, "Client 1 world must be restored via keyframe");

            // 5. Client 1 installs keyframe and initiates 5-second countdown
            coord1.OnKeyframeInstalled();
            Assert.That(coord1.State, Is.EqualTo(ClientReconnectState.Countdown));
            Assert.That(coord1.CountdownRemainingSeconds, Is.EqualTo(5.0f));

            Assert.That(fixture.Presenters[1].State.IsResumeCountdownActive, Is.True);
            Assert.That(fixture.Presenters[1].State.CountdownSecondsRemaining, Is.EqualTo(5));

            // Advance countdown
            coord1.AdvanceCountdown(2.0f, out var ready1);
            fixture.Presenters[1].AdvanceRealTime(2.0);
            Assert.That(ready1, Is.False);
            Assert.That(fixture.Presenters[1].State.CountdownSecondsRemaining, Is.EqualTo(3));

            coord1.AdvanceCountdown(3.0f, out var ready2);
            fixture.Presenters[1].AdvanceRealTime(3.0);
            Assert.That(ready2, Is.True);

            // Simulation auto-resumes
            world.Rig.Host.Resume();
            Assert.That(world.Rig.Host.IsPaused, Is.False, "Host must resume when countdown finishes");
            Assert.That(fixture.Presenters[0].State.IsTacticalPauseActive, Is.False);
            Assert.That(fixture.Presenters[1].State.IsTacticalPauseActive, Is.False);
            Assert.That(fixture.Overlays[0].IsVisible, Is.False);

            // 6. Continue battle for 30 more ticks with all 4 players actively participating
            client1.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P2, client1.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit2 },
                fixture.Unit4);

            for (var i = 0; i < 30; i++)
            {
                world.PumpTick();
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);

                while (reconnectCarrier.TryDequeueEvent(out var evt))
                {
                    if (evt.Type == TransportEventType.Message)
                    {
                        var err = MessageCodec.TryDecodeHeader(evt.Data, 0, evt.Length, out var header);
                        if (err == MessageError.None && header.Type == TransportMessageType.Snapshot)
                        {
                            var payload = new byte[header.PayloadLength];
                            Array.Copy(evt.Data, header.PayloadOffset, payload, 0, header.PayloadLength);
                            client1.Bridge.HandleSnapshotPayload(header.SnapshotTick, payload, header.PayloadLength, world.Rig.Clock.NowMs);
                        }
                    }
                }
                client1.Bridge.Pump(world.Rig.Clock.NowMs);
            }

            Assert.That(world.Rig.Host.CurrentTick, Is.GreaterThanOrEqualTo(pausedTick + 30));

            // 7. Verify all 4 clients maintain identical bit-for-bit checksums
            var chk0 = client0.Receiver.World.ComputeStateChecksum();
            var chk1 = client1.Receiver.World.ComputeStateChecksum();
            var chk2 = client2.Receiver.World.ComputeStateChecksum();
            var chk3 = client3.Receiver.World.ComputeStateChecksum();

            Assert.That(chk0, Is.Not.Zero);
            Assert.That(chk1, Is.EqualTo(chk0), "Reconnected client 1 checksum must match host world");
            Assert.That(chk2, Is.EqualTo(chk0), "Client 2 checksum must match host world");
            Assert.That(chk3, Is.EqualTo(chk0), "Client 3 checksum must match host world");
        }

        [Test]
        public void Playtest2v2_PlayerAbandonsMatch_GraceExpires_GameResumesInTwoVsOne()
        {
            // Short grace window of 10 ticks for fast test execution
            using var fixture = CreateFixture(disconnectGraceTicks: 10);
            var world = fixture.World;

            var client0 = world.Clients[0];
            var client1 = world.Clients[1];
            var client2 = world.Clients[2];
            var client3 = world.Clients[3];

            // 1. Advance 5 ticks
            for (var tick = 0; tick < 5; tick++)
            {
                world.PumpTick();
            }

            // 2. Player 4 (Team 1, Client 3) disconnects
            Assert.That(world.Rig.ServerTransport.Binder.TryGetTokenBySession(client3.Endpoint.Session, out var token3), Is.True);
            Assert.That(world.Rig.ServerTransport.Binder.TryGetByToken(token3, out var binding3), Is.True);

            client3.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            world.Rig.ServerCarrier.CloseConnection(binding3.ConnectionId, TransportDisconnectReason.TransportLost);

            for (var i = 0; i < 5; i++)
            {
                world.Pump(5);
            }

            Assert.That(world.Rig.Host.IsPaused, Is.True, "Host must pause when Player 4 disconnects");
            Assert.That(fixture.Presenters[0].State.IsTacticalPauseActive, Is.True);
            Assert.That(fixture.Presenters[0].State.PausingPlayer, Is.EqualTo(fixture.P4));
            Assert.That(fixture.Presenters[0].State.Slots[3].SlotStatus, Is.EqualTo(SlotStatus.Disconnected));

            // 3. Player 4 does not return; advance past grace window (11 ticks > 10 ticks grace)
            world.Rig.Host.AdvancePauseTicks(11);

            // Verify Player 4 transitioned to Abandoned
            Assert.That(world.Rig.Host.Sessions.TryGetPlayerState(world.Rig.Match, fixture.P4, out var p4State), Is.True);
            Assert.That(p4State, Is.EqualTo(PlayerConnectionState.Abandoned), "Player 4 must be Abandoned");
            Assert.That(fixture.Presenters[0].State.Slots[3].SlotStatus, Is.EqualTo(SlotStatus.Abandoned));

            // 4. Host automatically lifts tactical pause because no disconnected players remain in grace
            Assert.That(world.Rig.Host.IsPaused, Is.False, "Host must auto-resume once grace expires");
            Assert.That(fixture.Presenters[0].State.IsTacticalPauseActive, Is.False);
            Assert.That(fixture.Overlays[0].IsVisible, Is.False);

            // 5. Match continues in 2v1 (P1, P2 vs P3)
            client0.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P1, client0.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit1 },
                fixture.Unit3);
            client1.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P2, client1.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit2 },
                fixture.Unit3);
            client2.Commands.TrySubmitAttack(
                new CommandHeader(fixture.P3, client2.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                new[] { fixture.Unit3 },
                fixture.Unit1);

            var tickBefore2v1 = world.Rig.Host.CurrentTick;
            for (var tick = 0; tick < 20; tick++)
            {
                world.PumpTick();
            }

            Assert.That(world.Rig.Host.CurrentTick, Is.EqualTo(tickBefore2v1 + 20), "Simulation must advance 20 ticks in 2v1");

            // Remaining 3 active clients maintain identical checksums
            var chk0 = client0.Receiver.World.ComputeStateChecksum();
            var chk1 = client1.Receiver.World.ComputeStateChecksum();
            var chk2 = client2.Receiver.World.ComputeStateChecksum();

            Assert.That(chk0, Is.Not.Zero);
            Assert.That(chk1, Is.EqualTo(chk0), "Client 1 checksum must match Client 0 in 2v1");
            Assert.That(chk2, Is.EqualTo(chk0), "Client 2 checksum must match Client 0 in 2v1");
        }

        // ------------------------------------------------------------------
        // Step 5.6.2: Milestone M2 acceptance (shared vision, splash, team win)
        // ------------------------------------------------------------------

        [Test]
        public void Playtest2v2_SharedVision_AlliedSightRevealsEnemiesInReplicationSlice()
        {
            using var fixture = CreateFixture(
                fogFilter: new FogOfWarReplicationFilter());
            var world = fixture.World;
            var client0 = world.Clients[0];

            // The P1 army sits at its fixture base; the shared-vision geometry
            // is staged far away from it: an allied scout within sight of an
            // enemy, and that enemy beyond the P1 army's own sight radius.
            // Sight radius is the engine default of 20 000 mm.
            var allyScout = world.Rig.Host.Server.SpawnUnit(
                fixture.P2,
                new CombatStats(100, 1, 7000, 3),
                new WorldPointMm(-10_000, 0),
                speedMmPerTick: 500,
                autoAcquire: false,
                UnitKinds.Infantry);
            var enemy = world.Rig.Host.Server.SpawnUnit(
                fixture.P3,
                new CombatStats(100, 1, 7000, 3),
                new WorldPointMm(-25_000, 0),
                speedMmPerTick: 500,
                autoAcquire: false,
                UnitKinds.Infantry);
            var farEnemy = world.Rig.Host.Server.SpawnUnit(
                fixture.P4,
                new CombatStats(100, 1, 7000, 3),
                new WorldPointMm(200_000, 0),
                speedMmPerTick: 500,
                autoAcquire: false,
                UnitKinds.Infantry);

            // The fog filter serves masks from its per-tick history, so the
            // scenario asserts need a completed tick recorded after spawn.
            for (var tick = 0; tick < 3; tick++)
            {
                world.PumpTick();
            }

            // P2 (15 000 mm from the enemy) sees it; the P1 army (about
            // 36 400 mm away) cannot see it on its own — but shared vision is
            // exactly the feature under test: through the ally, P1 sees it too.
            var lastCompletedTick = world.Rig.Host.CurrentTick - 1;
            Assert.That(
                world.Rig.Host.Server.VisibilityFilter.IsVisible(
                    lastCompletedTick, fixture.P2, enemy),
                Is.True, "scenario setup: the allied scout must see the enemy");
            Assert.That(
                world.Rig.Host.Server.VisibilityFilter.IsVisible(
                    lastCompletedTick, fixture.P1, farEnemy),
                Is.False,
                "an enemy nobody's sight reaches stays hidden even through the team map");

            for (var tick = 0; tick < 5; tick++)
            {
                world.PumpTick();
            }

            // Shared vision: the enemy the ally revealed lands in P1's
            // replication slice, and the far enemy stays out of it.
            Assert.That(
                client0.Receiver.World.Contains(enemy),
                Is.True,
                "an enemy revealed by an ally must reach the ally's teammate over the wire");
            Assert.That(
                client0.Receiver.World.Contains(farEnemy),
                Is.False,
                "an enemy nobody sees must not reach the teammate's slice");
            Assert.That(
                client0.Receiver.World.Contains(allyScout),
                Is.True,
                "allies are always visible to each other");
        }

        [Test]
        public void Playtest2v2_FriendlySplash_DoesNotDamageAlliedUnits()
        {
            // Team relation: the red ally (P2) must be untouchable by splash.
            using var fixture = CreateFixture(
                combatRelation: Team2v2CombatRelation.Instance);
            var world = fixture.World;
            var client0 = world.Clients[0];

            // P1 artillery (splash 8000) fires at a blue cluster; a red ally
            // stands well inside the blast radius — friendly splash is
            // forbidden, so only the blue units lose health.
            var artillery = world.Rig.Host.Server.SpawnUnit(
                fixture.P1,
                new CombatStats(
                    100, 40, 15_000, 1,
                    (byte)DamageType.ArtilleryExplosive, (byte)ArmorType.HeavyVehicle,
                    splashRadiusMm: 8_000),
                new WorldPointMm(0, 0),
                speedMmPerTick: 500,
                autoAcquire: false,
                UnitKinds.Tank);
            var ally = world.Rig.Host.Server.SpawnUnit(
                fixture.P2,
                new CombatStats(100, 1, 7000, 3),
                new WorldPointMm(10_000, -12_000),
                speedMmPerTick: 500,
                autoAcquire: false,
                UnitKinds.Infantry);
            var primary = world.Rig.Host.Server.SpawnUnit(
                fixture.P3,
                new CombatStats(1000, 1, 7000, 3),
                new WorldPointMm(10_000, -8_000),
                speedMmPerTick: 500,
                autoAcquire: false,
                UnitKinds.Infantry);
            var nearbyEnemy = world.Rig.Host.Server.SpawnUnit(
                fixture.P3,
                new CombatStats(1000, 1, 7000, 3),
                new WorldPointMm(10_000, -6_000),
                speedMmPerTick: 500,
                autoAcquire: false,
                UnitKinds.Infantry);

            // The order rides the canonical client command path (wire > session
            // > schedule), exactly as the green SimultaneousCombat scenario
            // drives its shots. Ten shots at cooldown 1: the blue cluster loses
            // a known amount of health while the red ally stands untouched
            // inside the blast radius.
            Assert.That(
                client0.Commands.TrySubmitAttack(
                    new CommandHeader(fixture.P1, client0.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                    new[] { artillery },
                    primary),
                Is.EqualTo(MatchCommandRejection.None), "the artillery order must pass preflight");

            for (var tick = 0; tick < 10; tick++)
            {
                world.PumpTick();
            }

            // Exact pins over nine landed shots (the wire order executes one
            // tick after submission, so ten pumps carry nine shots): the direct
            // hit is 40 per shot (ArtilleryExplosive x legacy sentinel =
            // x1.000), the hostile neighbor takes the linear falloff at 2000 mm
            // of an 8000 mm radius (30 per shot), and the red ally at 4000 mm
            // takes nothing — friendly splash is forbidden outright (RND §7.3).
            Assert.That(
                world.Rig.Host.Server.TryGetUnit(primary, out var primaryState), Is.True);
            Assert.That(
                1000 - primaryState.CurrentHealth,
                Is.EqualTo(9 * DamageCalculator.CalculateDamage(
                    40, DamageType.ArtilleryExplosive, (ArmorType)CombatStats.LegacySentinel)),
                "the primary target takes exactly the direct hits");
            Assert.That(
                world.Rig.Host.Server.TryGetUnit(nearbyEnemy, out var nearbyState), Is.True);
            Assert.That(
                1000 - nearbyState.CurrentHealth,
                Is.EqualTo(9 * SplashCalculator.CalculateLinearFalloff(40, 8_000, 2_000)),
                "the hostile neighbor inside the blast takes the linear falloff");
            Assert.That(
                world.Rig.Host.Server.TryGetUnit(ally, out var allyState), Is.True);
            Assert.That(
                allyState.CurrentHealth, Is.EqualTo(100),
                "friendly splash is forbidden outright (RND §7.3): the red ally is untouched");
            Assert.That(
                world.Rig.Host.Server.TryGetUnit(artillery, out var artilleryState), Is.True);
            Assert.That(artilleryState.CurrentHealth, Is.EqualTo(100),
                "the attacker never splashes itself");
        }

        [Test]
        public void Playtest2v2_TeamVictory_TerminatesAllFourClientsWithMatchFinished()
        {
            // The team relation decides the outcome: Team Red wins when Team
            // Blue dies (a skirmish host would report a player victory instead).
            using var fixture = CreateFixture(
                combatRelation: Team2v2CombatRelation.Instance);
            var world = fixture.World;
            var client0 = world.Clients[0];

            // The production runner owns this wiring (Step 4.6.4, M1.1 P1-02):
            // a terminal outcome detaches every session with MatchFinished, and
            // the client endpoint surfaces it to the coordinator. The bare rig
            // has no runner, so the test composes the same two lines.
            world.Rig.Host.SessionTerminated += (session, reason) =>
                world.Rig.ServerTransport.DetachSession(session, reason);
            for (var i = 0; i < 4; i++)
            {
                var coordinator = fixture.Coordinators[i];
                world.Clients[i].Endpoint.Terminated += reason =>
                    coordinator.OnSessionTerminated(reason);
            }

            // Terminal reasons captured per client through the endpoint's
            // termination event.
            var reasons = new List<TransportDisconnectReason> { 0, 0, 0, 0 };
            for (var i = 0; i < 4; i++)
            {
                var index = i;
                world.Clients[i].Endpoint.Terminated += reason => reasons[index] = reason;
            }

            // A one-shot killer next to the blue cluster decides the match
            // through the authoritative combat path: Team Blue (P3, P4) dies,
            // Team Red survives, the outcome is a team victory. The kills are
            // explicit wire orders from client 0 (P1), not auto-acquisition,
            // so the scenario stays deterministic.
            var killer = world.Rig.Host.Server.SpawnUnit(
                fixture.P1,
                new CombatStats(100_000, 1_000, 12_000, 1),
                new WorldPointMm(8_000, 15_000),
                speedMmPerTick: 1,
                autoAcquire: false);

            Assert.That(
                client0.Commands.TrySubmitAttack(
                    new CommandHeader(fixture.P1, client0.NextCommandSequence++, world.Rig.Host.CurrentTick + 1, GameCommandType.Attack),
                    new[] { killer },
                    fixture.Unit3),
                Is.EqualTo(MatchCommandRejection.None),
                "the killer must accept the order onto Unit 3");
            Assert.That(
                client0.Commands.TrySubmitAttack(
                    new CommandHeader(fixture.P1, client0.NextCommandSequence++, world.Rig.Host.CurrentTick + 2, GameCommandType.Attack),
                    new[] { killer },
                    fixture.Unit4),
                Is.EqualTo(MatchCommandRejection.None),
                "the queued second order removes Unit 4 right after");

            var budget = 0;
            while (!world.Rig.Host.Server.Outcome.IsTerminal && budget < 200)
            {
                world.PumpTick();
                budget++;
            }

            Assert.That(budget, Is.LessThan(200),
                "the kills must land well inside the tick budget");
            Assert.That(world.Rig.Host.Server.Outcome.IsTerminal, Is.True,
                "eliminating Team Blue must terminate the match");
            Assert.That(
                world.Rig.Host.Server.Outcome.IsTeamVictory, Is.True);
            Assert.That(
                world.Rig.Host.Server.Outcome.WinningTeam,
                Is.EqualTo(MatchTopology2v2.TeamRed));

            // The terminal outcome fans out to every attached client as
            // MatchFinished, and all four sessions terminate cleanly.
            var terminated = 0;
            var terminationBudget = 0;
            while (terminated < 4 && terminationBudget < 100)
            {
                world.Pump(5);
                terminated = 0;
                for (var i = 0; i < 4; i++)
                {
                    if (fixture.Coordinators[i].State == ClientReconnectState.Terminated)
                    {
                        terminated++;
                    }
                }

                terminationBudget++;
            }

            for (var i = 0; i < 4; i++)
            {
                Assert.That(
                    fixture.Coordinators[i].State,
                    Is.EqualTo(ClientReconnectState.Terminated),
                    $"client {i} must be terminally closed (no reconnect armed)");
                Assert.That(
                    world.Clients[i].Endpoint.State,
                    Is.EqualTo(ClientTransportState.Disconnected),
                    $"client {i} socket must be closed by the server");
                Assert.That(
                    reasons[i],
                    Is.EqualTo(TransportDisconnectReason.MatchFinished),
                    $"client {i} must receive the MatchFinished terminal reason");
            }
        }
    }
}
