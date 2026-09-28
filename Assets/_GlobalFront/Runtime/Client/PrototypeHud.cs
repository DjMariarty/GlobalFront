using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GlobalFront.Client
{
    public sealed class PrototypeHud : MonoBehaviour
    {
        private const string SliceButtonLabel = "Launch 400-Unit Tactical Vertical Slice [F9]";

        private PrototypeRtsController _controller;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _outcomeStyle;

        private void Awake()
        {
            _controller = GetComponent<PrototypeRtsController>();
        }

        private void Update()
        {
#if !UNITY_SERVER
            // F9 hands the scene to the Phase 3 slice. Null in a build with no
            // keyboard device, which is the same reason the camera rig reads its
            // own device defensively.
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f9Key.wasPressedThisFrame)
            {
                GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice();
            }
#endif
        }

        private void OnGUI()
        {
#if !UNITY_SERVER
            // The slice switch destroys this component's controller at the end
            // of the frame it is requested in, and this panel reads the host
            // through it. Without the guard the switch shows an error panel
            // instead of the battlefield it just built.
            if (_controller == null)
            {
                return;
            }

            EnsureStyles();

            var host = _controller.Host;
            var driver = host != null ? host.TickDriver : null;
            var backlogMs = (host != null ? host.TickBacklogSeconds : 0.0) * 1000.0;

            var area = new Rect(18f, 18f, 460f, 215f);
            GUI.Box(area, GUIContent.none);

            GUILayout.BeginArea(new Rect(32f, 28f, 435f, 195f));
            GUILayout.Label("GLOBAL FRONT - BATTLE PROTOTYPE 0.3", _titleStyle);
            GUILayout.Space(5f);
            GUILayout.Label(
                $"Authoritative simulation contract: {SimulationConstants.ServerTickRate} Hz\n" +
                $"Server tick: {host?.CurrentTick.ToString() ?? "—"} (driver {driver?.Tick.ToString() ?? "—"})   " +
                $"Backlog: {backlogMs:F1} ms\n" +
                $"Alive: blue {_controller.FriendlyAlive} / red {_controller.EnemyAlive}   " +
                $"Selected: {_controller.SelectedCount}\n" +
                $"Status: {_controller.BattleStatusMessage}\n" +
                $"{_controller.LastCommandMessage}\n" +
                "LMB/drag: select   Shift: add/remove\n" +
                "RMB ground: move   RMB enemy: attack\n" +
                "Camera: WASD / arrows / screen edge   Zoom: mouse wheel",
                _bodyStyle);
            GUILayout.Space(6f);
            if (GUILayout.Button(SliceButtonLabel))
            {
                GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice();
            }

            GUILayout.EndArea();

            if (_controller.Outcome.IsTerminal)
            {
                var banner = new Rect(
                    Screen.width * 0.5f - 190f,
                    Screen.height * 0.5f - 45f,
                    380f,
                    90f);
                GUI.Box(banner, GUIContent.none);
                GUI.Label(banner, _controller.BattleStatusMessage, _outcomeStyle);
            }
#endif
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null)
            {
                return;
            }

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.38f, 0.78f, 1f) }
            };

            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = Color.white }
            };

            _outcomeStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 32,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.38f, 0.88f, 1f) }
            };
        }
    }
}
