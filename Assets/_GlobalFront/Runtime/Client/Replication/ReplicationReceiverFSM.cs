using System;
using GlobalFront.Core.Snapshot;

namespace GlobalFront.Client.Replication
{
    /// <summary>
    /// States of the client replication receiver (Phase 2.6, ADR-010 /
    /// R&amp;D §5.1). <see cref="ConnectionFailed"/> is terminal for this phase:
    /// reconnect and full resync are the Phase 2.7 contour.
    /// </summary>
    public enum ReplicationReceiverState : byte
    {
        /// <summary>No keyframe base: the client cannot apply any delta.</summary>
        Unbased = 0,

        /// <summary>Base installed; deltas that pass the Apply-Guard are applied.</summary>
        Streaming = 1,

        /// <summary>
        /// An incoming delta depends on a <c>BaseTick</c> the client does not
        /// hold, but the dependency is inside the retained history window: a
        /// cumulative delta was requested and is expected to restore
        /// <see cref="Streaming"/>.
        /// </summary>
        CatchingUp = 2,

        /// <summary>
        /// The base is unusable (another keyframe generation, a dependency
        /// outside the window, or a world that could not be applied): a fresh
        /// keyframe was requested.
        /// </summary>
        Rebasing = 3,

        /// <summary>Baseline acquisition exhausted its attempt budget.</summary>
        ConnectionFailed = 4
    }

    /// <summary>Verdict of the Apply-Guard for one incoming delta packet.</summary>
    public enum ReplicationApplyDecision : byte
    {
        /// <summary>The packet passes the guard and must be applied.</summary>
        Apply = 0,

        /// <summary><c>Tick &lt;= LastAppliedTick</c>: a duplicate or a late reorder.</summary>
        Stale = 1,

        /// <summary>
        /// <c>BaseTick &gt; LastAppliedTick</c> inside the window: ask for a
        /// cumulative delta and stay ready to apply one.
        /// </summary>
        CatchUpRequired = 2,

        /// <summary>The base cannot be used: ask for a fresh keyframe.</summary>
        RebaseRequired = 3,

        /// <summary>The receiver is terminal; nothing is processed any more.</summary>
        Rejected = 4
    }

    /// <summary>Why the Apply-Guard reached its verdict.</summary>
    public enum ReplicationGuardReason : byte
    {
        None = 0,

        /// <summary>
        /// <c>Tick &gt; LastAppliedTick &amp;&amp; BaseTick &lt;= LastAppliedTick
        /// &amp;&amp; KeyframeRef == CurrentKeyframeSeq</c> (OD-14).
        /// </summary>
        Established = 1,

        StaleTick = 2,
        MissingBaseline = 3,
        KeyframeMismatch = 4,
        DependencyGap = 5,
        BaseStale = 6,
        Terminal = 7
    }

    /// <summary>Repair traffic the receiver asks the server for.</summary>
    public enum ReplicationRequestKind : byte
    {
        None = 0,

        /// <summary>
        /// C0 request for a fresh keyframe (<c>KeyframeTick = 0</c>): no base,
        /// an incompatible base, or a base older than the retained window.
        /// </summary>
        SnapshotRequest = 1,

        /// <summary>
        /// C0 request for the cumulative delta of
        /// <c>(LastAppliedTick, now]</c>: the dependency is inside the window.
        /// </summary>
        DeltaResume = 2
    }

    /// <summary>Why the receiver reached <see cref="ReplicationReceiverState.ConnectionFailed"/>.</summary>
    public enum ReplicationFailureReason : byte
    {
        None = 0,

        /// <summary>
        /// The baseline attempt budget was exhausted. Recovery (reconnect plus a
        /// full resync) is the Phase 2.7 contour; locally the receiver only
        /// offers <see cref="ReplicationReceiverFSM.Reset"/>.
        /// </summary>
        ResyncRequired = 1
    }

