using System;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Client.UI;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using EntityId = GlobalFront.Core.Model.EntityId;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;
using Object = UnityEngine.Object;

namespace GlobalFront.Tests.EditMode.Client.Presentation
{
    /// <summary>
    /// Phase 3, step 3.6: the 30 Hz minimap and the zero-GC tactical HUD
    /// (<see cref="MinimapProjection"/>, <see cref="MinimapRadarModel"/>,
    /// <see cref="MinimapInteractionController"/>, <see cref="TacticalHudPointerBlocker"/>,
    /// <see cref="SelectionHudState"/>, <see cref="PermanentHudPresenter"/>,
    /// <see cref="SelectionHudPresenter"/>, ADR-012/OD-24, OD-27, OD-28).
    ///
    /// Four things these tests exist to defend.
    ///
    /// <b>The map has to be the same map at both ends.</b> A radar is a projection with
    /// three spaces — millimetres, UV and pixels — and every one of them is assertable
    /// as an equality, so they are asserted as equalities. A click that lands one cell
    /// off is not a rounding complaint but a squad sent to the wrong bank of the river.
    ///
    /// <b>Thirty hertz has to mean thirty hertz.</b> OD-24 removed the second camera
    /// precisely so the radar could not cost a frame's worth of work; a throttle that
    /// rebuilds anyway restores that cost behind a constant that says otherwise. The
    /// cadence is therefore asserted both ways: a sub-tick advance does nothing, a
    /// past-tick advance does.
    ///
    /// <b>The HUD owns its pixels.</b> Step 3.5's selection reads one mouse position and
    /// asks the ground what is under it. Without a gate, dragging a box across the
    /// minimap also drags a box across the terrain behind it, and a right-click that
    /// moves a squad across the map also issues a second move to the world point under
    /// the widget. Each pointer-capture test states what the same input does with the
    /// gate absent, so the gate is what the assertion proves.
    ///
    /// <b>Nothing in a steady frame may be written twice.</b> The dynamic canvas has two
    /// separate guarantees and they are not the same one: <c>Is.Not.AllocatingGCMemory</c>
    /// proves no string is built, and <see cref="SelectionHudPresenter.TextMutationCount"/>
    /// proves no graphic is dirtied. A panel that assigns an unchanged string allocates
    /// nothing and still rebuilds its canvas batch every frame, which is the cost the
    /// three-canvas isolation exists to remove.
    /// </summary>
    [TestFixture]
    public sealed class MinimapAndTacticalHudTests
    {
        private const int SlotCapacity = 512;
        private const double TickSeconds = 0.05;
        private const double FrameSeconds = 1.0 / 60.0;
        private const int FullHealth = UnitCatalog.PrototypeMaximumHealth;

        private const byte Local = 1;
        private const byte Enemy = 2;

        /// <summary>OD-27: the canonical half-width of the map, in millimetres.</summary>
        private const int MapHalfMm = MinimapBounds.DefaultHalfExtentMillimetres;

        private const int Ax = 1000;
        private const int Bx = 3000;
        private const int Cx = 5000;
        private const int RowZ = 2000;

        /// <summary>A minimap widget far from the battlefield pixels the fixture units occupy.</summary>
        private static readonly Rect RadarScreenRect = new Rect(0f, 0f, 200f, 200f);

        private Transform _root;
        private UnitViewPool _pool;
        private ClientReplicationWorld _world;
        private UnitViewTickBuffer _buffer;
        private UnitViewBinder _binder;
        private UnitSelectionController _selection;
        private MinimapRadarModel _radar;
        private RecordingCommandSink _sink;
        private TestProjector _projector;
        private readonly List<GameObject> _created = new List<GameObject>();
        private ulong _tick;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("MinimapAndTacticalHudTests_Root").transform;
            _pool = new UnitViewPool(_root);
            _world = new ClientReplicationWorld(SlotCapacity);
            _buffer = new UnitViewTickBuffer(SlotCapacity, TickSeconds);
            _binder = new UnitViewBinder(_pool, _world, _buffer);
            _selection = new UnitSelectionController(_binder, _world, new PlayerId(Local));
            _radar = new MinimapRadarModel(_world, _selection);
            _sink = new RecordingCommandSink();
            _projector = new TestProjector();
            _tick = 10;
        }

        [TearDown]
        public void TearDown()
        {
            _pool?.Dispose();

            for (var index = 0; index < _created.Count; index++)
            {
                if (_created[index] != null)
                {
                    Object.DestroyImmediate(_created[index]);
                }
            }

            _created.Clear();

            if (_root != null)
            {
                Object.DestroyImmediate(_root.gameObject);
            }

            _root = null;
            _radar = null;
            _selection = null;
            _binder = null;
            _buffer = null;
            _world = null;
            _pool = null;
        }

        // -------------------------------------------------------------------- fixtures

        private static DeltaAddRecord Add(
            ulong entity,
            byte owner,
            int xMillimetres,
            int zMillimetres,
            byte kind)
        {
            return new DeltaAddRecord(
                new EntityId(entity),
                new PlayerId(owner),
                new WorldPointMm(xMillimetres, zMillimetres),
                FullHealth,
                false,
                new WorldPointMm(0, 0),
                new EntityId(0),
                false,
                kind);
        }

