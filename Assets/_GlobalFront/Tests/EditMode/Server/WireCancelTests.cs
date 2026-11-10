using GlobalFront.Core.Catalog;
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

namespace GlobalFront.Tests.EditMode.Server
{
    /// <summary>
    /// N-05 (ТЗ 6.0-B): the wire Cancel gate
    /// <see cref="MatchServer.TryOrderCancel"/> — construction frames and
    /// production queue slots cancel through the same entry point the
    /// transport dispatches to, with the P1-04 full-refund semantics and the
    /// fail-closed rejection ladder (unknown / foreign / non-frame).
    /// The allocation-free invariant covers the gate itself; the decode and
    /// ingest path stays under the suite-wide policy (WireCommand is a class,
    /// ingest may allocate — see CommandIngress_ZeroGC_OnCommandProcessingHotPath).
    /// </summary>
    [TestFixture]
    public sealed class WireCancelTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);
        private static readonly PlayerId PlayerTwo = new PlayerId(2);

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

        private static CommandHeader CancelHeader(uint sequence) =>
            new CommandHeader(PlayerOne, sequence, 1UL, GameCommandType.Cancel);

        private static CommandHeader BuildHeader(uint sequence) =>
            new CommandHeader(PlayerOne, sequence, 1UL, GameCommandType.Build);

        private static CommandHeader ProduceHeader(uint sequence, ulong requestedTick) =>
            new CommandHeader(PlayerOne, sequence, requestedTick, GameCommandType.Produce);

        private static EntityId SpawnBuilder(MatchServer server, int idValue)
        {
            var entity = new EntityId((ulong)idValue);
            Assert.That(
                server.SpawnUnitWithEntity(
                    entity, PlayerOne, new CombatStats(100, 1, 0, 1),
                    new WorldPointMm(0, -10_000), 500, false, UnitKinds.Tank,
                    blueprintId: 174),
                Is.EqualTo(entity),
                "scenario setup: the physical builder must spawn");
            return entity;
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

        [Test]
        public void WireCancel_BuildingConstruction_CancelsAndRefundsCredits()
        {
            var server = CreateServer();
            var builder = SpawnBuilder(server, 900);
            var barracks = Def(113);
            var startingCredits = server.Economy.GetCredits(PlayerOne);

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), builder, new BlueprintId(113), 0, 0),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                server.Economy.GetCredits(PlayerOne),
                Is.EqualTo(startingCredits - barracks.Cost),
                "scenario sanity: the accepted build debits its catalog cost");

            Assert.That(server.Construction.Count, Is.EqualTo(1));

            var frame = FindFrame(server, 113);
            Assert.That(
                server.TryOrderCancel(CancelHeader(2), frame.Entity, -1),
                Is.EqualTo(MatchCommandRejection.None),
                "slot -1 routes the wire cancel to the construction frame");

            Assert.That(server.Construction.Count, Is.EqualTo(0),
                "the cancelled site leaves the construction registry");
            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(startingCredits),
                "the full (100%) refund reaches the wallet");
            Assert.That(
                server.TryGetUnit(frame.Entity, out var corpse) && corpse.CurrentHealth == 0,
                Is.True,
                "the frame dies through the regular death flow");

            // The grid cell is free again: the same cell accepts a new order.
            Assert.That(
                server.TryOrderBuild(BuildHeader(3), builder, new BlueprintId(113), 0, 0),
                Is.EqualTo(MatchCommandRejection.None),
                "the cancelled cell accepts a fresh build order");
        }

        [Test]
        public void WireCancel_ProductionQueue_CancelsSlotAndRefundsCredits()
        {
            var server = CreateServer();
            SpawnCompletedFactory(server);
            var tankOrder = Def(401);
            var creditsAfterFactory = server.Economy.GetCredits(PlayerOne);

            Assert.That(
                server.TryScheduleProduction(
                    ProduceHeader(1, server.CurrentTick + 1),
                    new EntityId(800), new BlueprintId(401)),
                Is.EqualTo(MatchCommandRejection.None));
            Assert.That(
                server.Production.TryGetBuildingState(800, out var queue) &&
                    queue.QueueCount == 1,
                Is.True, "scenario sanity: the queued order sits in the factory");
            Assert.That(
                server.Economy.GetCredits(PlayerOne),
                Is.EqualTo(creditsAfterFactory - tankOrder.Cost));

            Assert.That(
                server.TryOrderCancel(CancelHeader(2), new EntityId(800), 0),
                Is.EqualTo(MatchCommandRejection.None),
                "slot 0 routes the wire cancel to the production queue");

            Assert.That(
                server.Production.TryGetBuildingState(800, out var emptied) &&
                    emptied.QueueCount == 0,
                Is.True, "the cancelled slot leaves the factory queue empty");
            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(creditsAfterFactory),
                "the full (100%) production refund reaches the wallet");
        }

        [Test]
        public void WireCancel_ForeignBuilding_IsRejected()
        {
            var server = CreateServer();

            // PlayerTwo's construction frame, started the direct way (no
            // builder needed for the site to exist).
            Assert.That(
                server.TryStartBuilding(
                    PlayerTwo, 900, new WorldPointMm(0, 0),
                    new BuildingFootprint(2000, 2000), ConstructionMethod.DozerWorker,
                    25, 500, 0, false, null, UnitKinds.BaseStructure, 113, out var rejection),
                Is.True, $"foreign frame start must succeed: {rejection}");

            var creditsBefore = server.Economy.GetCredits(PlayerOne);

            Assert.That(
                server.TryOrderCancel(CancelHeader(1), new EntityId(900), -1),
                Is.EqualTo(MatchCommandRejection.NotEntityOwner),
                "a rival's frame is not cancellable through the wire");

            Assert.That(server.Construction.Count, Is.EqualTo(1),
                "the foreign site stays under construction");
            Assert.That(
                server.Economy.GetCredits(PlayerOne), Is.EqualTo(creditsBefore),
                "a rejected cancel moves no credits");
        }

        [Test]
        public void WireCancel_Gate_IsAllocationFree()
        {
            var server = CreateServer();
            var builder = SpawnBuilder(server, 900);

            // Build a frame and cancel it once outside the meter: the first
            // call JITs the gate, the measured call must be allocation-free.
            Assert.That(
                server.TryOrderBuild(BuildHeader(1), builder, new BlueprintId(113), 0, 0),
                Is.EqualTo(MatchCommandRejection.None));

            // Warmup.
            server.TryOrderCancel(CancelHeader(2), new EntityId(0), -1);

            var frame = FindFrame(server, 113);
            // The gate returns MatchCommandRejection: without the explicit
            // TestDelegate the value-returning lambda binds to the generic
            // Assert.That<T> overload and NUnit throws at runtime (the
            // BlueprintCatalog suite hit the same trap).
            TestDelegate measured = () =>
                server.TryOrderCancel(CancelHeader(3), frame.Entity, -1);
            Assert.That(
                measured,
                GcAssert.Not.AllocatingGCMemory(),
                "the cancel gate executes without allocations");
        }

        private static ServerUnitSnapshot FindFrame(MatchServer server, ushort blueprintId)
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
    }
}
