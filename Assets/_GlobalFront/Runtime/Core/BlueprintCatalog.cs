using System;

namespace GlobalFront.Core.Catalog
{
    /// <summary>
    /// Read side of the blueprint registry: O(1), allocation-free row lookup by
    /// typed or raw id, plus an allocation-free id enumeration for tools and
    /// validators. Consumers in Server (production, construction, the
    /// future roster digests) and Client (presentation bridges) program against
    /// this interface, not the concrete table, so a data-driven or
    /// server-published catalog can replace the compiled one without touching
    /// call sites.
    /// </summary>
    public interface IBlueprintCatalog
    {
        /// <summary>Resolves a blueprint row; a miss yields <c>default</c> and false.</summary>
        bool TryGet(BlueprintId id, out BlueprintDefinition definition);

        /// <summary>
        /// Raw-id overload for call sites still holding the protocol's
        /// <c>ushort</c> (production queues, wire codecs).
        /// </summary>
        bool TryGet(ushort id, out BlueprintDefinition definition);

        /// <summary>Number of rows the catalog carries.</summary>
        int Count { get; }

        /// <summary>
        /// Copies every row id into <paramref name="destination"/> in ascending
        /// id order. Zero-GC; throws when the span cannot hold them all. Pair
        /// with <see cref="TryGet(BlueprintId, out BlueprintDefinition)"/> to
        /// walk the whole table without allocations.
        /// </summary>
        int CopyBlueprintIds(Span<ushort> destination);
    }

    /// <summary>
    /// The authoritative blueprint registry (Step 5.5.1): a load-time-validated,
    /// immutable table of <see cref="BlueprintDefinition"/> rows addressed by
    /// <see cref="BlueprintId"/>.
    ///
    /// Validation is fail-fast by design — every structural mistake (a None id,
    /// a duplicate, a building without a footprint, a vehicle that cannot move,
    /// an unpriced or unsighted row) surfaces as an exception while the catalog
    /// is built, not as a silently wrong rule five phases later. A row that had
    /// already been stored broken would read back as truth everywhere the
    /// lookup is trusted, so the constructor is the only gate that can afford
    /// to be loud.
    ///
    /// Lookup is a flat struct array indexed directly by
    /// <see cref="BlueprintId.Value"/> (length = highest id + 1): O(1), zero
    /// allocation, no dictionary and no LINQ — the same discipline the client's
    /// <c>UnitCatalog</c> table set, scaled to the ushort id space. Absent ids
    /// fall outside the array or hit an empty slot whose
    /// <see cref="BlueprintDefinition.Id"/> is still None, and both shapes
    /// return a miss.
    ///
    /// The CombatStats line of every row is validated twice by construction:
    /// the stats struct's own constructor refuses impossible values before this
    /// class ever sees them, and the health re-check below keeps that guarantee
    /// local to the catalog's contract even if the struct ever relaxes.
    /// </summary>
    public sealed class BlueprintCatalog : IBlueprintCatalog
    {
        private readonly BlueprintDefinition[] _byId;
        private readonly int _count;

        /// <summary>
        /// Builds a table from hand-authored or data-driven rows. Rejects a row
        /// for <see cref="BlueprintId.None"/> (a miss is not an id), duplicate
        /// ids, an unusable faction or category, a non-positive cost, build
        /// time or vision, negative power fields, a Building/Defense row
        /// without a construction footprint, and an Infantry/Vehicle/Aircraft
        /// row that cannot move.
        /// </summary>
        public BlueprintCatalog(BlueprintDefinition[] definitions)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            // First pass: per-row structural invariants and the id ceiling the
            // backing array needs. Second pass: duplicate detection against the
            // array itself — an empty slot's Id is None, so a written row is
            // distinguishable from an untouched one without a side table.
            var highestId = 0;
            for (var index = 0; index < definitions.Length; index++)
            {
                BlueprintDefinition definition = definitions[index];
                ValidateRow(definition, nameof(definitions));
                if (definition.Id.Value > highestId)
                {
                    highestId = definition.Id.Value;
                }
            }

