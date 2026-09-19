using System;
using System.Collections.Generic;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Reconnect;
using GlobalFront.Core.Simulation;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Snapshot;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Server-side transport orchestrator of Phase 2.5 (ADR-009). Connects
    /// the carrier to the existing authoritative boundary WITHOUT owning any
    /// simulation: commands flow through the unchanged Phase 2.4 session
    /// gate into the unchanged <see cref="MatchServer"/>; snapshots flow out
    /// as opaque Snapshot Protocol v1 payloads. Host/simulation thread only
    /// (concurrency invariant) — call <see cref="Pump"/> from the host pump.
    /// </summary>
    public sealed class ServerTransportHost
    {
        private readonly INetworkCarrier _carrier;
        private readonly SessionManager _sessions;
        private readonly MatchServer _server;
        private readonly TransportSessionBinder _binder = new TransportSessionBinder();
        private readonly byte[] _messageBuffer =
            new byte[TransportProtocol.MaxMessageBytes];
        private ulong _currentDrainTick;
        private struct PendingCommandAck
        {
            public int ConnectionId;
            public byte[] Buffer;
            public int Length;
        }
        private readonly List<PendingCommandAck> _pendingAcks = new List<PendingCommandAck>();

        /// <summary>
        /// Optional match every accepted session auto-joins during handshake
        /// (prototype/vertical-slice mode; lobby/matchmaking stay out of
        /// scope for Phase 2.5).
        /// </summary>
        public MatchId AutoJoinMatch;

        /// <summary>Raised on the host thread when a session finishes handshake.</summary>
        public event Action<SessionId, MatchId, PlayerId> SessionAttached;

        /// <summary>Raised on the host thread when a session successfully re-attaches (Phase 2.7, ADR-011).</summary>
        public event Action<SessionId, MatchId, PlayerId> SessionReattached;

        /// <summary>Optional provider for active keyframe sequence generation on reconnect response.</summary>
        public Func<SessionId, ushort> KeyframeSeqProvider { get; set; }

        /// <summary>Raised on the host thread when a session is lost/closed.</summary>
        public event Action<SessionId, TransportDisconnectReason> SessionDetached;

        /// <summary>
        /// Raised on the host thread for every valid client→server replication
        /// feedback message (SnapshotAck / ReplicationRequest, Phase 2.6 step
        /// 2.6.4). The payload slice is valid only inside the handler call.
        /// </summary>
        public event Action<ReplicationFeedbackMessage> ReplicationFeedbackReceived;

        public ServerTransportHost(
            INetworkCarrier carrier,
            SessionManager sessions,
            MatchServer server)
        {
            _carrier = carrier ?? throw new ArgumentNullException(nameof(carrier));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public INetworkCarrier Carrier => _carrier;

        public TransportSessionBinder Binder => _binder;

        public TransportMetrics Metrics => _carrier.Metrics;

        public int AttachedCount => _binder.BoundCount;

        public void StartServer() => _carrier.StartServer();

        /// <summary>
        /// Advances carrier timers and processes queued receive events.
        /// Host/simulation thread only.
        /// </summary>
        public void Pump(long nowMs) => Pump(nowMs, _server.CurrentTick);

        /// <summary>
        /// Advances carrier timers and processes queued receive events with explicit drain-tick (D1 fix).
        /// Host/simulation thread only.
        /// </summary>
        public void Pump(long nowMs, ulong drainTick)
        {
            _currentDrainTick = drainTick;
            _carrier.Pump(nowMs);
            while (_carrier.TryDequeueEvent(out var transportEvent))
            {
                HandleEvent(transportEvent);
            }
            DrainPendingAcks();
        }

        private void DrainPendingAcks()
        {
            for (var i = _pendingAcks.Count - 1; i >= 0; i--)
            {
                var ack = _pendingAcks[i];
                if (_carrier.Send(ack.ConnectionId, TransportChannel.Control, ack.Buffer, 0, ack.Length))
                {
                    _pendingAcks.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Sends one Snapshot Protocol v1 payload (opaque to the transport)
        /// to the attached session, carried in the C2 envelope with the
        /// SnapshotTick ordering key.
        /// </summary>
        public bool SendSnapshot(SessionId session, ulong snapshotTick, byte[] snapshotBytes, int length)
        {
            if (!_binder.TryGetTokenBySession(session, out var token))
            {
                return false;
            }

            if (!_binder.TryGetByToken(token, out var binding))
            {
                return false;
            }

            var offset = MessageCodec.WriteHeader(
                _messageBuffer, 0, TransportMessageType.Snapshot, token, snapshotTick);
            Array.Copy(snapshotBytes, 0, _messageBuffer, offset, length);
            return _carrier.Send(
                binding.ConnectionId,
                TransportChannel.Snapshot,
                _messageBuffer,
                0,
                offset + length);
        }

        /// <summary>Broadcasts a snapshot to every attached session.</summary>
        public void BroadcastSnapshot(ulong snapshotTick, byte[] snapshotBytes, int length)
        {
            foreach (var pair in _binder.SnapshotTargets)
            {
                SendSnapshot(pair, snapshotTick, snapshotBytes, length);
            }
        }

        /// <summary>Graceful server shutdown: best-effort notice, then close.</summary>
        public void Shutdown()
        {
            foreach (var session in _binder.SnapshotTargets)
            {
                SendDisconnect(session, TransportDisconnectReason.ServerShutdown);
            }

            _carrier.Shutdown(TransportDisconnectReason.ServerShutdown);
            _binder.ReleaseAll();
        }

        // ------------------------------------------------------------------
        // Event handling (host thread)
        // ------------------------------------------------------------------

        private void HandleEvent(TransportEvent transportEvent)
        {
            switch (transportEvent.Type)
            {
                case TransportEventType.Connected:
                    // Await the ConnectRequest message for validation.
                    return;

                case TransportEventType.Disconnected:
                    HandleConnectionLost(transportEvent);
                    return;

                case TransportEventType.Message:
                    HandleMessage(transportEvent);
                    return;
            }
        }

        private void HandleConnectionLost(TransportEvent transportEvent)
        {
            if (!_binder.TryGetByConnection(transportEvent.ConnectionId, out var binding))
            {
                return;
            }

            if (transportEvent.Reason == TransportDisconnectReason.ClientRequested)
            {
                // Graceful remote close (carrier-level notice): terminates the
                // session exactly like the Disconnect message path.
                _sessions.CloseSession(binding.Session);
            }
            else
            {
                // Transport loss feeds the existing Phase 2.4 grace machinery
                // with the current authoritative tick; the binder releases only
                // when the session is closed (grace may still allow reconnect in
                // Phase 2.7).
                var atTick = _currentDrainTick > 0 ? _currentDrainTick : _server.CurrentTick;
                _sessions.NotifyConnectionLost(binding.Session, atTick);
            }

            _binder.Release(binding.Session);
            SessionDetached?.Invoke(binding.Session, transportEvent.Reason);
        }

        private void HandleMessage(TransportEvent transportEvent)
        {
            // Direct C0 opcode check for ReconnectRequest (opcode 12, size 64)
            if (transportEvent.Length >= ReconnectWireCodec.RequestSizeBytes &&
                transportEvent.Data != null &&
                transportEvent.Data[0] == ReconnectWireCodec.RequestOpcode)
            {
                HandleReconnectRequest(transportEvent, 0, transportEvent.Length);
                return;
            }

            var error = MessageCodec.TryDecodeHeader(
                transportEvent.Data, 0, transportEvent.Length, out var header);
            switch (error)
            {
                case MessageError.None:
                    break;
                case MessageError.BadMessageVersion:
                    _carrier.Metrics.DroppedBadVersion++;
                    return;
                default:
                    _carrier.Metrics.DroppedMalformed++;
                    return;
            }

            switch (header.Type)
            {
                case (TransportMessageType)ReconnectWireCodec.RequestOpcode:
                    HandleReconnectRequest(transportEvent, header.PayloadOffset, header.PayloadLength);
                    return;

                case TransportMessageType.ConnectRequest:
                    HandleConnectRequest(transportEvent, header);
                    return;

                case TransportMessageType.Command:
                    HandleCommand(transportEvent, header);
                    return;

                case TransportMessageType.Disconnect:
                    HandleGracefulDisconnect(transportEvent, header);
                    return;

                case TransportMessageType.Ping:
                    HandlePing(transportEvent, header);
                    return;

                case TransportMessageType.SnapshotAck:
                    HandleReplicationFeedback(
                        transportEvent, header, TransportMessageType.SnapshotAck,
                        GlobalFront.Core.Snapshot.SnapshotAckCodec.SizeBytes);
                    return;

                case TransportMessageType.ReplicationRequest:
                    HandleReplicationFeedback(
                        transportEvent, header, TransportMessageType.ReplicationRequest,
                        GlobalFront.Core.Snapshot.ReplicationRequestCodec.SizeBytes);
                    return;

                default:
                    // Snapshots from clients are protocol violations.
                    _carrier.Metrics.DroppedMalformed++;
                    return;
            }
        }

        private void HandleReconnectRequest(TransportEvent transportEvent, int offset, int length)
        {
            var span = new ReadOnlySpan<byte>(transportEvent.Data, offset, length);
            if (!ReconnectWireCodec.TryDecodeRequest(span, out var request))
            {
                _carrier.Metrics.DroppedMalformed++;
                return;
            }

            var currentTick = _currentDrainTick > 0 ? _currentDrainTick : _server.CurrentTick;
            var handle = _sessions.CreateConnectionHandle();
            var result = _sessions.TryRebindSession(
                request.SessionId, request.Secret, handle, currentTick, out var session);

            ushort activeKeyframeSeq = 0;
            if (result == RebindResult.Accepted)
            {
                _binder.Release(session.Id);
                var token = _binder.Bind(session.Id, handle, session.Match, session.Player, transportEvent.ConnectionId);
                _carrier.AssignSessionToken(transportEvent.ConnectionId, token);

                SessionReattached?.Invoke(session.Id, session.Match, session.Player);

                if (KeyframeSeqProvider != null)
                {
                    activeKeyframeSeq = KeyframeSeqProvider(session.Id);
                }
            }

            var reconnectResult = (GlobalFront.Core.Reconnect.ReconnectResult)result;
            var response = new ReconnectResponse(
                reconnectResult,
                session?.Player ?? default,
                session?.Match ?? default,
                currentTick,
                activeKeyframeSeq);

            if (ReconnectWireCodec.TryEncodeResponse(response, _messageBuffer, out var written))
            {
                _carrier.Send(
                    transportEvent.ConnectionId, TransportChannel.Control, _messageBuffer, 0, written);
            }
        }

        private void HandleConnectRequest(TransportEvent transportEvent, TransportMessageHeader header)
        {
            if (header.SessionToken != TransportProtocol.HandshakeToken)
            {
                _carrier.Metrics.DroppedBadToken++;
                return;
            }

            if (!MessageCodec.TryDecodeConnectRequest(
                    transportEvent.Data,
                    header.PayloadOffset,
                    transportEvent.Length,
                    out var snapshotVersion,
                    out _))
            {
                _carrier.Metrics.DroppedMalformed++;
                return;
            }

            if (snapshotVersion != SnapshotProtocol.Version)
            {
                SendConnectDenied(transportEvent.ConnectionId, ConnectDenyReason.SnapshotVersionMismatch);
                return;
            }

            if (_binder.BoundCount >= SimulationConstants.MaxPlayers)
            {
                SendConnectDenied(transportEvent.ConnectionId, ConnectDenyReason.ServerFull);
                return;
            }

            var handle = _sessions.CreateConnectionHandle();
            var session = _sessions.CreateSession(handle, out var secret);

            var match = AutoJoinMatch;
            var player = default(PlayerId);
            if (match.IsValid)
            {
                var join = _sessions.TryJoinMatch(session, match, out player);
                if (join != JoinResult.Assigned)
                {
                    _sessions.CloseSession(session);
                    SendConnectDenied(transportEvent.ConnectionId, ConnectDenyReason.Internal);
                    return;
                }
            }

            var token = _binder.Bind(session, handle, match, player, transportEvent.ConnectionId);
            _carrier.AssignSessionToken(transportEvent.ConnectionId, token);

            var accept = new ConnectAcceptPayload
            {
                SessionToken = token,
                Session = session,
                Match = match,
                Player = player
            };
            var offset = MessageCodec.WriteHeader(
                _messageBuffer, 0, TransportMessageType.ConnectAccept, token, 0);
            offset = MessageCodec.EncodeConnectAccept(_messageBuffer, offset, accept);
            _carrier.Send(
                transportEvent.ConnectionId, TransportChannel.Control, _messageBuffer, 0, offset);

            // Transmit 33-byte SessionSecretMessage (opcode 9) on C0 with the issued secret (ADR-011)
            var secretMessage = new SessionSecretMessage(secret);
            if (ReconnectWireCodec.TryEncodeSecretMessage(secretMessage, _messageBuffer, out var secretWritten))
            {
                _carrier.Send(
                    transportEvent.ConnectionId, TransportChannel.Control, _messageBuffer, 0, secretWritten);
            }

            SessionAttached?.Invoke(session, match, player);
        }

        private void HandleCommand(TransportEvent transportEvent, TransportMessageHeader header)
        {
            if (!_binder.TryGetByToken(header.SessionToken, out var binding) ||
                binding.ConnectionId != transportEvent.ConnectionId)
            {
                _carrier.Metrics.DroppedBadToken++;
                return;
            }

            if (!CommandWireCodec.TryDecode(
                    transportEvent.Data,
                    header.PayloadOffset,
                    header.PayloadLength,
                    out var wire,
                    out var wireError))
            {
                _carrier.Metrics.DroppedMalformed++;
                _ = wireError;
                return;
            }

            var gate = _sessions.ValidateCommand(binding.Session, wire.Header);
            if (gate != SessionRejection.None)
            {
                SendCommandAck(
                    binding,
                    wire.Header.Player,
                    wire.Header.Sequence,
                    gate,
                    MatchCommandRejection.SessionRejected);
                return;
            }

            MatchCommandRejection result;
            switch (wire.Header.Type)
            {
                case GameCommandType.Move:
                    result = _server.TryEnqueueMove(
                        wire.Header, wire.Entities, wire.Destination, wire.Formation);
                    break;
                case GameCommandType.Attack:
                    result = _server.TryEnqueueAttack(
                        wire.Header, wire.Entities, wire.Target);
                    break;
                case GameCommandType.Stop:
                    result = _server.TryEnqueueStop(wire.Header, wire.Entities);
                    break;
                default:
                    _carrier.Metrics.DroppedMalformed++;
                    return;
            }

            SendCommandAck(
                binding,
                wire.Header.Player,
                wire.Header.Sequence,
                SessionRejection.None,
                result);
        }

        private void HandleGracefulDisconnect(TransportEvent transportEvent, TransportMessageHeader header)
        {
            if (!_binder.TryGetByToken(header.SessionToken, out var binding) ||
                binding.ConnectionId != transportEvent.ConnectionId)
            {
                _carrier.Metrics.DroppedBadToken++;
                return;
            }

            _sessions.CloseSession(binding.Session);
            _binder.Release(binding.Session);
            SessionDetached?.Invoke(binding.Session, TransportDisconnectReason.ClientRequested);
        }

        private void HandlePing(TransportEvent transportEvent, TransportMessageHeader header)
        {
            if (!MessageCodec.TryDecodePingPong(
                    transportEvent.Data, header.PayloadOffset, transportEvent.Length, out var timestamp))
            {
                _carrier.Metrics.DroppedMalformed++;
                return;
            }

            var offset = MessageCodec.WriteHeader(
                _messageBuffer, 0, TransportMessageType.Pong, header.SessionToken, 0);
            offset = MessageCodec.EncodePingPong(_messageBuffer, offset, timestamp);
            _carrier.Send(
                transportEvent.ConnectionId, TransportChannel.Control, _messageBuffer, 0, offset);
        }

        /// <summary>
        /// Validates one client→server replication feedback message (session
        /// token plus minimum payload size) and forwards the payload slice to
        /// the replication layer (step 2.6.4). The buffer is owned by the
        /// transport event; handlers decode immediately and never retain it.
        /// </summary>
        private void HandleReplicationFeedback(
            TransportEvent transportEvent,
            TransportMessageHeader header,
            TransportMessageType expectedType,
            int minimumPayloadBytes)
        {
            if (!_binder.TryGetByToken(header.SessionToken, out var binding) ||
                binding.ConnectionId != transportEvent.ConnectionId)
            {
                _carrier.Metrics.DroppedBadToken++;
                return;
            }

            if (header.PayloadLength < minimumPayloadBytes)
            {
                _carrier.Metrics.DroppedMalformed++;
                return;
            }

            ReplicationFeedbackReceived?.Invoke(new ReplicationFeedbackMessage(
                binding.Session,
                expectedType,
                transportEvent.Data,
                header.PayloadOffset,
                header.PayloadLength));
        }

        private void SendConnectDenied(int connectionId, ConnectDenyReason reason)
        {
            var offset = MessageCodec.WriteHeader(
                _messageBuffer, 0, TransportMessageType.ConnectDenied, TransportProtocol.HandshakeToken, 0);
            offset = MessageCodec.EncodeConnectDenied(_messageBuffer, offset, reason);
            _carrier.Send(connectionId, TransportChannel.Control, _messageBuffer, 0, offset);
            _carrier.CloseConnection(connectionId, TransportDisconnectReason.Rejected);
        }

        private void SendCommandAck(
            TransportSessionBinder.Binding binding,
            PlayerId player,
            uint sequence,
            SessionRejection sessionRejection,
            MatchCommandRejection commandRejection)
        {
            var offset = MessageCodec.WriteHeader(
                _messageBuffer, 0, TransportMessageType.CommandAck, binding.Token, 0);
            offset = MessageCodec.EncodeCommandAck(
                _messageBuffer,
                offset,
                new CommandAckPayload
                {
                    Player = player,
                    CommandSequence = sequence,
                    Session = sessionRejection,
                    Command = commandRejection
                });
            if (!_carrier.Send(
                    binding.ConnectionId, TransportChannel.Control, _messageBuffer, 0, offset))
            {
                var copy = new byte[offset];
                Array.Copy(_messageBuffer, 0, copy, 0, offset);
                _pendingAcks.Add(new PendingCommandAck
                {
                    ConnectionId = binding.ConnectionId,
                    Buffer = copy,
                    Length = offset
                });
            }
        }

        private void SendDisconnect(SessionId session, TransportDisconnectReason reason)
        {
            if (!_binder.TryGetTokenBySession(session, out var token) ||
                !_binder.TryGetByToken(token, out var binding))
            {
                return;
            }

            var offset = MessageCodec.WriteHeader(
                _messageBuffer, 0, TransportMessageType.Disconnect, token, 0);
            offset = MessageCodec.EncodeDisconnect(_messageBuffer, offset, reason);
            _carrier.Send(
                binding.ConnectionId, TransportChannel.Control, _messageBuffer, 0, offset);
        }
    }
}
