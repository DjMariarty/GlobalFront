using System;
using System.Collections.Generic;
using GlobalFront.Core.Simulation;

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

    /// <summary>
    /// M2 remediation (P0-01): an immutable PlayerId→TeamId table for one 2v2
    /// lobby. The historical <see cref="MatchTopology2v2.GetTeamForPlayer"/>
    /// fold hardcoded <c>player.Value &lt;= 2</c> — correct only while the
    /// lobby's join order equals the PlayerId order. A lobby of players
    /// [2, 3, 4, 5] (one seat retired before the match started) split teams
    /// inconsistently: the fog filter keyed on slot order while the combat
    /// relation keyed on the id fold, so two players shared a team in FoW and
    /// killed each other in combat. A mapping built once from the lobby's
    /// slot order feeds BOTH the fog filter and the combat relation, keeping
    /// them consistent by construction.
    /// </summary>
    /// <remarks>
    /// Setup-path only: the table is allocated at match start and read on
    /// every combat tick — reads allocate nothing.
    /// </remarks>
    public readonly struct TeamSlotMapping2v2
    {
        private const byte UnassignedTeam = byte.MaxValue;

        /// <summary>Team byte per <see cref="PlayerId.Value"/>; index 0 unused.</summary>
        private readonly byte[] _teamByPlayerValue;

        private TeamSlotMapping2v2(byte[] teamByPlayerValue)
        {
            _teamByPlayerValue = teamByPlayerValue;
        }

        /// <summary>
        /// Builds the mapping from the lobby's slot order: the first two slots
        /// form Team Red, the last two Team Blue — the same geometry
        /// <see cref="MatchTopology2v2.GetTeamForSlot"/> and the dedicated
        /// server's spawn layout use.
        /// </summary>
        public static TeamSlotMapping2v2 FromSlots(IReadOnlyList<PlayerId> slots)
        {
            if (slots == null || slots.Count != MatchTopology2v2.SlotCount)
            {
                throw new ArgumentException(
                    $"A 2v2 mapping needs exactly {MatchTopology2v2.SlotCount} slots.",
                    nameof(slots));
            }

            var table = new byte[SimulationConstants.MaxPlayers + 1];
            for (var index = 0; index < table.Length; index++)
            {
                table[index] = UnassignedTeam;
            }

            for (var slot = 0; slot < MatchTopology2v2.SlotCount; slot++)
            {
                var player = slots[slot];
                if (!player.IsValid)
                {
                    throw new ArgumentException(
                        $"Slot {slot} carries an invalid PlayerId.", nameof(slots));
                }

                table[player.Value] = MatchTopology2v2.GetTeamForSlot(slot).Value;
            }

            return new TeamSlotMapping2v2(table);
        }

        /// <summary>
        /// The canonical default mapping: players 1 and 2 form Team Red,
        /// every valid player above 2 folds into Team Blue — exactly the
        /// Step 5.6.1 behaviour the static
        /// <see cref="MatchTopology2v2.GetTeamForPlayer"/> hardcodes. Keeps
        /// unconfigured relations (the <c>Instance</c> singleton, tests, the
        /// playtest rig) bit-for-bit identical to the pre-remediation code.
        /// </summary>
        public static TeamSlotMapping2v2 DefaultCanonical()
        {
            var table = new byte[SimulationConstants.MaxPlayers + 1];
            for (var value = 1; value < table.Length; value++)
            {
                table[value] = value <= 2
                    ? MatchTopology2v2.TeamRed.Value
                    : MatchTopology2v2.TeamBlue.Value;
            }

            return new TeamSlotMapping2v2(table);
        }

        /// <summary>
        /// Returns the lobby's team for <paramref name="player"/>. Fails fast
        /// on an invalid or unmapped player: a 2v2 relation that cannot answer
        /// "which team" must never guess (a wrong guess turns an ally into a
        /// legal target).
        /// </summary>
        public TeamId GetTeamForPlayer(PlayerId player)
        {
            if (!player.IsValid || _teamByPlayerValue == null ||
                player.Value >= _teamByPlayerValue.Length ||
                _teamByPlayerValue[player.Value] == UnassignedTeam)
            {
                throw new ArgumentException(
                    $"Player {player.Value} is not mapped to a 2v2 team.",
                    nameof(player));
            }

            return new TeamId(_teamByPlayerValue[player.Value]);
        }

        /// <summary>
        /// N-03 (ТЗ 6.0-D): whether the mapping carries a table at all — a
        /// default struct answers every query through the caller's fallback
        /// instead of throwing.
        /// </summary>
        public bool IsConfigured => _teamByPlayerValue != null;

        /// <summary>
        /// N-03 (ТЗ 6.0-D): whether <paramref name="player"/> carries a team
        /// in this mapping — client-side safe (neutral objects, retired ids
        /// and out-of-lobby players all read as unassigned, never throw).
        /// </summary>
        public bool IsAssigned(PlayerId player)
        {
            return player.IsValid &&
                   _teamByPlayerValue != null &&
                   player.Value < _teamByPlayerValue.Length &&
                   _teamByPlayerValue[player.Value] != UnassignedTeam;
        }

        /// <summary>
        /// N-03 (ТЗ 6.0-D): the non-throwing team read for client paths —
        /// unassigned players (neutrals, retired ids, out-of-lobby ids)
        /// return false instead of an exception. The fail-fast
        /// <see cref="GetTeamForPlayer"/> stays for the server paths.
        /// </summary>
        public bool TryGetTeamForPlayer(PlayerId player, out TeamId team)
        {
            if (IsAssigned(player))
            {
                team = new TeamId(_teamByPlayerValue[player.Value]);
                return true;
            }

            team = default;
            return false;
        }

        /// <summary>
        /// N-03 (ТЗ 6.0-D): the client-safe ally test — never throws. Two
        /// unassigned players (or one unassigned and one assigned) are not
        /// allies; only players that BOTH carry teams in this mapping and
        /// share that team answer true. The server's hostility paths keep
        /// the fail-fast <see cref="GetTeamForPlayer"/> contract.
        /// </summary>
        public bool AreAllies(PlayerId left, PlayerId right)
        {
            return TryGetTeamForPlayer(left, out var leftTeam) &&
                   TryGetTeamForPlayer(right, out var rightTeam) &&
                   leftTeam == rightTeam;
        }
    }
}
