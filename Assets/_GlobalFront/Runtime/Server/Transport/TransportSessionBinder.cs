using System.Collections.Generic;
using System.Security.Cryptography;
using GlobalFront.Core.Model;
using GlobalFront.Server.Sessions;

namespace GlobalFront.Server.Transport
{
    /// <summary>
    /// Server-side attribution registry of Phase 2.5 (ADR-009): maps the
    /// crypto-random 64-bit session token to
    /// <c>ConnectionHandle → SessionId</c> (Phase 2.4 identity). The token
    /// is NOT authentication — it identifies an attachment inside an
    /// established connection; authentication remains deferred. All calls are
    /// host/simulation-thread only.
    /// </summary>
    public sealed class TransportSessionBinder
    {
        public struct Binding
        {
            public ulong Token;
            public ConnectionHandle Handle;
            public SessionId Session;
            public MatchId Match;
            public PlayerId Player;
            public int ConnectionId;
        }

        private readonly Dictionary<ulong, Binding> _byToken =
            new Dictionary<ulong, Binding>();
        private readonly Dictionary<SessionId, ulong> _tokenBySession =
            new Dictionary<SessionId, ulong>();
        private readonly Dictionary<int, ulong> _tokenByConnection =
            new Dictionary<int, ulong>();
        private readonly List<SessionId> _cachedSnapshotTargets = new();
        private readonly RandomNumberGenerator _random = RandomNumberGenerator.Create();

        public int BoundCount => _byToken.Count;

        /// <summary>Cached list of the currently bound sessions (broadcast targets). Zero GC.</summary>
        public IReadOnlyList<SessionId> SnapshotTargets => _cachedSnapshotTargets;

        /// <summary>Issues a crypto-random token and records the binding.</summary>
        public ulong Bind(
            SessionId session,
            ConnectionHandle handle,
            MatchId match,
            PlayerId player,
            int connectionId)
        {
            ulong token;
            do
            {
                var bytes = new byte[8];
                _random.GetBytes(bytes);
                token =
                    (ulong)bytes[0] |
                    ((ulong)bytes[1] << 8) |
                    ((ulong)bytes[2] << 16) |
                    ((ulong)bytes[3] << 24) |
                    ((ulong)bytes[4] << 32) |
                    ((ulong)bytes[5] << 40) |
                    ((ulong)bytes[6] << 48) |
                    ((ulong)bytes[7] << 56);
            }
            while (token == TransportProtocol.HandshakeToken || _byToken.ContainsKey(token));

            var binding = new Binding
            {
                Token = token,
                Handle = handle,
                Session = session,
                Match = match,
                Player = player,
                ConnectionId = connectionId
            };
            _byToken[token] = binding;
            _tokenBySession[session] = token;
            _tokenByConnection[connectionId] = token;
            if (!_cachedSnapshotTargets.Contains(session))
            {
                _cachedSnapshotTargets.Add(session);
            }
            return token;
        }

        public bool TryGetByToken(ulong token, out Binding binding) =>
            _byToken.TryGetValue(token, out binding);

        public bool TryGetByConnection(int connectionId, out Binding binding)
        {
            binding = default;
            return _tokenByConnection.TryGetValue(connectionId, out var token) &&
                _byToken.TryGetValue(token, out binding);
        }

        public bool TryGetTokenBySession(SessionId session, out ulong token) =>
            _tokenBySession.TryGetValue(session, out token);

        /// <summary>Releases the binding for a session (disconnect/close).</summary>
        public bool Release(SessionId session)
        {
            if (!_tokenBySession.TryGetValue(session, out var token))
            {
                return false;
            }

            _tokenBySession.Remove(session);
            if (_byToken.TryGetValue(token, out var binding))
            {
                _byToken.Remove(token);
                _tokenByConnection.Remove(binding.ConnectionId);
            }

            _cachedSnapshotTargets.Remove(session);
            return true;
        }

        /// <summary>Alias for <see cref="Release"/>.</summary>
        public bool Unbind(SessionId session) => Release(session);

        public void ReleaseAll()
        {
            _byToken.Clear();
            _tokenBySession.Clear();
            _tokenByConnection.Clear();
            _cachedSnapshotTargets.Clear();
        }
    }
}
