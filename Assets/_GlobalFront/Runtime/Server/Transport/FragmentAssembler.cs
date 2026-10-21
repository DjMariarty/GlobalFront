using System.Buffers;
using System.Collections.Generic;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Splits oversized transport messages into fragment payloads. The
    /// fragment identity is (MessageId, FragmentIndex, FragmentCount);
    /// fragment 0..N-1 carry consecutive slices of the message bytes.
    /// </summary>
    public static class MessageFragmenter
    {
        public static int GetFragmentCount(int messageLength)
        {
            if (messageLength <= 0)
            {
                return 0;
            }

            return (messageLength + TransportProtocol.FragmentPayloadBytes - 1) /
                TransportProtocol.FragmentPayloadBytes;
        }

        /// <summary>
        /// Offset/length of one fragment slice. The logical
        /// <paramref name="messageLength"/> must be passed explicitly:
        /// callers frequently encode into reusable oversized buffers, so the
        /// backing array length must never be used here.
        /// </summary>
        public static void GetFragmentPayload(
            int messageLength,
            int fragmentIndex,
            out int offset,
            out int length)
        {
            offset = fragmentIndex * TransportProtocol.FragmentPayloadBytes;
            length = System.Math.Min(
                TransportProtocol.FragmentPayloadBytes,
                messageLength - offset);
        }
    }

    /// <summary>
    /// Bounded per-peer fragment reassembly (ADR-009, P2-6): lifetime
    /// <see cref="TransportProtocol.FragmentLifetimeMs"/> on the transport
    /// clock, at most <see cref="TransportProtocol.MaxFragmentGroups"/>
    /// concurrent groups, at most
    /// <see cref="TransportProtocol.MaxFragmentsPerGroup"/> fragments per
    /// group, message size capped at
    /// <see cref="TransportProtocol.MaxMessageBytes"/> and total reassembly
    /// memory capped at
    /// <see cref="TransportProtocol.ReassemblyBudgetBytes"/> per peer.
    /// Admission control evicts the oldest group; nothing allocates without
    /// bound.
    /// </summary>
    public sealed class FragmentAssembler
    {
        private sealed class Group
        {
            public ushort MessageId;
            public ushort FragmentCount;
            public int ReceivedCount;
            public int TotalBytes;
            public long StartedAtMs;
            public long LastActivityMs;
            public byte[][] Fragments;
            public int[] FragmentLengths;
        }

        private readonly List<Group> _groups = new List<Group>();
        private readonly Stack<Group> _groupPool = new Stack<Group>(TransportProtocol.MaxFragmentGroups);

        public int GroupCount => _groups.Count;

        public int AllocatedBytes
        {
            get
            {
                var total = 0;
                for (var index = 0; index < _groups.Count; index++)
                {
                    total += _groups[index].TotalBytes;
                }

                return total;
            }
        }

        /// <summary>
        /// Adds a fragment. Returns the complete message when the group
        /// finishes, null while incomplete. Invalid or over-budget input
        /// returns null and discards the fragment.
        /// </summary>
        public byte[] AddFragment(
            ushort messageId,
            ushort fragmentIndex,
            ushort fragmentCount,
            byte[] data,
            int offset,
            int length,
            long nowMs)
        {
            Expire(nowMs);

            if (fragmentCount <= 1 ||
                fragmentCount > TransportProtocol.MaxFragmentsPerGroup ||
                fragmentIndex >= fragmentCount ||
                length <= 0 ||
                length > TransportProtocol.FragmentPayloadBytes)
            {
                return null;
            }

            // Inadmissible declarations are rejected BEFORE any eviction:
            // otherwise one hostile fragment claiming a huge fragmentCount
            // evicts every legitimate in-flight group of the peer and is
            // dropped only afterwards. A declaration is impossible when
            // even the smallest complete message it can carry exceeds
            // MaxMessageBytes, or when its worst-case reservation cannot
            // fit the peer's reassembly budget.
            var worstCase = (long)fragmentCount * TransportProtocol.FragmentPayloadBytes;
            var smallestComplete =
                (long)(fragmentCount - 1) * TransportProtocol.FragmentPayloadBytes + 1;
            if (smallestComplete > TransportProtocol.MaxMessageBytes ||
                worstCase > TransportProtocol.ReassemblyBudgetBytes)
            {
                return null;
            }

            var group = FindGroup(messageId, fragmentCount);
            if (group == null)
            {
                if (_groups.Count >= TransportProtocol.MaxFragmentGroups)
                {
                    EvictOldest();
                }

                // Admission uses the worst-case upper bound; the exact total
                // is enforced during accumulation (oversized messages are
                // discarded the moment they exceed MaxMessageBytes).
                EvictUntilBudget((int)worstCase);
                if (AllocatedBytes + worstCase > TransportProtocol.ReassemblyBudgetBytes)
                {
                    return null;
                }

                group = RentGroup(messageId, fragmentCount, nowMs);
                _groups.Add(group);
            }

            if (group.Fragments[fragmentIndex] != null)
            {
                // Duplicate fragment: ignore, keep group alive.
                group.LastActivityMs = nowMs;
                return null;
            }

            var slice = ArrayPool<byte>.Shared.Rent(length);
            System.Array.Copy(data, offset, slice, 0, length);
            group.Fragments[fragmentIndex] = slice;
            group.FragmentLengths[fragmentIndex] = length;
            group.ReceivedCount++;
            group.TotalBytes += length;
            group.LastActivityMs = nowMs;

            if (group.TotalBytes > TransportProtocol.MaxMessageBytes)
            {
                _groups.Remove(group);
                ReleaseGroup(group);
                return null;
            }

            if (group.ReceivedCount < group.FragmentCount)
            {
                return null;
            }

            _groups.Remove(group);
            var message = new byte[group.TotalBytes];
            var writeOffset = 0;
            for (var index = 0; index < group.FragmentCount; index++)
            {
                var fragment = group.Fragments[index];
                if (fragment == null)
                {
                    ReleaseGroup(group);
                    return null;
                }

                var fragmentLength = group.FragmentLengths[index];
                System.Array.Copy(fragment, 0, message, writeOffset, fragmentLength);
                writeOffset += fragmentLength;
            }

            ReleaseGroup(group);
            return message;
        }

        /// <summary>Drops groups whose lifetime expired at <paramref name="nowMs"/>.</summary>
        public void Expire(long nowMs)
        {
            for (var index = _groups.Count - 1; index >= 0; index--)
            {
                if (nowMs - _groups[index].StartedAtMs > TransportProtocol.FragmentLifetimeMs)
                {
                    var expired = _groups[index];
                    _groups.RemoveAt(index);
                    ReleaseGroup(expired);
                }
            }
        }

        /// <summary>
        /// Discards any in-progress group belonging to
        /// <paramref name="messageId"/> (latest-wins snapshot supersede and
        /// connection teardown). Returns true when a group was removed.
        /// </summary>
        public bool DiscardGroup(ushort messageId)
        {
            var removed = false;
            for (var index = _groups.Count - 1; index >= 0; index--)
            {
                if (_groups[index].MessageId == messageId)
                {
                    var discarded = _groups[index];
                    _groups.RemoveAt(index);
                    ReleaseGroup(discarded);
                    removed = true;
                }
            }

            return removed;
        }

        public void Reset()
        {
            for (var index = 0; index < _groups.Count; index++)
            {
                ReleaseGroup(_groups[index]);
            }

            _groups.Clear();
        }

        private Group FindGroup(ushort messageId, ushort fragmentCount)
        {
            for (var index = 0; index < _groups.Count; index++)
            {
                var group = _groups[index];
                if (group.MessageId == messageId && group.FragmentCount == fragmentCount)
                {
                    return group;
                }
            }

            return null;
        }

        private void EvictOldest()
        {
            if (_groups.Count == 0)
            {
                return;
            }

            var oldest = 0;
            for (var index = 1; index < _groups.Count; index++)
            {
                if (_groups[index].StartedAtMs < _groups[oldest].StartedAtMs)
                {
                    oldest = index;
                }
            }

            var evicted = _groups[oldest];
            _groups.RemoveAt(oldest);
            ReleaseGroup(evicted);
        }

        private Group RentGroup(ushort messageId, ushort fragmentCount, long nowMs)
        {
            var group = _groupPool.Count > 0 ? _groupPool.Pop() : new Group();
            group.MessageId = messageId;
            group.FragmentCount = fragmentCount;
            group.ReceivedCount = 0;
            group.TotalBytes = 0;
            group.StartedAtMs = nowMs;
            group.LastActivityMs = nowMs;
            group.Fragments = ArrayPool<byte[]>.Shared.Rent(fragmentCount);
            System.Array.Clear(group.Fragments, 0, group.Fragments.Length);
            group.FragmentLengths = ArrayPool<int>.Shared.Rent(fragmentCount);
            return group;
        }

        private void ReleaseGroup(Group group)
        {
            var fragments = group.Fragments;
            if (fragments != null)
            {
                for (var index = 0; index < fragments.Length; index++)
                {
                    var slice = fragments[index];
                    if (slice != null)
                    {
                        fragments[index] = null;
                        ArrayPool<byte>.Shared.Return(slice);
                    }
                }

                ArrayPool<byte[]>.Shared.Return(fragments);
                group.Fragments = null;
            }

            if (group.FragmentLengths != null)
            {
                ArrayPool<int>.Shared.Return(group.FragmentLengths);
                group.FragmentLengths = null;
            }

            group.MessageId = 0;
            group.FragmentCount = 0;
            group.ReceivedCount = 0;
            group.TotalBytes = 0;
            group.StartedAtMs = 0;
            group.LastActivityMs = 0;
            _groupPool.Push(group);
        }

        private void EvictUntilBudget(int incomingBytes)
        {
            while (_groups.Count > 0 &&
                AllocatedBytes + incomingBytes > TransportProtocol.ReassemblyBudgetBytes)
            {
                EvictOldest();
            }
        }
    }
}
