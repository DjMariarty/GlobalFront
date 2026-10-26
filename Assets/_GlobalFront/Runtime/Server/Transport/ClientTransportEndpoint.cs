using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Reconnect;
using GlobalFront.Server.Snapshot;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Client-side transport attachment (Phase 2.5, ADR-009). Performs the
    /// handshake, holds the server-assigned identity
    /// (SessionId/MatchId/PlayerId/token), submits command messages and
    /// surfaces authoritative <see cref="CommandAckPayload"/> results and
    /// opaque snapshot payloads. Engine-independent: pumping is driven by
    /// the client runtime (Unity Update or a test loop) on a single thread.
    /// </summary>
    public enum ClientTransportState : byte
    {
        Idle = 0,
        Connecting = 1,
        Established = 2,
        Disconnected = 3
    }

    public sealed class ClientTransportEndpoint
    {
        /// <summary>
        /// Size of one reusable snapshot payload buffer: a message payload can
        /// never exceed the transport message bound minus the message header
        /// and the C2 SnapshotTick envelope field.
        /// </summary>
        private static readonly int SnapshotBufferBytes =
            TransportProtocol.MaxMessageBytes - TransportProtocol.MessageHeaderSize -
            TransportProtocol.SnapshotTickSize;

        /// <summary>
        /// Depth of the reusable snapshot payload ring (audit P1-3): instead of
        /// allocating <c>new byte[payloadLength]</c> per incoming packet, every
        /// <see cref="SnapshotReceived"/> dispatch copies into the next slot.
        /// Depth 4 lets a consumer retain a handful of payloads (test rigs do)
        /// while the steady-state hot path stays allocation-free.
        /// </summary>
        private const int SnapshotBufferCount = 4;

        private readonly INetworkCarrier _carrier;
        private readonly byte[] _messageBuffer =
            new byte[TransportProtocol.MaxMessageBytes];
        private readonly byte[][] _snapshotBuffers = new byte[SnapshotBufferCount][];
        private readonly byte[] _reconnectRequestBuffer = new byte[ReconnectRequest.SizeBytes];
        private readonly byte[] _readyToResumeBuffer = new byte[ClientReadyToResumeCodec.SizeBytes];

        private int _snapshotBufferIndex;
        private int _connectionId;
        private bool _connectRequested;
        private string _lastHost;
        private int _lastPort;
        private bool _isReconnecting;
        private bool _connectingViaReconnect;
        private ulong _reconnectTick;
        private long _connectingStartedMs;

        /// <summary>
        /// How long a socket may stay in <see cref="ClientTransportState.Connecting"/>
        /// before the endpoint aborts it and returns to Disconnected. A lost
        /// connect/handshake packet otherwise pins the reconnect policy
        /// forever, because every retry is refused while Connecting.
        /// </summary>
        public long ConnectTimeoutMs { get; set; } = 2000;

        public ClientTransportEndpoint(INetworkCarrier carrier)
        {
            _carrier = carrier ?? throw new ArgumentNullException(nameof(carrier));

            for (var index = 0; index < SnapshotBufferCount; index++)
            {
                _snapshotBuffers[index] = new byte[SnapshotBufferBytes];
            }
        }

        public ClientTransportState State { get; private set; } = ClientTransportState.Idle;

        public SessionId Session { get; private set; }

        public MatchId Match { get; private set; }

        public PlayerId Player { get; private set; }

        public ulong Token { get; private set; }
        public SessionSecret32 Secret { get; private set; }

        /// <summary>Raised when the server issues the 32-byte session secret (OD-18 / ADR-011).</summary>
        public event Action<SessionSecret32> SecretReceived;

        /// <summary>Raised when the server answers a ReconnectRequest (opcode 13).</summary>
        public event Action<ReconnectResponse> ReconnectResponseReceived;

        /// <summary>Raised when the handshake completes and identity is assigned.</summary>
        public event Action Attached;

        public event Action<ConnectDenyReason> Denied;

        /// <summary>Authoritative command result (asynchronous, ADR-009 P2-3).</summary>
        public event Action<CommandAckPayload> CommandAckReceived;

        /// <summary>Opaque Snapshot Protocol v1 payload + envelope SnapshotTick.</summary>
        /// <remarks>
        /// The payload is one of <see cref="SnapshotBufferCount"/> rotating
        /// preallocated buffers, handed out with its live length: it is valid
        /// during the handler call and until
        /// <see cref="SnapshotBufferCount"/> further snapshots arrive on this
        /// endpoint. Consumers decode synchronously (the replication bridge
        /// does); anything that must outlive the call copies the bytes.
        /// </remarks>
        public event Action<ulong, byte[], int> SnapshotReceived;

        public event Action<TransportDisconnectReason> Lost;

        /// <summary>
        /// Echo timestamp of a server Pong (OD-47). The payload is the
        /// timestamp this client put in its own Ping, so RTT is measured
        /// entirely on the client's monotonic clock.
        /// </summary>
        public event Action<long> PongReceived;

        public INetworkCarrier Carrier => _carrier;

        public void Connect(string host, int port)
        {
            if (State != ClientTransportState.Idle)
            {
                return;
            }

            _lastHost = host;
            _lastPort = port;
            _connectingStartedMs = _carrier.Clock.NowMs;
            _connectingViaReconnect = false;
            _connectionId = _carrier.ConnectToServer(host, port);
            State = ClientTransportState.Connecting;
        }

        /// <summary>
        /// Opens a replacement socket for the already-established session and
        /// sends the C0 ReconnectRequest as soon as the carrier reports the
        /// new connection. The endpoint keeps its SessionId and secret; the
        /// server answers with a fresh token that the response handler adopts.
        /// </summary>
        public void Reconnect(ulong lastAppliedTick) =>
            Reconnect(_lastHost, _lastPort, lastAppliedTick);

        public void Reconnect(string host, int port, ulong lastAppliedTick)
        {
            // The state guard, not _isReconnecting: a connect timeout revives
            // the retry path while _isReconnecting is still true (the next
            // Connected event must send a ReconnectRequest, not a handshake).
            // Idle is deliberately allowed: the retry policy never reaches it,
            // and a session whose coordinator was primed without a socket may
            // still open one; a request without a bound session is refused by
            // the server.
            if (State == ClientTransportState.Established ||
                State == ClientTransportState.Connecting)
            {
                return;
            }

            _lastHost = host;
            _lastPort = port;
            _reconnectTick = lastAppliedTick;
            _isReconnecting = true;
            _connectRequested = false;
            _connectingStartedMs = _carrier.Clock.NowMs;
            _connectingViaReconnect = true;
            State = ClientTransportState.Connecting;
            _connectionId = _carrier.ConnectToServer(host, port);
        }

        /// <summary>Advances the carrier and dispatches received events.</summary>
        public void Pump(long nowMs)
        {
            _carrier.Pump(nowMs);
            while (_carrier.TryDequeueEvent(out var transportEvent))
            {
                try
                {
                    HandleEvent(transportEvent);
                }
                finally
                {
                    _carrier.ReturnEventBuffer(transportEvent.Data);
                }
            }

            // Abort a reconnect socket whose connect/handshake never
            // completed: a lost connect packet would otherwise pin the
            // endpoint in Connecting and block every retry. The initial
            // handshake is deliberately not timed out — it has no retry path,
            // so Disconnected would only strand the caller. The Disconnected
            // event this close enqueues is drained next pump and is a no-op
            // because the state is already Disconnected.
            if (State == ClientTransportState.Connecting &&
                _connectingViaReconnect &&
                nowMs - _connectingStartedMs >= ConnectTimeoutMs)
            {
                _connectingViaReconnect = false;
                _carrier.CloseConnection(_connectionId, TransportDisconnectReason.Timeout);
                State = ClientTransportState.Disconnected;
            }
        }

        /// <summary>
        /// Queues an encoded wire command for delivery. The returned value
        /// reflects local pre-flight only; authoritative acceptance arrives
        /// later via <see cref="CommandAckReceived"/>.
        /// </summary>
        public bool TrySendCommand(byte[] wireCommand, int length)
        {
            if (State != ClientTransportState.Established || length <= 0)
            {
                return false;
            }

            var offset = MessageCodec.WriteHeader(
                _messageBuffer, 0, TransportMessageType.Command, Token, 0);
            Array.Copy(wireCommand, 0, _messageBuffer, offset, length);
            return _carrier.Send(
                _connectionId, TransportChannel.Command, _messageBuffer, 0, offset + length);
        }

        /// <summary>
        /// Queues one client→server replication feedback record (a 34-byte
        /// SnapshotAck or a repair request, Phase 2.6 step 2.6.4) for delivery
        /// on the reliable control channel. The returned value reflects local
        /// pre-flight only; the record itself is the authoritative content.
        /// </summary>
        public bool TrySendReplicationFeedback(
            TransportMessageType type,
            byte[] payload,
            int length)
        {
            if (State != ClientTransportState.Established ||
                length <= 0 ||
                (type != TransportMessageType.SnapshotAck &&
                    type != TransportMessageType.ReplicationRequest))
            {
                return false;
            }

            var offset = MessageCodec.WriteHeader(_messageBuffer, 0, type, Token, 0);
            Array.Copy(payload, 0, _messageBuffer, offset, length);
            return _carrier.Send(
                _connectionId, TransportChannel.Control, _messageBuffer, 0, offset + length);
        }

        /// <summary>
        /// Queues one client→server Ping on the reliable control channel. The
        /// timestamp is echoed back verbatim by the server, so the caller owns
        /// the RTT measurement. Returns false while the handshake has not
        /// completed: there is no session token to attribute the packet to.
        /// </summary>
        public bool TrySendPing(long clientSentTimestampMs)
        {
            // ClientTransportState has no Reconnecting state (Phase 2.7
            // reconnects are driven by ClientReconnectCoordinator and a fresh
            // handshake), so Established is the only ping-capable state.
            if (State != ClientTransportState.Established)
            {
                return false;
            }

            var offset = MessageCodec.WriteHeader(
                _messageBuffer, 0, TransportMessageType.Ping, Token, 0);
            offset = MessageCodec.EncodePingPong(_messageBuffer, offset, clientSentTimestampMs);
            return _carrier.Send(
                _connectionId, TransportChannel.Control, _messageBuffer, 0, offset);
        }

        /// <summary>
        /// Sends a standalone C0 ClientReadyToResume frame (opcode 14) after
        /// the post-reconnect countdown (OD-20). The buffer is preallocated;
        /// only an established session with a valid identity can signal.
        /// </summary>
        public bool TrySendReadyToResume()
        {
            if (State != ClientTransportState.Established ||
                !Session.IsValid ||
                !ClientReadyToResumeCodec.TryEncode(Session, _readyToResumeBuffer, out var written))
            {
                return false;
            }

            return _carrier.Send(
                _connectionId, TransportChannel.Control, _readyToResumeBuffer, 0, written);
        }

        public void GracefulDisconnect()
        {
            if (State == ClientTransportState.Disconnected || State == ClientTransportState.Idle)
            {
                return;
            }

            if (State == ClientTransportState.Established)
            {
                var offset = MessageCodec.WriteHeader(
                    _messageBuffer, 0, TransportMessageType.Disconnect, Token, 0);
                offset = MessageCodec.EncodeDisconnect(
                    _messageBuffer, offset, TransportDisconnectReason.ClientRequested);
                _carrier.Send(
                    _connectionId, TransportChannel.Control, _messageBuffer, 0, offset);
            }

            _carrier.CloseConnection(_connectionId, TransportDisconnectReason.ClientRequested);
            State = ClientTransportState.Disconnected;
        }

        /// <summary>
        /// Drops connection without sending graceful disconnect (Phase 2.7, ADR-011 test support).
        /// Triggers grace window on server instead of closing session.
        /// </summary>
        public void DropConnection(TransportDisconnectReason reason = TransportDisconnectReason.TransportLost)
        {
            if (State == ClientTransportState.Disconnected || State == ClientTransportState.Idle)
            {
                return;
            }

            _carrier.CloseConnection(_connectionId, reason);
            State = ClientTransportState.Disconnected;
            Lost?.Invoke(reason);
        }

        private void HandleEvent(TransportEvent transportEvent)
        {
            switch (transportEvent.Type)
            {
                case TransportEventType.Connected:
                    if (_isReconnecting)
                    {
                        SendReconnectRequest();
                        _isReconnecting = false;
                    }
                    else
                    {
                        SendConnectRequest();
                    }

                    return;

                case TransportEventType.Disconnected:
                    if (State != ClientTransportState.Disconnected)
                    {
                        State = ClientTransportState.Disconnected;
                        Lost?.Invoke(transportEvent.Reason);
                    }

                    return;

                case TransportEventType.Message:
                    HandleMessage(transportEvent);
                    return;
            }
        }

        private void SendConnectRequest()
        {
            if (_connectRequested)
            {
                return;
            }

            _connectRequested = true;
            var offset = MessageCodec.WriteHeader(
                _messageBuffer,
                0,
                TransportMessageType.ConnectRequest,
                TransportProtocol.HandshakeToken,
                0);
            offset = MessageCodec.EncodeConnectRequest(
                _messageBuffer, offset, SnapshotProtocol.Version, false, Guid.Empty);
            _carrier.Send(
                _connectionId, TransportChannel.Control, _messageBuffer, 0, offset);
        }

        private void SendReconnectRequest()
        {
            var secret = Secret;
            var request = new ReconnectRequest(Session, in secret, _reconnectTick);
            if (!ReconnectWireCodec.TryEncodeRequest(request, _reconnectRequestBuffer, out var written))
            {
                return;
            }

            _carrier.Send(
                _connectionId, TransportChannel.Control, _reconnectRequestBuffer, 0, written);
        }

        private void HandleMessage(TransportEvent transportEvent)
        {
            if (transportEvent.Length == ReconnectWireCodec.SecretMessageSizeBytes &&
                transportEvent.Data != null &&
                transportEvent.Data[0] == ReconnectWireCodec.SecretOpcode)
            {
                if (ReconnectWireCodec.TryDecodeSecretMessage(
                        transportEvent.Data.AsSpan(0, transportEvent.Length), out var secretMsg))
                {
                    Secret = secretMsg.Secret;
                    SecretReceived?.Invoke(Secret);
                }
                return;
            }

            if (transportEvent.Length >= ReconnectWireCodec.ResponseSizeBytes &&
                transportEvent.Data != null &&
                transportEvent.Data[0] == ReconnectWireCodec.ResponseOpcode)
            {
                if (ReconnectWireCodec.TryDecodeResponse(
                        transportEvent.Data.AsSpan(0, transportEvent.Length), out var response))
                {
                    if (response.Result == ReconnectResult.Accepted)
                    {
                        State = ClientTransportState.Established;
                        _connectingViaReconnect = false;
                        if (response.Token != 0)
                        {
                            Token = response.Token;
                            _carrier.AssignSessionToken(_connectionId, response.Token);
                        }
                        if (response.AssignedPlayer != default)
                        {
                            Player = response.AssignedPlayer;
                        }
                        if (response.Match != default)
                        {
                            Match = response.Match;
                        }
                    }
                    ReconnectResponseReceived?.Invoke(response);
                }
                return;
            }

            var error = MessageCodec.TryDecodeHeader(
                transportEvent.Data, 0, transportEvent.Length, out var header);
            if (error != MessageError.None)
            {
                _carrier.Metrics.DroppedMalformed++;
                return;
            }

            switch (header.Type)
            {
                case TransportMessageType.ConnectAccept:
                    if (MessageCodec.TryDecodeConnectAccept(
                            transportEvent.Data,
                            header.PayloadOffset,
                            transportEvent.Length,
                            out var accept))
                    {
                        Token = accept.SessionToken;
                        Session = accept.Session;
                        Match = accept.Match;
                        Player = accept.Player;
                        State = ClientTransportState.Established;
                        _carrier.AssignSessionToken(_connectionId, Token);
                        Attached?.Invoke();
                    }

                    return;

                case TransportMessageType.ConnectDenied:
                    var reason = header.PayloadLength > 0
                        ? (ConnectDenyReason)transportEvent.Data[header.PayloadOffset]
                        : ConnectDenyReason.Internal;
                    State = ClientTransportState.Disconnected;
                    Denied?.Invoke(reason);
                    return;

                case TransportMessageType.CommandAck:
                    if (MessageCodec.TryDecodeCommandAck(
                            transportEvent.Data,
                            header.PayloadOffset,
                            transportEvent.Length,
                            out var ack))
                    {
                        CommandAckReceived?.Invoke(ack);
                    }

                    return;

                case TransportMessageType.Snapshot:
                    var payloadLength = header.PayloadLength;
                    if (payloadLength < 0 || payloadLength > SnapshotBufferBytes)
                    {
                        _carrier.Metrics.DroppedMalformed++;
                        return;
                    }

                    // Zero-GC dispatch (audit P1-3): copy into the next pooled
                    // slot instead of allocating per packet. The event handler
                    // receives a buffer reused after SnapshotBufferCount more
                    // snapshots (see the event remarks).
                    var payload = _snapshotBuffers[_snapshotBufferIndex];
                    _snapshotBufferIndex = (_snapshotBufferIndex + 1) % SnapshotBufferCount;
                    Array.Copy(
                        transportEvent.Data, header.PayloadOffset, payload, 0, payloadLength);
                    SnapshotReceived?.Invoke(header.SnapshotTick, payload, payloadLength);
                    return;

                case TransportMessageType.Ping:
                    if (MessageCodec.TryDecodePingPong(
                            transportEvent.Data,
                            header.PayloadOffset,
                            transportEvent.Length,
                            out var timestamp))
                    {
                        var offset = MessageCodec.WriteHeader(
                            _messageBuffer, 0, TransportMessageType.Pong, Token, 0);
                        offset = MessageCodec.EncodePingPong(_messageBuffer, offset, timestamp);
                        _carrier.Send(
                            _connectionId, TransportChannel.Control, _messageBuffer, 0, offset);
                    }

                    return;

                case TransportMessageType.Pong:
                    if (MessageCodec.TryDecodePingPong(
                            transportEvent.Data,
                            header.PayloadOffset,
                            transportEvent.Length,
                            out var originalTimestamp))
                    {
                        PongReceived?.Invoke(originalTimestamp);
                    }

                    return;

                default:
                    _carrier.Metrics.DroppedMalformed++;
                    return;
            }
        }
    }
}
