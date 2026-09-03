using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Snapshot;

namespace GlobalFront.Client.Replication
{
    /// <summary>Outcome of feeding one replication packet to the receiver.</summary>
    public enum ClientReplicationOutcome : byte
    {
        /// <summary>The records were applied and the confirmed tick moved forward.</summary>
        Applied = 0,

        /// <summary>A complete keyframe installed the base and the world.</summary>
        BaselineInstalled = 1,

        /// <summary><c>Tick &lt;= LastAppliedTick</c>: dropped without any side effect.</summary>
        DroppedStale = 2,

        /// <summary>
        /// The packet depends on a tick the client does not hold, inside the
        /// retained window: a <c>DeltaResume</c> was queued and the gap reported.
        /// </summary>
        CatchUpRequested = 3,

        /// <summary>
        /// The base is unusable (another keyframe generation, a dependency
        /// outside the window, or no base at all): a <c>SnapshotRequest</c> was
        /// queued.
        /// </summary>
        RebaseRequested = 4,

        /// <summary>The receiver is terminal; nothing is processed any more.</summary>
        RejectedTerminal = 5,

        /// <summary>
        /// A record could not be applied, so the client world is no longer a
        /// faithful mirror and must be re-based. The world may hold a partially
        /// applied tick - see <see cref="ClientReplicationReceiver.WorldPartiallyApplied"/>.
        /// </summary>
        WorldInconsistent = 6,

        /// <summary>
        /// The client table cannot hold the authoritative world. This is a
        /// capacity/configuration error, detected before any record is applied.
        /// </summary>
        CapacityExceeded = 7,

        /// <summary>The header or the decoded record sections are not usable.</summary>
        InvalidPayload = 8,

        /// <summary>
        /// One part of a split tick: parts are assembled by the transport
        /// integration (step 2.6.4), and the confirmed tick only moves once every
        /// part of a tick arrived.
        /// </summary>
        IncompleteMultipart = 9
    }

    /// <summary>
    /// Client replication receiver (Phase 2.6, step 2.6.3, ADR-010): the single
    /// entry point for everything the replication stream sends, tying together
    /// the Apply-Guard state machine (<see cref="ReplicationReceiverFSM"/>), the
    /// client world table (<see cref="ClientReplicationWorld"/>) and the
    /// feedback generator (<see cref="ReplicationFeedbackGenerator"/>).
    ///
    /// Contract:
    /// <list type="bullet">
    /// <item><description>
    /// A completely assembled keyframe installs the base (<see cref="ReceiveKeyframe"/>);
    /// until then no delta is applied.
    /// </description></item>
    /// <item><description>
    /// An Establishing Delta that passes the guard moves the client world from
    /// <c>BaseTick</c> to <c>Tick</c> exactly, so intermediate ticks may be lost.
    /// </description></item>
    /// <item><description>
    /// Anything the client cannot apply turns into documented repair traffic
    /// (<c>DeltaResume</c> / <c>SnapshotRequest</c>) plus an immediate feedback
    /// signal, never into a silent desync.
    /// </description></item>
    /// <item><description>
    /// Every applied tick is acknowledged on C0 at the 10 Hz base cadence.
    /// </description></item>
    /// </list>
    ///
    /// Threading and timing: like the rest of the client network layer, the
    /// receiver is driven from the host/main thread only (concurrency invariant
    /// of ADR-009) and takes a monotonic <c>nowMs</c> per call, so it never reads
    /// a wall-clock and tests drive it with plain integers.
    ///
    /// Zero-GC: records arrive as caller-owned spans (the pooled decode buffers
    /// of step 2.6.1), the world table is preallocated, and repair/feedback
    /// traffic uses bounded rings. Applying a continuous delta stream allocates
    /// nothing, which EditMode tests assert with Unity's GC.Alloc recorder.
    ///
    /// Not wired into <c>LocalMatchHost</c> or the presentation layer yet: the
    /// end-to-end coupling with the transport pump and the keyframe slice
    /// assembly is step 2.6.4.
    /// </summary>
    public sealed class ClientReplicationReceiver
    {
        private readonly ClientReplicationWorld _world;
        private readonly ReplicationReceiverFSM _fsm;
        private readonly ReplicationFeedbackGenerator _feedback;

        public ClientReplicationReceiver()
            : this(ClientReplicationWorld.DefaultCapacity)
        {
        }

        public ClientReplicationReceiver(int worldCapacity)
            : this(worldCapacity, ReplicationReceiverConfig.Default, ReplicationFeedbackGenerator.DefaultMinIntervalMs)
        {
        }

