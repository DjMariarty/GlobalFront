using System;
using System.Collections;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.DedicatedServer;
using GlobalFront.Server;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using EntityId = GlobalFront.Core.Model.EntityId;
using Object = UnityEngine.Object;

namespace GlobalFront.Tests.PlayMode
{
    /// <summary>
    /// Phase 4, step 4.2 (OD-38, OD-47): the real end-to-end path — one
    /// <see cref="DedicatedServerRunner"/> and two remote clients over actual
    /// UDP loopback through LiteNetLib.
    ///
    /// The server's tick driver is put in manual mode before the first frame,
    /// so the test, not the wall clock, decides when tick 0 ends. That makes
    /// the baseline deterministic: both clients must be attached before the
    /// match starts, the emitter answers their requests on a completed tick,
    /// and the move command the first client sends is applied by a tick the
    /// test asks for. Everything after the command is waited for with a
    /// wall-clock bound, because the carriers deliver on their worker
    /// threads.
    /// </summary>
    [TestFixture]
    public sealed class NetworkedClientPlayModeTests
    {
        private const float HandshakeTimeoutSeconds = 20f;
        private const float BaselineTimeoutSeconds = 20f;
        private const float DeliveryTimeoutSeconds = 20f;

        private static readonly CombatStats StandardStats =
            new CombatStats(maximumHealth: 100, damage: 25, rangeMm: 7000, cooldownTicks: 10);

        private readonly List<RemoteSliceSession> _sessions = new List<RemoteSliceSession>();
        private GameObject _serverObject;
        private DedicatedServerRunner _server;
        private int _savedTargetFrameRate;

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _sessions.Count; index++)
            {
                _sessions[index]?.Dispose();
            }

            _sessions.Clear();

            if (_server != null)
            {
                _server.Shutdown();
            }

            if (_serverObject != null)
            {
                Object.Destroy(_serverObject);
            }

