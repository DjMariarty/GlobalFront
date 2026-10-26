using System;
using System.Collections.Generic;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Presentation;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.DedicatedServer;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using UnityEngine;

namespace GlobalFront.Client
{
    /// <summary>
    /// Interactive entry point of the Step 4.6.5 playtest: the same 402-unit,
    /// Fog-of-War match the synthetic PlayMode test drives, with the camera rig
    /// and the selection driver in the player's hands instead of a bot's.
    ///
    /// The menu item cannot start a match directly — entering Play Mode reloads
    /// the domain and Unity destroys editor-side objects — so it leaves a
    /// <see cref="UnityEditor.SessionState"/> flag behind and the runtime hook
    /// picks it up after the scene loads. The first frame then stands up a
    /// dedicated server on an OS-assigned free port, connects the ordinary
    /// dedicated client to it, and starts the match once the client's handshake
    /// has taken Player 1. Player 2 is held by a server-side session with no
    /// transport binding: the match needs both owners to exist, and the player
    /// should face an army rather than an empty field.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractivePlaytestBootstrap : MonoBehaviour
    {
        /// <summary>Menu path of the interactive playtest.</summary>
        public const string MenuPath = "GlobalFront/Playtest/Launch 400-Unit Dedicated Match";

        /// <summary>Combatants per side, plus a base each, is the 402-unit roster.</summary>
        public const int UnitsPerPlayer = 200;

        /// <summary>Authoritative tick rate, matching the synthetic playtest.</summary>
        public const int PlaytestTickRate = 20;

        private const string PendingSessionKey = "GlobalFront.InteractivePlaytest.Pending";
        private const string ServerObjectName = "[GlobalFront Interactive Playtest Server]";
        private const float ClientConnectTimeoutSeconds = 20f;

        private static readonly CombatStats PlaytestCombatStats =
            new CombatStats(UnitCatalog.PrototypeMaximumHealth, damage: 16, rangeMm: 7000, cooldownTicks: 10);

        private static bool _pendingHandled;

        private DedicatedServerRunner _server;
        private GameObject _serverObject;
        private TacticalVerticalSliceRunner _runner;
        private bool _started;
        private float _deadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void LaunchIfRequested()
        {
#if UNITY_EDITOR
            if (_pendingHandled ||
                !UnityEditor.SessionState.GetBool(PendingSessionKey, false))
            {
                return;
            }

            _pendingHandled = true;
            UnityEditor.SessionState.SetBool(PendingSessionKey, false);
            CreateBootstrap();
#endif
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem(MenuPath)]
        public static void LaunchFromMenu()
        {
            if (UnityEditor.EditorApplication.isCompiling)
            {
                Debug.LogWarning("[GlobalFront] the playtest cannot start while scripts are compiling.");
                return;
            }

            if (UnityEditor.EditorApplication.isPlaying)
            {
                CreateBootstrap();
                return;
            }

            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            UnityEditor.SessionState.SetBool(PendingSessionKey, true);
            UnityEditor.EditorApplication.EnterPlaymode();
        }
#endif

        private static void CreateBootstrap()
        {
            if (FindAnyObjectByType<InteractivePlaytestBootstrap>() != null)
            {
                Debug.LogWarning("[GlobalFront] an interactive playtest is already running.");
                return;
            }

            var root = new GameObject("[GlobalFront Interactive Playtest]");
            DontDestroyOnLoad(root);
            root.AddComponent<InteractivePlaytestBootstrap>();
        }

        private void Update()
        {
            if (!_started)
            {
                _started = true;
                StartServerAndClient();
                return;
            }

            if (_runner == null || _runner.RemoteSession == null)
            {
                enabled = false;
                return;
            }

            if (_runner.RemoteSession.IsConnected)
            {
                StartMatch();
                enabled = false;
                return;
            }

            if (Time.realtimeSinceStartup > _deadline)
            {
                Fail($"the dedicated client did not connect within {ClientConnectTimeoutSeconds:0} s.");
            }
        }

        private void StartServerAndClient()
        {
            try
            {
                _serverObject = new GameObject(ServerObjectName);
                DontDestroyOnLoad(_serverObject);
                _server = _serverObject.AddComponent<DedicatedServerRunner>();

                // Port 0 asks the OS for a free port, so a previous playtest or a
                // second editor cannot collide with this one.
                _server.Initialize(0, PlaytestTickRate, maxPlayers: 2);

                if (!(_server.Carrier is LiteNetLibCarrier carrier) || carrier.LocalPort <= 0)
                {
                    Fail("the dedicated server did not bind a UDP port.");
                    return;
                }

                _runner = GlobalFrontRuntimeBootstrap.ActivateDedicatedClient(
                    "127.0.0.1", carrier.LocalPort, createVisuals: true);
                _deadline = Time.realtimeSinceStartup + ClientConnectTimeoutSeconds;
                Debug.Log($"[GlobalFront] playtest server listening on UDP {carrier.LocalPort}; connecting the local client...");
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        private void StartMatch()
        {
            try
            {
                var host = _server != null ? _server.Host : null;
                if (host == null)
                {
                    Fail("the server host disappeared before the match could start.");
                    return;
                }

                var local = _runner.LocalPlayer;
                if (!local.IsValid)
                {
                    Fail("the client has not been assigned a player yet.");
                    return;
                }

                // A second owner for the match roster. It never connects a
                // transport, so its army holds its spawn until the player finds it.
                var opponentSession = host.CreateSession(host.CreateConnectionHandle());
                if (host.TryJoinMatch(opponentSession, _server.Match, out var opponent) != JoinResult.Assigned)
                {
                    Fail("the server refused the opponent session.");
                    return;
                }

                if (!host.TryStartSessionMatch(_server.Match, BuildMatchConfig(local, opponent), out _))
                {
                    Fail("the server refused the 402-unit match.");
                    return;
                }

                _runner.CameraController.EnableEdgePan = true;
                _runner.CameraController.SetFocusPoint(new Vector3(-45f, 0f, 0f), immediate: true);
                _runner.CameraController.SetHeight(90f, immediate: true);
                _runner.CameraController.SnapToTarget();

                Debug.Log(
                    "[GlobalFront] 402-unit match running. Left-drag to box-select, right-click to move or attack; " +
                    "the enemy is hidden by Fog of War until your units make contact.");
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        private void Fail(string message)
        {
            Debug.LogError("[GlobalFront] interactive playtest: " + message);
            enabled = false;
            Shutdown();
        }

        private void OnDestroy() => Shutdown();

        private void Shutdown()
        {
            if (_server != null)
            {
                _server.Shutdown();
                _server = null;
            }

            if (_serverObject != null)
            {
                Destroy(_serverObject);
                _serverObject = null;
            }
        }

        private static MatchConfig BuildMatchConfig(PlayerId local, PlayerId opponent)
        {
            var units = new List<UnitSpawnSpec>(UnitsPerPlayer * 2 + 2);
            AddArmy(units, local, frontStartX: -25_000, rearStartX: -65_000);
            AddArmy(units, opponent, frontStartX: 25_000, rearStartX: 65_000);
            units.Add(new UnitSpawnSpec(
                local, new WorldPointMm(-90_000, 0), 1, false, UnitKinds.BaseStructure));
            units.Add(new UnitSpawnSpec(
                opponent, new WorldPointMm(90_000, 0), 1, false, UnitKinds.BaseStructure));
            return new MatchConfig(PlaytestCombatStats, units.ToArray());
        }

        private static void AddArmy(
            List<UnitSpawnSpec> units,
            PlayerId owner,
            int frontStartX,
            int rearStartX)
        {
            for (var block = 0; block < 2; block++)
            {
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
                        true,
                        kind));
                }
            }
        }
    }
}