    /// <summary>
    /// One queued repair request. Pure value; the queue behind it is
    /// preallocated, so asking for a keyframe or a cumulative delta never
    /// allocates.
    /// </summary>
    public readonly struct ReplicationRequest : IEquatable<ReplicationRequest>
    {
        public ReplicationRequest(
            ReplicationRequestKind kind,
            int attempt,
            long requestedAtMs,
            ulong lastAppliedTick,
            ulong baseKeyframeTick,
            ulong keyframeTick)
        {
            Kind = kind;
            Attempt = attempt;
            RequestedAtMs = requestedAtMs;
            LastAppliedTick = lastAppliedTick;
            BaseKeyframeTick = baseKeyframeTick;
            KeyframeTick = keyframeTick;
        }

        public ReplicationRequestKind Kind { get; }

        /// <summary>1-based number of this attempt inside its episode.</summary>
        public int Attempt { get; }

        /// <summary>Monotonic millisecond timestamp taken when the request was queued.</summary>
        public long RequestedAtMs { get; }

        /// <summary>Tick the client confirms; the base of a cumulative delta.</summary>
        public ulong LastAppliedTick { get; }

        /// <summary>Tick of the client's keyframe base (0 = no base).</summary>
        public ulong BaseKeyframeTick { get; }

        /// <summary>
        /// Requested keyframe tick. Zero asks for a fresh keyframe instead of a
        /// specific one, which is the only mode Phase 2.6 uses.
        /// </summary>
        public ulong KeyframeTick { get; }

        public bool Equals(ReplicationRequest other) =>
            Kind == other.Kind &&
            Attempt == other.Attempt &&
            RequestedAtMs == other.RequestedAtMs &&
            LastAppliedTick == other.LastAppliedTick &&
            BaseKeyframeTick == other.BaseKeyframeTick &&
            KeyframeTick == other.KeyframeTick;

        public override bool Equals(object obj) => obj is ReplicationRequest other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Kind);
            hash.Add(Attempt);
            hash.Add(RequestedAtMs);
            hash.Add(LastAppliedTick);
            hash.Add(BaseKeyframeTick);
            hash.Add(KeyframeTick);
            return hash.ToHashCode();
        }

        public override string ToString() =>
            $"{Kind}(attempt={Attempt}, at={RequestedAtMs} ms, last={LastAppliedTick}, " +
            $"base={BaseKeyframeTick}, keyframe={KeyframeTick})";

        public static bool operator ==(ReplicationRequest left, ReplicationRequest right) => left.Equals(right);

        public static bool operator !=(ReplicationRequest left, ReplicationRequest right) => !left.Equals(right);
    }

    /// <summary>
    /// Timing and window constants of the receiver. Every value is
    /// protocol-significant (ADR-010): changing one requires a compatibility
    /// review, not a code edit.
    /// </summary>
    public readonly struct ReplicationReceiverConfig : IEquatable<ReplicationReceiverConfig>
    {
        /// <summary>
        /// <c>RetainedDeltaHistoryWindow</c> (OD-11): 120 ticks, 6 s at 20 Hz.
        /// A dependency gap up to this size can be closed by a cumulative delta;
        /// beyond it only a fresh keyframe helps. Must stay equal to
        /// <c>ReplicationHistoryRing.Capacity</c> on the server - an EditMode
        /// test guards the pair against drift.
        /// </summary>
        public const int DefaultWindowTicks = 120;

        /// <summary>
        /// Exactly three baseline (or resume) attempts per episode, with the
        /// documented exponential backoff 0.5 s -&gt; 1.0 s -&gt; 2.0 s. The third
        /// backoff is also the grace period after the third attempt: when it
        /// expires the receiver escalates instead of waiting forever.
        /// </summary>
        public const int AttemptLimit = 3;

        public const long DefaultFirstBackoffMs = 500;

        public const long DefaultSecondBackoffMs = 1000;

        public const long DefaultThirdBackoffMs = 2000;

        /// <summary>
        /// Bound of the preallocated request ring. Three attempts per episode is
        /// the only producer, so the ring never overflows in practice; the bound
        /// exists so a pump that stops draining cannot grow memory.
        /// </summary>
        public const int DefaultRequestQueueCapacity = 8;

        public static readonly ReplicationReceiverConfig Default = new ReplicationReceiverConfig(
            DefaultWindowTicks,
            DefaultFirstBackoffMs,
            DefaultSecondBackoffMs,
            DefaultThirdBackoffMs,
            DefaultRequestQueueCapacity);

        public ReplicationReceiverConfig(
            int windowTicks,
            long firstBackoffMs,
            long secondBackoffMs,
            long thirdBackoffMs,
            int requestQueueCapacity)
        {
            if (windowTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(windowTicks));
            }

            if (firstBackoffMs <= 0 || secondBackoffMs <= 0 || thirdBackoffMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(firstBackoffMs));
            }

            if (requestQueueCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(requestQueueCapacity));
            }

            WindowTicks = windowTicks;
            FirstBackoffMs = firstBackoffMs;
            SecondBackoffMs = secondBackoffMs;
            ThirdBackoffMs = thirdBackoffMs;
            RequestQueueCapacity = requestQueueCapacity;
        }

        public int WindowTicks { get; }

        public long FirstBackoffMs { get; }

        public long SecondBackoffMs { get; }

        public long ThirdBackoffMs { get; }

        public int RequestQueueCapacity { get; }

        /// <summary>False for a default-constructed (all-zero) value.</summary>
        public bool IsValid =>
            WindowTicks > 0 &&
            FirstBackoffMs > 0 &&
            SecondBackoffMs > 0 &&
            ThirdBackoffMs > 0 &&
            RequestQueueCapacity > 0;

        /// <summary>
        /// Delay after attempt <paramref name="attempt"/> (1-based). The delay
        /// after the third attempt is the terminal grace period.
        /// </summary>
        public long BackoffMs(int attempt)
        {
            if (attempt <= 1)
            {
                return FirstBackoffMs;
            }

            return attempt == 2 ? SecondBackoffMs : ThirdBackoffMs;
        }

        public bool Equals(ReplicationReceiverConfig other) =>
            WindowTicks == other.WindowTicks &&
            FirstBackoffMs == other.FirstBackoffMs &&
            SecondBackoffMs == other.SecondBackoffMs &&
            ThirdBackoffMs == other.ThirdBackoffMs &&
            RequestQueueCapacity == other.RequestQueueCapacity;

        public override bool Equals(object obj) => obj is ReplicationReceiverConfig other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            WindowTicks, FirstBackoffMs, SecondBackoffMs, ThirdBackoffMs, RequestQueueCapacity);

        public override string ToString() =>
            $"ReceiverConfig(window={WindowTicks}, attempts={AttemptLimit}, " +
            $"backoff={FirstBackoffMs}/{SecondBackoffMs}/{ThirdBackoffMs} ms, queue={RequestQueueCapacity})";

        public static bool operator ==(ReplicationReceiverConfig left, ReplicationReceiverConfig right) =>
            left.Equals(right);

        public static bool operator !=(ReplicationReceiverConfig left, ReplicationReceiverConfig right) =>
            !left.Equals(right);
    }

    /// <summary>
    /// Apply-Guard and repair state machine of the client replication receiver
    /// (Phase 2.6, step 2.6.3, ADR-010 / OD-14).
    ///
    /// Guard: a delta is applied when
    /// <c>Tick &gt; LastAppliedTick &amp;&amp; BaseTick &lt;= LastAppliedTick
    /// &amp;&amp; KeyframeRef == CurrentKeyframeSeq</c>. Packets at or behind the
    /// confirmed tick are dropped as stale, and skipping intermediate ticks is
    /// safe: an Establishing Delta carries the whole interval
    /// <c>(BaseTick, Tick]</c> with absolute per-field values, so no "+1 chain"
    /// and no local tick ring are needed.
    ///
    /// Repair: <c>BaseTick &gt; LastAppliedTick</c> inside
    /// <see cref="ReplicationReceiverConfig.WindowTicks"/> moves to
    /// <see cref="ReplicationReceiverState.CatchingUp"/> and asks for
    /// <c>DeltaResume(LastAppliedTick)</c>; a larger gap or a
    /// <c>KeyframeRef</c> mismatch moves to
    /// <see cref="ReplicationReceiverState.Rebasing"/> and asks for a fresh
    /// keyframe. Every episode sends exactly
    /// <see cref="ReplicationReceiverConfig.AttemptLimit"/> requests with the
    /// 0.5 s / 1.0 s / 2.0 s backoff; exhausting a baseline episode is terminal
    /// (<see cref="ReplicationFailureReason.ResyncRequired"/>), exhausting a
    /// catch-up episode escalates to a re-baseline so the receiver can never
    /// stall silently.
    ///
    /// Timing is injected as a monotonic <c>nowMs</c> argument, exactly like
    /// <c>ClientTransportEndpoint.Pump(long)</c>: the FSM never reads a
    /// wall-clock and never uses simulation ticks, so tests drive it with plain
    /// integers. Zero-GC: all queues and counters are preallocated.
    ///
    /// <c>KeyframeRef</c> is an explicit argument so the receiver stays
    /// decoupled from the wire: the transport bridge supplies the reference
    /// carried by the delta header's own <c>KeyframeRef</c> field (header
    /// bytes 34..35 since the 2.6.4 wire revision, audit P1-1), and direct
    /// callers may pass any generation they want the guard to evaluate.
    /// </summary>
    public sealed class ReplicationReceiverFSM
    {
        private readonly ReplicationReceiverConfig _config;
        private readonly ReplicationRequest[] _requests;

        private int _requestHead;
        private int _requestCount;

        private ReplicationRequestKind _episodeKind;
        private bool _episodePendingStart = true;
        private int _attemptsMade;
        private long _episodeStartMs;
        private long _nextAttemptDueMs;

        public ReplicationReceiverFSM()
            : this(ReplicationReceiverConfig.Default)
        {
        }

        public ReplicationReceiverFSM(ReplicationReceiverConfig config)
        {
            if (!config.IsValid)
            {
                throw new ArgumentException(
                    "A default-constructed ReplicationReceiverConfig is invalid; use ReplicationReceiverConfig.Default.",
                    nameof(config));
            }

            _config = config;
            _requests = new ReplicationRequest[config.RequestQueueCapacity];
        }

        public ReplicationReceiverConfig Config => _config;

        public ReplicationReceiverState State { get; private set; } = ReplicationReceiverState.Unbased;

        /// <summary>Last replication tick whose records were applied completely.</summary>
        public ulong LastAppliedTick { get; private set; }

        /// <summary>Tick of the installed keyframe base; 0 while there is none.</summary>
        public ulong BaseKeyframeTick { get; private set; }

        /// <summary>Sequence of the installed keyframe generation.</summary>
        public ushort CurrentKeyframeSeq { get; private set; }

        /// <summary>False while unbased and while a re-baseline is running.</summary>
        public bool HasBaseline { get; private set; }

        public ReplicationFailureReason FailureReason { get; private set; }

        /// <summary>Attempts made inside the current repair episode (0..3).</summary>
        public int AttemptsMade => _attemptsMade;

        public long EpisodeStartMs => _episodeStartMs;

        /// <summary>
        /// When the next attempt is due, or - after the third attempt - when the
        /// episode escalates.
        /// </summary>
        public long NextAttemptDueMs => _nextAttemptDueMs;

        public int PendingRequestCount => _requestCount;

        /// <summary>Deltas that passed the guard (progress recorded).</summary>
        public int AppliedCount { get; private set; }

        public int StaleDropCount { get; private set; }

        public int KeyframeMismatchCount { get; private set; }

        public int DependencyGapCount { get; private set; }

        public int BaseStaleCount { get; private set; }

        public int MissingBaselineCount { get; private set; }

        public int RejectedCount { get; private set; }

        /// <summary>
        /// Repair signals that arrived while the matching episode was already
        /// running: the schedule owns the pacing, so no extra request is queued.
        /// </summary>
        public int SuppressedRequestCount { get; private set; }

        /// <summary>Requests dropped because the repair they asked for arrived.</summary>
        public int ObsoleteRequestCount { get; private set; }

        /// <summary>Requests overwritten in the bounded ring (latest wins).</summary>
        public int RequestOverflowCount { get; private set; }

        public int TotalRequestCount { get; private set; }

        public int SnapshotRequestCount { get; private set; }

        public int DeltaResumeCount { get; private set; }

        /// <summary>Catch-up episodes that exhausted their budget.</summary>
        public int CatchUpEscalationCount { get; private set; }

        /// <summary>Baseline episodes that exhausted their budget.</summary>
        public int BaselineTimeoutCount { get; private set; }

        /// <summary>Progress reports that did not move the confirmed tick forward.</summary>
        public int IgnoredProgressCount { get; private set; }

        /// <summary>Keyframe installations refused because the receiver is terminal.</summary>
        public int IgnoredBaselineCount { get; private set; }

        /// <summary>Progress recorded in a state that must not apply deltas.</summary>
        public int AnomalyCount { get; private set; }

        /// <summary>
        /// Evaluates the Apply-Guard without touching anything: the same verdict
        /// <see cref="OnDelta"/> acts on, usable for diagnostics and tests.
        /// </summary>
        public ReplicationApplyDecision Inspect(
            in DeltaSnapshotHeader header,
            ushort keyframeRef,
            out ReplicationGuardReason reason)
        {
            if (State == ReplicationReceiverState.ConnectionFailed)
            {
                reason = ReplicationGuardReason.Terminal;
                return ReplicationApplyDecision.Rejected;
            }

            // Stale, duplicate or reordered-behind: never applied, never repaired.
            if (header.Tick <= LastAppliedTick)
            {
                reason = ReplicationGuardReason.StaleTick;
                return ReplicationApplyDecision.Stale;
            }

            if (!HasBaseline)
            {
                reason = ReplicationGuardReason.MissingBaseline;
                return ReplicationApplyDecision.RebaseRequired;
            }

            if (keyframeRef != CurrentKeyframeSeq)
            {
                reason = ReplicationGuardReason.KeyframeMismatch;
                return ReplicationApplyDecision.RebaseRequired;
            }

            if (header.BaseTick <= LastAppliedTick)
            {
                reason = ReplicationGuardReason.Established;
                return ReplicationApplyDecision.Apply;
            }

            var gap = header.BaseTick - LastAppliedTick;
            if (gap <= (ulong)_config.WindowTicks)
            {
                reason = ReplicationGuardReason.DependencyGap;
                return ReplicationApplyDecision.CatchUpRequired;
            }

            reason = ReplicationGuardReason.BaseStale;
            return ReplicationApplyDecision.RebaseRequired;
        }

        /// <summary>
        /// Apply-Guard plus the transitions and repair traffic it implies. A
        /// verdict of <see cref="ReplicationApplyDecision.Apply"/> does not
        /// advance <see cref="LastAppliedTick"/>: the caller applies the records
        /// first and then reports progress through <see cref="NoteApplied"/>.
        /// </summary>
        public ReplicationApplyDecision OnDelta(
            long nowMs,
            in DeltaSnapshotHeader header,
            ushort keyframeRef,
            out ReplicationGuardReason reason)
        {
            var decision = Inspect(in header, keyframeRef, out reason);

            switch (decision)
            {
                case ReplicationApplyDecision.Stale:
                    StaleDropCount++;
                    break;

                case ReplicationApplyDecision.Rejected:
                    RejectedCount++;
                    break;

                case ReplicationApplyDecision.CatchUpRequired:
                    DependencyGapCount++;
                    BeginRepair(
                        ReplicationReceiverState.CatchingUp,
                        ReplicationRequestKind.DeltaResume,
                        nowMs);
                    break;

                case ReplicationApplyDecision.RebaseRequired:
                    CountRebase(reason);
                    BeginRepair(
                        ReplicationReceiverState.Rebasing,
                        ReplicationRequestKind.SnapshotRequest,
                        nowMs);
                    break;
            }

            return decision;
        }

        /// <summary>
        /// Reports that the records of <paramref name="tick"/> were applied to
        /// the client world. This is the only way the confirmed tick moves
        /// forward, and it is what a catch-up episode ends with.
        /// </summary>
        public void NoteApplied(ulong tick)
        {
            if (State == ReplicationReceiverState.ConnectionFailed || tick <= LastAppliedTick)
            {
                IgnoredProgressCount++;
                return;
            }

            LastAppliedTick = tick;
            AppliedCount++;

            if (State == ReplicationReceiverState.CatchingUp)
            {
                // The cumulative delta arrived: the stream is healthy again.
                State = ReplicationReceiverState.Streaming;
                _attemptsMade = 0;
                _episodeKind = ReplicationRequestKind.None;
                ClearPendingRequests();
            }
            else if (State != ReplicationReceiverState.Streaming)
            {
                AnomalyCount++;
            }
        }

        /// <summary>
        /// Installs a completely assembled keyframe: it defines the new base,
        /// the new keyframe generation and the confirmed tick, and returns the
        /// receiver to <see cref="ReplicationReceiverState.Streaming"/> from any
        /// non-terminal state. Queued repair requests become obsolete.
        /// </summary>
        public void NoteBaseline(ushort keyframeSeq, ulong tick)
        {
            if (State == ReplicationReceiverState.ConnectionFailed)
            {
                IgnoredBaselineCount++;
                return;
            }

            CurrentKeyframeSeq = keyframeSeq;
            BaseKeyframeTick = tick;
            LastAppliedTick = tick;
            HasBaseline = true;
            FailureReason = ReplicationFailureReason.None;
            State = ReplicationReceiverState.Streaming;
            _attemptsMade = 0;
            _episodeKind = ReplicationRequestKind.None;
            _episodePendingStart = false;
            ClearPendingRequests();
        }

        /// <summary>
        /// Declares the current base unusable - e.g. the client world could not
        /// apply a record, so the mirror is no longer trustworthy - and asks for
        /// a fresh keyframe. Uses the same three-attempt budget as
        /// <see cref="ReplicationReceiverState.Unbased"/>.
        /// </summary>
        public void ReportBaselineLoss(long nowMs)
        {
            if (State == ReplicationReceiverState.ConnectionFailed)
            {
                return;
            }

            BeginRepair(
                ReplicationReceiverState.Rebasing,
                ReplicationRequestKind.SnapshotRequest,
                nowMs);
        }

        /// <summary>
        /// Drives the repair schedule of the current episode. Call it once per
        /// pump with a monotonic timestamp; a late call compresses the schedule
        /// but never exceeds the attempt limit.
        /// </summary>
        public void Update(long nowMs)
        {
            switch (State)
            {
                case ReplicationReceiverState.Unbased:
                case ReplicationReceiverState.CatchingUp:
                case ReplicationReceiverState.Rebasing:
                    break;
                default:
                    return;
            }

            if (_episodePendingStart)
            {
                _episodePendingStart = false;
                StartEpisode(
                    State,
                    State == ReplicationReceiverState.CatchingUp
                        ? ReplicationRequestKind.DeltaResume
                        : ReplicationRequestKind.SnapshotRequest,
                    nowMs);
                return;
            }

            while (_attemptsMade < ReplicationReceiverConfig.AttemptLimit && nowMs >= _nextAttemptDueMs)
            {
                MakeAttempt(nowMs);
            }

            if (_attemptsMade >= ReplicationReceiverConfig.AttemptLimit && nowMs >= _nextAttemptDueMs)
            {
                Escalate(nowMs);
            }
        }

        public bool TryTakeRequest(out ReplicationRequest request)
        {
            if (_requestCount == 0)
            {
                request = default;
                return false;
            }

            request = _requests[_requestHead];
            _requests[_requestHead] = default;
            _requestHead = (_requestHead + 1) % _requests.Length;
            _requestCount--;
            return true;
        }

        /// <summary>Drops queued repair traffic, e.g. when the repair arrived.</summary>
        public void ClearPendingRequests()
        {
            ObsoleteRequestCount += _requestCount;
            for (var index = 0; index < _requests.Length; index++)
            {
                _requests[index] = default;
            }

            _requestHead = 0;
            _requestCount = 0;
        }

        /// <summary>
        /// Returns the receiver to its initial <see cref="ReplicationReceiverState.Unbased"/>
        /// state, including the diagnostics: a new attachment (or a Phase 2.7
        /// resync) starts from scratch. The next <see cref="Update"/> opens a new
        /// baseline episode.
        /// </summary>
        public void Reset()
        {
            State = ReplicationReceiverState.Unbased;
            LastAppliedTick = 0UL;
            BaseKeyframeTick = 0UL;
            CurrentKeyframeSeq = 0;
            HasBaseline = false;
            FailureReason = ReplicationFailureReason.None;

            _episodeKind = ReplicationRequestKind.None;
            _episodePendingStart = true;
            _attemptsMade = 0;
            _episodeStartMs = 0L;
            _nextAttemptDueMs = 0L;

            for (var index = 0; index < _requests.Length; index++)
            {
                _requests[index] = default;
            }

            _requestHead = 0;
            _requestCount = 0;

            AppliedCount = 0;
            StaleDropCount = 0;
            KeyframeMismatchCount = 0;
            DependencyGapCount = 0;
            BaseStaleCount = 0;
            MissingBaselineCount = 0;
            RejectedCount = 0;
            SuppressedRequestCount = 0;
            ObsoleteRequestCount = 0;
            RequestOverflowCount = 0;
            TotalRequestCount = 0;
            SnapshotRequestCount = 0;
            DeltaResumeCount = 0;
            CatchUpEscalationCount = 0;
            BaselineTimeoutCount = 0;
            IgnoredProgressCount = 0;
            IgnoredBaselineCount = 0;
            AnomalyCount = 0;
        }

        public override string ToString() =>
            $"ReceiverFSM(state={State}, last={LastAppliedTick}, base={BaseKeyframeTick}, " +
            $"keyframeSeq={CurrentKeyframeSeq}, attempts={_attemptsMade}/{ReplicationReceiverConfig.AttemptLimit}, " +
            $"pending={_requestCount}, failure={FailureReason})";

        private void CountRebase(ReplicationGuardReason reason)
        {
            switch (reason)
            {
                case ReplicationGuardReason.KeyframeMismatch:
                    KeyframeMismatchCount++;
                    break;
                case ReplicationGuardReason.BaseStale:
                    BaseStaleCount++;
                    break;
                default:
                    MissingBaselineCount++;
                    break;
            }
        }

        /// <summary>
        /// Starts a repair episode unless the matching episode is already
        /// running: an episode owns its own pacing, so repeated bad packets must
        /// not multiply the request traffic.
        /// </summary>
        private void BeginRepair(
            ReplicationReceiverState target,
            ReplicationRequestKind kind,
            long nowMs)
        {
            if (State == ReplicationReceiverState.Unbased && !_episodePendingStart)
            {
                // The baseline episode is already running and owns the pacing; a
                // delta must neither restart it nor add traffic to it.
                SuppressedRequestCount++;
                return;
            }

            if (State == target)
            {
                SuppressedRequestCount++;
                return;
            }

            StartEpisode(target, kind, nowMs);
        }

        private void StartEpisode(
            ReplicationReceiverState state,
            ReplicationRequestKind kind,
            long nowMs)
        {
            State = state;
            if (kind == ReplicationRequestKind.SnapshotRequest)
            {
                // Asking for a fresh keyframe means the current base is unusable.
                HasBaseline = false;
            }

            _episodeKind = kind;
            _episodePendingStart = false;
            _episodeStartMs = nowMs;
            _attemptsMade = 0;
            MakeAttempt(nowMs);
        }

        private void MakeAttempt(long nowMs)
        {
            _attemptsMade++;
            Enqueue(new ReplicationRequest(
                _episodeKind,
                _attemptsMade,
                nowMs,
                LastAppliedTick,
                BaseKeyframeTick,
                0UL));

            // The schedule is anchored at the episode start, not at the previous
            // attempt: a pump that calls Update late still fires every attempt as
            // soon as the clock passes it, and the attempt limit holds.
            _nextAttemptDueMs = _episodeStartMs + DueOffsetMs(_attemptsMade);
        }

        /// <summary>
        /// Cumulative offset of the next event after <paramref name="completedAttempts"/>
        /// attempts: 500 ms, 1500 ms, then 3500 ms - the last one being the
        /// terminal deadline rather than a fourth attempt.
        /// </summary>
        private long DueOffsetMs(int completedAttempts)
        {
            var offset = 0L;
            for (var attempt = 1;
                attempt <= completedAttempts && attempt <= ReplicationReceiverConfig.AttemptLimit;
                attempt++)
            {
                offset += _config.BackoffMs(attempt);
            }

            return offset;
        }

        private void Escalate(long nowMs)
        {
            if (State == ReplicationReceiverState.CatchingUp)
            {
                // Liveness guard: a catch-up the server never answered must not
                // stall the receiver forever, so it degrades to a re-baseline.
                // The retry limit itself is not fixed by ADR-010 (TBD).
                CatchUpEscalationCount++;
                StartEpisode(
                    ReplicationReceiverState.Rebasing,
                    ReplicationRequestKind.SnapshotRequest,
                    nowMs);
                return;
            }

            BaselineTimeoutCount++;
            State = ReplicationReceiverState.ConnectionFailed;
            FailureReason = ReplicationFailureReason.ResyncRequired;
            HasBaseline = false;
            _attemptsMade = 0;
            _episodeKind = ReplicationRequestKind.None;
            _episodePendingStart = false;
            ClearPendingRequests();
        }

        private void Enqueue(in ReplicationRequest request)
        {
            if (_requestCount == _requests.Length)
            {
                // Bounded ring, latest wins: an undrained pump must not grow memory.
                _requestHead = (_requestHead + 1) % _requests.Length;
                _requestCount--;
                RequestOverflowCount++;
            }

            _requests[(_requestHead + _requestCount) % _requests.Length] = request;
            _requestCount++;

            TotalRequestCount++;
            if (request.Kind == ReplicationRequestKind.SnapshotRequest)
            {
                SnapshotRequestCount++;
            }
            else
            {
                DeltaResumeCount++;
            }
        }
    }
}
