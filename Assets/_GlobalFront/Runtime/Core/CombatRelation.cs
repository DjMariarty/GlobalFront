using GlobalFront.Core.Model;

namespace GlobalFront.Core.Combat
{
    /// <summary>
    /// Decides who may shoot whom, and when a battle is over (Phase 1.0, P1-2).
    /// The simulation previously derived both from <see cref="PlayerId"/>
    /// identity, which is wrong for any team topology: allies became hostile to
    /// each other, and a match whose survivors shared a team but not an owner
    /// never terminated.
    /// </summary>
    /// <remarks>
    /// Implementations run on every combat tick, so they must allocate nothing.
    /// </remarks>
    public interface ICombatRelation
    {
        /// <summary>
        /// Returns true when <paramref name="left"/> may acquire and damage
        /// <paramref name="right"/>. Implementations are not required to reject
        /// an owner that equals <paramref name="left"/>; self-targeting is
        /// already refused by <see cref="CombatantState.TryAssignTarget"/>.
        /// </summary>
        bool AreEnemies(PlayerId left, PlayerId right);

        /// <summary>
        /// Outcome for the living combatants in <c>canonical[0 .. count)</c>.
        /// </summary>
        /// <remarks>
        /// <paramref name="canonical"/> is the resolver's instance scratch buffer:
        /// read only the first <paramref name="count"/> entries, never mutate
        /// them, and never retain the array past the call.
        /// </remarks>
        BattleOutcome DetermineOutcome(CombatantTickInput[] canonical, int count);
    }

    /// <summary>
    /// Every distinct player is an enemy, and the battle ends when one player
    /// alone survives. This is the historical free-for-all behavior and stays
    /// the default so 1v1 skirmish results are bit-for-bit unchanged.
    /// </summary>
    public sealed class SkirmishCombatRelation : ICombatRelation
    {
        public static readonly SkirmishCombatRelation Instance =
            new SkirmishCombatRelation();

        private SkirmishCombatRelation()
        {
        }

        /// <remarks>
        /// Deliberately no <c>IsValid</c> check: two default (invalid) owners
        /// must keep reading as the same player, exactly as the previous
        /// <c>Owner == attacker.Owner</c> comparisons did.
        /// </remarks>
        public bool AreEnemies(PlayerId left, PlayerId right) => left != right;

        public BattleOutcome DetermineOutcome(CombatantTickInput[] canonical, int count)
        {
            var hasLivingOwner = false;
            var soleOwner = default(PlayerId);

            for (var index = 0; index < count; index++)
            {
                var state = canonical[index].State;
                if (!state.IsAlive)
                {
                    continue;
                }

                if (!hasLivingOwner)
                {
                    hasLivingOwner = true;
                    soleOwner = state.Owner;
                    continue;
                }

                if (state.Owner != soleOwner)
                {
                    return BattleOutcome.InProgress;
                }
            }

            return hasLivingOwner
                ? BattleOutcome.Victory(soleOwner)
                : BattleOutcome.Draw;
        }
    }

    /// <summary>
    /// Hostility and termination follow a 2v2 team table: players 1 and 2 form
    /// Team Red, players 3 and 4 form Team Blue. A team whose members are all
    /// dead loses even while its allies keep fighting, which is what lets such
    /// a match terminate at all.
    /// </summary>
    /// <remarks>
    /// M2 remediation (P0-01): the relation reads a
    /// <see cref="TeamSlotMapping2v2"/> instead of folding on
    /// <c>player.Value &lt;= 2</c>. A lobby whose join order no longer equals
    /// the PlayerId order (players [2, 3, 4, 5] after one seat retired) must
    /// decide hostility from the same mapping the fog filter uses — otherwise
    /// two players share a team in FoW and kill each other in combat. The
    /// unconfigured default keeps the historical canonical mapping, so the
    /// <see cref="Instance"/> singleton and every existing test behave exactly
    /// as before; a dynamic lobby calls <see cref="ConfigureSlots"/> at match
    /// start. Valid for exactly four players per configured lobby.
    /// </remarks>
    public sealed class Team2v2CombatRelation : ICombatRelation
    {
        public static readonly Team2v2CombatRelation Instance =
            new Team2v2CombatRelation();

        private TeamSlotMapping2v2 _teamMapping;

        /// <summary>
        /// Canonical default mapping: players 1-2 Team Red, players 3+ Team
        /// Blue — the Step 5.6.1 fold the static
        /// <see cref="MatchTopology2v2.GetTeamForPlayer"/> hardcodes.
        /// </summary>
        public Team2v2CombatRelation()
        {
            _teamMapping = TeamSlotMapping2v2.DefaultCanonical();
        }

        /// <summary>
        /// M2 remediation (P0-01): replaces the team table with the lobby's
        /// slot mapping. Called once at match start, before the first tick;
        /// the same mapping must also reach
        /// <c>FogOfWarReplicationFilter.SetPlayerTeam</c> so combat hostility
        /// and fog ally-visibility can never disagree.
        /// </summary>
        public void ConfigureSlots(TeamSlotMapping2v2 mapping)
        {
            _teamMapping = mapping;
        }

        /// <summary>
        /// M2 remediation (P0-01): the team this relation currently assigns to
        /// <paramref name="player"/> — the lobby mapping's read side, so
        /// diagnostics and tests can compare combat hostility against the fog
        /// filter's team table. Fails fast on an unmapped player.
        /// </summary>
        public TeamId GetTeamForPlayer(PlayerId player) =>
            _teamMapping.GetTeamForPlayer(player);

        /// <remarks>
        /// Fails fast on an invalid or unmapped owner. Unreachable from the
        /// resolver and the server, both of which validate owners before a
        /// combatant exists.
        /// </remarks>
        public bool AreEnemies(PlayerId left, PlayerId right) =>
            _teamMapping.GetTeamForPlayer(left) != _teamMapping.GetTeamForPlayer(right);

        public BattleOutcome DetermineOutcome(CombatantTickInput[] canonical, int count)
        {
            var hasLivingTeam = false;
            var soleTeam = default(TeamId);

            for (var index = 0; index < count; index++)
            {
                var state = canonical[index].State;
                if (!state.IsAlive)
                {
                    continue;
                }

                var team = _teamMapping.GetTeamForPlayer(state.Owner);
                if (!hasLivingTeam)
                {
                    hasLivingTeam = true;
                    soleTeam = team;
                    continue;
                }

                if (team != soleTeam)
                {
                    return BattleOutcome.InProgress;
                }
            }

            return hasLivingTeam
                ? BattleOutcome.TeamVictory(soleTeam)
                : BattleOutcome.Draw;
        }
    }
}
