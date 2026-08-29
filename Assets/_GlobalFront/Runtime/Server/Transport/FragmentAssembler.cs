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
        }

        private readonly List<Group> _groups = new List<Group>();

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
                var worstCase = System.Math.Min(
                    (long)fragmentCount * TransportProtocol.FragmentPayloadBytes,
                    (long)TransportProtocol.ReassemblyBudgetBytes + 1);
                EvictUntilBudget((int)worstCase);
                if (AllocatedBytes + worstCase > TransportProtocol.ReassemblyBudgetBytes)
                {
                    return null;
                }

                group = new Group
                {
                    MessageId = messageId,
                    FragmentCount = fragmentCount,
                    StartedAtMs = nowMs,
                    Fragments = new byte[fragmentCount][]
                };
                _groups.Add(group);
            }

            if (group.Fragments[fragmentIndex] != null)
            {
                // Duplicate fragment: ignore, keep group alive.
                group.LastActivityMs = nowMs;
                return null;
            }

            var slice = new byte[length];
            System.Array.Copy(data, offset, slice, 0, length);
            group.Fragments[fragmentIndex] = slice;
            group.ReceivedCount++;
            group.TotalBytes += length;
            group.LastActivityMs = nowMs;

            if (group.TotalBytes > TransportProtocol.MaxMessageBytes)
            {
                _groups.Remove(group);
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
                    return null;
                }

                System.Array.Copy(fragment, 0, message, writeOffset, fragment.Length);
                writeOffset += fragment.Length;
            }

            return message;
        }

        /// <summary>Drops groups whose lifetime expired at <paramref name="nowMs"/>.</summary>
        public void Expire(long nowMs)
        {
            for (var index = _groups.Count - 1; index >= 0; index--)
            {
                if (nowMs - _groups[index].StartedAtMs > TransportProtocol.FragmentLifetimeMs)
                {
                    _groups.RemoveAt(index);
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
                    _groups.RemoveAt(index);
                    removed = true;
                }
            }

            return removed;
        }

        public void Reset() => _groups.Clear();

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

            _groups.RemoveAt(oldest);
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
