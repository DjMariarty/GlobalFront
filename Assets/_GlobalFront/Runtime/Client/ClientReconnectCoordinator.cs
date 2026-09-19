using System;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Reconnect;
using GlobalFront.Server.Transport;

namespace GlobalFront.Client
{
    /// <summary>
    /// States of the client-side session reconnection FSM (Phase 2.7, ADR-011).
    /// </summary>
    public enum ClientReconnectState : byte
    {
        /// <summary>Unattached or initial state before session establishment.</summary>
        Idle = 0,

        /// <summary>Normal gameplay attachment; session secret and IDs cached.</summary>
        Connected = 1,

        /// <summary>Socket disconnected; carrier reconnecting and sending ReconnectRequest.</summary>
        Reconnecting = 2,

        /// <summary>Server accepted re-attachment; waiting for and assembling Keyframe slices.</summary>
        Resyncing = 3,

        /// <summary>Keyframe installed; 5-second countdown active per OD-20.</summary>
        Countdown = 4,

        /// <summary>Terminal failure (grace window expired, session lost, match finished).</summary>
        Terminated = 5
    }

    /// <summary>
    /// Client-side reconnection coordinator and finite state machine (Phase 2.7, ADR-011 / OD-18..OD-22).
    /// Manages session caching, automatic re-attachment negotiation via C0 wire codec,
    /// resync priming for <see cref="ClientReplicationReceiver"/>, and OD-20 5-second countdown resumption.
    /// Preserves client command queue during pause without clearing (OD-19).
    /// </summary>
    public sealed class ClientReconnectCoordinator
    {
        public const float DefaultCountdownSeconds = 5.0f;

        private readonly ClientReplicationReceiver _replicationReceiver;

        public SessionId SessionId { get; private set; }
        public MatchId MatchId { get; private set; }
        public PlayerId PlayerId { get; private set; }
        public SessionSecret32 Secret { get; private set; }
        public ulong LastAppliedTick { get; set; }
        public ClientReconnectState State { get; private set; } = ClientReconnectState.Idle;
        public float CountdownRemainingSeconds { get; private set; }
        public int ReconnectAttemptCount { get; private set; }
        public ushort ActiveKeyframeSeq { get; private set; }
        public ulong ServerTick { get; private set; }

        public bool CanSubmitCommands => State == ClientReconnectState.Connected;
        public bool IsReconnecting => State == ClientReconnectState.Reconnecting ||
                                      State == ClientReconnectState.Resyncing ||
                                      State == ClientReconnectState.Countdown;

        public ClientReplicationReceiver Receiver => _replicationReceiver;

        /// <summary>Raised whenever the reconnection state transitions.</summary>
        public event Action<ClientReconnectState> StateChanged;

        /// <summary>Raised when reconnect response is accepted and resync begins.</summary>
        public event Action<ushort> ResyncStarted;

        /// <summary>Raised when the 5-second countdown finishes and gameplay resumes.</summary>
        public event Action ReadyToResume;

        public ClientReconnectCoordinator(ClientReplicationReceiver replicationReceiver = null)
        {
            _replicationReceiver = replicationReceiver;
        }

        /// <summary>
        /// Caches active session credentials and identifiers upon initial match entry.
        /// </summary>
        public void CacheSession(
            SessionId session,
            MatchId match,
            PlayerId player,
            in SessionSecret32 secret)
        {
            SessionId = session;
            MatchId = match;
            PlayerId = player;
            Secret = secret;
            ReconnectAttemptCount = 0;
            CountdownRemainingSeconds = DefaultCountdownSeconds;
            SetState(ClientReconnectState.Connected);
        }

        /// <summary>
        /// Handles transport disconnection event, entering the Reconnecting state.
        /// </summary>
        public void OnTransportDisconnected(TransportDisconnectReason reason)
        {
            if (State == ClientReconnectState.Terminated)
            {
                return;
            }

            ReconnectAttemptCount++;
            SetState(ClientReconnectState.Reconnecting);
        }

        /// <summary>
        /// Builds a 64-byte wire ReconnectRequest for channel C0 (opcode = 12).
        /// Zero-GC allocation on hot path.
        /// </summary>
        public bool TryBuildReconnectRequest(Span<byte> buffer, out int written)
        {
            if (State != ClientReconnectState.Reconnecting || buffer.Length < ReconnectRequest.SizeBytes)
            {
                written = 0;
                return false;
            }

            var secret = Secret;
            var request = new ReconnectRequest(SessionId, in secret, LastAppliedTick);
            return ReconnectWireCodec.TryEncodeRequest(request, buffer, out written);
        }

        /// <summary>
        /// Processes authoritative C0 ReconnectResponse from the server.
        /// </summary>
        public void OnReconnectResponse(in ReconnectResponse response)
        {
            if (response.Result == ReconnectResult.Accepted)
            {
                ActiveKeyframeSeq = response.ActiveKeyframeSeq;
                ServerTick = response.ServerTick;

                if (response.AssignedPlayer != default)
                {
                    PlayerId = response.AssignedPlayer;
                }

                if (response.Match != default)
                {
                    MatchId = response.Match;
                }

                _replicationReceiver?.PrepareForResync(response.ActiveKeyframeSeq);
                ResyncStarted?.Invoke(response.ActiveKeyframeSeq);
                SetState(ClientReconnectState.Resyncing);
            }
            else
            {
                SetState(ClientReconnectState.Terminated);
            }
        }

        /// <summary>
        /// Invoked when the client replication layer has assembled and installed the baseline keyframe.
        /// Starts the 5-second countdown timer per OD-20.
        /// </summary>
        public void OnKeyframeInstalled()
        {
            if (State == ClientReconnectState.Resyncing)
            {
                CountdownRemainingSeconds = DefaultCountdownSeconds;
                SetState(ClientReconnectState.Countdown);
            }
        }

        /// <summary>
        /// Advances the 5-second countdown timer. When timer expires (<= 0), transitions
        /// to Connected state and outputs readyToResume = true.
        /// </summary>
        public void AdvanceCountdown(float deltaSeconds, out bool readyToResume)
        {
            if (State == ClientReconnectState.Countdown)
            {
                CountdownRemainingSeconds -= deltaSeconds;
                if (CountdownRemainingSeconds <= 0f)
                {
                    CountdownRemainingSeconds = 0f;
                    SetState(ClientReconnectState.Connected);
                    ReadyToResume?.Invoke();
                    readyToResume = true;
                }
                else
                {
                    readyToResume = false;
                }
            }
            else
            {
                readyToResume = (State == ClientReconnectState.Connected);
            }
        }

        private void SetState(ClientReconnectState newState)
        {
            if (State == newState)
            {
                return;
            }

            State = newState;
            StateChanged?.Invoke(newState);
        }
    }
}
