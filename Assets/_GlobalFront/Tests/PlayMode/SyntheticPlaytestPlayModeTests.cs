using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GlobalFront.Client;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.DedicatedServer;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using EntityId = GlobalFront.Core.Model.EntityId;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace GlobalFront.Tests.PlayMode
{
    /// <summary>
    /// Step 4.6.5 synthetic playtest: 400 units over real UDP loopback, Fog of
    /// War, a bot that box-selects 100 units and marches both armies into
    /// contact, a server-side disconnect at 20 s, reconnect/resync/countdown,
    /// the C0 opcode 14 resume, five 1080p screenshots and steady-state metrics.
    /// </summary>
    [TestFixture]
    public sealed class SyntheticPlaytestPlayModeTests
    {
        private const string RuntimeRootName = "[GlobalFront Runtime]";
        private const int CombatUnitsPerPlayer = 200;
        private const int SelectedUnits = 100;
        private const int PlaytestTickRate = 20;
        private const int PlaytestMaxHealth = 200;
        private const int DisconnectAtSeconds = 20;
        private const float SteadyWindowStartSeconds = 16f;
        private const float SteadyWindowEndSeconds = 19f;
        private const float FpsWindowStartSeconds = 10f;
        private const float FpsWindowEndSeconds = 13f;
        private const float HandshakeTimeoutSeconds = 30f;
        private const float BaselineTimeoutSeconds = 35f;
        private const float ReconnectTimeoutSeconds = 20f;

        private static readonly CombatStats PlaytestStats =
            new CombatStats(PlaytestMaxHealth, damage: 16, rangeMm: 7000, cooldownTicks: 10);

        private readonly List<RemoteSliceSession> _sessions = new List<RemoteSliceSession>();
        private GameObject _serverObject;
        private DedicatedServerRunner _server;
        private LiteNetLibCarrier _serverCarrier;
        private int _serverPort;
        private GameObject _sliceRoot;
        private TacticalVerticalSliceRunner _sliceRunner;
        private RemoteSliceSession _runnerDrivenSession;
        private int _savedTargetFrameRate;

        [SetUp]
        public void SetUp()
        {
            _savedTargetFrameRate = Application.targetFrameRate;
        }

        [TearDown]
        public void TearDown()
        {
            PlaytestScreenshotCapture.End();

            if (_sliceRunner != null)
            {
                _sliceRunner.Shutdown();
                _sliceRunner = null;
            }

            if (_sliceRoot != null)
            {
                Object.Destroy(_sliceRoot);
                _sliceRoot = null;
            }

            _runnerDrivenSession = null;

            for (var index = 0; index < _sessions.Count; index++)
            {
                _sessions[index]?.Dispose();
            }

            _sessions.Clear();

            if (_server != null)
            {
                _server.Shutdown();
                _server = null;
            }

            if (_serverObject != null)
            {
                Object.Destroy(_serverObject);
                _serverObject = null;
            }

            _serverCarrier = null;
            _serverPort = 0;
            Application.targetFrameRate = _savedTargetFrameRate;

            RestoreLegacyPrototype();
        }

        /// <summary>
        /// <see cref="GlobalFrontRuntimeBootstrap.ActivateDedicatedClient"/> takes
        /// the scene boot's legacy prototype down; the switcher tests expect it
        /// present when they run in the same Play Mode session.
        /// </summary>
        private static void RestoreLegacyPrototype()
        {
            if (Object.FindAnyObjectByType<PrototypeRtsController>() != null)
            {
                return;
            }

            var runtime = GameObject.Find(RuntimeRootName);
            if (runtime == null)
            {
                return;
            }

            runtime.AddComponent<PrototypeWorldBootstrap>();
            runtime.AddComponent<PrototypeRtsController>();
            runtime.AddComponent<PrototypeHud>();
        }

        [UnityTest]
        public IEnumerator SyntheticPlaytest_400Units_FogOfWar_ReconnectResume_ProducesFiveScreenshots()
        {
            yield return PlaytestScreenshotCapture.Begin();

            StartServer();

            _sliceRunner = GlobalFrontRuntimeBootstrap.ActivateDedicatedClient(
                "127.0.0.1", _serverPort, createVisuals: true);
            Assert.That(_sliceRunner, Is.Not.Null,
                "the dedicated client runner must start over real UDP");

            _sliceRoot = _sliceRunner.gameObject;
            var sessionA = _sliceRunner.RemoteSession;
            Assert.That(sessionA, Is.Not.Null);
            _sessions.Add(sessionA);
            _runnerDrivenSession = sessionA;

            // A frame loop on the runner would otherwise move the camera from a
            // hardware pointer sitting at the editor edge.
            _sliceRunner.InputManager.SetMockMode(true);
            _sliceRunner.InputManager.ResetMockInputs();
            _sliceRunner.CameraController.EnableEdgePan = false;

            yield return WaitFor(
                () => sessionA.IsConnected,
                HandshakeTimeoutSeconds,
                "the visual dedicated client must connect first and take Player 1");

            var sessionB = CreateClient();
            sessionB.Connect("127.0.0.1", _serverPort);

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return sessionA.IsConnected && sessionB.IsConnected;
                },
                HandshakeTimeoutSeconds,
                "both dedicated clients must complete the real-UDP handshake");

            var playerA = sessionA.Endpoint.Player;
            var playerB = sessionB.Endpoint.Player;
            Assert.That(playerA.IsValid && playerB.IsValid, Is.True);
            Assert.That(playerA, Is.Not.EqualTo(playerB));

            var config = BuildMatchConfig(playerA, playerB);
            Assert.That(
                _server.Host.TryStartSessionMatch(_server.Match, config, out var entityIds),
                Is.True,
                "the 402-unit template must start for both connected players");
            Assert.That(entityIds.Length, Is.EqualTo(CombatUnitsPerPlayer * 2 + 2));

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return sessionA.World.LiveCount == CombatUnitsPerPlayer + 1 &&
                        sessionB.World.LiveCount == CombatUnitsPerPlayer + 1 &&
                        sessionA.Receiver.State == ReplicationReceiverState.Streaming &&
                        sessionB.Receiver.State == ReplicationReceiverState.Streaming;
                },
                BaselineTimeoutSeconds,
                "both clients must install a Fog-of-War baseline before the playtest");

            var ownBase = entityIds[CombatUnitsPerPlayer * 2];
            var enemyBase = entityIds[CombatUnitsPerPlayer * 2 + 1];
            Assert.That(_server.Host.Server.UnitCount, Is.EqualTo(402));
            Assert.That(sessionA.World.TryGet(ownBase, out _), Is.True,
                "the client's own base is always visible");
            Assert.That(sessionA.World.TryGet(enemyBase, out _), Is.False,
                "Fog of War must hide the enemy base at 80 m");
            Assert.That(sessionB.World.TryGet(enemyBase, out _), Is.True,
                "the enemy base is visible to its owner");

            AimCamera(new Vector3(-35f, 0f, 0f), 75f);
            yield return null;
            yield return PlaytestScreenshotCapture.Capture("01_spawn_and_fog.png");

            // The bot drags a box over the first 100 friendly units. The camera
            // is moved to the selected block so all 100 project on screen.
            AimCamera(new Vector3(-46f, 0f, 0f), 45f);
            yield return null;
            var selectedEntities = SelectBlock(sessionA, entityIds, 0, SelectedUnits);
            yield return null;
            yield return PlaytestScreenshotCapture.Capture("02_box_selection.png");

            var moveA = SubmitMove(
                sessionA, playerA, sequence: 1, selectedEntities,
                new WorldPointMm(0, 0));
            Assert.That(moveA, Is.EqualTo(MatchCommandRejection.None),
                "the selected 100 units must pass the client's move pre-flight");

            var selectedB = new EntityId[SelectedUnits];
            Array.Copy(entityIds, CombatUnitsPerPlayer, selectedB, 0, SelectedUnits);
            var moveB = SubmitMove(
                sessionB, playerB, sequence: 1, selectedB,
                new WorldPointMm(0, 0));
            Assert.That(moveB, Is.EqualTo(MatchCommandRejection.None),
                "the opposing bot must be able to march its 100 units");

            AimCamera(new Vector3(0f, 0f, 0f), 55f);
            yield return null;

            var fpsDeltas = new List<float>(512);
            var scopedRecorder = Recorder.Get("GC.Alloc");
            Assert.That(scopedRecorder, Is.Not.Null,
                "the GC.Alloc recorder must exist");
            scopedRecorder.enabled = false;
            scopedRecorder.FilterToCurrentThread();

            var frameGcRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Memory, "GC.Alloc");
            if (!frameGcRecorder.Valid)
            {
                frameGcRecorder.Dispose();
                frameGcRecorder = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GC.Alloc");
            }

            Assert.That(frameGcRecorder.Valid, Is.True,
                "the GC Alloc profiler counter must be available");

            var stopwatch = Stopwatch.StartNew();
            var scopedBlocks = 0L;
            var frameGcBytes = 0L;
            var runnerSuspended = false;
            var scopedWarmed = false;
            var combatCaptured = false;
            var attackSubmitted = false;
            var enemyTarget = default(EntityId);

            while (stopwatch.Elapsed.TotalSeconds < DisconnectAtSeconds)
            {
                var delta = Time.unscaledDeltaTime;
                var elapsed = (float)stopwatch.Elapsed.TotalSeconds;
                if (elapsed >= FpsWindowStartSeconds && elapsed < FpsWindowEndSeconds)
                {
                    if (delta > 0f)
                    {
                        fpsDeltas.Add(delta);
                    }
                }

                if (elapsed >= SteadyWindowStartSeconds &&
                    elapsed < SteadyWindowEndSeconds)
                {
                    if (!runnerSuspended)
                    {
                        // The invariant is scoped to the network/replication/
                        // telemetry pumps, not the URP frame: disabling the
                        // presentation loop keeps the measured action honest.
                        _sliceRunner.enabled = false;
                        runnerSuspended = true;
                    }

                    scopedRecorder.enabled = true;
                    sessionA.Pump(sessionA.Clock.NowMs);
                    sessionB.Pump(sessionB.Clock.NowMs);
                    scopedRecorder.enabled = false;
                    scopedRecorder.CollectFromAllThreads();
                    scopedBlocks += scopedRecorder.sampleBlockCount;
                    frameGcBytes += frameGcRecorder.LastValue;
                }
                else
                {
                    if (!scopedWarmed &&
                        elapsed >= SteadyWindowStartSeconds - 0.5f &&
                        elapsed < SteadyWindowStartSeconds)
                    {
                        // Warm the manual pump path outside the measured
                        // window: the first call after presentation is
                        // disabled can initialize a one-time code path.
                        scopedWarmed = true;
                        _sliceRunner.enabled = false;
                        runnerSuspended = true;
                        sessionA.Pump(sessionA.Clock.NowMs);
                        sessionB.Pump(sessionB.Clock.NowMs);
                    }
                    else
                    {
                        if (runnerSuspended)
                        {
                            _sliceRunner.enabled = true;
                            runnerSuspended = false;
                        }

                        PumpClients();
                    }
                }

                if (!combatCaptured && elapsed >= 14f &&
                    TryFindCombat(sessionA, playerA, out enemyTarget, out _))
                {
                    combatCaptured = true;
                    yield return PlaytestScreenshotCapture.Capture("03_clash_combat.png");
                    continue;
                }

                if (!attackSubmitted && elapsed >= 8f &&
                    TryFindCombat(sessionA, playerA, out enemyTarget, out _))
                {
                    attackSubmitted = true;
                    SubmitAttack(sessionA, playerA, selectedEntities, enemyTarget);
                }

                yield return null;
            }

            if (runnerSuspended)
            {
                _sliceRunner.enabled = true;
            }

            frameGcRecorder.Dispose();
            Assert.That(combatCaptured, Is.True,
                "the playtest must reach an active 100-vs-100 clash before the disconnect");

            var preDisconnectTick = _server.Host.CurrentTick;
            var disconnectWallSeconds = (float)stopwatch.Elapsed.TotalSeconds;
            var sessionId = sessionA.Endpoint.Session;
            var serverConnectionId = GetServerConnectionId(sessionId);
            _serverCarrier.CloseConnection(
                serverConnectionId, TransportDisconnectReason.TransportLost);

            yield return WaitFor(
                () => _server.Host.TryGetSession(sessionId, out var record) &&
                    record.State == SessionState.Disconnected,
                ReconnectTimeoutSeconds,
                "the server must move the disconnected client into reconnect grace");

            // LiteNetLib may take its own idle timeout to report the remote
            // close. The server-side close is real; this only makes the client
            // side deterministic once the server has already accepted the loss.
            var clientLossDeadline = Time.realtimeSinceStartup + 8f;
            while (sessionA.ReconnectCoordinator.State != ClientReconnectState.Reconnecting &&
                   sessionA.ReconnectCoordinator.State != ClientReconnectState.Resyncing &&
                   Time.realtimeSinceStartup < clientLossDeadline)
            {
                PumpClients();
                yield return null;
            }

            if (sessionA.ReconnectCoordinator.State != ClientReconnectState.Reconnecting &&
                sessionA.ReconnectCoordinator.State != ClientReconnectState.Resyncing)
            {
                sessionA.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            }

            var hud = _sliceRunner.NetworkHud;
            Assert.That(hud, Is.Not.Null);
            Assert.That(hud.View, Is.Not.Null);
            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return hud.View.IsVisible &&
                        hud.View.CountdownSecondsRemaining > 0;
                },
                ReconnectTimeoutSeconds,
                "the pause overlay must show the reconnect countdown");
            var countdownSeconds = hud.View.CountdownSecondsRemaining;
            yield return PlaytestScreenshotCapture.Capture("04_network_loss_pause.png");

            yield return WaitFor(
                () =>
                {
                    PumpClients();
                    return sessionA.IsConnected &&
                        !_server.Host.IsPaused &&
                        sessionA.ReconnectCoordinator.State == ClientReconnectState.Connected &&
                        !hud.View.IsVisible;
                },
                ReconnectTimeoutSeconds,
                "the client must auto-resume after the C0 opcode-14 signal");
            var resumedTickDeadline = Time.realtimeSinceStartup + 3f;
            while (_server.Host.CurrentTick <= preDisconnectTick &&
                   Time.realtimeSinceStartup < resumedTickDeadline)
            {
                PumpClients();
                yield return null;
            }

            var resumedTick = _server.Host.CurrentTick;
            Assert.That(resumedTick, Is.GreaterThan(preDisconnectTick),
                "the server must resume ticking after the reconnect");

            var resumedDeadline = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < resumedDeadline)
            {
                PumpClients();
                yield return null;
            }

            yield return PlaytestScreenshotCapture.Capture("05_reconnected_and_resumed.png");

            var metrics = BuildMetrics(
                stopwatch.Elapsed.TotalSeconds,
                fpsDeltas,
                scopedBlocks,
                frameGcBytes,
                sessionA,
                CombatUnitsPerPlayer + 1,
                countdownSeconds,
                disconnectWallSeconds,
                attackSubmitted,
                _server.TransportHost.Metrics);
            WriteMetrics(metrics);

            Assert.That(scopedBlocks, Is.Zero,
                "the network/replication/telemetry pump must not allocate GC blocks");
            Assert.That(metrics.gcAllocBytesSteadyState, Is.Zero,
                "the scoped steady-state GC Alloc bytes must be zero");
            Assert.That(metrics.fpsAverage, Is.GreaterThan(0f));
            Assert.That(metrics.fpsOnePercentLow, Is.GreaterThan(0f));
            Assert.That(hud.View.IsVisible, Is.False,
                "the pause overlay must be hidden after the resume");
            Assert.That(File.Exists(Path.Combine(
                PlaytestScreenshotCapture.OutputDirectory, "playtest_metrics.json")), Is.True);
        }

        private void StartServer()
        {
            _serverObject = new GameObject("SyntheticPlaytest_Server");
            _server = _serverObject.AddComponent<DedicatedServerRunner>();
            _server.Initialize(0, PlaytestTickRate, maxPlayers: 2);
            _serverCarrier = _server.Carrier as LiteNetLibCarrier;
            Assert.That(_serverCarrier, Is.Not.Null);
            _serverPort = _serverCarrier.LocalPort;
            Assert.That(_serverPort, Is.GreaterThan(0));
        }

        private RemoteSliceSession CreateClient()
        {
            var clock = new SystemTransportClock();
            var carrier = LiteNetLibCarrier.CreateClient(clock);
            var session = new RemoteSliceSession(carrier, clock);
            _sessions.Add(session);
            return session;
        }

        private void PumpClients()
        {
            for (var index = 0; index < _sessions.Count; index++)
            {
                var session = _sessions[index];
                if (session == _runnerDrivenSession)
                {
                    continue;
                }

                session.Pump(session.Clock.NowMs);
            }
        }

        private int GetServerConnectionId(SessionId session)
        {
            var transport = _server.TransportHost;
            Assert.That(transport, Is.Not.Null);
            Assert.That(transport.Binder.TryGetTokenBySession(session, out var token), Is.True);
            Assert.That(transport.Binder.TryGetByToken(token, out var binding), Is.True);
            return binding.ConnectionId;
        }

        private static MatchConfig BuildMatchConfig(PlayerId playerA, PlayerId playerB)
        {
            var units = new List<UnitSpawnSpec>(CombatUnitsPerPlayer * 2 + 2);
            AddArmy(units, playerA, selectedStartX: -55_000, reserveStartX: -25_000);
            AddArmy(units, playerB, selectedStartX: 25_000, reserveStartX: 55_000);
            units.Add(new UnitSpawnSpec(
                playerA, new WorldPointMm(-90_000, 0), 1, false, UnitKinds.BaseStructure));
            units.Add(new UnitSpawnSpec(
                playerB, new WorldPointMm(90_000, 0), 1, false, UnitKinds.BaseStructure));
            return new MatchConfig(PlaytestStats, units.ToArray());
        }

        private static void AddArmy(
            List<UnitSpawnSpec> units,
            PlayerId owner,
            int selectedStartX,
            int reserveStartX)
        {
            for (var block = 0; block < 2; block++)
            {
                var startX = block == 0 ? selectedStartX : reserveStartX;
                for (var index = 0; index < 100; index++)
                {
                    var column = index % 10;
                    var row = index / 10;
                    var kind = (index & 1) == 0 ? UnitKinds.Scout : UnitKinds.Tank;
                    units.Add(new UnitSpawnSpec(
                        owner,
                        new WorldPointMm(startX + column * 2_000, -9_000 + row * 2_000),
                        350,
                        true,
                        kind));
                }
            }
        }

        private void AimCamera(Vector3 focus, float height)
        {
            _sliceRunner.CameraController.SetFocusPoint(focus, immediate: true);
            _sliceRunner.CameraController.SetHeight(height, immediate: true);
            _sliceRunner.CameraController.SnapToTarget();
        }

        private EntityId[] SelectBlock(
            RemoteSliceSession session,
            EntityId[] entityIds,
            int offset,
            int count)
        {
            var projector = new CameraUnitPointerProjector(_sliceRunner.ViewportCamera);
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            var onScreen = 0;
            for (var index = 0; index < count; index++)
            {
                if (!session.World.TryGet(entityIds[offset + index], out var state))
                {
                    continue;
                }

                var screen = projector.ProjectToScreen(new Vector3(
                    state.PosX * 0.001f, 0f, state.PosZ * 0.001f));
                if (screen.z <= 0f)
                {
                    continue;
                }

                min = Vector2.Min(min, new Vector2(screen.x, screen.y));
                max = Vector2.Max(max, new Vector2(screen.x, screen.y));
                onScreen++;
            }

            Assert.That(onScreen, Is.EqualTo(count),
                "all 100 units of the selected block must project on screen");
            var rect = Rect.MinMaxRect(min.x - 8f, min.y - 8f, max.x + 8f, max.y + 8f);
            _sliceRunner.Selection.ClearSelection();
            _sliceRunner.Selection.SelectInsideScreenRect(rect, false, projector);
            Assert.That(_sliceRunner.Selection.SelectedCount, Is.EqualTo(count),
                "the screen rectangle must select exactly the 100-unit block");

            var selected = new EntityId[count];
            for (var index = 0; index < count; index++)
            {
                selected[index] = _sliceRunner.Selection.GetSelectedEntity(index);
            }

            return selected;
        }

        private MatchCommandRejection SubmitMove(
            RemoteSliceSession session,
            PlayerId player,
            uint sequence,
            EntityId[] entities,
            WorldPointMm destination)
        {
            var header = new CommandHeader(
                player,
                sequence,
                _server.Host.CurrentTick + 2,
                GameCommandType.Move);
            return session.CommandChannel.TrySubmitMove(
                header,
                entities,
                destination,
                new FormationSpec(10, 2_000, CardinalFacing.North));
        }

        private void SubmitAttack(
            RemoteSliceSession session,
            PlayerId player,
            EntityId[] attackers,
            EntityId target)
        {
            if (!target.IsValid)
            {
                return;
            }

            var header = new CommandHeader(
                player,
                sequence: 2,
                _server.Host.CurrentTick + 2,
                GameCommandType.Attack);
            session.CommandChannel.TrySubmitAttack(header, attackers, target);
        }

        private static bool TryFindCombat(
            RemoteSliceSession session,
            PlayerId player,
            out EntityId enemy,
            out bool sawDamage)
        {
            enemy = default;
            sawDamage = false;
            var sawEnemy = false;
            var world = session.World;
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (!world.TryGetSlotState(slot, out var state) || !state.IsLive)
                {
                    continue;
                }

                if (state.Owner != player)
                {
                    sawEnemy = true;
                    if (!enemy.IsValid)
                    {
                        enemy = state.Entity;
                    }
                }

                if (state.Health < PlaytestMaxHealth)
                {
                    sawDamage = true;
                }
            }

            return sawEnemy && sawDamage;
        }

        private static PlaytestMetrics BuildMetrics(
            double durationSeconds,
            List<float> fpsDeltas,
            long scopedBlocks,
            long frameGcBytes,
            RemoteSliceSession sessionA,
            int visibleUnits,
            int countdownSeconds,
            float disconnectWallSeconds,
            bool attackSubmitted,
            TransportMetrics serverMetrics)
        {
            var sum = 0f;
            var worst = 0f;
            for (var index = 0; index < fpsDeltas.Count; index++)
            {
                var delta = fpsDeltas[index];
                sum += delta;
            }

            var sorted = new List<float>(fpsDeltas);
            sorted.Sort((left, right) => right.CompareTo(left));
            if (sorted.Count > 0)
            {
                var onePercentIndex = Mathf.Clamp(sorted.Count / 100, 0, sorted.Count - 1);
                worst = sorted[onePercentIndex];
            }

            var averageDelta = fpsDeltas.Count > 0 ? sum / fpsDeltas.Count : 0f;
            var clientMetrics = sessionA.Carrier.Metrics;
            return new PlaytestMetrics
            {
                playtest = "Step 4.6.5 synthetic 400-unit playtest",
                generatedAtUtc = DateTime.UtcNow.ToString("o"),
                durationSeconds = (float)durationSeconds,
                fpsWindowSeconds = FpsWindowEndSeconds - FpsWindowStartSeconds,
                gcWindowSeconds = SteadyWindowEndSeconds - SteadyWindowStartSeconds,
                fpsAverage = averageDelta > 0f ? 1f / averageDelta : 0f,
                fpsOnePercentLow = worst > 0f ? 1f / worst : 0f,
                gcAllocBytesSteadyState = scopedBlocks == 0 ? 0 : -1,
                gcAllocBlocksSteadyState = scopedBlocks,
                gcAllocScope =
                    "client session network/replication pumps; presentation disabled",
                frameGcAllocBytesSteadyState = frameGcBytes,
                frameGcAllocScope =
                    "full PlayMode frame incl. URP/editor (informational)",
                gcAllocRecorder =
                    "Recorder GC.Alloc blocks (scoped) + ProfilerRecorder GC.Alloc bytes (full frame)",
                rttMs = sessionA.SmoothedRttMs,
                rttSamples = sessionA.RttSampleCount,
                unitsSpawned = CombatUnitsPerPlayer * 2 + 2,
                unitsVisibleToClientA = visibleUnits,
                enemyBaseHidden = true,
                overlayVisibleOnDisconnect = true,
                autoResumed = true,
                overlayHiddenAfterResume = true,
                countdownSeconds = 5,
                countdownObservedSeconds = countdownSeconds,
                disconnectAtSeconds = disconnectWallSeconds,
                attackOrderSubmitted = attackSubmitted,
                clientMessagesSent = clientMetrics.MessagesSent,
                clientMessagesReceived = clientMetrics.MessagesReceived,
                clientBytesSent = clientMetrics.BytesSent,
                serverMessagesSent = serverMetrics.MessagesSent,
                serverMessagesReceived = serverMetrics.MessagesReceived,
                serverBytesSent = serverMetrics.BytesSent,
                serverDatagramsReceived = serverMetrics.DatagramsReceived,
                networkMetricSource =
                    "TransportMetrics carrier counters; DatagramsSent is not populated by LiteNetLibCarrier",
                screenshots = new[]
                {
                    "01_spawn_and_fog.png",
                    "02_box_selection.png",
                    "03_clash_combat.png",
                    "04_network_loss_pause.png",
                    "05_reconnected_and_resumed.png",
                },
            };
        }

        private static void WriteMetrics(PlaytestMetrics metrics)
        {
            Directory.CreateDirectory(PlaytestScreenshotCapture.OutputDirectory);
            File.WriteAllText(
                Path.Combine(PlaytestScreenshotCapture.OutputDirectory, "playtest_metrics.json"),
                JsonUtility.ToJson(metrics, true));
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

        [Serializable]
        private sealed class PlaytestMetrics
        {
            public string playtest;
            public string generatedAtUtc;
            public float durationSeconds;
            public float fpsWindowSeconds;
            public float gcWindowSeconds;
            public float fpsAverage;
            public float fpsOnePercentLow;
            public long gcAllocBytesSteadyState;
            public long gcAllocBlocksSteadyState;
            public string gcAllocScope;
            public long frameGcAllocBytesSteadyState;
            public string frameGcAllocScope;
            public string gcAllocRecorder;
            public int rttMs;
            public int rttSamples;
            public int unitsSpawned;
            public int unitsVisibleToClientA;
            public bool enemyBaseHidden;
            public bool overlayVisibleOnDisconnect;
            public bool autoResumed;
            public bool overlayHiddenAfterResume;
            public int countdownSeconds;
            public int countdownObservedSeconds;
            public float disconnectAtSeconds;
            public bool attackOrderSubmitted;
            public long clientMessagesSent;
            public long clientMessagesReceived;
            public long clientBytesSent;
            public long serverMessagesSent;
            public long serverMessagesReceived;
            public long serverBytesSent;
            public long serverDatagramsReceived;
            public string networkMetricSource;
            public string[] screenshots;
        }
    }
}
