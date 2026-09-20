namespace GlobalFront.Server.Sessions
{
    /// <summary>
    /// Lifecycle state of one server-side session (Phase 2.4, ADR-008).
    /// Transitions are decided exclusively by the server; client calls are
    /// requests that receive typed results.
    /// </summary>
    public enum SessionState : byte
    {
        /// <summary>Registered on the server, not yet bound to a match.</summary>
        Created = 0,

        /// <summary>Bound to a match slot with a live connection.</summary>
        Connected = 1,

        /// <summary>
        /// Connection lost; the grace window is running. The PlayerId binding
        /// and the server-side command sequence are retained so a reconnect
        /// can resume the same identity (OD-4).
        /// </summary>
        Disconnected = 2,

        /// <summary>Terminal: grace expired, the player left, or the match was torn down.</summary>
        Closed = 3,

        /// <summary>Alias for Connected under ADR-011 re-attachment terminology.</summary>
        Attached = Connected,

        /// <summary>Alias for Disconnected under ADR-011 re-attachment terminology.</summary>
        Detached = Disconnected,

        /// <summary>Alias for Closed when grace expires or session is abandoned (Phase 2.8, P2-1).</summary>
        Abandoned = Closed
    }

    /// <summary>Lifecycle phase of one session-managed match (Phase 2.4, ADR-008).</summary>
    public enum MatchPhase : byte
    {
        /// <summary>Accepting joins; <c>MatchServer.InitializeMatch</c> has not run.</summary>
        Forming = 0,

        /// <summary>Initialized and ticking; commands from connected slots are admissible.</summary>
        Running = 1,

        /// <summary>
        /// The authoritative <c>BattleOutcome</c> became terminal. This is
        /// the terminal phase of the Phase 2.4 lifecycle: sessions stop
        /// submitting, joins are refused, and the simulation record stays
        /// available for diagnostics.
        /// </summary>
        Finished = 2,

        /// <summary>
        /// RESERVED — future lifecycle state, never assigned in Phase 2.4.
        /// Intended final teardown: sessions released and the registry entry
        /// disposed. Teardown / registry disposal belongs to the
        /// dedicated/server lifecycle and requires a separate decision
        /// (ADR-008); until then the lifecycle ends at
        /// <see cref="Finished"/>.
        /// </summary>
        Closed = 3
    }

    /// <summary>
    /// Connection state of one player slot inside a match (Phase 2.4,
    /// ADR-008). A slot's PlayerId is assigned once and never reused within
    /// its match (OD-3).
    /// </summary>
    public enum PlayerConnectionState : byte
    {
        /// <summary>PlayerId bound while the match is forming.</summary>
        Assigned = 0,

        /// <summary>Match running with a live connection: commands accepted.</summary>
        Connected = 1,

        /// <summary>Grace window: slot and PlayerId retained, commands rejected.</summary>
        Disconnected = 2,

        /// <summary>
        /// Grace expired, the player left, or the match finished. The PlayerId
        /// is retired for this match and is never reassigned.
        /// </summary>
        Abandoned = 3
    }

    /// <summary>Why the session gate refused a command before it could reach <c>MatchServer</c>.</summary>
    public enum SessionRejection : byte
    {
        None = 0,
        UnknownSession = 1,
        SessionClosed = 2,
        SessionNotConnected = 3,
        NoBoundMatch = 4,

        /// <summary><c>CommandHeader.Player</c> does not equal the session's bound PlayerId.</summary>
        PlayerMismatch = 5,

        MatchNotRunning = 6
    }

    /// <summary>Result of a join request against a forming match.</summary>
    public enum JoinResult : byte
    {
        Assigned = 0,
        UnknownSession = 1,
        SessionClosed = 2,
        SessionAlreadyBound = 3,
        UnknownMatch = 4,
        MatchNotForming = 5,
        MatchFull = 6
    }

    /// <summary>Result of a reconnect attempt (identity semantics only; resync is Phase 2.7).</summary>
    public enum ReconnectResult : byte
    {
        Reconnected = 0,
        UnknownSession = 1,
        SessionClosed = 2,

        /// <summary>The session is not in the Disconnected state.</summary>
        NotDisconnected = 3,

        GraceExpired = 4
    }

    /// <summary>Result of a session re-attachment / rebind attempt (Phase 2.7, ADR-011).</summary>
    public enum RebindResult : byte
    {
        Accepted = 0,
        SessionNotFound = 1,
        InvalidSecret = 2,
        GraceExpired = 3,
        MatchFinished = 4
    }
}
