using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server.Transport;
using GlobalFront.Server.Replication;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Integration.Replication
{
    /// <summary>
    /// Step 2.6.4 streaming evidence over a loss-free virtual pipe: the tick
    /// loop, the replication emitter and the client receiver form one contour
    /// where the client mirror converges bit-exact with the authoritative
    /// world while acks keep the server's delta base current.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationStreamIntegrationTests
    {
        [Test]
        public void Stream_TwoClients_FortyTicks_ConvergeBitExact()
        {
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            // The baseline episode (SnapshotRequest → keyframe) takes a couple
            // of pump round trips before the delta stream starts.
            for (var tick = 0; tick < 4; tick++)
            {
                world.PumpTick();
            }

            var entityA = world.Clients[0].FirstEntityOf(new PlayerId(1));
            var entityB = world.Clients[1].FirstEntityOf(new PlayerId(2));

            // One long move per unit: positions change on every tick, so every
            // tick carries a non-empty establishing delta.
            world.Clients[0].SubmitMoveTo(
                new PlayerId(1), entityA, new WorldPointMm(500_000, 0),
                world.Rig.Host.CurrentTick + 1);
            world.Clients[1].SubmitMoveTo(
                new PlayerId(2), entityB, new WorldPointMm(-500_000, 0),
                world.Rig.Host.CurrentTick + 1);

            const int tickCount = 40;
            for (var tick = 0; tick < tickCount; tick++)
            {
                world.PumpTick();

                Assert.That(world.Clients[0].Receiver.State,
                    Is.EqualTo(ReplicationReceiverState.Streaming), $"client A at tick {tick}");
                Assert.That(world.Clients[1].Receiver.State,
                    Is.EqualTo(ReplicationReceiverState.Streaming), $"client B at tick {tick}");
            }

            foreach (var client in world.Clients)
            {
                Assert.That(
                    world.PumpUntilCaughtUp(client), Is.True,
                    "the client mirror must confirm the last authoritative tick");
                world.AssertWorldsMatch(client);

                Assert.That(client.Receiver.LastAppliedTick,
                    Is.EqualTo(world.Rig.Host.CurrentTick));
                Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
                Assert.That(client.Receiver.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
                Assert.That(client.Receiver.WorldPartiallyApplied, Is.False);

                // The loss-free stream never repairs: no catch-up, no rebase,
                // no forced keyframes after the baseline.
                Assert.That(client.Receiver.CatchUpCount, Is.EqualTo(0), "no loss must mean no repair");
                Assert.That(client.Receiver.RebaseCount, Is.EqualTo(0), "no loss must mean no rebase");
                Assert.That(client.Receiver.IsWorldUsable, Is.True);

                // The uplink flowed: the emitter observed acks, the bridge sent them.
                Assert.That(world.Emitter.AckCount, Is.GreaterThan(0), "server must have processed acks");
                Assert.That(client.Bridge.SentAckCount, Is.GreaterThan(0), "bridge must have sent acks");
                Assert.That(world.Emitter.EmittedDeltaCount, Is.GreaterThanOrEqualTo((long)tickCount),
                    "every tick must have produced a delta per client");
            }

            // Audit P1-2: deltas carry the deterministic state fingerprint at
            // the 1 Hz cadence — 40 streamed ticks must include at least two
            // checksummed deltas per client stream (ticks 20 and 40).
            Assert.That(world.Emitter.StateChecksumCount,
                Is.GreaterThanOrEqualTo(2 * world.Clients.Count),
                "the 1 Hz state checksum cadence must mark deltas with HasChecksum");
        }

        [Test]
        public void Stream_IdleWorld_150Ticks_NoRebaselinesAndStableStreaming()
        {
            // Audit P1-4: a quiet world must not stall the delta stream. The
            // emitter's header-only keep-alive deltas (one per 20 silent
            // ticks) keep the client's confirmed tick — and with it the shared
            // delta base — inside the 120-tick history window, so no
            // re-baseline ever fires, while the world stands still and after
            // content returns.
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            // The baseline episode installs one keyframe per client; nothing
            // moves afterwards.
            for (var tick = 0; tick < 6; tick++)
            {
                world.PumpTick();
            }

            foreach (var client in world.Clients)
            {
                Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            }

            var baselineKeyframes = world.Emitter.EmittedKeyframeCount;
            var baselineSlices = world.Emitter.EmittedSliceCount;

            // 150 idle ticks: no command, no movement. The stream stays
            // stable — Streaming throughout, no repair, and the confirmed
            // tick tracks the server within one keep-alive horizon.
            for (var tick = 0; tick < 150; tick++)
            {
                world.PumpTick();
                foreach (var client in world.Clients)
                {
                    Assert.That(client.Receiver.State,
                        Is.EqualTo(ReplicationReceiverState.Streaming), $"idle tick {tick}");
                    Assert.That(client.Receiver.RebaseCount, Is.Zero, $"idle tick {tick}");
                    Assert.That(client.Receiver.CatchUpCount, Is.Zero, $"idle tick {tick}");
                }
            }

            foreach (var client in world.Clients)
            {
                Assert.That(client.Receiver.LastAppliedTick,
                    Is.GreaterThanOrEqualTo(world.Rig.Host.CurrentTick -
                        (ServerReplicationEmitterConfig.DefaultIdleKeepAliveTicks - 1)),
                    "the keep-alive deltas must keep the confirmed tick near the server");
            }

            // Content returns: plain deltas must pick up from the keep-alive
            // base — no re-baseline, no catch-up, no extra keyframe.
            var entityA = world.Clients[0].FirstEntityOf(new PlayerId(1));
            var entityB = world.Clients[1].FirstEntityOf(new PlayerId(2));
            world.Clients[0].SubmitMoveTo(
                new PlayerId(1), entityA, new WorldPointMm(500_000, 0), world.Rig.Host.CurrentTick + 1);
            world.Clients[1].SubmitMoveTo(
                new PlayerId(2), entityB, new WorldPointMm(-500_000, 0), world.Rig.Host.CurrentTick + 1);

            foreach (var client in world.Clients)
            {
                Assert.That(
                    world.PumpUntilCaughtUp(client, budgetMs: 20000, keepTicking: true), Is.True,
                    "content after the idle period must converge over plain deltas");
                world.AssertWorldsMatch(client);

                Assert.That(client.Receiver.RebaseCount, Is.Zero,
                    "an idle world must never force a re-baseline");
                Assert.That(client.Receiver.CatchUpCount, Is.Zero,
                    "the keep-alive base must still cover the content delta");
                Assert.That(client.Receiver.StaleDropCount, Is.Zero,
                    "keep-alive deltas must never arrive stale");
                Assert.That(client.Receiver.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
                Assert.That(client.Receiver.IsWorldUsable, Is.True);
            }

            // Not a single extra keyframe was emitted: the eviction fallback
            // never fired during or after the quiet period.
            Assert.That(world.Emitter.EmittedKeyframeCount, Is.EqualTo(baselineKeyframes),
                "an idle world must not produce re-baseline keyframes");
            Assert.That(world.Emitter.EmittedSliceCount, Is.EqualTo(baselineSlices));
            Assert.That(world.Emitter.KeyframeFallbackCount, Is.Zero);
            Assert.That(world.Emitter.IdleKeepAliveDeltaCount,
                Is.GreaterThanOrEqualTo(2 * 5),
                "each client's stream must have carried keep-alive deltas");
        }

        [Test]
        public void Stream_AttachBeforeMatch_KeyframeThenSpawnDeltas_Converge()
        {
            // Clients attach while the world is still empty: the receiver opens
            // its baseline episode with a SnapshotRequest, the emitter answers
            // with an empty keyframe, and the spawned units later reach it as
            // ADD records of a delta.
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), ServerReplicationEmitterConfig.Default, clientCount: 1);
            for (var tick = 0; tick < 4; tick++)
            {
                world.PumpTick();
            }

            var client = world.Clients[0];
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming),
                "the empty keyframe must install the baseline");
            Assert.That(client.Bridge.AssembledKeyframeCount, Is.EqualTo(1));
            Assert.That(client.Receiver.BaseKeyframeTick, Is.GreaterThan(0UL),
                "the baseline must not sit at tick 0");

            world.StartMatch(new PlayerId(1));
            world.PumpTick();

            Assert.That(
                world.PumpUntilCaughtUp(client), Is.True,
                "the spawn must reach the client as deltas");
            world.AssertWorldsMatch(client);
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
        }

        [TestCase(ServerReplicationEmitterConfig.DefaultMaxSlicePayloadBytes, 2)]
        [TestCase(512, 10)]
        public void Stream_MultiSliceKeyframe_AssemblesAndConverges(int sliceBudgetBytes, int extraTicks)
        {
            // ~450 units make the baseline larger than any single slice at both
            // budgets: the default 16 KB budget still splits into two slices
            // and the 512 B budget into ~36, exercising part ordering, dedup
            // and completion accounting over the real transport.
            var config = new ServerReplicationEmitterConfig(
                ServerReplicationEmitterConfig.DefaultMaxClients,
                ServerReplicationEmitterConfig.DefaultSnapshotCapacity,
                ServerReplicationEmitterConfig.DefaultAddCapacity,
                ServerReplicationEmitterConfig.DefaultUpdateCapacity,
                ServerReplicationEmitterConfig.DefaultRemoveCapacity,
                ServerReplicationEmitterConfig.DefaultPacingRefillBytesPerTick,
                ServerReplicationEmitterConfig.DefaultPacingBurstCapBytes,
                sliceBudgetBytes,
                ServerReplicationEmitterConfig.DefaultPeriodicKeyframeTicks);
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), config, clientCount: 2);
            world.StartMatchWithUnits(450);

            // Two units keep moving so every follow-up tick carries a non-empty
            // delta and the confirmed tick can track the authoritative one.
            var moving1 = world.Clients[0].FirstEntityOf(new PlayerId(1));
            var moving2 = world.Clients[1].FirstEntityOf(new PlayerId(2));
            world.Clients[0].SubmitMoveTo(
                new PlayerId(1), moving1, new WorldPointMm(400_000, 0), world.Rig.Host.CurrentTick + 1);
            world.Clients[1].SubmitMoveTo(
                new PlayerId(2), moving2, new WorldPointMm(-400_000, 0), world.Rig.Host.CurrentTick + 1);

            // Keyframe streaming is paced over several ticks.
            for (var tick = 0; tick < extraTicks; tick++)
            {
                world.PumpTick();
            }

            var client = world.Clients[0];
            Assert.That(
                world.PumpUntilCaughtUp(client, budgetMs: 30000, keepTicking: true), Is.True,
                "the sliced keyframe and the follow-up deltas must converge");

            Assert.That(client.Receiver.BaseKeyframeTick, Is.GreaterThan(0UL));
            Assert.That(client.Bridge.AssembledKeyframeCount, Is.EqualTo(1));
            Assert.That(client.Bridge.AssembledSliceCount, Is.GreaterThanOrEqualTo(2),
                "the baseline must have arrived as several slices");
            Assert.That(client.Receiver.World.LiveCount, Is.EqualTo(450));
            Assert.That(world.Emitter.EmittedSliceCount, Is.GreaterThanOrEqualTo(2));
            world.AssertWorldsMatch(client);
        }
    }
}
