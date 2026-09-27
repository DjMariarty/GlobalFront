using System;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using UnityEngine;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// What a radar blip stands for (Phase 3, step 3.6, ADR-012/OD-24).
    ///
    /// Three values rather than two flags because the player has to be able to see
    /// the third case at a glance: their own units, someone else's units, and the
    /// subset of their own units they have selected. Selected is a separate value
    /// rather than an extra bit so a blip can never be drawn as an unselected
    /// friendly that the selection says otherwise about — the state <em>is</em> the
    /// colour, and the two cannot disagree.
    /// </summary>
    public enum MinimapBlipKind : byte
    {
        /// <summary>A live unit owned by the local player.</summary>
        Friendly = 0,

        /// <summary>A live unit owned by anyone else.</summary>
        Enemy = 1,

        /// <summary>A live unit owned by the local player and in their selection.</summary>
        Selected = 2,
    }

    /// <summary>
    /// One blip of the last radar rebuild: where it sits in radar UV space, and
    /// which of the three colours it earns.
    ///
    /// UV rather than a pixel position because the widget's size on screen is
    /// layout, and a blip list expressed in the layout's units would have to be
    /// rebuilt every time the HUD was resized. UV is also the space the baked
    /// terrain sprite is indexed in (OD-24), so a blip and the ground it sits on
    /// are addressed with the same numbers.
    /// </summary>
    public readonly struct MinimapBlip
    {
        public MinimapBlip(Vector2 uv, MinimapBlipKind kind, int slot, ulong entity)
        {
            Uv = uv;
            Kind = kind;
            Slot = slot;
            Entity = entity;
        }

        /// <summary>Position on the radar, [0..1] from the southwest corner.</summary>
        public Vector2 Uv { get; }

        /// <summary>Which of the three colours this blip is drawn with.</summary>
        public MinimapBlipKind Kind { get; }

        /// <summary>Replication slot the blip was read from, for a caller that wants the live state back.</summary>
        public int Slot { get; }

        /// <summary>
        /// Entity the blip represents. Carried so a click on a blip can name the
        /// unit it was made from without a second pass over the world table, and so
        /// a test can assert <em>which</em> unit produced a position rather than
        /// only that some unit did.
        /// </summary>
        public ulong Entity { get; }
    }

    /// <summary>
    /// The 30 Hz radar: which units are alive right now, where they are, and whose
    /// they are (Phase 3, step 3.6, ADR-012/OD-24 &amp; OD-27).
    ///
    /// <b>Why a model rather than just a widget.</b> OD-24 bans the obvious
    /// implementation — a second URP camera looking down at the battlefield — and
    /// prescribes a baked terrain image with a lightweight blip overlay refreshed at
    /// 30 Hz. Everything that decides <em>what</em> the overlay shows is therefore
    /// plain data: a slot walk, a comparison against the local player, a projection
    /// into UV. That part lives here, allocation-free and with no reference to
    /// <see cref="UnityEngine.Object"/>, so the exact positions, the exact
    /// classifications and the exact cadence are all assertable in EditMode. The
    /// widget is the part that cannot be, and it is deliberately thin.
    ///
    /// <b>Why it reads the replication table rather than the views.</b> Ownership is
    /// authoritative state and <see cref="UnitView"/> caches none of it (step 3.3);
    /// a radar that inferred owner from a hull prefab would be a client-side guess
    /// rendered in the colour the player trusts to tell friend from foe. Position
    /// comes from the same table, which means blips move on the 10 Hz replication
    /// cadence rather than on the interpolation ring — at a 128-cell grid over a
    /// 400 m map a cell is about three metres, so the ring's smoothing is below the
    /// resolution the widget has to begin with.
    ///
    /// <b>Why 30 Hz and why a clock argument.</b> The throttle is the point of the
    /// type: between refreshes a frame has to do one comparison and leave. Passing
    /// the clock in rather than reading <see cref="Time.unscaledTime"/> is what makes
    /// "did it skip the frame or not" a testable statement, and is the same
    /// convention <see cref="UnitSelectionController"/> already keeps for its
    /// double-click window.
    /// </summary>
    public sealed class MinimapRadarModel
    {
        /// <summary>
        /// The cadence OD-24 fixes for the blip overlay: half the frame rate of a
        /// 60 FPS client, and three times the 10 Hz the replication stream runs at,
        /// so every replicated packet reaches the radar inside one of its own ticks
        /// without the widget ever being the thing that lags.
        /// </summary>
        public const double DefaultUpdateIntervalSeconds = 1.0 / 30.0;

        /// <summary>
        /// Lower bound on the throttle. A radar that refreshes at the frame rate is
        /// the second-camera cost OD-24 removed, arriving back through a settings
        /// field, so the interval is clamped rather than trusted.
        /// </summary>
        public const double MinimumUpdateIntervalSeconds = 1.0 / 120.0;

        /// <summary>Upper bound, past which the overlay stops reading as a radar and starts reading as frozen.</summary>
        public const double MaximumUpdateIntervalSeconds = 1.0;

        /// <summary>
        /// The slot an out-of-range blip read reports. Same sentinel as
        /// <see cref="UnitSelectionController.GetSelectedSlot"/>, because it answers the
        /// same question, and a 0 here would name slot 0 — a real slot.
        /// </summary>
        public const int NoSlot = -1;

        private readonly ClientReplicationWorld _world;
        private readonly UnitSelectionController _selection;
        private readonly MinimapBlip[] _blips;

        private MinimapBounds _bounds;
        private double _updateIntervalSeconds = DefaultUpdateIntervalSeconds;
        private int _blipCount;
        private Rect _normalizedCameraView;
        private bool _hasCameraView;

        /// <summary>
        /// Wall clock at which the next rebuild is allowed. Seeded to zero so the
        /// very first <see cref="Advance"/> rebuilds — a radar that waits a thirtieth
        /// of a second before showing anything is a blank widget at match start.
        /// </summary>
        private double _nextRefreshSeconds;

        private double _lastRefreshSeconds;

        public MinimapRadarModel(
            ClientReplicationWorld world,
            UnitSelectionController selection,
            MinimapBounds bounds = default,
            int capacity = 0)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (selection == null)
            {
                throw new ArgumentNullException(nameof(selection));
            }

            _world = world;
            _selection = selection;
            _bounds = bounds == default ? MinimapBounds.Default : bounds;

            // One blip per replicated unit is the whole array. Sized to the table
            // rather than to LiveCount because the count changes every packet and a
            // radar that had to grow mid-match would be an allocation in exactly the
            // combat spike OD-28 is trying to keep flat.
            var slots = world.Capacity;
            _blips = new MinimapBlip[capacity > 0 && capacity < slots ? capacity : slots];
        }

        /// <summary>The world extent the radar maps.</summary>
        public MinimapBounds Bounds => _bounds;

        /// <summary>Who is local, read through the selection so a reconnect updates the radar's colours with it.</summary>
        public PlayerId LocalPlayerId => _selection.LocalPlayerId;

        /// <summary>
        /// Seconds between rebuilds. Sanitised rather than validated: this arrives
        /// from a settings screen, and a NaN here would silently freeze the radar
        /// forever (<c>now &gt;= NaN</c> is false for every frame), which is a worse
        /// failure than a slider that snaps back to the default.
        /// </summary>
        public double UpdateIntervalSeconds
        {
            get => _updateIntervalSeconds;
            set => _updateIntervalSeconds = !double.IsNaN(value) && !double.IsInfinity(value) &&
                                           value >= MinimumUpdateIntervalSeconds &&
                                           value <= MaximumUpdateIntervalSeconds
                ? value
                : DefaultUpdateIntervalSeconds;
        }

        /// <summary>Rebuilds performed since construction.</summary>
        public int RefreshCount { get; private set; }

        /// <summary>Blips the last rebuild produced.</summary>
        public int BlipCount => _blipCount;

        /// <summary>Blips the array can hold, which is what a renderer sizes its own buffers to.</summary>
        public int BlipCapacity => _blips.Length;

        /// <summary>Blips dropped because the array was full: non-zero means the capacity argument was too small.</summary>
        public int SkippedBlipCount { get; private set; }

        /// <summary>Clock of the last rebuild, so a caller can see the cadence it produced.</summary>
        public double LastRefreshSeconds => _lastRefreshSeconds;

        /// <summary>True once a camera viewport rectangle has been supplied.</summary>
        public bool HasCameraView => _hasCameraView;

        /// <summary>
        /// The camera's ground footprint in radar UV, [0..1] with Y along world Z.
        /// Meaningless while <see cref="HasCameraView"/> is false.
        /// </summary>
        public Rect NormalizedCameraView => _normalizedCameraView;

        /// <summary>The last rebuild's blips, in ascending replication-slot order.</summary>
        public ReadOnlySpan<MinimapBlip> Blips => new ReadOnlySpan<MinimapBlip>(_blips, 0, _blipCount);

        /// <summary>
        /// Blip <paramref name="index"/>; an empty blip (entity 0, slot
        /// <c>-1</c>) when out of range. The slot sentinel is
        /// <see cref="UnitSelectionController.GetSelectedSlot"/>'s own "no slot", so a
        /// consumer walking past the end gets the same "nothing here" answer the
        /// selection layer already gives rather than a plausible-looking slot 0.
        /// </summary>
        public MinimapBlip GetBlip(int index) =>
            (uint)index < (uint)_blipCount
                ? _blips[index]
                : new MinimapBlip(default, default, NoSlot, 0);

        /// <summary>
        /// Replaces the mapped extent. <see langword="false"/> and unchanged when the
        /// extent is zero or inverted — a <see cref="MinimapBounds"/> built through
        /// its own constructor cannot be either, so the case this guards is the
        /// <c>default</c> struct a caller got hold of by forgetting to assign one.
        /// Every mapping downstream divides by the extent, so refusing the write is
        /// the only safe answer.
        /// </summary>
        public bool TrySetBounds(MinimapBounds bounds)
        {
            if (bounds.ExtentXmm <= 0 || bounds.ExtentZmm <= 0)
            {
                return false;
            }

            _bounds = bounds;
            return true;
        }

        /// <summary>
        /// Advances the radar clock. Returns true when this call rebuilt the blips.
        ///
        /// The whole steady-state cost of a frame between ticks is the one
        /// comparison below — the slot walk does not run, nothing is written, and
        /// the widget keeps the picture it was given. That is what makes "30 Hz"
        /// more than a comment: a radar that walked the table every frame and threw
        /// the result away would look identical on screen and fail the budget.
        /// </summary>
        public bool Advance(double nowSeconds)
        {
            if (!IsFinite(nowSeconds))
            {
                // A clock that is not a number says nothing about whether time has
                // passed, and the comparison below would answer false forever. Hold
                // the schedule rather than rebuild: rebuilding every poisoned frame
                // would turn a bad clock into an unthrottled one.
                return false;
            }

            if (nowSeconds < _lastRefreshSeconds)
            {
                // The clock went backwards — a resync (OD-20) re-baselining a match
                // clock, or a caller that restarted its own timer. Resynchronise to
                // it and rebuild now, because the alternative is a frozen radar for
                // as long as the gap that opened was.
                _nextRefreshSeconds = nowSeconds;
            }
            else if (nowSeconds < _nextRefreshSeconds)
            {
                return false;
            }

            Rebuild();
            _lastRefreshSeconds = nowSeconds;
            _nextRefreshSeconds = nowSeconds + _updateIntervalSeconds;
            return true;
        }

        /// <summary>
        /// Rebuilds the blip set from the replication table right now, ignoring the
        /// throttle. Called by <see cref="Advance"/> and by anything that needs the
        /// radar correct on the frame it changed — a match start, a resync, the
        /// frame the player switches map bounds.
        /// </summary>
        /// <returns>How many blips the rebuild produced.</returns>
        public int Rebuild()
        {
            var localPlayer = _selection.LocalPlayerId;
            var bounds = _bounds;
            var blips = _blips;
            var count = 0;
            var skipped = 0;

            // One ascending sweep of the slot table, so the blip order is a function
            // of the table alone. Ordering by screen position would be prettier to
            // read in a test, but it would make the array depend on where units
            // happen to be, and the widget's draw order — which pixel wins when two
            // blips land in the same cell — would change between frames.
            for (var slot = 0; slot < _world.Capacity; slot++)
            {
                if (!_world.TryGetSlotState(slot, out var state))
                {
                    continue;
                }

                // A unit at zero hit points is a corpse the client is still holding
                // for its death presentation, not a combatant. Showing it would put a
                // blip on the radar for a unit no command can address.
                if (state.Health <= 0)
                {
                    continue;
                }

                if (count >= blips.Length)
                {
                    skipped++;
                    continue;
                }

                var friendly = state.Owner == localPlayer;
                var kind = friendly && _selection.IsSelected(state.Entity)
                    ? MinimapBlipKind.Selected
                    : friendly ? MinimapBlipKind.Friendly : MinimapBlipKind.Enemy;

                blips[count++] = new MinimapBlip(
                    MinimapProjection.MillimetresToUv(bounds, state.PosX, state.PosZ),
                    kind,
                    slot,
                    state.Entity.Value);
            }

            _blipCount = count;
            SkippedBlipCount = skipped;
            RefreshCount++;
            return count;
        }

        /// <summary>
        /// Sets the viewport indicator from a world-space ground rectangle in
        /// metres — the <see cref="RtsCameraController.MapBounds"/> convention, X
        /// across and world Z down <c>Rect.y</c>.
        ///
        /// Takes the rectangle rather than a <see cref="Camera"/> so the model stays
        /// free of a rendering dependency and the indicator is assertable in
        /// EditMode, and so the frame that owns a camera can decide how it derives
        /// the footprint (see <see cref="MinimapProjection.TryCameraGroundRect"/>).
        ///
        /// A zero-area footprint is refused as firmly as a non-finite one: it is the
        /// same rectangle <see cref="MinimapProjection.GroundRectToUv"/> cannot map to
        /// anything meaningful, and the derived <c>xMax</c> is checked because a large
        /// finite <c>x</c> and <c>width</c> can overflow it to <c>+Infinity</c>.
        /// </summary>
        public void SetCameraViewGroundRect(Rect groundRectMetres)
        {
            if (!IsFinite(groundRectMetres.x) || !IsFinite(groundRectMetres.y) ||
                !IsFinite(groundRectMetres.width) || !IsFinite(groundRectMetres.height) ||
                !IsFinite(groundRectMetres.xMax) || !IsFinite(groundRectMetres.yMax) ||
                groundRectMetres.width <= 0f || groundRectMetres.height <= 0f)
            {
                _hasCameraView = false;
                _normalizedCameraView = default;
                return;
            }

            _normalizedCameraView = MinimapProjection.GroundRectToUv(_bounds, groundRectMetres);
            _hasCameraView = true;
        }

        /// <summary>Hides the viewport indicator, for a HUD with no camera to report.</summary>
        public void ClearCameraView()
        {
            _hasCameraView = false;
            _normalizedCameraView = default;
        }

        /// <summary>
        /// Drops the picture without touching the schedule: the frame after a
        /// resync should show the new world, and <see cref="Advance"/> is entitled to
        /// wait for its next tick because <see cref="Rebuild"/> is the caller's move.
        /// </summary>
        public void ClearBlips()
        {
            _blipCount = 0;
            SkippedBlipCount = 0;
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool IsFinite(float value) => UnitPickMath.IsFinite(value);
    }
}
