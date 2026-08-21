using GlobalFront.Core.Model;

namespace GlobalFront.Server.Sessions
{
    /// <summary>
    /// Immutable view of one server-side session, used by tests and
    /// diagnostics (Phase 2.4, ADR-008). Mirrors the ServerUnitSnapshot
    /// convention: the registry never hands out mutable state.
    /// </summary>
    public readonly struct SessionRecord
    {
        public SessionRecord(
            SessionId session,
            SessionState state,
            MatchId match,
            PlayerId player,
            ConnectionHandle connection,
            ulong disconnectedAtTick)
        {
            Session = session;
            State = state;
            Match = match;
            Player = player;
            Connection = connection;
            DisconnectedAtTick = disconnectedAtTick;
        }

        public SessionId Session { get; }

        public SessionState State { get; }

        /// <summary>Bound match; invalid while the session is unbound.</summary>
        public MatchId Match { get; }

        /// <summary>Server-assigned PlayerId; invalid while the session is unbound.</summary>
        public PlayerId Player { get; }

        public ConnectionHandle Connection { get; }

        /// <summary>Server tick at which the session entered the Disconnected state.</summary>
        public ulong DisconnectedAtTick { get; }
    }

    /// <summary>
    /// Everything a reconnecting client needs to resume its identity
    /// (Phase 2.4, ADR-008). The actual resync flow is Phase 2.7; this
    /// receipt defines the identity semantics it will build on. The client
    /// resumes command numbering strictly above LastAcceptedSequence, which
    /// the authoritative server already deduplicates per player.
    /// </summary>
    public readonly struct ReconnectReceipt
    {
        public ReconnectReceipt(
            MatchId match,
            PlayerId player,
            ulong currentTick,
            uint lastAcceptedSequence)
        {
            Match = match;
            Player = player;
            CurrentTick = currentTick;
            LastAcceptedSequence = lastAcceptedSequence;
        }

        public MatchId Match { get; }

        public PlayerId Player { get; }

        public ulong CurrentTick { get; }

        public uint LastAcceptedSequence { get; }
    }

    /// <summary>
    /// Combined outcome of a session-gated command submission (Phase 2.4,
    /// ADR-008): the session gate verdict first, then — only when the gate
    /// passed — the authoritative <see cref="MatchCommandRejection"/> from
    /// <see cref="MatchServer"/>, whose own validation is unchanged.
    /// </summary>
    public readonly struct SessionCommandResult
    {
        private SessionCommandResult(SessionRejection session, MatchCommandRejection command)
        {
            Session = session;
            Command = command;
        }

        public SessionRejection Session { get; }

        public MatchCommandRejection Command { get; }

        public bool IsAccepted =>
            Session == SessionRejection.None && Command == MatchCommandRejection.None;

        public static SessionCommandResult Rejected(SessionRejection session) =>
            new SessionCommandResult(session, MatchCommandRejection.None);

        public static SessionCommandResult FromMatch(MatchCommandRejection command) =>
            new SessionCommandResult(SessionRejection.None, command);
    }
}
