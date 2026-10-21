using System;
using GlobalFront.Server;

namespace GlobalFront.Client
{
    /// <summary>
    /// Client-side compatibility wrapper over the engine-neutral
    /// <see cref="ServerHost"/> (OD-36). All authoritative ownership and the
    /// two-phase tick contract live in <see cref="ServerHost"/>; this class
    /// only adds the client reconnect binding, which is the one operation
    /// that necessarily depends on a <see cref="ClientReconnectCoordinator"/>.
    ///
    /// The wrapper keeps the historical <c>LocalMatchHost</c> API and
    /// constructor set intact so every existing client and test behaves
    /// exactly as before the relocation.
    /// </summary>
    public sealed class LocalMatchHost : ServerHost
    {
        /// <summary>
        /// Raised when real-time advances while the host is running or paused.
        /// This client-facing event stays here so the server assembly carries
        /// no <c>Action&lt;double&gt;</c> field (its floating-point gate scans
        /// fields, not signatures).
        /// </summary>
        public event Action<double> RealTimeAdvanced;

        /// <summary>
        /// Creates a host around an externally-constructed server. Intended
        /// for tests and parity integrations where unit registration must be
        /// controlled by the caller. The tick driver starts in manual mode.
        /// </summary>
        public LocalMatchHost(MatchServer server) : base(server)
        {
        }

        /// <summary>
        /// Creates a host around an externally-constructed server with an
        /// explicit session disconnect grace in server ticks (Phase 2.4,
        /// OD-2). The tick driver starts in manual mode.
        /// </summary>
        public LocalMatchHost(MatchServer server, int disconnectGraceTicks)
            : base(server, disconnectGraceTicks)
        {
        }

        /// <summary>
        /// Creates a host with a fresh empty server and an explicit disconnect grace window in ticks.
        /// </summary>
        public LocalMatchHost(int disconnectGraceTicks)
            : base(new MatchServer(), new TickDriver(), disconnectGraceTicks)
        {
        }

        /// <summary>
        /// Creates a host with a fresh empty server and a driver for the
        /// canonical 20 Hz contract. The tick driver starts in manual mode.
        /// </summary>
        public LocalMatchHost() : base()
        {
        }

        protected override void OnRealTimeAdvanced(double elapsedSeconds) =>
            RealTimeAdvanced?.Invoke(elapsedSeconds);

        /// <summary>
        /// Binds the client reconnect coordinator so simulation automatically unpauses
        /// when the 5-second countdown finishes (OD-20 / P1-1).
        /// </summary>
        public void BindReconnectCoordinator(ClientReconnectCoordinator coordinator)
        {
            if (coordinator == null)
            {
                throw new ArgumentNullException(nameof(coordinator));
            }

            coordinator.ReadyToResume += Resume;
        }
    }
}
