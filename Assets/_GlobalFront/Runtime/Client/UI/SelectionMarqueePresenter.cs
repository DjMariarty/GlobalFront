using UnityEngine;
using UnityEngine.UI;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// Draws the selection marquee on its own transient canvas (Phase 3, step 3.5,
    /// ADR-012/OD-24).
    ///
    /// OD-24 fixes the HUD at three canvases — permanent, dynamic, and a transient one
    /// for the drag box — and this is the third. The reason a marquee cannot share the
    /// dynamic canvas is rebuild cost: a canvas batch rebuilds every graphic that
    /// references it whenever any of them changes, so a rectangle that is resized on
    /// every frame of a drag would dirty the whole selection panel four to eight times
    /// a second along with it. Isolated here, one <see cref="Image"/> is the only thing
    /// that ever rebuilds.
    ///
    /// One image is also the only geometry, which is what lets the per-frame
    /// cost be two <see cref="RectTransform"/> writes and nothing else. No
    /// <see cref="GameObject"/> is created or destroyed while dragging, and no
    /// material, texture or mesh is touched.
    ///
    /// <b>What it draws without content.</b> A 9-slice border is content, and a drag box
    /// that waits for it would be a rubber band the player cannot see during the very
    /// first matches they play. Without art the widget falls back to a four-pixel white
    /// sprite drawn <see cref="Image.Type.Simple"/> and tinted by
    /// <see cref="frameColor"/> — one translucent quad, stretched to the rectangle the
    /// pointer describes. <see cref="Image.Type.Sliced"/> is only asked for once there is
    /// a bordered sprite to slice, because slicing a sprite with no borders produces the
    /// same stretched fill with nine times the vertices.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SelectionMarqueePresenter : MonoBehaviour
    {
        /// <summary>
        /// Above the permanent HUD and the dynamic selection panel, so a marquee is
        /// never drawn underneath the thing it is about to select.
        /// </summary>
        public const int DefaultSortingOrder = 1000;

        [Header("Content (optional; assigned by the HUD scene)")]
        [SerializeField] private Sprite frameSprite;

        // A translucent cyan-blue: blue reads as "mine" against the green of friendly
        // units and the terrain, and 35% keeps the squad inside the box visible while it
        // is being boxed.
        [SerializeField] private Color frameColor = new Color(0.25f, 0.75f, 1f, 0.35f);
        [SerializeField] private int sortingOrder = DefaultSortingOrder;

        private GameObject _canvasObject;
        private Canvas _canvas;
        private Image _frame;
        private RectTransform _frameRect;
        private bool _isVisible;

        /// <summary>
        /// The untextured drag box. Shared by every marquee in the process because it is
        /// a four-pixel patch of the engine's own white texture, and a per-canvas copy
        /// would be a native object leaked on every HUD rebuild.
        /// </summary>
        private static Sprite _fallbackMarqueeSprite;

        /// <summary>True once the transient canvas and its single frame image exist.</summary>
        public bool IsBuilt => _frame != null;

        /// <summary>True while the marquee is on screen.</summary>
        public bool IsVisible => _isVisible;

        /// <summary>
        /// Creates the transient canvas and its one sliced image, once.
        ///
        /// Idempotent: the drag loop calls nothing else, and a second build would leak
        /// a canvas that keeps drawing the previous frame.
        /// </summary>
        /// <returns>True when the presenter is usable after the call.</returns>
        public bool Build()
        {
            if (_frame != null)
            {
                return true;
            }

            _canvasObject = new GameObject("SelectionMarqueeCanvas");
            _canvasObject.transform.SetParent(transform, false);
            _canvasObject.layer = gameObject.layer;

            _canvas = _canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = sortingOrder;

            var frameObject = new GameObject("SelectionFrame");
            frameObject.transform.SetParent(_canvasObject.transform, false);
            frameObject.layer = _canvasObject.layer;

            _frame = frameObject.AddComponent<Image>();
            _frameRect = frameObject.transform as RectTransform;

            // Bottom-left anchors and pivot, because that is the space the rectangle
            // arrives in: Camera.WorldToScreenPoint and ScreenPointToRay both count
            // pixels from the bottom-left, and so does an overlay canvas rect measured
            // from (0, 0). Any other pivot would need a per-frame subtraction for what
            // this setup gets for free.
            _frameRect.anchorMin = Vector2.zero;
            _frameRect.anchorMax = Vector2.zero;
            _frameRect.pivot = Vector2.zero;
            _frameRect.localScale = Vector3.one;

            _frame.type = frameSprite != null ? Image.Type.Sliced : Image.Type.Simple;
            _frame.sprite = ResolveFrameSprite(frameSprite);
            _frame.color = frameColor;

            // The marquee is a rubber band, not a control. Left clickable, it would
            // swallow the very release that is about to end the drag.
            _frame.raycastTarget = false;
            _frame.enabled = false;
            _isVisible = false;
            return true;
        }

        /// <summary>
        /// Places the marquee. Steady-state allocation free, including while dragging.
        ///
        /// Takes the rectangle rather than the two pointer positions because
        /// normalising them is the controller's job and doing it twice would let the
        /// drawn box and the selected units disagree if either copy were edited.
        /// </summary>
        public void Present(bool visible, Rect screenRectPx)
        {
            if (_frame == null)
            {
                return;
            }

            // A rect the presenter cannot draw with is a hidden marquee, not a
            // half-drawn one. RectTransform accepts NaN silently and then reports a
            // degenerate layout for every later frame of the drag — and the value sticks,
            // so the box is broken for the next one too.
            if (visible && !IsDrawable(screenRectPx))
            {
                visible = false;
            }

            if (visible)
            {
                _frameRect.anchoredPosition = new Vector2(screenRectPx.xMin, screenRectPx.yMin);
                _frameRect.sizeDelta = new Vector2(screenRectPx.width, screenRectPx.height);
            }

            // Toggled on change only: assigning a Graphic's enabled flag dirties its
            // batch for rebuild even when it was already set that way, which is a
            // rebuild per frame for a box that has not moved.
            if (visible != _isVisible)
            {
                _isVisible = visible;
                _frame.enabled = visible;
            }
        }

        /// <summary>
        /// Whether a rectangle can be written into a <see cref="RectTransform"/>: four
        /// finite corners and a non-negative size. <see cref="Rect"/> normalises a
        /// negative width into its <c>xMax</c>, so a box measured backwards is a box
        /// that draws somewhere other than where the player dragged.
        /// </summary>
        private static bool IsDrawable(Rect rect)
        {
            var width = rect.width;
            var height = rect.height;
            return float.IsFinite(rect.xMin) && float.IsFinite(rect.yMin) &&
                   float.IsFinite(width) && float.IsFinite(height) &&
                   width >= 0f && height >= 0f;
        }

        /// <summary>Assigns the 9-slice border sprite content supplies, at load.</summary>
        public void SetFrameSprite(Sprite sprite)
        {
            frameSprite = sprite;
            if (_frame != null)
            {
                // Sprite and type travel together: content arriving turns the stretched
                // fill into a bordered box, and content being taken away (a reconnect that
                // hands back nothing) has to turn it back rather than leave a sliced
                // nothing on screen.
                _frame.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
                _frame.sprite = ResolveFrameSprite(sprite);
            }
        }

        /// <summary>
        /// <paramref name="preferred"/> when content has supplied a border, and a shared
        /// four-pixel patch of the engine's white texture when it has not.
        /// </summary>
        private static Sprite ResolveFrameSprite(Sprite preferred)
        {
            // Unity's comparison rather than the built-in one: a sprite content handed back
            // can already have been destroyed, and the reference still reads as "supplied".
            if (preferred != null)
            {
                return preferred;
            }

            if (_fallbackMarqueeSprite == null)
            {
                _fallbackMarqueeSprite = Sprite.Create(
                    Texture2D.whiteTexture,
                    new Rect(0f, 0f, 4f, 4f),
                    new Vector2(0.5f, 0.5f));
                _fallbackMarqueeSprite.name = "GlobalFront Marquee Fill";
                _fallbackMarqueeSprite.hideFlags = HideFlags.DontSave;
            }

            return _fallbackMarqueeSprite;
        }

        /// <summary>
        /// Tears down what <see cref="Build"/> created. The presenter's own GameObject
        /// survives — a caller may keep driving <see cref="Present"/> and it stays a
        /// no-op until built again.
        /// </summary>
        public void Release()
        {
            _frame = null;
            _frameRect = null;
            _canvas = null;
            _isVisible = false;

            if (_canvasObject != null)
            {
#if UNITY_EDITOR
                Object.DestroyImmediate(_canvasObject);
#else
                Object.Destroy(_canvasObject);
#endif
                _canvasObject = null;
            }
        }

        private void OnDestroy() => Release();
    }
}
