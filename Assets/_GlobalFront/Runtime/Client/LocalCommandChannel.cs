using System;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;

namespace GlobalFront.Client
{
    /// <summary>
    /// <see cref="ICommandChannel"/> implementation that wraps a
    /// <see cref="LocalMatchHost"/>, delegating every command submission
    /// directly to the in-process authoritative server. This is the only
    /// channel implementation for the pre-alpha phase; a future
    /// <c>NetworkCommandChannel</c> will replace it when transport is added.
    ///
    /// The channel holds no simulation state of its own — it is a thin
    /// adapter that translates the <see cref="ICommandChannel"/> contract
    /// into <see cref="LocalMatchHost"/> method calls.
    /// </summary>
    public sealed class LocalCommandChannel : ICommandChannel
    {
        private readonly LocalMatchHost _host;

        /// <summary>
        /// Creates a channel that forwards commands to the specified
        /// <see cref="LocalMatchHost"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="host"/> is <c>null</c>.
        /// </exception>
        public LocalCommandChannel(LocalMatchHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>Underlying host, exposed for diagnostics and tests.</summary>
        public LocalMatchHost Host => _host;

        /// <inheritdoc/>
        public MatchCommandRejection TrySubmitMove(
            CommandHeader header,
            EntityId[] entities,
            WorldPointMm destination,
            FormationSpec formation) =>
            _host.TryEnqueueMove(header, entities, destination, formation);

        /// <inheritdoc/>
        public MatchCommandRejection TrySubmitAttack(
            CommandHeader header,
            EntityId[] attackers,
            EntityId target) =>
            _host.TryEnqueueAttack(header, attackers, target);

        /// <inheritdoc/>
        public MatchCommandRejection TrySubmitStop(
            CommandHeader header,
            EntityId[] entities) =>
            _host.TryEnqueueStop(header, entities);
    }
}