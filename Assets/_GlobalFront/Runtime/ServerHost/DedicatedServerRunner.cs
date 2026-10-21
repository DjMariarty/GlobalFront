using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Transport;
using UnityEngine;

namespace GlobalFront.DedicatedServer
{
    /// <summary>
    /// Unity hosting layer of the dedicated server (OD-35, OD-37). Composes
    /// the engine-neutral <see cref="ServerHost"/> with a carrier and a
    /// transport host, then owns the frame loop:
    /// <code>
    /// Update(): drain sockets (Pump) → advance virtual ticks (AdvanceRealTime)
    /// </code>
    ///
    /// Draining before ticking puts commands received in this frame on the
    /// closest authoritative tick instead of the following one. All
    /// simulation stays inside <see cref="ServerHost"/>; this component
    /// carries no gameplay logic.
    /// </summary>
    public sealed class DedicatedServerRunner : MonoBehaviour
    {
        /// <summary>
        /// Frame pacing target: a tick deadline is missed by at most one
        /// frame (about 8.3 ms, a sixth of a 20 Hz tick).
        /// </summary>
        public const int DedicatedServerTargetFramerate = 120;

        private ServerHost _host;
        private INetworkCarrier _carrier;
        private ServerTransportHost _transportHost;
        private ITransportClock _clock;
        private MatchId _match;
        private long _lastPumpMs;

        /// <summary>The hosted server core, or null before initialization / after shutdown.</summary>
        public ServerHost Host => _host;

        /// <summary>The transport substrate, or null before initialization / after shutdown.</summary>
        public INetworkCarrier Carrier => _carrier;

        /// <summary>The auto-join match every handshake is attached to.</summary>
        public MatchId Match => _match;

        /// <summary>True between a successful <see cref="Initialize(int,int,int)"/> and <see cref="Shutdown"/>.</summary>
        public bool IsRunning => _host != null;

        /// <summary>
        /// Starts a server on an explicit UDP port through LiteNetLib.
        /// Intended for the production bootstrap.
        /// </summary>
        public void Initialize(
            int port,
            int tickRate = SimulationConstants.ServerTickRate,
            int maxPlayers = SimulationConstants.MaxPlayers)
        {
            if (_host != null)
            {
                throw new InvalidOperationException("the dedicated server runner is already initialized");
            }

            if (port < 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            if (tickRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRate));
            }

            if (maxPlayers <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            }

            var clock = new SystemTransportClock();
            var carrier = LiteNetLibCarrier.CreateServer(clock);
            carrier.StartServer(port);
            InitializeCore(carrier, clock, tickRate, maxPlayers);
        }

        /// <summary>
        /// Starts a server over a caller-supplied transport. This is the seam
        /// deterministic tests use: a virtual clock and virtual carrier drive
        /// the exact same loop without binding a socket or reading wall time.
        /// </summary>
        public void Initialize(
            INetworkCarrier carrier,
            ITransportClock clock,
            int tickRate = SimulationConstants.ServerTickRate,
            int maxPlayers = SimulationConstants.MaxPlayers)
        {
            if (_host != null)
            {
                throw new InvalidOperationException("the dedicated server runner is already initialized");
            }

            InitializeCore(carrier, clock, tickRate, maxPlayers);
        }

        private void InitializeCore(
            INetworkCarrier carrier,
            ITransportClock clock,
            int tickRate,
            int maxPlayers)
        {
            if (carrier == null)
            {
                throw new ArgumentNullException(nameof(carrier));
            }

            if (clock == null)
            {
                throw new ArgumentNullException(nameof(clock));
            }

            if (tickRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRate));
            }

            if (maxPlayers <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            }

            _clock = clock;
            _carrier = carrier;
            _host = new ServerHost(tickRate);
            _transportHost = new ServerTransportHost(_carrier, _host.Sessions, _host.Server);

            _match = _host.CreateSessionMatch(maxPlayers);
            _transportHost.AutoJoinMatch = _match;

            _host.AttachReplication(
                new TransportReplicationAdapter(_transportHost),
                ServerReplicationEmitterConfig.Default);

            // StartServer is idempotent: the port overload above already
            // started LiteNetLib, while an injected carrier starts here.
            _transportHost.StartServer();
            _host.StartRealTime();
            _lastPumpMs = _clock.NowMs;
            Application.targetFrameRate = DedicatedServerTargetFramerate;
        }

        /// <summary>
        /// One server frame: drain transport events, then advance virtual
        /// ticks. Elapsed time comes from the monotonic transport clock, so
        /// the whole loop is deterministic under a virtual clock and never
        /// depends on Unity's frame time.
        /// </summary>
        public void Update()
        {
            if (_host == null)
            {
                return;
            }

            var nowMs = _clock.NowMs;
            var elapsedSeconds = (nowMs - _lastPumpMs) / 1000.0;
            _lastPumpMs = nowMs;

            if (elapsedSeconds < 0.0)
            {
                elapsedSeconds = 0.0;
            }

            _transportHost.Pump(nowMs, _host.Server.CurrentTick);
            _host.AdvanceRealTime(elapsedSeconds);
        }

        /// <summary>
        /// Stops ticking, announces a graceful shutdown to attached clients,
        /// and closes the carrier. Idempotent: Unity may call it through
        /// <see cref="OnDestroy"/> after an explicit shutdown.
        /// </summary>
        public void Shutdown()
        {
            if (_host == null)
            {
                return;
            }

            _host.StopRealTime();
            _transportHost.Shutdown();

            _transportHost = null;
            _carrier = null;
            _clock = null;
            _host = null;
            _match = default;
            _lastPumpMs = 0;
        }

        private void OnDestroy() => Shutdown();
    }
}
