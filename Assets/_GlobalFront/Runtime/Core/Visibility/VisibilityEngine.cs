using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;

namespace GlobalFront.Core.Visibility
{
    /// <summary>
    /// Engine-free view of one unit consumed by
    /// <see cref="VisibilityEngine"/>. Mirrors the fields of
    /// <c>GlobalFront.Server.ServerUnitSnapshot</c> that visibility needs; the
    /// engine lives in <c>GlobalFront.Core</c>, which cannot reference the
    /// server assembly.
    /// </summary>
    public readonly struct VisibilityUnitSnapshot
    {
        public VisibilityUnitSnapshot(EntityId entity, PlayerId owner, WorldPointMm position)
        {
            Entity = entity;
            Owner = owner;
            Position = position;
        }

        public EntityId Entity { get; }

        public PlayerId Owner { get; }

        public WorldPointMm Position { get; }
    }

    /// <summary>
    /// Server-side visibility math (ADR-014, Step 4.3.1). Answers, for every
    /// player, which of the at most 1024 live units that player can see:
    ///
    /// 1. Own units are always visible.
    /// 2. Units of players mapped to the same <see cref="TeamId"/> are always
    ///    visible. Without a mapping every other player is an enemy.
    /// 3. An enemy is visible when it lies inside the sight radius of any own
    ///    or allied unit: <c>dx^2 + dz^2 &lt;= sightRadius^2</c>.
    ///
    /// The spatial index is a uniform grid of
    /// <see cref="SimulationConstants.VisibilityGridCellSizeMm"/> cells with
    /// per-unit linked lists. Compact scenes use a dense rectangle indexed
    /// directly; scenes whose bounding rectangle exceeds
    /// <see cref="MaxDenseCells"/> cells fall back to preallocated
    /// open-addressed buckets. Either way the query scans only cells that can
    /// contain a unit inside the radius: O(N * k) instead of O(N^2).
    ///
    /// Zero-GC: every buffer is allocated in the constructor;
    /// <see cref="CalculateVisibility"/> allocates nothing on the hot path.
    /// </summary>
    public sealed class VisibilityEngine
    {
        public const int CellSizeMm = SimulationConstants.VisibilityGridCellSizeMm;

        public const int MaxTrackedUnits = SimulationConstants.MaxUnitsPerMatch;

        public const int MaxPlayers = SimulationConstants.MaxPlayers;

        /// <summary>Largest dense rectangle, in cells (128 x 128 cells ~= 1.05 km square).</summary>
        public const int MaxDenseCells = 16384;

        private const int HashBits = 12;
        private const int HashCapacity = 1 << HashBits;
        private const int HashMask = HashCapacity - 1;
        private const int EmptyBucket = -2;
        private const int NoUnit = -1;
        private const byte UnassignedTeam = byte.MaxValue;
        private const int OwnerLookupWidth = 256;

        /// <summary>
        /// Per-unit grid node. Next and Stamp share one 8-byte slot so the
        /// common "already visible" chain step reads a single array element.
        /// </summary>
        private struct CellNode
        {
            public int Next;
            public int Stamp;
        }

        private readonly CellNode[] _nodes = new CellNode[MaxTrackedUnits];
        private readonly long[] _unitPackedPosition = new long[MaxTrackedUnits];
        private readonly int[] _denseHeads = new int[MaxDenseCells];
        private readonly int[] _denseCounts = new int[MaxDenseCells];
        private readonly int[] _denseVisibleStamp = new int[MaxDenseCells];
        private readonly long[] _bucketKeys = new long[HashCapacity];
        private readonly int[] _bucketHeads = new int[HashCapacity];
        private readonly int[] _bucketCounts = new int[HashCapacity];
        private readonly int[] _bucketVisibleStamp = new int[HashCapacity];
        private readonly int[] _usedBuckets = new int[HashCapacity];
        private readonly int[] _unitBitIndex = new int[MaxTrackedUnits];
        private readonly int[] _unitCellX = new int[MaxTrackedUnits];
        private readonly int[] _unitCellZ = new int[MaxTrackedUnits];
        private readonly byte[] _unitOwner = new byte[MaxTrackedUnits];
        private readonly bool[] _friendlyOwner = new bool[(MaxPlayers + 1) * OwnerLookupWidth];
        private readonly bool[] _playerHasFriendly = new bool[MaxPlayers + 1];
        private readonly byte[] _teamByPlayer = new byte[MaxPlayers + 1];

