using GlobalFront.Core.Economy;
using GlobalFront.Core.Model;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Server.Transport
{
    /// <summary>
    /// M2 remediation (P1-03) transport suite for the private
    /// <see cref="TransportMessageType.PlayerStateSync"/> channel (opcode 15):
    /// the framed-envelope gate, the server-side refusal of client-forged
    /// syncs, and the owner-only delivery rule — a session's economy digest
    /// never reaches a rival client.
    /// </summary>
    [TestFixture]
    public sealed class PlayerStateSyncTransportTests
    {
        [Test]
        public void MessageCodec_PlayerStateSync_HeaderPasses_AndUnknownTypesStayRejected()
        {
            var buffer = new byte[64];
            var offset = MessageCodec.WriteHeader(
                buffer, 0, TransportMessageType.PlayerStateSync, 0x1122334455667788UL, 0);
            buffer[offset] = PlayerStateSyncCodec.Version;

            Assert.That(
                MessageCodec.TryDecodeHeader(buffer, 0, offset + 1, out var header),
                Is.EqualTo(MessageError.None),
                "opcode 15 rides the framed envelope — its header must decode");
            Assert.That(header.Type, Is.EqualTo(TransportMessageType.PlayerStateSync));
            Assert.That(header.PayloadLength, Is.EqualTo(1));

            var unknown = new byte[64];
            var unknownOffset = MessageCodec.WriteHeader(
                unknown, 0, TransportMessageType.PlayerStateSync, 1UL, 0);
            unknown[4] = 16;

            Assert.That(
                MessageCodec.TryDecodeHeader(unknown, 0, unknownOffset + 1, out _),
                Is.EqualTo(MessageError.UnknownMessageType),
                "the gate stays closed beyond opcode 15");
        }

        [Test]
        public void ServerTransportHost_ClientForgedPlayerStateSync_IsDropped()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);

            var forged = new byte[64];
            var length = MessageCodec.WriteHeader(
                forged, 0, TransportMessageType.PlayerStateSync, client.Token, 0);
            forged[length++] = PlayerStateSyncCodec.Version;

            var droppedBefore = rig.ServerCarrier.Metrics.DroppedMalformed;
            Assert.That(
                client.Carrier.Send(1, TransportChannel.Command, forged, 0, length), Is.True);
            rig.Pump(50);

            Assert.That(
                rig.ServerCarrier.Metrics.DroppedMalformed,
                Is.GreaterThan(droppedBefore),
                "a client naming the server-only private channel commits a protocol " +
                "violation — the frame is dropped and counted, never handled");
        }

        [Test]
        public void PlayerStateSyncEmitter_DeliversOwnerOnlyState()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile(), capacity: 2);
            var clientA = TransportTestSupport.CreateClient(rig);
            var clientB = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, clientA), Is.True);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, clientB), Is.True);

            Assert.That(
                rig.ServerTransport.TryGetPlayerForSession(clientA.Session, out var playerA),
                Is.True, "an established auto-joined session owns a PlayerId");
            Assert.That(
                rig.ServerTransport.TryGetPlayerForSession(clientB.Session, out var playerB),
                Is.True);
            Assert.That(playerA, Is.Not.EqualTo(playerB),
                "two sessions own distinct PlayerIds");

            rig.Host.Server.Economy.SetCredits(playerA, 1111);
            rig.Host.Server.Economy.SetCredits(playerB, 2222);

            var countA = 0;
            PlayerStateSyncPayload payloadA = default;
            clientA.PlayerStateSyncReceived += (payload, digestCount) =>
            {
                payloadA = payload;
                countA++;
            };

            var countB = 0;
            PlayerStateSyncPayload payloadB = default;
            clientB.PlayerStateSyncReceived += (payload, digestCount) =>
            {
                payloadB = payload;
                countB++;
            };

            var emitter = new PlayerStateSyncEmitter(rig.Host.Server, rig.ServerTransport);
            emitter.OnTickCompleted(20);
            rig.Pump(50);
            clientA.Pump(rig.Clock.NowMs);
            clientB.Pump(rig.Clock.NowMs);

            Assert.That(countA, Is.EqualTo(1), "client A received exactly one sync");
            Assert.That(countB, Is.EqualTo(1), "client B received exactly one sync");

            Assert.That(payloadA.Player, Is.EqualTo(playerA),
                "client A's sync names A as the owner");
            Assert.That(payloadA.Credits, Is.EqualTo(1111),
                "client A's sync carries A's own balance");
            Assert.That(payloadA.Tick, Is.EqualTo(20UL));

            Assert.That(payloadB.Player, Is.EqualTo(playerB),
                "client B's sync names B as the owner");
            Assert.That(payloadB.Credits, Is.EqualTo(2222),
                "client B's sync carries B's own balance — no cross-leak to a rival");
        }

        [Test]
        public void ClientEndpoint_MalformedPlayerStateSync_IsDropped()
        {
            var rig = TransportTestSupport.CreateRig(new ImpairmentProfile());
            var client = TransportTestSupport.CreateClient(rig);
            Assert.That(TransportTestSupport.PumpUntilEstablished(rig, client), Is.True);
            Assert.That(
                rig.ServerTransport.TryGetPlayerForSession(client.Session, out _), Is.True);

            var malformedBefore = client.Carrier.Metrics.DroppedMalformed;

            var received = 0;
            client.PlayerStateSyncReceived += (payload, digestCount) => received++;

            // A real envelope header with the client's own token, but a body
            // no decoder can accept (version byte 0xFF).
            var garbage = new byte[] { 0xFF, 0x00, 0x13, 0x37 };
            Assert.That(
                rig.ServerTransport.SendPlayerStateSync(client.Session, garbage, garbage.Length),
                Is.True);
            rig.Pump(50);
            client.Pump(rig.Clock.NowMs);

            Assert.That(
                client.Carrier.Metrics.DroppedMalformed,
                Is.EqualTo(malformedBefore + 1),
                "a corrupt private-state payload is dropped and counted");
            Assert.That(received, Is.EqualTo(0),
                "no event fires for a payload the decoder refused");
        }
    }
}
