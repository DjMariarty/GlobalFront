using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode;
using GlobalFront.Tests.EditMode.Integration.Replication;
using NUnit.Framework;
using CoreEntityId = GlobalFront.Core.Model.EntityId;
using ReconnectResult = GlobalFront.Core.Reconnect.ReconnectResult;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Integration.Reconnection
{
    [TestFixture]
    public sealed class ReconnectionIntegrationTests
    {
        [Test]
        public void HappyPath_FullReconnectResyncAndTacticalPause()
        {
            // 1. Start match with 2 players and moving units over perfect pipe
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client0 = world.Clients[0];
            var coord0 = new ClientReconnectCoordinator(client0.Receiver);
            world.Rig.Host.BindReconnectCoordinator(coord0);
            coord0.ResyncStarted += seq => client0.Bridge.PrepareForResync(seq);
            coord0.CacheSession(
                client0.Endpoint.Session,
                client0.Endpoint.Match,
                client0.Endpoint.Player,
                client0.Endpoint.Secret);
            client0.Endpoint.Lost += reason => coord0.OnTransportDisconnected(reason);

            // 2. Issue move command to far destination for player 1's unit
            var p1Unit = client0.FirstEntityOf(new PlayerId(1));
            client0.SubmitMoveTo(new PlayerId(1), p1Unit, new WorldPointMm(40000, 40000), requestedTick: 1);

            // 3. Advance 10 ticks: unit begins movement
            for (var i = 0; i < 10; i++)
            {
                world.PumpTick();
            }

            Assert.That(client0.Receiver.LastAppliedTick, Is.EqualTo(10UL));
            Assert.That(client0.Receiver.TryGetUnit(p1Unit, out var unitAtTick10), Is.True);
            var initialX = unitAtTick10.Position.X;
            Assert.That(initialX, Is.GreaterThan(0), "Unit must have started moving");

            // 4. Disconnect client 0 involuntarily
            Assert.That(world.Rig.ServerTransport.Binder.TryGetTokenBySession(client0.Endpoint.Session, out var token0), Is.True);
            Assert.That(world.Rig.ServerTransport.Binder.TryGetByToken(token0, out var binding0), Is.True);
            client0.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            world.Rig.ServerCarrier.CloseConnection(binding0.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var i = 0; i < 5; i++)
            {
                world.Pump(5);
            }

            Assert.That(client0.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected));
            Assert.That(coord0.State, Is.EqualTo(ClientReconnectState.Reconnecting));

            // 5. Tactical Pause (OD-18 / P0-1 / P0-2)
            Assert.That(world.Rig.Host.IsPaused, Is.True, "Host must auto-pause on player disconnect (OD-18)");
            var pausedTick = world.Rig.Host.CurrentTick;
            Assert.That(world.Rig.Host.TickOnce(), Is.False, "Simulation must not advance during tactical pause (OD-18)");
            Assert.That(world.Rig.Host.CurrentTick, Is.EqualTo(pausedTick), "Tick counter remains frozen");

            // 6. Client 0 establishes new carrier socket for reconnect
            var reconnectAddress = world.Rig.Pipe.CreateEndpoint();
            var reconnectCarrier = OwnDatagramCarrier.CreateClient(
                world.Rig.Pipe, reconnectAddress, TransportTestSupport.ServerAddressOf(world.Rig), world.Rig.Clock);
            var reconnectConnId = reconnectCarrier.ConnectToServer("virtual", 0);

            for (var i = 0; i < 5; i++)
            {
                world.Rig.Pump(5);
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);
            }

            // 7. Client 0 encodes and sends C0 ReconnectRequest
            coord0.LastAppliedTick = client0.Receiver.LastAppliedTick;
            var reqBuffer = new byte[ReconnectRequest.SizeBytes];
            Assert.That(coord0.TryBuildReconnectRequest(reqBuffer, out var written), Is.True);
            reconnectCarrier.Send(reconnectConnId, TransportChannel.Control, reqBuffer, 0, written);

            // 8. Client 0 receives ReconnectResponse and assembled Keyframe slices while simulation remains paused (P0-3)
            Assert.That(world.Rig.Host.IsPaused, Is.True, "Host must remain paused while slices are pumped and assembled");
            var responseReceived = false;
            ReconnectResponse reconnectResponse = default;
            var elapsed = 0L;
            var eventsDequeued = 0;
            var snapshotsDequeued = 0;
            var headersDecoded = 0;
            var lastErrorCode = MessageError.None;
            while ((!responseReceived || !client0.Receiver.IsWorldUsable) && elapsed < 5000)
            {
                world.Rig.Pump(5);
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);
                client0.Bridge.Pump(world.Rig.Clock.NowMs);

                while (reconnectCarrier.TryDequeueEvent(out var evt))
                {
                    eventsDequeued++;
                    if (evt.Type == TransportEventType.Message)
                    {
                        if (evt.Length > 0 && evt.Data[0] == ReconnectWireCodec.ResponseOpcode)
                        {
                            Assert.That(ReconnectWireCodec.TryDecodeResponse(evt.Data.AsSpan(0, evt.Length), out reconnectResponse), Is.True);
                            responseReceived = true;
                            coord0.OnReconnectResponse(in reconnectResponse);
                        }
                        else
                        {
                            snapshotsDequeued++;
                            lastErrorCode = MessageCodec.TryDecodeHeader(evt.Data, 0, evt.Length, out var header);
                            if (lastErrorCode == MessageError.None && header.Type == TransportMessageType.Snapshot)
                            {
                                headersDecoded++;
                                var payload = new byte[header.PayloadLength];
                                Array.Copy(evt.Data, header.PayloadOffset, payload, 0, header.PayloadLength);
                                client0.Bridge.HandleSnapshotPayload(header.SnapshotTick, payload, header.PayloadLength, world.Rig.Clock.NowMs);
                            }
                        }
                    }
                }
                elapsed += 5;
            }

            Assert.That(responseReceived, Is.True);
            Assert.That(reconnectResponse.Result, Is.EqualTo(ReconnectResult.Accepted));
            Assert.That(coord0.State, Is.EqualTo(ClientReconnectState.Resyncing));

            // Verify Keyframe installed
            Assert.That(client0.Receiver.IsWorldUsable, Is.True,
                $"Diag: bSeq={client0.Bridge.AssembledKeyframeSeq}, bCount={client0.Bridge.AssembledKeyframeCount}, slices={client0.Bridge.AssembledSliceCount}, stale={client0.Bridge.DroppedStaleSliceCount}, dup={client0.Bridge.DroppedDuplicateSliceCount}, malf={client0.Bridge.MalformedPayloadCount}, leg={client0.Bridge.IgnoredLegacySnapshotCount}, usable={client0.Receiver.IsWorldUsable}, recSeq={client0.Receiver.CurrentKeyframeSeq}, respSeq={reconnectResponse.ActiveKeyframeSeq}");
            Assert.That(client0.Receiver.CurrentKeyframeSeq, Is.EqualTo(reconnectResponse.ActiveKeyframeSeq));

            coord0.OnKeyframeInstalled();
            Assert.That(coord0.State, Is.EqualTo(ClientReconnectState.Countdown));
            Assert.That(coord0.CountdownRemainingSeconds, Is.EqualTo(5.0f));

            // 9. 5-second countdown timer (OD-20)
            coord0.AdvanceCountdown(2.0f, out var ready1);
            Assert.That(ready1, Is.False);
            Assert.That(coord0.CountdownRemainingSeconds, Is.EqualTo(3.0f).Within(0.001f));

            coord0.AdvanceCountdown(3.0f, out var ready2);
            Assert.That(ready2, Is.True);
            Assert.That(coord0.State, Is.EqualTo(ClientReconnectState.Connected));

            // 10. Tactical pause automatically lifted upon 5-second countdown completion (OD-20 / P1-1)
            Assert.That(world.Rig.Host.IsPaused, Is.False, "Simulation must resume automatically when countdown finishes");

            for (var i = 0; i < 20; i++)
            {
                world.PumpTick();
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);
                while (reconnectCarrier.TryDequeueEvent(out var evt))
                {
                    if (evt.Type == TransportEventType.Message)
                    {
                        var error = MessageCodec.TryDecodeHeader(evt.Data, 0, evt.Length, out var header);
                        if (error == MessageError.None && header.Type == TransportMessageType.Snapshot)
                        {
                            var payload = new byte[header.PayloadLength];
                            Array.Copy(evt.Data, header.PayloadOffset, payload, 0, header.PayloadLength);
                            client0.Bridge.HandleSnapshotPayload(header.SnapshotTick, payload, header.PayloadLength, world.Rig.Clock.NowMs);
                        }
                    }
                }
                client0.Bridge.Pump(world.Rig.Clock.NowMs);
            }

            Assert.That(world.Rig.Host.CurrentTick, Is.GreaterThan(pausedTick + 15));

            // 11. Units continued moving to original destination without command drop (OD-19)
            Assert.That(client0.Receiver.TryGetUnit(p1Unit, out var unitLater), Is.True);
            Assert.That(unitLater.Position.X, Is.GreaterThan(initialX), "Unit resumed movement towards destination");
        }

        [Test]
        public void Reconnect_TimeoutGraceExpired_SessionTerminated()
        {
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(),
                ServerReplicationEmitterConfig.Default,
                clientCount: 2,
                disconnectGraceTicks: 10);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client0 = world.Clients[0];
            var coord0 = new ClientReconnectCoordinator(client0.Receiver);
            coord0.CacheSession(
                client0.Endpoint.Session,
                client0.Endpoint.Match,
                client0.Endpoint.Player,
                client0.Endpoint.Secret);

            // Involuntary disconnect at tick 0
            Assert.That(world.Rig.ServerTransport.Binder.TryGetTokenBySession(client0.Endpoint.Session, out var token0), Is.True);
            Assert.That(world.Rig.ServerTransport.Binder.TryGetByToken(token0, out var binding0), Is.True);
            client0.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            world.Rig.ServerCarrier.CloseConnection(binding0.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var i = 0; i < 5; i++)
            {
                world.Pump(5);
            }

            // Unpause simulation to advance ticks past grace window (OD-18 grace is 10 ticks in this test)
            world.Rig.Host.Resume();
            for (var i = 0; i < 15; i++)
            {
                Assert.That(world.Rig.Host.TickOnce(), Is.True);
            }

            // Attempt reconnect
            var reconnectAddress = world.Rig.Pipe.CreateEndpoint();
            var reconnectCarrier = OwnDatagramCarrier.CreateClient(
                world.Rig.Pipe, reconnectAddress, TransportTestSupport.ServerAddressOf(world.Rig), world.Rig.Clock);
            var reconnectConnId = reconnectCarrier.ConnectToServer("virtual", 0);

            for (var i = 0; i < 5; i++)
            {
                world.Rig.Pump(5);
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);
            }

            coord0.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            var reqBuffer = new byte[ReconnectRequest.SizeBytes];
            Assert.That(coord0.TryBuildReconnectRequest(reqBuffer, out var written), Is.True);
            reconnectCarrier.Send(reconnectConnId, TransportChannel.Control, reqBuffer, 0, written);

            var responseReceived = false;
            ReconnectResponse reconnectResponse = default;
            var elapsed = 0L;
            while (!responseReceived && elapsed < 5000)
            {
                world.Rig.Pump(5);
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);

                while (reconnectCarrier.TryDequeueEvent(out var evt))
                {
                    if (evt.Type == TransportEventType.Message && evt.Length > 0 && evt.Data[0] == ReconnectWireCodec.ResponseOpcode)
                    {
                        Assert.That(ReconnectWireCodec.TryDecodeResponse(evt.Data.AsSpan(0, evt.Length), out reconnectResponse), Is.True);
                        responseReceived = true;
                        break;
                    }
                }
                elapsed += 5;
            }

            Assert.That(responseReceived, Is.True);
            Assert.That(reconnectResponse.Result, Is.EqualTo(ReconnectResult.GraceExpired));

            coord0.OnReconnectResponse(in reconnectResponse);
            Assert.That(coord0.State, Is.EqualTo(ClientReconnectState.Terminated));
        }

        [Test]
        public void Reconnect_Security_InvalidSecret_Rejected()
        {
            var world = ReplicationIntegrationWorld.Create(
                new ImpairmentProfile(), ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client0 = world.Clients[0];
            var correctSecret = client0.Endpoint.Secret;

            // Involuntary disconnect
            Assert.That(world.Rig.ServerTransport.Binder.TryGetTokenBySession(client0.Endpoint.Session, out var token0), Is.True);
            Assert.That(world.Rig.ServerTransport.Binder.TryGetByToken(token0, out var binding0), Is.True);
            client0.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            world.Rig.ServerCarrier.CloseConnection(binding0.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var i = 0; i < 5; i++)
            {
                world.Pump(5);
            }

            // Connect rogue carrier
            var rogueAddress = world.Rig.Pipe.CreateEndpoint();
            var rogueCarrier = OwnDatagramCarrier.CreateClient(
                world.Rig.Pipe, rogueAddress, TransportTestSupport.ServerAddressOf(world.Rig), world.Rig.Clock);
            var rogueConnId = rogueCarrier.ConnectToServer("virtual", 0);

            for (var i = 0; i < 5; i++)
            {
                world.Rig.Pump(5);
                rogueCarrier.Pump(world.Rig.Clock.NowMs);
            }

            // Forged secret
            var forgedSecretBytes = new byte[32];
            for (var i = 0; i < 32; i++) forgedSecretBytes[i] = 0xFF;
            var forgedSecret = new SessionSecret32(forgedSecretBytes);

            var rogueCoordinator = new ClientReconnectCoordinator();
            rogueCoordinator.CacheSession(client0.Endpoint.Session, client0.Endpoint.Match, client0.Endpoint.Player, in forgedSecret);
            rogueCoordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);

            var reqBuffer = new byte[ReconnectRequest.SizeBytes];
            Assert.That(rogueCoordinator.TryBuildReconnectRequest(reqBuffer, out var written), Is.True);
            rogueCarrier.Send(rogueConnId, TransportChannel.Control, reqBuffer, 0, written);

            var responseReceived = false;
            ReconnectResponse reconnectResponse = default;
            var elapsed = 0L;
            while (!responseReceived && elapsed < 5000)
            {
                world.Rig.Pump(5);
                rogueCarrier.Pump(world.Rig.Clock.NowMs);

                while (rogueCarrier.TryDequeueEvent(out var evt))
                {
                    if (evt.Type == TransportEventType.Message && evt.Length > 0 && evt.Data[0] == ReconnectWireCodec.ResponseOpcode)
                    {
                        Assert.That(ReconnectWireCodec.TryDecodeResponse(evt.Data.AsSpan(0, evt.Length), out reconnectResponse), Is.True);
                        responseReceived = true;
                        break;
                    }
                }
                elapsed += 5;
            }

            Assert.That(responseReceived, Is.True);
            Assert.That(reconnectResponse.Result, Is.EqualTo(ReconnectResult.InvalidSecret));

            rogueCoordinator.OnReconnectResponse(in reconnectResponse);
            Assert.That(rogueCoordinator.State, Is.EqualTo(ClientReconnectState.Terminated));

            // Verify the original session is still preserved in manager
            Assert.That(world.Rig.Host.Sessions.TryGetSession(client0.Endpoint.Session, out var sessionRec), Is.True);
            Assert.That(sessionRec.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(sessionRec.Secret, Is.EqualTo(correctSecret));
        }

        [Test]
        public void Reconnect_PacketLossStress_UnderFivePercentLoss()
        {
            var profile = new ImpairmentProfile
            {
                LossProbability = 0.05,
                LatencyMs = 2,
                Seed = 2026
            };

            var world = ReplicationIntegrationWorld.Create(
                profile, ServerReplicationEmitterConfig.Default, clientCount: 2);
            world.Rig.Host.AutoPauseOnDisconnect = false;
            world.StartMatch(new PlayerId(1), new PlayerId(2));

            var client0 = world.Clients[0];
            var coord0 = new ClientReconnectCoordinator(client0.Receiver);
            coord0.ResyncStarted += seq => client0.Bridge.PrepareForResync(seq);
            coord0.CacheSession(
                client0.Endpoint.Session,
                client0.Endpoint.Match,
                client0.Endpoint.Player,
                client0.Endpoint.Secret);

            // Advance 5 ticks
            for (var i = 0; i < 5; i++)
            {
                world.PumpTick();
            }

            // Involuntary disconnect
            Assert.That(world.Rig.ServerTransport.Binder.TryGetTokenBySession(client0.Endpoint.Session, out var token0), Is.True);
            Assert.That(world.Rig.ServerTransport.Binder.TryGetByToken(token0, out var binding0), Is.True);
            client0.Endpoint.DropConnection(TransportDisconnectReason.TransportLost);
            world.Rig.ServerCarrier.CloseConnection(binding0.ConnectionId, TransportDisconnectReason.TransportLost);
            for (var i = 0; i < 5; i++)
            {
                world.Pump(5);
            }

            coord0.OnTransportDisconnected(TransportDisconnectReason.TransportLost);

            // Connect reconnect socket
            var reconnectAddress = world.Rig.Pipe.CreateEndpoint();
            var reconnectCarrier = OwnDatagramCarrier.CreateClient(
                world.Rig.Pipe, reconnectAddress, TransportTestSupport.ServerAddressOf(world.Rig), world.Rig.Clock);
            var reconnectConnId = reconnectCarrier.ConnectToServer("virtual", 0);

            // Pump with generous budget until carrier connects
            for (var i = 0; i < 30; i++)
            {
                world.Rig.Pump(10);
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);
            }

            // Send ReconnectRequest over reliable Control channel
            var reqBuffer = new byte[ReconnectRequest.SizeBytes];
            Assert.That(coord0.TryBuildReconnectRequest(reqBuffer, out var written), Is.True);
            reconnectCarrier.Send(reconnectConnId, TransportChannel.Control, reqBuffer, 0, written);

            ReconnectTestUplink reconnectUplink = null;
            var responseReceived = false;
            ReconnectResponse reconnectResponse = default;
            var elapsed = 0L;
            var tickAcc = 0;

            while ((!responseReceived || !client0.Receiver.IsWorldUsable) && elapsed < 15000)
            {
                if (!responseReceived && elapsed > 0 && elapsed % 200 == 0)
                {
                    reconnectCarrier.Send(reconnectConnId, TransportChannel.Control, reqBuffer, 0, written);
                }

                world.Rig.Pump(10);
                reconnectCarrier.Pump(world.Rig.Clock.NowMs);
                client0.Bridge.Pump(world.Rig.Clock.NowMs);

                tickAcc += 10;
                if (tickAcc >= 50)
                {
                    tickAcc = 0;
                    world.Rig.Host.TickOnce();
                }

                while (reconnectCarrier.TryDequeueEvent(out var evt))
                {
                    if (evt.Type == TransportEventType.Message)
                    {
                        if (evt.Length > 0 && evt.Data[0] == ReconnectWireCodec.ResponseOpcode)
                        {
                            if (ReconnectWireCodec.TryDecodeResponse(evt.Data.AsSpan(0, evt.Length), out reconnectResponse))
                            {
                                responseReceived = true;
                                coord0.OnReconnectResponse(in reconnectResponse);

                                Assert.That(world.Rig.ServerTransport.Binder.TryGetTokenBySession(client0.Endpoint.Session, out var activeToken), Is.True);
                                reconnectCarrier.AssignSessionToken(reconnectConnId, activeToken);
                                reconnectUplink = new ReconnectTestUplink(reconnectCarrier, reconnectConnId, activeToken);
                                client0.Bridge.BindUplink(reconnectUplink);
                            }
                        }
                        else
                        {
                            var error = MessageCodec.TryDecodeHeader(evt.Data, 0, evt.Length, out var header);
                            if (error == MessageError.None && header.Type == TransportMessageType.Snapshot)
                            {
                                var payload = new byte[header.PayloadLength];
                                Array.Copy(evt.Data, header.PayloadOffset, payload, 0, header.PayloadLength);
                                if (reconnectUplink != null)
                                {
                                    reconnectUplink.RaiseSnapshotReceived(header.SnapshotTick, payload, header.PayloadLength);
                                }
                                else
                                {
                                    client0.Bridge.HandleSnapshotPayload(header.SnapshotTick, payload, header.PayloadLength, world.Rig.Clock.NowMs);
                                }
                            }
                        }
                    }
                }
                elapsed += 10;
            }

            Assert.That(responseReceived, Is.True, "Reconnect response must arrive within retry budget under 5% loss");
            Assert.That(reconnectResponse.Result, Is.EqualTo(ReconnectResult.Accepted));
            Assert.That(client0.Receiver.IsWorldUsable, Is.True);
            Assert.That(client0.Receiver.CurrentKeyframeSeq, Is.GreaterThanOrEqualTo(reconnectResponse.ActiveKeyframeSeq));

            coord0.OnKeyframeInstalled();
            coord0.AdvanceCountdown(5.0f, out var ready);
            Assert.That(ready, Is.True);
            Assert.That(coord0.State, Is.EqualTo(ClientReconnectState.Connected));
        }

        private sealed class ReconnectTestUplink : IReplicationUplink
        {
            private readonly INetworkCarrier _carrier;
            private readonly int _connectionId;
            private readonly ulong _token;
            private readonly byte[] _messageBuffer = new byte[TransportProtocol.MaxMessageBytes];

            public ReconnectTestUplink(INetworkCarrier carrier, int connectionId, ulong token)
            {
                _carrier = carrier;
                _connectionId = connectionId;
                _token = token;
            }

            public event Action<ulong, byte[], int> SnapshotPayloadReceived;

            public bool TrySendFeedback(ReplicationFeedbackKind kind, byte[] payload, int length)
            {
                if (payload == null || length <= 0)
                {
                    return false;
                }

                var type = kind == ReplicationFeedbackKind.Ack
                    ? TransportMessageType.SnapshotAck
                    : TransportMessageType.ReplicationRequest;
                var offset = MessageCodec.WriteHeader(_messageBuffer, 0, type, _token, 0);
                Array.Copy(payload, 0, _messageBuffer, offset, length);
                return _carrier.Send(_connectionId, TransportChannel.Control, _messageBuffer, 0, offset + length);
            }

            public void RaiseSnapshotReceived(ulong tick, byte[] buffer, int length)
            {
                SnapshotPayloadReceived?.Invoke(tick, buffer, length);
            }
        }
    }
}