        private int Spawn(
            ulong entity,
            int xMillimetres,
            int zMillimetres,
            byte owner = Local,
            byte kind = UnitKinds.Scout)
        {
            Assert.That(
                _world.ApplyAdd(Add(entity, owner, xMillimetres, zMillimetres, kind)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            _pool.Warmup(kind, _pool.OwnedCount(kind) + 1);
            BindFrame();
            return SlotOf(entity);
        }

        private void SpawnUnitOnly(ulong entity, int xMillimetres, int zMillimetres, byte owner = Local)
        {
            Assert.That(
                _world.ApplyAdd(Add(entity, owner, xMillimetres, zMillimetres, UnitKinds.Scout)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
        }

        private void BindFrame()
        {
            _tick += 2;
            _buffer.CaptureTick(_tick, _world);
            _binder.Render(FrameSeconds);
        }

        /// <summary>Delivers a health update and lets presentation follow it, as a casualty really arrives.</summary>
        private void SetHealth(ulong entity, int health)
        {
            Assert.That(
                _world.ApplyUpdate(new DeltaUpdateRecord(
                    new EntityId(entity),
                    (byte)UnitDirtyMask.Health,
                    new PlayerId(Local),
                    new WorldPointMm(0, 0),
                    health,
                    false,
                    new WorldPointMm(0, 0),
                    new EntityId(0),
                    false)),
                Is.EqualTo(ClientWorldApplyResult.Ok));

            BindFrame();
        }

        private int SlotOf(ulong entity)
        {
            for (var slot = 0; slot < _world.Capacity; slot++)
            {
                if (_world.TryGetSlotState(slot, out var state) && state.Entity.Value == entity)
                {
                    return slot;
                }
            }

            Assert.Fail($"entity {entity} is not in the client world");
            return -1;
        }

        private T Component<T>(string name) where T : Component
        {
            var host = new GameObject(name);
            host.transform.SetParent(_root, false);
            _created.Add(host);
            return host.AddComponent<T>();
        }

        private GameObject Child(string name)
        {
            var host = new GameObject(name);
            host.transform.SetParent(_root, false);
            _created.Add(host);
            return host;
        }

        /// <summary>
        /// A GameObject carrying a real <see cref="RectTransform"/> at an exact size,
        /// which is what a UI widget is. <c>AddComponent</c> onto a plain GameObject is
        /// how the runtime presenters build their own tree; a test that wants a bare rect
        /// has to ask for one.
        /// </summary>
        private RectTransform Widget(string name, Vector2 sizePx)
        {
            var host = new GameObject(name, typeof(RectTransform));
            host.transform.SetParent(_root, false);
            _created.Add(host);

            var rect = host.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = sizePx;
            return rect;
        }

        private sealed class Harness
        {
            public PermanentHudPresenter Permanent;
            public SelectionHudPresenter Dynamic;
            public SelectionHudState State;
            public TacticalHudPointerBlocker Blocker;
            public MinimapInteractionController Interaction;
            public RtsCameraController CameraRig;
            public Camera Camera;
        }

        /// <summary>
        /// Builds both HUD canvases, a minimap input handler and the camera it pans, with
        /// the blocking rectangles wired into a shared gate — the arrangement the
        /// bootstrap is meant to produce, assembled here so that the integration is what
        /// gets tested rather than each component in isolation.
        /// </summary>
        private Harness BuildHarness()
        {
            var harness = new Harness
            {
                State = new SelectionHudState(_selection, _world),
                Blocker = new TacticalHudPointerBlocker(),
            };

            var rig = Child("CameraRig");
            harness.Camera = rig.AddComponent<Camera>();
            harness.Camera.pixelRect = new Rect(0f, 0f, 800f, 600f);
            harness.Camera.fieldOfView = 60f;
            harness.Camera.nearClipPlane = 0.3f;
            harness.Camera.farClipPlane = 2000f;

            harness.CameraRig = rig.AddComponent<RtsCameraController>();
            var input = rig.AddComponent<RtsInputManager>();
            input.SetMockMode(true);
            harness.CameraRig.SetInputManager(input);
            harness.CameraRig.InitializeState();

            var hud = Child("TacticalHud");

            harness.Permanent = hud.AddComponent<PermanentHudPresenter>();
            Assert.That(harness.Permanent.Build(), Is.True);
            harness.Permanent.Configure(_radar, _selection, _sink);
            harness.Permanent.RegisterBlockingRects(harness.Blocker);

            harness.Dynamic = hud.AddComponent<SelectionHudPresenter>();
            Assert.That(harness.Dynamic.Build(), Is.True);
            harness.Dynamic.Configure(harness.State);
            harness.Dynamic.RegisterBlockingRects(harness.Blocker);

            harness.Interaction = hud.AddComponent<MinimapInteractionController>();
            harness.Interaction.Configure(_radar, harness.CameraRig, _selection, _sink);
            harness.Interaction.SetMinimapScreenRect(RadarScreenRect);

            return harness;
        }

        /// <summary>Screen pixel whose UV lands on the radar at the given millimetre point.</summary>
        private static Vector2 RadarPixelForMillimetres(int xMillimetres, int zMillimetres)
        {
            var uv = MinimapProjection.MillimetresToUv(MinimapBounds.Default, xMillimetres, zMillimetres);
            return new Vector2(
                RadarScreenRect.xMin + uv.x * RadarScreenRect.width,
                RadarScreenRect.yMin + uv.y * RadarScreenRect.height);
        }

        private MinimapBlip BlipFor(ulong entity)
        {
            for (var index = 0; index < _radar.BlipCount; index++)
            {
                var blip = _radar.GetBlip(index);
                if (blip.Entity == entity)
                {
                    return blip;
                }
            }

            Assert.Fail($"no radar blip for entity {entity}");
            return default;
        }

        private MinimapBlipQuad QuadFor(MinimapBlipQuad[] quads, int count, ulong entity)
        {
            for (var index = 0; index < count; index++)
            {
                if (quads[index].Entity == entity)
                {
                    return quads[index];
                }
            }

            Assert.Fail($"no blip quad for entity {entity}");
            return default;
        }

        // ============================================================ 1. coordinate math

        [Test]
        public void MillimetresToUv_Od27Bounds_MapOriginAndCornersExactly()
        {
            var bounds = MinimapBounds.Default;

            Assert.That(
                MinimapProjection.MillimetresToUv(bounds, 0, 0),
                Is.EqualTo(new Vector2(0.5f, 0.5f)),
                "OD-27 centres the map on the origin, so the origin is the centre of the radar");

            Assert.That(
                MinimapProjection.MillimetresToUv(bounds, -MapHalfMm, -MapHalfMm),
                Is.EqualTo(new Vector2(0f, 0f)),
                "the southwest corner of the map is the southwest corner of the widget");

            Assert.That(
                MinimapProjection.MillimetresToUv(bounds, MapHalfMm, MapHalfMm),
                Is.EqualTo(new Vector2(1f, 1f)));
        }

        [Test]
        public void MillimetresToUv_UsesWorldXForUAndWorldZForV()
        {
            // A transpose is invisible on a square map probed at symmetric points, so the
            // two axes are tested with different values rather than at the corners.
            var uv = MinimapProjection.MillimetresToUv(MinimapBounds.Default, 100_000, -50_000);
            Assert.That(uv.x, Is.GreaterThan(0.5f), "+X must advance U");
            Assert.That(uv.y, Is.LessThan(0.5f), "-Z must retreat along V, so the radar reads north-up");
        }

        [TestCase(0, 0)]
        [TestCase(-MapHalfMm, -MapHalfMm)]
        [TestCase(MapHalfMm, MapHalfMm)]
        [TestCase(137, -49_123)]
        [TestCase(-1, 1)]
        [TestCase(199_999, -199_999)]
        [TestCase(-200_000, 200_000)]
        [TestCase(1, -1)]
        public void UvRoundTrip_RecoversTheExactMillimetrePoint(int xMillimetres, int zMillimetres)
        {
            var bounds = MinimapBounds.Default;
            var uv = MinimapProjection.MillimetresToUv(bounds, xMillimetres, zMillimetres);

            Assert.That(
                MinimapProjection.TryUvToMillimetres(bounds, uv, out var back),
                Is.EqualTo(MinimapMappingResult.Ok));
            Assert.That(
                back.X,
                Is.EqualTo(xMillimetres),
                "the round trip must be exact, not merely within a tolerance");
            Assert.That(back.Z, Is.EqualTo(zMillimetres));
        }

        [TestCase(-250_000)]
        [TestCase(250_000)]
        [TestCase(int.MaxValue)]
        [TestCase(int.MinValue)]
        public void MillimetresToUv_ClampsUnitsOutsideTheMapWithoutFailing(int xMillimetres)
        {
            var uv = MinimapProjection.MillimetresToUv(MinimapBounds.Default, xMillimetres, 0);

            Assert.That(uv.x, Is.EqualTo(xMillimetres < 0 ? 0f : 1f), "an off-map unit pins to the edge");
            Assert.That(uv.y, Is.EqualTo(0.5f), "and the on-map axis is untouched");
            Assert.That(uv.x, Is.InRange(0f, 1f));
            Assert.That(uv.y, Is.InRange(0f, 1f));
        }

        [Test]
        public void MillimetresToUv_AtTheEndsOfTheIntRange_DoesNotOverflow()
        {
            // int.MinValue - (-200 000) wraps a 32-bit subtraction, and a wrapped
            // difference is a UV of the wrong sign rather than a clamped one.
            var uv = MinimapProjection.MillimetresToUv(MinimapBounds.Default, int.MinValue, int.MaxValue);

            Assert.That(uv.x, Is.EqualTo(0f));
            Assert.That(uv.y, Is.EqualTo(1f));
        }

        [TestCase(0f, 0f)]
        [TestCase(1f, 1f)]
        [TestCase(0.5f, 0.25f)]
        public void TryUvToWorldMetres_LandsOnTheGroundPlane(float u, float v)
        {
            Assert.That(
                MinimapProjection.TryUvToWorldMetres(MinimapBounds.Default, new Vector2(u, v), out var world),
                Is.EqualTo(MinimapMappingResult.Ok));
            Assert.That(world.y, Is.EqualTo(0f), "OD-27: the playfield is exactly Y = 0");
        }

        [TestCase(0.25f, 1.5f)]
        [TestCase(1.0001f, 0.5f)]
        [TestCase(-0.001f, 0.5f)]
        [TestCase(0.5f, -0.5f)]
        public void TryUvToMillimetres_RefusesAnOutOfRangeUvRatherThanInventingAPoint(float u, float v)
        {
            var result = MinimapProjection.TryUvToMillimetres(
                MinimapBounds.Default,
                new Vector2(u, v),
                out var point);

            Assert.That(result, Is.EqualTo(MinimapMappingResult.OutsideUnitRange),
                "UV is a display space that clamps; a millimetre point is a command destination that may not");
            Assert.That(point, Is.EqualTo(default(WorldPointMm)));
        }

        [TestCase(float.NaN, 0.5f)]
        [TestCase(float.PositiveInfinity, 0.5f)]
        [TestCase(0.5f, float.NaN)]
        [TestCase(0.5f, float.NegativeInfinity)]
        public void TryUvToMillimetres_RefusesANonFiniteUv(float u, float v)
        {
            Assert.That(
                MinimapProjection.TryUvToMillimetres(
                    MinimapBounds.Default,
                    new Vector2(u, v),
                    out var point),
                Is.EqualTo(MinimapMappingResult.NonFiniteInput));
            Assert.That(point.IsWithinSimulationBounds, Is.True,
                "a refused conversion must leave the default behind, not half a hit");
        }

        [Test]
        public void TryUvToMillimetres_AtTheFullWireRange_IsAcceptedNotRefused()
        {
            var bounds = new MinimapBounds(
                -SimulationConstants.MaxWorldCoordinateMm,
                -SimulationConstants.MaxWorldCoordinateMm,
                SimulationConstants.MaxWorldCoordinateMm,
                SimulationConstants.MaxWorldCoordinateMm);

            Assert.That(
                MinimapProjection.TryUvToMillimetres(bounds, Vector2.one, out var point),
                Is.EqualTo(MinimapMappingResult.Ok),
                "the far edge of the largest legal map is legal, and an off-by-one guard here would cut the playfield short");
            Assert.That(point.X, Is.EqualTo(SimulationConstants.MaxWorldCoordinateMm));
            Assert.That(point.Z, Is.EqualTo(SimulationConstants.MaxWorldCoordinateMm));
        }

        [TestCase(0, 0, 0, 0)]
        [TestCase(5, 5, 5, 5)]
        [TestCase(10, 0, 5, 10)]
        [TestCase(1, 1, 0, 0)]
        public void MinimapBounds_RejectZeroAndInvertedExtents(int minX, int minZ, int maxX, int maxZ)
        {
            Assert.That(
                MinimapBounds.TryCreate(minX, minZ, maxX, maxZ, out _),
                Is.False,
                "every mapping downstream divides by the extent");

            Assert.Throws<ArgumentOutOfRangeException>(() => new MinimapBounds(minX, minZ, maxX, maxZ));
        }

        [Test]
        public void MinimapBounds_RejectCoordinatesOutsideTheWireRange()
        {
            Assert.That(
                MinimapBounds.TryCreate(-SimulationConstants.MaxWorldCoordinateMm - 1, 0, 1000, 1000, out _),
                Is.False);
            Assert.That(
                MinimapBounds.TryCreate(0, 0, 1000, 1000, out var bounds),
                Is.True);
            Assert.That(bounds.ExtentXmm, Is.EqualTo(1000));
            Assert.That(bounds.ExtentZmm, Is.EqualTo(1000));
        }

        [Test]
        public void MinimapBounds_AcceptsTheWidestMapTheWireAllows()
        {
            // Max minus Min overflows a 32-bit subtraction at the ends of the range, so
            // the extent is widened to long on the way in. The largest legal map is the
            // one that would otherwise read as inverted and be refused.
            Assert.That(
                MinimapBounds.TryCreate(
                    -SimulationConstants.MaxWorldCoordinateMm,
                    -SimulationConstants.MaxWorldCoordinateMm,
                    SimulationConstants.MaxWorldCoordinateMm,
                    SimulationConstants.MaxWorldCoordinateMm,
                    out var bounds),
                Is.True);
            Assert.That(bounds.ExtentXmm, Is.EqualTo(2 * SimulationConstants.MaxWorldCoordinateMm));
            Assert.That(bounds.ExtentZmm, Is.GreaterThan(0));
        }

        [Test]
        public void CustomBounds_RemapTheWholeWidget()
        {
            var bounds = new MinimapBounds(0, 0, 1000, 2000);

            Assert.That(MinimapProjection.MillimetresToUv(bounds, 500, 1000), Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(MinimapProjection.MillimetresToUv(bounds, 0, 0), Is.EqualTo(Vector2.zero));
            Assert.That(MinimapProjection.MillimetresToUv(bounds, 1000, 2000), Is.EqualTo(Vector2.one));

            Assert.That(
                MinimapProjection.TryUvToMillimetres(bounds, new Vector2(0.25f, 0.75f), out var point),
                Is.EqualTo(MinimapMappingResult.Ok));
            Assert.That(point.X, Is.EqualTo(250));
            Assert.That(point.Z, Is.EqualTo(1500), "a non-square extent must not share one scale on both axes");
        }

        [TestCase(0f, 0f)]
        [TestCase(1f, 1f)]
        [TestCase(0.5f, 0.5f)]
        [TestCase(0.37f, 0.62f)]
        [TestCase(0.999f, 0.001f)]
        public void RadarPixelRoundTrip_IsAnExactInverse(float u, float v)
        {
            const int size = MinimapBlipGeometry.DefaultRadarGridSizePixels;
            var pixel = MinimapProjection.UvToPixel(new Vector2(u, v), size);

            Assert.That(pixel.x, Is.InRange(0, size - 1));
            Assert.That(pixel.y, Is.InRange(0, size - 1));

            var centre = MinimapProjection.PixelToUv(pixel, size);
            Assert.That(
                MinimapProjection.UvToPixel(centre, size),
                Is.EqualTo(pixel),
                "quantising an already-quantised position must not move it, or a blip would drift every frame");
        }

        [Test]
        public void RadarPixelMapping_PlacesTheCardinalPointsOnTheGrid()
        {
            const int size = 128;
            Assert.That(MinimapProjection.UvToPixel(Vector2.zero, size), Is.EqualTo(Vector2Int.zero));
            Assert.That(
                MinimapProjection.UvToPixel(Vector2.one, size),
                Is.EqualTo(new Vector2Int(size - 1, size - 1)),
                "UV 1 is the last column, not one past it");
            Assert.That(
                MinimapProjection.UvToPixel(new Vector2(0.5f, 0.5f), size),
                Is.EqualTo(new Vector2Int(64, 64)));
        }

        [TestCase(128, true)]
        [TestCase(64, true)]
        [TestCase(8, true)]
        [TestCase(1024, true)]
        [TestCase(7, false)]
        [TestCase(0, false)]
        [TestCase(-1, false)]
        [TestCase(1025, false)]
        public void RadarGridSize_IsValidatedBeforeItBecomesADivisor(int size, bool valid)
        {
            Assert.That(MinimapProjection.IsValidRadarSize(size), Is.EqualTo(valid));

            if (valid)
            {
                return;
            }

            // A rejected size has to answer with a defined value rather than NaN: an
            // unguarded (size - 1) at zero is a division by -1, and at 1 a division by 0.
            Assert.That(MinimapProjection.UvToPixel(new Vector2(0.5f, 0.5f), size), Is.EqualTo(Vector2Int.zero));
            Assert.That(MinimapProjection.PixelToUv(new Vector2Int(1, 1), size), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void TryScreenToUv_AcceptsTheCentreAndBothCornersOfTheWidget()
        {
            var rect = new Rect(100f, 200f, 256f, 256f);

            Assert.That(
                MinimapProjection.TryScreenToUv(rect, new Vector2(228f, 328f), out var centre),
                Is.EqualTo(MinimapMappingResult.Ok));
            Assert.That(centre, Is.EqualTo(new Vector2(0.5f, 0.5f)));

            Assert.That(
                MinimapProjection.TryScreenToUv(rect, new Vector2(100f, 200f), out var lowerLeft),
                Is.EqualTo(MinimapMappingResult.Ok));
            Assert.That(lowerLeft, Is.EqualTo(Vector2.zero));

            Assert.That(
                MinimapProjection.TryScreenToUv(rect, new Vector2(356f, 456f), out var upperRight),
                Is.EqualTo(MinimapMappingResult.Ok));
            Assert.That(upperRight, Is.EqualTo(Vector2.one),
                "the far edge of the widget is inside it, so it must not be a dead zone");
        }

        [Test]
        public void TryScreenToUv_RefusesEveryClickBesideTheWidget()
        {
            var rect = new Rect(100f, 200f, 256f, 256f);

            Assert.That(
                MinimapProjection.TryScreenToUv(rect, new Vector2(99.9f, 328f), out _),
                Is.EqualTo(MinimapMappingResult.OutsideRadar));
            Assert.That(
                MinimapProjection.TryScreenToUv(rect, new Vector2(356.1f, 328f), out _),
                Is.EqualTo(MinimapMappingResult.OutsideRadar));
            Assert.That(
                MinimapProjection.TryScreenToUv(rect, new Vector2(228f, 199.9f), out _),
                Is.EqualTo(MinimapMappingResult.OutsideRadar));
            Assert.That(
                MinimapProjection.TryScreenToUv(rect, new Vector2(228f, 456.1f), out _),
                Is.EqualTo(MinimapMappingResult.OutsideRadar));
        }

        [Test]
        public void TryScreenToUv_RefusesNonFiniteAndDegenerateInput()
        {
            var rect = new Rect(100f, 200f, 256f, 256f);

            Assert.That(
                MinimapProjection.TryScreenToUv(rect, new Vector2(float.NaN, 328f), out _),
                Is.EqualTo(MinimapMappingResult.NonFiniteInput));
            Assert.That(
                MinimapProjection.TryScreenToUv(rect, new Vector2(228f, float.PositiveInfinity), out _),
                Is.EqualTo(MinimapMappingResult.NonFiniteInput));
            Assert.That(
                MinimapProjection.TryScreenToUv(new Rect(10f, 10f, 0f, 100f), new Vector2(10f, 50f), out _),
                Is.EqualTo(MinimapMappingResult.OutsideRadar),
                "a zero-width widget cannot be clicked meaningfully");
            Assert.That(
                MinimapProjection.TryScreenToUv(new Rect(10f, 10f, float.NaN, 100f), new Vector2(10f, 50f), out _),
                Is.EqualTo(MinimapMappingResult.NonFiniteInput));
        }

        [Test]
        public void CameraGroundRect_DescribesTheGroundInsideTheFrustum()
        {
            var rig = Child("GroundRectRig");
            var camera = rig.AddComponent<Camera>();
            camera.pixelRect = new Rect(0f, 0f, 800f, 600f);
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 500f;
            camera.transform.position = new Vector3(0f, 45f, -35f);
            camera.transform.rotation = Quaternion.Euler(52f, 0f, 0f);

            Assert.That(
                MinimapProjection.TryCameraGroundRect(camera, out var ground),
                Is.True,
                "a pitched RTS view meets the ground at all four corners");
            Assert.That(ground.width, Is.GreaterThan(0f));
            Assert.That(ground.height, Is.GreaterThan(0f));
            Assert.That(ground.xMin, Is.LessThan(0f), "the view straddles the axis it looks down at");
            Assert.That(ground.xMax, Is.GreaterThan(0f));

            // The near edge must be nearer in world Z than the far edge, or the indicator
            // would be drawn upside down on a north-up radar.
            Assert.That(ground.yMin, Is.LessThan(ground.yMax));

            var uv = MinimapProjection.GroundRectToUv(MinimapBounds.Default, ground);
            Assert.That(uv.xMin, Is.GreaterThanOrEqualTo(0f));
            Assert.That(uv.xMax, Is.LessThanOrEqualTo(1f));
            Assert.That(uv.yMin, Is.GreaterThanOrEqualTo(0f));
            Assert.That(uv.yMax, Is.LessThanOrEqualTo(1f));
            Assert.That(uv.width, Is.GreaterThan(0f));
        }

        [Test]
        public void CameraGroundRect_RefusesAViewThatNeverMeetsTheGround()
        {
            var rig = Child("SkyRig");
            var camera = rig.AddComponent<Camera>();
            camera.pixelRect = new Rect(0f, 0f, 800f, 600f);
            camera.fieldOfView = 60f;
            camera.transform.position = new Vector3(0f, 45f, 0f);

            camera.transform.rotation = Quaternion.Euler(-30f, 0f, 0f);
            Assert.That(MinimapProjection.TryCameraGroundRect(camera, out _), Is.False,
                "pitched at the sky: a rectangle built from the corners that do hit would describe ground the player cannot see");

            camera.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
            Assert.That(MinimapProjection.TryCameraGroundRect(camera, out _), Is.False,
                "a horizontal view is parallel to the plane");

            Assert.That(MinimapProjection.TryCameraGroundRect(null, out _), Is.False);
        }

        // ======================================================= 2. cadence and blips

        [Test]
        public void Radar_AdvancingBelowTheTick_LeavesTheSnapshotAlone()
        {
            Spawn(1, Ax, RowZ);

            Assert.That(_radar.Advance(0.0), Is.True, "the first advance primes the radar");
            var refreshes = _radar.RefreshCount;
            var position = BlipFor(1).Uv;

            Assert.That(_radar.Advance(0.01), Is.False);
            Assert.That(_radar.Advance(0.02), Is.False);
            Assert.That(_radar.Advance(0.025), Is.False);
            Assert.That(
                _radar.RefreshCount,
                Is.EqualTo(refreshes),
                "OD-24: a frame between 30 Hz ticks must do no blip work at all");

            // A unit that appeared between ticks is still absent until the next tick, and
            // a unit that moved is still drawn where it was. That is the throttle, not a
            // defect: the next tick shows the new state.
            SpawnUnitOnly(2, 40_000, RowZ);
            Assert.That(_radar.Advance(0.03), Is.False);
            Assert.That(_radar.BlipCount, Is.EqualTo(1));
            Assert.That(BlipFor(1).Uv, Is.EqualTo(position));

            Assert.That(_radar.Advance(0.04), Is.True, "one tick later both changes land");
            Assert.That(_radar.BlipCount, Is.EqualTo(2));
        }

        [Test]
        public void Radar_AdvancingPastTheTick_RebuildsTheSnapshot()
        {
            Spawn(1, Ax, RowZ);
            _radar.Advance(0.0);
            var refreshes = _radar.RefreshCount;

            Assert.That(_radar.Advance(1.0 / 25.0), Is.True, "one frame past a thirtieth of a second");
            Assert.That(_radar.RefreshCount, Is.EqualTo(refreshes + 1));
        }

        [Test]
        public void Radar_IntervalIsTheThirtyHertzOd24Names()
        {
            Assert.That(MinimapRadarModel.DefaultUpdateIntervalSeconds, Is.EqualTo(1.0 / 30.0).Within(1e-12));
            Assert.That(_radar.UpdateIntervalSeconds, Is.EqualTo(MinimapRadarModel.DefaultUpdateIntervalSeconds));
        }

        [TestCase(0.0)]
        [TestCase(0.001)]
        [TestCase(5.0)]
        [TestCase(-1.0)]
        public void Radar_APoisonedIntervalFallsBackToThirtyHertz(double attempt)
        {
            _radar.UpdateIntervalSeconds = attempt;
            Assert.That(
                _radar.UpdateIntervalSeconds,
                Is.EqualTo(MinimapRadarModel.DefaultUpdateIntervalSeconds),
                "a settings field cannot turn the radar back into a per-frame cost");

            _radar.UpdateIntervalSeconds = double.NaN;
            Assert.That(
                _radar.UpdateIntervalSeconds,
                Is.EqualTo(MinimapRadarModel.DefaultUpdateIntervalSeconds),
                "a NaN interval freezes the radar outright, since now >= NaN is never true");
        }

        [Test]
        public void Radar_ClassifiesFriendlyEnemyAndSelectedDistinctly()
        {
            var friendlySlot = Spawn(1, Ax, RowZ);
            var enemySlot = Spawn(2, Bx, RowZ, owner: Enemy);
            var selectedSlot = Spawn(3, Cx, RowZ);

            Assert.That(_radar.Rebuild(), Is.EqualTo(3));

            Assert.That(BlipFor(1).Kind, Is.EqualTo(MinimapBlipKind.Friendly));
            Assert.That(
                BlipFor(2).Kind,
                Is.EqualTo(MinimapBlipKind.Enemy),
                "the player cannot command another player's units, so they must not share a colour");
            Assert.That(BlipFor(3).Kind, Is.EqualTo(MinimapBlipKind.Friendly));

            _selection.AddSelected(new EntityId(3), selectedSlot);
            Assert.That(_radar.Rebuild(), Is.EqualTo(3));
            Assert.That(BlipFor(3).Kind, Is.EqualTo(MinimapBlipKind.Selected));
            Assert.That(
                BlipFor(1).Kind,
                Is.EqualTo(MinimapBlipKind.Friendly),
                "selecting one unit must not recolour the squad");

            Assert.That(BlipFor(1).Slot, Is.EqualTo(friendlySlot));
            Assert.That(BlipFor(2).Slot, Is.EqualTo(enemySlot));
        }

        [Test]
        public void Radar_ExcludesDeadUnits()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ, owner: Enemy);
            Assert.That(_radar.Rebuild(), Is.EqualTo(2));

            SetHealth(1, 0);
            Assert.That(_radar.Rebuild(), Is.EqualTo(1), "a casualty is not a combatant");

            for (var index = 0; index < _radar.BlipCount; index++)
            {
                Assert.That(_radar.GetBlip(index).Entity, Is.EqualTo(2UL));
            }
        }

        [Test]
        public void Radar_ReclassifiesEverythingWhenTheLocalPlayerChanges()
        {
            Spawn(1, Ax, RowZ, owner: Local);
            Spawn(2, Bx, RowZ, owner: Enemy);

            _radar.Rebuild();
            Assert.That(BlipFor(1).Kind, Is.EqualTo(MinimapBlipKind.Friendly));
            Assert.That(BlipFor(2).Kind, Is.EqualTo(MinimapBlipKind.Enemy));

            // A reconnect hands the client a different PlayerId (ADR-008). A radar that
            // had cached the old one would keep painting the player's new army red.
            _selection.LocalPlayerId = new PlayerId(Enemy);
            _radar.Rebuild();
            Assert.That(BlipFor(1).Kind, Is.EqualTo(MinimapBlipKind.Enemy));
            Assert.That(BlipFor(2).Kind, Is.EqualTo(MinimapBlipKind.Friendly));
            Assert.That(_radar.LocalPlayerId, Is.EqualTo(new PlayerId(Enemy)));
        }

        [Test]
        public void Radar_PositionsEachBlipAtItsUnitsMappedUv()
        {
            Spawn(1, 100_000, -100_000);
            Spawn(2, -50_000, 25_000);
            _radar.Rebuild();

            Assert.That(
                BlipFor(1).Uv,
                Is.EqualTo(MinimapProjection.MillimetresToUv(MinimapBounds.Default, 100_000, -100_000)));
            Assert.That(
                BlipFor(2).Uv,
                Is.EqualTo(MinimapProjection.MillimetresToUv(MinimapBounds.Default, -50_000, 25_000)));
        }

        [Test]
        public void Radar_ClampsOffMapUnitsOntoTheWidgetInsteadOfThrowingThemOff()
        {
            Spawn(1, 300_000, -300_000);
            _radar.Rebuild();

            var blip = BlipFor(1);
            Assert.That(blip.Uv, Is.EqualTo(new Vector2(1f, 0f)), "an off-map unit pins to the corner");
            Assert.That(blip.Uv.x, Is.InRange(0f, 1f));
            Assert.That(blip.Uv.y, Is.InRange(0f, 1f));
        }

        [Test]
        public void Radar_BlipsAreInAscendingSlotOrder()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            Spawn(3, Cx, RowZ);
            _radar.Rebuild();

            var previous = -1;
            for (var index = 0; index < _radar.BlipCount; index++)
            {
                var slot = _radar.GetBlip(index).Slot;
                Assert.That(slot, Is.GreaterThan(previous), "the draw order must be the table's, not the map's");
                previous = slot;
            }
        }

        [Test]
        public void Radar_OutOfRangeBlipReadsAreInert()
        {
            Spawn(1, Ax, RowZ);
            _radar.Rebuild();

            Assert.That(_radar.GetBlip(0).Entity, Is.EqualTo(1UL));
            Assert.That(_radar.GetBlip(-1).Entity, Is.EqualTo(0UL));
            Assert.That(_radar.GetBlip(_radar.BlipCount).Entity, Is.EqualTo(0UL));
            Assert.That(
                _radar.GetBlip(9999).Slot,
                Is.EqualTo(MinimapRadarModel.NoSlot),
                "a read past the end must not name slot 0, which is a unit");
            Assert.That(
                _radar.GetBlip(9999).Kind,
                Is.EqualTo(MinimapBlipKind.Friendly),
                "an empty blip carries no classification of its own");
        }

        [Test]
        public void Radar_CountsBlipsItHadNoRoomFor()
        {
            var tiny = new MinimapRadarModel(_world, _selection, MinimapBounds.Default, 2);

            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            Spawn(3, Cx, RowZ);

            Assert.That(tiny.Rebuild(), Is.EqualTo(2));
            Assert.That(tiny.BlipCapacity, Is.EqualTo(2));
            Assert.That(
                tiny.SkippedBlipCount,
                Is.EqualTo(1),
                "a silently truncated radar is the worst possible answer, because it looks correct");
        }

        [Test]
        public void Radar_ClearBlipsEmptiesThePictureWithoutTouchingTheSchedule()
        {
            Spawn(1, Ax, RowZ);
            _radar.Rebuild();
            Assert.That(_radar.BlipCount, Is.EqualTo(1));

            var refreshes = _radar.RefreshCount;
            _radar.ClearBlips();

            Assert.That(_radar.BlipCount, Is.EqualTo(0));
            Assert.That(_radar.RefreshCount, Is.EqualTo(refreshes), "clearing is not a rebuild");
        }

        [Test]
        public void Radar_BackwardsClockReschedulesInsteadOfFreezing()
        {
            Spawn(1, Ax, RowZ);
            Assert.That(_radar.Advance(10.0), Is.True);

            // A resync (OD-20) can hand the client an earlier clock. Without the
            // reschedule the next tick would be eight seconds away and the radar blank.
            Assert.That(_radar.Advance(2.0), Is.True, "a clock that went backwards rebuilds now");
            Assert.That(_radar.Advance(2.01), Is.False);
            Assert.That(_radar.Advance(2.1), Is.True);
        }

        [Test]
        public void Radar_NonFiniteClockAdvancesNothing()
        {
            Spawn(1, Ax, RowZ);
            _radar.Advance(0.0);
            var refreshes = _radar.RefreshCount;

            Assert.That(_radar.Advance(double.NaN), Is.False);
            Assert.That(_radar.Advance(double.PositiveInfinity), Is.False);
            Assert.That(_radar.Advance(double.NegativeInfinity), Is.False);
            Assert.That(
                _radar.RefreshCount,
                Is.EqualTo(refreshes),
                "an unbounded comparison would rebuild every poisoned frame, i.e. never throttle");
        }

        [Test]
        public void Radar_CameraIndicatorFollowsTheMappedGroundRect()
        {
            Assert.That(_radar.HasCameraView, Is.False, "no indicator until a footprint is reported");

            _radar.SetCameraViewGroundRect(new Rect(-50f, -50f, 100f, 100f));
            Assert.That(_radar.HasCameraView, Is.True);

            var uv = _radar.NormalizedCameraView;
            Assert.That(uv.xMin, Is.EqualTo(0.375f).Within(1e-5f), "-50 m of a 400 m map is U 0.375");
            Assert.That(uv.xMax, Is.EqualTo(0.625f).Within(1e-5f));
            Assert.That(uv.yMin, Is.EqualTo(0.375f).Within(1e-5f));
            Assert.That(uv.yMax, Is.EqualTo(0.625f).Within(1e-5f));

            _radar.SetCameraViewGroundRect(new Rect(float.NaN, 0f, 10f, 10f));
            Assert.That(_radar.HasCameraView, Is.False, "a footprint that is not a number is no footprint");

            _radar.SetCameraViewGroundRect(new Rect(0f, 0f, 20f, 20f));
            Assert.That(_radar.HasCameraView, Is.True);
            _radar.ClearCameraView();
            Assert.That(_radar.HasCameraView, Is.False);
        }

        [Test]
        public void Radar_BoundsAreReplaceableAndBadOnesRefused()
        {
            Assert.That(_radar.TrySetBounds(new MinimapBounds(0, 0, 4000, 4000)), Is.True);
            Assert.That(_radar.Bounds.ExtentXmm, Is.EqualTo(4000));

            Assert.That(_radar.TrySetBounds(default), Is.False, "a zero extent divides everything");
            Assert.That(_radar.Bounds.ExtentXmm, Is.EqualTo(4000), "and the refusal leaves the old map in place");

            Spawn(1, 2000, 2000);
            _radar.Rebuild();
            Assert.That(
                BlipFor(1).Uv,
                Is.EqualTo(new Vector2(0.5f, 0.5f)),
                "the new bounds are the ones the blips are mapped through");
        }

        // ======================================================= 3. blip geometry

        [Test]
        public void BlipGeometry_EmitsOneQuadPerBlipWithTheKindsColour()
        {
            Spawn(1, 100_000, 0);
            Spawn(2, -100_000, 0, owner: Enemy);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _radar.Rebuild();

            var palette = MinimapBlipPalette.Default;
            var quads = new MinimapBlipQuad[_radar.BlipCapacity];
            var written = MinimapBlipGeometry.BuildQuads(
                _radar.Blips,
                new Rect(0f, 0f, 200f, 200f),
                quads,
                palette,
                3f);

            Assert.That(written, Is.EqualTo(_radar.BlipCount), "one quad per live unit, no per-unit object");

            var friendly = QuadFor(quads, written, 1);
            var enemy = QuadFor(quads, written, 2);
            Assert.That(friendly.Color, Is.EqualTo(palette.Selected), "entity 1 is selected");
            Assert.That(enemy.Color, Is.EqualTo(palette.Enemy));
            Assert.That(
                enemy.Center.x,
                Is.LessThan(friendly.Center.x),
                "an entity at -100 m sits left of one at +100 m");
        }

        [Test]
        public void BlipGeometry_PaletteColoursAreDistinctSoAKindCannotBeMistakenForAnother()
        {
            var palette = MinimapBlipPalette.Default;

            Assert.That(palette.ColorFor(MinimapBlipKind.Friendly), Is.Not.EqualTo(palette.ColorFor(MinimapBlipKind.Enemy)));
            Assert.That(palette.ColorFor(MinimapBlipKind.Selected), Is.Not.EqualTo(palette.ColorFor(MinimapBlipKind.Friendly)));
            Assert.That(palette.ColorFor(MinimapBlipKind.Selected), Is.Not.EqualTo(palette.ColorFor(MinimapBlipKind.Enemy)));
        }

        [Test]
        public void BlipGeometry_KeepsEdgeBlipsInsideTheWidget()
        {
            var blips = new[]
            {
                new MinimapBlip(Vector2.zero, MinimapBlipKind.Friendly, 0, 1UL),
                new MinimapBlip(Vector2.one, MinimapBlipKind.Enemy, 1, 2UL),
            };
            var quads = new MinimapBlipQuad[2];
            var rect = new Rect(0f, 0f, 128f, 128f);

            Assert.That(
                MinimapBlipGeometry.BuildQuads(blips, rect, quads, MinimapBlipPalette.Default, 5f),
                Is.EqualTo(2));

            Assert.That(quads[0].Center.x, Is.GreaterThanOrEqualTo(2.5f), "half a blip in from the border");
            Assert.That(quads[0].Center.y, Is.GreaterThanOrEqualTo(2.5f));
            Assert.That(quads[1].Center.x, Is.LessThanOrEqualTo(128f - 2.5f));
            Assert.That(quads[1].Center.y, Is.LessThanOrEqualTo(128f - 2.5f));
        }

        [Test]
        public void BlipGeometry_EmitsNothingForAWidgetWithNoArea()
        {
            var blips = new[] { new MinimapBlip(new Vector2(0.5f, 0.5f), MinimapBlipKind.Friendly, 0, 1UL) };
            var quads = new MinimapBlipQuad[1];

            Assert.That(
                MinimapBlipGeometry.BuildQuads(blips, new Rect(0f, 0f, 0f, 64f), quads, MinimapBlipPalette.Default),
                Is.EqualTo(0),
                "a collapsed widget has no pixel to put a blip on");
            Assert.That(
                MinimapBlipGeometry.BuildQuads(blips, new Rect(0f, 0f, float.NaN, 64f), quads, MinimapBlipPalette.Default),
                Is.EqualTo(0));
            Assert.That(
                MinimapBlipGeometry.BuildQuads(
                    blips,
                    new Rect(0f, 0f, 64f, 64f),
                    quads,
                    MinimapBlipPalette.Default,
                    3f,
                    0),
                Is.EqualTo(0),
                "an invalid grid size is a divide by minus one, not a finer radar");
        }

        [Test]
        public void BlipGeometry_ABlipIsNeverDegenerate()
        {
            var blips = new[] { new MinimapBlip(new Vector2(0.5f, 0.5f), MinimapBlipKind.Friendly, 0, 1UL) };
            var quads = new MinimapBlipQuad[1];

            MinimapBlipGeometry.BuildQuads(
                blips,
                new Rect(0f, 0f, 100f, 100f),
                quads,
                MinimapBlipPalette.Default,
                0f);

            Assert.That(
                quads[0].Extent,
                Is.EqualTo(new Vector2(0.5f, 0.5f)),
                "at size 0 every blip is four invisible vertices, and 400 of them cost as much as 400 visible ones");
        }

        [Test]
        public void BlipGeometry_SnapsToTheRadarGridTheBakedTerrainIsDrawnAt()
        {
            // Two units three millimetres apart are inside one cell of a 128-grid over
            // 400 m, so they share a pixel; two a tenth of the map apart do not.
            var blips = new[]
            {
                new MinimapBlip(
                    MinimapProjection.MillimetresToUv(MinimapBounds.Default, 0, 0),
                    MinimapBlipKind.Friendly,
                    0,
                    1UL),
                new MinimapBlip(
                    MinimapProjection.MillimetresToUv(MinimapBounds.Default, 3, 0),
                    MinimapBlipKind.Friendly,
                    1,
                    2UL),
                new MinimapBlip(
                    MinimapProjection.MillimetresToUv(MinimapBounds.Default, 40_000, 0),
                    MinimapBlipKind.Friendly,
                    2,
                    3UL),
            };
            var quads = new MinimapBlipQuad[3];

            MinimapBlipGeometry.BuildQuads(
                blips,
                new Rect(0f, 0f, 200f, 200f),
                quads,
                MinimapBlipPalette.Default,
                3f,
                128);

            Assert.That(quads[0].Center, Is.EqualTo(quads[1].Center), "one cell, one pixel");
            Assert.That(quads[2].Center.x, Is.GreaterThan(quads[0].Center.x));
        }

        [Test]
        public void RadarGraphic_PublishesOncePerRadarTickAndIsQuietInBetween()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            Assert.That(_radar.Rebuild(), Is.EqualTo(2));

            var graphic = Widget("RadarGraphic", Vector2.zero).gameObject.AddComponent<MinimapRadarGraphic>();
            graphic.FallbackWidgetRectPx = new Rect(0f, 0f, 200f, 200f);

            Assert.That(graphic.PublishFromModel(), Is.False, "no model, nothing to publish");

            graphic.SetModel(_radar, _radar.BlipCapacity);
            Assert.That(graphic.HasModel, Is.True);
            Assert.That(graphic.QuadCount, Is.EqualTo(2), "binding a model that has already rebuilt publishes at once");
            Assert.That(graphic.WidgetRectPx, Is.EqualTo(new Rect(0f, 0f, 200f, 200f)),
                "a widget the canvas has not laid out yet falls back to the rect its driver declared");

            Assert.That(graphic.PublishFromModel(), Is.False,
                "the same radar generation must not dirty the canvas batch twice");

            _radar.Rebuild();
            Assert.That(graphic.PublishFromModel(), Is.True, "a new generation does");
            Assert.That(graphic.PublishFromModel(), Is.False);
            Assert.That(graphic.QuadCount, Is.EqualTo(2));
        }

        [Test]
        public void RadarGraphic_UsesItsOwnLaidOutRectWhenThereIsOne()
        {
            Spawn(1, 0, 0);
            _radar.Rebuild();

            var widget = Widget("LaidOutRadar", new Vector2(300f, 300f));
            var graphic = widget.gameObject.AddComponent<MinimapRadarGraphic>();
            graphic.FallbackWidgetRectPx = new Rect(0f, 0f, 1f, 1f);
            graphic.SetModel(_radar, _radar.BlipCapacity);

            Assert.That(graphic.WidgetRectPx, Is.EqualTo(new Rect(0f, 0f, 300f, 300f)),
                "the canvas's own layout wins over the fallback, or resizing the widget would do nothing");
            Assert.That(graphic.QuadCount, Is.EqualTo(1));
        }

        // ================================================= 4. minimap click interactions

        [Test]
        public void MinimapLeftClick_JumpsTheCameraToTheGroundPointUnderThePixel()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            var selectionBefore = _selection.SelectedCount;

            var pixel = RadarPixelForMillimetres(-120_000, 80_000);
            Assert.That(harness.Interaction.HandleLeftPointerDown(pixel), Is.True);
            harness.CameraRig.ManualUpdate(1f);

            Assert.That(harness.CameraRig.FocusPoint.x, Is.EqualTo(-120f).Within(0.01f));
            Assert.That(harness.CameraRig.FocusPoint.z, Is.EqualTo(80f).Within(0.01f));
            Assert.That(harness.CameraRig.FocusPoint.y, Is.EqualTo(0f), "OD-27: the focus is on the ground plane");

            Assert.That(
                _selection.SelectedCount,
                Is.EqualTo(selectionBefore),
                "looking around the map is not an order to change the selection");
            Assert.That(_sink.Count, Is.EqualTo(0), "and the left button never commands");
        }

        [Test]
        public void MinimapLeftClick_WithoutJumpEnabled_PansInsteadOfSnapping()
        {
            var harness = BuildHarness();
            harness.CameraRig.SetFocusPoint(Vector3.zero, immediate: true);
            harness.Interaction.LeftClickJumpsCamera = false;

            Assert.That(
                harness.Interaction.HandleLeftPointerDown(RadarPixelForMillimetres(150_000, 0)),
                Is.True);

            Assert.That(harness.CameraRig.TargetFocusPoint.x, Is.EqualTo(150f).Within(0.01f));
            Assert.That(harness.CameraRig.FocusPoint.x, Is.EqualTo(0f), "a pan starts where the player was looking");

            harness.CameraRig.ManualUpdate(1f);
            Assert.That(harness.CameraRig.FocusPoint.x, Is.GreaterThan(1f), "and travels from there");
        }

        [Test]
        public void MinimapLeftDrag_KeepsPanningWhileTheButtonIsHeld()
        {
            var harness = BuildHarness();
            harness.CameraRig.SetFocusPoint(Vector3.zero, immediate: true);

            Assert.That(
                harness.Interaction.HandleLeftPointerDown(RadarPixelForMillimetres(-180_000, 0)),
                Is.True);
            Assert.That(harness.Interaction.IsDraggingCameraOnMinimap, Is.True);

            Assert.That(harness.Interaction.HandleLeftPointerDrag(RadarPixelForMillimetres(0, 0)), Is.True);
            Assert.That(harness.Interaction.LastCameraTargetMetres.x, Is.EqualTo(0f).Within(0.01f));

            // Travel beyond the widget is a drag that has left the radar: the view stays
            // where the last in-widget sample asked rather than snapping to an edge the
            // player never clicked.
            Assert.That(harness.Interaction.HandleLeftPointerDrag(new Vector2(500f, 500f)), Is.False);
            Assert.That(harness.Interaction.LastCameraTargetMetres.x, Is.EqualTo(0f).Within(0.01f));
            Assert.That(harness.Interaction.IsDraggingCameraOnMinimap, Is.True, "the drag is still the radar's");

            Assert.That(harness.Interaction.HandleLeftPointerUp(new Vector2(500f, 500f)), Is.False);
            Assert.That(harness.Interaction.IsDraggingCameraOnMinimap, Is.False);
            Assert.That(harness.Interaction.HandleLeftPointerDrag(RadarPixelForMillimetres(0, 0)), Is.False,
                "after the release there is nothing left to pan");
        }

        [Test]
        public void MinimapRightClick_SendsTheSelectionToTheMappedMillimetreDestination()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));
            harness.Interaction.RequestedTick = 777;