        public ClientReplicationReceiver(
            int worldCapacity,
            ReplicationReceiverConfig config,
            long ackMinIntervalMs)
        {
            _world = new ClientReplicationWorld(worldCapacity);
            _fsm = new ReplicationReceiverFSM(config);
            _feedback = new ReplicationFeedbackGenerator(ackMinIntervalMs);
        }

        /// <summary>The replicated world mirror; read-only for everyone else.</summary>
        public ClientReplicationWorld World => _world;

        /// <summary>Apply-Guard state and repair schedule.</summary>
        public ReplicationReceiverFSM Fsm => _fsm;

        /// <summary>Feedback pacing and gap reporting.</summary>
        public ReplicationFeedbackGenerator Feedback => _feedback;

        public ReplicationReceiverState State => _fsm.State;

        /// <summary>Last replication tick whose records were applied completely.</summary>
        public ulong LastAppliedTick => _fsm.LastAppliedTick;

        /// <summary>Tick of the installed keyframe base; 0 while there is none.</summary>
        public ulong BaseKeyframeTick => _fsm.BaseKeyframeTick;

        /// <summary>Keyframe generation the current base belongs to.</summary>
        public ushort CurrentKeyframeSeq => _fsm.CurrentKeyframeSeq;

        public ReplicationFailureReason FailureReason => _fsm.FailureReason;

        public int PendingRequestCount => _fsm.PendingRequestCount;

        public int TotalRequestCount => _fsm.TotalRequestCount;

        /// <summary>
        /// True when the mirror can be consumed by the presentation layer: a
        /// baseline is installed and no tick was left half-applied. While false,
        /// the world content must be ignored, never rendered.
        /// </summary>
        public bool IsWorldUsable => _fsm.HasBaseline && !WorldPartiallyApplied;

        /// <summary>
        /// True when a record application failed midway and the table holds a
        /// partial tick. Cleared by the next complete keyframe.
        /// </summary>
        public bool WorldPartiallyApplied { get; private set; }

        /// <summary>Keyframes installed.</summary>
        public int KeyframeCount { get; private set; }

        /// <summary>Unit records installed by keyframes (ADD records of a baseline).</summary>
        public int KeyframeUnitCount { get; private set; }

        /// <summary>Deltas whose records were applied.</summary>
        public int AppliedDeltaCount { get; private set; }

        /// <summary>ADD records applied from deltas (keyframe units are counted separately).</summary>
        public int AppliedAddCount { get; private set; }

        public int AppliedUpdateCount { get; private set; }

        public int AppliedRemoveCount { get; private set; }

        public int StaleDropCount { get; private set; }

        public int CatchUpCount { get; private set; }

        public int RebaseCount { get; private set; }

        public int KeyframeMismatchCount { get; private set; }

        public int RejectedCount { get; private set; }

        /// <summary>Applications that left the mirror untrustworthy.</summary>
        public int InconsistentApplyCount { get; private set; }

        public int CapacityRejectCount { get; private set; }

        public int InvalidPayloadCount { get; private set; }

        public int IncompleteMultipartCount { get; private set; }

        /// <summary>Convenience read of the mirror.</summary>
        public bool TryGetUnit(EntityId entity, out ClientUnitState state) => _world.TryGet(entity, out state);

        /// <summary>
        /// Installs a completely assembled keyframe: the world table is cleared
        /// and rebuilt from the keyframe's unit records, which use the same
        /// 39-byte layout as a delta ADD, and the receiver returns to
        /// <see cref="ReplicationReceiverState.Streaming"/>.
        ///
        /// Slice assembly and NACK repair of an incomplete keyframe belong to the
        /// transport integration (step 2.6.4); this call requires the full unit
        /// set of one keyframe generation.
        /// </summary>
        public ClientReplicationOutcome ReceiveKeyframe(
            long nowMs,
            ushort keyframeSeq,
            ulong tick,
            ReadOnlySpan<DeltaAddRecord> units)
        {
            if (State == ReplicationReceiverState.ConnectionFailed)
            {
                // Terminal: recovery is the Phase 2.7 contour, so the mirror is
                // not torn down for a keyframe that will never be used.
                RejectedCount++;
                return ClientReplicationOutcome.RejectedTerminal;
            }

            _world.Reset();
            WorldPartiallyApplied = false;

            if (!_world.HasCapacityFor(units.Length))
            {
                CapacityRejectCount++;
                return Fail(nowMs, ClientReplicationOutcome.CapacityExceeded, partiallyApplied: false);
            }

            for (var index = 0; index < units.Length; index++)
            {
                var result = _world.ApplyAdd(in units[index]);
                if (result != ClientWorldApplyResult.Ok)
                {
                    return Fail(nowMs, OutcomeOf(result), partiallyApplied: true);
                }

                KeyframeUnitCount++;
            }

            _fsm.NoteBaseline(keyframeSeq, tick);
            _feedback.NoteApplied(tick, tick);
            KeyframeCount++;
            return ClientReplicationOutcome.BaselineInstalled;
        }

