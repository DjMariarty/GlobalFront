namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Transport diagnostics counters (ADR-009 performance baseline).
    /// Malformed/rate-limited traffic is dropped silently and only surfaces
    /// through these counters — never through exceptions into the host tick
    /// loop.
    /// </summary>
    public sealed class TransportMetrics
    {
        public long DatagramsReceived;
        public long DatagramsSent;
        public long BytesReceived;
        public long BytesSent;

        public long MessagesReceived;
        public long MessagesSent;

        public long DroppedMalformed;
        public long DroppedBadVersion;
        public long DroppedBadToken;
        public long DroppedReplayOrWindow;
        public long DroppedRateLimited;
        public long DroppedStaleSnapshot;
        public long DroppedQueueOverflow;
        public long DroppedFragment;
        public long DroppedConnectionLimit;

        public long Retransmissions;
        public long DuplicateSuppressed;

        public int QueueDepth;
        public int PeakQueueDepth;
        public int ReassemblyBytesPeak;

        public long ConnectionTimeouts;
        public long MaxRetransmitLosses;

        public void NoteQueueDepth(int depth)
        {
            QueueDepth = depth;
            if (depth > PeakQueueDepth)
            {
                PeakQueueDepth = depth;
            }
        }
    }
}
