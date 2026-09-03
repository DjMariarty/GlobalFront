using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Transport;
using NUnit.Framework;
namespace GlobalFront.Tests.EditMode.Integration.Replication
{
    /// <summary>
    /// Shared rig for the step 2.6.4 integration tests (ADR-010): the full
    /// chain LocalMatchHost tick loop → ServerReplicationEmitter → Phase 2.5
    /// transport (C2 deltas and keyframe slices, C0 acks and repair requests)
    /// → ClientTransportReplicationBridge → ClientReplicationReceiver, over
    /// the deterministic VirtualNetworkPipe.
    ///
    /// Composition order mirrors the runtime: the emitter is created before
    /// clients connect (attachments are tracked from then on), and every test
    /// drives authoritative ticks with <c>TickOnce</c> interleaved with
    /// transport pumping on the virtual clock.
    /// </summary>
    public sealed class ReplicationIntegrationWorld
    {
        public TransportTestSupport.Rig Rig;
        public ImpairmentProfile Profile;
        public ServerReplicationEmitter Emitter;
        public readonly List<ClientLeg> Clients = new List<ClientLeg>();

        public sealed class ClientLeg
        {
            public TransportTestSupport.Rig Rig;
            public ClientTransportEndpoint Endpoint;
            public NetworkCommandChannel Commands;
            public ClientReplicationReceiver Receiver;
            public ClientTransportReplicationBridge Bridge;
            public uint NextCommandSequence = 1;
            public readonly List<CommandAckPayload> CommandAcks = new List<CommandAckPayload>();

            public EntityId FirstEntityOf(PlayerId player)
            {
                var snapshots = Rig.Host.Server.GetAllSnapshots();
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

            /// <summary>Submits one move to a far destination (units keep moving).</summary>
            public void SubmitMoveTo(PlayerId player, EntityId entity, WorldPointMm destination, ulong requestedTick)
            {
                var accepted = Commands.TrySubmitMove(
                    new CommandHeader(player, NextCommandSequence++, requestedTick, GameCommandType.Move),
                    new[] { entity },
                    destination,
                    new FormationSpec(1, 2000, CardinalFacing.North));
                Assert.That(accepted, Is.EqualTo(MatchCommandRejection.None), "the move must pass the local pre-flight");
            }
        }

        /// <summary>
        /// Builds the rig, the emitter (wired to <c>TickCompleted</c>) and
        /// <paramref name="clientCount"/> fully connected client legs.
        /// </summary>
        public static ReplicationIntegrationWorld Create(
            ImpairmentProfile profile,
            ServerReplicationEmitterConfig emitterConfig,
            int clientCount = 1)
        {
            var world = new ReplicationIntegrationWorld
            {
                Rig = TransportTestSupport.CreateRig(profile),
                Profile = profile
            };

            var adapter = new TransportReplicationAdapter(world.Rig.ServerTransport);
            world.Emitter = new ServerReplicationEmitter(world.Rig.Host.Server, adapter, emitterConfig);
            world.Rig.Host.TickCompleted += world.Emitter.OnTickCompleted;

            for (var index = 0; index < clientCount; index++)
            {
                world.AddClient();
            }

            foreach (var client in world.Clients)
            {
                Assert.That(
                    TransportTestSupport.PumpUntilEstablished(world.Rig, client.Endpoint),
                    Is.True, "the client must reach Established");
            }

            return world;
        }

        public ClientLeg AddClient()
        {
            var endpoint = TransportTestSupport.CreateClient(Rig);
            var leg = new ClientLeg
            {
                Rig = Rig,
                Endpoint = endpoint,
                Receiver = new ClientReplicationReceiver(ClientReplicationWorld.DefaultCapacity),
                Commands = null
            };
            leg.Commands = new NetworkCommandChannel(endpoint);
            leg.Commands.CommandResultReceived += leg.CommandAcks.Add;
            leg.Bridge = new ClientTransportReplicationBridge(
                new ClientTransportUplink(endpoint), leg.Receiver);
            Clients.Add(leg);
            return leg;
        }

        /// <summary>Starts the session match with one moving-capable unit per owner.</summary>
        public void StartMatch(params PlayerId[] owners)
        {
            var config = TransportTestSupport.BuildMatchConfig(owners);
            Assert.That(
                Rig.Host.TryStartSessionMatch(Rig.Match, config, out _), Is.True,
                "the session match must start");
        }

