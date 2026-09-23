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
    ///    permanently past the newest sample and freeze the whole view.
    /// 5. Starvation clamps toward the authoritative move target
    ///    (<see cref="MaxExtrapolationTicks"/> ticks at
    ///    <see cref="UnitViewTickBuffer.MaxStepPerTickMm"/>) and then holds, which
    ///    turns a 0.7 m stop-and-go at 10 Hz (1.4 m at 5 Hz) into a short reach
    ///    plus a hold.
    /// 6. The render clock is continuously re-anchored toward the leading edge of
    ///    the ring, so a clamped catch-up cannot accumulate drift until the buffer
    ///    starves for the rest of the match.
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

        /// <summary>Above this skew the render clock is snapped rather than nudged.</summary>
        private const double DriftSnapTicks = HistoryTicks * 0.5;

        /// <summary>Rate at which a bounded skew is bled off, fraction per second.</summary>
        private const double DriftCorrectionPerSecond = 0.05;

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
        /// OD-29 whose stats the binder resolved elsewhere). It wins over the
        /// catalog for that slot until the slot is recycled, and 0 is a legal input:
        /// it yields an empty bar rather than the <c>0/0 = NaN</c> that
        /// <c>PrototypeUnit.MaximumHealth</c> can produce today.
        /// </summary>
        public void SetSlotMaximumHealth(int slot, int maximumHealth)
        {
            if ((uint)slot >= (uint)_capacity)
            {
                return;
            }

            _slotInvMaxHealth[slot] = maximumHealth > 0 ? 1f / maximumHealth : 0f;
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
                    // client world is authoritative over what may be presented.
                    if (_slotNewestTick[slot] != EmptyTick)
                    {
                        ClearSlot(slot);
                    }

                    continue;
                }

                var baseIndex = slot * HistoryTicks;
                var recycled = _slotEntity[slot] != state.Entity.Value;
                if (recycled)
                {
                    // The table recycles freed slots, so a slot can start holding a
                    // different unit whose history is not this unit's at all.
                    ClearSlot(slot);
                    _slotEntity[slot] = state.Entity.Value;
                    _slotInvMaxHealth[slot] = 0f;

                    // Presentation state is per slot, not per unit. Left alone, the
                    // replacement inherits the casualty's heading and slew clock, and
                    // a unit that took over a slot facing the other way spends up to
                    // three quarters of a second turning through the dead unit's turn
                    // — Mecanim-free presentation has nothing else to hide it with.
                    _slotBodyYaw[slot] = 0f;
                    _slotTurretYaw[slot] = 0f;
                    _slotLastRenderTick[slot] = 0.0;
                    _recycledSlotCount++;
                }

                var hadSample = _slotNewestTick[slot] != EmptyTick;
                var previousCell = hadSample
                    ? baseIndex + (int)(_slotNewestTick[slot] & HistoryMask)
                    : -1;

                // OD-29: a slot that starts holding a unit resolves its health
                // denominator from the replicated archetype exactly once, here. Only
                // when the slot is unresolved, so a binder that already called
                // SetSlotMaximumHealth keeps its value, and an Unknown archetype
                // leaves the 0 that makes the bar read empty instead of dividing.
                if (!hadSample && _slotInvMaxHealth[slot] == 0f &&
                    _catalog.TryGet(state.UnitKind, out var definition))
                {
                    _slotInvMaxHealth[slot] = definition.InvMaxHealth;
                }

                // "Keep the previous heading" must read the last <b>captured</b>
                // yaw, not the slewed presentation yaw: the latter is owned by
                // TrySample, and overwriting it per packet would turn every
                // arrival into an instant snap and defeat the slew entirely.
                var heldBodyYaw = previousCell >= 0
                    ? _bodyYawDegrees[previousCell]
                    : _slotBodyYaw[slot];
                var heldTurretYaw = previousCell >= 0
                    ? _turretYawDegrees[previousCell]
                    : _slotTurretYaw[slot];

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
                    // so the first pose rendered for this slot is exact. Zeroing the yaw
                    // above is not enough on its own: the slew would still start there
                    // and crawl to the real heading at MaxBodyDegreesPerSecond, and the
                    // zeroed render-clock anchor only buys an instant turn in as much as
                    // the elapsed time happens to allow.
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

            var skew = _renderTick - target;
            if (skew > DriftSnapTicks || skew < -DriftSnapTicks)
            {
                _renderTick = target;
            }
            else
            {
                var correction = DriftCorrectionPerSecond * unscaledDeltaSeconds;
                _renderTick -= skew * (correction < 1.0 ? correction : 1.0);
            }

            // Park instead of overshooting: while starved, the clock is capped at
            // the extrapolation budget past the newest sample. Without the cap the
            // clock runs tens of ticks past authority, the drift snap then yanks it
            // back, and every yank teleports the unit from its clamped reach to the
            // last authoritative point and out again — a worse artefact than the
            // freeze this path replaced.
            var ceiling = (double)_capturedTick + MaxExtrapolationTicks;
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
        }

        /// <summary>
        /// Rotates <paramref name="current"/> towards <paramref name="target"/>
        /// along the shortest arc, at most
        /// <paramref name="maxDegreesPerSecond"/> x <paramref name="deltaSeconds"/>.
        /// Never emits NaN and never rotates without elapsed time, which keeps
        /// <see cref="TrySample"/> idempotent inside one frame.
        /// </summary>
        public static float SlewDegrees(
            float current,
            float target,
            float maxDegreesPerSecond,
            float deltaSeconds)
        {
            if (float.IsNaN(target))
            {
                // Hold the last good heading rather than poison the transform.
                return float.IsNaN(current) ? 0f : current;
            }

            if (float.IsNaN(current))
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
            return wrapped < 0f ? wrapped + 360f : wrapped;
        }

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
            if (elapsedTicks <= 0.0)
            {
                elapsedTicks = 0.0;
            }
            else
            {
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
            if (fraction > 1f)
            {
                fraction = 1f;
            }
            else if (fraction < 0f)
            {
                fraction = 0f;
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
