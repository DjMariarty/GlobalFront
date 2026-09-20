using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;

namespace GlobalFront.Client
{
    /// <summary>
    /// Authoritative local host (ADR-007). Owns the authoritative
    /// <see cref="MatchServer"/> and the <see cref="TickDriver"/> that
    /// schedules its ticks, forwarding client commands and exposing
    /// <see cref="ServerUnitSnapshot"/> arrays so the presentation layer can
    /// synchronize from authoritative state:
    /// <code>
    /// TickDriver  →  LocalMatchHost (ServerHost)  →  MatchServer
    /// </code>
    ///
    /// The host owns the server lifecycle. Per authoritative tick the host
    /// runs the established two-phase contract:
    ///   1. <see cref="TickStarting"/> — client commands requested for this
    ///      tick are forwarded to the server (server still at tick N-1), so a
    ///      command requested for tick N is applied during tick N;
    ///   2. <see cref="MatchServer.TickOnce"/> — the authoritative simulation;
    ///   3. <see cref="TickCompleted"/> — presentation consumes the tick N
    ///      snapshots.
    ///
    /// The driver starts in manual mode: deterministic tests advance ticks
    /// through <see cref="TickOnce"/>. The client runtime switches the driver
    /// to real-time mode (<see cref="StartRealTime"/> +
    /// <see cref="AdvanceRealTime"/>), pacing the same simulation core at the
    /// 20 Hz contract with bounded catch-up. A future dedicated server host
    /// uses the identical <see cref="TickDriver"/>/
    /// <see cref="MatchServer"/> pair without any Unity client lifecycle.
    /// </summary>
    public sealed class LocalMatchHost
    {
        /// <summary>
        /// Default disconnect grace for session-managed matches, in server
        /// ticks (200 seconds at the 20 Hz contract, Phase 2.7 / OD-18).
        /// </summary>
        public const int DefaultDisconnectGraceTicks = 4000;

        private readonly MatchServer _server;
        private readonly TickDriver _tickDriver;
        private readonly SessionManager _sessions;

        /// <summary>
        /// The session-managed match started through
        /// <see cref="TryStartSessionMatch"/>, if any. The local prototype
        /// host runs exactly one session-managed match at a time; a future
        /// dedicated host may manage several.
        /// </summary>
        private MatchId _activeSessionMatch;
        private double _pausedSecondsAccumulator;
        private ulong _pausedGraceTicks;
        private bool _isAdvancingRealTime;

        /// <summary>
        /// Creates a host around an externally-constructed server. Intended
        /// for tests and parity integrations where unit registration must be
        /// controlled by the caller. The tick driver starts in manual mode.
        /// </summary>
        public LocalMatchHost(MatchServer server)
            : this(server, new TickDriver(), DefaultDisconnectGraceTicks)
        {
        }

        /// <summary>
        /// Creates a host around an externally-constructed server with an
        /// explicit session disconnect grace in server ticks (Phase 2.4,
        /// OD-2). The tick driver starts in manual mode.
        /// </summary>
        public LocalMatchHost(MatchServer server, int disconnectGraceTicks)
            : this(server, new TickDriver(), disconnectGraceTicks)
        {
        }

        /// <summary>
        /// Creates a host with a fresh empty server and an explicit disconnect grace window in ticks.
        /// </summary>
        public LocalMatchHost(int disconnectGraceTicks)
            : this(new MatchServer(), new TickDriver(), disconnectGraceTicks)
        {
        }

        /// <summary>
        /// Creates a host with a fresh empty server and a driver for the
        /// canonical 20 Hz contract. The tick driver starts in manual mode.
        /// </summary>
        public LocalMatchHost() : this(new MatchServer(), new TickDriver(), DefaultDisconnectGraceTicks)
        {
        }

        private LocalMatchHost(MatchServer server, TickDriver tickDriver, int disconnectGraceTicks)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _tickDriver = tickDriver ?? throw new ArgumentNullException(nameof(tickDriver));
            _sessions = new SessionManager(disconnectGraceTicks);
            _tickDriver.TickDue += OnDriverTickDue;
            _sessions.SessionDisconnected += (session, tick) =>
            {
                if (AutoPauseOnDisconnect)
                {
                    Pause();
                }
            };
            _sessions.SessionGraceExpired += OnSessionGraceExpired;
            _sessions.SessionClosed += OnSessionClosed;
        }

