using System;
using System.Collections.Generic;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode;
using NUnit.Framework;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Integration.Replication
{
    /// <summary>
    /// Step 4.6.3 (OD-40 / OD-43): 400 combat units keep bit-exact parity under
    /// the Wi-Fi (1%) and WAN (5%) presets, and a long outage that evicts the
    /// delta history escalates to a keyframe rebase and restores the world.
    ///
    /// These rigs attach no visibility filter, so <c>AssertWorldsMatch</c> is a
    /// full-roster comparison; the dedicated runner's FoW scope is a different
    /// configuration and is not exercised here.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationParityUnderImpairmentTests
    {
        private const int UnitCount = 400;
        private const int ArmySize = UnitCount / 2;
        private const int CombatTicks = 120;

        [Test]
        public void Combat400_UnderWifiImpairment_PreservesChecksumParity()
        {
            var world = CreateCombatWorld(ImpairmentProfile.Wifi(seed: 101));
            var client = world.Clients[0];

            PumpCombatTicks(world, client, CombatTicks, "Wi-Fi");

            Assert.That(world.Rig.Pipe.DroppedCount, Is.GreaterThan(0),
                "the Wi-Fi preset must actually drop traffic in this window");
            // The server keeps ticking while the client drains, so the brief's
            // keepTicking:true would never let an impaired client reach the
            // current tick; the final tick is fixed while the repair drains.
            Assert.That(world.PumpUntilCaughtUp(client, budgetMs: 20000, keepTicking: false), Is.True,
                "the Wi-Fi client must converge to the final authoritative tick");

            Assert.That(client.Receiver.ChecksumCheckCount, Is.GreaterThan(0),
                "the parity claim needs at least one checksum-bearing delta applied");
            Assert.That(client.Receiver.ChecksumMismatchCount, Is.EqualTo(0),
                "OD-40: the client fingerprint must match every authoritative checksum");
            Assert.That(client.Receiver.InconsistentApplyCount, Is.EqualTo(0),
                "1% loss must never leave a half-applied tick");
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            world.AssertWorldsMatch(client);
        }

        [Test]
        public void Combat400_UnderWanImpairment_RecoversViaDeltaResume_AndPreservesChecksum()
        {
            var world = CreateCombatWorld(ImpairmentProfile.Wan(seed: 202));
            var client = world.Clients[0];

            PumpCombatTicks(world, client, CombatTicks, "WAN");

            Assert.That(world.Rig.Pipe.DroppedCount, Is.GreaterThan(0),
                "the WAN preset must actually drop traffic in this window");
            Assert.That(world.PumpUntilCaughtUp(client, budgetMs: 30000, keepTicking: false), Is.True,
                "the WAN client must converge after the run");

            Assert.That(client.Receiver.ChecksumCheckCount, Is.GreaterThan(0));
            Assert.That(client.Receiver.ChecksumMismatchCount, Is.EqualTo(0),
                "OD-40: 5% loss must not produce a checksum divergence");
            Assert.That(client.Receiver.CatchUpCount, Is.GreaterThanOrEqualTo(1),
                "5% loss must produce at least one dependency gap");
            Assert.That(world.Emitter.DeltaResumeServedCount, Is.GreaterThanOrEqualTo(1),
                "the emitter must serve a cumulative DeltaResume");
            Assert.That(client.Receiver.LastAppliedTick, Is.EqualTo(world.Rig.Host.CurrentTick));
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            world.AssertWorldsMatch(client);
        }

        [Test]
        public void Combat400_UnderBurstOutage_EscalatesToKeyframeRebase_AndRestoresBitwiseParity()
        {
            var world = CreateCombatWorld(ImpairmentProfile.Wan(seed: 303));
            var client = world.Clients[0];

            // Let the client settle before the outage so its last applied tick
            // is known and will fall outside the retained delta window later.
            PumpCombatTicks(world, client, 10, "pre-outage");
            Assert.That(world.PumpUntilCaughtUp(client, budgetMs: 15000, keepTicking: false), Is.True,
                "the client must be caught up before the outage");
            var keyframesBefore = client.Receiver.KeyframeCount;

            // 150 ticks exceed the 120-tick delta history, so the client's
            // resume base is evicted and only a keyframe can restore it.
            world.Rig.Pipe.SetPartitioned(true);
            for (var tick = 0; tick < 150; tick++)
            {
                world.PumpTick();
            }

            Assert.That(world.Rig.Pipe.DroppedCount, Is.GreaterThan(0),
                "the partitioned window must have dropped traffic");
            world.Rig.Pipe.SetPartitioned(false);
            world.Profile.LossProbability = 0;
            world.Profile.LatencyMs = 0;
            world.Profile.ReorderJitterMs = 0;

            // A paused server sends no deltas, so the client would never learn
            // it is behind. Let one post-outage delta reveal the gap first:
            // its base is already past the client's tick, which is exactly the
            // dependency gap the FSM turns into a SnapshotRequest.
            var requestsBefore = client.Receiver.TotalRequestCount;
            var gapDetected = false;
            for (var tick = 0; tick < 30 && !gapDetected; tick++)
            {
                world.PumpTick();
                gapDetected = client.Receiver.TotalRequestCount > requestsBefore ||
                              client.Receiver.State != ReplicationReceiverState.Streaming;
            }

            Assert.That(gapDetected, Is.True,
                "the first post-outage delta must reveal the missing range");

            // Freeze the authoritative tick for the recovery: a live server
            // keeps advancing, so "caught up to the current tick" would chase
            // a moving target. The request is now in flight; while paused,
            // PumpPausedSlices refills the pacing bucket and pumps the 16 KB
            // keyframe (a drained emitter cannot send it in one tick).
            world.Rig.Host.Pause();
            Assert.That(world.PumpUntilCaughtUp(client, budgetMs: 20000, keepTicking: false), Is.True,
                "the rebased client must catch up to the paused authoritative tick");
            world.Rig.Host.Resume();
            Assert.That(client.Receiver.KeyframeCount, Is.GreaterThan(keyframesBefore),
                "the evicted delta window must force a keyframe rebase");
            Assert.That(client.Receiver.ChecksumMismatchCount, Is.EqualTo(0));
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(client.Receiver.LastAppliedTick, Is.EqualTo(world.Rig.Host.CurrentTick));
            world.AssertWorldsMatch(client);
        }

        private static void PumpCombatTicks(
            ReplicationIntegrationWorld world,
            ReplicationIntegrationWorld.ClientLeg client,
            int tickCount,
            string label)
        {
            for (var tick = 0; tick < tickCount; tick++)
            {
                world.PumpTick();
                Assert.That(client.Receiver.State,
                    Is.Not.EqualTo(ReplicationReceiverState.ConnectionFailed),
                    $"{label} tick {tick}: the receiver must never terminate under impairment");
                Assert.That(client.Receiver.WorldPartiallyApplied, Is.False,
                    $"{label} tick {tick}: the mirror must never hold a partial tick");
            }
        }

        private static ReplicationIntegrationWorld CreateCombatWorld(ImpairmentProfile profile)
        {
            var world = ReplicationIntegrationWorld.Create(
                profile, ServerReplicationEmitterConfig.Default, clientCount: 2);
            Assert.That(
                world.Rig.Host.TryStartSessionMatch(
                    world.Rig.Match,
                    new MatchConfig(TransportTestSupport.StandardStats, BuildCombatSpecs()),
                    out _),
                Is.True,
                "the 400-unit combat match must start");
            IssueArmyMoves(world);
            return world;
        }

        private static UnitSpawnSpec[] BuildCombatSpecs()
        {
            var specs = new UnitSpawnSpec[UnitCount];
            for (var index = 0; index < UnitCount; index++)
            {
                var friendly = index < ArmySize;
                var ordinal = friendly ? index : index - ArmySize;
                var column = ordinal % 20;
                var row = ordinal / 20;
                var x = (friendly ? -50_000 : 50_000) + (column - 10) * 3_000;
                var z = (row - 5) * 3_000;
                specs[index] = new UnitSpawnSpec(
                    new PlayerId((byte)(friendly ? 1 : 2)),
                    new WorldPointMm(x, z),
                    350,
                    autoAcquireEnemies: true);
            }

            return specs;
        }

        private static void IssueArmyMoves(ReplicationIntegrationWorld world)
        {
            var player1 = EntitiesOf(world, new PlayerId(1));
            var player2 = EntitiesOf(world, new PlayerId(2));
            Assert.That(player1.Length, Is.EqualTo(ArmySize));
            Assert.That(player2.Length, Is.EqualTo(ArmySize));

            for (var offset = 0; offset < ArmySize; offset += SimulationConstants.MaxSelectedEntities)
            {
                SubmitBatch(world, world.Clients[0], new PlayerId(1), player1, offset,
                    new WorldPointMm(20_000, 0), CardinalFacing.East);
                SubmitBatch(world, world.Clients[1], new PlayerId(2), player2, offset,
                    new WorldPointMm(-20_000, 0), CardinalFacing.West);
            }
        }

        private static void SubmitBatch(
            ReplicationIntegrationWorld world,
            ReplicationIntegrationWorld.ClientLeg client,
            PlayerId player,
            EntityId[] entities,
            int offset,
            WorldPointMm destination,
            CardinalFacing facing)
        {
            var count = Math.Min(SimulationConstants.MaxSelectedEntities, entities.Length - offset);
            Assert.That(count, Is.GreaterThan(0));
            var batch = new EntityId[count];
            Array.Copy(entities, offset, batch, 0, count);
            var header = new CommandHeader(
                player,
                client.NextCommandSequence++,
                world.Rig.Host.CurrentTick + 1,
                GameCommandType.Move);
            Assert.That(
                client.Commands.TrySubmitMove(
                    header, batch, destination, new FormationSpec(0, 2000, facing)),
                Is.EqualTo(MatchCommandRejection.None),
                $"the {count}-unit army move must pass the local pre-flight");
        }

        private static EntityId[] EntitiesOf(ReplicationIntegrationWorld world, PlayerId player)
        {
            var snapshots = new ServerUnitSnapshot[world.Rig.Host.UnitCount];
            var count = world.Rig.Host.CopySnapshots(snapshots);
            var entities = new List<EntityId>(count);
            for (var index = 0; index < count; index++)
            {
                if (snapshots[index].Owner == player)
                {
                    entities.Add(snapshots[index].Entity);
                }
            }

            return entities.ToArray();
        }
    }
}
