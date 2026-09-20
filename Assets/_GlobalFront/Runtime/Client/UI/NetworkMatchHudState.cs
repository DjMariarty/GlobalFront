using System;
using GlobalFront.Core.Model;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// Lifecycle connection status of a player slot in the network HUD (Phase 2.8, Step 2.8.2).
    /// </summary>
    public enum SlotStatus : byte
    {
        Connected = 0,
        Disconnected = 1,
        Reconnecting = 2,
        Abandoned = 3
    }

    /// <summary>
    /// Represents the network HUD state for a single player slot in 2v2 matches (Phase 2.8, Step 2.8.2).
    /// </summary>
    public struct NetworkPlayerSlotState
    {
        public PlayerId PlayerId;
        public TeamId TeamId;
        public SlotStatus SlotStatus;
        public int PingMs;

        /// <summary>
        /// Alias for <see cref="SlotStatus"/> for convenient API access.
        /// </summary>
        public SlotStatus Status
        {
            get => SlotStatus;
            set => SlotStatus = value;
        }

        public NetworkPlayerSlotState(PlayerId playerId, TeamId teamId, SlotStatus slotStatus = SlotStatus.Connected, int pingMs = 0)
        {
            PlayerId = playerId;
            TeamId = teamId;
            SlotStatus = slotStatus;
            PingMs = pingMs;
        }
    }

    /// <summary>
    /// Data model / ViewModel representing the state of the 2v2 network HUD,
    /// tactical pause, and resume countdown (Phase 2.8, Step 2.8.2).
    /// Zero-GC in steady state gameplay.
    /// </summary>
    public sealed class NetworkMatchHudState
    {
        public const int SlotCount = MatchTopology2v2.SlotCount;
        public const float DefaultGraceSeconds = 200.0f;

        private readonly NetworkPlayerSlotState[] _slots = new NetworkPlayerSlotState[SlotCount];

        /// <summary>
        /// Preallocated array of 4 player slots (0..3).
        /// </summary>
        public NetworkPlayerSlotState[] Slots => _slots;

        /// <summary>
        /// Direct by-ref indexer for zero-copy slot mutation.
        /// </summary>
        public ref NetworkPlayerSlotState this[int index] => ref _slots[index];

        /// <summary>
        /// True when tactical pause is active due to a player disconnection.
        /// </summary>
        public bool IsTacticalPauseActive { get; set; }

        /// <summary>
        /// The player whose disconnection triggered the tactical pause.
        /// </summary>
        public PlayerId PausingPlayer { get; set; }

        /// <summary>
        /// Remaining seconds in the 200.0s grace window before abandonment.
        /// </summary>
        public float RemainingGraceSeconds { get; set; }

        /// <summary>
        /// True when the 5-second resumption countdown is ticking.
        /// </summary>
        public bool IsResumeCountdownActive { get; set; }

        /// <summary>
        /// Countdown seconds remaining (5, 4, 3, 2, 1).
        /// </summary>
        public int CountdownSecondsRemaining { get; set; }

        public NetworkMatchHudState()
        {
            Reset();
        }

        /// <summary>
        /// Resets slots and pause flags according to canonical 2v2 topology.
        /// </summary>
        public void Reset()
        {
            for (var i = 0; i < SlotCount; i++)
            {
                _slots[i] = new NetworkPlayerSlotState(
                    MatchTopology2v2.GetPlayerForSlot(i),
                    MatchTopology2v2.GetTeamForSlot(i),
                    SlotStatus.Connected,
                    0);
            }

            IsTacticalPauseActive = false;
            PausingPlayer = default;
            RemainingGraceSeconds = DefaultGraceSeconds;
            IsResumeCountdownActive = false;
            CountdownSecondsRemaining = 0;
        }

        /// <summary>
        /// Finds the 0-indexed slot for a given PlayerId, returning true if found.
        /// Zero-GC allocation.
        /// </summary>
        public bool TryGetSlot(PlayerId player, out int slotIndex)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].PlayerId == player)
                {
                    slotIndex = i;
                    return true;
                }
            }

            slotIndex = -1;
            return false;
        }
    }
}
