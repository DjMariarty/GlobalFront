using System;
using GlobalFront.Client.Presentation;
using UnityEngine;
using UnityEngine.UI;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// The three colours a radar blip can take, already in the vertex format uGUI
    /// writes (Phase 3, step 3.6, OD-24).
    /// </summary>
    public struct MinimapBlipPalette
    {
        /// <summary>The local player's own units.</summary>
        public Color32 Friendly;

        /// <summary>Everyone else's live units.</summary>
        public Color32 Enemy;

        /// <summary>The local player's units that are currently selected.</summary>
        public Color32 Selected;

        public MinimapBlipPalette(Color32 friendly, Color32 enemy, Color32 selected)
        {
            Friendly = friendly;
            Enemy = enemy;
            Selected = selected;
        }

        /// <summary>
        /// The prototype palette: friendly green, enemy red, selection amber, the
        /// Generals/Zero Hour reading of the three cases (ADR-003 keeps the product
        /// formula, so this is the conventional choice rather than a new one).
        /// </summary>
        public static MinimapBlipPalette Default => new MinimapBlipPalette(
            new Color32(96, 220, 120, 255),
            new Color32(226, 84, 74, 255),
            new Color32(252, 206, 92, 255));

        /// <summary>The colour for one blip state.</summary>
        public Color32 ColorFor(MinimapBlipKind kind) => kind switch
        {
            MinimapBlipKind.Enemy => Enemy,
            MinimapBlipKind.Selected => Selected,
            _ => Friendly,
        };
    }

    /// <summary>
    /// One radar blip reduced to the geometry it earns: a centre, a half-extent and
    /// a colour.
    /// </summary>
    public readonly struct MinimapBlipQuad
    {
        public MinimapBlipQuad(Vector2 center, Vector2 extent, Color32 color, ulong entity)
        {
            Center = center;
            Extent = extent;
            Color = color;
            Entity = entity;
        }

        /// <summary>Position in the widget's own rect space, pixels from its bottom-left.</summary>
        public Vector2 Center { get; }

        /// <summary>Half the quad's size, so the blip is a square of <c>2 × Extent</c>.</summary>
        public Vector2 Extent { get; }

        public Color32 Color { get; }

        /// <summary>Entity the blip came from, carried so an assertion can name the unit rather than only the pixel.</summary>
        public ulong Entity { get; }
    }

    /// <summary>
    /// Blips to quads, and nothing else (Phase 3, step 3.6).
    ///
    /// Split out from <see cref="MinimapRadarGraphic"/> for the same reason the
    /// radar model is split out from the widget: the interesting claims — a blip
    /// lands on the cell its unit is in, an off-map unit pins to the edge instead of
    /// flying off the widget, a selected friendly is a different colour from an
    /// unselected one — are all statements about numbers, and a
    /// <see cref="VertexHelper"/> is not something an EditMode test can read back.
    /// The graphic is the thin adapter at the end of this.
    /// </summary>
    public static class MinimapBlipGeometry
    {
        /// <summary>
        /// Default side of the quantisation grid a radar snaps to, the resolution
        /// OD-24's baked terrain image is produced at. A 400 m map over 128 cells is
        /// just over three metres per cell, which is finer than a unit's own 1 m
        /// footprint reads at on a widget a few hundred pixels across.
        /// </summary>
        public const int DefaultRadarGridSizePixels = 128;

        /// <summary>Smallest blip that is still a dot rather than a sub-pixel smear.</summary>
        public const float MinBlipSizePixels = 1f;

        /// <summary>
        /// The blip size the widget asks for by default, and the one a non-finite size
        /// falls back to.
        /// </summary>
        public const float DefaultBlipSizePixels = 3f;

        /// <summary>
        /// Fills <paramref name="destination"/> with one quad per blip, in the same
        /// order, and returns how many were written.
        ///
        /// Positions are quantised to the radar grid before being scaled into the
        /// widget, rather than scaled straight from UV, so that the overlay and the
        /// baked terrain underneath it agree about which pixel a unit is on. Without
        /// the snap, resizing the widget would slide every blip off the ground tile
        /// it was drawn against, and the map the player reads a blip from would stop
        /// being the map the blip is on.
        ///
        /// Allocation-free: the caller owns the destination array, it is sized once
        /// to the blip capacity, and this writes into it.
        /// </summary>
        public static int BuildQuads(
            ReadOnlySpan<MinimapBlip> blips,
            Rect widgetRectPx,
            MinimapBlipQuad[] destination,
            MinimapBlipPalette palette,
            float blipSizePixels = DefaultBlipSizePixels,
            int radarGridSizePixels = DefaultRadarGridSizePixels)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            // A widget with no area has no pixel to put a blip on, and every centre
            // computed from it would land on the same corner.
            if (!MinimapProjection.IsValidRadarSize(radarGridSizePixels) ||
                !IsFiniteRect(widgetRectPx) ||
                widgetRectPx.width <= 0f || widgetRectPx.height <= 0f)
            {
                return 0;
            }

            // A blip has to be a whole number of pixels across. At 0 the quads are
            // degenerate and uGUI still uploads them, so 400 invisible vertices cost
            // the same as 400 visible ones.
            //
            // A size that is not a number is the same mistake in the other direction:
            // Mathf.Max and Mathf.Min both propagate a NaN, so one poisoned serialized
            // field would put NaN into every corner of every quad and uGUI would drop the
            // whole batch — a radar that is blank, not a radar that is too big. The
            // fallback is the size the default parameter asks for.
            var blipSize = UnitPickMath.IsFinite(blipSizePixels)
                ? blipSizePixels
                : DefaultBlipSizePixels;

            // Half the widget is the most a blip can be and still describe a point
            // rather than cover the map with it; past that the inset below inverts,
            // insetMin exceeding insetMax, and every blip lands on one corner.
            var maxHalf = Mathf.Min(widgetRectPx.width, widgetRectPx.height) * 0.5f;
            var half = Mathf.Min(
                Mathf.Max(MinBlipSizePixels, blipSize) * 0.5f,
                maxHalf);
            var extent = new Vector2(half, half);

            // Keep the square inside the widget. An edge-pinned blip whose centre is
            // on the border loses half of it to the mask, which makes the units
            // furthest away look smaller than the ones nearby.
            var insetMin = new Vector2(widgetRectPx.xMin + half, widgetRectPx.yMin + half);
            var insetMax = new Vector2(widgetRectPx.xMax - half, widgetRectPx.yMax - half);
            var insetSize = new Vector2(
                Mathf.Max(0f, insetMax.x - insetMin.x),
                Mathf.Max(0f, insetMax.y - insetMin.y));

            var written = 0;
            var count = Math.Min(blips.Length, destination.Length);
            for (var index = 0; index < count; index++)
            {
                var blip = blips[index];

                // A blip whose UV is not a number is dropped rather than drawn: the
                // quantisation below turns NaN into a quad at the widget's own origin,
                // which is a unit reporting in from the south-west corner of the map
                // forever. A missing dot is the honest reading of a state the client
                // cannot place.
                if (!UnitPickMath.IsFinite(blip.Uv.x) || !UnitPickMath.IsFinite(blip.Uv.y))
                {
                    continue;
                }

                var snapped = MinimapProjection.PixelToUv(
                    MinimapProjection.UvToPixel(blip.Uv, radarGridSizePixels),
                    radarGridSizePixels);

                var center = new Vector2(
                    insetMin.x + snapped.x * insetSize.x,
                    insetMin.y + snapped.y * insetSize.y);

                destination[written++] = new MinimapBlipQuad(
                    center,
                    extent,
                    palette.ColorFor(blip.Kind),
                    blip.Entity);
            }

            return written;
        }

        /// <summary>
        /// Every edge, not just the stored position and size: <c>x + width</c> can
        /// overflow to <c>+Infinity</c> with both finite inputs, and an infinite edge
        /// makes the inset arithmetic below produce an insetMax of -Infinity.
        /// </summary>
        private static bool IsFiniteRect(Rect rect) =>
            !float.IsNaN(rect.x) && !float.IsInfinity(rect.x) &&
            !float.IsNaN(rect.y) && !float.IsInfinity(rect.y) &&
            !float.IsNaN(rect.width) && !float.IsInfinity(rect.width) &&
            !float.IsNaN(rect.height) && !float.IsInfinity(rect.height) &&
            !float.IsInfinity(rect.xMax) && !float.IsInfinity(rect.yMax);
    }

    /// <summary>
    /// Draws the radar overlay as one quad per live unit, inside a single uGUI
    /// batch (Phase 3, step 3.6, ADR-012/OD-24).
    ///
    /// OD-24 rules out the two easy implementations. A second URP camera looking down
    /// at the battlefield costs a full scene render per frame for a widget a few
    /// hundred pixels wide, and a per-unit blip <see cref="GameObject"/> costs a
    /// spawn, a destroy and a layout rebuild every time the army changes — which in
    /// a fight is continuously. This draws neither: it is one graphic, one mesh, one
    /// draw call, and its mesh is rebuilt at most once per radar tick.
    ///
    /// The alternative OD-24 allows — a <c>Color32[]</c> written into a
    /// <see cref="Texture2D"/> and uploaded with <c>SetPixels32</c> — was not taken
    /// because it puts a texture upload on the overlay's refresh path and gives the
    /// baked terrain image a second, separately-sized resolution to stay in step
    /// with. A mesh gets the widget's own rect for free.
    ///
    /// <b>Why it goes quiet between ticks.</b> <see cref="Graphic.SetVerticesDirty"/>
    /// on an unchanged mesh is not free: it re-registers the graphic with
    /// <c>CanvasUpdateRegistry</c>, which rewrites the whole batch, and OD-24's
    /// three-canvas isolation exists precisely so that a changing minimap does not
    /// rebuild a selection panel. The dirty call is therefore gated on the radar's
    /// own refresh generation, so between 30 Hz ticks this component's per-frame cost
    /// is one integer comparison.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MinimapRadarGraphic : MaskableGraphic
    {
        [Header("Appearance (content-supplied)")]
        [SerializeField] private Sprite blipSprite;
        [SerializeField] private Color friendlyColor = new Color(0.38f, 0.86f, 0.47f, 1f);
        [SerializeField] private Color enemyColor = new Color(0.89f, 0.33f, 0.29f, 1f);
        [SerializeField] private Color selectedColor = new Color(0.99f, 0.81f, 0.36f, 1f);
        [SerializeField, Min(1f)] private float blipSizePixels = 3f;
        [SerializeField, Range(8, 1024)] private int radarGridSizePixels =
            MinimapBlipGeometry.DefaultRadarGridSizePixels;

        private MinimapRadarModel _model;
        private MinimapBlipQuad[] _quads = new MinimapBlipQuad[0];
        private int _quadCount;
        private int _lastPublishedRefreshCount = -1;
        private int _lastPublishedBlipCount = -1;
        private Rect _lastPublishedWidgetRect;
        private readonly UIVertex[] _corners = new UIVertex[4];
        private Rect _widgetRectPx = new Rect(0f, 0f, 1f, 1f);

        /// <summary>The blip source this overlay draws.</summary>
        public MinimapRadarModel Model => _model;

        /// <summary>Quads the last mesh build produced.</summary>
        public int QuadCount => _quadCount;

        /// <summary>True once a model has been supplied and a mesh published from it.</summary>
        public bool HasModel => _model != null;

        /// <summary>
        /// The rect used when the canvas has not laid this widget out, so a driver
        /// that refreshes the radar before the first layout pass still gets a mapping
        /// instead of forty blips stacked on one corner.
        /// </summary>
        public Rect FallbackWidgetRectPx
        {
            get => _widgetRectPx;
            set => _widgetRectPx = value;
        }

        /// <summary>
        /// The rect blips are laid out inside right now: the widget's own laid-out rect
        /// when the canvas has given it one, and <see cref="FallbackWidgetRectPx"/>
        /// otherwise. Read-only view of the resolution
        /// <see cref="PublishFromModel"/> applies, so a driver that declared a fallback
        /// can see whether it was used.
        /// </summary>
        public Rect WidgetRectPx => ResolveWidgetRect();

        /// <summary>
        /// Supplies the radar source and the blip capacity it can report, and publishes
        /// the current snapshot immediately. A null model takes the overlay away: the
        /// mesh is cleared and the publish caches are dropped, so a HUD rebound to a new
        /// model cannot keep drawing the quads of the old one.
        /// </summary>
        public void SetModel(MinimapRadarModel model, int blipCapacity)
        {
            _model = model;
            if (blipCapacity < 0)
            {
                blipCapacity = 0;
            }

            // Only ever grows: a resync that briefly empties the table must not shrink
            // the array, because the next full-match rebuild would then re-allocate on
            // the frame the player is least able to afford it.
            if (_quads.Length < blipCapacity)
            {
                _quads = new MinimapBlipQuad[blipCapacity];
            }

            _lastPublishedRefreshCount = -1;
            _lastPublishedBlipCount = -1;

            if (model == null)
            {
                // PublishFromModel refuses with no model, so the stale picture has to be
                // taken down here. Vertices left in the mesh after the source goes are
                // blips for units this client no longer has any answer about.
                if (_quadCount != 0)
                {
                    _quadCount = 0;
                    SetVerticesDirty();
                }

                return;
            }

            PublishFromModel();
        }

        /// <summary>
        /// Pulls the model's current snapshot if it has been rebuilt since the last
        /// call. Returns true when the mesh was republished — which, with a throttled
        /// model, is at most once per radar tick.
        ///
        /// Three things decide "changed", because all three move the picture without
        /// the rebuild generation noticing: the generation itself, the number of blips
        /// (<see cref="MinimapRadarModel.ClearBlips"/> empties the table on purpose
        /// without rebuilding), and the widget's rect, which a layout pass can give a
        /// size after the first publish put every blip on the fallback rect.
        /// </summary>
        public bool PublishFromModel()
        {
            if (_model == null)
            {
                return false;
            }

            var widgetRect = ResolveWidgetRect();
            if (_model.RefreshCount == _lastPublishedRefreshCount &&
                _model.BlipCount == _lastPublishedBlipCount &&
                widgetRect == _lastPublishedWidgetRect)
            {
                return false;
            }

            _lastPublishedRefreshCount = _model.RefreshCount;
            _lastPublishedBlipCount = _model.BlipCount;
            _lastPublishedWidgetRect = widgetRect;
            _quadCount = MinimapBlipGeometry.BuildQuads(
                _model.Blips,
                widgetRect,
                _quads,
                Palette,
                blipSizePixels,
                radarGridSizePixels);

            SetVerticesDirty();
            return true;
        }

        /// <summary>The palette currently in use, from the serialized colours.</summary>
        public MinimapBlipPalette Palette => new MinimapBlipPalette(
            friendlyColor, enemyColor, selectedColor);

        /// <inheritdoc/>
        /// <remarks>
        /// A sprite the caller assigns is optional; without one the built-in white
        /// texture is what makes a quad a flat colour.
        /// </remarks>
        public override Texture mainTexture =>
            blipSprite != null ? blipSprite.texture : Texture2D.whiteTexture;

        /// <summary>
        /// Emits one quad per published blip. Reads only the array
        /// <see cref="PublishFromModel"/> filled, so the mesh rebuild and the blip
        /// snapshot cannot disagree about what is on the radar.
        /// </summary>
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            for (var index = 0; index < _quadCount; index++)
            {
                AddQuad(vh, _quads[index]);
            }
        }

        private void AddQuad(VertexHelper vh, MinimapBlipQuad quad)
        {
            var min = quad.Center - quad.Extent;
            var max = quad.Center + quad.Extent;

            // Corner order is uGUI's own quad convention (bl, br, tl, tr), which is
            // also the order VertexHelper.AddUIVertexQuad expects — getting it wrong
            // produces a bow-tie whose two triangles cover different pixels each frame
            // depending on the index order the batch happens to use.
            SetCorner(0, min.x, min.y);
            SetCorner(1, max.x, min.y);
            SetCorner(2, min.x, max.y);
            SetCorner(3, max.x, max.y);
            _corners[0].color = quad.Color;
            _corners[1].color = quad.Color;
            _corners[2].color = quad.Color;
            _corners[3].color = quad.Color;
            vh.AddUIVertexQuad(_corners);
        }

        private void SetCorner(int index, float x, float y)
        {
            var corner = UIVertex.simpleVert;
            corner.position = new Vector3(x, y, 0f);

            // Full-texture UVs on every corner. A blip is a flat colour, so what the
            // UV says does not matter as long as it stays inside [0..1] — sampling
            // outside it on a wrapped texture is how a blip ends up tinted with the
            // opposite corner of the UI atlas.
            corner.uv0 = new Vector2(0.5f, 0.5f);
            _corners[index] = corner;
        }

        private Rect ResolveWidgetRect()
        {
            var size = ((RectTransform)transform).rect.size;
            if (size.x > 0f && size.y > 0f)
            {
                // Blips are laid out from the rect's own bottom-left, not from the
                // canvas's, so the widget's anchoredPosition and pivot are irrelevant
                // and the mapping is the same whether the minimap is 200 px in a
                // corner or half the screen in a map-review mode.
                return new Rect(0f, 0f, size.x, size.y);
            }

            return _widgetRectPx;
        }
    }
}
