using System;
using System.Collections.Generic;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using UnityEngine;
using UnityEngine.InputSystem;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FixedSimulationRunner))]
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
        private const int ShadowMovementPerTickMm = 350;

        private readonly UnitRegistry _registry = new UnitRegistry();
        private UnitSelection _selection;
        private PrototypeCommandQueue _commandQueue;
        private readonly Dictionary<CoreEntityId, WorldPointMm> _tickStartPositions =
            new Dictionary<CoreEntityId, WorldPointMm>();

        private readonly PlayerId _localPlayer = new PlayerId(1);
        private FixedSimulationRunner _runner;
        private Camera _camera;
        private Vector2 _selectionStart;
        private Vector2 _selectionCurrent;
        private bool _selectionPointerDown;
        private BattleOutcome _outcome = BattleOutcome.InProgress;
        private bool _battleInitialized;

        /// <summary>
        /// Shadow authoritative server created at startup. On this step the
        /// server only exists and holds registered units — it does not
        /// participate in the simulation tick. Command forwarding and
        /// parity validation belong to a later integration step.
        /// </summary>
        private MatchServer _shadowServer;

        /// <summary>True when the shadow server has been initialized.</summary>
        public bool ShadowServerActive => _shadowServer != null;

        /// <summary>Number of units registered in the shadow server.</summary>
        public int ShadowServerUnitCount => _shadowServer?.UnitCount ?? 0;

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
            _runner = GetComponent<FixedSimulationRunner>();
            _selection = new UnitSelection(_registry);
            _commandQueue = new PrototypeCommandQueue(
                _registry, _localPlayer, FormationSpacingMm);
        }

        private void OnEnable()
        {
            if (_runner == null)
            {
                _runner = GetComponent<FixedSimulationRunner>();
            }

            _runner.TickExecuted += OnTickExecuted;
        }

        private void Start()
        {
#if !UNITY_SERVER
            _camera = Camera.main != null
                ? Camera.main
                : FindAnyObjectByType<Camera>();
            RefreshUnits();
            UpdateRosterCounts();
            _battleInitialized = FriendlyAlive > 0 && EnemyAlive > 0;
            InitializeShadowServer();
#endif
        }

        private void OnDisable()
        {
            if (_runner != null)
            {
                _runner.TickExecuted -= OnTickExecuted;
            }
        }

        private void Update()
        {
#if !UNITY_SERVER
            ReadSelectionInput();
            ReadCommandInput();
#endif
        }

        private void LateUpdate()
        {
#if !UNITY_SERVER
            RefreshAttackHighlights();
            var alpha = _runner.InterpolationAlpha;
            for (var index = 0; index < _registry.Count; index++)
            {
                _registry[index].Render(alpha);
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

        private void RefreshUnits() => _registry.Refresh(_localPlayer);

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

            QueueMoveCommand(ToWorldPointMm(ray.GetPoint(distance)));
        }

        private void QueueMoveCommand(WorldPointMm destination)
        {
            _commandQueue.QueueMove(
                destination,
                _selection.GetSelectedEntityIds(),
                _runner.Tick + 1);
        }

        private void QueueAttackCommand(CoreEntityId target)
        {
            _commandQueue.QueueAttack(
                target,
                _selection.GetSelectedEntityIds(),
                _runner.Tick + 1);
        }

        private void OnTickExecuted(ulong tick)
        {
            if (_outcome.IsTerminal || !_battleInitialized || _registry.Count == 0)
            {
                return;
            }

            _commandQueue.ApplyPending(tick);
            ClearInvalidAttackTargets();
            AcquireAutomaticTargets();
            CaptureTickStartPositions();
            AdvanceMovementPhase();

            var combatInputs = new CombatantTickInput[_registry.Count];
            for (var index = 0; index < _registry.Count; index++)
            {
                var unit = _registry[index];
                combatInputs[index] = new CombatantTickInput(
                    unit.CombatState,
                    unit.CurrentPosition);
            }

            var combatResult = CombatTickResolver.Resolve(combatInputs, tick);
            if (combatResult.EventCount > 0)
            {
                _commandQueue.OverrideMessage(
                    $"{combatResult.EventCount} attacks resolved at tick {tick}");
            }

            for (var index = 0; index < _registry.Count; index++)
            {
                _registry[index].SynchronizeCombatPresentation();
            }

            _selection.PruneDead();
            UpdateRosterCounts();
            _outcome = combatResult.Outcome;

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
        /// Creates the shadow authoritative server and registers every
        /// discovered client unit with its scene entity id so both sides
        /// share the same id space. Must be called after
        /// <see cref="RefreshUnits"/>. The server is not ticked on this
        /// step — it only validates that unit registration is possible.
        /// </summary>
        private void InitializeShadowServer()
        {
            _shadowServer = new MatchServer();

            for (var index = 0; index < _registry.Count; index++)
            {
                var unit = _registry[index];
                _shadowServer.SpawnUnitWithEntity(
                    unit.Entity,
                    unit.Owner,
                    unit.CombatState.Stats,
                    unit.CurrentPosition,
                    ShadowMovementPerTickMm,
                    unit.AutoAcquireEnemies);
            }
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

        private void ClearInvalidAttackTargets()
        {
            for (var index = 0; index < _registry.Count; index++)
            {
                var unit = _registry[index];
                if (!unit.IsAlive || !unit.AttackTarget.IsValid)
                {
                    continue;
                }

                if (!_registry.TryGetUnit(unit.AttackTarget, out var target) ||
                    !target.IsAlive ||
                    target.Owner == unit.Owner)
                {
                    unit.ClearAttackTarget();
                }
            }
        }

        private void AcquireAutomaticTargets()
        {
            var maximumDistance =
                (ulong)SimulationConstants.AutoAcquireRangeMm *
                SimulationConstants.AutoAcquireRangeMm;

            for (var unitIndex = 0; unitIndex < _registry.Count; unitIndex++)
            {
                var unit = _registry[unitIndex];
                if (!unit.IsAlive ||
                    unit.AttackTarget.IsValid ||
                    !unit.AutoAcquireEnemies)
                {
                    continue;
                }

                PrototypeUnit bestTarget = null;
                var bestDistance = ulong.MaxValue;
                for (var targetIndex = 0; targetIndex < _registry.Count; targetIndex++)
                {
                    var candidate = _registry[targetIndex];
                    if (!candidate.IsAlive || candidate.Owner == unit.Owner)
                    {
                        continue;
                    }

                    var distance = CombatMath.SquaredDistance(
                        unit.CurrentPosition,
                        candidate.CurrentPosition);
                    if (distance > maximumDistance || distance >= bestDistance)
                    {
                        continue;
                    }

                    bestDistance = distance;
                    bestTarget = candidate;
                }

                if (bestTarget != null)
                {
                    unit.SetAttackTarget(bestTarget.Entity);
                }
            }
        }

        private void CaptureTickStartPositions()
        {
            _tickStartPositions.Clear();
            for (var index = 0; index < _registry.Count; index++)
            {
                var unit = _registry[index];
                _tickStartPositions[unit.Entity] = unit.CurrentPosition;
            }
        }

        private void AdvanceMovementPhase()
        {
            for (var index = 0; index < _registry.Count; index++)
            {
                var unit = _registry[index];
                if (!unit.IsAlive)
                {
                    continue;
                }

                if (unit.AttackTarget.IsValid &&
                    _registry.TryGetUnit(unit.AttackTarget, out var target) &&
                    target.IsAlive &&
                    _tickStartPositions.TryGetValue(target.Entity, out var targetPosition))
                {
                    if (CombatMath.IsWithinRange(
                            unit.CurrentPosition,
                            targetPosition,
                            unit.CombatState.Stats.RangeMm))
                    {
                        unit.StopMovement();
                        unit.AdvanceOneTick();
                    }
                    else
                    {
                        unit.AdvanceTowardsAttackTarget(targetPosition);
                    }
                }
                else
                {
                    unit.AdvanceOneTick();
                }
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


        private static WorldPointMm ToWorldPointMm(Vector3 position) =>
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