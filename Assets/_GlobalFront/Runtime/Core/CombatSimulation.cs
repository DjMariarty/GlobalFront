using System;
using System.Collections.Generic;
using System.Threading;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Combat
{
    public readonly struct CombatStats : IEquatable<CombatStats>
    {
        public CombatStats(
            int maximumHealth,
            int damage,
            int rangeMm,
            uint cooldownTicks)
        {
            if (maximumHealth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumHealth));
            }

            if (damage <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(damage));
            }

            if (rangeMm < 0 ||
                rangeMm > SimulationConstants.MaxWorldCoordinateMm)
            {
                throw new ArgumentOutOfRangeException(nameof(rangeMm));
            }

            if (cooldownTicks == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cooldownTicks));
            }

            MaximumHealth = maximumHealth;
            Damage = damage;
            RangeMm = rangeMm;
            CooldownTicks = cooldownTicks;
        }

        public int MaximumHealth { get; }

        public int Damage { get; }

        public int RangeMm { get; }

        public uint CooldownTicks { get; }

        public bool IsValid =>
            MaximumHealth > 0 &&
            Damage > 0 &&
            RangeMm >= 0 &&
            RangeMm <= SimulationConstants.MaxWorldCoordinateMm &&
            CooldownTicks > 0;

        public bool Equals(CombatStats other) =>
            MaximumHealth == other.MaximumHealth &&
            Damage == other.Damage &&
            RangeMm == other.RangeMm &&
            CooldownTicks == other.CooldownTicks;

        public override bool Equals(object obj) =>
            obj is CombatStats other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(MaximumHealth, Damage, RangeMm, CooldownTicks);
    }

    public sealed class CombatantState
    {
        public CombatantState(
            EntityId entity,
            PlayerId owner,
            CombatStats stats)
        {
            if (!entity.IsValid)
            {
                throw new ArgumentException("Entity must be valid.", nameof(entity));
            }

            if (!owner.IsValid)
            {
                throw new ArgumentException("Owner must be valid.", nameof(owner));
            }

            if (!stats.IsValid)
            {
                throw new ArgumentException("Combat stats must be valid.", nameof(stats));
            }

            Entity = entity;
            Owner = owner;
            Stats = stats;
            CurrentHealth = stats.MaximumHealth;
        }

        public EntityId Entity { get; }

        public PlayerId Owner { get; }

        public CombatStats Stats { get; }

        public int CurrentHealth { get; private set; }

        public EntityId AttackTarget { get; private set; }

        public ulong NextAttackTick { get; private set; }

        private bool AttackScheduleExhausted { get; set; }

        public bool IsAlive => CurrentHealth > 0;

        public bool HasAttackTarget => AttackTarget.IsValid;

        public bool TryAssignTarget(EntityId target)
        {
            if (!IsAlive || !target.IsValid || target == Entity)
            {
                return false;
            }

            AttackTarget = target;
            return true;
        }

        public void ClearTarget()
        {
            AttackTarget = default;
        }

        /// <summary>
        /// Overwrites presentation-relevant state from an authoritative source.
        /// Called by the hosting layer after each server tick to synchronize
        /// the client-side combat state with the server's snapshot. Does not
        /// touch <see cref="NextAttackTick"/> or
        /// <see cref="AttackScheduleExhausted"/> because the client no longer
        /// resolves combat locally.
        /// </summary>
        public void SynchronizeFromAuthoritative(
            int currentHealth,
            EntityId attackTarget)
        {
            CurrentHealth = currentHealth;

            if (!IsAlive)
            {
                ClearTarget();
                return;
            }

            AttackTarget = attackTarget.IsValid && attackTarget != Entity
                ? attackTarget
                : default;
        }

        public bool CanFire(ulong tick) =>
            IsAlive &&
            HasAttackTarget &&
            !AttackScheduleExhausted &&
            tick >= NextAttackTick;

        public void CommitShot(ulong tick)
        {
            if (!CanFire(tick))
            {
                throw new InvalidOperationException("Combatant cannot fire on this tick.");
            }

            if (tick > ulong.MaxValue - Stats.CooldownTicks)
            {
                NextAttackTick = ulong.MaxValue;
                AttackScheduleExhausted = true;
                return;
            }

            NextAttackTick = tick + Stats.CooldownTicks;
        }

        public bool ApplyDamage(long damage)
        {
            if (!IsAlive || damage <= 0)
            {
                return false;
            }

            if (damage >= CurrentHealth)
            {
                CurrentHealth = 0;
                ClearTarget();
                return true;
            }

            CurrentHealth -= (int)damage;
            return false;
        }
    }

    public readonly struct CombatantTickInput : IComparable<CombatantTickInput>
    {
        public CombatantTickInput(CombatantState state, WorldPointMm position)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Position = position;
        }

        public CombatantState State { get; }

        public WorldPointMm Position { get; }

        public int CompareTo(CombatantTickInput other) =>
            State.Entity.Value.CompareTo(other.State.Entity.Value);
    }

    public readonly struct DamageEvent : IEquatable<DamageEvent>
    {
        public DamageEvent(EntityId attacker, EntityId target, int damage)
        {
            Attacker = attacker;
            Target = target;
            Damage = damage;
        }

        public EntityId Attacker { get; }

        public EntityId Target { get; }

        public int Damage { get; }

        public bool Equals(DamageEvent other) =>
            Attacker == other.Attacker &&
            Target == other.Target &&
            Damage == other.Damage;

        public override bool Equals(object obj) =>
            obj is DamageEvent other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Attacker, Target, Damage);
    }

    public enum BattleOutcomeKind : byte
    {
        InProgress = 0,
        Victory = 1,
        Draw = 2
    }

    public readonly struct BattleOutcome : IEquatable<BattleOutcome>
    {
        private BattleOutcome(
            BattleOutcomeKind kind,
            PlayerId winner,
            TeamId winningTeam)
        {
            Kind = kind;
            Winner = winner;
            WinningTeam = winningTeam;
        }

        public BattleOutcomeKind Kind { get; }

        public PlayerId Winner { get; }

        /// <summary>
        /// Winning team, and meaningful only when
        /// <see cref="IsTeamVictory"/> holds. A player victory leaves it at
        /// <c>default(TeamId)</c>, which happens to be Team Red because
        /// <see cref="TeamId"/> has no unset sentinel — never read it unless the
        /// outcome came from <see cref="TeamVictory"/>.
        /// </summary>
        public TeamId WinningTeam { get; }

        public bool IsTerminal => Kind != BattleOutcomeKind.InProgress;

        /// <summary>
        /// True for a victory awarded to a team rather than to a player, in
        /// which case <see cref="Winner"/> is deliberately invalid.
        /// </summary>
        public bool IsTeamVictory =>
            Kind == BattleOutcomeKind.Victory && !Winner.IsValid;

        public static BattleOutcome InProgress =>
            new BattleOutcome(BattleOutcomeKind.InProgress, default, default);

        public static BattleOutcome Draw =>
            new BattleOutcome(BattleOutcomeKind.Draw, default, default);

        public static BattleOutcome Victory(PlayerId winner)
        {
            if (!winner.IsValid)
            {
                throw new ArgumentException("Winner must be valid.", nameof(winner));
            }

            return new BattleOutcome(BattleOutcomeKind.Victory, winner, default);
        }

        /// <summary>
        /// Awards the battle to a whole team (Phase 1.0, P1-2). Carries no
        /// winning player because no single member of the team won.
        /// </summary>
        public static BattleOutcome TeamVictory(TeamId winningTeam) =>
            new BattleOutcome(BattleOutcomeKind.Victory, default, winningTeam);

        public bool Equals(BattleOutcome other) =>
            Kind == other.Kind &&
            Winner == other.Winner &&
            WinningTeam == other.WinningTeam;

        public override bool Equals(object obj) =>
            obj is BattleOutcome other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine((byte)Kind, Winner, WinningTeam);
    }

    public readonly struct CombatTickResult
    {
        private readonly DamageEvent[] _events;
        private readonly int _eventCount;

        internal CombatTickResult(DamageEvent[] events, int eventCount, BattleOutcome outcome)
        {
            _events = events ?? Array.Empty<DamageEvent>();
            _eventCount = eventCount;
            Outcome = outcome;
        }

        internal CombatTickResult(DamageEvent[] events, BattleOutcome outcome)
            : this(events, events?.Length ?? 0, outcome)
        {
        }

        public int EventCount => _eventCount;

        public BattleOutcome Outcome { get; }

        public DamageEvent GetEvent(int index)
        {
            if (index < 0 || index >= _eventCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return _events[index];
        }
    }

    public static class CombatMath
    {
        public static ulong SquaredDistance(WorldPointMm left, WorldPointMm right)
        {
            var deltaX = Absolute((long)left.X - right.X);
            var deltaZ = Absolute((long)left.Z - right.Z);
            var squaredX = deltaX * deltaX;
            var squaredZ = deltaZ * deltaZ;

            return ulong.MaxValue - squaredX < squaredZ
                ? ulong.MaxValue
                : squaredX + squaredZ;
        }

        public static bool IsWithinRange(
            WorldPointMm left,
            WorldPointMm right,
            int rangeMm)
        {
            if (rangeMm < 0)
            {
                return false;
            }

            var unsignedRange = (ulong)rangeMm;
            return SquaredDistance(left, right) <= unsignedRange * unsignedRange;
        }

        private static ulong Absolute(long value) =>
            value < 0 ? (ulong)(-value) : (ulong)value;
    }

    /// <summary>
    /// Resolves one combat tick. All scratch state is owned by the instance, so
    /// two MatchServers may resolve on different threads without interleaving
    /// into each other's roster (Phase 1.0, P1-1).
    /// </summary>
    public sealed class CombatTickResolver
    {
        private sealed class CombatantComparer : IComparer<CombatantTickInput>
        {
            public static readonly CombatantComparer Instance = new CombatantComparer();
            public int Compare(CombatantTickInput left, CombatantTickInput right) =>
                left.State.Entity.Value.CompareTo(right.State.Entity.Value);
        }

        private CombatantTickInput[] _canonical =
            new CombatantTickInput[SimulationConstants.MaxUnitsPerMatch];
        private readonly Dictionary<EntityId, CombatantTickInput> _byEntity =
            new Dictionary<EntityId, CombatantTickInput>(SimulationConstants.MaxUnitsPerMatch);
        private readonly List<DamageEvent> _events =
            new List<DamageEvent>(SimulationConstants.MaxUnitsPerMatch);
        private readonly Dictionary<EntityId, long> _accumulatedDamage =
            new Dictionary<EntityId, long>(SimulationConstants.MaxUnitsPerMatch);

        private readonly DamageEvent[][] _eventBuffers = new DamageEvent[][]
        {
            new DamageEvent[SimulationConstants.MaxUnitsPerMatch],
            new DamageEvent[SimulationConstants.MaxUnitsPerMatch],
            new DamageEvent[SimulationConstants.MaxUnitsPerMatch],
            new DamageEvent[SimulationConstants.MaxUnitsPerMatch]
        };
        private int _bufferIndex;

        /// <summary>
        /// 0 while this instance is idle, 1 for the duration of one
        /// <see cref="Resolve(CombatantTickInput[], int, ulong, ICombatRelation)"/> call.
        /// The scratch buffers below are cleared on entry, so a second resolve on the
        /// same instance would erase the first one's roster mid-tick. The check and the
        /// claim are one <see cref="Interlocked.CompareExchange(ref int, int, int)"/> and
        /// the release is one <see cref="Interlocked.Exchange(ref int, int)"/>, so two
        /// threads racing to enter cannot both pass: exactly one owns the instance and
        /// every other is refused. Being an <see cref="int"/> rather than a
        /// <see cref="bool"/> is what makes that exchange atomic, and the release in the
        /// <c>finally</c> is a full barrier, so a thread that observes the claim also
        /// observes the roster it protects.
        /// </summary>
        private int _isResolving;

        /// <summary>
        /// Backward-compatibility seam for callers that have no resolver of their
        /// own. One instance per thread, so it is thread-safe across threads and
        /// guarded against reentrancy on the same instance, but it is not an
        /// ownership boundary: authoritative code must own a resolver.
        /// A ThreadStatic field carries no initializer because one would run on the
        /// first thread only.
        /// </summary>
        [ThreadStatic]
        private static CombatTickResolver s_threadStaticInstance;

        private static CombatTickResolver ThreadDefault =>
            s_threadStaticInstance ??= new CombatTickResolver();

        /// <summary>
        /// Resolves the whole array on the calling thread's default resolver.
        /// The count-carrying form is instance-only.
        /// </summary>
        public static CombatTickResult Resolve(
            CombatantTickInput[] combatants,
            ulong tick,
            ICombatRelation relation = null) =>
            ThreadDefault.Resolve(combatants, combatants?.Length ?? 0, tick, relation);

        /// <summary>
        /// Resolves one tick. The returned events alias a four-slot ring owned by
        /// this instance, so the result is only valid until four further resolves
        /// on the same resolver. Strictly non-reentrant: the claim is taken atomically
        /// with <see cref="Interlocked.CompareExchange(ref int, int, int)"/>, so calling
        /// back into this instance during a resolve — from the same thread or from another
        /// one that shares it — throws instead of corrupting the roster mid-tick. Only one
        /// caller is ever inside the body, which is mutual exclusion for the claim, not for
        /// the whole instance: concurrent resolves are refused, never serialized.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when this instance is already resolving, whether the holder is the calling
        /// thread or one of the others. The claim is released by a <c>finally</c>, so a
        /// resolve that throws — including the argument checks below and the duplicate-entity
        /// <see cref="ArgumentException"/> — leaves the instance usable.
        /// </exception>
        public CombatTickResult Resolve(
            CombatantTickInput[] combatants,
            int count,
            ulong tick,
            ICombatRelation relation = null)
        {
            if (Interlocked.CompareExchange(ref _isResolving, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "CombatTickResolver is not reentrant. Recursive calls to Resolve " +
                    "on the same instance are not supported.");
            }

            try
            {
                if (combatants == null)
                {
                    throw new ArgumentNullException(nameof(combatants));
                }

                if (count < 0 || count > combatants.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }

                // Resolved once so every hostility and outcome decision in the tick
                // uses the same relation (Phase 1.0, P1-2).
                var hostility = relation ?? SkirmishCombatRelation.Instance;

                _byEntity.Clear();
                _events.Clear();
                _accumulatedDamage.Clear();

                if (_canonical.Length < count)
                {
                    var newCapacity = Math.Max(count, Math.Max(_canonical.Length * 2, 64));
                    _canonical = new CombatantTickInput[newCapacity];
                }

                Array.Copy(combatants, 0, _canonical, 0, count);
                SortCanonical(_canonical, count);

                for (var index = 0; index < count; index++)
                {
                    var input = _canonical[index];
                    if (_byEntity.ContainsKey(input.State.Entity))
                    {
                        throw new ArgumentException(
                            "Combatant entities must be unique.",
                            nameof(combatants));
                    }

                    _byEntity.Add(input.State.Entity, input);
                }

                ClearInvalidTargets(_canonical, count, _byEntity, hostility);

                for (var index = 0; index < count; index++)
                {
                    var attackerInput = _canonical[index];
                    var attacker = attackerInput.State;
                    if (!attacker.IsAlive ||
                        !attacker.HasAttackTarget ||
                        !attacker.CanFire(tick) ||
                        !_byEntity.TryGetValue(attacker.AttackTarget, out var targetInput) ||
                        !CombatMath.IsWithinRange(
                            attackerInput.Position,
                            targetInput.Position,
                            attacker.Stats.RangeMm))
                    {
                        continue;
                    }

                    attacker.CommitShot(tick);
                    var damageEvent = new DamageEvent(
                        attacker.Entity,
                        targetInput.State.Entity,
                        attacker.Stats.Damage);
                    _events.Add(damageEvent);

                    _accumulatedDamage.TryGetValue(
                        damageEvent.Target,
                        out var currentDamage);
                    _accumulatedDamage[damageEvent.Target] =
                        currentDamage > long.MaxValue - damageEvent.Damage
                            ? long.MaxValue
                            : currentDamage + damageEvent.Damage;
                }

                for (var index = 0; index < count; index++)
                {
                    var target = _canonical[index].State;
                    if (_accumulatedDamage.TryGetValue(target.Entity, out var damage))
                    {
                        target.ApplyDamage(damage);
                    }
                }

                ClearInvalidTargets(_canonical, count, _byEntity, hostility);

                var outcome = hostility.DetermineOutcome(_canonical, count);
                if (_events.Count == 0)
                {
                    return new CombatTickResult(Array.Empty<DamageEvent>(), 0, outcome);
                }

                _bufferIndex = (_bufferIndex + 1) % _eventBuffers.Length;
                var buffer = _eventBuffers[_bufferIndex];
                if (buffer.Length < _events.Count)
                {
                    buffer = new DamageEvent[Math.Max(_events.Count, buffer.Length * 2)];
                    _eventBuffers[_bufferIndex] = buffer;
                }

                _events.CopyTo(buffer, 0);
                return new CombatTickResult(buffer, _events.Count, outcome);
            }
            finally
            {
                Interlocked.Exchange(ref _isResolving, 0);
            }
        }

        private static void ClearInvalidTargets(
            CombatantTickInput[] canonical,
            int count,
            Dictionary<EntityId, CombatantTickInput> byEntity,
            ICombatRelation hostility)
        {
            for (var index = 0; index < count; index++)
            {
                var attacker = canonical[index].State;
                if (!attacker.IsAlive)
                {
                    attacker.ClearTarget();
                    continue;
                }

                if (!attacker.HasAttackTarget)
                {
                    continue;
                }

                if (!byEntity.TryGetValue(attacker.AttackTarget, out var target) ||
                    !target.State.IsAlive ||
                    !hostility.AreEnemies(attacker.Owner, target.State.Owner))
                {
                    attacker.ClearTarget();
                }
            }
        }

        private static void SortCanonical(CombatantTickInput[] array, int count)
        {
            if (count <= 1) return;
            var isSorted = true;
            for (var i = 1; i < count; i++)
            {
                if (array[i].State.Entity.Value < array[i - 1].State.Entity.Value)
                {
                    isSorted = false;
                    break;
                }
            }
            if (isSorted) return;

            QuickSort(array, 0, count - 1);
        }

        private static void QuickSort(CombatantTickInput[] array, int left, int right)
        {
            var i = left;
            var j = right;
            var pivot = array[(left + right) / 2].State.Entity.Value;

            while (i <= j)
            {
                while (array[i].State.Entity.Value < pivot) i++;
                while (array[j].State.Entity.Value > pivot) j--;
                if (i <= j)
                {
                    var temp = array[i];
                    array[i] = array[j];
                    array[j] = temp;
                    i++;
                    j--;
                }
            }

            if (left < j) QuickSort(array, left, j);
            if (i < right) QuickSort(array, i, right);
        }
    }
}
