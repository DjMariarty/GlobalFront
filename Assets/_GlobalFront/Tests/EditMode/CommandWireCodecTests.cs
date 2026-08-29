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
    /// Phase 2.5 CommandWireCodec tests: round-trip, golden bytes,
    /// malformed/version/truncation handling. The authoritative CommandHeader
    /// type itself is unchanged.
    /// </summary>
    [TestFixture]
    public sealed class CommandWireCodecTests
    {
        private static CommandHeader Header(GameCommandType type, uint sequence = 7, ulong tick = 100) =>
            new CommandHeader(new PlayerId(1), sequence, tick, type);

        [Test]
        public void Move_RoundTrip_PreservesAllFields()
        {
            var buffer = new byte[CommandWireCodec.GetMoveSize(2)];
            var entities = new[] { new EntityId(3), new EntityId(9) };
            var length = CommandWireCodec.EncodeMove(
                buffer,
                Header(GameCommandType.Move),
                entities,
                new WorldPointMm(15000, -8000),
                new FormationSpec(2, 4000, CardinalFacing.East));

            Assert.That(length, Is.EqualTo(buffer.Length));
            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, length, out var wire, out var error),
                Is.True, error.ToString());

            Assert.That(wire.Header.Type, Is.EqualTo(GameCommandType.Move));
            Assert.That(wire.Header.Player.Value, Is.EqualTo(1));
            Assert.That(wire.Header.Sequence, Is.EqualTo(7u));
            Assert.That(wire.Header.RequestedTick, Is.EqualTo(100ul));
            Assert.That(wire.Entities, Is.EqualTo(entities));
            Assert.That(wire.Destination, Is.EqualTo(new WorldPointMm(15000, -8000)));
            Assert.That(wire.Formation.Columns, Is.EqualTo(2));
            Assert.That(wire.Formation.SpacingMm, Is.EqualTo(4000));
            Assert.That(wire.Formation.Facing, Is.EqualTo(CardinalFacing.East));
        }

        [Test]
        public void Attack_RoundTrip_PreservesAllFields()
        {
            var buffer = new byte[CommandWireCodec.GetAttackSize(1)];
            var length = CommandWireCodec.EncodeAttack(
                buffer, Header(GameCommandType.Attack), new[] { new EntityId(5) }, new EntityId(11));

            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, length, out var wire, out _), Is.True);
            Assert.That(wire.Header.Type, Is.EqualTo(GameCommandType.Attack));
            Assert.That(wire.Entities, Is.EqualTo(new[] { new EntityId(5) }));
            Assert.That(wire.Target, Is.EqualTo(new EntityId(11)));
        }

        [Test]
        public void Stop_RoundTrip_PreservesAllFields()
        {
            var buffer = new byte[CommandWireCodec.GetStopSize(2)];
            var length = CommandWireCodec.EncodeStop(
                buffer, Header(GameCommandType.Stop), new[] { new EntityId(2), new EntityId(4) });

            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, length, out var wire, out _), Is.True);
            Assert.That(wire.Header.Type, Is.EqualTo(GameCommandType.Stop));
            Assert.That(wire.Entities, Is.EqualTo(new[] { new EntityId(2), new EntityId(4) }));
        }

        [Test]
        public void GoldenBytes_MoveLayout_IsStable()
        {
            var buffer = new byte[CommandWireCodec.GetMoveSize(1)];
            var length = CommandWireCodec.EncodeMove(
                buffer,
                new CommandHeader(new PlayerId(2), 5, 42, GameCommandType.Move),
                new[] { new EntityId(1) },
                new WorldPointMm(1000, 2000),
                new FormationSpec(0, 2000, CardinalFacing.North));

            var expected = new byte[]
            {
                0x01,                               // version
                0x01,                               // Move
                0x02,                               // PlayerId 2
                0x05, 0x00, 0x00, 0x00,             // sequence 5
                0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // tick 42
                0x01,                               // count
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // entity 1
                0xE8, 0x03, 0x00, 0x00,             // dest X 1000
                0xD0, 0x07, 0x00, 0x00,             // dest Z 2000
                0x00, 0x00, 0x00, 0x00,             // columns 0
                0xD0, 0x07, 0x00, 0x00,             // spacing 2000
                0x00                                // facing North
            };

            Assert.That(length, Is.EqualTo(expected.Length));
            Assert.That(buffer, Is.EqualTo(expected));
        }

        [Test]
        public void BadVersion_IsRejected()
        {
            var buffer = new byte[CommandWireCodec.GetStopSize(1)];
            var length = CommandWireCodec.EncodeStop(
                buffer, Header(GameCommandType.Stop), new[] { new EntityId(1) });
            buffer[0] = 0x99;

            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, length, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(CommandWireError.BadVersion));
        }

        [Test]
        public void UnknownCommandType_IsRejected()
        {
            var buffer = new byte[CommandWireCodec.GetStopSize(1)];
            var length = CommandWireCodec.EncodeStop(
                buffer, Header(GameCommandType.Stop), new[] { new EntityId(1) });
            buffer[1] = (byte)GameCommandType.UseAbility;

            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, length, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(CommandWireError.UnknownCommandType));
        }

        [Test]
        public void TruncatedPayload_IsRejected()
        {
            var buffer = new byte[CommandWireCodec.GetMoveSize(2)];
            var length = CommandWireCodec.EncodeMove(
                buffer,
                Header(GameCommandType.Move),
                new[] { new EntityId(1), new EntityId(2) },
                new WorldPointMm(0, 0),
                new FormationSpec(1, 2000, CardinalFacing.North));

            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, length - 3, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(CommandWireError.TruncatedPayload));
        }

        [Test]
        public void TooShort_IsRejected()
        {
            var buffer = new byte[CommandWireCodec.HeaderSizeBytes - 1];
            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, buffer.Length, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(CommandWireError.TooShort));
        }

        [Test]
        public void TooManyEntities_IsRejected()
        {
            var buffer = new byte[CommandWireCodec.HeaderSizeBytes + 1];
            buffer[0] = CommandWireCodec.Version;
            buffer[1] = (byte)GameCommandType.Stop;
            buffer[2] = 1;
            buffer[CommandWireCodec.HeaderSizeBytes] = 200;

            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, buffer.Length, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(CommandWireError.TooManyEntities));
        }

        [Test]
        public void InvalidEntity_IsRejected()
        {
            var buffer = new byte[CommandWireCodec.GetStopSize(1)];
            var length = CommandWireCodec.EncodeStop(
                buffer, Header(GameCommandType.Stop), new[] { new EntityId(0) });

            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, length, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(CommandWireError.InvalidEntity));
        }

        [Test]
        public void TrailingBytes_AreRejected()
        {
            var buffer = new byte[CommandWireCodec.GetStopSize(1) + 2];
            var length = CommandWireCodec.EncodeStop(
                buffer, Header(GameCommandType.Stop), new[] { new EntityId(1) });

            Assert.That(
                CommandWireCodec.TryDecode(buffer, 0, length + 2, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(CommandWireError.TrailingBytes));
        }

        [Test]
        public void DecodeAtOffset_ReadsEmbeddedPayload()
        {
            var header = new CommandHeader(new PlayerId(1), 1, 1, GameCommandType.Stop);
            var temp = new byte[CommandWireCodec.GetStopSize(1)];
            var length = CommandWireCodec.EncodeStop(temp, header, new[] { new EntityId(1) });
            var wrapper = new byte[10 + length];
            System.Array.Copy(temp, 0, wrapper, 10, length);

            Assert.That(
                CommandWireCodec.TryDecode(wrapper, 10, length, out var wire, out _), Is.True);
            Assert.That(wire.Header.Sequence, Is.EqualTo(1u));
        }
    }

    /// <summary>
    /// Phase 2.5 envelope and message codec tests: framing round-trips,
    /// magic/version/channel/length validation, version-space separation.
    /// </summary>
    [TestFixture]
    public sealed class TransportCodecTests
    {
        [Test]
        public void Envelope_RoundTrip_PreservesAllFields()
        {
            var buffer = new byte[TransportProtocol.MaxDatagramBytes];
            var payload = new byte[] { 9, 8, 7, 6, 5 };
            var size = EnvelopeCodec.Encode(
                buffer,
                TransportChannel.Command,
                0x1122334455667788ul,
                sequence: 300,
                ackNumber: 120,
                ackBitmap: 0b1010,
                messageId: 5,
                fragmentIndex: 1,
                fragmentCount: 3,
                payload: payload,
                payloadOffset: 0,
                payloadLength: payload.Length);

            Assert.That(
                EnvelopeCodec.TryDecode(buffer, size, out var envelope),
                Is.EqualTo(EnvelopeError.None));
            Assert.That(envelope.Channel, Is.EqualTo(TransportChannel.Command));
            Assert.That(envelope.SessionToken, Is.EqualTo(0x1122334455667788ul));
            Assert.That(envelope.Sequence, Is.EqualTo(300));
            Assert.That(envelope.AckNumber, Is.EqualTo(120));
            Assert.That(envelope.AckBitmap, Is.EqualTo(0b1010u));
            Assert.That(envelope.HasFragment, Is.True);
            Assert.That(envelope.MessageId, Is.EqualTo(5));
            Assert.That(envelope.FragmentIndex, Is.EqualTo(1));
            Assert.That(envelope.FragmentCount, Is.EqualTo(3));
            Assert.That(envelope.PayloadLength, Is.EqualTo(payload.Length));
            var received = new byte[envelope.PayloadLength];
            System.Array.Copy(buffer, envelope.PayloadOffset, received, 0, received.Length);
            Assert.That(received, Is.EqualTo(payload));
        }

        [Test]
        public void Envelope_BadMagic_IsRejected()
        {
            var buffer = EncodeSimpleEnvelope();
            buffer[0] = 0x00;

            Assert.That(
                EnvelopeCodec.TryDecode(buffer, buffer.Length, out _),
                Is.EqualTo(EnvelopeError.BadMagic));
        }

        [Test]
        public void Envelope_BadCarrierVersion_IsRejected()
        {
            var buffer = EncodeSimpleEnvelope();
            buffer[2] = 0xEE;

            Assert.That(
                EnvelopeCodec.TryDecode(buffer, buffer.Length, out _),
                Is.EqualTo(EnvelopeError.BadCarrierVersion));
        }

        [Test]
        public void Envelope_BadChannel_IsRejected()
        {
            var buffer = EncodeSimpleEnvelope();
            buffer[4] = 0x03;

            Assert.That(
                EnvelopeCodec.TryDecode(buffer, buffer.Length, out _),
                Is.EqualTo(EnvelopeError.BadChannel));
        }

        [Test]
        public void Envelope_BadPayloadLength_IsRejected()
        {
            var buffer = EncodeSimpleEnvelope();
            // PayloadLength larger than the datagram remainder.
            buffer[21] = 0xFF;
            buffer[22] = 0xFF;

            Assert.That(
                EnvelopeCodec.TryDecode(buffer, buffer.Length, out _),
                Is.EqualTo(EnvelopeError.BadPayloadLength));
        }

        [Test]
        public void Envelope_TooShort_IsRejected()
        {
            var buffer = new byte[TransportProtocol.EnvelopeHeaderSize - 1];
            Assert.That(
                EnvelopeCodec.TryDecode(buffer, buffer.Length, out _),
                Is.EqualTo(EnvelopeError.TooShort));
        }

        [Test]
        public void MessageHeader_RoundTrip_ControlMessage()
        {
            var buffer = new byte[TransportProtocol.MessageHeaderSize];
            MessageCodec.WriteHeader(buffer, 0, TransportMessageType.Ping, 0xABCDul, 0);

            Assert.That(
                MessageCodec.TryDecodeHeader(buffer, 0, buffer.Length, out var header),
                Is.EqualTo(MessageError.None));
            Assert.That(header.Type, Is.EqualTo(TransportMessageType.Ping));
            Assert.That(header.SessionToken, Is.EqualTo(0xABCDul));
            Assert.That(header.PayloadLength, Is.EqualTo(0));
        }

        [Test]
        public void MessageHeader_SnapshotMessage_CarriesSnapshotTick()
        {
            var buffer = new byte[TransportProtocol.MessageHeaderSize + TransportProtocol.SnapshotTickSize];
            MessageCodec.WriteHeader(buffer, 0, TransportMessageType.Snapshot, 1ul, 777ul);

            Assert.That(
                MessageCodec.TryDecodeHeader(buffer, 0, buffer.Length, out var header),
                Is.EqualTo(MessageError.None));
            Assert.That(header.SnapshotTick, Is.EqualTo(777ul));
        }

        [Test]
        public void MessageHeader_BadVersion_IsRejected()
        {
            var buffer = new byte[TransportProtocol.MessageHeaderSize];
            MessageCodec.WriteHeader(buffer, 0, TransportMessageType.Ping, 0, 0);
            buffer[2] = 0xEE;

            Assert.That(
                MessageCodec.TryDecodeHeader(buffer, 0, buffer.Length, out _),
                Is.EqualTo(MessageError.BadMessageVersion));
        }

        [Test]
        public void MessageHeader_BadMagic_IsRejected()
        {
            var buffer = new byte[TransportProtocol.MessageHeaderSize];
            MessageCodec.WriteHeader(buffer, 0, TransportMessageType.Ping, 0, 0);
            buffer[0] = 0x00;

            Assert.That(
                MessageCodec.TryDecodeHeader(buffer, 0, buffer.Length, out _),
                Is.EqualTo(MessageError.BadMagic));
        }

        [Test]
        public void MessageHeader_UnknownType_IsRejected()
        {
            var buffer = new byte[TransportProtocol.MessageHeaderSize];
            MessageCodec.WriteHeader(buffer, 0, TransportMessageType.Ping, 0, 0);
            buffer[4] = 0x7F;

            Assert.That(
                MessageCodec.TryDecodeHeader(buffer, 0, buffer.Length, out _),
                Is.EqualTo(MessageError.UnknownMessageType));
        }

        [Test]
        public void CommandAck_RoundTrip_CarriesBothRejections()
        {
            var buffer = new byte[MessageCodec.CommandAckSize];
            MessageCodec.EncodeCommandAck(
                buffer,
                0,
                new CommandAckPayload
                {
                    Player = new PlayerId(3),
                    CommandSequence = 42,
                    Session = SessionRejection.PlayerMismatch,
                    Command = MatchCommandRejection.SessionRejected
                });

            Assert.That(
                MessageCodec.TryDecodeCommandAck(buffer, 0, buffer.Length, out var ack), Is.True);
            Assert.That(ack.Player.Value, Is.EqualTo(3));
            Assert.That(ack.CommandSequence, Is.EqualTo(42u));
            Assert.That(ack.Session, Is.EqualTo(SessionRejection.PlayerMismatch));
            Assert.That(ack.Command, Is.EqualTo(MatchCommandRejection.SessionRejected));
            Assert.That(ack.IsAccepted, Is.False);
        }

        [Test]
        public void ConnectAccept_RoundTrip_CarriesIdentity()
        {
            var buffer = new byte[MessageCodec.ConnectAcceptSize];
            var session = new SessionId(System.Guid.NewGuid());
            var match = new MatchId(System.Guid.NewGuid());
            MessageCodec.EncodeConnectAccept(
                buffer,
                0,
                new ConnectAcceptPayload
                {
                    SessionToken = 123456789ul,
                    Session = session,
                    Match = match,
                    Player = new PlayerId(2)
                });

            Assert.That(
                MessageCodec.TryDecodeConnectAccept(buffer, 0, buffer.Length, out var accept), Is.True);
            Assert.That(accept.SessionToken, Is.EqualTo(123456789ul));
            Assert.That(accept.Session, Is.EqualTo(session));
            Assert.That(accept.Match, Is.EqualTo(match));
            Assert.That(accept.Player.Value, Is.EqualTo(2));
        }

        private static byte[] EncodeSimpleEnvelope()
        {
            var buffer = new byte[TransportProtocol.EnvelopeHeaderSize + 1];
            EnvelopeCodec.Encode(
                buffer,
                TransportChannel.Control,
                0,
                1,
                0,
                0,
                0,
                0,
                0,
                new byte[] { 1 },
                0,
                1);
            return buffer;
        }
    }
}
