using System;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;

namespace GlobalFront.Client
{
    /// <summary>
    /// <see cref="ICommandChannel"/> implementation that wraps a
    /// <see cref="LocalMatchHost"/>, delegating every command submission to
    /// the in-process authoritative server through the Phase 2.4 session
    /// gate (ADR-008): the channel is attributed with the SessionId it was
    /// created for, and the host accepts a command only when that session is
    /// connected and bound to <c>header.Player</c>. This is the only channel
    /// implementation for the pre-alpha phase; a future
    /// <c>NetworkCommandChannel</c> will carry attribution from the
    /// transport instead.
    ///
    /// The channel holds no simulation state of its own — it is a thin
    /// adapter that translates the <see cref="ICommandChannel"/> contract
    /// into <see cref="LocalMatchHost"/> method calls.
    /// </summary>
    public sealed class LocalCommandChannel : ICommandChannel
    {
        private readonly LocalMatchHost _host;
        private readonly SessionId _session;

        /// <summary>
        /// Creates a channel that forwards commands attributed to
        /// <paramref name="session"/> to the specified
        /// <see cref="LocalMatchHost"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="host"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="session"/> is invalid: every command
        /// submission must be attributable to a server-issued session.
        /// </exception>
        public LocalCommandChannel(LocalMatchHost host, SessionId session)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            if (!session.IsValid)
            {
                throw new ArgumentException("Session must be valid.", nameof(session));
            }

            _session = session;
        }

        /// <summary>Underlying host, exposed for diagnostics and tests.</summary>
        public LocalMatchHost Host => _host;

        /// <summary>Server-issued session this channel submits as.</summary>
        public SessionId Session => _session;

        /// <inheritdoc/>
        public MatchCommandRejection TrySubmitMove(
            CommandHeader header,
            EntityId[] entities,
            WorldPointMm destination,
            FormationSpec formation) =>
            ToRejection(_host.TrySessionMove(_session, header, entities, destination, formation));

        /// <inheritdoc/>
        public MatchCommandRejection TrySubmitAttack(
            CommandHeader header,
            EntityId[] attackers,
            EntityId target) =>
            ToRejection(_host.TrySessionAttack(_session, header, attackers, target));

        /// <inheritdoc/>
        public MatchCommandRejection TrySubmitStop(
            CommandHeader header,
            EntityId[] entities) =>
            ToRejection(_host.TrySessionStop(_session, header, entities));

        /// <summary>
        /// Collapses the session-gated result onto the Phase 2.2 channel
        /// contract: any session-gate refusal surfaces as
        /// <see cref="MatchCommandRejection.SessionRejected"/>; otherwise the
        /// unchanged MatchServer verdict is returned.
        /// </summary>
        private static MatchCommandRejection ToRejection(SessionCommandResult result) =>
            result.Session != SessionRejection.None
                ? MatchCommandRejection.SessionRejected
                : result.Command;
    }
}
