using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalFront.Client;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using ReconnectResult = GlobalFront.Core.Reconnect.ReconnectResult;

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
            public ClientTransportEndpoint Second;
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
                flow.Second = second;
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
        public void ConnectRequest_FromAlreadyBoundConnection_IsDeniedAndRosterPreserved()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile(), capacity: 2);

            var hijacker = TransportTestSupport.CreateClient(rig);
            Assert.That(
                TransportTestSupport.PumpUntilEstablished(rig, hijacker), Is.True,
                "first handshake must complete");
            Assert.That(hijacker.Player.Value, Is.EqualTo(1));

            var boundToken = hijacker.Token;
            var denied = ConnectDenyReason.None;
            hijacker.Denied += reason => denied = reason;

            // Replay a fully valid ConnectRequest through the SAME socket.
            var handshake = new byte[64];
            var handshakeLength = MessageCodec.WriteHeader(
                handshake, 0, TransportMessageType.ConnectRequest, TransportProtocol.HandshakeToken, 0);
            handshakeLength = MessageCodec.EncodeConnectRequest(
                handshake,
                handshakeLength,
                SnapshotProtocolVersion(),
                false,
                System.Guid.Empty);

            var carrier = (OwnDatagramCarrier)hijacker.Carrier;
            Assert.That(
                carrier.Send(1, TransportChannel.Control, handshake, 0, handshakeLength),
                Is.True);

            var elapsed = 0L;
            while (denied == ConnectDenyReason.None && elapsed < 5000)
            {
                rig.Pump(5);
                hijacker.Pump(rig.Clock.NowMs);
                elapsed += 5;
            }

            Assert.That(denied, Is.EqualTo(ConnectDenyReason.SessionResumeRejected),
                "a repeated handshake on an already bound socket must be denied");
            Assert.That(hijacker.Token, Is.EqualTo(boundToken),
                "the hijack attempt must not replace the original identity");

            Assert.That(
                rig.Host.Sessions.TryGetAssignedCount(rig.Match, out var assignedPlayers),
                Is.True);
            Assert.That(assignedPlayers, Is.EqualTo(1),
                "the rejected handshake must not consume a second PlayerId slot");

            var legitimate = TransportTestSupport.CreateClient(rig);
            Assert.That(
                TransportTestSupport.PumpUntilEstablished(rig, legitimate), Is.True,
                "the second roster slot must remain available to a legitimate player");
            Assert.That(legitimate.Player.Value, Is.EqualTo(2));
        }

        [Test]
        public void DoubleBindOnSameConnection_IsDenied()
        {
            var flow = Flow.Establish();
            var binder = flow.Rig.ServerTransport.Binder;
            var sessionA = flow.Client.Session;
            var sessionB = flow.Second.Session;

            Assert.That(binder.TryGetTokenBySession(sessionA, out var tokenA), Is.True);
            Assert.That(binder.TryGetByToken(tokenA, out var bindingA), Is.True);
            var connectionA = bindingA.ConnectionId;

            Assert.That(flow.Rig.Host.Sessions.TryGetSession(sessionB, out var recordB), Is.True);
            var secretB = recordB.Secret;
            Assert.That(binder.TryGetTokenBySession(sessionB, out var tokenB), Is.True);
            Assert.That(binder.TryGetByToken(tokenB, out var bindingB), Is.True);
            Assert.That(bindingB.ConnectionId, Is.Not.EqualTo(connectionA));

            // B loses its socket in a normal network blip; its secret stays valid.
            flow.Rig.ServerCarrier.CloseConnection(
                bindingB.ConnectionId, TransportDisconnectReason.TransportLost);
            var elapsed = 0L;
            while (elapsed < 1000 &&
                !(flow.Rig.Host.Sessions.TryGetSession(sessionB, out var detached) &&
                    detached.State == SessionState.Disconnected))
            {
                flow.Rig.Pump(5);
                elapsed += 5;
            }

            Assert.That(flow.Rig.Host.Sessions.TryGetSession(sessionB, out var detachedB), Is.True);
            Assert.That(detachedB.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(binder.BoundCount, Is.EqualTo(1));

            // Socket A now asks to re-attach B as well.
            var request = new ReconnectRequest(sessionB, secretB.ToByteArray(), 0);
            var buffer = new byte[ReconnectWireCodec.RequestSizeBytes];
            Assert.That(ReconnectWireCodec.TryEncodeRequest(request, buffer, out var written), Is.True);
            var carrier = (OwnDatagramCarrier)flow.Client.Carrier;
            Assert.That(carrier.Send(1, TransportChannel.Control, buffer, 0, written), Is.True);

            ReconnectResponse response = default;
            var received = false;
            elapsed = 0L;
            while (!received && elapsed < 5000)
            {
                flow.Rig.Pump(5);
                carrier.Pump(flow.Rig.Clock.NowMs);
                while (carrier.TryDequeueEvent(out var transportEvent))
                {
                    if (transportEvent.Type == TransportEventType.Message &&
                        transportEvent.Length >= ReconnectWireCodec.ResponseSizeBytes &&
                        transportEvent.Data[0] == ReconnectWireCodec.ResponseOpcode)
                    {
                        Assert.That(
                            ReconnectWireCodec.TryDecodeResponse(
                                transportEvent.Data.AsSpan(0, transportEvent.Length),
                                out response),
                            Is.True);
                        received = true;
                    }
                }

                elapsed += 5;
            }

            Assert.That(received, Is.True, "the hijacking socket must receive an explicit denial");
            Assert.That(response.Result, Is.EqualTo(ReconnectResult.Denied));
            Assert.That(binder.BoundCount, Is.EqualTo(1),
                "a denied reconnect must not add a second session to the socket");
            Assert.That(binder.TryGetByConnection(connectionA, out var survivor), Is.True);
            Assert.That(survivor.Session, Is.EqualTo(sessionA));
            Assert.That(survivor.Token, Is.EqualTo(tokenA));
            Assert.That(flow.Rig.Host.Sessions.TryGetSession(sessionB, out var stillDetached), Is.True);
            Assert.That(stillDetached.State, Is.EqualTo(SessionState.Disconnected),
                "the rejected request must not alter the displaced session");
        }

        [Test]
        public void ReleaseOfDisplacedSession_PreservesActiveConnectionMapping()
        {
            var binder = new TransportSessionBinder();
            var sessionA = new SessionId(Guid.NewGuid());
            var sessionB = new SessionId(Guid.NewGuid());
            const int connection = 41;

            var tokenA = binder.Bind(
                sessionA, new ConnectionHandle(1), new MatchId(1ul), new PlayerId(1), connection);
            var tokenB = binder.Bind(
                sessionB, new ConnectionHandle(2), new MatchId(1ul), new PlayerId(2), connection + 1);

            // Stage the displaced index state the old Bind/Release pair could
            // produce: the socket maps to B while A's binding still exists.
            var mapField = typeof(TransportSessionBinder).GetField(
                "_tokenByConnection", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(mapField, Is.Not.Null);
            var tokenByConnection = (Dictionary<int, ulong>)mapField.GetValue(binder);
            tokenByConnection.Remove(connection + 1);
            tokenByConnection[connection] = tokenB;

            var byTokenField = typeof(TransportSessionBinder).GetField(
                "_byToken", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(byTokenField, Is.Not.Null);
            var byToken =
                (Dictionary<ulong, TransportSessionBinder.Binding>)byTokenField.GetValue(binder);
            var moved = byToken[tokenB];
            moved.ConnectionId = connection;
            byToken[tokenB] = moved;

            Assert.That(binder.Release(sessionA), Is.True);

            Assert.That(binder.TryGetByConnection(connection, out var survivor), Is.True,
                "Release(A) must not delete a socket mapping owned by B");
            Assert.That(survivor.Session, Is.EqualTo(sessionB));
            Assert.That(survivor.Token, Is.EqualTo(tokenB));
            Assert.That(binder.TryGetByToken(tokenA, out _), Is.False);
            Assert.That(binder.TryGetTokenBySession(sessionA, out _), Is.False);
            Assert.That(binder.BoundCount, Is.EqualTo(1));
            Assert.That(binder.SnapshotTargets.Count, Is.EqualTo(1));
            Assert.That(binder.SnapshotTargets[0], Is.EqualTo(sessionB));
        }

        [Test]
        public void BoundCount_ReturnsToBaseline_OnDisconnect()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            var binder = rig.ServerTransport.Binder;
            Assert.That(binder.BoundCount, Is.EqualTo(1));
            Assert.That(rig.ServerTransport.AttachedCount, Is.EqualTo(1));
            Assert.That(binder.TryGetTokenBySession(client.Session, out var token), Is.True);
            Assert.That(binder.TryGetByToken(token, out var binding), Is.True);

            client.GracefulDisconnect();
            rig.Pump(5);
            client.Pump(rig.Clock.NowMs);

            Assert.That(binder.BoundCount, Is.EqualTo(0), "bound sessions must not leak");
            Assert.That(rig.ServerTransport.AttachedCount, Is.EqualTo(0));
            Assert.That(binder.TryGetTokenBySession(client.Session, out _), Is.False);
            Assert.That(binder.TryGetByToken(token, out _), Is.False);
            Assert.That(binder.TryGetByConnection(binding.ConnectionId, out _), Is.False);
            Assert.That(binder.SnapshotTargets.Count, Is.EqualTo(0));
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