        /// <summary>
        /// Feeds one decoded delta packet. The record sinks are the caller's
        /// pooled decode buffers; the header counts say how much of them is live.
        ///
        /// Order of operations is the wire section order ADD -&gt; UPDATE -&gt;
        /// REMOVE, which is what makes an Establishing Delta self-contained: a
        /// unit spawned and killed inside the covered interval cancels out on the
        /// server and never reaches this table.
        /// </summary>
        public ClientReplicationOutcome ReceiveDelta(
            long nowMs,
            in DeltaSnapshotHeader header,
            ushort keyframeRef,
            ReadOnlySpan<DeltaAddRecord> addSink,
            ReadOnlySpan<DeltaUpdateRecord> updateSink,
            ReadOnlySpan<DeltaRemoveRecord> removeSink)
        {
            if (header.MessageType != DeltaSnapshotProtocol.MessageTypeDelta ||
                header.DeltaProtocolVersion != DeltaSnapshotProtocol.Version)
            {
                InvalidPayloadCount++;
                return ClientReplicationOutcome.InvalidPayload;
            }

            if (addSink.Length < header.AddCount ||
                updateSink.Length < header.UpdateCount ||
                removeSink.Length < header.RemoveCount)
            {
                InvalidPayloadCount++;
                return ClientReplicationOutcome.InvalidPayload;
            }

            var decision = _fsm.OnDelta(nowMs, in header, keyframeRef, out var reason);
            switch (decision)
            {
                case ReplicationApplyDecision.Apply:
                    // The guard passed: fall through to the record application.
                    break;

                case ReplicationApplyDecision.Stale:
                    StaleDropCount++;
                    return ClientReplicationOutcome.DroppedStale;

                case ReplicationApplyDecision.Rejected:
                    RejectedCount++;
                    return ClientReplicationOutcome.RejectedTerminal;

                case ReplicationApplyDecision.CatchUpRequired:
                    CatchUpCount++;
                    _feedback.NoteDependencyGap(
                        _fsm.LastAppliedTick, _fsm.BaseKeyframeTick, header.BaseTick, baseStale: false);
                    return ClientReplicationOutcome.CatchUpRequested;

                case ReplicationApplyDecision.RebaseRequired:
                    RebaseCount++;
                    if (reason == ReplicationGuardReason.KeyframeMismatch)
                    {
                        KeyframeMismatchCount++;
                    }

                    if (reason == ReplicationGuardReason.BaseStale)
                    {
                        _feedback.NoteDependencyGap(
                            _fsm.LastAppliedTick, _fsm.BaseKeyframeTick, header.BaseTick, baseStale: true);
                    }
                    else
                    {
                        _feedback.NoteBaselineStale(_fsm.LastAppliedTick, _fsm.BaseKeyframeTick);
                    }

                    return ClientReplicationOutcome.RebaseRequested;

                default:
                    InvalidPayloadCount++;
                    return ClientReplicationOutcome.InvalidPayload;
            }

            if (header.PartCount > 1)
            {
                // Assembly of a split tick is the transport integration's job
                // (step 2.6.4). Until every part arrived, the tick does not exist
                // for the mirror - but the incomplete assembly is reported right
                // away, as R&D 4.3 requires.
                IncompleteMultipartCount++;
                _feedback.NoteDependencyGap(
                    _fsm.LastAppliedTick, _fsm.BaseKeyframeTick, header.Tick, baseStale: false);
                return ClientReplicationOutcome.IncompleteMultipart;
            }

            if (!_world.HasCapacityFor(header.AddCount))
            {
                CapacityRejectCount++;
                return Fail(nowMs, ClientReplicationOutcome.CapacityExceeded, partiallyApplied: false);
            }

            var adds = addSink.Slice(0, header.AddCount);
            for (var index = 0; index < adds.Length; index++)
            {
                var result = _world.ApplyAdd(in adds[index]);
                if (result != ClientWorldApplyResult.Ok)
                {
                    return Fail(nowMs, OutcomeOf(result), partiallyApplied: true);
                }

                AppliedAddCount++;
            }

            var updates = updateSink.Slice(0, header.UpdateCount);
            for (var index = 0; index < updates.Length; index++)
            {
                var result = _world.ApplyUpdate(in updates[index]);
                if (result != ClientWorldApplyResult.Ok)
                {
                    // An UPDATE for an entity the client does not hold breaks the
                    // establishing contract: only a keyframe can restore trust.
                    return Fail(nowMs, OutcomeOf(result), partiallyApplied: true);
                }

                AppliedUpdateCount++;
            }

            var removes = removeSink.Slice(0, header.RemoveCount);
            for (var index = 0; index < removes.Length; index++)
            {
                var result = _world.ApplyRemove(in removes[index]);
                if (result == ClientWorldApplyResult.Ok)
                {
                    AppliedRemoveCount++;
                }
                else if (result != ClientWorldApplyResult.UnknownEntity)
                {
                    // An echoed tombstone is a documented no-op; anything else is not.
                    return Fail(nowMs, OutcomeOf(result), partiallyApplied: true);
                }
            }

            WorldPartiallyApplied = false;
            _fsm.NoteApplied(header.Tick);
            _feedback.NoteApplied(header.Tick, _fsm.BaseKeyframeTick);
            AppliedDeltaCount++;
            return ClientReplicationOutcome.Applied;
        }

