using System;

namespace GlobalFront.Core.Structures
{
    /// <summary>
    /// Assignment table for construction builders (Dozer / Worker). Pre-allocated,
    /// zero-GC, swap-and-pop removal. Only assigned builders occupy records — an
    /// id absent from the table is <see cref="BuilderActivity.Idle"/>. Records are
    /// immutable snapshots; every transition writes a new record back into the
    /// slot, keeping the table free of in-place struct mutation.
    /// </summary>
    public sealed class BuilderFsm
    {
        public const int MaxBuilders = 128;

        private readonly BuilderRecord[] _records;

        private int _count;

        public BuilderFsm()
        {
            _records = new BuilderRecord[MaxBuilders];
        }

        public int Count => _count;

        /// <summary>
        /// Assigns a builder to a site. The first assigned builder fixes the site's
        /// builder kind; a Dozer site takes exactly one builder, a Worker site up
        /// to <see cref="ConstructionRegistry.MaxGlaWorkers"/>. A builder may hold
        /// only one assignment at a time (reassignment goes through Unassign).
        /// </summary>
        public BuilderAssignmentRejection Assign(ulong builderEntityId, BuilderKind kind, ulong siteEntityId)
        {
            if (builderEntityId == 0)
            {
                return BuilderAssignmentRejection.BuilderUnknown;
            }

            if (siteEntityId == 0)
            {
                return BuilderAssignmentRejection.SiteUnknown;
            }

            for (int i = 0; i < _count; i++)
            {
                if (_records[i].EntityId == builderEntityId)
                {
                    return BuilderAssignmentRejection.AlreadyAssigned;
                }
            }

            int siteBuilderCount = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_records[i].SiteEntityId == siteEntityId)
                {
                    siteBuilderCount++;
                    if (_records[i].Kind != kind)
                    {
                        return BuilderAssignmentRejection.BuilderKindMismatch;
                    }
                }
            }

            if (kind == BuilderKind.Dozer && siteBuilderCount >= 1)
            {
                return BuilderAssignmentRejection.SiteBuilderLimitReached;
            }

            if (kind == BuilderKind.Worker && siteBuilderCount >= ConstructionRegistry.MaxGlaWorkers)
            {
                return BuilderAssignmentRejection.SiteBuilderLimitReached;
            }

            if (_count >= MaxBuilders)
            {
                return BuilderAssignmentRejection.BuilderTableFull;
            }

            _records[_count] = new BuilderRecord(builderEntityId, kind, BuilderActivity.MovingToSite, siteEntityId);
            _count++;
            return BuilderAssignmentRejection.None;
        }

        /// <summary>
        /// Releases a builder (recall, new orders, cancel). The builder leaves the
        /// table and becomes <see cref="BuilderActivity.Idle"/>.
        /// </summary>
        public bool Unassign(ulong builderEntityId)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_records[i].EntityId == builderEntityId)
                {
                    RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Releases every builder assigned to a site (cancel or completion). All of
        /// them become <see cref="BuilderActivity.Idle"/>.
        /// </summary>
        public int UnassignSite(ulong siteEntityId)
        {
            int removed = 0;
            for (int i = _count - 1; i >= 0; i--)
            {
                if (_records[i].SiteEntityId == siteEntityId)
                {
                    RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// Writes a new activity for an assigned builder. Used by the construction
        /// tick to flip MovingToSite/Building on the radius gate.
        /// </summary>
        public bool SetActivity(ulong builderEntityId, BuilderActivity activity)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_records[i].EntityId == builderEntityId)
                {
                    BuilderRecord record = _records[i];
                    _records[i] = new BuilderRecord(record.EntityId, record.Kind, activity, record.SiteEntityId);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Frees the records of builders reported dead this tick. Builders absent
        /// from the input array are treated as not simulated this tick and keep
        /// their records (their sites simply stay frozen).
        /// </summary>
        public void RemoveDead(BuilderTickInput[] builders, int count)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                ulong entityId = _records[i].EntityId;
                for (int j = 0; j < count; j++)
                {
                    if (builders[j].EntityId == entityId && !builders[j].IsAlive)
                    {
                        RemoveAt(i);
                        break;
                    }
                }
            }
        }

        public bool TryGet(ulong builderEntityId, out BuilderRecord record)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_records[i].EntityId == builderEntityId)
                {
                    record = _records[i];
                    return true;
                }
            }

            record = default;
            return false;
        }

        /// <summary>Direct slot access in assignment order (until a removal shuffles the tail).</summary>
        public BuilderRecord GetAt(int index)
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, $"Index must be within [0, {_count}).");
            }

            return _records[index];
        }

        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                _records[i] = default;
            }

            _count = 0;
        }

        private void RemoveAt(int index)
        {
            _count--;
            _records[index] = _records[_count];
            _records[_count] = default;
        }
    }
}
