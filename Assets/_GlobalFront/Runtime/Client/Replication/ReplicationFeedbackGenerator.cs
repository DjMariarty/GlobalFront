using System;
using GlobalFront.Core.Snapshot;

namespace GlobalFront.Client.Replication
{
    /// <summary>
    /// Builds and paces the client feedback of the replication stream
    /// (Phase 2.6, step 2.6.3, ADR-010 / R&amp;D §4.2-§4.3).
    ///
    /// Delivery on C2 is unreliable, so the client is the only source of truth
    /// for the base the server should encode against: every applied replication
    /// tick produces one <see cref="SnapshotAck"/> carrying the confirmed
    /// <c>LastAppliedTick</c>, at the 10 Hz base cadence (OD-12). Signals that
    /// arrive between two emissions are coalesced into the newest state instead
    /// of being queued, so the uplink stays at 34 B x 10 Hz = 340 B/s and no
    /// progress is ever lost - the pending acknowledgement always reports the
    /// latest confirmed tick.
    ///
    /// A tick that could not be applied is reported immediately (subject to the
    /// same 100 ms floor) with the advisory gap window:
    /// <c>MissingBase</c> plus a 64-bit <c>MissingBitmap</c>. The bitmap never
    /// makes the client wait for lost intermediate ticks - it only tells the
    /// server which change-sets a cumulative delta has to cover.
    ///
    /// Zero-GC: the generator holds plain integers and produces structs; no call
    /// allocates.
    /// </summary>
    public sealed class ReplicationFeedbackGenerator
    {
        /// <summary>Base replication cadence (OD-12): one ack per applied tick.</summary>
        public const int DefaultCadenceHz = 10;

        /// <summary>
        /// Emission floor implied by <see cref="DefaultCadenceHz"/>, and the
        /// minimum spacing between two immediate "could not apply" signals.
        /// </summary>
        public const long DefaultMinIntervalMs = 1000 / DefaultCadenceHz;

        private readonly long _minIntervalMs;

        private ulong _lastAppliedTick;
        private ulong _baseKeyframeTick;
        private ulong _missingBase;
        private ulong _missingBitmap;
        private AckHealthFlags _health;
        private bool _pending;
        private bool _hasSent;
        private long _lastSentMs;

        public ReplicationFeedbackGenerator()
            : this(DefaultMinIntervalMs)
        {
        }

        public ReplicationFeedbackGenerator(long minIntervalMs)
        {
            if (minIntervalMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minIntervalMs));
            }