        /// <summary>Starts the session match with <paramref name="unitCount"/> units, owners alternating.</summary>
        public void StartMatchWithUnits(int unitCount)
        {
            var specs = new UnitSpawnSpec[unitCount];
            for (var index = 0; index < unitCount; index++)
            {
                specs[index] = new UnitSpawnSpec(
                    new PlayerId((byte)((index % 2) + 1)),
                    new WorldPointMm((index / 2) * 15000, (index % 7) * 12000),
                    350,
                    false);
            }

            var config = new MatchConfig(TransportTestSupport.StandardStats, specs);
            Assert.That(
                Rig.Host.TryStartSessionMatch(Rig.Match, config, out _), Is.True,
                "the session match must start");
        }

        /// <summary>One authoritative tick followed by transport pumping.</summary>
        public void PumpTick(long pumpMs = 5)
        {
            Rig.Host.TickOnce();
            Pump(pumpMs);
        }

        public void Pump(long milliseconds = 5)
        {
            Rig.Pump(milliseconds);
            var now = Rig.Clock.NowMs;
            for (var index = 0; index < Clients.Count; index++)
            {
                var client = Clients[index];
                client.Endpoint.Pump(now);
                client.Bridge.Pump(now);
            }
        }

        /// <summary>
        /// Pumps until the client confirmed the authoritative tick and streams,
        /// or the virtual-time budget ends. With <paramref name="keepTicking"/>
        /// the server keeps ticking so the repair machinery (requests,
        /// cumulative answers, keyframes) has fresh traffic to work with.
        /// </summary>
        public bool PumpUntilCaughtUp(
            ClientLeg client,
            long budgetMs = 15000,
            long stepMs = 5,
            bool keepTicking = false)
        {
            var elapsed = 0L;
            while (elapsed < budgetMs)
            {
                if (keepTicking)
                {
                    Rig.Host.TickOnce();
                }

                Pump(stepMs);
                elapsed += stepMs;
                if (client.Receiver.LastAppliedTick >= Rig.Host.CurrentTick &&
                    client.Receiver.State == ReplicationReceiverState.Streaming &&
                    !client.Receiver.WorldPartiallyApplied)
                {
                    return true;
                }
            }

            Assert.Fail(
                "the client did not converge within {0} ms of virtual time.\n{1}",
                budgetMs, DescribeState(client));
            return false;
        }

