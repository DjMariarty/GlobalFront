using System;
using System.Collections.Generic;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using UnityEngine;
using UnityEngine.InputSystem;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client
{
    /// <summary>
    /// Client presentation/input controller for the prototype battle.
    /// Owns no authoritative tick: since Phase 2.3 the server tick lifecycle
    /// is driven by the <see cref="TickDriver"/> inside
    /// <see cref="LocalMatchHost"/> (ADR-007). This component only pumps the
    /// host's real-time driver once per frame, forwards client commands
    /// through <see cref="ICommandChannel"/> on the host's
    /// <see cref="LocalMatchHost.TickStarting"/> event and consumes
    /// <see cref="ServerUnitSnapshot"/> arrays on
    /// <see cref="LocalMatchHost.TickCompleted"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PrototypeRtsController : MonoBehaviour
    {
        private const int MillimetresPerMetre = 1000;
        private const int FormationSpacingMm = 3200;
        private const float ClickDragThresholdPixels = 10f;

        /// <summary>
        /// Movement speed in millimetres per tick, duplicated from
        /// <see cref="PrototypeUnit"/> to avoid expanding its public API.
        /// Must be kept in sync when the prototype balance changes.
        /// </summary>
        private const int MovementPerTickMm = 350;

        /// <summary>
        /// Combat stats for the prototype match. In a production build this
        /// would come from a data-driven unit catalog; here it is the
        /// authoritative source for all units in the match.
        /// </summary>
        private static readonly CombatStats PrototypeCombatStats =
            new CombatStats(
                maximumHealth: 100,
                damage: 25,
                rangeMm: 7000,
                cooldownTicks: 10);

        private readonly UnitRegistry _registry = new UnitRegistry();
        private UnitSelection _selection;
        private PrototypeCommandQueue _commandQueue;

        /// <summary>
        /// Template marker for the locally controlled army in the prototype
        /// scene. During <see cref="InitializeLocalHost"/> it is replaced by
        /// the authoritative PlayerId assigned by the server-side session
        /// layer (Phase 2.4, ADR-008): the client never chooses its
        /// authoritative PlayerId.
        /// </summary>
        private PlayerId _localPlayer = new PlayerId(1);

        private Camera _camera;
        private Vector2 _selectionStart;
        private Vector2 _selectionCurrent;
        private bool _selectionPointerDown;
        private BattleOutcome _outcome = BattleOutcome.InProgress;

        /// <summary>
        /// Authoritative local host that owns both the
        /// <see cref="MatchServer"/> and the <see cref="TickDriver"/> that
        /// schedules its ticks (ADR-007). The client pumps the driver in
        /// <see cref="Update"/> and consumes snapshots on
        /// <see cref="LocalMatchHost.TickCompleted"/>; the client no longer
        /// starts or owns authoritative server ticks.
        /// </summary>
        private LocalMatchHost _localHost;

        /// <summary>
        /// Command channel abstraction that decouples the command queue from
        /// the concrete authoritative backend. Currently wraps the local
        /// host; will be replaced by a network channel in Phase 2.
        /// </summary>
        private ICommandChannel _commandChannel;

        /// <summary>
        /// Server-assigned identity of the local client (Phase 2.4,
        /// ADR-008). Null until <see cref="InitializeLocalHost"/> binds the
        /// local session, or forever on the empty-server path.
        /// </summary>
        private ClientSession _clientSession;

        /// <summary>
        /// Server-side placeholder session for the prototype's non-local
        /// army. Kept for diagnostics and future reconnect semantics; it
        /// issues no commands in the prototype.
        /// </summary>
        private SessionId _enemySession;

        /// <summary>The authoritative local match host, for diagnostics and tests.</summary>
        public LocalMatchHost Host => _localHost;

        /// <summary>Server-assigned local identity (Phase 2.4), for diagnostics and tests.</summary>
        public ClientSession LocalSession => _clientSession;

        /// <summary>True when the local match host has been initialized.</summary>
        public bool HostActive => _localHost != null;

        /// <summary>Number of units registered in the local match host.</summary>
        public int HostUnitCount => _localHost?.UnitCount ?? 0;

        public int SelectedCount => _selection.Count;

        public int FriendlyAlive => _registry.FriendlyAlive;

        public int EnemyAlive => _registry.EnemyAlive;

        public BattleOutcome Outcome => _outcome;

        public bool IsDragSelecting =>
            _selectionPointerDown &&
            (_selectionCurrent - _selectionStart).sqrMagnitude >=
            ClickDragThresholdPixels * ClickDragThresholdPixels;

        public string LastCommandMessage => _commandQueue.LastCommandMessage;

        public string BattleStatusMessage
        {
            get
            {
                if (_outcome.Kind == BattleOutcomeKind.Draw)
                {
                    return "DRAW";
                }

                if (_outcome.Kind == BattleOutcomeKind.Victory)
                {
                    return _outcome.Winner == _localPlayer
                        ? "VICTORY"
                        : "DEFEAT";
                }

                return "BATTLE IN PROGRESS";
            }
        }

        private void Awake()
        {
            _selection = new UnitSelection(_registry);
            _commandQueue = new PrototypeCommandQueue(
                _registry, _localPlayer, FormationSpacingMm);
        }

        private void OnEnable()
        {
            if (_localHost != null)
            {
                _localHost.TickStarting += OnHostTickStarting;
                _localHost.TickCompleted += OnHostTickCompleted;
            }
        }

        private void Start()
        {
#if !UNITY_SERVER
            _camera = Camera.main != null
                ? Camera.main
                : FindAnyObjectByType<Camera>();
            InitializeLocalHost();
            UpdateRosterCounts();
#endif
        }

        private void OnDisable()
        {
            if (_localHost != null)
            {
                _localHost.TickStarting -= OnHostTickStarting;
                _localHost.TickCompleted -= OnHostTickCompleted;
            }
        }

        private void Update()
        {
#if !UNITY_SERVER
            ReadSelectionInput();
            ReadCommandInput();
            PumpServerTicks();
#endif
        }

        private void LateUpdate()
        {
#if !UNITY_SERVER
            RefreshAttackHighlights();
            var alpha = (float)((_localHost?.TickBacklogSeconds ?? 0.0) /
                        SimulationConstants.ServerTickDurationSeconds);
            for (var index = 0; index < _registry.Count; index++)
            {
                _registry[index].Render(Mathf.Clamp01(alpha));
            }
#endif
        }

        private void OnGUI()
        {
#if !UNITY_SERVER
            DrawHealthBars();

            if (!IsDragSelecting)
            {
                return;
            }

            var rect = GetGuiSelectionRect(_selectionStart, _selectionCurrent);
            var previousColor = GUI.color;
            GUI.color = new Color(0.25f, 0.80f, 1f, 0.55f);
            GUI.Box(rect, GUIContent.none);
            GUI.color = previousColor;
#endif
        }

        private void ReadSelectionInput()
        {
            var mouse = Mouse.current;
            if (mouse == null || _camera == null)
            {
                return;
            }

            var pointer = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame && !IsPointerOverHud(pointer))
            {
                _selectionPointerDown = true;
                _selectionStart = pointer;
                _selectionCurrent = pointer;
            }

            if (_selectionPointerDown && mouse.leftButton.isPressed)
            {
                _selectionCurrent = pointer;
            }

            if (!_selectionPointerDown || !mouse.leftButton.wasReleasedThisFrame)
            {
                return;
            }

            _selectionCurrent = pointer;
            if (IsPointerOverHud(pointer))
            {
                _selectionPointerDown = false;
                return;
            }

            var additive = IsShiftPressed();
            if (IsDragSelecting)
            {
                _selection.SelectInsideScreenRect(
                    _camera, _selectionStart, _selectionCurrent, additive, _localPlayer);
            }
            else
            {
                _selection.SelectUnderPointer(_camera, pointer, additive, _localPlayer);
            }

            _selectionPointerDown = false;
        }

        private void ReadCommandInput()
        {
            var mouse = Mouse.current;
            if (mouse == null || _camera == null ||
                _selection.Count == 0 ||
                _outcome.IsTerminal ||
                !mouse.rightButton.wasPressedThisFrame)
            {
                return;
            }

            var pointer = mouse.position.ReadValue();
            if (IsPointerOverHud(pointer))
            {
                return;
            }

            var ray = _camera.ScreenPointToRay(pointer);
            if (Physics.Raycast(
                    ray,
                    out var hit,
                    600f,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                var hitUnit = hit.collider.GetComponentInParent<PrototypeUnit>();
                if (hitUnit != null &&
                    hitUnit.IsAlive &&
                    hitUnit.Owner != _localPlayer)
                {
                    QueueAttackCommand(hitUnit.Entity);
                    return;
                }
            }

            var groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (!groundPlane.Raycast(ray, out var distance))
            {
                return;
            }

            QueueMoveCommandToWorld(PointMmFromWorld(ray.GetPoint(distance)));
        }

        private void QueueMoveCommandToWorld(WorldPointMm destination)
        {
            _commandQueue.QueueMove(
                destination,
                _selection.GetSelectedEntityIds(),
                NextServerTick());
        }

        private void QueueAttackCommand(CoreEntityId target)
        {
            _commandQueue.QueueAttack(
                target,
                _selection.GetSelectedEntityIds(),
                NextServerTick());
        }

        /// <summary>
        /// The next authoritative tick a newly issued command can target.
        /// The server still applies this tick, so tick N+1 is the next tick
        /// in which the command can take effect.
        /// </summary>
        private ulong NextServerTick() =>
            (_localHost != null ? _localHost.CurrentTick : 0ul) + 1ul;

        /// <summary>
        /// Feeds frame time into the host's <see cref="TickDriver"/>. The
        /// driver schedules the authoritative ticks (20 Hz, bounded
        /// catch-up); the host runs the simulation for each scheduled tick.
        /// The client presentation never starts ticks itself.
        /// </summary>
        private void PumpServerTicks()
        {
            if (_localHost != null &&
                _localHost.TickDriverMode == TickDriverMode.RealTime)
            {
                _localHost.AdvanceRealTime(Time.unscaledDeltaTime);
            }
        }

        /// <summary>
        /// Host phase 1: the server is still at tick N-1. Forwards every
        /// client command requested for tick N so it is applied during tick
        /// N, preserving the pre-Phase 2.3 command timing contract.
        /// </summary>
        private void OnHostTickStarting(ulong tick)
        {
            if (_outcome.IsTerminal ||
                _localHost.UnitCount == 0 ||
                _registry.Count == 0)
            {
                return;
            }

            _commandQueue.ForwardPending(tick, _commandChannel);
        }

        /// <summary>
        /// Host phase 3: the server has fully simulated tick N. Synchronizes
        /// every presentation unit from the authoritative snapshots.
        /// </summary>
        private void OnHostTickCompleted(ulong tick)
        {
            if (_localHost.UnitCount == 0 || _registry.Count == 0)
            {
                return;
            }

            ApplyHostSnapshots();

            for (var index = 0; index < _registry.Count; index++)
            {
                _registry[index].SynchronizeCombatPresentation();
            }

            _selection.PruneDead();
            UpdateRosterCounts();
            _outcome = _localHost.Outcome;

            if (_outcome.IsTerminal)
            {
                _commandQueue.Clear();
                for (var index = 0; index < _registry.Count; index++)
                {
                    var unit = _registry[index];
                    unit.ClearAttackTarget();
                    unit.SetAutoAcquireEnemies(false);
                    unit.StopMovement();
                    unit.SnapPresentation();
                }

                _commandQueue.OverrideMessage(BattleStatusMessage);
            }
        }

        /// <summary>
        /// Synchronizes every client presentation unit from the authoritative
        /// server snapshots produced by the last authoritative tick.
        /// </summary>
        private void ApplyHostSnapshots()
        {
            var snapshots = _localHost.GetAllSnapshots();
            for (var index = 0; index < snapshots.Length; index++)
            {
                var snapshot = snapshots[index];
                if (_registry.TryGetUnit(snapshot.Entity, out var unit))
                {
                    unit.ApplyServerSnapshot(snapshot);
                }
            }
        }

        private void InitializeLocalHost()
        {
            _localHost = new LocalMatchHost();
            _localHost.TickStarting += OnHostTickStarting;
            _localHost.TickCompleted += OnHostTickCompleted;

            // 1. Discover presentation units without EntityIds
            var presentationUnits = _registry.DiscoverUnassignedUnits();
            if (presentationUnits.Length == 0)
            {
                // No armies in this scene: the server tick lifecycle still
                // runs independently of match initialization (ADR-007), so
                // the driver starts pacing an empty authoritative server.
                _localHost.StartRealTime();
                return;
            }

            // 2. Phase 2.4 (ADR-008): identity is server-assigned. The
            //    prototype template declares armies with scene-local owners;
            //    the host's session layer assigns authoritative PlayerIds
            //    and maps the template onto them deterministically. The
            //    local army's session joins first, so the monotonic server
            //    assignment deterministically gives it the lowest PlayerId.
            var templateOwners = CollectSortedOwners(presentationUnits);
            var match = _localHost.CreateSessionMatch(templateOwners.Length);

            var localTemplateOwner = _localPlayer;
            var ownerToAssigned = new Dictionary<PlayerId, PlayerId>();
            var localSession = default(SessionId);
            for (var index = 0; index < templateOwners.Length; index++)
            {
                var session = _localHost.CreateSession(_localHost.CreateConnectionHandle());
                var join = _localHost.TryJoinMatch(session, match, out var assigned);
                if (join != JoinResult.Assigned)
                {
                    throw new InvalidOperationException(
                        "Prototype session join failed: " + join);
                }

                ownerToAssigned[templateOwners[index]] = assigned;
                if (templateOwners[index] == localTemplateOwner)
                {
                    localSession = session;
                }
                else
                {
                    _enemySession = session;
                }
            }

            // 3. Build the host-supplied MatchConfig template (OD-6) from
            //    presentation unit data; owners are still template values.
            var specs = new UnitSpawnSpec[presentationUnits.Length];
            for (var index = 0; index < presentationUnits.Length; index++)
            {
                var unit = presentationUnits[index];
                specs[index] = new UnitSpawnSpec(
                    unit.Owner,
                    unit.CurrentPosition,
                    MovementPerTickMm,
                    unit.AutoAcquireEnemies);
            }

            var config = new MatchConfig(PrototypeCombatStats, specs);

            // 4. Session-aware start: the host maps the assigned PlayerIds
            //    onto the template and initializes the authoritative server,
            //    which assigns EntityIds.
            if (!_localHost.TryStartSessionMatch(match, config, out var serverEntityIds))
            {
                throw new InvalidOperationException(
                    "Prototype session match failed to start.");
            }

            // 5. Adopt the server-assigned identity and bind the channel to
            //    the local session; the command queue follows suit.
            _localPlayer = ownerToAssigned[localTemplateOwner];
            _clientSession = new ClientSession(localSession, match, _localPlayer);
            _commandChannel = new LocalCommandChannel(_localHost, localSession);
            _commandQueue = new PrototypeCommandQueue(
                _registry, _localPlayer, FormationSpacingMm);

            // 6. Remap presentation owners to the authoritative PlayerIds,
            //    then assign the server EntityIds.
            for (var index = 0; index < presentationUnits.Length; index++)
            {
                var unit = presentationUnits[index];
                unit.AssignAuthoritativeEntity(serverEntityIds[index]);
                unit.RemapAuthoritativePlayer(ownerToAssigned[unit.Owner]);
            }

            // 7. Refresh registry now that units have EntityIds
            _registry.Refresh(_localPlayer);

            // 8. The server tick driver now owns the authoritative loop.
            _localHost.StartRealTime();
        }

        /// <summary>
        /// Distinct template owners of the presentation units in ascending
        /// PlayerId order. This order defines the deterministic join order
        /// for the session-aware match start (Phase 2.4, ADR-008).
        /// </summary>
        private static PlayerId[] CollectSortedOwners(PrototypeUnit[] units)
        {
            var owners = new List<PlayerId>();
            for (var index = 0; index < units.Length; index++)
            {
                var owner = units[index].Owner;
                if (!owner.IsValid || owners.Contains(owner))
                {
                    continue;
                }

                owners.Add(owner);
            }

            owners.Sort((left, right) => left.Value.CompareTo(right.Value));
            return owners.ToArray();
        }

        private void RefreshAttackHighlights()
        {
            for (var index = 0; index < _registry.Count; index++)
            {
                _registry[index].SetAttackHighlighted(false);
            }

            var selectedEntities = _selection.GetSelectedEntityIds();
            for (var index = 0; index < selectedEntities.Length; index++)
            {
                if (!_registry.TryGetUnit(selectedEntities[index], out var selected) ||
                    !selected.IsAlive ||
                    !selected.AttackTarget.IsValid ||
                    !_registry.TryGetUnit(selected.AttackTarget, out var target) ||
                    !target.IsAlive)
                {
                    continue;
                }

                target.SetAttackHighlighted(true);
            }
        }

        private void UpdateRosterCounts() => _registry.UpdateRosterCounts(_localPlayer);

        private void DrawHealthBars()
        {
            if (_camera == null)
            {
                return;
            }

            var previousColor = GUI.color;
            for (var index = 0; index < _registry.Count; index++)
            {
                var unit = _registry[index];
                if (!unit.IsAlive)
                {
                    continue;
                }

                var screenPoint = _camera.WorldToScreenPoint(
                    unit.transform.position + Vector3.up * 1.8f);
                if (screenPoint.z <= 0f)
                {
                    continue;
                }

                var rect = new Rect(
                    screenPoint.x - 18f,
                    Screen.height - screenPoint.y,
                    36f,
                    5f);
                GUI.color = Color.black;
                GUI.DrawTexture(rect, Texture2D.whiteTexture);

                var healthFraction =
                    unit.CurrentHealth / (float)unit.MaximumHealth;
                var fill = new Rect(
                    rect.x + 1f,
                    rect.y + 1f,
                    (rect.width - 2f) * healthFraction,
                    rect.height - 2f);
                GUI.color = Color.Lerp(
                    new Color(0.90f, 0.12f, 0.08f),
                    new Color(0.15f, 0.90f, 0.25f),
                    healthFraction);
                GUI.DrawTexture(fill, Texture2D.whiteTexture);
            }

            GUI.color = previousColor;
        }

        private static WorldPointMm PointMmFromWorld(Vector3 position) =>
            new WorldPointMm(
                Mathf.RoundToInt(position.x * MillimetresPerMetre),
                Mathf.RoundToInt(position.z * MillimetresPerMetre));

        private static bool IsShiftPressed()
        {
            var keyboard = Keyboard.current;
            return keyboard != null &&
                   (keyboard.leftShiftKey.isPressed ||
                    keyboard.rightShiftKey.isPressed);
        }

        private static bool IsPointerOverHud(Vector2 pointer) =>
            pointer.x <= 480f && pointer.y >= Screen.height - 205f;

        private static Rect GetGuiSelectionRect(Vector2 start, Vector2 end)
        {
            var startGui = new Vector2(start.x, Screen.height - start.y);
            var endGui = new Vector2(end.x, Screen.height - end.y);
            var minimum = Vector2.Min(startGui, endGui);
            var maximum = Vector2.Max(startGui, endGui);
            return Rect.MinMaxRect(
                minimum.x,
                minimum.y,
                maximum.x,
                maximum.y);
        }
    }
}
