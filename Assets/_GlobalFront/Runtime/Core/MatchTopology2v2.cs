using System;

namespace GlobalFront.Core.Model
{
    /// <summary>
    /// Configuration and helper utilities for 2v2 match topology (Phase 2.8, Step 2.8.1).
    /// Defines 4 player slots across 2 teams:
    /// - Team 0 (Red): Slot 0 (Player 1), Slot 1 (Player 2)
    /// - Team 1 (Blue): Slot 2 (Player 3), Slot 3 (Player 4)
    /// </summary>
    public static class MatchTopology2v2
    {
        public const int SlotCount = 4;
        public const int TeamCount = 2;

        public static readonly TeamId TeamRed = new TeamId(0);
        public static readonly TeamId TeamBlue = new TeamId(1);

        public static readonly PlayerId PlayerSlot0 = new PlayerId(1);
        public static readonly PlayerId PlayerSlot1 = new PlayerId(2);
        public static readonly PlayerId PlayerSlot2 = new PlayerId(3);
        public static readonly PlayerId PlayerSlot3 = new PlayerId(4);

        /// <summary>
        /// Returns the TeamId for a given 0-indexed player slot (0..3).
        /// Slot 0 and Slot 1 belong to Team 0 (Red); Slot 2 and Slot 3 belong to Team 1 (Blue).
        /// </summary>
        public static TeamId GetTeamForSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SlotCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex), $"Slot index must be between 0 and {SlotCount - 1}.");
            }

            return slotIndex < 2 ? TeamRed : TeamBlue;
        }

        /// <summary>
        /// Returns the TeamId for a given PlayerId in 2v2 topology.
        /// Players 1 and 2 belong to Team 0 (Red); Players 3 and 4 belong to Team 1 (Blue).
        /// </summary>
        public static TeamId GetTeamForPlayer(PlayerId player)
        {
            if (!player.IsValid)
            {
                throw new ArgumentException("Player must be valid.", nameof(player));
            }

            return player.Value <= 2 ? TeamRed : TeamBlue;
        }

        /// <summary>
        /// Returns the PlayerId corresponding to a 0-indexed slot (0..3).
        /// </summary>
        public static PlayerId GetPlayerForSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SlotCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex));
            }

            return new PlayerId((byte)(slotIndex + 1));
        }

        /// <summary>
        /// Returns true if two players belong to the same team.
        /// </summary>
        public static bool AreAllies(PlayerId left, PlayerId right)
        {
            return GetTeamForPlayer(left) == GetTeamForPlayer(right);
        }

        /// <summary>
        /// Returns true if two players belong to opposing teams.
        /// </summary>
        public static bool AreEnemies(PlayerId left, PlayerId right)
        {
            return GetTeamForPlayer(left) != GetTeamForPlayer(right);
        }
    }
}
