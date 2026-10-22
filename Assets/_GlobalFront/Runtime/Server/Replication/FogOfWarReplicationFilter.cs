using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Visibility;

namespace GlobalFront.Server.Replication
{
    /// <summary>
    /// Fog-of-war implementation of <see cref="IReplicationFilter"/>
    /// (ADR-014, OD-39): wraps <see cref="VisibilityEngine"/> and
    /// <see cref="VisibilityHistoryRing"/> and exposes the per-tick visibility
    /// sets of every player.
    ///
    /// <see cref="UpdateTick"/> copies the authoritative capture into one
    /// preallocated <see cref="VisibilityUnitSnapshot"/> buffer, runs the
    /// engine into a preallocated mask array and records the masks for the
    /// target tick. Neither the copy nor the query allocates.
    ///
    /// Entity ids outside the mask range 1..1024 cannot be addressed by
    /// <see cref="BitMask1024"/>: they are skipped and counted in
    /// <see cref="IgnoredUnitCount"/> instead of throwing, so a world that
    /// briefly outgrows the mask capacity never takes the replication tick
    /// down. A skipped unit is invisible to every player — the fail-closed
    /// direction for an anti-maphack filter.
    ///
    /// Disabling the filter stops both the visibility computation and mask
    /// recording; the emitter then streams the global world. Re-enabling it
    /// mid-match leaves older mask ticks missing, which simply routes affected
    /// receivers through a fresh keyframe.
    /// </summary>
    public sealed class FogOfWarReplicationFilter : IReplicationFilter
    {
        private readonly VisibilityEngine _engine;
        private readonly VisibilityHistoryRing _history;
        private readonly VisibilityUnitSnapshot[] _units =
            new VisibilityUnitSnapshot[VisibilityEngine.MaxTrackedUnits];
        private readonly BitMask1024[] _masks = new BitMask1024[VisibilityEngine.MaxPlayers + 1];
        private readonly int _sightRadiusMm;

        /// <summary>
        /// Creates a filter. <paramref name="sightRadiusMm"/> must match the
        /// radius the game rules use; the default is the simulation contract.
        /// </summary>
        public FogOfWarReplicationFilter(
            bool enabled = true,
            int sightRadiusMm = SimulationConstants.DefaultSightRadiusMm)
        {
            if (sightRadiusMm < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sightRadiusMm));
            }

            IsEnabled = enabled;
            _sightRadiusMm = sightRadiusMm;
            _engine = new VisibilityEngine();
            _history = new VisibilityHistoryRing();
        }

        /// <inheritdoc />
        public bool IsEnabled { get; set; }

        /// <summary>The underlying visibility math (diagnostics/configuration).</summary>
        public VisibilityEngine Engine => _engine;

        /// <summary>The retained per-tick masks (diagnostics).</summary>
        public VisibilityHistoryRing History => _history;

        /// <summary>Units skipped because their entity id is outside 1..1024.</summary>
        public int IgnoredUnitCount { get; private set; }

        /// <summary>Marks a player as a team member; allies become mutually visible.</summary>
        public void SetPlayerTeam(PlayerId player, TeamId team) => _engine.SetPlayerTeam(player, team);

        /// <summary>Drops every team mapping: only a player's own units are visible.</summary>
        public void ClearPlayerTeams() => _engine.ClearPlayerTeams();

        /// <inheritdoc />
        public void UpdateTick(ulong tick, ReadOnlySpan<ServerUnitSnapshot> world, int unitCount)
        {
            if (!IsEnabled)
            {
                return;
            }

            if (unitCount < 0 || unitCount > world.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(unitCount), unitCount, "Unit count must fit the input span.");
            }

            var count = 0;
            for (var index = 0; index < unitCount; index++)
            {
                var unit = world[index];
                var entityValue = unit.Entity.Value;
                if (entityValue == 0 || entityValue > BitMask1024.BitCount ||
                    count >= _units.Length)
                {
                    IgnoredUnitCount++;
                    continue;
                }

                _units[count++] = new VisibilityUnitSnapshot(
                    unit.Entity, unit.Owner, unit.Position);
            }

            _engine.CalculateVisibility(
                new ReadOnlySpan<VisibilityUnitSnapshot>(_units, 0, count),
                count,
                _masks,
                _sightRadiusMm);
            _history.RecordTick(tick, _masks);
        }

        /// <inheritdoc />
        public bool TryGetVisibilityMask(ulong tick, PlayerId player, out BitMask1024 mask)
        {
            if (!IsEnabled)
            {
                mask = default;
                return false;
            }

            return _history.TryGetVisibility(tick, player, out mask);
        }

        /// <inheritdoc />
        public bool IsVisible(ulong tick, PlayerId player, EntityId entity)
        {
            var entityValue = entity.Value;
            if (entityValue == 0 || entityValue > BitMask1024.BitCount)
            {
                return false;
            }

            return TryGetVisibilityMask(tick, player, out var mask) &&
                   mask.IsSet((int)entityValue - 1);
        }
    }
}
