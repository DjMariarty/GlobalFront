using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Structures;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Step 5.3.2 — server-side production integration: factories auto-register
    /// when their frames complete, the AdvanceProduction phase sits between
    /// construction and combat, finished units spawn at the exit point and
    /// march to their rally point, orders pass fail-closed gates, a full roster
    /// holds finished orders in the head, and a destroyed factory burns its
    /// queue (Generals canon). Capacity stays strictly total-based per the
    /// architect's veto: the roster never shrinks and never exceeds 1024.
    /// </summary>
    [TestFixture]
    public sealed class MatchServerProductionTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);

        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        private static readonly CombatStats StandardStats = new CombatStats(100, 10, 12000, 2);

        private static readonly BuildingFootprint ShedFootprint = new BuildingFootprint(500, 500);

        // ------------------------------------------------------------------
        // 1 — TryEnqueueProduce rejection hierarchy
        // ------------------------------------------------------------------

        [Test]
        public void TryEnqueueProduce_UnknownBuilding_RejectsWithBuildingNotFound()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryEnqueueProduce(PlayerOne, 999, 7, 10, 100, out ProductionRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ProductionRejection.BuildingNotFound));
        }

        [Test]
        public void TryEnqueueProduce_ForeignBuilding_RejectsWithInvalidOwner()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(
                server.TryEnqueueProduce(PlayerTwo, factoryId, 7, 10, 100, out ProductionRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ProductionRejection.InvalidOwner), "fail-closed: a foreign order is indistinguishable from probing");
            Assert.That(server.Production.TryGetBuildingState(factoryId, out var state), Is.True);
            Assert.That(state.QueueCount, Is.EqualTo(0));
        }

        [Test]
        public void TryEnqueueProduce_DeadBuilding_RejectsWithBuildingDead()
        {
            var server = CreateFactory(out ulong factoryId);
            KillFactory(server, factoryId);

            Assert.That(
                server.TryEnqueueProduce(PlayerOne, factoryId, 7, 10, 100, out ProductionRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ProductionRejection.BuildingDead));
        }

        [Test]
        public void TryEnqueueProduce_FrameUnderConstruction_RejectsWithBuildingNotCompleted()
        {
            var server = new MatchServer();
            // A frozen dozer-built frame: 1‰ per tick, no builders — it lingers.
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, 1, 100, 0, out _),
                Is.True);

            Assert.That(
                server.TryEnqueueProduce(PlayerOne, 900, 7, 10, 100, out ProductionRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ProductionRejection.BuildingNotCompleted), "a frame under construction cannot take orders");
        }

        [Test]
        public void TryEnqueueProduce_CompletedNonFactory_RejectsWithBuildingNotFound()
        {
            var server = new MatchServer();
            // A completed building that never became a factory.
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 1000, 100, 0, out _),
                Is.True);
            server.TickOnce();

            Assert.That(
                server.TryEnqueueProduce(PlayerOne, 900, 7, 10, 100, out ProductionRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ProductionRejection.BuildingNotFound), "a non-factory is invisible to production orders");
        }

        [Test]
        public void TryEnqueueProduce_Success_OnARegisteredFactory()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(
                server.TryEnqueueProduce(PlayerOne, factoryId, 7, 10, 100, out ProductionRejection rejection),
                Is.True, $"a valid order must succeed, got {rejection}");
            Assert.That(server.Production.TryGetBuildingState(factoryId, out var state), Is.True);
            Assert.That(state.QueueCount, Is.EqualTo(1));
        }

        [Test]
        public void TryEnqueueProduce_RingFillsToNine_TenthRejectsWithQueueFull()
        {
            var server = CreateFactory(out ulong factoryId);
            for (int i = 0; i < 9; i++)
            {
                Assert.That(
                    server.TryEnqueueProduce(PlayerOne, factoryId, (ushort)(i + 1), 10, 100, out _),
                    Is.True, $"slot {i} must be accepted");
            }

            Assert.That(
                server.TryEnqueueProduce(PlayerOne, factoryId, 99, 10, 100, out ProductionRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ProductionRejection.QueueFull), "the 10th pre-order cannot join the ring");
        }

        [Test]
        public void TryEnqueueProduce_InvalidBlueprints_Reject()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(
                server.TryEnqueueProduce(PlayerOne, factoryId, 0, 10, 100, out ProductionRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ProductionRejection.InvalidBlueprint), "blueprint 0 is 'no blueprint'");

            Assert.That(
                server.TryEnqueueProduce(PlayerOne, factoryId, 7, 0, 100, out rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ProductionRejection.InvalidBlueprint), "a zero-tick order breaks the tick model");

            Assert.That(
                server.TryEnqueueProduce(PlayerOne, factoryId, 7, 10, -1, out rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ProductionRejection.InvalidBlueprint), "a negative cost is nonsense");
        }

        // ------------------------------------------------------------------
        // 2 — TryCancelProduce: 100% refund and compaction
        // ------------------------------------------------------------------

        [Test]
        public void TryCancelProduce_RejectionChain_BehavesAsDocumented()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(server.TryEnqueueProduce(PlayerOne, factoryId, 7, 10, 100, out _), Is.True);

            Assert.That(server.TryCancelProduce(PlayerOne, 999, 0, out int refundUnknown, out ProductionRejection unknown), Is.False);
            Assert.That(unknown, Is.EqualTo(ProductionRejection.BuildingNotFound));
            Assert.That(refundUnknown, Is.EqualTo(0));

            Assert.That(server.TryCancelProduce(PlayerTwo, factoryId, 0, out int refundForeign, out ProductionRejection foreign), Is.False);
            Assert.That(foreign, Is.EqualTo(ProductionRejection.InvalidOwner));
            Assert.That(refundForeign, Is.EqualTo(0));

            Assert.That(server.TryCancelProduce(PlayerOne, factoryId, -1, out _, out ProductionRejection low), Is.False);
            Assert.That(low, Is.EqualTo(ProductionRejection.InvalidSlotIndex));
            Assert.That(server.TryCancelProduce(PlayerOne, factoryId, 1, out _, out ProductionRejection high), Is.False);
            Assert.That(high, Is.EqualTo(ProductionRejection.InvalidSlotIndex), "QueueCount is the exclusive upper bound");
        }

        [Test]
        public void TryCancelProduce_HeadMiddleTail_RefundFullyAndCompact()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(server.TryEnqueueProduce(PlayerOne, factoryId, 1, 10, 100, out _), Is.True);
            Assert.That(server.TryEnqueueProduce(PlayerOne, factoryId, 2, 10, 200, out _), Is.True);
            Assert.That(server.TryEnqueueProduce(PlayerOne, factoryId, 3, 10, 300, out _), Is.True);

            Assert.That(server.TryCancelProduce(PlayerOne, factoryId, 0, out int headRefund, out _), Is.True);
            Assert.That(headRefund, Is.EqualTo(100), "the head refunds 100%");
            Assert.That(server.Production.TryGetQueueItem(factoryId, 0, out var afterHead), Is.True);
            Assert.That(afterHead.BlueprintId, Is.EqualTo(2), "the queue compacts to the next order");

            Assert.That(server.TryCancelProduce(PlayerOne, factoryId, 1, out int tailRefund, out _), Is.True);
            Assert.That(tailRefund, Is.EqualTo(300), "the tail refunds 100%");
            Assert.That(server.TryCancelProduce(PlayerOne, factoryId, 0, out int lastRefund, out _), Is.True);
            Assert.That(lastRefund, Is.EqualTo(200), "the last remaining order refunds 100%");
            Assert.That(server.Production.TryGetBuildingState(factoryId, out var state), Is.True);
            Assert.That(state.QueueCount, Is.EqualTo(0), "the ring drains to empty");
        }

        // ------------------------------------------------------------------
        // 3 — TrySetRallyPoint gates
        // ------------------------------------------------------------------

        [Test]
        public void TrySetRallyPoint_FailClosed_ForUnknownDeadAndForeign()
        {
            var server = CreateFactory(out ulong factoryId);

            Assert.That(server.TrySetRallyPoint(PlayerOne, 999, new WorldPointMm(0, 0), out SetRallyRejection unknown), Is.False);
            Assert.That(unknown, Is.EqualTo(SetRallyRejection.BuildingNotFound));

            Assert.That(server.TrySetRallyPoint(PlayerTwo, factoryId, new WorldPointMm(0, 0), out SetRallyRejection foreign), Is.False);
            Assert.That(foreign, Is.EqualTo(SetRallyRejection.InvalidOwner), "fail-closed: a foreign order is indistinguishable from probing");

            KillFactory(server, factoryId);
            Assert.That(server.TrySetRallyPoint(PlayerOne, factoryId, new WorldPointMm(0, 0), out SetRallyRejection dead), Is.False);
            Assert.That(dead, Is.EqualTo(SetRallyRejection.BuildingNotFound), "fail-closed: dead buildings are invisible to rally orders");
        }

        [Test]
        public void TrySetRallyPoint_OutOfBounds_Rejects()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(
                server.TrySetRallyPoint(PlayerOne, factoryId, new WorldPointMm(200_001, 0), out SetRallyRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(SetRallyRejection.OutOfBounds));
        }

        [Test]
        public void TrySetRallyPoint_SnapsToLatticeAndFlags()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(server.TrySetRallyPoint(PlayerOne, factoryId, new WorldPointMm(123, 456), out _), Is.True);
            Assert.That(server.Production.TryGetBuildingState(factoryId, out var state), Is.True);
            Assert.That(state.HasRally, Is.True);
            Assert.That(state.RallyPoint, Is.EqualTo(new WorldPointMm(0, 500)), "the rally lands on the 500 mm lattice");
        }

        // ------------------------------------------------------------------
        // 4 — the full lifecycle: dozer → factory → tank → exit → rally
        // ------------------------------------------------------------------

        [Test]
        public void FullLifecycle_DozerBuildsFactory_FactoryProducesTank_TankMarchesToRally()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, 25, 100, 0, true, null, out _),
                Is.True, "the factory is ordered as a production building");
            server.SpawnUnit(PlayerTwo, StandardStats, new WorldPointMm(-150_000, 0), 500); // keep the match non-terminal

            EntityId dozer = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(0, 0), 500);
            Assert.That(server.TryAssignBuilder(PlayerOne, dozer.Value, BuilderKind.Dozer, 900), Is.EqualTo(BuilderAssignmentRejection.None));
            server.ExecuteTicks(40);

            Assert.That(server.Construction.Count, Is.EqualTo(0), "the factory frame completed");
            Assert.That(server.Production.TryGetBuildingState(900, out var factory), Is.True, "the completed frame auto-registered as a factory");

            Assert.That(server.TrySetRallyPoint(PlayerOne, 900, new WorldPointMm(10_000, 0), out _), Is.True);
            Assert.That(server.TryEnqueueProduce(PlayerOne, 900, 7, 20, 100, out _), Is.True, "a tank is ordered (20 ticks)");

            server.ExecuteTicks(19);
            Assert.That(server.UnitCount, Is.EqualTo(3), "frame + dozer + opponent — the tank is not built yet");
            Assert.That(server.Production.TryGetQueueItem(900, 0, out var almost), Is.True);
            Assert.That(almost.RemainingTicks, Is.EqualTo(1));

            server.TickOnce();
            Assert.That(server.UnitCount, Is.EqualTo(4), "the 20th production tick spawns the tank");
            EntityId tank = new EntityId(903);
            Assert.That(server.TryGetUnit(tank, out var tankSnapshot), Is.True);
            Assert.That(tankSnapshot.Position, Is.EqualTo(new WorldPointMm(0, -3000)), "the unit leaves through the exit point");
            Assert.That(tankSnapshot.UnitKind, Is.EqualTo(UnitKinds.Tank), "the default blueprint produces tanks");
            Assert.That(tankSnapshot.CurrentHealth, Is.EqualTo(100));
            Assert.That(tankSnapshot.HasMoveTarget, Is.True, "the rally order is issued at spawn");
            Assert.That(tankSnapshot.MoveTarget, Is.EqualTo(new WorldPointMm(10_000, 0)));

            server.TickOnce();
            Assert.That(server.TryGetUnit(tank, out var marching), Is.True);
            Assert.That(marching.Position, Is.Not.EqualTo(new WorldPointMm(0, -3000)), "the tank marched away from the exit");
            Assert.That(marching.HasMoveTarget, Is.True);
        }

        // ------------------------------------------------------------------
        // 5 — LowPower throttling on the live server
        // ------------------------------------------------------------------

        [Test]
        public void LowPower_ThrottlesProductionToExactlyEightyTicks()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(server.PowerLedger.RegisterBuilding(PlayerOne, 8000, -1), Is.EqualTo(PowerLedgerRejection.None), "a lone consumer forces NetPower = -1");
            Assert.That(server.TryEnqueueProduce(PlayerOne, factoryId, 7, 40, 0, out _), Is.True);

            server.ExecuteTicks(79);
            Assert.That(server.UnitCount, Is.EqualTo(2), "79 throttled ticks must not spawn the 40-tick unit");
            Assert.That(server.Production.TryGetQueueItem(factoryId, 0, out var almost), Is.True);
            Assert.That(almost.RemainingTicks, Is.EqualTo(1));

            server.TickOnce();
            Assert.That(server.UnitCount, Is.EqualTo(3), "the 80th throttled tick spawns it");
        }

        [Test]
        public void ExemptOwner_UnderDeficit_KeepsFortyTicks()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(server.PowerLedger.RegisterBuilding(PlayerOne, 8000, -1), Is.EqualTo(PowerLedgerRejection.None));
            server.PowerLedger.SetPlayerPowerExempt(PlayerOne, true);
            Assert.That(server.TryEnqueueProduce(PlayerOne, factoryId, 7, 40, 0, out _), Is.True);

            server.ExecuteTicks(39);
            Assert.That(server.UnitCount, Is.EqualTo(2), "GLA doctrine: 39 of 40 ticks must not spawn");
            server.TickOnce();
            Assert.That(server.UnitCount, Is.EqualTo(3), "the exempt owner produces at the full rate");
        }

        // ------------------------------------------------------------------
        // 6 — full roster: the order waits, nothing throws
        // ------------------------------------------------------------------

        [Test]
        public void FullRoster_HoldsFinishedOrderWithoutExceptions()
        {
            var server = CreateFactory(out ulong factoryId);
            // CreateFactory already supplies the far-away P2 opponent that keeps
            // the match non-terminal: factory (1) + opponent (1) + 1022 fillers
            // = 1024 = the hard ceiling.
            for (int i = 0; i < 1022; i++)
            {
                server.SpawnUnit(
                    PlayerOne,
                    StandardStats,
                    new WorldPointMm(-150_000 + (i % 32) * 2_000, -150_000 + (i / 32) * 2_000),
                    500);
            }

            Assert.That(server.UnitCount, Is.EqualTo(1024), "scenario setup: the roster is exactly full");
            Assert.That(server.TryEnqueueProduce(PlayerOne, factoryId, 7, 5, 0, out _), Is.True);

            server.ExecuteTicks(10);

            Assert.That(server.UnitCount, Is.EqualTo(1024), "a full roster must not grow");
            Assert.That(server.Production.TryGetQueueItem(factoryId, 0, out ProductionQueueItem held), Is.True);
            Assert.That(held.RemainingTicks, Is.EqualTo(0), "the order finished its ticks and waits in the head");
            Assert.That(server.Production.TryGetBuildingState(factoryId, out var state), Is.True);
            Assert.That(state.QueueCount, Is.EqualTo(1), "the order is held, not dropped");
        }

        // ------------------------------------------------------------------
        // 7 — a destroyed factory burns its queue (Generals canon)
        // ------------------------------------------------------------------

        [Test]
        public void FactoryDestroyedByCombat_UnregistersEverywhere_QueueBurns()
        {
            var server = CreateFactory(out ulong factoryId);
            Assert.That(server.TryEnqueueProduce(PlayerOne, factoryId, 7, 100, 500, out _), Is.True);
            server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(-150_000, 0), 500); // keep the match non-terminal
            server.SpawnUnit(PlayerTwo, new CombatStats(100, 1000, 12000, 1), new WorldPointMm(3_000, 0), 500, autoAcquire: true);

            int ticks = 0;
            while (ticks < 300)
            {
                server.TickOnce();
                ticks++;
                server.TryGetUnit(new EntityId(factoryId), out var factory);
                if (factory.CurrentHealth == 0)
                {
                    break;
                }
            }

            server.TryGetUnit(new EntityId(factoryId), out var frame);
            Assert.That(frame.CurrentHealth, Is.EqualTo(0), "the scenario needs the factory destroyed");

            server.TickOnce();

            Assert.That(server.Production.TryGetBuildingState(factoryId, out _), Is.False, "the destroyed factory leaves the production registry");
            Assert.That(server.PowerLedger.GetPowerState(PlayerOne).Produced, Is.EqualTo(0), "its power entry is dropped");
            Assert.That(
                server.Placement.ValidatePlacement(new WorldPointMm(0, 0), ShedFootprint),
                Is.EqualTo(PlacementRejection.None),
                "its footprint is un-placed");
            Assert.That(server.Production.Count, Is.EqualTo(0), "the queue burned without any refund");
        }

        // ------------------------------------------------------------------
        // 8 — direct factory registration and zero-GC tick
        // ------------------------------------------------------------------

        [Test]
        public void TryRegisterProductionBuilding_DirectRegistration_GatesBehave()
        {
            var server = new MatchServer();
            Assert.That(server.TryRegisterProductionBuilding(900), Is.False, "an unknown building cannot register");

            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, 1, 100, 0, out _),
                Is.True);
            Assert.That(server.TryRegisterProductionBuilding(900), Is.False, "a frame under construction cannot register");
            Assert.That(server.TryRegisterProductionBuilding(901), Is.False, "an unknown id still cannot register");

            Assert.That(
                server.TryStartBuilding(PlayerOne, 901, new WorldPointMm(50_000, 0), ShedFootprint, ConstructionMethod.Modular, 1000, 100, 0, out _),
                Is.True);
            server.TickOnce();
            Assert.That(server.TryRegisterProductionBuilding(901), Is.True, "a completed non-factory registers on demand");
            Assert.That(server.TryRegisterProductionBuilding(901), Is.False, "a duplicate registration rejects");
            Assert.That(server.TryEnqueueProduce(PlayerOne, 901, 7, 10, 100, out _), Is.True, "the manually registered factory takes orders");
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
        public void ExecuteTicks_WithActiveFactories_IsAllocationFree()
        {
            var server = new MatchServer();
            for (int i = 0; i < 5; i++)
            {
                Assert.That(
                    server.TryStartBuilding(PlayerOne, (ulong)(900 + i), new WorldPointMm(-199_000 + i * 2_000, -199_000), ShedFootprint, ConstructionMethod.Modular, 1000, 1, 0, true, null, out _),
                    Is.True, $"factory {i} must start");
            }

            server.SpawnUnit(PlayerTwo, StandardStats, new WorldPointMm(150_000, 0), 500); // keep the match non-terminal (BEFORE the first tick)
            server.TickOnce(); // all five frames complete and auto-register
            Assert.That(server.Production.Count, Is.EqualTo(5));

            for (int i = 0; i < 5; i++)
            {
                Assert.That(
                    server.TryEnqueueProduce(PlayerOne, (ulong)(900 + i), 7, 2000, 1, out _),
                    Is.True, "2000-tick orders survive the measured window without spawning");
            }

            // Warmup: JIT the whole pipeline.
            server.ExecuteTicks(200);

            Assert.That(
                () => server.ExecuteTicks(1000),
                GcAssert.Not.AllocatingGCMemory(),
                "the server tick with active production must stay allocation-free");

            Assert.That(server.Production.Count, Is.EqualTo(5), "no factory drains inside the window");
            Assert.That(server.UnitCount, Is.EqualTo(6), "no unit spawned during the window (5 factories + opponent)");
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates a completed, auto-registered factory in a single tick:
        /// a Modular frame at 1000‰ per tick finishes on the first TickOnce.
        /// A far-away passive opponent keeps the match non-terminal so later
        /// ExecuteTicks runs keep driving the pipeline.
        /// </summary>
        private static MatchServer CreateFactory(out ulong factoryId)
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 1000, 100, 0, true, null, out ConstructionStartRejection rejection),
                Is.True, $"factory start must succeed: {rejection}");
            // The opponent MUST exist before the first TickOnce: a roster with a
            // single living owner resolves to Victory and every later tick
            // early-exits before construction and production.
            server.SpawnUnit(PlayerTwo, StandardStats, new GlobalFront.Core.Movement.WorldPointMm(-150_000, 0), 500);
            server.TickOnce();
            Assert.That(server.Production.TryGetBuildingState(900, out _), Is.True, "scenario setup: the factory must be registered");
            factoryId = 900;
            return server;
        }

        /// <summary>Ticks until the factory is destroyed by the scripted attacker.</summary>
        private static void KillFactory(MatchServer server, ulong factoryId)
        {
            server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(-150_000, 0), 500); // keep the match non-terminal
            server.SpawnUnit(PlayerTwo, new CombatStats(100, 1000, 12000, 1), new WorldPointMm(3_000, 0), 500, autoAcquire: true);
            int ticks = 0;
            while (ticks < 100)
            {
                server.TickOnce();
                ticks++;
                server.TryGetUnit(new EntityId(factoryId), out var factory);
                if (factory.CurrentHealth == 0)
                {
                    return;
                }
            }

            Assert.Fail("the scenario needs the factory to die in combat");
        }
    }
}
