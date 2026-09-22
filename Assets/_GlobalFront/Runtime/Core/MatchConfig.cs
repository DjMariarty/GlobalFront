using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Movement;

namespace GlobalFront.Core.Model
{
    /// <summary>
    /// Immutable specification for a single unit to be spawned by the
    /// authoritative server during match initialization. Contains only the
    /// data that the server needs to create the initial authoritative state.
    /// </summary>
    public readonly struct UnitSpawnSpec : IEquatable<UnitSpawnSpec>
    {
        public UnitSpawnSpec(
            PlayerId owner,
            WorldPointMm position,
            int speedMmPerTick,
            bool autoAcquireEnemies,
            byte unitKind = UnitKinds.Unknown)
        {
            if (!owner.IsValid)
            {
                throw new ArgumentException("Owner must be valid.", nameof(owner));
            }

            if (!position.IsWithinSimulationBounds)
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            if (speedMmPerTick <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(speedMmPerTick));
            }

            Owner = owner;
            Position = position;
            SpeedMmPerTick = speedMmPerTick;
            AutoAcquireEnemies = autoAcquireEnemies;
            UnitKind = unitKind;
        }

        public PlayerId Owner { get; }

        public WorldPointMm Position { get; }

        public int SpeedMmPerTick { get; }

        public bool AutoAcquireEnemies { get; }

        /// <summary>
        /// OD-29 archetype of the unit to spawn (<see cref="UnitKinds"/>). Part of
        /// the specification identity: two otherwise identical specs that ask for
        /// different archetypes are not the same unit, and a config replayed from a
        /// template must reproduce the same kinds.
        /// </summary>
        public byte UnitKind { get; }

        public bool Equals(UnitSpawnSpec other) =>
            Owner == other.Owner &&
            Position == other.Position &&
            SpeedMmPerTick == other.SpeedMmPerTick &&
            AutoAcquireEnemies == other.AutoAcquireEnemies &&
            UnitKind == other.UnitKind;

        public override bool Equals(object obj) =>
            obj is UnitSpawnSpec other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Owner, Position, SpeedMmPerTick, AutoAcquireEnemies, UnitKind);
    }

    /// <summary>
    /// Immutable match configuration that fully describes the initial
    /// authoritative state of a match. The server creates all units from
    /// this config and assigns EntityIds authoritatively. The client never
    /// determines authoritative EntityIds or combat stats.
    /// </summary>
    public sealed class MatchConfig
    {
        public MatchConfig(CombatStats unitStats, UnitSpawnSpec[] units)
        {
            if (!unitStats.IsValid)
            {
                throw new ArgumentException("Unit stats must be valid.", nameof(unitStats));
            }

            UnitStats = unitStats;
            Units = units ?? throw new ArgumentNullException(nameof(units));
        }

        /// <summary>
        /// Combat stats applied to every unit in the match. In a future
        /// version this may be replaced by per-archetype stats.
        /// </summary>
        public CombatStats UnitStats { get; }

        /// <summary>
        /// Ordered array of unit spawn specifications. The server assigns
        /// EntityIds sequentially in this order, so identical configs
        /// produce identical EntityId assignments.
        /// </summary>
        public UnitSpawnSpec[] Units { get; }

        public int UnitCount => Units.Length;
    }
}