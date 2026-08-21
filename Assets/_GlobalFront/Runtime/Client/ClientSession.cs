using GlobalFront.Core.Model;

namespace GlobalFront.Client
{
    /// <summary>
    /// Passive holder of the identity the authoritative server assigned to
    /// this client (Phase 2.4, ADR-008). The client is a consumer of
    /// identity, never a source: the SessionId, MatchId and PlayerId all come
    /// from the server-side session layer (via the hosting layer in the local
    /// prototype, via the future transport later).
    /// </summary>
    public sealed class ClientSession
    {
        public ClientSession(SessionId session, MatchId match, PlayerId player)
        {
            Session = session;
            Match = match;
            Player = player;
        }

        public SessionId Session { get; }

        public MatchId Match { get; }

        /// <summary>Server-assigned PlayerId; the client never chooses it.</summary>
        public PlayerId Player { get; }
    }
}
