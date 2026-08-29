namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Per-endpoint token-bucket rate limiter (ADR-009 security baseline).
    /// Budgets are packets/second and bytes/second; excess traffic is
    /// dropped with a counter instead of being processed. Driven by the
    /// transport clock — no wall-clock reads.
    /// </summary>
    public sealed class RateLimiter
    {
        private readonly int _packetsPerSecond;
        private readonly int _bytesPerSecond;

        private double _packetTokens;
        private double _byteTokens;
        private long _lastRefillMs;

        public int DroppedPackets { get; private set; }

        public RateLimiter(
            int packetsPerSecond = TransportProtocol.RateLimitPacketsPerSecond,
            int bytesPerSecond = TransportProtocol.RateLimitBytesPerSecond)
        {
            _packetsPerSecond = packetsPerSecond;
            _bytesPerSecond = bytesPerSecond;
            _packetTokens = packetsPerSecond;
            _byteTokens = bytesPerSecond;
        }

        /// <summary>
        /// Consumes one packet of <paramref name="bytes"/>. Returns false
        /// when either budget is exhausted; the packet must then be dropped.
        /// </summary>
        public bool TryConsume(int bytes, long nowMs)
        {
            Refill(nowMs);

            if (_packetTokens < 1.0 || _byteTokens < bytes)
            {
                DroppedPackets++;
                return false;
            }

            _packetTokens -= 1.0;
            _byteTokens -= bytes;
            return true;
        }

        private void Refill(long nowMs)
        {
            if (_lastRefillMs == 0)
            {
                _lastRefillMs = nowMs;
            }

            var elapsedMs = nowMs - _lastRefillMs;
            if (elapsedMs <= 0)
            {
                return;
            }

            _lastRefillMs = nowMs;
            var seconds = elapsedMs / 1000.0;
            _packetTokens = System.Math.Min(
                _packetsPerSecond, _packetTokens + seconds * _packetsPerSecond);
            _byteTokens = System.Math.Min(
                _bytesPerSecond, _byteTokens + seconds * _bytesPerSecond);
        }
    }
}
