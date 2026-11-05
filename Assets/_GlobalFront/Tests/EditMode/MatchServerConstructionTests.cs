using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
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
    /// Step 5.2.4 — vertical integration of construction into MatchServer: the
    /// AdvanceConstruction phase sits between movement and combat, frames are
    /// regular roster units with their final archetype (RND §2.3), completions
    /// flow power into the ledger, destroyed frames collapse their sites without
    /// a refund, and cancellation refunds 100% and kills the frame through the
    /// regular death flow. The wire protocol is untouched — frames replicate as
    /// ordinary units, and matches that never call TryStartBuilding behave
    /// exactly as before (all 1502 pre-integration tests stay green).
    /// </summary>
    [TestFixture]
    public sealed class MatchServerConstructionTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);

        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        private static readonly CombatStats StandardStats = new CombatStats(100, 10, 12000, 2);

        private static readonly BuildingFootprint ShedFootprint = new BuildingFootprint(500, 500);

        private const int DefaultWorkPerTick = 25;

        private static void SpawnPassiveOpponent(MatchServer server) =>
            server.SpawnUnit(
                PlayerTwo,
                StandardStats,
                new WorldPointMm(100_000, 100_000),
                500);

        // ------------------------------------------------------------------
        // 1 — frame spawn semantics
        // ------------------------------------------------------------------

        [Test]
        public void TryStartBuilding_SpawnsFrameAsBaseStructureUnit()
        {
            var server = new MatchServer();

            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, DefaultWorkPerTick, 777, 5, out ConstructionStartRejection rejection),
                Is.True, $"a valid start must succeed, got {rejection}");

            Assert.That(server.UnitCount, Is.EqualTo(1));
            Assert.That(server.TryGetUnit(new EntityId(900), out var frame), Is.True);
            Assert.That(frame.UnitKind, Is.EqualTo(UnitKinds.BaseStructure), "a frame owns its final archetype from tick 0");
            Assert.That(frame.Owner, Is.EqualTo(PlayerOne));
            Assert.That(frame.Position, Is.EqualTo(new WorldPointMm(0, 0)));
            Assert.That(frame.CurrentHealth, Is.EqualTo(500), "the placeholder frame stats until the blueprint registry lands");
            Assert.That(frame.HasMoveTarget, Is.False, "buildings never receive move orders in Step 5.2.4");

            Assert.That(server.Placement.TryGet(900, out var placed), Is.True, "the frame blocks its footprint from tick 0");
            Assert.That(placed.Footprint, Is.EqualTo(ShedFootprint));
            Assert.That(server.Construction.TryGet(900, out var site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(0));
            Assert.That(site.PaidCost, Is.EqualTo(777));
            Assert.That(server.PowerLedger.GetPowerState(PlayerOne).Produced, Is.EqualTo(0), "an incomplete frame gives no power");
        }

        [Test]
        public void TryStartBuilding_SnapsTheCenter_ForUnitAndRegistries()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(123, 456), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out _),
                Is.True);

            Assert.That(server.TryGetUnit(new EntityId(900), out var frame), Is.True);
            Assert.That(frame.Position, Is.EqualTo(new WorldPointMm(0, 500)), "the frame unit stands on the snapped centre");
            Assert.That(server.Placement.TryGet(900, out var placed), Is.True);
            Assert.That(placed.OriginCenter, Is.EqualTo(new WorldPointMm(0, 500)));
        }

        [Test]
        public void TryStartBuilding_RaisesTheEntityHighWaterMark()
        {
            var server = new MatchServer();
            Assert.That(server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out _), Is.True);

            ulong next = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(-100_000, 0), 500).Value;
            Assert.That(next, Is.GreaterThan(900ul), "a later server-allocated id must never collide with the caller-supplied frame id");
        }

        // ------------------------------------------------------------------
        // 2 — the full cycle: approach, build, complete, power flows
        // ------------------------------------------------------------------

        [Test]
        public void FullCycle_FrameBuiltByDozer_CompletesAndPowersTheLedger()
        {
            var server = new MatchServer();
            SpawnPassiveOpponent(server);
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, DefaultWorkPerTick, 777, 5, out _),
                Is.True);

            // 16 000 mm out: 1 000 mm past the 15 000 mm work radius.
            EntityId dozer = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(16_000, 0), 500);
            Assert.That(server.TryAssignBuilder(PlayerOne, dozer.Value, BuilderKind.Dozer, 900), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(server.Construction.TryGetBuilderActivity(dozer.Value, out var before), Is.True);
            Assert.That(before, Is.EqualTo(BuilderActivity.MovingToSite));

            Assert.That(
                server.TryEnqueueMove(
                    new CommandHeader(PlayerOne, 1, 1, GameCommandType.Move),
                    new[] { dozer },
                    new WorldPointMm(0, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            // Two ticks of approach (progress frozen), then 40 working ticks at
            // 25‰ complete the site; a small tail lets completion land.
            server.ExecuteTicks(45);

            Assert.That(server.Construction.Count, Is.EqualTo(0), "a completed site leaves the construction registry");
            Assert.That(server.Placement.TryGet(900, out _), Is.True, "the finished building stays placed");

            PlayerPowerState power = server.PowerLedger.GetPowerState(PlayerOne);
            Assert.That(power.Produced, Is.EqualTo(5), "completion connected the frame's power delta");
            Assert.That(power.IsLowPower, Is.False);

            Assert.That(server.TryGetUnit(new EntityId(900), out var building), Is.True);
            Assert.That(building.CurrentHealth, Is.EqualTo(500), "completion does not damage the building");
            Assert.That(server.TryGetUnit(dozer, out var doneDozer), Is.True);
            Assert.That(doneDozer.HasMoveTarget, Is.False, "the dozer arrived");
            Assert.That(server.Construction.TryGetBuilderActivity(dozer.Value, out _), Is.False, "completion releases the builder (Idle)");
        }

        // ------------------------------------------------------------------
        // 3 — builder death freezes, never resets; replacement resumes
        // ------------------------------------------------------------------

        [Test]
        public void BuilderDeath_FreezesProgressAndFreesTheSlotForReplacement()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, DefaultWorkPerTick, 100, 0, out _),
                Is.True);

            // The dozer at (1 000, 0) is inside the work radius and is the
            // attacker's closest enemy, so the fire lands on the builder.
            EntityId dozer = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(1_000, 0), 500);
            Assert.That(server.TryAssignBuilder(PlayerOne, dozer.Value, BuilderKind.Dozer, 900), Is.EqualTo(BuilderAssignmentRejection.None));
            server.SpawnUnit(PlayerTwo, StandardStats, new WorldPointMm(3_000, 0), 500, autoAcquire: true);

            // Tick until the dozer dies (10 hits at 10 damage over cooldown 2).
            int ticks = 0;
            while (ticks < 100)
            {
                server.TickOnce();
                ticks++;
                server.TryGetUnit(dozer, out var snapshot);
                if (snapshot.CurrentHealth == 0)
                {
                    break;
                }
            }

            Assert.That(server.TryGetUnit(dozer, out var deadDozer), Is.True);
            Assert.That(deadDozer.CurrentHealth, Is.EqualTo(0), "the scenario needs the builder to die in combat");

            // The builder died in Phase 6 (combat). Run one tick so Phase 5
            // (construction) processes the dead input, frees the slot and records frozen.
            server.TickOnce();

            Assert.That(server.Construction.TryGet(900, out ConstructionSiteState frozen), Is.True);
            Assert.That(frozen.BuildProgressMilli, Is.GreaterThan(0), "the dozer worked before dying");
            int frozenProgress = frozen.BuildProgressMilli;
            Assert.That(frozen.IsFrozen, Is.True);
            Assert.That(frozen.PresentBuilders, Is.EqualTo(0));

            server.ExecuteTicks(10);
            Assert.That(server.Construction.TryGet(900, out ConstructionSiteState still), Is.True);
            Assert.That(still.BuildProgressMilli, Is.EqualTo(frozenProgress), "death freezes — progress never resets");

            EntityId replacement = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(0, 0), 500);
            Assert.That(server.TryAssignBuilder(PlayerOne, replacement.Value, BuilderKind.Dozer, 900), Is.EqualTo(BuilderAssignmentRejection.None), "a dead builder's slot is freed for a replacement");
            server.TickOnce();
            Assert.That(server.Construction.TryGet(900, out ConstructionSiteState resumed), Is.True);
            Assert.That(resumed.BuildProgressMilli, Is.EqualTo(frozenProgress + DefaultWorkPerTick), "work resumes from the frozen point");
        }

        // ------------------------------------------------------------------
        // 4 — blackout rules through the live ledger
        // ------------------------------------------------------------------

        [Test]
        public void Blackout_FreezesModularSite_ButDozerWorkerKeepsBuilding()
        {
            var server = new MatchServer();
            SpawnPassiveOpponent(server);
            Assert.That(server.PowerLedger.RegisterBuilding(PlayerOne, 8000, -1), Is.EqualTo(PowerLedgerRejection.None), "a lone consumer forces NetPower = -1");

            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(-10_000, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out _),
                Is.True);
            Assert.That(
                server.TryStartBuilding(PlayerOne, 901, new WorldPointMm(10_000, 0), ShedFootprint, ConstructionMethod.DozerWorker, DefaultWorkPerTick, 100, 1, out _),
                Is.True);

            EntityId dozer = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(10_000, 0), 500);
            Assert.That(server.TryAssignBuilder(PlayerOne, dozer.Value, BuilderKind.Dozer, 901), Is.EqualTo(BuilderAssignmentRejection.None));

            server.ExecuteTicks(10);

            Assert.That(server.Construction.TryGet(900, out var modular), Is.True);
            Assert.That(modular.BuildProgressMilli, Is.EqualTo(0), "a blackout freezes modular self-assembly");
            Assert.That(modular.IsPowered, Is.False);
            Assert.That(modular.IsFrozen, Is.True);

            Assert.That(server.Construction.TryGet(901, out var dozerSite), Is.True);
            Assert.That(dozerSite.BuildProgressMilli, Is.EqualTo(250), "dozers are autonomous — a blackout never slows them");
        }

        [Test]
        public void Completion_UnderBlackout_StillFlowsPower()
        {
            var server = new MatchServer();
            Assert.That(server.PowerLedger.RegisterBuilding(PlayerOne, 8000, -1), Is.EqualTo(PowerLedgerRejection.None), "arrange a blackout");

            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, 1000, 100, 5, out _),
                Is.True, "BaseWorkPerTick = 1000 completes in a single tick");
            EntityId dozer = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(0, 0), 500);
            Assert.That(server.TryAssignBuilder(PlayerOne, dozer.Value, BuilderKind.Dozer, 900), Is.EqualTo(BuilderAssignmentRejection.None));

            server.TickOnce();

            Assert.That(server.Construction.Count, Is.EqualTo(0), "one tick at 1000‰ completes the site");
            Assert.That(server.PowerLedger.GetPowerState(PlayerOne).Produced, Is.EqualTo(5), "connecting power does not depend on the blackout");
        }

        // ------------------------------------------------------------------
        // 5 — a frame destroyed by combat collapses its site (no refund)
        // ------------------------------------------------------------------

        [Test]
        public void FrameDestroyedByCombat_CollapsesSiteWithoutRefundOrPower()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, DefaultWorkPerTick, 777, 5, out _),
                Is.True);
            // A far-away survivor keeps the match non-terminal so the pipeline
            // keeps running after the frame dies.
            server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(-150_000, 0), 500);
            server.SpawnUnit(PlayerTwo, StandardStats, new WorldPointMm(3_000, 0), 500, autoAcquire: true);

            int ticks = 0;
            ServerUnitSnapshot frame = default;
            while (ticks < 300)
            {
                server.TickOnce();
                ticks++;
                server.TryGetUnit(new EntityId(900), out frame);
                if (frame.CurrentHealth == 0)
                {
                    break;
                }
            }

            Assert.That(server.TryGetUnit(new EntityId(900), out frame), Is.True);
            Assert.That(frame.CurrentHealth, Is.EqualTo(0), "the scenario needs the frame destroyed");

            server.TickOnce();

            Assert.That(server.Construction.Count, Is.EqualTo(0), "a destroyed frame collapses its site");
            Assert.That(
                server.Placement.ValidatePlacement(new WorldPointMm(0, 0), ShedFootprint),
                Is.EqualTo(PlacementRejection.None),
                "the collapsed site frees its footprint");
            Assert.That(server.PowerLedger.GetPowerState(PlayerOne).Produced, Is.EqualTo(0), "a destroyed frame never feeds the grid");
        }

        // ------------------------------------------------------------------
        // 6 — cancellation: refund, un-place, frame dies, builders idle
        // ------------------------------------------------------------------

        [Test]
        public void TryCancelBuilding_RefundsFully_KillsFrameAndFreesEverything()
        {
            var server = new MatchServer();
            SpawnPassiveOpponent(server);
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, DefaultWorkPerTick, 777, 5, out _),
                Is.True);
            EntityId dozer = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(0, 0), 500);
            Assert.That(server.TryAssignBuilder(PlayerOne, dozer.Value, BuilderKind.Dozer, 900), Is.EqualTo(BuilderAssignmentRejection.None));
            server.ExecuteTicks(4);
            Assert.That(server.Construction.TryGet(900, out var partial), Is.True);
            Assert.That(partial.BuildProgressMilli, Is.GreaterThan(0), "the scenario needs partial progress");

            Assert.That(server.TryCancelBuilding(PlayerOne, 900, out int refundCost), Is.True);
            Assert.That(refundCost, Is.EqualTo(777), "cancellation refunds 100% of the paid cost");

            Assert.That(server.Construction.Count, Is.EqualTo(0));
            Assert.That(
                server.Placement.ValidatePlacement(new WorldPointMm(0, 0), ShedFootprint),
                Is.EqualTo(PlacementRejection.None),
                "the cancelled spot is rebuildable");
            Assert.That(server.TryGetUnit(new EntityId(900), out var frame), Is.True);
            Assert.That(frame.CurrentHealth, Is.EqualTo(0), "the frame dies through the regular death flow");
            Assert.That(server.Construction.TryGetBuilderActivity(dozer.Value, out _), Is.False, "the builder is released (Idle)");
            Assert.That(server.PowerLedger.GetPowerState(PlayerOne).Produced, Is.EqualTo(0), "the cancelled frame never feeds the grid");

            Assert.That(server.TryCancelBuilding(PlayerOne, 900, out _), Is.False, "a second cancel reports absence");
        }

        [Test]
        public void TryCancelBuilding_ForeignOwner_ReturnsFalse()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out _),
                Is.True);

            Assert.That(server.TryCancelBuilding(PlayerTwo, 900, out int refundCost), Is.False, "fail-closed: a foreign cancel is indistinguishable from an unknown entity");
            Assert.That(refundCost, Is.EqualTo(0));
            Assert.That(server.Construction.Count, Is.EqualTo(1), "the site survives the foreign cancel");
        }

        // ------------------------------------------------------------------
        // 7 — start gates on the server entry point
        // ------------------------------------------------------------------

        [Test]
        public void TryStartBuilding_InvalidOwner_RejectsWithoutAnyStateChange()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(new PlayerId(0), 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out ConstructionStartRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.InvalidOwner));
            Assert.That(server.UnitCount, Is.EqualTo(0));
            Assert.That(server.Construction.Count, Is.EqualTo(0));
        }

        [Test]
        public void TryStartBuilding_DuplicateEntityId_Rejects()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(-10_000, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out _),
                Is.True);

            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(10_000, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out ConstructionStartRejection rejection),
                Is.False, "the same id cannot be reused while the frame lives");
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.InvalidEntityId));

            EntityId unit = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(50_000, 0), 500);
            Assert.That(
                server.TryStartBuilding(PlayerOne, unit.Value, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out rejection),
                Is.False, "a caller-supplied id colliding with the roster must reject");
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.InvalidEntityId));
        }

        [Test]
        public void TryStartBuilding_OverlappingFootprint_RejectsAndRollsBackLedger()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out _),
                Is.True);

            Assert.That(
                server.TryStartBuilding(PlayerOne, 901, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out ConstructionStartRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.OverlapsExistingBuilding));
            Assert.That(server.UnitCount, Is.EqualTo(1), "a rejected start must not spawn a unit");
            Assert.That(server.Construction.Count, Is.EqualTo(1));
            Assert.That(server.PowerLedger.GetPowerState(PlayerOne).Produced, Is.EqualTo(0), "no orphaned ledger entry may survive the rejected start");
        }

        [Test]
        public void TryStartBuilding_PastRosterCapacity_RejectsWithRosterFull()
        {
            var server = new MatchServer();
            for (int i = 0; i < 1024; i++)
            {
                server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(-150_000 + (i % 32) * 2_000, -150_000 + (i / 32) * 2_000), 500);
            }

            Assert.That(server.UnitCount, Is.EqualTo(1024));
            Assert.That(
                server.TryStartBuilding(PlayerOne, 9999, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out ConstructionStartRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.RosterFull), "the frame is a roster unit and respects the roster chokepoint");
        }

        [Test]
        public void TryStartBuilding_PastLedgerCapacity_RejectsWithPowerLedgerFull()
        {
            var server = new MatchServer();
            for (int i = 0; i < 512; i++)
            {
                Assert.That(server.PowerLedger.RegisterBuilding(PlayerOne, (ulong)(5000 + i), 1), Is.EqualTo(PowerLedgerRejection.None));
            }

            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, DefaultWorkPerTick, 100, 1, out ConstructionStartRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.PowerLedgerFull));
            Assert.That(server.UnitCount, Is.EqualTo(0), "a rejected start must not spawn a frame");
        }

        // ------------------------------------------------------------------
        // 8 — builder assignment gates on the server entry point
        // ------------------------------------------------------------------

        [Test]
        public void TryAssignBuilder_ForeignBuilderAndDeadBuilder_Reject()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerOne, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, DefaultWorkPerTick, 100, 0, out _),
                Is.True);

            EntityId foreign = server.SpawnUnit(PlayerTwo, StandardStats, new WorldPointMm(20_000, 0), 500);
            Assert.That(
                server.TryAssignBuilder(PlayerOne, foreign.Value, BuilderKind.Dozer, 900),
                Is.EqualTo(BuilderAssignmentRejection.OwnerMismatch),
                "fail-closed: a foreign builder is indistinguishable from a missing one");

            EntityId dozer = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(1_000, 0), 500);
            server.SpawnUnit(PlayerTwo, StandardStats, new WorldPointMm(2_500, 0), 500, autoAcquire: true);
            int ticks = 0;
            while (ticks < 100)
            {
                server.TickOnce();
                ticks++;
                server.TryGetUnit(dozer, out var snapshot);
                if (snapshot.CurrentHealth == 0)
                {
                    break;
                }
            }

            Assert.That(server.TryGetUnit(dozer, out var dead), Is.True);
            Assert.That(dead.CurrentHealth, Is.EqualTo(0), "the scenario needs a dead builder");
            Assert.That(
                server.TryAssignBuilder(PlayerOne, dozer.Value, BuilderKind.Dozer, 900),
                Is.EqualTo(BuilderAssignmentRejection.BuilderNotAlive));
        }

        [Test]
        public void TryAssignBuilder_ForeignSite_RejectsWithSiteUnknown()
        {
            var server = new MatchServer();
            Assert.That(
                server.TryStartBuilding(PlayerTwo, 900, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, DefaultWorkPerTick, 100, 0, out _),
                Is.True);
            EntityId dozer = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(0, 0), 500);

            Assert.That(
                server.TryAssignBuilder(PlayerOne, dozer.Value, BuilderKind.Dozer, 900),
                Is.EqualTo(BuilderAssignmentRejection.SiteUnknown),
                "fail-closed: a foreign site is indistinguishable from a missing one");
        }

        // ------------------------------------------------------------------
        // 9 — zero-GC tick with active construction
        // ------------------------------------------------------------------

        [Test]
        public void TickOnce_WithActiveConstruction_IsAllocationFree()
        {
            var server = new MatchServer();
            SpawnPassiveOpponent(server);

            // 10 dozer-worker sheds (builders inside the radius) + 40 modular
            // sheds, all at 1‰ per tick — every site completes exactly 1000
            // ticks after its start.
            for (int i = 0; i < 50; i++)
            {
                ConstructionMethod method = i < 10 ? ConstructionMethod.DozerWorker : ConstructionMethod.Modular;
                Assert.That(
                    server.TryStartBuilding(PlayerOne, (ulong)(1000 + i), new WorldPointMm(-199_000 + i * 2_000, -199_000), ShedFootprint, method, 1, 1, 1, out _),
                    Is.True, $"site {i} must start");
            }

            EntityId[] builders = new EntityId[10];
            for (int i = 0; i < 10; i++)
            {
                builders[i] = server.SpawnUnit(PlayerOne, StandardStats, new WorldPointMm(-199_000 + i * 2_000, -199_000), 500);
                Assert.That(server.TryAssignBuilder(PlayerOne, builders[i].Value, BuilderKind.Dozer, (ulong)(1000 + i)), Is.EqualTo(BuilderAssignmentRejection.None));
            }

            // Warmup: nothing completes at 1‰ per tick yet.
            server.ExecuteTicks(200);
            Assert.That(server.Construction.Count, Is.EqualTo(50));

            int calls = 0;
            int sink = 0;
            Assert.That(
                () =>
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        server.TickOnce();
                        sink += server.PowerLedger.GetPowerState(PlayerOne).Produced;
                        calls++;
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "the server tick with active construction must stay allocation-free");

            Assert.That(calls, Is.EqualTo(1000), "the measured window must actually run");
            Assert.That(sink, Is.GreaterThan(0), "completions must have connected power inside the window");
            Assert.That(server.Construction.Count, Is.EqualTo(0), "all sites completed within the window");
            Assert.That(server.PowerLedger.GetPowerState(PlayerOne).Produced, Is.EqualTo(50), "all 50 frames connected their +1 delta");
            Assert.That(server.UnitCount, Is.EqualTo(61), "50 frames + 10 dozers + 1 passive opponent, no roster churn");
        }
    }
}
