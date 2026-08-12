using System;
using System.Collections.Generic;
using GlobalFront.Core.Model;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client
{
    /// <summary>
    /// Owns the client-side catalogue of battle units: scene discovery,
    /// deterministic ordering, lookup by entity id and roster counts.
    /// The registry never mutates gameplay state; it only indexes
    /// <see cref="PrototypeUnit"/> instances so that selection, command
    /// application and simulation systems operate on one shared,
    /// deterministically ordered collection.
    /// </summary>
    public sealed class UnitRegistry
    {
        private readonly List<PrototypeUnit> _units = new List<PrototypeUnit>();
        private readonly Dictionary<CoreEntityId, PrototypeUnit> _unitById =
            new Dictionary<CoreEntityId, PrototypeUnit>();

        public int Count => _units.Count;

        public PrototypeUnit this[int index] => _units[index];

        public int FriendlyAlive { get; private set; }

        public int EnemyAlive { get; private set; }

        public bool TryGetUnit(CoreEntityId entity, out PrototypeUnit unit) =>
            _unitById.TryGetValue(entity, out unit);

        /// <summary>
        /// Discovers all <see cref="PrototypeUnit"/> instances in the scene
        /// that have not yet been assigned an authoritative EntityId. Returns
        /// them in a deterministic order (by scene position: Z ascending,
        /// then X ascending) so the controller can build a
        /// <see cref="GlobalFront.Core.Model.MatchConfig"/> and map
        /// server-assigned EntityIds back to presentation units.
        /// </summary>
        public PrototypeUnit[] DiscoverUnassignedUnits()
        {
            var foundUnits = UnityEngine.Object.FindObjectsByType<PrototypeUnit>();
            var unassigned = new System.Collections.Generic.List<PrototypeUnit>();

            for (var index = 0; index < foundUnits.Length; index++)
            {
                var unit = foundUnits[index];
                if (!unit.Entity.IsValid)
                {
                    unassigned.Add(unit);
                }
            }

            unassigned.Sort(CompareUnitsByPosition);
            return unassigned.ToArray();
        }

        /// <summary>
        /// Indexes all <see cref="PrototypeUnit"/> instances in the scene that
        /// have been assigned authoritative EntityIds. Orders them
        /// deterministically by entity id, drops invalid or duplicate entities
        /// and enables enemy auto-acquire for units that do not belong to
        /// <paramref name="localPlayer"/>.
        /// </summary>
        public void Refresh(PlayerId localPlayer)
        {
            _units.Clear();
            _unitById.Clear();

            var foundUnits = UnityEngine.Object.FindObjectsByType<PrototypeUnit>();
            Array.Sort(foundUnits, CompareUnits);

            for (var index = 0; index < foundUnits.Length; index++)
            {
                var unit = foundUnits[index];
                if (!unit.Entity.IsValid || _unitById.ContainsKey(unit.Entity))
                {
                    continue;
                }

                if (unit.Owner != localPlayer)
                {
                    unit.SetAutoAcquireEnemies(true);
                }

                _units.Add(unit);
                _unitById.Add(unit.Entity, unit);
            }
        }

        public void UpdateRosterCounts(PlayerId localPlayer)
        {
            var friendly = 0;
            var enemy = 0;
            for (var index = 0; index < _units.Count; index++)
            {
                var unit = _units[index];
                if (!unit.IsAlive)
                {
                    continue;
                }

                if (unit.Owner == localPlayer)
                {
                    friendly++;
                }
                else
                {
                    enemy++;
                }
            }

            FriendlyAlive = friendly;
            EnemyAlive = enemy;
        }

        /// <summary>
        /// Canonical deterministic ordering used for discovery sorting and
        /// selection sorting: ascending <see cref="EntityId.Value"/>.
        /// </summary>
        public static int CompareUnits(PrototypeUnit left, PrototypeUnit right) =>
            left.Entity.Value.CompareTo(right.Entity.Value);

        /// <summary>
        /// Deterministic ordering for units before server-assigned EntityIds:
        /// Z ascending, then X ascending. This matches the order in which
        /// <see cref="PrototypeWorldBootstrap"/> creates units so the
        /// controller can build a <see cref="GlobalFront.Core.Model.MatchConfig"/>
        /// that produces identical server-side EntityId assignment.
        /// </summary>
        public static int CompareUnitsByPosition(PrototypeUnit left, PrototypeUnit right)
        {
            var leftPos = left.CurrentPosition;
            var rightPos = right.CurrentPosition;
            var zComparison = leftPos.Z.CompareTo(rightPos.Z);
            return zComparison != 0
                ? zComparison
                : leftPos.X.CompareTo(rightPos.X);
        }
    }
}