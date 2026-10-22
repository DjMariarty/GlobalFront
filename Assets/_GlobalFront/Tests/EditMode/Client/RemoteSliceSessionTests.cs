using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Model;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Client
{
    /// <summary>
    /// Phase 4, step 4.2 (OD-38, OD-47): the client-side network session.
    ///
    /// The fixture drives the session over a scripted carrier rather than a
    /// socket, because three of the claims are about exact timing — handshake
    /// completion, the one-hertz ping cadence and the RTT arithmetic — and a
    /// real socket would make every one of them a race. The carrier is
    /// still a full protocol participant: it accepts the handshake and
    /// echoes Pings with the project codecs, so the session exercises the
    /// same wire path the LiteNetLib carrier uses.
    /// </summary>
    [TestFixture]
    public sealed class RemoteSliceSessionTests
    {
        private sealed class ScriptedCarrier : INetworkCarrier
        {
            private static readonly SessionId TestSession =
                new SessionId(new Guid("11111111-2222-3333-4444-555555555555"));

            private static readonly MatchId TestMatch =
                new MatchId(new Guid("66666666-7777-8888-9999-aaaaaaaaaaaa"));

            private readonly Queue<TransportEvent> _events = new Queue<TransportEvent>();
            private readonly ITransportClock _clock;
            private ulong _token;

            public ScriptedCarrier(ITransportClock clock)
            {
                _clock = clock;
            }

            public bool AutoAccept = true;

            public bool DenyConnect;

            public bool AutoPong = true;

            public readonly List<long> PingTimestamps = new List<long>();

            public int PingCount => PingTimestamps.Count;

            public TransportMetrics Metrics { get; } = new TransportMetrics();

            public ITransportClock Clock => _clock;

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
                    return false;
                }

                switch (header.Type)
                {
                    case TransportMessageType.ConnectRequest:
                        if (DenyConnect)
                        {
                            EnqueueMessage(buffer =>
                            {
                                var write = MessageCodec.WriteHeader(
                                    buffer, 0, TransportMessageType.ConnectDenied,
                                    TransportProtocol.HandshakeToken, 0);
                                return MessageCodec.EncodeConnectDenied(
                                    buffer, write, ConnectDenyReason.ServerFull);
                            });
                        }
                        else if (AutoAccept)
                        {
                            EnqueueMessage(buffer =>
                            {
                                var write = MessageCodec.WriteHeader(
                                    buffer, 0, TransportMessageType.ConnectAccept,
                                    TransportProtocol.HandshakeToken, 0);
                                return MessageCodec.EncodeConnectAccept(
                                    buffer,
                                    write,
                                    new ConnectAcceptPayload
                                    {
                                        SessionToken = 42,
                                        Session = TestSession,
                                        Match = TestMatch,
                                        Player = new PlayerId(1)
                                    });
                            });
                        }

                        return true;

                    case TransportMessageType.Ping:
                        if (MessageCodec.TryDecodePingPong(
                                data,
                                header.PayloadOffset,
                                length,
                                out var timestamp))
                        {
                            PingTimestamps.Add(timestamp);
                            if (AutoPong)
                            {
                                EnqueueMessage(buffer =>
                                {
                                    var write = MessageCodec.WriteHeader(
                                        buffer, 0, TransportMessageType.Pong, _token, 0);
                                    return MessageCodec.EncodePingPong(
                                        buffer, write, timestamp);
                                });
                            }
                        }

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
                _token = token;
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

            public void EnqueueDisconnect(TransportDisconnectReason reason)
            {
                Enqueue(new TransportEvent
                {
                    Type = TransportEventType.Disconnected,
                    ConnectionId = 1,
                    Reason = reason
                });
            }

            public void Dispose()
            {
            }

            private void EnqueueMessage(Func<byte[], int> writePayload)
            {
                var buffer = new byte[64];
                var length = writePayload(buffer);
                var data = new byte[length];
                Array.Copy(buffer, data, length);
                Enqueue(new TransportEvent
                {
                    Type = TransportEventType.Message,
                    ConnectionId = 1,
                    Channel = TransportChannel.Control,
                    Data = data,
                    Length = length
                });
            }

            private void Enqueue(TransportEvent transportEvent)
            {
                _events.Enqueue(transportEvent);
            }
        }

        private static readonly PlayerId ExpectedPlayer = new PlayerId(1);

        /// <summary>
        /// Pumps the session at <paramref name="nowMs"/> with the virtual clock
        /// advanced to the same instant: RTT is measured on the injected
        /// clock, exactly as the production frame loop hands it the same
        /// value it passes to <see cref="RemoteSliceSession.Pump"/>.
        /// </summary>
        private static void PumpAt(
            VirtualTransportClock clock,
            RemoteSliceSession session,
            long nowMs)
        {
            if (nowMs > clock.NowMs)
            {
                clock.Advance(nowMs - clock.NowMs);
            }

            session.Pump(nowMs);
        }

        [Test]
        public void Connect_CompletesHandshake_AndRaisesConnected()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock);
            using var session = new RemoteSliceSession(carrier, clock);

            var connectedCount = 0;
            session.Connected += () => connectedCount++;

            session.Connect("127.0.0.1", 7777);
            Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Connecting));

            PumpAt(clock, session, 0);

            Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Established),
                "the accept must complete the handshake");
            Assert.That(session.IsConnected, Is.True);
            Assert.That(connectedCount, Is.EqualTo(1),
                "Connected is raised exactly once when the server accepts");
            Assert.That(session.Endpoint.Player, Is.EqualTo(ExpectedPlayer),
                "the session carries the server-assigned identity");
        }

        [Test]
        public void PingCadence_IsOneHertz_OnAnEstablishedConnection()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock);
            using var session = new RemoteSliceSession(carrier, clock);
            session.Connect("127.0.0.1", 7777);

            PumpAt(clock, session, 0);
            Assert.That(carrier.PingCount, Is.EqualTo(1),
                "the first ping goes out as soon as the connection is established");

            for (var now = 100L; now < 1000L; now += 100L)
            {
                PumpAt(clock, session, now);
            }

            Assert.That(carrier.PingCount, Is.EqualTo(1),
                "no second ping may leave before the 1000 ms boundary");

            PumpAt(clock, session, 1000);
            Assert.That(carrier.PingCount, Is.EqualTo(2),
                "the second ping is due at 1000 ms");

            PumpAt(clock, session, 1999);
            Assert.That(carrier.PingCount, Is.EqualTo(2));

            PumpAt(clock, session, 2000);
            Assert.That(carrier.PingCount, Is.EqualTo(3));
            Assert.That(carrier.PingTimestamps,
                Is.EqualTo(new[] { 0L, 1000L, 2000L }),
                "the timestamp is the client clock value at send time");
        }

        [Test]
        public void Pong_ComputesRttFromTheClientClock_AndAppliesEwma()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock);
            using var session = new RemoteSliceSession(carrier, clock);

            var published = new List<int>();
            session.RttUpdated += published.Add;

            session.Connect("127.0.0.1", 7777);
            PumpAt(clock, session, 0);

            PumpAt(clock, session, 40);
            Assert.That(session.RttSampleCount, Is.EqualTo(1));
            Assert.That(session.SmoothedRttMs, Is.EqualTo(40),
                "the first sample is the raw client-clock round trip");

            PumpAt(clock, session, 1000);
            PumpAt(clock, session, 1080);
            Assert.That(session.RttSampleCount, Is.EqualTo(2));
            Assert.That(session.SmoothedRttMs, Is.EqualTo(45),
                "0.875 * 40 + 0.125 * 80 must smooth to 45");
            Assert.That(published,
                Is.EqualTo(new[] { 40, 45 }),
                "every accepted sample publishes the smoothed value");
        }

        [Test]
        public void TransportLoss_RaisesDisconnected_WithTheCarrierReason()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock);
            using var session = new RemoteSliceSession(carrier, clock);
            session.Connect("127.0.0.1", 7777);
            PumpAt(clock, session, 0);

            TransportDisconnectReason? observed = null;
            session.Disconnected += reason => observed = reason;

            carrier.EnqueueDisconnect(TransportDisconnectReason.Timeout);
            PumpAt(clock, session, 10);

            Assert.That(observed, Is.EqualTo(TransportDisconnectReason.Timeout),
                "a transport loss must surface through Disconnected");
            Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected));
        }

        [Test]
        public void DeniedHandshake_RaisesDisconnected_AsRejected()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock) { DenyConnect = true };
            using var session = new RemoteSliceSession(carrier, clock);

            TransportDisconnectReason? observed = null;
            session.Disconnected += reason => observed = reason;

            session.Connect("127.0.0.1", 7777);
            PumpAt(clock, session, 0);

            Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected));
            Assert.That(observed, Is.EqualTo(TransportDisconnectReason.Rejected),
                "a denied handshake is a Rejected disconnect, not a silent idle");
        }

        [Test]
        public void Ping_IsNotSentWhileTheHandshakeIsIncomplete()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock) { AutoAccept = false };
            using var session = new RemoteSliceSession(carrier, clock);
            session.Connect("127.0.0.1", 7777);

            for (var now = 0L; now <= 3000L; now += 100L)
            {
                PumpAt(clock, session, now);
            }

            Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Connecting));
            Assert.That(carrier.PingCount, Is.EqualTo(0),
                "there is no session token to attribute a ping before the handshake");
        }
    }
}
