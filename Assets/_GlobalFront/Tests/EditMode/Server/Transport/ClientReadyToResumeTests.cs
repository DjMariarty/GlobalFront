using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Model;
using GlobalFront.Core.Reconnect;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using CoreReconnectResult = GlobalFront.Core.Reconnect.ReconnectResult;
using Is = NUnit.Framework.Is;
using ServerReconnectResult = GlobalFront.Server.Sessions.ReconnectResult;

namespace GlobalFront.Tests.EditMode.Server.Transport
{
    /// <summary>
    /// OD-20 dedicated resume protocol: the standalone C0
    /// ClientReadyToResume frame must round-trip the Guid SessionId, reach the
    /// host only from the socket bound to that session, and leave spoofed or
    /// malformed frames without any resume signal.
    /// </summary>
    [TestFixture]
    public sealed class ClientReadyToResumeTests
    {
        private static readonly SessionId SessionA = new SessionId(new Guid(
            0x01020304, 0x0506, 0x0708,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10));

        private static readonly SessionId OtherSession = new SessionId(new Guid(
            0x11121314, 0x1516, 0x1718,
            0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20));

        [Test]
        public void Codec_RoundTripsGuidSession_WithPinnedLayout()
        {
            var buffer = new byte[ClientReadyToResumeCodec.SizeBytes];

            Assert.That(ClientReadyToResumeCodec.TryEncode(SessionA, buffer, out var written), Is.True);
            Assert.That(written, Is.EqualTo(25));
            Assert.That(ClientReadyToResumeCodec.SizeBytes, Is.EqualTo(25),
                "SessionId is Guid-backed: 1 opcode + 1 version + 16 id + 7 reserved");
            Assert.That(buffer[0], Is.EqualTo((byte)TransportMessageType.ClientReadyToResume));
            Assert.That(buffer[0], Is.EqualTo(ClientReadyToResumeCodec.Opcode));
            Assert.That(buffer[1], Is.EqualTo(ClientReadyToResumeCodec.ProtocolVersion));

            var idBytes = new byte[ClientReadyToResumeCodec.SessionIdSizeBytes];
            Array.Copy(buffer, ClientReadyToResumeCodec.SessionIdOffset, idBytes, 0, idBytes.Length);
            Assert.That(new Guid(idBytes), Is.EqualTo(SessionA.Value),
                "the frame must carry the full 16-byte SessionId");

            for (var index = ClientReadyToResumeCodec.ReservedOffset;
                 index < ClientReadyToResumeCodec.SizeBytes;
                 index++)
            {
                Assert.That(buffer[index], Is.Zero, $"reserved byte {index} must be zero");
            }

            Assert.That(ClientReadyToResumeCodec.TryDecode(buffer, out var decoded), Is.True);
            Assert.That(decoded, Is.EqualTo(SessionA));
        }

        [Test]
        public void Codec_RejectsWrongSizeOpcodeAndVersion()
        {
            var buffer = new byte[ClientReadyToResumeCodec.SizeBytes];
            Assert.That(ClientReadyToResumeCodec.TryEncode(SessionA, buffer, out _), Is.True);

            Assert.That(
                ClientReadyToResumeCodec.TryDecode(
                    new ReadOnlySpan<byte>(buffer, 0, ClientReadyToResumeCodec.SizeBytes - 1), out _),
                Is.False,
                "a short frame must be rejected");

            var longer = new byte[ClientReadyToResumeCodec.SizeBytes + 1];
            Array.Copy(buffer, longer, buffer.Length);
            Assert.That(ClientReadyToResumeCodec.TryDecode(longer, out _), Is.False,
                "a frame with trailing bytes must be rejected");

            var wrongOpcode = (byte[])buffer.Clone();
            wrongOpcode[0] = ClientReadyToResumeCodec.Opcode + 1;
            Assert.That(ClientReadyToResumeCodec.TryDecode(wrongOpcode, out _), Is.False);

            var wrongVersion = (byte[])buffer.Clone();
            wrongVersion[1] = ClientReadyToResumeCodec.ProtocolVersion + 1;
            Assert.That(ClientReadyToResumeCodec.TryDecode(wrongVersion, out _), Is.False);
        }