            _byId = new BlueprintDefinition[highestId + 1];
            for (var index = 0; index < definitions.Length; index++)
            {
                BlueprintDefinition definition = definitions[index];
                if (_byId[definition.Id.Value].Id.IsValid)
                {
                    throw new ArgumentException(
                        $"Duplicate blueprint id {definition.Id.Value} in the catalog.",
                        nameof(definitions));
                }

                _byId[definition.Id.Value] = definition;
            }

            _count = definitions.Length;
        }

        public bool TryGet(BlueprintId id, out BlueprintDefinition definition)
        {
            if (id.IsValid && id.Value < _byId.Length)
            {
                definition = _byId[id.Value];
                if (definition.Id.IsValid)
                {
                    return true;
                }
            }

            definition = default;
            return false;
        }

        public bool TryGet(ushort id, out BlueprintDefinition definition)
        {
            return TryGet(new BlueprintId(id), out definition);
        }

        public int Count => _count;

        public int CopyBlueprintIds(Span<ushort> destination)
        {
            if (destination.Length < _count)
            {
                throw new ArgumentException(
                    $"Destination span holds {destination.Length} ids, catalog has {_count}.",
                    nameof(destination));
            }

            var written = 0;
            for (var value = 0; value < _byId.Length; value++)
            {
                if (_byId[value].Id.IsValid)
                {
                    destination[written++] = (ushort)value;
                }
            }

            return written;
        }

        private static void ValidateRow(BlueprintDefinition definition, string paramName)
        {
            if (!definition.Id.IsValid)
            {
                throw new ArgumentException(
                    "A blueprint row carries BlueprintId.None; a lookup miss is not an id.",
                    paramName);
            }

            if (definition.Faction != FactionId.None && !FactionId.IsValid(definition.Faction))
            {
                throw new ArgumentException(
                    $"Blueprint {definition.Id.Value} claims faction {definition.Faction}, " +
                    "which is outside [None, FactionId.Count).",
                    paramName);
            }

            if (definition.Category == BlueprintCategory.None)
            {
                throw new ArgumentException(
                    $"Blueprint {definition.Id.Value} has no category.",
                    paramName);
            }

            if (definition.Cost < 0)
            {
                throw new ArgumentException(
                    $"Blueprint {definition.Id.Value} costs {definition.Cost} credits.",
                    paramName);
            }

            if (definition.BuildTimeTicks == 0)
            {
                throw new ArgumentException(
                    $"Blueprint {definition.Id.Value} builds in zero ticks.",
                    paramName);
            }

            if (definition.VisionRangeMm <= 0)
            {
                throw new ArgumentException(
                    $"Blueprint {definition.Id.Value} sees {definition.VisionRangeMm} mm.",
                    paramName);
            }

            if (definition.PowerProduction < 0 || definition.PowerConsumption < 0)
            {
                throw new ArgumentException(
                    $"Blueprint {definition.Id.Value} has a negative power field " +
                    $"(+{definition.PowerProduction}/-{definition.PowerConsumption}); " +
                    "power fields are non-negative magnitudes.",
                    paramName);
            }

            if (definition.Stats.MaximumHealth <= 0)
            {
                throw new ArgumentException(
                    $"Blueprint {definition.Id.Value} has {definition.Stats.MaximumHealth} maximum health.",
                    paramName);
            }

            var isStationaryStructure =
                definition.Category == BlueprintCategory.Building ||
                definition.Category == BlueprintCategory.Defense;
            var isMobileUnit =
                definition.Category == BlueprintCategory.Infantry ||
                definition.Category == BlueprintCategory.Vehicle ||
                definition.Category == BlueprintCategory.Aircraft;

            if (isStationaryStructure &&
                (definition.FootprintWidthCells <= 0 || definition.FootprintHeightCells <= 0))
            {
                throw new ArgumentException(
                    $"Blueprint {definition.Id.Value} is a {definition.Category} with footprint " +
                    $"{definition.FootprintWidthCells}x{definition.FootprintHeightCells}; " +
                    "construction rows need a footprint on the 500 mm grid.",
                    paramName);
            }

            if (isMobileUnit && definition.SpeedMmPerTick <= 0)
            {
                throw new ArgumentException(
                    $"Blueprint {definition.Id.Value} is a {definition.Category} moving at " +
                    $"{definition.SpeedMmPerTick} mm/tick.",
                    paramName);
            }
        }
    }
}
