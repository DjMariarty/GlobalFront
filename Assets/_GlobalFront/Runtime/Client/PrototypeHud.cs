using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using UnityEngine;

namespace GlobalFront.Client
{
    public sealed class PrototypeHud : MonoBehaviour
    {
        private PrototypeRtsController _controller;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _outcomeStyle;

        private void Awake()
        {
            _controller = GetComponent<PrototypeRtsController>();
        }

        private void OnGUI()
        {
#if !UNITY_SERVER
            EnsureStyles();

            var host = _controller.Host;
            var driver = host != null ? host.TickDriver : null;
            var backlogMs = (host != null ? host.TickBacklogSeconds : 0.0) * 1000.0;

            var area = new Rect(18f, 18f, 460f, 185f);
            GUI.Box(area, GUIContent.none);

            GUILayout.BeginArea(new Rect(32f, 28f, 435f, 165f));
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
