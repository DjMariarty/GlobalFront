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

    public readonly struct CombatantTickInput
    {
        public CombatantTickInput(CombatantState state, WorldPointMm position)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Position = position;
        }

        public CombatantState State { get; }

        public WorldPointMm Position { get; }
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

    public sealed class CombatTickResult
    {
        private readonly DamageEvent[] _events;

        internal CombatTickResult(DamageEvent[] events, BattleOutcome outcome)
        {
            _events = events;
            Outcome = outcome;
        }

        public int EventCount => _events.Length;

        public BattleOutcome Outcome { get; }

        public DamageEvent GetEvent(int index) => _events[index];
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
        public static CombatTickResult Resolve(
            CombatantTickInput[] combatants,
            ulong tick)
        {
            if (combatants == null)
            {
                throw new ArgumentNullException(nameof(combatants));
            }

            var canonical = new CombatantTickInput[combatants.Length];
            Array.Copy(combatants, canonical, combatants.Length);
            Array.Sort(canonical, CompareCombatants);

            var byEntity = new Dictionary<EntityId, CombatantTickInput>(canonical.Length);
            for (var index = 0; index < canonical.Length; index++)
            {
                var input = canonical[index];
                if (byEntity.ContainsKey(input.State.Entity))
                {
                    throw new ArgumentException(
                        "Combatant entities must be unique.",
                        nameof(combatants));
                }

                byEntity.Add(input.State.Entity, input);
            }

            ClearInvalidTargets(canonical, byEntity);

            var events = new List<DamageEvent>();
            var accumulatedDamage = new Dictionary<EntityId, long>();

            for (var index = 0; index < canonical.Length; index++)
            {
                var attackerInput = canonical[index];
                var attacker = attackerInput.State;
                if (!attacker.IsAlive ||
                    !attacker.HasAttackTarget ||
                    !attacker.CanFire(tick) ||
                    !byEntity.TryGetValue(attacker.AttackTarget, out var targetInput) ||
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
                events.Add(damageEvent);

                accumulatedDamage.TryGetValue(
                    damageEvent.Target,
                    out var currentDamage);
                accumulatedDamage[damageEvent.Target] =
                    currentDamage > long.MaxValue - damageEvent.Damage
                        ? long.MaxValue
                        : currentDamage + damageEvent.Damage;
            }

            for (var index = 0; index < canonical.Length; index++)
            {
                var target = canonical[index].State;
                if (accumulatedDamage.TryGetValue(target.Entity, out var damage))
                {
                    target.ApplyDamage(damage);
                }
            }

            ClearInvalidTargets(canonical, byEntity);

            return new CombatTickResult(
                events.ToArray(),
                DetermineOutcome(canonical));
        }

        private static void ClearInvalidTargets(
            CombatantTickInput[] canonical,
            Dictionary<EntityId, CombatantTickInput> byEntity)
        {
            for (var index = 0; index < canonical.Length; index++)
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

        private static BattleOutcome DetermineOutcome(CombatantTickInput[] canonical)
        {
            var hasLivingOwner = false;
            var soleOwner = default(PlayerId);

            for (var index = 0; index < canonical.Length; index++)
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

        private static int CompareCombatants(
            CombatantTickInput left,
            CombatantTickInput right) =>
            left.State.Entity.Value.CompareTo(right.State.Entity.Value);
    }
}
