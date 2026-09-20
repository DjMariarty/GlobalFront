using System;
using GlobalFront.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// Lightweight UI overlay component for tactical pause and reconnection countdown (Phase 2.8, Step 2.8.2).
    /// Supports Canvas UI Text components as well as headless / test execution without Canvas references.
    /// Safe against NullReferenceException when UI references are unassigned.
    /// Zero-GC in steady state gameplay.
    /// </summary>
    public sealed class TacticalPauseOverlay : MonoBehaviour
    {
        [Header("UI References (Optional)")]
        [SerializeField] private GameObject _overlayRoot;
        [SerializeField] private Text _messageText;
        [SerializeField] private Text _timerText;
        [SerializeField] private Text _countdownBannerText;

        // Cached values to avoid string allocations when unchanged
        private int _cachedTimerSeconds = -1;
        private int _cachedCountdownSeconds = -1;
        private PlayerId _cachedPausingPlayer;
        private GUIStyle _boxStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;

        /// <summary>
        /// True if the tactical pause overlay is currently visible.
        /// </summary>
        public bool IsVisible { get; private set; }

        /// <summary>
        /// Current tactical pause message text.
        /// </summary>
        public string Message { get; private set; } = string.Empty;

        /// <summary>
        /// Current grace timer text.
        /// </summary>
        public string TimerText { get; private set; } = string.Empty;

        /// <summary>
        /// Current resumption countdown banner text.
        /// </summary>
        public string CountdownBannerText { get; private set; } = string.Empty;

        /// <summary>
        /// Player whose disconnection caused the pause.
        /// </summary>
        public PlayerId PausingPlayer { get; private set; }

        /// <summary>
        /// Remaining grace seconds displayed on the timer.
        /// </summary>
        public float RemainingGraceSeconds { get; private set; }

        /// <summary>
        /// Remaining countdown seconds displayed on the banner.
        /// </summary>
        public int CountdownSecondsRemaining { get; private set; }

        /// <summary>
        /// Controls the overlay visibility.
        /// </summary>
        public void SetVisible(bool visible)
        {
            IsVisible = visible;
            if (_overlayRoot != null)
            {
                _overlayRoot.SetActive(visible);
            }
        }

        /// <summary>
        /// Updates the tactical pause message and grace timer.
        /// </summary>
        public void UpdatePauseState(PlayerId playerId, float remainingSeconds)
        {
            PausingPlayer = playerId;
            RemainingGraceSeconds = remainingSeconds;

            if (playerId != _cachedPausingPlayer)
            {
                _cachedPausingPlayer = playerId;
                Message = $"Тактическая пауза: Игрок {playerId} переподключается...";
                if (_messageText != null)
                {
                    _messageText.text = Message;
                }
            }

            var roundedSec = (int)Math.Round(remainingSeconds);
            if (roundedSec != _cachedTimerSeconds)
            {
                _cachedTimerSeconds = roundedSec;
                TimerText = $"Осталось времени: {remainingSeconds:F0}с";
                if (_timerText != null)
                {
                    _timerText.text = TimerText;
                }
            }
        }

        /// <summary>
        /// Updates the 5-second resumption countdown banner.
        /// </summary>
        public void UpdateCountdown(int countdownSeconds)
        {
            CountdownSecondsRemaining = countdownSeconds;

            if (countdownSeconds != _cachedCountdownSeconds)
            {
                _cachedCountdownSeconds = countdownSeconds;
                CountdownBannerText = $"Бой продолжится через: {countdownSeconds}...";
                if (_countdownBannerText != null)
                {
                    _countdownBannerText.text = CountdownBannerText;
                }
            }
        }

        /// <summary>
        /// Synchronizes this overlay view directly from a <see cref="NetworkMatchHudState"/>.
        /// </summary>
        public void UpdateFromState(NetworkMatchHudState state)
        {
            if (state == null)
            {
                SetVisible(false);
                return;
            }

            var shouldBeVisible = state.IsTacticalPauseActive || state.IsResumeCountdownActive;
            SetVisible(shouldBeVisible);

            if (!shouldBeVisible)
            {
                return;
            }

            if (state.IsTacticalPauseActive)
            {
                UpdatePauseState(state.PausingPlayer, state.RemainingGraceSeconds);
            }

            if (state.IsResumeCountdownActive)
            {
                UpdateCountdown(state.CountdownSecondsRemaining);
            }
        }

        /// <summary>
        /// Hides the overlay and resets cached display values.
        /// </summary>
        public void Hide()
        {
            SetVisible(false);
            _cachedTimerSeconds = -1;
            _cachedCountdownSeconds = -1;
            _cachedPausingPlayer = default;
        }

#if !UNITY_SERVER
        private void OnGUI()
        {
            if (!IsVisible)
            {
                return;
            }

            EnsureStyles();

            var width = 480f;
            var height = CountdownSecondsRemaining > 0 ? 140f : 100f;
            var rect = new Rect(
                (Screen.width - width) * 0.5f,
                Screen.height * 0.25f,
                width,
                height);

            GUI.Box(rect, GUIContent.none, _boxStyle);

            GUILayout.BeginArea(new Rect(rect.x + 16f, rect.y + 12f, rect.width - 32f, rect.height - 24f));
            if (!string.IsNullOrEmpty(Message))
            {
                GUILayout.Label(Message, _titleStyle);
            }
            if (!string.IsNullOrEmpty(TimerText))
            {
                GUILayout.Label(TimerText, _bodyStyle);
            }
            if (CountdownSecondsRemaining > 0 && !string.IsNullOrEmpty(CountdownBannerText))
            {
                GUILayout.Space(8f);
                GUILayout.Label(CountdownBannerText, _titleStyle);
            }
            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null)
            {
                return;
            }

            _boxStyle = new GUIStyle(GUI.skin.box);
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.2f) }
            };
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = Color.white }
            };
        }
#endif
    }
}
