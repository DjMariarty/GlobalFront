using System;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Snapshot;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Client.Replication
{
    /// <summary>
    /// Client feedback contract (Phase 2.6, step 2.6.3, ADR-010 / R&amp;D §4.2–4.3):
    /// the <see cref="SnapshotAck"/> wire record and the generator that paces it
    /// at the 10 Hz base cadence.
    ///
    /// Wire note: R&amp;D §4.2 fixes the field offsets 0 / 1 / 2 / 10 / 18 / 26,
    /// which total 34 bytes; the prose figure "33 bytes" (and the derived
    /// "33 B x 10 Hz = 330 B/s") does not match those offsets. The offsets are
    /// normative here, so <see cref="SnapshotAckCodec.SizeBytes"/> is 34 and the
    /// uplink budget is 340 B/s. Recorded as a documentation conflict.
    /// </summary>
    [TestFixture]
    public sealed class ReplicationFeedbackGeneratorTests
    {
        // ---------------------------------------------------------------
        // SnapshotAck wire record
        // ---------------------------------------------------------------

        [Test]
        public void Codec_GoldenBytes_MatchTheDocumentedOffsets()
        {
            var ack = new SnapshotAck(
                (byte)(AckHealthFlags.GapPresent | AckHealthFlags.BaseStale),
                0x0102030405060708UL,
                0x1112131415161718UL,
                12345UL,
                0b1011UL);
            var buffer = new byte[SnapshotAckCodec.SizeBytes];

            var result = SnapshotAckCodec.TryEncode(ack, buffer, out var written);

            Assert.That(result, Is.EqualTo(SnapshotAckCodecResult.Ok));
            Assert.That(written, Is.EqualTo(34));
            Assert.That(SnapshotAckCodec.SizeBytes, Is.EqualTo(34));
            Assert.That(buffer, Is.EqualTo(new byte[]
            {
                0x05,                                     //  0 MessageType = SnapshotAck
                0x05,                                     //  1 HealthFlags = GapPresent | BaseStale
                0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,   //  2 LastAppliedTick
                0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,   // 10 BaseKeyframeTick
                0x39, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,   // 18 MissingBase = 12345
                0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00    // 26 MissingBitmap = 0b1011
            }));
        }

        [Test]
        public void Codec_RoundTrip_IsBitExact()
        {
            var cases = new[]
            {
                new SnapshotAck(0, 0UL, 0UL, 0UL, 0UL),
                SnapshotAck.CreateProgress(1234UL, 1200UL),
                SnapshotAck.CreateGap(10UL, 5UL, 11UL, 0b111UL, false, false),
                SnapshotAck.CreateGap(10UL, 5UL, 11UL, ulong.MaxValue, true, true),
                new SnapshotAck((byte)AckHealthFlags.BaseStale, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)
            };

            var buffer = new byte[SnapshotAckCodec.SizeBytes];
            foreach (var expected in cases)
            {
                Assert.That(SnapshotAckCodec.TryEncode(expected, buffer, out var written),
                    Is.EqualTo(SnapshotAckCodecResult.Ok));
                Assert.That(written, Is.EqualTo(SnapshotAckCodec.SizeBytes));
                Assert.That(SnapshotAckCodec.TryDecode(buffer, out var actual),
                    Is.EqualTo(SnapshotAckCodecResult.Ok));
                Assert.That(actual, Is.EqualTo(expected));
                Assert.That(actual.MessageType, Is.EqualTo(0x05));
            }
        }

        [Test]
        public void Codec_ShortDestination_IsRejectedAndLeftUntouched()
        {
            var buffer = new byte[SnapshotAckCodec.SizeBytes - 1];
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = 0xAB;
            }

            var result = SnapshotAckCodec.TryEncode(
                SnapshotAck.CreateProgress(7UL, 3UL), buffer, out var written);

            Assert.That(result, Is.EqualTo(SnapshotAckCodecResult.BufferTooSmall));
            Assert.That(written, Is.Zero);
            foreach (var b in buffer)
            {
                Assert.That(b, Is.EqualTo(0xAB), "a rejected encode must not write a partial record");
            }
        }

        [Test]
        public void Codec_ShortSource_IsRejected()
        {
            var buffer = new byte[SnapshotAckCodec.SizeBytes];
            Assert.That(SnapshotAckCodec.TryEncode(SnapshotAck.CreateProgress(1UL, 1UL), buffer, out _),
                Is.EqualTo(SnapshotAckCodecResult.Ok));

            Assert.That(SnapshotAckCodec.TryDecode(buffer.AsSpan(0, 33), out _),
                Is.EqualTo(SnapshotAckCodecResult.BufferTooSmall),
                "a truncated record is short, not malformed - same convention as the delta codec");
            Assert.That(SnapshotAckCodec.TryDecode(ReadOnlySpan<byte>.Empty, out _),
                Is.EqualTo(SnapshotAckCodecResult.BufferTooSmall));
        }

        [Test]
        public void Codec_UnknownMessageType_IsRejected()
        {
            var buffer = new byte[SnapshotAckCodec.SizeBytes];
            Assert.That(SnapshotAckCodec.TryEncode(SnapshotAck.CreateProgress(1UL, 1UL), buffer, out _),
                Is.EqualTo(SnapshotAckCodecResult.Ok));
            buffer[0] = 0x03;

            Assert.That(SnapshotAckCodec.TryDecode(buffer, out _),
                Is.EqualTo(SnapshotAckCodecResult.Malformed));
        }

        [Test]
        public void Codec_ReservedHealthBits_AreRejectedInBothDirections()
        {
            var buffer = new byte[SnapshotAckCodec.SizeBytes];
            var ack = new SnapshotAck(1 << 3, 1UL, 1UL, 0UL, 0UL);

            Assert.That(SnapshotAckCodec.TryEncode(ack, buffer, out _),
                Is.EqualTo(SnapshotAckCodecResult.InvalidArgument),
                "bit 3..7 of HealthFlags are reserved in version 1");

            Assert.That(SnapshotAckCodec.TryEncode(SnapshotAck.CreateProgress(1UL, 1UL), buffer, out _),
                Is.EqualTo(SnapshotAckCodecResult.Ok));
            buffer[1] = 0x80;
            Assert.That(SnapshotAckCodec.TryDecode(buffer, out _),
                Is.EqualTo(SnapshotAckCodecResult.Malformed));
        }

        [Test]
        public void Ack_FlagsAndEquality()
        {
            var gap = SnapshotAck.CreateGap(10UL, 5UL, 11UL, 0b1111UL, false, true);

            Assert.That(gap.MessageType, Is.EqualTo(SnapshotAckCodec.MessageType));
            Assert.That(gap.GapPresent, Is.True);
            Assert.That(gap.BaseStale, Is.True);
            Assert.That(gap.BitmapOverflow, Is.False);
            Assert.That(gap.HasGap, Is.True);
            Assert.That(gap.HealthFlags, Is.EqualTo((byte)(AckHealthFlags.GapPresent | AckHealthFlags.BaseStale)));

            var progress = SnapshotAck.CreateProgress(10UL, 5UL);
            Assert.That(progress.HasGap, Is.False);
            Assert.That(progress.HealthFlags, Is.EqualTo((byte)AckHealthFlags.None));
            Assert.That(progress.MissingBase, Is.Zero);
            Assert.That(progress.MissingBitmap, Is.Zero);
            Assert.That(progress, Is.Not.EqualTo(gap));
            Assert.That(progress.Equals((object)gap), Is.False);
            Assert.That(progress.Equals((object)SnapshotAck.CreateProgress(10UL, 5UL)), Is.True);
            Assert.That(progress.GetHashCode(),
                Is.EqualTo(SnapshotAck.CreateProgress(10UL, 5UL).GetHashCode()));
            Assert.That(progress == SnapshotAck.CreateProgress(10UL, 5UL), Is.True);
            Assert.That(progress != gap, Is.True);
            Assert.That(progress.ToString(), Does.Contain("last=10"));
        }

        [Test]
        public void Ack_CreateGap_SetsTheOverflowFlagOnAFullBitmap()
        {
            var overflow = SnapshotAck.CreateGap(0UL, 0UL, 1UL, ulong.MaxValue, true, false);

            Assert.That(overflow.BitmapOverflow, Is.True);
            Assert.That(overflow.GapPresent, Is.True);
            Assert.That(overflow.BaseStale, Is.False);
        }

        // ---------------------------------------------------------------
        // Generator: progress acks at the base cadence
        // ---------------------------------------------------------------

        [Test]
        public void Generator_AppliedTickProducesOneAck()
        {
            var generator = new ReplicationFeedbackGenerator();
            Assert.That(generator.HasPendingAck, Is.False);

            generator.NoteApplied(20UL, 10UL);

            Assert.That(generator.HasPendingAck, Is.True);
            Assert.That(generator.PendingAck.LastAppliedTick, Is.EqualTo(20UL));
            Assert.That(generator.PendingAck.BaseKeyframeTick, Is.EqualTo(10UL));
            Assert.That(generator.PendingAck.HealthFlags, Is.EqualTo((byte)AckHealthFlags.None));

            Assert.That(generator.TryTakeAck(1000, out var ack), Is.True);
            Assert.That(ack.LastAppliedTick, Is.EqualTo(20UL));
            Assert.That(ack.BaseKeyframeTick, Is.EqualTo(10UL));
            Assert.That(generator.HasPendingAck, Is.False);
            Assert.That(generator.SentAckCount, Is.EqualTo(1));
            Assert.That(generator.LastSentMs, Is.EqualTo(1000L));
            Assert.That(generator.TryTakeAck(2000, out _), Is.False, "nothing new happened, nothing to ack");
        }

        [Test]
        public void Generator_RespectsThe100MsCadenceFloor()
        {
            var generator = new ReplicationFeedbackGenerator();
            Assert.That(generator.MinIntervalMs, Is.EqualTo(100L));
            generator.NoteApplied(20UL, 10UL);
            Assert.That(generator.TryTakeAck(1000, out _), Is.True);

            generator.NoteApplied(21UL, 10UL);
            Assert.That(generator.TryTakeAck(1099, out _), Is.False, "10 Hz base cadence");

            Assert.That(generator.TryTakeAck(1100, out var ack), Is.True);
            Assert.That(ack.LastAppliedTick, Is.EqualTo(21UL));
        }

        [Test]
        public void Generator_CoalescesSignalsIntoTheLatestState()
        {
            var generator = new ReplicationFeedbackGenerator();
            generator.NoteApplied(20UL, 10UL);
            generator.NoteApplied(22UL, 10UL);
            generator.NoteApplied(24UL, 10UL);

            Assert.That(generator.CoalescedSignalCount, Is.EqualTo(2));
            Assert.That(generator.HasPendingAck, Is.True);
            Assert.That(generator.TryTakeAck(500, out var ack), Is.True);
            Assert.That(ack.LastAppliedTick, Is.EqualTo(24UL), "latest state wins; no progress is lost");
            Assert.That(generator.SentAckCount, Is.EqualTo(1));
        }

        [Test]
        public void Generator_FirstAckIsNotDelayedByTheCadenceFloor()
        {
            var generator = new ReplicationFeedbackGenerator();
            generator.NoteApplied(1UL, 1UL);

            Assert.That(generator.HasSentAck, Is.False);
            Assert.That(generator.TryTakeAck(0, out var ack), Is.True);
            Assert.That(ack.LastAppliedTick, Is.EqualTo(1UL));
        }

        [Test]
        public void Generator_NonForwardProgressIsNotAcked()
        {
            var generator = new ReplicationFeedbackGenerator();
            generator.NoteApplied(20UL, 10UL);

            generator.NoteApplied(20UL, 10UL);
            generator.NoteApplied(4UL, 10UL);

            Assert.That(generator.PendingAck.LastAppliedTick, Is.EqualTo(20UL));
            Assert.That(generator.IgnoredSignalCount, Is.EqualTo(2));
        }

        // ---------------------------------------------------------------
        // Generator: gap reporting
        // ---------------------------------------------------------------

        [Test]
        public void Generator_DependencyGap_FillsMissingBaseAndBitmap()
        {
            var generator = new ReplicationFeedbackGenerator();
            generator.NoteApplied(10UL, 5UL);
            generator.TryTakeAck(1000, out _);

            // A delta needs BaseTick 13 while the client only holds tick 10:
            // the ticks 11, 12 and 13 are missing.
            generator.NoteDependencyGap(10UL, 5UL, 13UL, false);

            Assert.That(generator.HasPendingAck, Is.True);
            Assert.That(generator.MissingBase, Is.EqualTo(11UL));
            Assert.That(generator.MissingBitmap, Is.EqualTo(0b111UL));
            Assert.That(generator.GapSignalCount, Is.EqualTo(1));
            Assert.That(generator.TryTakeAck(1100, out var ack), Is.True);
            Assert.That(ack.MissingBase, Is.EqualTo(11UL));
            Assert.That(ack.MissingBitmap, Is.EqualTo(0b111UL));
            Assert.That(ack.GapPresent, Is.True);
            Assert.That(ack.BitmapOverflow, Is.False);
            Assert.That(ack.BaseStale, Is.False);
            Assert.That(ack.LastAppliedTick, Is.EqualTo(10UL));
            Assert.That(ack.BaseKeyframeTick, Is.EqualTo(5UL));
        }

        [Test]
        public void Generator_GapOfSixtyFourTicks_ReportsOverflow()
        {
            var generator = new ReplicationFeedbackGenerator();

            generator.NoteDependencyGap(100UL, 50UL, 164UL, false);

            Assert.That(generator.MissingBase, Is.EqualTo(101UL));
            Assert.That(generator.MissingBitmap, Is.EqualTo(ulong.MaxValue));
            Assert.That(generator.PendingAck.BitmapOverflow, Is.True,
                "a full bitmap cannot express the exact gap length, so it says so");
            Assert.That(generator.PendingAck.BaseStale, Is.False);
        }

        [Test]
        public void Generator_GapBeyondTheWindow_SetsBaseStale()
        {
            var generator = new ReplicationFeedbackGenerator();

            generator.NoteDependencyGap(100UL, 50UL, 400UL, true);

            Assert.That(generator.PendingAck.BitmapOverflow, Is.True);
            Assert.That(generator.PendingAck.BaseStale, Is.True);
            Assert.That(generator.PendingAck.GapPresent, Is.True);
        }

        [Test]
        public void Generator_GapWithoutForwardTicks_IsIgnored()
        {
            var generator = new ReplicationFeedbackGenerator();
            generator.NoteApplied(10UL, 5UL);
            generator.TryTakeAck(0, out _);

            generator.NoteDependencyGap(10UL, 5UL, 10UL, false);
            generator.NoteDependencyGap(10UL, 5UL, 4UL, false);

            Assert.That(generator.MissingBase, Is.Zero);
            Assert.That(generator.MissingBitmap, Is.Zero);
            Assert.That(generator.IgnoredSignalCount, Is.EqualTo(2));
        }

        [Test]
        public void Generator_AppliedTickClearsTheGap()
        {
            var generator = new ReplicationFeedbackGenerator();
            generator.NoteDependencyGap(10UL, 5UL, 13UL, false);
            Assert.That(generator.PendingAck.GapPresent, Is.True);

            generator.NoteApplied(13UL, 5UL);

            Assert.That(generator.MissingBase, Is.Zero);
            Assert.That(generator.MissingBitmap, Is.Zero);
            Assert.That(generator.PendingAck.HealthFlags, Is.EqualTo((byte)AckHealthFlags.None));
            Assert.That(generator.PendingAck.LastAppliedTick, Is.EqualTo(13UL));
        }

        [Test]
        public void Generator_BaselineStale_IsSignalledWithoutAGap()
        {
            var generator = new ReplicationFeedbackGenerator();
            generator.NoteApplied(10UL, 5UL);
            generator.TryTakeAck(0, out _);

            generator.NoteBaselineStale(10UL, 5UL);

            Assert.That(generator.HasPendingAck, Is.True);
            Assert.That(generator.PendingAck.BaseStale, Is.True);
            Assert.That(generator.PendingAck.GapPresent, Is.False);
            Assert.That(generator.PendingAck.MissingBase, Is.Zero);
            Assert.That(generator.PendingAck.BaseKeyframeTick, Is.EqualTo(5UL));
            Assert.That(generator.BaselineStaleSignalCount, Is.EqualTo(1));
        }

        [Test]
        public void Generator_Reset_ClearsEverySignal()
        {
            var generator = new ReplicationFeedbackGenerator();
            generator.NoteDependencyGap(10UL, 5UL, 13UL, true);

            generator.Reset();

            Assert.That(generator.HasPendingAck, Is.False);
            Assert.That(generator.LastAppliedTick, Is.Zero);
            Assert.That(generator.BaseKeyframeTick, Is.Zero);
            Assert.That(generator.MissingBase, Is.Zero);
            Assert.That(generator.MissingBitmap, Is.Zero);
            Assert.That(generator.HealthFlags, Is.EqualTo(AckHealthFlags.None));
            Assert.That(generator.HasSentAck, Is.False);
            Assert.That(generator.SentAckCount, Is.Zero);
        }

        [Test]
        public void Generator_CustomCadence_IsHonoured()
        {
            var generator = new ReplicationFeedbackGenerator(200);
            Assert.That(generator.MinIntervalMs, Is.EqualTo(200L));
            generator.NoteApplied(1UL, 1UL);
            Assert.That(generator.TryTakeAck(0, out _), Is.True);

            generator.NoteApplied(2UL, 1UL);
            Assert.That(generator.TryTakeAck(199, out _), Is.False);
            Assert.That(generator.TryTakeAck(200, out var ack), Is.True);
            Assert.That(ack.LastAppliedTick, Is.EqualTo(2UL));
        }

        [Test]
        public void Generator_RejectsANonPositiveCadenceInterval()
        {
            Assert.That(() => new ReplicationFeedbackGenerator(0),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new ReplicationFeedbackGenerator(-1),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Generator_DocumentedCadenceConstants()
        {
            // OD-12: the base replication cadence is 10 Hz, i.e. one ack per
            // applied replication tick at a 100 ms floor (34 B x 10 Hz = 340 B/s).
            Assert.That(ReplicationFeedbackGenerator.DefaultCadenceHz, Is.EqualTo(10));
            Assert.That(ReplicationFeedbackGenerator.DefaultMinIntervalMs, Is.EqualTo(100L));
        }
    }
}
