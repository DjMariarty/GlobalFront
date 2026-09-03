using GlobalFront.Client.Replication;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server.Replication;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Client.Replication
{
    /// <summary>
    /// Apply-Guard and state-machine behaviour of the client replication
    /// receiver FSM (Phase 2.6, step 2.6.3, ADR-010 / OD-14).
    ///
    /// Guard under test: a delta is applied when
    /// <c>Tick &gt; LastAppliedTick &amp;&amp; BaseTick &lt;= LastAppliedTick
    /// &amp;&amp; KeyframeRef == CurrentKeyframeSeq</c>. Skipping intermediate
    /// ticks is safe; a dependency gap inside the 120-tick window asks for a
    /// cumulative delta; anything older or referencing another keyframe asks for
    /// a fresh baseline; three failed baseline attempts are terminal.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationReceiverFsmTests
    {
        private const ushort Keyframe = 7;

        internal static DeltaSnapshotHeader Delta(
            ulong tick,
            ulong baseTick,
            ushort addCount = 0,
            ushort updateCount = 0,
            ushort removeCount = 0,
            DeltaFlags flags = DeltaFlags.None)
        {
            return DeltaSnapshotHeader.CreateDelta(
                tick, baseTick, flags, 0, addCount, updateCount, removeCount);
        }

        /// <summary>An FSM that already holds a keyframe base at <paramref name="tick"/>.</summary>
        private static ReplicationReceiverFSM Streaming(
            ulong tick = 10,
            ushort keyframeSeq = Keyframe,
            ReplicationReceiverConfig config = default)
        {
            var fsm = new ReplicationReceiverFSM(
                config.WindowTicks > 0 ? config : ReplicationReceiverConfig.Default);
            fsm.NoteBaseline(keyframeSeq, tick);
            return fsm;
        }

        // ---------------------------------------------------------------
        // Apply-Guard: accept
        // ---------------------------------------------------------------

        [Test]
        public void Guard_NewerTickOnTheConfirmedBase_Applies()
        {
            var fsm = Streaming(tick: 10);

            var decision = fsm.OnDelta(0, Delta(11, 10), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.Apply));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.Established));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(fsm.PendingRequestCount, Is.Zero, "an applicable delta needs no repair traffic");

            fsm.NoteApplied(11);
            Assert.That(fsm.LastAppliedTick, Is.EqualTo(11UL));
            Assert.That(fsm.AppliedCount, Is.EqualTo(1));
        }

        [Test]
        public void Guard_SkippedIntermediateTicks_StillApplies()
        {
            var fsm = Streaming(tick: 10);

            // Establishing delta: the interval (10, 20] is self-contained, so the
            // receiver never has to wait for ticks 11..19.
            var decision = fsm.OnDelta(0, Delta(20, 10), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.Apply));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.Established));

            fsm.NoteApplied(20);
            Assert.That(fsm.LastAppliedTick, Is.EqualTo(20UL));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Streaming));
        }

        [Test]
        public void Guard_OlderBaseThanLastApplied_Applies()
        {
            var fsm = Streaming(tick: 100);

            var decision = fsm.OnDelta(0, Delta(101, 40), Keyframe, out _);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.Apply),
                "BaseTick <= LastAppliedTick is a superset delta and is always applicable");
        }

        [Test]
        public void Guard_ReorderedNewerPacket_IsNotDroppedAsStale()
        {
            var fsm = Streaming(tick: 10);
            fsm.OnDelta(0, Delta(14, 10), Keyframe, out _);
            fsm.NoteApplied(14);

            // A packet encoded before tick 14 arrives late: it is stale, not the
            // newer one that came first.
            var late = fsm.OnDelta(1, Delta(12, 10), Keyframe, out var lateReason);

            Assert.That(late, Is.EqualTo(ReplicationApplyDecision.Stale));
            Assert.That(lateReason, Is.EqualTo(ReplicationGuardReason.StaleTick));
            Assert.That(fsm.LastAppliedTick, Is.EqualTo(14UL));
        }

        // ---------------------------------------------------------------
        // Apply-Guard: reject
        // ---------------------------------------------------------------

        [Test]
        public void Guard_StaleTick_IsDroppedWithoutSideEffects()
        {
            var fsm = Streaming(tick: 10);
            fsm.OnDelta(0, Delta(11, 10), Keyframe, out _);
            fsm.NoteApplied(11);

            var decision = fsm.OnDelta(1, Delta(11, 10), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.Stale));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.StaleTick));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(fsm.PendingRequestCount, Is.Zero);
            Assert.That(fsm.StaleDropCount, Is.EqualTo(1));
        }

        [Test]
        public void Guard_KeyframeMismatch_RebasesAndAsksForAFreshKeyframe()
        {
            var fsm = Streaming(tick: 10, keyframeSeq: 7);

            var decision = fsm.OnDelta(100, Delta(11, 10), 8, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.RebaseRequired));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.KeyframeMismatch));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(fsm.KeyframeMismatchCount, Is.EqualTo(1));
            Assert.That(fsm.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));
            Assert.That(request.KeyframeTick, Is.Zero, "SnapshotRequest{KeyframeTick=0} asks for a fresh keyframe");
            Assert.That(request.Attempt, Is.EqualTo(1));
            Assert.That(request.RequestedAtMs, Is.EqualTo(100L));
            Assert.That(fsm.PendingRequestCount, Is.Zero);
        }

        [Test]
        public void Guard_DependencyGapInsideTheWindow_CatchesUpWithDeltaResume()
        {
            var fsm = Streaming(tick: 10);

            var decision = fsm.OnDelta(200, Delta(50, 13), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.CatchUpRequired));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.DependencyGap));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.CatchingUp));
            Assert.That(fsm.DependencyGapCount, Is.EqualTo(1));
            Assert.That(fsm.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.DeltaResume));
            Assert.That(request.LastAppliedTick, Is.EqualTo(10UL), "DeltaResume carries the confirmed tick");
            Assert.That(request.BaseKeyframeTick, Is.EqualTo(10UL));
            Assert.That(request.Attempt, Is.EqualTo(1));
        }

        [Test]
        public void Guard_GapOfExactlyTheWindow_CatchesUp()
        {
            var fsm = Streaming(tick: 10);

            var decision = fsm.OnDelta(0, Delta(200, 130), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.CatchUpRequired));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.DependencyGap));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.CatchingUp));
        }

        [Test]
        public void Guard_GapBeyondTheWindow_Rebases()
        {
            var fsm = Streaming(tick: 10);

            var decision = fsm.OnDelta(0, Delta(400, 131), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.RebaseRequired));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.BaseStale));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(fsm.BaseStaleCount, Is.EqualTo(1));
            Assert.That(fsm.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));
        }

        [Test]
        public void Guard_WithoutABaseline_NeverAppliesADelta()
        {
            var fsm = new ReplicationReceiverFSM();
            fsm.Update(0);
            var queued = fsm.PendingRequestCount;

            var decision = fsm.OnDelta(10, Delta(5, 0), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.RebaseRequired));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.MissingBaseline));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Unbased),
                "an unbased receiver keeps its running baseline episode instead of restarting it");
            Assert.That(fsm.AttemptsMade, Is.EqualTo(1));
            Assert.That(fsm.PendingRequestCount, Is.EqualTo(queued));
            Assert.That(fsm.SuppressedRequestCount, Is.EqualTo(1));
        }

        [Test]
        public void Guard_WhileRebasing_OnlyAKeyframeRestoresStreaming()
        {
            var fsm = Streaming(tick: 10, keyframeSeq: 7);
            fsm.OnDelta(0, Delta(11, 10), 9, out _);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));

            var decision = fsm.OnDelta(10, Delta(12, 10), 7, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.RebaseRequired));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.MissingBaseline),
                "the base was declared unusable, so even a well-formed delta is refused");
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
        }

        [Test]
        public void Inspect_IsPureAndChangesNothing()
        {
            var fsm = Streaming(tick: 10, keyframeSeq: 7);

            var decision = fsm.Inspect(Delta(50, 13), 99, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.RebaseRequired));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.KeyframeMismatch));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(fsm.PendingRequestCount, Is.Zero);
            Assert.That(fsm.TotalRequestCount, Is.Zero);
            Assert.That(fsm.LastAppliedTick, Is.EqualTo(10UL));
        }

        // ---------------------------------------------------------------
        // Transitions
        // ---------------------------------------------------------------

        [Test]
        public void NoteBaseline_MovesUnbasedToStreaming()
        {
            var fsm = new ReplicationReceiverFSM();
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Unbased));
            Assert.That(fsm.HasBaseline, Is.False);

            fsm.NoteBaseline(Keyframe, 42);

            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(fsm.HasBaseline, Is.True);
            Assert.That(fsm.CurrentKeyframeSeq, Is.EqualTo(Keyframe));
            Assert.That(fsm.BaseKeyframeTick, Is.EqualTo(42UL));
            Assert.That(fsm.LastAppliedTick, Is.EqualTo(42UL));
            Assert.That(fsm.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
        }

        [Test]
        public void NoteBaseline_MovesRebasingToStreaming()
        {
            var fsm = Streaming(tick: 10, keyframeSeq: 7);
            fsm.OnDelta(0, Delta(11, 10), 8, out _);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));

            fsm.NoteBaseline(8, 500);

            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(fsm.CurrentKeyframeSeq, Is.EqualTo(8));
            Assert.That(fsm.LastAppliedTick, Is.EqualTo(500UL));
            Assert.That(fsm.BaseKeyframeTick, Is.EqualTo(500UL));
        }

        [Test]
        public void NoteBaseline_RestartsTheAttemptBudgetAndDropsObsoleteRequests()
        {
            var fsm = new ReplicationReceiverFSM();
            fsm.Update(0);
            fsm.Update(500);
            Assert.That(fsm.PendingRequestCount, Is.EqualTo(2));

            fsm.NoteBaseline(Keyframe, 30);

            Assert.That(fsm.PendingRequestCount, Is.Zero, "queued baseline requests are obsolete once the base arrived");
            Assert.That(fsm.ObsoleteRequestCount, Is.EqualTo(2));
            Assert.That(fsm.AttemptsMade, Is.Zero);

            // A later loss of the base gets a fresh budget of three attempts.
            fsm.ReportBaselineLoss(1000);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(fsm.AttemptsMade, Is.EqualTo(1));
            Assert.That(fsm.TotalRequestCount, Is.EqualTo(3));
        }

        [Test]
        public void CumulativeDelta_ReturnsCatchingUpToStreaming()
        {
            var fsm = Streaming(tick: 10);
            fsm.OnDelta(0, Delta(50, 13), Keyframe, out _);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.CatchingUp));

            // The server answers DeltaResume(10) with a cumulative delta based on 10.
            var decision = fsm.OnDelta(300, Delta(50, 10), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.Apply));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.Established));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.CatchingUp),
                "the FSM returns to STREAMING only after the records were applied");

            fsm.NoteApplied(50);

            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(fsm.LastAppliedTick, Is.EqualTo(50UL));
        }

        [Test]
        public void ReportBaselineLoss_LeavesStreamingAndAsksForAKeyframe()
        {
            var fsm = Streaming(tick: 10);

            fsm.ReportBaselineLoss(77);

            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(fsm.HasBaseline, Is.False);
            Assert.That(fsm.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));
            Assert.That(request.RequestedAtMs, Is.EqualTo(77L));
            Assert.That(request.LastAppliedTick, Is.EqualTo(10UL));
        }

        [Test]
        public void ReportBaselineLoss_WhileAlreadyRebasing_DoesNotRestartTheEpisode()
        {
            var fsm = Streaming(tick: 10);
            fsm.ReportBaselineLoss(0);
            var attempts = fsm.AttemptsMade;

            fsm.ReportBaselineLoss(10);
            fsm.ReportBaselineLoss(20);

            Assert.That(fsm.AttemptsMade, Is.EqualTo(attempts));
            Assert.That(fsm.SuppressedRequestCount, Is.EqualTo(2));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
        }

        // ---------------------------------------------------------------
        // UNBASED timeout: exactly three attempts, then terminal
        // ---------------------------------------------------------------

        [Test]
        public void Unbased_SendsExactlyThreeBaselineRequestsWithTheDocumentedBackoff()
        {
            var fsm = new ReplicationReceiverFSM();

            fsm.Update(0);
            Assert.That(fsm.PendingRequestCount, Is.EqualTo(1), "attempt 1 leaves immediately");
            Assert.That(fsm.NextAttemptDueMs, Is.EqualTo(500L));

            fsm.Update(499);
            Assert.That(fsm.TotalRequestCount, Is.EqualTo(1));

            fsm.Update(500);
            Assert.That(fsm.TotalRequestCount, Is.EqualTo(2), "attempt 2 after the 0.5 s backoff");
            Assert.That(fsm.NextAttemptDueMs, Is.EqualTo(1500L));

            fsm.Update(1499);
            Assert.That(fsm.TotalRequestCount, Is.EqualTo(2));

            fsm.Update(1500);
            Assert.That(fsm.TotalRequestCount, Is.EqualTo(3), "attempt 3 after the 1.0 s backoff");
            Assert.That(fsm.NextAttemptDueMs, Is.EqualTo(3500L));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Unbased));
            Assert.That(fsm.SnapshotRequestCount, Is.EqualTo(3));
            Assert.That(fsm.DeltaResumeCount, Is.Zero);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                Assert.That(fsm.TryTakeRequest(out var request), Is.True);
                Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));
                Assert.That(request.Attempt, Is.EqualTo(attempt));
                Assert.That(request.KeyframeTick, Is.Zero);
            }

            Assert.That(fsm.TryTakeRequest(out _), Is.False);
        }

        [Test]
        public void Unbased_FailsTerminallyAfterTheThirdAttemptTimesOut()
        {
            var fsm = new ReplicationReceiverFSM();
            fsm.Update(0);
            fsm.Update(500);
            fsm.Update(1500);

            fsm.Update(3499);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Unbased));

            fsm.Update(3500);

            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed));
            Assert.That(fsm.FailureReason, Is.EqualTo(ReplicationFailureReason.ResyncRequired));
            Assert.That(fsm.TotalRequestCount, Is.EqualTo(3), "never a fourth attempt");
            Assert.That(fsm.PendingRequestCount, Is.Zero, "a terminal receiver stops requesting");
        }

        [Test]
        public void Unbased_BaselineBeforeTheDeadline_PreventsTheFailure()
        {
            var fsm = new ReplicationReceiverFSM();
            fsm.Update(0);
            fsm.Update(500);
            fsm.Update(1500);

            fsm.NoteBaseline(Keyframe, 20);
            fsm.Update(3500);
            fsm.Update(10_000);

            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(fsm.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
            Assert.That(fsm.TotalRequestCount, Is.EqualTo(3));
        }

        [Test]
        public void Unbased_ASlowPump_StillFiresEveryAttemptAndEscalatesOnTime()
        {
            var fsm = new ReplicationReceiverFSM();

            fsm.Update(0);
            Assert.That(fsm.SnapshotRequestCount, Is.EqualTo(1));

            // The pump was blocked for ten seconds: the whole schedule is overdue,
            // but the attempt limit still holds and the receiver does not linger.
            fsm.Update(10_000);

            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed));
            Assert.That(fsm.SnapshotRequestCount, Is.EqualTo(3), "exactly three attempts, never more");
            Assert.That(fsm.FailureReason, Is.EqualTo(ReplicationFailureReason.ResyncRequired));
        }

        [Test]
        public void Rebasing_ExhaustsTheSameThreeAttemptBudget()
        {
            var fsm = Streaming(tick: 10, keyframeSeq: 7);

            fsm.OnDelta(1000, Delta(11, 10), 8, out _);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(fsm.AttemptsMade, Is.EqualTo(1));

            fsm.Update(1500);
            Assert.That(fsm.AttemptsMade, Is.EqualTo(2));
            fsm.Update(2500);
            Assert.That(fsm.AttemptsMade, Is.EqualTo(3));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));

            fsm.Update(4499);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));

            fsm.Update(4500);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed));
            Assert.That(fsm.FailureReason, Is.EqualTo(ReplicationFailureReason.ResyncRequired));
        }

        [Test]
        public void CatchingUp_ExhaustedResumeBudget_EscalatesToRebasing()
        {
            var fsm = Streaming(tick: 10);

            fsm.OnDelta(0, Delta(50, 13), Keyframe, out _);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.CatchingUp));
            Assert.That(fsm.DeltaResumeCount, Is.EqualTo(1));

            fsm.Update(500);
            fsm.Update(1500);
            Assert.That(fsm.DeltaResumeCount, Is.EqualTo(3), "three resume attempts, then escalation");
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.CatchingUp));

            fsm.Update(3500);

            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing),
                "a catch-up that never converges must not stall forever");
            Assert.That(fsm.CatchUpEscalationCount, Is.EqualTo(1));
            Assert.That(fsm.SnapshotRequestCount, Is.EqualTo(1));

            fsm.Update(4000);
            fsm.Update(5000);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));

            fsm.Update(7000);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed));
        }

        [Test]
        public void CatchingUp_RepeatedGappedDeltas_DoNotRestartTheEpisode()
        {
            var fsm = Streaming(tick: 10);
            fsm.OnDelta(0, Delta(50, 13), Keyframe, out _);

            fsm.OnDelta(10, Delta(60, 20), Keyframe, out var reason);
            fsm.OnDelta(20, Delta(70, 30), Keyframe, out _);

            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.DependencyGap));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.CatchingUp));
            Assert.That(fsm.AttemptsMade, Is.EqualTo(1));
            Assert.That(fsm.SuppressedRequestCount, Is.EqualTo(2));
            Assert.That(fsm.DependencyGapCount, Is.EqualTo(3));
        }

        [Test]
        public void CatchingUp_GapBeyondTheWindow_EscalatesToRebasing()
        {
            var fsm = Streaming(tick: 10);
            fsm.OnDelta(0, Delta(50, 13), Keyframe, out _);

            var decision = fsm.OnDelta(10, Delta(400, 300), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.RebaseRequired));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.BaseStale));
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
        }

        // ---------------------------------------------------------------
        // Terminal state
        // ---------------------------------------------------------------

        [Test]
        public void ConnectionFailed_IsTerminal()
        {
            var fsm = new ReplicationReceiverFSM();
            fsm.Update(0);
            fsm.Update(3500);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed));

            var decision = fsm.OnDelta(60_001, Delta(70, 60), Keyframe, out var reason);

            Assert.That(decision, Is.EqualTo(ReplicationApplyDecision.Rejected));
            Assert.That(reason, Is.EqualTo(ReplicationGuardReason.Terminal));
            Assert.That(fsm.RejectedCount, Is.EqualTo(1));

            fsm.NoteBaseline(Keyframe, 80);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed),
                "recovery is Phase 2.7: the terminal state is left only through Reset");
            Assert.That(fsm.HasBaseline, Is.False);

            fsm.NoteApplied(90);
            Assert.That(fsm.LastAppliedTick, Is.Zero);

            fsm.Update(120_000);
            Assert.That(fsm.PendingRequestCount, Is.Zero);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed));
        }

        [Test]
        public void Reset_RearmsTheReceiverForTheNextAttachment()
        {
            var fsm = new ReplicationReceiverFSM();
            fsm.Update(0);
            fsm.Update(3500);
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed));

            fsm.Reset();

            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Unbased));
            Assert.That(fsm.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
            Assert.That(fsm.HasBaseline, Is.False);
            Assert.That(fsm.LastAppliedTick, Is.Zero);
            Assert.That(fsm.CurrentKeyframeSeq, Is.Zero);
            Assert.That(fsm.BaseKeyframeTick, Is.Zero);
            Assert.That(fsm.PendingRequestCount, Is.Zero);

            fsm.Update(0);
            Assert.That(fsm.TotalRequestCount, Is.EqualTo(1), "a fresh baseline episode starts again");
            Assert.That(fsm.State, Is.EqualTo(ReplicationReceiverState.Unbased));
        }

        [Test]
        public void NoteApplied_RejectsNonForwardProgress()
        {
            var fsm = Streaming(tick: 10);

            fsm.NoteApplied(10);
            fsm.NoteApplied(4);

            Assert.That(fsm.LastAppliedTick, Is.EqualTo(10UL));
            Assert.That(fsm.IgnoredProgressCount, Is.EqualTo(2));
            Assert.That(fsm.AppliedCount, Is.Zero);
        }

        // ---------------------------------------------------------------
        // Request queue and configuration
        // ---------------------------------------------------------------

        [Test]
        public void RequestQueue_KeepsTheLatestRequestsOnOverflow()
        {
            var config = new ReplicationReceiverConfig(
                ReplicationReceiverConfig.DefaultWindowTicks,
                ReplicationReceiverConfig.DefaultFirstBackoffMs,
                ReplicationReceiverConfig.DefaultSecondBackoffMs,
                ReplicationReceiverConfig.DefaultThirdBackoffMs,
                requestQueueCapacity: 2);
            var fsm = new ReplicationReceiverFSM(config);

            fsm.Update(0);
            fsm.Update(500);
            fsm.Update(1500);

            Assert.That(fsm.PendingRequestCount, Is.EqualTo(2));
            Assert.That(fsm.RequestOverflowCount, Is.EqualTo(1));
            Assert.That(fsm.TryTakeRequest(out var oldest), Is.True);
            Assert.That(oldest.Attempt, Is.EqualTo(2), "the oldest request is overwritten, latest wins");
            Assert.That(fsm.TryTakeRequest(out var newest), Is.True);
            Assert.That(newest.Attempt, Is.EqualTo(3));
        }

        [Test]
        public void ClearPendingRequests_DropsQueuedTraffic()
        {
            var fsm = new ReplicationReceiverFSM();
            fsm.Update(0);
            Assert.That(fsm.PendingRequestCount, Is.EqualTo(1));

            fsm.ClearPendingRequests();

            Assert.That(fsm.PendingRequestCount, Is.Zero);
            Assert.That(fsm.TryTakeRequest(out _), Is.False);
        }

        [Test]
        public void DefaultWindow_MatchesTheServerRetainedHistoryWindow()
        {
            // OD-11: one normative constant shared by both ends. Drift here means
            // the client asks for cumulative deltas the server can no longer build.
            Assert.That(
                ReplicationReceiverConfig.DefaultWindowTicks,
                Is.EqualTo(ReplicationHistoryRing.Capacity));
            Assert.That(
                new ReplicationReceiverFSM().Config.WindowTicks,
                Is.EqualTo(120));
        }

        [Test]
        public void DefaultConfig_FixesTheThreeAttemptBudgetAndBackoff()
        {
            var config = ReplicationReceiverConfig.Default;

            Assert.That(ReplicationReceiverConfig.AttemptLimit, Is.EqualTo(3));
            Assert.That(config.BackoffMs(1), Is.EqualTo(500L));
            Assert.That(config.BackoffMs(2), Is.EqualTo(1000L));
            Assert.That(config.BackoffMs(3), Is.EqualTo(2000L));
            Assert.That(config.RequestQueueCapacity, Is.EqualTo(ReplicationReceiverConfig.DefaultRequestQueueCapacity));
        }

        [Test]
        public void Config_RejectsInvalidValues()
        {
            Assert.That(
                () => new ReplicationReceiverConfig(0, 500, 1000, 2000, 8),
                Throws.InstanceOf<System.ArgumentOutOfRangeException>());
            Assert.That(
                () => new ReplicationReceiverConfig(120, 0, 1000, 2000, 8),
                Throws.InstanceOf<System.ArgumentOutOfRangeException>());
            Assert.That(
                () => new ReplicationReceiverConfig(120, 500, 1000, 2000, 0),
                Throws.InstanceOf<System.ArgumentOutOfRangeException>());
            Assert.That(
                () => new ReplicationReceiverFSM(default),
                Throws.ArgumentException,
                "a default-constructed config is all-zero and therefore invalid");
        }

        [Test]
        public void CustomWindow_IsHonouredByTheGuard()
        {
            var config = new ReplicationReceiverConfig(4, 500, 1000, 2000, 8);
            var fsm = Streaming(tick: 10, keyframeSeq: Keyframe, config: config);

            Assert.That(
                fsm.OnDelta(0, Delta(30, 14), Keyframe, out var inside),
                Is.EqualTo(ReplicationApplyDecision.CatchUpRequired));
            Assert.That(inside, Is.EqualTo(ReplicationGuardReason.DependencyGap));

            fsm.NoteBaseline(Keyframe, 10);
            Assert.That(
                fsm.OnDelta(0, Delta(30, 15), Keyframe, out var outside),
                Is.EqualTo(ReplicationApplyDecision.RebaseRequired));
            Assert.That(outside, Is.EqualTo(ReplicationGuardReason.BaseStale));
        }

        [Test]
        public void Request_StructuralEqualityAndText()
        {
            var left = new ReplicationRequest(
                ReplicationRequestKind.DeltaResume, 2, 1500, 10, 5, 0);
            var same = new ReplicationRequest(
                ReplicationRequestKind.DeltaResume, 2, 1500, 10, 5, 0);
            var other = new ReplicationRequest(
                ReplicationRequestKind.SnapshotRequest, 2, 1500, 10, 5, 0);

            Assert.That(left, Is.EqualTo(same));
            Assert.That(left == same, Is.True);
            Assert.That(left != other, Is.True);
            Assert.That(left.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(left.ToString(), Does.Contain("DeltaResume"));
            Assert.That(default(ReplicationRequest).Kind, Is.EqualTo(ReplicationRequestKind.None));
        }

        [Test]
        public void StateEnum_CoversTheDocumentedFsmStates()
        {
            Assert.That(System.Enum.GetNames(typeof(ReplicationReceiverState)), Is.EquivalentTo(new[]
            {
                "Unbased", "Streaming", "CatchingUp", "Rebasing", "ConnectionFailed"
            }));
        }
    }
}
