using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Model;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// OD-36: the host core is engine-neutral and lives in
    /// <c>GlobalFront.Server</c>. These tests drive it directly, without a
    /// <see cref="LocalMatchHost"/> and without any Unity API.
    /// </summary>
    [TestFixture]
    public sealed class ServerHostTests
    {
        [Test]
        public void ServerHost_LivesInEngineNeutralServerAssembly()
        {
            var assembly = typeof(ServerHost).Assembly;
            Assert.That(assembly.GetName().Name, Is.EqualTo("GlobalFront.Server"));

            var references = assembly.GetReferencedAssemblies();
            for (var index = 0; index < references.Length; index++)
            {
                var name = references[index].Name;
                Assert.That(
                    name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    name.StartsWith("UnityEditor", StringComparison.Ordinal),
                    Is.False,
                    $"GlobalFront.Server must stay engine-free, but references {name}");
                Assert.That(
                    name,
                    Is.Not.EqualTo("GlobalFront.Client"),
                    "the host core must not depend on the client assembly");
            }

            Assert.That(
                typeof(ServerHost).GetMethod("BindReconnectCoordinator"),
                Is.Null,
                "the client-only reconnect binding must stay on LocalMatchHost");
        }

        [Test]
        public void LocalMatchHost_IsAThinSubclassOfServerHost()
        {
            Assert.That(typeof(LocalMatchHost).BaseType, Is.EqualTo(typeof(ServerHost)));
            Assert.That(
                typeof(LocalMatchHost).GetMethod("BindReconnectCoordinator"),
                Is.Not.Null,
                "the client wrapper must keep its reconnect binding");
        }

        [Test]
        public void Constructor_WithTickRate_PreservesTheRate()
        {
            var host = new ServerHost(60);

            Assert.That(host.TickDriver.TickDurationSeconds, Is.EqualTo(1.0 / 60.0).Within(1e-12));
            Assert.That(host.TickDriverMode, Is.EqualTo(TickDriverMode.Manual));
            Assert.That(host.CurrentTick, Is.Zero);
        }

        [Test]
        public void TickOnce_RunsTheTwoPhaseLifecycle()
        {
            var host = new ServerHost();
            var lifecycle = new List<string>();
            host.TickStarting += tick => lifecycle.Add($"starting:{tick}");
            host.TickCompleted += tick => lifecycle.Add($"completed:{tick}");

            Assert.That(host.TickOnce(), Is.True);

            Assert.That(host.CurrentTick, Is.EqualTo(1UL));
            Assert.That(lifecycle, Is.EqualTo(new[] { "starting:1", "completed:1" }));
        }

        [Test]
        public void StartRealTime_AdvancesWithBoundedCatchUp()
        {
            var host = new ServerHost();
            host.StartRealTime();

            Assert.That(host.AdvanceRealTime(0.25), Is.EqualTo(4));
            Assert.That(host.CurrentTick, Is.EqualTo(4UL));

            Assert.That(host.AdvanceRealTime(0.0), Is.EqualTo(1));
            Assert.That(host.CurrentTick, Is.EqualTo(5UL));
        }

        [Test]
        public void Pause_HaltsTicks_AndResumeContinues()
        {
            var host = new ServerHost();
            host.StartRealTime();

            host.Pause();
            Assert.That(host.IsPaused, Is.True);
            Assert.That(host.AdvanceRealTime(0.2), Is.Zero);
            Assert.That(host.CurrentTick, Is.Zero);

            host.Resume();
            Assert.That(host.IsPaused, Is.False);
            Assert.That(host.AdvanceRealTime(0.2), Is.EqualTo(4));
            Assert.That(host.CurrentTick, Is.EqualTo(4UL));
        }

        [Test]
        public void Pause_DrainsLargeElapsedTimeWithinItsBoundedBudget()
        {
            var host = new ServerHost();
            host.Pause();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.That(host.AdvanceRealTime(1e7), Is.Zero);
            watch.Stop();

            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(500));
            Assert.That(host.CurrentTick, Is.Zero);
        }

        [Test]
        public void AttachReplication_TracksAttachedClientsAndRecordsTicks()
        {
            var host = new ServerHost();
            var transport = new RecordingReplicationTransport();
            var emitter = host.AttachReplication(
                transport,
                ServerReplicationEmitterConfig.Default);

            var match = host.CreateSessionMatch(2);
            var session = host.CreateSession(host.CreateConnectionHandle());
            Assert.That(
                host.TryJoinMatch(session, match, out var player),
                Is.EqualTo(JoinResult.Assigned));

            transport.RaiseSessionAttached(session, match, player);
            Assert.That(emitter.TrackedClientCount, Is.EqualTo(1));

            // The first completed tick only captures the baseline; the emitter
            // can record a diff from the second tick onward.
            Assert.That(host.TickOnce(), Is.True);
            Assert.That(host.TickOnce(), Is.True);
            Assert.That(emitter.History.NewestTick, Is.EqualTo(2UL));
            Assert.That(emitter.History.Count, Is.EqualTo(1));
            Assert.That(ReferenceEquals(host.Replication, emitter), Is.True);
        }

        [Test]
        public void AttachReplication_Twice_IsRejected()
        {
            var host = new ServerHost();
            host.AttachReplication(new RecordingReplicationTransport());

            Assert.Throws<InvalidOperationException>(
                () => host.AttachReplication(new RecordingReplicationTransport()));
        }

        private sealed class RecordingReplicationTransport : IReplicationTransport
        {
            public int SendCount { get; private set; }

            public bool SendToSession(SessionId session, ulong envelopeTick, byte[] buffer, int length)
            {
                SendCount++;
                return true;
            }

            public event Action<SessionId, MatchId, PlayerId> SessionAttached;

            // The emitter only consumes attachments; the remaining seam
            // events must exist to satisfy the interface but are never raised
            // by this fake.
            public event Action<SessionId, MatchId, PlayerId> SessionReattached
            {
                add { }
                remove { }
            }

            public event Action<SessionId> PostSessionReattached
            {
                add { }
                remove { }
            }

            public event Action<SessionId, TransportDisconnectReason> SessionDetached
            {
                add { }
                remove { }
            }

            public event Action<ReplicationFeedbackMessage> FeedbackReceived
            {
                add { }
                remove { }
            }

            public void RaiseSessionAttached(SessionId session, MatchId match, PlayerId player) =>
                SessionAttached?.Invoke(session, match, player);
        }
    }
}
