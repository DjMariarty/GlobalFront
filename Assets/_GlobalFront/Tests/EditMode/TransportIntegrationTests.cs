#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Snapshot;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Phase 2.5 end-to-end integration over the deterministic
    /// VirtualNetworkPipe: 2 clients + 1 host through the FULL chain
    /// client endpoint → carrier → session attribution → session gate →
    /// MatchServer, RequestedTick semantics on TickOnce, snapshot delivery
    /// with latest-wins ordering keys, graceful disconnect, timeout feeding
    /// the Phase 2.4 grace machinery, and run-to-run determinism.
    /// </summary>
    [TestFixture]
    public sealed class TransportIntegrationTests
    {
        private sealed class World
        {
            public TransportTestSupport.Rig Rig;
            public ClientTransportEndpoint ClientA;
            public ClientTransportEndpoint ClientB;
            public NetworkCommandChannel ChannelA;
            public NetworkCommandChannel ChannelB;
            public readonly List<CommandAckPayload> AcksA = new List<CommandAckPayload>();
            public readonly List<CommandAckPayload> AcksB = new List<CommandAckPayload>();
            public readonly List<(ulong Tick, byte[] Payload)> SnapshotsA =
                new List<(ulong, byte[])>();

            public static World Create(ImpairmentProfile profile)
            {
                var world = new World
                {
                    Rig = TransportTestSupport.CreateRig(profile)
                };
                world.ClientA = TransportTestSupport.CreateClient(world.Rig);
                world.ClientB = TransportTestSupport.CreateClient(world.Rig);
                Assert.That(
                    TransportTestSupport.PumpUntilEstablished(world.Rig, world.ClientA), Is.True);
                Assert.That(
                    TransportTestSupport.PumpUntilEstablished(world.Rig, world.ClientB), Is.True);

                var config = TransportTestSupport.BuildMatchConfig(
                    new PlayerId(1), new PlayerId(2));
                Assert.That(
                    world.Rig.Host.TryStartSessionMatch(world.Rig.Match, config, out _), Is.True);

                world.ChannelA = new NetworkCommandChannel(world.ClientA);
                world.ChannelB = new NetworkCommandChannel(world.ClientB);
                world.ChannelA.CommandResultReceived += world.AcksA.Add;
                world.ChannelB.CommandResultReceived += world.AcksB.Add;
                world.ClientA.SnapshotReceived += (tick, payload, length) =>
                    // The endpoint hands out reusable buffers: retain a copy.
                    world.SnapshotsA.Add((tick, payload.AsSpan(0, length).ToArray()));
                return world;
            }

            public void Pump(long milliseconds = 5)
            {
                Rig.Pump(milliseconds);
                ClientA.Pump(Rig.Clock.NowMs);
                ClientB.Pump(Rig.Clock.NowMs);
            }
        }

        [Test]
        public void TwoClients_AttachWithDistinctServerAssignedPlayerIds()
        {
            var world = World.Create(new ImpairmentProfile());

            Assert.That(world.ClientA.Player.IsValid, Is.True);
            Assert.That(world.ClientB.Player.IsValid, Is.True);
            Assert.That(world.ClientA.Player, Is.Not.EqualTo(world.ClientB.Player));
            Assert.That(world.ClientA.Match, Is.EqualTo(world.Rig.Match));
            Assert.That(world.ClientB.Match, Is.EqualTo(world.Rig.Match));
            Assert.That(world.Rig.Host.Sessions, Is.Not.Null);
        }

        [Test]
        public void Commands_BothPlayers_AppliedWithRequestedTickSemantics()
        {
            var world = World.Create(new ImpairmentProfile());
            var server = world.Rig.Host.Server;

            var entityA = FirstEntityOf(server, world.ClientA.Player);
            var entityB = FirstEntityOf(server, world.ClientB.Player);
            var startA = PositionOf(server, entityA);
            var startB = PositionOf(server, entityB);

            var tick = world.Rig.Host.CurrentTick + 1;
            world.ChannelA.TrySubmitMove(
                new CommandHeader(world.ClientA.Player, 1, tick, GameCommandType.Move),
                new[] { entityA },
                new WorldPointMm(startA.X + 5000, startA.Z),
                new FormationSpec(1, 2000, CardinalFacing.North));
            world.ChannelB.TrySubmitMove(
                new CommandHeader(world.ClientB.Player, 1, tick, GameCommandType.Move),
                new[] { entityB },
                new WorldPointMm(startB.X - 5000, startB.Z),
                new FormationSpec(1, 2000, CardinalFacing.North));

            // Pump until both authoritative acks arrive.
            var elapsed = 0L;
            while ((world.AcksA.Count < 1 || world.AcksB.Count < 1) && elapsed < 5000)
            {
                world.Pump(5);
                elapsed += 5;
            }

            Assert.That(world.AcksA.Count, Is.EqualTo(1));
            Assert.That(world.AcksB.Count, Is.EqualTo(1));
            Assert.That(world.AcksA[0].IsAccepted, Is.True);
            Assert.That(world.AcksB[0].IsAccepted, Is.True);

            // The authoritative tick applies the commands: exactly one step
            // of movement per tick (RequestedTick N applied during tick N).
            world.Rig.Host.TickOnce();
            var movedA = PositionOf(server, entityA);
            var movedB = PositionOf(server, entityB);
            Assert.That(movedA.X, Is.EqualTo(startA.X + 350));
            Assert.That(movedB.X, Is.EqualTo(startB.X - 350));
        }

        [Test]
        public void SnapshotDelivery_CarriesSnapshotTick_ForLatestWins()
        {
            var world = World.Create(new ImpairmentProfile());
            var session = world.ClientA.Session;

            var payloadA = new byte[] { 1, 1, 1 };
            var payloadStale = new byte[] { 2, 2 };
            var payloadLatest = new byte[] { 3, 3, 3, 3 };

            Assert.That(
                world.Rig.ServerTransport.SendSnapshot(session, 5, payloadA, payloadA.Length),
                Is.True);
            Assert.That(
                world.Rig.ServerTransport.SendSnapshot(session, 3, payloadStale, payloadStale.Length),
                Is.True);
            Assert.That(
                world.Rig.ServerTransport.SendSnapshot(session, 7, payloadLatest, payloadLatest.Length),
                Is.True);

            var elapsed = 0L;
            while (world.SnapshotsA.Count < 3 && elapsed < 5000)
            {
                world.Pump(5);
                elapsed += 5;
            }

            Assert.That(world.SnapshotsA.Count, Is.EqualTo(3),
                "C2 delivers sequenced by send order; ordering key is SnapshotTick");

            // Consumer-side latest-wins over the envelope SnapshotTick: the
            // transport payload itself stays opaque.
            ulong applied = 0;
            byte[] appliedPayload = null;
            foreach (var (tick, payload) in world.SnapshotsA)
            {
                if (tick > applied)
                {
                    applied = tick;
                    appliedPayload = payload;
                }
            }

            Assert.That(applied, Is.EqualTo(7ul));
            Assert.That(appliedPayload, Is.EqualTo(payloadLatest));
        }

        [Test]
        public void Snapshot_MultiFragment_C2_LatestWins_AfterReassembly()
        {
            var world = World.Create(new ImpairmentProfile());
            var session = world.ClientA.Session;

            // Two multi-fragment snapshots through the FULL stack: each must
            // arrive as exactly ONE logical snapshot (never one event per
            // fragment), byte-identical, with latest-wins applied to the
            // message-level SnapshotTick after full reassembly.
            var older = BuildSnapshotPayload(150, 100);
            var newer = BuildSnapshotPayload(220, 900);

            Assert.That(
                world.Rig.ServerTransport.SendSnapshot(session, 5, older, older.Length), Is.True);
            Assert.That(
                world.Rig.ServerTransport.SendSnapshot(session, 9, newer, newer.Length), Is.True);

            var elapsed = 0L;
            while (world.SnapshotsA.Count < 2 && elapsed < 10000)
            {
                world.Pump(5);
                elapsed += 5;
            }

            Assert.That(world.SnapshotsA.Count, Is.EqualTo(2),
                "multi-fragment C2 snapshots deliver as one logical snapshot each");
            Assert.That(world.SnapshotsA[0].Tick, Is.EqualTo(5ul));
            Assert.That(world.SnapshotsA[0].Payload, Is.EqualTo(older),
                "older snapshot must reassemble byte-identical");
            Assert.That(world.SnapshotsA[1].Tick, Is.EqualTo(9ul));
            Assert.That(world.SnapshotsA[1].Payload, Is.EqualTo(newer),
                "newer snapshot must reassemble byte-identical");

            ulong applied = 0;
            byte[] appliedPayload = null;
            foreach (var (tick, payload) in world.SnapshotsA)
            {
                if (tick > applied)
                {
                    applied = tick;
                    appliedPayload = payload;
                }
            }

            Assert.That(applied, Is.EqualTo(9ul), "latest-wins must select the newest SnapshotTick");
            Assert.That(appliedPayload, Is.EqualTo(newer));
        }

        [Test]
        public void LargeSnapshot_3000Entities_OwnCarrier_FullDelivery_AcceptedAsLatest()
        {
            // Reorder/jitter but NO loss: a ~117 KB snapshot fragments into
            // ~100 C2 datagrams and must fully reassemble through the own
            // carrier and be accepted as the latest snapshot.
            var world = World.Create(new ImpairmentProfile
            {
                LatencyMs = 2,
                ReorderJitterMs = 5
            });
            var session = world.ClientA.Session;

            var small = BuildSnapshotPayload(4, 1);
            var large = BuildSnapshotPayload(3000, 777);

            Assert.That(
                world.Rig.ServerTransport.SendSnapshot(session, 5, small, small.Length), Is.True);
            Assert.That(
                world.Rig.ServerTransport.SendSnapshot(session, 42, large, large.Length), Is.True);

            var elapsed = 0L;
            var largeDelivered = 0;
            while (largeDelivered < 1 && elapsed < 30000)
            {
                world.Pump(5);
                elapsed += 5;
                largeDelivered = 0;
                foreach (var (tick, _) in world.SnapshotsA)
                {
                    if (tick == 42ul)
                    {
                        largeDelivered++;
                    }
                }
            }

            Assert.That(largeDelivered, Is.EqualTo(1),
                "the 3000-entity fragmented snapshot must be delivered exactly once");

            byte[] deliveredPayload = null;
            ulong latestTick = 0;
            foreach (var (tick, payload) in world.SnapshotsA)
            {
                if (tick > latestTick)
                {
                    latestTick = tick;
                }

                if (tick == 42ul)
                {
                    deliveredPayload = payload;
                }
            }

            Assert.That(deliveredPayload, Is.EqualTo(large),
                "the large snapshot must reassemble byte-identical");
            Assert.That(latestTick, Is.EqualTo(42ul),
                "the large snapshot must be accepted as the latest snapshot");
        }

        private static byte[] BuildSnapshotPayload(int entities, int seed)
        {
            var snapshots = new ServerUnitSnapshot[entities];
            for (var index = 0; index < entities; index++)
            {
                snapshots[index] = new ServerUnitSnapshot(
                    new EntityId((ulong)index + 1),
                    new PlayerId(1),
                    new WorldPointMm(index * 100 + seed, index * 50 + seed),
                    100 + (index & 0x3F),
                    (index & 1) == 0,
                    new WorldPointMm(seed, seed),
                    new EntityId((ulong)seed),
                    (index & 2) == 0);
            }

            return SnapshotSerializer.Serialize(
                new SnapshotPacketHeader(
                    SnapshotProtocol.Version, 1, (uint)entities),
                snapshots);
        }

        [Test]
        public void GracefulDisconnect_ClosesSession_AndReleasesBinding()
        {
            var world = World.Create(new ImpairmentProfile());
            var session = world.ClientA.Session;

            world.ClientA.GracefulDisconnect();
            var elapsed = 0L;
            var closed = false;
            while (!closed && elapsed < 5000)
            {
                world.Pump(5);
                closed = world.Rig.Host.Sessions.TryGetSession(session, out var record) &&
                    record.State == SessionState.Closed;
                elapsed += 5;
            }

            Assert.That(closed, Is.True, "graceful disconnect must close the session");
            Assert.That(world.Rig.ServerTransport.AttachedCount, Is.EqualTo(1),
                "only the remaining client stays attached");
        }

        [Test]
        public void TransportTimeout_FeedsPhase24Grace_Mechanics()
        {
            var world = World.Create(new ImpairmentProfile());
            var session = world.ClientA.Session;

            // Full partition: no traffic, carriers time out on the clock.
            world.Rig.Pipe.SetPartitioned(true);
            var elapsed = 0L;
            var disconnected = false;
            while (!disconnected && elapsed < TransportProtocol.IdleTimeoutMs + 10000)
            {
                world.Pump(100);
                disconnected = world.Rig.Host.Sessions.TryGetSession(session, out var record) &&
                    record.State == SessionState.Disconnected;
                elapsed += 100;
            }

            Assert.That(disconnected, Is.True,
                "transport timeout must notify the SessionManager (grace window starts)");

            // The Phase 2.4 grace semantics remain intact: the PlayerId
            // binding survives while the session is Disconnected.
            world.Rig.Host.Sessions.TryGetSession(session, out var finalRecord);
            Assert.That(finalRecord.Player.IsValid, Is.True);
        }

        [Test]
        public void DeterministicRuns_ProduceIdenticalAuthoritativeState()
        {
            var first = RunScenario(seed: 2026);
            var second = RunScenario(seed: 2026);

            Assert.That(second.FinalPositions, Is.EqualTo(first.FinalPositions));
            Assert.That(second.AcceptedAcks, Is.EqualTo(first.AcceptedAcks));
        }

        private static (string FinalPositions, int AcceptedAcks) RunScenario(int seed)
        {
            var world = World.Create(new ImpairmentProfile
            {
                LossProbability = 0.1,
                DuplicationProbability = 0.1,
                ReorderJitterMs = 20,
                LatencyMs = 5,
                Seed = seed
            });

            var server = world.Rig.Host.Server;
            var entity = FirstEntityOf(server, world.ClientA.Player);
            var tick = world.Rig.Host.CurrentTick + 1;
            world.ChannelA.TrySubmitMove(
                new CommandHeader(world.ClientA.Player, 1, tick, GameCommandType.Move),
                new[] { entity },
                new WorldPointMm(8000, 0),
                new FormationSpec(1, 2000, CardinalFacing.North));

            var elapsed = 0L;
            while (world.AcksA.Count < 1 && elapsed < 10000)
            {
                world.Pump(5);
                elapsed += 5;
            }

            for (var index = 0; index < 10; index++)
            {
                world.Rig.Host.TickOnce();
            }

            var snapshots = world.Rig.Host.GetAllSnapshots();
            var positions = new System.Text.StringBuilder();
            for (var index = 0; index < snapshots.Length; index++)
            {
                positions.Append(snapshots[index].Entity.Value)
                    .Append(':')
                    .Append(snapshots[index].Position.X)
                    .Append(',')
                    .Append(snapshots[index].Position.Z)
                    .Append(';');
            }

            var accepted = 0;
            foreach (var ack in world.AcksA)
            {
                if (ack.IsAccepted)
                {
                    accepted++;
                }
            }

            return (positions.ToString(), accepted);
        }

        private static EntityId FirstEntityOf(GlobalFront.Server.MatchServer server, PlayerId player)
        {
            var snapshots = server.GetAllSnapshots();
            for (var index = 0; index < snapshots.Length; index++)
            {
                if (snapshots[index].Owner == player)
                {
                    return snapshots[index].Entity;
                }
            }

            Assert.Fail($"no unit for player {player}");
            return default;
        }

        private static WorldPointMm PositionOf(GlobalFront.Server.MatchServer server, EntityId entity)
        {
            Assert.That(server.TryGetUnit(entity, out var snapshot), Is.True);
            return snapshot.Position;
        }
    }
}
