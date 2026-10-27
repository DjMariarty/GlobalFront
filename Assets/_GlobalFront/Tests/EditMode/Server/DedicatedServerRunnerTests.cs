using System;
using GlobalFront.Client;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;
using GlobalFront.DedicatedServer;
using GlobalFront.Server;
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
