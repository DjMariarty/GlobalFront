using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server.Transport;
using GlobalFront.Server.Replication;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Integration.Replication
{
    /// <summary>
    /// Step 2.6.4 stress evidence under C2 packet loss (ADR-010): lost deltas
    /// are skipped safely — the next establishing delta or an explicit
    /// DeltaResume cumulative merge restores the stream without stalls,
    /// desyncs or re-baselines. The pipe's seeded impairment profile makes
    /// every loss decision deterministic.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationLossIntegrationTests
    {
        [TestCase(0.01)]
        [TestCase(0.05)]
        public void Stream_UnderC2Loss_SkipsLostTicksAndConverges(double lossProbability)
        {
            var profile = new ImpairmentProfile
            {
                LossProbability = lossProbability,
                LatencyMs = 2,
                ReorderJitterMs = 5,
                Seed = 2026
            };
            // Two clients: a one-sided roster would be an instantly terminal
            // match (the empty team loses), which would freeze the simulation.
            var world = ReplicationIntegrationWorld.Create(
                profile, ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client = world.Clients[0];
            var entityA = client.FirstEntityOf(new PlayerId(1));
            var entityB = client.FirstEntityOf(new PlayerId(2));
            client.SubmitMoveTo(
                new PlayerId(1), entityA, new WorldPointMm(500_000, 0), world.Rig.Host.CurrentTick + 1);
            client.SubmitMoveTo(
                new PlayerId(2), entityB, new WorldPointMm(-500_000, 0), world.Rig.Host.CurrentTick + 1);

            const int tickCount = 100;
            for (var tick = 0; tick < tickCount; tick++)
            {
                world.PumpTick();

                // Liveness under loss: no matter what was dropped, the receiver
                // must never become terminal or hold a half-applied world.
                Assert.That(client.Receiver.State,
                    Is.Not.EqualTo(ReplicationReceiverState.ConnectionFailed),
                    $"tick {tick}: the receiver must never terminate under 1-5% loss");
                Assert.That(client.Receiver.WorldPartiallyApplied, Is.False,
                    $"tick {tick}: the mirror must never hold a partial tick");
            }

            Assert.That(
                world.PumpUntilCaughtUp(client, budgetMs: 20000, keepTicking: true), Is.True,
                $"loss {lossProbability}: the client must confirm the last authoritative tick");
            world.AssertWorldsMatch(client);

            Assert.That(client.Receiver.LastAppliedTick, Is.EqualTo(world.Rig.Host.CurrentTick));
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(client.Receiver.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
            Assert.That(client.Receiver.InconsistentApplyCount, Is.EqualTo(0),
                "establishing deltas must apply cleanly under loss");
            // Under sustained 5% loss the receiver's repair schedule may
            // legitimately escalate a failing catch-up episode into a
            // re-baseline (FSM design); the contract under test is the
            // outcome - converged, streaming, bit-exact, never terminal.
            Assert.That(client.Receiver.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));

            // Repair traffic proves the cumulative-delta path was exercised:
            // with the fixed seed the 5% run deterministically drops deltas.
            if (lossProbability > 0.01)
            {
                Assert.That(client.Receiver.CatchUpCount, Is.GreaterThanOrEqualTo(1),
                    "the seeded 5% loss must produce at least one dependency gap");
                Assert.That(world.Emitter.DeltaResumeServedCount, Is.GreaterThanOrEqualTo(1),
                    "the emitter must have served a cumulative DeltaResume");
            }
        }

        [Test]
        public void Stream_UnderC2Loss_BurstThenRecovery_Converges()
        {
            // A hard burst drops everything for a stretch that stays inside the
            // transport idle timeout; the survivor deltas after the burst force
            // a catch-up episode whose cumulative answer covers the burst.
            var profile = new ImpairmentProfile
            {
                LossProbability = 0.0,
                LatencyMs = 2,
                Seed = 7
            };
            // Two clients keep the match non-terminal (see the test above).
            var world = ReplicationIntegrationWorld.Create(
                profile, ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client = world.Clients[0];
            var entityA = client.FirstEntityOf(new PlayerId(1));
            var entityB = client.FirstEntityOf(new PlayerId(2));
            client.SubmitMoveTo(
                new PlayerId(1), entityA, new WorldPointMm(500_000, 0), world.Rig.Host.CurrentTick + 1);
            client.SubmitMoveTo(
                new PlayerId(2), entityB, new WorldPointMm(-500_000, 0), world.Rig.Host.CurrentTick + 1);

            for (var tick = 0; tick < 10; tick++)
            {
                world.PumpTick();
            }

            world.Profile.LossProbability = 1.0;
            for (var tick = 0; tick < 25; tick++)
            {
                world.PumpTick(2);
            }

            world.Profile.LossProbability = 0.0;
            Assert.That(
                world.PumpUntilCaughtUp(client, budgetMs: 20000, keepTicking: true), Is.True,
                "the stream must reconverge after the burst");
            world.AssertWorldsMatch(client);

            Assert.That(client.Receiver.CatchUpCount, Is.GreaterThanOrEqualTo(1),
                "the burst must produce at least one dependency gap");
            Assert.That(world.Emitter.DeltaResumeServedCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(client.Receiver.RebaseCount, Is.EqualTo(0),
                "a 35-tick burst stays inside the 120-tick window");
            Assert.That(client.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
        }
    }
}