        /// <summary>
        /// Raised before the server simulates tick N, while the server is
        /// still at tick N-1. The client uses this to forward pending
        /// commands requested for tick N, preserving the contract that a
        /// command requested for tick N is applied during tick N.
        /// </summary>
        public event Action<ulong> TickStarting;

        /// <summary>
        /// Raised after the server has fully simulated tick N. The client
        /// presentation layer consumes <see cref="GetAllSnapshots"/> from
        /// this point, and the replication emitter attached through
        /// <see cref="AttachReplication"/> streams its deltas from here.
        /// </summary>
        public event Action<ulong> TickCompleted;

        /// <summary>
        /// Raised when tactical pause is activated (Phase 2.8, Step 2.8.2).
        /// </summary>
        public event Action Paused;

        /// <summary>
        /// Raised when simulation resumes from tactical pause (Phase 2.8, Step 2.8.2).
        /// </summary>
        public event Action Resumed;

        /// <summary>
        /// Raised when real-time advances while the host is running or paused.
        /// </summary>
        public event Action<double> RealTimeAdvanced;

        /// <summary>
        /// Raised when pause ticks advance directly outside of AdvanceRealTime.
        /// </summary>
        public event Action<ulong> PauseTicksAdvanced;

        /// <summary>Underlying authoritative server, exposed for diagnostics.</summary>
        public MatchServer Server => _server;

        /// <summary>
        /// The replication emitter attached through
        /// <see cref="AttachReplication"/>, or null while replication is off.
        /// </summary>
        public ServerReplicationEmitter Replication { get; private set; }

        /// <summary>Underlying tick driver, exposed for diagnostics.</summary>
        public TickDriver TickDriver => _tickDriver;

        /// <summary>
        /// Session / player identity authority (Phase 2.4, ADR-008). The
        /// host owns this manager alongside the MatchServer and TickDriver;
        /// it is the sole source of authoritative PlayerId assignment.
        /// </summary>
        public SessionManager Sessions => _sessions;

        /// <summary>The session-managed match started by this host, if any.</summary>
        public MatchId ActiveSessionMatch => _activeSessionMatch;

        public ulong CurrentTick => _server.CurrentTick;

        public BattleOutcome Outcome => _server.Outcome;

        public int UnitCount => _server.UnitCount;

        public bool IsActive => _server != null;

        /// <summary>Driver scheduling mode (manual or real-time).</summary>
        public TickDriverMode TickDriverMode => _tickDriver.Mode;

        /// <summary>Queued (not yet simulated) time on the driver, in seconds.</summary>
        public double TickBacklogSeconds => _tickDriver.BacklogSeconds;

        /// <summary>
        /// Switches the driver to real-time pacing at the 20 Hz contract with
        /// bounded catch-up. Ticks are then scheduled by
        /// <see cref="AdvanceRealTime"/>; manual <see cref="TickOnce"/> calls
        /// are rejected until <see cref="StopRealTime"/>.
        /// </summary>
        public void StartRealTime() => _tickDriver.StartRealTime();

        /// <summary>
        /// Stops real-time pacing and returns the driver to manual mode. The
        /// tick index and backlog are preserved.
        /// </summary>
        public void StopRealTime() => _tickDriver.StopRealTime();

        /// <summary>
        /// Feeds elapsed wall time into the driver in real-time mode. Each
        /// scheduled tick runs the full two-phase host lifecycle
        /// (<see cref="TickStarting"/> → server tick →
        /// <see cref="TickCompleted"/>). Returns the number of ticks
        /// executed; returns 0 when the driver is in manual mode.
        /// When tactical pause is active, pumps in-flight keyframe slices (OD-18 / P0-3).
        /// </summary>
        public int AdvanceRealTime(double elapsedSeconds)
        {
            if (IsPaused)
            {
                PumpPausedSlices();

                _pausedSecondsAccumulator += elapsedSeconds;
                _isAdvancingRealTime = true;
                try
                {
                    while (_pausedSecondsAccumulator >= _tickDriver.TickDurationSeconds)
                    {
                        _pausedSecondsAccumulator -= _tickDriver.TickDurationSeconds;
                        AdvancePauseTicks(1);
                    }
                }
                finally
                {
                    _isAdvancingRealTime = false;
                }
            }

            var executed = _tickDriver.AdvanceRealTime(elapsedSeconds);
            RealTimeAdvanced?.Invoke(elapsedSeconds);
            return executed;
        }

