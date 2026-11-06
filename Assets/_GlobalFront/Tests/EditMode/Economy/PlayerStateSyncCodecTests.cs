using System;
using GlobalFront.Core.Economy;
using GlobalFront.Core.Model;
using NUnit.Framework;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Economy
{
    /// <summary>
    /// Step 5.4.2 — wire contract of the private PlayerStateSync channel
    /// (opcode 15): round-trip stability, exact layout, and the fail-closed
    /// decode chain (short body, wrong version, truncated digest, oversized
    /// count, trailing bytes, undersized destination).
    /// </summary>
    [TestFixture]
    public sealed class PlayerStateSyncCodecTests
    {
        private static PlayerStateSyncPayload MakePayload(ulong tick)
        {
            return new PlayerStateSyncPayload(
                PlayerStateSyncCodec.Version,
                tick,
                new PlayerId(3),
                123_456,
                1_000_000,
                900,
                750,
                isLowPower: true);
        }

        private static PlayerQueueDigestEntry[] MakeDigest()
        {
            return new[]
            {
                new PlayerQueueDigestEntry(900, 3, 42, 17),
                new PlayerQueueDigestEntry(901, 1, 7, 1),
                new PlayerQueueDigestEntry(902, 0, 0, 0)
            };
        }

        [Test]
        public void RoundTrip_PreservesPayloadAndDigest()
        {
            var digest = MakeDigest();
            var buffer = new byte[PlayerStateSyncCodec.GetEncodedSize(digest.Length)];

            int written = PlayerStateSyncCodec.Encode(buffer, MakePayload(777), digest);
            Assert.That(written, Is.EqualTo(buffer.Length));

            var decoded = new PlayerQueueDigestEntry[digest.Length];
            Assert.That(
                PlayerStateSyncCodec.TryDecode(buffer, decoded, out int count, out var payload, out var error),
                Is.True);
            Assert.That(error, Is.EqualTo(PlayerStateSyncError.None));
            Assert.That(count, Is.EqualTo(digest.Length));

            Assert.That(payload.Version, Is.EqualTo(PlayerStateSyncCodec.Version));
            Assert.That(payload.Tick, Is.EqualTo(777UL));
            Assert.That(payload.Player, Is.EqualTo(new PlayerId(3)));
            Assert.That(payload.Credits, Is.EqualTo(123_456));
            Assert.That(payload.TotalHarvestedCredits, Is.EqualTo(1_000_000));
            Assert.That(payload.PowerProduced, Is.EqualTo(900));
            Assert.That(payload.PowerConsumed, Is.EqualTo(750));
            Assert.That(payload.IsLowPower, Is.True);

            for (var index = 0; index < digest.Length; index++)
            {
                Assert.That(decoded[index].BuildingEntityId, Is.EqualTo(digest[index].BuildingEntityId));
                Assert.That(decoded[index].QueueCount, Is.EqualTo(digest[index].QueueCount));
                Assert.That(decoded[index].HeadBlueprintId, Is.EqualTo(digest[index].HeadBlueprintId));
                Assert.That(decoded[index].HeadRemainingTicks, Is.EqualTo(digest[index].HeadRemainingTicks));
            }
        }

        [Test]
        public void RoundTrip_EmptyDigest_KeepsQueueCountZero()
        {
            var buffer = new byte[PlayerStateSyncCodec.GetEncodedSize(0)];
            Assert.That(PlayerStateSyncCodec.Encode(buffer, MakePayload(5), System.Array.Empty<PlayerQueueDigestEntry>()), Is.EqualTo(buffer.Length));

            Assert.That(
                PlayerStateSyncCodec.TryDecode(buffer, new PlayerQueueDigestEntry[1], out int count, out var payload, out _),
                Is.True);
            Assert.That(count, Is.EqualTo(0));
            Assert.That(payload.Credits, Is.EqualTo(123_456));
        }

        [Test]
        public void EncodedSize_FollowsTheDeclaredLayout()
        {
            Assert.That(PlayerStateSyncCodec.HeaderSizeBytes, Is.EqualTo(28));
            Assert.That(PlayerStateSyncCodec.EntrySizeBytes, Is.EqualTo(13));
            Assert.That(PlayerStateSyncCodec.GetEncodedSize(0), Is.EqualTo(28));
            Assert.That(PlayerStateSyncCodec.GetEncodedSize(3), Is.EqualTo(28 + 3 * 13));

            var buffer = new byte[PlayerStateSyncCodec.GetEncodedSize(1)];
            int written = PlayerStateSyncCodec.Encode(
                buffer, MakePayload(1), new[] { new PlayerQueueDigestEntry(1, 1, 2, 3) });
            Assert.That(written, Is.EqualTo(PlayerStateSyncCodec.GetEncodedSize(1)));
        }

        [Test]
        public void HeaderBytes_GoldenLayout_IsStable()
        {
            var buffer = new byte[PlayerStateSyncCodec.GetEncodedSize(1)];
            PlayerStateSyncCodec.Encode(
                buffer, MakePayload(0x0102030405060708), new[] { new PlayerQueueDigestEntry(0xAABBCCDDEEFF0011, 255, 0x1234, 0x5678) });

            Assert.That(buffer[0], Is.EqualTo(PlayerStateSyncCodec.Version));
            Assert.That(buffer[1], Is.EqualTo(0x08), "tick is little-endian");
            Assert.That(buffer[8], Is.EqualTo(0x01));
            Assert.That(buffer[9], Is.EqualTo(3), "player id sits right after the tick");
            // credits 123456 = 0x0001E240 → LE bytes 40 E2 01 00
            Assert.That(buffer[10], Is.EqualTo(0x40));
            Assert.That(buffer[12], Is.EqualTo(0x01));
            Assert.That(buffer[26], Is.EqualTo(1), "isLowPower encodes as 1");
            Assert.That(buffer[27], Is.EqualTo(1), "digest count");
            Assert.That(buffer[28], Is.EqualTo(0x11), "entity id is little-endian");
            Assert.That(buffer[35], Is.EqualTo(0xAA));
            Assert.That(buffer[36], Is.EqualTo(255), "queue count");
            Assert.That(buffer[37], Is.EqualTo(0x34));
            Assert.That(buffer[39], Is.EqualTo(0x78), "remaining ticks, little-endian low byte");
            Assert.That(buffer[40], Is.EqualTo(0x56), "remaining ticks, high byte");
        }

        [Test]
        public void Decode_TooShort_IsRejected()
        {
            Assert.That(
                PlayerStateSyncCodec.TryDecode(
                    new byte[PlayerStateSyncCodec.HeaderSizeBytes - 1],
                    new PlayerQueueDigestEntry[4],
                    out _,
                    out _,
                    out var error),
                Is.False);
            Assert.That(error, Is.EqualTo(PlayerStateSyncError.TooShort));
        }

        [Test]
        public void Decode_BadVersion_IsRejected()
        {
            var digest = MakeDigest();
            var buffer = new byte[PlayerStateSyncCodec.GetEncodedSize(digest.Length)];
            PlayerStateSyncCodec.Encode(buffer, MakePayload(1), digest);
            buffer[0] = 0x99;

            Assert.That(
                PlayerStateSyncCodec.TryDecode(buffer, new PlayerQueueDigestEntry[4], out _, out _, out var error),
                Is.False);
            Assert.That(error, Is.EqualTo(PlayerStateSyncError.BadVersion));
        }

        [Test]
        public void Decode_TruncatedDigest_IsRejected()
        {
            var digest = MakeDigest();
            var full = new byte[PlayerStateSyncCodec.GetEncodedSize(digest.Length)];
            PlayerStateSyncCodec.Encode(full, MakePayload(1), digest);

            // The header claims 3 entries; the body only carries 2.
            var truncated = new byte[PlayerStateSyncCodec.HeaderSizeBytes + 2 * PlayerStateSyncCodec.EntrySizeBytes];
            full.AsSpan(0, truncated.Length).CopyTo(truncated);

            Assert.That(
                PlayerStateSyncCodec.TryDecode(truncated, new PlayerQueueDigestEntry[4], out _, out _, out var error),
                Is.False);
            Assert.That(error, Is.EqualTo(PlayerStateSyncError.TruncatedPayload));
        }

        [Test]
        public void Decode_CountAboveCapacity_IsRejected()
        {
            var buffer = new byte[PlayerStateSyncCodec.HeaderSizeBytes];
            buffer[0] = PlayerStateSyncCodec.Version;
            buffer[27] = (byte)(PlayerStateSyncCodec.MaxQueueDigestEntries + 1);

            Assert.That(
                PlayerStateSyncCodec.TryDecode(buffer, new PlayerQueueDigestEntry[4], out _, out _, out var error),
                Is.False);
            Assert.That(error, Is.EqualTo(PlayerStateSyncError.TooManyEntries));
        }

        [Test]
        public void Decode_TrailingBytes_AreRejected()
        {
            var digest = MakeDigest();
            var buffer = new byte[PlayerStateSyncCodec.GetEncodedSize(digest.Length) + 4];
            PlayerStateSyncCodec.Encode(buffer, MakePayload(1), digest);

            Assert.That(
                PlayerStateSyncCodec.TryDecode(buffer, new PlayerQueueDigestEntry[4], out _, out _, out var error),
                Is.False);
            Assert.That(error, Is.EqualTo(PlayerStateSyncError.TrailingBytes));
        }

        [Test]
        public void Decode_UndersizedDestination_IsRejected()
        {
            var digest = MakeDigest();
            var buffer = new byte[PlayerStateSyncCodec.GetEncodedSize(digest.Length)];
            PlayerStateSyncCodec.Encode(buffer, MakePayload(1), digest);

            Assert.That(
                PlayerStateSyncCodec.TryDecode(buffer, new PlayerQueueDigestEntry[2], out _, out _, out var error),
                Is.False);
            Assert.That(error, Is.EqualTo(PlayerStateSyncError.DestinationTooSmall));
        }

        [Test]
        public void Encode_TooSmallDestination_ReturnsNegative()
        {
            var digest = MakeDigest();
            var buffer = new byte[PlayerStateSyncCodec.GetEncodedSize(digest.Length) - 1];
            Assert.That(PlayerStateSyncCodec.Encode(buffer, MakePayload(1), digest), Is.EqualTo(-1));
        }

        [Test]
        public void Encode_InvalidPayload_IsRejected()
        {
            var digest = MakeDigest();
            var buffer = new byte[PlayerStateSyncCodec.GetEncodedSize(digest.Length)];

            var wrongVersion = new PlayerStateSyncPayload(
                (byte)(PlayerStateSyncCodec.Version + 1), 1, new PlayerId(1), 0, 0, 0, 0, false);
            Assert.That(PlayerStateSyncCodec.Encode(buffer, wrongVersion, digest), Is.EqualTo(-1), "version mismatch must not encode");

            var invalidPlayer = new PlayerStateSyncPayload(
                PlayerStateSyncCodec.Version, 1, new PlayerId(0), 0, 0, 0, 0, false);
            Assert.That(PlayerStateSyncCodec.Encode(buffer, invalidPlayer, digest), Is.EqualTo(-1), "neutral is not an owner");

            var hugeQueue = new[] { new PlayerQueueDigestEntry(1, 300, 2, 3) };
            var bigBuffer = new byte[PlayerStateSyncCodec.GetEncodedSize(1)];
            Assert.That(
                PlayerStateSyncCodec.Encode(bigBuffer, MakePayload(1), hugeQueue), Is.EqualTo(-1),
                "a queue count beyond one byte must not encode");
        }
    }
}
