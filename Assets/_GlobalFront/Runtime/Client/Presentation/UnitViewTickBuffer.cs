using System;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// How the presentation layer obtained the pose it is about to render.
    /// Diagnostic on purpose: it separates a healthy interpolated frame from a
    /// starved one, which is the difference between "smooth" and "the network is
    /// degrading" in a playtest report.
    /// </summary>
    public enum UnitViewPoseSource : byte
    {
        /// <summary>Between two captured ticks; the nominal case.</summary>
        Interpolated = 0,

        /// <summary>
        /// Only one side of the bracket existed: a single sample, the re-prime
        /// after <see cref="UnitViewTickBuffer.Flush"/> or
        /// <see cref="UnitViewTickBuffer.Resync"/>, or a render clock that has not
        /// reached the first sample yet. Never produced by a division, so a sparse
        /// ring cannot yield NaN.
        /// </summary>
        Snapped = 1,

        /// <summary>
        /// The buffer starved and the pose was pushed toward the authoritative move
        /// target inside the extrapolation budget.
        /// </summary>
        Extrapolated = 2,

        /// <summary>
        /// Starved with the budget (or the move target itself) exhausted: the unit
        /// holds instead of teleporting when the next packet lands.
        /// </summary>
        Held = 3
    }

    /// <summary>
    /// One presentation pose for one unit slot, read at the current render clock.
    /// Millimetre integers are widened to <see cref="float"/> here and converted
    /// to world units by the view binder, which keeps this type free of Unity
    /// Engine dependencies.
    ///
    /// Health is a step value copied from the authoritative sample at or below the
    /// render clock, never an interpolation: fractional hit points read as the
    /// unit healing itself whenever two ticks arrive out of visual order.
    /// </summary>
    public readonly struct UnitViewPose
    {
        public UnitViewPose(
            float xMillimetres,
            float zMillimetres,
            int health,
            float healthFraction,
            float bodyYawDegrees,
            float turretYawDegrees,
            UnitViewPoseSource source)
        {
            XMillimetres = xMillimetres;
            ZMillimetres = zMillimetres;
            Health = health;
            HealthFraction = healthFraction;
            BodyYawDegrees = bodyYawDegrees;
            TurretYawDegrees = turretYawDegrees;
            Source = source;
        }

        /// <summary>Interpolated position X in millimetres.</summary>
        public float XMillimetres { get; }

        /// <summary>Interpolated position Z in millimetres.</summary>
        public float ZMillimetres { get; }

        /// <summary>Authoritative hit points at the render clock.</summary>
        public int Health { get; }

        /// <summary>
        /// <c>Health * (1 / maxHealth)</c> of the slot, exactly 0 while the slot
        /// has no resolved maximum health. That zero is the reason a bar can never
        /// divide by zero.
        /// </summary>
        public float HealthFraction { get; }

        /// <summary>
        /// Hull yaw in degrees for the <c>Quaternion.Euler(0, yaw, 0)</c>
        /// convention, angular slew applied. Presentation-only state: it is never
        /// an input to the simulation, so peers may legitimately disagree.
        /// </summary>
        public float BodyYawDegrees { get; }

        /// <summary>
        /// Turret yaw in degrees: tracked onto the authoritative attack target when
        /// one exists, aligned with the hull otherwise.
        /// </summary>
        public float TurretYawDegrees { get; }

        public UnitViewPoseSource Source { get; }

        /// <summary>True when the pose was built without an authoritative support tick.</summary>
        public bool IsReconstructed =>
            Source == UnitViewPoseSource.Extrapolated || Source == UnitViewPoseSource.Held;
    }

    /// <summary>
    /// Presentation interpolator of replicated unit state (Phase 3, step 3.2): the
    /// OD-23 design with the P0-2, P0-3 and P0-4 review corrections applied.
    ///
    /// The simulation ticks at <see cref="SimulationConstants.ServerTickRate"/> Hz
    /// while replication arrives at 10 Hz and degrades to 5 Hz under OD-12, so the
    /// tick stream entering this buffer is <b>sparse, and its stride changes at
    /// runtime</b>: entries sit 1, 2 or 4 ticks apart, and a tick the server
    /// skipped is skipped for good. Every rule below follows from that one fact.
    ///
    /// 1. Ring of <see cref="HistoryTicks"/> entries per slot, indexed strictly as
    ///    <c>tick &amp; 31</c> over <see cref="ulong"/> ticks. Tick 0 is the empty
    ///    sentinel, so an entry is occupied exactly when the tick stored in the
    ///    cell equals the tick being looked for: no occupancy flags that can drift
    ///    out of sync, and no window in which a retransmitted
    ///    <c>newest - 32</c> packet overwrites a live interpolation anchor.
    /// 2. Bracket samples are found by <b>walking occupied cells</b>, never by
    ///    arithmetic on <c>newer - 1</c>, which would read an empty slot or a
    ///    sample left over from 32 ticks back.
    /// 3. A single sample, or a non-positive denominator, resolves to
    ///    <see cref="UnitViewPoseSource.Snapped"/>: <c>alpha</c> is only ever
    ///    divided by a proven <c>newerTick &gt; olderTick</c>.
    /// 4. Render delay is derived from the <b>observed packet interval</b>
    ///    (<c>k x interval</c> plus a jitter allowance), so the 5 Hz floor gets
    ///    ~8 ticks rather than the fixed 3 ticks that would leave the render clock
    ///    permanently past the newest sample and freeze the whole view. Each sample
    ///    enters that estimate bounded by <see cref="MaxIntervalSampleTicks"/>, so a
    ///    single stall wider than the supported cadences costs one packet of delay
    ///    rather than setting the play-out ceiling for the next few seconds.
    /// 5. Starvation clamps toward the authoritative move target
    ///    (<see cref="MaxExtrapolationTicks"/> ticks at
    ///    <see cref="UnitViewTickBuffer.MaxStepPerTickMm"/>) and then holds, which
    ///    turns a 0.7 m stop-and-go at 10 Hz (1.4 m at 5 Hz) into a short reach
    ///    plus a hold.
    /// 6. The render clock is continuously re-anchored toward the leading edge of
    ///    the ring, so a clamped catch-up cannot accumulate drift until the buffer
    ///    starves for the rest of the match. Only a clock that genuinely lags is
    ///    snapped forward; one that runs ahead is parked at the extrapolation
    ///    ceiling, because snapping it back is a reverse teleport.
    /// 7. Hull and turret yaw prefer the authoritative destination over derived
    ///    displacement, and hold the previous heading inside
    ///    <see cref="YawDeadzoneMillimetres"/>, so formation micro-jitter of a
    ///    standing unit cannot spin it.
    ///
    /// Strict zero-GC: state is one flat array per field sized
    /// <c>capacity x HistoryTicks</c> at construction (37 bytes per entry: ~4.9 MB
    /// at <see cref="ClientReplicationWorld.DefaultCapacity"/>, ~600 KB for a
    /// 512-unit client). <see cref="CaptureTick"/>, <see cref="Advance"/> and
    /// <see cref="TrySample"/> allocate nothing and call no Unity Engine API — the
    /// binder feeds <see cref="Advance"/> with <c>Time.unscaledDeltaTime</c> as a
    /// <see cref="double"/>.
    /// </summary>
    public sealed class UnitViewTickBuffer
    {
        /// <summary>Ring depth in simulation ticks; must stay a power of two.</summary>
        public const int HistoryTicks = 32;

        /// <summary>Bit mask of <see cref="HistoryTicks"/> for tick indexing.</summary>
        private const ulong HistoryMask = (ulong)(HistoryTicks - 1);

        /// <summary>Tick 0 means "nothing captured here" and is never accepted.</summary>
        private const ulong EmptyTick = 0;

        /// <summary>Default ring capacity, mirroring <see cref="ClientReplicationWorld"/>.</summary>
        public const int DefaultCapacity = ClientReplicationWorld.DefaultCapacity;

        /// <summary>
        /// Fastest authoritative unit step per tick, used as the extrapolation
        /// speed cap. It mirrors the prototype movement contract
        /// (<c>PrototypeUnit.MovementPerTickMm</c>). The archetype is replicated
        /// since OD-29, but speed is not: it lives on <c>UnitSpawnSpec</c> per unit,
        /// so the cap stays one conservative configured value until the roster
        /// publishes speeds (over-estimating only lengthens a reach by a tick, while
        /// under-estimating makes every fast unit freeze mid-move).
        /// </summary>
        public const int DefaultMaxStepPerTickMm = 350;

        /// <summary>
        /// Displacement below which a captured tick contributes no heading, so a
        /// standing unit's jitter cannot rotate it and a unit whose move target is
        /// its own position does not snap to north.
        /// </summary>
        public const float YawDeadzoneMillimetres = 10f;

        /// <summary>
        /// Nominal play-out delay in <b>packets</b> behind the newest sample. Two
        /// packets cover one lost packet plus one cadence switch toward 5 Hz.
        /// </summary>
        private const double DelayPacketsOfInterval = 2.0;

        /// <summary>Jitter allowance in packets of the observed interval deviation.</summary>
        private const double DelayJitterPackets = 0.5;

        /// <summary>Floor of the adaptive delay: the reviewed 150 ms nominal.</summary>
        public const int MinRenderDelayTicks = 3;

        /// <summary>
        /// Ceiling of the adaptive delay, kept well below
        /// <see cref="HistoryTicks"/> so the ring always retains several packets of
        /// headroom beyond the play-out delay.
        /// </summary>
        public const int MaxRenderDelayTicks = 12;

        /// <summary>
        /// How far past the newest sample the clock may run while the pose keeps
        /// moving; beyond that the unit holds and waits for authority.
        /// </summary>
        public const int MaxExtrapolationTicks = 2;

        /// <summary>
        /// Presentation mirror of <see cref="SimulationConstants.MaxCatchUpTicksPerFrame"/>:
        /// one giant frame (tab-out, editor pause, the OD-18 tactical pause) must
        /// not buy back the whole wall clock, because a render clock that ran ahead
        /// of authority starves the ring for good.
        /// </summary>
        public const int MaxAdvanceTicksPerCall = SimulationConstants.MaxCatchUpTicksPerFrame;

        /// <summary>
        /// Above this skew the render clock is snapped rather than nudged.
        /// <para>
        /// Derived from the two bounds that decide what a legitimate skew can be,
        /// not from <see cref="HistoryTicks"/>: a clock parked on the extrapolation
        /// ceiling sits at most <see cref="MaxRenderDelayTicks"/> +
        /// <see cref="MaxExtrapolationTicks"/> ticks ahead of the play-out target, so
        /// the + 2.0 margin is what makes <see cref="Advance"/> unable to see a
        /// starved clock as an overshoot and snap it backwards (audit P1-2).
        /// <see cref="UnitViewTickBufferTests.Starvation_HighDelay_DoesNotSnapClockBackwards"/>
        /// sits on exactly that boundary, and must be re-checked if either bound or
        /// <see cref="HistoryTicks"/> changes.
        /// </para>
        /// </summary>
        private const double DriftSnapTicks = MaxRenderDelayTicks + MaxExtrapolationTicks + 2.0;

        /// <summary>
        /// Largest single arrival interval the delay estimator may learn from: the
        /// 5 Hz replication floor (4 ticks) with room for one lost packet. Anything
        /// wider is an outage or a stall, not cadence, and a whole ring of it is
        /// already a <see cref="Reprime"/>.
        /// </summary>
        private const double MaxIntervalSampleTicks = 8.0;

        /// <summary>
        /// Rate at which a bounded skew is bled off, fraction per second. A quarter
        /// per second clears a play-out-delay-sized skew in a few seconds, which is
        /// how long a cadence switch or one lost packet should take to settle; the
        /// 0.05 this replaced needed twenty seconds and read as a permanent lag.
        /// </summary>
        private const double DriftCorrectionPerSecond = 0.25;

        /// <summary>Smoothing factor of the arrival-interval and jitter estimates.</summary>
        private const double ArrivalEmaAlpha = 0.25;

        private const float DegreesPerRadian = 57.29577951308232f;

        private const byte HasMoveTargetFlag = 1 << 0;

        private readonly ulong[] _sampleTick;
        private readonly int[] _positionX;
        private readonly int[] _positionZ;
        private readonly int[] _moveTargetX;
        private readonly int[] _moveTargetZ;
        private readonly int[] _health;
        private readonly float[] _bodyYawDegrees;
        private readonly float[] _turretYawDegrees;
        private readonly byte[] _flags;

        private readonly ulong[] _slotEntity;
        private readonly ulong[] _slotNewestTick;
        private readonly float[] _slotBodyYaw;
        private readonly float[] _slotTurretYaw;
        private readonly double[] _slotLastRenderTick;
        private readonly float[] _slotInvMaxHealth;

        /// <summary>
        /// True once <see cref="SetSlotMaximumHealth"/> named the denominator of the
        /// slot. Kept apart from the value because 0 is both "unresolved" and a
        /// deliberate override meaning "draw an empty bar", and the two cannot be
        /// told apart from a float.
        /// </summary>
        private readonly bool[] _slotHealthOverridden;

        private readonly int _capacity;
        private readonly double _tickDurationSeconds;
        private readonly IUnitCatalog _catalog;

        private ulong _capturedTick = EmptyTick;
        private ulong _minimumAcceptableTick = EmptyTick;
        private double _renderTick;
        private double _observedIntervalTicks;
        private double _observedJitterTicks;
        private double _renderDelayTicks = MinRenderDelayTicks;
        private bool _needsRenderClockAnchor = true;

        private long _stalePacketCount;
        private long _reprimeCount;
        private long _recycledSlotCount;
        private long _starvedSampleCount;

        /// <param name="capacity">
        /// Unit slots to cover; must be at least <see cref="ClientReplicationWorld.Capacity"/>.
        /// </param>
        /// <param name="tickDurationSeconds">Simulation tick length, in seconds.</param>
        /// <param name="catalog">
        /// Archetype table used to resolve the health denominator of a slot when it
        /// starts holding a unit. Null selects <see cref="UnitCatalog.Default"/>;
        /// an explicit <see cref="SetSlotMaximumHealth"/> still wins for that slot,
        /// which is how a caller keeps a per-match override.
        /// </param>
        public UnitViewTickBuffer(
            int capacity = DefaultCapacity,
            double tickDurationSeconds = SimulationConstants.ServerTickDurationSeconds,
            IUnitCatalog catalog = null)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            if (!(tickDurationSeconds > 0.0))
            {
                throw new ArgumentOutOfRangeException(nameof(tickDurationSeconds));
            }

            _capacity = capacity;
            _tickDurationSeconds = tickDurationSeconds;
            _catalog = catalog ?? UnitCatalog.Default;

            var entries = capacity * HistoryTicks;
            _sampleTick = new ulong[entries];
            _positionX = new int[entries];
            _positionZ = new int[entries];
            _moveTargetX = new int[entries];
            _moveTargetZ = new int[entries];
            _health = new int[entries];
            _bodyYawDegrees = new float[entries];
            _turretYawDegrees = new float[entries];
            _flags = new byte[entries];

            _slotEntity = new ulong[capacity];
            _slotNewestTick = new ulong[capacity];
            _slotBodyYaw = new float[capacity];
            _slotTurretYaw = new float[capacity];
            _slotLastRenderTick = new double[capacity];
            _slotInvMaxHealth = new float[capacity];
            _slotHealthOverridden = new bool[capacity];
        }

        /// <summary>Unit slots this buffer covers.</summary>
        public int Capacity => _capacity;

        /// <summary>Newest tick installed by <see cref="CaptureTick"/>.</summary>
        public ulong CapturedTick => _capturedTick;

        /// <summary>Fractional render clock, in simulation ticks.</summary>
        public double RenderTick => _renderTick;

        /// <summary>Current adaptive play-out delay, in ticks.</summary>
        public double RenderDelayTicks => _renderDelayTicks;

        /// <summary>Smoothed packet arrival interval in ticks: 2 at 10 Hz, 4 at 5 Hz.</summary>
        public double ObservedIntervalTicks => _observedIntervalTicks;

        /// <summary>Smoothed arrival-interval deviation, in ticks.</summary>
        public double ObservedJitterTicks => _observedJitterTicks;

        /// <summary>Replicated duplicates and late retransmissions the gates dropped.</summary>
        public long StalePacketCount => _stalePacketCount;

        /// <summary>Times a gap of <see cref="HistoryTicks"/> or more forced a ring re-prime.</summary>
        public long ReprimeCount => _reprimeCount;

        /// <summary>Times a table slot came back holding a different entity.</summary>
        public long RecycledSlotCount => _recycledSlotCount;

        /// <summary>Poses returned from a starved ring, for degradation telemetry.</summary>
        public long StarvedSampleCount => _starvedSampleCount;

        /// <summary>Extrapolation speed cap in millimetres per tick. Zero disables extrapolation.</summary>
        public int MaxStepPerTickMm { get; set; } = DefaultMaxStepPerTickMm;

        /// <summary>Hull slew limit in degrees per second.</summary>
        public float MaxBodyDegreesPerSecond { get; set; } = 270f;

        /// <summary>Turret slew limit in degrees per second.</summary>
        public float MaxTurretDegreesPerSecond { get; set; } = 360f;

        /// <summary>True when the slot holds at least one sample the renderer can use.</summary>
        public bool HasHistory(int slot) =>
            (uint)slot < (uint)_capacity && _slotNewestTick[slot] != EmptyTick;

        /// <summary>
        /// Overrides the health denominator of one slot. Normally
        /// <see cref="CaptureTick"/> resolves it from the replicated archetype
        /// through <see cref="IUnitCatalog"/>; this is the escape hatch for a caller
        /// that knows better (a per-match stat override, or an
        /// <see cref="GlobalFront.Core.Model.UnitKinds.Unknown"/> record from before
        /// OD-29 whose stats the binder resolved elsewhere). It wins over the catalog
        /// for that slot — including the 0 that asks for an empty bar — until the unit
        /// it was named for leaves the client's view: destroyed, fog-hidden, or
        /// replaced in the slot by another unit. A flush or resync of the same unit
        /// keeps it. 0 is a legal input: it yields an empty bar rather than the
        /// <c>0/0 = NaN</c> that <c>PrototypeUnit.MaximumHealth</c> can produce today.
        /// </summary>
        public void SetSlotMaximumHealth(int slot, int maximumHealth)
        {
            if ((uint)slot >= (uint)_capacity)
            {
                return;
            }

            _slotInvMaxHealth[slot] = maximumHealth > 0 ? 1f / maximumHealth : 0f;
            _slotHealthOverridden[slot] = true;
        }

        /// <summary>
        /// Records one authoritative replication tick for every live unit of the
        /// client world. Called once per applied packet (10 Hz nominal, 5 Hz at the
        /// OD-12 floor), not once per simulation tick.
        /// </summary>
        public void CaptureTick(ulong tick, ClientReplicationWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (tick == EmptyTick)
            {
                return;
            }

            // Gate 1. The codec leaves staleness to its consumer by contract (see
            // DeltaSnapshotHeader), and an unguarded write of
            // (newest - HistoryTicks) lands in the cell that is currently the
            // interpolation anchor of every unit in the ring.
            if (_capturedTick != EmptyTick && tick <= _capturedTick)
            {
                _stalePacketCount++;
                return;
            }

            // Gate 2. Anything below the resync base describes a world this client
            // no longer holds, and must not bracket against the new base.
            if (tick < _minimumAcceptableTick)
            {
                _stalePacketCount++;
                return;
            }

            // Gate 3. A full-ring gap cannot be interpolated across: dropping the
            // history and re-priming is honest, blending across it is a teleport.
            if (_capturedTick != EmptyTick && tick - _capturedTick >= HistoryTicks)
            {
                Reprime();
            }

            var previousTick = _capturedTick;
            var limit = _capacity < world.Capacity ? _capacity : world.Capacity;

            for (var slot = 0; slot < limit; slot++)
            {
                if (!world.TryGetSlotState(slot, out var state))
                {
                    // A destroyed or fog-hidden unit leaves no view behind: the
                    // client world is authoritative over what may be presented. The
                    // health denominator leaves with it, whether it came from the
                    // catalog or from SetSlotMaximumHealth: nothing identifies the
                    // next occupant of this slot as the unit that denominator was
                    // named for, so an override that survived the death would be
                    // inherited by whoever spawns here next.
                    if (_slotNewestTick[slot] != EmptyTick)
                    {
                        ClearSlot(slot);
                        _slotInvMaxHealth[slot] = 0f;
                        _slotHealthOverridden[slot] = false;
                    }

                    continue;
                }

                var baseIndex = slot * HistoryTicks;
                var recycled = _slotEntity[slot] != state.Entity.Value;
                if (recycled)
                {
                    // The table recycles freed slots, so a slot can start holding a
                    // different unit whose history is not this unit's at all. Only a
                    // slot taken over from a unit this buffer still holds sheds that
                    // unit's health state: a slot that names no unit at all is a first
                    // assignment, and a binder that named its denominator before the
                    // unit ever arrived named it for this unit.
                    //
                    // The witness has to be the identity, not the history.
                    // ClearAllSamples wipes the newest tick on every flush and
                    // re-prime while deliberately leaving both the identity and the
                    // denominator, so a slot handed to a different unit across an
                    // OD-20 resync would otherwise read as a first assignment and
                    // inherit the override.
                    var tookOverUnit = _slotEntity[slot] != 0;
                    ClearSlot(slot);
                    _slotEntity[slot] = state.Entity.Value;
                    if (tookOverUnit)
                    {
                        _slotInvMaxHealth[slot] = 0f;
                        _slotHealthOverridden[slot] = false;
                    }
                    _recycledSlotCount++;
                }

                var hadSample = _slotNewestTick[slot] != EmptyTick;
                var previousCell = hadSample
                    ? baseIndex + (int)(_slotNewestTick[slot] & HistoryMask)
                    : -1;

                // OD-29: a slot that starts holding a unit resolves its health
                // denominator from the replicated archetype exactly once, here. Only
                // while nothing has named it, so a binder that already called
                // SetSlotMaximumHealth keeps its value — including an explicit 0,
                // which asks for an empty bar and is otherwise indistinguishable from
                // "no stat resolved" — and an Unknown archetype leaves the 0 that
                // makes the bar read empty instead of dividing.
                if (!hadSample && !_slotHealthOverridden[slot] &&
                    _catalog.TryGet(state.UnitKind, out var definition))
                {
                    _slotInvMaxHealth[slot] = definition.InvMaxHealth;
                }

                // Presentation state is per slot, not per unit. A recycled slot has no
                // heading of its own and must not inherit the casualty's: without this
                // the replacement spends up to three quarters of a second turning
                // through the dead unit's turn, and Mecanim-free presentation has
                // nothing else to hide it with. Any other empty history is the same
                // unit across a resync or a fog gap, whose yaw Flush kept on purpose.
                var fallbackBodyYaw = recycled ? 0f : _slotBodyYaw[slot];
                var fallbackTurretYaw = recycled ? 0f : _slotTurretYaw[slot];

                // "Keep the previous heading" must read the last <b>captured</b>
                // yaw, not the slewed presentation yaw: the latter is owned by
                // TrySample, and overwriting it per packet would turn every
                // arrival into an instant snap and defeat the slew entirely.
                var heldBodyYaw = previousCell >= 0
                    ? _bodyYawDegrees[previousCell]
                    : fallbackBodyYaw;
                var heldTurretYaw = previousCell >= 0
                    ? _turretYawDegrees[previousCell]
                    : fallbackTurretYaw;

                var bodyYaw = ResolveBodyYaw(in state, previousCell, heldBodyYaw);
                var turretYaw = ResolveTurretYaw(in state, world, bodyYaw, heldTurretYaw);

                var cell = baseIndex + (int)(tick & HistoryMask);
                _sampleTick[cell] = tick;
                _positionX[cell] = state.PosX;
                _positionZ[cell] = state.PosZ;
                _health[cell] = state.Health;
                _bodyYawDegrees[cell] = bodyYaw;
                _turretYawDegrees[cell] = turretYaw;

                if (state.HasMoveTarget)
                {
                    _flags[cell] = HasMoveTargetFlag;
                    _moveTargetX[cell] = state.MoveTargetX;
                    _moveTargetZ[cell] = state.MoveTargetZ;
                }
                else
                {
                    _flags[cell] = 0;
                    _moveTargetX[cell] = 0;
                    _moveTargetZ[cell] = 0;
                }

                _slotNewestTick[slot] = tick;

                if (recycled)
                {
                    // Seed the reset state from the replacement's own captured heading
                    // so the first pose rendered for this slot is exact. The fallback
                    // reset above only decides what the heading derivation starts
                    // from: without this seed the slew would still crawl there from
                    // north at MaxBodyDegreesPerSecond, and a stale render-clock anchor
                    // buys an instant turn only as far as the elapsed time allows.
                    _slotBodyYaw[slot] = bodyYaw;
                    _slotTurretYaw[slot] = turretYaw;
                    _slotLastRenderTick[slot] = _renderTick;
                }
            }

            ObserveArrival(tick, previousTick);
            _capturedTick = tick;
        }

        /// <summary>
        /// Advances the fractional render clock by wall-clock time. The clock is
        /// what makes presentation smooth while authority stays discrete.
        /// </summary>
        public void Advance(double unscaledDeltaSeconds)
        {
            // Rejects NaN as well as zero and negative input: a NaN clock
            // propagates into every pose downstream and never recovers.
            if (!(unscaledDeltaSeconds > 0.0))
            {
                return;
            }

            var maxSeconds = MaxAdvanceTicksPerCall * _tickDurationSeconds;
            if (unscaledDeltaSeconds > maxSeconds)
            {
                unscaledDeltaSeconds = maxSeconds;
            }

            if (_needsRenderClockAnchor && _capturedTick != EmptyTick)
            {
                AnchorRenderClock();
            }

            _renderTick += unscaledDeltaSeconds / _tickDurationSeconds;

            if (_capturedTick == EmptyTick)
            {
                return;
            }

            // Re-anchor. Nudging towards the leading edge of the ring keeps a
            // clamped catch-up, or a long stall, from leaving the clock stranded
            // behind authority forever — which would read as a match-long freeze.
            var target = (double)_capturedTick - _renderDelayTicks;
            if (target < 0.0)
            {
                target = 0.0;
            }

            // Park instead of overshooting: while starved, the clock is capped at
            // the extrapolation budget past the newest sample. The cap is what
            // defines "ahead of authority", so the drift test below has to see the
            // parked clock: one clamped frame on top of a parked clock used to read
            // as a half-ring overshoot and snap the clock back to the play-out
            // target, teleporting every unit up to MaxRenderDelayTicks +
            // MaxExtrapolationTicks ticks (4.9 m at the step cap) in reverse, which
            // is a worse artefact than the freeze this path replaced.
            var ceiling = (double)_capturedTick + MaxExtrapolationTicks;
            var parked = _renderTick > ceiling ? ceiling : _renderTick;

            var skew = parked - target;
            if (skew < -DriftSnapTicks)
            {
                // Genuinely stranded behind authority: recover the whole distance in
                // one step instead of bleeding it off over seconds. Overshoot in the
                // other direction is not snapped but parked by the cap below, so a
                // snap can never move the clock backwards.
                _renderTick = target;
            }
            else
            {
                var correction = DriftCorrectionPerSecond * unscaledDeltaSeconds;
                _renderTick -= skew * (correction < 1.0 ? correction : 1.0);
            }

            if (_renderTick > ceiling)
            {
                _renderTick = ceiling;
            }
        }

        /// <summary>
        /// Reads the pose of one unit slot at the current render clock. Call it
        /// once per unit per frame: rotation advances along the render clock, and a
        /// second call inside the same frame is a deliberate no-op.
        /// </summary>
        public bool TrySample(int slot, out UnitViewPose pose)
        {
            pose = default;
            if ((uint)slot >= (uint)_capacity)
            {
                return false;
            }

            var newest = _slotNewestTick[slot];
            if (newest == EmptyTick)
            {
                return false;
            }

            var render = _renderTick;
            var baseIndex = slot * HistoryTicks;

            // Walk expected ticks newest -> oldest and accept a cell only when it
            // really holds that tick. This is what makes the sparse,
            // stride-changing stream safe: neighbours are found, never subtracted.
            var olderCell = -1;
            var olderTick = 0.0;
            var newerCell = -1;
            var newerTick = 0.0;
            for (var age = 0; age < HistoryTicks && (ulong)age < newest; age++)
            {
                // newest - age stays >= 1, so tick 0 — the empty sentinel that an
                // Array.Clear leaves in every untouched cell — can never be
                // mistaken for an occupied sample of tick 0.
                var tick = newest - (ulong)age;
                var cell = baseIndex + (int)(tick & HistoryMask);
                if (_sampleTick[cell] != tick)
                {
                    continue;
                }

                if ((double)tick <= render)
                {
                    olderCell = cell;
                    olderTick = tick;
                    break;
                }

                // Descending walk, so the last value written is the closest
                // occupied tick above the render clock.
                newerCell = cell;
                newerTick = tick;
            }

            if (olderCell < 0)
            {
                if (newerCell < 0)
                {
                    return false;
                }

                // The clock has not reached the oldest sample yet (fresh prime).
                // Hold on it: extrapolating backwards would invent motion that
                // authority never ordered.
                return WritePose(
                    slot,
                    newerCell,
                    render,
                    _bodyYawDegrees[newerCell],
                    _turretYawDegrees[newerCell],
                    _positionX[newerCell],
                    _positionZ[newerCell],
                    UnitViewPoseSource.Snapped,
                    out pose);
            }

            if (newerCell >= 0)
            {
                // newerTick > olderTick holds by construction (the walk only
                // assigns newer cells above the render clock), so this division can
                // never be 0/0 — the single-sample case never reaches here.
                var alpha = (render - olderTick) / (newerTick - olderTick);
                var interpolated = LerpMillimetres(_positionX[olderCell], _positionX[newerCell], alpha);
                var interpolatedZ = LerpMillimetres(_positionZ[olderCell], _positionZ[newerCell], alpha);
                var floatAlpha = (float)alpha;

                return WritePose(
                    slot,
                    olderCell,
                    render,
                    LerpAngleDegrees(_bodyYawDegrees[olderCell], _bodyYawDegrees[newerCell], floatAlpha),
                    LerpAngleDegrees(_turretYawDegrees[olderCell], _turretYawDegrees[newerCell], floatAlpha),
                    interpolated,
                    interpolatedZ,
                    UnitViewPoseSource.Interpolated,
                    out pose);
            }

            // Starved: the render clock has passed the newest authority sample.
            var lead = render - olderTick;
            if (lead <= 0.0)
            {
                return WritePose(
                    slot,
                    olderCell,
                    render,
                    _bodyYawDegrees[olderCell],
                    _turretYawDegrees[olderCell],
                    _positionX[olderCell],
                    _positionZ[olderCell],
                    UnitViewPoseSource.Snapped,
                    out pose);
            }

            _starvedSampleCount++;
            var budgetTicks = lead <= MaxExtrapolationTicks ? lead : MaxExtrapolationTicks;
            var source = lead >= MaxExtrapolationTicks
                ? UnitViewPoseSource.Held
                : UnitViewPoseSource.Extrapolated;

            var fromX = (double)_positionX[olderCell];
            var fromZ = (double)_positionZ[olderCell];
            var stepMm = budgetTicks * (MaxStepPerTickMm > 0 ? MaxStepPerTickMm : 0);
            var poseX = (float)fromX;
            var poseZ = (float)fromZ;

            if ((_flags[olderCell] & HasMoveTargetFlag) != 0)
            {
                var targetDx = (double)_moveTargetX[olderCell] - fromX;
                var targetDz = (double)_moveTargetZ[olderCell] - fromZ;
                var remainingMm = Math.Sqrt(targetDx * targetDx + targetDz * targetDz);

                if (remainingMm > 0.0)
                {
                    if (stepMm >= remainingMm)
                    {
                        // The destination is authoritative: arriving early and
                        // waiting beats running past it and snapping back.
                        poseX = _moveTargetX[olderCell];
                        poseZ = _moveTargetZ[olderCell];
                        source = UnitViewPoseSource.Held;
                    }
                    else
                    {
                        var scale = stepMm / remainingMm;
                        poseX = (float)(fromX + targetDx * scale);
                        poseZ = (float)(fromZ + targetDz * scale);
                    }
                }
            }
            else if (stepMm > 0.0)
            {
                // No destination on the wire: continue along the last
                // authoritative heading until the budget is spent, then hold.
                var radians = _bodyYawDegrees[olderCell] / DegreesPerRadian;
                poseX = (float)(fromX + Math.Sin(radians) * stepMm);
                poseZ = (float)(fromZ + Math.Cos(radians) * stepMm);
            }

            return WritePose(
                slot,
                olderCell,
                render,
                _bodyYawDegrees[olderCell],
                _turretYawDegrees[olderCell],
                poseX,
                poseZ,
                source,
                out pose);
        }

        /// <summary>
        /// Drops every captured sample while keeping yaw state, so a unit that is
        /// still alive does not spin on resume. Use it wherever the world cannot be
        /// interpolated across the break any more: the OD-18 tactical pause, an
        /// OD-20 resync, a table rebuild. The render clock re-anchors on the next
        /// capture instead of blending new positions with pre-pause ones.
        /// </summary>
        public void Flush()
        {
            ClearAllSamples();
            ResetSlotRenderClocks(0.0);
            _capturedTick = EmptyTick;
            _minimumAcceptableTick = EmptyTick;
            _observedIntervalTicks = 0.0;
            _observedJitterTicks = 0.0;
            _renderDelayTicks = MinRenderDelayTicks;
            _needsRenderClockAnchor = true;
        }

        /// <summary>
        /// <see cref="Flush"/> plus an explicit base: the render clock restarts
        /// <see cref="RenderDelayTicks"/> behind <paramref name="snapshotTick"/>, and
        /// any tick below that base is refused as pre-resync traffic.
        /// </summary>
        public void Resync(ulong snapshotTick)
        {
            Flush();
            if (snapshotTick == EmptyTick)
            {
                return;
            }

            _minimumAcceptableTick = snapshotTick;
            var anchor = (double)snapshotTick - _renderDelayTicks;
            _renderTick = anchor > 0.0 ? anchor : 0.0;
            _needsRenderClockAnchor = false;

            // Flush zeroed the per-slot clocks, which would read as the whole ring
            // jumping forward by the new base on the first sampled frame. Re-anchor
            // them on the base the match restarts at: the first pose after a resync
            // is a snap, and the turn resumes from the next tick of the new clock.
            ResetSlotRenderClocks(_renderTick);
        }

        /// <summary>
        /// Rotates <paramref name="current"/> towards <paramref name="target"/>
        /// along the shortest arc, at most
        /// <paramref name="maxDegreesPerSecond"/> x <paramref name="deltaSeconds"/>.
        /// Never emits NaN and never rotates without elapsed time, which keeps
        /// <see cref="TrySample"/> idempotent inside one frame.
        /// </summary>
        /// <param name="deltaSeconds">Elapsed time this rotation may use.</param>
        /// <remarks>
        /// Never emits NaN or an infinity: a non-finite target holds the last finite
        /// heading, and a non-finite current adopts the target outright. It also
        /// never rotates without elapsed time, which keeps <see cref="TrySample"/>
        /// idempotent inside one frame.
        /// </remarks>
        public static float SlewDegrees(
            float current,
            float target,
            float maxDegreesPerSecond,
            float deltaSeconds)
        {
            if (!IsFinite(target))
            {
                // Hold the last good heading rather than poison the transform.
                return IsFinite(current) ? current : 0f;
            }

            if (!IsFinite(current))
            {
                return NormalizeDegrees(target);
            }

            if (!(deltaSeconds > 0.0f) || !(maxDegreesPerSecond > 0.0f))
            {
                return current;
            }

            var delta = ShortestArcDegrees(current, target);
            var limit = maxDegreesPerSecond * deltaSeconds;
            if (delta <= limit && delta >= -limit)
            {
                return NormalizeDegrees(target);
            }

            return NormalizeDegrees(current + (delta >= 0.0f ? limit : -limit));
        }

        /// <summary>Signed shortest rotation from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static float ShortestArcDegrees(float from, float to)
        {
            if (!IsFinite(from) || !IsFinite(to))
            {
                // An infinity makes the modulo NaN, and a NaN arc is not merely a
                // lost frame: it is written back as the slot heading and then turns
                // every later rotation of that unit into NaN too. An unknown heading
                // contributes no rotation instead.
                return 0f;
            }

            var delta = (to - from) % 360f;
            if (delta > 180f)
            {
                delta -= 360f;
            }
            else if (delta < -180f)
            {
                delta += 360f;
            }

            return delta;
        }

        private static float NormalizeDegrees(float degrees)
        {
            var wrapped = degrees % 360f;
            if (wrapped == 0f)
            {
                // -360f wraps to -0.0f. It compares equal to zero but reads as "-0"
                // in a debug HUD and signs a zero yaw delta, so hand back the real
                // zero.
                return 0.0f;
            }

            return wrapped < 0f ? wrapped + 360f : wrapped;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// Heading in degrees for the <c>Quaternion.Euler(0, yaw, 0)</c> convention,
        /// or <see cref="float.NaN"/> when the displacement is inside the deadzone
        /// and the caller must keep the previous heading.
        /// </summary>
        private static float HeadingDegrees(int fromX, int fromZ, int toX, int toZ)
        {
            // Widened before subtracting: world coordinates reach
            // SimulationConstants.MaxWorldCoordinateMm, so an int millimetre
            // difference can sit one step below overflow.
            var dx = (double)toX - fromX;
            var dz = (double)toZ - fromZ;
            var squared = dx * dx + dz * dz;
            if (squared < YawDeadzoneMillimetres * YawDeadzoneMillimetres)
            {
                return float.NaN;
            }

            return NormalizeDegrees((float)(Math.Atan2(dx, dz) * DegreesPerRadian));
        }

        /// <summary>
        /// Hull yaw of one captured tick. The rule order matters: the
        /// authoritative destination is exact and stable, displacement is the
        /// fallback, and micro-noise keeps the previous heading instead of spinning
        /// the unit.
        /// </summary>
        private float ResolveBodyYaw(
            in ClientUnitState state,
            int previousCell,
            float fallback)
        {
            if (state.HasMoveTarget)
            {
                var toTarget = HeadingDegrees(
                    state.PosX, state.PosZ, state.MoveTargetX, state.MoveTargetZ);
                if (!float.IsNaN(toTarget))
                {
                    return toTarget;
                }
            }

            if (previousCell >= 0)
            {
                // Direction is scale invariant, so a cadence switch changes the
                // distance travelled but not the heading derived from it.
                var moved = HeadingDegrees(
                    _positionX[previousCell],
                    _positionZ[previousCell],
                    state.PosX,
                    state.PosZ);
                if (!float.IsNaN(moved))
                {
                    return moved;
                }
            }

            return fallback;
        }

        private static float ResolveTurretYaw(
            in ClientUnitState state,
            ClientReplicationWorld world,
            float bodyYaw,
            float fallback)
        {
            var attackTarget = state.AttackTarget;
            if (!attackTarget.IsValid || !world.TryGet(attackTarget, out var targetState))
            {
                // No target, or a target this client cannot see: the turret rests
                // in line with the hull.
                return bodyYaw;
            }

            var aimed = HeadingDegrees(
                state.PosX, state.PosZ, targetState.PosX, targetState.PosZ);
            return float.IsNaN(aimed) ? fallback : aimed;
        }

        private void ObserveArrival(ulong tick, ulong previousTick)
        {
            if (previousTick == EmptyTick)
            {
                return;
            }

            var interval = (double)(tick - previousTick);

            // Bound the sample before anything reads it. One 31-tick hole is below the
            // re-prime gate and so arrives here as if it were cadence: unbounded, the
            // EMA takes a third of it and the delay it feeds slams the play-out
            // ceiling for the next few packets, putting every unit on screen a
            // play-out delay behind authority because of one stall. Clamped, the same
            // spike costs less than one packet of extra delay, while the whole
            // supported range — 10 Hz, 5 Hz, and one loss at either — passes through
            // untouched.
            if (interval > MaxIntervalSampleTicks)
            {
                interval = MaxIntervalSampleTicks;
            }

            if (_observedIntervalTicks <= 0.0)
            {
                _observedIntervalTicks = interval;
            }
            else
            {
                _observedIntervalTicks += ArrivalEmaAlpha * (interval - _observedIntervalTicks);
                var deviation = interval - _observedIntervalTicks;
                if (deviation < 0.0)
                {
                    deviation = -deviation;
                }

                _observedJitterTicks += ArrivalEmaAlpha * (deviation - _observedJitterTicks);
            }

            // Delay in packets, not in ticks: ~4 ticks at 10 Hz and ~8 at the 5 Hz
            // floor. The fixed 3 ticks of the reviewed design left the render clock
            // behind the newest sample only while the cadence was nominal.
            var delay = DelayPacketsOfInterval * _observedIntervalTicks
                + DelayJitterPackets * _observedJitterTicks;
            if (delay < MinRenderDelayTicks)
            {
                delay = MinRenderDelayTicks;
            }
            else if (delay > MaxRenderDelayTicks)
            {
                delay = MaxRenderDelayTicks;
            }

            _renderDelayTicks = delay;
        }

        private void AnchorRenderClock()
        {
            var target = (double)_capturedTick - _renderDelayTicks;
            _renderTick = target > 0.0 ? target : 0.0;
            _needsRenderClockAnchor = false;

            // The global clock just moved to a base the slots never saw, so their
            // elapsed-time anchors move with it. Written on the re-prime path only.
            ResetSlotRenderClocks(_renderTick);
        }

        /// <summary>
        /// Re-bases every slot's elapsed-time anchor onto <paramref name="baseTick"/>.
        /// A per-slot clock that outlives the render clock it was read from is both a
        /// frozen turn (see <see cref="WritePose"/>) and a leak of the previous
        /// match's timings into the next one, so each place that resets or re-anchors
        /// the global clock resets these with it. Loop rather than fill: allocation
        /// free on every supported runtime, and this runs on a discontinuity, never
        /// per frame.
        /// </summary>
        private void ResetSlotRenderClocks(double baseTick)
        {
            for (var slot = 0; slot < _slotLastRenderTick.Length; slot++)
            {
                _slotLastRenderTick[slot] = baseTick;
            }
        }

        private void Reprime()
        {
            ClearAllSamples();

            // The gap is a discontinuity, not a cadence sample: dropping the
            // captured tick here makes the arrival estimator skip this pair instead
            // of folding a multi-second outage into the delay it derives.
            _capturedTick = EmptyTick;
            _reprimeCount++;
            _needsRenderClockAnchor = true;
        }

        private void ClearSlot(int slot)
        {
            var baseIndex = slot * HistoryTicks;
            Array.Clear(_sampleTick, baseIndex, HistoryTicks);
            _slotNewestTick[slot] = EmptyTick;
            _slotEntity[slot] = 0;
        }

        private void ClearAllSamples()
        {
            // Array.Clear allocates nothing, and a full wipe happens on a resync or
            // a pathological gap, never on the per-packet path. Slot identity and
            // resolved maximum health survive: they describe the unit, not a tick.
            Array.Clear(_sampleTick, 0, _sampleTick.Length);
            Array.Clear(_slotNewestTick, 0, _slotNewestTick.Length);
        }

        private bool WritePose(
            int slot,
            int healthCell,
            double render,
            float desiredBodyYaw,
            float desiredTurretYaw,
            float xMillimetres,
            float zMillimetres,
            UnitViewPoseSource source,
            out UnitViewPose pose)
        {
            // Rotation advances along the render clock instead of a caller supplied
            // dt: that makes it frame-rate independent and makes a repeated read
            // inside one frame leave the yaw untouched.
            var elapsedTicks = render - _slotLastRenderTick[slot];
            if (!(elapsedTicks > 0.0))
            {
                // A resync or a drift re-anchor moved the clock backwards. Keeping
                // the stale anchor here clamped dt to 0 on every later frame until
                // the clock climbed back past it, which froze the turn for the rest
                // of the match because the starvation ceiling parks the clock below
                // the pre-resync reading for good. Re-anchor on the new base and turn
                // from it; the equal case writes the same value, so the per-frame
                // idempotence of TrySample survives.
                _slotLastRenderTick[slot] = render;
                elapsedTicks = 0.0;
            }
            else
            {
                // Cap the elapsed time the slew may see. The render clock may jump
                // forward by more than one clamped frame (a resync to a new base, a
                // catch-up snap towards authority), and an uncapped dt buys the whole
                // turn in one read, which is the instant 180 degree flip this limit
                // exists to prevent.
                if (elapsedTicks > MaxAdvanceTicksPerCall)
                {
                    elapsedTicks = MaxAdvanceTicksPerCall;
                }

                _slotLastRenderTick[slot] = render;
            }

            var dt = (float)(elapsedTicks * _tickDurationSeconds);
            var bodyYaw = SlewDegrees(
                _slotBodyYaw[slot], desiredBodyYaw, MaxBodyDegreesPerSecond, dt);
            var turretYaw = SlewDegrees(
                _slotTurretYaw[slot], desiredTurretYaw, MaxTurretDegreesPerSecond, dt);
            _slotBodyYaw[slot] = bodyYaw;
            _slotTurretYaw[slot] = turretYaw;

            var health = _health[healthCell];
            var fraction = health * _slotInvMaxHealth[slot];
            // Ordered so NaN fails the first test and lands on 0: a two-sided
            // comparison would let a poisoned denominator reach the bar.
            if (!(fraction > 0f))
            {
                fraction = 0f;
            }
            else if (fraction > 1f)
            {
                fraction = 1f;
            }

            pose = new UnitViewPose(
                xMillimetres,
                zMillimetres,
                health,
                fraction,
                bodyYaw,
                turretYaw,
                source);
            return true;
        }

        private static float LerpMillimetres(int from, int to, double alpha)
        {
            // Double precision: the millimetre span of a world coordinate
            // difference leaves almost no headroom in int32.
            var f = (double)from;
            return (float)(f + ((double)to - f) * alpha);
        }

        private static float LerpAngleDegrees(float from, float to, float alpha)
        {
            return NormalizeDegrees(from + ShortestArcDegrees(from, to) * alpha);
        }
    }
}
