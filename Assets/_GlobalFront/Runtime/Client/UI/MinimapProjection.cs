using System;
using GlobalFront.Client.Presentation;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using UnityEngine;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// The world extent a minimap covers, in the wire's integer millimetres
    /// (Phase 3, step 3.6, ADR-012/OD-27).
    ///
    /// Millimetres rather than metres because that is the unit the rest of the
    /// client speaks: the replication table stores <c>int PosX/PosZ</c> and
    /// <see cref="WorldPointMm"/> carries <c>int</c>, so
    /// a metre-valued bounds here would put a float multiply on either side of
    /// every mapping. The values are also what a <see cref="WorldPointMm"/> can
    /// hold, which is what makes "is the point the minimap produced legal?" a
    /// decidable question rather than a hope.
    ///
    /// The four edges are stored rather than a position and a size so the
    /// inverted-bounds case cannot be spelled: <c>max &lt;= min</c> is the only
    /// illegal shape, and a size field would have had to be interpreted with a
    /// sign convention to answer it.
    /// </summary>
    public readonly struct MinimapBounds : IEquatable<MinimapBounds>
    {
        /// <summary>
        /// Half-width of the canonical map: OD-27 fixes the playfield at
        /// 400 × 400 m centred on the origin, so ±200 000 mm on both axes.
        /// </summary>
        public const int DefaultHalfExtentMillimetres = 200_000;

        /// <summary>The OD-27 map, [-200 000 .. +200 000] mm on X and Z.</summary>
        public static readonly MinimapBounds Default =
            new MinimapBounds(
                -DefaultHalfExtentMillimetres,
                -DefaultHalfExtentMillimetres,
                DefaultHalfExtentMillimetres,
                DefaultHalfExtentMillimetres);

        public MinimapBounds(int minXmm, int minZmm, int maxXmm, int maxZmm)
        {
            if (!TryCreate(minXmm, minZmm, maxXmm, maxZmm, out var bounds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxXmm),
                    maxXmm,
                    $"minimap bounds must be a positive, ordered extent within ±{SimulationConstants.MaxWorldCoordinateMm} mm " +
                    $"(got X [{minXmm}..{maxXmm}], Z [{minZmm}..{maxZmm}]).");
            }

            this = bounds;
        }

        /// <summary>
        /// <see cref="MinimapBounds(int,int,int,int)"/> for values that come from
        /// data rather than from a code path that should stop on a bad map: a
        /// malformed map descriptor is a load-time refusal, not an exception in
        /// the frame loop.
        /// </summary>
        public static bool TryCreate(
            int minXmm,
            int minZmm,
            int maxXmm,
            int maxZmm,
            out MinimapBounds bounds)
        {
            bounds = default;

            const long limit = SimulationConstants.MaxWorldCoordinateMm;

            // Widened to long because the extent is a subtraction of two signed
            // millimetre coordinates: (MaxValue - MinValue) overflows an int, and
            // an overflowed extent is a negative one that reads as "inverted".
            var extentX = (long)maxXmm - minXmm;
            var extentZ = (long)maxZmm - minZmm;

            if (extentX <= 0 || extentZ <= 0)
            {
                return false;
            }

            if (minXmm < -limit || minXmm > limit || maxXmm < -limit || maxXmm > limit ||
                minZmm < -limit || minZmm > limit || maxZmm < -limit || maxZmm > limit)
            {
                return false;
            }

            bounds = new MinimapBounds(minXmm, minZmm, maxXmm, maxZmm, true);
            return true;
        }

        private MinimapBounds(int minXmm, int minZmm, int maxXmm, int maxZmm, bool ignored)
        {
            MinXmm = minXmm;
            MinZmm = minZmm;
            MaxXmm = maxXmm;
            MaxZmm = maxZmm;
        }

        /// <summary>Western edge in millimetres.</summary>
        public int MinXmm { get; }

        /// <summary>Southern edge in millimetres (world −Z).</summary>
        public int MinZmm { get; }

        /// <summary>Eastern edge in millimetres.</summary>
        public int MaxXmm { get; }

        /// <summary>Northern edge in millimetres (world +Z).</summary>
        public int MaxZmm { get; }

        /// <summary>East–west extent in millimetres; always positive.</summary>
        public int ExtentXmm => MaxXmm - MinXmm;

        /// <summary>North–south extent in millimetres; always positive.</summary>
        public int ExtentZmm => MaxZmm - MinZmm;

        public bool Equals(MinimapBounds other) =>
            MinXmm == other.MinXmm &&
            MinZmm == other.MinZmm &&
            MaxXmm == other.MaxXmm &&
            MaxZmm == other.MaxZmm;

        public override bool Equals(object obj) => obj is MinimapBounds other && Equals(other);

        public override int GetHashCode() =>
            unchecked((MinXmm * 397) ^ (MinZmm * 31) ^ (MaxXmm * 7) ^ MaxZmm);

        public override string ToString() =>
            $"MinimapBounds(x=[{MinXmm}..{MaxXmm}], z=[{MinZmm}..{MaxZmm}])";

        public static bool operator ==(MinimapBounds left, MinimapBounds right) => left.Equals(right);

        public static bool operator !=(MinimapBounds left, MinimapBounds right) => !left.Equals(right);
    }

    /// <summary>
    /// Which side of a minimap mapping refused, so a caller can tell "the value
    /// was nonsense" from "the value was fine but off the radar".
    ///
    /// Both answers matter to the HUD: a NaN pointer coordinate is a bug in
    /// whatever produced it and must change nothing, while a click beside the
    /// minimap is an ordinary click somewhere else in the interface that must
    /// fall through to whatever owns that pixel.
    /// </summary>
    public enum MinimapMappingResult : byte
    {
        /// <summary>The mapping produced a usable value.</summary>
        Ok = 0,

        /// <summary>A coordinate was NaN or infinite.</summary>
        NonFiniteInput = 1,

        /// <summary>A screen position was outside the minimap rectangle.</summary>
        OutsideRadar = 2,

        /// <summary>A normalised coordinate was outside [0..1].</summary>
        OutsideUnitRange = 3,

        /// <summary>
        /// The mapping succeeded arithmetically but the world point it names lies
        /// outside <see cref="SimulationConstants.MaxWorldCoordinateMm"/>, so no
        /// <see cref="WorldPointMm"/> can carry it and no command may ask for it.
        /// </summary>
        OutsideWorldBounds = 4,
    }

    /// <summary>
    /// The three coordinate spaces a minimap lives between, and the arithmetic
    /// that converts them (Phase 3, step 3.6, ADR-012/OD-27).
    ///
    /// <list type="bullet">
    /// <item><description>World millimetres — the authoritative unit, the one a
    /// <see cref="WorldPointMm"/> command carries.</description></item>
    /// <item><description>Normalised radar UV — [0..1] from the southwest corner
    /// to the northeast, the space a baked terrain sprite and a radar texture are
    /// both indexed in.</description></item>
    /// <item><description>Radar pixel indices — the quantised grid a blip actually
    /// lands on.</description></item>
    /// </list>
    ///
    /// Every function here is static and pure. That is the same reason
    /// <see cref="UnitPickMath"/> is: a minimap is a projection, so its rules are
    /// assertable as equalities in EditMode rather than by looking at a rendered
    /// frame, and a bug in the mapping would otherwise only ever be caught by a
    /// human noticing that a click jumped the camera somewhere nearby-but-wrong.
    ///
    /// <b>Axis convention.</b> World X maps to UV.x and world Z maps to UV.y, so
    /// the radar reads north-up with +Z towards the top of the widget. That is
    /// also the convention <see cref="RtsCameraController.MapBounds"/> already
    /// states (its <c>Rect.y</c> is the world Z axis), and it is what lets the
    /// camera's own bounds be fed to a minimap without a transpose.
    ///
    /// <b>Precision.</b> UV is a <see cref="float"/>, so it carries roughly
    /// seven significant digits; the round trip millimetres → UV → millimetres is
    /// exact for any extent whose full width is under about 16 km because the
    /// quantisation error stays below half a millimetre. The OD-27 map is 400 m,
    /// two orders of magnitude inside that, and
    /// <see cref="TryUvToMillimetres"/> therefore rounds a value that is already
    /// an integer rather than one that merely looks like one. A map wide enough
    /// to break that would be quantising its own blips long before it lost a
    /// millimetre.
    /// </summary>
    public static class MinimapProjection
    {
        /// <summary>
        /// Lowest radar resolution worth drawing. Below this a 400 m map puts
        /// nearly three metres into a pixel and two units side by side cannot be
        /// told apart, so the guard exists to reject a mis-typed field rather than
        /// to describe a limit of the math.
        /// </summary>
        public const int MinRadarSizePixels = 8;

        /// <summary>Upper bound on a radar texture/grid, so a poisoned size field cannot ask for a huge buffer.</summary>
        public const int MaxRadarSizePixels = 1024;

        /// <summary>True when a radar resolution is inside the usable range.</summary>
        public static bool IsValidRadarSize(int sizePixels) =>
            sizePixels >= MinRadarSizePixels && sizePixels <= MaxRadarSizePixels;

        /// <summary>
        /// Clamps a value into [0..1] without propagating a NaN.
        ///
        /// <c>Mathf.Clamp01(float.NaN)</c> is NaN, because every one of its
        /// comparisons is false for NaN and it returns the input on that path.
        /// A single NaN here reaches a <see cref="Rect"/> a frame later and stays
        /// in it, so the clamps that follow are all false; the fallback has to be
        /// an explicit branch.
        /// </summary>
        public static float ClampUnit(float value)
        {
            if (!UnitPickMath.IsFinite(value))
            {
                return 0f;
            }

            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        /// <summary>Both components clamped into [0..1]; a non-finite component becomes zero.</summary>
        public static Vector2 ClampUnit(Vector2 value) => new Vector2(ClampUnit(value.x), ClampUnit(value.y));

        /// <summary>
        /// World millimetres to radar UV, clamped into [0..1].
        ///
        /// Total by construction: units outside the mapped region are a real thing
        /// a radar has to show (a unit off the edge of the baked area, a custom
        /// bounds narrower than the live map), and the display answer is "pinned to
        /// the edge", not "absent" and certainly not an exception in the blip loop.
        /// The extent is positive on both axes by
        /// <see cref="MinimapBounds.TryCreate"/>, so no division by zero is
        /// reachable here.
        /// </summary>
        public static Vector2 MillimetresToUv(in MinimapBounds bounds, int xMillimetres, int zMillimetres)
        {
            // Subtract in long before dividing: an int subtraction of two
            // millimetre coordinates overflows at the ends of the range, and an
            // overflowed difference is a negative UV rather than a far one.
            var u = ((long)xMillimetres - bounds.MinXmm) / (double)bounds.ExtentXmm;
            var v = ((long)zMillimetres - bounds.MinZmm) / (double)bounds.ExtentZmm;
            return ClampUnit(new Vector2((float)u, (float)v));
        }

        /// <summary>
        /// A world point in metres — the space <see cref="UnityEngine.Vector3"/>
        /// and every transform live in — to radar UV.
        /// </summary>
        public static Vector2 WorldMetresToUv(in MinimapBounds bounds, Vector3 world)
        {
            if (!IsFinite(world.x) || !IsFinite(world.z))
            {
                return Vector2.zero;
            }

            return MillimetresToUv(
                bounds,
                MetresToMillimetres(world.x),
                MetresToMillimetres(world.z));
        }

        /// <summary>
        /// Radar UV to a world point in millimetres.
        ///
        /// Fails rather than clamping, which is the opposite of
        /// <see cref="MillimetresToUv"/> and deliberately so: UV is a display
        /// space, where pinning a stray unit to the edge is the useful answer,
        /// while a millimetre point produced here is about to be named in a
        /// command. Silently inventing a legal destination for an illegal request
        /// is how a HUD moves a squad to the corner of the map because a pointer
        /// coordinate went NaN three frames earlier.
        /// </summary>
        public static MinimapMappingResult TryUvToMillimetres(
            in MinimapBounds bounds,
            Vector2 uv,
            out WorldPointMm point)
        {
            point = default;

            if (!UnitPickMath.IsFinite(uv.x) || !UnitPickMath.IsFinite(uv.y))
            {
                return MinimapMappingResult.NonFiniteInput;
            }

            if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f)
            {
                return MinimapMappingResult.OutsideUnitRange;
            }

            var xMillimetres = (double)bounds.MinXmm + (double)uv.x * bounds.ExtentXmm;
            var zMillimetres = (double)bounds.MinZmm + (double)uv.y * bounds.ExtentZmm;

            // Bounded before the int cast, exactly as UnitPickMath does it: past
            // 2^31 the cast wraps rather than traps, so a check on the already
            // converted value would accept a wrapped coordinate and reject a legal
            // one sitting next to it.
            const double limit = SimulationConstants.MaxWorldCoordinateMm;
            if (xMillimetres < -limit || xMillimetres > limit ||
                zMillimetres < -limit || zMillimetres > limit)
            {
                return MinimapMappingResult.OutsideWorldBounds;
            }

            point = new WorldPointMm(
                (int)System.Math.Round(xMillimetres, System.MidpointRounding.AwayFromZero),
                (int)System.Math.Round(zMillimetres, System.MidpointRounding.AwayFromZero));
            return MinimapMappingResult.Ok;
        }

        /// <summary>
        /// Radar UV to a world position in metres on the playfield plane, ready to
        /// be handed to <see cref="RtsCameraController.SetFocusPoint"/>.
        ///
        /// Y is pinned to 0 rather than derived: the walkable surface is exactly
        /// the ground plane by OD-27, and <c>ClampToBounds</c> inside the camera
        /// rewrites Y on every focus change anyway.
        /// </summary>
        public static MinimapMappingResult TryUvToWorldMetres(
            in MinimapBounds bounds,
            Vector2 uv,
            out Vector3 world)
        {
            world = Vector3.zero;

            var result = TryUvToMillimetres(bounds, uv, out var point);
            if (result != MinimapMappingResult.Ok)
            {
                return result;
            }

            world = new Vector3(
                MillimetresToMetres(point.X),
                UnitPickMath.GroundHeightMetres,
                MillimetresToMetres(point.Z));
            return MinimapMappingResult.Ok;
        }

        /// <summary>
        /// Radar UV to the centre of the pixel that carries it.
        ///
        /// Quantised with round-half-away-from-zero so the boundaries land on the
        /// last index rather than one past it: at UV = 1 the result is
        /// <c>size - 1</c>, which is what lets
        /// <see cref="PixelToUv"/> be its exact inverse without a clamp at the
        /// top edge.
        /// </summary>
        public static Vector2Int UvToPixel(Vector2 uv, int radarSizePixels)
        {
            if (!IsValidRadarSize(radarSizePixels))
            {
                return Vector2Int.zero;
            }

            var u = ClampUnit(uv.x);
            var v = ClampUnit(uv.y);
            var last = radarSizePixels - 1;
            return new Vector2Int(
                (int)System.Math.Round((double)u * last, System.MidpointRounding.AwayFromZero),
                (int)System.Math.Round((double)v * last, System.MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// The centre UV of one radar pixel, clamped into [0..1].
        ///
        /// <see cref="UvToPixel"/> can only produce an in-range index, but this is also
        /// the inverse a caller reaches for with an index read from outside that pair —
        /// a partially filled blip buffer, a widget resized between the quantise and the
        /// draw. An out-of-range UV past here becomes a quad position outside the widget
        /// and, on a masked radar, a blip that silently disappears.
        /// </summary>
        public static Vector2 PixelToUv(Vector2Int pixel, int radarSizePixels)
        {
            if (!IsValidRadarSize(radarSizePixels))
            {
                return Vector2.zero;
            }

            var last = (double)(radarSizePixels - 1);
            return ClampUnit(new Vector2((float)(pixel.x / last), (float)(pixel.y / last)));
        }

        /// <summary>
        /// A point in screen pixels to radar UV, given the minimap's rectangle on
        /// screen.
        ///
        /// Refuses rather than clamps, because a screen click is a question about
        /// intent: outside the widget the player clicked something else, and a
        /// clamped answer would move the camera to the nearest edge of the map on
        /// the strength of a click that was never on the minimap at all.
        ///
        /// Both rectangles in this layer are measured from the bottom-left, which
        /// is what an overlay canvas and <c>Input.mousePosition</c> both agree on —
        /// the same convention <see cref="SelectionMarqueePresenter"/> builds its
        /// frame in.
        /// </summary>
        public static MinimapMappingResult TryScreenToUv(
            Rect radarRectPx,
            Vector2 screenPixels,
            out Vector2 uv)
        {
            uv = default;

            if (!UnitPickMath.IsFinite(screenPixels.x) || !UnitPickMath.IsFinite(screenPixels.y) ||
                !UnitPickMath.IsFinite(radarRectPx.x) || !UnitPickMath.IsFinite(radarRectPx.y) ||
                !UnitPickMath.IsFinite(radarRectPx.width) || !UnitPickMath.IsFinite(radarRectPx.height))
            {
                return MinimapMappingResult.NonFiniteInput;
            }

            var minX = radarRectPx.xMin;
            var minY = radarRectPx.yMin;
            var maxX = radarRectPx.xMax;
            var maxY = radarRectPx.yMax;

            // xMax is x + width, so both components can be finite and the edge still be
            // +Infinity. Without this the subtraction below is Infinity - x = Infinity and
            // the division returns 0 for every pixel, which reads as a radar pinned to
            // its own south-west corner rather than a refused mapping.
            if (!UnitPickMath.IsFinite(maxX) || !UnitPickMath.IsFinite(maxY))
            {
                return MinimapMappingResult.NonFiniteInput;
            }

            if (maxX <= minX || maxY <= minY)
            {
                return MinimapMappingResult.OutsideRadar;
            }

            if (screenPixels.x < minX || screenPixels.x > maxX ||
                screenPixels.y < minY || screenPixels.y > maxY)
            {
                return MinimapMappingResult.OutsideRadar;
            }

            uv = new Vector2(
                (screenPixels.x - minX) / (maxX - minX),
                (screenPixels.y - minY) / (maxY - minY));

            // The division above can land a hair outside [0..1] for a pixel exactly
            // on the far edge, and TryUvToMillimetres refuses that rather than
            // clamping it, so the boundary of the widget would be a dead zone.
            uv = ClampUnit(uv);
            return MinimapMappingResult.Ok;
        }

        /// <summary>
        /// The ground rectangle the camera's frustum covers on the Y = 0 plane —
        /// the minimap's viewport indicator.
        ///
        /// Taken from the four viewport corners rather than derived from the
        /// camera's height and pitch, because the four corners are the whole
        /// question: height, pitch, field of view and aspect all feed the answer,
        /// and re-deriving them here would put a second, always-slightly-different
        /// model of the camera next to the one <see cref="RtsCameraController"/>
        /// already owns.
        ///
        /// <see cref="Camera.ViewportPointToRay"/> is used instead of
        /// <see cref="Camera.ScreenPointToRay"/> on purpose. The screen version
        /// needs a pixel size, which in EditMode reports the editor window unless a
        /// <c>RenderTexture</c> is attached — fine for the selection tests that
        /// already do that, and a needless dependency for a widget that only ever
        /// wants the four corners of the frame.
        ///
        /// All four corners must hit. Three of four is a camera pitched at the sky,
        /// and a rectangle built from three of its corners describes the ground the
        /// player cannot see.
        /// </summary>
        public static bool TryCameraGroundRect(Camera camera, out Rect groundRectMetres)
        {
            groundRectMetres = default;
            if (camera == null)
            {
                return false;
            }

            var minX = float.PositiveInfinity;
            var minY = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var maxY = float.NegativeInfinity;

            for (var corner = 0; corner < 4; corner++)
            {
                var u = (corner & 1) == 0 ? 0f : 1f;
                var v = (corner & 2) == 0 ? 0f : 1f;

                var ray = camera.ViewportPointToRay(new Vector3(u, v, 0f));
                if (!UnitPickMath.TryIntersectGroundPlane(ray.origin, ray.direction, out var hit))
                {
                    return false;
                }

                if (hit.x < minX)
                {
                    minX = hit.x;
                }

                if (hit.z < minY)
                {
                    minY = hit.z;
                }

                if (hit.x > maxX)
                {
                    maxX = hit.x;
                }

                if (hit.z > maxY)
                {
                    maxY = hit.z;
                }
            }

            groundRectMetres = new Rect(minX, minY, maxX - minX, maxY - minY);
            return true;
        }

        /// <summary>
        /// A world-space ground rectangle (X across, Z down the <c>Rect.y</c> axis,
        /// the <see cref="RtsCameraController.MapBounds"/> convention) into radar UV
        /// space, so the viewport indicator can be drawn without a second mapping
        /// pass at the point of use.
        ///
        /// The result is ordered by construction and may be <c>default</c>: both edges
        /// of the mapped box go through <see cref="WorldMetresToUv"/>, which clamps
        /// into [0..1], and <see cref="Rect.MinMaxRect"/> does not normalise its own
        /// arguments, so an inverted input would otherwise come back as a rectangle
        /// with negative size — an indicator whose <c>anchorMin</c> sits beyond its
        /// <c>anchorMax</c>, which uGUI renders as a box covering the whole widget.
        /// </summary>
        public static Rect GroundRectToUv(in MinimapBounds bounds, Rect groundRectMetres)
        {
            if (!IsFinite(groundRectMetres.x) || !IsFinite(groundRectMetres.y) ||
                !IsFinite(groundRectMetres.width) || !IsFinite(groundRectMetres.height) ||
                !IsFinite(groundRectMetres.xMax) || !IsFinite(groundRectMetres.yMax) ||
                groundRectMetres.width < 0f || groundRectMetres.height < 0f)
            {
                return default;
            }

            var min = WorldMetresToUv(bounds, new Vector3(groundRectMetres.xMin, 0f, groundRectMetres.yMin));
            var max = WorldMetresToUv(bounds, new Vector3(groundRectMetres.xMax, 0f, groundRectMetres.yMax));
            return Rect.MinMaxRect(
                Mathf.Min(min.x, max.x),
                Mathf.Min(min.y, max.y),
                Mathf.Max(min.x, max.x),
                Mathf.Max(min.y, max.y));
        }

        /// <summary>
        /// Metres to the wire's millimetres, in <c>double</c> and rounded away from
        /// zero — the same rule <see cref="UnitPickMath.TryToMillimetrePoint"/>
        /// applies, restated here because the minimap's inputs are its own metre
        /// values rather than a ray hit.
        ///
        /// Saturated rather than cast. The unchecked <c>(int)</c> of a value past
        /// 2 147 483.647 m wraps to <see cref="int.MinValue"/>, which would put a unit
        /// far off the east edge of a huge map at its south-west corner — the one
        /// answer worse than a refusal, because it is a legal coordinate in the wrong
        /// place. A NaN input is zero rather than a wrapped value: it names no place,
        /// and the mapping that consumes it clamps to the south-west corner it can
        /// legitimately mean.
        /// </summary>
        public static int MetresToMillimetres(double metres)
        {
            if (double.IsNaN(metres))
            {
                return 0;
            }

            var rounded = System.Math.Round(
                metres * UnitPickMath.MetresToMillimetres,
                System.MidpointRounding.AwayFromZero);

            if (rounded >= int.MaxValue)
            {
                return int.MaxValue;
            }

            if (rounded <= int.MinValue)
            {
                return int.MinValue;
            }

            return (int)rounded;
        }

        /// <summary>Millimetres to metres, the inverse of <see cref="MetresToMillimetres"/>.</summary>
        public static float MillimetresToMetres(int millimetres) =>
            millimetres * UnitView.MillimetresToMetres;

        private static bool IsFinite(float value) => UnitPickMath.IsFinite(value);
    }
}
