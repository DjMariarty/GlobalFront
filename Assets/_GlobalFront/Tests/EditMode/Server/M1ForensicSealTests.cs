#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Server
{
    /// <summary>
    /// M1.1 forensic-seal witnesses: terminal session lifecycle across the
    /// engine-neutral host and the transport (ready deadline, match finished,
    /// server shutdown) plus the carrier teardown accounting for fragment
    /// assemblies. Each test drives the production objects; the transport
    /// witnesses use the virtual pipe, the host witnesses use a probe
    /// replication transport that can raise the reattach signal.
    /// </summary>
    [TestFixture]
    public sealed class M1ForensicSealTests
    {
        private static readonly PlayerId Player1 = new PlayerId(1);
        private static readonly PlayerId Player2 = new PlayerId(2);

        private static readonly CombatStats StandardStats =
            new CombatStats(maximumHealth: 100, damage: 10, rangeMm: 7_000, cooldownTicks: 10);

        /// <summary>
        /// M1.1 F3: a reattached session that misses its ready deadline is
        /// closed by the host, and the hosting layer is told to detach the
        /// socket instead of leaving the client in Resyncing forever.
        /// </summary>
        [Test]
        public void ReadyDeadline_ClosesTheSession_AndRaisesReadyTimeoutTermination()
        {
            var host = new ServerHost();
            var match = host.CreateSessionMatch(2);
            var session1 = host.CreateSession(host.CreateConnectionHandle());
            var session2 = host.CreateSession(host.CreateConnectionHandle());
            Assert.That(host.TryJoinMatch(session1, match, out var player1), Is.EqualTo(JoinResult.Assigned));
            Assert.That(host.TryJoinMatch(session2, match, out var player2), Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                host.TryStartSessionMatch(match, TwoPlayerConfig(player1, player2), out _),
                Is.True);

            var probe = new ProbeReplicationTransport();
            host.AttachReplication(probe, ServerReplicationEmitterConfig.Default);

            var terminated = new List<(SessionId Session, TransportDisconnectReason Reason)>();
            host.SessionTerminated += (session, reason) => terminated.Add((session, reason));

            host.Pause();
            probe.RaiseReattached(session1, match, player1);
            host.AdvancePauseTicks(ServerHost.DefaultResumeReadyTimeoutTicks - 1);
            Assert.That(terminated, Is.Empty,
                "one tick before the deadline the session must still be alive");

            host.AdvancePauseTicks(1);
            Assert.That(terminated, Has.Count.EqualTo(1),
                "the ready deadline must terminate exactly the overdue session");
            Assert.That(terminated[0].Session, Is.EqualTo(session1));
            Assert.That(terminated[0].Reason, Is.EqualTo(TransportDisconnectReason.ReadyTimeout));

            Assert.That(host.TryGetSession(session1, out var closed), Is.True);
            Assert.That(closed.State, Is.EqualTo(SessionState.Closed));
            Assert.That(host.TryGetSession(session2, out var alive), Is.True);
            Assert.That(alive.State, Is.EqualTo(SessionState.Connected),
                "the session that signalled readiness must keep its connection");
        }

        /// <summary>
        /// M1.1 F3 (transport half): detaching sends the terminal status and
        /// releases the binding, so the client sees Disconnected + the reason
        /// and the server stops routing to a dead session.
        /// </summary>
        [Test]
        public void DetachSession_SendsTerminalStatus_AndReleasesTheSocket()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(
                TransportTestSupport.PumpUntilEstablished(rig, client),
                Is.True,
                "the client must complete the handshake");

            TransportDisconnectReason? terminal = null;
            client.Terminated += reason => terminal = reason;

            Assert.That(
                rig.ServerTransport.DetachSession(client.Session, TransportDisconnectReason.ReadyTimeout),
                Is.True);
            Assert.That(rig.ServerTransport.Binder.BoundCount, Is.Zero,
                "the binding must be released before the socket is closed");

            rig.Pipe.Advance(10);
            rig.Clock.Advance(10);
            client.Pump(rig.Clock.NowMs);

            Assert.That(terminal, Is.EqualTo(TransportDisconnectReason.ReadyTimeout),
                "the client must receive the terminal reason, not a silent loss");
            Assert.That(client.State, Is.EqualTo(ClientTransportState.Disconnected));
        }

        /// <summary>
        /// M1.1 F6: a server shutting down is terminal. The client must not
        /// treat the notice as packet loss and start the reconnect policy.
        /// </summary>
        [Test]
        public void ServerShutdown_DeliversTerminalStatus_AndTheClientTerminates()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            TransportDisconnectReason? terminal = null;
            client.Terminated += reason => terminal = reason;

            rig.ServerTransport.Shutdown();
            rig.Pipe.Advance(10);
            rig.Clock.Advance(10);
            client.Pump(rig.Clock.NowMs);

            Assert.That(terminal, Is.EqualTo(TransportDisconnectReason.ServerShutdown));
            Assert.That(client.State, Is.EqualTo(ClientTransportState.Disconnected));
        }

        /// <summary>
        /// M1.1 P2-5: closing a connection with a partially assembled
        /// fragmented message must return its rented slices to the pool, not
        /// leak them until process exit.
        /// </summary>
        [Test]
        public void ConnectionClose_ReturnsInProgressAssemblySlicesToThePool()
        {
            var baseline = TransportBufferPool.OutstandingCount;
            var profile = new ImpairmentProfile { LossProbability = 0.5, Seed = 7 };
            var rig = TransportTestSupport.CreateRig(profile);
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            Assert.That(
                rig.ServerTransport.Binder.TryGetTokenBySession(client.Session, out var token),
                Is.True);
            Assert.That(rig.ServerTransport.Binder.TryGetByToken(token, out var binding), Is.True);

            // Seven fragments with half of them lost: at least one assembly
            // group must stay incomplete on the client side.
            var payload = new byte[TransportProtocol.FragmentPayloadBytes * 7];
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)(index & 0xFF);
            }

            Assert.That(
                rig.ServerCarrier.Send(
                    binding.ConnectionId,
                    TransportChannel.Snapshot,
                    payload,
                    0,
                    payload.Length),
                Is.True);
            for (var pump = 0; pump < 10 && TransportBufferPool.OutstandingCount == baseline; pump++)
            {
                rig.Pipe.Advance(5);
                rig.Clock.Advance(5);
                client.Pump(rig.Clock.NowMs);
            }

            Assert.That(
                TransportBufferPool.OutstandingCount, Is.GreaterThan(baseline),
                "the fixture needs a partially assembled group holding pooled slices");

            client.DropConnection(TransportDisconnectReason.TransportLost);
            rig.Pipe.Advance(5);
            rig.Clock.Advance(5);
            client.Pump(rig.Clock.NowMs);

            Assert.That(
                TransportBufferPool.OutstandingCount, Is.EqualTo(baseline),
                "the closed connection must return every leased fragment slice");
        }

        /// <summary>
        /// M1.1 P2-5 (shutdown half): the carrier-wide teardown is also a
        /// connection-cleanup path and must release in-progress assemblies.
        /// </summary>
        [Test]
        public void CarrierShutdown_ReturnsInProgressAssemblySlicesToThePool()
        {
            var baseline = TransportBufferPool.OutstandingCount;
            var profile = new ImpairmentProfile { LossProbability = 0.5, Seed = 11 };
            var rig = TransportTestSupport.CreateRig(profile);
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);
            Assert.That(
                rig.ServerTransport.Binder.TryGetTokenBySession(client.Session, out var token),
                Is.True);
            Assert.That(rig.ServerTransport.Binder.TryGetByToken(token, out var binding), Is.True);

            var payload = new byte[TransportProtocol.FragmentPayloadBytes * 7];
            Assert.That(
                rig.ServerCarrier.Send(
                    binding.ConnectionId,
                    TransportChannel.Snapshot,
                    payload,
                    0,
                    payload.Length),
                Is.True);
            for (var pump = 0; pump < 10 && TransportBufferPool.OutstandingCount == baseline; pump++)
            {
                rig.Pipe.Advance(5);
                rig.Clock.Advance(5);
                client.Pump(rig.Clock.NowMs);
            }

            Assert.That(
                TransportBufferPool.OutstandingCount, Is.GreaterThan(baseline),
                "the fixture needs a partially assembled group holding pooled slices");

            client.Carrier.Shutdown(TransportDisconnectReason.TransportLost);
            Assert.That(
                TransportBufferPool.OutstandingCount, Is.EqualTo(baseline),
                "carrier shutdown must not strand assembly slices");
        }

        /// <summary>
        /// M1.1 F5 (host half): the terminal outcome retires the match and
        /// names every participant so the hosting layer can deliver the
        /// MatchFinished status to each of them.
        /// </summary>
        [Test]
        public void MatchFinished_NotifiesEverySession_WithMatchFinished()
        {
            var host = new ServerHost();
            var match = host.CreateSessionMatch(2);
            var session1 = host.CreateSession(host.CreateConnectionHandle());
            var session2 = host.CreateSession(host.CreateConnectionHandle());
            Assert.That(host.TryJoinMatch(session1, match, out var player1), Is.EqualTo(JoinResult.Assigned));
            Assert.That(host.TryJoinMatch(session2, match, out var player2), Is.EqualTo(JoinResult.Assigned));
            Assert.That(
                host.TryStartSessionMatch(match, TwoPlayerConfig(player1, player2), out _),
                Is.True);

            var terminated = new List<(SessionId Session, TransportDisconnectReason Reason)>();
            host.SessionTerminated += (session, reason) => terminated.Add((session, reason));

            // A one-shot killer next to the enemy army ends the match in a
            // few authoritative ticks; no test-only despawn is involved.
            host.Server.SpawnUnit(
                player2,
                new CombatStats(maximumHealth: 100_000, damage: 1_000, rangeMm: 12_000, cooldownTicks: 1),
                new WorldPointMm(5_000, 0),
                speedMmPerTick: 1,
                autoAcquire: true);

            for (var tick = 0; tick < 50 && !host.Server.Outcome.IsTerminal; tick++)
            {
                host.TickOnce();
            }

            Assert.That(host.Server.Outcome.IsTerminal, Is.True,
                "the killer must eliminate the first army");
            Assert.That(terminated, Has.Count.EqualTo(2),
                "both participants must receive the terminal status once");
            Assert.That(terminated[0].Reason, Is.EqualTo(TransportDisconnectReason.MatchFinished));
            Assert.That(terminated[1].Reason, Is.EqualTo(TransportDisconnectReason.MatchFinished));
            Assert.That(
                new[] { terminated[0].Session, terminated[1].Session },
                Is.EquivalentTo(new[] { session1, session2 }));
            Assert.That(host.Sessions.TryGetMatchPhase(match, out var phase), Is.True);
            Assert.That(phase, Is.EqualTo(MatchPhase.Finished));
        }

        private static MatchConfig TwoPlayerConfig(PlayerId player1, PlayerId player2) =>
            new MatchConfig(
                StandardStats,
                new[]
                {
                    new UnitSpawnSpec(player1, new WorldPointMm(0, 0), 350, false),
                    new UnitSpawnSpec(player2, new WorldPointMm(60_000, 0), 350, false)
                });

        private sealed class ProbeReplicationTransport : IReplicationTransport
        {
            public bool SendToSession(SessionId session, ulong envelopeTick, byte[] buffer, int length) =>
                true;

            public event Action<SessionId, MatchId, PlayerId> SessionAttached;
            public event Action<SessionId, MatchId, PlayerId> SessionReattached;
            public event Action<SessionId> PostSessionReattached;
            public event Action<SessionId, TransportDisconnectReason> SessionDetached;
            public event Action<ReplicationFeedbackMessage> FeedbackReceived;

            public void RaiseReattached(SessionId session, MatchId match, PlayerId player) =>
                SessionReattached?.Invoke(session, match, player);
        }
    }
}
