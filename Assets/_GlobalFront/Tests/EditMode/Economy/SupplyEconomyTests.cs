using System;
using GlobalFront.Core.Economy;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Economy
{
    /// <summary>
    /// Step 5.4.1 — supply economy registry: neutral docks with finite reserves
    /// and 4 loading slots, player centers with instant unloading, and the
    /// deterministic harvester FSM. All geometry in these tests is axis-aligned
    /// so every StepTowards position is trivially computable (±500 mm steps).
    /// Transition ticks never move the harvester; movement begins the tick
    /// after the state was set. Integer-only; the per-tick path must stay
    /// allocation-free (0 B GC.Alloc).
    /// </summary>
    [TestFixture]
    public sealed class SupplyEconomyTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);

        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        private const int DockId = 100;

        private const int CenterId = 200;

        // ------------------------------------------------------------------
        // 1 — protocol values
        // ------------------------------------------------------------------

        [Test]
        public void EconomyEnums_CarryTheDocumentedNumericValues()
        {
            Assert.That((byte)HarvesterState.Idle, Is.EqualTo(0));
            Assert.That((byte)HarvesterState.ToDock, Is.EqualTo(1));
            Assert.That((byte)HarvesterState.WaitSlot, Is.EqualTo(2));
            Assert.That((byte)HarvesterState.Loading, Is.EqualTo(3));
            Assert.That((byte)HarvesterState.ToCenter, Is.EqualTo(4));
            Assert.That((byte)HarvesterState.Unload, Is.EqualTo(5), "reserved: unloading is instant and never persisted");
            Assert.That((byte)EconomyRegistrationRejection.None, Is.EqualTo(0));
            Assert.That((byte)EconomyRegistrationRejection.InvalidEntityId, Is.EqualTo(1));
            Assert.That((byte)EconomyRegistrationRejection.InvalidOwner, Is.EqualTo(2));
            Assert.That((byte)EconomyRegistrationRejection.DuplicateEntity, Is.EqualTo(3));
            Assert.That((byte)EconomyRegistrationRejection.RegistryFull, Is.EqualTo(4));
            Assert.That((byte)EconomyRegistrationRejection.EntityNotFound, Is.EqualTo(5));
        }

        // ------------------------------------------------------------------
        // 2 — registration gates
        // ------------------------------------------------------------------

        [Test]
        public void TryRegisterDock_RejectsInvalidInput()
        {
            var registry = new SupplyEconomyRegistry();
            Assert.That(
                registry.TryRegisterDock(0, new WorldPointMm(0, 0), 30_000, out EconomyRegistrationRejection zero),
                Is.False);
            Assert.That(zero, Is.EqualTo(EconomyRegistrationRejection.InvalidEntityId), "0 is reserved as 'no entity'");

            Assert.That(
                registry.TryRegisterDock(100, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 30_000, out _),
                Is.True);
            Assert.That(
                registry.TryRegisterDock(100, new GlobalFront.Core.Movement.WorldPointMm(50_000, 0), 10_000, out EconomyRegistrationRejection duplicate),
                Is.False);
            Assert.That(duplicate, Is.EqualTo(EconomyRegistrationRejection.DuplicateEntity));
            Assert.That(registry.DockCount, Is.EqualTo(1));
        }

        [Test]
        public void TryRegisterDock_RejectsNonPositiveReserve()
        {
            var registry = new SupplyEconomyRegistry();
            Assert.Throws<ArgumentOutOfRangeException>(
                () => registry.TryRegisterDock(100, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 0, out _));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => registry.TryRegisterDock(100, new GlobalFront.Core.Movement.WorldPointMm(0, 0), -5, out _));
        }

        [Test]
        public void TryRegisterCenter_RejectsInvalidOwner()
        {
            var registry = new SupplyEconomyRegistry();
            Assert.That(
                registry.TryRegisterCenter(200, new PlayerId(0), new GlobalFront.Core.Movement.WorldPointMm(0, 0), out EconomyRegistrationRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(EconomyRegistrationRejection.InvalidOwner));
        }

        [Test]
        public void TryRegister_DuplicateEntityId_IsRejectedAcrossAllTables()
        {
            var registry = new SupplyEconomyRegistry();
            Assert.That(registry.TryRegisterDock(100, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 30_000, out _), Is.True);
            Assert.That(
                registry.TryRegisterCenter(100, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(50_000, 0), out EconomyRegistrationRejection centerDuplicate),
                Is.False, "entity ids are global — a dock id cannot be reused by a center");
            Assert.That(centerDuplicate, Is.EqualTo(EconomyRegistrationRejection.DuplicateEntity));
            Assert.That(
                registry.TryRegisterHarvester(100, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 500, 300, out EconomyRegistrationRejection harvesterDuplicate),
                Is.False);
            Assert.That(harvesterDuplicate, Is.EqualTo(EconomyRegistrationRejection.DuplicateEntity));
        }

        [Test]
        public void TryRegisterHarvester_PastCapacity_RejectsWithRegistryFull()
        {
            var registry = new SupplyEconomyRegistry();
            for (int i = 0; i < SimulationConstants.MaxHarvesters; i++)
            {
                Assert.That(
                    registry.TryRegisterHarvester((ulong)(i + 1), PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(i * 1_000, 0), 500, 300, out _),
                    Is.True, $"harvester {i} must register");
            }

            Assert.That(
                registry.TryRegisterHarvester((ulong)(SimulationConstants.MaxHarvesters + 1), PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 500, 300, out EconomyRegistrationRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(EconomyRegistrationRejection.RegistryFull));
        }

        // ------------------------------------------------------------------
        // 3 — unregister: bindings released, survivors re-target
        // ------------------------------------------------------------------

        [Test]
        public void UnregisterEntity_Dock_BoundHarvestersFallBackToIdle()
        {
            var registry = new SupplyEconomyRegistry();
            registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 30_000, out _);
            Assert.That(registry.TryRegisterHarvester(300, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(1_000, 0), 500, 300, out _), Is.True);

            registry.AdvanceTick(); // Idle → ToDock
            Assert.That(registry.TryGetHarvesterState(300, out var state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.ToDock));

            Assert.That(registry.UnregisterEntity(DockId), Is.True);
            Assert.That(registry.TryGetHarvesterState(300, out state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.Idle), "a bound harvester falls back to Idle with its cargo kept");
            Assert.That(registry.DockCount, Is.EqualTo(0));
        }

        [Test]
        public void UnregisterEntity_Harvester_FreesItsSlotAndQueuePosition()
        {
            var registry = new SupplyEconomyRegistry();
            registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 30_000, out _);

            // Four harvesters right at the dock: t1 is the ToDock transition,
            // t2 moves and arrives (1 000 mm is inside the 6 000 mm radius).
            for (int i = 0; i < 4; i++)
            {
                Assert.That(
                    registry.TryRegisterHarvester((ulong)(300 + i), PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(1_000, 0), 500, 300, out _),
                    Is.True);
            }

            // The fifth queues, the sixth would sit beyond a full queue — here we
            // verify a queued harvester's removal frees its queue position.
            Assert.That(registry.TryRegisterHarvester(305, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(1_000, 0), 500, 300, out _), Is.True);

            registry.AdvanceTick(); // t1: ToDock transitions
            registry.AdvanceTick(); // t2: arrivals — 300..303 load, 305 waits
            Assert.That(registry.TryGetDockState(DockId, out var dock), Is.True);
            Assert.That(dock.OccupiedSlotCount, Is.EqualTo(4));
            Assert.That(dock.WaitingQueueCount, Is.EqualTo(1));

            Assert.That(registry.UnregisterEntity(305), Is.True, "the queued harvester unregisters");
            Assert.That(registry.TryGetDockState(DockId, out dock), Is.True);
            Assert.That(dock.WaitingQueueCount, Is.EqualTo(0), "its queue position is freed");

            Assert.That(registry.UnregisterEntity(301), Is.True, "a loading harvester unregisters");
            Assert.That(registry.TryGetDockState(DockId, out dock), Is.True);
            Assert.That(dock.OccupiedSlotCount, Is.EqualTo(3), "its slot is freed");
        }

        [Test]
        public void UnregisterEntity_Center_BoundHarvesterRetargetsOrIdles()
        {
            var registry = new SupplyEconomyRegistry();
            registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 300, out _); // tiny reserve
            registry.TryRegisterCenter(CenterId, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(12_000, 0), out _);
            Assert.That(registry.TryRegisterHarvester(300, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(1_000, 0), 500, 300, out _), Is.True);

            // t2 arrival, loading t3..t42, t43: ToCenter with the full cargo.
            for (int tick = 1; tick <= 49; tick++)
            {
                registry.AdvanceTick();
            }

            Assert.That(registry.TryGetHarvesterState(300, out var state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.ToCenter), "the scenario needs cargo in flight to the center");
            Assert.That(state.CarriedDollars, Is.EqualTo(300));

            Assert.That(registry.UnregisterEntity(CenterId), Is.True);
            for (int tick = 0; tick < 5; tick++)
            {
                registry.AdvanceTick(); // re-seek finds no friendly center → Idle
            }

            Assert.That(registry.TryGetHarvesterState(300, out state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.Idle), "no friendly center remains — the harvester idles with its cargo");
        }

        // ------------------------------------------------------------------
        // 4 — credit ledger and saturation
        // ------------------------------------------------------------------

        [Test]
        public void CreditLedger_SetGetAndSpend_BehaveAtomically()
        {
            var registry = new SupplyEconomyRegistry();
            Assert.That(registry.GetCredits(PlayerOne), Is.EqualTo(0), "a fresh ledger starts empty");

            registry.SetCredits(PlayerOne, 5_000);
            Assert.That(registry.GetCredits(PlayerOne), Is.EqualTo(5_000));

            Assert.That(registry.TrySpendCredits(PlayerOne, 2_000), Is.True);
            Assert.That(registry.GetCredits(PlayerOne), Is.EqualTo(3_000));

            Assert.That(registry.TrySpendCredits(PlayerOne, 9_999), Is.False, "spending beyond the balance fails atomically");
            Assert.That(registry.GetCredits(PlayerOne), Is.EqualTo(3_000), "a failed spend changes nothing");

            Assert.That(registry.TrySpendCredits(PlayerOne, -1), Is.False, "a negative cost is refused");
        }

        [Test]
        public void AddCredits_SaturatesAtMaxCredits()
        {
            var registry = new SupplyEconomyRegistry();
            registry.SetCredits(PlayerOne, SimulationConstants.MaxCredits - 1);

            registry.AddCredits(PlayerOne, 100);

            Assert.That(registry.GetCredits(PlayerOne), Is.EqualTo(SimulationConstants.MaxCredits), "the balance saturates at MaxCredits without int overflow");
        }

        // ------------------------------------------------------------------
        // 5 — the full cycle: dock → 4-slot loading → center → back
        // ------------------------------------------------------------------

        [Test]
        public void FullCycle_TransitionsIdleToToDock_OnTheFirstTick()
        {
            var registry = new SupplyEconomyRegistry();
            registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 30_000, out _);
            registry.TryRegisterCenter(CenterId, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(12_000, 0), out _);
            Assert.That(registry.TryRegisterHarvester(300, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(10_000, 0), 500, 300, out _), Is.True);

            Assert.That(registry.TryGetHarvesterState(300, out var before), Is.True);
            Assert.That(before.State, Is.EqualTo(HarvesterState.Idle));

            registry.AdvanceTick();

            Assert.That(registry.TryGetHarvesterState(300, out var after), Is.True);
            Assert.That(after.State, Is.EqualTo(HarvesterState.ToDock), "the first tick seeks the nearest non-empty dock");
            Assert.That(after.TargetDockId, Is.EqualTo(DockId));
        }

        [Test]
        public void FullCycle_ApproachArrivesOnTick9_AndOccupiesSlotZero()
        {
            var registry = CreateFullCycleEconomy();

            // t1 transition (no movement); t2..t9: eight 500 mm steps from
            // 10 000 down to 6 000 — inside the 6 000 mm interaction radius.
            for (int tick = 1; tick <= 9; tick++)
            {
                registry.AdvanceTick();
            }

            Assert.That(registry.TryGetHarvesterState(300, out var state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.Loading));
            Assert.That(state.LoadingTicksRemaining, Is.EqualTo(SimulationConstants.DefaultLoadingDurationTicks), "the countdown starts at exactly 40");
            Assert.That(registry.TryGetDockState(DockId, out var dock), Is.True);
            Assert.That(dock.OccupiedSlotCount, Is.EqualTo(1), "slot 0 is occupied");
        }

        [Test]
        public void FullCycle_LoadingLastsExactlyFortyTicks_ThenCargoIsTaken()
        {
            var registry = CreateFullCycleEconomy();

            registry.AdvanceTick(); // t1: ToDock
            for (int tick = 2; tick <= 9; tick++)
            {
                registry.AdvanceTick(); // t9: arrival, Loading starts
            }

            for (int tick = 10; tick <= 48; tick++)
            {
                registry.AdvanceTick();
            }

            Assert.That(registry.TryGetHarvesterState(300, out var almost), Is.True);
            Assert.That(almost.LoadingTicksRemaining, Is.EqualTo(1), "39 of the 40 loading ticks elapsed");
            Assert.That(almost.CarriedDollars, Is.EqualTo(0), "the atomic withdrawal happens only when the countdown hits zero");
            Assert.That(registry.TryGetDockState(DockId, out var dock), Is.True);
            Assert.That(dock.RemainingDollars, Is.EqualTo(30_000), "the dock is untouched until loading completes");

            registry.AdvanceTick(); // t49: the 40th tick

            Assert.That(registry.TryGetHarvesterState(300, out var loaded), Is.True);
            Assert.That(loaded.CarriedDollars, Is.EqualTo(300), "the atomic withdrawal takes exactly the capacity");
            Assert.That(loaded.State, Is.EqualTo(HarvesterState.ToCenter), "loading complete — head to the nearest friendly center");
            Assert.That(registry.TryGetDockState(DockId, out dock), Is.True);
            Assert.That(dock.RemainingDollars, Is.EqualTo(29_700), "30 000 − 300 = 29 700");
        }

        [Test]
        public void FullCycle_UnloadIsInstant_AndTheHarvesterTurnsBack()
        {
            var registry = new SupplyEconomyRegistry();
            // A distant center (20 000 mm from the dock): the harvester cannot
            // reach it within the dock radius — the unload lands on tick 65.
            Assert.That(
                registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 30_000, out _),
                Is.True);
            Assert.That(
                registry.TryRegisterCenter(CenterId, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(20_000, 0), out _),
                Is.True);
            Assert.That(
                registry.TryRegisterHarvester(300, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(10_000, 0), 500, 300, out _),
                Is.True);

            // t1 transition, t2..t9 approach, t10..t49 loading, t50..t64 center approach.
            for (int tick = 1; tick <= 64; tick++)
            {
                registry.AdvanceTick();
            }

            Assert.That(registry.TryGetHarvesterState(300, out var state), Is.True);
            Assert.That(state.CarriedDollars, Is.EqualTo(300), "the scenario needs cargo in flight");
            Assert.That(state.State, Is.EqualTo(HarvesterState.ToCenter));

            registry.AdvanceTick(); // t65: distance to the center hits the 6 000 mm radius

            Assert.That(registry.TryGetHarvesterState(300, out state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.ToDock), "unloading is instant — the harvester immediately turns back to the dock");
            Assert.That(state.CarriedDollars, Is.EqualTo(0));
            Assert.That(state.TargetDockId, Is.EqualTo(DockId), "the dock still has reserves — return to it");
            Assert.That(registry.GetCredits(PlayerOne), Is.EqualTo(300), "the unload credited the owner");
        }

        [Test]
        public void FullCycle_TotalHarvestedTracksOnlyIncome()
        {
            var registry = CreateFullCycleEconomy();
            for (int tick = 1; tick <= 57; tick++)
            {
                registry.AdvanceTick(); // t57: the first unload lands
            }

            PlayerEconomyState economy = registry.GetPlayerState(PlayerOne);
            Assert.That(economy.Credits, Is.EqualTo(300));
            Assert.That(economy.TotalHarvestedCredits, Is.EqualTo(300), "harvested income is accumulated");

            Assert.That(registry.TrySpendCredits(PlayerOne, 100), Is.True);
            economy = registry.GetPlayerState(PlayerOne);
            Assert.That(economy.Credits, Is.EqualTo(200));
            Assert.That(economy.TotalHarvestedCredits, Is.EqualTo(300), "spending never reduces the harvested statistic");

            registry.AddCredits(PlayerOne, 50);
            economy = registry.GetPlayerState(PlayerOne);
            Assert.That(economy.Credits, Is.EqualTo(250));
            Assert.That(economy.TotalHarvestedCredits, Is.EqualTo(350), "a positive delta counts as income by contract");
        }

        // ------------------------------------------------------------------
        // 6 — slot competition and the waiting queue
        // ------------------------------------------------------------------

        /// <summary>Six harvesters staggered so arrivals land on ticks 9..14.</summary>
        private static SupplyEconomyRegistry CreateSixHarvesterCompetition()
        {
            var registry = new SupplyEconomyRegistry();
            registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 30_000, out _);
            registry.TryRegisterCenter(CenterId, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(12_000, 0), out _);
            for (int i = 0; i < 6; i++)
            {
                // Start distance 10 000 + 500·i → arrival tick 9 + i (the 500 mm
                // step lands exactly on the 6 000 mm radius boundary).
                Assert.That(
                    registry.TryRegisterHarvester((ulong)(400 + i), PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(10_000 + i * 500, 0), 500, 300, out _),
                    Is.True, $"harvester {i} must register");
            }

            return registry;
        }

        [Test]
        public void Competition_FourSlotsFilled_TwoHarvestersQueue()
        {
            var registry = CreateSixHarvesterCompetition();

            for (int tick = 1; tick <= 14; tick++)
            {
                registry.AdvanceTick(); // arrivals land on ticks 9..14
            }

            Assert.That(registry.TryGetDockState(DockId, out var dock), Is.True);
            Assert.That(dock.OccupiedSlotCount, Is.EqualTo(4), "exactly the four loading slots are taken");
            Assert.That(dock.WaitingQueueCount, Is.EqualTo(2), "the 5th and 6th arrivals are waiting in the queue");

            Assert.That(registry.TryGetHarvesterState(404, out var fifth), Is.True);
            Assert.That(fifth.State, Is.EqualTo(HarvesterState.WaitSlot));
            Assert.That(registry.TryGetHarvesterState(405, out var sixth), Is.True);
            Assert.That(sixth.State, Is.EqualTo(HarvesterState.WaitSlot));
        }

        [Test]
        public void Competition_QueueHeadIsPromoted_TheMomentASlotFrees()
        {
            var registry = CreateSixHarvesterCompetition();
            for (int tick = 1; tick <= 14; tick++)
            {
                registry.AdvanceTick();
            }

            // The first loader (id 400, arrived tick 9) finishes on tick 49:
            // 40 loading ticks from tick 10 through 49. The freed slot goes to
            // the queue head (id 404, arrived tick 13) instantly.
            for (int tick = 15; tick <= 49; tick++)
            {
                registry.AdvanceTick();
            }

            Assert.That(registry.TryGetHarvesterState(404, out var promoted), Is.True);
            Assert.That(promoted.State, Is.EqualTo(HarvesterState.Loading), "the queue head occupies the freed slot the moment it frees");
            Assert.That(registry.TryGetDockState(DockId, out var dock), Is.True);
            Assert.That(dock.WaitingQueueCount, Is.EqualTo(1), "only the 6th harvester is still queued");
        }

        [Test]
        public void Competition_SecondInQueue_PromotedOnTheNextRelease()
        {
            var registry = CreateSixHarvesterCompetition();
            for (int tick = 1; tick <= 50; tick++)
            {
                registry.AdvanceTick(); // tick 49: head promoted; tick 50: second loader (id 401) finishes
            }

            Assert.That(registry.TryGetHarvesterState(405, out var promoted), Is.True);
            Assert.That(promoted.State, Is.EqualTo(HarvesterState.Loading), "the second queue entry is promoted when the next slot frees");
            Assert.That(registry.TryGetDockState(DockId, out var dock), Is.True);
            Assert.That(dock.WaitingQueueCount, Is.EqualTo(0), "the queue drains completely");
        }

        [Test]
        public void Competition_SameTickArrivals_TieBreakBySmallestEntityId()
        {
            var registry = new SupplyEconomyRegistry();
            registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 30_000, out _);
            registry.TryRegisterCenter(CenterId, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(12_000, 0), out _);

            // Four staggered harvesters (start distances 6 500..7 400 → arrival
            // ticks 2..5, finishes t42..t45) fill every slot; two harvesters at
            // the same spot (7 400 mm) arrive together on tick 4 and queue —
            // same TickArrived, so the queue must hold them in EntityId order.
            for (int i = 0; i < 4; i++)
            {
                Assert.That(
                    registry.TryRegisterHarvester((ulong)(110 + i), PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(6_500 + i * 300, 0), 500, 300, out _),
                    Is.True);
            }

            Assert.That(registry.TryRegisterHarvester(503, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(7_400, 0), 500, 300, out _), Is.True);
            Assert.That(registry.TryRegisterHarvester(507, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(7_400, 0), 500, 300, out _), Is.True);

            for (int tick = 1; tick <= 41; tick++)
            {
                registry.AdvanceTick();
            }

            Assert.That(registry.TryGetDockState(DockId, out var dock), Is.True);
            Assert.That(dock.OccupiedSlotCount, Is.EqualTo(4), "all four loaders are mid-loading at tick 41");
            Assert.That(dock.WaitingQueueCount, Is.EqualTo(2), "both same-tick arrivals are queued by tick 41");

            registry.AdvanceTick(); // tick 42: loader 110 finishes — its slot frees and the queue head promotes

            Assert.That(registry.TryGetHarvesterState(503, out var promoted), Is.True);
            Assert.That(promoted.State, Is.EqualTo(HarvesterState.Loading), "the EntityId tie-break promotes 503 first");
            Assert.That(registry.TryGetHarvesterState(507, out var stillWaiting), Is.True);
            Assert.That(stillWaiting.State, Is.EqualTo(HarvesterState.WaitSlot), "507 keeps its queue position behind 503");
        }

        // ------------------------------------------------------------------
        // 7 — depletion, partial load and re-targeting
        // ------------------------------------------------------------------

        [Test]
        public void Depletion_PartialLoad_TakesExactlyTheRemainder()
        {
            var registry = new SupplyEconomyRegistry();
            registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 150, out _);
            registry.TryRegisterCenter(CenterId, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(12_000, 0), out _);
            Assert.That(registry.TryRegisterHarvester(300, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(1_000, 0), 500, 300, out _), Is.True);

            for (int tick = 1; tick <= 42; tick++)
            {
                registry.AdvanceTick(); // t2 arrival, t42: the 40th loading tick
            }

            Assert.That(registry.TryGetHarvesterState(300, out var state), Is.True);
            Assert.That(state.CarriedDollars, Is.EqualTo(150), "the remainder is taken — the dock drains to exactly zero");
            Assert.That(registry.TryGetDockState(DockId, out var dock), Is.True);
            Assert.That(dock.RemainingDollars, Is.EqualTo(0), "no negative balance");
            Assert.That(state.State, Is.EqualTo(HarvesterState.ToCenter), "the partial cargo goes to the center");
        }

        [Test]
        public void Depletion_AfterUnload_SwitchesToTheSecondDock()
        {
            var registry = new SupplyEconomyRegistry();
            registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 150, out _);
            ulong secondDockId = 101;
            registry.TryRegisterDock(secondDockId, new GlobalFront.Core.Movement.WorldPointMm(30_000, 0), 10_000, out _);
            registry.TryRegisterCenter(CenterId, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(12_000, 0), out _);
            Assert.That(registry.TryRegisterHarvester(300, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(1_000, 0), 500, 300, out _), Is.True);

            for (int tick = 1; tick <= 165; tick++)
            {
                registry.AdvanceTick(); // load 150 from dock 1, unload, shuttle to dock 2 and fill 300
            }

            Assert.That(registry.TryGetHarvesterState(300, out var state), Is.True);
            Assert.That(state.TargetDockId, Is.EqualTo(secondDockId), "the depleted dock is abandoned for the nearest non-empty one");
            Assert.That(registry.TryGetDockState(secondDockId, out var second), Is.True);
            Assert.That(second.RemainingDollars, Is.EqualTo(9_700), "10 000 − 300: the harvester completed a full load from the second dock");
            Assert.That(registry.GetCredits(PlayerOne), Is.EqualTo(450), "150 partial + 300 full load delivered");
        }

        [Test]
        public void Depletion_AllDocksEmpty_HarvesterIdles_ThenRetargetsANewDock()
        {
            var registry = new SupplyEconomyRegistry();
            registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 150, out _);
            registry.TryRegisterCenter(CenterId, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(12_000, 0), out _);
            Assert.That(registry.TryRegisterHarvester(300, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(1_000, 0), 500, 300, out _), Is.True);

            for (int tick = 1; tick <= 90; tick++)
            {
                registry.AdvanceTick(); // load 150, unload, find no dock → Idle
            }

            Assert.That(registry.TryGetHarvesterState(300, out var state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.Idle), "every dock is depleted — the harvester idles");
            Assert.That(registry.DockCount, Is.EqualTo(1), "the depleted dock still exists but is not selectable");

            Assert.That(registry.TryRegisterDock(101, new GlobalFront.Core.Movement.WorldPointMm(30_000, 0), 10_000, out _), Is.True);
            registry.AdvanceTick();
            Assert.That(registry.TryGetHarvesterState(300, out state), Is.True);
            Assert.That(state.State, Is.EqualTo(HarvesterState.ToDock), "a freshly registered non-empty dock is picked up on the next tick");
            Assert.That(state.TargetDockId, Is.EqualTo(101ul));
        }

        // ------------------------------------------------------------------
        // 8 — zero-GC hot path
        // ------------------------------------------------------------------

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
        public void AdvanceTick_With4Docks4Centers20Harvesters_DoesNotAllocate()
        {
            var registry = new SupplyEconomyRegistry();
            for (int i = 0; i < 4; i++)
            {
                Assert.That(
                    registry.TryRegisterDock((ulong)(100 + i), new GlobalFront.Core.Movement.WorldPointMm(i * 40_000, 0), 1_000_000, out _),
                    Is.True, $"dock {i} must register");
                Assert.That(
                    registry.TryRegisterCenter((ulong)(200 + i), PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(i * 40_000, 8_000), out _),
                    Is.True, $"center {i} must register");
            }

            for (int i = 0; i < 20; i++)
            {
                int dockIndex = i % 4;
                Assert.That(
                    registry.TryRegisterHarvester((ulong)(500 + i), PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(dockIndex * 40_000 + 4_000, 0), 500, 300, out _),
                    Is.True, $"harvester {i} must register");
            }

            // Warmup: JIT the whole FSM pipeline.
            for (int tick = 0; tick < 200; tick++)
            {
                registry.AdvanceTick();
            }

            Assert.That(
                () =>
                {
                    for (int tick = 0; tick < 2000; tick++)
                    {
                        registry.AdvanceTick();
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "the economy tick is a per-tick hot path and must be allocation-free");

            Assert.That(registry.HarvesterCount, Is.EqualTo(20), "harvesters keep cycling through the FSM");
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// The canonical full-cycle setup: dock 100 (0, 0) with 30 000 $,
        /// center 200 (12 000, 0) owned by player one, harvester 300 at
        /// (10 000, 0) with speed 500 and capacity 300. All movement is
        /// axis-aligned: approach t2..t9, loading t10..t49, center approach
        /// t50..t57, unload t57.
        /// </summary>
        private static SupplyEconomyRegistry CreateFullCycleEconomy()
        {
            var registry = new SupplyEconomyRegistry();
            Assert.That(
                registry.TryRegisterDock(DockId, new GlobalFront.Core.Movement.WorldPointMm(0, 0), 30_000, out _),
                Is.True);
            Assert.That(
                registry.TryRegisterCenter(CenterId, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(12_000, 0), out _),
                Is.True);
            Assert.That(
                registry.TryRegisterHarvester(300, PlayerOne, new GlobalFront.Core.Movement.WorldPointMm(10_000, 0), 500, 300, out _),
                Is.True);
            return registry;
        }
    }
}
