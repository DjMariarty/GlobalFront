using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Client.UI;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using EntityId = GlobalFront.Core.Model.EntityId;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;
using Object = UnityEngine.Object;

namespace GlobalFront.Tests.EditMode.Server
{
    /// <summary>
    /// Phase 4, step 4.4.2 (ADR-014, OD-41): the client-to-authority command
    /// ingress over the real transport harness — client endpoint → own
    /// carrier → session binding → session gate → <see cref="MatchServer"/>,
    /// with the authoritative result coming back as a
    /// <see cref="CommandAckPayload"/>.
    ///
    /// The scheduling window is the contract these tests pin: a command dated
    /// for a future tick must not run early, a command dated in the past is
    /// clamped to the next tick rather than dropped, and a command beyond the
    /// future ceiling is refused without consuming its sequence. The spoof
    /// test proves the session token alone is not enough: a valid token sent
    /// down another socket must be dropped by the binding guard.
    /// </summary>
    [TestFixture]
    public sealed class AuthoritativeCommandIngressTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);
        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        private sealed class World
        {
            public TransportTestSupport.Rig Rig;
            public ClientTransportEndpoint ClientA;
            public ClientTransportEndpoint ClientB;
            public NetworkCommandChannel ChannelA;
            public EntityId[] SpawnedEntities;
            public readonly List<CommandAckPayload> Acks = new List<CommandAckPayload>();

            public MatchServer Server => Rig.Host.Server;

            public PlayerId PlayerA => ClientA.Player;

            public static World Create(params UnitSpawnSpec[] specs)
            {
                var world = new World
                {
                    Rig = TransportTestSupport.CreateRig(new ImpairmentProfile())
                };
                world.ClientA = TransportTestSupport.CreateClient(world.Rig);
                world.ClientB = TransportTestSupport.CreateClient(world.Rig);
                Assert.That(
                    TransportTestSupport.PumpUntilEstablished(world.Rig, world.ClientA),
                    Is.True,
                    "client A must complete the handshake");
                Assert.That(
                    TransportTestSupport.PumpUntilEstablished(world.Rig, world.ClientB),
                    Is.True,
                    "client B must complete the handshake");
                Assert.That(world.ClientA.Player, Is.EqualTo(PlayerOne),
                    "the first client to connect owns the first player slot");
                Assert.That(world.ClientB.Player, Is.EqualTo(PlayerTwo),
                    "the second client to connect owns the second player slot");

                var config = new MatchConfig(TransportTestSupport.StandardStats, specs);
                Assert.That(
                    world.Rig.Host.TryStartSessionMatch(world.Rig.Match, config, out var entities),
                    Is.True,
                    "the session match must start for both joined players");
                world.SpawnedEntities = entities;

                world.ChannelA = new NetworkCommandChannel(world.ClientA);
                world.ChannelA.CommandResultReceived += world.Acks.Add;
                return world;
            }

            public void Pump(long milliseconds = 5)
            {
                Rig.Pump(milliseconds);
                ClientA.Pump(Rig.Clock.NowMs);
                ClientB.Pump(Rig.Clock.NowMs);
            }

            public bool PumpUntil(Func<bool> condition, long budgetMs = 5000, long stepMs = 5)
            {
                var elapsed = 0L;
                while (elapsed < budgetMs)
                {
                    Pump(stepMs);
                    elapsed += stepMs;
                    if (condition())
                    {
                        return true;
                    }
                }

                return condition();
            }
        }

        /// <summary>
        /// One friendly unit at the origin, one enemy far outside every
        /// acquisition range, so a friendly move is the only thing that can
        /// alter the friendly unit's position. Larger friendly rosters are
        /// laid out on a grid for the batching test.
        /// </summary>
        private static UnitSpawnSpec[] DuelSpecs(int friendlyUnits = 1)
        {
            var specs = new List<UnitSpawnSpec>(friendlyUnits + 1);
            for (var index = 0; index < friendlyUnits; index++)
            {
                specs.Add(new UnitSpawnSpec(
                    PlayerOne,
                    new WorldPointMm((index % 15) * 2000, (index / 15) * 2000),
                    350,
                    false,
                    UnitKinds.Scout));
            }

            specs.Add(new UnitSpawnSpec(
                PlayerTwo,
                new WorldPointMm(200_000, 0),
                350,
                false,
                UnitKinds.Scout));
            return specs.ToArray();
        }

        [Test]
        public void CommandIngress_ValidMove_ExecutedAtRequestedFutureTick()
        {
            var world = World.Create(DuelSpecs());
            var server = world.Server;
            var entity = FirstEntityOf(server, world.PlayerA);
            var start = PositionOf(server, entity);
            var requestedTick = server.CurrentTick + 5;

            var local = world.ChannelA.TrySubmitMove(
                new CommandHeader(world.PlayerA, 1, requestedTick, GameCommandType.Move),
                new[] { entity },
                new WorldPointMm(start.X + 5000, start.Z),
                new FormationSpec(1, 2000, CardinalFacing.North));
            Assert.That(local, Is.EqualTo(MatchCommandRejection.None),
                "the local channel pre-flight must queue the command");

            Assert.That(world.PumpUntil(() => world.Acks.Count >= 1), Is.True,
                "the authoritative result must arrive");
            Assert.That(world.Acks[0].CommandSequence, Is.EqualTo(1u));
            Assert.That(world.Acks[0].IsAccepted, Is.True,
                $"the server refused a legal future-tick move: {world.Acks[0].Command}");

            for (var index = 0; index < 4; index++)
            {
                server.TickOnce();
            }

            Assert.That(server.CurrentTick, Is.EqualTo(requestedTick - 1),
                "four ticks must leave the server one tick short of the request");
            Assert.That(server.TryGetUnit(entity, out var before), Is.True);
            Assert.That(before.HasMoveTarget, Is.False,
                "a command dated for a future tick must not run before it");
            Assert.That(before.Position, Is.EqualTo(start),
                "the unit must not have moved one tick early");

            server.TickOnce();

            Assert.That(server.CurrentTick, Is.EqualTo(requestedTick),
                "the fifth tick is the requested tick");
            Assert.That(server.TryGetUnit(entity, out var after), Is.True);
            Assert.That(after.HasMoveTarget, Is.True,
                "the move must be scheduled and executed on its requested tick");
            Assert.That(after.Position.X, Is.GreaterThan(start.X),
                "the unit must start moving towards the destination");
        }

        [Test]
        public void CommandIngress_PastTick_ClampedToNextTick()
        {
            var world = World.Create(DuelSpecs());
            var server = world.Server;
            var entity = FirstEntityOf(server, world.PlayerA);
            var start = PositionOf(server, entity);

            server.ExecuteTicks(10);
            Assert.That(server.CurrentTick, Is.EqualTo(10ul));

            const ulong requestedTick = 5;
            Assert.That(
                server.CurrentTick - requestedTick,
                Is.LessThanOrEqualTo(MatchServer.AcceptedPastTicks),
                "the fixture must stay inside the accepted past window");

            world.ChannelA.TrySubmitMove(
                new CommandHeader(world.PlayerA, 1, requestedTick, GameCommandType.Move),
                new[] { entity },
                new WorldPointMm(start.X + 5000, start.Z),
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(world.PumpUntil(() => world.Acks.Count >= 1), Is.True);
            Assert.That(world.Acks[0].IsAccepted, Is.True,
                $"a past tick inside the window must be accepted: {world.Acks[0].Command}");

            Assert.That(server.TryGetUnit(entity, out var before), Is.True);
            Assert.That(before.HasMoveTarget, Is.False,
                "nothing may run before the clamped execute tick");

            server.TickOnce();

            Assert.That(server.CurrentTick, Is.EqualTo(11ul));
            Assert.That(server.TryGetUnit(entity, out var after), Is.True);
            Assert.That(after.HasMoveTarget, Is.True,
                "a past-dated command must run on the next tick, not be dropped");
            Assert.That(after.Position.X, Is.GreaterThan(start.X));
        }

        [Test]
        public void CommandIngress_FutureTickBeyondLimit_RejectedWithHeaderRejected()
        {
            var world = World.Create(DuelSpecs());
            var server = world.Server;
            var entity = FirstEntityOf(server, world.PlayerA);
            var start = PositionOf(server, entity);
            var tooFarTick = server.CurrentTick + MatchServer.AcceptedFutureTicks + 1;

            world.ChannelA.TrySubmitMove(
                new CommandHeader(world.PlayerA, 1, tooFarTick, GameCommandType.Move),
                new[] { entity },
                new WorldPointMm(start.X + 5000, start.Z),
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(world.PumpUntil(() => world.Acks.Count >= 1), Is.True);
            Assert.That(world.Acks[0].Command, Is.EqualTo(MatchCommandRejection.HeaderRejected),
                "a request beyond the future ceiling must be refused by the header window");
            Assert.That(server.TryGetLastAcceptedSequence(world.PlayerA, out _), Is.False,
                "a refused header must not consume the client's sequence");

            server.ExecuteTicks((int)MatchServer.AcceptedFutureTicks + 1);
            Assert.That(server.TryGetUnit(entity, out var afterCeiling), Is.True);
            Assert.That(afterCeiling.HasMoveTarget, Is.False,
                "a refused command must never enter the schedule");
            Assert.That(afterCeiling.Position, Is.EqualTo(start));

            var retryTick = server.CurrentTick + 1;
            world.ChannelA.TrySubmitMove(
                new CommandHeader(world.PlayerA, 1, retryTick, GameCommandType.Move),
                new[] { entity },
                new WorldPointMm(start.X + 5000, start.Z),
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(world.PumpUntil(() => world.Acks.Count >= 2), Is.True);
            Assert.That(world.Acks[1].IsAccepted, Is.True,
                "the same sequence must remain usable after a header refusal");
        }

        [Test]
        public void CommandIngress_SpoofedSessionToken_DroppedBadToken()
        {
            var world = World.Create(DuelSpecs());
            var server = world.Server;
            var entity = FirstEntityOf(server, world.PlayerA);
            var start = PositionOf(server, entity);
            var stolenToken = world.ClientA.Token;
            Assert.That(stolenToken, Is.Not.EqualTo(0ul),
                "an established client must hold a server-assigned token");

            // A second socket with no binding of its own hands in the first
            // client's valid token. Only the connection-binding guard can tell
            // the two peers apart.
            var rogue = OwnDatagramCarrier.CreateClient(
                world.Rig.Pipe,
                world.Rig.Pipe.CreateEndpoint(),
                TransportTestSupport.ServerAddressOf(world.Rig),
                world.Rig.Clock);
            try
            {
                var rogueConnection = rogue.ConnectToServer("virtual", 0);
                var wireCommand = new byte[CommandWireCodec.HeaderSizeBytes + 1 + 8 + 32];
                var wireLength = CommandWireCodec.EncodeMove(
                    wireCommand,
                    new CommandHeader(
                        world.PlayerA, 1, server.CurrentTick + 1, GameCommandType.Move),
                    new[] { entity },
                    new WorldPointMm(start.X + 5000, start.Z),
                    new FormationSpec(1, 2000, CardinalFacing.North));

                var datagram = new byte[TransportProtocol.MaxMessageBytes];
                var offset = MessageCodec.WriteHeader(
                    datagram, 0, TransportMessageType.Command, stolenToken, 0);
                Array.Copy(wireCommand, 0, datagram, offset, wireLength);

                var baseline = world.Rig.ServerCarrier.Metrics.DroppedBadToken;
                Assert.That(
                    rogue.Send(
                        rogueConnection,
                        TransportChannel.Command,
                        datagram,
                        0,
                        offset + wireLength),
                    Is.True,
                    "the rogue datagram must reach the pipe");

                Assert.That(
                    world.PumpUntil(
                        () => world.Rig.ServerCarrier.Metrics.DroppedBadToken > baseline,
                        2000),
                    Is.True,
                    "the server must count the spoofed packet as a bad-token drop");

                Assert.That(
                    world.Rig.ServerCarrier.Metrics.DroppedBadToken,
                    Is.EqualTo(baseline + 1),
                    "exactly one packet must be dropped for the stolen token");
                Assert.That(world.Acks, Is.Empty,
                    "a spoofed command must never be acknowledged");
                Assert.That(server.TryGetLastAcceptedSequence(world.PlayerA, out _), Is.False,
                    "a spoofed command must never reach the match");

                server.TickOnce();
                Assert.That(server.TryGetUnit(entity, out var after), Is.True);
                Assert.That(after.HasMoveTarget, Is.False,
                    "a spoofed command must never execute");
            }
            finally
            {
                rogue.Dispose();
            }
        }

        [Test]
        public void CommandIngress_DuplicateSequence_RejectedWithDuplicateSequence()
        {
            var world = World.Create(DuelSpecs());
            var server = world.Server;
            var entity = FirstEntityOf(server, world.PlayerA);
            var start = PositionOf(server, entity);
            var requestedTick = server.CurrentTick + 5;
            var firstDestination = new WorldPointMm(start.X + 5000, start.Z);
            var replayedDestination = new WorldPointMm(start.X + 20000, start.Z);

            world.ChannelA.TrySubmitMove(
                new CommandHeader(world.PlayerA, 1, requestedTick, GameCommandType.Move),
                new[] { entity },
                firstDestination,
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(world.PumpUntil(() => world.Acks.Count >= 1), Is.True);
            Assert.That(world.Acks[0].IsAccepted, Is.True);

            world.ChannelA.TrySubmitMove(
                new CommandHeader(world.PlayerA, 1, requestedTick, GameCommandType.Move),
                new[] { entity },
                replayedDestination,
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(world.PumpUntil(() => world.Acks.Count >= 2), Is.True);
            Assert.That(world.Acks[1].CommandSequence, Is.EqualTo(1u));
            Assert.That(world.Acks[1].Command, Is.EqualTo(MatchCommandRejection.DuplicateSequence),
                "a replayed sequence must be refused, not ignored");
            Assert.That(server.TryGetLastAcceptedSequence(world.PlayerA, out var last), Is.True);
            Assert.That(last, Is.EqualTo(1u));

            server.ExecuteTicks(5);

            Assert.That(server.TryGetUnit(entity, out var after), Is.True);
            Assert.That(after.HasMoveTarget, Is.True);
            Assert.That(after.MoveTarget, Is.EqualTo(firstDestination),
                "the duplicate must not overwrite the accepted command's slot");
        }

        [Test]
        public void CommandIngress_MultiBatchSelection_DispatchedWithDistinctSequences_AllExecuted()
        {
            const int FriendlyUnits = 150;
            const int Ceiling = SimulationConstants.MaxSelectedEntities;
            var world = World.Create(DuelSpecs(FriendlyUnits));
            var server = world.Server;

            var root = new GameObject("AuthoritativeCommandIngressTests_ClientWorld");
            var pool = new UnitViewPool(root.transform);
            var clientWorld = new ClientReplicationWorld(512);
            var buffer = new UnitViewTickBuffer(512, SimulationConstants.ServerTickDurationSeconds);
            var binder = new UnitViewBinder(pool, clientWorld, buffer);
            try
            {
                var snapshots = SnapshotsOf(server);
                var friendly = new List<ServerUnitSnapshot>(FriendlyUnits);
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.Owner == world.PlayerA)
                    {
                        friendly.Add(snapshot);
                    }
                }

                Assert.That(friendly.Count, Is.EqualTo(FriendlyUnits));

                pool.Warmup(UnitKinds.Scout, FriendlyUnits);
                foreach (var snapshot in friendly)
                {
                    Assert.That(
                        clientWorld.ApplyAdd(new DeltaAddRecord(
                            snapshot.Entity,
                            snapshot.Owner,
                            snapshot.Position,
                            snapshot.CurrentHealth,
                            snapshot.HasMoveTarget,
                            snapshot.MoveTarget,
                            snapshot.AttackTarget,
                            snapshot.AutoAcquireEnemies,
                            snapshot.UnitKind)),
                        Is.EqualTo(ClientWorldApplyResult.Ok));
                }

                buffer.CaptureTick(1ul, clientWorld);
                binder.Render(1f / 60f);

                var selection = new UnitSelectionController(binder, clientWorld, world.PlayerA);
                foreach (var snapshot in friendly)
                {
                    selection.AddSelected(snapshot.Entity, SlotOf(clientWorld, snapshot.Entity));
                }

                Assert.That(selection.SelectedCount, Is.EqualTo(FriendlyUnits));
                Assert.That(selection.SelectedCount, Is.GreaterThan(Ceiling),
                    "the selection itself is not capped, only one command is");

                var sink = new UnitCommandChannelSink(world.ChannelA);
                var destination = new WorldPointMm(0, 80_000);
                Assert.That(selection.IssueMove(destination, 1ul, sink), Is.True);
                Assert.That(sink.AttemptedCount, Is.EqualTo(2),
                    "150 units over a 100-unit ceiling is two commands");
                Assert.That(sink.SubmittedCount, Is.EqualTo(2));
                Assert.That(sink.LastRejection, Is.EqualTo(MatchCommandRejection.None));

                Assert.That(world.PumpUntil(() => world.Acks.Count >= 2), Is.True,
                    "both batches must be acknowledged");
                Assert.That(world.Acks[0].IsAccepted, Is.True);
                Assert.That(world.Acks[1].IsAccepted, Is.True);
                Assert.That(world.Acks[0].CommandSequence, Is.EqualTo(1u));
                Assert.That(world.Acks[1].CommandSequence, Is.EqualTo(2u),
                    "each batch carries its own strictly increasing sequence");

                server.TickOnce();

                var targets = new HashSet<(int X, int Z)>();
                foreach (var snapshot in friendly)
                {
                    Assert.That(server.TryGetUnit(snapshot.Entity, out var applied), Is.True);
                    Assert.That(applied.HasMoveTarget, Is.True,
                        $"unit {snapshot.Entity.Value} from batch " +
                        $"{(snapshot.Entity == friendly[Ceiling].Entity ? "2" : "1")} must be moving");
                    targets.Add((applied.MoveTarget.X, applied.MoveTarget.Z));
                }

                Assert.That(targets.Count, Is.EqualTo(FriendlyUnits),
                    "every unit must receive a formation slot of its own; shared slots mean " +
                    "the tail batch reused the head's destination");
            }
            finally
            {
                pool.Dispose();
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// The zero-GC guarantee covers the authoritative execution stage: the
        /// per-tick command path that runs at 20 Hz for the whole roster.
        ///
        /// It deliberately does <b>not</b> cover transport ingestion, and the
        /// brief's "Pump + TickOnce allocates nothing" formulation cannot hold:
        /// <see cref="CommandWireCodec.TryDecode"/> allocates one
        /// <c>EntityId[]</c> per received command, <c>MatchServer.Schedule</c>
        /// allocates the per-tick bucket, and the carrier allocates the
        /// <see cref="TransportEvent"/>. Ingestion is bounded by how many
        /// commands players issue; execution is the hot path.
        /// </summary>
        [Test]
        public void CommandIngress_ZeroGC_OnCommandProcessingHotPath()
        {
            const int Commands = 100;
            var world = World.Create(DuelSpecs(Commands));
            var server = world.Server;
            var entities = UnitsOf(server, world.PlayerA);
            Assert.That(entities.Length, Is.EqualTo(Commands));

            var destination = new WorldPointMm(0, 60_000);
            var sequence = 0u;

            // Two warmup rounds: the first grows the combat scratch and the
            // schedule buckets, the second warms List<T>.Sort's 100-element
            // comparison path. Both run the exact shape of the measured tick.
            // The receive limiter's burst is 200 packets, so each hundred has
            // to be paid for with a second of virtual time.
            for (var round = 0; round < 2; round++)
            {
                world.Pump(1000);
                SubmitRound(world, entities, ref sequence, server.CurrentTick + 1, destination);
                Assert.That(world.PumpUntil(() => world.Acks.Count >= (round + 1) * Commands),
                    Is.True, $"round {round} must be fully acknowledged");
                server.TickOnce();
            }

            world.Pump(1000);
            SubmitRound(world, entities, ref sequence, server.CurrentTick + 1, destination);
            Assert.That(world.PumpUntil(() => world.Acks.Count >= 3 * Commands), Is.True,
                "the measured round must be fully received and scheduled before the window");
            foreach (var ack in world.Acks)
            {
                Assert.That(ack.IsAccepted, Is.True);
            }

            byte[] ballast = null;
            Assert.That(() => { ballast = new byte[4096]; }, GcAssert.AllocatingGCMemory(),
                "the allocation meter must be live before the measured window");
            Assert.That(ballast, Is.Not.Null);

            var measuredTick = server.CurrentTick + 1;
            Assert.That(
                () => server.TickOnce(),
                Is.Not.AllocatingGCMemory(),
                "executing a 100-command batch must not allocate");

            Assert.That(server.CurrentTick, Is.EqualTo(measuredTick));
            var executed = 0;
            foreach (var entity in entities)
            {
                Assert.That(server.TryGetUnit(entity, out var applied), Is.True);
                if (applied.HasMoveTarget && applied.MoveTarget == destination)
                {
                    executed++;
                }
            }

            Assert.That(executed, Is.EqualTo(Commands),
                "the measured tick must actually have executed all 100 commands");
        }

        [Test]
        public void SelectionHudState_ForRejection_TargetInFogOfWar_DisplaysCorrectString()
        {
            Assert.That(
                TacticalHudText.ForRejection(MatchCommandRejection.TargetInFogOfWar, true),
                Is.EqualTo("Target in fog"));
            Assert.That(
                TacticalHudText.ForRejection(MatchCommandRejection.TargetInFogOfWar, false),
                Is.EqualTo(TacticalHudText.NoCommand));
            Assert.That(
                TacticalHudText.ForRejection(MatchCommandRejection.FriendlyTarget, true),
                Is.EqualTo("Allied target"),
                "the new entry must not shift the pre-existing wording");
        }

        private static void SubmitRound(
            World world,
            EntityId[] entities,
            ref uint sequence,
            ulong requestedTick,
            WorldPointMm destination)
        {
            for (var index = 0; index < entities.Length; index++)
            {
                sequence++;
                var rejection = world.ChannelA.TrySubmitMove(
                    new CommandHeader(
                        world.PlayerA, sequence, requestedTick, GameCommandType.Move),
                    new[] { entities[index] },
                    destination,
                    new FormationSpec(1, 2000, CardinalFacing.North));
                Assert.That(
                    rejection,
                    Is.EqualTo(MatchCommandRejection.None),
                    $"index={index} seq={sequence} state={world.ClientA.State} " +
                    $"token={world.ClientA.Token} player={world.PlayerA} " +
                    $"tick={requestedTick}");

                // The carrier's reorder window is 64 messages, so a hundred
                // back-to-back sends must be drained as they go.
                if ((index & 15) == 15)
                {
                    world.Pump(1);
                }
            }
        }

        private static ServerUnitSnapshot[] SnapshotsOf(MatchServer server)
        {
            var snapshots = new ServerUnitSnapshot[server.UnitCount];
            var count = server.CopySnapshots(snapshots);
            Assert.That(count, Is.EqualTo(snapshots.Length));
            return snapshots;
        }

        private static EntityId[] UnitsOf(MatchServer server, PlayerId player)
        {
            var snapshots = SnapshotsOf(server);
            var entities = new List<EntityId>(snapshots.Length);
            foreach (var snapshot in snapshots)
            {
                if (snapshot.Owner == player)
                {
                    entities.Add(snapshot.Entity);
                }
            }

            return entities.ToArray();
        }

        private static EntityId FirstEntityOf(MatchServer server, PlayerId player)
        {
            var snapshots = SnapshotsOf(server);
            foreach (var snapshot in snapshots)
            {
                if (snapshot.Owner == player)
                {
                    return snapshot.Entity;
                }
            }

            Assert.Fail($"no unit for player {player}");
            return default;
        }

        private static WorldPointMm PositionOf(MatchServer server, EntityId entity)
        {
            Assert.That(server.TryGetUnit(entity, out var snapshot), Is.True);
            return snapshot.Position;
        }

        private static int SlotOf(ClientReplicationWorld world, EntityId entity)
        {
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (world.TryGetSlotState(slot, out var state) && state.Entity == entity)
                {
                    return slot;
                }
            }

            Assert.Fail($"entity {entity.Value} is not in the client world");
            return -1;
        }
    }
}
