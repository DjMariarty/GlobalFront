using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Economy;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Structures;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Server
{
    /// <summary>
    /// Step 5.4.2 — server integration of the supply economy and the secondary
    /// income sources: the AdvanceEconomy phase sits between automatic target
    /// acquisition and general movement (RND §1.6), harvesters auto-register
    /// when a factory releases them, registry-driven positions mirror into the
    /// roster so movement replicates, supply centers / drop zones / black
    /// markets auto-register when their frames complete, an infantry captures
    /// a neutral Oil Derrick in 80 uninterrupted ticks, and every payout flows
    /// through the saturating per-player ledger. Every scenario spawns a
    /// passive opponent before the first TickOnce — a single-owner roster
    /// resolves to Victory and every later tick early-exits past the economy.
    /// </summary>
    [TestFixture]
    public sealed class MatchServerEconomyTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);

        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        private static readonly CombatStats StandardStats = new CombatStats(100, 10, 12000, 2);

        private static readonly BuildingFootprint ShedFootprint = new BuildingFootprint(500, 500);

        private const ulong DockId = 910;

        private const ulong CenterId = 920;

        private const ulong DerrickId = 930;

        private const ulong HarvesterId = 921;

        private const ushort HarvesterBlueprint = 1;

        private const ushort HackerBlueprint = 2;

        // ------------------------------------------------------------------
        // wiring: production → auto-registration → position sync
        // ------------------------------------------------------------------

        [Test]
        public void Harvester_AutoRegistersOnProductionCompletion()
        {
            MatchServer server = CreateHarvesterScenario(out ulong harvesterId, dockDollars: 300);

            Assert.That(server.Economy.HarvesterCount, Is.EqualTo(1), "the released truck joined the supply FSM");
            Assert.That(server.TryGetUnit(new EntityId(harvesterId), out var snapshot), Is.True);
            Assert.That(snapshot.UnitKind, Is.EqualTo(UnitKinds.Harvester));

            Assert.That(server.Economy.TryGetHarvesterState(harvesterId, out var state), Is.True);
            Assert.That(state.Owner, Is.EqualTo(PlayerOne));
            Assert.That(state.CapacityDollars, Is.EqualTo(SimulationConstants.DefaultHarvesterCapacityDollars));
            Assert.That(state.State, Is.EqualTo(HarvesterState.Idle), "the FSM starts on the next economy tick, not at spawn");
        }

        [Test]
        public void Harvester_MarchesTowardDock_AndTheRosterPositionSyncs()
        {
            MatchServer server = CreateHarvesterScenario(out ulong harvesterId, dockDollars: 300);

            // e1: transition (no movement); e2..e5: four 500 mm steps from
            // 10 000 down to 8 000. The registry owns the step, the roster
            // mirrors it — if either half is unwired the server position
            // freezes at the spawn point.
            server.ExecuteTicks(5);

            Assert.That(server.TryGetUnit(new EntityId(harvesterId), out var snapshot), Is.True);
            Assert.That(snapshot.Position, Is.EqualTo(new WorldPointMm(8_000, 0)));
            Assert.That(server.Economy.TryGetHarvesterState(harvesterId, out var state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.ToDock));
        }

        [Test]
        public void Harvester_FullCycle_DeliversExactly300Dollars()
        {
            MatchServer server = CreateHarvesterScenario(out _, dockDollars: 300);

            // e1 transition, e2..e9 approach, e10..e49 loading, e50 unload —
            // the same canon timing the registry tests pin down, plus the two
            // setup ticks (factory completion, truck release).
            server.ExecuteTicks(60);

            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(300), "the unload credited the owner");
            PlayerEconomyState ledger = server.Economy.GetPlayerState(PlayerOne);
            Assert.That(ledger.TotalHarvestedCredits, Is.EqualTo(300));
            Assert.That(server.Economy.TryGetDockState(DockId, out SupplyDockSnapshot dock), Is.True);
            Assert.That(dock.RemainingDollars, Is.EqualTo(0), "the dock held exactly one load");
            Assert.That(dock.OccupiedSlotCount, Is.EqualTo(0), "the truck left after the instant unload");
        }

        [Test]
        public void Harvester_CyclesUntilTheDockRunsDry_ThenIdles()
        {
            MatchServer server = CreateHarvesterScenario(out _, dockDollars: 600);

            // Two full cycles: +300 around e50, +300 around e93; the empty
            // dock sends the truck to Idle afterwards.
            server.ExecuteTicks(105);

            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(600));
            Assert.That(server.Economy.TryGetDockState(DockId, out SupplyDockSnapshot dock), Is.True);
            Assert.That(dock.RemainingDollars, Is.EqualTo(0));
            Assert.That(server.Economy.TryGetHarvesterState(HarvesterId, out var state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.Idle), "no dock with reserves — the FSM parks");
        }

        [Test]
        public void Harvester_DeathInCombat_UnregistersAndFreesTheDockSlot()
        {
            MatchServer server = CreateHarvesterScenario(out _, dockDollars: 30_000);

            // The truck is on its slot loading (arrival at e9 + 2 setup ticks);
            // a scripted attacker one-shots it next to the dock.
            server.ExecuteTicks(12);
            Assert.That(server.Economy.TryGetHarvesterState(HarvesterId, out var before), Is.True);
            Assert.That(before.State, Is.EqualTo(HarvesterState.Loading), "scenario setup: the truck must be on its slot");
            Assert.That(server.Economy.TryGetDockState(DockId, out var dockBefore), Is.True);
            Assert.That(dockBefore.OccupiedSlotCount, Is.EqualTo(1));

            server.SpawnUnit(
                PlayerTwo, new CombatStats(100, 1000, 12000, 1), new WorldPointMm(6_100, 0), 500, autoAcquire: true);
            server.ExecuteTicks(6);

            Assert.That(server.Economy.HarvesterCount, Is.EqualTo(0), "the corpse left the registry");
            Assert.That(server.Economy.TryGetDockState(DockId, out SupplyDockSnapshot dockAfter), Is.True);
            Assert.That(dockAfter.OccupiedSlotCount, Is.EqualTo(0), "the slot is freed for the next truck");
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(0), "no posthumous payout");
        }

        [Test]
        public void SupplyCenter_AutoRegistersOnCompletion_PlainBuildingsDoNot()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, CenterId, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, false, null, UnitKinds.SupplyCenter, out var rejection),
                Is.True, $"center start must succeed: {rejection}");
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 940, new WorldPointMm(30_000, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, false, null, out rejection),
                Is.True, $"plain start must succeed: {rejection}");
            SpawnPassiveOpponent(server);

            server.TickOnce();

            Assert.That(server.Economy.CenterCount, Is.EqualTo(1), "only the SupplyCenter archetype registered");
            Assert.That(server.TryGetUnit(new EntityId(CenterId), out var center), Is.True);
            Assert.That(center.UnitKind, Is.EqualTo(UnitKinds.SupplyCenter), "the frame owns its final archetype from tick 0");
        }

        // ------------------------------------------------------------------
        // Oil Derrick: registration and capture
        // ------------------------------------------------------------------

        [Test]
        public void TryRegisterOilDerrick_FailClosedGates()
        {
            var server = new MatchServer();

            Assert.That(server.TryRegisterOilDerrick(0, new WorldPointMm(0, 0), out var rejection), Is.False);
            Assert.That(rejection, Is.EqualTo(EconomyRegistrationRejection.InvalidEntityId));

            Assert.That(server.TryRegisterOilDerrick(DerrickId, new WorldPointMm(0, 0), out rejection), Is.True);
            Assert.That(server.TryRegisterOilDerrick(DerrickId, new WorldPointMm(1_000, 0), out rejection), Is.False);
            Assert.That(rejection, Is.EqualTo(EconomyRegistrationRejection.DuplicateEntity));

            // 32 derricks max; the 33rd is refused.
            Assert.That(server.SecondaryEconomy.DerrickCount, Is.EqualTo(1));
            for (var index = 0; index < SimulationConstants.MaxOilDerricks - 1; index++)
            {
                Assert.That(
                    server.TryRegisterOilDerrick((ulong)(2000 + index), new WorldPointMm(0, 0), out rejection),
                    Is.True);
            }

            Assert.That(server.TryRegisterOilDerrick(3000, new WorldPointMm(0, 0), out rejection), Is.False);
            Assert.That(rejection, Is.EqualTo(EconomyRegistrationRejection.RegistryFull));
        }

        [Test]
        public void EntityIdSpace_IsSharedAcrossEconomyKinds()
        {
            var server = new MatchServer();
            Assert.That(server.TryRegisterSupplyDock(DockId, new WorldPointMm(0, 0), 30_000, out _), Is.True);
            Assert.That(
                server.TryRegisterOilDerrick(DockId, new WorldPointMm(0, 0), out var rejection), Is.False,
                "a dock and a derrick cannot share one entity id");
            Assert.That(rejection, Is.EqualTo(EconomyRegistrationRejection.DuplicateEntity));
        }

        [Test]
        public void Capture_CompletesAfterExactly80UninterruptedTicks()
        {
            MatchServer server = CreateDerrickScenario(out ulong infantryId, out _);
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, DerrickId), Is.True);

            server.ExecuteTicks(79);
            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var state), Is.True);
            Assert.That(state.OccupationTicksProgress, Is.EqualTo(79), "one progress step per economy tick");
            Assert.That(state.CurrentOwner.IsValid, Is.False, "ownership flips only on the 80th tick");

            server.ExecuteTicks(1);
            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out state), Is.True);
            Assert.That(state.CurrentOwner, Is.EqualTo(PlayerOne), "80 ticks of occupation flip the derrick");
            Assert.That(state.OccupierEntityId, Is.EqualTo(0UL), "the capture attempt closes on success");
            Assert.That(state.OccupationTicksProgress, Is.EqualTo(0));
        }

        [Test]
        public void Capture_DamagedOccupier_CollapsesToZero()
        {
            // A tanky occupier: the point is the damage signal, not the kill —
            // the infantry must outlive the whole probe.
            MatchServer server = CreateDerrickScenario(
                out ulong infantryId, out _, new CombatStats(10_000, 10, 12000, 2));
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, DerrickId), Is.True);
            server.ExecuteTicks(5);
            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var before), Is.True);
            Assert.That(before.OccupationTicksProgress, Is.GreaterThan(0), "scenario setup: occupation must be running");

            // A chip-damage shooter: the occupier survives, the attempt does not.
            server.SpawnUnit(
                PlayerTwo, new CombatStats(100, 10, 12000, 1), new WorldPointMm(5_000, 0), 500, autoAcquire: true);
            server.ExecuteTicks(10);

            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var after), Is.True);
            Assert.That(after.OccupationTicksProgress, Is.EqualTo(0), "any damage snaps the progress to zero");
            Assert.That(after.OccupierEntityId, Is.EqualTo(0UL));
            Assert.That(server.TryGetUnit(new EntityId(infantryId), out var infantry), Is.True);
            Assert.That(infantry.CurrentHealth, Is.GreaterThan(0), "the occupier survived — only the attempt died");

            server.ExecuteTicks(80);
            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var settled), Is.True);
            Assert.That(settled.CurrentOwner.IsValid, Is.False, "a reset attempt never completes on its own");
        }

        [Test]
        public void Capture_OccupierLeavingTheRadius_CollapsesToZero()
        {
            MatchServer server = CreateDerrickScenario(out ulong infantryId, out _);
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, DerrickId), Is.True);
            server.ExecuteTicks(5);

            EnqueueMove(server, PlayerOne, 1, 6, new[] { new EntityId(infantryId) }, new WorldPointMm(50_000, 0));
            server.ExecuteTicks(25);

            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var state), Is.True);
            Assert.That(state.OccupationTicksProgress, Is.EqualTo(0), "leaving the 6000 mm radius is a reset");
            Assert.That(state.OccupierEntityId, Is.EqualTo(0UL));
        }

        [Test]
        public void Capture_DeadOccupier_CollapsesToZero_AndKeepsTheDerrick()
        {
            MatchServer server = CreateDerrickScenario(out ulong infantryId, out _);
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, DerrickId), Is.True);

            // An anchor owner keeps the match non-terminal after the occupier
            // dies: a single-owner roster resolves to Victory and every later
            // tick early-exits before the registry can observe the corpse.
            server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(100_000, 0), 500);
            server.SpawnUnit(
                PlayerTwo, new CombatStats(100, 1000, 12000, 1), new WorldPointMm(3_200, 0), 500, autoAcquire: true);
            server.ExecuteTicks(6);

            Assert.That(server.TryGetUnit(new EntityId(infantryId), out var infantry), Is.True);
            Assert.That(infantry.CurrentHealth, Is.EqualTo(0), "scenario setup: the occupier must be dead");
            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var state), Is.True);
            Assert.That(state.OccupierEntityId, Is.EqualTo(0UL));
            Assert.That(state.OccupationTicksProgress, Is.EqualTo(0));
            Assert.That(server.SecondaryEconomy.DerrickCount, Is.EqualTo(1), "the derrick itself is a world object — it survives");
        }

        [Test]
        public void Derrick_PaysOwnerExactly100DollarsEvery40Ticks()
        {
            MatchServer server = CreateDerrickScenario(out ulong infantryId, out _);
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, DerrickId), Is.True);

            server.ExecuteTicks(120); // capture at e80, first payout inside the next 40-tick window
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(100));

            server.ExecuteTicks(40);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(200));

            server.ExecuteTicks(40);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(300), "the cadence is exact: +100 per 40 ticks");
        }

        [Test]
        public void Derrick_NeutralPaysNobody()
        {
            var server = new MatchServer();
            Assert.That(server.TryRegisterOilDerrick(DerrickId, new WorldPointMm(0, 0), out _), Is.True);
            SpawnPassiveOpponent(server);

            server.ExecuteTicks(200);

            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(0));
            Assert.That(server.Economy.GetCredits(PlayerTwo), Is.EqualTo(0));
            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var state), Is.True);
            Assert.That(state.CurrentOwner.IsValid, Is.False, "nobody occupied the derrick — it stays neutral");
        }

        [Test]
        public void TryOrderCapture_FailClosedOnRosterValidation()
        {
            MatchServer server = CreateDerrickScenario(out ulong infantryId, out _);

            var tank = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(3_000, 500), 500, unitKind: UnitKinds.Tank);
            Assert.That(server.TryOrderCapture(PlayerOne, tank.Value, DerrickId), Is.False, "only infantry captures");
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, 999), Is.False, "unknown derrick");
            Assert.That(server.TryOrderCapture(PlayerTwo, infantryId, DerrickId), Is.False, "foreign unit");
            Assert.That(server.TryOrderCapture(new PlayerId(0), infantryId, DerrickId), Is.False, "neutral is not an owner");
        }

        [Test]
        public void TryOrderCapture_OwnershipConflicts_AreRefused()
        {
            MatchServer server = CreateDerrickScenario(out ulong infantryId, out _);

            // A live attempt by another player cannot be hijacked.
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, DerrickId), Is.True);
            server.ExecuteTicks(5);
            var rival = server.SpawnUnit(PlayerTwo, StandardStats, new WorldPointMm(3_100, 0), 500, unitKind: UnitKinds.Infantry);
            Assert.That(server.TryOrderCapture(PlayerTwo, rival.Value, DerrickId), Is.False, "the slot is taken");

            // After a completed capture the owner refuses its own re-capture.
            server.ExecuteTicks(80);
            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var state), Is.True);
            Assert.That(state.CurrentOwner, Is.EqualTo(PlayerOne));
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, DerrickId), Is.False, "already owned");
        }

        [Test]
        public void Capture_RecaptureByAnotherPlayer_FlipsOwnershipAndPayouts()
        {
            MatchServer server = CreateDerrickScenario(out ulong infantryId, out _);
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, DerrickId), Is.True);
            server.ExecuteTicks(80);
            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var captured), Is.True);
            Assert.That(captured.CurrentOwner, Is.EqualTo(PlayerOne));

            var rival = server.SpawnUnit(PlayerTwo, StandardStats, new WorldPointMm(3_100, 0), 500, unitKind: UnitKinds.Infantry);
            Assert.That(server.TryOrderCapture(PlayerTwo, rival.Value, DerrickId), Is.True, "a rival may start a fresh attempt");
            server.ExecuteTicks(80);

            Assert.That(server.SecondaryEconomy.TryGetDerrickState(DerrickId, out var flipped), Is.True);
            Assert.That(flipped.CurrentOwner, Is.EqualTo(PlayerTwo), "80 uninterrupted rival ticks flip the derrick");

            server.ExecuteTicks(40);
            Assert.That(server.Economy.GetCredits(PlayerTwo), Is.EqualTo(100), "payouts follow the new owner");
            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(200),
                "the previous owner kept collecting while it still held the derrick (payouts at e119 and e159)");
        }

        // ------------------------------------------------------------------
        // income buildings and the hacker
        // ------------------------------------------------------------------

        [Test]
        public void DropZone_Pays1500DollarsEvery1200Ticks()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 940, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, false, null, UnitKinds.DropZone, out var rejection),
                Is.True, $"drop zone start must succeed: {rejection}");
            SpawnPassiveOpponent(server);

            server.TickOnce();
            Assert.That(server.SecondaryEconomy.IncomeBuildingCount, Is.EqualTo(1), "the completed frame auto-registered");

            server.ExecuteTicks(1210); // the first payout lands inside this window
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(1500));

            server.ExecuteTicks(1200);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(3000), "one payout per 1200-tick window, exactly");
        }

        [Test]
        public void BlackMarket_Pays50DollarsEvery80Ticks()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 940, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, false, null, UnitKinds.BlackMarket, out var rejection),
                Is.True, $"black market start must succeed: {rejection}");
            SpawnPassiveOpponent(server);

            server.TickOnce();

            server.ExecuteTicks(90);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(50));

            server.ExecuteTicks(80);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(100));
        }

        [Test]
        public void IncomeBuilding_DeathStopsPayments_AndDeregisters()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 940, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, false, null, UnitKinds.DropZone, out _),
                Is.True);
            SpawnPassiveOpponent(server);
            // An anchor owner keeps the match non-terminal once the building
            // dies — the deregistration tick must still run.
            server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(100_000, 0), 500);
            server.TickOnce();

            server.ExecuteTicks(1210);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(1500), "scenario setup: one payout landed");

            server.SpawnUnit(
                PlayerTwo, new CombatStats(100, 2000, 12000, 1), new WorldPointMm(1_000, 0), 500, autoAcquire: true);
            server.ExecuteTicks(10);
            Assert.That(server.SecondaryEconomy.IncomeBuildingCount, Is.EqualTo(0), "the destroyed building deregistered");

            server.ExecuteTicks(1210);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(1500), "a dead drop zone pays nothing");
        }

        [Test]
        public void Hacker_Pays20DollarsEvery40Ticks_WhileAliveAndIdle()
        {
            MatchServer server = CreateHackerScenario(out ulong hackerId);

            Assert.That(server.SecondaryEconomy.HackerCount, Is.EqualTo(1), "the produced hacker auto-registered");

            server.ExecuteTicks(45);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(20));

            server.ExecuteTicks(40);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(40));
            Assert.That(server.TryGetUnit(new EntityId(hackerId), out var snapshot), Is.True);
            Assert.That(snapshot.UnitKind, Is.EqualTo(UnitKinds.Hacker));
        }

        [Test]
        public void Hacker_AttackingFreezesThePayoutTimer_ItResumesWhenIdle()
        {
            MatchServer server = CreateHackerScenario(out ulong hackerId, out ulong targetId);

            Assert.That(
                server.TryEnqueueAttack(
                    new CommandHeader(PlayerOne, 1, 3, GameCommandType.Attack),
                    new[] { new EntityId(hackerId) },
                    new EntityId(targetId)),
                Is.EqualTo(MatchCommandRejection.None));

            server.ExecuteTicks(80);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(0), "an attacking hacker pays nothing");

            // A Move order is the canonical way back to idle: ExecuteMove
            // clears the attack target AND the auto-acquire stance (a Stop
            // would not — ExecuteAttack turned the aggressive stance on, and
            // acquisition would immediately re-target). The move lands on
            // tick 83, the first tick of the next window; the frozen timer
            // resumes from where it froze.
            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(PlayerOne, 2, 83, GameCommandType.Move),
                    new[] { new EntityId(hackerId) },
                    new WorldPointMm(10_000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            server.ExecuteTicks(50);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(20), "idle again — the timer resumed");
        }

        [Test]
        public void Hacker_DeathStopsPayments_AndDeregisters()
        {
            MatchServer server = CreateHackerScenario(out ulong hackerId, out ulong targetId);
            // An anchor owner keeps the match non-terminal once the hacker and
            // the factory die — the deregistration tick must still run.
            server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(100_000, 0), 500);

            server.ExecuteTicks(45);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(20), "scenario setup: one payout landed");

            server.SpawnUnit(
                PlayerTwo, new CombatStats(100, 1000, 12000, 1), new WorldPointMm(500, 0), 500, autoAcquire: true);
            server.ExecuteTicks(10);
            Assert.That(server.SecondaryEconomy.HackerCount, Is.EqualTo(0), "the dead hacker deregistered");

            server.ExecuteTicks(80);
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.EqualTo(20), "a dead hacker pays nothing");
        }

        // ------------------------------------------------------------------
        // zero-GC
        // ------------------------------------------------------------------

        [Test]
        public void AdvanceEconomy_IsAllocationFree_Over1000Ticks()
        {
            MatchServer server = CreateFullEconomyScenario(out ulong harvesterId, out ulong infantryId);
            Assert.That(server.TryOrderCapture(PlayerOne, infantryId, DerrickId), Is.True);
            server.ExecuteTicks(200); // warmup + steady state: capture done, truck cycling, payouts flowing

            Assert.That(
                () => server.ExecuteTicks(1000),
                GcAssert.Not.AllocatingGCMemory(),
                "the economy tick is a per-tick hot path and must be allocation-free");
            Assert.That(server.Economy.GetCredits(PlayerOne), Is.GreaterThan(0), "sanity: the economy actually ran");
            Assert.That(server.Economy.TryGetHarvesterState(harvesterId, out _), Is.True, "the truck survived the run");
        }

        // ------------------------------------------------------------------
        // scenario helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Factory at the origin releasing trucks onto the dock road (exit at
        /// 10 000 mm), a neutral dock at the origin and a friendly supply
        /// center at 12 000 mm — the canon full-cycle geometry of the registry
        /// tests, at 500 mm per tick.
        /// </summary>
        private static MatchServer CreateHarvesterScenario(out ulong harvesterId, int dockDollars)
        {
            var server = new MatchServer();
            SpawnPassiveOpponent(server); // must exist before the first TickOnce

            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, true, new WorldPointMm(10_000, 0), out var rejection),
                Is.True, $"factory start must succeed: {rejection}");
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, CenterId, new WorldPointMm(12_000, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, false, null, UnitKinds.SupplyCenter, out rejection),
                Is.True, $"center start must succeed: {rejection}");
            server.RegisterBlueprint(HarvesterBlueprint, StandardStats, 500, UnitKinds.Harvester, autoAcquire: false);
            Assert.That(server.TryRegisterSupplyDock(DockId, new WorldPointMm(0, 0), dockDollars, out _), Is.True);

            server.TickOnce(); // factory + center complete and auto-register
            Assert.That(server.Production.TryGetBuildingState(900, out _), Is.True, "scenario setup: the factory must be registered");
            Assert.That(server.Economy.CenterCount, Is.EqualTo(1), "scenario setup: the center must be registered");

            Assert.That(
                server.TryEnqueueProduce(PlayerOne, 900, HarvesterBlueprint, 1, 0, out var produceRejection),
                Is.True, $"produce must succeed: {produceRejection}");
            server.TickOnce(); // the truck spawns at (10 000, 0) and auto-registers
            harvesterId = HarvesterId; // deterministic: passive=1, factory=900, center=920, truck=921

            Assert.That(server.TryGetUnit(new EntityId(harvesterId), out var truck), Is.True);
            Assert.That(truck.UnitKind, Is.EqualTo(UnitKinds.Harvester), "scenario setup: the truck must be the produced unit");
            Assert.That(truck.Position, Is.EqualTo(new WorldPointMm(10_000, 0)));
            return server;
        }

        /// <summary>Factory producing a single Hacker; the P2 target (if any) spawns at 5000 mm.</summary>
        private static MatchServer CreateHackerScenario(out ulong hackerId, out ulong targetId)
        {
            var server = new MatchServer();
            // The mandatory opponent doubles as the attack target: tough enough
            // to survive 80 ticks of hacker fire (10 damage every 2nd tick).
            targetId = server.SpawnUnit(
                    PlayerTwo, new CombatStats(10_000, 10, 12000, 2), new WorldPointMm(5_000, 0), 500).Value;

            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, true, null, out var rejection),
                Is.True, $"factory start must succeed: {rejection}");
            server.RegisterBlueprint(HackerBlueprint, StandardStats, 500, UnitKinds.Hacker, autoAcquire: false);

            server.TickOnce();
            Assert.That(
                server.TryEnqueueProduce(PlayerOne, 900, HackerBlueprint, 1, 0, out var produceRejection),
                Is.True, $"produce must succeed: {produceRejection}");
            server.TickOnce();
            hackerId = 901; // deterministic: target=1, factory=900, hacker=901

            Assert.That(server.TryGetUnit(new EntityId(hackerId), out var hacker), Is.True);
            Assert.That(hacker.UnitKind, Is.EqualTo(UnitKinds.Hacker), "scenario setup: the hacker must be the produced unit");
            return server;
        }

        private static MatchServer CreateHackerScenario(out ulong hackerId)
        {
            return CreateHackerScenario(out hackerId, out _);
        }

        /// <summary>Neutral derrick at the origin; a P1 infantry sits inside the 6000 mm radius.</summary>
        private static MatchServer CreateDerrickScenario(
            out ulong infantryId,
            out ulong secondInfantryId,
            CombatStats? infantryStats = null)
        {
            var server = new MatchServer();
            SpawnPassiveOpponent(server);
            Assert.That(server.TryRegisterOilDerrick(DerrickId, new WorldPointMm(0, 0), out _), Is.True);

            infantryId = server.SpawnUnit(
                PlayerOne,
                infantryStats ?? StandardStats,
                new WorldPointMm(3_000, 0),
                500,
                unitKind: UnitKinds.Infantry).Value;
            secondInfantryId = infantryId;
            return server;
        }

        /// <summary>
        /// Steady-state economy for the zero-GC probe: cycling truck, owned
        /// derrick (after the capture settles), drop zone, black market and an
        /// idle hacker.
        /// </summary>
        private static MatchServer CreateFullEconomyScenario(out ulong harvesterId, out ulong infantryId)
        {
            MatchServer server = CreateHarvesterScenario(out harvesterId, dockDollars: 30_000);
            Assert.That(server.TryRegisterOilDerrick(DerrickId, new WorldPointMm(50_000, 0), out _), Is.True);
            infantryId = server.SpawnUnit(
                PlayerOne, StandardStats, new WorldPointMm(53_000, 0), 500, unitKind: UnitKinds.Infantry).Value;
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 940, new WorldPointMm(30_000, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, false, null, UnitKinds.DropZone, out _),
                Is.True);
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 950, new WorldPointMm(-30_000, 0), ShedFootprint, ConstructionMethod.Modular,
                    1000, 100, 0, false, null, UnitKinds.BlackMarket, out _),
                Is.True);
            server.RegisterBlueprint(HackerBlueprint, StandardStats, 500, UnitKinds.Hacker, autoAcquire: false);
            Assert.That(server.TryEnqueueProduce(PlayerOne, 900, HackerBlueprint, 1, 0, out _), Is.True);

            server.TickOnce(); // drop zone + black market complete; hacker spawns
            return server;
        }

        /// <summary>
        /// A second owner keeps the battle outcome in progress, which lets the
        /// tests observe the economy without the match terminating early.
        /// </summary>
        private static void SpawnPassiveOpponent(MatchServer server) =>
            server.SpawnUnit(
                PlayerTwo,
                StandardStats,
                new WorldPointMm(-150_000, 0),
                500);

        private static void EnqueueMove(
            MatchServer server,
            PlayerId player,
            uint sequence,
            ulong requestedTick,
            EntityId[] units,
            WorldPointMm destination)
        {
            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(player, sequence, requestedTick, GameCommandType.Move),
                    units,
                    destination,
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));
        }
    }
}