        /// <summary>
        /// Drives the repair schedule (baseline backoff, catch-up retries,
        /// terminal escalation). Call it once per pump.
        /// </summary>
        public void Update(long nowMs) => _fsm.Update(nowMs);

        /// <summary>Drains one queued repair request for the transport to send.</summary>
        public bool TryTakeRequest(out ReplicationRequest request) => _fsm.TryTakeRequest(out request);

        /// <summary>Drains the pending acknowledgement when the cadence allows it.</summary>
        public bool TryTakeAck(long nowMs, out SnapshotAck ack) => _feedback.TryTakeAck(nowMs, out ack);

        /// <summary>
        /// Returns the receiver to its initial unbased state, including the world
        /// mirror, the feedback signals and the diagnostics: a new attachment (or
        /// a Phase 2.7 resync) starts from scratch. The next
        /// <see cref="Update"/> opens a new baseline episode.
        /// </summary>
        public void Reset()
        {
            _world.Reset();
            _fsm.Reset();
            _feedback.Reset();
            WorldPartiallyApplied = false;

            KeyframeCount = 0;
            KeyframeUnitCount = 0;
            AppliedDeltaCount = 0;
            AppliedAddCount = 0;
            AppliedUpdateCount = 0;
            AppliedRemoveCount = 0;
            StaleDropCount = 0;
            CatchUpCount = 0;
            RebaseCount = 0;
            KeyframeMismatchCount = 0;
            RejectedCount = 0;
            InconsistentApplyCount = 0;
            CapacityRejectCount = 0;
            InvalidPayloadCount = 0;
            IncompleteMultipartCount = 0;
        }

        public override string ToString() =>
            $"ClientReplicationReceiver(state={State}, last={LastAppliedTick}, base={BaseKeyframeTick}, " +
            $"keyframeSeq={CurrentKeyframeSeq}, usable={IsWorldUsable}, world={_world.LiveCount}/{_world.Capacity}, " +
            $"applied={AppliedDeltaCount}, keyframes={KeyframeCount})";

        /// <summary>
        /// Reports an application that could not be completed: the mirror is no
        /// longer trustworthy, so the base is declared lost, a fresh keyframe is
        /// requested and the server is told immediately that the base is stale.
        /// </summary>
        private ClientReplicationOutcome Fail(
            long nowMs,
            ClientReplicationOutcome outcome,
            bool partiallyApplied)
        {
            WorldPartiallyApplied = partiallyApplied;
            InconsistentApplyCount++;
            _fsm.ReportBaselineLoss(nowMs);
            _feedback.NoteBaselineStale(_fsm.LastAppliedTick, _fsm.BaseKeyframeTick);
            return outcome;
        }

        private static ClientReplicationOutcome OutcomeOf(ClientWorldApplyResult result) =>
            result == ClientWorldApplyResult.CapacityExceeded
                ? ClientReplicationOutcome.CapacityExceeded
                : ClientReplicationOutcome.WorldInconsistent;
    }
}
