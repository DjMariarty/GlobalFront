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

        /// <summary>
        /// Re-points the move target of the single entity without touching its
        /// position, which is how a presentation test demands a turn without having
        /// to walk the unit across the map first.
        /// </summary>
        private static DeltaUpdateRecord Retarget(int moveTargetX, int moveTargetZ)
        {
            return new DeltaUpdateRecord(
                new EntityId(SingleEntity),
                (byte)(UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget),
                new PlayerId(1),
                new WorldPointMm(0, 0),
                0,
                true,
                new WorldPointMm(moveTargetX, moveTargetZ),
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

        /// <summary>
        /// Audit P1-1: <see cref="UnitViewTickBuffer.Resync"/> to an earlier snapshot
        /// restarts the render clock below the reading each slot last took. A slot
        /// clock left behind there sees dt = 0 on every frame — and because the
        /// starvation ceiling parks the render clock below the stale reading for the
        /// rest of the match, the turn never restarts.
        /// </summary>
        [Test]
        public void Resync_Backward_DoesNotFreezeYaw()
        {
            const int EastX = 100_000;
            var world = NewWorld(AddRecord(0, 0, hasMoveTarget: true, moveTargetX: EastX));
            var buffer = new UnitViewTickBuffer(SlotCapacity);

            // Settle a healthy 10 Hz feed high on the tick line so every slot's
            // elapsed-time anchor ends up well above the base below.
            for (var packet = 0; packet < 20; packet++)
            {
                Assert.That(
                    world.ApplyUpdate(MoveTo(packet * 2 * StepPerTickMm, 0)),
                    Is.EqualTo(ClientWorldApplyResult.Ok));
                buffer.CaptureTick((ulong)(200 + packet * 2), world);
                for (var frame = 0; frame < FramesPerPacket; frame++)
                {
                    buffer.Advance(FrameSeconds);
                    Assert.That(buffer.TrySample(0, out var settled), Is.True);
                    AssertFinite(in settled);
                }
            }

            Assert.That(buffer.TrySample(0, out var beforeResync), Is.True);
            Assert.That(beforeResync.BodyYawDegrees, Is.EqualTo(90f).Within(0.5f));
            var staleAnchor = buffer.RenderTick;
            Assert.That(staleAnchor, Is.GreaterThan(210d));

            // Turn the unit around and re-base the match behind the stale anchor.
            Assert.That(
                world.ApplyUpdate(Retarget(-EastX, 0)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            buffer.Resync(120);
            Assert.That(buffer.RenderTick, Is.LessThan(staleAnchor));
            Assert.That(
                buffer.RenderTick + UnitViewTickBuffer.MaxExtrapolationTicks,
                Is.LessThan(staleAnchor),
                "the parked clock must stay under the stale anchor: that is what froze it");

            var maxStepDegrees = buffer.MaxBodyDegreesPerSecond * (float)FrameSeconds;
            var previousYaw = beforeResync.BodyYawDegrees;
            var start = previousYaw;
            var moved = 0f;
            var previousRemaining = float.PositiveInfinity;
            var capturedStart = false;

            for (var packet = 0; packet < 12; packet++)
            {
                Assert.That(
                    world.ApplyUpdate(MoveTo(14_000 + packet * 2 * StepPerTickMm, 0)),
                    Is.EqualTo(ClientWorldApplyResult.Ok));
                buffer.CaptureTick((ulong)(120 + packet * 2), world);
                for (var frame = 0; frame < FramesPerPacket; frame++)
                {
                    buffer.Advance(FrameSeconds);
                    Assert.That(buffer.TrySample(0, out var turning), Is.True);
                    AssertFinite(in turning);

                    if (!capturedStart)
                    {
                        start = turning.BodyYawDegrees;
                        capturedStart = true;
                    }

                    Assert.That(
                        Math.Abs(UnitViewTickBuffer.ShortestArcDegrees(previousYaw, turning.BodyYawDegrees)),
                        Is.LessThanOrEqualTo(maxStepDegrees + 1e-3f),
                        "a re-anchored clock must still slew the turn, not throw it");
                    previousYaw = turning.BodyYawDegrees;

                    var remaining = Math.Abs(
                        UnitViewTickBuffer.ShortestArcDegrees(turning.BodyYawDegrees, 270f));
                    Assert.That(
                        remaining,
                        Is.LessThanOrEqualTo(previousRemaining + 1e-3f),
                        "the hull may not turn away from the order");
                    previousRemaining = remaining;
                    moved = Math.Max(
                        moved,
                        Math.Abs(UnitViewTickBuffer.ShortestArcDegrees(start, turning.BodyYawDegrees)));
                }
            }

            Assert.That(
                moved,
                Is.GreaterThan(90f),
                "a yaw frozen by a stale slot clock never moves at all");
            Assert.That(previousRemaining, Is.LessThan(45f), "and it keeps closing on its target");
        }

        /// <summary>
        /// Audit P1-1: the render clock may jump forward by more than one clamped
        /// frame — a catch-up snap towards authority, or the first sample after a
        /// <see cref="UnitViewTickBuffer.Flush"/> — and an uncapped elapsed time buys
        /// the whole turn in that one read, which is the instant 180 degree flip
        /// <see cref="UnitViewTickBuffer.MaxBodyDegreesPerSecond"/> exists to prevent.
        ///
        /// Named for the clock jump rather than for
        /// <see cref="UnitViewTickBuffer.Resync"/>: a resync now re-bases the slot
        /// clocks with the render clock, so the surviving way to get a multi-tick
        /// forward step is the drift snap below, which this drives on purpose and
        /// proves it happened.
        /// </summary>
        [Test]
        public void CatchUpSnap_Forward_ClampsSlewDelta()
        {
            const int EastX = 100_000;
            var world = NewWorld(AddRecord(0, 0, hasMoveTarget: true, moveTargetX: EastX));
            var buffer = new UnitViewTickBuffer(SlotCapacity);

            var nextTick = 400UL;
            for (var packet = 0; packet < 4; packet++)
            {
                buffer.CaptureTick(nextTick, world);
                nextTick += 2;
                for (var frame = 0; frame < FramesPerPacket; frame++)
                {
                    buffer.Advance(FrameSeconds);
                    Assert.That(buffer.TrySample(0, out _), Is.True);
                }
            }

            Assert.That(buffer.TrySample(0, out var settled), Is.True);
            Assert.That(settled.BodyYawDegrees, Is.EqualTo(90f).Within(0.5f));

            // Order the opposite heading, then let packets land faster than the
            // presentation clock is allowed to run: the tab-out / OD-18 resume case.
            Assert.That(
                world.ApplyUpdate(Retarget(-EastX, 0)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            var maxStepDegrees = buffer.MaxBodyDegreesPerSecond
                * UnitViewTickBuffer.MaxAdvanceTicksPerCall * (float)TickSeconds;
            var previousYaw = settled.BodyYawDegrees;
            var biggestClockJump = 0d;
            var biggestYawJump = 0f;

            for (var burst = 0; burst < 6; burst++)
            {
                for (var packet = 0; packet < 10; packet++)
                {
                    buffer.CaptureTick(nextTick, world);
                    nextTick += 2;
                }

                var before = buffer.RenderTick;
                buffer.Advance(0.25);
                biggestClockJump = Math.Max(biggestClockJump, buffer.RenderTick - before);

                Assert.That(buffer.TrySample(0, out var pose), Is.True);
                AssertFinite(in pose);
                biggestYawJump = Math.Max(
                    biggestYawJump,
                    Math.Abs(UnitViewTickBuffer.ShortestArcDegrees(previousYaw, pose.BodyYawDegrees)));
                previousYaw = pose.BodyYawDegrees;
            }

            Assert.That(
                biggestClockJump,
                Is.GreaterThan(UnitViewTickBuffer.MaxAdvanceTicksPerCall),
                "the test must actually have snapped the clock forward, or it proves nothing");
            Assert.That(
                biggestYawJump,
                Is.LessThanOrEqualTo(maxStepDegrees + 0.01f),
                "one read may not turn further than the slew limit allows for its dt");
            Assert.That(
                biggestYawJump,
                Is.GreaterThan(0f),
                "the clamp bounds the turn, it does not stop it");
            Assert.That(previousYaw, Is.EqualTo(270f).Within(0.01f), "and the bounded turn still arrives");
        }

        /// <summary>
        /// Audit P1-2: with the adaptive play-out delay near its ceiling, the clock is
        /// parked MaxExtrapolationTicks past authority while the drift target sits a
        /// whole delay behind it. The overshoot test used to run before the parking
        /// clamp, so one frame at the catch-up budget read as a half-ring overshoot and
        /// snapped the clock back to the play-out target — every starved unit
        /// teleporting a dozen-plus ticks in reverse.
        /// </summary>
        [Test]
        public void Starvation_HighDelay_DoesNotSnapClockBackwards()
        {
            const int EastX = 100_000;
            const double BigFrameSeconds = 0.25;      // above the MaxAdvanceTicksPerCall budget
            const int CadenceTicks = 6;               // 3.3 Hz: pushes the delay to its ceiling
            var world = NewWorld(AddRecord(0, 0, hasMoveTarget: true, moveTargetX: EastX));
            var buffer = new UnitViewTickBuffer(SlotCapacity);

            var tick = 300UL;
            for (var packet = 0; packet < 6; packet++)
            {
                Assert.That(
                    world.ApplyUpdate(MoveTo(packet * CadenceTicks * StepPerTickMm, 0)),
                    Is.EqualTo(ClientWorldApplyResult.Ok));
                buffer.CaptureTick(tick, world);
                tick += CadenceTicks;
                for (var frame = 0; frame < 4; frame++)
                {
                    buffer.Advance(FrameSeconds * 2);
                }
            }

            Assert.That(
                buffer.RenderDelayTicks,
                Is.GreaterThan(10d),
                "the finding needs the long-delay window: 2 packets of this cadence");

            // Starve: authority stops, so the clock runs out to the extrapolation
            // ceiling and parks there.
            for (var frame = 0; frame < 100; frame++)
            {
                buffer.Advance(FrameSeconds);
            }

            var newest = buffer.CapturedTick;
            var ceiling = (double)newest + UnitViewTickBuffer.MaxExtrapolationTicks;
            Assert.That(
                buffer.RenderTick,
                Is.EqualTo(ceiling).Within(1e-9),
                "a starved clock parks exactly on the ceiling");

            Assert.That(buffer.TrySample(0, out var parked), Is.True);
            AssertFinite(in parked);
            Assert.That(parked.Source, Is.EqualTo(UnitViewPoseSource.Held));
            var lastX = parked.XMillimetres;

            for (var frame = 0; frame < 5; frame++)
            {
                buffer.Advance(BigFrameSeconds);
                Assert.That(
                    buffer.RenderTick,
                    Is.GreaterThanOrEqualTo(ceiling - 1e-9),
                    "a starved clock may never be snapped backwards");
                Assert.That(
                    buffer.RenderTick,
                    Is.EqualTo(ceiling).Within(1e-9),
                    "and it stays parked instead of drifting off");

                Assert.That(buffer.TrySample(0, out var held), Is.True);
                AssertFinite(in held);
                Assert.That(
                    held.XMillimetres,
                    Is.GreaterThanOrEqualTo(lastX - 0.001f),
                    "the unit must not teleport back to the last authoritative point");
                lastX = held.XMillimetres;
            }
        }

        /// <summary>
        /// Audit P2-1: an infinity reaching the yaw of a slot used to be turned into
        /// NaN by the modulo inside <see cref="UnitViewTickBuffer.ShortestArcDegrees"/>,
        /// and NaN written back as the slot heading poisons every later rotation of
        /// that unit.
        /// </summary>
        [Test]
        public void SlewDegrees_InfinityInput_DoesNotReturnNaN()
        {
            var notAHeading = new[] { float.PositiveInfinity, float.NegativeInfinity };
            foreach (var bad in notAHeading)
            {
                var holdsLastHeading = UnitViewTickBuffer.SlewDegrees(37f, bad, 270f, 0.016f);
                var adoptsTarget = UnitViewTickBuffer.SlewDegrees(bad, 90f, 270f, 0.016f);
                var hasNothing = UnitViewTickBuffer.SlewDegrees(bad, bad, 270f, 0.016f);
                var arcFromTarget = UnitViewTickBuffer.ShortestArcDegrees(37f, bad);
                var arcFromCurrent = UnitViewTickBuffer.ShortestArcDegrees(bad, 37f);

                Assert.That(
                    float.IsNaN(holdsLastHeading) || float.IsNaN(adoptsTarget)
                    || float.IsNaN(hasNothing) || float.IsNaN(arcFromTarget)
                    || float.IsNaN(arcFromCurrent),
                    Is.False,
                    "no non-finite heading may leave the slew");

                // An untrustworthy target is held, never chased into the dark; an
                // untrustworthy current adopts the authority outright, and an empty
                // transform starts at north.
                Assert.That(holdsLastHeading, Is.EqualTo(37f));
                Assert.That(adoptsTarget, Is.EqualTo(90f));
                Assert.That(hasNothing, Is.EqualTo(0f));
                Assert.That(arcFromTarget, Is.EqualTo(0f), "an unknown heading turns nothing");
                Assert.That(arcFromCurrent, Is.EqualTo(0f));

                // A good order after the poison still slews normally.
                Assert.That(
                    UnitViewTickBuffer.SlewDegrees(holdsLastHeading, 90f, 270f, 0.016f),
                    Is.EqualTo(37f + 270f * 0.016f).Within(1e-3f));
            }
        }

        /// <summary>
        /// Audit P3: -360 degrees wraps to negative zero, which compares equal to 0 but
        /// signs a zero yaw delta and prints as "-0" in a debug HUD.
        /// </summary>
        [Test]
        public void SlewDegrees_FullTurnWrap_ReturnsPositiveZero()
        {
            var wrapped = UnitViewTickBuffer.SlewDegrees(-360f, -360f, 270f, 0.016f);
            Assert.That(wrapped, Is.EqualTo(0f));

            // The sign of the reciprocal distinguishes the two zeros exactly, without
            // depending on a bit-cast helper or on number formatting.
            Assert.That(1f / wrapped, Is.GreaterThan(0f), "must be +0.0, not -0.0");
        }

        /// <summary>
        /// Audit P2-3: an explicit <c>SetSlotMaximumHealth(slot, 0)</c> asks for an
        /// empty bar, but 0 stored as the reciprocal was indistinguishable from "no
        /// denominator yet", so the first capture overwrote the override with the
        /// catalog row.
        /// </summary>
        [Test]
        public void SetSlotMaximumHealth_ExplicitZero_SurvivesCatalog()
        {
            var catalog = new UnitCatalog(new[]
            {
                new UnitDefinition(UnitKinds.Tank, "Tank", 400, 900),
            });

            var world = NewWorld(AddRecord(0, 0, health: 200, unitKind: UnitKinds.Tank));
            var buffer = new UnitViewTickBuffer(SlotCapacity, catalog: catalog);

            // The override is made before the first capture, which is exactly when the
            // catalog used to win.
            buffer.SetSlotMaximumHealth(0, 0);
            buffer.CaptureTick(10, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var blanked), Is.True);
            Assert.That(blanked.Health, Is.EqualTo(200), "raw authority survives");
            Assert.That(
                blanked.HealthFraction,
                Is.EqualTo(0f),
                "an explicit 0 is a request, not a missing stat");

            // The override belongs to the unit that asked for it: a recycled slot must
            // go back to resolving from the archetype.
            Assert.That(
                world.ApplyRemove(new DeltaRemoveRecord(
                    new EntityId(SingleEntity), DeltaRemoveCause.Destroyed)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(
                world.ApplyAdd(new DeltaAddRecord(
                    new EntityId(7),
                    new PlayerId(1),
                    new WorldPointMm(10, 10),
                    200,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false,
                    UnitKinds.Tank)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            buffer.CaptureTick(12, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var recycled), Is.True);
            Assert.That(
                recycled.HealthFraction,
                Is.EqualTo(0.5f).Within(1e-6f),
                "the override must not outlive the unit that set it");
        }

        /// <summary>
        /// ADD for an explicit entity id and archetype, so a test can watch one unit
        /// die and another take its slot.
        /// </summary>
        private static DeltaAddRecord Spawned(ulong entity, byte unitKind, int health)
        {
            return new DeltaAddRecord(
                new EntityId(entity),
                new PlayerId(1),
                new WorldPointMm(0, 0),
                health,
                false,
                new WorldPointMm(0, 0),
                new EntityId(0),
                false,
                unitKind);
        }

        private static UnitCatalog HealthCatalog()
        {
            // Distinct maxima per archetype, and neither equal to the override the
            // tests below name, so a wrong denominator cannot coincidentally match.
            return new UnitCatalog(new[]
            {
                new UnitDefinition(UnitKinds.Scout, "Scout", 200, 500),
                new UnitDefinition(UnitKinds.Tank, "Tank", 400, 900),
            });
        }

        private static ClientReplicationWorld WorldWith(params DeltaAddRecord[] adds)
        {
            var world = new ClientReplicationWorld(SlotCapacity);
            foreach (var add in adds)
            {
                Assert.That(world.ApplyAdd(add), Is.EqualTo(ClientWorldApplyResult.Ok));
            }

            return world;
        }

        /// <summary>
        /// Audit N-1: an override belongs to the unit it was named for. When the
        /// client watches that unit leave the table, the slot's health state has to
        /// leave with it, or whoever spawns into the recycled slot inherits it.
        /// </summary>
        [Test]
        public void SetSlotMaximumHealth_DeathFollowedByNewUnit_ResolvesFromCatalog()
        {
            var world = WorldWith(Spawned(1, UnitKinds.Tank, 50));
            var buffer = new UnitViewTickBuffer(SlotCapacity, catalog: HealthCatalog());

            // A per-match stat override for the tank: 50 of 123, not 50 of its
            // archetype's 400.
            buffer.SetSlotMaximumHealth(0, 123);
            buffer.CaptureTick(10, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var overridden), Is.True);
            Assert.That(overridden.HealthFraction, Is.EqualTo(50f / 123f).Within(1e-6f));

            // The death is observable: a later packet finds the slot empty, which is
            // also the moment the buffer stops knowing who the override was for.
            Assert.That(
                world.ApplyRemove(new DeltaRemoveRecord(
                    new EntityId(1), DeltaRemoveCause.Destroyed)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            buffer.CaptureTick(12, world);
            Assert.That(buffer.HasHistory(0), Is.False);

            // A scout takes the recycled slot with its archetype on the wire, so the
            // bar is the catalog's 50/200. Inheriting 1/123 drew it at 41 percent.
            Assert.That(
                world.ApplyAdd(Spawned(2, UnitKinds.Scout, 50)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            buffer.CaptureTick(14, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var successor), Is.True);
            Assert.That(successor.Health, Is.EqualTo(50));
            Assert.That(
                successor.HealthFraction,
                Is.EqualTo(0.25f).Within(1e-6f),
                "a dead unit's override must not follow into its successor");
        }

        /// <summary>
        /// Audit N-1, the variant the observed death cannot cover: an OD-20 resync
        /// drops the ring but deliberately keeps the slot identity and the resolved
        /// denominator, and the handover to another unit then happens with no empty
        /// packet in between.
        /// </summary>
        [Test]
        public void SetSlotMaximumHealth_ResyncHandover_ResolvesFromCatalog()
        {
            var world = WorldWith(Spawned(1, UnitKinds.Tank, 50));
            var buffer = new UnitViewTickBuffer(SlotCapacity, catalog: HealthCatalog());

            buffer.SetSlotMaximumHealth(0, 123);
            buffer.CaptureTick(10, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var overridden), Is.True);
            Assert.That(overridden.HealthFraction, Is.EqualTo(50f / 123f).Within(1e-6f));

            // The resync clears every sample, so "did this slot have a view?" is no
            // longer a usable witness for "did this slot have a unit?".
            Assert.That(
                world.ApplyRemove(new DeltaRemoveRecord(
                    new EntityId(1), DeltaRemoveCause.Destroyed)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(
                world.ApplyAdd(Spawned(2, UnitKinds.Scout, 50)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            buffer.Resync(400);
            Assert.That(buffer.HasHistory(0), Is.False);

            buffer.CaptureTick(402, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var successor), Is.True);
            Assert.That(
                successor.HealthFraction,
                Is.EqualTo(0.25f).Within(1e-6f),
                "the override must not survive a handover across the resync gap");
        }

        /// <summary>
        /// The other side of N-1: a flush of the same unit is a pause, not a death.
        /// Clearing the health state in <see cref="UnitViewTickBuffer.Flush"/> would
        /// close the handover case too, but it would also blank the bar forever for
        /// the pre-OD-29 records whose denominator only a binder knows — the override
        /// is what that escape hatch is for, so it is kept across a pause and dropped
        /// only when the unit itself is gone.
        /// </summary>
        [Test]
        public void SetSlotMaximumHealth_SurvivesFlushOfTheSameUnit()
        {
            var world = WorldWith(Spawned(1, UnitKinds.Tank, 50));

            // Tank is in the catalog at 400: if the override were dropped, the bar
            // would silently come back as the archetype's instead of the unit's.
            var buffer = new UnitViewTickBuffer(SlotCapacity, catalog: HealthCatalog());
            buffer.SetSlotMaximumHealth(0, 123);
            buffer.CaptureTick(10, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var before), Is.True);
            Assert.That(before.HealthFraction, Is.EqualTo(50f / 123f).Within(1e-6f));

            // The OD-18 tactical pause: history drops, the unit and its stats do not.
            buffer.Flush();
            Assert.That(buffer.TrySample(0, out _), Is.False);
            buffer.CaptureTick(12, world);
            buffer.Advance(TickSeconds);
            Assert.That(buffer.TrySample(0, out var after), Is.True);
            Assert.That(
                after.HealthFraction,
                Is.EqualTo(50f / 123f).Within(1e-6f),
                "a pause must not silently replace a stat override with the archetype");
        }

        /// <summary>
        /// Audit F-4: one 31-tick hole is below the re-prime gate, so it reaches the
        /// arrival estimator as an ordinary cadence sample. Unbounded it puts a third
        /// of the outage into the interval, and the delay built on top of that slams
        /// the play-out ceiling — every unit on screen a play-out delay behind
        /// authority because the transport stalled once.
        /// </summary>
        [Test]
        public void ObserveArrival_LargeGap_DoesNotSpikeDelayToCeiling()
        {
            var world = NewWorld(AddRecord(0, 0));
            var buffer = new UnitViewTickBuffer(SlotCapacity);

            // Settle the estimator on a nominal 10 Hz cadence first, so what is
            // measured below is the spike and not the cold start.
            for (var packet = 0; packet < 8; packet++)
            {
                buffer.CaptureTick((ulong)(100 + packet * 2), world);
            }

            Assert.That(buffer.RenderDelayTicks, Is.EqualTo(4d).Within(1e-9), "2 packets of 2 ticks");
            Assert.That(buffer.ObservedIntervalTicks, Is.EqualTo(2d).Within(1e-9));

            Assert.That(buffer.CapturedTick, Is.EqualTo(114UL));
            buffer.CaptureTick(145, world);
            Assert.That(buffer.ReprimeCount, Is.EqualTo(0), "a 31-tick gap is under the ring depth");
            Assert.That(
                buffer.RenderDelayTicks,
                Is.LessThanOrEqualTo(8.0d),
                "one stall may not set the play-out ceiling");

            // Three nominal packets later the estimate is back on the cadence, which
            // is the difference between one packet of hiccup and seconds of lag.
            for (var packet = 0; packet < 3; packet++)
            {
                buffer.CaptureTick((ulong)(147 + packet * 2), world);
            }

            Assert.That(
                buffer.RenderDelayTicks,
                Is.LessThan(6d),
                "a bounded spike must not leave the play-out delay elevated");
            Assert.That(buffer.ObservedIntervalTicks, Is.LessThan(2.7d));
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
