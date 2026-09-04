using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server.Transport;

namespace GlobalFront.Server.Replication
{
    /// <summary>
    /// Protocol-significant configuration of the server replication emitter
    /// (Phase 2.6, step 2.6.4, ADR-010). Every value shapes the wire: changing
    /// one requires a compatibility review, not a code edit.
    /// </summary>
    public readonly struct ServerReplicationEmitterConfig : IEquatable<ServerReplicationEmitterConfig>
    {
        public const int DefaultMaxClients = 8;
        public const int DefaultSnapshotCapacity = 4096;
        public const int DefaultAddCapacity = 512;
        public const int DefaultUpdateCapacity = 4096;
        public const int DefaultRemoveCapacity = 512;

        /// <summary>Per-tick pacing refill, ~100 KB/s at the 20 Hz tick (R&amp;D §10).</summary>
        public const int DefaultPacingRefillBytesPerTick = 2048;

        /// <summary>Per-tick burst cap, R&amp;D §3.4 (≤ 16 KB per replication tick).</summary>
        public const int DefaultPacingBurstCapBytes = 16384;

        /// <summary>Keyframe slice payload budget; R&amp;D §11 recommends 8–16 KB.</summary>
        public const int DefaultMaxSlicePayloadBytes = 16384;

        /// <summary>0 disables periodic keyframes (ADR-010 leaves the interval to the owner).</summary>
        public const int DefaultPeriodicKeyframeTicks = 0;

        /// <summary>
        /// Idle keep-alive cadence (audit P1-4): when no packet was sent to a
        /// client for this many ticks, the emitter emits a header-only
        /// establishing delta so the client's confirmed tick — and with it the
        /// shared delta base — never drifts out of the 120-tick history window.
        /// 20 ticks = 1 s at the 20 Hz contract.
        /// </summary>
        public const int DefaultIdleKeepAliveTicks = 20;

        /// <summary>
        /// State-checksum cadence (audit P1-2): every delta sent at least this
        /// many ticks after the previous one carries
        /// <see cref="DeltaFlags.HasChecksum"/> with a deterministic fingerprint
        /// of the live world. 20 ticks = 1 Hz at the 20 Hz contract.
        /// </summary>
        public const int DefaultChecksumIntervalTicks = 20;

        public ServerReplicationEmitterConfig(
            int maxClients,
            int snapshotCapacity,
            int addCapacity,
            int updateCapacity,
            int removeCapacity,
            int pacingRefillBytesPerTick,
            int pacingBurstCapBytes,
            int maxSlicePayloadBytes,
            int periodicKeyframeTicks)
        {
            if (maxClients < 1 || maxClients > TransportProtocol.MaxConnections)
            {
                throw new ArgumentOutOfRangeException(nameof(maxClients));
            }

            if (snapshotCapacity < 1 ||
                addCapacity < 1 ||
                updateCapacity < 1 ||
                removeCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(snapshotCapacity));
            }

            if (pacingRefillBytesPerTick < 1 ||
                pacingBurstCapBytes < pacingRefillBytesPerTick ||
                maxSlicePayloadBytes < KeyframeSliceCodec.HeaderSizeBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(pacingRefillBytesPerTick));
            }

