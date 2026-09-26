using System;
using GlobalFront.Core.Model;

namespace GlobalFront.Client.Catalog
{
    /// <summary>
    /// One row of the client's presentation reference for a replicated
    /// <see cref="UnitKinds"/> archetype: what a unit of that kind is called, how
    /// much health the server's stats give it, and how wide its selection ring is.
    ///
    /// <see cref="InvMaxHealth"/> is precomputed because the health bar denominator
    /// is read once per unit per frame: dividing there would put a float division in
    /// the hot path, and a zero or unresolved maximum would surface as the
    /// <c>0/0 = NaN</c> that this table exists to prevent.
    /// </summary>
    public readonly struct UnitDefinition
    {
        public UnitDefinition(
            byte kind,
            string displayName,
            int maximumHealth,
            int radiusMillimetres)
        {
            Kind = kind;
            DisplayName = displayName;
            MaximumHealth = maximumHealth;
            RadiusMillimetres = radiusMillimetres;
            InvMaxHealth = maximumHealth > 0 ? 1f / maximumHealth : 0f;
        }

        /// <summary>Wire archetype id, matching <see cref="UnitKinds"/>.</summary>
        public byte Kind { get; }

        public string DisplayName { get; }

        /// <summary>Hit points a fresh unit of this kind has on the server.</summary>
        public int MaximumHealth { get; }

        /// <summary>Selection ring radius in millimetres.</summary>
        public int RadiusMillimetres { get; }

        /// <summary><c>1 / MaximumHealth</c>, or 0 when the maximum is unresolved.</summary>
        public float InvMaxHealth { get; }

        /// <summary>True when the row can drive presentation: a real kind with usable geometry and stats.</summary>
        public bool IsResolved =>
            UnitKinds.IsDefined(Kind) && MaximumHealth > 0 && RadiusMillimetres > 0;
    }

    /// <summary>
    /// Lookup the client performs for every replicated archetype. Kept as an
    /// interface so a data-driven roster (faction tables, Addressables, or a
    /// server-published digest of the same table) can replace the built-in
    /// prototype rows without touching the presentation layer.
    /// </summary>
    public interface IUnitCatalog
    {
        /// <summary>
        /// False for <see cref="UnitKinds.Unknown"/> and for any kind the client has
        /// no row for; the caller then keeps its no-stat-resolved fallback rather
        /// than guessing an archetype.
        /// </summary>
        bool TryGet(byte kind, out UnitDefinition definition);
    }

    /// <summary>
    /// Prototype archetype table (OD-29). Backed by an array indexed by the kind
    /// byte, so <see cref="TryGet"/> is a bounds check and one struct copy: O(1) and
    /// allocation-free, which matters because the presentation layer asks per unit
    /// whenever a slot is (re)initialised.
    ///
    /// The health values deliberately mirror what the authoritative side currently
    /// applies — <c>MatchConfig.UnitStats</c> is still a single
    /// <see cref="GlobalFront.Core.Combat.CombatStats"/> shared by every unit, and
    /// per-archetype stats are an owner decision (roster/balance are not invented
    /// here). A row therefore states the shared maximum rather than a made-up one:
    /// inventing "Tank = 300" here would draw a full-health tank at one third. The
    /// radii follow the prototype's own 1 m primitive footprint and are the values
    /// the selection ring uses until real silhouettes exist.
    /// </summary>
    public sealed class UnitCatalog : IUnitCatalog
    {
        private readonly UnitDefinition[] _byKind;

        /// <summary>
        /// Builds a table from hand-authored or data-driven rows. Rejects a row for
        /// <see cref="UnitKinds.Unknown"/> (unknown is a lookup miss, not a kind),
        /// duplicate kinds, kinds outside the protocol's range, and rows that are not
        /// usable for presentation, so a mistake surfaces at load instead of silently
        /// re-skinned units later.
        ///
        /// Rejecting unresolved rows is what makes <see cref="TryGet"/> mean
        /// something: a row that had already been stored with a zero denominator
        /// would read back as "this archetype has zero hit points" everywhere the
        /// caller trusts the lookup, which is the exact <c>0/0</c> and empty-bar shape
        /// this table exists to prevent. A roster still in progress leaves the kind
        /// out of the array instead of shipping a placeholder row for it.
        /// </summary>
        public UnitCatalog(UnitDefinition[] definitions)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            _byKind = new UnitDefinition[UnitKinds.Count];

            for (var index = 0; index < definitions.Length; index++)
            {
                var definition = definitions[index];
                if (!UnitKinds.IsDefined(definition.Kind))
                {
                    throw new ArgumentException(
                        $"Unit kind {definition.Kind} is not a defined archetype.",
                        nameof(definitions));
                }

                if (!definition.IsResolved)
                {
                    throw new ArgumentException(
                        $"Unit kind {definition.Kind} is not resolved: MaximumHealth and RadiusMillimetres must both be positive (got {definition.MaximumHealth} and {definition.RadiusMillimetres}).",
                        nameof(definitions));
                }

                if (definition.DisplayName == null)
                {
                    throw new ArgumentException(
                        $"Unit kind {definition.Kind} has no display name.",
                        nameof(definitions));
                }

                if (_byKind[definition.Kind].Kind != UnitKinds.Unknown)
                {
                    throw new ArgumentException(
                        $"Duplicate unit kind {definition.Kind} in the catalog.",
                        nameof(definitions));
                }

                _byKind[definition.Kind] = definition;
            }
        }

        /// <summary>Prototype rows shipped with the client build.</summary>
        public static UnitCatalog Default { get; } = new UnitCatalog(new[]
        {
            new UnitDefinition(UnitKinds.Scout, "Scout", PrototypeMaximumHealth, PrototypeRadiusMm),
            new UnitDefinition(UnitKinds.Tank, "Tank", PrototypeMaximumHealth, PrototypeRadiusMm),

            // Structures share the placeholder geometry on purpose: no building
            // exists in the authoritative model yet, so any radius here would be
            // invented. Roster stats and footprints stay owner decisions; per kind
            // rows are what Phase 5 fills in.
            new UnitDefinition(UnitKinds.BaseStructure, "Base structure", PrototypeMaximumHealth, PrototypeRadiusMm),
        });

        /// <summary>
        /// Shared authoritative maximum health today. Equal to the prototype
        /// <c>CombatStats.maximumHealth</c> on purpose; per-archetype stats are
        /// pending an owner decision.
        /// </summary>
        public const int PrototypeMaximumHealth = 100;

        /// <summary>Unit footprint radius, from the prototype's 1 m capsule.</summary>
        public const int PrototypeRadiusMm = 500;

        public bool TryGet(byte kind, out UnitDefinition definition)
        {
            if (!UnitKinds.IsDefined(kind))
            {
                definition = default;
                return false;
            }

            definition = _byKind[kind];
            if (!definition.IsResolved)
            {
                definition = default;
                return false;
            }

            return true;
        }
    }
}
