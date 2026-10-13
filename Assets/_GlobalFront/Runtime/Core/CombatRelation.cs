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
        /// <paramref name="canonical"/> is the resolver's shared static buffer:
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
    /// Hostility and termination follow <see cref="MatchTopology2v2"/>: players
    /// 1 and 2 form Team Red, players 3 and 4 form Team Blue. A team whose
    /// members are all dead loses even while its allies keep fighting, which is
    /// what lets such a match terminate at all.
    /// </summary>
    /// <remarks>
    /// Valid for exactly four players. <see cref="MatchTopology2v2.GetTeamForPlayer"/>
    /// folds every owner above 2 into Team Blue, so a larger match must not
    /// reuse this relation.
    /// </remarks>
    public sealed class Team2v2CombatRelation : ICombatRelation
    {
        public static readonly Team2v2CombatRelation Instance =
            new Team2v2CombatRelation();

        private Team2v2CombatRelation()
        {
        }

        /// <remarks>
        /// Fails fast on an invalid owner. Unreachable from the resolver and the
        /// server, both of which validate owners before a combatant exists.
        /// </remarks>
        public bool AreEnemies(PlayerId left, PlayerId right) =>
            MatchTopology2v2.AreEnemies(left, right);

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

                var team = MatchTopology2v2.GetTeamForPlayer(state.Owner);
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
