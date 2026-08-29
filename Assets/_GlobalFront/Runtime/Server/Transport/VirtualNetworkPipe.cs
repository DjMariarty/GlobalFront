using System.Collections.Generic;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Message-level carrier contract of Phase 2.5 (ADR-009). Hides the
    /// delivery substrate: <c>OwnDatagramCarrier</c> (project envelope + ARQ
    /// over <see cref="VirtualNetworkPipe"/> or raw UDP) and
    /// <c>LiteNetLibCarrier</c> (adopted carrier) implement the SAME
    /// contract. Events are produced on receive/worker paths and consumed
    /// only on the host/simulation thread (concurrency invariant).
    /// </summary>
    public enum TransportEventType : byte
    {
        Connected = 0,
        Disconnected = 1,
        Message = 2
    }

    /// <summary>
    /// Carrier event dequeued by the endpoint on the host thread.
    /// <see cref="Data"/> is owned by the event; length in
    /// <see cref="Length"/>.
    /// </summary>
    public struct TransportEvent
    {
        public TransportEventType Type;
        public int ConnectionId;
        public TransportChannel Channel;
        public TransportDisconnectReason Reason;
        public byte[] Data;
        public int Length;
    }

    public interface INetworkCarrier : System.IDisposable
    {
        TransportMetrics Metrics { get; }

        /// <summary>Current transport clock time hook for timers.</summary>
        ITransportClock Clock { get; }

        /// <summary>Server side: begin accepting connections.</summary>
        void StartServer();

        /// <summary>Client side: open the connection to the server.</summary>
        int ConnectToServer(string host, int port);

        /// <summary>
        /// Sends one transport message over the channel's delivery
        /// semantics. Messages above MaxMessageBytes are rejected.
        /// </summary>
        bool Send(int connectionId, TransportChannel channel, byte[] data, int offset, int length);

        /// <summary>Gracefully closes a connection (best-effort notice).</summary>
        void CloseConnection(int connectionId, TransportDisconnectReason reason);

        /// <summary>
        /// Assigns the session token mirrored into carrier-level attribution
        /// for this connection (own-carrier envelope filtering). Carriers
        /// without envelope attribution treat this as a no-op.
        /// </summary>
        void AssignSessionToken(int connectionId, ulong token);

        /// <summary>Stops the carrier; open connections are dropped.</summary>
        void Shutdown(TransportDisconnectReason reason);

        /// <summary>
        /// Advances carrier timers (retransmission, keepalive, idle) and
        /// delivery processing. Host thread only.
        /// </summary>
        void Pump(long nowMs);

        /// <summary>Dequeues the next event; host thread only.</summary>
        bool TryDequeueEvent(out TransportEvent transportEvent);

        /// <summary>Current event queue depth (bounded).</summary>
        int EventQueueDepth { get; }
    }

    /// <summary>
    /// Seeded deterministic impairment profile for
    /// <see cref="VirtualNetworkPipe"/>: loss, duplication, reordering,
    /// latency, corruption and partition — all reproducible by seed.
    /// </summary>
    public sealed class ImpairmentProfile
    {
        public double LossProbability;
        public double DuplicationProbability;
        public double CorruptionProbability;
        public long LatencyMs;
        public long ReorderJitterMs;
        public bool Partitioned;
        public int Seed = 1;
    }

    /// <summary>
    /// Deterministic in-memory datagram network (ADR-009 test foundation).
    /// Moves raw datagrams between endpoint addresses through a seeded
    /// impairment model; delivery timing advances only through
    /// <see cref="Advance"/>, so runs are fully reproducible and free of
    /// wall-clock dependency.
    /// </summary>
    public sealed class VirtualNetworkPipe
    {
        private sealed class Pending
        {
            public int From;
            public int To;
            public byte[] Data;
            public long DeliverAtMs;
        }

        private readonly object _gate = new object();
        private readonly ImpairmentProfile _profile;
        private readonly System.Random _random;
        private readonly List<Pending> _inFlight = new List<Pending>();
        private readonly Dictionary<int, Queue<Pending>> _delivered =
            new Dictionary<int, Queue<Pending>>();

        private long _nowMs;
        private int _nextEndpoint = 1;

        public VirtualNetworkPipe(ImpairmentProfile profile)
        {
            _profile = profile ?? new ImpairmentProfile();
            _random = new System.Random(_profile.Seed);
        }

        public long NowMs
        {
            get { lock (_gate) { return _nowMs; } }
        }

        public int CreateEndpoint()
        {
            lock (_gate)
            {
                var id = _nextEndpoint++;
                _delivered[id] = new Queue<Pending>();
                return id;
            }
        }

        public void SetPartitioned(bool partitioned)
        {
            lock (_gate)
            {
                _profile.Partitioned = partitioned;
            }
        }

        /// <summary>Sends a datagram through the impairment model.</summary>
        public void SendDatagram(int from, int to, byte[] data, int length)
        {
            lock (_gate)
            {
                if (_profile.Partitioned)
                {
                    return;
                }

                if (_random.NextDouble() < _profile.LossProbability)
                {
                    return;
                }

                Schedule(from, to, CopyWithCorruption(data, length));

                if (_random.NextDouble() < _profile.DuplicationProbability)
                {
                    Schedule(from, to, CopyWithCorruption(data, length));
                }
            }
        }

        /// <summary>Advances virtual time, releasing due deliveries in FIFO order.</summary>
        public void Advance(long milliseconds)
        {
            lock (_gate)
            {
                _nowMs += milliseconds;

                // Forward iteration keeps same-deadline datagrams in send
                // order (FIFO); reverse iteration would flip bursts.
                var dueCount = 0;
                for (var index = 0; index < _inFlight.Count; index++)
                {
                    var pending = _inFlight[index];
                    if (pending.DeliverAtMs > _nowMs)
                    {
                        continue;
                    }

                    // Unknown destinations silently drop the datagram —
                    // real-network behavior, keeping spoofed-source traffic
                    // from leaking into the delivery table.
                    if (_delivered.TryGetValue(pending.To, out var deliveryQueue))
                    {
                        deliveryQueue.Enqueue(pending);
                    }

                    _inFlight[index] = null;
                    dueCount++;
                }

                if (dueCount > 0)
                {
                    _inFlight.RemoveAll(item => item == null);
                }
            }
        }

        /// <summary>Dequeues one delivered datagram for the endpoint, if any.</summary>
        public bool TryReceive(int endpoint, out int from, out byte[] datagram)
        {
            lock (_gate)
            {
                if (_delivered.TryGetValue(endpoint, out var queue) && queue.Count > 0)
                {
                    var pending = queue.Dequeue();
                    from = pending.From;
                    datagram = pending.Data;
                    return true;
                }

                from = 0;
                datagram = null;
                return false;
            }
        }

        public int InFlightCount
        {
            get { lock (_gate) { return _inFlight.Count; } }
        }

        private void Schedule(int from, int to, byte[] data)
        {
            var jitter = _profile.ReorderJitterMs > 0
                ? _random.Next(0, (int)_profile.ReorderJitterMs + 1)
                : 0;
            var pending = new Pending
            {
                From = from,
                To = to,
                Data = data,
                DeliverAtMs = _nowMs + _profile.LatencyMs + jitter
            };
            _inFlight.Add(pending);
        }

        private byte[] CopyWithCorruption(byte[] data, int length)
        {
            var copy = new byte[length];
            System.Array.Copy(data, copy, length);
            if (_profile.CorruptionProbability > 0 &&
                _random.NextDouble() < _profile.CorruptionProbability &&
                length > 0)
            {
                var position = _random.Next(length);
                copy[position] = (byte)(copy[position] ^ 0xFF);
            }

            return copy;
        }
    }
}