            Application.targetFrameRate = _savedTargetFrameRate;
        }

        [UnityTest]
        public IEnumerator DedicatedServer_TwoRemoteClients_HandshakeMoveAndReplicate()
        {
            _savedTargetFrameRate = Application.targetFrameRate;

            _serverObject = new GameObject("NetworkedClientPlayModeTests_Server");
            _server = _serverObject.AddComponent<DedicatedServerRunner>();
            _server.Initialize(0);

            // Deterministic authority: the test is the only tick source.
            _server.Host.StopRealTime();

            var serverCarrier = _server.Carrier as LiteNetLibCarrier;
            Assert.That(serverCarrier, Is.Not.Null,
                "DedicatedServerRunner.Initialize(port) must create a LiteNetLib carrier");
            var port = serverCarrier.LocalPort;
            Assert.That(port, Is.GreaterThan(0),
                "the dedicated server must bind a UDP port");

            var sessionA = new RemoteSliceSession();
            var sessionB = new RemoteSliceSession();
            _sessions.Add(sessionA);
            _sessions.Add(sessionB);
            sessionA.Connect("127.0.0.1", port);
            sessionB.Connect("127.0.0.1", port);

            yield return WaitFor(
                () =>
                {
                    sessionA.Pump(sessionA.Clock.NowMs);
                    sessionB.Pump(sessionB.Clock.NowMs);
                    return sessionA.IsConnected && sessionB.IsConnected;
                },
                HandshakeTimeoutSeconds,
                "both remote clients must complete the UDP handshake");

            var playerA = sessionA.Endpoint.Player;
            var playerB = sessionB.Endpoint.Player;
            Assert.That(playerA.IsValid, Is.True);
            Assert.That(playerB.IsValid, Is.True);
            Assert.That(playerA, Is.Not.EqualTo(playerB),
                "the server must assign distinct player ids");
            Assert.That(sessionA.Endpoint.Match, Is.EqualTo(_server.Match));
            Assert.That(sessionB.Endpoint.Match, Is.EqualTo(_server.Match));

            Assert.That(_server.Host.CurrentTick, Is.EqualTo(0ul),
                "the server must still be at tick 0 before the first manual tick");

            var specs = new[]
            {
                new UnitSpawnSpec(playerA, new WorldPointMm(0, 0), 350, false),
                new UnitSpawnSpec(playerB, new WorldPointMm(20_000, 0), 350, false)
            };
            Assert.That(
                _server.Host.TryStartSessionMatch(
                    _server.Match, new MatchConfig(StandardStats, specs), out var entityIds),
                Is.True,
                "the dedicated server must start the match for both attached sessions");

            // The emitter is request-driven and serves a keyframe only on a
            // completed tick, so the test ticks while both clients request.
            yield return WaitFor(
                () =>
                {
                    _server.Host.TickOnce();
                    sessionA.Pump(sessionA.Clock.NowMs);
                    sessionB.Pump(sessionB.Clock.NowMs);
                    return sessionA.World.LiveCount == 2 &&
                        sessionB.World.LiveCount == 2 &&
                        sessionA.Receiver.State == ReplicationReceiverState.Streaming &&
                        sessionB.Receiver.State == ReplicationReceiverState.Streaming;
                },
                BaselineTimeoutSeconds,
                "both clients must install the initial baseline keyframe");

            var entityA = FindOwnedUnit(sessionA.World, playerA);
            Assert.That(entityA.IsValid, Is.True,
                "the baseline must describe the unit owned by the first client");
            Assert.That(sessionA.World.TryGet(entityA, out var start), Is.True);

            var acknowledgements = new List<CommandAckPayload>();
            sessionA.CommandChannel.CommandResultReceived += acknowledgements.Add;

            var submit = sessionA.CommandChannel.TrySubmitMove(
                new CommandHeader(
                    playerA, 1, _server.Host.CurrentTick + 1, GameCommandType.Move),
                new[] { entityA },
                new WorldPointMm(start.Position.X + 5000, start.Position.Z),
                new FormationSpec(1, 2000, CardinalFacing.North));
            Assert.That(submit, Is.EqualTo(MatchCommandRejection.None),
                "the move must pass the client's local pre-flight");

            yield return WaitFor(
                () =>
                {
                    sessionA.Pump(sessionA.Clock.NowMs);
                    sessionB.Pump(sessionB.Clock.NowMs);
                    return acknowledgements.Count > 0;
                },
                DeliveryTimeoutSeconds,
                "the dedicated server must acknowledge the move over UDP");

            Assert.That(acknowledgements[0].IsAccepted, Is.True,
                $"the authoritative move was rejected: {acknowledgements[0].Session}/" +
                $"{acknowledgements[0].Command}");

            // Three authoritative ticks are the delta the clients must see.
            for (var index = 0; index < 3; index++)
            {
                _server.Host.TickOnce();
            }

            var targetTick = _server.Host.CurrentTick;

            yield return WaitFor(
                () =>
                {
                    sessionA.Pump(sessionA.Clock.NowMs);
                    sessionB.Pump(sessionB.Clock.NowMs);
                    return sessionA.Receiver.LastAppliedTick >= targetTick &&
                        sessionB.Receiver.LastAppliedTick >= targetTick &&
                        sessionA.World.TryGet(entityA, out var movedA) &&
                        movedA.Position != start.Position &&
                        sessionB.World.TryGet(entityA, out var movedB) &&
                        movedB.Position != start.Position;
                },
                DeliveryTimeoutSeconds,
                "both clients must receive the delta carrying the move");

            Assert.That(_server.Host.Server.TryGetUnit(entityA, out var serverUnit), Is.True);
            Assert.That(sessionA.World.TryGet(entityA, out var clientA), Is.True);
            Assert.That(sessionB.World.TryGet(entityA, out var clientB), Is.True);

            Assert.That(clientA.Position, Is.EqualTo(serverUnit.Position),
                "client 1 must agree with authority");
            Assert.That(clientB.Position, Is.EqualTo(serverUnit.Position),
                "client 2 must agree with authority");
            Assert.That(clientA.Position, Is.Not.EqualTo(start.Position),
                "the move must have changed the replicated position");
        }

        private static IEnumerator WaitFor(
            Func<bool> step,
            float timeoutSeconds,
            string failureMessage)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!step())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Assert.Fail($"{failureMessage} (waited {timeoutSeconds:F0} s)");
                }

                yield return null;
            }
        }

        private static EntityId FindOwnedUnit(ClientReplicationWorld world, PlayerId owner)
        {
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (world.TryGetSlotState(slot, out var state) && state.Owner == owner)
                {
                    return state.Entity;
                }
            }

            return default;
        }
    }
}
