using GlobalFront.Core.Model;

namespace GlobalFront.Core.Economy
{
    /// <summary>
    /// Owner-only economic digest carried by the private
    /// <c>TransportMessageType.PlayerStateSync</c> channel (Step 5.4.2): the
    /// player's credit ledger, power-grid state and production queue heads.
    /// The server emits this message strictly to the owning session so rival
    /// clients can never observe another player's balance (anti-maphack).
    /// The queue digest itself travels as a separate
    /// <see cref="PlayerQueueDigestEntry"/> span next to this struct — the
    /// codec never allocates. Scalar fields only; the wire version byte lives
    /// in <see cref="Version"/> and must equal
    /// <see cref="PlayerStateSyncCodec.Version"/>.
    /// </summary>
    public readonly struct PlayerStateSyncPayload
    {
        public PlayerStateSyncPayload(
            byte version,
            ulong tick,
            PlayerId player,
            int credits,
            int totalHarvestedCredits,
            int powerProduced,
            int powerConsumed,
            bool isLowPower)
        {
            Version = version;
            Tick = tick;
            Player = player;
            Credits = credits;
            TotalHarvestedCredits = totalHarvestedCredits;
            PowerProduced = powerProduced;
            PowerConsumed = powerConsumed;
            IsLowPower = isLowPower;
        }

        /// <summary>Wire format version; must equal <see cref="PlayerStateSyncCodec.Version"/>.</summary>
        public byte Version { get; }

        /// <summary>Authoritative simulation tick the digest was taken at.</summary>
        public ulong Tick { get; }

        /// <summary>Owning player; the message must never be routed to anyone else.</summary>
        public PlayerId Player { get; }

        /// <summary>Saturating credit balance (see <c>SimulationConstants.MaxCredits</c>).</summary>
        public int Credits { get; }

        /// <summary>Lifetime income accumulator (harvesting + secondary sources).</summary>
        public int TotalHarvestedCredits { get; }

        /// <summary>Power grid output (see <c>PlayerPowerState.Produced</c>).</summary>
        public int PowerProduced { get; }

        /// <summary>Power grid demand (see <c>PlayerPowerState.Consumed</c>).</summary>
        public int PowerConsumed { get; }

        /// <summary>Blackout flag (net power below zero and not exempt).</summary>
        public bool IsLowPower { get; }
    }

    /// <summary>
    /// One production building's queue head inside the
    /// <see cref="PlayerStateSyncPayload"/> digest. QueueCount is 0 when the
    /// building's queue is empty; the head fields are then encoded as zero.
    /// </summary>
    public readonly struct PlayerQueueDigestEntry
    {
        public PlayerQueueDigestEntry(
            ulong buildingEntityId,
            int queueCount,
            ushort headBlueprintId,
            ushort headRemainingTicks)
        {
            BuildingEntityId = buildingEntityId;
            QueueCount = queueCount;
            HeadBlueprintId = headBlueprintId;
            HeadRemainingTicks = headRemainingTicks;
        }

        public ulong BuildingEntityId { get; }

        /// <summary>Items waiting in the production queue (0..9).</summary>
        public int QueueCount { get; }

        /// <summary>Blueprint of the head item; 0 when the queue is empty.</summary>
        public ushort HeadBlueprintId { get; }

        /// <summary>Ticks left on the head item; 0 when the queue is empty.</summary>
        public ushort HeadRemainingTicks { get; }
    }
}
