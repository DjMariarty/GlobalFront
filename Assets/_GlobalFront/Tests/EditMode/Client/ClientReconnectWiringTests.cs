using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode;
using NUnit.Framework;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.EditMode.Client
{
    /// <summary>
    /// Phase 4, step 4.5.1 (OD-18, OD-20): the wiring between the replication
    /// receiver, the C0 transport endpoint and the reconnection coordinator.
    ///
    /// The keyframe-installed signal is the only automatic trigger of the
    /// OD-20 countdown: these tests deliberately never call
    /// <see cref="ClientReconnectCoordinator.OnKeyframeInstalled"/> where the
    /// production path must do it, so a missing event subscription fails the
    /// assertion instead of being masked by test code.
    /// </summary>
    [TestFixture]
    public sealed class ClientReconnectWiringTests
    {
        private static readonly SessionId TestSession =
            new SessionId(new Guid("11111111-2222-3333-4444-555555555555"));

        private static readonly MatchId TestMatch =
            new MatchId(98765432101234UL);

        private static readonly PlayerId TestPlayer = new PlayerId(1);

        private static readonly SessionSecret32 TestSecret = CreateSecret();

        private static SessionSecret32 CreateSecret()
        {
            var bytes = new byte[SessionSecret32.SizeBytes];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)(0x40 + index);
            }

            return new SessionSecret32(bytes);
        }

        private sealed class ScriptedCarrier : INetworkCarrier
        {
            private readonly Queue<TransportEvent> _events = new Queue<TransportEvent>();

            public ScriptedCarrier(ITransportClock clock)
            {
                Clock = clock;
            }

            public bool AutoAccept = true;
            public bool AutoSendSecret = true;

            public TransportMetrics Metrics { get; } = new TransportMetrics();

            public ITransportClock Clock { get; }

            public int EventQueueDepth => _events.Count;

            public void StartServer()
            {
            }

            public int ConnectToServer(string host, int port)
            {
                Enqueue(new TransportEvent
                {
                    Type = TransportEventType.Connected,
                    ConnectionId = 1
                });
                return 1;
            }

            public bool Send(
                int connectionId,
                TransportChannel channel,
                byte[] data,
                int offset,
                int length)
            {
                if (MessageCodec.TryDecodeHeader(data, offset, length, out var header) !=
                    MessageError.None)
                {
                    return true;
                }

                switch (header.Type)
                {
                    case TransportMessageType.ConnectRequest:
                        if (AutoAccept)
                        {
                            EnqueueFramed(TransportMessageType.ConnectAccept, buffer =>
                            {
                                var write = MessageCodec.WriteHeader(
                                    buffer, 0, TransportMessageType.ConnectAccept,
                                    TransportProtocol.HandshakeToken, 0);
                                return MessageCodec.EncodeConnectAccept(
                                    buffer, write, new ConnectAcceptPayload
                                    {
                                        SessionToken = 42,
                                        Session = TestSession,
                                        Match = TestMatch,
                                        Player = TestPlayer
                                    });
                            });
                        }

                        if (AutoSendSecret)
                        {
                            EnqueueRaw(EncodeSecret());
                        }

                        return true;

                    case TransportMessageType.Ping:
                        return true;

                    default:
                        return true;
                }
            }

            public void CloseConnection(int connectionId, TransportDisconnectReason reason)
            {
            }

            public void AssignSessionToken(int connectionId, ulong token)
            {
            }

            public void Shutdown(TransportDisconnectReason reason)
            {
            }

            public void Pump(long nowMs)
            {
            }

            public bool TryDequeueEvent(out TransportEvent transportEvent)
            {
                if (_events.Count > 0)
                {
                    transportEvent = _events.Dequeue();
                    return true;
                }

                transportEvent = default;
                return false;
            }

            public void Dispose()
            {
            }

            public void EnqueueRaw(byte[] data)
            {
                Enqueue(new TransportEvent
                {
                    Type = TransportEventType.Message,
                    ConnectionId = 1,
                    Channel = TransportChannel.Control,
                    Data = data,
                    Length = data.Length
                });
            }

            public void EnqueueDisconnect(TransportDisconnectReason reason)
            {
                Enqueue(new TransportEvent
                {
                    Type = TransportEventType.Disconnected,
                    ConnectionId = 1,
                    Reason = reason
                });
            }

            private void EnqueueFramed(
                TransportMessageType type,
                Func<byte[], int> writePayload)
            {
                var buffer = new byte[128];
                var length = writePayload(buffer);
                var data = new byte[length];
                Array.Copy(buffer, data, length);
                EnqueueRaw(data);
            }

            private void Enqueue(TransportEvent transportEvent)
            {
                _events.Enqueue(transportEvent);
            }
        }

        private static DeltaAddRecord[] SingleUnit(ulong entity)
        {
            return new[]
            {
                new DeltaAddRecord(
                    new CoreEntityId(entity),
                    TestPlayer,
                    new WorldPointMm(1000, 2000),
                    100,
                    false,
                    default,
                    default,
                    false)
            };
        }

        private static byte[] EncodeSecret()
        {
            var buffer = new byte[ReconnectWireCodec.SecretMessageSizeBytes];
            Assert.That(
                ReconnectWireCodec.TryEncodeSecretMessage(
                    new SessionSecretMessage(TestSecret), buffer, out var written),
                Is.True);
            Assert.That(written, Is.EqualTo(ReconnectWireCodec.SecretMessageSizeBytes));
            return buffer;
        }

        private static byte[] EncodeResponse(in ReconnectResponse response)
        {
            var buffer = new byte[ReconnectWireCodec.ResponseSizeBytes];
            Assert.That(
                ReconnectWireCodec.TryEncodeResponse(in response, buffer, out var written),
                Is.True);
            Assert.That(written, Is.EqualTo(ReconnectWireCodec.ResponseSizeBytes));
            return buffer;
        }

        private static int CountCurrentThreadGcAllocations(Action action)
        {
            var recorder = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try
            {
                action();
            }
            finally
            {
                recorder.enabled = false;
                recorder.CollectFromAllThreads();
            }

            return recorder.sampleBlockCount;
        }

        [Test]
        public void KeyframeInstalled_EventFires_WithAuthoritativeTick()
        {
            var receiver = new ClientReplicationReceiver(8);
            var fired = 0;
            var observedTick = 0UL;
            receiver.KeyframeInstalled += tick =>
            {
                fired++;
                observedTick = tick;
            };

            var units = SingleUnit(1);
            var outcome = receiver.ReceiveKeyframe(1000, 1, 150UL, units);

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(fired, Is.EqualTo(1));
            Assert.That(observedTick, Is.EqualTo(150UL));

            // The signal is on the replication hot path: invocation must be
            // allocation-free even with a listener attached. The first call
            // above is the warmup; the measured window is the third install.
            Assert.That(receiver.ReceiveKeyframe(1016, 2, 151UL, units),
                Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            var allocations = CountCurrentThreadGcAllocations(
                () => receiver.ReceiveKeyframe(1032, 3, 152UL, units));
            Assert.That(allocations, Is.EqualTo(0));
        }

        [Test]
        public void Coordinator_AutoSubscribedToReceiver_TransitionsToCountdownOnKeyframe()
        {
            var receiver = new ClientReplicationReceiver(8);
            using var coordinator = new ClientReconnectCoordinator(receiver);

            var response = new ReconnectResponse(
                ReconnectResult.Accepted,
                TestPlayer,
                TestMatch,
                serverTick: 100UL,
                activeKeyframeSeq: 1);
            coordinator.OnReconnectResponse(in response);
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Resyncing));

            // The critical invariant: no manual OnKeyframeInstalled() call.
            var outcome = receiver.ReceiveKeyframe(200, 1, 150UL, SingleUnit(1));

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown));
            Assert.That(coordinator.CountdownRemainingSeconds,
                Is.EqualTo(ClientReconnectCoordinator.DefaultCountdownSeconds));
        }

        [Test]
        public void ClientTransportEndpoint_DecodesSecretMessage_AndRaisesSecretReceived()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock);
            var endpoint = new ClientTransportEndpoint(carrier);

            var receivedCount = 0;
            var receivedSecret = default(SessionSecret32);
            endpoint.SecretReceived += secret =>
            {
                receivedCount++;
                receivedSecret = secret;
            };

            carrier.EnqueueRaw(EncodeSecret());
            endpoint.Pump(0);

            Assert.That(receivedCount, Is.EqualTo(1));
            Assert.That(receivedSecret, Is.EqualTo(TestSecret));
            Assert.That(endpoint.Secret, Is.EqualTo(TestSecret));
        }

        [Test]
        public void ClientTransportEndpoint_DecodesReconnectResponse_AndRaisesEvent()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock);
            var endpoint = new ClientTransportEndpoint(carrier);

            var receivedCount = 0;
            var receivedResponse = default(ReconnectResponse);
            endpoint.ReconnectResponseReceived += response =>
            {
                receivedCount++;
                receivedResponse = response;
            };

            var assignedPlayer = new PlayerId(2);
            var response = new ReconnectResponse(
                ReconnectResult.Accepted,
                assignedPlayer,
                TestMatch,
                serverTick: 700UL,
                activeKeyframeSeq: 3);
            carrier.EnqueueRaw(EncodeResponse(in response));
            endpoint.Pump(0);

            Assert.That(receivedCount, Is.EqualTo(1));
            Assert.That(receivedResponse.Result, Is.EqualTo(ReconnectResult.Accepted));
            Assert.That(receivedResponse.ActiveKeyframeSeq, Is.EqualTo((ushort)3));
            Assert.That(receivedResponse.ServerTick, Is.EqualTo(700UL));
            Assert.That(endpoint.State, Is.EqualTo(ClientTransportState.Established));
            Assert.That(endpoint.Player, Is.EqualTo(assignedPlayer));
            Assert.That(endpoint.Match, Is.EqualTo(TestMatch));
        }

        [Test]
        public void RemoteSliceSession_ComposesCoordinator_AndCoordinatesReconnectFlow()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock);
            using var session = new RemoteSliceSession(carrier, clock);

            Assert.That(session.ReconnectCoordinator, Is.Not.Null);
            Assert.That(session.ReconnectCoordinator.Receiver, Is.SameAs(session.Receiver),
                "the coordinator must observe the session's own receiver");

            session.Connect("virtual", 0);
            session.Pump(0);

            Assert.That(session.IsConnected, Is.True);
            Assert.That(session.ReconnectCoordinator.State,
                Is.EqualTo(ClientReconnectState.Connected));
            Assert.That(session.ReconnectCoordinator.Secret, Is.EqualTo(TestSecret));
            Assert.That(session.ReconnectCoordinator.SessionId, Is.EqualTo(TestSession));

            carrier.EnqueueDisconnect(TransportDisconnectReason.TransportLost);
            session.Pump(10);

            Assert.That(session.ReconnectCoordinator.State,
                Is.EqualTo(ClientReconnectState.Reconnecting));

            var response = new ReconnectResponse(
                ReconnectResult.Accepted,
                TestPlayer,
                TestMatch,
                serverTick: 800UL,
                activeKeyframeSeq: 3);
            carrier.EnqueueRaw(EncodeResponse(in response));
            session.Pump(20);

            Assert.That(session.ReconnectCoordinator.State,
                Is.EqualTo(ClientReconnectState.Resyncing));
            Assert.That(session.ReconnectCoordinator.ActiveKeyframeSeq, Is.EqualTo((ushort)3));
            Assert.That(session.Receiver.IsWorldUsable, Is.False);
            Assert.That(session.Bridge.AssembledKeyframeSeq, Is.EqualTo((ushort)2),
                "the bridge must be primed below the incoming keyframe generation");
        }

        [Test]
        public void Coordinator_Dispose_UnsubscribesFromReceiver_Cleanly()
        {
            var receiver = new ClientReplicationReceiver(8);
            var coordinator = new ClientReconnectCoordinator(receiver);
            var stateChanges = 0;
            coordinator.StateChanged += _ => stateChanges++;

            coordinator.Dispose();

            var outcome = receiver.ReceiveKeyframe(300, 1, 150UL, SingleUnit(1));

            Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Idle));
            Assert.That(stateChanges, Is.EqualTo(0));
        }
    }
}
