using System;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Reconnect;
using GlobalFront.Server.Transport;

namespace GlobalFront.Client
{
    /// <summary>
    /// Client-side network session of the tactical slice (Phase 4, step 4.2,
    /// ADR-014 / OD-38, OD-47): everything a remote client needs between a
    /// UDP socket and the presentation stack in one object.
    ///
    /// It composes the transport endpoint, the replication uplink, the
    /// receiver/bridge pair that turns C2 payloads into
    /// <see cref="ClientReplicationWorld"/> state, the C1 command channel,
    /// and the RTT loop. The slice runner addresses these parts directly, so
    /// a dedicated client has exactly one owner for the connection and no
    /// code path that could accidentally create a second one.
    ///
    /// RTT (OD-47): the client pings once per second on C0 with its own
    /// monotonic millisecond timestamp; the server echoes it; the sample is
    /// <c>clientNowMs - originalTimestamp</c> and is smoothed with the
    /// standard EWMA factor 0.125. No server clock ever enters the
    /// arithmetic — the wire carries only the client's own stamp.
    /// </summary>
    public sealed class RemoteSliceSession : IDisposable
    {
        /// <summary>Ping cadence on an established connection.</summary>
        public const int PingIntervalMs = 1000;

        /// <summary>EWMA factor for the smoothed RTT.</summary>
        public const double RttSmoothingFactor = 0.125;

        private readonly INetworkCarrier _carrier;
        private readonly ITransportClock _clock;
        private readonly ClientReplicationReceiver _receiver;
        private readonly ClientTransportReplicationBridge _bridge;
        private readonly Action<long> _onPong;
        private readonly Action _onAttached;
        private readonly Action<TransportDisconnectReason> _onLost;
        private readonly Action<ConnectDenyReason> _onDenied;
        private readonly Action<SessionSecret32> _onSecretReceived;
        private readonly Action<ReconnectResponse> _onReconnectResponse;
        private readonly Action _onReadyToResume;

        private double _smoothedRttMs;
        private int _rttSampleCount;
        private long _lastPingMs;
        private bool _hasSentPing;
        private bool _disposed;
        private long _lastReconnectAttemptMs;
        private bool _hasReconnectAttempt;
        private bool _readyToResumePending;
        private long _lastCountdownPumpMs;

        /// <summary>
        /// Builds a session over the supplied carrier and clock. Both are
        /// optional: with no carrier the session owns a LiteNetLib client
        /// side, and with no clock it owns a monotonic stopwatch clock.
        /// </summary>
        public RemoteSliceSession(INetworkCarrier carrier = null, ITransportClock clock = null)
        {
            _clock = clock ?? new SystemTransportClock();
            _carrier = carrier ?? LiteNetLibCarrier.CreateClient(_clock);

            Endpoint = new ClientTransportEndpoint(_carrier);
            Uplink = new ClientTransportUplink(Endpoint);
            CommandChannel = new NetworkCommandChannel(Endpoint);

            _receiver = new ClientReplicationReceiver(ClientReplicationWorld.DefaultCapacity);

            // A 1024-unit roster can legitimately remove more than the bridge's
            // 512-entry default sink in one tick; the slice's own bridge is sized
            // to the world capacity for the same reason.
            var bridgeConfig = new ClientTransportReplicationBridgeConfig(
                ClientTransportReplicationBridgeConfig.DefaultMaxKeyframeUnits,
                ClientTransportReplicationBridgeConfig.DefaultMaxParts,
                ClientTransportReplicationBridgeConfig.DefaultMaxSlicePayloadBytes,
                addSink: ClientReplicationWorld.DefaultCapacity,
                updateSink: ClientReplicationWorld.DefaultCapacity,
                removeSink: ClientReplicationWorld.DefaultCapacity);
            _bridge = new ClientTransportReplicationBridge(Uplink, _receiver, in bridgeConfig);

            ReconnectCoordinator = new ClientReconnectCoordinator(_receiver);
            _onSecretReceived = OnSecretReceived;
            _onReconnectResponse = OnReconnectResponse;
            _onReadyToResume = OnCoordinatorReadyToResume;

            _onPong = OnPongReceived;
            _onAttached = OnAttached;
            _onLost = OnLost;
            _onDenied = OnDenied;

            Endpoint.PongReceived += _onPong;
            Endpoint.Attached += _onAttached;
            Endpoint.Lost += _onLost;
            Endpoint.Denied += _onDenied;
            Endpoint.SecretReceived += _onSecretReceived;
            Endpoint.ReconnectResponseReceived += _onReconnectResponse;
            ReconnectCoordinator.ReadyToResume += _onReadyToResume;
        }