        /// <summary>Full protocol-state dump for convergence diagnostics.</summary>
        public string DescribeState(ClientLeg client)
        {
            var receiver = client.Receiver;
            var bridge = client.Bridge;
            var emitter = Emitter;
            var serverMetrics = Rig.ServerCarrier.Metrics;
            var clientMetrics = client.Endpoint.Carrier.Metrics;
            var server = Rig.Host.Server;
            var viewFound = emitter.TryGetClientView(client.Endpoint.Session, out var view);
            var ring = emitter.History;
            var liveGate = Rig.Host.Sessions.ValidateCommand(
                client.Endpoint.Session,
                new CommandHeader(new PlayerId(1), 99, Rig.Host.CurrentTick + 1, GameCommandType.Move));
            var sessionText = Rig.Host.TryGetSession(client.Endpoint.Session, out var sessionRecord)
                ? $"state={sessionRecord.State} match={sessionRecord.Match.IsValid} player={sessionRecord.Player.Value}"
                : "missing";
            return
                $"server tick={Rig.Host.CurrentTick} units={server.UnitCount}\n" +
                $"ring: count={ring.Count} oldest={ring.OldestTick} newest={ring.NewestTick} " +
                $"evicted={ring.EvictedThroughTick} overflow={ring.OverflowCount} ignored={ring.IgnoredRecordCount}\n" +
                $"emitter client view: found={viewFound} active={view.Active} " +
                $"lastAcked={view.LastAckedTick} lastSent={view.LastSentTick} " +
                $"kfTick={view.KeyframeTick} kfSeq={view.KeyframeSeq} inFlight={view.KeyframeInFlight} " +
                $"needsKf={view.NeedsKeyframe} tokens={view.PacingTokens}\n" +
                $"first unit: {(server.UnitCount > 0 && server.GetAllSnapshots()[0] is var u ? $"pos=({u.Position.X},{u.Position.Z}) target={u.HasMoveTarget}" : "none")}\n" +
                $"command acks: {string.Join(" | ", client.CommandAcks.ConvertAll(a => $"seq={a.CommandSequence} accepted={a.IsAccepted} session={a.Session} command={a.Command}"))}\n" +
                $"last accepted seq P1: {(server.TryGetLastAcceptedSequence(new PlayerId(1), out var seq) ? seq.ToString() : "none")}\n" +
                $"live gate: {liveGate}\n" +
                $"session record: {sessionText}\n" +
                $"receiver: state={receiver.State} last={receiver.LastAppliedTick} " +
                $"base={receiver.BaseKeyframeTick} seq={receiver.CurrentKeyframeSeq} " +
                $"failure={receiver.FailureReason} partial={receiver.WorldPartiallyApplied}\n" +
                $"receiver counters: applied={receiver.AppliedDeltaCount} stale={receiver.StaleDropCount} " +
                $"catchup={receiver.CatchUpCount} rebase={receiver.RebaseCount} " +
                $"kfMismatch={receiver.KeyframeMismatchCount} multipart={receiver.IncompleteMultipartCount} " +
                $"invalid={receiver.InvalidPayloadCount} inconsistent={receiver.InconsistentApplyCount} " +
                $"capacity={receiver.CapacityRejectCount}\n" +
                $"receiver requests: total={receiver.TotalRequestCount} " +
                $"snapshot={receiver.Fsm.SnapshotRequestCount} resume={receiver.Fsm.DeltaResumeCount} " +
                $"pending={receiver.PendingRequestCount} attempts={receiver.Fsm.AttemptsMade}\n" +
                $"bridge: keyframes={bridge.AssembledKeyframeCount} slices={bridge.AssembledSliceCount} " +
                $"deltas={bridge.ForwardedDeltaCount} acks={bridge.SentAckCount} " +
                $"requests={bridge.SentRequestCount} malformed={bridge.MalformedPayloadCount} " +
                $"staleSlices={bridge.DroppedStaleSliceCount} dupSlices={bridge.DroppedDuplicateSliceCount}\n" +
                $"emitter: deltas={emitter.EmittedDeltaCount} keyframes={emitter.EmittedKeyframeCount} " +
                $"slices={emitter.EmittedSliceCount} resumes={emitter.DeltaResumeServedCount} " +
                $"fallbacks={emitter.KeyframeFallbackCount} acks={emitter.AckCount} " +
                $"requests={emitter.RequestCount} malformedFeedback={emitter.MalformedFeedbackCount} " +
                $"sendFailures={emitter.SendFailureCount} shortfall={emitter.SnapshotShortfallCount} " +
                $"diffFailures={emitter.DiffFailureCount}\n" +
                $"server carrier: sent={serverMetrics.DatagramsSent} recv={serverMetrics.DatagramsReceived} " +
                $"staleSnap={serverMetrics.DroppedStaleSnapshot} replayWin={serverMetrics.DroppedReplayOrWindow} " +
                $"rateLimited={serverMetrics.DroppedRateLimited} overflow={serverMetrics.DroppedQueueOverflow} " +
                $"malformed={serverMetrics.DroppedMalformed} retrans={serverMetrics.Retransmissions} " +
                $"maxRetransLosses={serverMetrics.MaxRetransmitLosses} timeouts={serverMetrics.ConnectionTimeouts}\n" +
                $"client carrier: sent={clientMetrics.DatagramsSent} recv={clientMetrics.DatagramsReceived} " +
                $"staleSnap={clientMetrics.DroppedStaleSnapshot} replayWin={clientMetrics.DroppedReplayOrWindow} " +
                $"rateLimited={clientMetrics.DroppedRateLimited} overflow={clientMetrics.DroppedQueueOverflow} " +
                $"malformed={clientMetrics.DroppedMalformed} retrans={clientMetrics.Retransmissions} " +
                $"maxRetransLosses={clientMetrics.MaxRetransmitLosses} timeouts={clientMetrics.ConnectionTimeouts}\n" +
                $"endpoint state={client.Endpoint.State}";
        }

        /// <summary>Bit-exact comparison of the authoritative world and the client mirror.</summary>
        public void AssertWorldsMatch(ClientLeg client)
        {
            var server = Rig.Host.Server;
            var snapshots = new ServerUnitSnapshot[server.UnitCount];
            Assert.That(
                server.CopySnapshots(snapshots), Is.EqualTo(server.UnitCount),
                "the capture scratch must hold the whole world");

            var receiver = client.Receiver;
            Assert.That(receiver.World.LiveCount, Is.EqualTo(snapshots.Length),
                "the client table must hold exactly the replicated units");

            for (var index = 0; index < snapshots.Length; index++)
            {
                var unit = snapshots[index];
                Assert.That(receiver.TryGetUnit(unit.Entity, out var clientState), Is.True,
                    $"entity {unit.Entity.Value} must exist on the client");
                Assert.That(clientState.Owner, Is.EqualTo(unit.Owner), $"entity {unit.Entity.Value} owner");
                Assert.That(clientState.Position, Is.EqualTo(unit.Position), $"entity {unit.Entity.Value} position");
                Assert.That(clientState.Health, Is.EqualTo(unit.CurrentHealth), $"entity {unit.Entity.Value} health");                Assert.That(clientState.HasMoveTarget, Is.EqualTo(unit.HasMoveTarget), $"entity {unit.Entity.Value} hasMoveTarget");
                Assert.That(clientState.MoveTarget, Is.EqualTo(unit.MoveTarget), $"entity {unit.Entity.Value} moveTarget");
                Assert.That(clientState.AttackTarget, Is.EqualTo(unit.AttackTarget), $"entity {unit.Entity.Value} attackTarget");
                Assert.That(clientState.AutoAcquire, Is.EqualTo(unit.AutoAcquireEnemies), $"entity {unit.Entity.Value} autoAcquire");
            }
        }
    }

