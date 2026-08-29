namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Transport channels of Phase 2.5 (ADR-009).
    /// </summary>
    public enum TransportChannel : byte
    {
        /// <summary>Reliable ordered control traffic (handshake, ping, acks).</summary>
        Control = 0,

        /// <summary>Reliable ordered client commands.</summary>
        Command = 1,

        /// <summary>Unreliable sequenced snapshots, latest-wins, never ACKed.</summary>
        Snapshot = 2
    }

    /// <summary>
    /// Project-owned message types. The wire identity of the message model
    /// is <see cref="TransportProtocol.MessageVersion"/>, independent of the
    /// carrier framing version and of <c>SnapshotProtocol.Version</c>.
    /// </summary>
    public enum TransportMessageType : byte
    {
        None = 0,
        ConnectRequest = 1,
        ConnectAccept = 2,
        ConnectDenied = 3,
        Disconnect = 4,
        Ping = 5,
        Pong = 6,
        Command = 7,
        CommandAck = 8,
        Snapshot = 9
    }

    /// <summary>Why the server denied a connect request.</summary>
    public enum ConnectDenyReason : byte
    {
        None = 0,
        MessageVersionMismatch = 1,
        SnapshotVersionMismatch = 2,
        ServerFull = 3,
        Internal = 4
    }

    /// <summary>Reason carried by graceful disconnects and reported on loss.</summary>
    public enum TransportDisconnectReason : byte
    {
        None = 0,
        ClientRequested = 1,
        ServerShutdown = 2,
        Timeout = 3,
        MaxRetransmits = 4,
        TransportLost = 5,
        Rejected = 6
    }

    /// <summary>
    /// Protocol constants and limits of the Phase 2.5 transport (ADR-009).
    /// Version spaces are strictly separated:
    /// <see cref="CarrierVersion"/> — own carrier framing only;
    /// <see cref="MessageVersion"/> — project message model;
    /// <c>SnapshotProtocol.Version</c> — snapshot payload only.
    /// </summary>
    public static class TransportProtocol
    {
        /// <summary>Magic prefix rejecting foreign/garbage datagrams.</summary>
        public const ushort Magic = 0x4647;

        /// <summary>Version of the own-carrier datagram framing.</summary>
        public const ushort CarrierVersion = 1;

        /// <summary>Version of the project message model.</summary>
        public const ushort MessageVersion = 1;

        /// <summary>Reserved session token value used during handshake.</summary>
        public const ulong HandshakeToken = 0;

        /// <summary>Size of the message-level header (magic/version/type/token).</summary>
        public const int MessageHeaderSize = 13;

        /// <summary>Additional C2 envelope field: SnapshotTick (opaque payload ordering).</summary>
        public const int SnapshotTickSize = 8;

        // --- Datagram / envelope limits (own carrier) ---

        /// <summary>Target datagram size (MTU-safe, avoids IP fragmentation).</summary>
        public const int MaxDatagramBytes = 1200;

        /// <summary>Own-carrier envelope size without fragment header.</summary>
        public const int EnvelopeHeaderSize = 23;

        /// <summary>Own-carrier fragment info size.</summary>
        public const int FragmentHeaderSize = 6;

        /// <summary>Payload bytes per fragment datagram.</summary>
        public const int FragmentPayloadBytes =
            MaxDatagramBytes - EnvelopeHeaderSize - FragmentHeaderSize;

        // --- Message / reassembly limits (ADR-009) ---

        /// <summary>Max transport message size (resync-readiness &gt; 64 KB).</summary>
        public const int MaxMessageBytes = 1024 * 1024;

        /// <summary>Max reassembly memory per peer.</summary>
        public const int ReassemblyBudgetBytes = 2 * 1024 * 1024;

        /// <summary>Max concurrent fragment groups per peer.</summary>
        public const int MaxFragmentGroups = 4;

        /// <summary>Max fragments per group.</summary>
        public const int MaxFragmentsPerGroup = 2048;

        /// <summary>Fragment group lifetime in milliseconds.</summary>
        public const long FragmentLifetimeMs = 2000;

        // --- Reliability (own carrier invariants, ADR-009) ---

        /// <summary>Receive window: seqs accepted within (AckBase, AckBase + W].</summary>
        public const int ReceiveWindow = 1024;

        /// <summary>Reorder window: out-of-order buffering depth.</summary>
        public const int ReorderWindow = 64;

        /// <summary>Selective-ack bitmap width (seqs ahead of AckNumber).</summary>
        public const int AckBitmapBits = 32;

        public const long InitialRtoMs = 200;
        public const long MinRtoMs = 50;
        public const long MaxRtoMs = 1000;
        public const int MaxRetransmits = 10;

        // --- Liveness / keepalive (OD-8 reference values, tunable) ---

        public const long KeepaliveIntervalMs = 1000;
        public const long IdleTimeoutMs = 5000;

        // --- Rate limiting baseline (per endpoint) ---

        public const int RateLimitPacketsPerSecond = 200;
        public const int RateLimitBytesPerSecond = 256 * 1024;

        // --- Bounded queues ---

        public const int MaxQueuedReceiveEvents = 4096;

        // --- Bounded connection state (security baseline) ---

        /// <summary>
        /// Max concurrent carrier connections. Unknown/spoofed sources must
        /// never create unbounded per-connection state; traffic beyond the
        /// cap is dropped with a counter.
        /// </summary>
        public const int MaxConnections = 64;

        /// <summary>Max tracked pre-handshake remote addresses.</summary>
        public const int MaxTrackedPreHandshakePeers = 256;
    }
}
