using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Snapshot;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Snapshot
{
    /// <summary>
    /// Isolated EditMode coverage of the Core delta wire codec (Phase 2.6, step
    /// 2.6.1, ADR-010): golden byte layout, lossless round-trips, varint delta
    /// entity ids, dirty-mask semantics (including OD-14), robustness against
    /// truncation/corruption and the zero-GC hot-path contract.
    ///
    /// Snapshot Protocol v1 is referenced read-only here to prove the ADD record
    /// stays byte-compatible; no v1 code is exercised for its own behaviour.
    /// </summary>
    [TestFixture]
    public sealed class DeltaSnapshotWireCodecTests
    {
        private const int HeaderSize = DeltaSnapshotProtocol.HeaderSizeBytes;

        private const int MessageTypeOffset = 0;
        private const int VersionOffset = 1;
        private const int TickOffset = 5;
        private const int BaseTickOffset = 13;
        private const int PartIndexOffset = 21;
        private const int PartCountOffset = 22;
        private const int FlagsOffset = 23;
        private const int AddCountOffset = 28;
        private const int UpdateCountOffset = 30;
        private const int RemoveCountOffset = 32;
        private const int KeyframeRefOffset = 34;

        private const int FuzzSeed = 20260903;
        private const int MaxFuzzRecords = 512;

        // ---------------------------------------------------------------
        // Test data helpers
        // ---------------------------------------------------------------

        private static DeltaAddRecord MakeAdd(
            ulong entity,
            byte owner = 1,
            int posX = 1000,
            int posZ = 2000,
            int currentHealth = 75,
            bool hasMoveTarget = true,
            int moveTargetX = 5000,
            int moveTargetZ = 6000,
            ulong attackTarget = 0,
            bool autoAcquireEnemies = false)
        {
            return new DeltaAddRecord(
                new EntityId(entity),
                new PlayerId(owner),
                new WorldPointMm(posX, posZ),
                currentHealth,
                hasMoveTarget,
                new WorldPointMm(moveTargetX, moveTargetZ),
                new EntityId(attackTarget),
                autoAcquireEnemies);
        }

        private static DeltaUpdateRecord MakeUpdate(
            ulong entity,
            UnitDirtyMask mask,
            byte owner = 0,
            int posX = 0,
            int posZ = 0,
            int currentHealth = 0,
            bool hasMoveTarget = false,
            int moveTargetX = 0,
            int moveTargetZ = 0,
            ulong attackTarget = 0,
            bool autoAcquireEnemies = false)
        {
            return new DeltaUpdateRecord(
                new EntityId(entity),
                (byte)mask,
                new PlayerId(owner),
                new WorldPointMm(posX, posZ),
                currentHealth,
                hasMoveTarget,
                new WorldPointMm(moveTargetX, moveTargetZ),
                new EntityId(attackTarget),
                autoAcquireEnemies);
        }

        private static DeltaRemoveRecord MakeRemove(
            ulong entity,
            DeltaRemoveCause cause = DeltaRemoveCause.Destroyed)
        {
            return new DeltaRemoveRecord(new EntityId(entity), cause);
        }

        private static DeltaSnapshotHeader MakeHeader(
            int addCount,
            int updateCount,
            int removeCount,
            ulong tick = 120,
            ulong baseTick = 100,
            byte partIndex = 0,
            byte partCount = 1,
            DeltaFlags flags = DeltaFlags.None,
            uint stateChecksum = 0,
            ushort keyframeRef = 0)
        {
            return DeltaSnapshotHeader.CreateDeltaPart(
                tick,
                baseTick,
                partIndex,
                partCount,
                flags,
                stateChecksum,
                (ushort)addCount,
                (ushort)updateCount,
                (ushort)removeCount,
                keyframeRef);
        }

        /// <summary>Encodes a packet and asserts success; returns the exact packet bytes.</summary>
        private static byte[] EncodeOk(
            DeltaAddRecord[] adds,
            DeltaUpdateRecord[] updates,
            DeltaRemoveRecord[] removes,
            ulong tick = 120,
            ulong baseTick = 100,
            byte partIndex = 0,
            byte partCount = 1,
            DeltaFlags flags = DeltaFlags.None,
            uint stateChecksum = 0,
            ushort keyframeRef = 0)
        {
            var header = MakeHeader(
                adds.Length, updates.Length, removes.Length,
                tick, baseTick, partIndex, partCount, flags, stateChecksum, keyframeRef);
            return EncodeOk(header, adds, updates, removes);
        }

        private static byte[] EncodeOk(
            DeltaSnapshotHeader header,
            DeltaAddRecord[] adds,
            DeltaUpdateRecord[] updates,
            DeltaRemoveRecord[] removes)
        {
            var capacity = DeltaSnapshotWireCodec.GetMaxEncodedSize(adds.Length, updates.Length, removes.Length);
            var buffer = new byte[capacity];
            var result = DeltaSnapshotWireCodec.TryEncode(header, adds, updates, removes, buffer, out var written);

            Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok), "the test packet must encode");
            Assert.That(written, Is.GreaterThanOrEqualTo(HeaderSize), "a packet always carries the header");
            Assert.That(written, Is.LessThanOrEqualTo(DeltaSnapshotProtocol.MaxPacketBytes));

            return buffer.AsSpan(0, written).ToArray();
        }

        /// <summary>
        /// Decodes the way a replication consumer does: header first (to size the
        /// record buffers), then the sections.
        /// </summary>
        private static DeltaCodecResult Decode(
            ReadOnlySpan<byte> packet,
            out DeltaSnapshotHeader header,
            out DeltaAddRecord[] adds,
            out DeltaUpdateRecord[] updates,
            out DeltaRemoveRecord[] removes)
        {
            adds = Array.Empty<DeltaAddRecord>();
            updates = Array.Empty<DeltaUpdateRecord>();
            removes = Array.Empty<DeltaRemoveRecord>();

            var result = DeltaSnapshotWireCodec.TryDecodeHeader(packet, out header);
            if (result != DeltaCodecResult.Ok)
            {
                return result;
            }

            adds = new DeltaAddRecord[header.AddCount];
            updates = new DeltaUpdateRecord[header.UpdateCount];
            removes = new DeltaRemoveRecord[header.RemoveCount];
            return DeltaSnapshotWireCodec.TryDecode(packet, out header, adds, updates, removes);
        }

        private static DeltaCodecResult DecodeOk(
            byte[] packet,
            out DeltaSnapshotHeader header,
            out DeltaAddRecord[] adds,
            out DeltaUpdateRecord[] updates,
            out DeltaRemoveRecord[] removes)
        {
            var result = Decode(packet, out header, out adds, out updates, out removes);
            Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok), "the test packet must decode");
            return result;
        }

        private static void AssertPayload(byte[] packet, byte[] expectedPayload, string because)
        {
            Assert.That(packet.Length, Is.EqualTo(HeaderSize + expectedPayload.Length), because);
            Assert.That(
                packet.AsSpan(HeaderSize).SequenceEqual(expectedPayload),
                Is.True,
                because + " (payload bytes: " + BitConverter.ToString(packet, HeaderSize) + ")");
        }

        private static void PatchUInt8(byte[] packet, int offset, byte value) => packet[offset] = value;

        private static void PatchUInt32(byte[] packet, int offset, uint value)
        {
            packet[offset] = (byte)(value & 0xFF);
            packet[offset + 1] = (byte)((value >> 8) & 0xFF);
            packet[offset + 2] = (byte)((value >> 16) & 0xFF);
            packet[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void PatchUInt64(byte[] packet, int offset, ulong value)
        {
            for (var index = 0; index < 8; index++)
            {
                packet[offset + index] = (byte)((value >> (index * 8)) & 0xFF);
            }
        }

        private static byte[] BuildReferencePacket()
        {
            var adds = new[]
            {
                MakeAdd(7, owner: 1, posX: 1000, posZ: 2000, currentHealth: 75,
                    hasMoveTarget: true, moveTargetX: 5000, moveTargetZ: 6000,
                    attackTarget: 0, autoAcquireEnemies: false),
                MakeAdd(42, owner: 2, posX: -1000, posZ: -2000, currentHealth: 100,
                    hasMoveTarget: false, moveTargetX: 0, moveTargetZ: 0,
                    attackTarget: 7, autoAcquireEnemies: true),
                MakeAdd(4300, owner: 10, posX: 0, posZ: 123456, currentHealth: 1,
                    hasMoveTarget: true, moveTargetX: -999999, moveTargetZ: 999999,
                    attackTarget: 42, autoAcquireEnemies: false)
            };

            var updates = new[]
            {
                MakeUpdate(5, UnitDirtyMask.Owner | UnitDirtyMask.Position, owner: 3, posX: 11111, posZ: -22222),
                MakeUpdate(6, UnitDirtyMask.Health, currentHealth: 42),
                MakeUpdate(1000, UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                    hasMoveTarget: true, moveTargetX: 777, moveTargetZ: 888),
                MakeUpdate(100000, UnitDirtyMask.AttackTarget | UnitDirtyMask.AutoAcquire,
                    attackTarget: 99999, autoAcquireEnemies: true)
            };

            var removes = new[]
            {
                MakeRemove(9, DeltaRemoveCause.Destroyed),
                MakeRemove(10, DeltaRemoveCause.FogOfWarHidden),
                MakeRemove(1000, DeltaRemoveCause.Destroyed)
            };

            return EncodeOk(adds, updates, removes, tick: 5000, baseTick: 4990);
        }

        // ---------------------------------------------------------------
        // Round-trip
        // ---------------------------------------------------------------

        [Test]
        public void RoundTrip_AllSections_IsLossless()
        {
            var adds = new[]
            {
                MakeAdd(7), MakeAdd(42, owner: 2, posX: -1000, posZ: -2000, currentHealth: 100,
                    hasMoveTarget: false, moveTargetX: 0, moveTargetZ: 0, attackTarget: 7, autoAcquireEnemies: true),
                MakeAdd(4300, owner: 10, posX: 0, posZ: 123456, currentHealth: 1,
                    moveTargetX: -999999, moveTargetZ: 999999, attackTarget: 42)
            };
            var updates = new[]
            {
                MakeUpdate(5, UnitDirtyMask.Owner | UnitDirtyMask.Position, owner: 3, posX: 11111, posZ: -22222),
                MakeUpdate(6, UnitDirtyMask.Health, currentHealth: 42),
                MakeUpdate(1000, UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                    hasMoveTarget: true, moveTargetX: 777, moveTargetZ: 888),
                MakeUpdate(100000, UnitDirtyMask.AttackTarget | UnitDirtyMask.AutoAcquire,
                    attackTarget: 99999, autoAcquireEnemies: true)
            };
            var removes = new[]
            {
                MakeRemove(9), MakeRemove(10, DeltaRemoveCause.FogOfWarHidden), MakeRemove(1000)
            };

            var packet = EncodeOk(adds, updates, removes, tick: 5000, baseTick: 4990);
            var result = DecodeOk(packet, out var header, out var decodedAdds, out var decodedUpdates, out var decodedRemoves);

            Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(header, Is.EqualTo(MakeHeader(adds.Length, updates.Length, removes.Length, 5000, 4990)));
            Assert.That(decodedAdds, Is.EqualTo(adds));
            Assert.That(decodedUpdates, Is.EqualTo(updates));
            Assert.That(decodedRemoves, Is.EqualTo(removes));
        }

        [Test]
        public void RoundTrip_IsByteDeterministic()
        {
            var first = BuildReferencePacket();
            var second = BuildReferencePacket();

            Assert.That(first, Is.EqualTo(second), "identical input must produce identical bytes");
        }

        [Test]
        public void RoundTrip_EmptySections_ProducesHeaderOnlyPacket()
        {
            var packet = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                Array.Empty<DeltaUpdateRecord>(),
                Array.Empty<DeltaRemoveRecord>());

            Assert.That(packet.Length, Is.EqualTo(HeaderSize));

            var result = DecodeOk(packet, out var header, out var adds, out var updates, out var removes);
            Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(header.AddCount, Is.Zero);
            Assert.That(header.UpdateCount, Is.Zero);
            Assert.That(header.RemoveCount, Is.Zero);
            Assert.That(adds, Is.Empty);
            Assert.That(updates, Is.Empty);
            Assert.That(removes, Is.Empty);
        }

        [Test]
        public void RoundTrip_PreservesFlagsChecksumKeyframeRefAndPartAddressing()
        {
            var adds = new[] { MakeAdd(3) };
            var packet = EncodeOk(
                adds,
                Array.Empty<DeltaUpdateRecord>(),
                Array.Empty<DeltaRemoveRecord>(),
                tick: 900,
                baseTick: 880,
                partIndex: 1,
                partCount: 3,
                flags: DeltaFlags.HasChecksum | DeltaFlags.HasTombstoneEcho,
                stateChecksum: 0xDEADBEEF,
                keyframeRef: 0x1234);

            var result = DecodeOk(packet, out var header, out var decodedAdds, out _, out _);

            Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(header.MessageType, Is.EqualTo(DeltaSnapshotProtocol.MessageTypeDelta));
            Assert.That(header.DeltaProtocolVersion, Is.EqualTo(DeltaSnapshotProtocol.Version));
            Assert.That(header.Tick, Is.EqualTo(900UL));
            Assert.That(header.BaseTick, Is.EqualTo(880UL));
            Assert.That(header.PartIndex, Is.EqualTo(1));
            Assert.That(header.PartCount, Is.EqualTo(3));
            Assert.That(header.Flags, Is.EqualTo((byte)(DeltaFlags.HasChecksum | DeltaFlags.HasTombstoneEcho)));
            Assert.That(header.HasChecksum, Is.True);
            Assert.That(header.HasTombstoneEcho, Is.True);
            Assert.That(header.StateChecksum, Is.EqualTo(0xDEADBEEFu));
            Assert.That(header.KeyframeRef, Is.EqualTo(0x1234),
                "the keyframe generation reference must round-trip through the wire");
            Assert.That(decodedAdds, Is.EqualTo(adds));
        }

        [Test]
        public void RoundTrip_UnmaskedFieldsAreNotTransmitted()
        {
            // The caller filled every field but masked only the position: the
            // unmasked fields must not reach the wire, so they decode as default.
            var updates = new[]
            {
                MakeUpdate(11, UnitDirtyMask.Position, owner: 9, posX: 4000, posZ: -4000,
                    currentHealth: 999, hasMoveTarget: true, moveTargetX: 12, moveTargetZ: 34,
                    attackTarget: 77, autoAcquireEnemies: true)
            };

            var packet = EncodeOk(Array.Empty<DeltaAddRecord>(), updates, Array.Empty<DeltaRemoveRecord>());
            DecodeOk(packet, out _, out _, out var decodedUpdates, out _);

            Assert.That(decodedUpdates[0].Entity, Is.EqualTo(new EntityId(11)));
            Assert.That(decodedUpdates[0].DirtyMask, Is.EqualTo((byte)UnitDirtyMask.Position));
            Assert.That(decodedUpdates[0].Position, Is.EqualTo(new WorldPointMm(4000, -4000)));
            Assert.That(decodedUpdates[0].Owner.Value, Is.Zero, "unmasked owner must not be transmitted");
            Assert.That(decodedUpdates[0].CurrentHealth, Is.Zero, "unmasked health must not be transmitted");
            Assert.That(decodedUpdates[0].HasMoveTarget, Is.False);
            Assert.That(decodedUpdates[0].MoveTarget, Is.EqualTo(new WorldPointMm(0, 0)));
            Assert.That(decodedUpdates[0].AttackTarget, Is.EqualTo(new EntityId(0)));
            Assert.That(decodedUpdates[0].AutoAcquireEnemies, Is.False);
        }

        [Test]
        public void Header_Layout_IsLittleEndianWithKeyframeRefAtBytes34And35()
        {
            var packet = EncodeOk(
                new[] { MakeAdd(1) },
                new[] { MakeUpdate(2, UnitDirtyMask.Health, currentHealth: 7) },
                new[] { MakeRemove(3) },
                tick: 0x1112131415161718UL,
                baseTick: 0x0102030405060708UL,
                partIndex: 0,
                partCount: 1,
                flags: DeltaFlags.HasChecksum,
                stateChecksum: 0xAABBCCDD,
                keyframeRef: 0xCAFE);

            Assert.That(packet.Length, Is.GreaterThanOrEqualTo(HeaderSize));
            Assert.That(packet[MessageTypeOffset], Is.EqualTo(0x03));
            Assert.That(packet[VersionOffset], Is.EqualTo(1));
            Assert.That(BitConverter.ToUInt64(packet, TickOffset), Is.EqualTo(0x1112131415161718UL));
            Assert.That(BitConverter.ToUInt64(packet, BaseTickOffset), Is.EqualTo(0x0102030405060708UL));
            Assert.That(packet[PartIndexOffset], Is.Zero);
            Assert.That(packet[PartCountOffset], Is.EqualTo(1));
            Assert.That(packet[FlagsOffset], Is.EqualTo((byte)DeltaFlags.HasChecksum));
            Assert.That(BitConverter.ToUInt32(packet, FlagsOffset + 1), Is.EqualTo(0xAABBCCDDu));
            Assert.That(BitConverter.ToUInt16(packet, AddCountOffset), Is.EqualTo(1));
            Assert.That(BitConverter.ToUInt16(packet, UpdateCountOffset), Is.EqualTo(1));
            Assert.That(BitConverter.ToUInt16(packet, RemoveCountOffset), Is.EqualTo(1));
            Assert.That(BitConverter.ToUInt16(packet, KeyframeRefOffset), Is.EqualTo(0xCAFE),
                "header bytes 34..35 carry the KeyframeRef as little-endian u16");

            // TryDecodeHeader must surface the reference without touching the
            // sections: it is the value the apply-guard compares against.
            Assert.That(
                DeltaSnapshotWireCodec.TryDecodeHeader(packet, out var headerOnly),
                Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(headerOnly.KeyframeRef, Is.EqualTo(0xCAFE));
        }

        [Test]
        public void TryDecodeHeader_ReadsCounts_WithoutInterpretingSections()
        {
            var packet = BuildReferencePacket();

            var result = DeltaSnapshotWireCodec.TryDecodeHeader(packet, out var header);

            Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(header.AddCount, Is.EqualTo(3));
            Assert.That(header.UpdateCount, Is.EqualTo(4));
            Assert.That(header.RemoveCount, Is.EqualTo(3));

            // The header path must not look at section bytes at all: destroying the
            // payload while keeping its length leaves the header readable, and the
            // very same bytes are rejected by the full decode.
            var destroyed = (byte[])packet.Clone();
            for (var index = HeaderSize; index < destroyed.Length; index++)
            {
                destroyed[index] = 0xFF;
            }

            Assert.That(DeltaSnapshotWireCodec.TryDecodeHeader(destroyed, out var destroyedHeader),
                Is.EqualTo(DeltaCodecResult.Ok),
                "the header is self-contained and independent from the sections");
            Assert.That(destroyedHeader, Is.EqualTo(header));
            Assert.That(Decode(destroyed, out _, out _, out _, out _),
                Is.Not.EqualTo(DeltaCodecResult.Ok),
                "destroyed sections must never decode into records");

            // A header-only slice is rejected: the declared counts need payload bytes.
            Assert.That(DeltaSnapshotWireCodec.TryDecodeHeader(packet.AsSpan(0, HeaderSize), out _),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall),
                "declared counts must be covered by the received payload");
        }

        [Test]
        public void GetEncodedSize_MatchesEncodedLength_AndMaxSizeIsAnUpperBound()
        {
            var adds = new[] { MakeAdd(1), MakeAdd(2), MakeAdd(300) };
            var updates = new[]
            {
                MakeUpdate(400, UnitDirtyMask.Position, posX: 1, posZ: 2),
                MakeUpdate(500, UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                    hasMoveTarget: true, moveTargetX: 3, moveTargetZ: 4)
            };
            var removes = new[] { MakeRemove(600), MakeRemove(70000) };

            var sizeResult = DeltaSnapshotWireCodec.GetEncodedSize(adds, updates, removes, out var size);
            Assert.That(sizeResult, Is.EqualTo(DeltaCodecResult.Ok));

            var packet = EncodeOk(adds, updates, removes);
            Assert.That(packet.Length, Is.EqualTo(size), "the measured size must be exact");

            var max = DeltaSnapshotWireCodec.GetMaxEncodedSize(adds.Length, updates.Length, removes.Length);
            Assert.That(max, Is.GreaterThanOrEqualTo(size), "the worst-case bound must cover the exact size");
            Assert.That(max, Is.EqualTo(HeaderSize + (3 * 39) + (2 * 44) + (2 * 11)));
        }

        [Test]
        public void GetMaxEncodedSize_RejectsCountsThatTheHeaderCannotExpress()
        {
            Assert.That(DeltaSnapshotWireCodec.GetMaxEncodedSize(-1, 0, 0), Is.EqualTo(-1));
            Assert.That(DeltaSnapshotWireCodec.GetMaxEncodedSize(0, ushort.MaxValue + 1, 0), Is.EqualTo(-1));
            Assert.That(DeltaSnapshotWireCodec.GetMaxEncodedSize(0, 0, 0), Is.EqualTo(HeaderSize));
        }

        [Test]
        public void AddRecord_IsByteIdenticalToSnapshotProtocolV1Record()
        {
            Assert.That(DeltaSnapshotProtocol.AddRecordSizeBytes, Is.EqualTo(SnapshotProtocol.SnapshotSizeBytes),
                "the ADD record reuses the v1 unit record layout");

            var unit = new ServerUnitSnapshot(
                new EntityId(7),
                new PlayerId(2),
                new WorldPointMm(1000, -2000),
                75,
                true,
                new WorldPointMm(5000, 6000),
                new EntityId(99),
                true);

            var v1Packet = SnapshotSerializer.Serialize(
                new SnapshotPacketHeader(SnapshotProtocol.Version, 1, 1),
                new[] { unit });

            var add = new DeltaAddRecord(
                unit.Entity, unit.Owner, unit.Position, unit.CurrentHealth,
                unit.HasMoveTarget, unit.MoveTarget, unit.AttackTarget, unit.AutoAcquireEnemies);
            var deltaPacket = EncodeOk(new[] { add }, Array.Empty<DeltaUpdateRecord>(), Array.Empty<DeltaRemoveRecord>());

            Assert.That(
                deltaPacket.AsSpan(HeaderSize, DeltaSnapshotProtocol.AddRecordSizeBytes)
                    .SequenceEqual(v1Packet.AsSpan(SnapshotProtocol.HeaderSizeBytes, SnapshotProtocol.SnapshotSizeBytes)),
                Is.True,
                "the ADD record must be byte-identical to the v1 unit record so keyframes can be re-based");
        }

        // ---------------------------------------------------------------
        // Varint delta entity ids
        // ---------------------------------------------------------------

        [Test]
        public void VarintZigzag_EncodesDeltas_1_2_100_10000_AsExpectedBytes()
        {
            var cases = new (long Delta, byte[] Bytes)[]
            {
                (1L, new byte[] { 0x02 }),
                (2L, new byte[] { 0x04 }),
                (100L, new byte[] { 0xC8, 0x01 }),
                (10000L, new byte[] { 0xA0, 0x9C, 0x01 })
            };

            foreach (var (delta, expected) in cases)
            {
                var buffer = new byte[DeltaSnapshotProtocol.VarintMaxSizeBytes];
                Assert.That(DeltaSnapshotWireCodec.TryWriteVarintZigzag(delta, buffer, out var written), Is.True);
                Assert.That(written, Is.EqualTo(expected.Length), $"delta {delta} length");
                Assert.That(buffer.AsSpan(0, written).ToArray(), Is.EqualTo(expected), $"delta {delta} bytes");
                Assert.That(DeltaSnapshotWireCodec.GetVarintZigzagSize(delta), Is.EqualTo(expected.Length));

                var offset = 0;
                Assert.That(
                    DeltaSnapshotWireCodec.TryReadVarintZigzag(buffer.AsSpan(0, written), ref offset, out var decoded),
                    Is.True);
                Assert.That(decoded, Is.EqualTo(delta), $"delta {delta} round-trip");
                Assert.That(offset, Is.EqualTo(written), $"delta {delta} must consume exactly its encoding");
            }
        }

        [Test]
        public void VarintZigzag_RoundTripsTheFullSignedRange()
        {
            var values = new[]
            {
                0L, 1L, -1L, 63L, -64L, 64L, -65L, 127L, -128L, 100L, -100L,
                10000L, -10000L, int.MaxValue, int.MinValue,
                long.MaxValue, long.MinValue, long.MaxValue - 1, long.MinValue + 1
            };

            foreach (var value in values)
            {
                var buffer = new byte[DeltaSnapshotProtocol.VarintMaxSizeBytes];
                Assert.That(DeltaSnapshotWireCodec.TryWriteVarintZigzag(value, buffer, out var written), Is.True,
                    $"value {value} must fit into the maximal varint");
                Assert.That(written, Is.InRange(1, DeltaSnapshotProtocol.VarintMaxSizeBytes));
                Assert.That(written, Is.EqualTo(DeltaSnapshotWireCodec.GetVarintZigzagSize(value)));

                var offset = 0;
                Assert.That(DeltaSnapshotWireCodec.TryReadVarintZigzag(buffer, ref offset, out var decoded), Is.True);
                Assert.That(decoded, Is.EqualTo(value), $"value {value} round-trip");
            }
        }

        [Test]
        public void VarintZigzag_RejectsTruncatedAndOverlongEncodings()
        {
            // Eleven continuation bytes: longer than the 64-bit maximum.
            var overlong = new byte[DeltaSnapshotProtocol.VarintMaxSizeBytes + 1];
            for (var index = 0; index < overlong.Length; index++)
            {
                overlong[index] = 0x80;
            }

            var offset = 0;
            Assert.That(DeltaSnapshotWireCodec.TryReadVarintZigzag(overlong, ref offset, out _), Is.False);
            Assert.That(offset, Is.Zero, "a rejected varint must not move the cursor");

            // Ten bytes whose last group carries bits beyond the 64-bit range.
            var overflow = new byte[DeltaSnapshotProtocol.VarintMaxSizeBytes];
            for (var index = 0; index < overflow.Length - 1; index++)
            {
                overflow[index] = 0xFF;
            }

            overflow[overflow.Length - 1] = 0x7F;
            offset = 0;
            Assert.That(DeltaSnapshotWireCodec.TryReadVarintZigzag(overflow, ref offset, out _), Is.False);
            Assert.That(offset, Is.Zero);

            // A trailing continuation byte without a terminating group.
            offset = 0;
            Assert.That(DeltaSnapshotWireCodec.TryReadVarintZigzag(new byte[] { 0x80 }, ref offset, out _), Is.False);
            Assert.That(offset, Is.Zero);

            // An empty source.
            offset = 0;
            Assert.That(DeltaSnapshotWireCodec.TryReadVarintZigzag(ReadOnlySpan<byte>.Empty, ref offset, out _), Is.False);
        }

        [Test]
        public void TryWriteVarintZigzag_FailsCleanlyWhenDestinationIsTooSmall()
        {
            var buffer = new byte[2];
            Assert.That(DeltaSnapshotWireCodec.TryWriteVarintZigzag(10000L, buffer, out var written), Is.False);
            Assert.That(written, Is.Zero);
        }

        [Test]
        public void UpdateSection_EncodesEntityIdsAsAscendingVarintDeltas()
        {
            var updates = new[]
            {
                MakeUpdate(1, UnitDirtyMask.Health, currentHealth: 100),
                MakeUpdate(3, UnitDirtyMask.Health, currentHealth: 100),
                MakeUpdate(103, UnitDirtyMask.Health, currentHealth: 100),
                MakeUpdate(10103, UnitDirtyMask.Health, currentHealth: 100)
            };

            var packet = EncodeOk(Array.Empty<DeltaAddRecord>(), updates, Array.Empty<DeltaRemoveRecord>());

            // Deltas 1, 2, 100, 10000 followed by mask 0x04 and health 100 (i32 LE).
            AssertPayload(packet, new byte[]
            {
                0x02, 0x04, 0x64, 0x00, 0x00, 0x00,
                0x04, 0x04, 0x64, 0x00, 0x00, 0x00,
                0xC8, 0x01, 0x04, 0x64, 0x00, 0x00, 0x00,
                0xA0, 0x9C, 0x01, 0x04, 0x64, 0x00, 0x00, 0x00
            }, "update ids must be encoded as zigzag varint deltas from the previous record");

            DecodeOk(packet, out _, out _, out var decodedUpdates, out _);
            Assert.That(decodedUpdates, Is.EqualTo(updates));
        }

        [Test]
        public void RemoveSection_EncodesEntityIdsAsAscendingVarintDeltas()
        {
            var removes = new[]
            {
                MakeRemove(1, DeltaRemoveCause.Destroyed),
                MakeRemove(101, DeltaRemoveCause.FogOfWarHidden)
            };

            var packet = EncodeOk(Array.Empty<DeltaAddRecord>(), Array.Empty<DeltaUpdateRecord>(), removes);

            // Deltas 1 and 100 followed by the cause byte.
            AssertPayload(packet, new byte[] { 0x02, 0x00, 0xC8, 0x01, 0x01 },
                "remove ids must be encoded as zigzag varint deltas from the previous tombstone");

            DecodeOk(packet, out _, out _, out _, out var decodedRemoves);
            Assert.That(decodedRemoves, Is.EqualTo(removes));
        }

        [Test]
        public void Sections_AreIndependent_IdDeltasRestartPerSection()
        {
            // The ADD section ends at 500 while UPDATE/REMOVE restart from 0.
            var adds = new[] { MakeAdd(500) };
            var updates = new[] { MakeUpdate(2, UnitDirtyMask.Health, currentHealth: 5) };
            var removes = new[] { MakeRemove(3) };

            var packet = EncodeOk(adds, updates, removes);

            var updateSectionOffset = HeaderSize + DeltaSnapshotProtocol.AddRecordSizeBytes;
            Assert.That(packet[updateSectionOffset], Is.EqualTo(0x04),
                "the first update id must be a delta from 0 (id 2 -> zigzag 4)");
            Assert.That(packet[updateSectionOffset + 1], Is.EqualTo((byte)UnitDirtyMask.Health));

            var removeSectionOffset = updateSectionOffset + 2 + 4;
            Assert.That(packet[removeSectionOffset], Is.EqualTo(0x06),
                "the first remove id must be a delta from 0 (id 3 -> zigzag 6)");

            DecodeOk(packet, out _, out var decodedAdds, out var decodedUpdates, out var decodedRemoves);
            Assert.That(decodedAdds, Is.EqualTo(adds));
            Assert.That(decodedUpdates, Is.EqualTo(updates));
            Assert.That(decodedRemoves, Is.EqualTo(removes));
        }

        [Test]
        public void Encode_RejectsNonAscendingOrDuplicateEntityIds()
        {
            var buffer = new byte[DeltaSnapshotProtocol.MaxPacketBytes];

            var unsortedAdds = new[] { MakeAdd(10), MakeAdd(5) };
            Assert.That(
                DeltaSnapshotWireCodec.TryEncode(
                    MakeHeader(2, 0, 0), unsortedAdds, Array.Empty<DeltaUpdateRecord>(),
                    Array.Empty<DeltaRemoveRecord>(), buffer, out var written),
                Is.EqualTo(DeltaCodecResult.InvalidArgument),
                "canonical ascending order is a protocol invariant");
            Assert.That(written, Is.Zero);

            var duplicateUpdates = new[]
            {
                MakeUpdate(7, UnitDirtyMask.Health, currentHealth: 1),
                MakeUpdate(7, UnitDirtyMask.Health, currentHealth: 2)
            };
            Assert.That(
                DeltaSnapshotWireCodec.TryEncode(
                    MakeHeader(0, 2, 0), Array.Empty<DeltaAddRecord>(), duplicateUpdates,
                    Array.Empty<DeltaRemoveRecord>(), buffer, out written),
                Is.EqualTo(DeltaCodecResult.InvalidArgument),
                "entity ids are never reused inside a section");
            Assert.That(written, Is.Zero);

            var unsortedRemoves = new[] { MakeRemove(9), MakeRemove(8) };
            Assert.That(
                DeltaSnapshotWireCodec.TryEncode(
                    MakeHeader(0, 0, 2), Array.Empty<DeltaAddRecord>(), Array.Empty<DeltaUpdateRecord>(),
                    unsortedRemoves, buffer, out written),
                Is.EqualTo(DeltaCodecResult.InvalidArgument));
            Assert.That(written, Is.Zero);
        }

        [Test]
        public void Encode_RejectsInvalidEntityIds()
        {
            var buffer = new byte[DeltaSnapshotProtocol.MaxPacketBytes];

            Assert.That(
                DeltaSnapshotWireCodec.TryEncode(
                    MakeHeader(1, 0, 0), new[] { MakeAdd(0) }, Array.Empty<DeltaUpdateRecord>(),
                    Array.Empty<DeltaRemoveRecord>(), buffer, out _),
                Is.EqualTo(DeltaCodecResult.InvalidArgument),
                "entity id 0 is never valid");

            Assert.That(
                DeltaSnapshotWireCodec.TryEncode(
                    MakeHeader(0, 1, 0), Array.Empty<DeltaAddRecord>(),
                    new[] { MakeUpdate(ulong.MaxValue, UnitDirtyMask.Health, currentHealth: 1) },
                    Array.Empty<DeltaRemoveRecord>(), buffer, out _),
                Is.EqualTo(DeltaCodecResult.InvalidArgument),
                "ids must stay inside the signed 63-bit varint space");
        }

        [Test]
        public void Decode_RejectsNonAscendingUpdateIds()
        {
            var updates = new[]
            {
                MakeUpdate(5, UnitDirtyMask.Health, currentHealth: 1),
                MakeUpdate(6, UnitDirtyMask.Health, currentHealth: 2)
            };
            var packet = EncodeOk(Array.Empty<DeltaAddRecord>(), updates, Array.Empty<DeltaRemoveRecord>());

            // Second record starts right after: varint(5) + mask + i32 health.
            var secondRecord = HeaderSize + DeltaSnapshotWireCodec.GetVarintZigzagSize(5) + 1 + 4;
            PatchUInt8(packet, secondRecord, 0x00); // delta 0 -> id would not ascend

            var result = Decode(packet, out _, out _, out _, out _);
            Assert.That(result, Is.EqualTo(DeltaCodecResult.Malformed));
        }

        [Test]
        public void Decode_RejectsNonAscendingAddIds()
        {
            var packet = EncodeOk(new[] { MakeAdd(5), MakeAdd(10) },
                Array.Empty<DeltaUpdateRecord>(), Array.Empty<DeltaRemoveRecord>());

            var secondRecord = HeaderSize + DeltaSnapshotProtocol.AddRecordSizeBytes;
            PatchUInt64(packet, secondRecord, 3UL);

            Assert.That(Decode(packet, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed));
        }

        [Test]
        public void Decode_RejectsZeroEntityIdInAddSection()
        {
            var packet = EncodeOk(new[] { MakeAdd(5) },
                Array.Empty<DeltaUpdateRecord>(), Array.Empty<DeltaRemoveRecord>());

            PatchUInt64(packet, HeaderSize, 0UL);

            Assert.That(Decode(packet, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed));
        }

        // ---------------------------------------------------------------
        // Dirty mask
        // ---------------------------------------------------------------

        [Test]
        public void UpdateRecord_PositionOnly_WritesMaskAndCoordinates()
        {
            var updates = new[] { MakeUpdate(1, UnitDirtyMask.Position, posX: -123456, posZ: 654321) };

            var packet = EncodeOk(Array.Empty<DeltaAddRecord>(), updates, Array.Empty<DeltaRemoveRecord>());

            // delta 1 -> 0x02, mask 0x02, posX -123456 (i32 LE), posZ 654321 (i32 LE)
            AssertPayload(packet, new byte[]
            {
                0x02, 0x02, 0xC0, 0x1D, 0xFE, 0xFF, 0xF1, 0xFB, 0x09, 0x00
            }, "a position-only update carries the id delta, the mask and two i32");

            DecodeOk(packet, out _, out _, out var decodedUpdates, out _);
            Assert.That(decodedUpdates[0], Is.EqualTo(updates[0]));
            Assert.That(decodedUpdates[0].DirtyMask, Is.EqualTo((byte)UnitDirtyMask.Position));
            Assert.That(decodedUpdates[0].Position, Is.EqualTo(new WorldPointMm(-123456, 654321)));
        }

        [Test]
        public void UpdateRecord_HealthOnly_WritesMaskAndFourBytes()
        {
            var updates = new[] { MakeUpdate(1, UnitDirtyMask.Health, currentHealth: 42) };

            var packet = EncodeOk(Array.Empty<DeltaAddRecord>(), updates, Array.Empty<DeltaRemoveRecord>());

            AssertPayload(packet, new byte[] { 0x02, 0x04, 0x2A, 0x00, 0x00, 0x00 },
                "a health-only update carries the id delta, the mask and one i32");

            DecodeOk(packet, out _, out _, out var decodedUpdates, out _);
            Assert.That(decodedUpdates[0].CurrentHealth, Is.EqualTo(42));
            Assert.That(decodedUpdates[0].Position, Is.EqualTo(new WorldPointMm(0, 0)));
        }

        [Test]
        public void UpdateRecord_OwnerAndAutoAcquire_WriteOneByteEach()
        {
            var ownerOnly = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[] { MakeUpdate(1, UnitDirtyMask.Owner, owner: 3) },
                Array.Empty<DeltaRemoveRecord>());
            AssertPayload(ownerOnly, new byte[] { 0x02, 0x01, 0x03 }, "owner-only update");
            DecodeOk(ownerOnly, out _, out _, out var decodedOwner, out _);
            Assert.That(decodedOwner[0].Owner.Value, Is.EqualTo(3));

            var autoOnly = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[] { MakeUpdate(1, UnitDirtyMask.AutoAcquire, autoAcquireEnemies: true) },
                Array.Empty<DeltaRemoveRecord>());
            AssertPayload(autoOnly, new byte[] { 0x02, 0x40, 0x01 }, "auto-acquire-only update");
            DecodeOk(autoOnly, out _, out _, out var decodedAuto, out _);
            Assert.That(decodedAuto[0].AutoAcquireEnemies, Is.True);
        }

        [Test]
        public void UpdateRecord_AttackTarget_IsEncodedRelativeToTheEntityId()
        {
            var targeted = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[] { MakeUpdate(1000, UnitDirtyMask.AttackTarget, attackTarget: 1001) },
                Array.Empty<DeltaRemoveRecord>());

            // id delta 1000 -> zigzag 2000 -> D0 0F; mask 0x20; target delta 1 -> 0x02
            AssertPayload(targeted, new byte[] { 0xD0, 0x0F, 0x20, 0x02 },
                "the attack target travels as a zigzag varint relative to the record entity id");
            DecodeOk(targeted, out _, out _, out var decodedTargeted, out _);
            Assert.That(decodedTargeted[0].AttackTarget, Is.EqualTo(new EntityId(1001)));

            var cleared = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[] { MakeUpdate(1000, UnitDirtyMask.AttackTarget, attackTarget: 0) },
                Array.Empty<DeltaRemoveRecord>());

            // target delta -1000 -> zigzag 1999 -> CF 0F
            AssertPayload(cleared, new byte[] { 0xD0, 0x0F, 0x20, 0xCF, 0x0F },
                "clearing the attack target is a negative relative delta");
            DecodeOk(cleared, out _, out _, out var decodedCleared, out _);
            Assert.That(decodedCleared[0].AttackTarget, Is.EqualTo(new EntityId(0)));
        }

        [Test]
        public void UpdateRecord_AllFields_RoundTrips()
        {
            const UnitDirtyMask all = UnitDirtyMask.Owner | UnitDirtyMask.Position | UnitDirtyMask.Health |
                UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget | UnitDirtyMask.AttackTarget |
                UnitDirtyMask.AutoAcquire;

            var updates = new[]
            {
                MakeUpdate(1, all, owner: 4, posX: 10, posZ: 20, currentHealth: 30,
                    hasMoveTarget: true, moveTargetX: 40, moveTargetZ: 50,
                    attackTarget: 2, autoAcquireEnemies: true)
            };

            var packet = EncodeOk(Array.Empty<DeltaAddRecord>(), updates, Array.Empty<DeltaRemoveRecord>());

            AssertPayload(packet, new byte[]
            {
                0x02, 0x7F,
                0x04,
                0x0A, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00,
                0x1E, 0x00, 0x00, 0x00,
                0x01,
                0x28, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x00,
                0x02,
                0x01
            }, "fields are written in ascending bit order");
            Assert.That(packet.Length, Is.EqualTo(HeaderSize + 26));

            DecodeOk(packet, out _, out _, out var decodedUpdates, out _);
            Assert.That(decodedUpdates[0], Is.EqualTo(updates[0]));
            Assert.That(decodedUpdates[0].HasFlag(all), Is.True);
        }

        [Test]
        public void UpdateRecord_ClearMoveTarget_OmitsCoordinates_Od14()
        {
            var cleared = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[]
                {
                    MakeUpdate(1, UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                        hasMoveTarget: false, moveTargetX: 5000, moveTargetZ: 6000)
                },
                Array.Empty<DeltaRemoveRecord>());

            // OD-14: bit 3 set with HasMoveTarget = 0 clears the target and the
            // coordinates are not transmitted at all.
            AssertPayload(cleared, new byte[] { 0x02, 0x18, 0x00 },
                "clearing the move target must not carry coordinates");

            DecodeOk(cleared, out _, out _, out var decodedCleared, out _);
            Assert.That(decodedCleared[0].DirtyMask,
                Is.EqualTo((byte)(UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget)));
            Assert.That(decodedCleared[0].HasMoveTarget, Is.False);
            Assert.That(decodedCleared[0].MoveTarget, Is.EqualTo(new WorldPointMm(0, 0)),
                "absent coordinates decode as the default point");

            var set = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[]
                {
                    MakeUpdate(1, UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                        hasMoveTarget: true, moveTargetX: 5000, moveTargetZ: 6000)
                },
                Array.Empty<DeltaRemoveRecord>());

            Assert.That(set.Length - cleared.Length, Is.EqualTo(8),
                "only the two omitted i32 coordinates may differ between clear and set");

            DecodeOk(set, out _, out _, out var decodedSet, out _);
            Assert.That(decodedSet[0].HasMoveTarget, Is.True);
            Assert.That(decodedSet[0].MoveTarget, Is.EqualTo(new WorldPointMm(5000, 6000)));
        }

        [Test]
        public void UpdateRecord_MoveTargetCoordinatesWithoutFlagBit_AreTransmitted()
        {
            var updates = new[]
            {
                MakeUpdate(1, UnitDirtyMask.MoveTarget, moveTargetX: 5000, moveTargetZ: 6000)
            };

            var packet = EncodeOk(Array.Empty<DeltaAddRecord>(), updates, Array.Empty<DeltaRemoveRecord>());

            AssertPayload(packet, new byte[]
            {
                0x02, 0x10, 0x88, 0x13, 0x00, 0x00, 0x70, 0x17, 0x00, 0x00
            }, "bit 4 alone re-targets the coordinates and keeps the client-side flag");

            DecodeOk(packet, out _, out _, out var decodedUpdates, out _);
            Assert.That(decodedUpdates[0].HasMoveTarget, Is.False, "bit 3 is absent, so the flag is not transmitted");
            Assert.That(decodedUpdates[0].MoveTarget, Is.EqualTo(new WorldPointMm(5000, 6000)));
        }

        [Test]
        public void DirtyMask_EachSingleBit_RoundTripsWithExpectedRecordSize()
        {
            var cases = new (UnitDirtyMask Mask, int FieldBytes)[]
            {
                (UnitDirtyMask.Owner, 1),
                (UnitDirtyMask.Position, 8),
                (UnitDirtyMask.Health, 4),
                (UnitDirtyMask.HasMoveTarget, 1),
                (UnitDirtyMask.MoveTarget, 8),
                (UnitDirtyMask.AttackTarget, 1),
                (UnitDirtyMask.AutoAcquire, 1)
            };

            foreach (var (mask, fieldBytes) in cases)
            {
                var updates = new[]
                {
                    MakeUpdate(1, mask, owner: 2, posX: 3, posZ: 4, currentHealth: 5,
                        hasMoveTarget: true, moveTargetX: 6, moveTargetZ: 7,
                        attackTarget: 2, autoAcquireEnemies: true)
                };

                var packet = EncodeOk(Array.Empty<DeltaAddRecord>(), updates, Array.Empty<DeltaRemoveRecord>());

                Assert.That(packet.Length, Is.EqualTo(HeaderSize + 1 + 1 + fieldBytes),
                    $"mask 0x{(byte)mask:X2} record size");

                var result = Decode(packet, out _, out _, out var decodedUpdates, out _);
                Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok), $"mask 0x{(byte)mask:X2} must decode");
                Assert.That(decodedUpdates[0].DirtyMask, Is.EqualTo((byte)mask));
                Assert.That(decodedUpdates[0].Entity, Is.EqualTo(new EntityId(1)));
            }
        }

        [Test]
        public void Encode_RejectsEmptyDirtyMask()
        {
            var buffer = new byte[DeltaSnapshotProtocol.MaxPacketBytes];

            var result = DeltaSnapshotWireCodec.TryEncode(
                MakeHeader(0, 1, 0),
                Array.Empty<DeltaAddRecord>(),
                new[] { MakeUpdate(1, UnitDirtyMask.None) },
                Array.Empty<DeltaRemoveRecord>(),
                buffer,
                out var written);

            Assert.That(result, Is.EqualTo(DeltaCodecResult.InvalidArgument),
                "an update without any dirty field cannot be encoded");
            Assert.That(written, Is.Zero);
        }

        [Test]
        public void Decode_RejectsEmptyDirtyMask()
        {
            var packet = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[] { MakeUpdate(1, UnitDirtyMask.Health, currentHealth: 42) },
                Array.Empty<DeltaRemoveRecord>());

            PatchUInt8(packet, HeaderSize + 1, 0x00);

            Assert.That(Decode(packet, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed));
        }

        [Test]
        public void Encode_RejectsReservedExtensionBit()
        {
            var buffer = new byte[DeltaSnapshotProtocol.MaxPacketBytes];

            foreach (var mask in new byte[] { 0x80, 0x81, 0xFF })
            {
                var result = DeltaSnapshotWireCodec.TryEncode(
                    MakeHeader(0, 1, 0),
                    Array.Empty<DeltaAddRecord>(),
                    new[] { MakeUpdate(1, (UnitDirtyMask)mask, currentHealth: 1) },
                    Array.Empty<DeltaRemoveRecord>(),
                    buffer,
                    out var written);

                Assert.That(result, Is.EqualTo(DeltaCodecResult.InvalidArgument),
                    $"mask 0x{mask:X2} uses the reserved extension bit, undefined in v1");
                Assert.That(written, Is.Zero);
            }
        }

        [Test]
        public void Decode_RejectsReservedExtensionBit()
        {
            var packet = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[] { MakeUpdate(1, UnitDirtyMask.Health, currentHealth: 42) },
                Array.Empty<DeltaRemoveRecord>());

            PatchUInt8(packet, HeaderSize + 1, (byte)(packet[HeaderSize + 1] | 0x80));

            Assert.That(Decode(packet, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed));
        }

        // ---------------------------------------------------------------
        // Buffer bounds, truncation and corruption
        // ---------------------------------------------------------------

        [Test]
        public void Encode_DestinationTooSmall_ReportsBufferTooSmall_AndWritesNothing()
        {
            var adds = new[] { MakeAdd(1), MakeAdd(2) };
            var updates = new[] { MakeUpdate(3, UnitDirtyMask.Position, posX: 9, posZ: -9) };
            var removes = new[] { MakeRemove(4) };
            var header = MakeHeader(adds.Length, updates.Length, removes.Length);

            Assert.That(
                DeltaSnapshotWireCodec.GetEncodedSize(adds, updates, removes, out var size),
                Is.EqualTo(DeltaCodecResult.Ok));

            var destination = new byte[size];
            for (var capacity = 0; capacity < size; capacity++)
            {
                Fill(destination, 0xA5);

                var result = DeltaSnapshotWireCodec.TryEncode(
                    header, adds, updates, removes, destination.AsSpan(0, capacity), out var written);

                Assert.That(result, Is.EqualTo(DeltaCodecResult.BufferTooSmall), $"capacity {capacity}");
                Assert.That(written, Is.Zero, $"capacity {capacity} must not report written bytes");
                for (var index = 0; index < destination.Length; index++)
                {
                    Assert.That(destination[index], Is.EqualTo(0xA5),
                        $"a failed encode must leave the pooled buffer untouched (capacity {capacity}, byte {index})");
                }
            }

            var ok = DeltaSnapshotWireCodec.TryEncode(header, adds, updates, removes, destination, out var exact);
            Assert.That(ok, Is.EqualTo(DeltaCodecResult.Ok), "the exact capacity must succeed");
            Assert.That(exact, Is.EqualTo(size));
        }

        [Test]
        public void Encode_AcceptsPayloadExactlyAtTheBudget()
        {
            // 210 ADD records (8190 bytes) + one REMOVE with a 1-byte id delta = 8192.
            var adds = new DeltaAddRecord[210];
            for (var index = 0; index < adds.Length; index++)
            {
                adds[index] = MakeAdd((ulong)(index + 1));
            }

            var removes = new[] { MakeRemove(63) };
            var header = MakeHeader(adds.Length, 0, removes.Length);
            var buffer = new byte[DeltaSnapshotWireCodec.GetMaxEncodedSize(adds.Length, 0, removes.Length)];

            var result = DeltaSnapshotWireCodec.TryEncode(
                header, adds, Array.Empty<DeltaUpdateRecord>(), removes, buffer, out var written);

            Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(written, Is.EqualTo(DeltaSnapshotProtocol.MaxPacketBytes));
            Assert.That(written - HeaderSize, Is.EqualTo(DeltaSnapshotProtocol.MaxPayloadBytes));

            var addSink = new DeltaAddRecord[adds.Length];
            var removeSink = new DeltaRemoveRecord[removes.Length];
            var decode = DeltaSnapshotWireCodec.TryDecode(
                buffer.AsSpan(0, written), out var decodedHeader, addSink,
                Array.Empty<DeltaUpdateRecord>(), removeSink);

            Assert.That(decode, Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(decodedHeader.AddCount, Is.EqualTo(210));
            Assert.That(addSink, Is.EqualTo(adds));
            Assert.That(removeSink, Is.EqualTo(removes));
        }

        [Test]
        public void Encode_RejectsPayloadAboveTheBudget()
        {
            // 211 ADD records = 8229 payload bytes, one record above the 8 KB budget.
            var adds = new DeltaAddRecord[211];
            for (var index = 0; index < adds.Length; index++)
            {
                adds[index] = MakeAdd((ulong)(index + 1));
            }

            var buffer = new byte[HeaderSize + (adds.Length * DeltaSnapshotProtocol.AddRecordSizeBytes)];

            Assert.That(
                DeltaSnapshotWireCodec.GetEncodedSize(
                    adds, Array.Empty<DeltaUpdateRecord>(), Array.Empty<DeltaRemoveRecord>(), out _),
                Is.EqualTo(DeltaCodecResult.PayloadTooLarge),
                "the size helper must report the budget violation so the tick can be split into parts");

            var result = DeltaSnapshotWireCodec.TryEncode(
                MakeHeader(adds.Length, 0, 0), adds, Array.Empty<DeltaUpdateRecord>(),
                Array.Empty<DeltaRemoveRecord>(), buffer, out var written);

            Assert.That(result, Is.EqualTo(DeltaCodecResult.PayloadTooLarge));
            Assert.That(written, Is.Zero);
        }

        [Test]
        public void Decode_RejectsPacketLargerThanTheProtocolMaximum()
        {
            var addSink = new DeltaAddRecord[1];
            var updateSink = new DeltaUpdateRecord[1];
            var removeSink = new DeltaRemoveRecord[1];

            var oversized = new byte[DeltaSnapshotProtocol.MaxPacketBytes + 1];
            oversized[MessageTypeOffset] = DeltaSnapshotProtocol.MessageTypeDelta;

            Assert.That(DeltaSnapshotWireCodec.TryDecodeHeader(oversized, out _),
                Is.EqualTo(DeltaCodecResult.PayloadTooLarge));
            Assert.That(DeltaSnapshotWireCodec.TryDecode(oversized, out _, addSink, updateSink, removeSink),
                Is.EqualTo(DeltaCodecResult.PayloadTooLarge));

            // Exactly at the maximum the length gate passes; the zeroed version field is rejected next.
            var exact = new byte[DeltaSnapshotProtocol.MaxPacketBytes];
            exact[MessageTypeOffset] = DeltaSnapshotProtocol.MessageTypeDelta;
            Assert.That(DeltaSnapshotWireCodec.TryDecodeHeader(exact, out _),
                Is.EqualTo(DeltaCodecResult.UnsupportedVersion));
        }

        [Test]
        public void Decode_TruncatedAtEveryLength_NeverSucceeds()
        {
            var packet = BuildReferencePacket();
            var addSink = new DeltaAddRecord[64];
            var updateSink = new DeltaUpdateRecord[64];
            var removeSink = new DeltaRemoveRecord[64];

            for (var length = 0; length < packet.Length; length++)
            {
                var result = DeltaSnapshotWireCodec.TryDecode(
                    packet.AsSpan(0, length), out _, addSink, updateSink, removeSink);

                Assert.That(result, Is.Not.EqualTo(DeltaCodecResult.Ok),
                    $"a packet truncated to {length} of {packet.Length} bytes must not decode");
                Assert.That(Enum.IsDefined(typeof(DeltaCodecResult), result), Is.True);
            }

            Assert.That(
                DeltaSnapshotWireCodec.TryDecode(
                    ReadOnlySpan<byte>.Empty, out _, addSink, updateSink, removeSink),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall));
            Assert.That(DeltaSnapshotWireCodec.TryDecodeHeader(ReadOnlySpan<byte>.Empty, out _),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall));
        }

        [Test]
        public void Decode_RejectsHeaderCountsThatThePayloadCannotHold()
        {
            var packet = BuildReferencePacket();

            var inflatedUpdates = (byte[])packet.Clone();
            PatchUInt16(inflatedUpdates, UpdateCountOffset, 1000);
            Assert.That(Decode(inflatedUpdates, out _, out _, out _, out _),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall), "inflated UpdateCount");

            var inflatedAdds = (byte[])packet.Clone();
            PatchUInt16(inflatedAdds, AddCountOffset, 200);
            Assert.That(Decode(inflatedAdds, out _, out _, out _, out _),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall), "inflated AddCount");

            var inflatedRemoves = (byte[])packet.Clone();
            PatchUInt16(inflatedRemoves, RemoveCountOffset, 4000);
            Assert.That(Decode(inflatedRemoves, out _, out _, out _, out _),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall), "inflated RemoveCount");
        }

        [Test]
        public void Decode_RecordSpansSmallerThanHeaderCounts_ReturnBufferTooSmall()
        {
            var packet = BuildReferencePacket(); // 3 adds, 4 updates, 3 removes

            Assert.That(
                DeltaSnapshotWireCodec.TryDecode(
                    packet, out _, new DeltaAddRecord[2], new DeltaUpdateRecord[4], new DeltaRemoveRecord[3]),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall), "add span too small");

            Assert.That(
                DeltaSnapshotWireCodec.TryDecode(
                    packet, out _, new DeltaAddRecord[3], new DeltaUpdateRecord[3], new DeltaRemoveRecord[3]),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall), "update span too small");

            Assert.That(
                DeltaSnapshotWireCodec.TryDecode(
                    packet, out _, new DeltaAddRecord[3], new DeltaUpdateRecord[4], new DeltaRemoveRecord[2]),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall), "remove span too small");

            Assert.That(
                DeltaSnapshotWireCodec.TryDecode(
                    packet, out _, new DeltaAddRecord[3], new DeltaUpdateRecord[4], new DeltaRemoveRecord[3]),
                Is.EqualTo(DeltaCodecResult.Ok), "exact spans must succeed");
        }

        [Test]
        public void Decode_RejectsWrongMessageType()
        {
            foreach (var messageType in new byte[] { 0x00, 0x01, 0x02, 0x04, 0xFF })
            {
                var packet = BuildReferencePacket();
                PatchUInt8(packet, MessageTypeOffset, messageType);

                Assert.That(Decode(packet, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed),
                    $"message type 0x{messageType:X2} is not an establishing delta");
            }
        }

        [Test]
        public void Decode_RejectsUnsupportedProtocolVersion()
        {
            foreach (var version in new uint[] { 0u, 2u, uint.MaxValue })
            {
                var packet = BuildReferencePacket();
                PatchUInt32(packet, VersionOffset, version);

                Assert.That(Decode(packet, out _, out _, out _, out _),
                    Is.EqualTo(DeltaCodecResult.UnsupportedVersion),
                    $"delta protocol version {version} must be rejected, not guessed at");
            }
        }

        [Test]
        public void Decode_RejectsReservedFlagBits()
        {
            foreach (var flag in new byte[] { 0x04, 0x08, 0x40, 0x80 })
            {
                var packet = BuildReferencePacket();
                PatchUInt8(packet, FlagsOffset, flag);

                Assert.That(Decode(packet, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed),
                    $"flag 0x{flag:X2} is reserved in v1");
            }

            var defined = BuildReferencePacket();
            PatchUInt8(defined, FlagsOffset, (byte)(DeltaFlags.HasChecksum | DeltaFlags.HasTombstoneEcho));
            Assert.That(Decode(defined, out var header, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(header.HasChecksum, Is.True);
            Assert.That(header.HasTombstoneEcho, Is.True);
        }

        [Test]
        public void Decode_RejectsInvalidPartAddressing()
        {
            var zeroParts = BuildReferencePacket();
            PatchUInt8(zeroParts, PartCountOffset, 0);
            Assert.That(Decode(zeroParts, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed),
                "PartCount must be at least 1");

            var indexAboveCount = BuildReferencePacket();
            PatchUInt8(indexAboveCount, PartIndexOffset, 3);
            Assert.That(Decode(indexAboveCount, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed),
                "PartIndex must stay below PartCount");

            var buffer = new byte[DeltaSnapshotProtocol.MaxPacketBytes];
            Assert.That(
                DeltaSnapshotWireCodec.TryEncode(
                    MakeHeader(0, 0, 0, partIndex: 2, partCount: 2),
                    Array.Empty<DeltaAddRecord>(), Array.Empty<DeltaUpdateRecord>(),
                    Array.Empty<DeltaRemoveRecord>(), buffer, out _),
                Is.EqualTo(DeltaCodecResult.InvalidArgument),
                "the encoder refuses to emit an unreachable part index");
        }

        [Test]
        public void Decode_RejectsBaseTickAfterTick()
        {
            var packet = BuildReferencePacket(); // tick 5000, base tick 4990
            PatchUInt64(packet, BaseTickOffset, 5001UL);

            Assert.That(Decode(packet, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed));
        }

        [Test]
        public void Decode_RejectsUnknownRemoveCause()
        {
            var packet = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                Array.Empty<DeltaUpdateRecord>(),
                new[] { MakeRemove(1) });

            var causeOffset = HeaderSize + DeltaSnapshotWireCodec.GetVarintZigzagSize(1);

            foreach (var cause in new byte[] { 2, 7, 0xFF })
            {
                var corrupted = (byte[])packet.Clone();
                PatchUInt8(corrupted, causeOffset, cause);

                Assert.That(Decode(corrupted, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed),
                    $"cause {cause} is reserved in v1");
            }

            PatchUInt8(packet, causeOffset, (byte)DeltaRemoveCause.FogOfWarHidden);
            var result = Decode(packet, out _, out _, out _, out var removes);
            Assert.That(result, Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(removes[0].Cause, Is.EqualTo((byte)DeltaRemoveCause.FogOfWarHidden));
        }

        [Test]
        public void Decode_RejectsInvalidBooleanBytes()
        {
            // ADD record: HasMoveTarget sits at byte 21, AutoAcquire at byte 38.
            foreach (var offsetInRecord in new[] { 21, 38 })
            {
                var packet = EncodeOk(new[] { MakeAdd(1) },
                    Array.Empty<DeltaUpdateRecord>(), Array.Empty<DeltaRemoveRecord>());
                PatchUInt8(packet, HeaderSize + offsetInRecord, 2);

                Assert.That(Decode(packet, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed),
                    $"boolean byte at record offset {offsetInRecord} must be 0 or 1");
            }

            // UPDATE record: the HasMoveTarget flag byte follows the id delta and the mask.
            var update = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[] { MakeUpdate(1, UnitDirtyMask.HasMoveTarget, hasMoveTarget: true) },
                Array.Empty<DeltaRemoveRecord>());
            AssertPayload(update, new byte[] { 0x02, 0x08, 0x01 }, "has-move-target-only update");

            PatchUInt8(update, HeaderSize + 2, 5);
            Assert.That(Decode(update, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed));

            // UPDATE record: the AutoAcquire flag byte.
            var autoAcquire = EncodeOk(
                Array.Empty<DeltaAddRecord>(),
                new[] { MakeUpdate(1, UnitDirtyMask.AutoAcquire, autoAcquireEnemies: true) },
                Array.Empty<DeltaRemoveRecord>());
            PatchUInt8(autoAcquire, HeaderSize + 2, 9);
            Assert.That(Decode(autoAcquire, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed));
        }

        [Test]
        public void Decode_RejectsTrailingBytes()
        {
            var packet = BuildReferencePacket();
            var padded = new byte[packet.Length + 1];
            Array.Copy(packet, padded, packet.Length);

            Assert.That(Decode(padded, out _, out _, out _, out _), Is.EqualTo(DeltaCodecResult.Malformed),
                "a v1 packet occupies exactly the bytes it declares");
        }

        [Test]
        public void Encode_RejectsInconsistentHeader()
        {
            var buffer = new byte[DeltaSnapshotProtocol.MaxPacketBytes];
            var adds = new[] { MakeAdd(1) };
            var noUpdates = Array.Empty<DeltaUpdateRecord>();
            var noRemoves = Array.Empty<DeltaRemoveRecord>();

            var cases = new (string Because, DeltaSnapshotHeader Header, int AddCount)[]
            {
                ("counts below the section length",
                    new DeltaSnapshotHeader(0x03, 1, 120, 100, 0, 1, 0, 0, 0, 0, 0), 1),
                ("counts above the section length",
                    new DeltaSnapshotHeader(0x03, 1, 120, 100, 0, 1, 0, 0, 2, 0, 0), 1),
                ("wrong message type",
                    new DeltaSnapshotHeader(0x01, 1, 120, 100, 0, 1, 0, 0, 1, 0, 0), 1),
                ("wrong delta protocol version",
                    new DeltaSnapshotHeader(0x03, 2, 120, 100, 0, 1, 0, 0, 1, 0, 0), 1),
                ("zero part count",
                    new DeltaSnapshotHeader(0x03, 1, 120, 100, 0, 0, 0, 0, 1, 0, 0), 1),
                ("part index outside the part count",
                    new DeltaSnapshotHeader(0x03, 1, 120, 100, 1, 1, 0, 0, 1, 0, 0), 1),
                ("reserved flag bits",
                    new DeltaSnapshotHeader(0x03, 1, 120, 100, 0, 1, 0x04, 0, 1, 0, 0), 1),
                ("base tick after tick",
                    new DeltaSnapshotHeader(0x03, 1, 100, 120, 0, 1, 0, 0, 1, 0, 0), 1)
            };

            foreach (var (because, header, addCount) in cases)
            {
                var section = addCount == 1 ? adds : Array.Empty<DeltaAddRecord>();
                var result = DeltaSnapshotWireCodec.TryEncode(
                    header, section, noUpdates, noRemoves, buffer, out var written);

                Assert.That(result, Is.EqualTo(DeltaCodecResult.InvalidArgument), because);
                Assert.That(written, Is.Zero, because);
            }
        }

        [Test]
        public void Decode_BitFlipFuzz_NeverThrowsAndPreservesInvariants()
        {
            var packet = BuildReferencePacket();
            var corrupted = new byte[packet.Length];
            var addSink = new DeltaAddRecord[MaxFuzzRecords];
            var updateSink = new DeltaUpdateRecord[MaxFuzzRecords];
            var removeSink = new DeltaRemoveRecord[MaxFuzzRecords];
            var random = new Random(FuzzSeed);

            var decoded = 0;
            var rejected = 0;

            for (var iteration = 0; iteration < 4000; iteration++)
            {
                Array.Copy(packet, corrupted, packet.Length);
                corrupted[random.Next(corrupted.Length)] ^= (byte)(1 << random.Next(8));

                var headerResult = DeltaSnapshotWireCodec.TryDecodeHeader(corrupted, out var header);
                if (headerResult != DeltaCodecResult.Ok)
                {
                    Assert.That(Enum.IsDefined(typeof(DeltaCodecResult), headerResult), Is.True);
                    rejected++;
                    continue;
                }

                var result = DeltaSnapshotWireCodec.TryDecode(
                    corrupted,
                    out header,
                    addSink.AsSpan(0, SinkLength(header.AddCount)),
                    updateSink.AsSpan(0, SinkLength(header.UpdateCount)),
                    removeSink.AsSpan(0, SinkLength(header.RemoveCount)));

                Assert.That(Enum.IsDefined(typeof(DeltaCodecResult), result), Is.True,
                    "the codec must answer with a defined result for every corrupted packet");

                if (result != DeltaCodecResult.Ok)
                {
                    rejected++;
                    continue;
                }

                decoded++;
                AssertAddIdsAscending(addSink.AsSpan(0, header.AddCount));
                AssertUpdateIdsAscending(updateSink.AsSpan(0, header.UpdateCount));
                AssertRemoveIdsAscending(removeSink.AsSpan(0, header.RemoveCount));
                Assert.That(header.BaseTick, Is.LessThanOrEqualTo(header.Tick));
            }

            TestContext.Out.WriteLine(
                $"delta codec bit-flip fuzz: decoded={decoded} rejected={rejected} of 4000");
            Assert.That(decoded, Is.GreaterThan(0),
                "single-bit flips outside the header must still leave decodable packets");
            Assert.That(rejected, Is.GreaterThan(0),
                "the corpus must contain corruption the codec rejects");
        }

        [Test]
        public void Decode_RandomGarbage_NeverThrows()
        {
            var random = new Random(FuzzSeed + 1);
            var garbage = new byte[DeltaSnapshotProtocol.MaxPacketBytes + 64];
            var addSink = new DeltaAddRecord[MaxFuzzRecords];
            var updateSink = new DeltaUpdateRecord[MaxFuzzRecords];
            var removeSink = new DeltaRemoveRecord[MaxFuzzRecords];

            for (var iteration = 0; iteration < 2000; iteration++)
            {
                random.NextBytes(garbage);
                var length = random.Next(0, garbage.Length + 1);
                var span = garbage.AsSpan(0, length);

                var headerResult = DeltaSnapshotWireCodec.TryDecodeHeader(span, out var header);
                Assert.That(Enum.IsDefined(typeof(DeltaCodecResult), headerResult), Is.True);
                Assert.That(headerResult, Is.Not.EqualTo(DeltaCodecResult.Ok),
                    $"random bytes of length {length} must never pass header validation");

                var result = DeltaSnapshotWireCodec.TryDecode(
                    span,
                    out header,
                    addSink.AsSpan(0, SinkLength(header.AddCount)),
                    updateSink.AsSpan(0, SinkLength(header.UpdateCount)),
                    removeSink.AsSpan(0, SinkLength(header.RemoveCount)));

                Assert.That(Enum.IsDefined(typeof(DeltaCodecResult), result), Is.True);
                Assert.That(result, Is.Not.EqualTo(DeltaCodecResult.Ok),
                    $"random bytes of length {length} must never decode into records");
            }
        }

        // ---------------------------------------------------------------
        // Zero-GC hot path
        // ---------------------------------------------------------------

        [Test]
        public void GcAllocationRecorder_ObservesKnownAllocation_Control()
        {
            // The editor's Mono runtime reports 0 from
            // GC.GetAllocatedBytesForCurrentThread(), so the zero-GC evidence uses
            // Unity's own GC.Alloc recorder. This control proves the recorder
            // observes a known allocation, i.e. the next test is not vacuous.
            byte[] ballast = null;

            Assert.That(
                () =>
                {
                    ballast = new byte[4096];
                },
                UnityEngine.TestTools.Constraints.Is.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");

            Assert.That(ballast, Is.Not.Null);
            Assert.That(ballast.Length, Is.EqualTo(4096));
        }

        [Test]
        public void EncodeDecode_HotPath_DoesNotAllocate()
        {
            var adds = new[]
            {
                MakeAdd(1), MakeAdd(2, owner: 2), MakeAdd(9000, owner: 3, posX: -5, posZ: 7)
            };
            var updates = new[]
            {
                MakeUpdate(9001, UnitDirtyMask.Position, posX: 11, posZ: 22),
                MakeUpdate(9002, UnitDirtyMask.Health | UnitDirtyMask.AttackTarget,
                    currentHealth: 5, attackTarget: 9001),
                MakeUpdate(9003, UnitDirtyMask.HasMoveTarget | UnitDirtyMask.MoveTarget,
                    hasMoveTarget: true, moveTargetX: 3, moveTargetZ: 4)
            };
            var removes = new[] { MakeRemove(9500), MakeRemove(9600, DeltaRemoveCause.FogOfWarHidden) };
            var header = MakeHeader(
                adds.Length, updates.Length, removes.Length,
                tick: 2000, baseTick: 1990, flags: DeltaFlags.HasChecksum, stateChecksum: 0x1234ABCD);

            var buffer = new byte[DeltaSnapshotWireCodec.GetMaxEncodedSize(adds.Length, updates.Length, removes.Length)];
            var addSink = new DeltaAddRecord[adds.Length];
            var updateSink = new DeltaUpdateRecord[updates.Length];
            var removeSink = new DeltaRemoveRecord[removes.Length];

            Assert.That(
                DeltaSnapshotWireCodec.GetEncodedSize(adds, updates, removes, out var expectedSize),
                Is.EqualTo(DeltaCodecResult.Ok));

            const int warmup = 200;
            const int measured = 2000;

            var warmupResult = RunHotPath(
                warmup, header, adds, updates, removes, buffer, addSink, updateSink, removeSink);
            Assert.That(warmupResult.Succeeded, Is.EqualTo(warmup),
                "the warm-up must succeed before the allocation window opens");

            var hotPath = (Succeeded: 0, WrittenTotal: 0L);

            // Block-bodied lambda on purpose: the negated constraint only receives
            // the delegate itself when NUnit binds Assert.That(TestDelegate, ...).
            Assert.That(
                () =>
                {
                    hotPath = RunHotPath(
                        measured, header, adds, updates, removes, buffer, addSink, updateSink, removeSink);
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "delta serialization and deserialization must be allocation-free (zero-GC hot path)");

            Assert.That(hotPath.Succeeded, Is.EqualTo(measured), "every hot-path iteration must encode and decode");
            Assert.That(hotPath.WrittenTotal, Is.EqualTo((long)measured * expectedSize));

            Assert.That(addSink, Is.EqualTo(adds));
            Assert.That(updateSink, Is.EqualTo(updates));
            Assert.That(removeSink, Is.EqualTo(removes));
        }

        private static (int Succeeded, long WrittenTotal) RunHotPath(
            int iterations,
            DeltaSnapshotHeader header,
            DeltaAddRecord[] adds,
            DeltaUpdateRecord[] updates,
            DeltaRemoveRecord[] removes,
            byte[] buffer,
            DeltaAddRecord[] addSink,
            DeltaUpdateRecord[] updateSink,
            DeltaRemoveRecord[] removeSink)
        {
            var succeeded = 0;
            var writtenTotal = 0L;

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var encode = DeltaSnapshotWireCodec.TryEncode(
                    header, adds, updates, removes, buffer, out var written);
                var decode = DeltaSnapshotWireCodec.TryDecode(
                    buffer.AsSpan(0, written), out var decoded, addSink, updateSink, removeSink);

                if (encode == DeltaCodecResult.Ok &&
                    decode == DeltaCodecResult.Ok &&
                    decoded == header)
                {
                    succeeded++;
                    writtenTotal += written;
                }
            }

            return (succeeded, writtenTotal);
        }

        private static int SinkLength(int declaredCount) => Math.Min(declaredCount, MaxFuzzRecords);

        private static void Fill(byte[] buffer, byte value)
        {
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = value;
            }
        }

        private static void PatchUInt16(byte[] packet, int offset, ushort value)
        {
            packet[offset] = (byte)(value & 0xFF);
            packet[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void AssertAddIdsAscending(ReadOnlySpan<DeltaAddRecord> records)
        {
            var previous = 0UL;
            for (var index = 0; index < records.Length; index++)
            {
                Assert.That(records[index].Entity.Value, Is.GreaterThan(previous),
                    "decoded add ids must stay strictly ascending");
                previous = records[index].Entity.Value;
            }
        }

        private static void AssertUpdateIdsAscending(ReadOnlySpan<DeltaUpdateRecord> records)
        {
            var previous = 0UL;
            for (var index = 0; index < records.Length; index++)
            {
                Assert.That(records[index].Entity.Value, Is.GreaterThan(previous),
                    "decoded update ids must stay strictly ascending");
                previous = records[index].Entity.Value;
            }
        }

        private static void AssertRemoveIdsAscending(ReadOnlySpan<DeltaRemoveRecord> records)
        {
            var previous = 0UL;
            for (var index = 0; index < records.Length; index++)
            {
                Assert.That(records[index].Entity.Value, Is.GreaterThan(previous),
                    "decoded remove ids must stay strictly ascending");
                previous = records[index].Entity.Value;
            }
        }
    }
}
