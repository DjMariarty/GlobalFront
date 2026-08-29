using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Adopted carrier of Phase 2.5 (ADR-009, OD-1): LiteNetLib 1.3.5 behind
    /// the project's <see cref="INetworkCarrier"/> contract. LiteNetLib
    /// provides the delivery semantics (reliable ordered for C0/C1,
    /// unreliable sequenced for C2, keepalive, retransmission,
    /// fragmentation); its internal ARQ/sequence machinery is NOT required
    /// to match the own-carrier invariants — only the delivery semantics of
    /// the contract are binding. Project message headers
    /// (<see cref="MessageCodec"/>) still ride inside every message for
    /// version/token/attribution validation.
    ///
    /// Concurrency (ADR-009): a dedicated worker thread polls LiteNetLib
    /// events and enqueues them into the bounded event queue; authoritative
    /// processing happens only when the endpoint dequeues on the
    /// host/simulation thread.
    /// </summary>
    public sealed class LiteNetLibCarrier : INetworkCarrier, INetEventListener
    {
        private const string ConnectKey = "GlobalFront-Phase-2.5";

        private readonly ITransportClock _clock;
        private readonly bool _isServer;
        private readonly TransportMetrics _metrics = new TransportMetrics();

        private readonly object _eventGate = new object();
        private readonly Queue<TransportEvent> _events = new Queue<TransportEvent>();

        private readonly object _peerGate = new object();
        private readonly Dictionary<int, NetPeer> _connectionToPeer = new Dictionary<int, NetPeer>();
        private readonly Dictionary<int, int> _peerToConnection = new Dictionary<int, int>();
        private readonly Dictionary<int, RateLimiter> _peerLimiters = new Dictionary<int, RateLimiter>();
        private readonly Dictionary<int, PeerSnapshotState> _snapshotStates =
            new Dictionary<int, PeerSnapshotState>();

        /// <summary>
        /// C2 state of one peer. LiteNetLib's Sequenced channel refuses
        /// payloads above a single packet (TooBigPacketException), so C2
        /// snapshots are fragmented at the application layer with the
        /// ADR-009 reassembly bounds; the 8-byte prefix carries
        /// (MessageId, SnapshotSeq, FragmentIndex, FragmentCount) so the
        /// receiver can reassemble and apply the same latest-wins/stale
        /// policy as the own carrier.
        /// </summary>
        private sealed class PeerSnapshotState
        {
            public const int PrefixBytes = 8;

            public readonly FragmentAssembler Assembler = new FragmentAssembler();
            public readonly Dictionary<ushort, ushort> SeqByMessageId =
                new Dictionary<ushort, ushort>();
            public bool HasDeliveredSeq;
            public ushort DeliveredSeq;
            public ushort NextMessageId = 1;
            public ushort NextSeq = 1;
        }

        private NetManager _manager;
        private Thread _worker;
        private volatile bool _running;
        private volatile bool _shutdown;
        private int _nextConnectionId = 1;
        private int _clientConnectionId;

        // Per-peer admission reservations (under _peerGate): peers accepted
        // via OnConnectionRequest whose Connected event has not been
        // dispatched yet. Tracking is keyed by the admitted peer itself, so
        // a disconnect releases ONLY that peer's reservation — an anonymous
        // counter would let churn (connect → deny/close → disconnect) shift
        // capacity accounting and transiently overshoot MaxConnections.
        private readonly HashSet<int> _pendingAdmissions = new HashSet<int>();

        /// <summary>Diagnostics: last worker-thread exception (if any).</summary>
        public string LastWorkerError;

        /// <summary>Diagnostics: last LiteNetLib disconnect reason (if any).</summary>
        public string LastDisconnectReason;

        /// <summary>Diagnostics: connection requests observed by the listener.</summary>
        public int ConnectionRequestsSeen;

        /// <summary>Diagnostics: NetManager.Start result.</summary>
        public bool ManagerStarted;

        /// <summary>Diagnostics: last socket error reported by LiteNetLib.</summary>
        public string LastNetworkError;

        /// <summary>Diagnostics: last send-path exception (if any).</summary>
        public string LastSendError;

        /// <summary>Diagnostics: manager runtime state summary.</summary>
        public string ManagerInfo =>
            _manager == null
                ? "null"
                : $"running={_manager.IsRunning} ipv6={_manager.IPv6Enabled} port={_manager.LocalPort}";

        private LiteNetLibCarrier(ITransportClock clock, bool isServer)
        {
            _clock = clock;
            _isServer = isServer;
        }

        /// <summary>Creates the server-side carrier (call StartServer/StartServer(port)).</summary>
        public static LiteNetLibCarrier CreateServer(ITransportClock clock) =>
            new LiteNetLibCarrier(clock, true);

        /// <summary>Creates the client-side carrier (call ConnectToServer).</summary>
        public static LiteNetLibCarrier CreateClient(ITransportClock clock) =>
            new LiteNetLibCarrier(clock, false);

        public TransportMetrics Metrics => _metrics;

        public ITransportClock Clock => _clock;

        public int EventQueueDepth
        {
            get { lock (_eventGate) { return _events.Count; } }
        }

        public void StartServer()
        {
            StartServerInternal(0);
        }

        /// <summary>Starts the server on an explicit UDP port (loopback tests).</summary>
        public void StartServer(int port)
        {
            StartServerInternal(port);
        }

        private void StartServerInternal(int port)
        {
            // Idempotent: ServerTransportHost.StartServer() also calls into
            // the carrier; restarting would orphan the bound socket and its
            // worker, silently breaking every connection to the old port.
            if (_manager != null)
            {
                return;
            }

            _manager = new NetManager(this)
            {
                DisconnectTimeout = (int)TransportProtocol.IdleTimeoutMs,
                // Phase 2.5 scope is IPv4 loopback/LAN; with IPv6 enabled
                // LiteNetLib binds a second socket whose ephemeral port can
                // differ from the ipv4 one, making LocalPort ambiguous.
                IPv6Enabled = false
            };
            ManagerStarted = _manager.Start(port);
            StartWorker();
        }

        /// <summary>Port the manager is bound to (for loopback tests).</summary>
        public int LocalPort => _manager?.LocalPort ?? 0;

        public int ConnectToServer(string host, int port)
        {
            if (_manager == null)
            {
                _manager = new NetManager(this)
                {
                    DisconnectTimeout = (int)TransportProtocol.IdleTimeoutMs,
                    IPv6Enabled = false
                };
                ManagerStarted = _manager.Start();
                StartWorker();
            }

            var peer = _manager.Connect(host, port, ConnectKey);
            lock (_peerGate)
            {
                _clientConnectionId = _nextConnectionId++;
                _connectionToPeer[_clientConnectionId] = peer;
                _peerToConnection[peer.Id] = _clientConnectionId;
                _peerLimiters[peer.Id] = new RateLimiter();
            }

            return _clientConnectionId;
        }

        public bool Send(int connectionId, TransportChannel channel, byte[] data, int offset, int length)
        {
            if (_shutdown)
            {
                return false;
            }

            if (length < 0 || length > TransportProtocol.MaxMessageBytes)
            {
                _metrics.DroppedMalformed++;
                return false;
            }

            NetPeer peer;
            lock (_peerGate)
            {
                if (!_connectionToPeer.TryGetValue(connectionId, out peer))
                {
                    return false;
                }
            }

            if (channel == TransportChannel.Snapshot)
            {
                return SendSnapshotFragmented(connectionId, peer, data, offset, length);
            }

            try
            {
                peer.Send(data, offset, length, DeliveryMethod.ReliableOrdered);
            }
            catch (System.Exception)
            {
                // A send failure must surface as a drop counter, never as an
                // exception in the host/simulation tick loop.
                _metrics.DroppedMalformed++;
                return false;
            }

            _metrics.MessagesSent++;
            _metrics.BytesSent += length;
            return true;
        }

        /// <summary>
        /// Application-level C2 fragmentation: LiteNetLib's Sequenced
        /// channel cannot carry payloads above one packet
        /// (TooBigPacketException), and Phase 2.6 snapshots must not be
        /// capped at MTU. Fragments ride Sequenced packets with the
        /// 8-byte snapshot prefix; reassembly/staleness mirrors the own
        /// carrier policy.
        /// </summary>
        private bool SendSnapshotFragmented(
            int connectionId, NetPeer peer, byte[] data, int offset, int length)
        {
            // Chunk size follows the peer's CURRENT MTU (LiteNetLib starts at
            // 1024 and grows via MTU discovery): Sequenced packets above
            // GetMaxSinglePacketSize throw TooBigPacketException, which must
            // never reach the host/simulation tick loop. The upper clamp is
            // the reassembly-side fragment length validation.
            var maxChunk = System.Math.Min(
                peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced) -
                    PeerSnapshotState.PrefixBytes,
                TransportProtocol.FragmentPayloadBytes);
            if (maxChunk < 64)
            {
                _metrics.DroppedMalformed++;
                return false;
            }

            var fragmentCount = length <= 0 ? 1 : (length + maxChunk - 1) / maxChunk;
            if (fragmentCount > TransportProtocol.MaxFragmentsPerGroup)
            {
                _metrics.DroppedMalformed++;
                return false;
            }

            ushort messageId;
            ushort sequence;
            lock (_peerGate)
            {
                if (!_snapshotStates.TryGetValue(connectionId, out var state))
                {
                    state = new PeerSnapshotState();
                    _snapshotStates[connectionId] = state;
                }

                messageId = state.NextMessageId++;
                sequence = state.NextSeq++;
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
                    fragmentOffset = offset + index * maxChunk;
                    fragmentLength = System.Math.Min(maxChunk, length - index * maxChunk);
                }

                var packet = new byte[PeerSnapshotState.PrefixBytes + fragmentLength];
                WriteUInt16(packet, 0, messageId);
                WriteUInt16(packet, 2, sequence);
                WriteUInt16(packet, 4, (ushort)index);
                WriteUInt16(packet, 6, (ushort)fragmentCount);
                if (fragmentLength > 0)
                {
                    System.Array.Copy(data, fragmentOffset, packet, PeerSnapshotState.PrefixBytes, fragmentLength);
                }

                try
                {
                    peer.Send(packet, 0, packet.Length, DeliveryMethod.Sequenced);
                }
                catch (System.Exception ex)
                {
                    LastSendError = ex.GetType().Name + ": " + ex.Message;
                    _metrics.DroppedMalformed++;
                    return false;
                }

                _metrics.BytesSent += packet.Length;
            }

            _metrics.MessagesSent++;
            return true;
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset) =>
            (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

        public void CloseConnection(int connectionId, TransportDisconnectReason reason)
        {
            NetPeer peer;
            lock (_peerGate)
            {
                if (!_connectionToPeer.TryGetValue(connectionId, out peer))
                {
                    return;
                }

                // Remove all per-peer state up front: the worker's
                // OnPeerDisconnected then sees an unknown peer and does not
                // double-report; limiter/snapshot/fragment state is freed.
                RemovePeerState(peer.Id, connectionId);
            }

            peer.Disconnect();
            EnqueueEvent(new TransportEvent
            {
                Type = TransportEventType.Disconnected,
                ConnectionId = connectionId,
                Reason = reason
            });
        }

        /// <summary>Live peer mapping count (bounded; drops to 0 on teardown).</summary>
        public int PeerMappingCount
        {
            get { lock (_peerGate) { return _connectionToPeer.Count; } }
        }

        private void RemovePeerState(int peerId, int connectionId)
        {
            _connectionToPeer.Remove(connectionId);
            _peerToConnection.Remove(peerId);
            _peerLimiters.Remove(peerId);
            _snapshotStates.Remove(connectionId);
        }

        public void AssignSessionToken(int connectionId, ulong token)
        {
            // LiteNetLib attribution rides on the message-level header
            // validated by the server transport host; no envelope token.
        }

        public void Shutdown(TransportDisconnectReason reason)
        {
            _shutdown = true;
            _running = false;
            _worker?.Join(1000);
            _worker = null;
            _manager?.Stop();
            lock (_eventGate)
            {
                _events.Clear();
            }

            lock (_peerGate)
            {
                _connectionToPeer.Clear();
                _peerToConnection.Clear();
                _peerLimiters.Clear();
                _snapshotStates.Clear();
                _pendingAdmissions.Clear();
            }
        }

        public void Pump(long nowMs)
        {
            // LiteNetLib keeps its own timers; nothing to advance here.
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

        private void StartWorker()
        {
            _running = true;
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "GlobalFront.LiteNetLib.Receive"
            };
            _worker.Start();
        }

        private void WorkerLoop()
        {
            while (_running)
            {
                try
                {
                    _manager?.PollEvents();
                }
                catch (System.Exception ex)
                {
                    // Event processing must never crash the worker; malformed
                    // input surfaces through counters only.
                    _metrics.DroppedMalformed++;
                    LastWorkerError = ex.GetType().Name + ": " + ex.Message;
                }

                Thread.Sleep(1);
            }
        }

        // ------------------------------------------------------------------
        // INetEventListener — LiteNetLib worker-thread callbacks. These only
        // enqueue into the bounded queue; no authoritative calls here.
        // ------------------------------------------------------------------

        public void OnPeerConnected(NetPeer peer)
        {
            int connectionId;
            lock (_peerGate)
            {
                // The accepted peer materializes: its reservation becomes a
                // real mapping (released per peer, never by bulk counter).
                _pendingAdmissions.Remove(peer.Id);

                if (_peerToConnection.TryGetValue(peer.Id, out connectionId))
                {
                    // Client-side connection confirmed.
                }
                else
                {
                    connectionId = _nextConnectionId++;
                    _connectionToPeer[connectionId] = peer;
                    _peerToConnection[peer.Id] = connectionId;
                    _peerLimiters[peer.Id] = new RateLimiter();
                }
            }

            EnqueueEvent(new TransportEvent
            {
                Type = TransportEventType.Connected,
                ConnectionId = connectionId
            });
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            LastDisconnectReason = disconnectInfo.Reason.ToString();
            int connectionId;
            lock (_peerGate)
            {
                if (!_peerToConnection.TryGetValue(peer.Id, out connectionId))
                {
                    // Admitted but never fully connected (or already
                    // released): free exactly THIS peer's reservation and
                    // nothing else, so churn cannot shift capacity.
                    _pendingAdmissions.Remove(peer.Id);
                    return;
                }

                // Free every per-peer record (mappings, limiter, C2
                // reassembly) as part of the disconnect itself.
                RemovePeerState(peer.Id, connectionId);
            }

            // RemoteConnectionClose/DisconnectPeer are the peer's graceful
            // disconnect notice; only timeout/transport loss feeds grace.
            TransportDisconnectReason reason;
            switch (disconnectInfo.Reason)
            {
                case DisconnectReason.Timeout:
                    reason = TransportDisconnectReason.Timeout;
                    break;
                case DisconnectReason.RemoteConnectionClose:
                case DisconnectReason.DisconnectPeerCalled:
                    reason = TransportDisconnectReason.ClientRequested;
                    break;
                default:
                    reason = TransportDisconnectReason.TransportLost;
                    break;
            }
            EnqueueEvent(new TransportEvent
            {
                Type = TransportEventType.Disconnected,
                ConnectionId = connectionId,
                Reason = reason
            });
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            var bytes = reader.GetRemainingBytes();
            reader.Recycle();

            RateLimiter limiter;
            int connectionId;
            lock (_peerGate)
            {
                if (!_peerToConnection.TryGetValue(peer.Id, out connectionId))
                {
                    _metrics.DroppedBadToken++;
                    return;
                }

                limiter = _peerLimiters[peer.Id];
            }

            if (!limiter.TryConsume(bytes.Length, _clock.NowMs))
            {
                _metrics.DroppedRateLimited++;
                return;
            }

            _metrics.DatagramsReceived++;
            _metrics.BytesReceived += bytes.Length;

            if (deliveryMethod == DeliveryMethod.Sequenced)
            {
                HandleSnapshotReceive(connectionId, bytes);
                return;
            }

            EnqueueEvent(new TransportEvent
            {
                Type = TransportEventType.Message,
                ConnectionId = connectionId,
                Channel = TransportChannel.Control,
                Data = bytes,
                Length = bytes.Length
            });
        }

        /// <summary>
        /// Reassembles application-fragmented C2 snapshots with the same
        /// latest-wins/stale policy as the own carrier; only a fully
        /// reassembled logical snapshot is delivered upward (one message,
        /// however many fragments it took).
        /// </summary>
        private void HandleSnapshotReceive(int connectionId, byte[] bytes)
        {
            PeerSnapshotState state;
            lock (_peerGate)
            {
                if (!_snapshotStates.TryGetValue(connectionId, out state))
                {
                    state = new PeerSnapshotState();
                    _snapshotStates[connectionId] = state;
                }
            }

            if (bytes.Length < PeerSnapshotState.PrefixBytes)
            {
                _metrics.DroppedMalformed++;
                return;
            }

            var messageId = ReadUInt16(bytes, 0);
            var sequence = ReadUInt16(bytes, 2);
            var fragmentIndex = ReadUInt16(bytes, 4);
            var fragmentCount = ReadUInt16(bytes, 6);
            if (fragmentCount == 0 ||
                fragmentCount > TransportProtocol.MaxFragmentsPerGroup ||
                fragmentIndex >= fragmentCount)
            {
                _metrics.DroppedMalformed++;
                return;
            }

            byte[] message = null;
            lock (_peerGate)
            {
                // Hard stale-drop: a strictly newer snapshot was already
                // delivered; late fragments of an older one must not
                // reassemble after it (latest-wins).
                if (state.HasDeliveredSeq &&
                    SerialArithmetic.Diff(sequence, state.DeliveredSeq) < 0)
                {
                    _metrics.DroppedStaleSnapshot++;
                    return;
                }

                if (fragmentCount == 1)
                {
                    var payload = new byte[bytes.Length - PeerSnapshotState.PrefixBytes];
                    System.Array.Copy(bytes, PeerSnapshotState.PrefixBytes, payload, 0, payload.Length);
                    message = payload;
                }
                else
                {
                    state.SeqByMessageId[messageId] = sequence;
                    message = state.Assembler.AddFragment(
                        messageId,
                        fragmentIndex,
                        fragmentCount,
                        bytes,
                        PeerSnapshotState.PrefixBytes,
                        bytes.Length - PeerSnapshotState.PrefixBytes,
                        _clock.NowMs);
                    if (message != null)
                    {
                        state.SeqByMessageId.Remove(messageId);
                    }
                }

                if (message != null)
                {
                    if (!state.HasDeliveredSeq ||
                        SerialArithmetic.Diff(sequence, state.DeliveredSeq) > 0)
                    {
                        state.DeliveredSeq = sequence;
                    }

                    state.HasDeliveredSeq = true;

                    // Latest-wins memory policy: with this snapshot
                    // delivered, older incomplete groups are stale and are
                    // discarded safely (collect first — no mutation while
                    // iterating).
                    var staleIds = new List<ushort>();
                    foreach (var pair in state.SeqByMessageId)
                    {
                        if (SerialArithmetic.Diff(pair.Value, sequence) < 0)
                        {
                            staleIds.Add(pair.Key);
                        }
                    }

                    for (var index = 0; index < staleIds.Count; index++)
                    {
                        state.Assembler.DiscardGroup(staleIds[index]);
                        state.SeqByMessageId.Remove(staleIds[index]);
                        _metrics.DroppedStaleSnapshot++;
                    }
                }
            }

            if (message != null)
            {
                _metrics.MessagesReceived++;
                EnqueueEvent(new TransportEvent
                {
                    Type = TransportEventType.Message,
                    ConnectionId = connectionId,
                    Channel = TransportChannel.Snapshot,
                    Data = message,
                    Length = message.Length
                });
            }
        }

        public void OnNetworkError(System.Net.IPEndPoint endPoint, SocketError socketError)
        {
            LastNetworkError = endPoint + ": " + socketError;
            _metrics.DroppedMalformed++;
        }

        public void OnNetworkReceiveUnconnected(
            System.Net.IPEndPoint remoteEndPoint,
            NetPacketReader reader,
            UnconnectedMessageType messageType)
        {
            reader.Recycle();
            _metrics.DroppedMalformed++;
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            // Latency is tracked by LiteNetLib internally; not consumed.
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            ConnectionRequestsSeen++;
            if (!_isServer || _shutdown)
            {
                request.Reject();
                return;
            }

            // Carrier-level admission control (same baseline as
            // OwnDatagramCarrier): the connection table is bounded by
            // MaxConnections; a request beyond the cap is rejected BEFORE
            // any per-peer state exists. The accepted peer's reservation is
            // tracked per peer until it materializes in OnPeerConnected.
            // Session attribution still happens at the message layer;
            // authentication stays deferred (ADR-009).
            lock (_peerGate)
            {
                if (_connectionToPeer.Count + _pendingAdmissions.Count >=
                    TransportProtocol.MaxConnections)
                {
                    _metrics.DroppedConnectionLimit++;
                    request.Reject();
                    return;
                }

                var peer = request.Accept();
                if (peer != null)
                {
                    _pendingAdmissions.Add(peer.Id);
                }
            }
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
    }
}
