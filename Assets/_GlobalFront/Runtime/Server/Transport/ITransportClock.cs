using System.Diagnostics;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Monotonic clock for all transport timing: RTO, keepalive, idle
    /// timeout, retransmission scheduling and fragment lifetime (ADR-009,
    /// P2-2). Transport timers never read wall-clock directly and never use
    /// simulation ticks; deterministic tests inject a virtual clock.
    /// </summary>
    public interface ITransportClock
    {
        /// <summary>Monotonic time in milliseconds.</summary>
        long NowMs { get; }
    }

    /// <summary>
    /// Production transport clock backed by a monotonic stopwatch. Not
    /// affected by wall-clock changes; unrelated to simulation ticks.
    /// </summary>
    public sealed class SystemTransportClock : ITransportClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public long NowMs => _stopwatch.ElapsedMilliseconds;
    }
}
