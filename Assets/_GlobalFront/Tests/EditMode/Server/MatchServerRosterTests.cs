using GlobalFront.Core.Catalog;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Structures;
using GlobalFront.Server;
using NUnit.Framework;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Server
{
    /// <summary>
    /// Step 5.5.3 server-side roster integration: the wire build gate requires
    /// a physical builder and caps secondary-income buildings at four per
    /// player, produced units carry their catalog stats, and a catalog-built
    /// building completes at its real blueprint health instead of the frame
    /// placeholder.
    /// </summary>
    [TestFixture]
    public sealed class MatchServerRosterTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);
        private static readonly PlayerId PlayerTwo = new PlayerId(2);
        private static readonly EntityId BuilderEntity = new EntityId(900);

        private static MatchServer CreateServer()
        {
            var server = new MatchServer();

            // The known MatchServer gotcha: a roster holding a single living
            // owner resolves to Victory on the first ResolveCombat and every
            // later TickOnce early-exits. A passive far-away opponent keeps
            // the simulation running.
            server.SpawnUnit(
                PlayerTwo, new CombatStats(100, 1, 0, 1), new WorldPointMm(-150_000, 0), 500);

            return server;
        }

        private static void SpawnBuilder(MatchServer server)
        {
            Assert.That(
                server.SpawnUnitWithEntity(
                    BuilderEntity, PlayerOne, new CombatStats(100, 1, 0, 1),
                    new WorldPointMm(0, -50_000), 500, false, UnitKinds.Tank),
                Is.EqualTo(BuilderEntity),
                "scenario setup: the physical builder must spawn");
        }

        private static CommandHeader BuildHeader(uint sequence) =>
            new CommandHeader(PlayerOne, sequence, 1UL, GameCommandType.Build);

        [Test]
        public void TryOrderBuild_WithoutBuilder_IsRejected()
        {
            var server = CreateServer();

            Assert.That(
                server.TryOrderBuild(BuildHeader(1), default, new BlueprintId(110), 0, 0),
                Is.EqualTo(MatchCommandRejection.BuilderRequired),
                "no faction builds from thin air: every blueprint needs its builder entity");
        }

        [Test]
        public void TryOrderBuild_SecondaryEconomyFifthBuilding_IsRejected()
        {
            // The five secondary-income classes: USA Drop Zone (160), China
            // Internet Center (215), GLA Black Market (358), Russia Oil
            // Drilling Rig (415), NATO Coalition Logistics Hub (515). The cap
            // counts completed buildings and in-progress frames alike.
            ushort[] secondary = { 160, 215, 358, 415, 515 };

            foreach (var blueprintId in secondary)
            {
                var server = CreateServer();
                SpawnBuilder(server);

                for (var order = 0; order < 4; order++)
                {
                    Assert.That(
                        server.TryOrderBuild(
                            BuildHeader((uint)(order + 1)), BuilderEntity,
                            new BlueprintId(blueprintId), order * 10, 0),
                        Is.EqualTo(MatchCommandRejection.None),
                        $"blueprint {blueprintId}: order {order + 1} of 4 must start");
                }

                Assert.That(
                    server.TryOrderBuild(
                        BuildHeader(99), BuilderEntity, new BlueprintId(blueprintId), 40, 0),
                    Is.EqualTo(MatchCommandRejection.ConstructionRejected),
                    $"blueprint {blueprintId}: the fifth secondary building is cut off server-side");
            }
        }

        [Test]
        public void ProducedUnit_AndCompletedBuilding_CarryCatalogStats()
        {
            var server = CreateServer();

            // Build the catalog's Russia War Factory (414) as a production
            // building outside the wire gate: Modular, one work tick at the
            // test's 1000‰ rate, tied to its blueprint row. The Reactor (411)
            // comes up with it: without the +7 grid the -2 factory blacks the
            // player out and the production queue never advances.
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 800, new WorldPointMm(0, 0),
                    new BuildingFootprint(3000, 3000), ConstructionMethod.Modular,
                    1000, 2000, -2, true, null, UnitKinds.BaseStructure, 414, out var startRejection),
                Is.True, $"factory start must succeed: {startRejection}");
            Assert.That(
                server.TryStartBuilding(
                    PlayerOne, 801, new WorldPointMm(15_000, 0),
                    new BuildingFootprint(2000, 2000), ConstructionMethod.Modular,
                    1000, 900, 7, false, null, UnitKinds.BaseStructure, 411, out startRejection),
                Is.True, $"reactor start must succeed: {startRejection}");
            server.ExecuteTicks(2);

            // Completion re-arms the building with the row's real stats: the
            // 2600-health Russia factory line, not the 500-health frame
            // placeholder.
            Assert.That(server.TryGetUnit(new EntityId(800), out var factory), Is.True);
            Assert.That(factory.CurrentHealth, Is.EqualTo(2600),
                "a completed catalog building owns its blueprint health");
            Assert.That(factory.BlueprintId, Is.EqualTo(414));

            // Queue a T-90M (401) through the wire gate: build time and cost
            // resolve from the catalog (500 ticks, 1100 credits).
            Assert.That(
                server.TryScheduleProduction(
                    new CommandHeader(PlayerOne, 1, 1UL, GameCommandType.Produce),
                    new EntityId(800), new BlueprintId(401)),
                Is.EqualTo(MatchCommandRejection.None));

            server.ExecuteTicks(505);

            var produced = FindUnitByBlueprint(server, 401);
            Assert.That(produced.UnitKind, Is.EqualTo(UnitKinds.Tank),
                "the T-90M maps to the Tank presentation slot");
            Assert.That(produced.CurrentHealth, Is.EqualTo(600),
                "the T-90M spawns with its catalog health, not the shared default 100");
            Assert.That(produced.BlueprintId, Is.EqualTo(401));
        }

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
    }
}
