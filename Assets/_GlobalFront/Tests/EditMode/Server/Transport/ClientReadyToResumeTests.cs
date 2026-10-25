using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Model;
using GlobalFront.Core.Reconnect;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using GlobalFront.Tests.EditMode;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
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
            var match = host.CreateSessionMatch(2);

            var sessionA = host.CreateSession(host.CreateConnectionHandle());
            var sessionB = host.CreateSession(host.CreateConnectionHandle());
            Assert.That(host.TryJoinMatch(sessionA, match, out _), Is.EqualTo(JoinResult.Assigned));
            Assert.That(host.TryJoinMatch(sessionB, match, out _), Is.EqualTo(JoinResult.Assigned));

            Assert.That(host.NotifyConnectionLost(sessionB, 0), Is.True);
            Assert.That(host.IsPaused, Is.True,
                "a lost session must pause the host");

            host.OnClientReadyToResume(sessionA);
            Assert.That(host.IsPaused, Is.True,
                "an early signal must not strand the session still in grace");

            var rebound = host.CreateConnectionHandle();
            Assert.That(
                host.TryReconnectSession(sessionB, rebound, 1, out _),
                Is.EqualTo(ServerReconnectResult.Reconnected));

            host.OnClientReadyToResume(sessionA);
            Assert.That(host.IsPaused, Is.False,
                "once no session is in grace the bound signal must resume the host");
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