            if (periodicKeyframeTicks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(periodicKeyframeTicks));
            }

            MaxClients = maxClients;
            SnapshotCapacity = snapshotCapacity;
            AddCapacity = addCapacity;
            UpdateCapacity = updateCapacity;
            RemoveCapacity = removeCapacity;
            PacingRefillBytesPerTick = pacingRefillBytesPerTick;
            PacingBurstCapBytes = pacingBurstCapBytes;
            MaxSlicePayloadBytes = maxSlicePayloadBytes;
            PeriodicKeyframeTicks = periodicKeyframeTicks;
        }

        public int MaxClients { get; }

        public int SnapshotCapacity { get; }

        public int AddCapacity { get; }

        public int UpdateCapacity { get; }

        public int RemoveCapacity { get; }

        public int PacingRefillBytesPerTick { get; }

        public int PacingBurstCapBytes { get; }

        public int MaxSlicePayloadBytes { get; }

        public int PeriodicKeyframeTicks { get; }

        public static ServerReplicationEmitterConfig Default => new ServerReplicationEmitterConfig(
            DefaultMaxClients,
            DefaultSnapshotCapacity,
            DefaultAddCapacity,
            DefaultUpdateCapacity,
            DefaultRemoveCapacity,
            DefaultPacingRefillBytesPerTick,
            DefaultPacingBurstCapBytes,
            DefaultMaxSlicePayloadBytes,
            DefaultPeriodicKeyframeTicks);

        public bool Equals(ServerReplicationEmitterConfig other) =>
            MaxClients == other.MaxClients &&
            SnapshotCapacity == other.SnapshotCapacity &&
            AddCapacity == other.AddCapacity &&
            UpdateCapacity == other.UpdateCapacity &&
            RemoveCapacity == other.RemoveCapacity &&
            PacingRefillBytesPerTick == other.PacingRefillBytesPerTick &&
            PacingBurstCapBytes == other.PacingBurstCapBytes &&
            MaxSlicePayloadBytes == other.MaxSlicePayloadBytes &&
            PeriodicKeyframeTicks == other.PeriodicKeyframeTicks;

        public override bool Equals(object obj) =>
            obj is ServerReplicationEmitterConfig other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            MaxClients, SnapshotCapacity, PacingRefillBytesPerTick, PacingBurstCapBytes,
            MaxSlicePayloadBytes, PeriodicKeyframeTicks, AddCapacity, UpdateCapacity);
    }

    /// <summary>Read-only diagnostic view of one tracked client (tests/telemetry).</summary>
    public readonly struct ServerReplicationClientView
    {
        public ServerReplicationClientView(
            SessionId session,
            bool active,
            bool needsKeyframe,
            bool keyframeInFlight,
            ulong lastAckedTick,
            ulong lastSentTick,
            ulong keyframeTick,
            ushort keyframeSeq,
            int pendingPartCount,
            int pendingNextPart,
            int pacingTokens,
            int stagedUnitCount)
        {
            Session = session;
            Active = active;
            NeedsKeyframe = needsKeyframe;
            KeyframeInFlight = keyframeInFlight;
            LastAckedTick = lastAckedTick;
            LastSentTick = lastSentTick;
            KeyframeTick = keyframeTick;
            KeyframeSeq = keyframeSeq;
            PendingPartCount = pendingPartCount;
            PendingNextPart = pendingNextPart;
            PacingTokens = pacingTokens;
            StagedUnitCount = stagedUnitCount;
        }

        public SessionId Session { get; }
        public bool Active { get; }
        public bool NeedsKeyframe { get; }
        public bool KeyframeInFlight { get; }
        public ulong LastAckedTick { get; }
        public ulong LastSentTick { get; }
        public ulong KeyframeTick { get; }
        public ushort KeyframeSeq { get; }
        public int PendingPartCount { get; }
        public int PendingNextPart { get; }
        public int PacingTokens { get; }
        public int StagedUnitCount { get; }
    }

    /// <summary>
    /// Server-side replication emitter (Phase 2.6, step 2.6.4, ADR-010): the
    /// missing link between the authoritative tick loop, the Phase 2.5
    /// transport and the client receiver of step 2.6.3.
    ///
    /// Per completed tick (<see cref="OnTickCompleted"/>):
    /// <list type="number">
    /// <item>the world is captured into preallocated buffers (zero-GC capture
    /// via <see cref="IServerSnapshotSource"/>) and the diff engine folds it
    /// into a per-tick change-set recorded in the
    /// <see cref="ReplicationHistoryRing"/>;</item>
    /// <item>every tracked client is serviced: an Establishing Delta of
    /// <c>(base, tick]</c> is merged with
    /// <see cref="ReplicationHistoryRing.TryMergeRange"/>, encoded and sent on
    /// C2, where <c>base = max(LastAckedTick, LastSentTick)</c>. The strict
    /// max() tiling never overlaps intervals: replaying an ADD record against
    /// a client that already holds it would fail as a duplicate and force a
    /// needless re-baseline. A lost delta therefore surfaces as a guarded gap
    /// on the client (CATCHING_UP) and is answered by a DeltaResume request —
    /// the cumulative merge is exactly what makes skipped ticks safe;</item>
    /// <item>a quiet world does not stall the stream: when 20 ticks passed
    /// without a sendable packet, the emitter emits a header-only (36 bytes,
    /// zero records) Establishing Delta. The client applies it, advances its
    /// confirmed tick and acks, so the shared base keeps moving and never
    /// falls out of the 120-tick history window — an idle world must never
    /// trigger a re-baseline storm (audit P1-4). The DeltaResume answer for an
    /// empty interval is the same header-only delta, so a repair episode also
    /// ends with a packet the client can ack;</item>
    /// <item>at the 1 Hz cadence a sent delta carries
    /// <see cref="DeltaFlags.HasChecksum"/> with a deterministic FNV-1a
    /// fingerprint over the live units (entity id, position, health),
    /// computed from the latest capture (audit P1-2);</item>
    /// <item>a keyframe replaces the stream whenever the merge is impossible
    /// (base evicted from the 120-tick window, a change-set overflowed its
    /// buffer, or the merged delta exceeds the 8 KB payload bound). Keyframes
    /// are cut into ≤ 16 KB slices with the
    /// <see cref="KeyframeSliceCodec"/> framing, share one
    /// <c>KeyframeSeq</c> generation and leave through the same token-bucket
    /// pacing as deltas (R&amp;D §3.4/§10: refill ≈ 100 KB/s, burst ≤ 16 KB per
    /// tick) so command traffic is never crowded out.</item>
    /// </list>
    ///
    /// Client feedback arrives on C0 through
    /// <see cref="IReplicationTransport.FeedbackReceived"/>: SnapshotAck
    /// records advance the client's confirmed base (and flag unbased or
    /// evicted clients for a keyframe), while wire requests map to a fresh
    /// keyframe (SnapshotRequest — the receiver FSM of step 2.6.3 opens its
    /// baseline episode with one as its very first action, so the emitter is
    /// deliberately request-driven and does not push keyframes on attach) or
    /// to a merged establishing delta from the client's confirmed tick
    /// (DeltaResume, falling back to a keyframe when the window or the
    /// payload bound is exceeded).
    ///
    /// Composition: construct the emitter (via
    /// <see cref="TransportReplicationAdapter"/>) BEFORE clients connect, and
    /// call <see cref="OnTickCompleted"/> from
    /// <c>LocalMatchHost.TickCompleted</c>. Zero-GC: every buffer is
    /// preallocated; the only allocations happen off the hot path (per-client
    /// keyframe staging on first use, client bookkeeping on attach/detach).
    /// </summary>
    public sealed class ServerReplicationEmitter
    {
        private readonly IServerSnapshotSource _source;
        private readonly IReplicationTransport _transport;
        private readonly ServerReplicationEmitterConfig _config;
        private readonly ServerSnapshotDiffEngine _engine;
        private readonly ReplicationHistoryRing _ring;
        private readonly ReplicationChangeSetBuilder _builder;
        private readonly ServerUnitSnapshot[][] _captures;
        private readonly int[] _captureCounts;
        private readonly DeltaAddRecord[] _addScratch;
        private readonly byte[] _deltaBuffer;
        private readonly byte[] _sliceBuffer;
        private readonly ClientState[] _clients;
        private readonly int _recordsPerSlice;
        private readonly int _effectiveBurstBytes;

        private int _captureIndex;
        private bool _hasCapture;
        private ulong _lastCompletedTick;

        // Diagnostics.
        private long _emittedDeltaCount;
        private long _emittedKeyframeCount;
        private long _emittedSliceCount;
        private long _deltaResumeServedCount;
        private long _keyframeFallbackCount;
        private long _ackCount;
        private long _requestCount;
        private long _malformedFeedbackCount;
        private long _sendFailureCount;
        private long _snapshotShortfallCount;
        private long _diffFailureCount;
        private long _idleKeepAliveDeltaCount;
        private long _stateChecksumCount;

        /// <summary>Per-client replication bookkeeping. See <see cref="ClientState"/>.</summary>
        private sealed class ClientState
        {
            public SessionId Session;
            public bool Active;
            public bool NeedsKeyframe;
            public bool KeyframeInFlight;
            public ulong LastAckedTick;
            public ulong LastSentTick;
            public ushort NextKeyframeSeq;
            public ulong KeyframeTick;
            public ushort KeyframeSeq;
            public int StagedUnitCount;
            public int PendingPartCount;
            public int PendingNextPart;
            public int PacingTokens;
            public ulong LastKeyframeTick;

            /// <summary>Tick the client's stream last carried a state checksum.</summary>
            public ulong LastChecksumTick;

            public byte[] KeyframeStaging;
        }

        public ServerReplicationEmitter(MatchServer server, IReplicationTransport transport)
            : this(new MatchServerSnapshotSource(server), transport, ServerReplicationEmitterConfig.Default)
        {
        }

        public ServerReplicationEmitter(
            MatchServer server,
            IReplicationTransport transport,
            in ServerReplicationEmitterConfig config)
            : this(new MatchServerSnapshotSource(server), transport, config)
        {
        }

        public ServerReplicationEmitter(
            IServerSnapshotSource source,
            IReplicationTransport transport,
            in ServerReplicationEmitterConfig config)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _config = config;

            _engine = new ServerSnapshotDiffEngine(
                config.AddCapacity, config.UpdateCapacity, config.RemoveCapacity);
            _ring = new ReplicationHistoryRing(
                config.AddCapacity, config.UpdateCapacity, config.RemoveCapacity);
            _builder = new ReplicationChangeSetBuilder(
                config.AddCapacity, config.UpdateCapacity, config.RemoveCapacity);

            _captures = new ServerUnitSnapshot[2][];
            _captures[0] = new ServerUnitSnapshot[config.SnapshotCapacity];
            _captures[1] = new ServerUnitSnapshot[config.SnapshotCapacity];
            _captureCounts = new int[2];
            _addScratch = new DeltaAddRecord[config.SnapshotCapacity];
            _deltaBuffer = new byte[DeltaSnapshotProtocol.MaxPacketBytes];
            _recordsPerSlice = Math.Max(
                1, KeyframeSliceCodec.MaxRecordsForSliceBudget(config.MaxSlicePayloadBytes));
            _sliceBuffer = new byte[KeyframeSliceCodec.GetSliceSize(_recordsPerSlice)];
            _effectiveBurstBytes = Math.Max(
                config.PacingBurstCapBytes, _sliceBuffer.Length);
            _clients = new ClientState[config.MaxClients];

            _transport.SessionAttached += OnSessionAttached;
            _transport.SessionDetached += OnSessionDetached;
            _transport.FeedbackReceived += OnFeedback;
        }

        public ServerReplicationEmitterConfig Config => _config;

        public ReplicationHistoryRing History => _ring;

        public int TrackedClientCount
        {
            get
            {
                var count = 0;
                for (var index = 0; index < _clients.Length; index++)
                {
                    if (_clients[index] != null && _clients[index].Active)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public long EmittedDeltaCount => _emittedDeltaCount;
        public long EmittedKeyframeCount => _emittedKeyframeCount;
        public long EmittedSliceCount => _emittedSliceCount;
        public long DeltaResumeServedCount => _deltaResumeServedCount;
        public long KeyframeFallbackCount => _keyframeFallbackCount;
        public long AckCount => _ackCount;
        public long RequestCount => _requestCount;
        public long MalformedFeedbackCount => _malformedFeedbackCount;
        public long SendFailureCount => _sendFailureCount;
        public long SnapshotShortfallCount => _snapshotShortfallCount;
        public long DiffFailureCount => _diffFailureCount;

        /// <summary>Header-only idle keep-alive deltas sent (audit P1-4).</summary>
        public long IdleKeepAliveDeltaCount => _idleKeepAliveDeltaCount;

        /// <summary>Deltas that carried a <see cref="DeltaFlags.HasChecksum"/> fingerprint (audit P1-2).</summary>
        public long StateChecksumCount => _stateChecksumCount;

        /// <summary>
        /// Advances the replication pipeline for one completed authoritative
        /// tick. Wire it to <c>LocalMatchHost.TickCompleted</c>; call it after
        /// the tick's simulation has run so the capture reflects the tick.
        /// </summary>
        public void OnTickCompleted(ulong tick)
        {
            CaptureAndRecord(tick);

            for (var index = 0; index < _clients.Length; index++)
            {
                var client = _clients[index];
                if (client == null || !client.Active)
                {
                    continue;
                }

                client.PacingTokens = Math.Min(
                    _effectiveBurstBytes, client.PacingTokens + _config.PacingRefillBytesPerTick);

                if (!client.KeyframeInFlight)
                {
                    if (client.NeedsKeyframe ||
                        ShouldSendPeriodicKeyframe(client, tick))
                    {
                        StartKeyframe(client, tick);
                    }
                }

                if (client.KeyframeInFlight)
                {
                    PumpKeyframeSlices(client);
                }
                else
                {
                    StreamDeltas(client, tick);
                }
            }
        }

        /// <summary>Diagnostic view of one tracked client.</summary>
        public bool TryGetClientView(SessionId session, out ServerReplicationClientView view)
        {
            var client = FindClient(session);
            if (client == null)
            {
                view = default;
                return false;
            }

            view = new ServerReplicationClientView(
                client.Session,
                client.Active,
                client.NeedsKeyframe,
                client.KeyframeInFlight,
                client.LastAckedTick,
                client.LastSentTick,
                client.KeyframeTick,
                client.KeyframeSeq,
                client.PendingPartCount,
                client.PendingNextPart,
                client.PacingTokens,
                client.StagedUnitCount);
            return true;
        }

        // ------------------------------------------------------------------
        // Tick pipeline
        // ------------------------------------------------------------------

        private void CaptureAndRecord(ulong tick)
        {
            _lastCompletedTick = tick;

            var next = 1 - _captureIndex;
            var count = _source.CopySnapshots(_captures[next]);
            if (count < 0)
            {
                // The world outgrew the configured capture capacity: keep the
                // last good capture, stop diffing (a missed diff tick makes
                // merges over it fail, which routes clients to a keyframe
                // instead of a silent desync).
                _snapshotShortfallCount++;
                return;
            }

            var previousIndex = _captureIndex;
            var previousCount = _captureCounts[previousIndex];
            var canDiff = _hasCapture;
            _captureIndex = next;
            _captureCounts[next] = count;
            _hasCapture = true;

            if (!canDiff)
            {
                return;
            }

            var result = _engine.Diff(
                _captures[previousIndex].AsSpan(0, previousCount),
                _captures[next].AsSpan(0, count),
                out var changeSet);
            if (result != ReplicationDiffResult.Ok)
            {
                // The tick has no replayable change-set; merges that would
                // cover it fail and clients re-baseline.
                _diffFailureCount++;
                return;
            }

            _ring.RecordTick(tick, in changeSet);
        }

        /// <summary>Captures the world outside the tick loop (repair path).</summary>
        private void EnsureCapture()
        {
            if (!_hasCapture)
            {
                CaptureAndRecord(_lastCompletedTick);
            }
        }

        private bool ShouldSendPeriodicKeyframe(ClientState client, ulong tick)
        {
            if (_config.PeriodicKeyframeTicks <= 0 ||
                client.LastKeyframeTick == 0 ||
                tick < client.LastKeyframeTick)
            {
                return false;
            }

            return tick - client.LastKeyframeTick >= (ulong)_config.PeriodicKeyframeTicks;
        }

        // ------------------------------------------------------------------
        // Delta stream
        // ------------------------------------------------------------------

        private void StreamDeltas(ClientState client, ulong tick)
        {
            if (client.NeedsKeyframe || client.KeyframeSeq == 0)
            {
                // No served keyframe yet: the request-driven baseline is in
                // flight, and a delta without an anchor would only be dropped.
                return;
            }

            var baseTick = Max(client.LastAckedTick, client.LastSentTick);
            if (baseTick >= tick)
            {
                return;
            }

            if (!_ring.TryMergeRange(baseTick, tick, _builder))
            {
                _keyframeFallbackCount++;
                QueueKeyframe(client);
                return;
            }

            var merged = _builder.ChangeSet;
            var isIdleKeepAlive = merged.RecordCount == 0;
            if (isIdleKeepAlive &&
                tick - client.LastSentTick <
                (ulong)ServerReplicationEmitterConfig.DefaultIdleKeepAliveTicks)
            {
                // Empty interval inside the keep-alive horizon: nothing to
                // anchor, and the client's confirmed tick is recent enough —
                // the next content delta still covers everything since the
                // shared base. LastSentTick must NOT advance — it is the base
                // the client is trusted to have applied, and it may only move
                // with a packet the client can actually apply.
                return;
            }

            // An empty interval beyond the keep-alive horizon still streams as
            // a header-only (36 bytes, zero records) Establishing Delta: it
            // advances the client's LastAppliedTick — and via its ack the
            // shared base — so a quiet world never lets the base fall out of
            // the 120-tick history ring and turn into a re-baseline storm
            // (audit P1-4). Advancing LastSentTick is safe here because this
            // empty delta IS a packet the client can apply.

            var sizeResult = DeltaSnapshotWireCodec.GetEncodedSize(
                merged.Adds, merged.Updates, merged.Removes, out var encodedSize);
            if (sizeResult != DeltaCodecResult.Ok ||
                encodedSize > DeltaSnapshotProtocol.MaxPacketBytes)
            {
                // An establishing delta beyond the payload bound means the
                // client drifted too far for one packet: re-baseline instead
                // of splitting (multipart deltas are reserved, not used).
                _keyframeFallbackCount++;
                QueueKeyframe(client);
                return;
            }

            if (client.PacingTokens < encodedSize)
            {
                // Out of budget: retry next tick from the same base.
                return;
            }

            var flags = DeltaFlags.None;
            var stateChecksum = 0u;
            if (tick - client.LastChecksumTick >=
                (ulong)ServerReplicationEmitterConfig.DefaultChecksumIntervalTicks)
            {
                stateChecksum = ComputeStateChecksum();
                flags |= DeltaFlags.HasChecksum;
                client.LastChecksumTick = tick;
                _stateChecksumCount++;
            }

            var header = DeltaSnapshotHeader.CreateDelta(
                tick,
                baseTick,
                flags,
                stateChecksum,
                (ushort)merged.AddCount,
                (ushort)merged.UpdateCount,
                (ushort)merged.RemoveCount,
                client.KeyframeSeq);
            var encodeResult = DeltaSnapshotWireCodec.TryEncode(
                header,
                merged.Adds,
                merged.Updates,
                merged.Removes,
                _deltaBuffer,
                out var written);
            if (encodeResult != DeltaCodecResult.Ok)
            {
                _keyframeFallbackCount++;
                QueueKeyframe(client);
                return;
            }

            if (!_transport.SendToSession(client.Session, tick, _deltaBuffer, written))
            {
                _sendFailureCount++;
                return;
            }

            client.LastSentTick = tick;
            client.PacingTokens -= written;
            _emittedDeltaCount++;
            if (isIdleKeepAlive)
            {
                _idleKeepAliveDeltaCount++;
            }
        }

        /// <summary>
        /// Deterministic 32-bit fingerprint of the live world (audit P1-2):
        /// FNV-1a over every captured unit in the capture's deterministic
        /// entity-id order, mixing entity id, position and health. Pure
        /// arithmetic — no allocations, no string or object hashing, so the
        /// value is reproducible across runs and platforms.
        /// </summary>
        private uint ComputeStateChecksum()
        {
            unchecked
            {
                var hash = 2166136261u;
                var capture = _captures[_captureIndex];
                var count = _captureCounts[_captureIndex];
                for (var index = 0; index < count; index++)
                {
                    var unit = capture[index];
                    hash = MixChecksumByte(hash, unit.Entity.Value);
                    hash = MixChecksumByte(hash, (ulong)unit.Position.X);
                    hash = MixChecksumByte(hash, (ulong)unit.Position.Z);
                    hash = MixChecksumByte(hash, (ulong)unit.CurrentHealth);
                }

                return hash;
            }
        }

        /// <summary>Folds the little-endian bytes of <paramref name="value"/> into the FNV-1a state.</summary>
        private static uint MixChecksumByte(uint hash, ulong value)
        {
            unchecked
            {
                for (var shift = 0; shift < sizeof(ulong) * 8; shift += 8)
                {
                    hash = (hash ^ (uint)((value >> shift) & 0xFF)) * 16777619u;
                }

                return hash;
            }
        }

        // ------------------------------------------------------------------
        // Keyframe pipeline
        // ------------------------------------------------------------------

        /// <summary>
        /// Flags a keyframe as needed and, unless one is already being paced
        /// out, starts and paces it immediately: a paused tick loop or a
        /// mid-pump request must not stall baseline repair.
        /// </summary>
        private void QueueKeyframe(ClientState client)
        {
            client.NeedsKeyframe = true;
            if (!client.KeyframeInFlight)
            {
                StartKeyframe(client, _lastCompletedTick);
                PumpKeyframeSlices(client);
            }
        }

        private void StartKeyframe(ClientState client, ulong tick)
        {
            EnsureCapture();

            var unitCount = _captureCounts[_captureIndex];
            var stagingBytes = unitCount * DeltaSnapshotProtocol.AddRecordSizeBytes;
            if (client.KeyframeStaging == null || client.KeyframeStaging.Length < stagingBytes)
            {
                client.KeyframeStaging = new byte[Math.Max(stagingBytes, 64)];
            }

            var capture = _captures[_captureIndex];
            for (var index = 0; index < unitCount; index++)
            {
                _addScratch[index] = ServerSnapshotDiffEngine.ToAddRecord(capture[index]);
            }

            var encodeResult = DeltaSnapshotWireCodec.TryEncodeAddRecords(
                _addScratch.AsSpan(0, unitCount),
                client.KeyframeStaging,
                out var staged);
            if (encodeResult != DeltaCodecResult.Ok)
            {
                // Unencodable world state (should be impossible for a capture
                // of the authoritative simulation): retry on a later tick.
                _snapshotShortfallCount++;
                return;
            }

            client.NextKeyframeSeq = (ushort)(client.NextKeyframeSeq + 1);
            client.KeyframeSeq = client.NextKeyframeSeq;
            client.KeyframeTick = tick;
            client.StagedUnitCount = unitCount;
            client.PendingPartCount = unitCount == 0
                ? 1
                : (unitCount + _recordsPerSlice - 1) / _recordsPerSlice;
            client.PendingNextPart = 0;
            client.KeyframeInFlight = true;
            client.NeedsKeyframe = false;
            client.LastSentTick = tick;
            client.LastKeyframeTick = tick;
            _emittedKeyframeCount++;
        }

        private void PumpKeyframeSlices(ClientState client)
        {
            while (client.KeyframeInFlight && client.PendingNextPart < client.PendingPartCount)
            {
                var part = client.PendingNextPart;
                var first = part * _recordsPerSlice;
                var count = Math.Min(_recordsPerSlice, client.StagedUnitCount - first);
                if (count < 0)
                {
                    count = 0;
                }

                var sliceSize = KeyframeSliceCodec.GetSliceSize(count);
                if (client.PacingTokens < sliceSize)
                {
                    // Out of budget: continue with the next slice next tick.
                    return;
                }

                var header = new KeyframeSliceHeader(
                    client.KeyframeTick,
                    client.KeyframeSeq,
                    (ushort)part,
                    (ushort)client.PendingPartCount,
                    (uint)client.StagedUnitCount,
                    (ushort)count);
                var headerSize = KeyframeSliceCodec.TryEncodeHeader(_sliceBuffer, in header);
                if (headerSize != KeyframeSliceCodec.HeaderSizeBytes)
                {
                    client.KeyframeInFlight = false;
                    _keyframeFallbackCount++;
                    client.NeedsKeyframe = true;
                    return;
                }

                if (count > 0)
                {
                    Array.Copy(
                        client.KeyframeStaging,
                        first * DeltaSnapshotProtocol.AddRecordSizeBytes,
                        _sliceBuffer,
                        headerSize,
                        count * DeltaSnapshotProtocol.AddRecordSizeBytes);
                }

                var envelopeTick = (client.KeyframeTick << 8) | (ulong)part;
                if (!_transport.SendToSession(client.Session, envelopeTick, _sliceBuffer, sliceSize))
                {
                    _sendFailureCount++;
                    return;
                }

                client.PacingTokens -= sliceSize;
                client.PendingNextPart++;
                _emittedSliceCount++;
                if (client.PendingNextPart >= client.PendingPartCount)
                {
                    client.KeyframeInFlight = false;
                }
            }
        }

        // ------------------------------------------------------------------
        // Client feedback (C0)
        // ------------------------------------------------------------------

        private void OnFeedback(ReplicationFeedbackMessage message)
        {
            var client = FindClient(message.Session);
            if (client == null)
            {
                return;
            }

            switch (message.Type)
            {
                case TransportMessageType.SnapshotAck:
                    _ackCount++;
                    HandleAck(client, message.Payload);
                    return;
                case TransportMessageType.ReplicationRequest:
                    _requestCount++;
                    HandleRequest(client, message.Payload);
                    return;
            }
        }

        private void HandleAck(ClientState client, ReadOnlySpan<byte> payload)
        {
            if (SnapshotAckCodec.TryDecode(payload, out var ack) != SnapshotAckCodecResult.Ok)
            {
                _malformedFeedbackCount++;
                return;
            }

            if (ack.LastAppliedTick > client.LastAckedTick)
            {
                client.LastAckedTick = ack.LastAppliedTick;
            }

            if (ack.LastAppliedTick == 0 && ack.BaseKeyframeTick == 0)
            {
                // The client reports no base at all (a tick-0 baseline is
                // reported as unbased too): (re-)send a keyframe. While the
                // send is already running the flag is a no-op.
                QueueKeyframe(client);
                return;
            }

            if (_ring.HasEvicted &&
                client.LastSentTick < _ring.EvictedThroughTick &&
                ack.LastAppliedTick < _ring.EvictedThroughTick)
            {
                // The client's base predates the retained window and no newer
                // baseline is on the wire: only a keyframe can restore it.
                _keyframeFallbackCount++;
                QueueKeyframe(client);
            }
        }

        private void HandleRequest(ClientState client, ReadOnlySpan<byte> payload)
        {
            if (ReplicationRequestCodec.TryDecode(payload, out var request) !=
                ReplicationRequestCodecResult.Ok)
            {
                _malformedFeedbackCount++;
                return;
            }

            if (request.IsSnapshotRequest)
            {
                QueueKeyframe(client);
                return;
            }

            // DeltaResume: rebuild (LastAppliedTick, last completed tick] as
            // one cumulative Establishing Delta.
            EnsureCapture();
            var target = _lastCompletedTick;
            var baseTick = request.LastAppliedTick;
            if (baseTick >= target)
            {
                // Already caught up (a stale retry): nothing to serve.
                return;
            }

            if (!_ring.TryMergeRange(baseTick, target, _builder))
            {
                _keyframeFallbackCount++;
                QueueKeyframe(client);
                return;
            }

            var merged = _builder.ChangeSet;
            if (merged.RecordCount == 0)
            {
                // Empty interval: the client's state already equals the target.
                // Answer on the wire instead of advancing silently: the
                // header-only delta is applied and acked, so the repair episode
                // ends with a packet (a silent LastSentTick advance would
                // strand the client in CatchingUp until it escalates).
                var emptyHeader = DeltaSnapshotHeader.CreateDelta(
                    target,
                    baseTick,
                    DeltaFlags.None,
                    0,
                    0,
                    0,
                    0,
                    client.KeyframeSeq);
                var emptyEncode = DeltaSnapshotWireCodec.TryEncode(
                    emptyHeader,
                    ReadOnlySpan<DeltaAddRecord>.Empty,
                    ReadOnlySpan<DeltaUpdateRecord>.Empty,
                    ReadOnlySpan<DeltaRemoveRecord>.Empty,
                    _deltaBuffer,
                    out var emptyWritten);
                if (emptyEncode != DeltaCodecResult.Ok)
                {
                    _keyframeFallbackCount++;
                    QueueKeyframe(client);
                    return;
                }

                if (!_transport.SendToSession(client.Session, target, _deltaBuffer, emptyWritten))
                {
                    _sendFailureCount++;
                    return;
                }

                if (client.LastSentTick < target)
                {
                    client.LastSentTick = target;
                }

                _deltaResumeServedCount++;
                return;
            }

            var sizeResult = DeltaSnapshotWireCodec.GetEncodedSize(
                merged.Adds, merged.Updates, merged.Removes, out var encodedSize);
            if (sizeResult != DeltaCodecResult.Ok ||
                encodedSize > DeltaSnapshotProtocol.MaxPacketBytes)
            {
                _keyframeFallbackCount++;
                QueueKeyframe(client);
                return;
            }

            var header = DeltaSnapshotHeader.CreateDelta(
                target,
                baseTick,
                DeltaFlags.None,
                0,
                (ushort)merged.AddCount,
                (ushort)merged.UpdateCount,
                (ushort)merged.RemoveCount,
                client.KeyframeSeq);
            var encodeResult = DeltaSnapshotWireCodec.TryEncode(
                header,
                merged.Adds,
                merged.Updates,
                merged.Removes,
                _deltaBuffer,
                out var written);
            if (encodeResult != DeltaCodecResult.Ok)
            {
                _keyframeFallbackCount++;
                QueueKeyframe(client);
                return;
            }

            if (!_transport.SendToSession(client.Session, target, _deltaBuffer, written))
            {
                _sendFailureCount++;
                return;
            }

            if (client.LastSentTick < target)
            {
                client.LastSentTick = target;
            }

            _deltaResumeServedCount++;
        }

        // ------------------------------------------------------------------
        // Client registry
        // ------------------------------------------------------------------

        private void OnSessionAttached(SessionId session, MatchId match, PlayerId player)
        {
            var client = FindClient(session);
            if (client != null)
            {
                return;
            }

            client = FindFreeSlot();
            if (client == null)
            {
                // Registry full: the client still streams legacy snapshots;
                // replication simply never starts for it.
                return;
            }

            client.Session = session;
            client.Active = true;
            client.NeedsKeyframe = false;
            client.KeyframeInFlight = false;
            client.LastAckedTick = 0;
            client.LastSentTick = 0;
            client.NextKeyframeSeq = 0;
            client.KeyframeTick = 0;
            client.KeyframeSeq = 0;
            client.StagedUnitCount = 0;
            client.PendingPartCount = 0;
            client.PendingNextPart = 0;
            client.PacingTokens = _effectiveBurstBytes;
            client.LastKeyframeTick = 0;
            client.LastChecksumTick = 0;
        }

        private void OnSessionDetached(SessionId session, TransportDisconnectReason reason)
        {
            var client = FindClient(session);
            if (client == null)
            {
                return;
            }

            client.Active = false;
            client.NeedsKeyframe = false;
            client.KeyframeInFlight = false;
            client.KeyframeStaging = null;
        }

        private ClientState FindClient(SessionId session)
        {
            for (var index = 0; index < _clients.Length; index++)
            {
                var client = _clients[index];
                if (client != null && client.Active && client.Session == session)
                {
                    return client;
                }
            }

            return null;
        }

        private ClientState FindFreeSlot()
        {
            for (var index = 0; index < _clients.Length; index++)
            {
                var client = _clients[index];
                if (client == null)
                {
                    client = new ClientState();
                    _clients[index] = client;
                    return client;
                }

                if (!client.Active)
                {
                    return client;
                }
            }

            return null;
        }

        private static ulong Max(ulong left, ulong right) => left >= right ? left : right;
    }
}