        [Test]
        public void Codec_EncodeAndDecode_AllocateZeroGC()
        {
            var buffer = new byte[ClientReadyToResumeCodec.SizeBytes];

            Assert.That(() =>
            {
                ClientReadyToResumeCodec.TryEncode(SessionA, buffer, out _);
            }, Is.Not.AllocatingGCMemory());

            Assert.That(() =>
            {
                ClientReadyToResumeCodec.TryDecode(buffer, out _);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Client_ReadySignal_RequiresEstablishedSession()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);

            Assert.That(client.State, Is.EqualTo(ClientTransportState.Connecting));
            Assert.That(client.TrySendReadyToResume(), Is.False,
                "a handshaking endpoint has no bound session to signal");
        }

        [Test]
        public void BoundSession_ReadySignal_ReachesHostExactlyOnce()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            var received = new List<SessionId>();
            rig.ServerTransport.ClientReadyToResumeReceived += received.Add;

            Assert.That(client.TrySendReadyToResume(), Is.True,
                "an established session must encode and queue the signal");
            PumpBoth(rig, client, 100);

            Assert.That(received, Has.Count.EqualTo(1),
                "the server must raise exactly one resume signal");
            Assert.That(received[0], Is.EqualTo(client.Session));
        }

        [Test]
        public void SpoofedSessionId_OnBoundSocket_IsDropped_WithoutSignal()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            Assert.That(rig.ServerTransport.Binder.TryGetTokenBySession(
                client.Session, out var token), Is.True);
            Assert.That(rig.ServerTransport.Binder.TryGetByToken(token, out var binding), Is.True);

            var received = 0;
            rig.ServerTransport.ClientReadyToResumeReceived += _ => received++;

            var frame = new byte[ClientReadyToResumeCodec.SizeBytes];
            Assert.That(ClientReadyToResumeCodec.TryEncode(OtherSession, frame, out _), Is.True);

            var droppedBefore = rig.ServerTransport.Metrics.DroppedBadToken;
            rig.ServerTransport.HandleMessage(new TransportEvent
            {
                Type = TransportEventType.Message,
                ConnectionId = binding.ConnectionId,
                Channel = TransportChannel.Control,
                Data = frame,
                Length = frame.Length
            });

            Assert.That(received, Is.Zero,
                "a frame naming another session must not raise the resume event");
            Assert.That(rig.ServerTransport.Metrics.DroppedBadToken,
                Is.EqualTo(droppedBefore + 1),
                "the spoof must be counted as a bad token");
        }

        [Test]
        public void UnboundConnection_IsDropped_WithoutSignal()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            var received = 0;
            rig.ServerTransport.ClientReadyToResumeReceived += _ => received++;

            var frame = new byte[ClientReadyToResumeCodec.SizeBytes];
            Assert.That(ClientReadyToResumeCodec.TryEncode(client.Session, frame, out _), Is.True);

            var droppedBefore = rig.ServerTransport.Metrics.DroppedBadToken;
            rig.ServerTransport.HandleMessage(new TransportEvent
            {
                Type = TransportEventType.Message,
                ConnectionId = int.MaxValue,
                Channel = TransportChannel.Control,
                Data = frame,
                Length = frame.Length
            });

