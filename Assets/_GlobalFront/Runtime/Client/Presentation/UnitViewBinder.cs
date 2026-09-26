using System;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using UnityEngine;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// Owns the mapping from replicated unit slots to <see cref="UnitView"/>
    /// instances (Phase 3, step 3.3): it spawns a view when a unit appears in
    /// <see cref="ClientReplicationWorld"/>, retires it back to the
    /// <see cref="UnitViewPool"/> when the unit dies or leaves the view, and writes
    /// <see cref="UnitViewTickBuffer"/> poses onto the survivors every frame.
    ///
    /// The replication table's slot index is the join key for everything here. The
    /// buffer captures ticks over that same index (see
    /// <see cref="UnitViewTickBuffer.CaptureTick"/>), so one array
    /// <c>slot -&gt; view</c> is the whole binding state: no dictionaries, no id
    /// hashing, no per-frame enumeration beyond one <c>for</c> over the slot range,
    /// and therefore no presentation allocations at all.
    ///
    /// Slot identity is the subtle part. Freed table slots are reused, so a slot can
    /// hold a different unit from one frame to the next; the entity check in
    /// <see cref="Render"/> catches that and rebinds instead of letting a new unit
    /// inherit the old unit's transform. The buffer needs the same rule, and applies
    /// it one step later (on the next capture), which is why a freshly bound view is
    /// held at its authoritative snap until the ring has actually recaptured the
    /// slot — see <see cref="_capturedTickAtBind"/>.
    /// </summary>
    public sealed class UnitViewBinder
    {
        /// <summary>
        /// How many destroyed-view recoveries get a console line. The recovery itself
        /// runs and is counted for ever; only the log is budgeted, because a view
        /// destroyed every frame would otherwise drown the rest of the log.
        /// </summary>
        private const int DestroyedViewLogBudget = 16;

        private readonly UnitViewPool _pool;
        private readonly ClientReplicationWorld _world;
        private readonly UnitViewTickBuffer _buffer;
        private readonly UnitView[] _viewBySlot;

        /// <summary>
        /// Render clock of the buffer at the moment a slot was bound. Until
        /// <see cref="UnitViewTickBuffer.CapturedTick"/> moves past it, the ring
        /// behind this slot still describes the unit that held the slot before —
        /// applying that pose would put the previous tenant back on screen, which on
        /// a recycled slot reads as the new unit teleporting in from a casualty's
        /// position. The view simply keeps the authoritative position it was snapped
        /// to, which is also what the first fresh sample would have shown.
        /// </summary>
        private readonly ulong[] _capturedTickAtBind;

        /// <summary>
        /// Presentation row per archetype, resolved once from the catalog at
        /// construction. A spawn has to state a unit's health fraction before the
        /// ring has anything to show, and the buffer's own copy of that table is
        /// private — so the binder keeps its own array-indexed lookup rather than
        /// paying a virtual catalog call per spawn. The same row carries the
        /// footprint radius the overlays are scaled from (OD-25). O(1), no allocation,
        /// and the same zeroed <see cref="UnitDefinition"/> for an archetype the
        /// client has no row for that keeps a bar from dividing by 0 and a ring from
        /// being drawn at all.
        /// </summary>
        private readonly UnitDefinition[] _definitionByKind;

        private readonly int _slotLimit;

        private int _boundCount;
        private long _spawnCount;
        private long _releaseCount;
        private long _rebindCount;
        private long _unpresentedCount;
        private long _destroyedViewCount;

        public UnitViewBinder(
            UnitViewPool pool,
            ClientReplicationWorld world,
            UnitViewTickBuffer buffer,
            IUnitCatalog catalog = null)
        {
            if (pool == null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            _pool = pool;
            _world = world;
            _buffer = buffer;

            // The narrower table bounds the loop: a slot the buffer cannot sample or
            // the world cannot report would otherwise be walked for nothing.
            _slotLimit = buffer.Capacity < world.Capacity ? buffer.Capacity : world.Capacity;
            _viewBySlot = new UnitView[_slotLimit];
            _capturedTickAtBind = new ulong[_slotLimit];

            var source = catalog ?? UnitCatalog.Default;
            _definitionByKind = new UnitDefinition[UnitKinds.Count];
            for (var kind = 1; kind < _definitionByKind.Length; kind++)
            {
                if (source.TryGet((byte)kind, out var definition))
                {
                    _definitionByKind[kind] = definition;
                }
            }

            if (_slotLimit > pool.MaximumViews)
            {
                // Warm-up can be fixed later, but this one is structural: with fewer
                // views than slots, some unit in a full-size match can never be
                // presented, and the symptom (an invisible unit) is far from the cause.
                Debug.LogWarning(
                    $"{nameof(UnitViewBinder)}: the replication world exposes {_slotLimit} unit slots but the view pool is capped at {pool.MaximumViews}; units past the cap will stay un-presented.");
            }
        }

        /// <summary>Pool views are taken from and returned to.</summary>
        public UnitViewPool Pool => _pool;

        /// <summary>Interpolation source of the poses written every frame.</summary>
        public UnitViewTickBuffer Buffer => _buffer;

        /// <summary>Views currently presented, one per live replicated unit that got one.</summary>
        public int BoundViewCount => _boundCount;

        /// <summary>
        /// Highest <see cref="BoundViewCount"/> this binder has reached: the number
        /// a load screen should warm the pool to, which is how OD-26's "no
        /// instantiate in combat" is actually kept true in a long match.
        /// </summary>
        public int PeakViewCount { get; private set; }

        /// <summary>Views taken out of the pool.</summary>
        public long SpawnCount => _spawnCount;

        /// <summary>Views returned to the pool.</summary>
        public long ReleaseCount => _releaseCount;

        /// <summary>Times a slot came back holding another entity and had to rebind.</summary>
        public long RebindCount => _rebindCount;

        /// <summary>
        /// Units that could not be presented because the pool had no view for them:
        /// exhausted with <see cref="UnitViewPool.AllowCombatGrowth"/> off (the OD-26
        /// default), or at its ceiling.
        /// </summary>
        public long UnpresentedCount => _unpresentedCount;

        /// <summary>
        /// Times a slot held a view whose GameObject was destroyed outside the pool.
        /// Any non-zero value is a bug in the destroyer — OD-26 bans <c>Destroy</c> in
        /// combat — recorded here so it is countable without reading the console.
        /// </summary>
        public long DestroyedViewCount => _destroyedViewCount;

        /// <summary>The view bound to one replication slot, if any.</summary>
        public bool TryGetView(int slot, out UnitView view)
        {
            view = (uint)slot < (uint)_slotLimit ? _viewBySlot[slot] : null;
            return view != null;
        }

        /// <summary>
        /// Number of replication slots this binder walks, and therefore the range a
        /// consumer may enumerate with <see cref="TryGetView"/>. The overlay pass
        /// (OD-25) builds its instance batches by walking exactly this range, so it
        /// needs the bound but never the array itself: handing out the reference would
        /// let a caller leave a slot pointing at a view the pool already took back.
        /// </summary>
        public int SlotCount => _slotLimit;

        /// <summary>
        /// Advances the render clock and writes one frame of presentation. Call it
        /// once per frame, after replication has been applied and after
        /// <see cref="UnitViewTickBuffer.CaptureTick"/> for any packet that arrived
        /// this frame.
        ///
        /// A tactical pause (OD-18) needs no other handling than
        /// <c>Render(0.0)</c>: the clock refuses to advance on a non-positive delta
        /// and the ring returns the same pose it returned last frame, so every view
        /// holds exactly where it was instead of sliding into the extrapolation
        /// budget while nobody is moving.
        /// </summary>
        public void Render(double unscaledDeltaSeconds)
        {
            _buffer.Advance(unscaledDeltaSeconds);

            var world = _world;
            var buffer = _buffer;
            var captured = buffer.CapturedTick;
            var views = _viewBySlot;
            var tickAtBind = _capturedTickAtBind;

            for (var slot = 0; slot < _slotLimit; slot++)
            {
                var view = views[slot];

                // A reference that is non-null but Unity-null is a view whose GameObject
                // was destroyed outside the pool. Treated as an ordinary release, this
                // would slip through: the pool's own reference check drops the call
                // before it can tidy its bookkeeping, the counters keep claiming the
                // corpse is presented, and the next frame repeats forever. Say so once,
                // and let the release below happen through the reference check.
                if (!ReferenceEquals(view, null) && view == null)
                {
                    _destroyedViewCount++;
                    if (_destroyedViewCount <= DestroyedViewLogBudget)
                    {
                        Debug.LogWarning(
                            $"{nameof(UnitViewBinder)}: replication slot {slot} held a view destroyed outside the pool ({nameof(UnityEngine.Object.Destroy)} in combat is banned by OD-26); it has been dropped and the unit rebound. Find and fix whatever destroyed it.");
                    }

                    ReleaseSlot(slot);
                    view = null;
                }

                if (!world.TryGetSlotState(slot, out var state))
                {
                    // Destroyed, fog-hidden, or never there: nothing may stay on
                    // screen. The client world, not the view, decides visibility.
                    if (view != null)
                    {
                        ReleaseSlot(slot);
                    }

                    continue;
                }

                if (view == null)
                {
                    if (!TryBindSlot(slot, in state, captured))
                    {
                        continue;
                    }

                    view = views[slot];
                }
                else if (view.EntityValue != state.Entity.Value)
                {
                    // Slot recycled onto another unit: the old view goes back and the
                    // new unit gets a fresh one. Position, health and identity all
                    // belong to the replacement, so nothing may be carried over.
                    _rebindCount++;
                    ReleaseSlot(slot);
                    if (!TryBindSlot(slot, in state, captured))
                    {
                        continue;
                    }

                    view = views[slot];
                }

                if (captured == tickAtBind[slot] || !buffer.TrySample(slot, out var pose))
                {
                    continue;
                }

                view.Apply(in pose);
            }
        }

        /// <summary>
        /// Handles an OD-20 re-baseline: the client table is wiped and rebuilt from a
        /// keyframe, so every slot index may now describe a different unit. Views are
        /// released first (they return to the pool, they are not destroyed) and the
        /// ring is re-primed at the snapshot tick; the next <see cref="Render"/>
        /// re-acquires from the warmed pool, which costs a pop and an activation and
        /// no instantiation.
        /// </summary>
        public void HandleResync(ulong snapshotTick)
        {
            ReleaseAllViews();
            _buffer.Resync(snapshotTick);
        }

        /// <summary>
        /// Releases every bound view without touching the buffer, for leaving a match
        /// or hiding the battlefield (spectator hand-off, OD-18 replay review). The
        /// pool keeps its objects, so re-entering a match is warm again.
        /// </summary>
        public int ReleaseAllViews()
        {
            var released = 0;
            for (var slot = 0; slot < _slotLimit; slot++)
            {
                if (_viewBySlot[slot] != null)
                {
                    ReleaseSlot(slot);
                    released++;
                }
            }

            return released;
        }

        /// <summary>
        /// Detaches and releases every bound view back to the pool.
        /// Convenience alias for <see cref="ReleaseAllViews"/> (P2-7).
        /// </summary>
        public void Detach() => ReleaseAllViews();

        private bool TryBindSlot(int slot, in ClientUnitState state, ulong capturedTick)
        {
            if (!_pool.TryAcquire(state.UnitKind, out var view))
            {
                // The pool could not serve the archetype: it is exhausted with combat
                // growth disabled (the OD-26 default), or already at its ceiling. Either
                // way the unit stays un-presented rather than allocating past the match's
                // warm-up budget, and the pool has logged why once.
                _unpresentedCount++;
                return false;
            }

            // Authoritative position and health first, interpolated pose from the next capture:
            // a unit that materialised at the world origin for three frames is a
            // worse artefact than one that holds still for a packet.
            var definition = (uint)state.UnitKind < (uint)_definitionByKind.Length
                ? _definitionByKind[state.UnitKind]
                : default;
            view.Bind(state.Entity, definition.MaximumHealth, definition.RadiusMillimetres);
            view.SnapToAuthority(in state, definition.InvMaxHealth);

            _viewBySlot[slot] = view;
            _capturedTickAtBind[slot] = capturedTick;
            _boundCount++;
            _spawnCount++;
            if (_boundCount > PeakViewCount)
            {
                PeakViewCount = _boundCount;
            }

            return true;
        }

        private void ReleaseSlot(int slot)
        {
            var view = _viewBySlot[slot];
            _viewBySlot[slot] = null;
            _capturedTickAtBind[slot] = 0;
            _boundCount--;

            if (!ReferenceEquals(view, null))
            {
                // Selection is dropped here rather than only inside the pool's
                // release, because the pool can refuse this view (a repeated release,
                // one it never created, a GameObject destroyed outside it) and return
                // before it touches the object — and a view that keeps a dead unit's
                // selection is one that puts a ring on whoever is drawn next.
                view.SetSelected(false);
            }

            // The slot reference is dropped even if the pool rejected the view: a
            // view the pool does not own any more must not stay bound to a slot,
            // because the next unit there would inherit it.
            if (_pool.Release(view))
            {
                _releaseCount++;
            }
        }
    }
}
