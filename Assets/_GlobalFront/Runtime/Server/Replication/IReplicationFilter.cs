using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Visibility;

namespace GlobalFront.Server.Replication
{
    /// <summary>
    /// Per-client view filter of the server replication stream (ADR-014,
    /// OD-39). The emitter asks the filter which units a receiver is allowed
    /// to know at the base and target tick; records that concern an entity
    /// outside that set never reach the wire.
    ///
    /// A filter must be allocation-free on the hot path: <see cref="UpdateTick"/>
    /// runs once per authoritative tick, and the query methods once per client
    /// per tick.
    /// </summary>
    public interface IReplicationFilter
    {
        /// <summary>
        /// False keeps the emitter on the global, unfiltered stream. The flag
        /// is read on every tick, so it may be toggled between matches.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Rebuilds the filter state for one authoritative tick. The span is
        /// the same captured world the diff engine sees, in ascending entity
        /// id order.
        /// </summary>
        void UpdateTick(ulong tick, ReadOnlySpan<ServerUnitSnapshot> world, int unitCount);

        /// <summary>
        /// Visibility set of one player at one retained tick. False when the
        /// tick is no longer retained (or was never recorded): a receiver
        /// based on that tick can only be restored by a keyframe.
        /// </summary>
        bool TryGetVisibilityMask(ulong tick, PlayerId player, out BitMask1024 mask);

        /// <summary>
        /// True when <paramref name="entity"/> is part of the player's known
        /// world at <paramref name="tick"/>.
        /// </summary>
        bool IsVisible(ulong tick, PlayerId player, EntityId entity);
    }
}