        private bool _denseGridActive;
        private int _denseMinX;
        private int _denseMinZ;
        private int _denseWidth;
        private int _denseHeight;
        private int _usedBucketCount;
        private int _occupiedCellCount;

        public VisibilityEngine()
        {
            for (var index = 0; index < HashCapacity; index++)
            {
                _bucketHeads[index] = EmptyBucket;
            }

            for (var index = 0; index < _teamByPlayer.Length; index++)
            {
                _teamByPlayer[index] = UnassignedTeam;
            }
        }

        /// <summary>
        /// Marks a player as a member of a team. Players that are never mapped
        /// are enemies of everyone except themselves.
        /// </summary>
        public void SetPlayerTeam(PlayerId player, TeamId team)
        {
            if (!player.IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(player));
            }

            if (!team.IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(team));
            }

            _teamByPlayer[player.Value] = team.Value;
        }

        /// <summary>Drops the team mapping: every other player becomes an enemy.</summary>
        public void ClearPlayerTeams()
        {
            for (var index = 0; index < _teamByPlayer.Length; index++)
            {
                _teamByPlayer[index] = UnassignedTeam;
            }
        }

        /// <summary>
        /// Recomputes every player's visibility mask.
        /// </summary>
        /// <param name="units">
        /// Live units. <see cref="VisibilityUnitSnapshot.Entity"/> must address
        /// the mask range 1..1024; the mask bit is <c>Value - 1</c>.
        /// </param>
        /// <param name="unitCount">Number of valid entries in <paramref name="units"/>.</param>
        /// <param name="playerVisibilityOut">
        /// Destination masks indexed by <see cref="PlayerId.Value"/>; index 0 is
        /// unused and is cleared. Must hold at least
        /// <see cref="SimulationConstants.MaxPlayers"/> + 1 entries.
        /// </param>
        /// <param name="sightRadiusMm">Sight radius applied to every friendly unit.</param>
        public void CalculateVisibility(
            ReadOnlySpan<VisibilityUnitSnapshot> units,
            int unitCount,
            Span<BitMask1024> playerVisibilityOut,
            int sightRadiusMm = SimulationConstants.DefaultSightRadiusMm)
        {
            if (unitCount < 0 || unitCount > units.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(unitCount), unitCount, "Unit count must fit the input span.");
            }

            if (unitCount > MaxTrackedUnits)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(unitCount), unitCount, "A match holds at most 1024 visible units.");
            }

            if (playerVisibilityOut.Length < MaxPlayers + 1)
            {
                throw new ArgumentException(
                    "Visibility output must hold MaxPlayers + 1 masks indexed by PlayerId.Value.",
                    nameof(playerVisibilityOut));
            }

            if (sightRadiusMm < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sightRadiusMm));
            }

            ResetGrid();

            for (var unitIndex = 0; unitIndex < unitCount; unitIndex++)
            {
                _nodes[unitIndex].Stamp = 0;
            }

            for (var playerValue = 0; playerValue <= MaxPlayers; playerValue++)
            {
                playerVisibilityOut[playerValue].ClearAll();
                _playerHasFriendly[playerValue] = false;
            }

            BuildFriendlyOwnerLookup();

            var minCellX = int.MaxValue;
            var minCellZ = int.MaxValue;
            var maxCellX = int.MinValue;
            var maxCellZ = int.MinValue;

            for (var unitIndex = 0; unitIndex < unitCount; unitIndex++)
            {
                var unit = units[unitIndex];
                var position = unit.Position;
                var cellX = FloorDiv(position.X, CellSizeMm);
                var cellZ = FloorDiv(position.Z, CellSizeMm);

                _unitPackedPosition[unitIndex] = ((long)position.X << 32) | (uint)position.Z;
                _unitCellX[unitIndex] = cellX;
                _unitCellZ[unitIndex] = cellZ;
                _unitOwner[unitIndex] = unit.Owner.Value;
                _unitBitIndex[unitIndex] = ResolveBitIndex(unit.Entity);

                if (cellX < minCellX)
                {
                    minCellX = cellX;
                }

                if (cellX > maxCellX)
                {
                    maxCellX = cellX;
                }

                if (cellZ < minCellZ)
                {
                    minCellZ = cellZ;
                }

                if (cellZ > maxCellZ)
                {
                    maxCellZ = cellZ;
                }
            }

            ActivateGrid(minCellX, minCellZ, maxCellX, maxCellZ, unitCount);

            for (var unitIndex = 0; unitIndex < unitCount; unitIndex++)
            {
                Insert(_unitCellX[unitIndex], _unitCellZ[unitIndex], unitIndex);
            }

            // Rules 1 and 2: own and allied units never depend on distance.
            for (var playerValue = 1; playerValue <= MaxPlayers; playerValue++)
            {
                var lookupBase = playerValue * OwnerLookupWidth;
                ref var mask = ref playerVisibilityOut[playerValue];
                var hasFriendly = false;
                for (var unitIndex = 0; unitIndex < unitCount; unitIndex++)
                {
                    if (!_friendlyOwner[lookupBase + _unitOwner[unitIndex]])
                    {
                        continue;
                    }

                    mask.Set(_unitBitIndex[unitIndex]);
                    _nodes[unitIndex].Stamp = playerValue;
                    hasFriendly = true;
                }

                _playerHasFriendly[playerValue] = hasFriendly;
            }

            var cellRange = (int)(((long)sightRadiusMm + CellSizeMm - 1) / CellSizeMm);
            var radiusSquared = (long)sightRadiusMm * sightRadiusMm;
            var neighborOffsetCount = (2L * cellRange + 1) * (2L * cellRange + 1);

            // Rule 3: every friendly unit opens the cells around it.
            for (var playerValue = 1; playerValue <= MaxPlayers; playerValue++)
            {
                if (!_playerHasFriendly[playerValue])
                {
                    continue;
                }

                var lookupBase = playerValue * OwnerLookupWidth;
                ref var mask = ref playerVisibilityOut[playerValue];

                for (var unitIndex = 0; unitIndex < unitCount; unitIndex++)
                {
                    if (!_friendlyOwner[lookupBase + _unitOwner[unitIndex]])
                    {
                        continue;
                    }

                    var packed = _unitPackedPosition[unitIndex];
                    var originX = (int)(packed >> 32);
                    var originZ = (int)packed;

                    if (neighborOffsetCount <= _occupiedCellCount)
                    {
                        if (_denseGridActive)
                        {
                            ScanDenseNeighbourhood(
                                ref mask, _unitCellX[unitIndex] - _denseMinX,
                                _unitCellZ[unitIndex] - _denseMinZ, originX, originZ,
                                sightRadiusMm, radiusSquared, playerValue, cellRange);
                        }
                        else
                        {
                            ScanHashedNeighbourhood(
                                ref mask, _unitCellX[unitIndex], _unitCellZ[unitIndex],
                                originX, originZ, sightRadiusMm, radiusSquared, playerValue,
                                cellRange);
                        }
                    }
                    else
                    {
                        // A large radius touches more cells than the world
                        // occupies: walking the units is cheaper and the
                        // per-unit distance test still rejects misses.
                        for (var target = 0; target < unitCount; target++)
                        {
                            if (_nodes[target].Stamp == playerValue)
                            {
                                continue;
                            }

                            if (TryMarkTarget(
                                ref mask, target, originX, originZ, sightRadiusMm, radiusSquared))
                            {
                                _nodes[target].Stamp = playerValue;
                            }
                        }
                    }
                }
            }
        }

        private void ScanDenseNeighbourhood(
            ref BitMask1024 mask,
            int baseX,
            int baseZ,
            int originX,
            int originZ,
            int sightRadiusMm,
            long radiusSquared,
            int viewerValue,
            int cellRange)
        {
            var heads = _denseHeads;
            var counts = _denseCounts;
            var visibleStamps = _denseVisibleStamp;
            var width = _denseWidth;
            var height = _denseHeight;
            for (var offsetX = -cellRange; offsetX <= cellRange; offsetX++)
            {
                var localX = baseX + offsetX;
                if ((uint)localX >= (uint)width)
                {
                    continue;
                }

                for (var offsetZ = -cellRange; offsetZ <= cellRange; offsetZ++)
                {
                    var localZ = baseZ + offsetZ;
                    if ((uint)localZ >= (uint)height)
                    {
                        continue;
                    }

                    var cell = (localZ * width) + localX;
                    var head = heads[cell];
                    if (head == NoUnit || visibleStamps[cell] == viewerValue)
                    {
                        continue;
                    }

                    if (ScanChain(ref mask, head, counts[cell], originX, originZ, sightRadiusMm,
                        radiusSquared, viewerValue))
                    {
                        visibleStamps[cell] = viewerValue;
                    }
                }
            }
        }

        private void ScanHashedNeighbourhood(
            ref BitMask1024 mask,
            int cellX,
            int cellZ,
            int originX,
            int originZ,
            int sightRadiusMm,
            long radiusSquared,
            int viewerValue,
            int cellRange)
        {
            var counts = _bucketCounts;
            var visibleStamps = _bucketVisibleStamp;
            var heads = _bucketHeads;
            for (var offsetX = -cellRange; offsetX <= cellRange; offsetX++)
            {
                for (var offsetZ = -cellRange; offsetZ <= cellRange; offsetZ++)
                {
                    var bucket = FindBucket(cellX + offsetX, cellZ + offsetZ);
                    if (bucket < 0 || visibleStamps[bucket] == viewerValue)
                    {
                        continue;
                    }

                    if (ScanChain(ref mask, heads[bucket], counts[bucket], originX, originZ,
                        sightRadiusMm, radiusSquared, viewerValue))
                    {
                        visibleStamps[bucket] = viewerValue;
                    }
                }
            }
        }

        private bool ScanChain(
            ref BitMask1024 mask,
            int head,
            int memberCount,
            int originX,
            int originZ,
            int sightRadiusMm,
            long radiusSquared,
            int viewerValue)
        {
            var visible = 0;
            var unitIndex = head;
            while (unitIndex != NoUnit)
            {
                ref var node = ref _nodes[unitIndex];
                var next = node.Next;
                if (node.Stamp == viewerValue)
                {
                    visible++;
                }
                else if (TryMarkTarget(
                    ref mask, unitIndex, originX, originZ, sightRadiusMm, radiusSquared))
                {
                    node.Stamp = viewerValue;
                    visible++;
                }

                unitIndex = next;
            }

            return visible == memberCount;
        }

        private bool TryMarkTarget(
            ref BitMask1024 mask,
            int unitIndex,
            int originX,
            int originZ,
            int sightRadiusMm,
            long radiusSquared)
        {
            var packed = _unitPackedPosition[unitIndex];
            var dx = (long)(int)(packed >> 32) - originX;
            if (dx > sightRadiusMm || dx < -sightRadiusMm)
            {
                return false;
            }

            var dz = (long)(int)packed - originZ;
            if (dz > sightRadiusMm || dz < -sightRadiusMm)
            {
                return false;
            }

            if ((dx * dx) + (dz * dz) > radiusSquared)
            {
                return false;
            }

            mask.Set(_unitBitIndex[unitIndex]);
            return true;
        }

        private void ActivateGrid(int minCellX, int minCellZ, int maxCellX, int maxCellZ, int unitCount)
        {
            var width = unitCount == 0 ? 0 : maxCellX - minCellX + 1;
            var height = unitCount == 0 ? 0 : maxCellZ - minCellZ + 1;
            if (unitCount > 0 && (long)width * height <= MaxDenseCells)
            {
                _denseGridActive = true;
                _denseMinX = minCellX;
                _denseMinZ = minCellZ;
                _denseWidth = width;
                _denseHeight = height;
                var cellCount = width * height;
                for (var index = 0; index < cellCount; index++)
                {
                    _denseHeads[index] = NoUnit;
                    _denseCounts[index] = 0;
                    _denseVisibleStamp[index] = 0;
                }

                return;
            }

            _denseGridActive = false;
        }

        private void Insert(int cellX, int cellZ, int unitIndex)
        {
            if (_denseGridActive)
            {
                var index = ((cellZ - _denseMinZ) * _denseWidth) + (cellX - _denseMinX);
                var denseHead = _denseHeads[index];
                if (denseHead == NoUnit)
                {
                    _occupiedCellCount++;
                }

                _nodes[unitIndex].Next = denseHead;
                _denseHeads[index] = unitIndex;
                _denseCounts[index]++;
                return;
            }

            var key = PackCell(cellX, cellZ);
            var bucket = HashCell(cellX, cellZ);

            while (_bucketHeads[bucket] != EmptyBucket && _bucketKeys[bucket] != key)
            {
                bucket = (bucket + 1) & HashMask;
            }

            if (_bucketHeads[bucket] == EmptyBucket)
            {
                _bucketKeys[bucket] = key;
                _bucketHeads[bucket] = NoUnit;
                _bucketCounts[bucket] = 0;
                _bucketVisibleStamp[bucket] = 0;
                _usedBuckets[_usedBucketCount++] = bucket;
                _occupiedCellCount++;
            }

            _nodes[unitIndex].Next = _bucketHeads[bucket];
            _bucketHeads[bucket] = unitIndex;
            _bucketCounts[bucket]++;
        }

        private int FindBucket(int cellX, int cellZ)
        {
            var key = PackCell(cellX, cellZ);
            var bucket = HashCell(cellX, cellZ);

            while (_bucketHeads[bucket] != EmptyBucket)
            {
                if (_bucketKeys[bucket] == key)
                {
                    return bucket;
                }

                bucket = (bucket + 1) & HashMask;
            }

            return NoUnit;
        }

        private void ResetGrid()
        {
            for (var index = 0; index < _usedBucketCount; index++)
            {
                var bucket = _usedBuckets[index];
                _bucketHeads[bucket] = EmptyBucket;
                _bucketCounts[bucket] = 0;
                _bucketVisibleStamp[bucket] = 0;
            }

            _usedBucketCount = 0;
            _occupiedCellCount = 0;
            _denseGridActive = false;
        }

        private void BuildFriendlyOwnerLookup()
        {
            for (var playerValue = 1; playerValue <= MaxPlayers; playerValue++)
            {
                var lookupBase = playerValue * OwnerLookupWidth;
                var viewerTeam = _teamByPlayer[playerValue];
                for (var ownerValue = 0; ownerValue < OwnerLookupWidth; ownerValue++)
                {
                    var friendly = ownerValue == playerValue;
                    if (!friendly && ownerValue >= 1 && ownerValue <= MaxPlayers)
                    {
                        var ownerTeam = _teamByPlayer[ownerValue];
                        friendly = ownerTeam != UnassignedTeam && ownerTeam == viewerTeam;
                    }

                    _friendlyOwner[lookupBase + ownerValue] = friendly;
                }
            }
        }

        private static int ResolveBitIndex(EntityId entity)
        {
            if (entity.Value == 0 || entity.Value > MaxTrackedUnits)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(entity), entity,
                    "Visibility masks address entity ids 1..1024 (bit index = EntityId.Value - 1).");
            }

            return (int)entity.Value - 1;
        }

        private static long PackCell(int cellX, int cellZ) =>
            ((long)cellX << 32) | (uint)cellZ;

        private static int HashCell(int cellX, int cellZ)
        {
            unchecked
            {
                var key = PackCell(cellX, cellZ);
                return (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> (64 - HashBits));
            }
        }

        private static int FloorDiv(int value, int divisor)
        {
            var quotient = value / divisor;
            if (value % divisor != 0 && (value < 0) != (divisor < 0))
            {
                quotient--;
            }

            return quotient;
        }
    }
}
