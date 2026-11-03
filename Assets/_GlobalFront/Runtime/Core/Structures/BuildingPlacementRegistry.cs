using System;
using GlobalFront.Core.Movement;

namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// Authoritative record of one placed building: snapped origin centre plus
    /// the closed AABB in integer millimetres. Min/Max are cached so overlap
    /// checks never recompute geometry.
    /// </summary>
    public readonly struct PlacedBuilding
    {
        public PlacedBuilding(
            ulong entityId,
            WorldPointMm originCenter,
            BuildingFootprint footprint,
            int minX,
            int maxX,
            int minZ,
            int maxZ)
        {
            EntityId = entityId;
            OriginCenter = originCenter;
            Footprint = footprint;
            MinX = minX;
            MaxX = maxX;
            MinZ = minZ;
            MaxZ = maxZ;
        }

        public readonly ulong EntityId;

        public readonly WorldPointMm OriginCenter;

        public readonly BuildingFootprint Footprint;

        public readonly int MinX;

        public readonly int MaxX;

        public readonly int MinZ;

        public readonly int MaxZ;
    }

    /// <summary>
    /// Pre-allocated registry of placed building AABBs. Zero-GC hot path: fixed
    /// backing array, for-loops only, O(1) swap-and-pop removal. Placement is
    /// free-form (anywhere on the map); the snap lattice and the strict AABB
    /// overlap gate are what keep every peer deterministic. Edge touching is
    /// legal — only area intersection rejects.
    /// </summary>
    public sealed class BuildingPlacementRegistry
    {
        public const int MaxPlacedBuildings = 512;

        /// <summary>Default half extent of the OD-27 competitive map (400 x 400 m).</summary>
        public const int DefaultHalfExtentMm = 200_000;

        private readonly PlacedBuilding[] _buildings;

        private readonly int _mapHalfExtentMm;

        private int _count;

        public BuildingPlacementRegistry(int mapHalfExtentMm = DefaultHalfExtentMm)
        {
            if (mapHalfExtentMm <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(mapHalfExtentMm), mapHalfExtentMm, "Map half extent must be strictly positive.");
            }

            _buildings = new PlacedBuilding[MaxPlacedBuildings];
            _mapHalfExtentMm = mapHalfExtentMm;
        }

        public int Count => _count;

        /// <summary>
        /// Computes the closed AABB of a building centred on
        /// <paramref name="center"/>. Pure geometry — no map bounds involved.
        /// </summary>
        public static void CalculateAABB(
            WorldPointMm center,
            BuildingFootprint footprint,
            out int minX,
            out int maxX,
            out int minZ,
            out int maxZ)
        {
            int halfWidthMm = footprint.WidthMm / 2;
            int halfLengthMm = footprint.LengthMm / 2;
            minX = center.X - halfWidthMm;
            maxX = center.X + halfWidthMm;
            minZ = center.Z - halfLengthMm;
            maxZ = center.Z + halfLengthMm;
        }

        /// <summary>True when the whole AABB fits inside the configured map extent.</summary>
        public bool IsInsideMapBounds(WorldPointMm center, BuildingFootprint footprint)
        {
            CalculateAABB(center, footprint, out int minX, out int maxX, out int minZ, out int maxZ);
            return minX >= -_mapHalfExtentMm && maxX <= _mapHalfExtentMm
                && minZ >= -_mapHalfExtentMm && maxZ <= _mapHalfExtentMm;
        }

        /// <summary>
        /// Validates a placement without mutating the registry. Checks map bounds,
        /// then strict AABB overlap against every registered building except
        /// <paramref name="ignoreEntityId"/> (0 disables the exemption; used to
        /// re-validate a building against its own record when moving).
        /// </summary>
        public PlacementRejection ValidatePlacement(
            WorldPointMm center,
            BuildingFootprint footprint,
            ulong ignoreEntityId = 0)
        {
            if (!IsInsideMapBounds(center, footprint))
            {
                return PlacementRejection.OutOfBounds;
            }

            CalculateAABB(center, footprint, out int minX, out int maxX, out int minZ, out int maxZ);
            for (int i = 0; i < _count; i++)
            {
                if (_buildings[i].EntityId == ignoreEntityId)
                {
                    continue;
                }

                if (minX < _buildings[i].MaxX && maxX > _buildings[i].MinX
                    && minZ < _buildings[i].MaxZ && maxZ > _buildings[i].MinZ)
                {
                    return PlacementRejection.OverlapsExistingBuilding;
                }
            }

            return PlacementRejection.None;
        }

        /// <summary>
        /// Validates and stores a placement. Rejection order: invalid/duplicate
        /// entity id (<see cref="PlacementRejection.InvalidEntityId"/>), geometry
        /// (bounds, overlap), then capacity (<see cref="PlacementRejection.RegistryFull"/>).
        /// On success <paramref name="rejection"/> is <see cref="PlacementRejection.None"/>;
        /// a false return never carries None.
        /// </summary>
        public bool TryRegister(
            ulong entityId,
            WorldPointMm center,
            BuildingFootprint footprint,
            out PlacementRejection rejection)
        {
            if (entityId == 0)
            {
                rejection = PlacementRejection.InvalidEntityId;
                return false;
            }

            for (int i = 0; i < _count; i++)
            {
                if (_buildings[i].EntityId == entityId)
                {
                    rejection = PlacementRejection.InvalidEntityId;
                    return false;
                }
            }

            rejection = ValidatePlacement(center, footprint);
            if (rejection != PlacementRejection.None)
            {
                return false;
            }

            if (_count >= MaxPlacedBuildings)
            {
                rejection = PlacementRejection.RegistryFull;
                return false;
            }

            CalculateAABB(center, footprint, out int minX, out int maxX, out int minZ, out int maxZ);
            _buildings[_count] = new PlacedBuilding(entityId, center, footprint, minX, maxX, minZ, maxZ);
            _count++;
            return true;
        }

        /// <summary>
        /// Removes a building by id with an O(1) swap-and-pop (the last record
        /// moves into the freed slot). Returns false when the id is not registered.
        /// </summary>
        public bool Unregister(ulong entityId)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_buildings[i].EntityId == entityId)
                {
                    _count--;
                    _buildings[i] = _buildings[_count];
                    _buildings[_count] = default;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Looks a building up by id; a miss yields the default record.</summary>
        public bool TryGet(ulong entityId, out PlacedBuilding building)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_buildings[i].EntityId == entityId)
                {
                    building = _buildings[i];
                    return true;
                }
            }

            building = default;
            return false;
        }

        /// <summary>
        /// Direct slot access in insertion order (until a removal shuffles the
        /// tail). Index must be within [0, <see cref="Count"/>).
        /// </summary>
        public PlacedBuilding GetAt(int index)
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, $"Index must be within [0, {_count}).");
            }

            return _buildings[index];
        }

        /// <summary>Drops every record and resets the counter.</summary>
        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                _buildings[i] = default;
            }

            _count = 0;
        }
    }
}
