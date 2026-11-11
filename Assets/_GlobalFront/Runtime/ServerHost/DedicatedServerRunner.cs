using System;
using System.Collections.Generic;
using GlobalFront.Core.Catalog;
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
        /// Players a 1v1 dedicated match waits for before spawning the armies.
        /// A team match waits for the four <see cref="MatchTopology2v2"/> slots
        /// instead (Step 5.6.1).
        /// </summary>
        public int RequiredPlayersForStart =>
            TopologyMode == MatchTopologyMode.Team2v2 ? MatchTopology2v2.SlotCount : 2;

        /// <summary>Units per player in the default 1v1 roster.</summary>
        public const int UnitsPerArmy = 6;

        // The two armies spawn far enough apart that neither starts in the
        // other's sight radius; contact is a played-out event, not a freebie.
        private const int ArmySeparationMm = 60_000;
        private const int ArmyColumnSpacingMm = 2_000;

        /// <summary>
        /// Team base points of the 2v2 spawn geometry (Step 5.6.1), indexed by
        /// <see cref="MatchTopology2v2"/> slot: Team Red (players 1, 2) holds
        /// the west — north-west and south-west — against Team Blue's east.
        /// Enemy bases sit 70 000 mm apart (outside the 20 000 mm initial sight
        /// radius), allied bases 40 000 mm apart.
        /// </summary>
        private static readonly WorldPointMm[] TeamBasePositions =
        {
            new WorldPointMm(-35_000, 20_000),  // slot 0: north-west
            new WorldPointMm(-35_000, -20_000), // slot 1: south-west
            new WorldPointMm(35_000, 20_000),   // slot 2: north-east
            new WorldPointMm(35_000, -20_000),  // slot 3: south-east
        };

        private static readonly CombatStats DefaultUnitStats =
            new CombatStats(maximumHealth: 100, damage: 25, rangeMm: 7000, cooldownTicks: 10);

        /// <summary>
        /// N-02: the catalog the RTS provisioning reads builder speeds and
        /// legacy kinds from — match-start path only, created once.
        /// </summary>
        private static readonly IBlueprintCatalog ProvisioningCatalog =
            DefaultBlueprintCatalog.Create();

        private readonly List<PlayerId> _joinedPlayers = new List<PlayerId>(SimulationConstants.MaxPlayers);

        private ServerHost _host;
        private INetworkCarrier _carrier;
        private ServerTransportHost _transportHost;
        private ITransportClock _clock;
        private MatchId _match;
        private long _lastPumpMs;
        private FogOfWarReplicationFilter _fogFilter;

        /// <summary>
        /// M2 remediation (P0-01): the team match's own combat relation — the
        /// lobby's slot mapping is configured into it at match start, so
        /// hostility follows the same table the fog filter uses instead of the
        /// hardcoded PlayerId fold.
        /// </summary>
        private Team2v2CombatRelation _teamRelation;

        /// <summary>
        /// M2 remediation (P1-03): pushes per-session private economy digests
        /// (opcode 15) on the host's TickCompleted cadence.
        /// </summary>
        private PlayerStateSyncEmitter _stateSyncEmitter;

        /// <summary>
        /// N-02 (ТЗ 6.0-C): the match layout's roster mode. The legacy
        /// <see cref="MatchRosterMode.PrototypeArmies"/> keeps the Phase 1–4
        /// six-unit prototype armies (the dozens of existing tests and the
        /// PlayMode slices); <see cref="MatchRosterMode.RtsStartingBase"/>
        /// provisions each player a real faction HQ, a builder and starting
        /// cash for the Phase 6 vertical slice. Set before
        /// <see cref="Initialize"/>, like <see cref="TopologyMode"/>.
        /// </summary>
        public MatchRosterMode RosterMode { get; set; } = MatchRosterMode.PrototypeArmies;

        /// <summary>
        /// N-02: the lobby's faction per player slot (indexed by slot, the
        /// same order <paramref name="owners"/> arrives in at match start).
        /// Null keeps the faction-less server (faction-gated rules disabled).
        /// Set before <see cref="Initialize"/>.
        /// </summary>
        public byte[] PlayerFactions { get; set; }

        /// <summary>
        /// N-02: builder spawn offset from the HQ base point, towards the
        /// map centre — the builder starts beside its HQ, not inside it.
        /// </summary>
        public const int BuilderOffsetMm = 5_000;

        /// <summary>N-02: the RTS starting cash (ТЗ 6.0-C).</summary>
        public const int DefaultStartingCredits = 10_000;

        /// <summary>
        /// The match layout this host fills (Step 5.6.1): a two-player skirmish
        /// or the four-slot 2v2 team match. Must be set before
        /// <see cref="Initialize"/> — the combat relation is fixed when the
        /// server core is constructed.
        /// </summary>
        public MatchTopologyMode TopologyMode
        {
            get => _topologyMode;
            set
            {
                if (_host != null)
                {
                    throw new InvalidOperationException(
                        "TopologyMode must be set before Initialize: the combat relation is fixed at host construction.");
                }

                _topologyMode = value;
            }
        }

        private MatchTopologyMode _topologyMode;

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

            // Step 5.6.1: the topology decides the hostility policy. A team
            // match runs Team2v2CombatRelation (players 1,2 vs 3,4); a skirmish
            // keeps the null skirmish default. M2 remediation (P0-01): the
            // relation is a per-match instance — the lobby's slot mapping is
            // configured into it at start, so hostility follows the same table
            // the fog filter uses instead of the PlayerId fold.
            Team2v2CombatRelation teamRelation = null;
            if (TopologyMode == MatchTopologyMode.Team2v2)
            {
                teamRelation = new Team2v2CombatRelation();
            }

            _teamRelation = teamRelation;
            _host = new ServerHost(tickRate, teamRelation, PlayerFactions);
            _transportHost = new ServerTransportHost(_carrier, _host.Sessions, _host.Server);

            // The forming match admits exactly the players the topology needs:
            // SessionManager.TryStartMatch requires the template owners to
            // cover the assigned players exactly, so an over-full lobby would
            // deadlock the start forever.
            var lobbyCapacity = Math.Min(maxPlayers, RequiredPlayersForStart);
            _match = _host.CreateSessionMatch(lobbyCapacity);
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

            // Step 5.6.1: the filter is kept so a team match can map the four
            // slots onto their teams at start (allies see each other through
            // the fog; unmapped players stay enemies-of-everyone).
            _fogFilter = new FogOfWarReplicationFilter();
            _host.AttachReplication(
                new TransportReplicationAdapter(_transportHost),
                ServerReplicationEmitterConfig.Default,
                _fogFilter);

            // M2 remediation (P1-03): every bound session receives its own
            // private economy digest (credits, power, queue heads) once a
            // second — owner-only delivery, rivals never see the balance.
            _stateSyncEmitter = new PlayerStateSyncEmitter(_host.Server, _transportHost);
            _host.TickCompleted += _stateSyncEmitter.OnTickCompleted;

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
            if (_stateSyncEmitter != null && _host != null)
            {
                _host.TickCompleted -= _stateSyncEmitter.OnTickCompleted;
            }

            _stateSyncEmitter = null;
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
            _teamRelation = null;
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
        /// Builds the base roster from the players currently in the lobby and
        /// hands it to the authoritative start path (M1.1 F1): the match starts
        /// only when exactly the required players are present. A lobby that
        /// waits too long keeps waiting — a one-army match would declare an
        /// instant victory and permanently close the second slot. A team match
        /// additionally maps the four slots onto their fog teams (Step 5.6.1).
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
            var config = RosterMode == MatchRosterMode.RtsStartingBase
                ? BuildRtsStartingRoster(
                    owners, PlayerFactions, TopologyMode, DefaultStartingCredits)
                : TopologyMode == MatchTopologyMode.Team2v2
                    ? BuildTeamMatchConfig(owners)
                    : BuildDefaultMatchConfig(owners);

            TeamSlotMapping2v2 teamMapping = default;
            if (TopologyMode == MatchTopologyMode.Team2v2)
            {
                // M2 remediation (P0-01): the lobby's slot order — not the
                // PlayerId values — decides the teams. One mapping feeds both
                // the combat relation and the fog filter, so allies can never
                // be hostile in combat while sharing a team in FoW (the
                // [2, 3, 4, 5] lobby defect). The relation is configured
                // before the match starts: the first tick already acquires
                // targets through it.
                teamMapping = TeamSlotMapping2v2.FromSlots(owners);
                _teamRelation.ConfigureSlots(teamMapping);
            }

            if (!_host.TryStartSessionMatch(_match, config, out _))
            {
                return false;
            }

            if (TopologyMode == MatchTopologyMode.Team2v2)
            {
                // Step 5.6.1: same-team units are always visible to each other,
                // but only once the filter knows the teams. Without this map a
                // 2v2 lobby spawns four mutually blind armies. M2 remediation
                // (P0-01): the teams come from the same mapping the combat
                // relation received above.
                for (var slot = 0; slot < owners.Length; slot++)
                {
                    _fogFilter.SetPlayerTeam(
                        owners[slot], teamMapping.GetTeamForPlayer(owners[slot]));
                }
            }

            return true;
        }

        /// <summary>
        /// Step 5.6.1 team geometry: exactly four owners in
        /// <see cref="MatchTopology2v2"/> slot order, Team Red (slots 0, 1) on
        /// the west against Team Blue (slots 2, 3) on the east. Each army keeps
        /// the <see cref="UnitsPerArmy"/> roster shape — the HQ sits exactly on
        /// the slot's base point, the rest form a column beside it. Enemy bases
        /// are 70 000 mm apart, allied bases 40 000 mm.
        /// </summary>
        public static MatchConfig BuildTeamMatchConfig(IReadOnlyList<PlayerId> owners)
        {
            if (owners == null)
            {
                throw new ArgumentNullException(nameof(owners));
            }

            if (owners.Count != MatchTopology2v2.SlotCount)
            {
                throw new ArgumentException(
                    $"a 2v2 roster needs exactly {MatchTopology2v2.SlotCount} players", nameof(owners));
            }

            var units = new UnitSpawnSpec[MatchTopology2v2.SlotCount * UnitsPerArmy];
            var count = 0;
            for (var slot = 0; slot < MatchTopology2v2.SlotCount; slot++)
            {
                if (!owners[slot].IsValid)
                {
                    throw new ArgumentException("owner must be valid", nameof(owners));
                }

                var basePoint = TeamBasePositions[slot];
                for (var unitIndex = 0; unitIndex < UnitsPerArmy; unitIndex++)
                {
                    var kind = unitIndex == 0
                        ? UnitKinds.BaseStructure
                        : (unitIndex & 1) == 0 ? UnitKinds.Tank : UnitKinds.Scout;

                    // The HQ (unit 0) sits exactly on the base point; the rest
                    // form a column pushing east of it, clear of the base cell.
                    var position = unitIndex == 0
                        ? basePoint
                        : new WorldPointMm(
                            basePoint.X + (((unitIndex + 1) / 2) * ArmyColumnSpacingMm),
                            basePoint.Z + ((unitIndex % 2 == 1) ? -1 : 1) * (ArmyColumnSpacingMm / 2));

                    units[count++] = new UnitSpawnSpec(
                        owners[slot],
                        position,
                        speedMmPerTick: 350,
                        autoAcquireEnemies: true,
                        kind);
                }
            }

            return new MatchConfig(DefaultUnitStats, units);
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

        /// <summary>
        /// N-02 (ТЗ 6.0-C): the RTS starting roster — per player one real
        /// faction HQ (catalog row, 8x8 footprint, on the base point,
        /// speed 0) and one faction builder beside it (offset towards the
        /// map centre, catalog mobility), plus the starting cash on the
        /// match config. Faction per slot comes from
        /// <paramref name="factions"/> when supplied and valid; a
        /// faction-less lobby provisions USA rows. The blueprint ids and
        /// legacy kinds are data-driven — no per-faction special cases.
        /// </summary>
        public static MatchConfig BuildRtsStartingRoster(
            IReadOnlyList<PlayerId> owners,
            IReadOnlyList<byte> factions = null,
            MatchTopologyMode topology = MatchTopologyMode.Skirmish1v1,
            int startingCredits = DefaultStartingCredits)
        {
            if (owners == null)
            {
                throw new ArgumentNullException(nameof(owners));
            }

            if (owners.Count == 0)
            {
                throw new ArgumentException("at least one player is required", nameof(owners));
            }

            if (topology == MatchTopologyMode.Team2v2 && owners.Count != MatchTopology2v2.SlotCount)
            {
                throw new ArgumentException(
                    "a 2v2 roster needs exactly 4 players", nameof(owners));
            }

            var units = new UnitSpawnSpec[owners.Count * 2];
            var count = 0;
            for (var slot = 0; slot < owners.Count; slot++)
            {
                var owner = owners[slot];
                if (!owner.IsValid)
                {
                    throw new ArgumentException("owner must be valid", nameof(owners));
                }

                WorldPointMm basePoint;
                if (topology == MatchTopologyMode.Team2v2)
                {
                    basePoint = TeamBasePositions[slot];
                }
                else
                {
                    var side = slot % 2 == 0 ? -1 : 1;
                    basePoint = new WorldPointMm(
                        side * ArmySeparationMm * (1 + (slot / 2)), 0);
                }

                var faction = factions != null &&
                              slot < factions.Count &&
                              FactionId.IsValid(factions[slot])
                    ? factions[slot]
                    : FactionId.USA;

                var hqBlueprint =
                    FactionRosterCompletenessValidator.GetHqBlueprintId(faction);
                var builderBlueprint =
                    FactionRosterCompletenessValidator.GetBuilderBlueprintId(faction);

                var builderSpeed = 350;
                var builderKind = UnitKinds.Tank;
                if (ProvisioningCatalog.TryGet(
                        new BlueprintId(builderBlueprint), out var builderDef))
                {
                    builderSpeed = builderDef.SpeedMmPerTick;
                    builderKind = builderDef.LegacyKind;
                }

                // HQ: a completed catalog building on the base point.
                units[count++] = new UnitSpawnSpec(
                    owner,
                    basePoint,
                    speedMmPerTick: 0,
                    autoAcquireEnemies: false,
                    UnitKinds.BaseStructure,
                    hqBlueprint);

                // Builder: beside the HQ, offset towards the map centre.
                units[count++] = new UnitSpawnSpec(
                    owner,
                    new WorldPointMm(
                        basePoint.X + (basePoint.X <= 0 ? BuilderOffsetMm : -BuilderOffsetMm),
                        basePoint.Z),
                    speedMmPerTick: builderSpeed,
                    autoAcquireEnemies: false,
                    builderKind,
                    builderBlueprint);
            }

            return new MatchConfig(DefaultUnitStats, units, startingCredits);
        }

        private void OnDestroy() => Shutdown();
    }

    /// <summary>
    /// N-02 (ТЗ 6.0-C): which roster a dedicated match starts with. The
    /// prototype armies keep every Phase 1–4 test and slice; the RTS mode
    /// provisions the playable starting base (HQ + builder + cash).
    /// Values are append-only: renumbering would re-shape recorded configs.
    /// </summary>
    public enum MatchRosterMode : byte
    {
        /// <summary>Legacy six-unit prototype army per player (Phase 1–4).</summary>
        PrototypeArmies = 0,

        /// <summary>Faction HQ + faction builder + starting cash per player.</summary>
        RtsStartingBase = 1
    }
}
