using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;

namespace GlobalFront.Client
{
    /// <summary>
    /// Hosts an authoritative <see cref="MatchServer"/> locally inside the
    /// client process without any network transport, prediction or
    /// reconciliation. The host owns the server, forwards client commands,
    /// executes one server tick per <see cref="FixedSimulationRunner.TickExecuted"/>
    /// event and exposes <see cref="ServerUnitSnapshot"/> arrays so the
    /// presentation layer can synchronize from authoritative state.
    ///
    /// This class is the bridge described in the LocalMatchHost task:
    /// <code>
    /// Input  →  Command  →  LocalMatchHost  →  MatchServer
    ///                                        →  Server Tick
    ///                                        →  ServerUnitSnapshot
    ///                                        →  Client Presentation
    /// </code>
    /// </summary>
    public sealed class LocalMatchHost
    {
        private readonly MatchServer _server;

        /// <summary>
        /// Creates a host around an externally-constructed server. Intended
        /// for tests and parity integrations where unit registration must be
        /// controlled by the caller.
        /// </summary>
        public LocalMatchHost(MatchServer server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        /// <summary>Creates a host with a fresh empty server.</summary>
        public LocalMatchHost() : this(new MatchServer())
        {
        }

        /// <summary>Underlying authoritative server, exposed for diagnostics.</summary>
        public MatchServer Server => _server;

        public ulong CurrentTick => _server.CurrentTick;

        public BattleOutcome Outcome => _server.Outcome;

        public int UnitCount => _server.UnitCount;

        public bool IsActive => _server != null;

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

        public MatchCommandRejection TryEnqueueStop(
            CommandHeader header,
            EntityId[] entities) =>
            _server.TryEnqueueStop(header, entities);

        /// <summary>
        /// Advances the authoritative simulation by exactly one tick. Must be
        /// called once per <see cref="FixedSimulationRunner.TickExecuted"/>.
        /// </summary>
        public void TickOnce() => _server.TickOnce();

        /// <summary>
        /// Returns a snapshot of every unit in deterministic entity-id order.
        /// The presentation layer calls this after <see cref="TickOnce"/> to
        /// synchronize <see cref="PrototypeUnit"/> instances from
        /// authoritative state.
        /// </summary>
        public ServerUnitSnapshot[] GetAllSnapshots() => _server.GetAllSnapshots();

        public bool TryGetUnit(EntityId entity, out ServerUnitSnapshot snapshot) =>
            _server.TryGetUnit(entity, out snapshot);
    }
}