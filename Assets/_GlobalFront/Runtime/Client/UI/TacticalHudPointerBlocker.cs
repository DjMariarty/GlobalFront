using System;
using GlobalFront.Client.Presentation;
using UnityEngine;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// Which parts of the screen the tactical HUD owns, and therefore where a
    /// battlefield click must not land (Phase 3, step 3.6, ADR-012/OD-24).
    ///
    /// <b>Why this exists at all.</b> The RTS pointer and the HUD pointer are the same
    /// pointer. <see cref="GlobalFront.Client.Presentation.UnitSelectionDriver"/> reads
    /// one mouse position and asks the ground what is under it, so without a gate a
    /// click on the minimap also reads as a click on the terrain behind the minimap:
    /// the player drags a selection box across the radar and clears their squad, or
    /// right-clicks to move a unit across the map and issues a second move to the
    /// world coordinate that happens to sit under the widget. Both are invisible in a
    /// screenshot and unmistakable in a fight.
    ///
    /// <b>Why rectangles rather than uGUI raycasting.</b> The alternative is
    /// <c>EventSystem.current.IsPointerOverGameObject()</c>, which walks every
    /// <c>Graphic</c>'s hit test. This runs on every frame for every pointer query,
    /// including the frames nobody clicks, and OD-28's budget is a per-frame one. A
    /// handful of axis-aligned comparisons against cached corners is the same answer
    /// for a HUD made of panels, and it costs nothing.
    ///
    /// <b>Entry lifetime.</b> A presenter registers a rectangle when its panel is built
    /// and frees the handle when the panel goes away. Slots are never compacted,
    /// because the handle is the only thing a presenter holds and shifting them would
    /// silently repoint one panel's release at another's rectangle.
    /// </summary>
    public sealed class TacticalHudPointerBlocker
    {
        /// <summary>
        /// Panels the HUD actually has: minimap, command bar, selection panel and
        /// match header leave room for two more without a resize.
        /// </summary>
        public const int DefaultCapacity = 6;

        /// <summary>
        /// Ceiling on registered panels. Past a few dozen the HUD is no longer a set of
        /// panels but a wall, and a caller that registered a rectangle per frame would
        /// turn the gate into the cost it exists to prevent.
        /// </summary>
        public const int MaxCapacity = 32;

        private readonly Rect[] _rects;
        private readonly RectTransform[] _sources;
        private readonly bool[] _blocking;
        private readonly bool[] _used;
        private readonly Vector3[] _corners = new Vector3[4];

        private int _usedCount;
        private int _blockingCount;

        public TacticalHudPointerBlocker(int capacity = DefaultCapacity)
        {
            if (capacity <= 0 || capacity > MaxCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    $"HUD rectangle capacity must be in [1, {MaxCapacity}].");
            }

            _rects = new Rect[capacity];
            _sources = new RectTransform[capacity];
            _blocking = new bool[capacity];
            _used = new bool[capacity];
        }

        /// <summary>Slots taken by a presenter, blocking or not.</summary>
        public int UsedCount => _usedCount;

        /// <summary>Slots a pointer inside which is refused.</summary>
        public int BlockingCount => _blockingCount;

        /// <summary>Whether the gate has anything to refuse.</summary>
        public bool IsEmpty => _blockingCount == 0;

        /// <summary>Capacity of the rectangle table.</summary>
        public int Capacity => _rects.Length;

        /// <summary>
        /// Registers a panel by its on-screen rectangle, in screen pixels measured from
        /// the bottom-left — the space <c>Input.mousePosition</c> and a screen-space
        /// overlay canvas both use.
        /// </summary>
        /// <returns>
        /// The entry's handle, for <see cref="Remove"/>, or -1 when the table is full.
        /// A full table is a HUD bug rather than a runtime condition: it means
        /// something is registering a panel per frame.
        /// </returns>
        public int AddScreenRect(Rect screenRectPx)
        {
            var slot = AllocateSlot();
            if (slot < 0)
            {
                return -1;
            }

            _rects[slot] = screenRectPx;
            SetBlocking(slot, HasArea(screenRectPx));
            return slot;
        }

        /// <summary>
        /// Registers a panel by its <see cref="RectTransform"/>, so a widget that moves,
        /// resizes or is hidden by a layout pass blocks exactly what the player can see
        /// rather than what was true when the HUD was built.
        /// </summary>
        /// <inheritdoc cref="AddScreenRect"/>
        public int AddRectTransform(RectTransform source)
        {
            if (source == null)
            {
                return -1;
            }

            var slot = AllocateSlot();
            if (slot < 0)
            {
                return -1;
            }

            _sources[slot] = source;
            _rects[slot] = ReadScreenRect(source);
            SetBlocking(slot, HasArea(_rects[slot]) && source.gameObject.activeInHierarchy);
            return slot;
        }

        /// <summary>
        /// Overwrites a registered rectangle. False when the handle names a
        /// transform-backed entry, whose rectangle is derived rather than supplied.
        /// </summary>
        public bool SetScreenRect(int handle, Rect screenRectPx)
        {
            if (!IsLive(handle) || _sources[handle] != null)
            {
                return false;
            }

            _rects[handle] = screenRectPx;
            SetBlocking(handle, HasArea(screenRectPx));
            return true;
        }

        /// <summary>Frees a slot taken by <see cref="AddScreenRect"/> or <see cref="AddRectTransform"/>.</summary>
        public bool Remove(int handle)
        {
            if (!IsLive(handle))
            {
                return false;
            }

            SetBlocking(handle, false);
            _used[handle] = false;
            _usedCount--;
            _rects[handle] = default;
            _sources[handle] = null;
            return true;
        }

        /// <summary>The rectangle registered at <paramref name="handle"/>.</summary>
        public Rect GetScreenRect(int handle) => IsLive(handle) ? _rects[handle] : default;

        /// <summary>
        /// Re-reads every transform-backed rectangle and re-decides which entries are
        /// blocking. Call once per frame before querying.
        ///
        /// A hidden panel must stop blocking: the selection panel is empty most of the
        /// time the player is not selecting anything, and an invisible rectangle over
        /// the battlefield would swallow clicks for a widget that is not on screen.
        /// </summary>
        public void Refresh()
        {
            for (var index = 0; index < _rects.Length; index++)
            {
                if (!_used[index])
                {
                    continue;
                }

                var source = _sources[index];
                if (source == null)
                {
                    SetBlocking(index, HasArea(_rects[index]));
                    continue;
                }

                if (!source.gameObject.activeInHierarchy)
                {
                    SetBlocking(index, false);
                    continue;
                }

                _rects[index] = ReadScreenRect(source);
                SetBlocking(index, HasArea(_rects[index]));
            }
        }

        /// <summary>Clears every registration, for tearing the HUD down.</summary>
        public void Clear()
        {
            for (var index = 0; index < _rects.Length; index++)
            {
                _rects[index] = default;
                _sources[index] = null;
                _blocking[index] = false;
                _used[index] = false;
            }

            _usedCount = 0;
            _blockingCount = 0;
        }

        /// <summary>
        /// True when a screen position is inside a panel the HUD owns.
        ///
        /// A non-finite position is never over the HUD, which is the safe direction: the
        /// caller then hands the pointer to the selection controller, which has its own
        /// refusal for coordinates it cannot resolve a ground point from. The
        /// alternative — treating NaN as "over the HUD" — would silently disable the
        /// battlefield for whatever produced it.
        /// </summary>
        public bool IsPointerOverHud(Vector2 screenPixels)
        {
            if (float.IsNaN(screenPixels.x) || float.IsInfinity(screenPixels.x) ||
                float.IsNaN(screenPixels.y) || float.IsInfinity(screenPixels.y))
            {
                return false;
            }

            for (var index = 0; index < _rects.Length; index++)
            {
                if (!_blocking[index])
                {
                    continue;
                }

                var rect = _rects[index];

                // Inclusive on every edge. A one-pixel dead band along a panel border is
                // where a player's click lands more often than anywhere else, because it
                // is where the widget visibly ends.
                if (screenPixels.x >= rect.xMin && screenPixels.x <= rect.xMax &&
                    screenPixels.y >= rect.yMin && screenPixels.y <= rect.yMax)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Takes the lowest free slot. Reuse before extension so a HUD that rebuilds one
        /// panel on a reconnect cannot walk the table to its ceiling one release at a
        /// time.
        /// </summary>
        private int AllocateSlot()
        {
            for (var index = 0; index < _used.Length; index++)
            {
                if (_used[index])
                {
                    continue;
                }

                _used[index] = true;
                _usedCount++;
                return index;
            }

            return -1;
        }

        private bool IsLive(int handle) => (uint)handle < (uint)_used.Length && _used[handle];

        private void SetBlocking(int index, bool blocking)
        {
            if (_blocking[index] == blocking)
            {
                return;
            }

            _blocking[index] = blocking;
            _blockingCount += blocking ? 1 : -1;
        }

        /// <summary>
        /// A rectangle with no positive, finite area blocks nothing. The test is on the
        /// stored <c>width</c> rather than on <c>xMax &gt; xMin</c>: Unity 6 derives
        /// <c>xMax</c> as <c>x + width</c> and does not normalise a negative size, so
        /// <c>new Rect(100, 100, -50, -50)</c> reads <c>xMin = 100, xMax = 50</c> — an
        /// inverted panel, not a valid one standing somewhere else. The derived edges are
        /// finite-checked too because <c>x + width</c> can overflow a <see cref="float"/>
        /// to <c>+Infinity</c>, and a single infinite edge would otherwise make every
        /// pointer on one half of the screen read as a click on the HUD, which is the
        /// battlefield switched off for the rest of the match.
        /// </summary>
        private static bool HasArea(Rect rect) =>
            UnitPickMath.IsFinite(rect.x) && UnitPickMath.IsFinite(rect.y) &&
            UnitPickMath.IsFinite(rect.width) && UnitPickMath.IsFinite(rect.height) &&
            UnitPickMath.IsFinite(rect.xMax) && UnitPickMath.IsFinite(rect.yMax) &&
            rect.width > 0f && rect.height > 0f;

        /// <summary>
        /// The four corners of a <see cref="RectTransform"/> as a screen-pixel rectangle.
        /// For a screen-space overlay canvas — which is what OD-24's HUD canvases are —
        /// Unity's world corners <em>are</em> screen pixels, so no
        /// <c>RectTransformUtility</c> conversion is involved and none of the canvas
        /// scaling modes can be mis-modelled here.
        ///
        /// All four corners are folded into a min/max rather than reading the pair at
        /// index 0 and 2: those two are only the lower-left and upper-right of an
        /// unrotated, unmirrored panel, and a panel that is either of those things would
        /// otherwise yield an inverted — that is, degenerate and non-blocking — rectangle.
        /// </summary>
        private Rect ReadScreenRect(RectTransform source)
        {
            source.GetWorldCorners(_corners);
            if (!TryEnclosingRect(out var rect))
            {
                return default;
            }

            return rect;
        }

        /// <summary>
        /// The axis-aligned box around the four corners currently in <c>_corners</c>, or
        /// <see langword="false"/> when any corner is non-finite.
        ///
        /// A NaN corner has to poison the result rather than be skipped, because every
        /// comparison against NaN is false and the remaining three corners would describe
        /// a plausible panel.
        /// </summary>
        private bool TryEnclosingRect(out Rect rectPx)
        {
            rectPx = default;

            var minX = float.PositiveInfinity;
            var minY = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var maxY = float.NegativeInfinity;

            for (var corner = 0; corner < _corners.Length; corner++)
            {
                var point = _corners[corner];
                if (!UnitPickMath.IsFinite(point.x) || !UnitPickMath.IsFinite(point.y))
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
            return HasArea(rectPx);
        }
    }
}
