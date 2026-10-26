using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GlobalFront.Client;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Client.UI;
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
using UnityEngine.UI;
using EntityId = GlobalFront.Core.Model.EntityId;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace GlobalFront.Tests.PlayMode
{
    /// <summary>
    /// Step 4.6.5 synthetic playtest: 400 units over real UDP loopback, Fog of
    /// War, a bot that box-selects the 100-unit front block and marches it onto
    /// the enemy line, which holds position and returns fire, a server-side
    /// disconnect at 20 s, reconnect/resync/countdown, the C0 opcode 14 resume,
    /// five 1080p screenshots and steady-state metrics.
    /// </summary>
    [TestFixture]
    public sealed class SyntheticPlaytestPlayModeTests
    {
        private const string RuntimeRootName = "[GlobalFront Runtime]";
        private const int CombatUnitsPerPlayer = 200;
        private const int SelectedUnits = 100;
        private const int PlaytestTickRate = 20;

        // The client's catalog is the only health denominator the HUD has, and it
        // clamps a unit at 100% of that archetype's maximum. A match that spawns
        // 200-HP units under a 100-HP catalog therefore reads 100% until a unit is
        // half dead, so the playtest has to use the catalog's maximum or its HUD
        // cannot show the damage the brief asks for.
        private const int PlaytestMaxHealth = UnitCatalog.PrototypeMaximumHealth;

        // Two damage per volley is a 25 s time-to-kill at the 10-tick cooldown:
        // slow enough that the selected squad survives the reconnect, fast enough
        // that the clash screenshot catches a readable mid-fight percentage.
        private const int PlaytestDamage = 2;

        // The selected squad marches into a stationary enemy line. The destination
        // sits half a formation cell (0.5 m) off the enemy grid on both axes, so
        // every defender has one unique nearest attacker once the squad arrives;
        // the staged fire below is then a 1:1 exchange instead of a focus-fire
        // that would kill a handful of units and leave the rest at 100%.
        private const int ContactCentreXMillimetres = 34_500;
        private const int ContactCentreZMillimetres = 500;
        private const float StagedEngagementSeconds = 8.5f;
        private const float ClashCaptureSeconds = 15f;

        private const int DisconnectAtSeconds = 20;
        private const float SteadyWindowStartSeconds = 16f;
        private const float SteadyWindowEndSeconds = 19f;
        private const float FpsWindowStartSeconds = 10f;
        private const float FpsWindowEndSeconds = 13f;
        private const float HandshakeTimeoutSeconds = 30f;
        private const float BaselineTimeoutSeconds = 35f;
        private const float ReconnectTimeoutSeconds = 20f;

        private static readonly CombatStats PlaytestStats =
            new CombatStats(PlaytestMaxHealth, PlaytestDamage, rangeMm: 7000, cooldownTicks: 10);

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

            // The bot drags a box over the 100-unit block that leads toward the
            // enemy. Selecting the rear block would leave the HUD showing a squad
            // that never takes a shot, which is the defect this playtest has to
            // disprove; the front block is the one that meets the enemy line.
            AimCamera(new Vector3(-16f, 0f, 0f), 45f);
            yield return null;
            var selectedEntities = SelectBlock(
                sessionA, entityIds, 0, SelectedUnits);
            yield return null;
            yield return PlaytestScreenshotCapture.Capture("02_box_selection.png");
            AssertHudColumnsDoNotOverlap(_sliceRunner.SelectionHud);

            // The selected squad marches onto the enemy front block's centre and
            // the enemy line holds position. The move clears the squad's automatic
            // acquisition, which is what the server's ExecuteMove promises; the
            // stationary enemy keeps its auto-acquire, so the squad walks into
            // distributed return fire and its HUD health has to fall.
            var moveA = SubmitMove(
                sessionA, playerA, sequence: 1, selectedEntities,
                new WorldPointMm(ContactCentreXMillimetres, ContactCentreZMillimetres));
            Assert.That(moveA, Is.EqualTo(MatchCommandRejection.None),
                "the selected 100 units must pass the client's move pre-flight");

            AimCamera(new Vector3(25f, 0f, 0f), 55f);
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
            var transportWarmed = false;
            var combatCaptured = false;
            var engagementStaged = false;
            var selectedSquadHealthAtClash = -1;

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
                    var frameBlocks = scopedRecorder.sampleBlockCount;
                    if (frameBlocks > 0)
                    {
                        Debug.Log($"[PlaytestGC] t={elapsed:F2}s blocks={frameBlocks}");
                    }

                    scopedBlocks += frameBlocks;
                    frameGcBytes += frameGcRecorder.LastValue;
                }
                else
                {
                    if (elapsed >= SteadyWindowStartSeconds - 0.8f &&
                        elapsed < SteadyWindowStartSeconds)
                    {
                        // Warm the manual pump path outside the measured
                        // window: one pump is not enough, because a delta that
                        // arrives after the presentation is disabled can still
                        // take a one-time decode/feedback path. Pump this way
                        // for the last 0.8 s before the window opens.
                        if (!runnerSuspended)
                        {
                            _sliceRunner.enabled = false;
                            runnerSuspended = true;
                        }

                        if (!transportWarmed)
                        {
                            // Populate LiteNetLib's packet pool before the
                            // window: its first sends allocate, and a ping
                            // cadence that lands in the window would otherwise
                            // be measured as a network-path allocation.
                            transportWarmed = true;
                            for (var ping = 0; ping < 24; ping++)
                            {
                                sessionA.Endpoint.TrySendPing(sessionA.Clock.NowMs);
                                sessionB.Endpoint.TrySendPing(sessionB.Clock.NowMs);
                            }
                        }

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

                if (!engagementStaged && elapsed >= StagedEngagementSeconds)
                {
                    engagementStaged = true;
                    StageDistributedEngagement(sessionB, playerB, entityIds, selectedEntities);
                }

                if (!combatCaptured && elapsed >= ClashCaptureSeconds)
                {
                    var healthPercent = _sliceRunner.SelectionHudModel.HealthPercent;
                    Assert.That(
                        _sliceRunner.Selection.SelectedCount,
                        Is.EqualTo(SelectedUnits),
                        "the clash must show the whole selected squad, not a pruned remnant");
                    Assert.That(
                        healthPercent,
                        Is.LessThan(100),
                        "the selected squad is in direct fire contact, so its HUD health must be below 100%");
                    Assert.That(healthPercent, Is.GreaterThan(0), "the selected squad must still be alive");
                    Assert.That(
                        _sliceRunner.SelectionHud.DisplayedHealth,
                        Is.Not.EqualTo("100%"),
                        "the panel must print the damaged value the model holds");
                    selectedSquadHealthAtClash = healthPercent;
                    combatCaptured = true;
                    yield return PlaytestScreenshotCapture.Capture("03_clash_combat.png");
                    continue;
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

            // The keyframe released every pooled view; the repair runs on the first
            // presentation frame that rebinds them. Read the rings back rather than
            // trusting the screenshot to show them.
            yield return null;
            var selection = _sliceRunner.Selection;
            Assert.That(
                selection.SelectedCount,
                Is.EqualTo(SelectedUnits),
                "the selection must survive the keyframe rebuild");
            var ringedViews = 0;
            for (var index = 0; index < selection.SelectedCount; index++)
            {
                var slot = selection.GetSelectedSlot(index);
                var entity = selection.GetSelectedEntity(index);
                if (_sliceRunner.Binder.TryGetView(slot, out var view) &&
                    view.EntityValue == entity.Value &&
                    view.IsSelected)
                {
                    ringedViews++;
                }
            }

            Assert.That(
                ringedViews,
                Is.EqualTo(selection.SelectedCount),
                "every selected unit must have its selection ring back after the keyframe resync");
            Assert.That(
                _sliceRunner.SelectionHud.DisplayedCount,
                Is.EqualTo("100"),
                "the panel must still report the squad it is drawing rings for");

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
                selectedSquadHealthAtClash,
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
            AddArmy(units, playerA, frontStartX: -25_000, rearStartX: -65_000, autoAcquire: true);
            AddArmy(units, playerB, frontStartX: 25_000, rearStartX: 65_000, autoAcquire: false);
            units.Add(new UnitSpawnSpec(
                playerA, new WorldPointMm(-90_000, 0), 1, false, UnitKinds.BaseStructure));
            units.Add(new UnitSpawnSpec(
                playerB, new WorldPointMm(90_000, 0), 1, false, UnitKinds.BaseStructure));
            return new MatchConfig(PlaytestStats, units.ToArray());
        }

        private static void AddArmy(
            List<UnitSpawnSpec> units,
            PlayerId owner,
            int frontStartX,
            int rearStartX,
            bool autoAcquire)
        {
            for (var block = 0; block < 2; block++)
            {
                // Twenty-two metres of air between a front block's trailing edge and
                // the rear block keeps the rear outside the 18 m auto-acquire radius,
                // so the clash stays a clean 100 vs 100 instead of both blocks joining.
                var startX = block == 0 ? frontStartX : rearStartX;
                for (var index = 0; index < 100; index++)
                {
                    var column = index % 10;
                    var row = index / 10;
                    var kind = (index & 1) == 0 ? UnitKinds.Scout : UnitKinds.Tank;
                    units.Add(new UnitSpawnSpec(
                        owner,
                        new WorldPointMm(startX + column * 2_000, -9_000 + row * 2_000),
                        350,
                        autoAcquire,
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

        /// <summary>
        /// Opens the staged engagement: the selected squad has arrived on the
        /// enemy grid, and every defender is given its own nearest attacker as an
        /// explicit target. One attack order naming a single target for a hundred
        /// attackers would focus-fire one unit at a time — a handful of casualties
        /// and the rest of the squad at 100%, which is the HUD reading this
        /// playtest exists to disprove. Per-unit orders keep the exchange 1:1, so
        /// the squad's mean health falls honestly.
        /// </summary>
        private void StageDistributedEngagement(
            RemoteSliceSession sessionB,
            PlayerId playerB,
            EntityId[] entityIds,
            EntityId[] attackers)
        {
            var host = _server.Host;
            Assert.That(host, Is.Not.Null);
            var session = sessionB.Endpoint.Session;
            Assert.That(session.IsValid, Is.True, "the second client must carry a server session id");

            var accepted = 0;
            for (var index = 0; index < SelectedUnits; index++)
            {
                var defender = entityIds[CombatUnitsPerPlayer + index];
                Assert.That(
                    sessionB.World.TryGet(defender, out var defenderState),
                    Is.True,
                    "every defender must be present in the second client's world");

                var target = default(EntityId);
                var bestDistance = long.MaxValue;
                for (var candidateIndex = 0; candidateIndex < attackers.Length; candidateIndex++)
                {
                    var candidate = attackers[candidateIndex];
                    if (!sessionB.World.TryGet(candidate, out var candidateState))
                    {
                        continue;
                    }

                    var deltaX = (long)candidateState.PosX - defenderState.PosX;
                    var deltaZ = (long)candidateState.PosZ - defenderState.PosZ;
                    var distance = deltaX * deltaX + deltaZ * deltaZ;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        target = candidate;
                    }
                }

                Assert.That(target.IsValid, Is.True, "every defender must see at least one attacker");

                var header = new CommandHeader(
                    playerB,
                    (uint)(index + 1),
                    host.CurrentTick + 2,
                    GameCommandType.Attack);
                var result = host.TrySessionAttack(session, header, new[] { defender }, target);
                Assert.That(
                    result.IsAccepted,
                    Is.True,
                    $"defender {index} was refused: session {result.Session}, command {result.Command}");
                accepted++;
            }

            Assert.That(accepted, Is.EqualTo(SelectedUnits));
        }

        /// <summary>
        /// The labels and values share one column in the panel, and "Base structure"
        /// is the longest label. Measured against the live font rather than trusted
        /// from the layout constants, because the failure the player saw was glyphs
        /// on top of each other, not rects.
        /// </summary>
        private static void AssertHudColumnsDoNotOverlap(SelectionHudPresenter hud)
        {
            Assert.That(hud, Is.Not.Null);
            Assert.That(hud.PanelTransform, Is.Not.Null);
            var labels = new[]
            {
                "Selected", "Scout", "Tank", "Structure", "Health", "Order", "Result", "Issued", "Refused",
            };

            var measured = 0;
            for (var index = 0; index < labels.Length; index++)
            {
                var label = RowText(hud, "Label", labels[index]);
                var value = RowText(hud, "Value", labels[index]);
                Assert.That(
                    label.preferredWidth,
                    Is.GreaterThan(0f),
                    $"the {labels[index]} label must be measurable text");
                measured++;

                var labelEnd = label.rectTransform.anchoredPosition.x + label.preferredWidth;
                Assert.That(
                    labelEnd,
                    Is.LessThanOrEqualTo(value.rectTransform.anchoredPosition.x),
                    $"the {labels[index]} label ends at {labelEnd:0.#} px, past the " +
                    $"{value.rectTransform.anchoredPosition.x:0.#} px value column");
            }

            Assert.That(measured, Is.EqualTo(labels.Length));
        }

        private static Text RowText(SelectionHudPresenter hud, string column, string label)
        {
            var found = hud.PanelTransform.Find(column + " " + label);
            Assert.That(found, Is.Not.Null, $"the inspector has no {column.ToLower()} for {label}");
            var text = found.GetComponent<Text>();
            Assert.That(text, Is.Not.Null);
            return text;
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
            int selectedSquadHealthAtClash,
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
                selectedSquadHealthPercentAtClash = selectedSquadHealthAtClash,
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
            public int selectedSquadHealthPercentAtClash;
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
