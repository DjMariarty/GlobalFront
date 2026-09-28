using System;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Replication;
using GlobalFront.Client.UI;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using UnityEngine;
using UnityEngine.Rendering;
using EntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// In-process replication bridge for the local player of a hosted match
    /// (Phase 3, step 3.7, ADR-012 / OD-23).
    ///
    /// It implements the downlink (<see cref="IReplicationTransport"/>, what
    /// <see cref="ServerReplicationEmitter"/> sends through) and the uplink
    /// (<see cref="IReplicationUplink"/>, what
    /// <see cref="ClientTransportReplicationBridge"/> sends feedback through)
    /// on one object, so a server tick and the packet it produces travel
    /// without a socket, a carrier or a datagram.
    ///
    /// There is no copy and no allocation on either hop. The emitter hands
    /// over the buffer it encoded into and the bridge decodes inside the same
    /// call, so forwarding the reference is safe — the protocol's own contract
    /// is that a payload is only guaranteed for the duration of the handler
    /// (see <see cref="IReplicationUplink.SnapshotPayloadReceived"/>). The
    /// feedback direction is the same story in reverse: the bridge's ack and
    /// request buffers are reused slots.
    ///
    /// Feedback is decoded here before it is routed, which is what the transport
    /// would do on a real connection and what makes "the wire was well formed"
    /// an assertion the slice can count instead of an assumption.
    /// </summary>
    public sealed class LocalReplicationLoopback : IReplicationTransport, IReplicationUplink
    {
        private readonly SessionId _session;
        private readonly MatchId _match;
        private readonly PlayerId _player;

        public LocalReplicationLoopback(SessionId session, MatchId match, PlayerId player)
        {
            if (!session.IsValid)
            {
                throw new ArgumentException("a loopback needs the session it stands for", nameof(session));
            }

            _session = session;
            _match = match;
            _player = player;
        }

        public event Action<SessionId, MatchId, PlayerId> SessionAttached;

        public event Action<SessionId, MatchId, PlayerId> SessionReattached;

        public event Action<SessionId> PostSessionReattached;

        public event Action<SessionId, TransportDisconnectReason> SessionDetached;

        public event Action<ReplicationFeedbackMessage> FeedbackReceived;

        public event Action<ulong, byte[], int> SnapshotPayloadReceived;

        public SessionId Session => _session;

        public MatchId Match => _match;

        public PlayerId Player => _player;

        /// <summary>Keyframe slices handed to the client side.</summary>
        public int ForwardedSliceCount { get; private set; }

        /// <summary>Deltas handed to the client side.</summary>
        public int ForwardedDeltaCount { get; private set; }

        /// <summary>Acknowledgements decoded on the way back to the emitter.</summary>
        public int RoutedAckCount { get; private set; }

        /// <summary>Requests decoded on the way back to the emitter.</summary>
        public int RoutedRequestCount { get; private set; }

        /// <summary>Bytes moved in both directions, for the bandwidth read-out.</summary>
        public int ForwardedPayloadBytes { get; private set; }

        /// <summary>
        /// Anything this bridge could not make sense of. It is the one counter
        /// that must stay at zero for the slice to claim the loop is closed.
        /// </summary>
        public int RejectedMessageCount { get; private set; }

        /// <summary>
        /// Raises <see cref="SessionAttached"/>. Call it after the emitter
        /// exists: the emitter only tracks sessions that attach from the moment
        /// it was constructed, and it is request-driven, so attaching alone
        /// sends nothing until the client asks for a baseline.
        /// </summary>
        public void AttachClient() => SessionAttached?.Invoke(_session, _match, _player);

        /// <summary>
        /// The reconnect path (OD-20 / ADR-011): the emitter answers a
        /// re-attachment with a pushed keyframe rather than waiting for a
        /// request, which is the one behaviour a resyncing client cannot
        /// afford to schedule itself.
        /// </summary>
        public void ReattachClient()
        {
            SessionReattached?.Invoke(_session, _match, _player);
            PostSessionReattached?.Invoke(_session);
        }

        /// <summary>Stops the emitter tracking this session, as a client leaving would.</summary>
        public void DetachClient(TransportDisconnectReason reason) =>
            SessionDetached?.Invoke(_session, reason);

        bool IReplicationTransport.SendToSession(
            SessionId session,
            ulong envelopeTick,
            byte[] buffer,
            int length)
        {
            if (buffer == null || length < 1 || length > buffer.Length || session != _session)
            {
                RejectedMessageCount++;
                return false;
            }

            // Classified on the protocol byte the same way the bridge will read
            // it, so these counters describe what the client side actually saw.
            if (buffer[0] == KeyframeSliceCodec.MessageType)
            {
                ForwardedSliceCount++;
            }
            else if (buffer[0] == DeltaSnapshotProtocol.MessageTypeDelta)
            {
                ForwardedDeltaCount++;
            }

            ForwardedPayloadBytes += length;
            SnapshotPayloadReceived?.Invoke(envelopeTick, buffer, length);
            return true;
        }

        bool IReplicationUplink.TrySendFeedback(
            ReplicationFeedbackKind kind,
            byte[] payload,
            int length)
        {
            if (payload == null || length < 1 || length > payload.Length)
            {
                RejectedMessageCount++;
                return false;
            }

            var source = new ReadOnlySpan<byte>(payload, 0, length);
            TransportMessageType type;
            switch (kind)
            {
                case ReplicationFeedbackKind.Ack:
                    if (SnapshotAckCodec.TryDecode(source, out _) != SnapshotAckCodecResult.Ok)
                    {
                        RejectedMessageCount++;
                        return false;
                    }

                    RoutedAckCount++;
                    type = TransportMessageType.SnapshotAck;
                    break;

                case ReplicationFeedbackKind.Request:
                    if (ReplicationRequestCodec.TryDecode(source, out _) != ReplicationRequestCodecResult.Ok)
                    {
                        RejectedMessageCount++;
                        return false;
                    }

                    RoutedRequestCount++;
                    type = TransportMessageType.ReplicationRequest;
                    break;

                default:
                    RejectedMessageCount++;
                    return false;
            }

            FeedbackReceived?.Invoke(
                new ReplicationFeedbackMessage(_session, type, payload, 0, length));
            return true;
        }
    }

    /// <summary>
    /// A snapshot source that stops describing units once they have stayed dead
    /// long enough (Phase 3, step 3.7).
    ///
    /// The reason this exists is a gap the vertical slice cannot route around:
    /// <see cref="MatchServer"/> never evicts a destroyed unit from the roster
    /// it copies into snapshots, so a real battle produces an unbounded stream
    /// of health-zero records and <see cref="ServerSnapshotDiffEngine"/> never
    /// sees an entity disappear. Without that disappearance there is no
    /// <see cref="DeltaRemoveRecord"/>, no view returns to the pool and the
    /// replicated world only ever grows.
    ///
    /// Replication is the layer that already owns "this is no longer part of
    /// the world I describe" — that is what <see cref="DeltaRemoveCause"/>
    /// models — so the reap lives here rather than in the simulation, which
    /// keeps the authoritative rules and the PlayMode contracts on
    /// <see cref="MatchServer"/> untouched.
    ///
    /// The fatal hit is still described: a unit is dropped one capture after it
    /// is first seen dead, so the client receives health zero, suppresses the
    /// bar, prunes the selection, and only then removes the entity.
    /// </summary>
    public sealed class CorpseReapSnapshotSource : IServerSnapshotSource
    {
        private readonly IServerSnapshotSource _inner;
        private readonly uint[] _deadSinceCapture;
        private readonly int _graceCaptures;
        private uint _capture;

        public CorpseReapSnapshotSource(
            IServerSnapshotSource inner,
            int trackedEntities,
            int graceCaptures = DefaultGraceCaptures)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (trackedEntities < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(trackedEntities));
            }

            if (graceCaptures < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(graceCaptures));
            }

            _deadSinceCapture = new uint[trackedEntities];
            _graceCaptures = graceCaptures;
        }

        /// <summary>
        /// Two captures: one to report the hit that killed the unit, one to let
        /// anything that reads health (bar, HUD, attrition) see it before the
        /// entity leaves the world.
        /// </summary>
        public const int DefaultGraceCaptures = 2;

        /// <summary>
        /// Marks a slot as already reaped. Without it the grace window keeps
        /// expiring on every later capture and the count reports how many times a
        /// corpse was left out rather than how many units this source removed.
        /// </summary>
        private const uint ReapedSentinel = uint.MaxValue;

        /// <summary>Units removed from the described roster by this source.</summary>
        public int ReapedCount { get; private set; }

        /// <summary>Captures taken through this source, one per replication tick.</summary>
        public uint CaptureCount => _capture;

        public int CopySnapshots(Span<ServerUnitSnapshot> destination)
        {
            var count = _inner.CopySnapshots(destination);
            if (count < 0)
            {
                return count;
            }

            _capture++;
            var written = 0;
            for (var index = 0; index < count; index++)
            {
                var snapshot = destination[index];
                var slot = EntitySlot(snapshot.Entity);
                if (slot < 0)
                {
                    // An entity id this source cannot track is described rather
                    // than dropped: guessing at the lifetime of something outside
                    // the tracked range is how a reap becomes a silent disappearance.
                    destination[written++] = snapshot;
                    continue;
                }

                if (snapshot.CurrentHealth > 0)
                {
                    _deadSinceCapture[slot] = 0;
                    destination[written++] = snapshot;
                    continue;
                }

                if (_deadSinceCapture[slot] == ReapedSentinel)
                {
                    continue;
                }

                if (_deadSinceCapture[slot] == 0)
                {
                    _deadSinceCapture[slot] = _capture;
                    destination[written++] = snapshot;
                    continue;
                }

                if (_capture - _deadSinceCapture[slot] < (uint)_graceCaptures)
                {
                    destination[written++] = snapshot;
                    continue;
                }

                _deadSinceCapture[slot] = ReapedSentinel;
                ReapedCount++;
            }

            return written;
        }

        private int EntitySlot(EntityId entity)
        {
            // EntityIds are assigned sequentially from 1 by the server, so the
            // id is the index. Anything else is refused rather than wrapped.
            var value = entity.Value;
            if (value == 0 || value > (uint)_deadSinceCapture.Length)
            {
                return -1;
            }

            return (int)value - 1;
        }
    }

    /// <summary>
    /// The 400-unit tactical vertical slice (Phase 3, step 3.7, ADR-012 /
    /// OD-23..OD-29): every accepted Phase 3 layer wired into one running
    /// client, from the authoritative tick to the pixels.
    ///
    /// What it owns, in the order the data moves:
    /// a <see cref="LocalMatchHost"/> pacing an authoritative
    /// <see cref="MatchServer"/>, a <see cref="LocalReplicationLoopback"/>
    /// carrying that host's packets to a
    /// <see cref="ClientReplicationReceiver"/> through a
    /// <see cref="ClientTransportReplicationBridge"/>, the
    /// <see cref="ClientReplicationWorld"/> the receiver applies into, and the
    /// presentation stack that reads it — <see cref="UnitViewTickBuffer"/>,
    /// <see cref="UnitViewPool"/>, <see cref="UnitViewBinder"/>,
    /// <see cref="UnitOverlayBatcher"/>, selection, commands, minimap radar and
    /// both tactical HUD canvases.
    ///
    /// Two design rules make it testable rather than merely runnable.
    /// <list type="number">
    /// <item><description>
    /// <b>One frame loop.</b> Every wired component that would drive itself from
    /// Unity's <c>Update</c> is left disabled and stepped through
    /// <see cref="StepSimulationTick"/> and <see cref="StepPresentationFrame"/>.
    /// A driver, a camera rig and a HUD presenter that each poll the same mouse
    /// and the same clock double-step it, and an EditMode proof of a
    /// self-stepping stack proves nothing about the frame the player gets.
    /// </description></item>
    /// <item><description>
    /// <b>Nothing allocates after <see cref="Initialize"/>.</b> The scenario is
    /// built once, the pool is warmed to the exact per-archetype counts, the
    /// bridge's decode sinks are pre-sized past a full-roster packet, and every
    /// steady-state method here works on pre-allocated arrays.
    /// </description></item>
    /// </list>
    ///
    /// Headless safety: <c>-batchmode -nographics</c> is a supported target. The
    /// HUD stack, the pool and the replication pipeline all run without a GPU;
    /// only <see cref="DrawOverlaysDirect"/> needs one, and it is gated on
    /// <c>createVisuals</c>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TacticalVerticalSliceRunner : MonoBehaviour
    {
        /// <summary>The OD-28 profile: 400 units on one battlefield.</summary>
        public const int DefaultUnitCount = 400;

        /// <summary>
        /// Two units is the smallest honest slice: one army on each side, so a
        /// match can reach a terminal outcome and a command can have an owner
        /// and a target.
        /// </summary>
        public const int MinUnitCount = 2;

        /// <summary>
        /// The battlefield is OD-27's 400 × 400 m, and the two armies start in
        /// opposite quadrants of it, in millimetres: southwest for the local
        /// player, northeast for the enemy.
        /// </summary>
        public const int FriendlyQuadrantMinMillimetres = -160_000;
        public const int FriendlyQuadrantMaxMillimetres = -40_000;
        public const int EnemyQuadrantMinMillimetres = 40_000;
        public const int EnemyQuadrantMaxMillimetres = 160_000;

        /// <summary>Prototype balance, duplicated from the legacy controller so the slice fights with the same numbers.</summary>
        public static readonly CombatStats DefaultUnitStats =
            new CombatStats(maximumHealth: 100, damage: 25, rangeMm: 7000, cooldownTicks: 10);

        /// <summary>Movement per authoritative tick, equal to the tick buffer's extrapolation ceiling.</summary>
        public const int MovementPerTickMillimetres = UnitViewTickBuffer.DefaultMaxStepPerTickMm;

        /// <summary>
        /// Where each army converges when the slice is told to march. The two
        /// points are fourteen metres apart because that is what puts the
        /// formations inside <see cref="SimulationConstants.AutoAcquireRangeMm"/>:
        /// armies ordered to the same quadrant corner would park both sides just
        /// out of contact and the battle would never start.
        /// </summary>
        private static readonly WorldPointMm FriendlyMarchDestination = new WorldPointMm(5_000, 5_000);
        private static readonly WorldPointMm EnemyMarchDestination = new WorldPointMm(-5_000, -5_000);

        /// <summary>
        /// OD-27's half-extent in metres, derived by an exact division rather
        /// than by multiplying millimetres by 0.001f: this number is compared for
        /// equality against the camera's map bounds and the ground mesh, and a
        /// float that is 199.99999 fails an equality nobody can read off the
        /// two-decimal report.
        /// </summary>
        private const float MapHalfExtentMetres = MinimapBounds.DefaultHalfExtentMillimetres / 1000f;

        private const int FormationColumns = 10;
        private const int FormationSpacingMillimetres = 3200;
        private const int MaxOrderBatch = SimulationConstants.MaxSelectedEntities;
        private const int CameraOverviewHeightMetres = 150;
        private const int GroundTiles = 8;

        /// <summary>
        /// How long the slice will wait for the baseline keyframe: enough ticks
        /// for the request, the paced slices of the largest legal roster and the
        /// acknowledgement, in exchange for a few tens of milliseconds of
        /// initialization in a full-capacity match.
        /// </summary>
        private const int BaselineConvergenceTicks = 256;

        // Wiring the runner owns.
        private LocalMatchHost _host;
        private ServerReplicationEmitter _emitter;
        private CorpseReapSnapshotSource _snapshotSource;
        private LocalReplicationLoopback _loopback;
        private ClientReplicationReceiver _receiver;
        private ClientTransportReplicationBridge _bridge;
        private ClientReplicationWorld _world;
        private UnitCatalog _catalog;
        private UnitViewTickBuffer _tickBuffer;
        private UnitViewPool _viewPool;
        private UnitViewBinder _binder;
        private UnitOverlayBatcher _overlayBatcher;
        private UnitSelectionController _selection;
        private UnitCommandChannelSink _commandSink;
        private LocalCommandChannel _commandChannel;
        private TacticalHudPointerBlocker _pointerBlocker;
        private MinimapRadarModel _radarModel;
        private SelectionHudState _selectionHudModel;

        // Components: the runner steps all of these itself.
        private UnitSelectionDriver _selectionDriver;
        private SelectionMarqueePresenter _marqueePresenter;
        private MinimapInteractionController _minimapInteraction;
        private PermanentHudPresenter _permanentHud;
        private SelectionHudPresenter _selectionHud;
        private RtsCameraController _cameraController;
        private RtsInputManager _inputManager;
        private Camera _viewportCamera;

        // A rig and a pointer the slice borrows from the scene are not the slice's to
        // leave rewired, so their pre-slice state is captured before it is overwritten.
        private bool _ownsInputManager;
        private bool _savedCameraEnabled;
        private Rect _savedMapBounds;
        private float _savedMinHeight;
        private float _savedMaxHeight;
        private float _savedMinPitch;
        private float _savedMaxPitch;
        private bool _savedInputMockMode;

        // Content created for visuals, tracked so teardown releases native objects.
        private GameObject _ownedCameraObject;
        private GameObject _contentRoot;
        private GameObject _prototypeRoot;
        private Mesh _groundMesh;
        private Material _groundMaterial;
        private Mesh[] _unitMeshes;
        private Material[] _unitMaterials;
        private int _nextMeshSlot;
        private Material _overlayMaterial;
        private bool _ownsOverlayMaterial;
        private Mesh _overlayRingMesh;
        private Mesh _overlayBarMesh;
        private int _overlayRingPassIndex = -1;
        private int _overlayBarPassIndex = -1;
        private bool _overlayResourcesUsable;
        private CommandBuffer _overlayCommandBuffer;
        private UnitOverlayRenderPass.OverlayChunkBuffers[] _ringChunks;
        private UnitOverlayRenderPass.OverlayChunkBuffers[] _barChunks;

        // Match identity and roster, resolved once at initialization.
        private MatchId _match;
        private SessionId _localSession;
        private SessionId _enemySession;
        private PlayerId _localPlayer;
        private PlayerId _enemyPlayer;
        private ClientSession _clientSession;
        private EntityId[] _friendlyEntities = Array.Empty<EntityId>();
        private EntityId[] _enemyEntities = Array.Empty<EntityId>();
        private int[] _entityCountByKind = new int[UnitKinds.Count];
        private uint[] _nextSequenceByPlayer = new uint[SimulationConstants.MaxPlayers + 1];

        private double _clockSeconds;
        private ulong _lastCapturedTick;
        private int _friendlyAliveCount;
        private int _enemyAliveCount;
        private bool _isInitialized;
        private bool _createVisuals;
        private int _unitCount;

        public LocalMatchHost Host => _host;

        public ServerReplicationEmitter Emitter => _emitter;

        public LocalReplicationLoopback Loopback => _loopback;

        public ClientReplicationReceiver Receiver => _receiver;

        public ClientTransportReplicationBridge Bridge => _bridge;

        public ClientReplicationWorld World => _world;

        public UnitCatalog Catalog => _catalog;

        public UnitViewTickBuffer TickBuffer => _tickBuffer;

        public UnitViewPool ViewPool => _viewPool;

        public UnitViewBinder Binder => _binder;

        public UnitOverlayBatcher OverlayBatcher => _overlayBatcher;

        public UnitSelectionController Selection => _selection;

        public UnitCommandChannelSink CommandSink => _commandSink;

        public ICommandChannel CommandChannel => _commandChannel;

        public UnitSelectionDriver SelectionDriver => _selectionDriver;

        public SelectionMarqueePresenter MarqueePresenter => _marqueePresenter;

        public TacticalHudPointerBlocker PointerBlocker => _pointerBlocker;

        public MinimapRadarModel RadarModel => _radarModel;

        public MinimapInteractionController MinimapInteraction => _minimapInteraction;

        public PermanentHudPresenter PermanentHud => _permanentHud;

        public SelectionHudState SelectionHudModel => _selectionHudModel;

        public SelectionHudPresenter SelectionHud => _selectionHud;

        public RtsCameraController CameraController => _cameraController;

        public RtsInputManager InputManager => _inputManager;

        public Camera ViewportCamera => _viewportCamera;

        public ClientSession LocalSession => _clientSession;

        public PlayerId LocalPlayer => _localPlayer;

        public PlayerId EnemyPlayer => _enemyPlayer;

        public SessionId EnemySession => _enemySession;

        public MatchId Match => _match;

        /// <summary>The roster size the slice was initialized with.</summary>
        public int UnitCount => _unitCount;

        /// <summary>Live units this side of the battle, read from the replicated world.</summary>
        public int FriendlyAliveCount => _friendlyAliveCount;

        /// <summary>
        /// Live units on the other side. Every owner that is not the local
        /// player counts: on a two-army slice that is the enemy, and ownership
        /// comes from the replicated record rather than from a view guess.
        /// </summary>
        public int EnemyAliveCount => _enemyAliveCount;

        public int ReapedCorpseCount => _snapshotSource?.ReapedCount ?? 0;

        public ulong CurrentTick => _host?.CurrentTick ?? 0ul;

        public double RenderTick => _tickBuffer?.RenderTick ?? 0.0;

        public bool IsInitialized => _isInitialized;

        public bool CreatesVisuals => _createVisuals;

        /// <summary>
        /// Builds and runs the whole slice: scenario, host, replication loop,
        /// presentation and HUD, then advances ticks until the baseline
        /// keyframe has installed every unit in the client world.
        /// </summary>
        /// <param name="unitCount">Units in the match, split between the two armies.</param>
        /// <param name="createVisuals">
        /// Build procedural hull, turret and ground meshes. Off, the pool creates
        /// its placeholder views and the slice runs headless with nothing to see;
        /// the overlays, the radar and the HUD are still wired either way.
        /// </param>
        /// <param name="autoStartRealTime">
        /// Hand the authoritative tick schedule to the host's
        /// <see cref="TickDriver"/>. Off leaves it manual, which is what
        /// <see cref="StepSimulationTick"/> requires.
        /// </param>
        /// <param name="enemyAutoAcquire">
        /// Whether the enemy army picks its own targets inside
        /// <see cref="SimulationConstants.AutoAcquireRangeMm"/>. The local army is
        /// always player-driven, so this is what makes the slice defensive or inert.
        /// </param>
        public void Initialize(
            int unitCount = DefaultUnitCount,
            bool createVisuals = true,
            bool autoStartRealTime = true,
            bool enemyAutoAcquire = true)
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException(
                    "the tactical vertical slice is already initialized; call Shutdown first");
            }

            if (unitCount < MinUnitCount || unitCount > ClientReplicationWorld.DefaultCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(unitCount),
                    $"a tactical vertical slice needs between {MinUnitCount} and {ClientReplicationWorld.DefaultCapacity} units.");
            }

            _unitCount = unitCount;
            _createVisuals = createVisuals;

            var friendlyCount = (unitCount + 1) / 2;
            var enemyCount = unitCount - friendlyCount;

            BuildHost(friendlyCount, enemyCount);
            AttachReplication();
            BuildPresentation();
            StartMatch(friendlyCount, enemyCount, enemyAutoAcquire);
            BuildInterface();
            ReplicateUntilBaseline(unitCount);

            if (autoStartRealTime)
            {
                _host.StartRealTime();
            }

            _isInitialized = true;
        }

        /// <summary>
        /// Advances exactly one authoritative tick and the replication it
        /// produces: the host simulates, the emitter streams, the loopback
        /// delivers, the receiver applies, and the tick buffer captures the
        /// world when the applied tick moved.
        /// </summary>
        public void StepSimulationTick()
        {
            EnsureInitialized();
            EnsureManualTickDriver();
            AdvanceOneTick();
        }

        /// <summary>
        /// The tick itself: simulate, then move the packets the tick produced.
        /// Kept apart from the public entry point because initializing the slice
        /// runs ticks before the slice counts as initialized — asking a runner
        /// to assert its own readiness mid-construction is how a constructor
        /// throws at the thing it is trying to build.
        /// </summary>
        private void AdvanceOneTick()
        {
            _clockSeconds += SimulationConstants.ServerTickDurationSeconds;
            _host.TickOnce();
            PumpReplication();
        }

        private void EnsureManualTickDriver()
        {
            if (_host.TickDriverMode != TickDriverMode.Manual)
            {
                throw new InvalidOperationException(
                    "the host is pacing its own ticks; StepSimulationTick needs a manual TickDriver");
            }
        }

        /// <summary>
        /// One presentation frame for the whole slice, in the order the layers
        /// require: the camera settles, views sample the tick buffer, the driver
        /// reads the pointer against the settled camera, the overlay batch is
        /// built from the positions that sampling just wrote, and only then do
        /// the two HUD canvases refresh.
        ///
        /// There is no render-tick argument because there is nothing to pass:
        /// the render clock belongs to <see cref="UnitViewTickBuffer"/> and is
        /// advanced by <see cref="UnitViewBinder.Render"/>, which reads
        /// <see cref="TickBuffer"/> afterwards.
        /// </summary>
        public void StepPresentationFrame(float deltaTimeSeconds, double unscaledTimeSeconds)
        {
            EnsureInitialized();

            _cameraController.ManualUpdate(deltaTimeSeconds);
            _binder.Render(deltaTimeSeconds);

            // Commands land on the next tick, the same contract the legacy
            // controller keeps (ADR-007), and both pointer paths read it here.
            var requestedTick = _host.CurrentTick + 1ul;
            _selectionDriver.RequestedTick = requestedTick;
            _minimapInteraction.RequestedTick = requestedTick;
            _permanentHud.RequestedTick = requestedTick;

            if (_permanentHud.TryGetMinimapScreenRect(out var minimapRectPx))
            {
                _minimapInteraction.SetMinimapScreenRect(minimapRectPx);
            }

            _selectionDriver.ManualUpdate(unscaledTimeSeconds);
            _overlayBatcher.BuildBatches(_binder, _cameraController.transform.rotation);
            _permanentHud.ManualUpdate(unscaledTimeSeconds);
            _selectionHud.ManualUpdate();
        }

        /// <summary>
        /// Draws the built overlay batches directly, for a Play Mode session
        /// whose active URP asset has no <see cref="UnitOverlayRendererFeature"/>
        /// on its renderer. When the feature is live it owns these batches and
        /// drawing here too would be the double-draw the legacy prototype bars
        /// still suffer from, so this returns without touching them.
        ///
        /// It reuses the render pass's own chunking: an instanced draw reads the
        /// first <c>count</c> entries of the array it is handed, so a batch past
        /// the shader's instance ceiling has to be copied down per draw.
        /// </summary>
        public void DrawOverlaysDirect()
        {
            if (!_overlayResourcesUsable || _createVisuals == false || _overlayCommandBuffer == null)
            {
                return;
            }

            var feature = UnitOverlayRendererFeature.Active;
            if (feature != null && feature.IsReady)
            {
                return;
            }

            var ringCount = _overlayBatcher.SelectionRingCount;
            var barCount = _overlayBatcher.HealthBarCount;
            if (ringCount == 0 && barCount == 0)
            {
                return;
            }

            var cmd = _overlayCommandBuffer;
            cmd.Clear();
            if (ringCount > 0)
            {
                DrawOverlayChunk(
                    cmd,
                    _overlayRingMesh,
                    _overlayRingPassIndex,
                    _overlayBatcher.SelectionRingMatrixBuffer,
                    _overlayBatcher.SelectionRingPropertyBuffer,
                    ringCount,
                    UnitOverlayGeometry.RingColorProperty,
                    _ringChunks);
            }

            if (barCount > 0)
            {
                DrawOverlayChunk(
                    cmd,
                    _overlayBarMesh,
                    _overlayBarPassIndex,
                    _overlayBatcher.HealthBarMatrixBuffer,
                    _overlayBatcher.HealthBarPropertyBuffer,
                    barCount,
                    UnitOverlayGeometry.HealthBarParamsProperty,
                    _barChunks);
            }

            Graphics.ExecuteCommandBuffer(cmd);
        }

        /// <summary>
        /// Orders both armies to converge on the centre, in batches of
        /// <see cref="SimulationConstants.MaxSelectedEntities"/> per order.
        ///
        /// It goes through the host's session command path rather than through
        /// the selection and the command sink, because the enemy army is owned by
        /// a different player and the selection controller would refuse to order
        /// units it does not own. That refusal is a rule, not an obstacle: an
        /// autonomous enemy has to be commanded by its own session, which a
        /// hosted local match happens to have.
        /// </summary>
        /// <returns>The number of move orders the host accepted.</returns>
        public int IssueConvergenceMarch()
        {
            EnsureInitialized();

            return IssueArmyOrders(
                       _localSession, _localPlayer, _friendlyEntities, GameCommandType.Move,
                       FriendlyMarchDestination, default) +
                   IssueArmyOrders(
                       _enemySession, _enemyPlayer, _enemyEntities, GameCommandType.Move,
                       EnemyMarchDestination, default);
        }

        /// <summary>
        /// Orders each army to attack a unit of the other, which is what actually
        /// starts the battle — and the reason it has to be said out loud is a rule
        /// in <see cref="MatchServer"/>: applying a move order clears the unit's
        /// automatic acquisition and its current target, so two armies that were
        /// only marched together stand fourteen metres apart and never fire.
        /// An attack order re-arms acquisition, so the melee keeps fighting after
        /// the named target dies.
        /// </summary>
        /// <param name="forLocalArmy">Order the player's units to fight.</param>
        /// <param name="forEnemyArmy">Order the autonomous army to fight.</param>
        /// <returns>The number of attack orders the host accepted.</returns>
        public int IssueEngagementOrders(
            bool forLocalArmy = true,
            bool forEnemyArmy = true)
        {
            EnsureInitialized();

            var accepted = 0;
            if (forLocalArmy)
            {
                accepted += IssueArmyOrders(
                    _localSession, _localPlayer, _friendlyEntities, GameCommandType.Attack,
                    default, _enemyEntities[0]);
            }

            if (forEnemyArmy)
            {
                accepted += IssueArmyOrders(
                    _enemySession, _enemyPlayer, _enemyEntities, GameCommandType.Attack,
                    default, _friendlyEntities[0]);
            }

            return accepted;
        }

        /// <summary>
        /// Releases everything the slice owns: the pool's views, the meshes and
        /// materials it created, the command buffer, the HUD canvases and the
        /// camera it created. Safe to call twice, and safe to call never —
        /// <see cref="OnDestroy"/> calls it.
        /// </summary>
        public void Shutdown()
        {
            if (_host != null)
            {
                _host.StopRealTime();
                if (_loopback != null)
                {
                    // Stops the emitter tracking this session, the same signal a
                    // client leaving a real match raises.
                    _loopback.DetachClient(TransportDisconnectReason.ClientRequested);
                }
            }

            _binder?.Detach();
            _viewPool?.Dispose();
            _overlayCommandBuffer?.Dispose();
            _overlayCommandBuffer = null;

            ReleaseOverlayResources();
            ReleaseContent();

            if (_minimapInteraction != null)
            {
                _minimapInteraction.CancelDrag();
            }

            DestroyWiredComponent(_marqueePresenter);
            DestroyWiredComponent(_permanentHud);
            DestroyWiredComponent(_selectionHud);
            DestroyWiredComponent(_selectionDriver);
            DestroyWiredComponent(_minimapInteraction);

            RestoreBorrowedRig();

            _binder = null;
            _viewPool = null;
            _tickBuffer = null;
            _catalog = null;
            _overlayBatcher = null;
            _selection = null;
            _selectionHudModel = null;
            _radarModel = null;
            _pointerBlocker = null;
            _marqueePresenter = null;
            _permanentHud = null;
            _selectionHud = null;
            _selectionDriver = null;
            _minimapInteraction = null;
            _cameraController = null;
            _viewportCamera = null;
            _inputManager = null;
            _ownsInputManager = false;
            _commandSink = null;
            _commandChannel = null;
            _bridge = null;
            _receiver = null;
            _world = null;
            _emitter = null;
            _snapshotSource = null;
            _host = null;
            _loopback = null;
            _clientSession = null;
            _unitCount = 0;
            _friendlyAliveCount = 0;
            _enemyAliveCount = 0;
            _lastCapturedTick = 0;
            _clockSeconds = 0.0;
            _createVisuals = false;
            _isInitialized = false;
        }

        /// <summary>
        /// Hands back the camera rig and the pointer this slice did not build. A rig the
        /// runner created goes to the axe with the rest of its content, so only the
        /// adopted one has a state to return; an input manager the runner added to its own
        /// root is destroyed with the component list rather than rewired.
        /// </summary>
        private void RestoreBorrowedRig()
        {
            if (_cameraController != null && _ownedCameraObject == null)
            {
                // The height and pitch setters clamp against each other, so the wide
                // end of each pair goes back first: a minimum restored while the
                // slice's maximum still stands would be clipped and the original lost.
                _cameraController.MaxHeight = _savedMaxHeight;
                _cameraController.MinHeight = _savedMinHeight;
                _cameraController.MaxPitch = _savedMaxPitch;
                _cameraController.MinPitch = _savedMinPitch;
                _cameraController.MapBounds = _savedMapBounds;
                _cameraController.enabled = _savedCameraEnabled;
            }

            if (_inputManager != null)
            {
                if (_ownsInputManager)
                {
                    DestroyContentObject(_inputManager);
                }
                else
                {
                    // Mock inputs the driver last set would otherwise steer the next
                    // reader of the same shared manager.
                    _inputManager.ResetMockInputs();
                    _inputManager.SetMockMode(_savedInputMockMode);
                }
            }
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (!_isInitialized || _host.TickDriverMode != TickDriverMode.RealTime)
            {
                return;
            }

            _clockSeconds += Time.unscaledDeltaTime;
            _host.AdvanceRealTime(Time.unscaledDeltaTime);
            PumpReplication();
            StepPresentationFrame(Time.unscaledDeltaTime, Time.unscaledTimeAsDouble);
#endif
        }

        private void LateUpdate()
        {
#if !UNITY_SERVER
            if (!_isInitialized)
            {
                return;
            }

            DrawOverlaysDirect();
#endif
        }

        private void OnDestroy() => Shutdown();

        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException(
                    "the tactical vertical slice has not been initialized");
            }
        }

        /// <summary>
        /// Routes the feedback the receiver queued and captures the world when
        /// an applied packet moved it. Capture is gated on the applied tick
        /// changing because the tick buffer counts a repeat as a stale packet,
        /// and a slice that lies about its own cadence cannot claim a steady state.
        /// </summary>
        private void PumpReplication()
        {
            _bridge.Pump(ClockMilliseconds);

            var applied = _receiver.LastAppliedTick;
            if (applied == 0ul || applied <= _lastCapturedTick)
            {
                return;
            }

            _tickBuffer.CaptureTick(applied, _world);
            _lastCapturedTick = applied;
            ComputeAliveCounts();
        }

        private long ClockMilliseconds => (long)(_clockSeconds * 1000.0 + 0.5);

        private void BuildHost(int friendlyCount, int enemyCount)
        {
            _host = new LocalMatchHost();
            _match = _host.CreateSessionMatch(2);

            // Join order decides identity: the local army joins first and is
            // assigned the lower PlayerId, which is the ADR-008 rule the legacy
            // prototype already depends on. The client never chooses a PlayerId.
            _localSession = _host.CreateSession(_host.CreateConnectionHandle());
            if (_host.TryJoinMatch(_localSession, _match, out _localPlayer) != JoinResult.Assigned)
            {
                throw new InvalidOperationException("the tactical slice could not join its local session.");
            }

            _enemySession = _host.CreateSession(_host.CreateConnectionHandle());
            if (_host.TryJoinMatch(_enemySession, _match, out _enemyPlayer) != JoinResult.Assigned)
            {
                throw new InvalidOperationException("the tactical slice could not join its enemy session.");
            }

            _clientSession = new ClientSession(_localSession, _match, _localPlayer);
            _commandChannel = new LocalCommandChannel(_host, _localSession);
            _commandSink = new UnitCommandChannelSink(_commandChannel);
            _entityCountByKind = new int[UnitKinds.Count];
            _friendlyEntities = new EntityId[friendlyCount];
            _enemyEntities = new EntityId[enemyCount];
        }

        private void AttachReplication()
        {
            _loopback = new LocalReplicationLoopback(_localSession, _match, _localPlayer);

            // The emitter has to exist before the session attaches: it only
            // tracks attachments that arrive after it was constructed.
            _snapshotSource = new CorpseReapSnapshotSource(
                new MatchServerSnapshotSource(_host.Server),
                ClientReplicationWorld.DefaultCapacity);
            _emitter = _host.AttachReplication(_loopback, ServerReplicationEmitterConfig.Default, _snapshotSource);

            _receiver = new ClientReplicationReceiver(ClientReplicationWorld.DefaultCapacity);
            _world = _receiver.World;

            // The default sinks are sized for an ordinary packet. This slice can
            // put a whole roster in one: 400 units reaped, spawned or repaired
            // inside a single tick would overflow a 256-entry remove sink, the
            // bridge would call the payload malformed, and the client would
            // quietly stop following the match.
            var bridgeConfig = new ClientTransportReplicationBridgeConfig(
                ClientTransportReplicationBridgeConfig.DefaultMaxKeyframeUnits,
                ClientTransportReplicationBridgeConfig.DefaultMaxParts,
                ClientTransportReplicationBridgeConfig.DefaultMaxSlicePayloadBytes,
                addSink: ClientReplicationWorld.DefaultCapacity,
                updateSink: ClientReplicationWorld.DefaultCapacity,
                removeSink: ClientReplicationWorld.DefaultCapacity);
            _bridge = new ClientTransportReplicationBridge(_loopback, _receiver, in bridgeConfig);

            _loopback.AttachClient();
        }

        private void BuildPresentation()
        {
            _catalog = CreateSliceCatalog();
            _tickBuffer = new UnitViewTickBuffer(
                ClientReplicationWorld.DefaultCapacity,
                SimulationConstants.ServerTickDurationSeconds,
                _catalog);

            var poolRoot = transform;
            _viewPool = new UnitViewPool(poolRoot);
            _binder = new UnitViewBinder(_viewPool, _world, _tickBuffer, _catalog);
            _overlayBatcher = new UnitOverlayBatcher(
                Mathf.Max(UnitOverlayBatcher.DefaultCapacity, _unitCount));
            _selection = new UnitSelectionController(_binder, _world, _localPlayer);
        }

        /// <summary>
        /// The three archetypes the catalog resolves, with a ring radius that
        /// fits the hull this slice builds for each.
        ///
        /// <see cref="UnitKinds.Artillery"/> does not exist: the defined kinds are
        /// Scout, Tank and BaseStructure, and the unit kind byte is protocol
        /// significant (it is the 40th byte of an add record), so the third
        /// archetype here is the third archetype the catalog and the HUD rows
        /// already carry rather than a new kind invented for a demo.
        /// </summary>
        private static UnitCatalog CreateSliceCatalog()
        {
            return new UnitCatalog(new[]
            {
                new UnitDefinition(UnitKinds.Scout, "Scout", UnitCatalog.PrototypeMaximumHealth, 1500),
                new UnitDefinition(UnitKinds.Tank, "Tank", UnitCatalog.PrototypeMaximumHealth, 2100),
                new UnitDefinition(UnitKinds.BaseStructure, "Base structure", UnitCatalog.PrototypeMaximumHealth, 2700),
            });
        }

        private void StartMatch(int friendlyCount, int enemyCount, bool enemyAutoAcquire)
        {
            var specs = new UnitSpawnSpec[_unitCount];
            for (var index = 0; index < _unitCount; index++)
            {
                var friendly = index < friendlyCount;
                var ordinal = friendly ? index : index - friendlyCount;
                var kind = KindAtOrdinal(ordinal);
                var owner = friendly ? _localPlayer : _enemyPlayer;

                specs[index] = new UnitSpawnSpec(
                    owner,
                    ScenarioPosition(friendly, ordinal, friendly ? friendlyCount : enemyCount),
                    MovementPerTickMillimetres,
                    // The local army is player-driven; the enemy is autonomous
                    // only as far as the slice was told to make it.
                    autoAcquireEnemies: !friendly && enemyAutoAcquire,
                    kind);

                _entityCountByKind[kind]++;
            }

            if (!_host.TryStartSessionMatch(_match, new MatchConfig(DefaultUnitStats, specs), out var entityIds) ||
                entityIds.Length != _unitCount)
            {
                throw new InvalidOperationException("the tactical slice match failed to start.");
            }

            // The server hands out EntityIds in template order, so the first
            // friendlyCount are the local army and the rest are the enemy's.
            for (var index = 0; index < _unitCount; index++)
            {
                if (index < friendlyCount)
                {
                    _friendlyEntities[index] = entityIds[index];
                }
                else
                {
                    _enemyEntities[index - friendlyCount] = entityIds[index];
                }
            }
        }

        /// <summary>
        /// Deterministic cycle across the three archetypes: every third ordinal
        /// is the same kind in both armies, so both sides field all three and the
        /// radar palette, the pool's per-kind free lists and the HUD's archetype
        /// rows are all exercised from the first frame.
        /// </summary>
        private static byte KindAtOrdinal(int ordinal)
        {
            switch (ordinal % 3)
            {
                case 0:
                    return UnitKinds.Scout;
                case 1:
                    return UnitKinds.Tank;
                default:
                    return UnitKinds.BaseStructure;
            }
        }

        /// <summary>
        /// A grid inside the army's own quadrant, spaced from that army's size so
        /// the whole roster fits its 120 m of ground whatever count was asked for.
        /// Ordinal order makes the layout the same run after run, which is what
        /// makes a benchmark comparable and a march reproducible.
        /// </summary>
        private static WorldPointMm ScenarioPosition(bool friendly, int ordinal, int armyCount)
        {
            var min = friendly ? FriendlyQuadrantMinMillimetres : EnemyQuadrantMinMillimetres;
            var max = friendly ? FriendlyQuadrantMaxMillimetres : EnemyQuadrantMaxMillimetres;
            var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, armyCount))));
            var spacing = columns > 1 ? (max - min) / (columns - 1) : 0;
            var column = ordinal % columns;
            var row = ordinal / columns;
            return new WorldPointMm(min + column * spacing, min + row * spacing);
        }

        private void BuildInterface()
        {
            ResolveCameraRig();

            if (_createVisuals)
            {
                BuildGround();
                BuildUnitPrototypes();
                TryBuildOverlayResources();
            }

            WarmupPool();

            _pointerBlocker = new TacticalHudPointerBlocker();
            _radarModel = new MinimapRadarModel(_world, _selection, MinimapBounds.Default);
            _selectionHudModel = new SelectionHudState(_selection, _world, _catalog)
            {
                // The panel reports refusals as well as orders, because an order
                // the server rejected is the thing a player needs to see.
                CommandFeedback = _commandSink,
            };

            BuildMarqueeCanvas();
            BuildPermanentHudCanvas();
            BuildSelectionHudCanvas();
            BuildSelectionDriver();
        }

        private void ResolveCameraRig()
        {
            // Reuse the rig the bootstrap already put on the scene camera: a
            // second camera would render the same battlefield twice, and
            // RtsCameraController provisions its own input manager when it wakes,
            // which is the instance the driver has to read.
            _cameraController = FindAnyObjectByType<RtsCameraController>();
            if (_cameraController == null)
            {
                var cameraObject = new GameObject("Tactical View Camera");
                _ownedCameraObject = cameraObject;
                cameraObject.transform.SetParent(transform, false);
                cameraObject.tag = "MainCamera";
                _viewportCamera = cameraObject.AddComponent<Camera>();
                _viewportCamera.nearClipPlane = 0.3f;
                _viewportCamera.farClipPlane = 900f;
                _cameraController = cameraObject.AddComponent<RtsCameraController>();
            }
            else
            {
                // Adopted rather than built: everything below is a rewrite of someone
                // else's serialized camera, so what it said first is kept for Shutdown.
                _savedCameraEnabled = _cameraController.enabled;
                _savedMapBounds = _cameraController.MapBounds;
                _savedMinHeight = _cameraController.MinHeight;
                _savedMaxHeight = _cameraController.MaxHeight;
                _savedMinPitch = _cameraController.MinPitch;
                _savedMaxPitch = _cameraController.MaxPitch;
                _viewportCamera = _cameraController.GetComponent<Camera>();
            }

            _cameraController.MapBounds = new Rect(
                -MapHalfExtentMetres,
                -MapHalfExtentMetres,
                MapHalfExtentMetres * 2f,
                MapHalfExtentMetres * 2f);
            _cameraController.MinHeight = 20f;
            _cameraController.MaxHeight = 260f;

            // The pitch band is what keeps the whole frustum on the ground. The
            // rig derives pitch from height, and MinimapProjection.TryCameraGroundRect
            // needs all four viewport corners to hit Y = 0; a shallower pitch
            // lets the top corners read sky, and the radar then has no viewport
            // indicator to draw at all.
            _cameraController.MinPitch = 55f;
            _cameraController.MaxPitch = 70f;

            // Overlooking the whole battlefield is the point of the slice: at the
            // rig's 80 m ceiling neither army fits on screen.
            _cameraController.SetFocusPoint(Vector3.zero, immediate: true);
            _cameraController.SetHeight(CameraOverviewHeightMetres, immediate: true);

            // Not ??: a destroyed RtsInputManager left behind by a previous
            // slice is fake-null, and the null-coalescing operator compares the
            // reference, not the object, so it would hand the driver a manager
            // that throws on its first property read.
            _inputManager = _cameraController.GetComponentInChildren<RtsInputManager>();
            if (_inputManager == null)
            {
                _inputManager = RtsInputManager.Instance;
            }

            if (_inputManager == null)
            {
                _inputManager = FindAnyObjectByType<RtsInputManager>();
            }

            if (_inputManager == null)
            {
                _inputManager = gameObject.AddComponent<RtsInputManager>();
                _ownsInputManager = true;
            }

            // The runner owns the frame loop, so the rig does not step itself.
            _cameraController.enabled = false;

            _savedInputMockMode = _inputManager.IsMockMode;

            // A slice with no battlefield has no player either: pin the manager to its
            // mock inputs so a measured frame is decided by what the test sets rather
            // than by where the editor's mouse happens to be. Hardware edges also make
            // a measured zero-GC window non-reproducible, which is the one thing it
            // must not be. A slice with visuals is played by someone holding that
            // mouse, so the same switch hands the hardware back to them; either way
            // Shutdown restores whatever this found.
            _inputManager.SetMockMode(!_createVisuals);
        }

        private void BuildMarqueeCanvas()
        {
            var go = CreateWiredGameObject("Selection Marquee");
            _marqueePresenter = go.AddComponent<SelectionMarqueePresenter>();
            _marqueePresenter.Build();
        }

        private void BuildPermanentHudCanvas()
        {
            var go = CreateWiredGameObject("Permanent Tactical HUD");
            _permanentHud = go.AddComponent<PermanentHudPresenter>();
            _permanentHud.SetViewportCamera(_viewportCamera);
            _permanentHud.Build();
            _permanentHud.Configure(_radarModel, _selection, _commandSink);
            _permanentHud.RegisterBlockingRects(_pointerBlocker);
            _permanentHud.enabled = false;
        }

        private void BuildSelectionHudCanvas()
        {
            var go = CreateWiredGameObject("Selection Detail HUD");
            _selectionHud = go.AddComponent<SelectionHudPresenter>();
            _selectionHud.Build();
            _selectionHud.Configure(_selectionHudModel);
            _selectionHud.RegisterBlockingRects(_pointerBlocker);
            _selectionHud.enabled = false;
        }

        private void BuildSelectionDriver()
        {
            var go = CreateWiredGameObject("Unit Selection Driver");
            _selectionDriver = go.AddComponent<UnitSelectionDriver>();
            _selectionDriver.SetInputManager(_inputManager);
            _selectionDriver.SetCamera(_viewportCamera);
            _selectionDriver.Configure(_selection, _commandSink);
            _selectionDriver.SetMarqueePresenter(_marqueePresenter);
            _selectionDriver.SetHudBlocker(_pointerBlocker);

            var minimapGo = CreateWiredGameObject("Minimap Interaction");
            _minimapInteraction = minimapGo.AddComponent<MinimapInteractionController>();
            _minimapInteraction.Configure(_radarModel, _cameraController, _selection, _commandSink);
            _minimapInteraction.enabled = false;

            _selectionDriver.SetMinimapInteraction(_minimapInteraction);

            // One mouse, one owner. The driver's own Update would step the same
            // pointer edges the runner steps, which would issue a command twice
            // and make the EditMode proof describe a frame nobody renders.
            _selectionDriver.enabled = false;
        }

        private GameObject CreateWiredGameObject(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(transform, false);
            return go;
        }

        /// <summary>
        /// Creates exactly the views the roster needs, per archetype, so no
        /// steady-state tick ever instantiates (OD-26).
        /// </summary>
        private void WarmupPool()
        {
            for (var kind = 1; kind < _entityCountByKind.Length; kind++)
            {
                var count = _entityCountByKind[kind];
                if (count > 0)
                {
                    _viewPool.Warmup((byte)kind, count);
                }
            }
        }

        private void ReplicateUntilBaseline(int unitCount)
        {
            // The emitter is request-driven, so the baseline only arrives after
            // the receiver has asked for it, the host has paced the slices out
            // against its pacing window, and the client has acknowledged them.
            // Ticking until the world is full is how the slice gets a first
            // frame that already describes the whole battlefield.
            var limit = BaselineConvergenceTicks;
            for (var attempt = 0; attempt < limit && _world.LiveCount < unitCount; attempt++)
            {
                AdvanceOneTick();
            }

            if (_world.LiveCount != unitCount)
            {
                // Naming what each half of the loop reported is the difference
                // between a failure a reader can diagnose and a number.
                throw new InvalidOperationException(
                    $"the replicated world holds {_world.LiveCount} of {unitCount} units after {limit} ticks " +
                    $"(receiver state {_receiver.State}, failure {_receiver.FailureReason}, " +
                    $"last applied tick {_receiver.LastAppliedTick}, baseline {_receiver.BaseKeyframeTick}; " +
                    $"loopback forwarded {_loopback.ForwardedSliceCount} slices and {_loopback.ForwardedDeltaCount} deltas, " +
                    $"rejected {_loopback.RejectedMessageCount}).");
            }

            // Binding happens in Render, and a zero delta is the documented
            // freeze: every view is acquired and snapped to authority before the
            // first frame rather than trickling in over the next few.
            _binder.Render(0.0);
        }

        private void ComputeAliveCounts()
        {
            var friendly = 0;
            var enemy = 0;
            var capacity = _world.Capacity;
            for (var slot = 0; slot < capacity; slot++)
            {
                if (!_world.TryGetSlotState(slot, out var state) || state.Health <= 0)
                {
                    continue;
                }

                if (state.Owner == _localPlayer)
                {
                    friendly++;
                }
                else
                {
                    enemy++;
                }
            }

            _friendlyAliveCount = friendly;
            _enemyAliveCount = enemy;
        }

        /// <summary>
        /// Issues one kind of order for a whole army in batches of
        /// <see cref="SimulationConstants.MaxSelectedEntities"/>, the ceiling the
        /// authoritative command payload carries. Sequence numbers advance per
        /// player, because the host rejects a replayed sequence rather than
        /// ignoring it.
        ///
        /// Each batch carries exactly its own units, and a move batch is aimed at
        /// its own block: <see cref="FormationLayout"/> assigns slots by position in
        /// the payload, so a shared buffer whose tail still holds the previous
        /// chunk would order those units a second time, and one destination for
        /// every chunk would bury each hundred in the one before it.
        /// </summary>
        private int IssueArmyOrders(
            SessionId session,
            PlayerId player,
            EntityId[] entities,
            GameCommandType kind,
            WorldPointMm destination,
            EntityId target)
        {
            var formation = new FormationSpec(
                FormationColumns,
                FormationSpacingMillimetres,
                CardinalFacing.North);
            var accepted = 0;
            var batchIndex = 0;

            for (var start = 0; start < entities.Length; start += MaxOrderBatch, batchIndex++)
            {
                var length = Math.Min(MaxOrderBatch, entities.Length - start);
                var batch = new EntityId[length];
                Array.Copy(entities, start, batch, 0, length);

                var sequence = ++_nextSequenceByPlayer[player.Value];
                var header = new CommandHeader(player, sequence, _host.CurrentTick + 1ul, kind);
                var result = kind == GameCommandType.Attack
                    ? _host.TrySessionAttack(session, header, batch, target)
                    : _host.TrySessionMove(
                        session,
                        header,
                        batch,
                        BatchDestination(destination, batchIndex),
                        formation);
                if (result.IsAccepted)
                {
                    accepted++;
                }
            }

            return accepted;
        }

        /// <summary>
        /// The destination of one order batch, offset from the army's aim point by a
        /// whole formation block per batch. The block is <see cref="FormationColumns"/>
        /// rows of <see cref="FormationSpacingMillimetres"/>, so the next batch forms up
        /// behind the first instead of on top of it. Formations in this slice face north,
        /// where the layout's forward axis is +Z.
        /// </summary>
        private static WorldPointMm BatchDestination(WorldPointMm destination, int batchIndex)
        {
            if (batchIndex == 0)
            {
                return destination;
            }

            var stride = (long)FormationColumns * FormationSpacingMillimetres;
            return new WorldPointMm(destination.X, checked((int)(destination.Z + batchIndex * stride)));
        }

        private void DrawOverlayChunk(
            CommandBuffer cmd,
            Mesh mesh,
            int passIndex,
            Matrix4x4[] matrices,
            Vector4[] properties,
            int count,
            int propertyId,
            UnitOverlayRenderPass.OverlayChunkBuffers[] chunks)
        {
            var chunkCount = UnitOverlayRenderPass.PrepareChunks(matrices, properties, count, propertyId, chunks);
            for (var index = 0; index < chunkCount; index++)
            {
                var chunk = chunks[index];
                cmd.DrawMeshInstanced(mesh, 0, _overlayMaterial, passIndex, chunk.Matrices, chunk.Count, chunk.Block);
            }
        }

        // ---------------------------------------------------------------- content

        private void BuildGround()
        {
            var root = new GameObject("Tactical Slice Content");
            root.transform.SetParent(transform, false);
            _contentRoot = root;

            _groundMesh = CreateGroundMesh("GlobalFront Tactical Ground 400x400");
            var ground = new GameObject("Tactical Ground 400x400");
            ground.transform.SetParent(root.transform, false);
            ground.transform.position = new Vector3(0f, UnitPickMath.GroundHeightMetres, 0f);
            var filter = ground.AddComponent<MeshFilter>();
            filter.sharedMesh = _groundMesh;

            // No collider: picking and marquee are arithmetic on the binder's
            // slots (OD-25), and a 400 m physics mesh would only add raycasts
            // nothing performs.
            var renderer = ground.AddComponent<MeshRenderer>();
            _groundMaterial = CreateContentMaterial("Tactical Ground", new Color(0.17f, 0.21f, 0.17f, 1f));
            renderer.sharedMaterial = _groundMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>
        /// One procedural hull and turret per archetype, registered as the
        /// pool's prototype for that kind. The catalog carries stats and ring
        /// radii only, so the geometry has to come from here — and it has to come
        /// from a prototype, because OD-26 bans creating a silhouette per unit.
        ///
        /// Hulls are authored with their base on y = 0: the binder pins every
        /// hull's position to the ground plane, so a centred mesh would arrive
        /// half buried.
        /// </summary>
        private void BuildUnitPrototypes()
        {
            var root = new GameObject("Unit Prototypes");
            root.transform.SetParent(transform, false);
            _prototypeRoot = root;
            _unitMeshes = new Mesh[UnitKinds.Count * 2];
            _unitMaterials = new Material[UnitKinds.Count];
            _nextMeshSlot = 0;

            // Deactivated before anything is instantiated from it: the pool
            // clones the prototype, so an active template would be a 401st unit
            // sitting at the origin for the whole match.
            root.SetActive(false);
            AddPrototype(UnitKinds.Scout, 2.6f, 2.2f, 3.4f, 1.6f, new Color(0.20f, 0.45f, 0.85f, 1f));
            AddPrototype(UnitKinds.Tank, 3.6f, 2.6f, 4.6f, 1.4f, new Color(0.42f, 0.46f, 0.34f, 1f));
            AddPrototype(UnitKinds.BaseStructure, 5.5f, 6.5f, 5.5f, 0.0f, new Color(0.72f, 0.55f, 0.22f, 1f));
        }

        private void AddPrototype(byte kind, float width, float height, float depth, float turretSize, Color color)
        {
            if (!_catalog.TryGet(kind, out var definition))
            {
                return;
            }

            var material = CreateContentMaterial($"Unit {definition.DisplayName}", color);
            _unitMaterials[kind] = material;

            var prototype = new GameObject($"Unit Prototype {definition.DisplayName}");
            prototype.transform.SetParent(_prototypeRoot.transform, false);
            var hull = prototype.AddComponent<MeshFilter>();
            hull.sharedMesh = CreateBoxMesh($"GlobalFront Hull {definition.DisplayName}", width, height, depth);
            TrackMesh(hull.sharedMesh);
            var renderer = prototype.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var view = prototype.AddComponent<UnitView>();
            if (turretSize > 0f)
            {
                var turret = new GameObject("Turret");
                turret.transform.SetParent(prototype.transform, false);
                turret.transform.localPosition = new Vector3(0f, height, 0f);
                var turretFilter = turret.AddComponent<MeshFilter>();
                turretFilter.sharedMesh = CreateBoxMesh(
                    $"GlobalFront Turret {definition.DisplayName}", turretSize, turretSize * 0.5f, turretSize);
                TrackMesh(turretFilter.sharedMesh);
                var turretRenderer = turret.AddComponent<MeshRenderer>();
                turretRenderer.sharedMaterial = material;
                turretRenderer.shadowCastingMode = ShadowCastingMode.Off;
                turretRenderer.receiveShadows = false;

                // A turret node is part of the budget, not a decoration: the tick
                // buffer writes a separate turret heading and Apply only touches a
                // rotation it has somewhere to put.
                view.AssignTurret(turret.transform);
            }

            if (!_viewPool.SetPrototype(kind, prototype))
            {
                DestroyContentObject(prototype);
            }
        }

        private void TrackMesh(Mesh mesh)
        {
            if (mesh == null || _unitMeshes == null || _nextMeshSlot >= _unitMeshes.Length)
            {
                return;
            }

            _unitMeshes[_nextMeshSlot++] = mesh;
        }

        private void TryBuildOverlayResources()
        {
            _overlayResourcesUsable = UnitOverlayGeometry.TryCreateResources(
                null,
                out _overlayMaterial,
                out _ownsOverlayMaterial,
                out _overlayRingMesh,
                out _overlayBarMesh,
                out _overlayRingPassIndex,
                out _overlayBarPassIndex);

            if (!_overlayResourcesUsable)
            {
                return;
            }

            _ringChunks = CreateOverlayChunks();
            _barChunks = CreateOverlayChunks();
            _overlayCommandBuffer = new CommandBuffer { name = "GlobalFront Tactical Overlays" };

            // A renderer feature already in the active URP asset owns these
            // batches; attaching here is the seam step 3.4 left for adoption.
            UnitOverlayRendererFeature.Active?.Attach(_binder);
        }

        private static UnitOverlayRenderPass.OverlayChunkBuffers[] CreateOverlayChunks()
        {
            var chunks = new UnitOverlayRenderPass.OverlayChunkBuffers[UnitOverlayRenderPass.MaxChunksPerBatch];
            for (var index = 0; index < chunks.Length; index++)
            {
                chunks[index] = new UnitOverlayRenderPass.OverlayChunkBuffers();
            }

            return chunks;
        }

        private void ReleaseOverlayResources()
        {
            if (_ownsOverlayMaterial && _overlayMaterial != null)
            {
                DestroyContentObject(_overlayMaterial);
            }

            _overlayMaterial = null;
            _ownsOverlayMaterial = false;
            DestroyContentObject(_overlayRingMesh);
            DestroyContentObject(_overlayBarMesh);
            _overlayRingMesh = null;
            _overlayBarMesh = null;
            _overlayRingPassIndex = -1;
            _overlayBarPassIndex = -1;
            _ringChunks = null;
            _barChunks = null;
            _overlayResourcesUsable = false;
        }

        private void ReleaseContent()
        {
            // The GameObjects first. Their renderers hold the meshes and materials
            // destroyed below, and a component left pointing at a freed native object
            // is a leak the pool cannot hand back and an error the next frame would
            // report, so teardown takes away the holders before the assets.
            DestroyContentObject(_prototypeRoot);
            _prototypeRoot = null;
            DestroyContentObject(_contentRoot);
            _contentRoot = null;
            DestroyContentObject(_ownedCameraObject);
            _ownedCameraObject = null;

            if (_unitMeshes != null)
            {
                for (var index = 0; index < _nextMeshSlot; index++)
                {
                    DestroyContentObject(_unitMeshes[index]);
                }

                _unitMeshes = null;
                _nextMeshSlot = 0;
            }

            if (_unitMaterials != null)
            {
                for (var index = 0; index < _unitMaterials.Length; index++)
                {
                    DestroyContentObject(_unitMaterials[index]);
                }

                _unitMaterials = null;
            }

            DestroyContentObject(_groundMaterial);
            _groundMaterial = null;
            DestroyContentObject(_groundMesh);
            _groundMesh = null;
        }

        private void DestroyWiredComponent(MonoBehaviour component)
        {
            if (component != null)
            {
                DestroyContentObject(component.gameObject);
            }
        }

        private Material CreateContentMaterial(string name, Color color)
        {
            // The prototype's shader choice, kept identical so the slice and the
            // legacy battlefield light the same way. A build with neither shader
            // gets no material rather than an exception in the render loop:
            // nothing here needs a texture to be correct.
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                return null;
            }

            return new Material(shader)
            {
                name = name,
                color = color,
                hideFlags = HideFlags.DontSave,
            };
        }

        private static void DestroyContentObject(UnityEngine.Object content)
        {
            if (content == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(content);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(content);
            }
#else
            UnityEngine.Object.Destroy(content);
#endif
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>
        /// A flat grid over OD-27's battlefield: one mesh, <see cref="GroundTiles"/>
        /// × <see cref="GroundTiles"/> tiles, so the 400 m square still has
        /// bounds a renderer can cull against instead of one 160 000-triangle slab.
        /// </summary>
        private static Mesh CreateGroundMesh(string name)
        {
            var halfExtent = MapHalfExtentMetres;
            var tiles = GroundTiles;
            var cells = tiles + 1;
            var step = halfExtent * 2f / tiles;
            var vertexCount = cells * cells;
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            for (var row = 0; row < cells; row++)
            {
                for (var column = 0; column < cells; column++)
                {
                    var index = row * cells + column;
                    vertices[index] = new Vector3(
                        -halfExtent + column * step,
                        0f,
                        -halfExtent + row * step);
                    normals[index] = Vector3.up;
                    uvs[index] = new Vector2(column, row);
                }
            }

            var triangles = new int[tiles * tiles * 6];
            var write = 0;
            for (var row = 0; row < tiles; row++)
            {
                for (var column = 0; column < tiles; column++)
                {
                    var corner = row * cells + column;
                    var opposite = corner + cells + 1;
                    triangles[write++] = corner;
                    triangles[write++] = opposite;
                    triangles[write++] = corner + 1;
                    triangles[write++] = corner;
                    triangles[write++] = corner + cells;
                    triangles[write++] = opposite;
                }
            }

            return new Mesh
            {
                name = name,
                hideFlags = HideFlags.DontSave,
                vertices = vertices,
                normals = normals,
                uv = uvs,
                triangles = triangles,
            };
        }

        /// <summary>
        /// A box with its base on the ground plane and flat per-face normals.
        ///
        /// Shared vertices would average the corner normals and render a hard
        /// edged hull as an inflated bag under URP/Lit, so each face carries its
        /// own four corners.
        /// </summary>
        private static Mesh CreateBoxMesh(string name, float width, float height, float depth)
        {
            var halfWidth = width * 0.5f;
            var halfDepth = depth * 0.5f;
            var corners = new[]
            {
                new Vector3(-halfWidth, 0f, -halfDepth),
                new Vector3(halfWidth, 0f, -halfDepth),
                new Vector3(halfWidth, 0f, halfDepth),
                new Vector3(-halfWidth, 0f, halfDepth),
                new Vector3(-halfWidth, height, -halfDepth),
                new Vector3(halfWidth, height, -halfDepth),
                new Vector3(halfWidth, height, halfDepth),
                new Vector3(-halfWidth, height, halfDepth),
            };

            // Four corner indices and the normal that face points along.
            var faces = new[]
            {
                new Face(new[] { 0, 3, 2, 1 }, new Vector3(0f, -1f, 0f)),
                new Face(new[] { 4, 5, 6, 7 }, Vector3.up),
                new Face(new[] { 0, 1, 5, 4 }, new Vector3(0f, 0f, -1f)),
                new Face(new[] { 2, 3, 7, 6 }, new Vector3(0f, 0f, 1f)),
                new Face(new[] { 0, 4, 7, 3 }, new Vector3(-1f, 0f, 0f)),
                new Face(new[] { 1, 2, 6, 5 }, new Vector3(1f, 0f, 0f)),
            };

            var vertexCount = faces.Length * 4;
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var triangles = new int[faces.Length * 6];
            for (var face = 0; face < faces.Length; face++)
            {
                var first = face * 4;
                for (var corner = 0; corner < 4; corner++)
                {
                    var index = first + corner;
                    vertices[index] = corners[faces[face].Corners[corner]];
                    normals[index] = faces[face].Normal;
                    uvs[index] = CornerUv[corner];
                }

                var tri = face * 6;
                triangles[tri] = first;
                triangles[tri + 1] = first + 1;
                triangles[tri + 2] = first + 2;
                triangles[tri + 3] = first;
                triangles[tri + 4] = first + 2;
                triangles[tri + 5] = first + 3;
            }

            var mesh = new Mesh
            {
                name = name,
                hideFlags = HideFlags.DontSave,
                vertices = vertices,
                normals = normals,
                uv = uvs,
                triangles = triangles,
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static readonly Vector2[] CornerUv =
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),
        };

        private readonly struct Face
        {
            public Face(int[] corners, Vector3 normal)
            {
                Corners = corners;
                Normal = normal;
            }

            public int[] Corners { get; }

            public Vector3 Normal { get; }
        }
    }
}
