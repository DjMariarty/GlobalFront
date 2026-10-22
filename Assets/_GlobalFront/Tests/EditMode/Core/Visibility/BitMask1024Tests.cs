using System;
using System.Runtime.InteropServices;
using GlobalFront.Core.Visibility;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Core.Visibility
{
    /// <summary>
    /// Isolated coverage of the 1024-bit visibility mask (ADR-014, Step 4.3.1):
    /// boundary bits 0/63/64/1023, word isolation, set algebra, popcount,
    /// bounds enforcement and the fixed 128-byte footprint that the visibility
    /// history ring copies by value.
    /// </summary>
    [TestFixture]
    public sealed class BitMask1024Tests
    {
        [Test]
        public void SetAndIsSet_RoundTripBoundaryBits()
        {
            var mask = new BitMask1024();

            mask.Set(0);
            mask.Set(63);
            mask.Set(64);
            mask.Set(1023);

            Assert.That(mask.IsSet(0), Is.True, "bit 0 (word 0, high bit position 0)");
            Assert.That(mask.IsSet(63), Is.True, "bit 63 (word 0, high bit position 63)");
            Assert.That(mask.IsSet(64), Is.True, "bit 64 (word 1, low bit position)");
            Assert.That(mask.IsSet(1023), Is.True, "bit 1023 (word 15, highest bit position)");
            Assert.That(mask.IsSet(1), Is.False);
            Assert.That(mask.IsSet(62), Is.False);
            Assert.That(mask.IsSet(65), Is.False);
            Assert.That(mask.IsSet(1022), Is.False);
            Assert.That(mask.PopCount(), Is.EqualTo(4));
        }

        [Test]
        public void Set_DoesNotLeakIntoNeighbouringBitsOrWords()
        {
            var mask = new BitMask1024();

            mask.Set(64);

            Assert.That(mask.W0, Is.Zero, "bit 64 must not touch word 0");
            Assert.That(mask.W1, Is.EqualTo(1UL), "bit 64 is the least significant bit of word 1");
            Assert.That(mask.PopCount(), Is.EqualTo(1));
        }

        [Test]
        public void Clear_RemovesOnlyTheTargetBit()
        {
            var mask = new BitMask1024();
            mask.Set(5);
            mask.Set(70);
            mask.Set(1023);

            mask.Clear(70);

            Assert.That(mask.IsSet(5), Is.True);
            Assert.That(mask.IsSet(70), Is.False);
            Assert.That(mask.IsSet(1023), Is.True);
            Assert.That(mask.PopCount(), Is.EqualTo(2));
        }

        [Test]
        public void ClearAll_EmptiesEveryWord()
        {
            var mask = new BitMask1024();
            for (var bit = 0; bit < BitMask1024.BitCount; bit++)
            {
                mask.Set(bit);
            }

            Assert.That(mask.PopCount(), Is.EqualTo(BitMask1024.BitCount));
            Assert.That(mask.IsEmpty, Is.False);

            mask.ClearAll();

            Assert.That(mask.IsEmpty, Is.True);
            Assert.That(mask.PopCount(), Is.Zero);
            for (var bit = 0; bit < BitMask1024.BitCount; bit++)
            {
                Assert.That(mask.IsSet(bit), Is.False, $"bit {bit} after ClearAll");
            }
        }

        [Test]
        public void IsEmpty_TracksWhetherAnyBitIsSet()
        {
            var mask = new BitMask1024();
            Assert.That(mask.IsEmpty, Is.True);

            mask.Set(1023);
            Assert.That(mask.IsEmpty, Is.False);

            mask.Clear(1023);
            Assert.That(mask.IsEmpty, Is.True);
        }

        [Test]
        public void PopCount_CountsAcrossAllWords()
        {
            var mask = new BitMask1024();
            for (var bit = 0; bit < BitMask1024.BitCount; bit += 7)
            {
                mask.Set(bit);
            }

            var expected = (BitMask1024.BitCount + 6) / 7;
            Assert.That(mask.PopCount(), Is.EqualTo(expected));
        }

        [Test]
        public void OrAndAndNot_UseBitwiseSetAlgebra()
        {
            var left = new BitMask1024();
            left.Set(3);
            left.Set(65);
            left.Set(1023);

            var right = new BitMask1024();
            right.Set(65);
            right.Set(100);

            var union = BitMask1024.Or(in left, in right);
            Assert.That(union.IsSet(3), Is.True);
            Assert.That(union.IsSet(65), Is.True);
            Assert.That(union.IsSet(100), Is.True);
            Assert.That(union.IsSet(1023), Is.True);
            Assert.That(union.PopCount(), Is.EqualTo(4));

            var intersection = BitMask1024.And(in left, in right);
            Assert.That(intersection.IsSet(65), Is.True);
            Assert.That(intersection.PopCount(), Is.EqualTo(1));

            var difference = BitMask1024.AndNot(in left, in right);
            Assert.That(difference.IsSet(3), Is.True);
            Assert.That(difference.IsSet(1023), Is.True);
            Assert.That(difference.IsSet(65), Is.False, "AndNot removes every bit shared with the right operand");
            Assert.That(difference.PopCount(), Is.EqualTo(2));
        }

        [Test]
        public void IndicesOutsideZeroTo1023_Throw()
        {
            var mask = new BitMask1024();

            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Set(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Set(BitMask1024.BitCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Clear(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Clear(BitMask1024.BitCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = mask.IsSet(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = mask.IsSet(BitMask1024.BitCount));
        }

        [Test]
        public void Mask_IsExactlyOneHundredTwentyEightBytes()
        {
            Assert.That(BitMask1024.SizeBytes, Is.EqualTo(128));
            Assert.That(Marshal.SizeOf<BitMask1024>(), Is.EqualTo(128),
                "the history ring stores 120 x 10 of these by value");
        }

        [Test]
        public void EqualityAndHashCode_CompareAllSixteenWords()
        {
            var left = new BitMask1024();
            left.Set(7);
            left.Set(900);

            var right = new BitMask1024();
            right.Set(7);
            right.Set(900);

            Assert.That(left.Equals(right), Is.True);
            Assert.That(left == right, Is.True);
            Assert.That(left != right, Is.False);
            Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));

            right.Set(901);

            Assert.That(left.Equals(right), Is.False);
            Assert.That(left == right, Is.False);
            Assert.That(left != right, Is.True);
        }
    }
}
