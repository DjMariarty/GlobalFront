using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Phase 2.5 command flow + security baseline tests: session
    /// attribution, async CommandAck carrying BOTH rejection details,
    /// duplicate/stale/future tick handling, forged tokens, malformed
    /// messages and rate limiting — all through the authoritative gate and
    /// the unchanged Phase 2.4 SessionManager/MatchServer semantics.
    /// </summary>
    [TestFixture]
    public sealed class TransportFlowAndSecurityTests
    {
        private sealed class Flow
        {
            public TransportTestSupport.Rig Rig;
            public ClientTransportEndpoint Client;
            public NetworkCommandChannel Channel;
            public readonly List<CommandAckPayload> Acks = new List<CommandAckPayload>();
            public uint NextSequence = 1;

            public static Flow Establish(ImpairmentProfile profile = null)
            {
                var flow = new Flow
                {
                    Rig = TransportTestSupport.CreateRig(profile ?? new ImpairmentProfile())
                };

                // Both sessions must auto-join before the match can start
                // (roster must exactly cover the template slots).
                flow.Client = TransportTestSupport.CreateClient(flow.Rig);
                var second = TransportTestSupport.CreateClient(flow.Rig);
                Assert.That(
                    TransportTestSupport.PumpUntilEstablished(flow.Rig, flow.Client), Is.True,
                    "handshake must complete");
                Assert.That(
                    TransportTestSupport.PumpUntilEstablished(flow.Rig, second), Is.True,
                    "second handshake must complete");

                var config = TransportTestSupport.BuildMatchConfig(
                    new PlayerId(1), new PlayerId(2));
                Assert.That(
                    flow.Rig.Host.TryStartSessionMatch(flow.Rig.Match, config, out _), Is.True);

                flow.Channel = new NetworkCommandChannel(flow.Client);
                flow.Channel.CommandResultReceived += flow.Acks.Add;
                return flow;
            }

            public void Pump(long milliseconds = 5)
            {
                Rig.Pump(milliseconds);
                Client.Pump(Rig.Clock.NowMs);
            }

            public void PumpUntilAcks(int count, long budgetMs = 5000)
            {
                var elapsed = 0L;
                while (Acks.Count < count && elapsed < budgetMs)
                {
                    Pump(5);
                    elapsed += 5;
                }
            }

            public CommandHeader Header(PlayerId player, ulong requestedTick, uint? sequence = null) =>
                new CommandHeader(
                    player,
                    sequence ?? NextSequence++,
                    requestedTick,
                    GameCommandType.Move);

            public MatchCommandRejection SubmitMove(PlayerId player, ulong requestedTick, uint? sequence = null)
            {
                var header = Header(player, requestedTick, sequence);
                return Channel.TrySubmitMove(
                    header,
                    new[] { EntityFor(player) },
                    new WorldPointMm(1000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North));
            }

            public EntityId EntityFor(PlayerId player)
            {
                var snapshots = Rig.Host.GetAllSnapshots();
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
        }

        [Test]
        public void AcceptedCommand_ReturnsAcceptedAck_Asynchronously()
        {
            var flow = Flow.Establish();
            var local = flow.SubmitMove(flow.Client.Player, 1);

            // Local pre-flight: queued, NOT authoritative acceptance.
            Assert.That(local, Is.EqualTo(MatchCommandRejection.None));
            Assert.That(flow.Acks.Count, Is.EqualTo(0),
                "authoritative result must not exist before the round trip");

            flow.PumpUntilAcks(1);
            Assert.That(flow.Acks.Count, Is.EqualTo(1));
            Assert.That(flow.Acks[0].IsAccepted, Is.True);
            Assert.That(flow.Acks[0].Session, Is.EqualTo(SessionRejection.None));
            Assert.That(flow.Acks[0].Command, Is.EqualTo(MatchCommandRejection.None));
            Assert.That(flow.Acks[0].Player, Is.EqualTo(flow.Client.Player));
        }

        [Test]
        public void ForgedPlayerId_AckCarriesPlayerMismatch_Detail()
        {
            var flow = Flow.Establish();
            var otherPlayer = new PlayerId((byte)(flow.Client.Player.Value == 1 ? 2 : 1));

            flow.SubmitMove(otherPlayer, 1);
            flow.PumpUntilAcks(1);

            Assert.That(flow.Acks.Count, Is.EqualTo(1));
            Assert.That(flow.Acks[0].Session, Is.EqualTo(SessionRejection.PlayerMismatch));
            Assert.That(flow.Acks[0].Command, Is.EqualTo(MatchCommandRejection.SessionRejected));
        }

        [Test]
        public void DuplicateSequence_AckCarriesMatchRejection_Detail()
        {
            var flow = Flow.Establish();
            flow.SubmitMove(flow.Client.Player, 1, sequence: 5);
            flow.PumpUntilAcks(1);
            Assert.That(flow.Acks[0].IsAccepted, Is.True);

            flow.SubmitMove(flow.Client.Player, 2, sequence: 5);
            flow.PumpUntilAcks(2);

            Assert.That(flow.Acks[1].Session, Is.EqualTo(SessionRejection.None));
            Assert.That(flow.Acks[1].Command, Is.EqualTo(MatchCommandRejection.DuplicateSequence));
        }

        [Test]
        public void StaleRequestedTick_AckCarriesHeaderRejected()
        {
            var flow = Flow.Establish();

            // Advance the authoritative tick far beyond the past window.
            for (var index = 0; index < 30; index++)
            {
                flow.Rig.Host.TickOnce();
            }

            flow.SubmitMove(flow.Client.Player, 1);
            flow.PumpUntilAcks(1);

            Assert.That(flow.Acks[0].Session, Is.EqualTo(SessionRejection.None));
            Assert.That(flow.Acks[0].Command, Is.EqualTo(MatchCommandRejection.HeaderRejected));
        }

        [Test]
        public void FutureRequestedTick_AckCarriesHeaderRejected()
        {
            var flow = Flow.Establish();
            flow.SubmitMove(flow.Client.Player, 500);
            flow.PumpUntilAcks(1);

            Assert.That(flow.Acks[0].Session, Is.EqualTo(SessionRejection.None));
            Assert.That(flow.Acks[0].Command, Is.EqualTo(MatchCommandRejection.HeaderRejected));
        }

        [Test]
        public void DisconnectedSession_CommandRejected_ByServerGate()
        {
            var flow = Flow.Establish();
            var session = flow.Client.Session;

            // Server-side transport loss: grace window starts, session is
            // still known but not Connected.
            Assert.That(
                flow.Rig.Host.Sessions.NotifyConnectionLost(
                    session, flow.Rig.Host.CurrentTick),
                Is.True);

            // Client still believes it is established; the authoritative
            // gate must reject.
            flow.SubmitMove(flow.Client.Player, 1);
            flow.PumpUntilAcks(1);

            Assert.That(flow.Acks.Count, Is.EqualTo(1));
            Assert.That(flow.Acks[0].Session, Is.EqualTo(SessionRejection.SessionNotConnected));
            Assert.That(flow.Acks[0].Command, Is.EqualTo(MatchCommandRejection.SessionRejected));
        }

        [Test]
        public void ForgedToken_MessageIsDropped_AndCounted()
        {
            var flow = Flow.Establish();

            // Craft a Command message with a random token and push it
            // through the established client carrier (bypassing endpoint
            // pre-flight): the server must drop it on attribution.
            var buffer = new byte[64];
            var offset = MessageCodec.WriteHeader(
                buffer, 0, TransportMessageType.Command, 0xDEADBEEFCAFEul, 0);
            buffer[offset] = CommandWireCodec.Version;
            var droppedBefore = flow.Rig.ServerTransport.Metrics.DroppedBadToken;

            var carrier = (OwnDatagramCarrier)flow.Client.Carrier;
            // Connection id of the client's single connection.
            carrier.Send(1, TransportChannel.Command, buffer, 0, offset + 1);

            flow.Pump(10);
            Assert.That(
                flow.Rig.ServerTransport.Metrics.DroppedBadToken,
                Is.GreaterThan(droppedBefore));
            Assert.That(flow.Acks.Count, Is.EqualTo(0),
                "a forged-token command must never reach the gate");
        }

        [Test]
        public void MalformedMessage_IsDropped_WithoutExceptions()
        {
            var flow = Flow.Establish();
            var metrics = flow.Rig.ServerTransport.Metrics;

            var carrier = (OwnDatagramCarrier)flow.Client.Carrier;

            // Bad message magic.
            var badMagic = new byte[] { 0x00, 0x00, 0x01, 0x00, 0x07, 0, 0, 0, 0, 0, 0, 0, 0 };
            var beforeMalformed = metrics.DroppedMalformed;
            carrier.Send(1, TransportChannel.Command, badMagic, 0, badMagic.Length);
            flow.Pump(10);
            Assert.That(metrics.DroppedMalformed, Is.GreaterThan(beforeMalformed));

            // Bad message version.
            var badVersion = new byte[13];
            MessageCodec.WriteHeader(badVersion, 0, TransportMessageType.Command, flow.Client.Token, 0);
            badVersion[2] = 0xEE;
            var beforeVersion = metrics.DroppedBadVersion;
            carrier.Send(1, TransportChannel.Command, badVersion, 0, badVersion.Length);
            flow.Pump(10);
            Assert.That(metrics.DroppedBadVersion, Is.GreaterThan(beforeVersion));
        }

        [Test]
        public void Flood_IsRateLimited_AndCounted()
        {
            var flow = Flow.Establish();
            var metrics = flow.Rig.ServerTransport.Metrics;
            var before = metrics.DroppedRateLimited;

            // A flood does not respect send-side flow control, so the burst
            // is injected as raw datagrams straight into the pipe (rig
            // construction order: server endpoint 1, first client 2).
            var message = new byte[TransportProtocol.MessageHeaderSize];
            MessageCodec.WriteHeader(message, 0, TransportMessageType.Ping, flow.Client.Token, 0);
            var buffer = new byte[TransportProtocol.MaxDatagramBytes];
            var size = EnvelopeCodec.Encode(
                buffer,
                TransportChannel.Control,
                flow.Client.Token,
                sequence: 1,
                ackNumber: 0,
                ackBitmap: 0,
                messageId: 0,
                fragmentIndex: 0,
                fragmentCount: 0,
                payload: message,
                payloadOffset: 0,
                payloadLength: message.Length);

            for (var index = 0; index < TransportProtocol.RateLimitPacketsPerSecond + 100; index++)
            {
                flow.Rig.Pipe.SendDatagram(2, 1, buffer, size);
                flow.Pump(0);
            }

            Assert.That(metrics.DroppedRateLimited - before, Is.GreaterThan(0),
                "burst traffic above the per-endpoint budget must be dropped");
        }

        [Test]
        public void ServerTransportHost_HandleMessage_RateLimitExceeded_DropsIncomingPackets()
        {
            var flow = Flow.Establish();
            var host = flow.Rig.ServerTransport;
            var initialDropped = host.DroppedPackets;

            var message = new byte[TransportProtocol.MessageHeaderSize + 8];
            MessageCodec.WriteHeader(message, 0, TransportMessageType.Ping, flow.Client.Token, 0);
            var buffer = new byte[TransportProtocol.MaxDatagramBytes];
            var size = EnvelopeCodec.Encode(
                buffer,
                TransportChannel.Control,
                flow.Client.Token,
                sequence: 1,
                ackNumber: 0,
                ackBitmap: 0,
                messageId: 0,
                fragmentIndex: 0,
                fragmentCount: 0,
                payload: message,
                payloadOffset: 0,
                payloadLength: message.Length);

            var transportEvent = new TransportEvent
            {
                Type = TransportEventType.Message,
                ConnectionId = 1,
                Data = buffer,
                Length = size
            };

            for (var index = 0; index < TransportProtocol.RateLimitPacketsPerSecond + 50; index++)
            {
                host.HandleMessage(transportEvent);
            }

            Assert.That(host.DroppedPackets - initialDropped, Is.GreaterThan(0),
                "ServerTransportHost must drop packets exceeding rate limit");
            Assert.That(flow.Rig.ServerCarrier.Metrics.DroppedRateLimited, Is.GreaterThan(0));
        }

        [Test]
        public void ServerFull_DeniesAdditionalConnections()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile(), capacity: 1);

            var first = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, first), Is.True);

            var second = TransportTestSupport.CreateClient(rig);
            var denied = false;
            var elapsed = 0L;
            while (!denied && elapsed < 5000)
            {
                rig.Pump(5);
                second.Pump(rig.Clock.NowMs);
                denied = second.State == ClientTransportState.Disconnected;
                elapsed += 5;
            }

            Assert.That(denied, Is.True, "the third attachment must be denied at capacity");
        }

        [Test]
        public void SnapshotVersionMismatch_IsDenied()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var clientAddress = rig.Pipe.CreateEndpoint();
            var carrier = OwnDatagramCarrier.CreateClient(
                rig.Pipe, clientAddress, 1, rig.Clock);
            var endpoint = new ClientTransportEndpoint(carrier);

            var denied = ConnectDenyReason.None;
            endpoint.Denied += reason => denied = reason;
            endpoint.Connect("virtual", 0);

            // Corrupt the ConnectRequest snapshot version on the wire by
            // injecting a handshake with a wrong snapshot version directly.
            var buffer = new byte[64];
            var offset = MessageCodec.WriteHeader(
                buffer, 0, TransportMessageType.ConnectRequest, TransportProtocol.HandshakeToken, 0);
            offset = MessageCodec.EncodeConnectRequest(
                buffer, offset, SnapshotProtocolVersion() + 1, false, System.Guid.Empty);

            var elapsed = 0L;
            while (denied == ConnectDenyReason.None && elapsed < 5000)
            {
                // Send after the carrier connection exists.
                rig.Pump(5);
                endpoint.Pump(rig.Clock.NowMs);
                carrier.Send(1, TransportChannel.Control, buffer, 0, offset);
                elapsed += 5;
            }

            Assert.That(denied, Is.EqualTo(ConnectDenyReason.SnapshotVersionMismatch));
        }

        private static uint SnapshotProtocolVersion() =>
            GlobalFront.Server.Snapshot.SnapshotProtocol.Version;
    }
}
