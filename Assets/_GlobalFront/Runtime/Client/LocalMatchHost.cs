using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;

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
        private readonly MatchServer _server;
        private readonly TickDriver _tickDriver;

        /// <summary>
        /// Creates a host around an externally-constructed server. Intended
        /// for tests and parity integrations where unit registration must be
        /// controlled by the caller. The tick driver starts in manual mode.
        /// </summary>
        public LocalMatchHost(MatchServer server) : this(server, new TickDriver())
        {
        }

        /// <summary>
        /// Creates a host with a fresh empty server and a driver for the
        /// canonical 20 Hz contract. The tick driver starts in manual mode.
        /// </summary>
        public LocalMatchHost() : this(new MatchServer(), new TickDriver())
        {
        }

        private LocalMatchHost(MatchServer server, TickDriver tickDriver)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _tickDriver = tickDriver ?? throw new ArgumentNullException(nameof(tickDriver));
            _tickDriver.TickDue += OnDriverTickDue;
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
        /// this point.
        /// </summary>
        public event Action<ulong> TickCompleted;

        /// <summary>Underlying authoritative server, exposed for diagnostics.</summary>
        public MatchServer Server => _server;

        /// <summary>Underlying tick driver, exposed for diagnostics.</summary>
        public TickDriver TickDriver => _tickDriver;

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
        /// </summary>
        public int AdvanceRealTime(double elapsedSeconds) =>
            _tickDriver.AdvanceRealTime(elapsedSeconds);

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
        public ServerUnitSnapshot[] GetAllSnapshots() => _server.GetAllSnapshots();

        public bool TryGetUnit(EntityId entity, out ServerUnitSnapshot snapshot) =>
            _server.TryGetUnit(entity, out snapshot);

        private void OnDriverTickDue(ulong tick)
        {
            TickStarting?.Invoke(tick);
            _server.TickOnce();
            TickCompleted?.Invoke(tick);
        }
    }
}
