using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client
{
    [DisallowMultipleComponent]
    public sealed class PrototypeUnit : MonoBehaviour
    {
        private const int MillimetresPerMetre = 1000;
        private const int MovementPerTickMm = 350;

        private static readonly CombatStats PrototypeCombatStats =
            new CombatStats(
                maximumHealth: 100,
                damage: 25,
                rangeMm: 7000,
                cooldownTicks: 10);

        private WorldPointMm _previousPosition;
        private WorldPointMm _currentPosition;
        private WorldPointMm _targetPosition;
        private bool _hasTarget;
        private float _presentationHeight;
        private GameObject _selectionIndicator;
        private GameObject _attackIndicator;
        private Renderer _bodyRenderer;
        private Collider _bodyCollider;
        private CombatantState _combatState;
        private bool _deathPresented;

        public CoreEntityId Entity { get; private set; }

        public PlayerId Owner { get; private set; }

        public CombatantState CombatState => _combatState;

        public WorldPointMm CurrentPosition => _currentPosition;

        public bool IsSelected { get; private set; }

        public bool HasTarget => _hasTarget;

        public bool IsAlive => _combatState != null && _combatState.IsAlive;

        public int CurrentHealth => _combatState?.CurrentHealth ?? 0;

        public int MaximumHealth => _combatState?.Stats.MaximumHealth ?? 0;

        public CoreEntityId AttackTarget =>
            _combatState?.AttackTarget ?? default;

        public bool AutoAcquireEnemies { get; private set; }

        /// <summary>
        /// Creates the presentation unit without an authoritative EntityId.
        /// The EntityId is assigned later by
        /// <see cref="AssignAuthoritativeEntity"/> after the server
        /// initializes the match and returns server-assigned ids.
        /// </summary>
        public void Initialize(PlayerId owner)
        {
            Owner = owner;
            _presentationHeight = transform.position.y;
            _currentPosition = FromUnityPosition(transform.position);
            _previousPosition = _currentPosition;
            _targetPosition = _currentPosition;
            _bodyRenderer = GetComponent<Renderer>();
            _bodyCollider = GetComponent<Collider>();
            CreateSelectionIndicator();
            CreateAttackIndicator();
        }

        /// <summary>
        /// Assigns the server-authoritative EntityId to this presentation
        /// unit and creates its combat state. Called by the controller after
        /// <see cref="LocalMatchHost.InitializeMatch"/> returns the
        /// server-assigned EntityIds. The client never determines
        /// authoritative EntityIds.
        /// </summary>
        public void AssignAuthoritativeEntity(CoreEntityId entity)
        {
            Entity = entity;
            _combatState = new CombatantState(entity, Owner, PrototypeCombatStats);
        }

        /// <summary>
        /// Remaps the presentation owner to the authoritative PlayerId
        /// assigned by the server (Phase 2.4, ADR-008). The template owner
        /// comes from the prototype scene; the authoritative owner is the
        /// server-side session assignment. Must run after
        /// <see cref="AssignAuthoritativeEntity"/> and before gameplay
        /// starts.
        /// </summary>
        public void RemapAuthoritativePlayer(PlayerId player)
        {
            if (Owner == player)
            {
                return;
            }

            Owner = player;
            _combatState = new CombatantState(Entity, Owner, PrototypeCombatStats);
        }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            if (_selectionIndicator != null)
            {
                _selectionIndicator.SetActive(selected);
            }
        }

        public void SetAttackHighlighted(bool highlighted)
        {
            if (_attackIndicator != null)
            {
                _attackIndicator.SetActive(highlighted && IsAlive);
            }
        }

        public void SetMoveTarget(WorldPointMm target)
        {
            _combatState.ClearTarget();
            AutoAcquireEnemies = false;
            _targetPosition = target;
            _hasTarget = _currentPosition != target;
        }

        public bool SetAttackTarget(CoreEntityId target)
        {
            if (!_combatState.TryAssignTarget(target))
            {
                return false;
            }

            AutoAcquireEnemies = true;
            StopMovement();
            return true;
        }

        public void SetAutoAcquireEnemies(bool enabled)
        {
            AutoAcquireEnemies = enabled;
        }

        public void ClearAttackTarget()
        {
            _combatState.ClearTarget();
        }

        /// <summary>
        /// Synchronizes this presentation unit from an authoritative server
        /// snapshot. Called by <see cref="PrototypeRtsController"/> after each
        /// <see cref="LocalMatchHost.TickOnce"/>. The previous position is
        /// preserved so <see cref="Render"/> can interpolate smoothly between
        /// the old and new server states.
        /// </summary>
        public void ApplyServerSnapshot(ServerUnitSnapshot snapshot)
        {
            _previousPosition = _currentPosition;
            _currentPosition = snapshot.Position;

            if (snapshot.HasMoveTarget)
            {
                _targetPosition = snapshot.MoveTarget;
                _hasTarget = _currentPosition != _targetPosition;
            }
            else
            {
                _targetPosition = _currentPosition;
                _hasTarget = false;
            }

            _combatState.SynchronizeFromAuthoritative(
                snapshot.CurrentHealth,
                snapshot.AttackTarget);

            AutoAcquireEnemies = snapshot.AutoAcquireEnemies;
        }

        public void StopMovement()
        {
            _targetPosition = _currentPosition;
            _hasTarget = false;
        }

        public void AdvanceTowardsAttackTarget(WorldPointMm target)
        {
            _targetPosition = target;
            _hasTarget = _currentPosition != target;
            AdvanceOneTick();
        }

        public void AdvanceOneTick()
        {
            _previousPosition = _currentPosition;
            if (!IsAlive || !_hasTarget)
            {
                return;
            }

            _currentPosition = PlanarMovement.StepTowards(
                _currentPosition,
                _targetPosition,
                MovementPerTickMm);
            _hasTarget = _currentPosition != _targetPosition;
        }

        public void Render(float interpolationAlpha)
        {
            if (!IsAlive)
            {
                return;
            }

            var previous = ToUnityPosition(_previousPosition, _presentationHeight);
            var current = ToUnityPosition(_currentPosition, _presentationHeight);
            transform.position = Vector3.Lerp(previous, current, interpolationAlpha);
        }

        public void SnapPresentation()
        {
            _previousPosition = _currentPosition;
            if (IsAlive)
            {
                transform.position = ToUnityPosition(
                    _currentPosition,
                    _presentationHeight);
            }
        }

        public void SynchronizeCombatPresentation()
        {
            if (IsAlive || _deathPresented)
            {
                return;
            }

            _deathPresented = true;
            SetSelected(false);
            StopMovement();
            if (_selectionIndicator != null)
            {
                _selectionIndicator.SetActive(false);
            }

            if (_attackIndicator != null)
            {
                _attackIndicator.SetActive(false);
            }

            if (_bodyRenderer != null)
            {
                _bodyRenderer.enabled = false;
            }

            if (_bodyCollider != null)
            {
                _bodyCollider.enabled = false;
            }
        }

        private void CreateSelectionIndicator()
        {
#if !UNITY_SERVER
            var indicator = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            indicator.name = "Selection Indicator";
            indicator.transform.SetParent(transform, false);
            indicator.transform.localPosition = new Vector3(0f, -0.95f, 0f);
            indicator.transform.localScale = new Vector3(1.25f, 0.025f, 1.25f);

            var indicatorCollider = indicator.GetComponent<Collider>();
            if (indicatorCollider != null)
            {
                indicatorCollider.enabled = false;
                DestroyImmediate(indicatorCollider);
            }

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                         Shader.Find("Unlit/Color") ??
                         Shader.Find("Standard");
            if (shader != null)
            {
                var material = new Material(shader)
                {
                    color = new Color(0.20f, 1f, 0.38f, 1f)
                };
                indicator.GetComponent<Renderer>().sharedMaterial = material;
            }

            _selectionIndicator = indicator;
            _selectionIndicator.SetActive(false);
#endif
        }

        private void CreateAttackIndicator()
        {
#if !UNITY_SERVER
            var indicator = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            indicator.name = "Attack Target Indicator";
            indicator.transform.SetParent(transform, false);
            indicator.transform.localPosition = new Vector3(0f, -0.91f, 0f);
            indicator.transform.localScale = new Vector3(1.55f, 0.018f, 1.55f);

            var indicatorCollider = indicator.GetComponent<Collider>();
            if (indicatorCollider != null)
            {
                indicatorCollider.enabled = false;
                DestroyImmediate(indicatorCollider);
            }

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                         Shader.Find("Unlit/Color") ??
                         Shader.Find("Standard");
            if (shader != null)
            {
                var material = new Material(shader)
                {
                    color = new Color(1f, 0.48f, 0.08f, 1f)
                };
                indicator.GetComponent<Renderer>().sharedMaterial = material;
            }

            _attackIndicator = indicator;
            _attackIndicator.SetActive(false);
#endif
        }

        private static WorldPointMm FromUnityPosition(Vector3 position) =>
            new WorldPointMm(
                Mathf.RoundToInt(position.x * MillimetresPerMetre),
                Mathf.RoundToInt(position.z * MillimetresPerMetre));

        private static Vector3 ToUnityPosition(WorldPointMm position, float height) =>
            new Vector3(
                position.X / (float)MillimetresPerMetre,
                height,
                position.Z / (float)MillimetresPerMetre);
    }
}