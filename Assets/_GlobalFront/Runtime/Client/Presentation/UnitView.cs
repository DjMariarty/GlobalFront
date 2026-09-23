using GlobalFront.Core.Model;
using UnityEngine;
using EntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// Physical presentation of one replicated unit (Phase 3, step 3.3, OD-26):
    /// a hull <see cref="Transform"/> with an optional turret child, driven only by
    /// poses produced by <see cref="UnitViewTickBuffer.TrySample"/>.
    ///
    /// The view holds no simulation state and makes no decisions: authority owns
    /// where a unit is, the tick buffer owns when the view is allowed to show it,
    /// and this type only converts a millimetre pose into a Unity transform write.
    /// That is also why there is no <c>Animator</c> here (OD-26 bans Mecanim for
    /// crowd units — track movement is a hull material UV scroll) and why no
    /// silhouette is created in code: meshes arrive as prefab prototypes handed to
    /// <see cref="UnitViewPool.SetPrototype"/> with the roster content, while the
    /// rings and health bars are instanced overlays drawn per frame from the
    /// binder's slot table (OD-25), not children of this object.
    ///
    /// <see cref="Apply"/> is the per-frame hot path for every visible unit, so it
    /// allocates nothing: two struct writes, no <c>foreach</c>, no LINQ, no
    /// <c>Quaternion.Inverse</c> chains, and no division.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitView : MonoBehaviour
    {
        /// <summary>
        /// Millimetre to metre conversion of the presentation axis. The wire is
        /// integer millimetres (<c>WorldPointMm</c>) and Unity is metres, so
        /// the multiply happens exactly once, here, and the value is a compile-time
        /// constant rather than a division by 1000 (a reciprocal multiply is what
        /// keeps the hot path out of the float divider).
        /// </summary>
        public const float MillimetresToMetres = 0.001f;

        /// <summary>
        /// Turret pivot, assigned by the prefab or by
        /// <see cref="AssignTurret"/>. Left empty for an archetype without a
        /// turret, which is the case <see cref="Apply"/> has to tolerate.
        /// </summary>
        [SerializeField] private Transform turret;

        private Transform _hull;
        private GameObject _viewObject;
        private ulong _entity;
        private byte _kind;
        private bool _isPooledView;

        /// <summary>Hull transform the pose is written to; the GameObject's own.</summary>
        public Transform Hull => _hull != null ? _hull : (_hull = transform);

        /// <summary>
        /// Cached GameObject, so the pool never re-marshals the component
        /// reference while activating and deactivating views.
        /// </summary>
        public GameObject ViewObject => _viewObject != null ? _viewObject : (_viewObject = gameObject);

        /// <summary>Turret pivot, or null when the archetype has none.</summary>
        public Transform Turret => turret;

        /// <summary>False for a hull-only archetype; <see cref="Apply"/> then skips turret yaw.</summary>
        public bool HasTurret => turret != null;

        /// <summary>
        /// Authoritative identity this view currently presents, in the raw form the
        /// binder compares against the replicated slot every frame. Copying the
        /// <see cref="EntityId"/> struct instead would put the same eight bytes on
        /// the stack twice per unit per frame for no benefit.
        /// </summary>
        public ulong EntityValue => _entity;

        /// <summary>
        /// Archetype this instance was created for (<see cref="UnitKinds"/>). Set
        /// once by <see cref="UnitViewPool"/> and never rewritten: the pool routes a
        /// release back to the free list of this value, so a view whose kind could
        /// change would hand a hull prototype to the wrong archetype.
        /// </summary>
        public byte UnitKind => _kind;

        /// <summary>
        /// True once <see cref="UnitViewPool"/> has built this instance. A view the
        /// pool never created (a hand-placed prototype in a scene, a test fixture)
        /// cannot be returned to it, and <see cref="UnitViewPool.Release"/> says so
        /// instead of pushing a foreign object onto a free list.
        /// </summary>
        public bool IsPooledView => _isPooledView;

        /// <summary>
        /// True between <see cref="Bind"/> and <see cref="Release"/>: a view sitting
        /// in the pool's free list is never bound, so an overlay pass can tell a
        /// presented unit from an idle instance.
        /// </summary>
        public bool IsBound => _entity != 0;

        /// <summary>Authoritative hit points of the last applied pose.</summary>
        public int Health { get; private set; }

        /// <summary>
        /// <c>health / maxHealth</c> of the last applied pose, already clamped and
        /// already a multiply inside the buffer. Overlay presentation (OD-25) reads
        /// this instead of dividing again per unit per frame.
        /// </summary>
        public float HealthFraction { get; private set; }

        /// <summary>
        /// Takes ownership of this view for one replicated entity. Called by
        /// <see cref="UnitViewBinder"/> immediately after
        /// <see cref="UnitViewPool.Acquire"/>, before any pose is applied.
        /// </summary>
        public void Bind(EntityId entity)
        {
            _entity = entity.Value;
        }

        /// <summary>
        /// Drops the identity and the health readout, called by the pool on release.
        /// The transform is deliberately left where it is: the GameObject is
        /// deactivated at the same moment, and resetting a transform nobody reads
        /// would put two more native writes on the despawn path.
        /// </summary>
        public void Release()
        {
            _entity = 0;
            Health = 0;
            HealthFraction = 0f;
        }

        /// <summary>
        /// Stamps the immutable archetype of this instance. Pool-internal: called
        /// exactly once per created view.
        /// </summary>
        public void AssignKind(byte kind)
        {
            _kind = kind;
        }

        /// <summary>
        /// Marks the view as pool-owned. Pool-internal, called at creation next to
        /// <see cref="AssignKind"/>.
        /// </summary>
        public void MarkPooledView()
        {
            _isPooledView = true;
        }

        /// <summary>
        /// Assigns the turret pivot. Used by the pool's placeholder hierarchy and
        /// available to any content author that wires the reference up in code
        /// rather than in the prefab.
        /// </summary>
        public void AssignTurret(Transform turretPivot)
        {
            turret = turretPivot;
        }

        /// <summary>
        /// Places the hull at an authoritative millimetre position without waiting
        /// for the interpolation ring to produce a pose. The binder calls this once,
        /// when a view is bound: a freshly spawned unit would otherwise render at
        /// the world origin for as many frames as the play-out delay is long, which
        /// reads as a unit materialising out of the middle of the map.
        /// </summary>
        public void SnapToMillimetres(int xMillimetres, int zMillimetres)
        {
            Hull.position = new Vector3(
                xMillimetres * MillimetresToMetres,
                0f,
                zMillimetres * MillimetresToMetres);
        }

        /// <summary>
        /// Writes one interpolated pose onto the transforms.
        ///
        /// Hull yaw and turret yaw are both absolute world headings published by
        /// <see cref="UnitViewTickBuffer"/>, so the turret takes a world rotation
        /// rather than a local one: adding the hull yaw a second time through the
        /// parent chain would aim the turret past its target by exactly the hull's
        /// heading. Y is pinned to 0 because the playfield is flat by contract
        /// (OD-27), so a pose can never carry height even if terrain arrives later.
        /// </summary>
        public void Apply(in UnitViewPose pose)
        {
            var hull = Hull;
            hull.position = new Vector3(
                pose.XMillimetres * MillimetresToMetres,
                0f,
                pose.ZMillimetres * MillimetresToMetres);
            hull.rotation = Quaternion.Euler(0f, pose.BodyYawDegrees, 0f);

            if (turret != null)
            {
                turret.rotation = Quaternion.Euler(0f, pose.TurretYawDegrees, 0f);
            }

            Health = pose.Health;
            HealthFraction = pose.HealthFraction;
        }
    }
}