        /// <summary>
        /// Advances elapsed grace ticks during tactical pause, pumping session grace expiry (Phase 2.8, P2-1).
        /// </summary>
        public void AdvancePauseTicks(ulong ticks = 1)
        {
            _pausedGraceTicks += ticks;
            _sessions.OnTickCompleted(_server.CurrentTick + _pausedGraceTicks);
            if (!_isAdvancingRealTime)
            {
                PauseTicksAdvanced?.Invoke(ticks);
            }
        }

        /// <summary>
        /// True when tick calculation is suspended for tactical pause (Phase 2.7, OD-18).
        /// </summary>
        public bool IsPaused => _tickDriver.IsPaused;

        /// <summary>Halts tick calculation for tactical pause (OD-18).</summary>
        public void Pause()
        {
            _tickDriver.Pause();
            Paused?.Invoke();
        }

        /// <summary>Resumes tick calculation after tactical pause (OD-18/OD-20/P2-1).</summary>
        public void Resume()
        {
            _pausedSecondsAccumulator = 0;
            _pausedGraceTicks = 0;
            _tickDriver.Resume();
            Resumed?.Invoke();
        }

        /// <summary>
        /// When true, the host automatically halts tick scheduling via <see cref="Pause"/>
        /// whenever an active session disconnects. Defaults to true (OD-18 / P0-1).
        /// </summary>
        public bool AutoPauseOnDisconnect { get; set; } = true;

        /// <summary>
        /// Pumps keyframe slices for reconnecting clients during tactical pause (OD-18 / P0-3).
        /// </summary>
        public void PumpPausedSlices()
        {
            if (IsPaused && Replication != null)
            {
                Replication.PumpPausedSlices();
            }
        }

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

        /// <summary>
        /// Handles player disconnection notice, halting ticks if <see cref="AutoPauseOnDisconnect"/> is enabled (OD-18).
        /// Deprecated under P2-2: <see cref="SessionManager.SessionDisconnected"/> is the single canonical pause source.
        /// </summary>
        [Obsolete("Deprecated under P2-2. SessionManager.SessionDisconnected is the single canonical pause source.", false)]
        public void OnPlayerDisconnected(SessionId session)
        {
            if (AutoPauseOnDisconnect)
            {
                Pause();
            }
        }

        private void OnSessionGraceExpired(SessionId session)
        {
            CheckAutoResumeAfterSessionChange();
        }

        private void OnSessionClosed(SessionId session)
        {
            CheckAutoResumeAfterSessionChange();
        }

        private void CheckAutoResumeAfterSessionChange()
        {
            if (IsPaused && !_sessions.HasDisconnectedSessionsInGrace(_activeSessionMatch))
            {
                Resume();
            }
        }

        /// <summary>
        /// Initializes the match from a <see cref="MatchConfig"/>, delegating
        /// to <see cref="MatchServer.InitializeMatch"/>. The server creates
        /// all units and assigns EntityIds authoritatively. Returns the
        /// server-assigned EntityIds in the same order as the config.
        /// </summary>
        public EntityId[] InitializeMatch(MatchConfig config) =>
            _server.InitializeMatch(config);

        public EntityId SpawnUnitWithEntity(
            EntityId entity,
            PlayerId owner,
            CombatStats stats,
            WorldPointMm position,
            int speedMmPerTick,
            bool autoAcquire = false) =>
            _server.SpawnUnitWithEntity(
                entity, owner, stats, position, speedMmPerTick, autoAcquire);

        // -----------------------------------------------------------------
        // Replication pipeline (Phase 2.6, step 2.6.4, audit P1-5). The host
        // owns the emitter lifecycle: it creates the emitter over the given
        // transport seam and drives it from TickCompleted, so every host
        // runtime (local prototype, dedicated server, integration rig) uses
        // the same replication pipeline instead of wiring it by hand.
        // -----------------------------------------------------------------

