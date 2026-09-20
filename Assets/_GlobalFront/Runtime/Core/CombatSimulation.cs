using System;
using System.Collections.Generic;
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
        private BattleOutcome(BattleOutcomeKind kind, PlayerId winner)
        {
            Kind = kind;
            Winner = winner;
        }

        public BattleOutcomeKind Kind { get; }

        public PlayerId Winner { get; }

        public bool IsTerminal => Kind != BattleOutcomeKind.InProgress;

        public static BattleOutcome InProgress =>
            new BattleOutcome(BattleOutcomeKind.InProgress, default);

        public static BattleOutcome Draw =>
            new BattleOutcome(BattleOutcomeKind.Draw, default);

        public static BattleOutcome Victory(PlayerId winner)
        {
            if (!winner.IsValid)
            {
                throw new ArgumentException("Winner must be valid.", nameof(winner));
            }

            return new BattleOutcome(BattleOutcomeKind.Victory, winner);
        }

        public bool Equals(BattleOutcome other) =>
            Kind == other.Kind && Winner == other.Winner;

        public override bool Equals(object obj) =>
            obj is BattleOutcome other && Equals(other);

        public override int GetHashCode() => HashCode.Combine((byte)Kind, Winner);
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

    public static class CombatTickResolver
    {
        private sealed class CombatantComparer : IComparer<CombatantTickInput>
        {
            public static readonly CombatantComparer Instance = new CombatantComparer();
            public int Compare(CombatantTickInput left, CombatantTickInput right) =>
                left.State.Entity.Value.CompareTo(right.State.Entity.Value);
        }

        private static CombatantTickInput[] s_canonical = new CombatantTickInput[1024];
        private static readonly Dictionary<EntityId, CombatantTickInput> s_byEntity =
            new Dictionary<EntityId, CombatantTickInput>(1024);
        private static readonly List<DamageEvent> s_events = new List<DamageEvent>(1024);
        private static readonly Dictionary<EntityId, long> s_accumulatedDamage =
            new Dictionary<EntityId, long>(1024);

        private static readonly DamageEvent[][] s_eventBuffers = new DamageEvent[][]
        {
            new DamageEvent[1024],
            new DamageEvent[1024],
            new DamageEvent[1024],
            new DamageEvent[1024]
        };
        private static int s_bufferIndex;

        public static CombatTickResult Resolve(
            CombatantTickInput[] combatants,
            ulong tick) =>
            Resolve(combatants, combatants?.Length ?? 0, tick);

        public static CombatTickResult Resolve(
            CombatantTickInput[] combatants,
            int count,
            ulong tick)
        {
            if (combatants == null)
            {
                throw new ArgumentNullException(nameof(combatants));
            }

            if (count < 0 || count > combatants.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            s_byEntity.Clear();
            s_events.Clear();
            s_accumulatedDamage.Clear();

            if (s_canonical.Length < count)
            {
                var newCapacity = Math.Max(count, Math.Max(s_canonical.Length * 2, 64));
                s_canonical = new CombatantTickInput[newCapacity];
            }

            Array.Copy(combatants, 0, s_canonical, 0, count);
            SortCanonical(s_canonical, count);

            for (var index = 0; index < count; index++)
            {
                var input = s_canonical[index];
                if (s_byEntity.ContainsKey(input.State.Entity))
                {
                    throw new ArgumentException(
                        "Combatant entities must be unique.",
                        nameof(combatants));
                }

                s_byEntity.Add(input.State.Entity, input);
            }

            ClearInvalidTargets(s_canonical, count, s_byEntity);

            for (var index = 0; index < count; index++)
            {
                var attackerInput = s_canonical[index];
                var attacker = attackerInput.State;
                if (!attacker.IsAlive ||
                    !attacker.HasAttackTarget ||
                    !attacker.CanFire(tick) ||
                    !s_byEntity.TryGetValue(attacker.AttackTarget, out var targetInput) ||
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
                s_events.Add(damageEvent);

                s_accumulatedDamage.TryGetValue(
                    damageEvent.Target,
                    out var currentDamage);
                s_accumulatedDamage[damageEvent.Target] =
                    currentDamage > long.MaxValue - damageEvent.Damage
                        ? long.MaxValue
                        : currentDamage + damageEvent.Damage;
            }

            for (var index = 0; index < count; index++)
            {
                var target = s_canonical[index].State;
                if (s_accumulatedDamage.TryGetValue(target.Entity, out var damage))
                {
                    target.ApplyDamage(damage);
                }
            }

            ClearInvalidTargets(s_canonical, count, s_byEntity);

            var outcome = DetermineOutcome(s_canonical, count);
            if (s_events.Count == 0)
            {
                return new CombatTickResult(Array.Empty<DamageEvent>(), 0, outcome);
            }

            s_bufferIndex = (s_bufferIndex + 1) % s_eventBuffers.Length;
            var buffer = s_eventBuffers[s_bufferIndex];
            if (buffer.Length < s_events.Count)
            {
                buffer = new DamageEvent[Math.Max(s_events.Count, buffer.Length * 2)];
                s_eventBuffers[s_bufferIndex] = buffer;
            }

            s_events.CopyTo(buffer, 0);
            return new CombatTickResult(buffer, s_events.Count, outcome);
        }

        private static void ClearInvalidTargets(
            CombatantTickInput[] canonical,
            int count,
            Dictionary<EntityId, CombatantTickInput> byEntity)
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
                    target.State.Owner == attacker.Owner)
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

        private static BattleOutcome DetermineOutcome(CombatantTickInput[] canonical, int count)
        {
            var hasLivingOwner = false;
            var soleOwner = default(PlayerId);

            for (var index = 0; index < count; index++)
            {
                var state = canonical[index].State;
                if (!state.IsAlive)
                {
                    continue;
                }

                if (!hasLivingOwner)
                {
                    hasLivingOwner = true;
                    soleOwner = state.Owner;
                    continue;
                }

                if (state.Owner != soleOwner)
                {
                    return BattleOutcome.InProgress;
                }
            }

            return hasLivingOwner
                ? BattleOutcome.Victory(soleOwner)
                : BattleOutcome.Draw;
        }
    }
}