            _minIntervalMs = minIntervalMs;
        }

        /// <summary>Minimum spacing between two emitted acknowledgements.</summary>
        public long MinIntervalMs => _minIntervalMs;

        /// <summary>True when a signal is waiting for its emission slot.</summary>
        public bool HasPendingAck => _pending;

        /// <summary>The acknowledgement the next emission would carry.</summary>
        public SnapshotAck PendingAck => Build();

        public ulong LastAppliedTick => _lastAppliedTick;

        public ulong BaseKeyframeTick => _baseKeyframeTick;

        public ulong MissingBase => _missingBase;

        public ulong MissingBitmap => _missingBitmap;

        public AckHealthFlags HealthFlags => _health;

        public bool HasSentAck => _hasSent;

        public long LastSentMs => _lastSentMs;

        /// <summary>Acknowledgements handed to the transport.</summary>
        public int SentAckCount { get; private set; }

        /// <summary>Signals that were folded into an already pending acknowledgement.</summary>
        public int CoalescedSignalCount { get; private set; }

        /// <summary>Dependency-gap signals (a delta could not be applied).</summary>
        public int GapSignalCount { get; private set; }

        /// <summary>Base-stale signals (keyframe mismatch or an unusable base).</summary>
        public int BaselineStaleSignalCount { get; private set; }

        /// <summary>Signals that carried no new information and were dropped.</summary>
        public int IgnoredSignalCount { get; private set; }

        /// <summary>
        /// Progress signal: the records of <paramref name="lastAppliedTick"/> were
        /// applied to the client world. Clears any previously reported gap,
        /// because the confirmed tick is now the base of the next delta.
        ///
        /// A tick that does not move the confirmed tick forward is ignored; in
        /// particular a baseline installed at tick 0 cannot be acked as progress,
        /// since <c>BaseKeyframeTick = 0</c> already means "no base" on the wire.
        /// </summary>
        public void NoteApplied(ulong lastAppliedTick, ulong baseKeyframeTick)
        {
            if (lastAppliedTick <= _lastAppliedTick)
            {
                IgnoredSignalCount++;
                return;
            }

            CountCoalescing();
            _lastAppliedTick = lastAppliedTick;
            _baseKeyframeTick = baseKeyframeTick;
            _missingBase = 0UL;
            _missingBitmap = 0UL;
            _health = AckHealthFlags.None;
            _pending = true;
        }

        /// <summary>
        /// Immediate signal for a delta whose <c>BaseTick</c> the client does not
        /// hold. The missing ticks are <c>(lastAppliedTick, missingThroughTick]</c>:
        /// bit <c>i</c> of the bitmap stands for <c>MissingBase + i</c>. A gap of
        /// 64 ticks or more fills the bitmap and sets
        /// <see cref="AckHealthFlags.BitmapOverflow"/>, which honestly says "the
        /// gap is longer than I can express".
        /// </summary>
        public void NoteDependencyGap(
            ulong lastAppliedTick,
            ulong baseKeyframeTick,
            ulong missingThroughTick,
            bool baseStale)
        {
            if (missingThroughTick <= lastAppliedTick)
            {
                IgnoredSignalCount++;
                return;
            }

            CountCoalescing();
            if (lastAppliedTick > _lastAppliedTick)
            {
                _lastAppliedTick = lastAppliedTick;
            }

            _baseKeyframeTick = baseKeyframeTick;
            _missingBase = lastAppliedTick + 1UL;

            var missingCount = missingThroughTick - lastAppliedTick;
            var overflow = missingCount >= SnapshotAckCodec.MissingBitmapBits;
            _missingBitmap = overflow
                ? ulong.MaxValue
                : (1UL << (int)missingCount) - 1UL;

            _health = AckHealthFlags.GapPresent;
            if (overflow)
            {
                _health |= AckHealthFlags.BitmapOverflow;
            }

            if (baseStale)
            {
                _health |= AckHealthFlags.BaseStale;
            }

            _pending = true;
            GapSignalCount++;
        }

        /// <summary>
        /// Immediate signal for a base the client cannot use any more: another
        /// keyframe generation, a dependency outside the retained window, or a
        /// world that refused a record. No gap window is reported - only a fresh
        /// keyframe helps, and the stale base tick tells the server which
        /// generation the client is stuck on.
        /// </summary>
        public void NoteBaselineStale(ulong lastAppliedTick, ulong staleBaseKeyframeTick)
        {
            CountCoalescing();
            if (lastAppliedTick > _lastAppliedTick)
            {
                _lastAppliedTick = lastAppliedTick;
            }

            _baseKeyframeTick = staleBaseKeyframeTick;
            _missingBase = 0UL;
            _missingBitmap = 0UL;
            _health = AckHealthFlags.BaseStale;
            _pending = true;
            BaselineStaleSignalCount++;
        }

        /// <summary>
        /// Hands the pending acknowledgement to the caller when the cadence floor
        /// allows it. The first acknowledgement of an attachment is never delayed;
        /// afterwards two emissions are at least <see cref="MinIntervalMs"/> apart.
        /// A deferred acknowledgement stays pending and carries the newest state.
        /// </summary>
        public bool TryTakeAck(long nowMs, out SnapshotAck ack)
        {
            ack = default;

            if (!_pending)
            {
                return false;
            }

            if (_hasSent && nowMs - _lastSentMs < _minIntervalMs)
            {
                return false;
            }

            ack = Build();
            _pending = false;
            _hasSent = true;
            _lastSentMs = nowMs;
            SentAckCount++;
            return true;
        }

        /// <summary>Drops every signal and diagnostic, e.g. between matches.</summary>
        public void Reset()
        {
            _lastAppliedTick = 0UL;
            _baseKeyframeTick = 0UL;
            _missingBase = 0UL;
            _missingBitmap = 0UL;
            _health = AckHealthFlags.None;
            _pending = false;
            _hasSent = false;
            _lastSentMs = 0L;
            SentAckCount = 0;
            CoalescedSignalCount = 0;
            GapSignalCount = 0;
            BaselineStaleSignalCount = 0;
            IgnoredSignalCount = 0;
        }

        public override string ToString() =>
            $"FeedbackGenerator(last={_lastAppliedTick}, base={_baseKeyframeTick}, " +
            $"missingBase={_missingBase}, bitmap=0x{_missingBitmap:X16}, flags={_health}, " +
            $"pending={_pending}, sent={SentAckCount})";

        private SnapshotAck Build() =>
            new SnapshotAck((byte)_health, _lastAppliedTick, _baseKeyframeTick, _missingBase, _missingBitmap);

        private void CountCoalescing()
        {
            if (_pending)
            {
                CoalescedSignalCount++;
            }
        }
    }
}