        /// <summary>
        /// Creates the <see cref="ServerReplicationEmitter"/> over
        /// <paramref name="transport"/> and hooks it to
        /// <see cref="TickCompleted"/>: from the next completed tick on, the
        /// host streams deltas and keyframes through the Phase 2.6 pipeline.
        ///
        /// Call BEFORE clients connect — session attachments are only tracked
        /// from the moment the emitter exists (the emitter is request-driven:
        /// clients pull their baseline with a SnapshotRequest). Attaching
        /// twice is an error; the config overload selects non-default
        /// protocol pacing/capacity values.
        /// </summary>
        public ServerReplicationEmitter AttachReplication(IReplicationTransport transport) =>
            AttachReplication(transport, ServerReplicationEmitterConfig.Default);

        public ServerReplicationEmitter AttachReplication(
            IReplicationTransport transport,
            in ServerReplicationEmitterConfig config)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            if (Replication != null)
            {
                throw new InvalidOperationException(
                    "the replication emitter is already attached to this host");
            }

            var emitter = new ServerReplicationEmitter(_server, transport, config);
            Replication = emitter;
            TickCompleted += emitter.OnTickCompleted;
            transport.SessionDetached += (session, reason) =>
            {
                if (reason != GlobalFront.Server.Transport.TransportDisconnectReason.ClientRequested && AutoPauseOnDisconnect)
                {
                    Pause();
                }

                CheckAutoResumeAfterSessionChange();
            };
            return emitter;
        }

        public MatchCommandRejection TryEnqueueMove(
            CommandHeader header,
            EntityId[] entities,
            WorldPointMm destination,
            FormationSpec formation) =>
            _server.TryEnqueueMove(header, entities, destination, formation);

        public MatchCommandRejection TryEnqueueAttack(
            CommandHeader header,
            EntityId[] attackers,
            EntityId target) =>
            _server.TryEnqueueAttack(header, attackers, target);

        public MatchCommandRejection TryEnqueueStop(CommandHeader header, EntityId[] entities) =>
            _server.TryEnqueueStop(header, entities);

        // -----------------------------------------------------------------
        // Session / Player Identity (Phase 2.4, ADR-008). Server-authoritative
        // identity APIs delegated to the owned SessionManager, plus the
        // session gate every external command submission passes through.
        // -----------------------------------------------------------------

        /// <summary>Creates a session-managed match accepting up to <paramref name="capacity"/> players.</summary>
        public MatchId CreateSessionMatch(int capacity) => _sessions.CreateMatch(capacity);

        /// <summary>Registers a session attached through the given transport-agnostic handle.</summary>
        public SessionId CreateSession(ConnectionHandle connection) => _sessions.CreateSession(connection);

        /// <summary>Issues the next monotonic transport-agnostic connection handle.</summary>
        public ConnectionHandle CreateConnectionHandle() => _sessions.CreateConnectionHandle();

        /// <summary>
        /// Binds a session to a forming match. The server assigns the
        /// PlayerId; clients never choose it.
        /// </summary>
        public JoinResult TryJoinMatch(SessionId session, MatchId match, out PlayerId assigned) =>
            _sessions.TryJoinMatch(session, match, out assigned);

        /// <summary>
        /// Session-aware match start (OD-6): maps the host-supplied template
        /// onto the assigned PlayerIds deterministically and initializes the
        /// authoritative server. Returns false — leaving the match forming —
        /// when the roster does not exactly cover the template slots.
        /// </summary>
        public bool TryStartSessionMatch(MatchId match, MatchConfig template, out EntityId[] entityIds)
        {
            if (!_sessions.TryStartMatch(match, template, _server, out entityIds))
            {
                return false;
            }

            _activeSessionMatch = match;
            return true;
        }

        /// <summary>Explicitly retires a running session-managed match (terminal outcomes are detected per tick).</summary>
        public bool NotifySessionMatchFinished(MatchId match) => _sessions.NotifyMatchFinished(match);

        /// <summary>Reports a lost connection for a connected session, starting the grace window.</summary>
        public bool NotifyConnectionLost(SessionId session, ulong atTick) =>
            _sessions.NotifyConnectionLost(session, atTick);

        /// <summary>Rebinds a disconnected session within the grace window and issues the reconnect receipt.</summary>
        public ReconnectResult TryReconnectSession(
            SessionId session,
            ConnectionHandle newConnection,
            ulong atTick,
            out ReconnectReceipt receipt) =>
            _sessions.TryReconnect(session, newConnection, atTick, out receipt);

        /// <summary>Terminates a session; its slot (if any) is abandoned and the PlayerId retired.</summary>
        public bool CloseSession(SessionId session) => _sessions.CloseSession(session);

        /// <summary>Read-only view of one session, for tests and diagnostics.</summary>
        public bool TryGetSession(SessionId session, out SessionRecord record) =>
            _sessions.TryGetSession(session, out record);

        /// <summary>
        /// Session-gated move submission (Phase 2.4). The session gate
        /// decides attribution and binding first; only a command from a
        /// connected session whose bound PlayerId equals
        /// <c>header.Player</c> reaches the unchanged MatchServer validation.
        /// </summary>
        public SessionCommandResult TrySessionMove(
            SessionId session,
            CommandHeader header,
            EntityId[] entities,
            WorldPointMm destination,
            FormationSpec formation)
        {
            var rejection = _sessions.ValidateCommand(session, header);
            if (rejection != SessionRejection.None)
            {
                return SessionCommandResult.Rejected(rejection);
            }

            return SessionCommandResult.FromMatch(
                _server.TryEnqueueMove(header, entities, destination, formation));
        }

        /// <summary>Session-gated attack submission (Phase 2.4). See <see cref="TrySessionMove"/>.</summary>
        public SessionCommandResult TrySessionAttack(
            SessionId session,
            CommandHeader header,
            EntityId[] attackers,
            EntityId target)
        {
            var rejection = _sessions.ValidateCommand(session, header);
            if (rejection != SessionRejection.None)
            {
                return SessionCommandResult.Rejected(rejection);
            }

            return SessionCommandResult.FromMatch(
                _server.TryEnqueueAttack(header, attackers, target));
        }

        /// <summary>Session-gated stop submission (Phase 2.4). See <see cref="TrySessionMove"/>.</summary>
        public SessionCommandResult TrySessionStop(SessionId session, CommandHeader header, EntityId[] entities)
        {
            var rejection = _sessions.ValidateCommand(session, header);
            if (rejection != SessionRejection.None)
            {
                return SessionCommandResult.Rejected(rejection);
            }

            return SessionCommandResult.FromMatch(_server.TryEnqueueStop(header, entities));
        }

        /// <summary>
        /// Deterministically executes exactly one authoritative tick in
        /// manual driver mode, running the two-phase host lifecycle. Returns
        /// false when the driver is in real-time mode, where the schedule is
        /// owned by <see cref="AdvanceRealTime"/>.
        /// </summary>
        public bool TickOnce() => _tickDriver.AdvanceManualTick();

        /// <summary>
        /// Returns a snapshot of every unit in deterministic entity-id order.
        /// The presentation layer calls this after <see cref="TickCompleted"/>
        /// to synchronize <see cref="PrototypeUnit"/> instances from
        /// authoritative state.
        /// </summary>
#pragma warning disable CS0618
        public ServerUnitSnapshot[] GetAllSnapshots() => _server.GetAllSnapshots();
#pragma warning restore CS0618

        public bool TryGetUnit(EntityId entity, out ServerUnitSnapshot snapshot) =>
            _server.TryGetUnit(entity, out snapshot);

        private void OnDriverTickDue(ulong tick)
        {
            TickStarting?.Invoke(tick);
            _server.TickOnce();
            TickCompleted?.Invoke(tick);

            // Phase 2.4 (ADR-008): grace expiry is tick-based (OD-2) and runs
            // after the tick contract; it never blocks or reorders ticks.
            _sessions.OnTickCompleted(tick);

            // A terminal authoritative outcome retires the session-managed
            // match: a finished match accepts no joins and no commands.
            if (_activeSessionMatch.IsValid && _server.Outcome.IsTerminal)
            {
                _sessions.NotifyMatchFinished(_activeSessionMatch);
            }
        }
    }
}
