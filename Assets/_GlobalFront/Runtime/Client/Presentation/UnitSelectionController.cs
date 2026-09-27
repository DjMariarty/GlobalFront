using System;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using UnityEngine;
using EntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// What a pointer press turned out to be once it released (step 3.5). Both
    /// candidates share the whole first half of their life — press, travel, hover —
    /// and differ only in distance, so the gesture is reported rather than requested.
    /// </summary>
    public enum SelectionGesture : byte
    {
        /// <summary>No press was outstanding.</summary>
        None = 0,

        /// <summary>Released inside the drag threshold: a single-unit click.</summary>
        Click = 1,

        /// <summary>Released past the drag threshold: a screen-space marquee.</summary>
        Marquee = 2,
    }

    /// <summary>
    /// Who the player currently has selected, and what clicking does to that set
    /// (Phase 3, step 3.5, ADR-012).
    ///
    /// A plain class, not a <see cref="UnityEngine.Object"/>, for the same reason
    /// <see cref="UnitViewBinder"/> and <see cref="UnitOverlayBatcher"/> are: it holds
    /// no scene state, its whole input is a slot table plus a projection, and standing
    /// it up in a test must not need a GameObject or a frame boundary.
    ///
    /// Two rules shape everything below.
    ///
    /// <b>No physics.</b> Picking and marquee are arithmetic over the binder's bound
    /// slots — see <see cref="UnitPickMath"/> for why a collider raycast cannot work
    /// against a pooled hull with an instanced overlay ring. Ownership and archetype
    /// come from <see cref="ClientReplicationWorld"/> rather than from the view, which
    /// deliberately caches none of either: <see cref="UnitView"/> is presentation
    /// state, and reading authority off the view would let a client-side guess decide
    /// whose units a command moves.
    ///
    /// <b>Canonical order.</b> The selection is stored sorted ascending by
    /// <see cref="EntityId.Value"/>, so the head of the array <em>is</em> the
    /// server-legal subset when a selection outgrows
    /// <see cref="SimulationConstants.MaxSelectedEntities"/>, and two clients with the
    /// same units selected send the same payload regardless of the order the player
    /// dragged through them.
    /// </summary>
    public sealed class UnitSelectionController
    {
        /// <summary>Pointer travel below which a press-release is a click, not a box.</summary>
        public const float DefaultDragThresholdPixels = 6f;

        /// <summary>
        /// Pick radius floor in metres. The prototype catalogue gives every archetype
        /// a 500 mm footprint, so without a floor a scout at zoomed-out scale is a few
        /// pixels wide and clicking it becomes a test of eyesight rather than of
        /// intent. One metre at the default 25 m camera height is roughly the width a
        /// unit occupies on screen.
        /// </summary>
        public const float DefaultMinimumPickRadiusMetres = 1f;

        /// <summary>Gap between two clicks on one unit that still counts as a double-click.</summary>
        public const double DefaultDoubleClickWindowSeconds = 0.30;

        /// <summary>
        /// Column spacing for a move formation, matching the prototype controller's
        /// <c>FormationSpacingMm</c>. <see cref="FormationSpec"/> requires a positive,
        /// even value, and the authoritative side validates the resulting layout.
        /// </summary>
        public const int DefaultFormationSpacingMillimetres = 3200;

        /// <summary>
        /// Squared compare tolerance for picking. Two units at genuinely identical
        /// positions produce bit-identical distances, so the tie-break has to fire on
        /// exact equality; this only absorbs the last bit of the multiply-add in the
        /// distance, where "closer" and "equidistant" would otherwise flip on noise.
        /// </summary>
        private const float DistanceTieEpsilon = 1e-6f;

        private const int NoSlot = -1;

        private readonly UnitViewBinder _binder;
        private readonly ClientReplicationWorld _world;

        /// <summary>Selected entities, ascending by value; <see cref="_selectedSlots"/> is parallel.</summary>
        private readonly ulong[] _selectedEntities;

        private readonly int[] _selectedSlots;

        /// <summary>
        /// Entity buffer handed to the sink. Sized to the server's ceiling rather than
        /// to the selection because no request may ever carry more, and refilling one
        /// fixed buffer keeps the command path out of the allocation pattern the
        /// pre-existing <see cref="PrototypeCommandQueue"/> has.
        /// </summary>
        private readonly EntityId[] _commandEntities =
            new EntityId[SimulationConstants.MaxSelectedEntities];

        private readonly int _capacity;

        private int _selectedCount;
        private uint _nextSequence = 1;
        private bool _pointerHeld;
        private Vector2 _pressOriginPx;
        private EntityId _hoveredEntity;

        private float _dragThresholdPixels = DefaultDragThresholdPixels;
        private float _minimumPickRadiusMetres = DefaultMinimumPickRadiusMetres;
        private double _doubleClickWindowSeconds = DefaultDoubleClickWindowSeconds;
        private int _formationSpacingMillimetres = DefaultFormationSpacingMillimetres;

        /// <summary>
        /// Identity of the last single-clicked unit, and when it happened. Kept as a
        /// raw entity value plus a caller-supplied clock rather than
        /// <see cref="Time.time"/>: the double-click rule has to be assertable in
        /// EditMode at an exact 0.29/0.31 second boundary, and a test cannot move
        /// <c>Time.time</c>.
        /// </summary>
        private ulong _lastClickEntity;
        private double _lastClickSeconds;

        public UnitSelectionController(
            UnitViewBinder binder,
            ClientReplicationWorld world,
            PlayerId localPlayer,
            int capacity = 0)
        {
            if (binder == null)
            {
                throw new ArgumentNullException(nameof(binder));
            }

            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            _binder = binder;
            _world = world;
            LocalPlayerId = localPlayer;

            // A selection can never hold more entries than there are slots to bind,
            // so the binder's own walk length is both the default and the hard ceiling.
            var slots = binder.SlotCount;
            _capacity = capacity > 0 && capacity < slots ? capacity : slots;
            _selectedEntities = new ulong[_capacity];
            _selectedSlots = new int[_capacity];
        }

        /// <summary>
        /// The player whose units are selectable and commandable. Assignable because
        /// the client learns it from the server at match start (ADR-008) and may be
        /// handed a different one after a reconnect.
        /// </summary>
        public PlayerId LocalPlayerId { get; set; }

        /// <summary>
        /// Drag distance in pixels that turns a click into a marquee. A non-finite or
        /// negative value falls back to the default rather than throwing: this is set
        /// from options screen data, and a poisoned field must not take selection down
        /// with it. Zero is legal — it means every pixel of travel is a box.
        /// </summary>
        public float DragThresholdPixels
        {
            get => _dragThresholdPixels;
            set => _dragThresholdPixels = SanitizeNonNegative(value, DefaultDragThresholdPixels);
        }

        /// <summary>
        /// Floor on the pick radius, so small footprints stay clickable. Sanitised like
        /// <see cref="DragThresholdPixels"/>; it is a compare bound, so a NaN would make
        /// every unit unclickable rather than throw.
        /// </summary>
        public float MinimumPickRadiusMetres
        {
            get => _minimumPickRadiusMetres;
            set => _minimumPickRadiusMetres = SanitizeNonNegative(value, DefaultMinimumPickRadiusMetres);
        }

        /// <summary>
        /// Double-click window in seconds. Sanitised like the others: the window is
        /// compared against a clock delta, so a NaN reads as "never a double-click".
        /// </summary>
        public double DoubleClickWindowSeconds
        {
            get => _doubleClickWindowSeconds;
            set => _doubleClickWindowSeconds = SanitizeNonNegative(value, DefaultDoubleClickWindowSeconds);
        }

        /// <summary>
        /// Column spacing a move formation is laid out with.
        ///
        /// Throws where the others fall back, because an out-of-range spacing is not a
        /// degraded preference but a guarantee <see cref="FormationSpec"/> and
        /// <c>MoveCommand.TryCreate</c> both enforce: a value outside
        /// <c>[2, MaxFormationSpacingMm]</c>, or an odd one, makes every move the server
        /// refuses, silently and for the rest of the match. A caller that sets it from
        /// data has to hear about that at the assignment.
        /// </summary>
        public int FormationSpacingMillimetres
        {
            get => _formationSpacingMillimetres;
            set
            {
                if (value <= 0 ||
                    value > SimulationConstants.MaxFormationSpacingMm ||
                    (value & 1) != 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        value,
                        $"formation spacing must be an even number of millimetres in " +
                        $"[2, {SimulationConstants.MaxFormationSpacingMm}].");
                }

                _formationSpacingMillimetres = value;
            }
        }

        /// <summary>True while the pointer is past the drag threshold with the button held.</summary>
        public bool IsDragging { get; private set; }

        /// <summary>
        /// The marquee rectangle in screen pixels, normalised so <c>xMin</c>/
        /// <c>yMin</c> are the lower corner. Meaningless while <see cref="IsDragging"/>
        /// is false.
        /// </summary>
        public Rect ScreenRectPx { get; private set; }

        /// <summary>Slot index of the last slot this controller selected into.</summary>
        public int SelectedCount => _selectedCount;

        /// <summary>Selected entity at <paramref name="index"/>, in ascending order.</summary>
        public EntityId GetSelectedEntity(int index) =>
            (uint)index < (uint)_selectedCount
                ? new EntityId(_selectedEntities[index])
                : default;

        /// <summary>Slot the selected entity at <paramref name="index"/> is presented through.</summary>
        public int GetSelectedSlot(int index) =>
            (uint)index < (uint)_selectedCount
                ? _selectedSlots[index]
                : NoSlot;

        /// <summary>Currently hovered friendly unit, or an invalid id.</summary>
        public EntityId HoveredEntity => _hoveredEntity;

        public bool HasHoveredUnit => _hoveredEntity.IsValid;

        /// <summary>True when the entity is part of the selection.</summary>
        public bool IsSelected(EntityId entity) =>
            entity.IsValid && IndexOfSelected(entity.Value) >= 0;

        /// <summary>
        /// Drops selections that no longer describe a live, owned, bound unit and
        /// clears their ring. Call once per frame before the overlay batch is built.
        ///
        /// The predicate is identity-plus-liveness rather than liveness alone: a
        /// replication slot is recycled, so the slot an entry names can come back
        /// holding a different unit. Clearing the flag only when the view still
        /// carries the entry's entity is what stops the new tenant from being switched
        /// off — or, worse, from being left on with a stranger's ring.
        /// </summary>
        public int PruneStaleSelection()
        {
            var write = 0;
            for (var index = 0; index < _selectedCount; index++)
            {
                var entity = _selectedEntities[index];
                var slot = _selectedSlots[index];
                if (IsStillSelectable(entity, slot))
                {
                    _selectedEntities[write] = entity;
                    _selectedSlots[write] = slot;
                    write++;
                    continue;
                }

                if (_binder.TryGetView(slot, out var view) && view.EntityValue == entity)
                {
                    view.SetSelected(false);
                }
            }

            var dropped = _selectedCount - write;
            _selectedCount = write;
            return dropped;
        }

        /// <summary>Clears the whole selection and every ring it was drawing.</summary>
        public void ClearSelection()
        {
            for (var index = 0; index < _selectedCount; index++)
            {
                if (_binder.TryGetView(_selectedSlots[index], out var view) &&
                    view.EntityValue == _selectedEntities[index])
                {
                    view.SetSelected(false);
                }
            }

            _selectedCount = 0;
        }

        /// <summary>
        /// Replaces the selection with the single unit under the pointer, or clears it
        /// when the pointer is on nothing selectable.
        /// </summary>
        public void SelectSingle(EntityId entity, int slot)
        {
            ClearSelection();
            AddSelected(entity, slot);
        }

        /// <summary>
        /// Adds a unit to the selection, leaving the rest of it alone.
        ///
        /// The slot has to be the one actually presenting that entity. Without the
        /// check, a caller working from a slot it captured before a recycling would put
        /// a ring on the stranger that took the slot — which is the exact failure this
        /// whole type is arranged to prevent, arriving through the front door.
        ///
        /// The same is true of everything <see cref="IsStillSelectable"/> states: this
        /// is the one entry point into <c>_selectedEntities</c>, so ownership and
        /// liveness are decided here rather than trusted from the caller. A public
        /// <c>AddSelected</c> that only checked identity would let a HUD button, or a
        /// test, put a ring on another player's tank and then have it ride along in the
        /// next Move payload.
        /// </summary>
        public void AddSelected(EntityId entity, int slot)
        {
            if (!entity.IsValid)
            {
                return;
            }

            if (!_binder.TryGetView(slot, out var view) || view.EntityValue != entity.Value)
            {
                return;
            }

            if (!IsStillSelectable(entity.Value, slot))
            {
                return;
            }

            var at = LowerBound(entity.Value);
            if (at < _selectedCount && _selectedEntities[at] == entity.Value)
            {
                return;
            }

            if (_selectedCount >= _capacity)
            {
                return;
            }

            var tail = _selectedCount - at;
            if (tail > 0)
            {
                Array.Copy(_selectedEntities, at, _selectedEntities, at + 1, tail);
                Array.Copy(_selectedSlots, at, _selectedSlots, at + 1, tail);
            }

            _selectedEntities[at] = entity.Value;
            _selectedSlots[at] = slot;
            _selectedCount++;

            view.SetSelected(true);
        }

        /// <summary>
        /// Removes a unit from the selection, as a shift-click on an already selected
        /// unit does. Returns whether anything was removed.
        /// </summary>
        public bool RemoveSelected(EntityId entity)
        {
            var index = IndexOfSelected(entity.Value);
            if (index < 0)
            {
                return false;
            }

            if (_binder.TryGetView(_selectedSlots[index], out var view) &&
                view.EntityValue == _selectedEntities[index])
            {
                view.SetSelected(false);
            }

            var tail = _selectedCount - index - 1;
            if (tail > 0)
            {
                Array.Copy(_selectedEntities, index + 1, _selectedEntities, index, tail);
                Array.Copy(_selectedSlots, index + 1, _selectedSlots, index, tail);
            }

            _selectedCount--;
            return true;
        }

        /// <summary>
        /// The pointer went down: arms a drag without committing to one. Nothing is
        /// selected here — a press that turns out to be a click is resolved on release,
        /// so a drag that starts on a unit and stays inside the deadzone still reads as
        /// a click on that unit rather than as a one-pixel box.
        /// </summary>
        public void BeginDrag(Vector2 screenPixels)
        {
            // A non-finite press never arms: _pressOriginPx is the reference every later
            // distance is measured from, so one bad pixel would keep the drag threshold
            // from ever resolving and leave the marquee rectangle poisoned for as long
            // as the button stayed down. The release then reads as no outstanding press,
            // which is the same answer as a click that never happened.
            if (!IsFinite(screenPixels))
            {
                _pointerHeld = false;
                IsDragging = false;
                return;
            }

            _pointerHeld = true;
            _pressOriginPx = screenPixels;
            IsDragging = false;
            ScreenRectPx = new Rect(screenPixels.x, screenPixels.y, 0f, 0f);
        }

        /// <summary>
        /// Per-frame pointer query: refreshes the hovered unit and, while the button is
        /// held, the marquee rectangle. Hot path, allocates nothing.
        /// </summary>
        public void UpdatePointer(Vector2 screenPixels, IUnitPointerProjector projector)
        {
            // Neither the hover nor an in-flight drag follows a pointer that has gone
            // non-finite, and the drag keeps whatever rectangle it last had: a HUD that
            // flickered a NaN would otherwise watch the box jump to the origin.
            if (!IsFinite(screenPixels))
            {
                _hoveredEntity = default;
                return;
            }

            if (projector == null)
            {
                _hoveredEntity = default;
                return;
            }

            _hoveredEntity = TryPickUnit(screenPixels, projector, friendlyOnly: true, out _);

            if (!_pointerHeld)
            {
                IsDragging = false;
                return;
            }

            if (!IsDragging)
            {
                var deltaX = screenPixels.x - _pressOriginPx.x;
                var deltaY = screenPixels.y - _pressOriginPx.y;
                if (deltaX * deltaX + deltaY * deltaY > DragThresholdPixels * DragThresholdPixels)
                {
                    IsDragging = true;
                }
            }

            if (IsDragging)
            {
                ScreenRectPx = NormalisedRect(_pressOriginPx, screenPixels);
            }
        }

        /// <summary>
        /// Resolves a press-release: a marquee if the pointer travelled past the
        /// threshold, a click on the release position otherwise. Both have to be
        /// decided here rather than by the caller, because the only thing that
        /// distinguishes them is travel the controller has been tracking since the
        /// press — a caller that guessed from <see cref="IsDragging" /> would clear the
        /// flag before the click could still be resolved.
        /// </summary>
        public SelectionGesture ResolveRelease(
            Vector2 screenPixels,
            bool additive,
            double nowSeconds,
            IUnitPointerProjector projector)
        {
            if (!_pointerHeld)
            {
                return SelectionGesture.None;
            }

            _pointerHeld = false;

            if (!IsFinite(screenPixels))
            {
                // A release the controller cannot locate says nothing about intent.
                // Deciding it would be a guess between a click at an invented pixel and
                // a box of NaN extents, and both of those destroy the selection.
                IsDragging = false;
                return SelectionGesture.None;
            }

            // IsDragging is written by UpdatePointer, and a caller that pressed and
            // released inside one frame — a fast click, or a driver that polls the
            // button edges rather than the held state — never gave it the travel to
            // notice. The gesture has to be re-derived from the press origin here, or a
            // 600 pixel drag releases as a click on its ending pixel and wipes the
            // selection the player was drawing a box around.
            if (!IsDragging)
            {
                var deltaX = screenPixels.x - _pressOriginPx.x;
                var deltaY = screenPixels.y - _pressOriginPx.y;
                if (deltaX * deltaX + deltaY * deltaY > DragThresholdPixels * DragThresholdPixels)
                {
                    IsDragging = true;
                }
            }

            if (!IsDragging)
            {
                OnPrimaryClick(screenPixels, additive, nowSeconds, projector);
                return SelectionGesture.Click;
            }

            IsDragging = false;
            ScreenRectPx = NormalisedRect(_pressOriginPx, screenPixels);
            SelectInsideScreenRect(ScreenRectPx, additive, projector);
            return SelectionGesture.Marquee;
        }

        /// <summary>
        /// Forgets the hovered unit without touching anything else.
        ///
        /// For the frames a pointer is not over the battlefield at all. Step 3.6's HUD
        /// gate is the caller: a pointer sitting on the minimap is not hovering the
        /// terrain behind it, and a hover that keeps tracking units the player cannot
        /// see through the panel would be a lie the first hover tooltip ever written
        /// would inherit.
        /// </summary>
        public void ClearHover() => _hoveredEntity = default;

        /// <summary>
        /// Abandons an in-flight press without deciding anything about it: no
        /// selection is cleared, no unit is added, no marquee is resolved.
        ///
        /// Needed because <see cref="ResolveRelease"/> is not the only way a press can
        /// end. A drag that started on the battlefield and travelled into the tactical
        /// HUD has to stop being a battlefield drag — the alternative, from step 3.6's
        /// pointer gate, is either a marquee that clears the squad behind the minimap
        /// or a press that stays armed for ever because nothing will release it.
        ///
        /// Clearing the selection here would be the bug this method exists to avoid:
        /// the player asked for that selection with the clicks they made on the
        /// battlefield, and a pointer that later drifted over a panel says nothing
        /// about wanting it gone.
        /// </summary>
        public void CancelDrag()
        {
            _pointerHeld = false;
            IsDragging = false;
            ScreenRectPx = default;
        }

        /// <summary>
        /// A plain click, with the whole click rule set applied: shift toggles, a
        /// double-click on the same unit takes every friendly unit of that archetype
        /// that is on screen, and a click on empty ground clears the selection.
        /// </summary>
        /// <returns>How many units the selection holds afterwards.</returns>
        public int OnPrimaryClick(
            Vector2 screenPixels,
            bool additive,
            double nowSeconds,
            IUnitPointerProjector projector)
        {
            // A pointer whose ground point cannot be resolved carries no information
            // about intent: the camera may be pitched at the sky, or the coordinates may
            // be NaN. Treating either as "clicked empty ground" would answer a lost
            // frame by wiping a hundred selected units, which is a worse outcome than
            // doing nothing at all.
            if (projector == null || !projector.TryGetGroundPoint(screenPixels, out _))
            {
                return _selectedCount;
            }

            var picked = TryPickUnit(screenPixels, projector, friendlyOnly: true, out var slot);
            if (!picked.IsValid)
            {
                // Empty ground. Shift holds the selection together: the modifier is the
                // player saying "keep what I have", so clicking off to the side while
                // holding it must not wipe the squad they are about to add to.
                if (!additive)
                {
                    ClearSelection();
                }

                _lastClickEntity = 0;
                return _selectedCount;
            }

            if (additive)
            {
                if (!RemoveSelected(picked))
                {
                    AddSelected(picked, slot);
                }

                _lastClickEntity = 0;
                return _selectedCount;
            }

            if (IsDoubleClick(picked, nowSeconds) &&
                _world.TryGetSlotState(slot, out var clickedState))
            {
                SelectKindOnScreen(clickedState.UnitKind, additive: false, projector);
                _lastClickEntity = 0;
                return _selectedCount;
            }

            _lastClickEntity = picked.Value;
            _lastClickSeconds = nowSeconds;
            SelectSingle(picked, slot);
            return _selectedCount;
        }

        /// <summary>
        /// Selects every live, friendly unit whose presented position projects inside
        /// the marquee. Enemy units are excluded by design — box selection commands a
        /// group, and the player cannot command another player's units, so including
        /// them would put a ring on units that must never be in a Move payload.
        /// </summary>
        /// <returns>How many units the selection holds afterwards.</returns>
        public int SelectInsideScreenRect(Rect screenRectPx, bool additive, IUnitPointerProjector projector)
        {
            if (projector == null)
            {
                return _selectedCount;
            }

            var minX = screenRectPx.xMin;
            var minY = screenRectPx.yMin;
            var maxX = screenRectPx.xMax;
            var maxY = screenRectPx.yMax;

            // Measured before the selection is dropped, because that order is the whole
            // point: a box the controller cannot describe is not a request to clear, and
            // a player whose drag produced a NaN should still have their squad.
            if (!IsFinite(minX) || !IsFinite(minY) || !IsFinite(maxX) || !IsFinite(maxY) ||
                maxX < minX || maxY < minY)
            {
                return _selectedCount;
            }

            if (!additive)
            {
                ClearSelection();
            }

            for (var slot = 0; slot < _binder.SlotCount; slot++)
            {
                if (!TryGetSelectable(slot, friendlyOnly: true, out var view, out var state))
                {
                    continue;
                }

                var screen = projector.ProjectToScreen(view.Hull.position);

                // Behind the camera is its own rejection rather than a consequence of
                // the rectangle test: WorldToScreenPoint mirrors a point that is behind
                // the lens into a plausible-looking pixel coordinate in front of it.
                if (!(screen.z > 0f))
                {
                    continue;
                }

                if (!UnitPickMath.IsInsideScreenRect(screen, minX, minY, maxX, maxY))
                {
                    continue;
                }

                AddSelected(state.Entity, slot);
            }

            return _selectedCount;
        }

        /// <summary>
        /// Selects every live, friendly unit of one archetype that is currently on
        /// screen — the double-click group select.
        /// </summary>
        /// <returns>How many units the selection holds afterwards.</returns>
        public int SelectKindOnScreen(byte unitKind, bool additive, IUnitPointerProjector projector)
        {
            if (projector == null)
            {
                return _selectedCount;
            }

            var screen = projector.ScreenSizePixels;

            // A projector that cannot state a viewport has no screen to test against, so
            // the select would clear the group and then add nothing back. Same ordering
            // rule as SelectInsideScreenRect: decide before destroying.
            if (!IsFinite(screen.x) || !IsFinite(screen.y) || screen.x <= 0f || screen.y <= 0f)
            {
                return _selectedCount;
            }

            if (!additive)
            {
                ClearSelection();
            }

            for (var slot = 0; slot < _binder.SlotCount; slot++)
            {
                if (!TryGetSelectable(slot, friendlyOnly: true, out var view, out var state))
                {
                    continue;
                }

                if (state.UnitKind != unitKind)
                {
                    continue;
                }

                var projected = projector.ProjectToScreen(view.Hull.position);
                if (!(projected.z > 0f))
                {
                    continue;
                }

                if (!UnitPickMath.IsInsideScreenRect(projected, 0f, 0f, screen.x, screen.y))
                {
                    continue;
                }

                AddSelected(state.Entity, slot);
            }

            return _selectedCount;
        }

        /// <summary>
        /// The secondary (right) button: attack the enemy under the pointer, otherwise
        /// move to the ground under it. Friendly units and empty ground both move, which
        /// is the Generals behaviour — clicking your own tank to move past it should not
        /// do nothing.
        /// </summary>
        /// <returns>True when a command was handed to the sink.</returns>
        public bool TryIssueCommandAtPointer(
            Vector2 screenPixels,
            IUnitPointerProjector projector,
            ulong requestedTick,
            IUnitCommandSink sink)
        {
            // No selection and no sink are both clean no-ops: an RTS that answers a
            // right-click on nothing with a malformed command teaches the player that
            // the click did something when it did nothing at all.
            if (sink == null || _selectedCount == 0 || projector == null || !LocalPlayerId.IsValid)
            {
                return false;
            }

            var target = TryPickUnit(screenPixels, projector, friendlyOnly: false, out var targetSlot);
            if (target.IsValid && IsEnemy(targetSlot))
            {
                return Emit(GameCommandType.Attack, default, target, requestedTick, sink);
            }

            if (!projector.TryGetGroundPoint(screenPixels, out var ground) ||
                !UnitPickMath.TryToMillimetrePoint(ground, out var destination))
            {
                return false;
            }

            return Emit(GameCommandType.Move, destination, default, requestedTick, sink);
        }

        /// <summary>
        /// Issues a stop for the whole selection (the S hotkey, and the API a HUD
        /// button calls).
        /// </summary>
        /// <returns>True when a command was handed to the sink.</returns>
        public bool IssueStop(ulong requestedTick, IUnitCommandSink sink)
        {
            if (sink == null || _selectedCount == 0 || !LocalPlayerId.IsValid)
            {
                return false;
            }

            return Emit(GameCommandType.Stop, default, default, requestedTick, sink);
        }

        /// <summary>
        /// Issues a move to an explicit millimetre destination. Public because the
        /// attack-move and rally-point paths of later steps need the same canonical
        /// payload without going through a pointer.
        /// </summary>
        /// <returns>True when a command was handed to the sink.</returns>
        public bool IssueMove(
            WorldPointMm destination,
            ulong requestedTick,
            IUnitCommandSink sink)
        {
            if (sink == null || _selectedCount == 0 || !LocalPlayerId.IsValid ||
                !destination.IsWithinSimulationBounds)
            {
                return false;
            }

            return Emit(GameCommandType.Move, destination, default, requestedTick, sink);
        }

        // ------------------------------------------------------------------- picking

        /// <summary>
        /// Nearest selectable unit whose footprint covers the pointer's ground point.
        /// Ties on distance go to the lower entity id, so two overlapping units resolve
        /// to the same one on every client rather than to whichever the slot table
        /// happened to reach first.
        /// </summary>
        private EntityId TryPickUnit(
            Vector2 screenPixels,
            IUnitPointerProjector projector,
            bool friendlyOnly,
            out int pickedSlot)
        {
            pickedSlot = NoSlot;
            if (projector == null ||
                !projector.TryGetGroundPoint(screenPixels, out var ground))
            {
                return default;
            }

            var bestEntity = 0UL;
            var bestDistance = 0f;
            var bestSlot = NoSlot;

            for (var slot = 0; slot < _binder.SlotCount; slot++)
            {
                if (!TryGetSelectable(slot, friendlyOnly, out var view, out var state))
                {
                    continue;
                }

                var position = view.Hull.position;
                var deltaX = position.x - ground.x;
                var deltaZ = position.z - ground.z;
                var distance = deltaX * deltaX + deltaZ * deltaZ;

                var reach = view.RadiusMillimetres * UnitView.MillimetresToMetres;
                if (reach < MinimumPickRadiusMetres)
                {
                    reach = MinimumPickRadiusMetres;
                }

                if (distance > reach * reach)
                {
                    continue;
                }

                if (bestSlot == NoSlot ||
                    distance < bestDistance - DistanceTieEpsilon ||
                    (distance <= bestDistance + DistanceTieEpsilon && state.Entity.Value < bestEntity))
                {
                    bestSlot = slot;
                    bestDistance = distance;
                    bestEntity = state.Entity.Value;
                }
            }

            pickedSlot = bestSlot;
            return bestSlot == NoSlot ? default : new EntityId(bestEntity);
        }

        /// <summary>
        /// A unit the pointer can act on right now: presented, alive on both the
        /// presented and the authoritative side, and the same entity in both.
        /// </summary>
        private bool TryGetSelectable(
            int slot,
            bool friendlyOnly,
            out UnitView view,
            out ClientUnitState state)
        {
            view = null;
            state = default;

            if (!_binder.TryGetView(slot, out var candidate) || !candidate.IsBound)
            {
                return false;
            }

            // The view's own readout, not just the authority's: a unit that just died
            // keeps its last interpolated pose for the death animation (OD-25 relies on
            // that), so until the table says otherwise the client is still drawing a
            // unit that is no longer selectable.
            if (candidate.Health <= 0)
            {
                return false;
            }

            if (!_world.TryGetSlotState(slot, out var slotState) ||
                slotState.Entity.Value != candidate.EntityValue ||
                slotState.Health <= 0)
            {
                return false;
            }

            if (friendlyOnly && slotState.Owner != LocalPlayerId)
            {
                return false;
            }

            view = candidate;
            state = slotState;
            return true;
        }

        private bool IsEnemy(int slot) =>
            _world.TryGetSlotState(slot, out var state) && state.Owner != LocalPlayerId;

        private bool IsStillSelectable(ulong entity, int slot)
        {
            if (!_binder.TryGetView(slot, out var view) ||
                !view.IsBound ||
                view.EntityValue != entity ||
                view.Health <= 0)
            {
                return false;
            }

            return _world.TryGetSlotState(slot, out var state) &&
                   state.Entity.Value == entity &&
                   state.Health > 0 &&
                   state.Owner == LocalPlayerId;
        }

        // ------------------------------------------------------------------- commands

        /// <summary>
        /// Freezes the current selection into one request: the head of an
        /// already-sorted array, so the subset the server sees is the lowest-numbered
        /// entities rather than an accident of drag order.
        ///
        /// Pruning is part of this rather than the callers' chore, because every one of
        /// them works from a selection it captured earlier: a unit that died, or whose
        /// slot was recycled since then, would otherwise be named in the payload, and
        /// the stranger's position would be read into the centroid that decides which
        /// way the formation faces.
        /// </summary>
        private bool Emit(
            GameCommandType type,
            WorldPointMm destination,
            EntityId target,
            ulong requestedTick,
            IUnitCommandSink sink)
        {
            PruneStaleSelection();

            var count = _selectedCount;
            if (count == 0)
            {
                return false;
            }

            if (count > _commandEntities.Length)
            {
                count = _commandEntities.Length;
            }

            for (var index = 0; index < count; index++)
            {
                _commandEntities[index] = new EntityId(_selectedEntities[index]);
            }

            var sequence = _nextSequence++;
            if (_nextSequence == 0)
            {
                // CommandHeader.Validate answers a Sequence of 0 with MissingSequence, and
                // stepping over it serves that rule alone. MatchServer's own requirement
                // is per player and monotonic — a sequence must beat the last one accepted
                // from this player or it is a DuplicateSequence — which a wrapped counter
                // cannot satisfy either way. That wrap takes 4 billion commands from one
                // client, so no match reaches it; 0 is the only value worth guarding.
                _nextSequence = 1;
            }

            var header = new CommandHeader(LocalPlayerId, sequence, requestedTick, type);

            // Only a move is laid out into a formation. Attack and Stop ignore both the
            // layout and the destination, so working out a centroid for them means
            // reading every selected unit's position to derive a facing nobody reads.
            var formation = default(FormationSpec);
            if (type == GameCommandType.Move)
            {
                var centreXMillimetres = 0L;
                var centreZMillimetres = 0L;
                for (var index = 0; index < count; index++)
                {
                    if (_world.TryGetSlotState(_selectedSlots[index], out var state))
                    {
                        centreXMillimetres += state.PosX;
                        centreZMillimetres += state.PosZ;
                    }
                }

                formation = new FormationSpec(
                    0,
                    FormationSpacingMillimetres,
                    ChooseFacing(
                        (long)destination.X - centreXMillimetres / count,
                        (long)destination.Z - centreZMillimetres / count));
            }

            sink.Submit(new IssuedCommand(
                header,
                _commandEntities,
                count,
                destination,
                target,
                formation));
            return true;
        }

        /// <summary>
        /// Cardinal facing of a move formation, from the centroid towards the
        /// destination. Same dominant-axis rule the prototype queue uses, kept local
        /// because that type is the Phase 2 playtest harness and selection has no
        /// reason to depend on it.
        ///
        /// The magnitudes are compared as unsigned rather than through
        /// <see cref="Math.Abs(int)"/> on the signed value: <c>Abs(long.MinValue)</c>
        /// throws rather than answering, and a centroid summed over
        /// <see cref="SimulationConstants.MaxSelectedEntities"/> millimetre coordinates
        /// is the kind of arithmetic that can reach the ends of the range.
        /// </summary>
        private static CardinalFacing ChooseFacing(long deltaXMillimetres, long deltaZMillimetres)
        {
            var x = UnsignedMagnitude(deltaXMillimetres);
            var z = UnsignedMagnitude(deltaZMillimetres);
            if (x > z)
            {
                return deltaXMillimetres >= 0 ? CardinalFacing.East : CardinalFacing.West;
            }

            return deltaZMillimetres >= 0 ? CardinalFacing.North : CardinalFacing.South;
        }

        /// <summary>
        /// <c>|value|</c> widened to a <c>ulong</c>, which is the one representation
        /// that holds the absolute value of every <see cref="long"/> including
        /// <see cref="long.MinValue"/>.
        /// </summary>
        private static ulong UnsignedMagnitude(long value) =>
            value >= 0 ? (ulong)value : unchecked((ulong)-value);

        // ------------------------------------------------------------------- set order

        /// <summary>First index whose entity is not below <paramref name="entity"/>.</summary>
        private int LowerBound(ulong entity)
        {
            var low = 0;
            var high = _selectedCount;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                if (_selectedEntities[middle] < entity)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        private int IndexOfSelected(ulong entity)
        {
            var index = LowerBound(entity);
            return index < _selectedCount && _selectedEntities[index] == entity ? index : -1;
        }

        private static Rect NormalisedRect(Vector2 corner, Vector2 other)
        {
            var minX = Mathf.Min(corner.x, other.x);
            var minY = Mathf.Min(corner.y, other.y);
            return new Rect(minX, minY, Mathf.Abs(corner.x - other.x), Mathf.Abs(corner.y - other.y));
        }

        /// <summary>
        /// Keeps a configurable bound inside the range where comparing against it
        /// means something. NaN is rejected rather than clamped: every clamp,
        /// <c>Mathf.Max</c> and <c>&gt;=</c> compare passes a NaN straight through, so
        /// the value that reaches a hot path would still be the poisoned one.
        /// </summary>
        private static float SanitizeNonNegative(float value, float fallback) =>
            IsFinite(value) && value >= 0f ? value : fallback;

        /// <summary><see cref="SanitizeNonNegative(float,float)"/> for a seconds measure.</summary>
        private static double SanitizeNonNegative(double value, double fallback) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0 ? value : fallback;

        private static bool IsFinite(Vector2 value) => IsFinite(value.x) && IsFinite(value.y);

        private static bool IsFinite(float value) => UnitPickMath.IsFinite(value);

        private bool IsDoubleClick(EntityId entity, double nowSeconds)
        {
            if (_lastClickEntity == 0 || _lastClickEntity != entity.Value)
            {
                return false;
            }

            var elapsed = nowSeconds - _lastClickSeconds;

            // One-sided rather than |elapsed|: a clock that goes backwards across a
            // reconnect or a paused match (OD-18) must not turn the next click into an
            // instant group select for whoever was clicked last.
            return elapsed >= 0.0 && elapsed <= DoubleClickWindowSeconds;
        }
    }
}
