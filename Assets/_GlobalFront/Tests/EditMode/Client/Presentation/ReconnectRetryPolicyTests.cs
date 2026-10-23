#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Model;
using GlobalFront.Core.Reconnect;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode;
using NUnit.Framework;
using Is = NUnit.Framework.Is;
using ReconnectResult = GlobalFront.Core.Reconnect.ReconnectResult;

namespace GlobalFront.Tests.EditMode.Client.Presentation
{
    /// <summary>
    /// Step 4.6.1 (OD-43 preparation): the reconnect retry policy and the
    /// socket-connect timeout, driven on a virtual clock with a carrier whose
    /// connect never completes. These are the two mechanics a 1%/5%-loss
    /// profile needs before any impairment harness can rely on reconnection.
    /// </summary>
    [TestFixture]
    public sealed class ReconnectRetryPolicyTests
    {
        [Test]
        public void Session_AutoRetriesReconnect_AtConfiguredInterval()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock);
            var session = new RemoteSliceSession(carrier, clock);

            try
            {
                session.ReconnectRetryIntervalMs = 500;
                session.Endpoint.ConnectTimeoutMs = 100;

                BeginLostSession(session, carrier, 0);

                Assert.That(session.ReconnectAttemptCount, Is.EqualTo(1),
                    "the first attempt must run on the pump that observes the loss");
                Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Connecting));

                session.Pump(250);
                Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected),
                    "the short connect timeout must release the hung socket");
                Assert.That(session.ReconnectAttemptCount, Is.EqualTo(1),
                    "half the interval must not start another attempt");

                session.Pump(500);
                Assert.That(session.ReconnectAttemptCount, Is.EqualTo(2),
                    "the configured interval must start the next attempt");
                Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Connecting));
                Assert.That(carrier.ConnectionsOpened, Is.EqualTo(3),
                    "initial socket + two reconnect sockets");
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void Endpoint_ConnectingTimeout_ResetsToDisconnected_AllowingNextRetry()
        {
            var clock = new VirtualTransportClock();
            var carrier = new ScriptedCarrier(clock);
            var session = new RemoteSliceSession(carrier, clock);

            try
            {
                session.ReconnectRetryIntervalMs = 500;
                Assert.That(session.Endpoint.ConnectTimeoutMs, Is.EqualTo(2000));

                BeginLostSession(session, carrier, 0);
                Assert.That(session.ReconnectAttemptCount, Is.EqualTo(1));

                session.Pump(1999);
                Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Connecting),
                    "the socket must survive just below the timeout");
                Assert.That(session.ReconnectAttemptCount, Is.EqualTo(1),
                    "a Connecting socket must block further attempts");

                session.Endpoint.Pump(2000);
                Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Disconnected),
                    "a lost SYN must not pin the endpoint in Connecting");

                session.Pump(2000);
                Assert.That(session.ReconnectAttemptCount, Is.EqualTo(2),
                    "the retry policy must re-arm the socket after the timeout");
                Assert.That(session.Endpoint.State, Is.EqualTo(ClientTransportState.Connecting));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void Session_AcceptedOrTerminated_StopsRetrying()
        {
            var acceptedClock = new VirtualTransportClock();
            var acceptedCarrier = new ScriptedCarrier(acceptedClock);
            var accepted = new RemoteSliceSession(acceptedCarrier, acceptedClock);
            try
            {
                BeginLostSession(accepted, acceptedCarrier, 0);
                Assert.That(accepted.ReconnectAttemptCount, Is.EqualTo(1));

                acceptedCarrier.EnqueueResponse(new ReconnectResponse(
                    ReconnectResult.Accepted,
                    new PlayerId(1),
                    new MatchId(1),
                    serverTick: 10,
                    activeKeyframeSeq: 2,
                    token: 0x1234));
                accepted.Pump(10);

                Assert.That(accepted.Endpoint.State, Is.EqualTo(ClientTransportState.Established));
                Assert.That(accepted.Endpoint.Token, Is.EqualTo(0x1234ul));
                Assert.That(accepted.ReconnectCoordinator.State,
                    Is.EqualTo(ClientReconnectState.Resyncing));
                Assert.That(accepted.ReconnectAttemptCount, Is.EqualTo(1));

                accepted.Pump(1000);
                accepted.Pump(5000);
                Assert.That(accepted.ReconnectAttemptCount, Is.EqualTo(1),
                    "an accepted response must freeze the retry policy");
                Assert.That(accepted.TryReconnect(), Is.False);
            }
            finally
            {
                accepted.Dispose();
            }

            var deniedClock = new VirtualTransportClock();
            var deniedCarrier = new ScriptedCarrier(deniedClock);
            var denied = new RemoteSliceSession(deniedCarrier, deniedClock);
            try
            {
                BeginLostSession(denied, deniedCarrier, 0);
                Assert.That(denied.ReconnectAttemptCount, Is.EqualTo(1));

                deniedCarrier.EnqueueResponse(new ReconnectResponse(
                    ReconnectResult.GraceExpired,
                    default,
                    default(MatchId),
                    serverTick: 0,
                    activeKeyframeSeq: 0));
                denied.Pump(10);
                Assert.That(denied.ReconnectCoordinator.State,
                    Is.EqualTo(ClientReconnectState.Terminated));

                denied.Pump(1000);
                denied.Pump(5000);
                Assert.That(denied.ReconnectAttemptCount, Is.EqualTo(1),
                    "a terminal response must freeze the retry policy");
                Assert.That(denied.TryReconnect(), Is.False);
            }
            finally
            {
                denied.Dispose();
            }
        }

        private static void BeginLostSession(
            RemoteSliceSession session,
            ScriptedCarrier carrier,
            long nowMs)
        {
            session.Connect("virtual", 0);
            carrier.EnqueueDisconnected();
            session.Pump(nowMs);
        }

        /// <summary>
        /// Carrier seam with manual events: <see cref="ConnectToServer"/> never
        /// completes (a lost SYN), and tests enqueue the only events the
        /// endpoint may see. No socket, no thread, no wall clock.
        /// </summary>
        private sealed class ScriptedCarrier : INetworkCarrier
        {
            private readonly TransportMetrics _metrics = new TransportMetrics();
            private readonly ITransportClock _clock;
            private readonly Queue<TransportEvent> _events = new Queue<TransportEvent>();
            private int _nextConnection = 1;

            public ScriptedCarrier(ITransportClock clock)
            {
                _clock = clock;
            }

            public TransportMetrics Metrics => _metrics;

            public ITransportClock Clock => _clock;

            public int ConnectionsOpened { get; private set; }

            public void StartServer()
            {
            }

            public int ConnectToServer(string host, int port)
            {
                ConnectionsOpened++;
                return _nextConnection++;
            }

            public bool Send(int connectionId, TransportChannel channel, byte[] data, int offset, int length) =>
                true;

            public void CloseConnection(int connectionId, TransportDisconnectReason reason) =>
                EnqueueDisconnected(connectionId, reason);

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

            public int EventQueueDepth => _events.Count;

            public void Dispose()
            {
            }

            public void EnqueueDisconnected() =>
                EnqueueDisconnected(1, TransportDisconnectReason.TransportLost);

            public void EnqueueResponse(ReconnectResponse response)
            {
                var buffer = new byte[ReconnectWireCodec.ResponseSizeBytes];
                Assert.That(ReconnectWireCodec.TryEncodeResponse(response, buffer, out var written), Is.True);
                _events.Enqueue(new TransportEvent
                {
                    Type = TransportEventType.Message,
                    ConnectionId = 1,
                    Channel = TransportChannel.Control,
                    Data = buffer,
                    Length = written
                });
            }

            private void EnqueueDisconnected(int connectionId, TransportDisconnectReason reason) =>
                _events.Enqueue(new TransportEvent
                {
                    Type = TransportEventType.Disconnected,
                    ConnectionId = connectionId,
                    Reason = reason
                });
        }
    }
}
