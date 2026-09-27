using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using UnityEngine;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// The picking arithmetic behind selection and command targeting (Phase 3,
    /// step 3.5): ray-to-ground intersection, the metre/millimetre boundary and
    /// screen-rectangle containment.
    ///
    /// Every one of these is a static pure function rather than a call into
    /// <c>UnityEngine.Physics</c> because a unit view carries no collider — the
    /// playfield is flat at Y = 0 by contract (OD-27), the crowd is rendered from a
    /// pooled hull with no collision surface behind it (OD-26), and the selection
    /// ring is an instanced overlay drawn from the binder's slot table (OD-25). A
    /// <c>Physics.Raycast</c> over that hierarchy answers "nothing" for every unit on
    /// screen, so picking has to be arithmetic over the same slot table the overlays
    /// are built from. That also makes it exact in EditMode, where no physics scene is
    /// being stepped.
    ///
    /// The second reason is determinism of the client's own decisions: a physics hit
    /// depends on collider insertion order and on which frame the physics step ran,
    /// so two clients clicking the same pixel could pick different units. Distance
    /// over the bound slot table cannot.
    /// </summary>
    public static class UnitPickMath
    {
        /// <summary>
        /// Height of the walkable plane. Not a tunable: OD-27 fixes every walkable
        /// surface at Y = 0 and bans the runtime Unity Terrain precisely so that the
        /// pathing grid and the visual height can never disagree.
        /// </summary>
        public const float GroundHeightMetres = 0f;

        /// <summary>
        /// Below this, a direction's vertical component is treated as parallel to the
        /// ground. The threshold is not zero on purpose: <c>ScreenPointToRay</c> on a
        /// camera pitched to its clamp produces a <c>direction.y</c> that is a tiny
        /// non-zero rather than an exact zero, and dividing by it yields an enter
        /// distance of ~1e8 metres — a "hit" far behind the map that then converts
        /// into a plausible-looking millimetre coordinate. A perfectly horizontal
        /// camera must miss, so the guard has to catch near-horizontal too.
        /// </summary>
        public const float ParallelAxisEpsilon = 1e-6f;

        /// <summary>
        /// Metres to the wire's integer millimetres: the inverse of
        /// <see cref="UnitView.MillimetresToMetres"/>, stated as a multiply for the
        /// same reason the view states its conversion as one.
        /// </summary>
        public const float MetresToMillimetres = 1000f;

        /// <summary>
        /// Intersects a ray with the ground plane and reports the hit point.
        ///
        /// False for a non-finite ray, for a ray parallel to or aimed away from the
        /// plane, and for a ray that starts on the wrong side of it. The last two are
        /// the cases a horizontal or upward-pitched camera produces; a caller that
        /// treated them as "hit the origin" would move every selected unit to the
        /// centre of the map the moment the player clicked sky.
        /// </summary>
        public static bool TryIntersectGroundPlane(
            Vector3 origin,
            Vector3 direction,
            out Vector3 point)
        {
            point = Vector3.zero;

            if (!IsFinite(origin.x) || !IsFinite(origin.y) || !IsFinite(origin.z) ||
                !IsFinite(direction.x) || !IsFinite(direction.y) || !IsFinite(direction.z))
            {
                return false;
            }

            if (Mathf.Abs(direction.y) <= ParallelAxisEpsilon)
            {
                return false;
            }

            var enter = (GroundHeightMetres - origin.y) / direction.y;

            // A ray that begins exactly on the plane and climbs away from it never
            // meets the plane ahead of itself, yet enter comes out as a signed zero
            // and `enter < 0f` is false for both -0f and +0f. Without this the pointer
            // reports the camera's own position as ground the player clicked.
            if (origin.y == GroundHeightMetres && direction.y > 0f)
            {
                return false;
            }

            if (enter < 0f)
            {
                // The plane is behind the ray. With direction.y far from zero this is
                // an upward-looking ray over ground that is behind the camera, which is
                // a miss rather than a hit at negative distance.
                return false;
            }

            // Computed in a local and only published once every component is known to
            // be finite: writing straight into the out parameter leaves an overflowed
            // ray — a camera flown to the edge of the float range — having reported a
            // miss while still handing the caller half a hit point.
            var hit = origin + direction * enter;
            if (!IsFinite(hit.x) || !IsFinite(hit.y) || !IsFinite(hit.z))
            {
                return false;
            }

            // Snapped rather than trusted: divide-and-multiply is not an identity in
            // binary floating point, so the raw y of a slanted ray lands a few
            // 1e-7 off the plane. The playfield is exactly Y = 0 (OD-27), and a caller
            // that later compares the hit against a unit position should not have to
            // carry a tolerance for a value that has one by contract.
            hit.y = GroundHeightMetres;
            point = hit;
            return true;
        }

        /// <summary>
        /// Converts a world-space metre position to the integer millimetre point a
        /// command carries, or refuses it.
        ///
        /// Refusal covers NaN and infinity (a poisoned pointer would otherwise reach
        /// the server as an arbitrary <see cref="int"/>), and anything outside
        /// <see cref="SimulationConstants.MaxWorldCoordinateMm"/>, which is the bound
        /// <see cref="WorldPointMm.IsWithinSimulationBounds"/> states and that
        /// <c>MoveCommand</c> validates against. The range is checked before the
        /// <see cref="int"/> cast rather than after it: past 2^31 milliseconds the cast
        /// wraps instead of trapping, so a check on the converted value would reject a
        /// legal point and accept a wrapped one.
        /// </summary>
        public static bool TryToMillimetrePoint(Vector3 world, out WorldPointMm point)
        {
            point = default;

            if (!IsFinite(world.x) || !IsFinite(world.z))
            {
                return false;
            }

            // Double, not float: at the ceiling the millimetre value needs 30 bits and
            // float carries 24, so a float multiply would round a legal coordinate
            // into an illegal one right where the guard is deciding. The rounding is
            // done on the double for the same reason — casting back to float to reach
            // Mathf.RoundToInt re-quantises to a 64 mm step up there, and float's
            // round-half-to-even turns an exact .5 mm into the wrong neighbour.
            var xMillimetres = (double)world.x * MetresToMillimetres;
            var zMillimetres = (double)world.z * MetresToMillimetres;
            const double bound = SimulationConstants.MaxWorldCoordinateMm;
            if (xMillimetres < -bound || xMillimetres > bound ||
                zMillimetres < -bound || zMillimetres > bound)
            {
                return false;
            }

            point = new WorldPointMm(
                (int)System.Math.Round(xMillimetres, System.MidpointRounding.AwayFromZero),
                (int)System.Math.Round(zMillimetres, System.MidpointRounding.AwayFromZero));
            return true;
        }

        /// <summary>
        /// True when a screen position falls inside an inclusive rectangle. Written
        /// as comparisons rather than as <c>Rect.Contains</c> because <see cref="Rect"/>
        /// normalises its <c>width</c> and <c>height</c>, which would silently accept a
        /// degenerate box whose <c>xMin</c> came from a NaN drag position.
        /// </summary>
        public static bool IsInsideScreenRect(
            Vector2 screenPosition,
            float minX,
            float minY,
            float maxX,
            float maxY)
        {
            if (!IsFinite(screenPosition.x) || !IsFinite(screenPosition.y))
            {
                return false;
            }

            return screenPosition.x >= minX &&
                   screenPosition.x <= maxX &&
                   screenPosition.y >= minY &&
                   screenPosition.y <= maxY;
        }

        /// <summary>
        /// Finite test that does not marshal a <see cref="Vector3"/> through a native
        /// call: the per-unit picking loop runs this for every bound view.
        /// </summary>
        public static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// The seam between selection logic and a camera.
    ///
    /// The controller needs two projections and nothing else from Unity's rendering
    /// world: pointer pixel to ground point, and world point to screen pixel. Both
    /// come from one camera transform and two matrices, so stating them as an
    /// interface lets the marquee and picking rules be driven in EditMode from either
    /// a real <see cref="Camera"/> (which <c>WorldToScreenPoint</c> answers without
    /// ever rendering) or from an exact hand-authored projection. The second matters
    /// because a projection through a real camera is only as predictable as the
    /// aspect and field of view the editor happens to hand out.
    /// </summary>
    public interface IUnitPointerProjector
    {
        /// <summary>
        /// Ground point under a screen position, or false when the pointer's ray never
        /// meets the walkable plane. Callers must treat false as "no position", not as
        /// the world origin.
        /// </summary>
        bool TryGetGroundPoint(Vector2 screenPixels, out Vector3 groundWorld);

        /// <summary>
        /// Screen position of a world point. The returned <c>z</c> is the signed
        /// distance in front of the camera, so <c>z &lt;= 0</c> means the point is
        /// behind it and must be excluded from any screen-space test.
        /// </summary>
        Vector3 ProjectToScreen(Vector3 world);

        /// <summary>
        /// Viewport size in pixels, so a selection can ask whether a unit is actually
        /// on screen rather than merely in front of the camera.
        /// </summary>
        Vector2 ScreenSizePixels { get; }
    }

    /// <summary>
    /// <see cref="IUnitPointerProjector"/> over a <see cref="Camera"/> (step 3.5).
    /// Holds the reference rather than resolving <c>Camera.main</c> per query: the
    /// marquee asks once per bound unit per frame, and a tag lookup there is a scene
    /// scan per unit.
    /// </summary>
    public sealed class CameraUnitPointerProjector : IUnitPointerProjector
    {
        private Camera _camera;

        public CameraUnitPointerProjector(Camera camera)
        {
            _camera = camera;
        }

        /// <summary>The camera projections are taken from; may be null.</summary>
        public Camera Camera => _camera;

        /// <summary>
        /// Re-points the projector after a camera swap or a resync. A stale camera
        /// would keep projecting the marquee from the view the player left.
        /// </summary>
        public void SetCamera(Camera camera) => _camera = camera;

        public bool TryGetGroundPoint(Vector2 screenPixels, out Vector3 groundWorld)
        {
            groundWorld = Vector3.zero;
            if (_camera == null)
            {
                return false;
            }

            if (!UnitPickMath.IsFinite(screenPixels.x) || !UnitPickMath.IsFinite(screenPixels.y))
            {
                return false;
            }

            var ray = _camera.ScreenPointToRay(screenPixels);
            return UnitPickMath.TryIntersectGroundPlane(ray.origin, ray.direction, out groundWorld);
        }

        public Vector3 ProjectToScreen(Vector3 world)
        {
            if (_camera == null)
            {
                // Behind-the-camera is the one answer that is safe for every caller:
                // the marquee culls on z > 0, and picking never compares against this.
                return new Vector3(0f, 0f, -1f);
            }

            return _camera.WorldToScreenPoint(world);
        }

        public Vector2 ScreenSizePixels
        {
            get
            {
                if (_camera == null)
                {
                    return Vector2.zero;
                }

                var width = _camera.pixelWidth;
                var height = _camera.pixelHeight;
                if (width <= 0 || height <= 0)
                {
                    // A camera that has never rendered reports zero. Screen is the
                    // only thing left to ask, and an off-screen test against it errs
                    // towards not selecting rather than selecting blind.
                    width = UnityEngine.Screen.width;
                    height = UnityEngine.Screen.height;
                }

                return new Vector2(width, height);
            }
        }
    }
}