            Assert.That(received, Is.Zero,
                "a socket without a session binding must not resume the match");
            Assert.That(rig.ServerTransport.Metrics.DroppedBadToken,
                Is.EqualTo(droppedBefore + 1));
        }

        [Test]
        public void WrongSizedFrame_IsDroppedAsMalformed()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            Assert.That(rig.ServerTransport.Binder.TryGetTokenBySession(
                client.Session, out var token), Is.True);
            Assert.That(rig.ServerTransport.Binder.TryGetByToken(token, out var binding), Is.True);

            var received = 0;
            rig.ServerTransport.ClientReadyToResumeReceived += _ => received++;

            // The historical 17-byte sketch cannot carry a Guid SessionId and
            // must be rejected, not parsed from a truncated frame.
            var shortFrame = new byte[17];
            shortFrame[0] = ClientReadyToResumeCodec.Opcode;
            shortFrame[1] = ClientReadyToResumeCodec.ProtocolVersion;

            var malformedBefore = rig.ServerTransport.Metrics.DroppedMalformed;
            rig.ServerTransport.HandleMessage(new TransportEvent
            {
                Type = TransportEventType.Message,
                ConnectionId = binding.ConnectionId,
                Channel = TransportChannel.Control,
                Data = shortFrame,
                Length = shortFrame.Length
            });

            Assert.That(received, Is.Zero);
            Assert.That(rig.ServerTransport.Metrics.DroppedMalformed,
                Is.EqualTo(malformedBefore + 1),
                "a fixed-size signal with the wrong length is malformed");
        }

        [Test]
        public void Host_KeepsPauseWhileAnotherSessionIsInGrace()
        {
            var host = new ServerHost(20);
            var transport = new ResumeTransport();
            host.AttachReplication(transport);

            var match = host.CreateSessionMatch(2);
            var sessionA = host.CreateSession(host.CreateConnectionHandle());
            var sessionB = host.CreateSession(host.CreateConnectionHandle());
            Assert.That(host.TryJoinMatch(sessionA, match, out _), Is.EqualTo(JoinResult.Assigned));
            Assert.That(host.TryJoinMatch(sessionB, match, out _), Is.EqualTo(JoinResult.Assigned));

            Assert.That(host.NotifyConnectionLost(sessionB, 0), Is.True);
            Assert.That(host.IsPaused, Is.True,
                "a lost session must pause the host");

            transport.Reattach(sessionA);
            Assert.That(host.AwaitingResumeCount, Is.EqualTo(1),
                "a reattached session must be marked as awaiting resume");
            host.OnClientReadyToResume(sessionA);
            Assert.That(host.IsPaused, Is.True,
                "an early signal must not strand the session still in grace");
            Assert.That(host.AwaitingResumeCount, Is.Zero,
                "the signal is consumed even though the pause remains");

            var rebound = host.CreateConnectionHandle();
            Assert.That(
                host.TryReconnectSession(sessionB, rebound, 1, out _),
                Is.EqualTo(ServerReconnectResult.Reconnected));

            transport.Reattach(sessionA);
            transport.Reattach(sessionB);
            host.OnClientReadyToResume(sessionA);
            Assert.That(host.IsPaused, Is.True,
                "B must still signal before the pause lifts");
            host.OnClientReadyToResume(sessionB);
            Assert.That(host.IsPaused, Is.False,
                "grace is empty and every reattached session has signaled");
        }

        [Test]
        public void Host_ReadyFromSessionWithoutReattach_DoesNotResume()
        {
            var host = new ServerHost(20);
            host.AttachReplication(new ResumeTransport());
            host.Pause();

            host.OnClientReadyToResume(new SessionId(Guid.NewGuid()));
            Assert.That(host.IsPaused, Is.True,
                "an unsolicited ready signal must not resume the host");
        }

        [Test]
        public void Host_ReattachedSessionWithoutReady_IsClosedAfterDeadline()
        {
            var host = new ServerHost(20);
            var transport = new ResumeTransport();
            host.AttachReplication(transport);
            host.Pause();

            var session = host.CreateSession(host.CreateConnectionHandle());
            transport.Reattach(session);
            Assert.That(host.AwaitingResumeCount, Is.EqualTo(1));

            host.AdvancePauseTicks(
                (ulong)(ServerHost.DefaultResumeReadyTimeoutTicks - 1));
            Assert.That(host.IsPaused, Is.True,
                "one tick short of the deadline must not end the wait");
            Assert.That(host.AwaitingResumeCount, Is.EqualTo(1));

            host.AdvancePauseTicks(1);

            Assert.That(
                host.AwaitingResumeCount, Is.Zero,
                "a silent reattached session must not hold the pause forever (P1-05)");
            Assert.That(
                host.IsPaused, Is.False,
                "the forced close must release the pause when nothing else is in grace");
            Assert.That(host.TryGetSession(session, out var record), Is.True);
            Assert.That(record.State, Is.EqualTo(SessionState.Closed),
                "the stalled session is dropped, not left half-bound");
        }

        [Test]
        public void Host_ClosingAnotherSession_DoesNotResumeWhileOneStillAwaitsReady()
        {
            var host = new ServerHost(20);
            var transport = new ResumeTransport();
            host.AttachReplication(transport);

            var match = host.CreateSessionMatch(2);
            var sessionA = host.CreateSession(host.CreateConnectionHandle());
            var sessionB = host.CreateSession(host.CreateConnectionHandle());
            Assert.That(host.TryJoinMatch(sessionA, match, out _), Is.EqualTo(JoinResult.Assigned));
            Assert.That(host.TryJoinMatch(sessionB, match, out _), Is.EqualTo(JoinResult.Assigned));

            Assert.That(host.NotifyConnectionLost(sessionB, 0), Is.True);
            Assert.That(host.IsPaused, Is.True);

            transport.Reattach(sessionA);
            Assert.That(host.AwaitingResumeCount, Is.EqualTo(1));

            Assert.That(
                host.CloseSession(sessionB), Is.True,
                "the other session leaves the match while A is still resyncing");

            Assert.That(
                host.IsPaused, Is.True,
                "B's exit must not lift the pause while A still owes its ready signal (P2-06)");
            Assert.That(host.AwaitingResumeCount, Is.EqualTo(1));

            host.OnClientReadyToResume(sessionA);
            Assert.That(host.IsPaused, Is.False,
                "once A signals and nothing is in grace, the pause lifts");
        }

        [Test]
        public void Host_DetachClearsAwaitingResume()
        {
            var host = new ServerHost(20);
            var transport = new ResumeTransport();
            host.AttachReplication(transport);
            host.Pause();

            var session = new SessionId(Guid.NewGuid());
            transport.Reattach(session);
            Assert.That(host.AwaitingResumeCount, Is.EqualTo(1));

            transport.Detach(session, TransportDisconnectReason.TransportLost);
            Assert.That(host.AwaitingResumeCount, Is.Zero,
                "a detached session must not keep the resume latched");
        }

        [Test]
        public void Session_PumpTicksCountdownByElapsedTime()
        {
            var established = CreateEstablishedSession(failFirstReadySend: false);
            try
            {
                DriveToCountdown(established.Session);
                var coordinator = established.Session.ReconnectCoordinator;
                var remaining = coordinator.CountdownRemainingSeconds;

                // The first Countdown pump only anchors the session clock.
                established.Pump(5);
                Assert.That(coordinator.CountdownRemainingSeconds, Is.EqualTo(remaining),
                    "the first Countdown pump must not move the countdown");

                for (var index = 0; index < 20; index++)
                {
                    established.Pump(100);
                }

                Assert.That(coordinator.CountdownRemainingSeconds,
                    Is.EqualTo(remaining - 2f).Within(0.001f),
                    "Pump must advance the countdown by the elapsed session time");

                for (var index = 0; index < 30; index++)
                {
                    established.Pump(100);
                }

                Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Connected),
                    "five seconds of session pumps must finish the countdown");
            }
            finally
            {
                established.Session.Dispose();
            }
        }

        [Test]
        public void Session_ReadySignalRetriesAfterFailedSend()
        {
            var established = CreateEstablishedSession(failFirstReadySend: true);
            try
            {
                var received = 0;
                established.Rig.ServerTransport.ClientReadyToResumeReceived += _ => received++;
                DriveToCountdown(established.Session);

                // Anchor the clock, then let five seconds of real session
                // pumps complete the countdown.
                established.Pump(5);
                for (var index = 0; index < 50; index++)
                {
                    established.Pump(100);
                }

                Assert.That(established.Carrier.ReadySendAttempts, Is.EqualTo(1));
                Assert.That(established.Carrier.FailedReadySends, Is.EqualTo(1));
                Assert.That(established.Session.HasPendingReadyToResume, Is.True,
                    "a failed local send must keep the resume obligation latched");
                Assert.That(received, Is.Zero);

                established.Pump(5);
                established.Pump(5);

                Assert.That(established.Carrier.ReadySendAttempts, Is.EqualTo(2),
                    "the next frame must retry the failed send");
                Assert.That(established.Session.HasPendingReadyToResume, Is.False);
                Assert.That(received, Is.EqualTo(1),
                    "the retried frame must reach the host exactly once");

                established.Pump(5);
                Assert.That(received, Is.EqualTo(1),
                    "a completed send must not repeat the signal");
            }
            finally
            {
                established.Session.Dispose();
            }
        }

        private sealed class ResumeTransport : IReplicationTransport
        {
            public event Action<SessionId, MatchId, PlayerId> SessionAttached
            {
                add { }
                remove { }
            }

            public event Action<SessionId, MatchId, PlayerId> SessionReattached;

            public event Action<SessionId> PostSessionReattached
            {
                add { }
                remove { }
            }

            public event Action<SessionId, TransportDisconnectReason> SessionDetached;

            public event Action<ReplicationFeedbackMessage> FeedbackReceived
            {
                add { }
                remove { }
            }

            public bool SendToSession(
                SessionId session, ulong envelopeTick, byte[] buffer, int length) => true;

            public void Reattach(SessionId session) =>
                SessionReattached?.Invoke(session, default, default);

            public void Detach(SessionId session, TransportDisconnectReason reason) =>
                SessionDetached?.Invoke(session, reason);
        }

        private sealed class EstablishedSession
        {
            public TransportTestSupport.Rig Rig;
            public RemoteSliceSession Session;
            public FailFirstReadySendCarrier Carrier;

            public void Pump(long milliseconds = 5)
            {
                Rig.Pump(milliseconds);
                Session.Pump(Rig.Clock.NowMs);
            }
        }

        private sealed class FailFirstReadySendCarrier : INetworkCarrier
        {
            private readonly INetworkCarrier _inner;

            public bool FailReadySend;
            public int ReadySendAttempts;
            public int FailedReadySends;

            public FailFirstReadySendCarrier(INetworkCarrier inner)
            {
                _inner = inner;
            }

            public TransportMetrics Metrics => _inner.Metrics;

            public ITransportClock Clock => _inner.Clock;

            public int EventQueueDepth => _inner.EventQueueDepth;

            public void StartServer() => _inner.StartServer();

            public int ConnectToServer(string host, int port) =>
                _inner.ConnectToServer(host, port);

            public bool Send(
                int connectionId,
                TransportChannel channel,
                byte[] data,
                int offset,
                int length)
            {
                if (data != null &&
                    offset >= 0 &&
                    offset < data.Length &&
                    length == ClientReadyToResumeCodec.SizeBytes &&
                    data[offset] == ClientReadyToResumeCodec.Opcode)
                {
                    ReadySendAttempts++;
                    if (FailReadySend)
                    {
                        FailReadySend = false;
                        FailedReadySends++;
                        return false;
                    }
                }

                return _inner.Send(connectionId, channel, data, offset, length);
            }

            public void CloseConnection(int connectionId, TransportDisconnectReason reason) =>
                _inner.CloseConnection(connectionId, reason);

            public void AssignSessionToken(int connectionId, ulong token) =>
                _inner.AssignSessionToken(connectionId, token);

            public void Shutdown(TransportDisconnectReason reason) => _inner.Shutdown(reason);

            public void Pump(long nowMs) => _inner.Pump(nowMs);

            public bool TryDequeueEvent(out TransportEvent transportEvent) =>
                _inner.TryDequeueEvent(out transportEvent);

            public void Dispose() => _inner.Dispose();
        }

        private static EstablishedSession CreateEstablishedSession(bool failFirstReadySend)
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var address = rig.Pipe.CreateEndpoint();
            var inner = OwnDatagramCarrier.CreateClient(
                rig.Pipe,
                address,
                TransportTestSupport.ServerAddressOf(rig),
                rig.Clock);
            var carrier = new FailFirstReadySendCarrier(inner)
            {
                FailReadySend = failFirstReadySend,
            };
            var session = new RemoteSliceSession(carrier, rig.Clock);
            session.Connect("virtual", 0);

            for (var elapsed = 0L; elapsed < 5000; elapsed += 5)
            {
                rig.Pump(5);
                session.Pump(rig.Clock.NowMs);
                if (session.ReconnectCoordinator.State == ClientReconnectState.Connected)
                {
                    return new EstablishedSession
                    {
                        Rig = rig,
                        Session = session,
                        Carrier = carrier,
                    };
                }
            }

            session.Dispose();
            Assert.Fail("the handshake did not establish");
            return null;
        }

        private static void DriveToCountdown(RemoteSliceSession session)
        {
            var coordinator = session.ReconnectCoordinator;
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            coordinator.OnReconnectResponse(new ReconnectResponse(
                CoreReconnectResult.Accepted,
                session.Endpoint.Player,
                session.Endpoint.Match,
                0,
                0));
            coordinator.OnKeyframeInstalled();
            Assert.That(coordinator.State, Is.EqualTo(ClientReconnectState.Countdown));
        }

        private static void PumpBoth(
            TransportTestSupport.Rig rig,
            ClientTransportEndpoint client,
            long budgetMs)
        {
            for (var elapsed = 0L; elapsed < budgetMs; elapsed += 5)
            {
                rig.Pump(5);
                client.Pump(rig.Clock.NowMs);
            }
        }
    }
}
