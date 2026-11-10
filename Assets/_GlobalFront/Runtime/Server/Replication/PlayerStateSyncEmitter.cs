using System;
using GlobalFront.Core.Economy;
using GlobalFront.Core.Model;
using GlobalFront.Server.Transport;

namespace GlobalFront.Server.Replication
{
    /// <summary>
    /// M2 remediation (P1-03): periodically pushes every attached session its
    /// OWN private economic digest (credits, harvested total, power grid,
    /// production queue heads) through
    /// <see cref="TransportMessageType.PlayerStateSync"/> (opcode 15). The
    /// payload is read from the authoritative ledgers
    /// (<see cref="MatchServer.Economy"/>, <see cref="MatchServer.PowerLedger"/>,
    /// <see cref="MatchServer.Production"/>) and delivered strictly to the
    /// owning session — <see cref="ServerTransportHost.SendPlayerStateSync"/>
    /// resolves the session's bound connection, so a rival client can never
    /// observe another player's balance (anti-maphack, same ownership rule as
    /// the fog-of-war replication filter).
    /// </summary>
    /// <remarks>
    /// Zero-GC: every buffer is preallocated once and reused; the per-tick
    /// path allocates nothing. Cadence is <see cref="PeriodTicks"/> (1 Hz at
    /// the 20 Hz tick) — the digest drives HUD display only, not simulation.
    /// </remarks>
    public sealed class PlayerStateSyncEmitter
    {
        /// <summary>Sync period in ticks: 20 ticks = 1 s at the 20 Hz tick.</summary>
        public const int PeriodTicks = 20;

        /// <summary>
        /// Digest cap sized to the datagram budget: header 28 B + 80 entries
        /// of 13 B + the 13 B envelope header = 1081 B, inside
        /// <see cref="TransportProtocol.MaxDatagramBytes"/> (1200). The codec
        /// itself allows 128 entries; a lobby saturating that cap would
        /// fragment, so the emitter clamps instead.
        /// </summary>
        public const int MaxDigestEntries = 80;

        private readonly MatchServer _server;
        private readonly ServerTransportHost _transport;
        private readonly PlayerQueueDigestEntry[] _digest =
            new PlayerQueueDigestEntry[MaxDigestEntries];
        private readonly byte[] _encodeBuffer =
            new byte[PlayerStateSyncCodec.HeaderSizeBytes +
                MaxDigestEntries * PlayerStateSyncCodec.EntrySizeBytes];

        public PlayerStateSyncEmitter(MatchServer server, ServerTransportHost transport)
        {
            _server = server ?? throw new System.ArgumentNullException(nameof(server));
            _transport = transport ?? throw new System.ArgumentNullException(nameof(transport));
        }

        /// <summary>
        /// Per-tick hook (wired to <see cref="ServerHost.TickCompleted"/>):
        /// every <see cref="PeriodTicks"/> ticks pushes one owner-only digest
        /// to each bound session. Sessions without a match binding (handshake
        /// in progress, terminated) are skipped.
        /// </summary>
        public void OnTickCompleted(ulong tick)
        {
            if (tick % PeriodTicks != 0)
            {
                return;
            }

            var targets = _transport.SnapshotTargets;
            for (var index = 0; index < targets.Count; index++)
            {
                if (_transport.TryGetPlayerForSession(targets[index], out var player))
                {
                    Emit(targets[index], tick, player);
                }
            }
        }

        private void Emit(SessionId session, ulong tick, PlayerId player)
        {
            var economy = _server.Economy.GetPlayerState(player);
            var power = _server.PowerLedger.GetPowerState(player);
            var digestCount = BuildQueueDigest(player);

            var payload = new PlayerStateSyncPayload(
                PlayerStateSyncCodec.Version,
                tick,
                player,
                economy.Credits,
                economy.TotalHarvestedCredits,
                power.Produced,
                power.Consumed,
                power.IsLowPower);

            var length = PlayerStateSyncCodec.Encode(
                _encodeBuffer, payload, _digest.AsSpan(0, digestCount));

            _transport.SendPlayerStateSync(session, _encodeBuffer, length);
        }

        /// <summary>
        /// One digest entry per registered (completed) production building of
        /// <paramref name="player"/>, capped at <see cref="MaxDigestEntries"/>.
        /// Queue slot 0 is the head: its blueprint and remaining ticks ride
        /// the entry; an empty queue encodes zeros.
        /// </summary>
        private int BuildQueueDigest(PlayerId player)
        {
            var production = _server.Production;
            var count = 0;
            for (var index = 0; index < production.Count && count < MaxDigestEntries; index++)
            {
                var building = production.GetAt(index);
                if (building.Owner != player)
                {
                    continue;
                }

                ushort headBlueprintId = 0;
                ushort headRemainingTicks = 0;
                if (building.QueueCount > 0 &&
                    production.TryGetQueueItem(building.BuildingEntityId, 0, out var head))
                {
                    headBlueprintId = head.BlueprintId;
                    headRemainingTicks = head.RemainingTicks;
                }

                _digest[count++] = new PlayerQueueDigestEntry(
                    building.BuildingEntityId,
                    building.QueueCount,
                    headBlueprintId,
                    headRemainingTicks);
            }

            return count;
        }
    }
}
