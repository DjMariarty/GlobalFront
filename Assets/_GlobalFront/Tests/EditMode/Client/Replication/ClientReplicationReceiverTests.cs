using System;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Client.Replication
{
    /// <summary>
    /// Client replication receiver: the facade that ties the Apply-Guard FSM,
    /// the client world table and the feedback generator together
    /// (Phase 2.6, step 2.6.3, ADR-010).
    ///
    /// The end-to-end property is the establishing-delta contract seen from the
    /// consuming side: a keyframe installs the base, every newer delta on that
    /// base moves the client world forward exactly (even across lost ticks), and
    /// anything the client cannot apply turns into the documented repair
    /// traffic instead of a silent desync. The last tests prove the whole path
    /// is allocation-free.
    /// </summary>
    [TestFixture]
    public sealed class ClientReplicationReceiverTests
    {
        private const ushort KeyframeSeq = 3;
        private const int UnitCount = 32;
        private const int WarmupIterations = 200;
        private const int MeasuredIterations = 1000;

        private static DeltaSnapshotHeader Header(
            ulong tick,
            ulong baseTick,
            int addCount = 0,
            int updateCount = 0,
            int removeCount = 0)
        {
            return DeltaSnapshotHeader.CreateDelta(
                tick, baseTick, DeltaFlags.None, 0,
                (ushort)addCount, (ushort)updateCount, (ushort)removeCount);
        }

        private static ClientReplicationReceiver BasedReceiver(
            int worldCapacity = 64,
            ulong baseTick = 100,
            ushort keyframeSeq = KeyframeSeq,
            params DeltaAddRecord[] units)
        {
            var receiver = new ClientReplicationReceiver(worldCapacity);
            var outcome = receiver.ReceiveKeyframe(0, keyframeSeq, baseTick, units);
            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(receiver.TryTakeAck(0, out _), Is.True, "the baseline ack is drained by the test setup");
            return receiver;
        }

        // ---------------------------------------------------------------
        // Keyframe baseline
        // ---------------------------------------------------------------

        [Test]
        public void ReceiveKeyframe_InstallsTheBaselineAndTheWholeWorld()
        {
            var receiver = new ClientReplicationReceiver(16);
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Unbased));
            Assert.That(receiver.IsWorldUsable, Is.False);

            var outcome = receiver.ReceiveKeyframe(
                500,
                KeyframeSeq,
                100,
                new[]
                {
                    ClientReplicationWorldTests.Add(1, owner: 2, posX: 10, posZ: 20, health: 90),
                    ClientReplicationWorldTests.Add(7, owner: 5, posX: -30, posZ: 40, health: 40,
                        hasMoveTarget: true, moveTargetX: 5000, moveTargetZ: 6000,
                        attackTarget: 1, autoAcquire: true)
                });

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(receiver.IsWorldUsable, Is.True);
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(100UL));
            Assert.That(receiver.BaseKeyframeTick, Is.EqualTo(100UL));
            Assert.That(receiver.CurrentKeyframeSeq, Is.EqualTo(KeyframeSeq));
            Assert.That(receiver.World.LiveCount, Is.EqualTo(2));
            Assert.That(receiver.KeyframeCount, Is.EqualTo(1));
            Assert.That(receiver.KeyframeUnitCount, Is.EqualTo(2),
                "keyframe units are counted separately from delta ADD records");
            Assert.That(receiver.TryGetUnit(new EntityId(7), out var unit), Is.True);
            Assert.That(unit.Owner, Is.EqualTo(new PlayerId(5)));
            Assert.That(unit.Position, Is.EqualTo(new WorldPointMm(-30, 40)));
            Assert.That(unit.MoveTarget, Is.EqualTo(new WorldPointMm(5000, 6000)));
            Assert.That(unit.AttackTarget, Is.EqualTo(new EntityId(1)));
            Assert.That(unit.AutoAcquire, Is.True);

            Assert.That(receiver.Feedback.HasPendingAck, Is.True, "installing the base is progress and must be acked");
            Assert.That(receiver.Feedback.PendingAck.LastAppliedTick, Is.EqualTo(100UL));
            Assert.That(receiver.Feedback.PendingAck.BaseKeyframeTick, Is.EqualTo(100UL));
        }

        [Test]
        public void ReceiveKeyframe_ReplacesThePreviousWorld()
        {
            var receiver = BasedReceiver(
                units: new[] { ClientReplicationWorldTests.Add(1), ClientReplicationWorldTests.Add(2) });
            var resetsBefore = receiver.World.ResetCount;

            var outcome = receiver.ReceiveKeyframe(
                1000, 9, 400, new[] { ClientReplicationWorldTests.Add(3) });

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(receiver.World.LiveCount, Is.EqualTo(1));
            Assert.That(receiver.World.Contains(new EntityId(1)), Is.False);
            Assert.That(receiver.World.Contains(new EntityId(3)), Is.True);
            Assert.That(receiver.CurrentKeyframeSeq, Is.EqualTo(9));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(400UL));
            Assert.That(receiver.World.ResetCount, Is.EqualTo(resetsBefore + 1),
                "re-baselining clears the table first");
        }

        [Test]
        public void ReceiveKeyframe_BeyondWorldCapacity_ReportsTheOverflow()
        {
            var receiver = new ClientReplicationReceiver(2);

            var outcome = receiver.ReceiveKeyframe(
                0, KeyframeSeq, 10,
                new[]
                {
                    ClientReplicationWorldTests.Add(1),
                    ClientReplicationWorldTests.Add(2),
                    ClientReplicationWorldTests.Add(3)
                });

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.CapacityExceeded));
            Assert.That(receiver.IsWorldUsable, Is.False);
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Rebasing),
                "an unusable world must be re-based, never rendered half-installed");
            Assert.That(receiver.InconsistentApplyCount, Is.EqualTo(1));
        }

        // ---------------------------------------------------------------
        // Delta application
        // ---------------------------------------------------------------

        [Test]
        public void ReceiveDelta_AppliesEverySectionInWireOrder()
        {
            var receiver = BasedReceiver(units: new[]
            {
                ClientReplicationWorldTests.Add(1, posX: 0, health: 100),
                ClientReplicationWorldTests.Add(2, posX: 0, health: 100)
            });

            var adds = new[] { ClientReplicationWorldTests.Add(3, owner: 4, posX: 77) };
            var updates = new[]
            {
                ClientReplicationWorldTests.Update(1, UnitDirtyMask.Position | UnitDirtyMask.Health, posX: 500, health: 25)
            };
            var removes = new[] { ClientReplicationWorldTests.Remove(2) };

            var outcome = receiver.ReceiveDelta(
                1000, Header(101, 100, 1, 1, 1), KeyframeSeq, adds, updates, removes);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.Applied));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(101UL));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(receiver.World.LiveCount, Is.EqualTo(2));
            Assert.That(receiver.TryGetUnit(new EntityId(1), out var moved), Is.True);
            Assert.That(moved.PosX, Is.EqualTo(500));
            Assert.That(moved.Health, Is.EqualTo(25));
            Assert.That(receiver.World.Contains(new EntityId(2)), Is.False);
            Assert.That(receiver.TryGetUnit(new EntityId(3), out var spawned), Is.True);
            Assert.That(spawned.Owner, Is.EqualTo(new PlayerId(4)));
            Assert.That(receiver.AppliedDeltaCount, Is.EqualTo(1));
            Assert.That(receiver.AppliedAddCount, Is.EqualTo(1));
            Assert.That(receiver.AppliedUpdateCount, Is.EqualTo(1));
            Assert.That(receiver.AppliedRemoveCount, Is.EqualTo(1));
            Assert.That(receiver.IsWorldUsable, Is.True);
        }

        [Test]
        public void ReceiveDelta_AcrossLostIntermediateTicks_Converges()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1, posX: 0) });

            // Ticks 101..119 were lost; the establishing delta of tick 120 is
            // based on the tick the client actually confirmed.
            var updates = new[]
            {
                ClientReplicationWorldTests.Update(1, UnitDirtyMask.Position, posX: 1200)
            };
            var outcome = receiver.ReceiveDelta(
                1000, Header(120, 100, 0, 1, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty, updates,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.Applied));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(120UL));
            Assert.That(receiver.TryGetUnit(new EntityId(1), out var unit), Is.True);
            Assert.That(unit.PosX, Is.EqualTo(1200), "skipping intermediate ticks is safe");
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(receiver.PendingRequestCount, Is.Zero, "no repair traffic is needed");
        }

        [Test]
        public void ReceiveDelta_StalePacket_LeavesTheWorldUntouched()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1, posX: 0) });
            var updates = new[] { ClientReplicationWorldTests.Update(1, UnitDirtyMask.Position, posX: 900) };
            Assert.That(
                receiver.ReceiveDelta(1000, Header(101, 100, 0, 1, 0), KeyframeSeq,
                    System.ReadOnlySpan<DeltaAddRecord>.Empty, updates,
                    System.ReadOnlySpan<DeltaRemoveRecord>.Empty),
                Is.EqualTo(ClientReplicationOutcome.Applied));
            Assert.That(receiver.TryTakeAck(1000, out _), Is.True, "the applied tick is acked and drained");

            var staleUpdates = new[] { ClientReplicationWorldTests.Update(1, UnitDirtyMask.Position, posX: -1) };
            var outcome = receiver.ReceiveDelta(2000, Header(101, 100, 0, 1, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty, staleUpdates,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.DroppedStale));
            Assert.That(receiver.StaleDropCount, Is.EqualTo(1));
            Assert.That(receiver.TryGetUnit(new EntityId(1), out var unit), Is.True);
            Assert.That(unit.PosX, Is.EqualTo(900), "a stale packet must never move a unit backwards");
            Assert.That(receiver.Feedback.HasPendingAck, Is.False, "a dropped duplicate is not progress");
        }

        [Test]
        public void ReceiveDelta_TombstoneEcho_IsAppliedIdempotently()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1) });
            var removes = new[] { ClientReplicationWorldTests.Remove(1) };
            Assert.That(
                receiver.ReceiveDelta(1000, Header(101, 100, 0, 0, 1), KeyframeSeq,
                    System.ReadOnlySpan<DeltaAddRecord>.Empty,
                    System.ReadOnlySpan<DeltaUpdateRecord>.Empty, removes),
                Is.EqualTo(ClientReplicationOutcome.Applied));

            // The echo horizon repeats the tombstone in the next packets.
            var echoed = new[] { ClientReplicationWorldTests.Remove(1) };
            var outcome = receiver.ReceiveDelta(1100, Header(102, 101, 0, 0, 1), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty,
                System.ReadOnlySpan<DeltaUpdateRecord>.Empty, echoed);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.Applied));
            Assert.That(receiver.World.LiveCount, Is.Zero);
            Assert.That(receiver.World.UnknownRemoveCount, Is.EqualTo(1));
            Assert.That(receiver.IsWorldUsable, Is.True);
        }

        [Test]
        public void ReceiveDelta_OD14Clear_PropagatesIntoTheClientWorld()
        {
            var receiver = BasedReceiver(units: new[]
            {
                ClientReplicationWorldTests.Add(1, hasMoveTarget: true, moveTargetX: 4000, moveTargetZ: 5000)
            });

            var updates = new[]
            {
                ClientReplicationWorldTests.Update(1,
                    UnitDirtyMask.Position | UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                    posX: 120, hasMoveTarget: false)
            };
            var outcome = receiver.ReceiveDelta(1000, Header(101, 100, 0, 1, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty, updates,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.Applied));
            Assert.That(receiver.TryGetUnit(new EntityId(1), out var unit), Is.True);
            Assert.That(unit.PosX, Is.EqualTo(120));
            Assert.That(unit.HasMoveTarget, Is.False);
            Assert.That(unit.MoveTarget, Is.EqualTo(new WorldPointMm(0, 0)));
        }

        // ---------------------------------------------------------------
        // Repair traffic
        // ---------------------------------------------------------------

        [Test]
        public void ReceiveDelta_DependencyGap_AsksForACumulativeDeltaAndReportsIt()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1, posX: 0) });

            var outcome = receiver.ReceiveDelta(
                1000, Header(150, 103, 0, 0, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty,
                System.ReadOnlySpan<DeltaUpdateRecord>.Empty,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.CatchUpRequested));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.CatchingUp));
            Assert.That(receiver.CatchUpCount, Is.EqualTo(1));
            Assert.That(receiver.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.DeltaResume));
            Assert.That(request.LastAppliedTick, Is.EqualTo(100UL));
            Assert.That(receiver.World.LiveCount, Is.EqualTo(1), "nothing was applied");
            Assert.That(receiver.Feedback.PendingAck.GapPresent, Is.True);
            Assert.That(receiver.Feedback.PendingAck.MissingBase, Is.EqualTo(101UL));
            Assert.That(receiver.Feedback.PendingAck.MissingBitmap, Is.EqualTo(0b111UL),
                "ticks 101, 102 and 103 are missing");
        }

        [Test]
        public void ReceiveDelta_CumulativeAnswer_RestoresStreamingAndClearsTheGap()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1, posX: 0) });
            receiver.ReceiveDelta(1000, Header(150, 103, 0, 0, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty,
                System.ReadOnlySpan<DeltaUpdateRecord>.Empty,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.CatchingUp));

            var updates = new[] { ClientReplicationWorldTests.Update(1, UnitDirtyMask.Position, posX: 321) };
            var outcome = receiver.ReceiveDelta(1400, Header(150, 100, 0, 1, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty, updates,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.Applied));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(150UL));
            Assert.That(receiver.TryGetUnit(new EntityId(1), out var unit), Is.True);
            Assert.That(unit.PosX, Is.EqualTo(321));
            Assert.That(receiver.Feedback.PendingAck.HasGap, Is.False, "the confirmed progress clears the gap");
            Assert.That(receiver.Feedback.PendingAck.LastAppliedTick, Is.EqualTo(150UL));
        }

        [Test]
        public void ReceiveDelta_GapBeyondTheWindow_AsksForAFreshKeyframe()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1) });

            var outcome = receiver.ReceiveDelta(1000, Header(900, 500, 0, 0, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty,
                System.ReadOnlySpan<DeltaUpdateRecord>.Empty,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.RebaseRequested));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(receiver.RebaseCount, Is.EqualTo(1));
            Assert.That(receiver.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));
            Assert.That(receiver.Feedback.PendingAck.BaseStale, Is.True);
            Assert.That(receiver.IsWorldUsable, Is.False, "the declared base is unusable until a keyframe lands");
        }

        [Test]
        public void ReceiveDelta_KeyframeMismatch_AsksForAFreshKeyframe()
        {
            var receiver = BasedReceiver(keyframeSeq: 3, units: new[] { ClientReplicationWorldTests.Add(1) });

            var outcome = receiver.ReceiveDelta(1000, Header(101, 100, 0, 0, 0), 4,
                System.ReadOnlySpan<DeltaAddRecord>.Empty,
                System.ReadOnlySpan<DeltaUpdateRecord>.Empty,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.RebaseRequested));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(receiver.KeyframeMismatchCount, Is.EqualTo(1));
            Assert.That(receiver.Feedback.PendingAck.BaseStale, Is.True);
        }

        [Test]
        public void ReceiveDelta_UpdateForUnknownEntity_ReportsAnInconsistentWorld()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1) });

            var updates = new[] { ClientReplicationWorldTests.Update(999, UnitDirtyMask.Health, health: 5) };
            var outcome = receiver.ReceiveDelta(1000, Header(101, 100, 0, 1, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty, updates,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.WorldInconsistent));
            Assert.That(receiver.IsWorldUsable, Is.False);
            Assert.That(receiver.WorldPartiallyApplied, Is.True);
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
            Assert.That(receiver.InconsistentApplyCount, Is.EqualTo(1));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(100UL), "a failed tick never advances the confirmed tick");
            Assert.That(receiver.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));
        }

        [Test]
        public void ReceiveDelta_WorldOverflow_ReportsCapacityWithoutTouchingTheWorld()
        {
            var receiver = BasedReceiver(worldCapacity: 1, units: new[] { ClientReplicationWorldTests.Add(1) });

            var adds = new[]
            {
                ClientReplicationWorldTests.Add(2),
                ClientReplicationWorldTests.Add(3)
            };
            var outcome = receiver.ReceiveDelta(1000, Header(101, 100, 2, 0, 0), KeyframeSeq,
                adds, System.ReadOnlySpan<DeltaUpdateRecord>.Empty,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.CapacityExceeded));
            Assert.That(receiver.World.LiveCount, Is.EqualTo(1), "the overflow is detected before any record is applied");
            Assert.That(receiver.World.Contains(new EntityId(2)), Is.False);
            Assert.That(receiver.IsWorldUsable, Is.False);
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Rebasing));
        }

        // ---------------------------------------------------------------
        // Payload validation
        // ---------------------------------------------------------------

        [Test]
        public void ReceiveDelta_WrongMessageType_IsRejected()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1) });
            var header = new DeltaSnapshotHeader(
                0x01, DeltaSnapshotProtocol.Version, 101, 100, 0, 1, 0, 0, 0, 0, 0);

            var outcome = receiver.ReceiveDelta(1000, header, KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty,
                System.ReadOnlySpan<DeltaUpdateRecord>.Empty,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.InvalidPayload));
            Assert.That(receiver.InvalidPayloadCount, Is.EqualTo(1));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
        }

        [Test]
        public void ReceiveDelta_UnsupportedDeltaVersion_IsRejected()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1) });
            var header = new DeltaSnapshotHeader(
                DeltaSnapshotProtocol.MessageTypeDelta, DeltaSnapshotProtocol.Version + 1,
                101, 100, 0, 1, 0, 0, 0, 0, 0);

            Assert.That(
                receiver.ReceiveDelta(1000, header, KeyframeSeq,
                    System.ReadOnlySpan<DeltaAddRecord>.Empty,
                    System.ReadOnlySpan<DeltaUpdateRecord>.Empty,
                    System.ReadOnlySpan<DeltaRemoveRecord>.Empty),
                Is.EqualTo(ClientReplicationOutcome.InvalidPayload));
        }

        [Test]
        public void ReceiveDelta_SinkShorterThanTheDeclaredCounts_IsRejected()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1) });

            var outcome = receiver.ReceiveDelta(
                1000, Header(101, 100, 0, 2, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty,
                new[] { ClientReplicationWorldTests.Update(1, UnitDirtyMask.Health, health: 1) },
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.InvalidPayload));
            Assert.That(receiver.InvalidPayloadCount, Is.EqualTo(1));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(100UL));
        }

        [Test]
        public void ReceiveDelta_MultipartTick_IsNotAppliedBeforeAssembly()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1, posX: 0) });
            var header = DeltaSnapshotHeader.CreateDeltaPart(
                101, 100, 0, 2, DeltaFlags.None, 0, 0, 1, 0);
            var updates = new[] { ClientReplicationWorldTests.Update(1, UnitDirtyMask.Position, posX: 55) };

            var outcome = receiver.ReceiveDelta(1000, header, KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty, updates,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.IncompleteMultipart));
            Assert.That(receiver.IncompleteMultipartCount, Is.EqualTo(1));
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(100UL),
                "LastAppliedTick moves only after every part of the tick arrived");
            Assert.That(receiver.TryGetUnit(new EntityId(1), out var unit), Is.True);
            Assert.That(unit.PosX, Is.Zero);
            Assert.That(receiver.Feedback.PendingAck.GapPresent, Is.True,
                "an incomplete assembly is an immediate feedback signal (R&D 4.3)");
        }

        [Test]
        public void ReceiveDelta_AfterTerminalFailure_IsRejected()
        {
            var receiver = new ClientReplicationReceiver(8);
            receiver.Update(0);
            receiver.Update(500);
            receiver.Update(1500);
            receiver.Update(3500);
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed));

            var outcome = receiver.ReceiveDelta(4000, Header(10, 0, 0, 0, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty,
                System.ReadOnlySpan<DeltaUpdateRecord>.Empty,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.RejectedTerminal));
            Assert.That(receiver.RejectedCount, Is.EqualTo(1));
            Assert.That(receiver.TryTakeRequest(out _), Is.False);
        }

        // ---------------------------------------------------------------
        // Pumping, feedback and reset
        // ---------------------------------------------------------------

        [Test]
        public void Update_DrivesTheBaselineRequestSchedule()
        {
            var receiver = new ClientReplicationReceiver(8);

            receiver.Update(0);
            Assert.That(receiver.TryTakeRequest(out var first), Is.True);
            Assert.That(first.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));
            Assert.That(first.Attempt, Is.EqualTo(1));

            receiver.Update(500);
            receiver.Update(1500);
            Assert.That(receiver.PendingRequestCount, Is.EqualTo(2));

            receiver.Update(3500);
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.ConnectionFailed));
            Assert.That(receiver.FailureReason, Is.EqualTo(ReplicationFailureReason.ResyncRequired));
            Assert.That(receiver.TotalRequestCount, Is.EqualTo(3));
        }

        [Test]
        public void TryTakeAck_PacesTheFeedbackAtTheBaseCadence()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1, posX: 0) });
            var updates = new[] { ClientReplicationWorldTests.Update(1, UnitDirtyMask.Position, posX: 1) };

            receiver.ReceiveDelta(1000, Header(101, 100, 0, 1, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty, updates,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);
            Assert.That(receiver.TryTakeAck(1000, out var ack), Is.True);
            Assert.That(ack.LastAppliedTick, Is.EqualTo(101UL));
            Assert.That(ack.MessageType, Is.EqualTo(0x05));

            var nextUpdates = new[] { ClientReplicationWorldTests.Update(1, UnitDirtyMask.Position, posX: 2) };
            receiver.ReceiveDelta(1050, Header(102, 101, 0, 1, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty, nextUpdates,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);
            Assert.That(receiver.TryTakeAck(1050, out _), Is.False, "10 Hz floor");
            Assert.That(receiver.TryTakeAck(1100, out var paced), Is.True);
            Assert.That(paced.LastAppliedTick, Is.EqualTo(102UL));
        }

        [Test]
        public void Reset_ClearsWorldStateMachineAndFeedback()
        {
            var receiver = BasedReceiver(units: new[] { ClientReplicationWorldTests.Add(1) });
            receiver.ReceiveDelta(1000, Header(900, 500, 0, 0, 0), KeyframeSeq,
                System.ReadOnlySpan<DeltaAddRecord>.Empty,
                System.ReadOnlySpan<DeltaUpdateRecord>.Empty,
                System.ReadOnlySpan<DeltaRemoveRecord>.Empty);

            receiver.Reset();

            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Unbased));
            Assert.That(receiver.World.LiveCount, Is.Zero);
            Assert.That(receiver.LastAppliedTick, Is.Zero);
            Assert.That(receiver.CurrentKeyframeSeq, Is.Zero);
            Assert.That(receiver.IsWorldUsable, Is.False);
            Assert.That(receiver.Feedback.HasPendingAck, Is.False);
            Assert.That(receiver.PendingRequestCount, Is.Zero);
            Assert.That(receiver.AppliedDeltaCount, Is.Zero);

            receiver.Update(0);
            Assert.That(receiver.TryTakeRequest(out var request), Is.True);
            Assert.That(request.Kind, Is.EqualTo(ReplicationRequestKind.SnapshotRequest));
        }

        [Test]
        public void Constructor_RejectsInvalidArguments()
        {
            Assert.That(() => new ClientReplicationReceiver(0),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new ClientReplicationReceiver(8, ReplicationReceiverConfig.Default, 0),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void DefaultConstructor_UsesTheDocumentedDefaults()
        {
            var receiver = new ClientReplicationReceiver();

            Assert.That(receiver.World.Capacity, Is.EqualTo(ClientReplicationWorld.DefaultCapacity));
            Assert.That(receiver.Fsm.Config.WindowTicks, Is.EqualTo(120));
            Assert.That(receiver.Feedback.MinIntervalMs, Is.EqualTo(100L));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Unbased));
        }

        // ---------------------------------------------------------------
        // Zero-GC hot path
        // ---------------------------------------------------------------

        [Test]
        public void GcAllocationRecorder_ObservesKnownAllocation_Control()
        {
            // The editor's Mono runtime reports 0 from
            // GC.GetAllocatedBytesForCurrentThread(), so zero-GC evidence uses
            // Unity's GC.Alloc recorder. This control proves the recorder works.
            byte[] ballast = null;

            Assert.That(
                () =>
                {
                    ballast = new byte[4096];
                },
                UnityEngine.TestTools.Constraints.Is.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");

            Assert.That(ballast, Is.Not.Null);
            Assert.That(ballast.Length, Is.EqualTo(4096));
        }

        [Test]
        public void Receiver_DeltaStreamHotPath_DoesNotAllocate()
        {
            var receiver = new ClientReplicationReceiver(UnitCount * 2);
            var baseline = new DeltaAddRecord[UnitCount];
            for (var index = 0; index < UnitCount; index++)
            {
                baseline[index] = ClientReplicationWorldTests.Add((ulong)(index + 1), posX: index * 10);
            }

            Assert.That(receiver.ReceiveKeyframe(0, KeyframeSeq, 1, baseline),
                Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));

            // Everything the hot path touches is preallocated: record buffers,
            // the request/ack sinks and the encode buffer.
            var updates = new DeltaUpdateRecord[UnitCount];
            var adds = new DeltaAddRecord[UnitCount];
            var removes = new DeltaRemoveRecord[UnitCount];
            var ackBuffer = new byte[SnapshotAckCodec.SizeBytes];
            var applied = 0;
            var acked = 0;
            var tick = 1UL;

            for (var iteration = 0; iteration < WarmupIterations; iteration++)
            {
                tick = RunIteration(receiver, tick, iteration, updates, adds, removes, ackBuffer, ref applied, ref acked);
            }

            // Block-bodied lambda on purpose: the negated constraint only receives
            // the delegate itself when NUnit binds Assert.That(TestDelegate, ...).
            Assert.That(
                () =>
                {
                    for (var iteration = 0; iteration < MeasuredIterations; iteration++)
                    {
                        tick = RunIteration(
                            receiver, tick, WarmupIterations + iteration,
                            updates, adds, removes, ackBuffer, ref applied, ref acked);
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "applying a continuous delta stream to the client world must be allocation-free");

            Assert.That(applied, Is.EqualTo(WarmupIterations + MeasuredIterations));
            Assert.That(acked, Is.EqualTo(WarmupIterations + MeasuredIterations),
                "every applied replication tick produced one paced ack");
            Assert.That(receiver.LastAppliedTick, Is.EqualTo(tick));
            Assert.That(receiver.World.LiveCount, Is.EqualTo(UnitCount));
            Assert.That(receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
        }

        [Test]
        public void Receiver_CatchUpAndRebasePath_DoesNotAllocate()
        {
            var receiver = new ClientReplicationReceiver(64);
            var baseline = new[] { ClientReplicationWorldTests.Add(1, posX: 0) };
            Assert.That(receiver.ReceiveKeyframe(0, KeyframeSeq, 10, baseline),
                Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));

            var requests = 0;
            var nowMs = 0L;

            for (var iteration = 0; iteration < WarmupIterations; iteration++)
            {
                nowMs = RepairOnce(receiver, nowMs, iteration, ref requests);
            }

            Assert.That(
                () =>
                {
                    for (var iteration = 0; iteration < MeasuredIterations; iteration++)
                    {
                        nowMs = RepairOnce(receiver, nowMs, WarmupIterations + iteration, ref requests);
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "the repair path (gap detection, request queueing, feedback) must be allocation-free");

            Assert.That(requests, Is.GreaterThan(0));
            Assert.That(receiver.CatchUpCount + receiver.RebaseCount, Is.GreaterThan(0));
        }

        /// <summary>
        /// One measured hot-path iteration: rewrite the update section, apply the
        /// tick, drain the paced ack into a pooled buffer.
        /// </summary>
        private static ulong RunIteration(
            ClientReplicationReceiver receiver,
            ulong previousTick,
            int iteration,
            DeltaUpdateRecord[] updates,
            DeltaAddRecord[] adds,
            DeltaRemoveRecord[] removes,
            byte[] ackBuffer,
            ref int applied,
            ref int acked)
        {
            for (var index = 0; index < updates.Length; index++)
            {
                updates[index] = ClientReplicationWorldTests.Update(
                    (ulong)(index + 1),
                    UnitDirtyMask.Position | UnitDirtyMask.Health,
                    posX: (index * 10) + iteration,
                    posZ: iteration,
                    health: 100 - (iteration % 50));
            }

            var tick = previousTick + 1;
            var nowMs = (long)tick * 100;
            var header = Header(tick, previousTick, 0, updates.Length, 0);
            if (receiver.ReceiveDelta(nowMs, header, KeyframeSeq, adds.AsSpan(0, 0), updates, removes.AsSpan(0, 0))
                == ClientReplicationOutcome.Applied)
            {
                applied++;
            }

            if (receiver.TryTakeAck(nowMs, out var ack) &&
                SnapshotAckCodec.TryEncode(ack, ackBuffer, out var written) == SnapshotAckCodecResult.Ok &&
                written == SnapshotAckCodec.SizeBytes)
            {
                acked++;
            }

            return tick;
        }

        /// <summary>
        /// One measured repair iteration: feed a delta whose base is ahead, drain
        /// the queued repair request and the immediate feedback signal.
        /// </summary>
        private static long RepairOnce(
            ClientReplicationReceiver receiver,
            long nowMs,
            int iteration,
            ref int requests)
        {
            var moment = nowMs + 100;
            var adds = ReadOnlySpan<DeltaAddRecord>.Empty;
            var updates = ReadOnlySpan<DeltaUpdateRecord>.Empty;
            var removes = ReadOnlySpan<DeltaRemoveRecord>.Empty;

            // A dependency gap of five ticks inside the window: CATCHING_UP.
            receiver.ReceiveDelta(moment, Header(500 + (ulong)iteration, receiver.LastAppliedTick + 5, 0, 0, 0),
                KeyframeSeq, adds, updates, removes);
            while (receiver.TryTakeRequest(out _))
            {
                requests++;
            }

            receiver.TryTakeAck(moment, out _);

            // A cumulative answer restores STREAMING so the next iteration can
            // exercise the same path again.
            receiver.ReceiveDelta(moment + 1, Header(500 + (ulong)iteration, receiver.LastAppliedTick, 0, 0, 0),
                KeyframeSeq, adds, updates, removes);
            receiver.TryTakeAck(moment + 2, out _);

            return moment + 2;
        }
    }
}
