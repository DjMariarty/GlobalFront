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

        private int _snapshotBufferIndex;
        private int _connectionId;
        private bool _connectRequested;

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

        public INetworkCarrier Carrier => _carrier;

        public void Connect(string host, int port)
        {
            if (State != ClientTransportState.Idle)
            {
                return;
            }

            _connectionId = _carrier.ConnectToServer(host, port);
            State = ClientTransportState.Connecting;
        }

        /// <summary>Advances the carrier and dispatches received events.</summary>
        public void Pump(long nowMs)
        {
            _carrier.Pump(nowMs);
            while (_carrier.TryDequeueEvent(out var transportEvent))
            {
                HandleEvent(transportEvent);
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
                    SendConnectRequest();
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

                default:
                    _carrier.Metrics.DroppedMalformed++;
                    return;
            }
        }
    }
}
