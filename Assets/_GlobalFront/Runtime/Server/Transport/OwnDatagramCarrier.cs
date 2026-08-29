using System.Collections.Generic;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Project's own UDP-style carrier implementing the
    /// <see cref="INetworkCarrier"/> contract over raw datagrams
    /// (<see cref="VirtualNetworkPipe"/> in deterministic tests; raw UDP in
    /// the future). Carries the full ADR-009 own-carrier machinery:
    /// envelope framing (<see cref="TransportProtocol.CarrierVersion"/>),
    /// ONE unified reliable sequence space per direction for C0+C1,
    /// separate sequenced space for C2 (never ACKed), serial-number
    /// wrap-around arithmetic, receive/reorder windows, cumulative +
    /// selective ACK, RTO retransmission with EWMA RTT, keepalive/idle
    /// detection on <see cref="ITransportClock"/>, per-peer rate limiting
    /// and bounded reassembly.
    /// </summary>
    public sealed class OwnDatagramCarrier : INetworkCarrier
    {
        private sealed class PendingItem
        {
            public ushort Seq;
            public TransportChannel Channel;
            public byte[] Fragment;
            public ushort MessageId;
            public ushort FragmentIndex;
            public ushort FragmentCount;
            public long FirstSentAtMs;
            public long NextRetransmitAtMs;
            public int Retransmits;
        }

        private sealed class ReceivedDatagram
        {
            public TransportChannel Channel;
            public byte[] Payload;
            public bool HasFragment;
            public ushort MessageId;
            public ushort FragmentIndex;
            public ushort FragmentCount;
        }

        private sealed class ReliableConnection
        {
            public int ConnectionId;
            public int RemoteEndpoint;
            public bool Closed;

            // Send state (reliable space).
            public ushort NextReliableSeq = 1;
            public ushort NextSnapshotSeq = 1;
            public ushort NextMessageId = 1;
            public long RtoMs = TransportProtocol.InitialRtoMs;
            public long SrttMs = TransportProtocol.InitialRtoMs;
            public readonly List<PendingItem> Unacked = new List<PendingItem>();

            // Receive state (reliable space).
            public ushort AckBase;
            public readonly Dictionary<ushort, ReceivedDatagram> Reorder =
                new Dictionary<ushort, ReceivedDatagram>();

            // Snapshot (C2) receive state: separate space, never ACKed.
            // DeliveredSeq tracks the newest fully delivered snapshot and
            // drives latest-wins: late fragments of an older message are
            // hard-dropped, and older incomplete groups are discarded once
            // a newer snapshot is actually delivered. SnapshotSeqByMessageId
            // maps in-progress snapshot groups to their message sequence.
            public bool HasSnapshotDelivered;
            public ushort SnapshotDeliveredSeq;
            public readonly Dictionary<ushort, ushort> SnapshotSeqByMessageId =
                new Dictionary<ushort, ushort>();

            public readonly FragmentAssembler Assembler = new FragmentAssembler();
            public readonly RateLimiter Limiter = new RateLimiter();

            public bool TokenAssigned;
            public ulong Token;

            public long LastReceiveMs;
            public long LastSendMs;
        }

        private readonly VirtualNetworkPipe _pipe;
        private readonly int _localEndpoint;
        private readonly int _serverEndpoint;
        private readonly bool _isServer;
        private readonly ITransportClock _clock;
        private readonly TransportMetrics _metrics = new TransportMetrics();

        private readonly object _eventGate = new object();
        private readonly Queue<TransportEvent> _events = new Queue<TransportEvent>();

        private readonly Dictionary<int, ReliableConnection> _connections =
            new Dictionary<int, ReliableConnection>();
        private readonly Dictionary<int, int> _endpointToConnection =
            new Dictionary<int, int>();
        private readonly Dictionary<int, PreHandshakeState> _preHandshake =
            new Dictionary<int, PreHandshakeState>();
        private readonly List<int> _preHandshakeScratch = new List<int>();

        /// <summary>Current live connection count (bounded by MaxConnections).</summary>
        public int ConnectionCount => _connections.Count;

        /// <summary>Tracked pre-handshake remote addresses (bounded).</summary>
        public int PreHandshakeTrackedCount => _preHandshake.Count;

        private readonly byte[] _sendBuffer =
            new byte[TransportProtocol.MaxDatagramBytes];

        // Reusable scratch so snapshot supersede cleanup stays allocation-free.
        private readonly List<ushort> _snapshotDiscardScratch = new List<ushort>();

        // Connections marked closed are removed from the maps in a sweep so
        // close paths triggered from inside Pump never mutate the collection
        // being iterated.
        private readonly List<int> _pendingRemovals = new List<int>();

        private sealed class PreHandshakeState
        {
            public readonly RateLimiter Limiter = new RateLimiter();
            public long LastSeenMs;
        }

        private int _nextConnectionId = 1;
        private bool _shutdown;

        private OwnDatagramCarrier(
            VirtualNetworkPipe pipe,
            int localEndpoint,
            int serverEndpoint,
            bool isServer,
            ITransportClock clock)
        {
            _pipe = pipe;
            _localEndpoint = localEndpoint;
            _serverEndpoint = serverEndpoint;
            _isServer = isServer;
            _clock = clock;
        }

        public static OwnDatagramCarrier CreateServer(
            VirtualNetworkPipe pipe, int serverEndpoint, ITransportClock clock) =>
            new OwnDatagramCarrier(pipe, serverEndpoint, serverEndpoint, true, clock);

        public static OwnDatagramCarrier CreateClient(
            VirtualNetworkPipe pipe, int clientEndpoint, int serverEndpoint, ITransportClock clock) =>
            new OwnDatagramCarrier(pipe, clientEndpoint, serverEndpoint, false, clock);

        public TransportMetrics Metrics => _metrics;

        public ITransportClock Clock => _clock;

        public int EventQueueDepth
        {
            get { lock (_eventGate) { return _events.Count; } }
        }

        public void StartServer()
        {
            // Server accepts connections implicitly from first datagrams.
        }

        public int ConnectToServer(string host, int port)
        {
            // Addresses are fixed by the VirtualNetworkPipe topology; the
            // host/port arguments apply to real-socket carriers only.
            var now = _clock.NowMs;
            var connection = new ReliableConnection
            {
                ConnectionId = _nextConnectionId++,
                RemoteEndpoint = _serverEndpoint,
                LastReceiveMs = now,
                LastSendMs = now
            };
            _connections[connection.ConnectionId] = connection;
            _endpointToConnection[_serverEndpoint] = connection.ConnectionId;
            EnqueueEvent(new TransportEvent
            {
                Type = TransportEventType.Connected,
                ConnectionId = connection.ConnectionId
            });
            return connection.ConnectionId;
        }

        /// <summary>
        /// Assigns the session token mirrored into envelope attribution for
        /// this connection (set by the endpoint layer after handshake).
        /// </summary>
        public void AssignSessionToken(int connectionId, ulong token)
        {
            if (_connections.TryGetValue(connectionId, out var connection))
            {
                connection.Token = token;
                connection.TokenAssigned = true;
            }
        }

        public bool Send(int connectionId, TransportChannel channel, byte[] data, int offset, int length)
        {
            if (_shutdown ||
                !_connections.TryGetValue(connectionId, out var connection) ||
                connection.Closed)
            {
                return false;
            }

            if (length < 0 || length > TransportProtocol.MaxMessageBytes)
            {
                _metrics.DroppedMalformed++;
                return false;
            }

            var now = _clock.NowMs;
            if (channel == TransportChannel.Snapshot)
            {
                SendSnapshotMessage(connection, data, offset, length, now);
                return true;
            }

            // Send-side flow control (ADR-009 windows). The hard cap is the
            // peer's receive window; the working cap is the REORDER window
            // measured from the oldest unacked item: running further ahead
            // of a hole than the receiver can buffer turns one lost packet
            // into a window-drop retransmission cascade (amplification).
            if (connection.Unacked.Count >= TransportProtocol.ReceiveWindow)
            {
                _metrics.DroppedQueueOverflow++;
                return false;
            }

            if (connection.Unacked.Count > 0)
            {
                var span = SerialArithmetic.Diff(
                    connection.NextReliableSeq, connection.Unacked[0].Seq);
                if (span >= TransportProtocol.ReorderWindow)
                {
                    _metrics.DroppedQueueOverflow++;
                    return false;
                }
            }

            SendReliableMessage(connection, channel, data, offset, length, now);
            return true;
        }

        public void CloseConnection(int connectionId, TransportDisconnectReason reason)
        {
            if (!_connections.TryGetValue(connectionId, out var connection) || connection.Closed)
            {
                return;
            }

            // Mark + defer the map removal: close paths can fire from inside
            // Pump's connection iteration, and the sweep at the end of Pump
            // (or the next one) frees all per-connection state.
            connection.Closed = true;
            _pendingRemovals.Add(connectionId);
            EnqueueEvent(new TransportEvent
            {
                Type = TransportEventType.Disconnected,
                ConnectionId = connectionId,
                Reason = reason
            });
        }

        public void Shutdown(TransportDisconnectReason reason)
        {
            _shutdown = true;
            foreach (var pair in _connections)
            {
                if (!pair.Value.Closed)
                {
                    pair.Value.Closed = true;
                    EnqueueEvent(new TransportEvent
                    {
                        Type = TransportEventType.Disconnected,
                        ConnectionId = pair.Key,
                        Reason = reason
                    });
                }
            }

            _connections.Clear();
            _endpointToConnection.Clear();
            _preHandshake.Clear();
            _pendingRemovals.Clear();
        }

        public void Pump(long nowMs)
        {
            if (_shutdown)
            {
                return;
            }

            SweepPendingRemovals();
            SweepPreHandshake(nowMs);

            while (_pipe.TryReceive(_localEndpoint, out var from, out var datagram))
            {
                HandleDatagram(from, datagram, datagram.Length, nowMs);
            }

            foreach (var pair in _connections)
            {
                var connection = pair.Value;
                if (!connection.Closed)
                {
                    ProcessTimers(connection, nowMs);
                }
            }

            SweepPendingRemovals();
        }

        /// <summary>
        /// Frees the state of closed connections: connection record,
        /// endpoint mapping, limiter, reorder buffer and fragment groups.
        /// </summary>
        private void SweepPendingRemovals()
        {
            for (var index = 0; index < _pendingRemovals.Count; index++)
            {
                var connectionId = _pendingRemovals[index];
                if (_connections.TryGetValue(connectionId, out var connection))
                {
                    _connections.Remove(connectionId);
                    _endpointToConnection.Remove(connection.RemoteEndpoint);
                }
            }

            _pendingRemovals.Clear();
        }

        /// <summary>Drops pre-handshake tracking state aged past the idle timeout.</summary>
        private void SweepPreHandshake(long nowMs)
        {
            _preHandshakeScratch.Clear();
            foreach (var pair in _preHandshake)
            {
                if (nowMs - pair.Value.LastSeenMs > TransportProtocol.IdleTimeoutMs)
                {
                    _preHandshakeScratch.Add(pair.Key);
                }
            }

            for (var index = 0; index < _preHandshakeScratch.Count; index++)
            {
                _preHandshake.Remove(_preHandshakeScratch[index]);
            }
        }

        public bool TryDequeueEvent(out TransportEvent transportEvent)
        {
            lock (_eventGate)
            {
                if (_events.Count > 0)
                {
                    transportEvent = _events.Dequeue();
                    _metrics.NoteQueueDepth(_events.Count);
                    return true;
                }

                transportEvent = default;
                return false;
            }
        }

        public void Dispose() => Shutdown(TransportDisconnectReason.TransportLost);

        // ------------------------------------------------------------------
        // Send path
        // ------------------------------------------------------------------

        private void SendReliableMessage(
            ReliableConnection connection,
            TransportChannel channel,
            byte[] data,
            int offset,
            int length,
            long nowMs)
        {
            var messageId = connection.NextMessageId++;
            var fragmentCount = MessageFragmenter.GetFragmentCount(length);
            if (fragmentCount == 0)
            {
                fragmentCount = 1;
            }

            for (var index = 0; index < fragmentCount; index++)
            {
                int fragmentOffset;
                int fragmentLength;
                if (length == 0)
                {
                    fragmentOffset = offset;
                    fragmentLength = 0;
                }
                else
                {
                    MessageFragmenter.GetFragmentPayload(length, index, out fragmentOffset, out fragmentLength);
                    fragmentOffset += offset;
                }

                var fragment = new byte[fragmentLength];
                if (fragmentLength > 0)
                {
                    System.Array.Copy(data, fragmentOffset, fragment, 0, fragmentLength);
                }

                var item = new PendingItem
                {
                    Seq = connection.NextReliableSeq,
                    Channel = channel,
                    Fragment = fragment,
                    MessageId = messageId,
                    FragmentIndex = (ushort)index,
                    FragmentCount = (ushort)fragmentCount,
                    FirstSentAtMs = nowMs,
                    NextRetransmitAtMs = nowMs + connection.RtoMs
                };
                connection.NextReliableSeq = SerialArithmetic.Next(connection.NextReliableSeq);
                connection.Unacked.Add(item);
                TransmitPending(connection, item, nowMs);
            }

            _metrics.MessagesSent++;
        }

        private void SendSnapshotMessage(
            ReliableConnection connection,
            byte[] data,
            int offset,
            int length,
            long nowMs)
        {
            var seq = connection.NextSnapshotSeq;
            connection.NextSnapshotSeq = SerialArithmetic.Next(connection.NextSnapshotSeq);

            var fragmentCount = MessageFragmenter.GetFragmentCount(length);
            if (fragmentCount == 0)
            {
                fragmentCount = 1;
            }

            var messageId = connection.NextMessageId++;
            for (var index = 0; index < fragmentCount; index++)
            {
                int fragmentOffset;
                int fragmentLength;
                if (length == 0)
                {
                    fragmentOffset = offset;
                    fragmentLength = 0;
                }
                else
                {
                    MessageFragmenter.GetFragmentPayload(length, index, out fragmentOffset, out fragmentLength);
                    fragmentOffset += offset;
                }

                var size = EnvelopeCodec.Encode(
                    _sendBuffer,
                    TransportChannel.Snapshot,
                    TokenFor(connection),
                    seq,
                    connection.AckBase,
                    BuildAckBitmap(connection),
                    messageId,
                    (ushort)index,
                    (ushort)fragmentCount,
                    data,
                    fragmentOffset,
                    fragmentLength);
                SendDatagram(connection, size, nowMs);
            }

            _metrics.MessagesSent++;
        }

        private void TransmitPending(ReliableConnection connection, PendingItem item, long nowMs)
        {
            var size = EnvelopeCodec.Encode(
                _sendBuffer,
                item.Channel,
                TokenFor(connection),
                item.Seq,
                connection.AckBase,
                BuildAckBitmap(connection),
                item.MessageId,
                item.FragmentIndex,
                item.FragmentCount,
                item.Fragment,
                0,
                item.Fragment.Length);
            SendDatagram(connection, size, nowMs);
        }

        private void SendDatagram(ReliableConnection connection, int size, long nowMs)
        {
            connection.LastSendMs = nowMs;
            _pipe.SendDatagram(_localEndpoint, connection.RemoteEndpoint, _sendBuffer, size);
            _metrics.DatagramsSent++;
            _metrics.BytesSent += size;
        }

        private static ulong TokenFor(ReliableConnection connection) =>
            connection.TokenAssigned ? connection.Token : TransportProtocol.HandshakeToken;

        private static uint BuildAckBitmap(ReliableConnection connection)
        {
            uint bitmap = 0;
            for (var bit = 0; bit < TransportProtocol.AckBitmapBits; bit++)
            {
                var seq = SerialArithmetic.Add(connection.AckBase, bit + 1);
                if (connection.Reorder.ContainsKey(seq))
                {
                    bitmap |= 1u << bit;
                }
            }

            return bitmap;
        }

        // ------------------------------------------------------------------
        // Receive path
        // ------------------------------------------------------------------

        private void HandleDatagram(int from, byte[] datagram, int length, long nowMs)
        {
            _metrics.DatagramsReceived++;
            _metrics.BytesReceived += length;

            ReliableConnection connection;
            if (_endpointToConnection.TryGetValue(from, out var connectionId))
            {
                connection = _connections[connectionId];
            }
            else
            {
                if (!_isServer || _shutdown)
                {
                    _metrics.DroppedMalformed++;
                    return;
                }

                // Pre-handshake rate limiting per remote address. The
                // tracking state itself is bounded (cap + age sweep in Pump
                // + removal on connection creation): spoofed sources must
                // not grow carrier state without limit.
                if (!_preHandshake.TryGetValue(from, out var preState))
                {
                    if (_preHandshake.Count >= TransportProtocol.MaxTrackedPreHandshakePeers)
                    {
                        _metrics.DroppedConnectionLimit++;
                        return;
                    }

                    preState = new PreHandshakeState();
                    _preHandshake[from] = preState;
                }

                preState.LastSeenMs = nowMs;
                if (!preState.Limiter.TryConsume(length, nowMs))
                {
                    _metrics.DroppedRateLimited++;
                    return;
                }

                // Bounded connection table.
                if (_connections.Count >= TransportProtocol.MaxConnections)
                {
                    _metrics.DroppedConnectionLimit++;
                    return;
                }

                connection = new ReliableConnection
                {
                    ConnectionId = _nextConnectionId++,
                    RemoteEndpoint = from,
                    LastReceiveMs = nowMs,
                    LastSendMs = nowMs
                };
                _connections[connection.ConnectionId] = connection;
                _endpointToConnection[from] = connection.ConnectionId;
                _preHandshake.Remove(from);
                EnqueueEvent(new TransportEvent
                {
                    Type = TransportEventType.Connected,
                    ConnectionId = connection.ConnectionId
                });
            }

            if (connection.Closed)
            {
                return;
            }

            if (!connection.Limiter.TryConsume(length, nowMs))
            {
                _metrics.DroppedRateLimited++;
                return;
            }

            var error = EnvelopeCodec.TryDecode(datagram, length, out var envelope);
            switch (error)
            {
                case EnvelopeError.None:
                    break;
                case EnvelopeError.BadMagic:
                case EnvelopeError.TooShort:
                case EnvelopeError.BadChannel:
                case EnvelopeError.BadPayloadLength:
                case EnvelopeError.BadFragmentHeader:
                    _metrics.DroppedMalformed++;
                    return;
                case EnvelopeError.BadCarrierVersion:
                    _metrics.DroppedBadVersion++;
                    return;
            }

            if (connection.TokenAssigned &&
                envelope.SessionToken != connection.Token &&
                envelope.SessionToken != TransportProtocol.HandshakeToken)
            {
                _metrics.DroppedBadToken++;
                return;
            }

            connection.LastReceiveMs = nowMs;
            ProcessAcks(connection, envelope.AckNumber, envelope.AckBitmap, nowMs);

            if (envelope.PayloadLength == 0 && !envelope.HasFragment)
            {
                // Ack-only heartbeat.
                return;
            }

            if (envelope.Channel == TransportChannel.Snapshot)
            {
                HandleSnapshotDatagram(connection, datagram, envelope, nowMs);
                return;
            }

            HandleReliableDatagram(connection, datagram, envelope, nowMs);
        }

        private void HandleSnapshotDatagram(
            ReliableConnection connection,
            byte[] datagram,
            TransportEnvelope envelope,
            long nowMs)
        {
            // Separate sequenced space, never ACKed. One snapshot message owns
            // ONE sequence; all of its fragments carry that same sequence and
            // are grouped by MessageId — a fragment stream is one logical
            // snapshot, never several independent ones.
            var sequence = envelope.Sequence;

            // Hard stale-drop: a strictly newer snapshot was already fully
            // delivered, so late fragments of an older message must not
            // reassemble after it (latest-wins).
            if (connection.HasSnapshotDelivered &&
                SerialArithmetic.Diff(sequence, connection.SnapshotDeliveredSeq) < 0)
            {
                _metrics.DroppedStaleSnapshot++;
                return;
            }

            if (envelope.HasFragment)
            {
                connection.SnapshotSeqByMessageId[envelope.MessageId] = sequence;
                var message = connection.Assembler.AddFragment(
                    envelope.MessageId,
                    envelope.FragmentIndex,
                    envelope.FragmentCount,
                    datagram,
                    envelope.PayloadOffset,
                    envelope.PayloadLength,
                    nowMs);
                NoteReassemblyPeak(connection);
                if (message != null)
                {
                    connection.SnapshotSeqByMessageId.Remove(envelope.MessageId);
                    NoteSnapshotDelivered(connection, sequence);
                    DiscardStaleSnapshotGroups(connection, sequence);
                    EnqueueMessage(connection, TransportChannel.Snapshot, message);
                }

                return;
            }

            // Single-datagram snapshot message.
            NoteSnapshotDelivered(connection, sequence);
            DiscardStaleSnapshotGroups(connection, sequence);
            DeliverPayload(connection, TransportChannel.Snapshot, datagram, envelope, nowMs);
        }

        private static void NoteSnapshotDelivered(ReliableConnection connection, ushort sequence)
        {
            if (!connection.HasSnapshotDelivered ||
                SerialArithmetic.Diff(sequence, connection.SnapshotDeliveredSeq) > 0)
            {
                connection.SnapshotDeliveredSeq = sequence;
            }

            connection.HasSnapshotDelivered = true;
        }

        /// <summary>
        /// Latest-wins memory policy: once a snapshot with sequence
        /// <paramref name="deliveredSequence"/> is delivered, every older
        /// incomplete snapshot group is stale and is discarded safely
        /// (in addition to the assembler lifetime/budget eviction that
        /// bounds everything else).
        /// </summary>
        private void DiscardStaleSnapshotGroups(ReliableConnection connection, ushort deliveredSequence)
        {
            _snapshotDiscardScratch.Clear();
            foreach (var pair in connection.SnapshotSeqByMessageId)
            {
                if (SerialArithmetic.Diff(pair.Value, deliveredSequence) < 0)
                {
                    _snapshotDiscardScratch.Add(pair.Key);
                }
            }

            for (var index = 0; index < _snapshotDiscardScratch.Count; index++)
            {
                var messageId = _snapshotDiscardScratch[index];
                connection.Assembler.DiscardGroup(messageId);
                connection.SnapshotSeqByMessageId.Remove(messageId);
                _metrics.DroppedStaleSnapshot++;
            }
        }

        private void HandleReliableDatagram(
            ReliableConnection connection,
            byte[] datagram,
            TransportEnvelope envelope,
            long nowMs)
        {
            var diff = SerialArithmetic.Diff(envelope.Sequence, connection.AckBase);
            if (diff <= 0)
            {
                _metrics.DroppedReplayOrWindow++;
                return;
            }

            if (diff > TransportProtocol.ReceiveWindow)
            {
                _metrics.DroppedReplayOrWindow++;
                return;
            }

            if (diff == 1)
            {
                connection.AckBase = envelope.Sequence;
                DeliverPayload(connection, envelope.Channel, datagram, envelope, nowMs);
                DrainReorder(connection, nowMs);
                return;
            }

            if (diff > TransportProtocol.ReorderWindow)
            {
                _metrics.DroppedReplayOrWindow++;
                return;
            }

            if (connection.Reorder.ContainsKey(envelope.Sequence))
            {
                _metrics.DuplicateSuppressed++;
                return;
            }

            var payload = new byte[envelope.PayloadLength];
            System.Array.Copy(
                datagram, envelope.PayloadOffset, payload, 0, envelope.PayloadLength);
            connection.Reorder[envelope.Sequence] = new ReceivedDatagram
            {
                Channel = envelope.Channel,
                Payload = payload,
                HasFragment = envelope.HasFragment,
                MessageId = envelope.MessageId,
                FragmentIndex = envelope.FragmentIndex,
                FragmentCount = envelope.FragmentCount
            };
        }

        private void DrainReorder(ReliableConnection connection, long nowMs)
        {
            while (true)
            {
                var next = SerialArithmetic.Next(connection.AckBase);
                if (!connection.Reorder.TryGetValue(next, out var datagram))
                {
                    return;
                }

                connection.Reorder.Remove(next);
                connection.AckBase = next;
                DeliverBuffered(connection, datagram, nowMs);
            }
        }

        private void DeliverPayload(
            ReliableConnection connection,
            TransportChannel channel,
            byte[] datagram,
            TransportEnvelope envelope,
            long nowMs)
        {
            if (envelope.HasFragment)
            {
                var message = connection.Assembler.AddFragment(
                    envelope.MessageId,
                    envelope.FragmentIndex,
                    envelope.FragmentCount,
                    datagram,
                    envelope.PayloadOffset,
                    envelope.PayloadLength,
                    nowMs);
                NoteReassemblyPeak(connection);
                if (message != null)
                {
                    EnqueueMessage(connection, channel, message);
                }

                return;
            }

            var payload = new byte[envelope.PayloadLength];
            System.Array.Copy(datagram, envelope.PayloadOffset, payload, 0, envelope.PayloadLength);
            EnqueueMessage(connection, channel, payload);
        }

        private void DeliverBuffered(ReliableConnection connection, ReceivedDatagram datagram, long nowMs)
        {
            if (datagram.HasFragment)
            {
                var message = connection.Assembler.AddFragment(
                    datagram.MessageId,
                    datagram.FragmentIndex,
                    datagram.FragmentCount,
                    datagram.Payload,
                    0,
                    datagram.Payload.Length,
                    nowMs);
                NoteReassemblyPeak(connection);
                if (message != null)
                {
                    EnqueueMessage(connection, datagram.Channel, message);
                }

                return;
            }

            EnqueueMessage(connection, datagram.Channel, datagram.Payload);
        }

        private void NoteReassemblyPeak(ReliableConnection connection)
        {
            var allocated = connection.Assembler.AllocatedBytes;
            if (allocated > _metrics.ReassemblyBytesPeak)
            {
                _metrics.ReassemblyBytesPeak = allocated;
            }
        }

        private void EnqueueMessage(ReliableConnection connection, TransportChannel channel, byte[] message)
        {
            _metrics.MessagesReceived++;
            EnqueueEvent(new TransportEvent
            {
                Type = TransportEventType.Message,
                ConnectionId = connection.ConnectionId,
                Channel = channel,
                Data = message,
                Length = message.Length
            });
        }

        private void EnqueueEvent(TransportEvent transportEvent)
        {
            lock (_eventGate)
            {
                if (_events.Count >= TransportProtocol.MaxQueuedReceiveEvents)
                {
                    _metrics.DroppedQueueOverflow++;
                    return;
                }

                _events.Enqueue(transportEvent);
                _metrics.NoteQueueDepth(_events.Count);
            }
        }

        // ------------------------------------------------------------------
        // ACK / timers
        // ------------------------------------------------------------------

        private void ProcessAcks(ReliableConnection connection, ushort ackNumber, uint ackBitmap, long nowMs)
        {
            for (var index = connection.Unacked.Count - 1; index >= 0; index--)
            {
                var item = connection.Unacked[index];
                var acked = SerialArithmetic.Diff(item.Seq, ackNumber) <= 0;
                if (!acked)
                {
                    var bit = SerialArithmetic.Diff(item.Seq, ackNumber) - 1;
                    acked = bit >= 0 && bit < TransportProtocol.AckBitmapBits &&
                        (ackBitmap & (1u << bit)) != 0;
                }

                if (!acked)
                {
                    continue;
                }

                if (item.Retransmits == 0)
                {
                    var sample = nowMs - item.FirstSentAtMs;
                    if (sample >= 0)
                    {
                        connection.SrttMs += (sample - connection.SrttMs) / 8;
                        var rto = connection.SrttMs * 2;
                        if (rto < TransportProtocol.MinRtoMs)
                        {
                            rto = TransportProtocol.MinRtoMs;
                        }
                        else if (rto > TransportProtocol.MaxRtoMs)
                        {
                            rto = TransportProtocol.MaxRtoMs;
                        }

                        connection.RtoMs = rto;
                    }
                }

                connection.Unacked.RemoveAt(index);
            }
        }

        private void ProcessTimers(ReliableConnection connection, long nowMs)
        {
            // Retransmission.
            for (var index = 0; index < connection.Unacked.Count; index++)
            {
                var item = connection.Unacked[index];
                if (nowMs < item.NextRetransmitAtMs)
                {
                    continue;
                }

                item.Retransmits++;
                if (item.Retransmits > TransportProtocol.MaxRetransmits)
                {
                    _metrics.MaxRetransmitLosses++;
                    CloseConnection(connection.ConnectionId, TransportDisconnectReason.MaxRetransmits);
                    return;
                }

                _metrics.Retransmissions++;

                // Per-item exponential backoff (200 → 400 → 800 → 1000 ms
                // cap). The connection RTO stays an EWMA of clean samples
                // (Karn rule); loss pressure backs off the individual item,
                // never the whole connection's estimate.
                var shift = item.Retransmits > 4 ? 4 : item.Retransmits;
                var interval = connection.RtoMs << shift;
                if (interval > TransportProtocol.MaxRtoMs)
                {
                    interval = TransportProtocol.MaxRtoMs;
                }

                item.NextRetransmitAtMs = nowMs + interval;
                TransmitPending(connection, item, nowMs);
            }

            // Idle timeout governs connections with no pending data; while
            // retransmissions are pending, the ARQ max-retransmit rule is the
            // loss authority (the two must not race for the same silence).
            if (connection.Unacked.Count == 0 &&
                nowMs - connection.LastReceiveMs > TransportProtocol.IdleTimeoutMs)
            {
                _metrics.ConnectionTimeouts++;
                CloseConnection(connection.ConnectionId, TransportDisconnectReason.Timeout);
                return;
            }

            // Keepalive heartbeat (ack-only datagram, no sequence consumed).
            if (nowMs - connection.LastSendMs >= TransportProtocol.KeepaliveIntervalMs)
            {
                var size = EnvelopeCodec.Encode(
                    _sendBuffer,
                    TransportChannel.Control,
                    TokenFor(connection),
                    SerialArithmetic.Add(connection.AckBase, 0),
                    connection.AckBase,
                    BuildAckBitmap(connection),
                    0,
                    0,
                    0,
                    _sendBuffer,
                    0,
                    0);
                SendDatagram(connection, size, nowMs);
            }
        }
    }
}
