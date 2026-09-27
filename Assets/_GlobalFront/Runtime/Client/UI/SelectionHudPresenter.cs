using GlobalFront.Client.Presentation;
using GlobalFront.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// Canvas 2 of the three OD-24 fixes: the dynamic selection panel — how many units,
    /// of which archetypes, how healthy, and what the last order did
    /// (Phase 3, step 3.6, ADR-012/OD-24).
    ///
    /// <b>The contract this type is the enforcement point for.</b> OD-24 says the
    /// dynamic canvas must not format a string or dirty a graphic on a frame where the
    /// observed state did not change. Both halves are kept here rather than in
    /// <see cref="SelectionHudState"/>, because the state model's job is to know what is
    /// true and this one's is to decide whether anything the player can see differs from
    /// what was drawn last frame. The two mechanisms are:
    /// <list type="number">
    /// <item><description>every value is a <c>string</c> taken out of
    /// <see cref="HudStringTable"/>, so writing it is a table read and formatting never
    /// happens on a frame at all; and</description></item>
    /// <item><description>each field is compared to the reference already displayed, so
    /// a write happens exactly when a value changed — and because the table hands back
    /// one shared instance per value, reference equality <em>is</em> value
    /// equality.</description></item>
    /// </list>
    ///
    /// <b>Why a counter rather than a GC assertion alone.</b> <c>IsNot.AllocatingGCMemory</c>
    /// proves the allocation half of OD-24, but not the rebuild half: a panel that writes
    /// the same string into a <see cref="Text"/> every frame allocates nothing and still
    /// dirties the canvas batch thirty times a second, which is the cost the three-canvas
    /// isolation exists to stop. <see cref="TextMutationCount"/> is therefore the
    /// assertion that matters, and it is the reason the comparison is written down rather
    /// than folded into an assignment.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SelectionHudPresenter : MonoBehaviour
    {
        /// <summary>Row pitch, in pixels.</summary>
        public const float RowPitchPixels = 22f;

        [Header("Layout")]
        [SerializeField] private Vector2 panelPosition = new Vector2(16f, 296f);
        [SerializeField] private Vector2 panelSize = new Vector2(240f, 208f);
        [SerializeField] private int sortingOrder = PermanentHudPresenter.SelectionCanvasSortingOrder;

        [Header("Content (optional; assigned by the HUD scene)")]
        [SerializeField] private Sprite panelSprite;
        [SerializeField] private Font font;
        [SerializeField] private Color panelColor = new Color(0.07f, 0.09f, 0.12f, 0.86f);

        private GameObject _canvasObject;
        private Canvas _canvas;
        private RectTransform _panelRect;
        private Text _countValue;
        private Text _healthValue;
        private Text _commandValue;
        private Text _rejectionValue;
        private Text _issuedValue;
        private Text _refusedValue;
        private Text[] _kindLabels = new Text[UnitKinds.Count];
        private Text[] _kindValues = new Text[UnitKinds.Count];

        private SelectionHudState _state;
        private TacticalHudPointerBlocker _blocker;
        private int _blockerHandle = -1;

        private string _cachedCount;
        private string _cachedHealth;
        private string _cachedCommand;
        private string _cachedRejection;
        private string _cachedIssued;
        private string _cachedRefused;
        private readonly string[] _cachedKindCounts = new string[UnitKinds.Count];

        private long _textMutations;

        /// <summary>True once the canvas and its rows exist.</summary>
        public bool IsBuilt => _canvas != null;

        /// <summary>The model this panel displays.</summary>
        public SelectionHudState State => _state;

        /// <summary>
        /// Writes that reached a <see cref="Text"/> since construction. A steady-state
        /// frame must add zero to this, which is OD-24's contract for the dynamic canvas
        /// stated as something a test can assert on rather than as a claim in a comment.
        /// </summary>
        public long TextMutationCount => _textMutations;

        /// <summary>The panel's rect transform, for a caller that wants its screen footprint.</summary>
        public RectTransform PanelTransform => _panelRect;

        /// <summary>Selection size row, for tests and for a HUD that re-skins the panel.</summary>
        public Text CountValue => _countValue;

        /// <summary>Mean-health row.</summary>
        public Text HealthValue => _healthValue;

        /// <summary>Archetype count row for one kind.</summary>
        public Text GetKindValue(int kindIndex) =>
            (uint)kindIndex < (uint)_kindValues.Length ? _kindValues[kindIndex] : null;

        /// <summary>Archetype name row for one kind — the label half, never the count.</summary>
        public Text GetKindLabel(int kindIndex) =>
            (uint)kindIndex < (uint)_kindLabels.Length ? _kindLabels[kindIndex] : null;

        /// <summary>Creates the dynamic canvas and its rows, once. Idempotent, like every Build in this layer.</summary>
        public bool Build()
        {
            if (_canvas != null)
            {
                return true;
            }

            _canvasObject = new GameObject("SelectionHudCanvas", typeof(RectTransform));
            _canvasObject.transform.SetParent(transform, false);
            _canvasObject.layer = gameObject.layer;

            _canvas = _canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = sortingOrder;

            var panelObject = new GameObject("SelectionPanel", typeof(RectTransform));
            panelObject.transform.SetParent(_canvasObject.transform, false);
            panelObject.layer = _canvasObject.layer;

            var background = panelObject.AddComponent<Image>();
            background.color = panelColor;
            background.sprite = panelSprite;
            background.type = Image.Type.Sliced;
            background.raycastTarget = false;

            _panelRect = panelObject.transform as RectTransform;
            _panelRect.anchorMin = Vector2.zero;
            _panelRect.anchorMax = Vector2.zero;
            _panelRect.pivot = Vector2.zero;
            _panelRect.anchoredPosition = panelPosition;
            _panelRect.sizeDelta = panelSize;
            _panelRect.localScale = Vector3.one;

            var row = 0;
            _countValue = CreateRow(_panelRect, "Selected", row++, 64f).value;

            // The archetype rows are laid out for every kind the wire can carry, using
            // the kind's own name as a placeholder, because the panel is built before a
            // catalog has necessarily been chosen. Configure replaces the labels with
            // the display names the catalog actually resolves.
            var scoutRow = CreateRow(_panelRect, "Scout", row++, 48f);
            _kindLabels[UnitKinds.Scout] = scoutRow.label;
            _kindValues[UnitKinds.Scout] = scoutRow.value;

            var tankRow = CreateRow(_panelRect, "Tank", row++, 48f);
            _kindLabels[UnitKinds.Tank] = tankRow.label;
            _kindValues[UnitKinds.Tank] = tankRow.value;

            var structureRow = CreateRow(_panelRect, "Structure", row++, 48f);
            _kindLabels[UnitKinds.BaseStructure] = structureRow.label;
            _kindValues[UnitKinds.BaseStructure] = structureRow.value;

            _healthValue = CreateRow(_panelRect, "Health", row++, 160f).value;
            _commandValue = CreateRow(_panelRect, "Order", row++, 88f).value;
            _rejectionValue = CreateRow(_panelRect, "Result", row++, 128f).value;
            _issuedValue = CreateRow(_panelRect, "Issued", row++, 64f).value;
            _refusedValue = CreateRow(_panelRect, "Refused", row++, 88f).value;

            ResetCache();
            return true;
        }

        /// <summary>Supplies the model whose numbers this panel mirrors.</summary>
        public void Configure(SelectionHudState state)
        {
            _state = state;

            if (_canvas == null || state == null)
            {
                return;
            }

            // Archetype names go to the label column, never the value column: the value
            // is the count the frame loop owns, and writing a name into it leaves a
            // number that can never be rewritten, because the dirty check compares
            // against the cache rather than against the text now on screen.
            SetLabel(_kindLabels[UnitKinds.Scout], state.KindName(UnitKinds.Scout));
            SetLabel(_kindLabels[UnitKinds.Tank], state.KindName(UnitKinds.Tank));
            SetLabel(_kindLabels[UnitKinds.BaseStructure], state.KindName(UnitKinds.BaseStructure));

            // Re-configuring a panel — a different catalog, a different session, the
            // same HUD re-adopted after a reconnect — means every displayed number is
            // suspect, so the frame loop starts from an empty cache again.
            ResetCache();
        }

        /// <summary>Unity's frame loop for a panel placed in a scene; tests drive <see cref="ManualUpdate"/>.</summary>
        private void Update() => ManualUpdate();

        /// <summary>
        /// Registers the panel as HUD-owned, so a click on it is not also a click on the
        /// terrain. Idempotent: re-registering releases the previous handle first, or a
        /// panel adopted twice would leave an orphaned rectangle over the battlefield for
        /// the rest of the match.
        /// </summary>
        public void RegisterBlockingRects(TacticalHudPointerBlocker blocker)
        {
            if (blocker == null || _panelRect == null)
            {
                return;
            }

            ReleaseBlockingRect();

            _blocker = blocker;
            _blockerHandle = blocker.AddRectTransform(_panelRect);
        }

        private void ReleaseBlockingRect()
        {
            if (_blocker == null)
            {
                return;
            }

            if (_blockerHandle >= 0)
            {
                _blocker.Remove(_blockerHandle);
            }

            _blockerHandle = -1;
            _blocker = null;
        }

        /// <summary>
        /// Re-reads the model and writes only what changed. One call per frame, from
        /// whoever owns the frame loop.
        /// </summary>
        public void ManualUpdate()
        {
            if (_canvas == null || _state == null)
            {
                return;
            }

            _state.Update();

            ApplyTo(ref _cachedCount, _countValue, _state.CountText);
            ApplyTo(ref _cachedHealth, _healthValue, _state.HealthText);
            ApplyTo(ref _cachedCommand, _commandValue, _state.CommandText);
            ApplyTo(ref _cachedRejection, _rejectionValue, _state.RejectionText);
            ApplyTo(ref _cachedIssued, _issuedValue, _state.SubmittedCommandText);
            ApplyTo(ref _cachedRefused, _refusedValue, _state.RejectedCommandText);

            for (var kind = 1; kind < UnitKinds.Count; kind++)
            {
                ApplyTo(ref _cachedKindCounts[kind], _kindValues[kind], _state.KindCountText((byte)kind));
            }
        }

        /// <summary>The value currently displayed in the selection-size row.</summary>
        public string DisplayedCount => _cachedCount;

        /// <summary>The value currently displayed in the mean-health row.</summary>
        public string DisplayedHealth => _cachedHealth;

        /// <summary>Archetype count currently displayed for one kind.</summary>
        public string DisplayedKindCount(byte kind) =>
            (uint)kind < (uint)_cachedKindCounts.Length ? _cachedKindCounts[kind] : null;

        /// <summary>Drops the panel and every row with it.</summary>
        public void Release()
        {
            ReleaseBlockingRect();

            _panelRect = null;
            _countValue = null;
            _healthValue = null;
            _commandValue = null;
            _rejectionValue = null;
            _issuedValue = null;
            _refusedValue = null;
            _kindLabels = new Text[UnitKinds.Count];
            _kindValues = new Text[UnitKinds.Count];
            _canvas = null;
            ResetCache();

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
        /// Writes a value only when it is a different string than the one on screen.
        ///
        /// Reference rather than <c>string.Equals</c> because every value reaching here
        /// came out of <see cref="HudStringTable"/>, where one instance serves one value
        /// — so the two comparisons have the same answer, and the cheap one is the one
        /// that runs on every frame of a match.
        /// </summary>
        private void ApplyTo(ref string cached, Text target, string value)
        {
            if (target == null || ReferenceEquals(cached, value))
            {
                cached = value;
                return;
            }

            cached = value;
            target.text = value;
            _textMutations++;
        }

        private void ResetCache()
        {
            _cachedCount = null;
            _cachedHealth = null;
            _cachedCommand = null;
            _cachedRejection = null;
            _cachedIssued = null;
            _cachedRefused = null;
            for (var index = 0; index < _cachedKindCounts.Length; index++)
            {
                _cachedKindCounts[index] = null;
            }
        }

        /// <summary>
        /// Lays out one label/value row and hands back both halves. A row has two
        /// different writers: its label is an archetype name that comes from the catalog
        /// at <see cref="Configure"/>, and its value is a number that comes from the
        /// model on every frame. Keeping only the value is how the name ends up printed
        /// over the count.
        /// </summary>
        private (Text label, Text value) CreateRow(
            RectTransform parent,
            string label,
            int rowIndex,
            float valueOffset)
        {
            // Rows counted down from the panel's top rather than up from its bottom, so
            // adding a row appends at the bottom instead of shifting everything the
            // player has learned to read.
            var y = panelSize.y - (rowIndex + 1) * RowPitchPixels;

            return (
                CreateText("Label " + label, parent, new Vector2(8f, y), new Vector2(120f, 20f), label),
                CreateText(
                    "Value " + label,
                    parent,
                    new Vector2(valueOffset, y),
                    new Vector2(64f, 20f),
                    HudStringTable.NotAvailable));
        }

        private Text CreateText(string name, RectTransform parent, Vector2 positionPx, Vector2 sizePx, string text)
        {
            var textObject = new GameObject(name, typeof(RectTransform));
            textObject.transform.SetParent(parent, false);
            textObject.layer = parent.gameObject.layer;

            var component = textObject.AddComponent<Text>();
            component.fontSize = 16;
            component.color = Color.white;
            component.font = font;
            component.text = text;
            component.raycastTarget = false;

            var rect = textObject.transform as RectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = positionPx;
            rect.sizeDelta = sizePx;
            rect.localScale = Vector3.one;
            return component;
        }

        private static void SetLabel(Text target, string label)
        {
            if (target != null)
            {
                target.text = label;
            }
        }
    }
}
