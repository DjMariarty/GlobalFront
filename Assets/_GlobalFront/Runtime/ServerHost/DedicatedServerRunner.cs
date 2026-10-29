using System;
using System.Collections.Generic;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
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

        /// <summary>
        /// Players a 1v1 dedicated match waits for before spawning the armies
        /// (Milestone M1 scope: 1v1 dedicated prototype; 2v2 is Milestone M2).
        /// </summary>
        public const int RequiredPlayersForStart = 2;

        /// <summary>Units per player in the default 1v1 roster.</summary>
        public const int UnitsPerArmy = 6;

        // The two armies spawn far enough apart that neither starts in the
        // other's sight radius; contact is a played-out event, not a freebie.
        private const int ArmySeparationMm = 60_000;
        private const int ArmyColumnSpacingMm = 2_000;

        private static readonly CombatStats DefaultUnitStats =
            new CombatStats(maximumHealth: 100, damage: 25, rangeMm: 7000, cooldownTicks: 10);

        private readonly List<PlayerId> _joinedPlayers = new List<PlayerId>(SimulationConstants.MaxPlayers);

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

        /// <summary>The transport host, or null before initialization / after shutdown.</summary>
        public ServerTransportHost TransportHost => _transportHost;

        /// <summary>The auto-join match every handshake is attached to.</summary>
        public MatchId Match => _match;

        /// <summary>
        /// When true (the production default), the runner owns the match start:
        /// once the lobby holds <see cref="RequiredPlayersForStart"/> players it
        /// builds the base roster and starts the session match. A harness that
        /// supplies its own roster and starts the match itself turns this off.
        /// </summary>
        public bool AutoStartMatch { get; set; } = true;

        /// <summary>Players whose handshake completed in the current lobby.</summary>
        public int JoinedPlayerCount => _joinedPlayers.Count;

        /// <summary>True once the authoritative match has been started (units exist).</summary>
        public bool IsMatchActive => _host != null && _host.ActiveSessionMatch.IsValid;

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
            _transportHost.SessionAttached += OnSessionAttached;
            _transportHost.SessionDetached += OnSessionDetached;

            // The dedicated runner is the composition root that owns both the
            // authoritative host and the transport: a bound client's
            // post-reconnect countdown completion lifts the OD-18 pause here,
            // without any manual game-layer Resume call.
            _transportHost.ClientReadyToResumeReceived += _host.OnClientReadyToResume;

            // Terminal session outcomes (ready deadline, match finished) are
            // decided by the host but owned by the transport: the runner
            // delivers the status and detaches the socket (M1.1 F3/F5).
            _host.SessionTerminated += OnHostSessionTerminated;

            _host.AttachReplication(
                new TransportReplicationAdapter(_transportHost),
                ServerReplicationEmitterConfig.Default,
                new FogOfWarReplicationFilter());

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

            // The session clock includes the pause ticks accumulated so far:
            // a disconnect inside a tactical pause must stamp the continuous
            // clock, not the frozen simulation tick (M1.1 F4).
            _transportHost.Pump(nowMs, _host.SessionGraceClock);
            TryStartMatchWhenReady();
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
            _host.SessionTerminated -= OnHostSessionTerminated;
            _transportHost.ClientReadyToResumeReceived -= _host.OnClientReadyToResume;
            _transportHost.SessionAttached -= OnSessionAttached;
            _transportHost.SessionDetached -= OnSessionDetached;
            _transportHost.Shutdown();

            _transportHost = null;
            _carrier = null;
            _clock = null;
            _host = null;
            _match = default;
            _lastPumpMs = 0;
            _joinedPlayers.Clear();
        }

        /// <summary>
        /// Records a completed handshake and starts the match as soon as the
        /// lobby is complete. Without this the dedicated entry point accepted
        /// clients into a <c>Forming</c> match with a zero-unit roster that
        /// never began (audit P1-01).
        /// </summary>
        private void OnSessionAttached(SessionId session, MatchId match, PlayerId player)
        {
            if (_host == null ||
                match != _match ||
                !player.IsValid ||
                _joinedPlayers.Contains(player))
            {
                return;
            }

            _joinedPlayers.Add(player);
            TryStartMatchWhenReady();
        }

        /// <summary>
        /// Delivers a terminal outcome to one client: the transport sends the
        /// status frame, releases the token binding and closes the socket, so
        /// the client's reconnect coordinator terminates instead of retrying
        /// into a dead session.
        /// </summary>
        private void OnHostSessionTerminated(SessionId session, TransportDisconnectReason reason)
        {
            _transportHost?.DetachSession(session, reason);
        }

        /// <summary>
        /// A session that leaves before the match starts must leave the lobby
        /// with it (M1.1 P2-2). Keeping the abandoned player would make the
        /// forming match full: the next handshake would never be able to start
        /// a 1v1, and the runner would wait forever on a slot nobody owns.
        /// After the start a disconnect is the grace window's business, not the
        /// lobby's.
        /// </summary>
        private void OnSessionDetached(SessionId session, TransportDisconnectReason reason)
        {
            if (_host == null || _host.ActiveSessionMatch.IsValid)
            {
                return;
            }

            if (!_host.TryGetSession(session, out var record) || !record.Player.IsValid)
            {
                return;
            }

            _joinedPlayers.Remove(record.Player);
            _host.ReleaseFormingSlot(session);
        }

        /// <summary>
        /// Builds the base 1v1 roster from the players currently in the lobby
        /// and hands it to the authoritative start path (M1.1 F1): the match
        /// starts only when exactly the two required players are present. A
        /// lobby that waits too long keeps waiting — a one-army match would
        /// declare an instant victory and permanently close the second slot.
        /// </summary>
        private bool TryStartMatchWhenReady()
        {
            if (_host == null ||
                !AutoStartMatch ||
                _joinedPlayers.Count != RequiredPlayersForStart ||
                _host.ActiveSessionMatch.IsValid ||
                _host.UnitCount > 0)
            {
                return false;
            }

            var owners = _joinedPlayers.ToArray();
            Array.Sort(owners, (left, right) => left.Value.CompareTo(right.Value));
            return _host.TryStartSessionMatch(_match, BuildDefaultMatchConfig(owners), out _);
        }

        /// <summary>
        /// Symmetric 1v1 roster: one small army per joined player, spawned on
        /// opposite sides of the map. Production composition code — the roster
        /// is authoritative server state, never a test/backdoor hand-off.
        /// </summary>
        public static MatchConfig BuildDefaultMatchConfig(IReadOnlyList<PlayerId> owners)
        {
            if (owners == null)
            {
                throw new ArgumentNullException(nameof(owners));
            }

            if (owners.Count == 0)
            {
                throw new ArgumentException("at least one player is required", nameof(owners));
            }

            var units = new UnitSpawnSpec[owners.Count * UnitsPerArmy];
            var count = 0;
            for (var playerIndex = 0; playerIndex < owners.Count; playerIndex++)
            {
                if (!owners[playerIndex].IsValid)
                {
                    throw new ArgumentException("owner must be valid", nameof(owners));
                }

                var side = playerIndex % 2 == 0 ? -1 : 1;
                var baseX = side * ArmySeparationMm * (1 + (playerIndex / 2));
                for (var unitIndex = 0; unitIndex < UnitsPerArmy; unitIndex++)
                {
                    var kind = unitIndex == 0
                        ? UnitKinds.BaseStructure
                        : (unitIndex & 1) == 0 ? UnitKinds.Tank : UnitKinds.Scout;
                    var column = unitIndex / 2;
                    var row = unitIndex % 2;
                    units[count++] = new UnitSpawnSpec(
                        owners[playerIndex],
                        new WorldPointMm(
                            baseX + (side * column * ArmyColumnSpacingMm),
                            -ArmyColumnSpacingMm / 2 + (row * ArmyColumnSpacingMm)),
                        speedMmPerTick: 350,
                        autoAcquireEnemies: true,
                        kind);
                }
            }

            return new MatchConfig(DefaultUnitStats, units);
        }

        private void OnDestroy() => Shutdown();
    }
}
