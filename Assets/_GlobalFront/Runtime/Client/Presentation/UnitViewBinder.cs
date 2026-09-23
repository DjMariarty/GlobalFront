using System;
using GlobalFront.Client.Replication;

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

        private readonly int _slotLimit;

        private int _boundCount;
        private long _spawnCount;
        private long _releaseCount;
        private long _rebindCount;
        private long _unpresentedCount;

        public UnitViewBinder(
            UnitViewPool pool,
            ClientReplicationWorld world,
            UnitViewTickBuffer buffer)
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

        /// <summary>Units that could not be presented because the pool was at its ceiling.</summary>
        public long UnpresentedCount => _unpresentedCount;

        /// <summary>The view bound to one replication slot, if any.</summary>
        public bool TryGetView(int slot, out UnitView view)
        {
            view = (uint)slot < (uint)_slotLimit ? _viewBySlot[slot] : null;
            return view != null;
        }

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

        private bool TryBindSlot(int slot, in ClientUnitState state, ulong capturedTick)
        {
            if (!_pool.TryAcquire(state.UnitKind, out var view))
            {
                // Pool at its ceiling: the unit stays un-presented rather than
                // allocating past the match's warm-up budget.
                _unpresentedCount++;
                return false;
            }

            view.Bind(state.Entity);

            // Authoritative position first, interpolated pose from the next capture:
            // a unit that materialised at the world origin for three frames is a
            // worse artefact than one that holds still for a packet.
            view.SnapToMillimetres(state.PosX, state.PosZ);

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
