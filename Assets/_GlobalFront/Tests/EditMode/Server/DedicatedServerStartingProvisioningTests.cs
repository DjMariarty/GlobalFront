using System;
using GlobalFront.Client;
using GlobalFront.Core.Catalog;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.DedicatedServer;
using GlobalFront.Server;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Server
{
    /// <summary>
    /// N-02 (ТЗ 6.0-C): the RTS starting provisioning — per player one real
    /// faction HQ (catalog row, 8x8 footprint, registered in the placement
    /// registry and the power ledger) plus a faction builder beside it, and
    /// the starting cash on the match config. The static roster builder is
    /// data-driven (no per-faction special cases); the integration scenario
    /// proves the dedicated runner provisions the lobby start end to end.
    /// </summary>
    [TestFixture]
    public sealed class DedicatedServerStartingProvisioningTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);
        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        [Test]
        public void BuildRtsStartingRoster_1v1_GeneratesHqAndBuilderPerPlayerWith10000Credits()
        {
            var owners = new[] { PlayerOne, PlayerTwo };

            var config = DedicatedServerRunner.BuildRtsStartingRoster(owners);

            Assert.That(config.UnitCount, Is.EqualTo(4),
                "one HQ and one builder per player");
            Assert.That(config.StartingCredits, Is.EqualTo(
                DedicatedServerRunner.DefaultStartingCredits));

            // Player 1: HQ on the west base point, builder beside it
            // (offset towards the map centre), catalog mobility and kind.
            var hq = config.Units[0];
            Assert.That(hq.Owner, Is.EqualTo(PlayerOne));
            Assert.That(hq.BlueprintId, Is.EqualTo(110), "the USA HQ row");
            Assert.That(hq.Position, Is.EqualTo(new WorldPointMm(-60_000, 0)));
            Assert.That(hq.SpeedMmPerTick, Is.EqualTo(0),
                "a completed building does not move");
            Assert.That(hq.UnitKind, Is.EqualTo(UnitKinds.BaseStructure));
            Assert.That(hq.AutoAcquireEnemies, Is.False);

            var builder = config.Units[1];
            Assert.That(builder.Owner, Is.EqualTo(PlayerOne));
            Assert.That(builder.BlueprintId, Is.EqualTo(174), "the USA Dozer row");
            Assert.That(builder.Position, Is.EqualTo(new WorldPointMm(-55_000, 0)));
            Assert.That(builder.SpeedMmPerTick, Is.EqualTo(500),
                "the builder's mobility comes from the catalog row");
            Assert.That(builder.UnitKind, Is.EqualTo(UnitKinds.Tank));
            Assert.That(builder.AutoAcquireEnemies, Is.False);

            // Player 2: mirrored to the east, same faction rows
            // (faction-less lobby provisions USA).
            Assert.That(config.Units[2].BlueprintId, Is.EqualTo(110));
            Assert.That(config.Units[2].Position, Is.EqualTo(new WorldPointMm(60_000, 0)));
            Assert.That(config.Units[3].BlueprintId, Is.EqualTo(174));
            Assert.That(config.Units[3].Position, Is.EqualTo(new WorldPointMm(55_000, 0)));
        }

        [Test]
        public void BuildRtsStartingRoster_2v2_UsesTeamBasePositionsAndFactionBlueprints()
        {
            var owners = new[]
            {
                new PlayerId(1), new PlayerId(2), new PlayerId(3), new PlayerId(4)
            };
            var factions = new byte[]
            {
                FactionId.USA, FactionId.China, FactionId.GLA, FactionId.Russia
            };

            var config = DedicatedServerRunner.BuildRtsStartingRoster(
                owners, factions, MatchTopologyMode.Team2v2);

            Assert.That(config.UnitCount, Is.EqualTo(8));
            Assert.That(config.StartingCredits, Is.EqualTo(
                DedicatedServerRunner.DefaultStartingCredits));

            // HQ rows follow the slot factions, positions follow the team
            // base geometry (Step 5.6.1).
            ushort[] expectedHq = { 110, 210, 310, 410 };
            ushort[] expectedBuilder = { 174, 224, 373, 424 };
            byte[] expectedBuilderKind =
            {
                UnitKinds.Tank, UnitKinds.Tank, UnitKinds.Infantry, UnitKinds.Tank
            };
            var teamBases = new[]
            {
                new WorldPointMm(-35_000, 20_000),
                new WorldPointMm(-35_000, -20_000),
                new WorldPointMm(35_000, 20_000),
                new WorldPointMm(35_000, -20_000),
            };

            for (var slot = 0; slot < 4; slot++)
            {
                var hq = config.Units[slot * 2];
                Assert.That(hq.BlueprintId, Is.EqualTo(expectedHq[slot]),
                    $"slot {slot}: the faction HQ row");
                Assert.That(hq.Position, Is.EqualTo(teamBases[slot]));
                Assert.That(hq.SpeedMmPerTick, Is.EqualTo(0));

                var builder = config.Units[(slot * 2) + 1];
                Assert.That(builder.BlueprintId, Is.EqualTo(expectedBuilder[slot]),
                    $"slot {slot}: the faction builder row");
                Assert.That(builder.UnitKind, Is.EqualTo(expectedBuilderKind[slot]),
                    $"slot {slot}: the builder's legacy kind comes from the catalog");
                Assert.That(builder.Position.X,
                    Is.EqualTo(teamBases[slot].X <= 0
                        ? teamBases[slot].X + DedicatedServerRunner.BuilderOffsetMm
                        : teamBases[slot].X - DedicatedServerRunner.BuilderOffsetMm));
                Assert.That(builder.Position.Z, Is.EqualTo(teamBases[slot].Z));
            }
        }

        [Test]
        public void DedicatedServer_RtsStartingBase_ProvisionsHqBuilderAndCredits()
        {
            var savedTargetFrameRate = Application.targetFrameRate;
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var serverCarrier = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var runnerObject = new GameObject("DedicatedServerRunner_RtsStartingBase");
            DedicatedServerRunner runner = null;
            var clients = new RemoteSliceSession[2];

            try
            {
                runner = runnerObject.AddComponent<DedicatedServerRunner>();
                runner.RosterMode = MatchRosterMode.RtsStartingBase;
                runner.Initialize(serverCarrier, clock);

                for (var index = 0; index < clients.Length; index++)
                {
                    clients[index] = new RemoteSliceSession(
                        OwnDatagramCarrier.CreateClient(
                            pipe, pipe.CreateEndpoint(), serverAddress, clock), clock);
                    clients[index].Connect("virtual", 0);
                }

                Assert.That(
                    PumpUntil(
                        runner, pipe, clock,
                        () => runner.IsMatchActive && runner.Host.UnitCount == 4,
                        "the RTS-provisioned 1v1 must start with 4 units",
                        clients),
                    Is.True);

                Assert.That(runner.Host.UnitCount, Is.EqualTo(4),
                    "one HQ and one builder per player — not the 12-unit prototype army");

                var snapshots = new ServerUnitSnapshot[SimulationConstants.MaxUnitsPerMatch];
                var count = runner.Host.CopySnapshots(snapshots);
                Assert.That(count, Is.EqualTo(4));

                var westHq = FindUnit(snapshots, PlayerOne, 110);
                Assert.That(westHq.Position, Is.EqualTo(new WorldPointMm(-60_000, 0)));
                Assert.That(westHq.UnitKind, Is.EqualTo(UnitKinds.BaseStructure));
                var westBuilder = FindUnit(snapshots, PlayerOne, 174);
                Assert.That(westBuilder.Position, Is.EqualTo(new WorldPointMm(-55_000, 0)));
                Assert.That(westBuilder.UnitKind, Is.EqualTo(UnitKinds.Tank));
                Assert.That(FindUnit(snapshots, PlayerTwo, 110).Position,
                    Is.EqualTo(new WorldPointMm(60_000, 0)));
                Assert.That(FindUnit(snapshots, PlayerTwo, 174).Position,
                    Is.EqualTo(new WorldPointMm(55_000, 0)));

                Assert.That(
                    runner.Host.Server.Economy.GetCredits(PlayerOne),
                    Is.EqualTo(DedicatedServerRunner.DefaultStartingCredits),
                    "player 1 starts with the RTS cash");
                Assert.That(
                    runner.Host.Server.Economy.GetCredits(PlayerTwo),
                    Is.EqualTo(DedicatedServerRunner.DefaultStartingCredits),
                    "player 2 starts with the RTS cash");

                Assert.That(
                    runner.Host.Server.Placement.IsPointOccupied(new WorldPointMm(-60_000, 0)),
                    Is.True, "the west HQ blocks its base point");
                Assert.That(
                    runner.Host.Server.Placement.IsPointOccupied(new WorldPointMm(60_000, 0)),
                    Is.True, "the east HQ blocks its base point");

                Assert.That(
                    runner.Host.Server.PowerLedger.GetPowerState(PlayerOne).Produced,
                    Is.EqualTo(5),
                    "the provisioned USA HQ feeds +5 into the grid as completed");
                Assert.That(
                    runner.Host.Server.PowerLedger.GetPowerState(PlayerTwo).Produced,
                    Is.EqualTo(5));
            }
            finally
            {
                for (var index = 0; index < clients.Length; index++)
                {
                    clients[index]?.Dispose();
                }

                runner?.Shutdown();
                if (runnerObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(runnerObject);
                }

                Application.targetFrameRate = savedTargetFrameRate;
            }
        }

        [Test]
        public void InitializeMatch_ProvisioningPath_IsAllocationFree()
        {
            // InitializeMatch runs once per server, so the warmup and the
            // measured call use two fresh servers built from the same
            // provisioning config (new MatchServer / the config itself stay
            // outside the meter; the meter covers the initialization path —
            // spawn loop, placement/power registration, credit payout).
            var owners = new[] { PlayerOne, PlayerTwo };
            var config = DedicatedServerRunner.BuildRtsStartingRoster(owners);

            var warmupServer = new MatchServer();
            warmupServer.InitializeMatch(config);

            var measuredServer = new MatchServer();

            // GC.GetAllocatedBytesForCurrentThread is the precise allocation
            // meter for a main-thread call — the Unity GcAssert constraint
            // proved flaky at recognizing method-group delegates here, while
            // this counter measures exactly the DoD invariant (0 managed
            // allocations on the initialization path).
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            measuredServer.InitializeMatch(config);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;

            Assert.That(allocated, Is.EqualTo(0),
                "the provisioning initialization (spawns, placement/power " +
                $"registration, credit payout) must not allocate (allocated " +
                $"{allocated} bytes)");
        }

        /// <summary>
        /// The RunTeam2v2 pump loop, trimmed to the provisioning scenario:
        /// advance the pipe and the clock, drain the server frame and pump
        /// every client's handshake — the lobby needs the client-side pumps
        /// to finish their ConnectAccept round-trips before the match starts.
        /// </summary>
        private static bool PumpUntil(
            DedicatedServerRunner runner,
            VirtualNetworkPipe pipe,
            VirtualTransportClock clock,
            Func<bool> condition,
            string message,
            params RemoteSliceSession[] sessions)
        {
            for (var elapsed = 0; elapsed < 15_000 && !condition(); elapsed += 10)
            {
                pipe.Advance(10);
                clock.Advance(10);
                runner.Update();
                for (var index = 0; index < sessions.Length; index++)
                {
                    sessions[index]?.Pump(clock.NowMs);
                }
            }

            if (!condition())
            {
                Assert.Fail(message + $" (state: active={runner.IsMatchActive} " +
                    $"units={runner.Host?.UnitCount.ToString() ?? "n/a"})");
                return false;
            }

            return true;
        }

        private static ServerUnitSnapshot FindUnit(
            ServerUnitSnapshot[] snapshots, PlayerId owner, ushort blueprintId)
        {
            for (var index = 0; index < snapshots.Length; index++)
            {
                if (snapshots[index].Owner == owner &&
                    snapshots[index].BlueprintId == blueprintId)
                {
                    return snapshots[index];
                }
            }

            Assert.Fail($"no unit with blueprint {blueprintId} belongs to player {owner.Value}");
            return default;
        }
    }
}
