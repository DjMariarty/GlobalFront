using System;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Client.Presentation
{
    /// <summary>
    /// Phase 3, step 3.2: the presentation interpolator of replicated unit state
    /// (<see cref="UnitViewTickBuffer"/>, the OD-23 design as corrected by the
    /// step 3.1 review — P0-2 sparse ring, P0-3 play-out delay, P0-4 heading).
    ///
    /// These tests defend the properties a 20 Hz simulation fed by 10 Hz
    /// replication (degrading to 5 Hz under OD-12) cannot get wrong silently: a
    /// sparse ring must <i>find</i> its bracket samples instead of computing them,
    /// one sample must never reach a division, a late retransmission must not
    /// overwrite a live anchor, starvation must clamp toward the authoritative move
    /// target and then park rather than freeze or teleport, and heading must ignore
    /// formation micro-jitter. The allocation evidence uses the project's GC.Alloc
    /// recorder pattern, because the editor's Mono runtime reports 0 from
    /// GC.GetAllocatedBytesForCurrentThread().
    /// </summary>
    [TestFixture]
    public sealed class UnitViewTickBufferTests
    {
        private const int SlotCapacity = 64;
        private const double TickSeconds = 0.05;           // 20 Hz simulation
        private const double FrameSeconds = 1.0 / 60.0;    // 60 fps presentation
        private const double PacketSeconds = 0.1;          // 10 Hz replication
        private const int FramesPerPacket = 6;
        private const int StepPerTickMm = 350;             // PrototypeUnit contract
        private const int StepPerPacketMm = StepPerTickMm * 2;
        private const int BudgetMm = UnitViewTickBuffer.MaxExtrapolationTicks * StepPerTickMm;
        private const ulong SingleEntity = 1;

        private static DeltaAddRecord AddRecord(
            int posX,
            int posZ,
            int health = 100,
            bool hasMoveTarget = false,
            int moveTargetX = 0,
            int moveTargetZ = 0,
            ulong attackTarget = 0,
            byte unitKind = UnitKinds.Unknown)
        {
            return new DeltaAddRecord(
                new EntityId(SingleEntity),
                new PlayerId(1),
                new WorldPointMm(posX, posZ),
                health,
                hasMoveTarget,
                new WorldPointMm(moveTargetX, moveTargetZ),
                new EntityId(attackTarget),
                false,
                unitKind);
        }

        private static DeltaUpdateRecord MoveTo(int posX, int posZ)
        {
            return new DeltaUpdateRecord(
                new EntityId(SingleEntity),
                (byte)UnitDirtyMask.Position,
                new PlayerId(1),
                new WorldPointMm(posX, posZ),
                0,
                false,
                new WorldPointMm(0, 0),
                new EntityId(0),
                false);
        }

        private static ClientReplicationWorld NewWorld(in DeltaAddRecord add)
        {
            var world = new ClientReplicationWorld(SlotCapacity);
            Assert.That(world.ApplyAdd(add), Is.EqualTo(ClientWorldApplyResult.Ok));
            return world;
        }

        private static void AssertFinite(in UnitViewPose pose)
        {
            Assert.That(float.IsNaN(pose.XMillimetres) || float.IsInfinity(pose.XMillimetres), Is.False, "X");
            Assert.That(float.IsNaN(pose.ZMillimetres) || float.IsInfinity(pose.ZMillimetres), Is.False, "Z");
            Assert.That(float.IsNaN(pose.HealthFraction), Is.False, "health fraction");
            Assert.That(float.IsNaN(pose.BodyYawDegrees), Is.False, "body yaw");
            Assert.That(float.IsNaN(pose.TurretYawDegrees), Is.False, "turret yaw");
        }

        [Test]
        public void Interpolation_10HzCadence_ProducesSmoothTrajectory()
        {
            var world = NewWorld(AddRecord(0, 0));
            var buffer = new UnitViewTickBuffer(SlotCapacity);
            var positions = new float[512];
            var count = 0;
            var interpolated = 0;
            var authorityX = 0;

            for (var packet = 0; packet < 24; packet++)
            {
                authorityX = packet * StepPerPacketMm;
                Assert.That(
                    world.ApplyUpdate(MoveTo(authorityX, 0)),
                    Is.EqualTo(ClientWorldApplyResult.Ok));

                // Sparse input: only even ticks exist, exactly the 10 Hz case where
                // the neighbour of tick 8 is tick 10 and tick 9 was never captured.
                buffer.CaptureTick((ulong)(2 + packet * 2), world);

                for (var frame = 0; frame < FramesPerPacket; frame++)
                {
                    buffer.Advance(FrameSeconds);
                    Assert.That(buffer.TrySample(0, out var pose), Is.True);
                    AssertFinite(in pose);

                    if (packet >= 4)
                    {
                        positions[count++] = pose.XMillimetres;
                        if (pose.Source == UnitViewPoseSource.Interpolated)
                        {
                            interpolated++;
                        }
                    }
                }
            }

            Assert.That(count, Is.GreaterThan(100));
            var totalTravel = positions[count - 1] - positions[0];
            Assert.That(totalTravel, Is.GreaterThan(0f));

            var largestStep = 0f;
            var previous = positions[0];
            for (var index = 1; index < count; index++)
            {
                var delta = positions[index] - previous;
                previous = positions[index];
                Assert.That(delta, Is.GreaterThanOrEqualTo(-1f), "the render clock must not run backwards");
                if (delta > largestStep)
                {
                    largestStep = delta;
                }
            }

            var averageStep = totalTravel / (count - 1);

            // Unsmoothed presentation would move one whole 700 mm packet inside a
            // single frame and nothing in the five frames around it, which puts the
            // max/mean ratio near 6. A bracket keeps it close to 1, so this ratio is
            // the staircase detector.
            Assert.That(
                largestStep / averageStep,
                Is.LessThan(2f),
                "presentation must not deliver authority as a staircase");
            Assert.That(
                (float)interpolated / count,
                Is.GreaterThan(0.9f),
                "a healthy 10 Hz feed must interpolate, not snap or hold");

            // The play-out delay is a deliberate, bounded margin behind authority.
            var lag = authorityX - positions[count - 1];
            Assert.That(lag, Is.GreaterThanOrEqualTo(0f));
            Assert.That(lag, Is.LessThanOrEqualTo(3 * StepPerPacketMm));
        }

        [Test]
        public void SafeAlpha_SingleSample_SnapsWithoutNaN()
        {
            var world = NewWorld(AddRecord(1000, 2000));
            var buffer = new UnitViewTickBuffer(SlotCapacity);
            buffer.CaptureTick(10, world);

            // Below the only sample the ring holds: one side of the bracket exists,
            // so alpha must never be evaluated and the pose must be an exact copy.
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var waiting), Is.True);
            AssertFinite(in waiting);
            Assert.That(waiting.Source, Is.EqualTo(UnitViewPoseSource.Snapped));
            Assert.That(waiting.XMillimetres, Is.EqualTo(1000f));
            Assert.That(waiting.ZMillimetres, Is.EqualTo(2000f));

            var starved = default(UnitViewPose);
            for (var frame = 0; frame < 40; frame++)
            {
                buffer.Advance(TickSeconds * 2);
                Assert.That(buffer.TrySample(0, out starved), Is.True);
                AssertFinite(in starved);
                Assert.That(
                    starved.ZMillimetres - 2000f,
                    Is.InRange(0f, BudgetMm + 1f),
                    "unbounded run-out is the teleport this path replaces");
            }

            Assert.That(starved.Source, Is.EqualTo(UnitViewPoseSource.Held));
            Assert.That(starved.ZMillimetres, Is.EqualTo(2000f + BudgetMm).Within(0.001f));
            Assert.That(buffer.ReprimeCount, Is.EqualTo(0));
        }

        [Test]
        public void InvMaxHealth_ZeroHealth_NoNaN()
        {
            var world = NewWorld(AddRecord(0, 0, health: 50));
            var buffer = new UnitViewTickBuffer(SlotCapacity);
            buffer.CaptureTick(10, world);
            buffer.Advance(TickSeconds);

            // An unresolved stat reads as an empty bar, never as NaN. This is the
            // exact shape of the PrototypeUnit.MaximumHealth ?? 0 defect.
            Assert.That(buffer.TrySample(0, out var unresolved), Is.True);
            Assert.That(unresolved.HealthFraction, Is.EqualTo(0f));
            Assert.That(unresolved.Health, Is.EqualTo(50), "raw authority survives");

            buffer.SetSlotMaximumHealth(0, 200);
            Assert.That(buffer.TrySample(0, out var scaled), Is.True);
            Assert.That(scaled.HealthFraction, Is.EqualTo(0.25f).Within(1e-6f));

            buffer.SetSlotMaximumHealth(0, 0);
            Assert.That(buffer.TrySample(0, out var zeroMaximum), Is.True);
            Assert.That(zeroMaximum.HealthFraction, Is.EqualTo(0f));

            buffer.SetSlotMaximumHealth(0, -5);
            Assert.That(buffer.TrySample(0, out var negative), Is.True);
            Assert.That(negative.HealthFraction, Is.EqualTo(0f));

            Assert.That(
                world.ApplyUpdate(new DeltaUpdateRecord(
                    new EntityId(SingleEntity),
                    (byte)UnitDirtyMask.Health,
                    new PlayerId(1),
                    new WorldPointMm(0, 0),
                    600,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            buffer.CaptureTick(12, world);
            buffer.SetSlotMaximumHealth(0, 100);

            // The render clock sits a play-out delay behind authority, so the fresh
            // 600 is not visible yet: presentation must never leak a future value
            // into a frame that authority has not reached.
            buffer.Advance(TickSeconds);
            Assert.That(buffer.RenderTick, Is.LessThan(12d));
            Assert.That(buffer.TrySample(0, out var notYet), Is.True);
            Assert.That(notYet.Health, Is.EqualTo(50), "no future state before its tick");
            Assert.That(notYet.HealthFraction, Is.EqualTo(0.5f).Within(1e-6f));

            for (var frame = 0; frame < 20; frame++)
            {
                buffer.Advance(FrameSeconds * 4);
            }

            Assert.That(buffer.TrySample(0, out var overflowing), Is.True);
            AssertFinite(in overflowing);
            Assert.That(overflowing.Health, Is.EqualTo(600));
            Assert.That(overflowing.HealthFraction, Is.EqualTo(1f), "the fraction stays inside [0,1]");
        }

        [Test]
        public void StalePacket_DroppedSafely()
        {
            var world = NewWorld(AddRecord(0, 0));
            var buffer = new UnitViewTickBuffer(SlotCapacity);

            for (var packet = 0; packet < 7; packet++)
            {
                world.ApplyUpdate(MoveTo(packet * StepPerPacketMm, 0));
                buffer.CaptureTick((ulong)(10 + packet * 2), world);
            }

            Assert.That(buffer.CapturedTick, Is.EqualTo(22UL));
            Assert.That(buffer.StalePacketCount, Is.EqualTo(0));

            buffer.CaptureTick(20, world);    // late retransmission
            buffer.CaptureTick(22, world);    // duplicate of the newest tick
            Assert.That(buffer.CapturedTick, Is.EqualTo(22UL));
            Assert.That(buffer.StalePacketCount, Is.EqualTo(2));

            // Tick 0 is the ring's empty sentinel rather than network staleness: it
            // is refused silently and must not pollute the degradation counter.
            buffer.CaptureTick(0, world);
            Assert.That(buffer.CapturedTick, Is.EqualTo(22UL));
            Assert.That(buffer.StalePacketCount, Is.EqualTo(2));
            Assert.That(buffer.ReprimeCount, Is.EqualTo(0), "a duplicate is not a ring gap");

            // The aliasing case ring depth alone cannot catch: a full-ring gap must
            // re-prime, and a packet from the far side of that gap must not be
            // allowed back into the cell that anchors every other unit.
            buffer.CaptureTick(54, world);
            Assert.That(buffer.ReprimeCount, Is.EqualTo(1));
            Assert.That(buffer.CapturedTick, Is.EqualTo(54UL));
            Assert.That(buffer.HasHistory(0), Is.True);

            buffer.CaptureTick(22, world);
            Assert.That(buffer.StalePacketCount, Is.EqualTo(3));
            Assert.That(buffer.CapturedTick, Is.EqualTo(54UL));

            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var pose), Is.True);
            AssertFinite(in pose);
            Assert.That(pose.Source, Is.EqualTo(UnitViewPoseSource.Snapped));
            Assert.That(pose.XMillimetres, Is.EqualTo(6 * StepPerPacketMm),
                "the re-primed ring must show the new authority, not a stale blend");
        }

        [Test]
        public void Extrapolation_ClampedTowardsMoveTarget()
        {
            const int targetX = 100_000;
            var world = NewWorld(AddRecord(
                0, 0, hasMoveTarget: true, moveTargetX: targetX));
            var buffer = new UnitViewTickBuffer(SlotCapacity);
            buffer.CaptureTick(10, world);

            var seenExtrapolated = false;
            var seenHeld = false;
            var lastX = 0f;
            for (var frame = 0; frame < 60; frame++)
            {
                buffer.Advance(FrameSeconds * 4);
                Assert.That(buffer.TrySample(0, out var pose), Is.True);
                AssertFinite(in pose);

                Assert.That(
                    pose.XMillimetres,
                    Is.GreaterThanOrEqualTo(lastX - 0.001f),
                    "the reach must not stutter backwards");
                Assert.That(
                    pose.XMillimetres,
                    Is.LessThanOrEqualTo(BudgetMm + 0.001f),
                    "extrapolation is bounded by the tick budget, not by wishful thinking");
                Assert.That(
                    pose.XMillimetres,
                    Is.LessThan(targetX),
                    "a clamped reach never runs past the authoritative destination");
                lastX = pose.XMillimetres;

                seenExtrapolated |= pose.Source == UnitViewPoseSource.Extrapolated;
                seenHeld |= pose.Source == UnitViewPoseSource.Held;
            }

            Assert.That(seenExtrapolated, Is.True, "one lost packet must be covered smoothly");
            Assert.That(seenHeld, Is.True, "sustained starvation must hold instead of teleporting");
            Assert.That(buffer.StarvedSampleCount, Is.GreaterThan(0));
            Assert.That(lastX, Is.EqualTo((float)BudgetMm).Within(0.001f));

            for (var frame = 0; frame < 30; frame++)
            {
                buffer.Advance(FrameSeconds * 4);
                Assert.That(buffer.TrySample(0, out var held), Is.True);
                Assert.That(held.XMillimetres, Is.EqualTo(lastX).Within(0.001f),
                    "a parked clock freezes the pose instead of snapping it back");
            }

            // Recovery: the next authoritative packet must take over exactly where
            // the clamped reach already put the unit, not with a jump.
            world.ApplyUpdate(MoveTo(BudgetMm, 0));
            buffer.CaptureTick(12, world);
            Assert.That(buffer.TrySample(0, out var recovered), Is.True);
            AssertFinite(in recovered);
            Assert.That(recovered.Source, Is.EqualTo(UnitViewPoseSource.Snapped));
            Assert.That(
                recovered.XMillimetres,
                Is.EqualTo(lastX).Within(1f),
                "the reach predicted the authority position, so recovery must be invisible");

            for (var frame = 0; frame < FramesPerPacket; frame++)
            {
                buffer.Advance(FrameSeconds);
                Assert.That(buffer.TrySample(0, out var resuming), Is.True);
                AssertFinite(in resuming);
                Assert.That(resuming.XMillimetres, Is.GreaterThanOrEqualTo(lastX - 1f));
                Assert.That(resuming.XMillimetres, Is.LessThanOrEqualTo(lastX + BudgetMm + 1f));
            }
        }

        [Test]
        public void Yaw_Deadzone_PreventsJitterWhenIdle()
        {
            var world = NewWorld(AddRecord(100_000, 100_000));
            var buffer = new UnitViewTickBuffer(SlotCapacity);
            buffer.CaptureTick(10, world);

            for (var frame = 0; frame < 30; frame++)
            {
                buffer.Advance(FrameSeconds);
            }

            // Sub-deadzone displacement: a unit standing in formation twitches by
            // millimetres, and heading derived from that spins it in place.
            var jitter = new[]
            {
                (100_004, 100_003),
                (99_996, 99_998),
                (100_001, 100_005),
            };

            for (var index = 0; index < jitter.Length; index++)
            {
                world.ApplyUpdate(MoveTo(jitter[index].Item1, jitter[index].Item2));
                buffer.CaptureTick((ulong)(12 + index * 2), world);
                for (var frame = 0; frame < 30; frame++)
                {
                    buffer.Advance(FrameSeconds);
                }

                Assert.That(buffer.TrySample(0, out var idle), Is.True);
                AssertFinite(in idle);
                Assert.That(idle.BodyYawDegrees, Is.EqualTo(0f).Within(1e-4f),
                    "micro-jitter must hold the previous heading");
            }

            // A genuine displacement must still turn the hull, so the deadzone is a
            // filter and not a lock.
            world.ApplyUpdate(MoveTo(104_000, 100_000));
            var reached = 0f;
            for (var packet = 0; packet < 5; packet++)
            {
                buffer.CaptureTick((ulong)(18 + packet * 2), world);
                for (var frame = 0; frame < FramesPerPacket; frame++)
                {
                    buffer.Advance(FrameSeconds);
                    Assert.That(buffer.TrySample(0, out var moving), Is.True);
                    reached = Math.Max(reached, moving.BodyYawDegrees);
                }
            }

            for (var frame = 0; frame < 120; frame++)
            {
                buffer.Advance(FrameSeconds);
                Assert.That(buffer.TrySample(0, out var settling), Is.True);
                AssertFinite(in settling);
                reached = Math.Max(reached, settling.BodyYawDegrees);
            }

            Assert.That(reached, Is.GreaterThan(45f), "genuine movement still changes heading");
        }

        [Test]
        public void FlushAndResync_ClearsHistoryCleanly()
        {
            var world = NewWorld(AddRecord(0, 0));
            var buffer = new UnitViewTickBuffer(SlotCapacity);

            for (var packet = 0; packet < 10; packet++)
            {
                world.ApplyUpdate(MoveTo(packet * StepPerPacketMm, 0));
                buffer.CaptureTick((ulong)(2 + packet * 2), world);
                buffer.Advance(PacketSeconds);
            }

            Assert.That(buffer.TrySample(0, out var before), Is.True);
            AssertFinite(in before);
            Assert.That(before.XMillimetres, Is.LessThan(10 * StepPerPacketMm));

            // The OD-18 / OD-20 discontinuity: history from before the pause may
            // never bracket against history after it.
            buffer.Resync(1000);
            Assert.That(buffer.CapturedTick, Is.EqualTo(0UL));
            Assert.That(buffer.HasHistory(0), Is.False);
            Assert.That(buffer.TrySample(0, out _), Is.False, "a flushed ring yields no pose");
            Assert.That(
                buffer.RenderTick,
                Is.EqualTo(1000 - UnitViewTickBuffer.MinRenderDelayTicks).Within(1e-9),
                "the clock restarts one play-out delay behind the new base");

            buffer.CaptureTick(500, world);
            Assert.That(buffer.StalePacketCount, Is.EqualTo(1), "pre-resync traffic is refused");
            Assert.That(buffer.CapturedTick, Is.EqualTo(0UL));

            const int newBaseX = 5_000_000;
            world.ApplyUpdate(MoveTo(newBaseX, 0));
            buffer.CaptureTick(1000, world);
            Assert.That(buffer.TrySample(0, out var primed), Is.True);
            AssertFinite(in primed);
            Assert.That(primed.Source, Is.EqualTo(UnitViewPoseSource.Snapped));
            Assert.That(primed.XMillimetres, Is.EqualTo(newBaseX),
                "the first pose after a resync is the new base, exactly");

            for (var packet = 1; packet < 6; packet++)
            {
                world.ApplyUpdate(MoveTo(newBaseX + packet * StepPerPacketMm, 0));
                buffer.CaptureTick((ulong)(1000 + packet * 2), world);
                for (var frame = 0; frame < FramesPerPacket; frame++)
                {
                    buffer.Advance(FrameSeconds);
                    Assert.That(buffer.TrySample(0, out var after), Is.True);
                    AssertFinite(in after);
                    Assert.That(
                        after.XMillimetres,
                        Is.GreaterThanOrEqualTo(newBaseX - 1f),
                        "nothing may blend across the resync gap");
                }
            }

            // A plain Flush behaves the same way without a new base.
            buffer.Flush();
            Assert.That(buffer.TrySample(0, out _), Is.False);
            Assert.That(buffer.RenderDelayTicks, Is.EqualTo(UnitViewTickBuffer.MinRenderDelayTicks));
            Assert.That(buffer.ObservedIntervalTicks, Is.EqualTo(0d), "the cadence estimate restarts");
        }

        [Test]
        public void ReplicatedUnitKind_ResolvesTheHealthDenominator()
        {
            // Distinct maxima per archetype, so the assertion can only pass if the
            // number came from the replicated kind and not from a constant.
            var catalog = new UnitCatalog(new[]
            {
                new UnitDefinition(UnitKinds.Scout, "Scout", 100, 500),
                new UnitDefinition(UnitKinds.Tank, "Tank", 400, 900),
            });

            var world = NewWorld(AddRecord(0, 0, health: 50, unitKind: UnitKinds.Tank));
            var buffer = new UnitViewTickBuffer(SlotCapacity, catalog: catalog);

            // No SetSlotMaximumHealth call: OD-29 put the kind on the wire, so the
            // bar denominator now resolves from the record itself.
            buffer.CaptureTick(10, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var tank), Is.True);
            Assert.That(tank.Health, Is.EqualTo(50));
            Assert.That(tank.HealthFraction, Is.EqualTo(0.125f).Within(1e-6f));

            // An Unknown archetype stays unresolved rather than borrowing a row.
            var legacyWorld = NewWorld(AddRecord(0, 0, health: 50));
            var legacyBuffer = new UnitViewTickBuffer(SlotCapacity, catalog: catalog);
            legacyBuffer.CaptureTick(10, legacyWorld);
            legacyBuffer.Advance(TickSeconds);
            Assert.That(legacyBuffer.TrySample(0, out var legacy), Is.True);
            Assert.That(legacy.HealthFraction, Is.EqualTo(0f));
            Assert.That(float.IsNaN(legacy.HealthFraction), Is.False);

            // An explicit override still wins, and only until the slot is recycled.
            buffer.SetSlotMaximumHealth(0, 50);
            Assert.That(buffer.TrySample(0, out var overridden), Is.True);
            Assert.That(overridden.HealthFraction, Is.EqualTo(1f));

            // Slot recycling: the table hands a freed slot to the next unit, and the
            // denominator must follow that unit's kind instead of its predecessor's.
            Assert.That(
                world.ApplyRemove(new DeltaRemoveRecord(
                    new EntityId(SingleEntity), DeltaRemoveCause.Destroyed)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(
                world.ApplyAdd(new DeltaAddRecord(
                    new EntityId(99),
                    new PlayerId(2),
                    new WorldPointMm(10, 10),
                    50,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false,
                    UnitKinds.Scout)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            buffer.CaptureTick(12, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var recycled), Is.True);
            Assert.That(recycled.HealthFraction, Is.EqualTo(0.5f).Within(1e-6f),
                "a recycled slot must re-resolve from the new unit's archetype");
        }

        [Test]
        public void ZeroGC_SustainedOperation()
        {
            var world = new ClientReplicationWorld(SlotCapacity);
            Assert.That(world.ApplyAdd(AddRecord(0, 0)), Is.EqualTo(ClientWorldApplyResult.Ok));
            for (var id = 2; id <= 8; id++)
            {
                Assert.That(
                    world.ApplyAdd(new DeltaAddRecord(
                        new EntityId((ulong)id),
                        new PlayerId(1),
                        new WorldPointMm(id * 1000, 0),
                        100,
                        false,
                        new WorldPointMm(0, 0),
                        new EntityId(0),
                        false)),
                    Is.EqualTo(ClientWorldApplyResult.Ok));
            }

            var buffer = new UnitViewTickBuffer(SlotCapacity);

            // Control first: the recorder must be proven able to see an allocation
            // before its silence means anything.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                UnityEngine.TestTools.Constraints.Is.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            void Drive(int iterations, ulong startTick)
            {
                var tick = startTick;
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    if ((iteration & 1) == 0)
                    {
                        world.ApplyUpdate(MoveTo((int)(tick * 10), 0));
                        buffer.CaptureTick(tick, world);
                        tick += 2;
                    }

                    buffer.Advance(FrameSeconds);
                    for (var slot = 0; slot < SlotCapacity; slot++)
                    {
                        buffer.TrySample(slot, out _);
                    }
                }
            }

            // Warmup: first-touch pages, estimator convergence, nothing lazy left.
            // The measured run continues the same 10 Hz cadence, so no ring gap
            // takes the re-prime path inside the measurement window.
            Drive(200, 2);
            Assert.That(
                () => Drive(1000, 202),
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "the presentation hot path must be allocation-free");

            Assert.That(buffer.CapturedTick, Is.GreaterThan(200UL), "the measured run did capture ticks");
            Assert.That(buffer.ReprimeCount, Is.EqualTo(0), "the cadence stayed continuous");
        }
    }
}
