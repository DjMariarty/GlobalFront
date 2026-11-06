using System;
using System.Buffers.Binary;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Economy
{
    /// <summary>Why a PlayerStateSync body was refused. Values are protocol-significant — never renumber.</summary>
    public enum PlayerStateSyncError : byte
    {
        None = 0,
        TooShort = 1,
        BadVersion = 2,
        TruncatedPayload = 3,
        TooManyEntries = 4,
        TrailingBytes = 5,
        DestinationTooSmall = 6
    }

    /// <summary>
    /// Binary wire codec for the private PlayerStateSync channel (opcode 15,
    /// Step 5.4.2). Little-endian, byte-aligned, strictly zero-GC on all Span
    /// hot paths; validation happens before the first write so a failing call
    /// leaves the destination untouched. Maximum encoded size is
    /// 28 + 128 × 13 = 1692 bytes, which exceeds the 1200-byte datagram budget
    /// and therefore relies on the transport's message fragmentation.
    ///
    /// Layout (little-endian):
    ///   [0]      version (1)
    ///   [1..8]   tick (u64)
    ///   [9]      player (u8)
    ///   [10..13] credits (i32)
    ///   [14..17] totalHarvestedCredits (i32)
    ///   [18..21] powerProduced (i32)
    ///   [22..25] powerConsumed (i32)
    ///   [26]     isLowPower (u8, 0/1)
    ///   [27]     queueDigestCount (u8)
    ///   entries: { buildingEntityId u64 | queueCount u8 | headBlueprintId u16 | headRemainingTicks u16 } × count
    /// </summary>
    public static class PlayerStateSyncCodec
    {
        /// <summary>Framed-envelope message type (<c>TransportMessageType.PlayerStateSync</c>).</summary>
        public const byte Opcode = 15;

        public const byte Version = 1;

        public const int HeaderSizeBytes = 28;
        public const int EntrySizeBytes = 13;

        /// <summary>Digest cap; one entry per production building (Step 5.3.2 registry bound).</summary>
        public const int MaxQueueDigestEntries = SimulationConstants.MaxProductionBuildings;

        /// <summary>Wire size of a message carrying the given digest count.</summary>
        public static int GetEncodedSize(int digestCount)
        {
            return HeaderSizeBytes + digestCount * EntrySizeBytes;
        }

        /// <summary>
        /// Encodes the payload plus its queue digest into destination. Returns
        /// the number of bytes written, or -1 when the destination is too
        /// small, the payload version/player is invalid, or a digest entry
        /// carries a queue count outside [0, 255].
        /// </summary>
        public static int Encode(
            Span<byte> destination,
            in PlayerStateSyncPayload payload,
            ReadOnlySpan<PlayerQueueDigestEntry> digest)
        {
            if (destination.Length < GetEncodedSize(digest.Length) ||
                digest.Length > MaxQueueDigestEntries ||
                payload.Version != Version ||
                !payload.Player.IsValid)
            {
                return -1;
            }

            for (var index = 0; index < digest.Length; index++)
            {
                if ((uint)digest[index].QueueCount > byte.MaxValue)
                {
                    return -1;
                }
            }

            destination[0] = payload.Version;
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(1, 8), payload.Tick);
            destination[9] = payload.Player.Value;
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(10, 4), payload.Credits);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(14, 4), payload.TotalHarvestedCredits);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(18, 4), payload.PowerProduced);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(22, 4), payload.PowerConsumed);
            destination[26] = payload.IsLowPower ? (byte)1 : (byte)0;
            destination[27] = (byte)digest.Length;

            var offset = HeaderSizeBytes;
            for (var index = 0; index < digest.Length; index++)
            {
                var entry = digest[index];
                BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, 8), entry.BuildingEntityId);
                destination[offset + 8] = (byte)entry.QueueCount;
                BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset + 9, 2), entry.HeadBlueprintId);
                BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset + 11, 2), entry.HeadRemainingTicks);
                offset += EntrySizeBytes;
            }

            return offset;
        }

        /// <summary>
        /// Decodes a PlayerStateSync body. The caller-provided digest span
        /// receives at most its own length of entries; a buffer smaller than
        /// the declared count fails with
        /// <see cref="PlayerStateSyncError.DestinationTooSmall"/>. Trailing
        /// bytes are rejected — the message is fixed-layout.
        /// </summary>
        public static bool TryDecode(
            ReadOnlySpan<byte> source,
            Span<PlayerQueueDigestEntry> digest,
            out int digestCount,
            out PlayerStateSyncPayload payload,
            out PlayerStateSyncError error)
        {
            digestCount = 0;
            payload = default;

            if (source.Length < HeaderSizeBytes)
            {
                error = PlayerStateSyncError.TooShort;
                return false;
            }

            var version = source[0];
            if (version != Version)
            {
                error = PlayerStateSyncError.BadVersion;
                return false;
            }

            var count = source[27];
            if (count > MaxQueueDigestEntries)
            {
                error = PlayerStateSyncError.TooManyEntries;
                return false;
            }

            var required = GetEncodedSize(count);
            if (source.Length < required)
            {
                error = PlayerStateSyncError.TruncatedPayload;
                return false;
            }

            if (source.Length > required)
            {
                error = PlayerStateSyncError.TrailingBytes;
                return false;
            }

            if (digest.Length < count)
            {
                error = PlayerStateSyncError.DestinationTooSmall;
                return false;
            }

            var tick = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(1, 8));
            var player = new PlayerId(source[9]);
            var credits = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(10, 4));
            var totalHarvested = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(14, 4));
            var powerProduced = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(18, 4));
            var powerConsumed = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(22, 4));
            var isLowPower = source[26] != 0;

            payload = new PlayerStateSyncPayload(
                version, tick, player, credits, totalHarvested, powerProduced, powerConsumed, isLowPower);

            var offset = HeaderSizeBytes;
            for (var index = 0; index < count; index++)
            {
                digest[index] = new PlayerQueueDigestEntry(
                    BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, 8)),
                    source[offset + 8],
                    BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset + 9, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset + 11, 2)));
                offset += EntrySizeBytes;
            }

            digestCount = count;
            error = PlayerStateSyncError.None;
            return true;
        }
    }
}
