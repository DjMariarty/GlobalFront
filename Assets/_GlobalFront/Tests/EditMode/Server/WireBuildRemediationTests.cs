using GlobalFront.Core.Catalog;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Structures;
using GlobalFront.Server;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Server
{
    /// <summary>
    /// M2 remediation acceptance suite: every P1 defect the wire build /
    /// production gates gained a fix for, with a regression test pinning the
    /// fixed behaviour.
    ///
    /// Coverage map (ТЗ «Ремедиация Фазы 5»):
    /// - P1-04 credits: InsufficientFunds_Rejected, DeductsCredits_AndRefundsOnCancel,
    ///   BusyBuilder_RejectsWithoutMutation, ConstructionRegistryFull_RefundsAndRollsBack.
    /// - P1-05 power sign: Reactor_GeneratesPositivePower.
    /// - P1-06 factory registration: Factory_AutoRegistersInProduction.
    /// - P1-07 catalog build time: BuildTime_FollowsCatalogTicks.
    /// - P1-08 builder/faction: TankCannotBeBuilder, ForeignFactionBlueprint_Rejected,
    ///   BusyBuilder_RejectsWithoutMutation.
    /// - P1-09 secondary cap: SecondaryEconomy_CappedAtFourTotal.
    /// - P1-10 coordinate overflow: ExtremeCellCoordinates_Rejected.
    /// - P1-11 queue validation: BuildingBlueprint_RejectedWithInvalidPayload,
    ///   HackerCap_CountsQueuedHackers.
    /// </summary>
    [TestFixture]
    public sealed class WireBuildRemediationTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);
        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        /// <summary>The canonical USA Dozer row — a BuilderIds entry (P1-08).</summary>
        private const ushort DozerBlueprint = 174;

        private static readonly IBlueprintCatalog Catalog = DefaultBlueprintCatalog.Create();

        private static MatchServer CreateServer()
        {
            var server = new MatchServer();

            // Passive far-away opponent keeps the simulation running (the
            // single-owner roster would resolve Victory on the first tick).
            server.SpawnUnit(
                PlayerTwo, new CombatStats(100, 1, 0, 1), new WorldPointMm(-150_000, 0), 500);

            server.Economy.SetCredits(PlayerOne, 1_000_000);
            return server;
        }

        private static BlueprintDefinition Def(ushort blueprintId)
        {
            Assert.That(
                Catalog.TryGet(new BlueprintId(blueprintId), out var definition), Is.True,
                $"scenario setup: blueprint {blueprintId} must resolve from the catalog");
            return definition;
        }

        /// <summary>
        /// Spawns a catalog builder row (the USA Dozer, blueprint 174) at the
        /// given position — inside the 15 m work radius of the site the test
        /// orders nearby.
        /// </summary>
        private static EntityId SpawnBuilder(MatchServer server, int idValue, int xMm, int zMm)
        {
            var entity = new EntityId((ulong)idValue);
            Assert.That(
                server.SpawnUnitWithEntity(
                    entity, PlayerOne, new CombatStats(100, 1, 0, 1),
                    new WorldPointMm(xMm, zMm), 500, false, UnitKinds.Tank,
                    blueprintId: DozerBlueprint),
                Is.EqualTo(entity),
                "scenario setup: the physical builder must spawn");
            return entity;
        }

        private static CommandHeader BuildHeader(uint sequence) =>
            new CommandHeader(PlayerOne, sequence, 1UL, GameCommandType.Build);

        private static CommandHeader ProduceHeader(uint sequence, ulong requestedTick) =>
            new CommandHeader(PlayerOne, sequence, requestedTick, GameCommandType.Produce);

        private static ServerUnitSnapshot FindUnitByBlueprint(MatchServer server, ushort blueprintId)
        {
            var snapshots = new ServerUnitSnapshot[server.UnitCount];
            Assert.That(server.CopySnapshots(snapshots), Is.EqualTo(server.UnitCount));

            for (var index = 0; index < snapshots.Length; index++)
            {
                if (snapshots[index].BlueprintId == blueprintId)
                {
                    return snapshots[index];
                }
            }

            Assert.Fail($"no living unit carries blueprint {blueprintId}");
            return default;
        }

        /// <summary>
        /// Spawns a completed production building the direct (non-wire) way —
        /// the roster-suite pattern: the Russia factory (414) plus the Reactor
        /// (411) so the -2 factory does not black the grid out.
        /// </summary>
        private static void SpawnCompletedFactory(MatchServer server)
        {
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 800, new WorldPointMm(0, 0),
                    new BuildingFootprint(3000, 3000), ConstructionMethod.Modular,
                    1000, 2000, -2, true, null, UnitKinds.BaseStructure, 414, out var rejection),
                Is.True, $"factory start must succeed: {rejection}");
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 801, new WorldPointMm(15_000, 0),
                    new BuildingFootprint(2000, 2000), ConstructionMethod.Modular,
                    1000, 900, 7, false, null, UnitKinds.BaseStructure, 411, out rejection),
                Is.True, $"reactor start must succeed: {rejection}");
            server.ExecuteTicks(2);
        }

        // ------------------------------------------------------------------
        // P1-04: credits
        // ------------------------------------------------------------------

        [Test]
        public void WireBuild_InsufficientFunds_Rejected()
        {
            var server = CreateServer();
            var builder = SpawnBuilder(server, 900, 0, -10_000);
            var barracks = Def(113);

            server.Economy.SetCredits(PlayerOne, barracks.Cost - 1);
            var unitsBefore = server.UnitCount;

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), builder, new BlueprintId(113), 0, 0),
                Is.EqualTo(MatchCommandRejection.InsufficientFunds),
                "a build the wallet cannot cover is refused at the gate");

            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(barracks.Cost - 1),
                "a rejected build leaves the wallet untouched — never negative");
            Assert.That(server.UnitCount, Is.EqualTo(unitsBefore),
                "a rejected build spawns no frame");
            Assert.That(server.Construction.Count, Is.EqualTo(0),
                "a rejected build starts no construction site");
        }

        [Test]
        public void WireBuild_DeductsCredits_AndRefundsOnCancel()
        {
            var server = CreateServer();
            var builder = SpawnBuilder(server, 900, 0, -10_000);
            var barracks = Def(113);
            var startingCredits = server.Economy.GetCredits(PlayerOne);

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), builder, new BlueprintId(113), 0, 0),
                Is.EqualTo(MatchCommandRejection.None));

            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(startingCredits - barracks.Cost),
                "an accepted build debits exactly the catalog cost");

            var frame = FindUnitByBlueprint(server, 113);
            Assert.That(
                server.TryCancelBuilding(PlayerOne, frame.Entity.Value, out var refund), Is.True);
            Assert.That(refund, Is.EqualTo(barracks.Cost),
                "cancelling a construction refunds 100% of the paid cost");
            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(startingCredits),
                "the refund reaches the wallet — nothing evaporates");
        }

        [Test]
        public void WireBuild_BusyBuilder_RejectsWithoutMutation()
        {
            var server = CreateServer();
            var builder = SpawnBuilder(server, 900, 0, -10_000);

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), builder, new BlueprintId(113), 0, 0),
                Is.EqualTo(MatchCommandRejection.None));

            var creditsAfterFirst = server.Economy.GetCredits(PlayerOne);
            var unitsAfterFirst = server.UnitCount;

            Assert.That(
                server.TryOrderBuild(BuildHeader(2), builder, new BlueprintId(113), 20, 0),
                Is.EqualTo(MatchCommandRejection.BuilderRequired),
                "a builder already holding an assignment cannot start a second site");

            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(creditsAfterFirst),
                "the refused second build debits nothing");
            Assert.That(server.UnitCount, Is.EqualTo(unitsAfterFirst),
                "the refused second build spawns no frame");
            Assert.That(server.Construction.Count, Is.EqualTo(1),
                "the refused second build starts no site — the first one stands alone");
        }

        [Test]
        public void WireBuild_ConstructionRegistryFull_RefundsAndRollsBack()
        {
            var server = CreateServer();
            var builder = SpawnBuilder(server, 900, 0, -10_000);

            // Saturate the construction registry with builder-less direct
            // sites (a 32x4 grid of 4x4 footprints, 5 cells apart). The wire
            // gate's pre-checks stay green — no builder is assigned and the
            // builder table is empty — so the spend happens and only the
            // registry-full start can refuse the order.
            for (var index = 0; index < ConstructionRegistry.MaxActiveConstructions; index++)
            {
                var cellX = (index % 32) * 10;
                var cellZ = (index / 32) * 10;
                Assert.That(
                    server.TryStartBuilding(
                        PlayerOne, (ulong)(10_000 + index),
                        new WorldPointMm(cellX * 500 + 250, cellZ * 500 + 250),
                        new BuildingFootprint(2000, 2000), ConstructionMethod.DozerWorker,
                        25, 0, 0, false, null, UnitKinds.BaseStructure, 0, out var rejection),
                    Is.True, $"site {index} must start: {rejection}");
            }

            var creditsBefore = server.Economy.GetCredits(PlayerOne);
            var unitsBefore = server.UnitCount;

            // Cell (350, 0) is clear of the saturated grid and inside the map
            // bounds — the only refusal left is the registry capacity.
            Assert.That(
                server.TryOrderBuild(BuildHeader(1), builder, new BlueprintId(113), 350, 0),
                Is.EqualTo(MatchCommandRejection.ConstructionRejected));

            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(creditsBefore),
                "a refused start refunds the debit — the wallet nets zero");
            Assert.That(server.UnitCount, Is.EqualTo(unitsBefore),
                "a refused start leaves no frame behind");
            Assert.That(server.Construction.Count, Is.EqualTo(
                ConstructionRegistry.MaxActiveConstructions));
        }

        // ------------------------------------------------------------------
        // P1-05: power sign
        // ------------------------------------------------------------------

        [Test]
        public void WireBuild_Reactor_GeneratesPositivePower()
        {
            var server = CreateServer();
            var reactor = Def(111);
            var factory = Def(114);

            // Both builders inside their site's 15 m work radius; the sites
            // sit 20 cells (10 m) apart — footprints never overlap.
            var reactorBuilder = SpawnBuilder(server, 900, 0, -10_000);
            var factoryBuilder = SpawnBuilder(server, 901, 10_000, -10_000);

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), reactorBuilder, new BlueprintId(111), 0, 0),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                server.TryOrderBuild(BuildHeader(2), factoryBuilder, new BlueprintId(114), 20, 0),
                Is.EqualTo(MatchCommandRejection.None));

            // The reactor (300 ticks) completes first; the factory (500
            // ticks) is still a frame, contributing nothing.
            server.ExecuteTicks(340);

            Assert.That(
                server.PowerLedger.GetPowerState(PlayerOne).NetPower,
                Is.EqualTo(reactor.PowerProduction),
                "the wire-built reactor produces positive power — the delta is not inverted");

            Assert.That(factory.PowerConsumption, Is.GreaterThan(0),
                "scenario sanity: the factory row consumes power");

            server.ExecuteTicks(200);

            Assert.That(
                server.PowerLedger.GetPowerState(PlayerOne).NetPower,
                Is.EqualTo(reactor.PowerProduction - factory.PowerConsumption),
                "the wire-built factory consumes power — NetPower drops by its demand");
        }

        // ------------------------------------------------------------------
        // P1-06: factory auto-registration
        // ------------------------------------------------------------------

        [Test]
        public void WireBuild_Factory_AutoRegistersInProduction()
        {
            var server = CreateServer();
            var builder = SpawnBuilder(server, 900, 10_000, -10_000);

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), builder, new BlueprintId(114), 20, 0),
                Is.EqualTo(MatchCommandRejection.None));

            server.ExecuteTicks(510);

            var factory = FindUnitByBlueprint(server, 114);
            Assert.That(
                server.Production.TryGetBuildingState(factory.Entity.Value, out _), Is.True,
                "a factory built through the wire gate registers in the production registry");

            Assert.That(
                server.TryScheduleProduction(
                    // Sequence 2: the build order committed sequence 1, and
                    // TryScheduleProduction commits the sequence before its
                    // payload gates — a replayed 1 would read as DuplicateSequence.
                    ProduceHeader(2, server.CurrentTick + 1),
                    new EntityId(factory.Entity.Value), new BlueprintId(401)),
                Is.EqualTo(MatchCommandRejection.None),
                "the wire-built factory accepts Produce orders — no UnknownEntity");
        }

        // ------------------------------------------------------------------
        // P1-07: catalog build time
        // ------------------------------------------------------------------

        [Test]
        public void WireBuild_BuildTime_FollowsCatalogTicks()
        {
            var server = CreateServer();
            var barracks = Def(113);
            Assert.That(barracks.BuildTimeTicks, Is.EqualTo(250u),
                "scenario sanity: the barracks row carries a 250-tick budget");

            var builder = SpawnBuilder(server, 900, 0, -10_000);

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), builder, new BlueprintId(113), 0, 0),
                Is.EqualTo(MatchCommandRejection.None));

            server.ExecuteTicks(10);

            Assert.That(server.Construction.Count, Is.EqualTo(1),
                "a 250-tick blueprint is nowhere near done after 10 ticks");
            var frame = FindUnitByBlueprint(server, 113);
            Assert.That(
                server.TryGetUnit(frame.Entity, out var frameState) &&
                    frameState.CurrentHealth == 500,
                Is.True,
                "the site is still the 500-health construction frame");

            server.ExecuteTicks(240);

            Assert.That(server.Construction.Count, Is.EqualTo(0),
                "the catalog tick budget — not a hard-coded constant — completes the site");
            Assert.That(
                server.TryGetUnit(frame.Entity, out var done) &&
                    done.CurrentHealth == barracks.Stats.MaximumHealth,
                Is.True,
                "the completed barracks owns its catalog health");
        }

        // ------------------------------------------------------------------
        // P1-08: builder and faction validation
        // ------------------------------------------------------------------

        [Test]
        public void WireBuild_TankCannotBeBuilder()
        {
            var server = CreateServer();

            // A combat tank: same mobile-armed legacy kind as the builder
            // rows, but spawned without a builder blueprint id.
            var tank = new EntityId(900);
            Assert.That(
                server.SpawnUnitWithEntity(
                    tank, PlayerOne, new CombatStats(100, 25, 7000, 10),
                    new WorldPointMm(0, -10_000), 500, false, UnitKinds.Tank),
                Is.EqualTo(tank));

            var creditsBefore = server.Economy.GetCredits(PlayerOne);

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), tank, new BlueprintId(113), 0, 0),
                Is.EqualTo(MatchCommandRejection.BuilderRequired),
                "combat armour is not a builder — only catalog builder rows build");

            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(creditsBefore),
                "the refused order debits nothing");
            Assert.That(server.Construction.Count, Is.EqualTo(0));
        }

        [Test]
        public void WireBuild_ForeignFactionBlueprint_Rejected()
        {
            // Player 1 commands USA; the China War Factory (214) is authored
            // for FactionId.China.
            var factions = new byte[11];
            factions[1] = FactionId.USA;
            var server = new MatchServer(null, null, factions);
            server.SpawnUnit(
                PlayerTwo, new CombatStats(100, 1, 0, 1), new WorldPointMm(-150_000, 0), 500);
            server.Economy.SetCredits(PlayerOne, 1_000_000);

            var builder = SpawnBuilder(server, 900, 0, -10_000);
            var creditsBefore = server.Economy.GetCredits(PlayerOne);

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), builder, new BlueprintId(214), 0, 0),
                Is.EqualTo(MatchCommandRejection.FactionMismatch),
                "a blueprint authored for another faction is refused");

            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(creditsBefore),
                "the refused order debits nothing");

            // Positive control: the USA HQ (110) passes the same gate.
            Assert.That(
                server.TryOrderBuild(BuildHeader(2), builder, new BlueprintId(110), 0, 0),
                Is.EqualTo(MatchCommandRejection.None),
                "an own-faction blueprint builds normally");
        }

        // ------------------------------------------------------------------
        // P1-09: secondary-economy class cap
        // ------------------------------------------------------------------

        [Test]
        public void WireBuild_SecondaryEconomy_CappedAtFourTotal()
        {
            // Four DIFFERENT secondary-income classes fill the ceiling; a
            // fifth building of yet another class is still cut off — the cap
            // applies to the class total, not one kind at a time. Builder ids
            // are spaced 50 apart so no spawn ever collides with a frame the
            // gate allocates right after the previous builder's high-water
            // mark.
            ushort[] secondary = { 160, 358, 415, 515, 215 };
            int[] builderIds = { 900, 950, 1000, 1050, 1100 };

            var server = CreateServer();

            for (var order = 0; order < 4; order++)
            {
                var builder = SpawnBuilder(server, builderIds[order], 0, -10_000);
                Assert.That(
                    server.TryOrderBuild(
                        BuildHeader((uint)(order + 1)), builder,
                        new BlueprintId(secondary[order]), order * 10, 0),
                    Is.EqualTo(MatchCommandRejection.None),
                    $"mixed-class order {order + 1} of 4 must start");
            }

            var fifthBuilder = SpawnBuilder(server, builderIds[4], 0, -10_000);
            Assert.That(
                server.TryOrderBuild(
                    BuildHeader(99), fifthBuilder, new BlueprintId(secondary[4]), 40, 0),
                Is.EqualTo(MatchCommandRejection.ConstructionRejected),
                "the fifth secondary building of the TOTAL class is refused");
        }

        // ------------------------------------------------------------------
        // P1-10: coordinate overflow
        // ------------------------------------------------------------------

        [Test]
        public void WireBuild_ExtremeCellCoordinates_Rejected()
        {
            var server = CreateServer();
            var builder = SpawnBuilder(server, 900, 0, -10_000);
            var creditsBefore = server.Economy.GetCredits(PlayerOne);
            var unitsBefore = server.UnitCount;

            Assert.That(
                server.TryOrderBuild(
                    BuildHeader(1), builder, new BlueprintId(113), int.MaxValue / 2, 0),
                Is.EqualTo(MatchCommandRejection.ConstructionRejected),
                "a huge cell must be refused BEFORE the mm multiplication, not wrap");

            Assert.That(
                server.TryOrderBuild(
                    BuildHeader(2), builder, new BlueprintId(113), 0, int.MinValue),
                Is.EqualTo(MatchCommandRejection.ConstructionRejected),
                "int.MinValue is refused without an Abs overflow");

            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(creditsBefore),
                "the refused orders debit nothing");
            Assert.That(server.UnitCount, Is.EqualTo(unitsBefore),
                "the refused orders spawn no frame");
            Assert.That(server.Construction.Count, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------
        // P1-11: queue validation
        // ------------------------------------------------------------------

        [Test]
        public void Produce_BuildingBlueprint_RejectedWithInvalidPayload()
        {
            var server = CreateServer();
            SpawnCompletedFactory(server);

            Assert.That(
                server.TryScheduleProduction(
                    ProduceHeader(1, server.CurrentTick + 1),
                    new EntityId(800), new BlueprintId(110)),
                Is.EqualTo(MatchCommandRejection.InvalidPayload),
                "buildings are raised through Build, never queued in a factory");
        }

        [Test]
        public void Produce_HackerCap_CountsQueuedHackers()
        {
            var server = CreateServer();

            // Eight living hackers; the factory queue carries the rest.
            for (var index = 0; index < 8; index++)
            {
                server.SpawnUnit(
                    PlayerOne, new CombatStats(100, 1, 0, 1),
                    new WorldPointMm(-50_000 + index * 2_000, -100_000), 500,
                    false, UnitKinds.Hacker);
            }

            SpawnCompletedFactory(server);

            var hackersQueued = 0;
            for (var order = 1; order <= 8; order++)
            {
                Assert.That(
                    server.TryScheduleProduction(
                        ProduceHeader((uint)order, server.CurrentTick + 1),
                        new EntityId(800), new BlueprintId(219)),
                    Is.EqualTo(MatchCommandRejection.None),
                    $"hacker {order + 8} of the 16 ceiling enqueues");
                hackersQueued++;
            }

            Assert.That(hackersQueued, Is.EqualTo(8));

            Assert.That(
                server.TryScheduleProduction(
                    ProduceHeader(99, server.CurrentTick + 1),
                    new EntityId(800), new BlueprintId(219)),
                Is.EqualTo(MatchCommandRejection.HackerCapacityReached),
                "hackers waiting in the queue count against the roster ceiling");
        }
    }
}
