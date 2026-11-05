using System;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Structures;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Step 5.3.1 — ring production queues, rally points and LowPower
    /// throttling. Up to 9 pre-orders per building (Generals canon); the
    /// per-mille accumulator halves under blackout (exactly 500‰) and flows to
    /// the next item on completion; cancellation and destruction refund 100%;
    /// finished units exit through the exit point or the nearest free lattice
    /// node of a deterministic spiral. Integer-only; the per-tick path must
    /// stay allocation-free (0 B GC.Alloc).
    /// </summary>
    [TestFixture]
    public sealed class ProductionTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);

        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        private const int WarmupIterations = 200;

        private const int MeasuredIterations = 1000;

        // ------------------------------------------------------------------
        // 1 — protocol values
        // ------------------------------------------------------------------

        [Test]
        public void ProductionRejection_CarriesTheDocumentedNumericValues()
        {
            Assert.That((byte)ProductionRejection.None, Is.EqualTo(0));
            Assert.That((byte)ProductionRejection.BuildingNotFound, Is.EqualTo(1));
            Assert.That((byte)ProductionRejection.InvalidOwner, Is.EqualTo(2));
            Assert.That((byte)ProductionRejection.BuildingNotCompleted, Is.EqualTo(3), "reserved for the Step 5.3.2 server gate");
            Assert.That((byte)ProductionRejection.BuildingDead, Is.EqualTo(4), "reserved for the Step 5.3.2 server gate");
            Assert.That((byte)ProductionRejection.QueueFull, Is.EqualTo(5));
            Assert.That((byte)ProductionRejection.InvalidBlueprint, Is.EqualTo(6));
            Assert.That((byte)ProductionRejection.InvalidSlotIndex, Is.EqualTo(7));
        }

        [Test]
        public void SetRallyRejection_CarriesTheDocumentedNumericValues()
        {
            Assert.That((byte)SetRallyRejection.None, Is.EqualTo(0));
            Assert.That((byte)SetRallyRejection.BuildingNotFound, Is.EqualTo(1));
            Assert.That((byte)SetRallyRejection.InvalidOwner, Is.EqualTo(2));
            Assert.That((byte)SetRallyRejection.OutOfBounds, Is.EqualTo(3));
        }

        // ------------------------------------------------------------------
        // 2 — registration and defaults
        // ------------------------------------------------------------------

        [Test]
        public void TryRegisterBuilding_DefaultsExitAndRallyToTheOffset()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);

            Assert.That(registry.TryGetBuildingState(100, out BuildingProductionState state), Is.True);
            Assert.That(state.ExitPoint, Is.EqualTo(new WorldPointMm(0, -3000)), "3 m south of the centre by default");
            Assert.That(state.RallyPoint, Is.EqualTo(new WorldPointMm(0, -3000)), "rally mirrors the exit until ordered");
            Assert.That(state.HasRally, Is.False);
            Assert.That(state.QueueCount, Is.EqualTo(0));
            Assert.That(state.WorkMilliAccumulator, Is.EqualTo(0));
            Assert.That(state.Center, Is.EqualTo(new WorldPointMm(0, 0)));
            Assert.That(state.Owner, Is.EqualTo(PlayerOne));
        }

        [Test]
        public void TryRegisterBuilding_CustomExit_IsSnapped()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0), new WorldPointMm(123, 456)), Is.True);
            Assert.That(registry.TryGetBuildingState(100, out BuildingProductionState state), Is.True);
            Assert.That(state.ExitPoint, Is.EqualTo(new WorldPointMm(0, 500)), "the exit lands on the 500 mm lattice");
        }

        [Test]
        public void TryRegisterBuilding_RejectsInvalidInput()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryRegisterBuilding(100, PlayerTwo, new WorldPointMm(50_000, 0)), Is.False, "duplicate entity id");
            Assert.That(registry.TryRegisterBuilding(0, PlayerOne, new WorldPointMm(0, 0)), Is.False, "0 is reserved as 'no entity'");
            Assert.That(registry.TryRegisterBuilding(101, new PlayerId(0), new WorldPointMm(0, 0)), Is.False, "invalid owner");
            Assert.That(registry.Count, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------
        // 3 — enqueue gates and the 9-slot ring
        // ------------------------------------------------------------------

        [Test]
        public void TryEnqueue_RejectsUnknownBuildingAndForeignOwner()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 10, 100), Is.EqualTo(ProductionRejection.BuildingNotFound));

            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerTwo, 7, 10, 100), Is.EqualTo(ProductionRejection.InvalidOwner));
            Assert.That(registry.TryGetBuildingState(100, out var state), Is.True);
            Assert.That(state.QueueCount, Is.EqualTo(0), "a rejected enqueue must not grow the queue");
        }

        [Test]
        public void TryEnqueue_RejectsInvalidBlueprints()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);

            Assert.That(registry.TryEnqueue(100, PlayerOne, 0, 10, 100), Is.EqualTo(ProductionRejection.InvalidBlueprint), "blueprint 0 is 'no blueprint'");
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 0, 100), Is.EqualTo(ProductionRejection.InvalidBlueprint), "a zero-tick order completes outside the tick model");
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 10, -1), Is.EqualTo(ProductionRejection.InvalidBlueprint), "a negative cost is nonsense");
            Assert.That(registry.TryGetBuildingState(100, out var state), Is.True);
            Assert.That(state.QueueCount, Is.EqualTo(0));
        }

        [Test]
        public void TryEnqueue_NinthSlotFits_TenthRejects_RefitsAfterCancel()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);

            for (int i = 0; i < SimulationConstants.MaxProductionQueueSlots; i++)
            {
                Assert.That(
                    registry.TryEnqueue(100, PlayerOne, (ushort)(i + 1), 10, 100),
                    Is.EqualTo(ProductionRejection.None),
                    $"slot {i} must be accepted (Generals canon: 9 pre-orders)");
            }

            Assert.That(registry.TryGetBuildingState(100, out var full), Is.True);
            Assert.That(full.QueueCount, Is.EqualTo(SimulationConstants.MaxProductionQueueSlots));

            Assert.That(
                registry.TryEnqueue(100, PlayerOne, 99, 10, 100),
                Is.EqualTo(ProductionRejection.QueueFull),
                "the 10th pre-order cannot join the ring");

            Assert.That(registry.TryCancel(100, PlayerOne, 0, out int refundCost), Is.EqualTo(ProductionRejection.None));
            Assert.That(refundCost, Is.EqualTo(100));
            Assert.That(
                registry.TryEnqueue(100, PlayerOne, 99, 10, 100),
                Is.EqualTo(ProductionRejection.None),
                "a freed slot accepts the next order");
        }

        // ------------------------------------------------------------------
        // 4 — cancellation: 100% refund, head reset, middle compaction
        // ------------------------------------------------------------------

        [Test]
        public void TryCancel_HeadRefundsFully_ResetsAccumulator_NextItemStartsFresh()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 1, 10, 100), Is.EqualTo(ProductionRejection.None));
            Assert.That(registry.TryEnqueue(100, PlayerOne, 2, 20, 200), Is.EqualTo(ProductionRejection.None));

            var completions = new ProductionCompletedUnit[SimulationConstants.MaxProductionBuildings];
            for (int tick = 0; tick < 5; tick++)
            {
                registry.AdvanceTick(null, null, completions);
            }

            Assert.That(registry.TryGetQueueItem(100, 0, out ProductionQueueItem active), Is.True);
            Assert.That(active.RemainingTicks, Is.EqualTo(5), "5 of the head's 10 ticks are burned");

            Assert.That(registry.TryCancel(100, PlayerOne, 0, out int refundCost), Is.EqualTo(ProductionRejection.None));
            Assert.That(refundCost, Is.EqualTo(100), "cancellation refunds 100% of the paid cost");

            Assert.That(registry.TryGetBuildingState(100, out var state), Is.True);
            Assert.That(state.WorkMilliAccumulator, Is.EqualTo(0), "cancelling the head discards its tick progress");
            Assert.That(state.QueueCount, Is.EqualTo(1));

            Assert.That(registry.TryGetQueueItem(100, 0, out ProductionQueueItem next), Is.True);
            Assert.That(next.BlueprintId, Is.EqualTo(2));
            Assert.That(next.RemainingTicks, Is.EqualTo(20), "the next item becomes active with its full tick count");
        }

        [Test]
        public void TryCancel_MiddleSlot_CompactsTheTail()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            for (int i = 0; i < SimulationConstants.MaxProductionQueueSlots; i++)
            {
                // Distinct blueprints make the post-compaction identity observable.
                Assert.That(registry.TryEnqueue(100, PlayerOne, (ushort)(i + 1), 10, 100 * (i + 1)), Is.EqualTo(ProductionRejection.None));
            }

            Assert.That(registry.TryCancel(100, PlayerOne, 4, out int refundCost), Is.EqualTo(ProductionRejection.None));
            Assert.That(refundCost, Is.EqualTo(500), "slot 4 held the 500-cost order");

            Assert.That(registry.TryGetBuildingState(100, out var state), Is.True);
            Assert.That(state.QueueCount, Is.EqualTo(8));

            // Slots 5..8 shifted into 4..7 with their identities intact.
            for (int slot = 4; slot < 8; slot++)
            {
                Assert.That(registry.TryGetQueueItem(100, slot, out ProductionQueueItem item), Is.True, $"slot {slot} must survive the compaction");
                Assert.That(item.BlueprintId, Is.EqualTo((ushort)(slot + 2)), $"slot {slot} now holds the order formerly at slot {slot + 1}");
                Assert.That(item.PaidCost, Is.EqualTo(100 * (slot + 2)));
            }

            Assert.That(registry.TryGetQueueItem(100, 8, out _), Is.False, "the compacted tail is empty");
            Assert.That(registry.TryGetQueueItem(100, 0, out ProductionQueueItem head), Is.True);
            Assert.That(head.BlueprintId, Is.EqualTo(1), "the head is untouched by a middle-slot cancel");
        }

        [Test]
        public void TryCancel_InvalidSlotIndex_Rejects()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 1, 10, 100), Is.EqualTo(ProductionRejection.None));

            Assert.That(registry.TryCancel(100, PlayerOne, -1, out int refundLow), Is.EqualTo(ProductionRejection.InvalidSlotIndex));
            Assert.That(registry.TryCancel(100, PlayerOne, 1, out int refundHigh), Is.EqualTo(ProductionRejection.InvalidSlotIndex), "Count is the exclusive upper bound");
            Assert.That(refundLow, Is.EqualTo(0));
            Assert.That(refundHigh, Is.EqualTo(0));
        }

        [Test]
        public void TryCancel_ForeignOwnerAndUnknownBuilding_Reject()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 1, 10, 100), Is.EqualTo(ProductionRejection.None));

            Assert.That(registry.TryCancel(100, PlayerTwo, 0, out int refundForeign), Is.EqualTo(ProductionRejection.InvalidOwner));
            Assert.That(registry.TryCancel(999, PlayerOne, 0, out int refundUnknown), Is.EqualTo(ProductionRejection.BuildingNotFound));
            Assert.That(refundForeign, Is.EqualTo(0));
            Assert.That(refundUnknown, Is.EqualTo(0));
            Assert.That(registry.TryGetBuildingState(100, out var state), Is.True);
            Assert.That(state.QueueCount, Is.EqualTo(1), "failed cancels leave the queue intact");
        }

        // ------------------------------------------------------------------
        // 5 — the LowPower throttle: exactly 40 / 80 / 40 ticks
        // ------------------------------------------------------------------

        [Test]
        public void AdvanceTick_NormalPower_CompletesExactlyInFortyTicks()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 40, 0), Is.EqualTo(ProductionRejection.None));

            var completions = new ProductionCompletedUnit[SimulationConstants.MaxProductionBuildings];
            for (int tick = 0; tick < 39; tick++)
            {
                Assert.That(registry.AdvanceTick(null, null, completions), Is.EqualTo(0), $"tick {tick + 1} must not complete a 40-tick unit");
            }

            Assert.That(registry.TryGetQueueItem(100, 0, out ProductionQueueItem almost), Is.True);
            Assert.That(almost.RemainingTicks, Is.EqualTo(1));

            Assert.That(registry.AdvanceTick(null, null, completions), Is.EqualTo(1), "the 40th tick completes the unit");
            Assert.That(registry.TryGetBuildingState(100, out var state), Is.True);
            Assert.That(state.QueueCount, Is.EqualTo(0));
        }

        [Test]
        public void AdvanceTick_LowPower_CompletesExactlyInEightyTicks()
        {
            var ledger = CreateLowPowerLedger();
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 40, 0), Is.EqualTo(ProductionRejection.None));

            var completions = new ProductionCompletedUnit[SimulationConstants.MaxProductionBuildings];
            for (int tick = 0; tick < 79; tick++)
            {
                Assert.That(registry.AdvanceTick(ledger, null, completions), Is.EqualTo(0), $"tick {tick + 1} must not complete under the 500‰ throttle");
            }

            Assert.That(registry.TryGetQueueItem(100, 0, out ProductionQueueItem almost), Is.True);
            Assert.That(almost.RemainingTicks, Is.EqualTo(1), "39 decrements over 79 throttled ticks");

            Assert.That(registry.AdvanceTick(ledger, null, completions), Is.EqualTo(1), "the 80th tick completes the throttled unit");
        }

        [Test]
        public void AdvanceTick_ExemptOwnerUnderDeficit_KeepsTheFullRate()
        {
            var ledger = CreateLowPowerLedger();
            ledger.SetPlayerPowerExempt(PlayerOne, true);
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 40, 0), Is.EqualTo(ProductionRejection.None));

            var completions = new ProductionCompletedUnit[SimulationConstants.MaxProductionBuildings];
            for (int tick = 0; tick < 39; tick++)
            {
                Assert.That(registry.AdvanceTick(ledger, null, completions), Is.EqualTo(0), $"tick {tick + 1} must not complete for an exempt owner");
            }

            Assert.That(registry.AdvanceTick(ledger, null, completions), Is.EqualTo(1), "GLA doctrine: the blackout penalty is ignored");
        }

        [Test]
        public void AdvanceTick_LeftoverWorkCarriesToTheNextItem()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 8000, -1), Is.EqualTo(PowerLedgerRejection.None), "start throttled");

            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 1, 1, 10), Is.EqualTo(ProductionRejection.None));
            Assert.That(registry.TryEnqueue(100, PlayerOne, 2, 2, 20), Is.EqualTo(ProductionRejection.None));

            var completions = new ProductionCompletedUnit[SimulationConstants.MaxProductionBuildings];

            // Tick 1 (throttled): 500‰ banked, item A (1 tick) untouched.
            Assert.That(registry.AdvanceTick(ledger, null, completions), Is.EqualTo(0));

            // Power restored: tick 2 pushes the accumulator to 1500‰ — the
            // decrement completes A and banks the 500‰ leftover for B.
            Assert.That(ledger.UnregisterBuilding(PlayerOne, 8000), Is.True);
            Assert.That(registry.AdvanceTick(ledger, null, completions), Is.EqualTo(1), "A completes on the restored tick");
            Assert.That(registry.TryGetBuildingState(100, out var state), Is.True);
            Assert.That(state.WorkMilliAccumulator, Is.EqualTo(500), "the leftover flows to the next item — nothing burns");

            // Throttle again: with the carried 500‰ B (2 ticks) needs only two
            // more banked 1000‰ cycles — t3 banks one decrement, t4 re-banks,
            // t5 completes; without the carry it would finish on t6.
            Assert.That(ledger.RegisterBuilding(PlayerOne, 8000, -1), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(registry.AdvanceTick(ledger, null, completions), Is.EqualTo(0), "t3 banks the first B decrement (acc 500 + 500 = 1000)");
            Assert.That(registry.AdvanceTick(ledger, null, completions), Is.EqualTo(0), "t4 re-banks (acc 0 + 500 — still short)");
            Assert.That(registry.AdvanceTick(ledger, null, completions), Is.EqualTo(1), "t5 completes B one throttle-cycle earlier than without the carry");
            Assert.That(registry.TryGetBuildingState(100, out var drained), Is.True);
            Assert.That(drained.QueueCount, Is.EqualTo(0));
            Assert.That(drained.WorkMilliAccumulator, Is.EqualTo(0), "an empty queue holds no work");
        }

        // ------------------------------------------------------------------
        // 6 — rally points
        // ------------------------------------------------------------------

        [Test]
        public void TrySetRallyPoint_SnapsAndFlagsTheBuilding()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);

            Assert.That(registry.TrySetRallyPoint(100, PlayerOne, new WorldPointMm(123, 456)), Is.EqualTo(SetRallyRejection.None));
            Assert.That(registry.TryGetBuildingState(100, out var state), Is.True);
            Assert.That(state.HasRally, Is.True);
            Assert.That(state.RallyPoint, Is.EqualTo(new WorldPointMm(0, 500)), "the rally lands on the 500 mm lattice");

            // The completion event carries the rally for the caller to route.
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 1, 0), Is.EqualTo(ProductionRejection.None));
            var completions = new ProductionCompletedUnit[SimulationConstants.MaxProductionBuildings];
            Assert.That(registry.AdvanceTick(null, null, completions), Is.EqualTo(1));
            Assert.That(completions[0].HasRally, Is.True);
            Assert.That(completions[0].RallyPoint, Is.EqualTo(new WorldPointMm(0, 500)));
        }

        [Test]
        public void TrySetRallyPoint_OutOfBounds_Rejects()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);

            Assert.That(registry.TrySetRallyPoint(100, PlayerOne, new WorldPointMm(200_001, 0)), Is.EqualTo(SetRallyRejection.OutOfBounds));
            Assert.That(registry.TrySetRallyPoint(100, PlayerOne, new WorldPointMm(0, -200_001)), Is.EqualTo(SetRallyRejection.OutOfBounds), "Z is bounded the same way as X");
            Assert.That(registry.TryGetBuildingState(100, out var state), Is.True);
            Assert.That(state.HasRally, Is.False, "a rejected rally keeps the default exit behaviour");
        }

        [Test]
        public void TrySetRallyPoint_ForeignOwnerAndUnknownBuilding_Reject()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);

            Assert.That(registry.TrySetRallyPoint(100, PlayerTwo, new WorldPointMm(0, 0)), Is.EqualTo(SetRallyRejection.InvalidOwner));
            Assert.That(registry.TrySetRallyPoint(999, PlayerOne, new WorldPointMm(0, 0)), Is.EqualTo(SetRallyRejection.BuildingNotFound));
        }

        // ------------------------------------------------------------------
        // 7 — exit gate and the deterministic spiral
        // ------------------------------------------------------------------

        [Test]
        public void AdvanceTick_FreeExit_SpawnsAtTheExitPoint()
        {
            var registry = new ProductionQueueRegistry();
            var placement = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 1, 0), Is.EqualTo(ProductionRejection.None));

            var completions = new ProductionCompletedUnit[SimulationConstants.MaxProductionBuildings];
            Assert.That(registry.AdvanceTick(null, placement, completions), Is.EqualTo(1));
            Assert.That(completions[0].SpawnPosition, Is.EqualTo(new WorldPointMm(0, -3000)));
        }

        [Test]
        public void AdvanceTick_OccupiedExit_SpiralsToTheNearestFreeNode()
        {
            var registry = new ProductionQueueRegistry();
            var placement = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 1, 0), Is.EqualTo(ProductionRejection.None));

            // A shed sits exactly on the exit point (0, -3000).
            Assert.That(placement.TryRegister(900, new WorldPointMm(0, -3000), new BuildingFootprint(500, 500), out _), Is.True);

            var completions = new ProductionCompletedUnit[SimulationConstants.MaxProductionBuildings];
            Assert.That(registry.AdvanceTick(null, placement, completions), Is.EqualTo(1));

            // Ring 1, north edge, first candidate: dx = -1, dz = -1.
            Assert.That(
                completions[0].SpawnPosition,
                Is.EqualTo(new WorldPointMm(-500, -3500)),
                "the spiral order is canonical: north edge of ring 1, left to right");
        }

        // ------------------------------------------------------------------
        // 8 — destruction refunds the whole queue; survivors keep theirs
        // ------------------------------------------------------------------

        [Test]
        public void UnregisterBuilding_RefundsEveryQueuedOrder()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 1, 10, 10), Is.EqualTo(ProductionRejection.None));
            Assert.That(registry.TryEnqueue(100, PlayerOne, 2, 10, 20), Is.EqualTo(ProductionRejection.None));
            Assert.That(registry.TryEnqueue(100, PlayerOne, 3, 10, 30), Is.EqualTo(ProductionRejection.None));

            Assert.That(registry.UnregisterBuilding(100, out int totalRefund), Is.True);
            Assert.That(totalRefund, Is.EqualTo(60), "destruction refunds 100% of every queued order");
            Assert.That(registry.TryGetBuildingState(100, out _), Is.False);

            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryGetBuildingState(100, out var fresh), Is.True);
            Assert.That(fresh.QueueCount, Is.EqualTo(0), "the rebuilt building starts with an empty queue");

            Assert.That(registry.UnregisterBuilding(999, out int unknownRefund), Is.False);
            Assert.That(unknownRefund, Is.EqualTo(0));
        }

        [Test]
        public void UnregisterBuilding_SwapAndPop_KeepsSurvivorQueuesIntact()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(1, PlayerOne, new WorldPointMm(-10_000, 0)), Is.True);
            Assert.That(registry.TryRegisterBuilding(2, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryRegisterBuilding(3, PlayerOne, new WorldPointMm(10_000, 0)), Is.True);

            for (int slot = 0; slot < 3; slot++)
            {
                Assert.That(registry.TryEnqueue(1, PlayerOne, (ushort)(slot + 1), 10, 10), Is.EqualTo(ProductionRejection.None));
            }

            Assert.That(registry.TryEnqueue(2, PlayerOne, 9, 10, 77), Is.EqualTo(ProductionRejection.None));
            Assert.That(registry.TryEnqueue(3, PlayerOne, 5, 10, 50), Is.EqualTo(ProductionRejection.None));
            Assert.That(registry.TryEnqueue(3, PlayerOne, 6, 10, 60), Is.EqualTo(ProductionRejection.None));

            // Building 2 sits between 1 and 3 — its removal swap-and-pops 3 into
            // the freed slot, and 3's flat queue items must travel with it.
            Assert.That(registry.UnregisterBuilding(2, out int refund), Is.True);
            Assert.That(refund, Is.EqualTo(77));

            Assert.That(registry.TryGetBuildingState(1, out var first), Is.True);
            Assert.That(first.QueueCount, Is.EqualTo(3));
            Assert.That(registry.TryGetQueueItem(1, 2, out var firstTail), Is.True);
            Assert.That(firstTail.BlueprintId, Is.EqualTo(3));

            Assert.That(registry.TryGetBuildingState(3, out var third), Is.True);
            Assert.That(third.QueueCount, Is.EqualTo(2), "the moved survivor keeps its queue depth");
            Assert.That(registry.TryGetQueueItem(3, 0, out var thirdHead), Is.True);
            Assert.That(thirdHead.BlueprintId, Is.EqualTo(5));
            Assert.That(registry.TryGetQueueItem(3, 1, out var thirdSecond), Is.True);
            Assert.That(thirdSecond.BlueprintId, Is.EqualTo(6));

            Assert.That(registry.TryEnqueue(3, PlayerOne, 7, 10, 70), Is.EqualTo(ProductionRejection.None), "the moved building keeps accepting orders");
            Assert.That(registry.TryGetQueueItem(3, 2, out var appended), Is.True);
            Assert.That(appended.BlueprintId, Is.EqualTo(7));
        }

        // ------------------------------------------------------------------
        // 9 — caller contract and zero-GC hot path
        // ------------------------------------------------------------------

        [Test]
        public void AdvanceTick_TooSmallCompletionSpan_Throws()
        {
            var registry = new ProductionQueueRegistry();
            Assert.That(registry.TryRegisterBuilding(100, PlayerOne, new WorldPointMm(0, 0)), Is.True);
            Assert.That(registry.TryEnqueue(100, PlayerOne, 7, 1, 0), Is.EqualTo(ProductionRejection.None));

            var tiny = new ProductionCompletedUnit[10];
            Assert.Throws<ArgumentException>(() => registry.AdvanceTick(null, null, tiny), "the span must hold one entry per production building");
        }

        [Test]
        public void GcRecorder_ObservesKnownAllocation_Control()
        {
            // The recorder must be proven live before it can prove absence.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");
            Assert.That(ballast, Is.Not.Null);
        }

        [Test]
        public void AdvanceTick_With50Factories_DoesNotAllocate()
        {
            var registry = new ProductionQueueRegistry();
            for (int i = 0; i < 50; i++)
            {
                Assert.That(registry.TryRegisterBuilding((ulong)(1000 + i), PlayerOne, new WorldPointMm(-199_000 + i * 2_000, -199_000)), Is.True, $"factory {i} must register");
                Assert.That(registry.TryEnqueue((ulong)(1000 + i), PlayerOne, 7, 1500, 1), Is.EqualTo(ProductionRejection.None), $"factory {i} queue must fill");
            }

            var completions = new ProductionCompletedUnit[SimulationConstants.MaxProductionBuildings];

            // Warmup: JIT the whole pipeline; 1500-tick orders survive the window.
            for (int i = 0; i < WarmupIterations; i++)
            {
                registry.AdvanceTick(null, null, completions);
            }

            Assert.That(registry.TryGetBuildingState(1000, out var warm), Is.True);
            Assert.That(warm.QueueCount, Is.EqualTo(1), "the warmup must not complete anything");
            Assert.That(warm.WorkMilliAccumulator, Is.EqualTo(0), "at the full rate every tick banks and spends exactly 1000‰");
            Assert.That(registry.TryGetQueueItem(1000, 0, out ProductionQueueItem warmHead), Is.True);
            Assert.That(warmHead.RemainingTicks, Is.EqualTo(1300), "200 warmup ticks burned 200 of the 1500");

            int calls = 0;
            int sink = 0;
            Assert.That(
                () =>
                {
                    for (int i = 0; i < MeasuredIterations; i++)
                    {
                        sink += registry.AdvanceTick(null, null, completions);
                        calls++;
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "the production tick is a per-tick hot path and must be allocation-free");

            Assert.That(calls, Is.EqualTo(MeasuredIterations), "the measured window must actually run");
            Assert.That(sink, Is.EqualTo(0), "1500-tick orders survive the 1000-tick window");
            Assert.That(registry.TryGetQueueItem(1000, 0, out ProductionQueueItem after), Is.True);
            Assert.That(after.RemainingTicks, Is.EqualTo(300), "1200 total ticks burned of the 1500");
            Assert.That(registry.Count, Is.EqualTo(50));
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        /// <summary>A one-player ledger whose grid sits at NetPower = -1 (LowPower).</summary>
        private static PowerLedger CreateLowPowerLedger()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 8000, -1), Is.EqualTo(PowerLedgerRejection.None), "a lone consumer forces NetPower = -1");
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.True, "scenario setup must produce a blackout");
            return ledger;
        }
    }
}
