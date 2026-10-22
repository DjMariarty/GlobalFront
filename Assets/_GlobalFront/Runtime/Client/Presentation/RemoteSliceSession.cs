using System;
using GlobalFront.Client.Replication;
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

        private double _smoothedRttMs;
        private int _rttSampleCount;
        private long _lastPingMs;
        private bool _hasSentPing;
        private bool _disposed;

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

            _onPong = OnPongReceived;
            _onAttached = OnAttached;
            _onLost = OnLost;
            _onDenied = OnDenied;

            Endpoint.PongReceived += _onPong;
            Endpoint.Attached += _onAttached;
            Endpoint.Lost += _onLost;
            Endpoint.Denied += _onDenied;
        }

        public INetworkCarrier Carrier => _carrier;

        public ITransportClock Clock => _clock;

        public ClientTransportEndpoint Endpoint { get; }

        public ClientTransportUplink Uplink { get; }

        public NetworkCommandChannel CommandChannel { get; }

        public ClientReplicationReceiver Receiver => _receiver;

        public ClientTransportReplicationBridge Bridge => _bridge;

        public ClientReplicationWorld World => _receiver.World;

        /// <summary>Latest EWMA-smoothed round-trip time in milliseconds.</summary>
        public int SmoothedRttMs { get; private set; }

        /// <summary>RTT samples accepted since construction.</summary>
        public int RttSampleCount => _rttSampleCount;

        /// <summary>Time of the last <see cref="Pump"/> call.</summary>
        public long LastPumpMs { get; private set; }

        public bool IsConnected => Endpoint.State == ClientTransportState.Established;

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
            Endpoint.PongReceived -= _onPong;
            Endpoint.Attached -= _onAttached;
            Endpoint.Lost -= _onLost;
            Endpoint.Denied -= _onDenied;

            Endpoint.GracefulDisconnect();
            _carrier.Dispose();
        }

        private void OnAttached()
        {
            Connected?.Invoke();
        }

        private void OnLost(TransportDisconnectReason reason)
        {
            Disconnected?.Invoke(reason);
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
