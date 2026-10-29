using System.Collections.Generic;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Shared power-of-two message buffers for the transport receive path.
    ///
    /// The carrier hands a received message to the event queue as an owned
    /// array: the queue crosses from the receive worker to the host thread, so
    /// the reader's pooled storage cannot be retained. The event carries the
    /// logical <c>Length</c>, so a buffer may be larger than its message.
    ///
    /// A process-wide pool rather than <see cref="System.Buffers.ArrayPool{T}"/>:
    /// ArrayPool keeps per-thread stacks, and a bucket's first rent on the
    /// receive worker allocates even when the host thread has already warmed
    /// it. That is exactly the steady-state GC the zero-GC window measures.
    /// </summary>
    internal static class TransportBufferPool
    {
        private const int MinBucketShift = 7;   // 128 B
        private const int MaxBucketShift = 20;  // 1 MiB
        private const int WarmBucketShift = 16; // 64 KiB; snapshots are kilobytes

        /// <summary>
        /// Buffers a bucket keeps. A 16-32 KiB snapshot fragments into 14-28
        /// datagrams of <see cref="TransportProtocol.FragmentPayloadBytes"/>,
        /// so 8 slots discarded half of every reassembly and the next message
        /// reallocated them (M1.1 P1-2). 32 covers a full 16 KiB message.
        /// </summary>
        private const int BuffersPerBucket = 32;

        /// <summary>
        /// The 2048-byte bucket is the slice size of every snapshot fragment
        /// (1171 payload bytes), so it must hold a whole 32 KiB snapshot on
        /// its own; the other buckets only ever see wider messages.
        /// </summary>
        private const int LargeBucketShift = 11; // 2048 B
        private const int LargeBuffersPerBucket = 64;

        /// <summary>
        /// Buffers preallocated per bucket by <see cref="Warm"/>. Warm-up must
        /// cover one full 16-fragment assembly, otherwise the first measured
        /// message still allocates its slices.
        /// </summary>
        private const int WarmBuffersPerBucket = 16;

        private static readonly Stack<byte[]>[] Buckets = CreateBuckets();
        private static readonly object Gate = new object();
        private static bool _warmed;
        private static long _rentCount;
        private static long _returnCount;

        /// <summary>
        /// Buffers handed out by <see cref="Rent"/>. Monotonic diagnostics: the
        /// difference to <see cref="ReturnCount"/> is what the transport is
        /// still holding, and it must come back to its baseline on every
        /// terminal path (rate-limit drop, queue overflow, teardown).
        /// </summary>
        public static long RentCount => System.Threading.Interlocked.Read(ref _rentCount);

        /// <summary>Buffers handed back through <see cref="Return"/>.</summary>
        public static long ReturnCount => System.Threading.Interlocked.Read(ref _returnCount);

        /// <summary>Rented buffers the pool has not seen returned.</summary>
        public static long OutstandingCount => RentCount - ReturnCount;

        public static void Warm()
        {
            if (_warmed)
            {
                return;
            }

            _warmed = true;
            lock (Gate)
            {
                for (var shift = MinBucketShift; shift <= WarmBucketShift; shift++)
                {
                    var bucket = Buckets[shift - MinBucketShift];
                    for (var index = 0; index < WarmBuffersPerBucket; index++)
                    {
                        bucket.Push(new byte[1 << shift]);
                    }
                }
            }
        }

        public static byte[] Rent(int length)
        {
            if (length <= 0)
            {
                return System.Array.Empty<byte>();
            }

            if (length > 1 << MaxBucketShift)
            {
                return new byte[length];
            }

            var shift = BucketShift(length);
            lock (Gate)
            {
                System.Threading.Interlocked.Increment(ref _rentCount);
                var bucket = Buckets[shift - MinBucketShift];
                if (bucket.Count > 0)
                {
                    return bucket.Pop();
                }
            }

            return new byte[1 << shift];
        }

        public static void Return(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0 || buffer.Length > 1 << MaxBucketShift)
            {
                return;
            }

            var shift = BucketShift(buffer.Length);
            if (1 << shift != buffer.Length)
            {
                // Only buffers this pool created are worth keeping.
                return;
            }

            lock (Gate)
            {
                // Counted even when the bucket is full: the buffer is released
                // to the GC either way, and only the Rent/Return balance
                // describes whether a caller dropped a buffer on the floor.
                System.Threading.Interlocked.Increment(ref _returnCount);
                var bucket = Buckets[shift - MinBucketShift];
                if (bucket.Count < CapacityFor(shift))
                {
                    bucket.Push(buffer);
                }
            }
        }

        private static int BucketShift(int length)
        {
            var shift = MinBucketShift;
            while (1 << shift < length && shift < MaxBucketShift)
            {
                shift++;
            }

            return shift;
        }

        private static int CapacityFor(int shift) =>
            shift >= LargeBucketShift ? LargeBuffersPerBucket : BuffersPerBucket;

        private static Stack<byte[]>[] CreateBuckets()
        {
            var buckets = new Stack<byte[]>[MaxBucketShift - MinBucketShift + 1];
            for (var index = 0; index < buckets.Length; index++)
            {
                buckets[index] = new Stack<byte[]>(CapacityFor(index + MinBucketShift));
            }

            return buckets;
        }
    }
}