    /// <summary>Non-allocating snapshot source for the zero-GC emitter test.</summary>
    public sealed class FakeSnapshotSource : IServerSnapshotSource
    {
        public readonly ServerUnitSnapshot[] Units;

        public FakeSnapshotSource(int count)
        {
            Units = new ServerUnitSnapshot[count];
            for (var index = 0; index < count; index++)
            {
                Units[index] = new ServerUnitSnapshot(
                    new EntityId((ulong)(index + 1)),
                    new PlayerId(1),
                    new WorldPointMm(index * 1000, 0),
                    100,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false);
            }
        }

        /// <summary>Moves one unit per call without allocating (struct rewrite).</summary>
        public void Mutate(int iteration)
        {
            var index = iteration % Units.Length;
            Units[index] = new ServerUnitSnapshot(
                Units[index].Entity,
                Units[index].Owner,
                new WorldPointMm(Units[index].Position.X + 1, Units[index].Position.Z),
                Units[index].CurrentHealth,
                true,
                new WorldPointMm(Units[index].Position.X + 100, Units[index].Position.Z),
                Units[index].AttackTarget,
                Units[index].AutoAcquireEnemies);
        }

        public int CopySnapshots(Span<ServerUnitSnapshot> destination)
        {
            if (destination.Length < Units.Length)
            {
                return -1;
            }

            Units.AsSpan().CopyTo(destination);
            return Units.Length;
        }
    }

    /// <summary>Non-allocating transport seam for the zero-GC emitter test.</summary>
    public sealed class FakeReplicationTransport : IReplicationTransport
    {
        public long DeltasSent;
        public long SlicesSent;
        public long KeyframesSent;
        public long SendFailures;

        private static readonly SessionId AttachedSession =
            new SessionId(new Guid(
                0x01020304, 0x0506, 0x0708,
                0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10));

        public event Action<SessionId, MatchId, PlayerId> SessionAttached;

        public event Action<SessionId, TransportDisconnectReason> SessionDetached;

        public event Action<ReplicationFeedbackMessage> FeedbackReceived;

        public void AttachOneClient() =>
            SessionAttached?.Invoke(AttachedSession, default, new PlayerId(1));

        public void DeliverAck(byte[] buffer) =>
            FeedbackReceived?.Invoke(new ReplicationFeedbackMessage(
                AttachedSession, TransportMessageType.SnapshotAck, buffer, 0, buffer.Length));

        /// <summary>
        /// Simulates the receiver FSM's baseline episode: the client's first
        /// pump asks for a keyframe. The request buffer is preallocated.
        /// </summary>
        public void DeliverSnapshotRequest()
        {
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.SnapshotRequest, attempt: 1, 0, 0, 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes];
            Assert.That(
                ReplicationRequestCodec.TryEncode(request, buffer, out var written),
                Is.EqualTo(ReplicationRequestCodecResult.Ok));
            FeedbackReceived?.Invoke(new ReplicationFeedbackMessage(
                AttachedSession, TransportMessageType.ReplicationRequest, buffer, 0, written));
        }

        public bool SendToSession(SessionId session, ulong envelopeTick, byte[] buffer, int length)
        {
            if (buffer == null || length < 1)
            {
                SendFailures++;
                return false;
            }

            if (buffer[0] == KeyframeSliceCodec.MessageType)
            {
                SlicesSent++;
                return true;
            }

            if (buffer[0] == DeltaSnapshotProtocol.MessageTypeDelta)
            {
                DeltasSent++;
                return true;
            }

            // Anything else is a keyframe carrier the fake does not model.
            KeyframesSent++;
            return true;
        }
    }

    /// <summary>Non-allocating uplink seam for the zero-GC bridge test.</summary>
    public sealed class FakeReplicationUplink : IReplicationUplink
    {
        public int AcksSent;
        public int RequestsSent;

        public event Action<ulong, byte[]> SnapshotPayloadReceived;

        public bool TrySendFeedback(ReplicationFeedbackKind kind, byte[] payload, int length)
        {
            if (kind == ReplicationFeedbackKind.Ack)
            {
                AcksSent++;
            }
            else
            {
                RequestsSent++;
            }

            return true;
        }

        public void Deliver(ulong tick, byte[] payload) =>
            SnapshotPayloadReceived?.Invoke(tick, payload);
    }
}