            Assert.That(
                harness.Interaction.HandleRightPointerUp(RadarPixelForMillimetres(-42_000, 91_000)),
                Is.True);

            Assert.That(_sink.Count, Is.EqualTo(1));
            var issued = _sink.Only;
            Assert.That(issued.Kind, Is.EqualTo(GameCommandType.Move));
            Assert.That(
                issued.Destination,
                Is.EqualTo(new WorldPointMm(-42_000, 91_000)),
                "the destination must be the exact point the pixel names, not a neighbour of it");
            Assert.That(issued.Header.RequestedTick, Is.EqualTo(777UL));
            Assert.That(issued.Header.Type, Is.EqualTo(GameCommandType.Move));
            Assert.That(issued.Entities.Length, Is.EqualTo(2));
            Assert.That(harness.Interaction.LastMoveDestinationMm, Is.EqualTo(new WorldPointMm(-42_000, 91_000)));
        }

        [Test]
        public void MinimapRightClick_WithNothingSelected_EmitsNoOrder()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ, owner: Enemy);

            Assert.That(harness.Interaction.HandleRightPointerUp(RadarPixelForMillimetres(0, 0)), Is.False);
            Assert.That(_sink.Count, Is.EqualTo(0), "an empty selection must not spend a command sequence");
        }

        [Test]
        public void MinimapRightClick_OutsideTheWidget_EmitsNoOrderAndLeavesTheCameraAlone()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            harness.CameraRig.SetFocusPoint(Vector3.zero, immediate: true);

            Assert.That(harness.Interaction.HandleRightPointerUp(new Vector2(4_000f, 4_000f)), Is.False);
            Assert.That(harness.Interaction.HandleLeftPointerDown(new Vector2(-1f, 50f)), Is.False);
            harness.CameraRig.ManualUpdate(1f);

            Assert.That(_sink.Count, Is.EqualTo(0));
            Assert.That(harness.CameraRig.FocusPoint.x, Is.EqualTo(0f));
            Assert.That(harness.CameraRig.FocusPoint.z, Is.EqualTo(0f));
        }

        [TestCase(0, 0)]
        [TestCase(137, -49_123)]
        [TestCase(-MapHalfMm, MapHalfMm)]
        [TestCase(MapHalfMm, -MapHalfMm)]
        public void MinimapClick_RoundTripsTheWidgetToTheMillimetrePoint(int xMm, int zMm)
        {
            var harness = BuildHarness();
            var pixel = RadarPixelForMillimetres(xMm, zMm);

            Assert.That(harness.Interaction.TryMapScreenToMillimetres(pixel, out var point), Is.True);
            Assert.That(point.X, Is.EqualTo(xMm));
            Assert.That(point.Z, Is.EqualTo(zMm));

            Assert.That(harness.Interaction.TryMapScreenToWorldMetres(pixel, out var world), Is.True);
            Assert.That(world.x, Is.EqualTo(xMm * 0.001f).Within(1e-4f));
            Assert.That(world.z, Is.EqualTo(zMm * 0.001f).Within(1e-4f));
        }

        [Test]
        public void MinimapClick_NonFinitePixelChangesNothing()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            harness.CameraRig.SetFocusPoint(new Vector3(10f, 0f, 10f), immediate: true);
            var focus = harness.CameraRig.FocusPoint;

            Assert.That(harness.Interaction.HandleLeftPointerDown(new Vector2(float.NaN, 100f)), Is.False);
            Assert.That(harness.Interaction.HandleRightPointerUp(new Vector2(100f, float.PositiveInfinity)), Is.False);
            Assert.That(harness.Interaction.HandleLeftPointerDown(new Vector2(100f, float.NegativeInfinity)), Is.False);
            harness.CameraRig.ManualUpdate(1f);

            Assert.That(harness.CameraRig.FocusPoint, Is.EqualTo(focus));
            Assert.That(_sink.Count, Is.EqualTo(0));
            Assert.That(harness.Interaction.IsDraggingCameraOnMinimap, Is.False);
            Assert.That(harness.Interaction.LastCameraTargetMetres, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void MinimapClick_BeforeARectangleIsDeclared_RefusesEveryButton()
        {
            var interaction = Child("UnboundMinimap").AddComponent<MinimapInteractionController>();
            interaction.Configure(_radar, null, _selection, _sink);

            Assert.That(interaction.HandleLeftPointerDown(new Vector2(50f, 50f)), Is.False);
            Assert.That(interaction.HandleRightPointerUp(new Vector2(50f, 50f)), Is.False);
            Assert.That(interaction.TryMapScreenToUv(new Vector2(50f, 50f), out _), Is.False);
            Assert.That(interaction.MinimapScreenRect, Is.EqualTo(default(Rect)));

            interaction.SetMinimapScreenRect(new Rect(0f, 0f, 0f, 0f));
            Assert.That(interaction.HandleLeftPointerDown(new Vector2(0f, 0f)), Is.False,
                "a collapsed widget is not a widget");
        }

        [Test]
        public void MinimapClick_RightClickDisabled_EmitsNothingButStillMaps()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            harness.Interaction.RightClickIssuesMove = false;
            var pixel = RadarPixelForMillimetres(0, 0);

            Assert.That(harness.Interaction.HandleRightPointerUp(pixel), Is.False);
            Assert.That(_sink.Count, Is.EqualTo(0));
            Assert.That(harness.Interaction.TryMapScreenToMillimetres(pixel, out _), Is.True,
                "the mapping stays available for whatever the binding becomes");
        }

        [Test]
        public void MinimapClick_PannedTargetIsClampedByTheCamerasOwnBounds()
        {
            var harness = BuildHarness();
            harness.CameraRig.MapBounds = new Rect(-50f, -50f, 100f, 100f);

            // A radar whose bounds are wider than the camera's clamp must not be able to
            // fly the view off the map the camera was told to stay inside.
            Assert.That(harness.Interaction.HandleLeftPointerDown(RadarPixelForMillimetres(190_000, 0)), Is.True);
            harness.CameraRig.ManualUpdate(1f);

            Assert.That(harness.CameraRig.TargetFocusPoint.x, Is.EqualTo(50f).Within(0.01f));
        }

        [Test]
        public void MinimapClick_WithNoCameraOrNoSinkChangesNothingAndThrowsNothing()
        {
            var interaction = Child("HeadlessMinimap").AddComponent<MinimapInteractionController>();
            interaction.Configure(_radar, null, _selection, null);
            interaction.SetMinimapScreenRect(RadarScreenRect);
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            // The mapping is the radar's own business, so it still answers; the two
            // effects it would have had are simply absent, and a HUD wired before its
            // camera or its session has to survive the frames in between.
            var pixel = RadarPixelForMillimetres(100_000, -100_000);
            Assert.That(interaction.HandleLeftPointerDown(pixel), Is.True);
            Assert.That(interaction.LastCameraTargetMetres.x, Is.EqualTo(100f).Within(0.01f),
                "the click was understood even though there was no camera to move");
            Assert.That(interaction.HandleRightPointerUp(pixel), Is.False, "and there was no sink to order through");
            Assert.That(_sink.Count, Is.EqualTo(0));
            Assert.DoesNotThrow(() => interaction.CancelDrag());
        }

        // ========================================================= 5. HUD pointer capture

        private sealed class DriverHarness
        {
            public UnitSelectionDriver Driver;
            public RtsInputManager Input;
            public SelectionMarqueePresenter Marquee;
        }

        private DriverHarness BuildDriver(TacticalHudPointerBlocker blocker)
        {
            var input = Component<RtsInputManager>("Input");
            input.SetMockMode(true);

            var marquee = Component<SelectionMarqueePresenter>("Marquee");
            Assert.That(marquee.Build(), Is.True);

            var driver = Component<UnitSelectionDriver>("Driver");
            driver.SetInputManager(input);
            driver.SetMarqueePresenter(marquee);
            driver.PointerProjector = _projector;
            driver.Configure(_selection, _sink);
            driver.SetHudBlocker(blocker);

            return new DriverHarness { Driver = driver, Input = input, Marquee = marquee };
        }

        /// <summary>A battlefield pixel that resolves to empty ground, far from every fixture unit.</summary>
        private static Vector2 EmptyGroundPixel() => new Vector2(700f, 500f);

        [Test]
        public void Driver_ClickInsideTheHudRect_DoesNotClearTheSelection()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            var blocker = new TacticalHudPointerBlocker();
            var harness = BuildDriver(blocker);

            var pixel = EmptyGroundPixel();
            blocker.AddScreenRect(new Rect(pixel.x - 40f, pixel.y - 40f, 80f, 80f));

            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));

            harness.Input.SetMockMousePosition(pixel);
            harness.Input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            harness.Driver.ManualUpdate(0.0);
            harness.Input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            harness.Driver.ManualUpdate(1.0 / 60.0);

            Assert.That(_selection.SelectedCount, Is.EqualTo(2),
                "OD-24: a click on the tactical HUD must not reach the battlefield behind it");

            // The same release with the gate removed does clear, which is what makes the
            // assertion above a statement about the gate rather than about nothing.
            harness.Driver.SetHudBlocker(null);
            harness.Input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            harness.Driver.ManualUpdate(2.0 / 60.0);
            harness.Input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            harness.Driver.ManualUpdate(3.0 / 60.0);
            Assert.That(_selection.SelectedCount, Is.EqualTo(0));
        }

        [Test]
        public void Driver_PressInsideTheHudRect_ArmsNoMarquee()
        {
            Spawn(1, Ax, RowZ);
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(new Rect(0f, 0f, 200f, 200f));
            var harness = BuildDriver(blocker);

            harness.Input.SetMockMousePosition(new Vector2(100f, 100f));
            harness.Input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            harness.Driver.ManualUpdate(0.0);

            // Travel well past the 6 pixel threshold, still inside the panel.
            harness.Input.SetMockMousePosition(new Vector2(190f, 30f));
            harness.Driver.ManualUpdate(1.0 / 60.0);

            Assert.That(_selection.IsDragging, Is.False, "a drag started on the minimap is not a battlefield drag");
            Assert.That(harness.Marquee.IsVisible, Is.False, "and it draws no box");

            harness.Input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            harness.Driver.ManualUpdate(2.0 / 60.0);
            Assert.That(_selection.SelectedCount, Is.EqualTo(0), "and its release selects nothing");
        }

        [Test]
        public void Driver_DragFromTheBattlefieldIntoTheHud_IsAbandonedAndKeepsTheSelection()
        {
            Spawn(1, Ax, RowZ);
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(new Rect(0f, 0f, 200f, 200f));
            var harness = BuildDriver(blocker);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            harness.Input.SetMockMousePosition(EmptyGroundPixel());
            harness.Input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            harness.Driver.ManualUpdate(0.0);

            // Mock input holds whatever it was last told, unlike a real button edge that
            // reports pressed for exactly one frame. Leaving down: true would re-arm the
            // press at the new pixel and the drag would never measure any travel.
            harness.Input.SetMockMouseButton(0, down: false, pressed: true, up: false);
            harness.Input.SetMockMousePosition(new Vector2(400f, 300f));
            harness.Driver.ManualUpdate(1.0 / 60.0);
            Assert.That(_selection.IsDragging, Is.True, "the drag is genuinely in flight before it reaches the panel");
            Assert.That(harness.Marquee.IsVisible, Is.True);

            harness.Input.SetMockMousePosition(new Vector2(100f, 100f));
            harness.Driver.ManualUpdate(2.0 / 60.0);

            Assert.That(_selection.IsDragging, Is.False, "travelling onto the panel ends the battlefield drag");
            Assert.That(harness.Marquee.IsVisible, Is.False);
            Assert.That(
                _selection.SelectedCount,
                Is.EqualTo(1),
                "and the squad the player had already selected survives a mouse that drifted");

            harness.Input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            harness.Driver.ManualUpdate(3.0 / 60.0);
            Assert.That(_selection.SelectedCount, Is.EqualTo(1), "so does its release");
        }

        [Test]
        public void Driver_ReleaseOnTheHudAfterABattlefieldPressLeavesNoArmedDrag()
        {
            Spawn(1, Ax, RowZ);
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(new Rect(0f, 0f, 200f, 200f));
            var harness = BuildDriver(blocker);

            // The pointer teleports onto the panel inside a single frame, so no drag is
            // ever visible to cancel on IsDragging alone. The release still has to be
            // swallowed and the press disarmed.
            harness.Input.SetMockMousePosition(EmptyGroundPixel());
            harness.Input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            harness.Driver.ManualUpdate(0.0);

            harness.Input.SetMockMousePosition(new Vector2(100f, 100f));
            harness.Input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            harness.Driver.ManualUpdate(1.0 / 60.0);

            harness.Input.SetMockMousePosition(EmptyGroundPixel());
            harness.Driver.ManualUpdate(2.0 / 60.0);
            Assert.That(_selection.IsDragging, Is.False, "nothing carried over from the swallowed release");
        }

        [Test]
        public void Driver_PressOnTheHudThenReleaseOnAUnit_SelectsNothing()
        {
            Spawn(1, Ax, RowZ);
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(new Rect(0f, 0f, 200f, 200f));
            var harness = BuildDriver(blocker);

            var unitPixel = TestProjector.ScreenForMetres(Ax * 0.001f, RowZ * 0.001f);
            harness.Input.SetMockMousePosition(new Vector2(100f, 100f));
            harness.Input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            harness.Driver.ManualUpdate(0.0);

            harness.Input.SetMockMousePosition(unitPixel);
            harness.Input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            harness.Driver.ManualUpdate(1.0 / 60.0);

            Assert.That(_selection.SelectedCount, Is.EqualTo(0),
                "the press never armed, so a release over a real unit is not its click");
        }

        [Test]
        public void Driver_RightClickInsideTheHudIssuesNoBattlefieldOrder()
        {
            Spawn(1, Ax, RowZ);
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(new Rect(0f, 0f, 200f, 200f));
            var harness = BuildDriver(blocker);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            harness.Input.SetMockMousePosition(new Vector2(100f, 100f));
            harness.Input.SetMockMouseButton(1, down: true, pressed: true, up: true);
            harness.Driver.ManualUpdate(0.0);
            Assert.That(_sink.Count, Is.EqualTo(0),
                "inside its own rectangle the minimap owns the right button, and the destination it names");

            harness.Input.SetMockMousePosition(TestProjector.ScreenForMetres(20f, 20f));
            harness.Input.SetMockMouseButton(1, down: false, pressed: false, up: true);
            harness.Driver.ManualUpdate(1.0 / 60.0);
            Assert.That(_sink.Count, Is.EqualTo(1), "outside the panel the battlefield still answers");
        }

        [Test]
        public void Driver_StopHotkeyStillWorksWhileThePointerIsOnTheHud()
        {
            Spawn(1, Ax, RowZ);
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(new Rect(0f, 0f, 200f, 200f));
            var harness = BuildDriver(blocker);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            harness.Input.SetMockMousePosition(new Vector2(100f, 100f));
            harness.Input.SetMockStopRequested(true);
            harness.Driver.ManualUpdate(0.0);

            Assert.That(_sink.Count, Is.EqualTo(1));
            Assert.That(
                _sink.Only.Kind,
                Is.EqualTo(GameCommandType.Stop),
                "a keyboard order is not a pointer order, so the pointer gate must not swallow it");
        }

        [Test]
        public void Driver_WithoutABlockerBehavesExactlyAsStepThreeFive()
        {
            Spawn(1, Ax, RowZ);
            var harness = BuildDriver(null);
            Assert.That(harness.Driver.HudBlocker, Is.Null);

            _selection.AddSelected(new EntityId(1), SlotOf(1));
            harness.Input.SetMockMousePosition(EmptyGroundPixel());
            harness.Input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            harness.Driver.ManualUpdate(0.0);
            harness.Input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            harness.Driver.ManualUpdate(1.0 / 60.0);

            Assert.That(_selection.SelectedCount, Is.EqualTo(0),
                "the gate is opt-in, and a build with no minimap loses none of step 3.5");
        }

        [Test]
        public void Driver_HoverIsClearedWhileThePointerIsOnTheHud()
        {
            Spawn(1, Ax, RowZ);
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(new Rect(0f, 0f, 200f, 200f));
            var harness = BuildDriver(blocker);

            var unitPixel = TestProjector.ScreenForMetres(Ax * 0.001f, RowZ * 0.001f);
            harness.Input.SetMockMousePosition(unitPixel);
            harness.Driver.ManualUpdate(0.0);
            Assert.That(_selection.HasHoveredUnit, Is.True);

            harness.Input.SetMockMousePosition(new Vector2(100f, 100f));
            harness.Driver.ManualUpdate(1.0 / 60.0);
            Assert.That(
                _selection.HasHoveredUnit,
                Is.False,
                "a unit behind the panel is not a unit the pointer is over");
        }

        [Test]
        public void Driver_SelectionIsStillPrunedWhileThePointerIsOnTheHud()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(new Rect(0f, 0f, 200f, 200f));
            var harness = BuildDriver(blocker);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));

            harness.Input.SetMockMousePosition(new Vector2(100f, 100f));
            harness.Driver.ManualUpdate(0.0);

            SetHealth(1, 0);
            harness.Driver.ManualUpdate(1.0 / 60.0);

            Assert.That(
                _selection.SelectedCount,
                Is.EqualTo(1),
                "a squad does not stop dying because the mouse is on the radar");
        }

        [Test]
        public void Blocker_ReportsOnlyInsideItsRectangles()
        {
            var blocker = new TacticalHudPointerBlocker();
            var handle = blocker.AddScreenRect(new Rect(10f, 20f, 30f, 40f));

            Assert.That(handle, Is.GreaterThanOrEqualTo(0));
            Assert.That(blocker.UsedCount, Is.EqualTo(1));
            Assert.That(blocker.BlockingCount, Is.EqualTo(1));
            Assert.That(blocker.IsEmpty, Is.False);
            Assert.That(blocker.IsPointerOverHud(new Vector2(10f, 20f)), Is.True, "inclusive on the lower corner");
            Assert.That(blocker.IsPointerOverHud(new Vector2(40f, 60f)), Is.True, "inclusive on the upper corner");
            Assert.That(blocker.IsPointerOverHud(new Vector2(9.9f, 20f)), Is.False);
            Assert.That(blocker.IsPointerOverHud(new Vector2(40.1f, 60f)), Is.False);
            Assert.That(blocker.GetScreenRect(handle), Is.EqualTo(new Rect(10f, 20f, 30f, 40f)));
        }

        [Test]
        public void Blocker_NonFinitePointerIsNeverOverHud()
        {
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(new Rect(0f, 0f, 4_000f, 4_000f));

            Assert.That(
                blocker.IsPointerOverHud(new Vector2(float.NaN, 0f)),
                Is.False,
                "treating NaN as HUD would silently disable the battlefield for whatever produced it");
            Assert.That(blocker.IsPointerOverHud(new Vector2(0f, float.PositiveInfinity)), Is.False);
        }

        [Test]
        public void Blocker_DegenerateAndReleasedRectanglesStopBlocking()
        {
            var blocker = new TacticalHudPointerBlocker();
            var zeroWidth = blocker.AddScreenRect(new Rect(50f, 50f, 0f, 40f));
            var negative = blocker.AddScreenRect(new Rect(50f, 50f, -30f, 40f));

            Assert.That(blocker.BlockingCount, Is.EqualTo(0),
                "a rect with no area covers nothing, however Unity derives its min and max");
            Assert.That(blocker.IsPointerOverHud(new Vector2(50f, 60f)), Is.False);

            var handle = blocker.AddScreenRect(new Rect(0f, 0f, 10f, 10f));
            Assert.That(blocker.BlockingCount, Is.EqualTo(1));
            Assert.That(blocker.Remove(handle), Is.True);
            Assert.That(blocker.BlockingCount, Is.EqualTo(0));
            Assert.That(blocker.IsPointerOverHud(new Vector2(5f, 5f)), Is.False);
            Assert.That(blocker.Remove(handle), Is.False, "a handle is not reusable by its owner");
            Assert.That(blocker.UsedCount, Is.EqualTo(2), "used slots count what is registered, blocking or not");

            Assert.That(blocker.Remove(zeroWidth), Is.True);
            Assert.That(blocker.Remove(negative), Is.True);
            Assert.That(blocker.UsedCount, Is.EqualTo(0));
            Assert.That(blocker.IsEmpty, Is.True);
        }

        [Test]
        public void Blocker_FullTableRefusesNewRectanglesRatherThanGrowing()
        {
            var blocker = new TacticalHudPointerBlocker(2);
            Assert.That(blocker.Capacity, Is.EqualTo(2));

            Assert.That(blocker.AddScreenRect(new Rect(0f, 0f, 1f, 1f)), Is.GreaterThanOrEqualTo(0));
            Assert.That(blocker.AddScreenRect(new Rect(2f, 2f, 1f, 1f)), Is.GreaterThanOrEqualTo(0));
            Assert.That(
                blocker.AddScreenRect(new Rect(4f, 4f, 1f, 1f)),
                Is.EqualTo(-1),
                "the gate is a fixed handful of panels, not a per-frame registration point");

            Assert.Throws<ArgumentOutOfRangeException>(() => new TacticalHudPointerBlocker(0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TacticalHudPointerBlocker(TacticalHudPointerBlocker.MaxCapacity + 1));
        }

        [Test]
        public void Blocker_ReleasedSlotsAreReusedBeforeTheCeiling()
        {
            var blocker = new TacticalHudPointerBlocker(2);
            var first = blocker.AddScreenRect(new Rect(0f, 0f, 4f, 4f));
            Assert.That(blocker.Remove(first), Is.True);

            var second = blocker.AddScreenRect(new Rect(8f, 8f, 4f, 4f));
            Assert.That(second, Is.EqualTo(first), "a HUD that rebuilds one panel must not walk to the ceiling");
            Assert.That(blocker.UsedCount, Is.EqualTo(1));
        }

        [Test]
        public void Blocker_RectTransformSourceStopsBlockingWhenHidden()
        {
            var rect = Widget("Panel", new Vector2(100f, 100f));
            var blocker = new TacticalHudPointerBlocker();
            var handle = blocker.AddRectTransform(rect);

            Assert.That(handle, Is.GreaterThanOrEqualTo(0));
            blocker.Refresh();
            Assert.That(blocker.BlockingCount, Is.EqualTo(1));

            rect.gameObject.SetActive(false);
            blocker.Refresh();
            Assert.That(
                blocker.BlockingCount,
                Is.EqualTo(0),
                "the selection panel is empty most of the time, and an invisible rectangle must not swallow clicks");

            rect.gameObject.SetActive(true);
            blocker.Refresh();
            Assert.That(blocker.BlockingCount, Is.EqualTo(1), "and it comes back when the panel does");
        }

        [Test]
        public void Blocker_RectTransformSourceFollowsAResize()
        {
            var rect = Widget("Panel", new Vector2(10f, 10f));
            var blocker = new TacticalHudPointerBlocker();
            blocker.AddRectTransform(rect);
            blocker.Refresh();

            Assert.That(blocker.IsPointerOverHud(new Vector2(50f, 50f)), Is.False);

            rect.sizeDelta = new Vector2(200f, 200f);
            blocker.Refresh();
            Assert.That(blocker.IsPointerOverHud(new Vector2(50f, 50f)), Is.True,
                "the gate has to block what is on screen, not what was on screen at build time");
        }

        [Test]
        public void Blocker_TransformBackedEntryRefusesAnExplicitRectangle()
        {
            var rect = Widget("Panel", new Vector2(10f, 10f));
            var blocker = new TacticalHudPointerBlocker();
            var transformHandle = blocker.AddRectTransform(rect);
            var explicitHandle = blocker.AddScreenRect(new Rect(0f, 0f, 5f, 5f));

            Assert.That(blocker.SetScreenRect(explicitHandle, new Rect(1f, 1f, 5f, 5f)), Is.True);
            Assert.That(
                blocker.SetScreenRect(transformHandle, new Rect(1f, 1f, 5f, 5f)),
                Is.False,
                "overwriting a derived rect would be undone by the next Refresh anyway");
            Assert.That(blocker.SetScreenRect(99, new Rect(0f, 0f, 5f, 5f)), Is.False);
        }

        [Test]
        public void Blocker_ANullSourceIsNotRegistered()
        {
            var blocker = new TacticalHudPointerBlocker();
            Assert.That(blocker.AddRectTransform(null), Is.EqualTo(-1));
            Assert.That(blocker.UsedCount, Is.EqualTo(0));
        }

        // ================================================ 6. dynamic selection HUD state

        [Test]
        public void HudState_OneUnit_ReportsCountKindAndHealth()
        {
            Spawn(1, Ax, RowZ, kind: UnitKinds.Scout);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            var state = new SelectionHudState(_selection, _world);
            state.Update();

            Assert.That(state.SelectedCount, Is.EqualTo(1));
            Assert.That(state.GetKindCount(UnitKinds.Scout), Is.EqualTo(1));
            Assert.That(state.GetKindCount(UnitKinds.Tank), Is.EqualTo(0));
            Assert.That(state.HealthPercent, Is.EqualTo(100));
            Assert.That(state.CountText, Is.EqualTo("1"));
            Assert.That(state.HealthText, Is.EqualTo("100%"));
            Assert.That(state.KindName(UnitKinds.Scout), Is.EqualTo("Scout"));
            Assert.That(state.PlayerText, Is.EqualTo("1"));
        }

        [Test]
        public void HudState_MixedKinds_CountEachArchetypeSeparately()
        {
            Spawn(1, Ax, RowZ, kind: UnitKinds.Scout);
            Spawn(2, Bx, RowZ, kind: UnitKinds.Scout);
            Spawn(3, Cx, RowZ, kind: UnitKinds.Tank);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));
            _selection.AddSelected(new EntityId(3), SlotOf(3));

            var state = new SelectionHudState(_selection, _world);
            state.Update();

            Assert.That(state.SelectedCount, Is.EqualTo(3));
            Assert.That(state.GetKindCount(UnitKinds.Scout), Is.EqualTo(2));
            Assert.That(state.GetKindCount(UnitKinds.Tank), Is.EqualTo(1));
            Assert.That(state.KindCountText(UnitKinds.Scout), Is.EqualTo("2"));
            Assert.That(state.KindCountText(UnitKinds.BaseStructure), Is.EqualTo("0"));
            Assert.That(state.KindName(UnitKinds.Tank), Is.EqualTo("Tank"));
        }

        [Test]
        public void HudState_TakesDamageAndResetsOnClear()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            var state = new SelectionHudState(_selection, _world);

            _selection.AddSelected(new EntityId(1), SlotOf(1));
            state.Update();
            Assert.That(state.HealthPercent, Is.EqualTo(100));

            SetHealth(1, 20);
            _selection.PruneStaleSelection();
            state.Update();
            Assert.That(state.HealthPercent, Is.EqualTo(20));

            _selection.AddSelected(new EntityId(2), SlotOf(2));
            state.Update();
            Assert.That(state.HealthPercent, Is.EqualTo(60), "the mean of 20 and 100 over the selection");

            _selection.ClearSelection();
            state.Update();
            Assert.That(state.SelectedCount, Is.EqualTo(0));
            Assert.That(state.HealthPercent, Is.EqualTo(-1), "nothing selected, so nothing to read out");
            Assert.That(state.HealthText, Is.EqualTo(HudStringTable.NotAvailable));
            Assert.That(state.CountText, Is.EqualTo("0"));
            Assert.That(state.GetKindCount(UnitKinds.Scout), Is.EqualTo(0));
        }

        [Test]
        public void HudState_DeadUnitsLeaveTheSelectionAndTheCount()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));

            var state = new SelectionHudState(_selection, _world);
            state.Update();
            Assert.That(state.SelectedCount, Is.EqualTo(2));

            SetHealth(1, 0);
            _selection.PruneStaleSelection();
            state.Update();

            Assert.That(state.SelectedCount, Is.EqualTo(1), "the casualty drops out of both the set and the panel");
            Assert.That(state.HealthPercent, Is.EqualTo(100));
        }

        [Test]
        public void HudState_UnresolvableArchetypeReportsNotAvailableRatherThanZero()
        {
            Spawn(1, Ax, RowZ, kind: UnitKinds.Unknown);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            var state = new SelectionHudState(_selection, _world);
            state.Update();

            Assert.That(state.SelectedCount, Is.EqualTo(1));
            Assert.That(
                state.HealthPercent,
                Is.EqualTo(-1),
                "no stats is not the same claim as zero hit points, and an empty bar on an unknown unit is a lie");
            Assert.That(state.HealthText, Is.EqualTo(HudStringTable.NotAvailable));
            Assert.That(state.KindName(UnitKinds.Unknown), Is.EqualTo(HudStringTable.NotAvailable));
        }

        [Test]
        public void HudState_CommandFeedbackNamesTheOrderAndItsOutcome()
        {
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            var channel = new ScriptedCommandChannel(MatchCommandRejection.NotEntityOwner);
            var sink = new UnitCommandChannelSink(channel);
            var state = new SelectionHudState(_selection, _world) { CommandFeedback = sink };

            state.Update();
            Assert.That(
                state.CommandText,
                Is.EqualTo(HudStringTable.NotAvailable),
                "a panel that opens reading Accepted reports a result nobody asked for");
            Assert.That(state.RejectionText, Is.EqualTo(TacticalHudText.NoCommand));
            Assert.That(state.SubmittedCommandText, Is.EqualTo(HudStringTable.NotAvailable));

            Assert.That(_selection.IssueStop(5, sink), Is.True);
            state.Update();
            Assert.That(state.LastCommandKind, Is.EqualTo(GameCommandType.Stop));
            Assert.That(state.CommandText, Is.EqualTo("Stop"));
            Assert.That(state.RejectionText, Is.EqualTo("Not your unit"));
            Assert.That(state.RejectedCommandCount, Is.EqualTo(1));
            Assert.That(state.SubmittedCommandCount, Is.EqualTo(0), "the channel refused it");

            channel.Rejection = MatchCommandRejection.None;
            Assert.That(_selection.IssueStop(6, sink), Is.True);
            state.Update();
            Assert.That(state.RejectionText, Is.EqualTo("Accepted"));
            Assert.That(state.SubmittedCommandCount, Is.EqualTo(1));
            Assert.That(
                state.RejectedCommandCount,
                Is.EqualTo(1),
                "an accepted order does not undo the earlier refusal");
        }

        [Test]
        public void HudState_WithoutASinkReportsNoCommandFeedback()
        {
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            var state = new SelectionHudState(_selection, _world);
            state.Update();

            Assert.That(state.CommandText, Is.EqualTo(HudStringTable.NotAvailable));
            Assert.That(state.SubmittedCommandCount, Is.EqualTo(0));
            Assert.That(state.LastCommandKind, Is.EqualTo(GameCommandType.None));
        }

        [Test]
        public void HudText_NamesEveryCommandAndRejectionFromATable()
        {
            Assert.That(TacticalHudText.ForCommand(GameCommandType.Move), Is.EqualTo("Move"));
            Assert.That(TacticalHudText.ForCommand(GameCommandType.AttackMove), Is.EqualTo("Attack move"));
            Assert.That(TacticalHudText.ForCommand(GameCommandType.Stop), Is.EqualTo("Stop"));
            Assert.That(
                TacticalHudText.ForRejection(MatchCommandRejection.FormationOutOfBounds, true),
                Is.EqualTo("Formation off map"));

            // An enum member added by a later phase must fall through to a named entry
            // rather than throw at the one place the HUD is not allowed to fail.
            Assert.That(TacticalHudText.ForCommand((GameCommandType)99), Is.EqualTo("Unrecognised"));
            Assert.That(TacticalHudText.ForRejection((MatchCommandRejection)250, true), Is.EqualTo("Unrecognised"));
            Assert.That(TacticalHudText.ForRejection(MatchCommandRejection.None, false),
                Is.EqualTo(TacticalHudText.NoCommand));
        }

        [Test]
        public void HudStringTable_SaturatesRatherThanAllocatingAboveItsRange()
        {
            Assert.That(HudStringTable.Count(0), Is.EqualTo("0"));
            Assert.That(HudStringTable.Count(1), Is.EqualTo("1"));
            Assert.That(HudStringTable.Count(HudStringTable.MaxCachedCount), Is.EqualTo("512"));
            Assert.That(
                HudStringTable.Count(HudStringTable.MaxCachedCount + 1),
                Is.EqualTo(HudStringTable.SaturatedCount));
            Assert.That(HudStringTable.Count(-5), Is.EqualTo("0"), "a negative count has no display of its own");

            Assert.That(HudStringTable.Percent(0), Is.EqualTo("0%"));
            Assert.That(HudStringTable.Percent(100), Is.EqualTo("100%"));
            Assert.That(HudStringTable.Percent(140), Is.EqualTo("100%"));
            Assert.That(HudStringTable.Health(-1), Is.EqualTo(HudStringTable.NotAvailable));
            Assert.That(HudStringTable.Health(0), Is.EqualTo("0%"));

            // The whole point of the table: a repeated value hands back the same
            // reference, because the presenter decides by reference.
            Assert.That(ReferenceEquals(HudStringTable.Count(37), HudStringTable.Count(37)), Is.True);
            Assert.That(ReferenceEquals(HudStringTable.Percent(37), HudStringTable.Percent(37)), Is.True);
            Assert.That(
                HudStringTable.Count(HudStringTable.MaxCachedCount + 1),
                Is.SameAs(HudStringTable.Count(int.MaxValue)),
                "saturation is a shared sentinel, not a format call");
        }

        // ======================================================= 7. the two HUD canvases

        [Test]
        public void Od24_TheThreeHudLayersAreThreeSeparateCanvases()
        {
            var harness = BuildHarness();
            var marquee = Component<SelectionMarqueePresenter>("Marquee");
            Assert.That(marquee.Build(), Is.True);

            var canvases = _root.GetComponentsInChildren<Canvas>(true);
            Assert.That(canvases.Length, Is.EqualTo(3), "OD-24 fixes the runtime HUD at three canvases");

            var orders = new HashSet<int>();
            var objects = new HashSet<GameObject>();
            foreach (var canvas in canvases)
            {
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                Assert.That(orders.Add(canvas.sortingOrder), Is.True, "no two layers may share an order");
                Assert.That(objects.Add(canvas.gameObject), Is.True, "and none may share a canvas");
            }

            Assert.That(orders, Does.Contain(SelectionMarqueePresenter.DefaultSortingOrder));
            Assert.That(orders, Does.Contain(PermanentHudPresenter.DefaultSortingOrder));
            Assert.That(orders, Does.Contain(PermanentHudPresenter.SelectionCanvasSortingOrder));
            Assert.That(
                SelectionMarqueePresenter.DefaultSortingOrder,
                Is.GreaterThan(PermanentHudPresenter.SelectionCanvasSortingOrder),
                "the marquee draws over the panel it is about to change");
        }

        [Test]
        public void Od24_TheRadarRebuildDoesNotDirtyTheSelectionPanel()
        {
            // The reason there are three canvases rather than one, stated as a test: a
            // radar tick re-tessellates its own graphic and nothing else.
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            harness.Dynamic.ManualUpdate();
            harness.Dynamic.ManualUpdate();

            var panelMutations = harness.Dynamic.TextMutationCount;
            for (var frame = 0; frame < 30; frame++)
            {
                harness.Permanent.ManualUpdate(frame / 60.0);
            }

            Assert.That(_radar.RefreshCount, Is.GreaterThan(1), "the radar did tick");
            Assert.That(harness.Permanent.Radar.QuadCount, Is.EqualTo(1));
            Assert.That(
                harness.Dynamic.TextMutationCount,
                Is.EqualTo(panelMutations),
                "and the dynamic panel still wrote nothing");
        }

        [Test]
        public void PermanentHud_BuildIsIdempotentAndRegistersItsPanels()
        {
            var harness = BuildHarness();

            Assert.That(harness.Permanent.IsBuilt, Is.True);
            var canvasesBefore = _root.GetComponentsInChildren<Canvas>(true).Length;
            var childrenBefore = harness.Permanent.transform.childCount;

            Assert.That(harness.Permanent.Build(), Is.True);
            Assert.That(
                _root.GetComponentsInChildren<Canvas>(true).Length,
                Is.EqualTo(canvasesBefore),
                "a second build must not stack a second HUD");
            Assert.That(harness.Permanent.transform.childCount, Is.EqualTo(childrenBefore));

            Assert.That(harness.Permanent.Radar, Is.Not.Null);
            Assert.That(harness.Permanent.MinimapTransform, Is.Not.Null);
            Assert.That(harness.Permanent.Radar.transform.parent, Is.EqualTo(harness.Permanent.MinimapTransform));

            // Minimap and command bar for the permanent canvas, selection panel for the
            // dynamic one. Fewer means a panel registered nothing.
            Assert.That(harness.Blocker.UsedCount, Is.EqualTo(3));
            Assert.That(harness.Blocker.BlockingCount, Is.EqualTo(3));
        }

        [Test]
        public void PermanentHud_ReleaseFreesTheCanvasAndTheBlockingRects()
        {
            var harness = BuildHarness();
            Assert.That(harness.Blocker.BlockingCount, Is.GreaterThan(0));

            harness.Permanent.Release();
            harness.Dynamic.Release();

            Assert.That(harness.Permanent.IsBuilt, Is.False);
            Assert.That(harness.Dynamic.IsBuilt, Is.False);
            Assert.That(
                harness.Blocker.UsedCount,
                Is.EqualTo(0),
                "a leaked rectangle leaves an invisible strip of the battlefield unclickable for the rest of the match");
            Assert.That(harness.Permanent.TryGetMinimapScreenRect(out _), Is.False);
        }

        [Test]
        public void PermanentHud_StopButtonIssuesStopThroughTheSink()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));
            harness.Permanent.RequestedTick = 4242;

            harness.Permanent.StopButton.onClick.Invoke();

            Assert.That(_sink.Count, Is.EqualTo(1));
            Assert.That(_sink.Only.Kind, Is.EqualTo(GameCommandType.Stop));
            Assert.That(_sink.Only.Header.RequestedTick, Is.EqualTo(4242UL));
            Assert.That(_sink.Only.Entities.Length, Is.EqualTo(2));
        }

        [Test]
        public void PermanentHud_StopWithNothingSelectedIssuesNothing()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);

            Assert.That(harness.Permanent.TryIssueStop(), Is.False);
            harness.Permanent.StopButton.onClick.Invoke();

            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void PermanentHud_BeforeConfigureBothHalvesStayQuiet()
        {
            var presenter = Child("UnboundHud").AddComponent<PermanentHudPresenter>();
            Assert.That(presenter.Build(), Is.True);

            Assert.DoesNotThrow(() => presenter.ManualUpdate(0.0));
            Assert.DoesNotThrow(() => presenter.ManualUpdate(1.0));
            Assert.DoesNotThrow(() => presenter.RegisterBlockingRects(null));
            Assert.That(presenter.TryIssueStop(), Is.False, "no selection and no sink");
        }

        [Test]
        public void PermanentHud_MinimapRectIsThePanelTheGateBlocks()
        {
            var harness = BuildHarness();

            Assert.That(harness.Permanent.TryGetMinimapScreenRect(out var rect), Is.True);
            Assert.That(rect.width, Is.GreaterThan(0f));
            Assert.That(rect.height, Is.GreaterThan(0f));

            // The rectangle the HUD blocks and the rectangle the radar answers clicks
            // inside must be the same panel, or one of them is lying to the player.
            harness.Blocker.Refresh();
            Assert.That(harness.Blocker.IsPointerOverHud(new Vector2(rect.center.x, rect.center.y)), Is.True);
        }

        [Test]
        public void PermanentHud_ViewportIndicatorTracksTheCamerasFootprint()
        {
            var harness = BuildHarness();
            var indicator = harness.Permanent.ViewportIndicatorTransform;
            Spawn(1, Ax, RowZ);

            _radar.SetCameraViewGroundRect(new Rect(-50f, -50f, 100f, 100f));
            harness.Permanent.ManualUpdate(0.0);

            Assert.That(indicator.anchorMin.x, Is.EqualTo(0.375f).Within(1e-4f));
            Assert.That(indicator.anchorMax.y, Is.EqualTo(0.625f).Within(1e-4f));

            var anchorMin = indicator.anchorMin;
            harness.Permanent.ManualUpdate(0.01);
            Assert.That(indicator.anchorMin, Is.EqualTo(anchorMin), "an unchanged footprint writes nothing");

            _radar.SetCameraViewGroundRect(new Rect(0f, 0f, 100f, 100f));
            Assert.That(_radar.Rebuild(), Is.GreaterThan(0));
            harness.Permanent.ManualUpdate(0.05);
            Assert.That(indicator.anchorMin.x, Is.EqualTo(0.5f).Within(1e-4f), "a moved one follows");
        }

        [Test]
        public void PermanentHud_HeaderFollowsTheLocalPlayerAndIsOtherwiseLeftAlone()
        {
            var harness = BuildHarness();

            harness.Permanent.ManualUpdate(0.0);
            Assert.That(harness.Permanent.HeaderValueText.text, Is.EqualTo("1"));

            for (var frame = 1; frame < 20; frame++)
            {
                harness.Permanent.ManualUpdate(frame / 60.0);
            }

            Assert.That(
                harness.Permanent.HeaderValueText.text,
                Is.EqualTo("1"),
                "a permanent panel that is not being interacted with must not be rebuilt by the frame loop");

            _selection.LocalPlayerId = new PlayerId(Enemy);
            harness.Permanent.ManualUpdate(1.0);
            Assert.That(
                harness.Permanent.HeaderValueText.text,
                Is.EqualTo("2"),
                "a reconnect has to be visible, or the player cannot tell whose units they are ordering");
        }

        [Test]
        public void SelectionHud_ReflectsTheSelectionIntoEveryRow()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ, kind: UnitKinds.Scout);
            Spawn(2, Bx, RowZ, kind: UnitKinds.Tank);
            Spawn(3, Cx, RowZ, kind: UnitKinds.Tank);

            harness.Dynamic.ManualUpdate();
            Assert.That(harness.Dynamic.DisplayedCount, Is.EqualTo("0"));

            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));
            _selection.AddSelected(new EntityId(3), SlotOf(3));
            harness.Dynamic.ManualUpdate();

            Assert.That(harness.Dynamic.DisplayedCount, Is.EqualTo("3"));
            Assert.That(harness.Dynamic.DisplayedKindCount(UnitKinds.Scout), Is.EqualTo("1"));
            Assert.That(harness.Dynamic.DisplayedKindCount(UnitKinds.Tank), Is.EqualTo("2"));
            Assert.That(harness.Dynamic.DisplayedHealth, Is.EqualTo("100%"));
            Assert.That(harness.Dynamic.CountValue.text, Is.EqualTo("3"), "the widget shows what the model says");
            Assert.That(harness.Dynamic.GetKindValue(UnitKinds.Tank).text, Is.EqualTo("2"));

            SetHealth(1, 0);
            _selection.PruneStaleSelection();
            harness.Dynamic.ManualUpdate();
            Assert.That(harness.Dynamic.DisplayedCount, Is.EqualTo("2"));
            Assert.That(harness.Dynamic.DisplayedKindCount(UnitKinds.Scout), Is.EqualTo("0"));
        }

        [Test]
        public void SelectionHud_AnUnchangedFrameWritesNoTextAtAll()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            harness.Dynamic.ManualUpdate();
            harness.Dynamic.ManualUpdate();
            var mutations = harness.Dynamic.TextMutationCount;
            Assert.That(mutations, Is.GreaterThan(0), "the first frame has to fill an empty panel");

            for (var frame = 0; frame < 20; frame++)
            {
                harness.Dynamic.ManualUpdate();
            }

            Assert.That(
                harness.Dynamic.TextMutationCount - mutations,
                Is.EqualTo(0),
                "OD-24: twenty steady frames must write twenty nothing");
        }

        [Test]
        public void SelectionHud_SubPercentHealthNoiseDoesNotDirtyThePanel()
        {
            var harness = BuildHarness();
            for (var index = 0; index < 10; index++)
            {
                Spawn((ulong)(index + 1), Ax + index * 2000, RowZ);
                _selection.AddSelected(new EntityId((ulong)(index + 1)), SlotOf((ulong)(index + 1)));
            }

            harness.Dynamic.ManualUpdate();
            Assert.That(harness.Dynamic.DisplayedHealth, Is.EqualTo("100%"));
            var mutations = harness.Dynamic.TextMutationCount;

            // One hit point off one unit of ten is half a percentage point: real movement
            // on the wire, invisible on the panel, and it must not cost a rebuild.
            SetHealth(1, FullHealth - 1);
            harness.Dynamic.ManualUpdate();

            Assert.That(harness.Dynamic.DisplayedHealth, Is.EqualTo("100%"), "99.9 quantises back to 100");
            Assert.That(
                harness.Dynamic.TextMutationCount,
                Is.EqualTo(mutations),
                "OD-24: a change nobody can see must not dirty the canvas");

            // Ten hit points off a second unit crosses the quantum, and only then is the
            // row rewritten.
            SetHealth(2, FullHealth - 10);
            harness.Dynamic.ManualUpdate();

            Assert.That(harness.Dynamic.DisplayedHealth, Is.EqualTo("99%"));
            Assert.That(
                harness.Dynamic.TextMutationCount,
                Is.EqualTo(mutations + 1),
                "one visible change is exactly one write");
        }

        [Test]
        public void SelectionHud_AVisibleChangeIsExactlyOneWrite()
        {
            var harness = BuildHarness();
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            harness.Dynamic.ManualUpdate();
            harness.Dynamic.ManualUpdate();

            var mutations = harness.Dynamic.TextMutationCount;
            SetHealth(1, 10);
            harness.Dynamic.ManualUpdate();

            Assert.That(
                harness.Dynamic.TextMutationCount - mutations,
                Is.EqualTo(1),
                "one changed value, one write — not a refresh of every row on the panel");
            Assert.That(harness.Dynamic.DisplayedHealth, Is.EqualTo("10%"));
        }

        [Test]
        public void SelectionHud_EveryArchetypeTheWireCanCarryGetsARow()
        {
            var harness = BuildHarness();

            Assert.That(harness.Dynamic.GetKindValue(UnitKinds.BaseStructure), Is.Not.Null,
                "a new kind must not be presentable but invisible on the panel");
            Assert.That(harness.Dynamic.GetKindValue(UnitKinds.Scout), Is.Not.Null);
            Assert.That(harness.Dynamic.GetKindValue(UnitKinds.Tank), Is.Not.Null);
            Assert.That(harness.Dynamic.GetKindValue(0), Is.Null, "Unknown is a lookup miss, not an archetype");
            Assert.That(harness.State.KindName(UnitKinds.Scout), Is.EqualTo("Scout"));
        }

        [Test]
        public void SelectionHud_BeforeConfigureDoesNothing()
        {
            var presenter = Child("UnboundPanel").AddComponent<SelectionHudPresenter>();
            Assert.That(presenter.Build(), Is.True);

            Assert.DoesNotThrow(() => presenter.ManualUpdate());
            Assert.That(presenter.TextMutationCount, Is.EqualTo(0));
            presenter.RegisterBlockingRects(null);
            Assert.That(presenter.DisplayedCount, Is.Null, "nothing has been displayed yet");
        }

        // ===================================================================== 8. zero GC

        [Test]
        public void TacticalHudAndRadar_TwoHundredFramesOverFourHundredUnits_DoNotAllocate()
        {
            byte[] ballast = null;
            Assert.That(() => { ballast = new byte[4096]; }, GcAssert.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            const int Units = 400;
            for (var index = 0; index < Units; index++)
            {
                Spawn(
                    (ulong)(index + 1),
                    -9_500 + (index % 20) * 1000,
                    -9_500 + (index / 20) * 1000,
                    owner: (byte)(index % 4 == 0 ? Enemy : Local),
                    kind: (byte)(index % 3 == 0 ? UnitKinds.Tank : UnitKinds.Scout));
            }

            Assert.That(_binder.BoundViewCount, Is.EqualTo(Units));
            Assert.That(_pool.GrowCount, Is.EqualTo(0), "the measured window must be steady state");

            _selection.SelectInsideScreenRect(new Rect(0f, 0f, 800f, 600f), false, _projector);
            Assert.That(_selection.SelectedCount, Is.GreaterThan(0));

            var harness = BuildHarness();
            harness.Blocker.Refresh();

            // A radar that has already published and a panel that has already filled, so
            // the measured window is steady state rather than first-contact cost.
            for (var frame = 0; frame < 20; frame++)
            {
                harness.Permanent.ManualUpdate(frame / 60.0);
                harness.Dynamic.ManualUpdate();
                _selection.PruneStaleSelection();
            }

            var created = _pool.TotalCreated;
            var mutations = harness.Dynamic.TextMutationCount;
            Assert.That(_radar.BlipCount, Is.EqualTo(Units));

            void Advance(int iterations)
            {
                for (var offset = 0; offset < iterations; offset++)
                {
                    var now = (20 + offset) / 60.0;

                    // One frame of the whole tactical HUD pipeline: the radar's cadence
                    // and its widget publish, the selection panel's recompute and its
                    // dirty check, the selection's own maintenance, and the pointer gate.
                    harness.Permanent.ManualUpdate(now);
                    harness.Dynamic.ManualUpdate();
                    _selection.PruneStaleSelection();
                    _selection.UpdatePointer(new Vector2(400f + offset % 7, 300f + offset % 5), _projector);
                    harness.Blocker.Refresh();
                    harness.Blocker.IsPointerOverHud(new Vector2(4f, 4f));
                }
            }

            Advance(10);
            Assert.That(
                () => Advance(200),
                Is.Not.AllocatingGCMemory(),
                "OD-28/OD-24: 200 frames of HUD and 30 Hz radar across 400 live units must allocate nothing");

            Assert.That(_pool.TotalCreated, Is.EqualTo(created), "and instantiate nothing either (OD-26)");
            Assert.That(_radar.BlipCount, Is.EqualTo(Units));
            Assert.That(
                harness.Dynamic.TextMutationCount,
                Is.EqualTo(mutations),
                "none of those frames should have written the panel either");
        }

        [Test]
        public void RadarRebuildAndBlipGeometry_OverFourHundredUnits_DoNotAllocate()
        {
            byte[] ballast = null;
            Assert.That(() => { ballast = new byte[4096]; }, GcAssert.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            for (var index = 0; index < 400; index++)
            {
                Spawn(
                    (ulong)(index + 1),
                    -9_500 + (index % 20) * 1000,
                    -9_500 + (index / 20) * 1000);
            }

            _selection.SelectInsideScreenRect(new Rect(0f, 0f, 800f, 600f), false, _projector);

            var quads = new MinimapBlipQuad[_radar.BlipCapacity];
            var widget = new Rect(0f, 0f, 224f, 224f);

            void Rebuild(int iterations)
            {
                for (var index = 0; index < iterations; index++)
                {
                    _radar.Rebuild();
                    MinimapBlipGeometry.BuildQuads(_radar.Blips, widget, quads, MinimapBlipPalette.Default);
                }
            }

            Rebuild(5);
            Assert.That(
                () => Rebuild(200),
                Is.Not.AllocatingGCMemory(),
                "the snapshot and its geometry are the two things every radar tick does");
            Assert.That(_radar.BlipCount, Is.EqualTo(400));
        }

        [Test]
        public void HudStateRecompute_OverAHundredSelectedUnits_DoesNotAllocate()
        {
            byte[] ballast = null;
            Assert.That(() => { ballast = new byte[4096]; }, GcAssert.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            for (var index = 0; index < 120; index++)
            {
                var entity = (ulong)(index + 1);
                Spawn(entity, index * 1000, 0);
                _selection.AddSelected(new EntityId(entity), SlotOf(entity));
            }

            var state = new SelectionHudState(_selection, _world);
            Assert.That(state.SelectedCount, Is.EqualTo(0), "nothing is derived until a frame asks");

            string count = null;
            string health = null;
            string kind = null;
            string command = null;
            string rejection = null;

            void Recompute(int iterations)
            {
                for (var index = 0; index < iterations; index++)
                {
                    state.Update();
                    count = state.CountText;
                    health = state.HealthText;
                    kind = state.KindCountText(UnitKinds.Tank);
                    command = state.CommandText;
                    rejection = state.RejectionText;
                }
            }

            Recompute(10);
            Assert.That(
                () => Recompute(200),
                Is.Not.AllocatingGCMemory(),
                "OD-24: every value the dynamic canvas shows has to come out of a table");

            Assert.That(count, Is.EqualTo("120"));
            Assert.That(health, Is.EqualTo("100%"));
            Assert.That(command, Is.EqualTo(HudStringTable.NotAvailable));
            Assert.That(rejection, Is.EqualTo(TacticalHudText.NoCommand));
        }

        // ================================ 9. triple-audit remediation (P1-1 .. P2-6)

        // ---- P1-1: what the pointer gate accepts as a rectangle, and how it measures one

        [Test]
        public void Blocker_AnInfiniteOrOverflowingRectangleBlocksNothing()
        {
            var blocker = new TacticalHudPointerBlocker();

            Assert.That(blocker.AddScreenRect(new Rect(0f, 0f, float.PositiveInfinity, 200f)),
                Is.GreaterThanOrEqualTo(0));
            Assert.That(blocker.AddScreenRect(new Rect(0f, 0f, 200f, float.PositiveInfinity)),
                Is.GreaterThanOrEqualTo(0));
            Assert.That(blocker.AddScreenRect(new Rect(float.NaN, 0f, 200f, 200f)),
                Is.GreaterThanOrEqualTo(0));
            Assert.That(blocker.AddScreenRect(new Rect(3.0e38f, 0f, 3.0e38f, 100f)),
                Is.GreaterThanOrEqualTo(0),
                "both stored components are finite — only the derived xMax overflows to +Infinity");

            Assert.That(blocker.UsedCount, Is.EqualTo(4));
            Assert.That(
                blocker.BlockingCount,
                Is.EqualTo(0),
                "an unbounded rectangle is refused, not accepted: it would own every pixel on half the screen");
            Assert.That(blocker.IsPointerOverHud(new Vector2(10f, 10f)), Is.False);
            Assert.That(blocker.IsPointerOverHud(new Vector2(1.0e30f, 50f)), Is.False,
                "the battlefield stays clickable where a corrupt panel is drawn");
        }

        [Test]
        public void Blocker_ANegativeSizedRectangleIsNotNormalised()
        {
            // What HasArea's comment rests on, pinned as a test: Unity 6 keeps x as xMin
            // and derives xMax = x + width, so a negative size is an inverted rectangle
            // rather than a swapped one. A Unity that changed the derivation fails here
            // instead of silently making the guard mean something else.
            var inverted = new Rect(100f, 100f, -50f, -50f);
            Assert.That(inverted.xMin, Is.EqualTo(100f));
            Assert.That(inverted.xMax, Is.EqualTo(50f));

            var blocker = new TacticalHudPointerBlocker();
            blocker.AddScreenRect(inverted);

            Assert.That(blocker.BlockingCount, Is.EqualTo(0));
            Assert.That(blocker.IsPointerOverHud(new Vector2(120f, 120f)), Is.False);
            Assert.That(blocker.IsPointerOverHud(new Vector2(75f, 75f)), Is.False,
                "and not anywhere inside the box the two edges describe either");
        }

        [Test]
        public void Blocker_RectTransformSourceBlocksItsEnclosingBox()
        {
            var rect = Widget("RotatedPanel", new Vector2(100f, 100f));
            var blocker = new TacticalHudPointerBlocker();
            var handle = blocker.AddRectTransform(rect);
            blocker.Refresh();
            Assert.That(blocker.IsPointerOverHud(new Vector2(50f, 50f)), Is.True);

            // A panel turned 45 degrees has its own lower-left corner at the origin and
            // its upper-right straight above it: measured from those two alone it is a
            // vertical line, so the visible widget stops blocking anything and the
            // battlefield receives the clicks on top of it.
            rect.rotation = Quaternion.Euler(0f, 0f, 45f);
            blocker.Refresh();

            Assert.That(blocker.BlockingCount, Is.EqualTo(1), "a rotated panel still blocks");
            Assert.That(blocker.GetScreenRect(handle).xMin, Is.LessThan(0f),
                "and the box it blocks is the one the rotation swings out to the left");
            Assert.That(
                blocker.IsPointerOverHud(new Vector2(-50f, 70f)),
                Is.True,
                "a panel measured from two corners encloses almost nothing, so the clicks on it reach the terrain");

            rect.rotation = Quaternion.identity;
            rect.localScale = new Vector3(-1f, 1f, 1f);
            blocker.Refresh();
            Assert.That(blocker.IsPointerOverHud(new Vector2(-50f, 50f)), Is.True,
                "a mirrored panel encloses the same box");
        }

        [Test]
        public void MinimapInteraction_ACorruptScreenRectIsNotAdopted()
        {
            var interaction = Child("CorruptMinimap").AddComponent<MinimapInteractionController>();
            interaction.Configure(_radar, null, _selection, _sink);
            interaction.SetMinimapScreenRect(RadarScreenRect);
            Assert.That(interaction.MinimapScreenRect, Is.EqualTo(RadarScreenRect));

            var corrupt = new[]
            {
                new Rect(0f, 0f, float.PositiveInfinity, 200f),
                new Rect(0f, 0f, 200f, float.PositiveInfinity),
                new Rect(float.NaN, 0f, 200f, 200f),
                new Rect(0f, float.NaN, 200f, 200f),
                new Rect(3.0e38f, 0f, 3.0e38f, 100f),
                new Rect(0f, 0f, 0f, 0f),
            };

            foreach (var rect in corrupt)
            {
                interaction.SetMinimapScreenRect(rect);
                Assert.That(interaction.MinimapScreenRect, Is.EqualTo(default(Rect)),
                    $"the {rect} rectangle is refused rather than published to whoever asks");
                Assert.That(interaction.TryMapScreenToUv(new Vector2(50f, 50f), out _), Is.False);
                Assert.That(interaction.HandleLeftPointerDown(new Vector2(50f, 50f)), Is.False);
                Assert.That(interaction.HandleRightPointerUp(new Vector2(50f, 50f)), Is.False);
            }

            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void PermanentHud_MinimapScreenRectEnclosesARotatedPanel()
        {
            var harness = BuildHarness();
            harness.Permanent.MinimapTransform.rotation = Quaternion.Euler(0f, 0f, 45f);

            Assert.That(harness.Permanent.TryGetMinimapScreenRect(out var rect), Is.True,
                "the radar is driven by this rectangle, so a measurement that misses it costs every click");
            Assert.That(rect.width, Is.GreaterThan(0f));
            Assert.That(rect.height, Is.GreaterThan(0f));
            Assert.That(UnitPickMath.IsFinite(rect.x) && UnitPickMath.IsFinite(rect.yMax), Is.True);
            Assert.That(rect.xMin, Is.LessThan(0f), "the corner the rotation swings past the anchor");
        }

        // ---- P1-2: which column of an archetype row a name belongs to

        [Test]
        public void SelectionHud_EveryArchetypeRowHasBothColumns()
        {
            var state = new SelectionHudState(_selection, _world, new RenamingCatalog());
            var presenter = Child("TwoColumnPanel").AddComponent<SelectionHudPresenter>();
            Assert.That(presenter.Build(), Is.True);
            presenter.Configure(state);

            var scoutLabel = presenter.GetKindLabel(UnitKinds.Scout);
            Assert.That(scoutLabel, Is.Not.Null, "the archetype row has a label half of its own");
            Assert.That(
                scoutLabel.text,
                Is.EqualTo("Humvee"),
                "and Configure is what names it, from the catalog the state resolved");
            Assert.That(presenter.GetKindLabel(UnitKinds.Tank).text, Is.EqualTo("Tank"));
            Assert.That(presenter.GetKindLabel(UnitKinds.BaseStructure).text, Is.EqualTo("Structure"));
            Assert.That(presenter.GetKindLabel(UnitKinds.Unknown), Is.Null,
                "Unknown is a lookup miss, not an archetype row");
            Assert.That(presenter.GetKindLabel(99), Is.Null);
        }

        [Test]
        public void SelectionHud_ARenamedArchetypeNeverReachesTheCountCell()
        {
            var state = new SelectionHudState(_selection, _world, new RenamingCatalog());
            var presenter = Child("RenamedPanel").AddComponent<SelectionHudPresenter>();
            Assert.That(presenter.Build(), Is.True);

            // The ordering a real HUD produces: the panel is built and driven before the
            // catalog is known, so Configure can arrive after the counts are on screen.
            presenter.Configure(state);
            presenter.ManualUpdate();
            Assert.That(presenter.DisplayedKindCount(UnitKinds.Scout), Is.EqualTo("0"));

            presenter.Configure(state);
            presenter.ManualUpdate();

            Assert.That(
                presenter.GetKindValue(UnitKinds.Scout).text,
                Is.EqualTo("0"),
                "the number stays where it is written: the dirty check compares against its own cache, so a name written into the value cell behind its back can never be overwritten");
            var label = presenter.GetKindLabel(UnitKinds.Scout);
            Assert.That(label, Is.Not.Null);
            Assert.That(
                label.text,
                Is.EqualTo("Humvee"),
                "a roster that renames a kind renames the row");
        }

        // ---- P1-3: scene lifecycle and the runtime wiring between the layers

        [Test]
        public void HudComponents_DriveThemselvesFromUnitysFrameLoop()
        {
            // EditMode cannot make Unity call Update, so what is assertable is that each
            // component owning a per-frame refresh declares one. Otherwise the HUD is
            // correct only while some other script remembers to call ManualUpdate, and
            // nothing in a scene enforces that.
            Assert.That(HasFrameLoopUpdate<PermanentHudPresenter>(), Is.True);
            Assert.That(
                HasFrameLoopUpdate<SelectionHudPresenter>(),
                Is.True,
                "the selection panel would otherwise freeze at whatever the bootstrap last wrote");
            Assert.That(HasFrameLoopUpdate<UnitSelectionDriver>(), Is.True);
            Assert.That(
                HasFrameLoopUpdate<MinimapInteractionController>(),
                Is.False,
                "the radar reads the pointer through the driver, which owns the one and only mouse");
        }

        private static bool HasFrameLoopUpdate<T>() =>
            typeof(T).GetMethod(
                "Update",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null;

        [Test]
        public void MinimapInteraction_DeactivationHookAbandonsAnArmedDrag()
        {
            var interaction = Child("LifecycleMinimap").AddComponent<MinimapInteractionController>();
            interaction.Configure(_radar, null, _selection, _sink);
            interaction.SetMinimapScreenRect(RadarScreenRect);

            Assert.That(interaction.HandleLeftPointerDown(new Vector2(100f, 100f)), Is.True);
            Assert.That(interaction.IsDraggingCameraOnMinimap, Is.True);

            InvokeLifecycleHook(interaction, "OnDisable");

            Assert.That(
                interaction.IsDraggingCameraOnMinimap,
                Is.False,
                "a HUD switched away never sees the release, so an armed drag would outlive it");
            Assert.That(
                interaction.HandleLeftPointerDrag(new Vector2(120f, 120f)),
                Is.False,
                "and the next sample is not a sweep the player started before the switch");
        }

        [Test]
        public void MinimapInteraction_FocusLostHookAbandonsAnArmedDrag()
        {
            var interaction = Child("FocusMinimap").AddComponent<MinimapInteractionController>();
            interaction.Configure(_radar, null, _selection, _sink);
            interaction.SetMinimapScreenRect(RadarScreenRect);

            Assert.That(interaction.HandleLeftPointerDown(new Vector2(100f, 100f)), Is.True);

            InvokeLifecycleHook(interaction, "OnApplicationFocus", false);
            Assert.That(
                interaction.IsDraggingCameraOnMinimap,
                Is.False,
                "a window that loses focus mid-sweep gets no release event either");

            // Regaining focus must not abandon anything that is still live.
            Assert.That(interaction.HandleLeftPointerDown(new Vector2(100f, 100f)), Is.True);
            InvokeLifecycleHook(interaction, "OnApplicationFocus", true);
            Assert.That(
                interaction.IsDraggingCameraOnMinimap,
                Is.True,
                "clicking back into the window is not a reason to drop the drag");
        }

        /// <summary>
        /// Runs one of Unity's lifecycle callbacks by the exact name Unity binds to.
        ///
        /// EditMode refuses to run behaviour callbacks at all — <c>SetActive(false)</c>
        /// does not raise <c>OnDisable</c>, and <c>GameObject.SendMessage</c> asserts on
        /// the spot (<c>ShouldRunBehaviour()</c>) — so the reachable claim is that the
        /// component declares the hook Unity will call and that the body abandons the
        /// drag. A rename, a removal or an emptied body fails here; that Unity calls the
        /// name at runtime is its own documented guarantee, and only a player build can
        /// observe it.
        /// </summary>
        private static void InvokeLifecycleHook(Component target, string method, object argument = null)
        {
            var flags = System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic;
            var info = target.GetType().GetMethod(method, flags);

            Assert.That(
                info,
                Is.Not.Null,
                $"{target.GetType().Name} must declare {method} for Unity to call it");
            Assert.That(
                info.GetParameters().Length,
                Is.EqualTo(argument == null ? 0 : 1),
                $"{method} must take the argument shape Unity supplies");

            info.Invoke(target, argument == null ? null : new[] { argument });
        }

        [Test]
        public void Driver_LeftPressOnTheMinimapLooksThroughTheRadarNotTheMarquee()
        {
            var harness = BuildHarness();
            var driver = BuildDriver(harness.Blocker);
            driver.Driver.SetMinimapInteraction(harness.Interaction);
            Assert.That(driver.Driver.MinimapInteraction, Is.SameAs(harness.Interaction));

            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            var pixel = RadarPixelForMillimetres(-120_000, 80_000);
            driver.Input.SetMockMousePosition(pixel);
            driver.Input.SetMockMouseButton(0, down: true, pressed: true, up: false);
            driver.Driver.ManualUpdate(0.0);

            Assert.That(harness.Interaction.IsDraggingCameraOnMinimap, Is.True,
                "the driver hands the press to the radar");
            harness.CameraRig.ManualUpdate(1f);
            Assert.That(harness.CameraRig.FocusPoint.x, Is.EqualTo(-120f).Within(0.01f));

            Assert.That(_selection.IsDragging, Is.False, "and the battlefield arms no marquee");
            Assert.That(driver.Marquee.IsVisible, Is.False);
            Assert.That(_selection.SelectedCount, Is.EqualTo(1), "nor does it clear the squad");
            Assert.That(_sink.Count, Is.EqualTo(0), "the left button never commands, on the radar or off it");

            driver.Input.SetMockMouseButton(0, down: false, pressed: true, up: false);
            driver.Input.SetMockMousePosition(RadarPixelForMillimetres(-60_000, 80_000));
            driver.Driver.ManualUpdate(1.0 / 60.0);
            Assert.That(harness.Interaction.LastCameraTargetMetres.x, Is.EqualTo(-60f).Within(0.01f),
                "the travel is a radar sweep, not a drag across the terrain behind it");

            driver.Input.SetMockMouseButton(0, down: false, pressed: false, up: true);
            driver.Driver.ManualUpdate(2.0 / 60.0);
            Assert.That(harness.Interaction.IsDraggingCameraOnMinimap, Is.False);
        }

        [Test]
        public void Driver_RightPressOnTheHudIssuesTheRadarOrderWithTheDriversTick()
        {
            var harness = BuildHarness();
            var driver = BuildDriver(harness.Blocker);
            driver.Driver.SetMinimapInteraction(harness.Interaction);
            driver.Driver.RequestedTick = 4242;

            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));

            driver.Input.SetMockMousePosition(RadarPixelForMillimetres(-42_000, 91_000));
            driver.Input.SetMockMouseButton(1, down: false, pressed: false, up: true);
            driver.Driver.ManualUpdate(0.0);

            Assert.That(_sink.Count, Is.EqualTo(1), "one order, from the radar");
            var issued = _sink.Only;
            Assert.That(issued.Kind, Is.EqualTo(GameCommandType.Move));
            Assert.That(issued.Destination, Is.EqualTo(new WorldPointMm(-42_000, 91_000)));
            Assert.That(
                issued.Header.RequestedTick,
                Is.EqualTo(4242UL),
                "the tick is the loop owner's, forwarded on the frame it is used");
            Assert.That(harness.Interaction.LastMoveDestinationMm, Is.EqualTo(new WorldPointMm(-42_000, 91_000)));
        }

        [Test]
        public void PermanentHud_ABoundCameraDrivesTheViewportIndicator()
        {
            var harness = BuildHarness();
            Assert.That(harness.Permanent.ViewportCamera, Is.Null, "nothing is bound by the build alone");

            harness.Camera.transform.position = new Vector3(0f, 45f, -35f);
            harness.Camera.transform.rotation = Quaternion.Euler(52f, 0f, 0f);
            harness.Permanent.SetViewportCamera(harness.Camera);
            Assert.That(harness.Permanent.ViewportCamera, Is.SameAs(harness.Camera));

            Assert.That(MinimapProjection.TryCameraGroundRect(harness.Camera, out var ground), Is.True);
            var expected = MinimapProjection.GroundRectToUv(MinimapBounds.Default, ground);

            harness.Permanent.ManualUpdate(0.0);

            Assert.That(_radar.HasCameraView, Is.True, "the HUD derives the footprint from its own camera");
            Assert.That(_radar.NormalizedCameraView, Is.EqualTo(expected));
            Assert.That(
                harness.Permanent.ViewportIndicatorTransform.anchorMin,
                Is.EqualTo(expected.position),
                "and the indicator is drawn from it in the same frame");
        }

        [Test]
        public void PermanentHud_AnUnboundCameraLeavesTheSuppliedFootprintAlone()
        {
            var harness = BuildHarness();
            _radar.SetCameraViewGroundRect(new Rect(-50f, -50f, 100f, 100f));

            harness.Permanent.ManualUpdate(0.0);

            Assert.That(_radar.HasCameraView, Is.True,
                "with no camera bound, whatever the frame loop supplied stands");
            Assert.That(_radar.NormalizedCameraView.xMin, Is.EqualTo(0.375f).Within(1e-5f));
        }

        // ---- P2-1: the millimetre conversion and the rectangles built from it

        [Test]
        public void MetresToMillimetres_OutsideTheIntRangeSaturates()
        {
            Assert.That(MinimapProjection.MetresToMillimetres(1.5), Is.EqualTo(1_500));
            Assert.That(
                MinimapProjection.MetresToMillimetres(3_000_000.0),
                Is.EqualTo(int.MaxValue),
                "past 2 147 483.647 m an unchecked cast wraps to int.MinValue, which is a legal coordinate at the opposite end of the map");
            Assert.That(MinimapProjection.MetresToMillimetres(-3_000_000.0), Is.EqualTo(int.MinValue));
            Assert.That(MinimapProjection.MetresToMillimetres(double.PositiveInfinity), Is.EqualTo(int.MaxValue));
            Assert.That(MinimapProjection.MetresToMillimetres(double.NegativeInfinity), Is.EqualTo(int.MinValue));
            Assert.That(MinimapProjection.MetresToMillimetres(double.NaN), Is.EqualTo(0));
        }

        [Test]
        public void WorldMetresToUv_AFarPointPinsToTheCornerItIsBeyond()
        {
            var bounds = MinimapBounds.Default;
            Assert.That(
                MinimapProjection.WorldMetresToUv(bounds, new Vector3(3_000_000f, 0f, 3_000_000f)),
                Is.EqualTo(new Vector2(1f, 1f)),
                "far east and north is the north-east corner, not the wrapped south-west one");
            Assert.That(
                MinimapProjection.WorldMetresToUv(bounds, new Vector3(-3_000_000f, 0f, -3_000_000f)),
                Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void GroundRectToUv_NeverReturnsAnInvertedOrUnboundedRectangle()
        {
            var bounds = MinimapBounds.Default;

            Assert.That(
                MinimapProjection.GroundRectToUv(bounds, new Rect(0f, 0f, -100f, -100f)),
                Is.EqualTo(default(Rect)),
                "an inverted footprint is refused: the indicator's anchors would put anchorMin beyond anchorMax");
            Assert.That(
                MinimapProjection.GroundRectToUv(bounds, new Rect(3.0e38f, 0f, 3.0e38f, 10f)),
                Is.EqualTo(default(Rect)),
                "and so is one whose derived edge is +Infinity");

            var widerThanTheMap = MinimapProjection.GroundRectToUv(
                bounds,
                new Rect(-250_000f, -250_000f, 500_000f, 500_000f));
            Assert.That(widerThanTheMap, Is.EqualTo(new Rect(0f, 0f, 1f, 1f)),
                "a view beyond every edge of a 400 m map is the whole map, in order");
            Assert.That(widerThanTheMap.xMin, Is.LessThanOrEqualTo(widerThanTheMap.xMax));
            Assert.That(widerThanTheMap.yMin, Is.LessThanOrEqualTo(widerThanTheMap.yMax));
        }

        [Test]
        public void Radar_CameraViewRefusesAZeroAreaOrOverflowingFootprint()
        {
            _radar.SetCameraViewGroundRect(new Rect(-50f, -50f, 100f, 100f));
            Assert.That(_radar.HasCameraView, Is.True);

            _radar.SetCameraViewGroundRect(new Rect(10f, 10f, 0f, 20f));
            Assert.That(_radar.HasCameraView, Is.False, "a footprint with no area has no corner to draw");
            Assert.That(_radar.NormalizedCameraView, Is.EqualTo(default(Rect)));

            _radar.SetCameraViewGroundRect(new Rect(50f, 50f, -100f, -100f));
            Assert.That(_radar.HasCameraView, Is.False, "neither has an inverted one");

            _radar.SetCameraViewGroundRect(new Rect(3.0e38f, 0f, 3.0e38f, 10f));
            Assert.That(
                _radar.HasCameraView,
                Is.False,
                "x and width are both finite — only xMax is +Infinity, and it is xMax that reaches the anchors");
        }

        // ---- P2-2: who the selection panel is allowed to count as the player's squad

        [Test]
        public void HudState_ADeadSelectedUnitLeavesTheNumbersBeforeThePruneRuns()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));

            var state = new SelectionHudState(_selection, _world);
            state.Update();
            Assert.That(state.SelectedCount, Is.EqualTo(2));

            SetHealth(1, 0);

            // Deliberately no PruneStaleSelection: the presenter runs before the
            // controller's prune on some frames, and a panel counting the corpse shows
            // a squad of two that a move order will be refused for.
            state.Update();

            Assert.That(state.SelectedCount, Is.EqualTo(1), "the casualty is out of the panel on the frame it dies");
            Assert.That(state.GetKindCount(UnitKinds.Scout), Is.EqualTo(1));
            Assert.That(state.HealthPercent, Is.EqualTo(100), "and it is not dragging the mean down");
        }

        [Test]
        public void HudState_AReconnectedLocalPlayerStartsFromAnEmptySquadRow()
        {
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            var state = new SelectionHudState(_selection, _world);
            state.Update();
            Assert.That(state.SelectedCount, Is.EqualTo(1));

            // A reconnect hands the controller a new local player without rebuilding the
            // selection it already holds; those units are somebody else's now.
            _selection.LocalPlayerId = new PlayerId(Enemy);
            state.Update();

            Assert.That(state.SelectedCount, Is.EqualTo(0), "whose units these now are not");
            Assert.That(state.GetKindCount(UnitKinds.Scout), Is.EqualTo(0));
            Assert.That(state.HealthPercent, Is.EqualTo(-1));
            Assert.That(state.CountText, Is.EqualTo("0"));
        }

        [Test]
        public void HudState_ARecycledSlotIsNotTheUnitTheSelectionNamed()
        {
            var slot = Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), slot);

            Assert.That(
                _world.ApplyRemove(new DeltaRemoveRecord(new EntityId(1), DeltaRemoveCause.Destroyed)),
                Is.EqualTo(ClientWorldApplyResult.Ok));
            Assert.That(Spawn(9, Ax, RowZ), Is.EqualTo(slot), "the table hands back the slot it just freed");

            var state = new SelectionHudState(_selection, _world);
            state.Update();

            Assert.That(state.SelectedCount, Is.EqualTo(0),
                "the selection still names entity 1 at that slot; a stranger took it");
            Assert.That(state.GetKindCount(UnitKinds.Scout), Is.EqualTo(0));
            Assert.That(state.HealthPercent, Is.EqualTo(-1));
        }

        [Test]
        public void HudState_OverhealedUnitsDoNotReportMoreThanWholeHealth()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));
            _selection.AddSelected(new EntityId(2), SlotOf(2));

            var state = new SelectionHudState(_selection, _world);
            state.Update();
            Assert.That(state.HealthPercent, Is.EqualTo(100));

            // The wire's hit points are an int the authority does not cap for this
            // client's benefit, and the catalog's maximum is a client-side number.
            SetHealth(1, FullHealth * 2);
            SetHealth(2, FullHealth / 2);
            state.Update();

            Assert.That(state.HealthPercent, Is.EqualTo(75), "a unit cannot contribute more than whole health");
            Assert.That(state.HealthText, Is.EqualTo("75%"));
        }

        [Test]
        public void HudState_AnOrderOnANewSinkReplacesTheLastChannelsWording()
        {
            Spawn(1, Ax, RowZ);
            _selection.AddSelected(new EntityId(1), SlotOf(1));

            var refused = new ScriptedCommandChannel(MatchCommandRejection.NotEntityOwner);
            var first = new UnitCommandChannelSink(refused);
            var state = new SelectionHudState(_selection, _world) { CommandFeedback = first };

            Assert.That(_selection.IssueStop(5, first), Is.True);
            state.Update();
            Assert.That(state.CommandText, Is.EqualTo("Stop"));
            Assert.That(state.RejectionText, Is.EqualTo("Not your unit"));

            // A reconnect builds a new sink over a new channel. It happens to have
            // attempted the same number of orders as the old one had reached, which is
            // the only thing the panel used to compare.
            var accepted = new ScriptedCommandChannel(MatchCommandRejection.None);
            var second = new UnitCommandChannelSink(accepted);
            Assert.That(_selection.IssueMove(new WorldPointMm(1_000, 2_000), 6, second), Is.True);

            state.CommandFeedback = second;
            state.Update();

            Assert.That(state.CommandText, Is.EqualTo("Move"), "the panel reports the order this session saw");
            Assert.That(state.RejectionText, Is.EqualTo("Accepted"));
            Assert.That(state.SubmittedCommandCount, Is.EqualTo(1));
        }

        // ---- P2-3: the widget-to-UV mapping's own bounds

        [Test]
        public void TryScreenToUv_AnOverflowingWidgetIsNotAMapping()
        {
            // x, y, width and height are all finite. xMax is x + width, and that is +Inf,
            // so the division returns 0 for every pixel and the radar answers for the
            // whole screen with a click that was never on it.
            Assert.That(
                MinimapProjection.TryScreenToUv(
                    new Rect(3.0e38f, 0f, 3.0e38f, 100f),
                    new Vector2(3.0e38f, 50f),
                    out _),
                Is.EqualTo(MinimapMappingResult.NonFiniteInput),
                "a widget whose far edge is +Infinity maps every pixel to UV zero and answers Ok");
            Assert.That(
                MinimapProjection.TryScreenToUv(
                    new Rect(0f, 3.0e38f, 100f, 3.0e38f),
                    new Vector2(50f, 3.0e38f),
                    out _),
                Is.EqualTo(MinimapMappingResult.NonFiniteInput));
        }

        [Test]
        public void PixelToUv_ClampsIndexesOutsideTheGrid()
        {
            Assert.That(
                MinimapProjection.PixelToUv(new Vector2Int(64, 64), 128).x,
                Is.EqualTo(64.0 / 127.0).Within(1e-6f),
                "an in-range index is the exact inverse of UvToPixel, unchanged");
            Assert.That(
                MinimapProjection.PixelToUv(new Vector2Int(-5, 200), 128),
                Is.EqualTo(new Vector2(0f, 1f)),
                "an index off either end is the cell it is beyond, not a point off the widget");
            Assert.That(
                MinimapProjection.PixelToUv(new Vector2Int(int.MinValue, int.MaxValue), 128),
                Is.EqualTo(new Vector2(0f, 1f)));
        }

        // ---- P2-4 and P2-6: the blip geometry and the overlay's dirty lifecycle

        [Test]
        public void BlipGeometry_ANonFiniteBlipSizeDrawsTheDefaultDot()
        {
            var blips = new[] { new MinimapBlip(new Vector2(0.5f, 0.5f), MinimapBlipKind.Friendly, 0, 1UL) };
            var quads = new MinimapBlipQuad[1];
            var widget = new Rect(0f, 0f, 100f, 100f);

            MinimapBlipGeometry.BuildQuads(
                blips, widget, quads, MinimapBlipPalette.Default, float.NaN);
            Assert.That(
                quads[0].Extent,
                Is.EqualTo(new Vector2(1.5f, 1.5f)),
                "Mathf.Max propagates a NaN into all four corners of every quad, and uGUI then drops the batch: a blank radar");

            MinimapBlipGeometry.BuildQuads(
                blips, widget, quads, MinimapBlipPalette.Default, float.PositiveInfinity);
            Assert.That(
                quads[0].Extent,
                Is.EqualTo(new Vector2(1.5f, 1.5f)),
                "and an infinite size is a blip covering the widget and the pixels outside it");
        }

        [Test]
        public void BlipGeometry_ABlipSizeLargerThanTheWidgetIsClampedToIt()
        {
            var blips = new[] { new MinimapBlip(new Vector2(0.5f, 0.5f), MinimapBlipKind.Friendly, 0, 1UL) };
            var quads = new MinimapBlipQuad[1];

            // A 300 px dot on a 64 px widget puts insetMin past insetMax, so insetSize
            // collapses to zero and every blip lands on the same inset corner.
            MinimapBlipGeometry.BuildQuads(
                blips, new Rect(0f, 0f, 64f, 64f), quads, MinimapBlipPalette.Default, 300f);

            Assert.That(
                quads[0].Extent,
                Is.EqualTo(new Vector2(32f, 32f)),
                "a dot cannot be wider than the radar it is drawn on");
            Assert.That(quads[0].Center.x, Is.GreaterThanOrEqualTo(0f));
            Assert.That(quads[0].Center.y, Is.LessThanOrEqualTo(64f), "a whole army still reads on a small radar");
        }

        [Test]
        public void BlipGeometry_SkipsABlipWhoseUvIsNotANumber()
        {
            var blips = new[]
            {
                new MinimapBlip(new Vector2(float.NaN, 0.5f), MinimapBlipKind.Friendly, 0, 1UL),
                new MinimapBlip(new Vector2(0.5f, 0.5f), MinimapBlipKind.Enemy, 1, 2UL),
                new MinimapBlip(new Vector2(0.5f, float.PositiveInfinity), MinimapBlipKind.Selected, 2, 3UL),
            };
            var quads = new MinimapBlipQuad[3];

            var written = MinimapBlipGeometry.BuildQuads(
                blips, new Rect(0f, 0f, 100f, 100f), quads, MinimapBlipPalette.Default, 3f);

            Assert.That(
                written,
                Is.EqualTo(1),
                "a blip the client cannot place is missing from the radar, not invented at its south-west corner");
            Assert.That(quads[0].Entity, Is.EqualTo(2UL));
            Assert.That(quads[0].Color, Is.EqualTo(MinimapBlipPalette.Default.Enemy));
        }

        [Test]
        public void BlipGeometry_RejectsAWidgetWhoseDerivedEdgeOverflows()
        {
            var blips = new[] { new MinimapBlip(new Vector2(0.5f, 0.5f), MinimapBlipKind.Friendly, 0, 1UL) };
            var quads = new MinimapBlipQuad[1];

            Assert.That(
                MinimapBlipGeometry.BuildQuads(
                    blips, new Rect(3.0e38f, 0f, 3.0e38f, 100f), quads, MinimapBlipPalette.Default, 3f),
                Is.EqualTo(0),
                "x and width are finite; xMax is +Infinity, and the inset arithmetic inverts on it");
        }

        [Test]
        public void RadarGraphic_ClearingTheModelsBlipsRepublishesThePicture()
        {
            Spawn(1, Ax, RowZ);
            Assert.That(_radar.Rebuild(), Is.EqualTo(1));

            var graphic = Widget("ClearRadar", Vector2.zero).gameObject.AddComponent<MinimapRadarGraphic>();
            graphic.FallbackWidgetRectPx = new Rect(0f, 0f, 200f, 200f);
            graphic.SetModel(_radar, _radar.BlipCapacity);
            Assert.That(graphic.QuadCount, Is.EqualTo(1));

            // ClearBlips deliberately does not bump the rebuild generation, so a dirty
            // check keyed on the generation alone leaves blips for units gone from it.
            _radar.ClearBlips();
            Assert.That(
                graphic.PublishFromModel(),
                Is.True,
                "the picture changed even though the rebuild generation did not");
            Assert.That(graphic.QuadCount, Is.EqualTo(0));
            Assert.That(graphic.PublishFromModel(), Is.False, "and it settles at once");
        }

        [Test]
        public void RadarGraphic_AWidgetResizeRepublishesTheMesh()
        {
            Spawn(1, Ax, RowZ);
            _radar.Rebuild();

            var widget = Widget("ResizedRadar", Vector2.zero);
            var graphic = widget.gameObject.AddComponent<MinimapRadarGraphic>();
            graphic.FallbackWidgetRectPx = new Rect(0f, 0f, 200f, 200f);
            graphic.SetModel(_radar, _radar.BlipCapacity);
            Assert.That(graphic.WidgetRectPx, Is.EqualTo(new Rect(0f, 0f, 200f, 200f)));

            // A layout pass that arrives after the first publish moves every blip, and
            // the radar generation has not moved at all.
            widget.sizeDelta = new Vector2(400f, 400f);
            Assert.That(
                graphic.PublishFromModel(),
                Is.True,
                "a resize moves every blip, and no radar generation was missed to say so");
            Assert.That(graphic.WidgetRectPx, Is.EqualTo(new Rect(0f, 0f, 400f, 400f)));
            Assert.That(graphic.QuadCount, Is.EqualTo(1));
            Assert.That(graphic.PublishFromModel(), Is.False);
        }

        [Test]
        public void RadarGraphic_DroppingTheModelTakesTheQuadsDown()
        {
            Spawn(1, Ax, RowZ);
            Spawn(2, Bx, RowZ);
            Assert.That(_radar.Rebuild(), Is.EqualTo(2));

            var graphic = Widget("ReleasedRadar", Vector2.zero).gameObject.AddComponent<MinimapRadarGraphic>();
            graphic.FallbackWidgetRectPx = new Rect(0f, 0f, 200f, 200f);
            graphic.SetModel(_radar, _radar.BlipCapacity);
            Assert.That(graphic.HasModel, Is.True);
            Assert.That(graphic.QuadCount, Is.EqualTo(2));

            graphic.SetModel(null, 0);
            Assert.That(graphic.HasModel, Is.False);
            Assert.That(
                graphic.QuadCount,
                Is.EqualTo(0),
                "a mesh left standing after its model is gone draws units this client has no answer about");
            Assert.That(graphic.PublishFromModel(), Is.False);

            // And rebinding publishes from scratch rather than comparing against the
            // generation cached from the model that was replaced.
            graphic.SetModel(_radar, _radar.BlipCapacity);
            Assert.That(graphic.QuadCount, Is.EqualTo(2));
        }

        // ---- P2-5: a HUD adopted twice must not leak gate slots

        [Test]
        public void HudPresenters_ReRegisteringTheirRectsLeaksNoSlots()
        {
            var harness = BuildHarness();
            Assert.That(harness.Blocker.UsedCount, Is.EqualTo(3));

            harness.Permanent.RegisterBlockingRects(harness.Blocker);
            harness.Dynamic.RegisterBlockingRects(harness.Blocker);

            Assert.That(
                harness.Blocker.UsedCount,
                Is.EqualTo(3),
                "a HUD re-adopted after a reconnect registers again, and the old handles go back first");
            Assert.That(harness.Blocker.BlockingCount, Is.EqualTo(3));

            harness.Permanent.Release();
            harness.Dynamic.Release();
            Assert.That(
                harness.Blocker.UsedCount,
                Is.EqualTo(0),
                "an orphaned slot is an invisible strip over the battlefield for the rest of the match");
            Assert.That(harness.Blocker.IsEmpty, Is.True);
        }

        // --------------------------------------------------------------------- helpers

        /// <summary>
        /// Linear, invertible and exact, so a pixel and the world point it names are
        /// asserted as equalities rather than as tolerances. The same shape the step 3.5
        /// fixture uses, for the same reason.
        /// </summary>
        private sealed class TestProjector : IUnitPointerProjector
        {
            public const float PixelsPerMetre = 10f;
            public const float Width = 800f;
            public const float Height = 600f;

            public bool GroundAvailable = true;

            public static Vector2 ScreenForMetres(float xMetres, float zMetres) => new Vector2(
                Width * 0.5f + xMetres * PixelsPerMetre,
                Height * 0.5f + zMetres * PixelsPerMetre);

            public Vector3 ProjectToScreen(Vector3 world) => new Vector3(
                Width * 0.5f + world.x * PixelsPerMetre,
                Height * 0.5f + world.z * PixelsPerMetre,
                1f);

            public bool TryGetGroundPoint(Vector2 screenPixels, out Vector3 groundWorld)
            {
                groundWorld = Vector3.zero;
                if (!GroundAvailable ||
                    !UnitPickMath.IsFinite(screenPixels.x) ||
                    !UnitPickMath.IsFinite(screenPixels.y))
                {
                    return false;
                }

                groundWorld = new Vector3(
                    (screenPixels.x - Width * 0.5f) / PixelsPerMetre,
                    0f,
                    (screenPixels.y - Height * 0.5f) / PixelsPerMetre);
                return true;
            }

            public Vector2 ScreenSizePixels => new Vector2(Width, Height);
        }

        private readonly struct CapturedCommand
        {
            public CapturedCommand(
                CommandHeader header,
                EntityId[] entities,
                WorldPointMm destination,
                EntityId target,
                FormationSpec formation,
                GameCommandType kind)
            {
                Header = header;
                Entities = entities;
                Destination = destination;
                Target = target;
                Formation = formation;
                Kind = kind;
            }

            public CommandHeader Header { get; }
            public EntityId[] Entities { get; }
            public WorldPointMm Destination { get; }
            public EntityId Target { get; }
            public FormationSpec Formation { get; }
            public GameCommandType Kind { get; }
        }

        /// <summary>
        /// Copies each request, because <see cref="IssuedCommand"/> hands over the
        /// controller's shared buffer and a recording test that kept the reference would
        /// be asserting against whatever the next click wrote into it.
        /// </summary>
        private sealed class RecordingCommandSink : IUnitCommandSink
        {
            public readonly List<CapturedCommand> Issues = new List<CapturedCommand>();

            public int Count => Issues.Count;

            public CapturedCommand Only
            {
                get
                {
                    Assert.That(Issues.Count, Is.EqualTo(1), "expected exactly one command");
                    return Issues[0];
                }
            }

            public void Submit(in IssuedCommand command)
            {
                var entities = new EntityId[command.EntityCount];
                for (var index = 0; index < entities.Length; index++)
                {
                    entities[index] = command.GetEntity(index);
                }

                Issues.Add(new CapturedCommand(
                    command.Header,
                    entities,
                    command.Destination,
                    command.Target,
                    command.Formation,
                    command.Kind));
            }
        }

        private sealed class ScriptedCommandChannel : ICommandChannel
        {
            public ScriptedCommandChannel(MatchCommandRejection rejection)
            {
                Rejection = rejection;
            }

            public MatchCommandRejection Rejection;

            public MatchCommandRejection TrySubmitMove(
                CommandHeader header,
                EntityId[] entities,
                WorldPointMm destination,
                FormationSpec formation) => Rejection;

            public MatchCommandRejection TrySubmitAttack(
                CommandHeader header,
                EntityId[] attackers,
                EntityId target) => Rejection;

            public MatchCommandRejection TrySubmitStop(CommandHeader header, EntityId[] entities) => Rejection;
        }

        /// <summary>
        /// A roster that calls the Scout something else (OD-29 allows a mod to rename a
        /// kind without changing its wire id), which is what distinguishes the label
        /// column of an archetype row from its value column.
        /// </summary>
        private sealed class RenamingCatalog : IUnitCatalog
        {
            private const int RowRadiusMm = 1_000;

            private static readonly UnitDefinition Humvee =
                new UnitDefinition(UnitKinds.Scout, "Humvee", FullHealth, RowRadiusMm);

            private static readonly UnitDefinition TankRow =
                new UnitDefinition(UnitKinds.Tank, "Tank", FullHealth, RowRadiusMm);

            private static readonly UnitDefinition StructureRow =
                new UnitDefinition(UnitKinds.BaseStructure, "Structure", FullHealth, RowRadiusMm);

            public bool TryGet(byte kind, out UnitDefinition definition)
            {
                switch (kind)
                {
                    case UnitKinds.Scout:
                        definition = Humvee;
                        return true;
                    case UnitKinds.Tank:
                        definition = TankRow;
                        return true;
                    case UnitKinds.BaseStructure:
                        definition = StructureRow;
                        return true;
                    default:
                        definition = default;
                        return false;
                }
            }
        }
    }
}
