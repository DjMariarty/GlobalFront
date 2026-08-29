namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Serial-number arithmetic for uint16 sequence spaces (ADR-009,
    /// RFC 1982 semantics). All sequence/ack comparisons in the transport
    /// go through these helpers so wrap-around is handled uniformly.
    /// </summary>
    public static class SerialArithmetic
    {
        /// <summary>
        /// Signed distance from <paramref name="older"/> to
        /// <paramref name="newer"/> in the uint16 sequence space:
        /// positive when <paramref name="newer"/> is ahead, negative when
        /// behind, zero when equal.
        /// </summary>
        public static int Diff(ushort newer, ushort older)
        {
            var diff = (newer - older) & 0xFFFF;
            return diff >= 0x8000 ? diff - 0x10000 : diff;
        }

        /// <summary>True when <paramref name="a"/> is strictly newer than <paramref name="b"/>.</summary>
        public static bool IsNewer(ushort a, ushort b) => Diff(a, b) > 0;

        /// <summary>Advances a sequence by one with wrap-around.</summary>
        public static ushort Next(ushort sequence) => (ushort)((sequence + 1) & 0xFFFF);

        /// <summary>Adds a non-negative offset with wrap-around.</summary>
        public static ushort Add(ushort sequence, int offset) =>
            (ushort)((sequence + offset) & 0xFFFF);
    }
}
