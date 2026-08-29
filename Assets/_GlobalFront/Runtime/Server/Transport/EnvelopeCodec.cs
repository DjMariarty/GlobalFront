namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Decoded own-carrier datagram envelope.
    /// </summary>
    public struct TransportEnvelope
    {
        public TransportChannel Channel;
        public ulong SessionToken;
        public ushort Sequence;
        public ushort AckNumber;
        public uint AckBitmap;
        public bool HasFragment;
        public ushort MessageId;
        public ushort FragmentIndex;
        public ushort FragmentCount;
        public int PayloadOffset;
        public int PayloadLength;
    }

    /// <summary>
    /// Result of envelope validation/decoding.
    /// </summary>
    public enum EnvelopeError : byte
    {
        None = 0,
        TooShort = 1,
        BadMagic = 2,
        BadCarrierVersion = 3,
        BadChannel = 4,
        BadPayloadLength = 5,
        BadFragmentHeader = 6
    }

    /// <summary>
    /// Own-carrier datagram framing (ADR-009, rev.3): used by the project's
    /// custom carrier (over <c>VirtualNetworkPipe</c> in tests and raw UDP in
    /// the future). Adopted carriers (LiteNetLib) do not use this envelope;
    /// <see cref="TransportProtocol.CarrierVersion"/> applies exclusively to
    /// this framing and is never mixed with
    /// <see cref="TransportProtocol.MessageVersion"/> or
    /// <c>SnapshotProtocol.Version</c>.
    ///
    /// Layout (little-endian):
    /// <code>
    /// 0   2  Magic
    /// 2   2  CarrierVersion
    /// 4   1  Flags: [Channel:2][HasFragment:1][Reserved:5]
    /// 5   8  SessionToken
    /// 13  2  Sequence      (reliable space for C0/C1; snapshot space for C2)
    /// 15  2  AckNumber     (reliable receive space of the direction only)
    /// 17  4  AckBitmap     (32 selective ack bits ahead of AckNumber)
    /// 21  2  PayloadLength
    /// [+6 FragmentHeader: MessageId u16, FragmentIndex u16, FragmentCount u16]
    /// </code>
    /// </summary>
    public static class EnvelopeCodec
    {
        public const int MaxPayloadPerDatagram =
            TransportProtocol.MaxDatagramBytes - TransportProtocol.EnvelopeHeaderSize;

        public static int Encode(
            byte[] buffer,
            TransportChannel channel,
            ulong sessionToken,
            ushort sequence,
            ushort ackNumber,
            uint ackBitmap,
            ushort messageId,
            ushort fragmentIndex,
            ushort fragmentCount,
            byte[] payload,
            int payloadOffset,
            int payloadLength)
        {
            var hasFragment = fragmentCount > 1;
            var offset = 0;
            buffer[offset++] = (byte)(TransportProtocol.Magic & 0xFF);
            buffer[offset++] = (byte)((TransportProtocol.Magic >> 8) & 0xFF);
            buffer[offset++] = (byte)(TransportProtocol.CarrierVersion & 0xFF);
            buffer[offset++] = (byte)((TransportProtocol.CarrierVersion >> 8) & 0xFF);
            buffer[offset++] = (byte)((byte)channel | (hasFragment ? 0x04 : 0));
            offset = WriteUInt64(buffer, offset, sessionToken);
            offset = WriteUInt16(buffer, offset, sequence);
            offset = WriteUInt16(buffer, offset, ackNumber);
            offset = WriteUInt32(buffer, offset, ackBitmap);
            offset = WriteUInt16(buffer, offset, (ushort)payloadLength);
            if (hasFragment)
            {
                offset = WriteUInt16(buffer, offset, messageId);
                offset = WriteUInt16(buffer, offset, fragmentIndex);
                offset = WriteUInt16(buffer, offset, fragmentCount);
            }

            System.Array.Copy(payload, payloadOffset, buffer, offset, payloadLength);
            return offset + payloadLength;
        }

        /// <summary>
        /// Validates and decodes the envelope of a received datagram. Never
        /// throws; malformed input yields an error code for silent drop plus
        /// counters.
        /// </summary>
        public static EnvelopeError TryDecode(
            byte[] data,
            int length,
            out TransportEnvelope envelope)
        {
            envelope = default;

            if (data == null || length < TransportProtocol.EnvelopeHeaderSize)
            {
                return EnvelopeError.TooShort;
            }

            var offset = 0;
            var magic = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            if (magic != TransportProtocol.Magic)
            {
                return EnvelopeError.BadMagic;
            }

            var carrierVersion = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            if (carrierVersion != TransportProtocol.CarrierVersion)
            {
                return EnvelopeError.BadCarrierVersion;
            }

            var flags = data[offset++];
            var channel = (TransportChannel)(flags & 0x03);
            if ((byte)channel > (byte)TransportChannel.Snapshot)
            {
                return EnvelopeError.BadChannel;
            }

            var hasFragment = (flags & 0x04) != 0;
            envelope.Channel = channel;
            envelope.HasFragment = hasFragment;
            envelope.SessionToken = ReadUInt64(data, ref offset);
            envelope.Sequence = ReadUInt16(data, ref offset);
            envelope.AckNumber = ReadUInt16(data, ref offset);
            envelope.AckBitmap = ReadUInt32(data, ref offset);
            var payloadLength = ReadUInt16(data, ref offset);

            if (hasFragment)
            {
                if (length < TransportProtocol.EnvelopeHeaderSize + TransportProtocol.FragmentHeaderSize)
                {
                    return EnvelopeError.TooShort;
                }

                envelope.MessageId = ReadUInt16(data, ref offset);
                envelope.FragmentIndex = ReadUInt16(data, ref offset);
                envelope.FragmentCount = ReadUInt16(data, ref offset);
                if (envelope.FragmentCount <= 1 ||
                    envelope.FragmentCount > TransportProtocol.MaxFragmentsPerGroup ||
                    envelope.FragmentIndex >= envelope.FragmentCount)
                {
                    return EnvelopeError.BadFragmentHeader;
                }
            }

            if (payloadLength > MaxPayloadPerDatagram ||
                length < offset + payloadLength)
            {
                return EnvelopeError.BadPayloadLength;
            }

            envelope.PayloadOffset = offset;
            envelope.PayloadLength = payloadLength;
            return EnvelopeError.None;
        }

        private static int WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            return offset;
        }

        private static int WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            buffer[offset++] = (byte)((value >> 16) & 0xFF);
            buffer[offset++] = (byte)((value >> 24) & 0xFF);
            return offset;
        }

        private static int WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            buffer[offset++] = (byte)((value >> 16) & 0xFF);
            buffer[offset++] = (byte)((value >> 24) & 0xFF);
            buffer[offset++] = (byte)((value >> 32) & 0xFF);
            buffer[offset++] = (byte)((value >> 40) & 0xFF);
            buffer[offset++] = (byte)((value >> 48) & 0xFF);
            buffer[offset++] = (byte)((value >> 56) & 0xFF);
            return offset;
        }

        private static ushort ReadUInt16(byte[] data, ref int offset)
        {
            var value = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            return value;
        }

        private static uint ReadUInt32(byte[] data, ref int offset)
        {
            var value = (uint)(data[offset] |
                (data[offset + 1] << 8) |
                (data[offset + 2] << 16) |
                (data[offset + 3] << 24));
            offset += 4;
            return value;
        }

        private static ulong ReadUInt64(byte[] data, ref int offset)
        {
            var value = (ulong)data[offset] |
                ((ulong)data[offset + 1] << 8) |
                ((ulong)data[offset + 2] << 16) |
                ((ulong)data[offset + 3] << 24) |
                ((ulong)data[offset + 4] << 32) |
                ((ulong)data[offset + 5] << 40) |
                ((ulong)data[offset + 6] << 48) |
                ((ulong)data[offset + 7] << 56);
            offset += 8;
            return value;
        }
    }
}
