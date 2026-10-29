using System;
using GlobalFront.Client;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.DedicatedServer;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// OD-35 / OD-37: the dedicated runner drives the exact same
    /// <c>ServerHost</c> as the client host, over an injected carrier and a
    /// virtual clock. No socket is bound and no wall time is read.
    /// </summary>
    [TestFixture]
    public sealed class DedicatedServerRunnerTests
    {
        private GameObject _runnerObject;
        private DedicatedServerRunner _runner;
        private FakeCarrier _carrier;
        private VirtualTransportClock _clock;

        [SetUp]
        public void SetUp()
        {
            _clock = new VirtualTransportClock();
            _carrier = new FakeCarrier(_clock);
            _runnerObject = new GameObject("dedicated-server-runner-tests");
            _runner = _runnerObject.AddComponent<DedicatedServerRunner>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_runnerObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_runnerObject);
                _runnerObject = null;
            }
        }

        [Test]
        public void Initialize_StartsTransportRealTimeAndAnAutoJoinMatch()
        {
            _runner.Initialize(_carrier, _clock);

            Assert.That(_runner.IsRunning, Is.True);
            Assert.That(_carrier.Started, Is.True);
            Assert.That(_runner.Host, Is.Not.Null);
            Assert.That(_runner.Host.TickDriverMode, Is.EqualTo(TickDriverMode.RealTime));
            Assert.That(_runner.Match.IsValid, Is.True);
            Assert.That(_runner.Host.CurrentTick, Is.Zero);
        }

        [Test]
        public void Update_PumpsTransportBeforeAdvancingTicks()
        {
            _runner.Initialize(_carrier, _clock);
            _clock.Advance(250);

            _runner.Update();

            Assert.That(_carrier.PumpCalls, Is.EqualTo(1),
                "Update must drain the carrier once per frame");
            Assert.That(_carrier.LastPumpNowMs, Is.EqualTo(250L));
            Assert.That(_runner.Host.CurrentTick, Is.EqualTo(4UL),
                "250 ms at 20 Hz is five ticks, but the bound is four per frame");

            // No clock advance: the queued fifth tick drains on the next frame.
            _runner.Update();
            Assert.That(_carrier.PumpCalls, Is.EqualTo(2));
            Assert.That(_runner.Host.CurrentTick, Is.EqualTo(5UL));
        }

        [Test]
        public void Update_WhilePaused_StillDrainsSocketsWithoutTicking()
        {
            _runner.Initialize(_carrier, _clock);
            _runner.Host.Pause();
            _clock.Advance(200);

            _runner.Update();

            Assert.That(_runner.Host.CurrentTick, Is.Zero,
                "tactical pause must stop live ticks");
            Assert.That(_carrier.PumpCalls, Is.EqualTo(1),
                "sockets must keep draining during tactical pause so grace and slices advance");
        }

        [Test]
        public void Shutdown_StopsTickingAndClosesTheCarrier()
        {
            _runner.Initialize(_carrier, _clock);
            _runner.Shutdown();

            Assert.That(_runner.IsRunning, Is.False);
            Assert.That(_runner.Host, Is.Null);
            Assert.That(_carrier.ShutDown, Is.True);
            Assert.That(
                _carrier.ShutdownReason,
                Is.EqualTo(TransportDisconnectReason.ServerShutdown));

            var pumpsAfterShutdown = _carrier.PumpCalls;
            _clock.Advance(200);
            _runner.Update();

            Assert.That(_carrier.PumpCalls, Is.EqualTo(pumpsAfterShutdown),
                "a stopped runner must ignore frames");
            Assert.DoesNotThrow(() => _runner.Shutdown(), "shutdown must be idempotent");
        }

        [Test]
        public void Initialize_Twice_IsRejected()
        {
            _runner.Initialize(_carrier, _clock);

            Assert.Throws<InvalidOperationException>(
                () => _runner.Initialize(_carrier, _clock));
        }

        [Test]
        public void Initialize_WithInvalidTickRate_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _runner.Initialize(_carrier, _clock, tickRate: 0));
            Assert.That(_runner.IsRunning, Is.False);
            Assert.That(_carrier.Started, Is.False, "validation must happen before binding the carrier");
        }

        [Test]
        public void TwoClients_AutoStartOneVersusOne_WithSpawnedArmies()
        {
            var savedTargetFrameRate = Application.targetFrameRate;
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var serverCarrier = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var runnerObject = new GameObject("DedicatedServerRunner_AutoStart");
            DedicatedServerRunner runner = null;
            RemoteSliceSession first = null;
            RemoteSliceSession second = null;

            try
            {
                runner = runnerObject.AddComponent<DedicatedServerRunner>();
                runner.Initialize(serverCarrier, clock);
                Assert.That(runner.IsMatchActive, Is.False,
                    "a dedicated server must not start a 1v1 before anyone joins");

                first = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);
                second = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);

                first.Connect("virtual", 0);
                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => first.IsConnected,
                    "the first client must complete the handshake",
                    first);
                Assert.That(runner.JoinedPlayerCount, Is.EqualTo(1));
                Assert.That(
                    runner.IsMatchActive, Is.False,
                    "one player cannot cover the two-army 1v1 roster");
                Assert.That(runner.Host.UnitCount, Is.Zero);

                second.Connect("virtual", 0);
                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => second.IsConnected && runner.IsMatchActive,
                    "the second handshake must start the match automatically",
                    first,
                    second);

                Assert.That(
                    runner.Host.ActiveSessionMatch, Is.EqualTo(runner.Match),
                    "the forming match must transition to the running match");
                Assert.That(runner.JoinedPlayerCount, Is.EqualTo(2));
                Assert.That(
                    runner.Host.UnitCount,
                    Is.EqualTo(DedicatedServerRunner.UnitsPerArmy * 2),
                    "both armies must spawn from the server-owned roster");

                var snapshots = new ServerUnitSnapshot[SimulationConstants.MaxUnitsPerMatch];
                var count = runner.Host.CopySnapshots(snapshots);
                Assert.That(count, Is.EqualTo(DedicatedServerRunner.UnitsPerArmy * 2));
                var owners = new System.Collections.Generic.HashSet<ulong>();
                for (var index = 0; index < count; index++)
                {
                    owners.Add(snapshots[index].Owner.Value);
                }

                Assert.That(
                    owners,
                    Is.EquivalentTo(new[]
                    {
                        first.Endpoint.Player.Value,
                        second.Endpoint.Player.Value
                    }),
                    "the roster must cover exactly the two attached players");

                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => first.World.LiveCount == DedicatedServerRunner.UnitsPerArmy &&
                        second.World.LiveCount == DedicatedServerRunner.UnitsPerArmy &&
                        first.Receiver.IsWorldUsable &&
                        second.Receiver.IsWorldUsable,
                    "both clients must install the filtered baseline of their own army",
                    first,
                    second);

                Assert.That(
                    first.Receiver.ChecksumMismatchCount, Is.Zero);
                Assert.That(
                    second.Receiver.ChecksumMismatchCount, Is.Zero);
            }
            finally
            {
                first?.Dispose();
                second?.Dispose();
                runner?.Shutdown();
                if (runnerObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(runnerObject);
                }

                Application.targetFrameRate = savedTargetFrameRate;
            }
        }

        /// <summary>
        /// M1.1 F1: the old 30-second safety deadline started a one-army match
        /// whose instant victory permanently closed the second slot. A lobby
        /// with one player must wait indefinitely and spawn nothing.
        /// </summary>
        [Test]
        public void SingleClient_WaitsIndefinitely_AndNeverStartsASoloMatch()
        {
            var savedTargetFrameRate = Application.targetFrameRate;
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var serverCarrier = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var runnerObject = new GameObject("DedicatedServerRunner_NoSoloStart");
            DedicatedServerRunner runner = null;
            RemoteSliceSession first = null;
            RemoteSliceSession second = null;

            try
            {
                runner = runnerObject.AddComponent<DedicatedServerRunner>();
                runner.Initialize(serverCarrier, clock);
                first = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);

                first.Connect("virtual", 0);
                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => first.IsConnected,
                    "the first client must complete the handshake",
                    first);
                Assert.That(runner.JoinedPlayerCount, Is.EqualTo(1));

                // Far beyond the retired 30-second deadline.
                for (var elapsed = 0; elapsed < 60_000; elapsed += 100)
                {
                    pipe.Advance(100);
                    clock.Advance(100);
                    runner.Update();
                    first.Pump(clock.NowMs);
                }

                Assert.That(runner.JoinedPlayerCount, Is.EqualTo(1));
                Assert.That(runner.IsMatchActive, Is.False,
                    "a 1v1 lobby must never start on a solo player");
                Assert.That(runner.Host.UnitCount, Is.Zero,
                    "no army may spawn before the second player arrives");
                Assert.That(runner.Host.Server.Outcome.IsTerminal, Is.False,
                    "a solo lobby must not declare an instant victory");

                second = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);
                second.Connect("virtual", 0);
                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => second.IsConnected && runner.IsMatchActive,
                    "the delayed second handshake must still start the 1v1",
                    first,
                    second);

                Assert.That(
                    runner.Host.UnitCount,
                    Is.EqualTo(DedicatedServerRunner.UnitsPerArmy * 2),
                    "both armies must spawn once the second player finally joins");
            }
            finally
            {
                first?.Dispose();
                second?.Dispose();
                runner?.Shutdown();
                if (runnerObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(runnerObject);
                }

                Application.targetFrameRate = savedTargetFrameRate;
            }
        }

        /// <summary>
        /// M1.1 F2: a player who disconnects before the match starts must be
        /// removed from the lobby and release the player slot. Otherwise the
        /// forming match stays full, and the next two handshakes can never
        /// start a 1v1.
        /// </summary>
        [Test]
        public void LobbyDisconnect_ReleasesTheSlot_SoTheNextTwoClientsCanStart()
        {
            var savedTargetFrameRate = Application.targetFrameRate;
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var serverCarrier = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var runnerObject = new GameObject("DedicatedServerRunner_LobbyDisconnect");
            DedicatedServerRunner runner = null;
            RemoteSliceSession first = null;
            RemoteSliceSession second = null;
            RemoteSliceSession third = null;

            try
            {
                runner = runnerObject.AddComponent<DedicatedServerRunner>();
                runner.Initialize(serverCarrier, clock);
                first = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);

                first.Connect("virtual", 0);
                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => first.IsConnected,
                    "the first client must complete the handshake",
                    first);
                Assert.That(runner.JoinedPlayerCount, Is.EqualTo(1));
                Assert.That(runner.IsMatchActive, Is.False);

                first.Endpoint.GracefulDisconnect();
                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => runner.JoinedPlayerCount == 0,
                    "a player who left before the start must be removed from the lobby",
                    first);
                Assert.That(runner.IsMatchActive, Is.False);
                Assert.That(runner.Host.Sessions.TryGetMatchPhase(runner.Match, out var phase), Is.True);
                Assert.That(phase, Is.EqualTo(MatchPhase.Forming),
                    "the match must still be waiting for its 1v1 roster");

                second = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);
                second.Connect("virtual", 0);
                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => second.IsConnected,
                    "the replacement handshake must be accepted",
                    first,
                    second);
                Assert.That(runner.JoinedPlayerCount, Is.EqualTo(1));
                Assert.That(second.Endpoint.Player, Is.EqualTo(new PlayerId(2)),
                    "the retired PlayerId must never be handed out again (OD-3)");
                Assert.That(runner.IsMatchActive, Is.False,
                    "one replacement player is still not a 1v1");

                third = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);
                third.Connect("virtual", 0);
                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => third.IsConnected && runner.IsMatchActive,
                    "the second live player must start the 1v1",
                    first,
                    second,
                    third);

                Assert.That(
                    runner.Host.UnitCount,
                    Is.EqualTo(DedicatedServerRunner.UnitsPerArmy * 2));
                var snapshots = new ServerUnitSnapshot[SimulationConstants.MaxUnitsPerMatch];
                var count = runner.Host.CopySnapshots(snapshots);
                var owners = new System.Collections.Generic.HashSet<ulong>();
                for (var index = 0; index < count; index++)
                {
                    owners.Add(snapshots[index].Owner.Value);
                }

                Assert.That(
                    owners,
                    Is.EquivalentTo(new[]
                    {
                        second.Endpoint.Player.Value,
                        third.Endpoint.Player.Value
                    }),
                    "the roster must cover the two live players, not the abandoned slot");
            }
            finally
            {
                first?.Dispose();
                second?.Dispose();
                third?.Dispose();
                runner?.Shutdown();
                if (runnerObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(runnerObject);
                }

                Application.targetFrameRate = savedTargetFrameRate;
            }
        }

        /// <summary>
        /// M1.1 F4: the disconnect stamp must use the continuous session
        /// clock. Stamping the frozen simulation tick made a break deep inside
        /// a tactical pause consume the pause ticks that already elapsed, so
        /// the client got seconds of grace instead of the full 200 s window.
        /// </summary>
        [Test]
        public void RepeatedDisconnectInsidePause_GetsTheFullGraceWindow()
        {
            var savedTargetFrameRate = Application.targetFrameRate;
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var serverCarrier = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var runnerObject = new GameObject("DedicatedServerRunner_PausedGrace");
            DedicatedServerRunner runner = null;
            RemoteSliceSession first = null;
            RemoteSliceSession second = null;

            try
            {
                runner = runnerObject.AddComponent<DedicatedServerRunner>();
                runner.Initialize(serverCarrier, clock);
                first = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);
                second = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);

                first.Connect("virtual", 0);
                PumpUntil(runner, pipe, clock, () => first.IsConnected,
                    "the first client must complete the handshake", first);
                second.Connect("virtual", 0);
                PumpUntil(runner, pipe, clock, () => second.IsConnected && runner.IsMatchActive,
                    "the second handshake must start the 1v1", first, second);

                var frozenTick = runner.Host.CurrentTick;
                runner.Host.Pause();
                runner.Host.AdvancePauseTicks(3900);
                var expectedStamp = runner.Host.SessionGraceClock;
                Assert.That(expectedStamp, Is.GreaterThan(frozenTick),
                    "the continuous clock must include the pause ticks");

                var session = first.Endpoint.Session;
                Assert.That(
                    runner.TransportHost.Binder.TryGetTokenBySession(session, out var token),
                    Is.True);
                Assert.That(runner.TransportHost.Binder.TryGetByToken(token, out var binding), Is.True);
                first.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);
                runner.Carrier.CloseConnection(
                    binding.ConnectionId, TransportDisconnectReason.TransportLost);

                // A frame that does not advance virtual time drains the loss
                // without adding pause ticks, so the stamp is exact.
                runner.Update();

                Assert.That(runner.Host.TryGetSession(session, out var lost), Is.True);
                Assert.That(lost.State, Is.EqualTo(SessionState.Disconnected));
                Assert.That(lost.DisconnectedAtTick, Is.EqualTo(expectedStamp),
                    "the stamp must be the continuous clock, not the frozen simulation tick");

                runner.Host.AdvancePauseTicks(200);
                Assert.That(runner.Host.TryGetSession(session, out var stillDisconnected), Is.True);
                Assert.That(
                    stillDisconnected.State, Is.EqualTo(SessionState.Disconnected),
                    "a break at pause tick 3900 must still receive the full 4000-tick window; " +
                    "the old frozen-tick stamp expired it after ~100 dropped ticks");
            }
            finally
            {
                first?.Dispose();
                second?.Dispose();
                runner?.Shutdown();
                if (runnerObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(runnerObject);
                }

                Application.targetFrameRate = savedTargetFrameRate;
            }
        }

        /// <summary>
        /// M1.1 F5: the terminal outcome must reach both connected clients as
        /// a MatchFinished status, terminate their reconnect FSM and detach
        /// their sockets.
        /// </summary>
        [Test]
        public void MatchFinished_SendsTerminalStatus_AndBothClientsTerminate()
        {
            var savedTargetFrameRate = Application.targetFrameRate;
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var serverCarrier = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var runnerObject = new GameObject("DedicatedServerRunner_MatchFinished");
            DedicatedServerRunner runner = null;
            RemoteSliceSession first = null;
            RemoteSliceSession second = null;

            try
            {
                runner = runnerObject.AddComponent<DedicatedServerRunner>();
                runner.Initialize(serverCarrier, clock);
                first = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);
                second = new RemoteSliceSession(
                    OwnDatagramCarrier.CreateClient(
                        pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);

                first.Connect("virtual", 0);
                PumpUntil(runner, pipe, clock, () => first.IsConnected,
                    "the first client must complete the handshake", first);
                second.Connect("virtual", 0);
                PumpUntil(runner, pipe, clock, () => second.IsConnected && runner.IsMatchActive,
                    "the second handshake must start the 1v1", first, second);

                TransportDisconnectReason? firstReason = null;
                TransportDisconnectReason? secondReason = null;
                first.Disconnected += reason => firstReason = reason;
                second.Disconnected += reason => secondReason = reason;

                // A one-shot killer next to the first army decides the match
                // through the authoritative combat path.
                runner.Host.Server.SpawnUnit(
                    second.Endpoint.Player,
                    new CombatStats(
                        maximumHealth: 100_000,
                        damage: 1_000,
                        rangeMm: 12_000,
                        cooldownTicks: 1),
                    new WorldPointMm(-55_000, 0),
                    speedMmPerTick: 1,
                    autoAcquire: true);

                PumpUntil(
                    runner,
                    pipe,
                    clock,
                    () => runner.Host.Server.Outcome.IsTerminal &&
                        first.ReconnectCoordinator.State == ClientReconnectState.Terminated &&
                        second.ReconnectCoordinator.State == ClientReconnectState.Terminated,
                    "the match outcome must terminate both clients",
                    first,
                    second);

                Assert.That(first.ReconnectCoordinator.State,
                    Is.EqualTo(ClientReconnectState.Terminated));
                Assert.That(second.ReconnectCoordinator.State,
                    Is.EqualTo(ClientReconnectState.Terminated));
                Assert.That(firstReason, Is.EqualTo(TransportDisconnectReason.MatchFinished));
                Assert.That(secondReason, Is.EqualTo(TransportDisconnectReason.MatchFinished));
                Assert.That(first.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected));
                Assert.That(second.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected));
                Assert.That(runner.TransportHost.Binder.BoundCount, Is.Zero,
                    "both sessions must be detached from their sockets");

                // Further frames must not resurrect the finished session.
                var attempts = first.ReconnectCoordinator.ReconnectAttemptCount;
                for (var frame = 0; frame < 5; frame++)
                {
                    pipe.Advance(500);
                    clock.Advance(500);
                    runner.Update();
                    first.Pump(clock.NowMs);
                }

                Assert.That(first.ReconnectCoordinator.State,
                    Is.EqualTo(ClientReconnectState.Terminated));
                Assert.That(first.ReconnectCoordinator.ReconnectAttemptCount,
                    Is.EqualTo(attempts),
                    "a terminated client must never arm another reconnect attempt");
            }
            finally
            {
                first?.Dispose();
                second?.Dispose();
                runner?.Shutdown();
                if (runnerObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(runnerObject);
                }

                Application.targetFrameRate = savedTargetFrameRate;
            }
        }

        [Test]
        public void Bootstrap_ParsesPortAndTickRateArguments()
        {
            Assert.That(
                DedicatedServerBootstrap.ParsePort(new[] { "-port", "9001" }),
                Is.EqualTo(9001));
            Assert.That(
                DedicatedServerBootstrap.ParsePort(new[] { "-port=9002" }),
                Is.EqualTo(9002));
            Assert.That(
                DedicatedServerBootstrap.ParsePort(new[] { "-port", "0" }),
                Is.EqualTo(DedicatedServerBootstrap.DefaultPort));
            Assert.That(
                DedicatedServerBootstrap.ParsePort(new[] { "-port", "not-a-number" }),
                Is.EqualTo(DedicatedServerBootstrap.DefaultPort));

            Assert.That(
                DedicatedServerBootstrap.ParseTickRate(new[] { "-tickrate", "30" }),
                Is.EqualTo(30));
            Assert.That(
                DedicatedServerBootstrap.ParseTickRate(Array.Empty<string>()),
                Is.EqualTo(20));

            Assert.That(
                DedicatedServerBootstrap.IsDedicatedServerRequested(
                    new[] { "-dedicatedServer" }),
                Is.True);
            Assert.That(
                DedicatedServerBootstrap.IsDedicatedServerRequested(Array.Empty<string>()),
                Is.False);
        }

        private static void PumpUntil(
            DedicatedServerRunner runner,
            VirtualNetworkPipe pipe,
            VirtualTransportClock clock,
            Func<bool> condition,
            string message,
            params RemoteSliceSession[] sessions)
        {
            for (var elapsed = 0; elapsed < 15_000 && !condition(); elapsed += 10)
            {
                // The virtual pipe owns its own delivery clock: advancing the
                // transport clock alone leaves every datagram in flight.
                pipe.Advance(10);
                clock.Advance(10);
                runner.Update();
                for (var index = 0; index < sessions.Length; index++)
                {
                    sessions[index].Pump(clock.NowMs);
                }
            }

            Assert.That(condition(), Is.True, () =>
            {
                var builder = new System.Text.StringBuilder(message);
                builder.Append("\nrunner: active=").Append(runner.IsMatchActive)
                    .Append(" joined=").Append(runner.JoinedPlayerCount)
                    .Append(" units=").Append(runner.Host?.UnitCount ?? -1)
                    .Append(" tick=").Append(runner.Host?.CurrentTick ?? 0);
                for (var index = 0; index < sessions.Length; index++)
                {
                    var session = sessions[index];
                    builder.Append("\nsession").Append(index)
                        .Append(": endpoint=").Append(session.Endpoint.State)
                        .Append(" connected=").Append(session.IsConnected)
                        .Append(" player=").Append(session.Endpoint.Player.Value)
                        .Append(" live=").Append(session.World.LiveCount)
                        .Append(" receiver=").Append(session.Receiver.State)
                        .Append(" carrier=").Append(session.Carrier.Metrics.DatagramsSent)
                        .Append('/').Append(session.Carrier.Metrics.DatagramsReceived);
                }

                return builder.ToString();
            });
        }

        private sealed class FakeCarrier : INetworkCarrier
        {
            public FakeCarrier(ITransportClock clock)
            {
                Clock = clock;
            }

            public bool Started { get; private set; }

            public bool ShutDown { get; private set; }

            public TransportDisconnectReason ShutdownReason { get; private set; }

            public int PumpCalls { get; private set; }

            public long LastPumpNowMs { get; private set; }

            public TransportMetrics Metrics { get; } = new TransportMetrics();

            public ITransportClock Clock { get; }

            public int EventQueueDepth => 0;

            public void StartServer() => Started = true;

            public int ConnectToServer(string host, int port) => 0;

            public bool Send(
                int connectionId,
                TransportChannel channel,
                byte[] data,
                int offset,
                int length) => true;

            public void CloseConnection(int connectionId, TransportDisconnectReason reason)
            {
            }

            public void AssignSessionToken(int connectionId, ulong token)
            {
            }

            public void Shutdown(TransportDisconnectReason reason)
            {
                ShutDown = true;
                ShutdownReason = reason;
            }

            public void Pump(long nowMs)
            {
                PumpCalls++;
                LastPumpNowMs = nowMs;
            }

            public bool TryDequeueEvent(out TransportEvent transportEvent)
            {
                transportEvent = default;
                return false;
            }

            public void Dispose()
            {
            }
        }
    }
}
