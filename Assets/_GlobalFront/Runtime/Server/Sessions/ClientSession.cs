using GlobalFront.Core.Model;
using GlobalFront.Core.Reconnect;

namespace GlobalFront.Server.Sessions
{
    /// <summary>
    /// Server-authoritative representation of one client session (Phase 2.7, ADR-011).
    /// Holds the cryptographic secret, attachment state, and player/match attribution.
    /// </summary>
    public sealed class ClientSession
    {
        public SessionId Id { get; }
        public SessionState State { get; }
        public MatchId Match { get; }
        public PlayerId Player { get; }
        public ConnectionHandle Connection { get; }
        public SessionSecret32 Secret { get; }
        public ulong DisconnectedAtTick { get; }

        public ulong DetachedTick => DisconnectedAtTick;
        public bool IsAttached => State == SessionState.Connected || State == SessionState.Created;
        public bool IsDetached => State == SessionState.Disconnected;

        public ClientSession(
            SessionId id,
            SessionState state,
            MatchId match,
            PlayerId player,
            ConnectionHandle connection,
            in SessionSecret32 secret,
            ulong disconnectedAtTick)
        {
            Id = id;
            State = state;
            Match = match;
            Player = player;
            Connection = connection;
            Secret = secret;
            DisconnectedAtTick = disconnectedAtTick;
        }
    }
}
