namespace GlobalFront.Core.Model
{
    /// <summary>
    /// Match layout selector of the dedicated host (Step 5.6.1, Milestone M2).
    ///
    /// Chooses how the lobby fills and how the armies spawn: a skirmish needs
    /// two players facing each other, a team match needs the four
    /// <see cref="MatchTopology2v2"/> slots — players 1 and 2 (Team Red) against
    /// players 3 and 4 (Team Blue). The mode is a host-level lobby decision,
    /// not sim state: the simulation only ever sees its consequences (the
    /// combat relation, the spawn template, the fog team mapping).
    ///
    /// Values are append-only: renumbering would re-shape every recorded or
    /// re-joined lobby.
    /// </summary>
    public enum MatchTopologyMode : byte
    {
        /// <summary>Two players, last-owner-standing (SkirmishCombatRelation).</summary>
        Skirmish1v1 = 0,

        /// <summary>Four players in two teams (Team2v2CombatRelation, MatchTopology2v2 geometry).</summary>
        Team2v2 = 1
    }
}
