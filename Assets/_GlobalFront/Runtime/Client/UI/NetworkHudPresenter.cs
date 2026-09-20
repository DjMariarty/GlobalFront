using System;
using System.Collections.Generic;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;
using GlobalFront.Server.Sessions;
using UnityEngine;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// Event-driven presenter coordinating network match HUD state with host, session manager,
    /// and client reconnection coordinator (Phase 2.8, Step 2.8.2).
    /// Zero-GC in steady state.
    /// </summary>
    public sealed class NetworkHudPresenter : IDisposable
    {
        private readonly NetworkMatchHudState _state;
        private readonly Dictionary<SessionId, PlayerId> _sessionToPlayer = new Dictionary<SessionId, PlayerId>();

        private LocalMatchHost _host;
        private SessionManager _sessionManager;
        private ClientReconnectCoordinator _coordinator;
        private TacticalPauseOverlay _overlay;

        public NetworkMatchHudState State => _state;
        public TacticalPauseOverlay View => _overlay;
        public LocalMatchHost Host => _host;
        public SessionManager Sessions => _sessionManager;
        public ClientReconnectCoordinator Coordinator => _coordinator;

        public NetworkHudPresenter(
            LocalMatchHost host = null,
            SessionManager sessionManager = null,
            ClientReconnectCoordinator coordinator = null,
            TacticalPauseOverlay overlay = null)
        {
            _state = new NetworkMatchHudState();

            _host = host;
            _sessionManager = sessionManager ?? host?.Sessions;
            _coordinator = coordinator;
            _overlay = overlay;

            WireSessionManager();
            WireHost();
            WireCoordinator();

            _overlay?.UpdateFromState(_state);
        }

        public NetworkHudPresenter(
            LocalMatchHost host,
            ClientReconnectCoordinator coordinator = null,
            TacticalPauseOverlay overlay = null)
            : this(host, null, coordinator, overlay)
        {
        }

        public NetworkHudPresenter(
            SessionManager sessionManager,
            ClientReconnectCoordinator coordinator = null,
            TacticalPauseOverlay overlay = null)
            : this(null, sessionManager, coordinator, overlay)
        {
        }

        /// <summary>
        /// Binds or replaces the visual overlay component.
        /// </summary>
        public void BindView(TacticalPauseOverlay overlay)
        {
            _overlay = overlay;
            _overlay?.UpdateFromState(_state);
        }

        /// <summary>
        /// Binds or replaces the client reconnect coordinator.
        /// </summary>
        public void BindCoordinator(ClientReconnectCoordinator coordinator)
        {
            if (_coordinator != null)
            {
                _coordinator.StateChanged -= OnCoordinatorStateChanged;
                _coordinator.ReadyToResume -= Resume;
            }

            _coordinator = coordinator;
            WireCoordinator();
        }

        /// <summary>
        /// Advances real time during tactical pause or countdown.
        /// </summary>
        public void AdvanceRealTime(double elapsedSeconds)
        {
            if (_state.IsTacticalPauseActive)
            {
                _state.RemainingGraceSeconds = Mathf.Max(0f, _state.RemainingGraceSeconds - (float)elapsedSeconds);
                _overlay?.UpdatePauseState(_state.PausingPlayer, _state.RemainingGraceSeconds);
            }

            if (_state.IsResumeCountdownActive)
            {
                if (_coordinator != null && _coordinator.State == ClientReconnectState.Countdown)
                {
                    var remaining = Mathf.Max(0, (int)Math.Ceiling(_coordinator.CountdownRemainingSeconds));
                    _state.CountdownSecondsRemaining = remaining;
                    if (remaining <= 0)
                    {
                        Resume();
                    }
                    else
                    {
                        _overlay?.UpdateCountdown(remaining);
                    }
                }
            }
        }

        /// <summary>
        /// Advances pause ticks, reducing RemainingGraceSeconds by (ticks * 0.05s).
        /// </summary>
        public void AdvancePauseTicks(ulong ticks = 1)
        {
            if (_state.IsTacticalPauseActive)
            {
                var seconds = (float)(ticks * SimulationConstants.ServerTickDurationSeconds);
                _state.RemainingGraceSeconds = Mathf.Max(0f, _state.RemainingGraceSeconds - seconds);
                _overlay?.UpdatePauseState(_state.PausingPlayer, _state.RemainingGraceSeconds);
            }
        }

        /// <summary>
        /// Explicitly triggers start of the 5-second resumption countdown.
        /// </summary>
        public void StartCountdown(int seconds = 5)
        {
            _state.IsResumeCountdownActive = true;
            _state.CountdownSecondsRemaining = seconds;
            _overlay?.SetVisible(true);
            _overlay?.UpdateCountdown(seconds);
        }

        /// <summary>
        /// Updates the remaining countdown seconds. Resumes automatically if remaining seconds reach 0.
        /// </summary>
        public void UpdateCountdown(int secondsRemaining)
        {
            _state.CountdownSecondsRemaining = secondsRemaining;
            if (secondsRemaining <= 0)
            {
                Resume();
            }
            else
            {
                _state.IsResumeCountdownActive = true;
                _overlay?.UpdateCountdown(secondsRemaining);
            }
        }

        /// <summary>
        /// Clears tactical pause and countdown flags, hiding the overlay.
        /// </summary>
        public void Resume()
        {
            _state.IsTacticalPauseActive = false;
            _state.IsResumeCountdownActive = false;
            _state.CountdownSecondsRemaining = 0;
            _overlay?.Hide();
        }

        /// <summary>
        /// Updates slot ping in milliseconds for a specific player.
        /// </summary>
        public void UpdatePing(PlayerId player, int pingMs)
        {
            if (_state.TryGetSlot(player, out var slotIdx))
            {
                _state[slotIdx].PingMs = pingMs;
            }
        }

        /// <summary>
        /// Updates status for a specific player slot.
        /// </summary>
        public void SetSlotStatus(PlayerId player, SlotStatus status)
        {
            if (_state.TryGetSlot(player, out var slotIdx))
            {
                _state[slotIdx].SlotStatus = status;
            }
        }

        private void WireSessionManager()
        {
            if (_sessionManager == null)
            {
                return;
            }

            _sessionManager.SessionDisconnected += OnSessionDisconnected;
            _sessionManager.SessionGraceExpired += OnSessionGraceExpired;
        }

        private void WireHost()
        {
            if (_host == null)
            {
                return;
            }

            _host.Resumed += Resume;
            _host.RealTimeAdvanced += OnHostRealTimeAdvanced;
            _host.PauseTicksAdvanced += AdvancePauseTicks;
        }

        private void WireCoordinator()
        {
            if (_coordinator == null)
            {
                return;
            }

            _coordinator.StateChanged += OnCoordinatorStateChanged;
            _coordinator.ReadyToResume += Resume;

            if (_coordinator.State == ClientReconnectState.Countdown)
            {
                StartCountdown((int)Math.Ceiling(_coordinator.CountdownRemainingSeconds));
            }
        }

        private void OnSessionDisconnected(SessionId session, ulong atTick)
        {
            var player = default(PlayerId);
            if (_sessionManager != null && _sessionManager.TryGetSession(session, out var record))
            {
                player = record.Player;
                _sessionToPlayer[session] = player;
            }

            if (!player.IsValid && _sessionToPlayer.TryGetValue(session, out var cachedPlayer))
            {
                player = cachedPlayer;
            }

            if (player.IsValid)
            {
                SetSlotStatus(player, SlotStatus.Disconnected);
            }

            _state.IsTacticalPauseActive = true;
            _state.PausingPlayer = player;

            var graceTicks = _sessionManager != null ? _sessionManager.DisconnectGraceTicks : 4000;
            var graceSeconds = (float)(graceTicks * SimulationConstants.ServerTickDurationSeconds);
            if (graceSeconds <= 0f)
            {
                graceSeconds = NetworkMatchHudState.DefaultGraceSeconds;
            }

            _state.RemainingGraceSeconds = graceSeconds;

            if (_overlay != null)
            {
                _overlay.SetVisible(true);
                _overlay.UpdatePauseState(_state.PausingPlayer, _state.RemainingGraceSeconds);
            }
        }

        private void OnSessionGraceExpired(SessionId session)
        {
            var player = default(PlayerId);
            if (_sessionToPlayer.TryGetValue(session, out var cachedPlayer))
            {
                player = cachedPlayer;
            }
            else if (_sessionManager != null && _sessionManager.TryGetSession(session, out var record))
            {
                player = record.Player;
            }

            if (player.IsValid)
            {
                SetSlotStatus(player, SlotStatus.Abandoned);
            }
        }

        private void OnHostRealTimeAdvanced(double elapsedSeconds)
        {
            if (_host != null && _host.IsPaused)
            {
                AdvanceRealTime(elapsedSeconds);
            }
        }

        private void OnCoordinatorStateChanged(ClientReconnectState newState)
        {
            switch (newState)
            {
                case ClientReconnectState.Countdown:
                    _state.IsResumeCountdownActive = true;
                    _state.CountdownSecondsRemaining = Math.Max(1, (int)Math.Ceiling(_coordinator.CountdownRemainingSeconds));
                    if (_overlay != null)
                    {
                        _overlay.SetVisible(true);
                        _overlay.UpdateCountdown(_state.CountdownSecondsRemaining);
                    }
                    break;

                case ClientReconnectState.Reconnecting:
                case ClientReconnectState.Resyncing:
                    if (_coordinator.PlayerId.IsValid)
                    {
                        SetSlotStatus(_coordinator.PlayerId, SlotStatus.Reconnecting);
                    }
                    break;

                case ClientReconnectState.Connected:
                    _state.IsResumeCountdownActive = false;
                    _state.CountdownSecondsRemaining = 0;
                    if (_coordinator.PlayerId.IsValid)
                    {
                        SetSlotStatus(_coordinator.PlayerId, SlotStatus.Connected);
                    }
                    if (!_state.IsTacticalPauseActive)
                    {
                        _overlay?.Hide();
                    }
                    break;
            }
        }

        public void Dispose()
        {
            if (_sessionManager != null)
            {
                _sessionManager.SessionDisconnected -= OnSessionDisconnected;
                _sessionManager.SessionGraceExpired -= OnSessionGraceExpired;
                _sessionManager = null;
            }

            if (_host != null)
            {
                _host.Resumed -= Resume;
                _host.RealTimeAdvanced -= OnHostRealTimeAdvanced;
                _host.PauseTicksAdvanced -= AdvancePauseTicks;
                _host = null;
            }

            if (_coordinator != null)
            {
                _coordinator.StateChanged -= OnCoordinatorStateChanged;
                _coordinator.ReadyToResume -= Resume;
                _coordinator = null;
            }
        }
    }
}
