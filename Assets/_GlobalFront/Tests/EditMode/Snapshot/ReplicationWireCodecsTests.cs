using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Snapshot;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Snapshot
{
    /// <summary>
    /// Isolated EditMode coverage of the step 2.6.4 wire codecs (ADR-010): the
    /// keyframe slice framing that streams a baseline over C2 and the
    /// replication request record that rides on C0. Round-trips, header
    /// validation, part addressing, truncation/corruption robustness and the
    /// ADD-record run helpers shared with the delta codec.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationWireCodecsTests
    {
        private static DeltaAddRecord Unit(ulong id, int posX = 100) =>
            new DeltaAddRecord(
                new EntityId(id),
                new PlayerId(1),
                new WorldPointMm(posX, 200),
                100,
                false,
                new WorldPointMm(0, 0),
                new EntityId(0),
                false);

        private static KeyframeSliceHeader SliceHeader(
            ulong tick = 41,
            ushort seq = 2,
            ushort partIndex = 0,
            ushort partCount = 3,
            uint total = 7,
            ushort sliceCount = 3) =>
            new KeyframeSliceHeader(tick, seq, partIndex, partCount, total, sliceCount);

        // ------------------------------------------------------------------
        // Keyframe slice: sizes and helpers
        // ------------------------------------------------------------------

        [Test]
        public void SliceSize_HeaderPlusRecords()
        {
            Assert.That(KeyframeSliceCodec.HeaderSizeBytes, Is.EqualTo(24));
            Assert.That(KeyframeSliceCodec.GetSliceSize(0), Is.EqualTo(24));
            Assert.That(KeyframeSliceCodec.GetSliceSize(3), Is.EqualTo(24 + 3 * 40));
            Assert.That(KeyframeSliceCodec.GetSliceSize(-1), Is.EqualTo(-1));
            Assert.That(
                KeyframeSliceCodec.GetSliceSize(ushort.MaxValue + 1), Is.EqualTo(-1));
        }

        [Test]
        public void SliceBudget_DerivesRecordCount()
        {
            Assert.That(
                KeyframeSliceCodec.MaxRecordsForSliceBudget(KeyframeSliceCodec.HeaderSizeBytes),
                Is.EqualTo(0));
            Assert.That(
                KeyframeSliceCodec.MaxRecordsForSliceBudget(KeyframeSliceCodec.HeaderSizeBytes + 38),
                Is.EqualTo(0));
            Assert.That(
                KeyframeSliceCodec.MaxRecordsForSliceBudget(KeyframeSliceCodec.HeaderSizeBytes + 39),
                Is.EqualTo(0),
                "OD-29 grew the framed record to 40 bytes: a v1-sized budget no longer fits one record");
            Assert.That(
                KeyframeSliceCodec.MaxRecordsForSliceBudget(KeyframeSliceCodec.HeaderSizeBytes + 40),
                Is.EqualTo(1));
            Assert.That(
                KeyframeSliceCodec.MaxRecordsForSliceBudget(16384),
                Is.EqualTo((16384 - 24) / 40));
        }

        // ------------------------------------------------------------------
        // Keyframe slice: encode/decode round-trip
        // ------------------------------------------------------------------

        [Test]
        public void Slice_RoundTrip_PreservesHeaderAndRecords()
        {
            var header = SliceHeader(tick: 77, seq: 9, partIndex: 2, partCount: 5, total: 11, sliceCount: 3);
            var adds = new[] { Unit(1), Unit(2, posX: 300), Unit(5, posX: 900) };
            var buffer = new byte[KeyframeSliceCodec.GetSliceSize(3)];

            Assert.That(
                KeyframeSliceCodec.TryEncodeSlice(header, adds, buffer, out var written),
                Is.EqualTo(KeyframeSliceCodecResult.Ok));
            Assert.That(written, Is.EqualTo(buffer.Length));

            var decoded = new DeltaAddRecord[8];
            Assert.That(
                KeyframeSliceCodec.TryDecodeSlice(
                    buffer.AsSpan(0, written), decoded, out var decodedHeader, out var read),
                Is.EqualTo(KeyframeSliceCodecResult.Ok));
            Assert.That(read, Is.EqualTo(3));
            Assert.That(decodedHeader.Tick, Is.EqualTo(77UL));
            Assert.That(decodedHeader.KeyframeSeq, Is.EqualTo((ushort)9));
            Assert.That(decodedHeader.PartIndex, Is.EqualTo((ushort)2));
            Assert.That(decodedHeader.PartCount, Is.EqualTo((ushort)5));
            Assert.That(decodedHeader.TotalUnitCount, Is.EqualTo(11U));
            Assert.That(decodedHeader.SliceAddCount, Is.EqualTo((ushort)3));

            for (var index = 0; index < 3; index++)
            {
                Assert.That(decoded[index].Entity, Is.EqualTo(adds[index].Entity));
                Assert.That(decoded[index].Position, Is.EqualTo(adds[index].Position));
                Assert.That(decoded[index].CurrentHealth, Is.EqualTo(adds[index].CurrentHealth));
            }
        }

        [Test]
        public void Slice_RoundTrip_PreservesUnitKind()
        {
            // A resync rebuilds the client world from keyframe ADD records only, so
            // a kind that survives deltas but not the slice path would leave every
            // re-attached client without meshes or health denominators.
            Assert.That(KeyframeSliceCodec.SliceVersion, Is.EqualTo((byte)2),
                "the framing version moved with the record it frames");

            var adds = new[]
            {
                new DeltaAddRecord(
                    new EntityId(1), new PlayerId(1), new WorldPointMm(0, 0), 100,
                    false, new WorldPointMm(0, 0), new EntityId(0), false, UnitKinds.Tank),
                new DeltaAddRecord(
                    new EntityId(2), new PlayerId(2), new WorldPointMm(500, 0), 100,
                    false, new WorldPointMm(0, 0), new EntityId(0), false, UnitKinds.Unknown),
            };

            var header = SliceHeader(tick: 90, seq: 3, partIndex: 0, partCount: 1, total: 2, sliceCount: 2);
            var buffer = new byte[KeyframeSliceCodec.GetSliceSize(2)];
            Assert.That(
                KeyframeSliceCodec.TryEncodeSlice(header, adds, buffer, out var written),
                Is.EqualTo(KeyframeSliceCodecResult.Ok));

            var decoded = new DeltaAddRecord[2];
            Assert.That(
                KeyframeSliceCodec.TryDecodeSlice(
                    buffer.AsSpan(0, written), decoded, out _, out var read),
                Is.EqualTo(KeyframeSliceCodecResult.Ok));
            Assert.That(read, Is.EqualTo(2));
            Assert.That(decoded[0].UnitKind, Is.EqualTo(UnitKinds.Tank));
            Assert.That(decoded[1].UnitKind, Is.EqualTo(UnitKinds.Unknown));
        }

        [Test]
        public void Slice_HeaderOnly_EncodesAndDecodes()
        {
            var header = SliceHeader(tick: 5, seq: 1, partIndex: 0, partCount: 1, total: 0, sliceCount: 0);
            var buffer = new byte[KeyframeSliceCodec.GetSliceSize(0)];

            Assert.That(
                KeyframeSliceCodec.TryEncodeSlice(
                    header, ReadOnlySpan<DeltaAddRecord>.Empty, buffer, out var written),
                Is.EqualTo(KeyframeSliceCodecResult.Ok));
            Assert.That(written, Is.EqualTo(KeyframeSliceCodec.HeaderSizeBytes));

            Assert.That(
                KeyframeSliceCodec.TryDecodeSlice(
                    buffer, new DeltaAddRecord[1], out var decoded, out var read),
                Is.EqualTo(KeyframeSliceCodecResult.Ok));
            Assert.That(read, Is.EqualTo(0));
            Assert.That(decoded.TotalUnitCount, Is.EqualTo(0U));
        }

        [Test]
        public void Slice_TooSmallDestination_IsRejectedWithoutWriting()
        {
            var header = SliceHeader(sliceCount: 2);
            var buffer = new byte[KeyframeSliceCodec.GetSliceSize(2) - 1];

            Assert.That(
                KeyframeSliceCodec.TryEncodeSlice(
                    header, new[] { Unit(1), Unit(2) }, buffer, out var written),
                Is.EqualTo(KeyframeSliceCodecResult.BufferTooSmall));
            Assert.That(written, Is.EqualTo(0));
            Assert.That(buffer, Is.EquivalentTo(new byte[buffer.Length]));
        }

        [Test]
        public void Slice_InconsistentCounts_AreRejectedAsInvalidArgument()
        {
            var buffer = new byte[128];
            var mismatched = SliceHeader(sliceCount: 2);

            Assert.That(
                KeyframeSliceCodec.TryEncodeSlice(
                    mismatched, new[] { Unit(1) }, buffer, out _),
                Is.EqualTo(KeyframeSliceCodecResult.InvalidArgument));

            var badParts = SliceHeader(partIndex: 3, partCount: 3);
            Assert.That(
                KeyframeSliceCodec.TryEncodeSlice(
                    badParts, new[] { Unit(1) }, buffer, out _),
                Is.EqualTo(KeyframeSliceCodecResult.InvalidArgument));
        }

        [Test]
        public void Slice_UnorderedEntities_AreRejected()
        {
            var buffer = new byte[KeyframeSliceCodec.GetSliceSize(2)];

            Assert.That(
                DeltaSnapshotWireCodec.TryEncodeAddRecords(
                    new[] { Unit(7), Unit(3) }, buffer, out _),
                Is.EqualTo(DeltaCodecResult.Malformed));
        }

        // ------------------------------------------------------------------
        // Keyframe slice: header validation
        // ------------------------------------------------------------------

        [Test]
        public void SliceHeader_Truncated_IsRejected()
        {
            var buffer = new byte[KeyframeSliceCodec.HeaderSizeBytes - 1];

            Assert.That(
                KeyframeSliceCodec.TryDecodeHeader(buffer, out _),
                Is.EqualTo(KeyframeSliceCodecResult.BufferTooSmall));
        }

        [Test]
        public void SliceHeader_WrongMessageType_IsRejected()
        {
            var header = SliceHeader();
            var buffer = new byte[KeyframeSliceCodec.HeaderSizeBytes];
            Assert.That(
                KeyframeSliceCodec.TryEncodeHeader(buffer, in header),
                Is.EqualTo(KeyframeSliceCodec.HeaderSizeBytes));
            buffer[0] = 0x03;

            Assert.That(
                KeyframeSliceCodec.TryDecodeHeader(buffer, out _),
                Is.EqualTo(KeyframeSliceCodecResult.Malformed));
        }

        [Test]
        public void SliceHeader_WrongVersion_IsRejected()
        {
            var header = SliceHeader();
            var buffer = new byte[KeyframeSliceCodec.HeaderSizeBytes];
            KeyframeSliceCodec.TryEncodeHeader(buffer, in header);
            buffer[1] = 0xFF;

            Assert.That(
                KeyframeSliceCodec.TryDecodeHeader(buffer, out _),
                Is.EqualTo(KeyframeSliceCodecResult.UnsupportedVersion));
        }

        [Test]
        public void SliceHeader_ReservedBytes_MustBeZero()
        {
            var header = SliceHeader();
            var buffer = new byte[KeyframeSliceCodec.HeaderSizeBytes];
            KeyframeSliceCodec.TryEncodeHeader(buffer, in header);
            buffer[22] = 1;

            Assert.That(
                KeyframeSliceCodec.TryDecodeHeader(buffer, out _),
                Is.EqualTo(KeyframeSliceCodecResult.Malformed));
        }

        [Test]
        public void SliceHeader_PartIndexMustBeInsidePartCount()
        {
            var header = SliceHeader(partIndex: 5, partCount: 3);
            var buffer = new byte[KeyframeSliceCodec.HeaderSizeBytes];

            Assert.That(
                KeyframeSliceCodec.TryEncodeHeader(buffer, in header),
                Is.EqualTo(-1));

            var zeroParts = SliceHeader(partIndex: 0, partCount: 0);
            Assert.That(
                KeyframeSliceCodec.TryEncodeHeader(buffer, in zeroParts),
                Is.EqualTo(-1));
        }

        [Test]
        public void Slice_TrailingBytes_AreRejected()
        {
            var header = SliceHeader(sliceCount: 1);
            var buffer = new byte[KeyframeSliceCodec.GetSliceSize(1) + 3];
            KeyframeSliceCodec.TryEncodeSlice(header, new[] { Unit(1) }, buffer, out _);

            Assert.That(
                KeyframeSliceCodec.TryDecodeSlice(
                    buffer, new DeltaAddRecord[4], out _, out _),
                Is.EqualTo(KeyframeSliceCodecResult.Malformed));
        }

        [Test]
        public void Slice_TruncatedRecords_AreRejected()
        {
            var header = SliceHeader(sliceCount: 2);
            var exact = new byte[KeyframeSliceCodec.GetSliceSize(2)];
            KeyframeSliceCodec.TryEncodeSlice(header, new[] { Unit(1), Unit(2) }, exact, out _);
            var truncated = exact.AsSpan(0, exact.Length - 5).ToArray();

            Assert.That(
                KeyframeSliceCodec.TryDecodeSlice(
                    truncated, new DeltaAddRecord[4], out _, out _),
                Is.EqualTo(KeyframeSliceCodecResult.BufferTooSmall));
        }

        // ------------------------------------------------------------------
        // Replication request
        // ------------------------------------------------------------------

        [Test]
        public void Request_RoundTrip_PreservesAllFields()
        {
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.DeltaResume,
                attempt: 2,
                lastAppliedTick: 1337,
                baseKeyframeTick: 1000,
                keyframeTick: 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes];

            Assert.That(
                ReplicationRequestCodec.TryEncode(request, buffer, out var written),
                Is.EqualTo(ReplicationRequestCodecResult.Ok));
            Assert.That(written, Is.EqualTo(ReplicationRequestCodec.SizeBytes));
            Assert.That(ReplicationRequestCodec.SizeBytes, Is.EqualTo(28));
            Assert.That(buffer[0], Is.EqualTo(ReplicationRequestCodec.MessageType));
            Assert.That(buffer[0], Is.EqualTo(0x06));

            Assert.That(
                ReplicationRequestCodec.TryDecode(buffer, out var decoded),
                Is.EqualTo(ReplicationRequestCodecResult.Ok));
            Assert.That(decoded.Kind, Is.EqualTo(ReplicationRequestWireKind.DeltaResume));
            Assert.That(decoded.Attempt, Is.EqualTo((byte)2));
            Assert.That(decoded.LastAppliedTick, Is.EqualTo(1337UL));
            Assert.That(decoded.BaseKeyframeTick, Is.EqualTo(1000UL));
            Assert.That(decoded.KeyframeTick, Is.EqualTo(0UL));
        }

        [Test]
        public void Request_SnapshotRequest_RoundTrips()
        {
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.SnapshotRequest, 1, 0, 0, 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes];

            ReplicationRequestCodec.TryEncode(request, buffer, out _);
            Assert.That(
                ReplicationRequestCodec.TryDecode(buffer, out var decoded),
                Is.EqualTo(ReplicationRequestCodecResult.Ok));
            Assert.That(decoded.IsSnapshotRequest, Is.True);
        }

        [Test]
        public void Request_TooSmallDestination_IsRejected()
        {
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.SnapshotRequest, 1, 0, 0, 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes - 1];

            Assert.That(
                ReplicationRequestCodec.TryEncode(request, buffer, out var written),
                Is.EqualTo(ReplicationRequestCodecResult.BufferTooSmall));
            Assert.That(written, Is.EqualTo(0));
        }

        [Test]
        public void Request_TruncatedSource_IsRejected()
        {
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.DeltaResume, 1, 10, 5, 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes];
            ReplicationRequestCodec.TryEncode(request, buffer, out _);

            Assert.That(
                ReplicationRequestCodec.TryDecode(
                    buffer.AsSpan(0, ReplicationRequestCodec.SizeBytes - 1), out _),
                Is.EqualTo(ReplicationRequestCodecResult.BufferTooSmall));
        }

        [Test]
        public void Request_UnknownKind_IsRejected()
        {
            var buffer = new byte[ReplicationRequestCodec.SizeBytes];
            buffer[0] = 0x06;
            buffer[1] = 0x7F;

            Assert.That(
                ReplicationRequestCodec.TryDecode(buffer, out _),
                Is.EqualTo(ReplicationRequestCodecResult.UnsupportedKind));
        }

        [Test]
        public void Request_WrongMessageType_IsRejected()
        {
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.DeltaResume, 1, 10, 5, 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes];
            ReplicationRequestCodec.TryEncode(request, buffer, out _);
            buffer[0] = 0x05;

            Assert.That(
                ReplicationRequestCodec.TryDecode(buffer, out _),
                Is.EqualTo(ReplicationRequestCodecResult.Malformed));
        }

        [Test]
        public void Request_ReservedByte_MustBeZero()
        {
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.DeltaResume, 1, 10, 5, 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes];
            ReplicationRequestCodec.TryEncode(request, buffer, out _);
            buffer[3] = 1;

            Assert.That(
                ReplicationRequestCodec.TryDecode(buffer, out _),
                Is.EqualTo(ReplicationRequestCodecResult.Malformed));
        }

        [Test]
        public void Request_TrailingBytes_AreIgnored()
        {
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.DeltaResume, 1, 10, 5, 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes + 4];
            ReplicationRequestCodec.TryEncode(request, buffer, out _);

            Assert.That(
                ReplicationRequestCodec.TryDecode(buffer, out var decoded),
                Is.EqualTo(ReplicationRequestCodecResult.Ok));
            Assert.That(decoded.LastAppliedTick, Is.EqualTo(10UL));
        }

        [Test]
        public void Request_NoneKind_IsRejectedOnEncode()
        {
            var request = new ReplicationRequestWire(
                ReplicationRequestWireKind.None, 1, 0, 0, 0);
            var buffer = new byte[ReplicationRequestCodec.SizeBytes];

            Assert.That(
                ReplicationRequestCodec.TryEncode(request, buffer, out _),
                Is.EqualTo(ReplicationRequestCodecResult.UnsupportedKind));
        }

        // ------------------------------------------------------------------
        // ADD record run helpers
        // ------------------------------------------------------------------

        [Test]
        public void AddRecords_Run_RoundTrips()
        {
            var adds = new[] { Unit(1), Unit(4, posX: 500), Unit(9, posX: 900) };
            var buffer = new byte[3 * DeltaSnapshotProtocol.AddRecordSizeBytes];

            Assert.That(
                DeltaSnapshotWireCodec.TryEncodeAddRecords(adds, buffer, out var written),
                Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(written, Is.EqualTo(buffer.Length));

            var decoded = new DeltaAddRecord[3];
            Assert.That(
                DeltaSnapshotWireCodec.TryDecodeAddRecords(buffer, 3, decoded, out var read),
                Is.EqualTo(DeltaCodecResult.Ok));
            Assert.That(read, Is.EqualTo(3));
            for (var index = 0; index < 3; index++)
            {
                Assert.That(decoded[index].Entity, Is.EqualTo(adds[index].Entity));
                Assert.That(decoded[index].Position, Is.EqualTo(adds[index].Position));
            }
        }

        [Test]
        public void AddRecords_Run_TooSmallDestination_IsRejected()
        {
            var buffer = new byte[DeltaSnapshotProtocol.AddRecordSizeBytes];

            Assert.That(
                DeltaSnapshotWireCodec.TryEncodeAddRecords(
                    new[] { Unit(1), Unit(2) }, buffer, out var written),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall));
            Assert.That(written, Is.EqualTo(0));
        }

        [Test]
        public void AddRecords_Run_CountMismatch_IsRejected()
        {
            var adds = new[] { Unit(1), Unit(2) };
            var buffer = new byte[2 * DeltaSnapshotProtocol.AddRecordSizeBytes];
            DeltaSnapshotWireCodec.TryEncodeAddRecords(adds, buffer, out _);
            var decoded = new DeltaAddRecord[4];

            Assert.That(
                DeltaSnapshotWireCodec.TryDecodeAddRecords(buffer, 1, decoded, out _),
                Is.EqualTo(DeltaCodecResult.Malformed));
            Assert.That(
                DeltaSnapshotWireCodec.TryDecodeAddRecords(
                    buffer.AsSpan(0, DeltaSnapshotProtocol.AddRecordSizeBytes), 2, decoded, out _),
                Is.EqualTo(DeltaCodecResult.BufferTooSmall));
        }

        [Test]
        public void AddRecords_Run_NegativeCount_IsInvalidArgument()
        {
            Assert.That(
                DeltaSnapshotWireCodec.TryDecodeAddRecords(
                    ReadOnlySpan<byte>.Empty, -1, new DeltaAddRecord[1], out _),
                Is.EqualTo(DeltaCodecResult.InvalidArgument));
        }
    }
}