        public INetworkCarrier Carrier => _carrier;

        public ITransportClock Clock => _clock;

        public ClientTransportEndpoint Endpoint { get; }

        public ClientTransportUplink Uplink { get; }

        public NetworkCommandChannel CommandChannel { get; }

        public ClientReplicationReceiver Receiver => _receiver;

        public ClientTransportReplicationBridge Bridge => _bridge;

        public ClientReconnectCoordinator ReconnectCoordinator { get; }

        public ClientReplicationWorld World => _receiver.World;

        /// <summary>Latest EWMA-smoothed round-trip time in milliseconds.</summary>
        public int SmoothedRttMs { get; private set; }

        /// <summary>RTT samples accepted since construction.</summary>
        public int RttSampleCount => _rttSampleCount;

        /// <summary>Time of the last <see cref="Pump"/> call.</summary>
        public long LastPumpMs { get; private set; }

        public bool IsConnected => Endpoint.State == ClientTransportState.Established;

        /// <summary>
        /// When true, <see cref="Pump"/> retries a lost session automatically
        /// once the coordinator is <see cref="ClientReconnectState.Reconnecting"/>
        /// and the endpoint is back in Disconnected (either because the
        /// replacement socket failed or because its connect timed out).
        /// </summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>Minimum milliseconds between automatic reconnect attempts.</summary>
        public long ReconnectRetryIntervalMs { get; set; } = 500;

        /// <summary>Automatic attempts made in the current loss episode.</summary>
        public int ReconnectAttemptCount { get; private set; }

        /// <summary>
        /// True while a completed reconnect countdown still owes the server a
        /// ClientReadyToResume signal. Cleared once a send is accepted.
        /// </summary>
        public bool HasPendingReadyToResume => _readyToResumePending;

        public event Action<int> RttUpdated;

        public event Action Connected;

        public event Action<TransportDisconnectReason> Disconnected;

