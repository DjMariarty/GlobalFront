using GlobalFront.Client.Presentation;
using UnityEngine;
using UnityEngine.UI;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// Canvas 1 of the three OD-24 fixes: the permanent tactical HUD — minimap, command
    /// bar and match header (Phase 3, step 3.6, ADR-012/OD-24).
    ///
    /// <b>What "permanent" buys.</b> OD-24 isolates the HUD into three canvases because
    /// a uGUI canvas rebuilds the geometry of <em>every</em> graphic that references it
    /// whenever any one of them dirties. The radar changes thirty times a second and
    /// the selection panel changes on every click; the chrome behind them changes when
    /// the player reconnects. On one canvas those three costs add up to a full rebuild
    /// thirty times a second, so the frame that keeps the minimap current also
    /// re-tessellates a border that has not moved since the match loaded. Split apart,
    /// each canvas rebuilds only its own contents — and this one rebuilds essentially
    /// never: its geometry is written at <see cref="Build"/>, and its only steady-state
    /// writes are a handful of <see cref="RectTransform"/> properties on the viewport
    /// indicator, and only on the frames the camera footprint actually moved.
    ///
    /// <b>The radar is not a camera.</b> OD-24 bans the second URP camera and prescribes
    /// a terrain image baked once at map load with a blip overlay drawn at 30 Hz over
    /// it. The baked sprite is content — <see cref="SetTerrainSprite"/> is where it
    /// arrives, exactly as <see cref="SelectionMarqueePresenter"/> waits for content to
    /// supply its 9-slice border — and this component owns the frame, the overlay and
    /// the cadence that drives them.
    ///
    /// <b>Sprites are optional, and that is deliberate.</b> A widget whose art has not
    /// arrived keeps its controls live and its geometry empty — but "empty geometry" is
    /// only true of a <see cref="Text"/> with no font. An <see cref="Image"/> with no
    /// sprite falls through to <c>Graphic.OnPopulateMesh</c>, which emits a solid quad in
    /// <see cref="Image.color"/>, so an untextured <c>Image</c> is a coloured panel, not an
    /// invisible one. That is what the dark HUD surfaces below are, and it is also why a
    /// decorative frame that spans the radar has to be switched off until its art exists:
    /// left enabled it paints over the blips it is supposed to sit around.
    ///
    /// <b>Fonts are not optional.</b> The same reasoning would leave every label blank —
    /// but unlike a border, text is not decoration the player can do without, and a panel
    /// of blank boxes reads as a broken HUD rather than a pending one. <see cref="ResolveFont"/>
    /// therefore falls back to the built-in legacy runtime font when content has not
    /// supplied one.
    ///
    /// <b>The layout is authored in reference pixels.</b> A screen-space overlay canvas
    /// without a <see cref="CanvasScaler"/> keeps its authored pixel size at any window
    /// size, so this HUD's bottom-left stack — minimap, command bar and the panel above
    /// them — grows until it swallows the quadrant under it in a small or free-aspect
    /// window. Both tactical canvases scale against
    /// <see cref="ReferenceResolutionPixels"/> so the panels keep their fraction of the
    /// screen instead of their fraction of the pixel budget.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PermanentHudPresenter : MonoBehaviour
    {
        /// <summary>
        /// Below the dynamic selection panel (10) and far below the transient marquee
        /// (1000): permanent chrome is the thing the other two are drawn over.
        /// </summary>
        public const int DefaultSortingOrder = 0;

        /// <summary>Sorting order OD-24's dynamic selection canvas sits at.</summary>
        public const int SelectionCanvasSortingOrder = 10;

        /// <summary>Side of the minimap widget, in pixels, at the default HUD layout.</summary>
        public const float DefaultMinimapSizePixels = 224f;

        /// <summary>Width of the command bar strip under the minimap.</summary>
        public const float DefaultCommandBarWidthPixels = 288f;

        /// <summary>Height of the command bar strip.</summary>
        public const float DefaultCommandBarHeightPixels = 56f;

        /// <summary>Gap left between the minimap and the bar under it.</summary>
        public const float PanelGapPixels = 8f;

        /// <summary>Distance the bottom-left-anchored panels sit from the screen corner.</summary>
        public static readonly Vector2 DefaultCornerOffset = new Vector2(16f, 16f);

        /// <summary>
        /// Screen size every tactical canvas authors its pixel layout against. Both canvases
        /// scale against it, so a panel that occupies a quarter of the screen height at
        /// 1920x1080 occupies a quarter of it at 1280x720 too — which is the difference
        /// between a HUD in the corner and a HUD over the army the player is trying to click.
        /// </summary>
        public static readonly Vector2 ReferenceResolutionPixels = new Vector2(1920f, 1080f);

        /// <summary>
        /// Half way between matching width and matching height, so neither a wide window nor
        /// a tall one gets a HUD sized for the axis it did not ask about.
        /// </summary>
        public const float ReferenceMatchWidthOrHeight = 0.5f;

        [Header("Layout (pixels, applied at build)")]
        [SerializeField] private Vector2 minimapCornerOffset = DefaultCornerOffset;
        [SerializeField] private float minimapSize = DefaultMinimapSizePixels;
        [SerializeField] private float commandBarWidth = DefaultCommandBarWidthPixels;
        [SerializeField] private float commandBarHeight = DefaultCommandBarHeightPixels;
        [SerializeField] private int sortingOrder = DefaultSortingOrder;

        [Header("Content (optional; assigned by the HUD scene)")]
        [SerializeField] private Sprite terrainSprite;
        [SerializeField] private Sprite frameSprite;
        [SerializeField] private Font font;
        [SerializeField] private Color panelColor = new Color(0.06f, 0.08f, 0.1f, 0.88f);
        [SerializeField] private Color frameColor = new Color(0.42f, 0.5f, 0.58f, 0.9f);
        [SerializeField] private Color viewportColor = new Color(1f, 1f, 1f, 0.28f);

        [Header("Wiring (optional; assign from the scene or in code)")]
        [Tooltip("The camera whose ground footprint the viewport indicator draws. Left " +
                 "empty, the indicator shows whatever the frame loop supplies through the " +
                 "radar model instead.")]
        [SerializeField] private Camera viewportCamera;

        private readonly Vector3[] _corners = new Vector3[4];

        /// <summary>
        /// The built-in label font, resolved the first time a HUD is built without content.
        /// Shared between canvases because they all ask <c>Resources</c> for the same one.
        /// </summary>
        private static Font _builtinFont;

        private GameObject _canvasObject;
        private Canvas _canvas;
        private RectTransform _minimapRect;
        private RectTransform _commandBarRect;
        private RectTransform _viewportRect;
        private Image _terrainImage;
        private Image _frameImage;
        private Image _viewportImage;
        private Text _headerValueText;
        private Button _stopButton;
        private MinimapRadarGraphic _radar;

        private MinimapRadarModel _model;
        private UnitSelectionController _selection;
        private IUnitCommandSink _sink;
        private TacticalHudPointerBlocker _blocker;
        private int _minimapBlockerHandle = -1;
        private int _commandBarBlockerHandle = -1;

        private string _cachedHeaderText;
        private Rect _cachedNormalizedViewport;
        private bool _cachedHasViewport;
        private int _lastPublishedRefreshCount = -1;

        /// <summary>True once the canvas and its widgets exist.</summary>
        public bool IsBuilt => _canvas != null;

        /// <summary>The radar overlay this presenter drives, once built.</summary>
        public MinimapRadarGraphic Radar => _radar;

        /// <summary>The command bar's stop control, once built.</summary>
        public Button StopButton => _stopButton;

        /// <summary>The match header's value row, once built.</summary>
        public Text HeaderValueText => _headerValueText;

        /// <summary>The minimap widget's rect transform, once built.</summary>
        public RectTransform MinimapTransform => _minimapRect;

        /// <summary>The command bar strip's rect transform, once built.</summary>
        public RectTransform CommandBarTransform => _commandBarRect;

        /// <summary>The widget the camera's ground footprint is drawn on.</summary>
        public RectTransform ViewportIndicatorTransform => _viewportRect;

        /// <summary>
        /// The camera the viewport indicator follows, or null when the frame loop
        /// derives the footprint itself and hands it to
        /// <see cref="MinimapRadarModel.SetCameraViewGroundRect"/>.
        /// </summary>
        public Camera ViewportCamera => viewportCamera;

        /// <summary>
        /// Points the viewport indicator at a camera. Null leaves the model's own
        /// footprint alone, which is what a test — or a HUD whose camera is owned
        /// elsewhere — relies on.
        /// </summary>
        public void SetViewportCamera(Camera camera) => viewportCamera = camera;

        /// <summary>
        /// Tick the Stop button asks for, assigned by the loop owner. The same contract
        /// <see cref="UnitSelectionDriver.RequestedTick"/> and
        /// <see cref="MinimapInteractionController.RequestedTick"/> keep: the HUD layer
        /// decides what the player asked for and never when it should land.
        /// </summary>
        public ulong RequestedTick { get; set; }

        /// <summary>
        /// Creates the permanent canvas and every widget on it, once.
        ///
        /// Idempotent for the same reason <see cref="SelectionMarqueePresenter.Build"/>
        /// is: a second build would stack a second copy of the whole HUD, and the
        /// buttons on the first would keep receiving clicks nobody could see.
        /// </summary>
        /// <returns>True when the HUD is usable after the call.</returns>
        public bool Build()
        {
            if (_canvas != null)
            {
                return true;
            }

            _canvasObject = new GameObject("TacticalHudCanvas", typeof(RectTransform));
            _canvasObject.transform.SetParent(transform, false);
            _canvasObject.layer = gameObject.layer;

            _canvas = _canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = sortingOrder;

            // The scaler goes on before any widget is laid out under this canvas. It lives
            // on the canvas object, so Release() takes it down with the panel tree it
            // scales — no separate teardown and no orphaned preWillRenderCanvases hook.
            var scaler = _canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolutionPixels;
            scaler.matchWidthOrHeight = ReferenceMatchWidthOrHeight;

            // Anchored to the bottom-left, because the minimap and the command bar are
            // the two things on screen that always live in the player's peripheral
            // corner. Anchoring there keeps them under the hands through a window resize
            // without a layout pass having to be involved.
            _minimapRect = CreatePanel(
                "MinimapPanel",
                _canvasObject.transform,
                minimapCornerOffset,
                new Vector2(minimapSize, minimapSize),
                panelColor);

            _terrainImage = CreateImage(
                "BakedTerrain",
                _minimapRect,
                Vector2.zero,
                Vector2.one,
                Color.white,
                Image.Type.Simple);
            _terrainImage.sprite = terrainSprite;

            // The baked map is the radar's backdrop, so before it exists the panel's own
            // colour has to be what sits behind the blips. An untextured Image is a solid
            // quad of its colour — and this one is authored white, so left enabled it would
            // paint the whole radar white under the blips.
            _terrainImage.enabled = terrainSprite != null;

            // The camera footprint is built before the radar, and that order is the
            // widget's whole design question: sibling order is paint order, so anything
            // created after the radar draws over the blips. Before content supplies the
            // 9-slice border that would make this a hollow frame it is an untextured
            // Image, which is a solid translucent quad — and from the slice's 150 m
            // overview the camera's own ground rect is wider than the map, so its
            // normalised form covers the full width of the radar and washes out every
            // unit dot in it. A lit region under the blips still says "this is the view"
            // and says it truthfully; over them it says nothing at all.
            _viewportImage = CreateImage(
                "ViewportIndicator",
                _minimapRect,
                Vector2.zero,
                Vector2.zero,
                viewportColor,
                Image.Type.Sliced);
            _viewportImage.sprite = frameSprite;
            _viewportRect = _viewportImage.rectTransform;

            // Hidden until the model has a camera footprint to report, so a HUD with no
            // camera bound shows a radar rather than a box parked in the corner.
            _viewportImage.enabled = false;
            _cachedHasViewport = false;

            _radar = CreateRadarOverlay(_minimapRect);

            _frameImage = CreateImage(
                "Frame",
                _minimapRect,
                Vector2.zero,
                Vector2.one,
                frameColor,
                Image.Type.Sliced);
            _frameImage.sprite = frameSprite;

            // This widget is stretched across the whole radar and drawn after it, so an
            // untextured frame is an opaque sheet over every blip. It earns its place back
            // when SetContent supplies the 9-slice art that makes it a border.
            _frameImage.enabled = frameSprite != null;

            _commandBarRect = CreatePanel(
                "CommandBar",
                _canvasObject.transform,
                minimapCornerOffset + new Vector2(0f, minimapSize + PanelGapPixels),
                new Vector2(commandBarWidth, commandBarHeight),
                panelColor);

            _stopButton = CreateButton("StopButton", _commandBarRect, "Stop");

            // Top-left, because the header is the one thing on this canvas that is read
            // rather than reached for: the minimap and the bar live under the mouse hand
            // in the corner, and the player's name does not.
            CreateText(
                "MatchHeaderLabel",
                _canvasObject.transform,
                new Vector2(0f, 1f),
                new Vector2(8f, -8f),
                new Vector2(64f, 26f),
                "Player");
            _headerValueText = CreateText(
                "MatchHeaderPlayer",
                _canvasObject.transform,
                new Vector2(0f, 1f),
                new Vector2(72f, -8f),
                new Vector2(48f, 26f),
                HudStringTable.NotAvailable);

            // Records what the widget is actually showing, so the first steady-state
            // frame of a header that has not changed is a comparison rather than a write.
            _cachedHeaderText = HudStringTable.NotAvailable;

            return true;
        }

        /// <summary>
        /// Supplies the radar source, the selection the command bar acts on, and where
        /// its orders go. Any of them may be null and a null simply leaves that half of
        /// the HUD quiet — a build that is presenting a match before it is playing one
        /// has a canvas and no session, and it must not throw on the first frame.
        /// </summary>
        public void Configure(
            MinimapRadarModel model,
            UnitSelectionController selectionController,
            IUnitCommandSink sink)
        {
            _model = model;
            _selection = selectionController;
            _sink = sink;

            if (_radar == null || model == null)
            {
                return;
            }

            // Sized to the radar's own capacity rather than to the current blip count, so
            // the frame a full army appears is not the frame the overlay decides it needs
            // to allocate.
            _radar.SetModel(model, model.BlipCapacity);
            _lastPublishedRefreshCount = _model.RefreshCount;
        }

        /// <summary>
        /// Registers the minimap and the command bar as HUD-owned rectangles, so the
        /// battlefield behind them stops seeing the player's clicks.
        ///
        /// Idempotent: a second call is a HUD re-adopted after a reconnect, and without
        /// releasing the first handles the table would hold two rectangles per panel and
        /// <see cref="Release"/> would free only the newest pair. The orphaned strip the
        /// old ones covered stays unclickable for the rest of the match, which is
        /// precisely the failure this gate is supposed to make impossible.
        /// </summary>
        public void RegisterBlockingRects(TacticalHudPointerBlocker blocker)
        {
            if (blocker == null || _canvas == null)
            {
                return;
            }

            ReleaseBlockingRects();

            _blocker = blocker;
            _minimapBlockerHandle = blocker.AddRectTransform(_minimapRect);
            _commandBarBlockerHandle = blocker.AddRectTransform(_commandBarRect);
        }

        private void ReleaseBlockingRects()
        {
            if (_blocker == null)
            {
                return;
            }

            if (_minimapBlockerHandle >= 0)
            {
                _blocker.Remove(_minimapBlockerHandle);
            }

            if (_commandBarBlockerHandle >= 0)
            {
                _blocker.Remove(_commandBarBlockerHandle);
            }

            _minimapBlockerHandle = -1;
            _commandBarBlockerHandle = -1;
            _blocker = null;
        }

        /// <summary>Assigns the baked terrain image content produces at map load (OD-24).</summary>
        public void SetTerrainSprite(Sprite sprite)
        {
            terrainSprite = sprite;
            if (_terrainImage != null)
            {
                _terrainImage.sprite = sprite;

                // Same rule as the frame, in the other direction: the white quad this
                // widget becomes without art is a backdrop nobody asked for.
                _terrainImage.enabled = sprite != null;
            }
        }

        /// <summary>Assigns the 9-slice border and the font content supplies at load.</summary>
        public void SetContent(Sprite frame, Font textFont)
        {
            frameSprite = frame;
            font = textFont;

            if (_frameImage != null)
            {
                _frameImage.sprite = frame;

                // An untextured Image draws a solid quad of its colour, so a frame that
                // spans the radar with no border art to draw is a sheet over the blips.
                _frameImage.enabled = frame != null;
            }

            if (_viewportImage != null)
            {
                _viewportImage.sprite = frame;
            }

            ApplyFont();
        }

        /// <summary>
        /// Advances the header, the radar and the viewport indicator. Deterministic entry
        /// point in the <c>RtsCameraController.ManualUpdate</c> shape, so the 30 Hz
        /// cadence and the steady-state quiet are assertable rather than only observable.
        /// </summary>
        public void ManualUpdate(double nowSeconds)
        {
            if (_canvas == null)
            {
                return;
            }

            UpdateHeader();

            if (_model == null)
            {
                return;
            }

            var rebuilt = _model.Advance(nowSeconds);
            if (!rebuilt && _model.RefreshCount == _lastPublishedRefreshCount)
            {
                // Two frames in three end here: one integer comparison, no widget
                // touched. That is the steady-state cost OD-24 asks for, and the radar's
                // own throttle is what decides when there is anything new to publish.
                return;
            }

            if (_model.RefreshCount != _lastPublishedRefreshCount)
            {
                _lastPublishedRefreshCount = _model.RefreshCount;
                _radar.PublishFromModel();
            }

            // The camera is the only honest source of the footprint, and asking it here
            // rather than in the frame loop keeps the indicator from describing the view
            // as it was before this frame's pan. With no camera bound, whatever the
            // caller put into the model stands: the HUD has nothing better to say.
            if (viewportCamera != null)
            {
                if (MinimapProjection.TryCameraGroundRect(viewportCamera, out var groundRect))
                {
                    _model.SetCameraViewGroundRect(groundRect);
                }
                else
                {
                    _model.ClearCameraView();
                }
            }

            UpdateViewportIndicator();
        }

        /// <summary>Unity's frame loop for a HUD placed in a scene; tests drive <see cref="ManualUpdate"/>.</summary>
        private void Update()
        {
            ManualUpdate(Time.unscaledTimeAsDouble);
        }

        /// <summary>
        /// Writes the match header, dirty-checked against the string last handed out.
        /// Public because the header's content is match state this component does not
        /// own; what the HUD derives by itself is the local player number.
        /// </summary>
        public void SetHeader(string text)
        {
            if (ReferenceEquals(text, _cachedHeaderText))
            {
                return;
            }

            _cachedHeaderText = text;

            if (_headerValueText != null)
            {
                _headerValueText.text = text;
            }
        }

        /// <summary>
        /// The Stop control's action, and the API the rest of the HUD can call for the
        /// same order.
        /// </summary>
        /// <returns>True when an order reached the sink.</returns>
        public bool TryIssueStop()
        {
            if (_selection == null || _sink == null)
            {
                return false;
            }

            // IssueStop owns every precondition: an empty selection and an invalid local
            // player both answer false without touching the sink, so a HUD button cannot
            // spend a command sequence on a click that meant nothing.
            return _selection.IssueStop(RequestedTick, _sink);
        }

        /// <summary>
        /// The minimap's rectangle in screen pixels, for a
        /// <see cref="MinimapInteractionController"/> that has no canvas of its own to
        /// ask.
        ///
        /// Built from all four world corners, not the pair at index 0 and 2: a panel
        /// that is rotated or mirrored — which a HUD parented under a layout node can
        /// be — has neither of those two as an extremum, and reading only them yields an
        /// inverted, zero-area rectangle. That would silently cost the radar every
        /// click, because <see cref="MinimapInteractionController.SetMinimapScreenRect"/>
        /// refuses a rectangle with no area.
        /// </summary>
        public bool TryGetMinimapScreenRect(out Rect rectPx)
        {
            rectPx = default;
            if (_minimapRect == null)
            {
                return false;
            }

            _minimapRect.GetWorldCorners(_corners);

            var minX = float.PositiveInfinity;
            var minY = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var maxY = float.NegativeInfinity;

            for (var corner = 0; corner < _corners.Length; corner++)
            {
                var point = _corners[corner];

                // A non-finite corner poisons the whole answer rather than being
                // skipped: every comparison against NaN is false, and the other three
                // corners would then describe a panel that is not there.
                if (!IsFinite(point.x) || !IsFinite(point.y))
                {
                    return false;
                }

                if (point.x < minX)
                {
                    minX = point.x;
                }

                if (point.y < minY)
                {
                    minY = point.y;
                }

                if (point.x > maxX)
                {
                    maxX = point.x;
                }

                if (point.y > maxY)
                {
                    maxY = point.y;
                }
            }

            rectPx = Rect.MinMaxRect(minX, minY, maxX, maxY);
            if (!IsFinite(rectPx.width) || !IsFinite(rectPx.height) ||
                rectPx.width <= 0f || rectPx.height <= 0f)
            {
                rectPx = default;
                return false;
            }

            return true;
        }

        /// <summary>Tears down what <see cref="Build"/> created and frees the blocking rects.</summary>
        public void Release()
        {
            ReleaseBlockingRects();

            _radar = null;
            _terrainImage = null;
            _frameImage = null;
            _viewportImage = null;
            _viewportRect = null;
            _headerValueText = null;
            _stopButton = null;
            _minimapRect = null;
            _commandBarRect = null;
            _canvas = null;
            _cachedHeaderText = null;

            if (_canvasObject != null)
            {
#if UNITY_EDITOR
                DestroyImmediate(_canvasObject);
#else
                Destroy(_canvasObject);
#endif
                _canvasObject = null;
            }
        }

        private void OnDestroy() => Release();

        /// <summary>
        /// uGUI's <c>onClick</c> carries no return channel, so the button goes through a
        /// void adapter and the bool stays on <see cref="TryIssueStop"/> for the
        /// programmatic callers that need to know whether the order was handed over.
        /// </summary>
        private void OnStopButtonClicked() => TryIssueStop();

        private void UpdateHeader()
        {
            if (_selection == null)
            {
                return;
            }

            var player = _selection.LocalPlayerId;

            // Straight out of the cached number table, so a frame in which the player has
            // not changed hands returns the same string reference as last time and
            // SetHeader's check costs a pointer comparison.
            SetHeader(player.IsValid ? HudStringTable.Count(player.Value) : HudStringTable.NotAvailable);
        }

        private void UpdateViewportIndicator()
        {
            if (_viewportRect == null || _viewportImage == null)
            {
                return;
            }

            var hasViewport = _model.HasCameraView;
            var viewport = hasViewport ? _model.NormalizedCameraView : default;

            if (hasViewport == _cachedHasViewport &&
                (!hasViewport || viewport == _cachedNormalizedViewport))
            {
                return;
            }

            _cachedHasViewport = hasViewport;
            _cachedNormalizedViewport = viewport;

            // Toggled on change only, for the reason SelectionMarqueePresenter states:
            // assigning a Graphic's enabled flag dirties its batch even when it was
            // already set that way.
            if (_viewportImage.enabled != hasViewport)
            {
                _viewportImage.enabled = hasViewport;
            }

            if (!hasViewport)
            {
                return;
            }

            // Anchors rather than sizeDelta, because the indicator lives inside a widget
            // that is itself stretched to the minimap: a rect expressed in [0..1] follows
            // the minimap through any resize without this code hearing about it.
            _viewportRect.anchorMin = viewport.position;
            _viewportRect.anchorMax = viewport.max;
            _viewportRect.offsetMin = Vector2.zero;
            _viewportRect.offsetMax = Vector2.zero;
        }

        private MinimapRadarGraphic CreateRadarOverlay(RectTransform parent)
        {
            var radarObject = new GameObject("RadarOverlay", typeof(RectTransform));
            radarObject.transform.SetParent(parent, false);
            radarObject.layer = parent.gameObject.layer;

            var graphic = radarObject.AddComponent<MinimapRadarGraphic>();
            var rect = radarObject.transform as RectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            // A blip is not a control, and the reason is the marquee's reason twice over:
            // a graphic that accepts clicks swallows the click the layer underneath it is
            // about to route.
            graphic.raycastTarget = false;
            return graphic;
        }

        private RectTransform CreatePanel(
            string name,
            Transform parent,
            Vector2 positionPx,
            Vector2 sizePx,
            Color color)
        {
            var panelObject = new GameObject(name, typeof(RectTransform));
            panelObject.transform.SetParent(parent, false);
            panelObject.layer = parent.gameObject.layer;

            var panel = panelObject.AddComponent<Image>();
            panel.color = color;
            panel.sprite = frameSprite;
            panel.type = Image.Type.Sliced;

            // A HUD panel that cannot be clicked through is the point of the widget, but
            // uGUI's own hit test is not how this one gets it: the frame, the radar and
            // the terrain image all leave raycastTarget false, and battlefield blocking
            // is TacticalHudPointerBlocker's job because that gate runs on a per-frame
            // path uGUI's hit test cannot afford.
            panel.raycastTarget = false;

            var rect = panelObject.transform as RectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = positionPx;
            rect.sizeDelta = sizePx;
            rect.localScale = Vector3.one;
            return rect;
        }

        private Image CreateImage(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Color color,
            Image.Type imageType)
        {
            var imageObject = new GameObject(name, typeof(RectTransform));
            imageObject.transform.SetParent(parent, false);
            imageObject.layer = parent.gameObject.layer;

            var image = imageObject.AddComponent<Image>();
            image.color = color;
            image.type = imageType;
            image.raycastTarget = false;

            var rect = imageObject.transform as RectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = Vector2.zero;
            rect.localScale = Vector3.one;
            return image;
        }

        private Button CreateButton(string name, Transform parent, string label)
        {
            // A Button needs a graphic as its surface. With no sprite the Image is a solid
            // quad of its colour, which is a legible button rather than an empty one; the
            // art content supplies later only restyles it.
            var buttonObject = new GameObject(name, typeof(RectTransform));
            buttonObject.transform.SetParent(parent, false);
            buttonObject.layer = parent.gameObject.layer;

            // The component goes on before the rect is read. A GameObject's transform is a
            // plain Transform until a RectTransform is asked for at construction or a uGUI
            // component installs one, and casting a plain Transform yields null — a null
            // that reads fine one statement later, which makes it a confusing thing to
            // debug at 3 a.m. Every builder in this file passes typeof(RectTransform).
            var image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.18f, 0.24f, 0.3f, 1f);
            image.sprite = frameSprite;
            image.type = Image.Type.Sliced;
            image.raycastTarget = true;

            var rect = buttonObject.transform as RectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(8f, 8f);
            rect.sizeDelta = new Vector2(88f, 40f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;

            // onClick carries no return channel, so the button goes through a void
            // adapter and the bool stays on TryIssueStop for the programmatic callers —
            // the hotkey path and the tests — that need to know whether the order was
            // actually handed over.
            button.onClick.AddListener(OnStopButtonClicked);

            CreateText(
                name + "Label",
                buttonObject.transform,
                Vector2.zero,
                new Vector2(8f, 8f),
                new Vector2(72f, 24f),
                label);
            return button;
        }

        private Text CreateText(
            string name,
            Transform parent,
            Vector2 anchor,
            Vector2 positionPx,
            Vector2 sizePx,
            string label)
        {
            var textObject = new GameObject(name, typeof(RectTransform));
            textObject.transform.SetParent(parent, false);
            textObject.layer = parent.gameObject.layer;

            var text = textObject.AddComponent<Text>();
            text.fontSize = 18;
            text.color = Color.white;
            text.font = ResolveFont(font);
            text.text = label;

            // raycastTarget defaults to true on Text, which for a label over a panel
            // would mean the label is what receives the click rather than the control the
            // player was aiming at.
            text.raycastTarget = false;

            var rect = textObject.transform as RectTransform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;

            // Pivot follows the anchor, which is what makes positionPx mean "inset from
            // that corner" for both a bottom-anchored button label and a top-anchored
            // header — the convention uGUI's own anchoredPosition is designed around.
            rect.pivot = anchor;
            rect.anchoredPosition = positionPx;
            rect.sizeDelta = sizePx;
            rect.localScale = Vector3.one;
            return text;
        }

        private static bool IsFinite(float value) => UnitPickMath.IsFinite(value);

        private void ApplyFont()
        {
            if (_canvasObject == null)
            {
                return;
            }

            var texts = _canvasObject.GetComponentsInChildren<Text>(true);
            for (var index = 0; index < texts.Length; index++)
            {
                texts[index].font = ResolveFont(font);
            }
        }

        /// <summary>
        /// <paramref name="preferred"/> when content has supplied a font, and the built-in
        /// legacy runtime font when it has not.
        ///
        /// A <see cref="Text"/> with no font generates no geometry, so the null-font HUD is
        /// not merely unstyled — every label, count and stat in it is invisible, and the
        /// player reads that as a broken panel rather than as art that has not arrived.
        /// Resolved once and cached because a HUD builds its labels one by one, and asking
        /// <c>Resources</c> per widget would repeat the same lookup for the same font.
        /// Public because <see cref="SelectionHudPresenter"/> builds its rows on the same
        /// canvas family and must not disagree about what a readable label is.
        /// </summary>
        public static Font ResolveFont(Font preferred)
        {
            // Unity's comparison, not the built-in one: a font asset content handed back
            // can have been destroyed, and a destroyed reference still feeds a Text that
            // draws nothing.
            if (preferred != null)
            {
                return preferred;
            }

            if (_builtinFont == null)
            {
                _builtinFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            return _builtinFont;
        }
    }
}
