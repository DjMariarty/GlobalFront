using System;
using System.Runtime.InteropServices;

namespace GlobalFront.Core.Visibility
{
    /// <summary>
    /// Fixed 1024-bit set used by the server visibility pipeline (ADR-014,
    /// Step 4.3.1). Bit <c>i</c> represents the unit whose
    /// <see cref="GlobalFront.Core.Model.EntityId.Value"/> is <c>i + 1</c>, so
    /// the mask addresses entities 1..1024 without a lookup table and without
    /// allocating.
    ///
    /// The struct is exactly 128 bytes (16 words): masks are copied by value
    /// into the visibility history ring, so the copy cost is the storage cost.
    /// Every accessor is bounds-checked; an index outside 0..1023 is a caller
    /// bug, not a silent no-op.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BitMask1024 : IEquatable<BitMask1024>
    {
        public const int BitCount = 1024;

        public const int WordCount = 16;

        public const int SizeBytes = WordCount * sizeof(ulong);

        public ulong W0, W1, W2, W3, W4, W5, W6, W7;
        public ulong W8, W9, W10, W11, W12, W13, W14, W15;

        public void Set(int entityIndex)
        {
            ValidateIndex(entityIndex);
            var bit = 1UL << (entityIndex & 63);
            switch (entityIndex >> 6)
            {
                case 0: W0 |= bit; break;
                case 1: W1 |= bit; break;
                case 2: W2 |= bit; break;
                case 3: W3 |= bit; break;
                case 4: W4 |= bit; break;
                case 5: W5 |= bit; break;
                case 6: W6 |= bit; break;
                case 7: W7 |= bit; break;
                case 8: W8 |= bit; break;
                case 9: W9 |= bit; break;
                case 10: W10 |= bit; break;
                case 11: W11 |= bit; break;
                case 12: W12 |= bit; break;
                case 13: W13 |= bit; break;
                case 14: W14 |= bit; break;
                case 15: W15 |= bit; break;
                default: throw new ArgumentOutOfRangeException(nameof(entityIndex));
            }
        }

        public void Clear(int entityIndex)
        {
            ValidateIndex(entityIndex);
            var bit = 1UL << (entityIndex & 63);
            switch (entityIndex >> 6)
            {
                case 0: W0 &= ~bit; break;
                case 1: W1 &= ~bit; break;
                case 2: W2 &= ~bit; break;
                case 3: W3 &= ~bit; break;
                case 4: W4 &= ~bit; break;
                case 5: W5 &= ~bit; break;
                case 6: W6 &= ~bit; break;
                case 7: W7 &= ~bit; break;
                case 8: W8 &= ~bit; break;
                case 9: W9 &= ~bit; break;
                case 10: W10 &= ~bit; break;
                case 11: W11 &= ~bit; break;
                case 12: W12 &= ~bit; break;
                case 13: W13 &= ~bit; break;
                case 14: W14 &= ~bit; break;
                case 15: W15 &= ~bit; break;
                default: throw new ArgumentOutOfRangeException(nameof(entityIndex));
            }
        }

        public bool IsSet(int entityIndex)
        {
            ValidateIndex(entityIndex);
            return (Word(entityIndex >> 6) & (1UL << (entityIndex & 63))) != 0;
        }

        public void ClearAll()
        {
            W0 = W1 = W2 = W3 = W4 = W5 = W6 = W7 = 0UL;
            W8 = W9 = W10 = W11 = W12 = W13 = W14 = W15 = 0UL;
        }

        public bool IsEmpty => (W0 | W1 | W2 | W3 | W4 | W5 | W6 | W7 |
                                W8 | W9 | W10 | W11 | W12 | W13 | W14 | W15) == 0UL;

        public int PopCount() =>
            CountBits(W0) + CountBits(W1) + CountBits(W2) + CountBits(W3) +
            CountBits(W4) + CountBits(W5) + CountBits(W6) + CountBits(W7) +
            CountBits(W8) + CountBits(W9) + CountBits(W10) + CountBits(W11) +
            CountBits(W12) + CountBits(W13) + CountBits(W14) + CountBits(W15);

        public static BitMask1024 Or(in BitMask1024 a, in BitMask1024 b) =>
            new BitMask1024
            {
                W0 = a.W0 | b.W0, W1 = a.W1 | b.W1, W2 = a.W2 | b.W2, W3 = a.W3 | b.W3,
                W4 = a.W4 | b.W4, W5 = a.W5 | b.W5, W6 = a.W6 | b.W6, W7 = a.W7 | b.W7,
                W8 = a.W8 | b.W8, W9 = a.W9 | b.W9, W10 = a.W10 | b.W10, W11 = a.W11 | b.W11,
                W12 = a.W12 | b.W12, W13 = a.W13 | b.W13, W14 = a.W14 | b.W14, W15 = a.W15 | b.W15,
            };

        public static BitMask1024 And(in BitMask1024 a, in BitMask1024 b) =>
            new BitMask1024
            {
                W0 = a.W0 & b.W0, W1 = a.W1 & b.W1, W2 = a.W2 & b.W2, W3 = a.W3 & b.W3,
                W4 = a.W4 & b.W4, W5 = a.W5 & b.W5, W6 = a.W6 & b.W6, W7 = a.W7 & b.W7,
                W8 = a.W8 & b.W8, W9 = a.W9 & b.W9, W10 = a.W10 & b.W10, W11 = a.W11 & b.W11,
                W12 = a.W12 & b.W12, W13 = a.W13 & b.W13, W14 = a.W14 & b.W14, W15 = a.W15 & b.W15,
            };

        public static BitMask1024 AndNot(in BitMask1024 a, in BitMask1024 b) =>
            new BitMask1024
            {
                W0 = a.W0 & ~b.W0, W1 = a.W1 & ~b.W1, W2 = a.W2 & ~b.W2, W3 = a.W3 & ~b.W3,
                W4 = a.W4 & ~b.W4, W5 = a.W5 & ~b.W5, W6 = a.W6 & ~b.W6, W7 = a.W7 & ~b.W7,
                W8 = a.W8 & ~b.W8, W9 = a.W9 & ~b.W9, W10 = a.W10 & ~b.W10, W11 = a.W11 & ~b.W11,
                W12 = a.W12 & ~b.W12, W13 = a.W13 & ~b.W13, W14 = a.W14 & ~b.W14, W15 = a.W15 & ~b.W15,
            };

        public bool Equals(BitMask1024 other) =>
            W0 == other.W0 && W1 == other.W1 && W2 == other.W2 && W3 == other.W3 &&
            W4 == other.W4 && W5 == other.W5 && W6 == other.W6 && W7 == other.W7 &&
            W8 == other.W8 && W9 == other.W9 && W10 == other.W10 && W11 == other.W11 &&
            W12 == other.W12 && W13 == other.W13 && W14 == other.W14 && W15 == other.W15;

        public override bool Equals(object obj) => obj is BitMask1024 other && Equals(other);

        public override int GetHashCode()
        {
            var low = HashCode.Combine(W0, W1, W2, W3, W4, W5, W6, W7);
            var high = HashCode.Combine(W8, W9, W10, W11, W12, W13, W14, W15);
            return HashCode.Combine(low, high);
        }

        public override string ToString() => $"BitMask1024(pop={PopCount()})";

        public static bool operator ==(BitMask1024 left, BitMask1024 right) => left.Equals(right);

        public static bool operator !=(BitMask1024 left, BitMask1024 right) => !left.Equals(right);

        private static void ValidateIndex(int entityIndex)
        {
            if ((uint)entityIndex >= BitCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(entityIndex), entityIndex, "Entity index must be in 0..1023.");
            }
        }

        private static int CountBits(ulong value)
        {
            // Unity's scripting profile does not expose System.Numerics.BitOperations.
            value -= (value >> 1) & 0x5555555555555555UL;
            value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
            value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((value * 0x0101010101010101UL) >> 56);
        }

        private ulong Word(int wordIndex)
        {
            switch (wordIndex)
            {
                case 0: return W0;
                case 1: return W1;
                case 2: return W2;
                case 3: return W3;
                case 4: return W4;
                case 5: return W5;
                case 6: return W6;
                case 7: return W7;
                case 8: return W8;
                case 9: return W9;
                case 10: return W10;
                case 11: return W11;
                case 12: return W12;
                case 13: return W13;
                case 14: return W14;
                case 15: return W15;
                default:
                    throw new ArgumentOutOfRangeException(nameof(wordIndex));
            }
        }
    }
}