        /// <summary>
        /// Opens the transport connection. The handshake itself completes on
        /// a later <see cref="Pump"/>; <see cref="Connected"/> is raised when
        /// the server's accept arrives.
        /// </summary>
        public void Connect(string host, int port)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RemoteSliceSession));
            }

            _hasSentPing = false;
            _lastPingMs = 0;
            Endpoint.Connect(host, port);
        }

        /// <summary>
        /// Starts one reconnect attempt for a lost session: opens a replacement
        /// socket and lets the endpoint send the cached-secret C0 request on
        /// connect. The coordinator must already be in
        /// <see cref="ClientReconnectState.Reconnecting"/>, which its transport
        /// loss handler sets. Retry pacing stays with the hosting layer — this
        /// call is one attempt, not a loop.
        /// </summary>
        public bool TryReconnect()
        {
            if (_disposed ||
                ReconnectCoordinator.State != ClientReconnectState.Reconnecting ||
                Endpoint.State == ClientTransportState.Connecting ||
                Endpoint.State == ClientTransportState.Established)
            {
                return false;
            }

            ReconnectCoordinator.LastAppliedTick = _receiver.LastAppliedTick;
            Endpoint.Reconnect(ReconnectCoordinator.LastAppliedTick);
            return true;
        }

        /// <summary>
        /// One session frame: drain the endpoint (which pumps the carrier),
        /// advance the replication bridge, then send a Ping when the 1 Hz
        /// cadence is due. The endpoint pumps the carrier itself — pumping it
        /// here as well would run the carrier's retransmission timers twice
        /// per frame.
        /// </summary>
        public void Pump(long nowMs)
        {
            if (_disposed)
            {
                return;
            }

            LastPumpMs = nowMs;
            Endpoint.Pump(nowMs);
            _bridge.Pump(nowMs);
            TryAutoReconnect(nowMs);
            TickReconnectCountdown(nowMs);
            TryFlushReadyToResume();

            if (Endpoint.State != ClientTransportState.Established)
            {
                return;
            }

            if (_hasSentPing && nowMs - _lastPingMs < PingIntervalMs)
            {
                return;
            }

            if (Endpoint.TrySendPing(nowMs))
            {
                _hasSentPing = true;
                _lastPingMs = nowMs;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _readyToResumePending = false;
            _lastCountdownPumpMs = 0;
            Endpoint.PongReceived -= _onPong;
            Endpoint.Attached -= _onAttached;
            Endpoint.Lost -= _onLost;
            Endpoint.Denied -= _onDenied;
            Endpoint.SecretReceived -= _onSecretReceived;
            Endpoint.ReconnectResponseReceived -= _onReconnectResponse;
            ReconnectCoordinator.ReadyToResume -= _onReadyToResume;

            ReconnectCoordinator.Dispose();
            Endpoint.GracefulDisconnect();
            _carrier.Dispose();
        }

        private void OnAttached()
        {
            if (Endpoint.Secret != default && Endpoint.Player.IsValid)
            {
                var secret = Endpoint.Secret;
                ReconnectCoordinator.CacheSession(
                    Endpoint.Session, Endpoint.Match, Endpoint.Player, in secret);
            }

            Connected?.Invoke();
        }

        private void OnLost(TransportDisconnectReason reason)
        {
            // A signal owed to the old socket is moot: the next reconnect
            // cycle installs a new keyframe and raises a fresh ReadyToResume.
            _readyToResumePending = false;
            _lastCountdownPumpMs = 0;
            _hasReconnectAttempt = false;
            ReconnectAttemptCount = 0;
            ReconnectCoordinator.OnTransportDisconnected(reason);
            Disconnected?.Invoke(reason);
        }

        private void TryAutoReconnect(long nowMs)
        {
            if (!AutoReconnect ||
                ReconnectCoordinator.State != ClientReconnectState.Reconnecting ||
                Endpoint.State != ClientTransportState.Disconnected)
            {
                return;
            }

            if (_hasReconnectAttempt &&
                nowMs - _lastReconnectAttemptMs < ReconnectRetryIntervalMs)
            {
                return;
            }

            _hasReconnectAttempt = true;
            _lastReconnectAttemptMs = nowMs;
            ReconnectAttemptCount++;
            TryReconnect();
        }

        private void OnSecretReceived(SessionSecret32 secret)
        {
            if (Endpoint.State == ClientTransportState.Established && Endpoint.Player.IsValid)
            {
                ReconnectCoordinator.CacheSession(
                    Endpoint.Session, Endpoint.Match, Endpoint.Player, in secret);
            }
        }

        private void OnReconnectResponse(ReconnectResponse response)
        {
            ReconnectCoordinator.OnReconnectResponse(in response);
            if (response.Result == ReconnectResult.Accepted)
            {
                _bridge.PrepareForResync(response.ActiveKeyframeSeq);
            }
        }

        private void OnCoordinatorReadyToResume()
        {
            // The countdown is client-local; the server learns about its
            // completion only through the authoritative C0 signal. Pump owns
            // the send path so one frame can never attempt it twice.
            _readyToResumePending = true;
        }

        private void TickReconnectCountdown(long nowMs)
        {
            if (ReconnectCoordinator.State != ClientReconnectState.Countdown)
            {
                _lastCountdownPumpMs = 0;
                return;
            }

            if (_lastCountdownPumpMs == 0)
            {
                _lastCountdownPumpMs = nowMs;
                return;
            }

            var elapsedMs = nowMs - _lastCountdownPumpMs;
            _lastCountdownPumpMs = nowMs;
            if (elapsedMs <= 0)
            {
                return;
            }

            // The session owns the OD-20 clock: no presentation or test layer
            // may advance this countdown a second time.
            ReconnectCoordinator.AdvanceCountdown(elapsedMs / 1000f, out _);
        }

        private void TryFlushReadyToResume()
        {
            if (!_readyToResumePending)
            {
                return;
            }

            // A local send can fail (queue/connection race). The reliable
            // channel only guarantees delivery once the frame is queued, so
            // keep the obligation and retry on the next frame until accepted.
            if (Endpoint.TrySendReadyToResume())
            {
                _readyToResumePending = false;
            }
        }

        private void OnDenied(ConnectDenyReason reason)
        {
            Disconnected?.Invoke(TransportDisconnectReason.Rejected);
        }

        private void OnPongReceived(long originalTimestamp)
        {
            // Strictly the client's own clock: the server merely echoed the
            // stamp this endpoint wrote.
            var sample = _clock.NowMs - originalTimestamp;
            if (sample < 0)
            {
                return;
            }

            if (_rttSampleCount == 0)
            {
                _smoothedRttMs = sample;
            }
            else
            {
                _smoothedRttMs =
                    (1.0 - RttSmoothingFactor) * _smoothedRttMs +
                    RttSmoothingFactor * sample;
            }

            _rttSampleCount++;
            SmoothedRttMs = (int)Math.Floor(_smoothedRttMs + 0.5);
            RttUpdated?.Invoke(SmoothedRttMs);
        }
    }
}
