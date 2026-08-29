using System;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Transport;

namespace GlobalFront.Client
{
    /// <summary>
    /// Network implementation of <see cref="ICommandChannel"/> (Phase 2.5,
    /// ADR-009). The interface contract is unchanged:
    /// <c>TrySubmit*</c> return values are LOCAL pre-flight/queued results
    /// ONLY — <see cref="MatchCommandRejection.None"/> means the command was
    /// queued for delivery, NOT that the authoritative server accepted it.
    /// The authoritative result arrives asynchronously through
    /// <see cref="CommandResultReceived"/> carrying both Phase 2.4 rejection
    /// details (<see cref="SessionRejection"/> and
    /// <see cref="MatchCommandRejection"/>), so the reason of a rejection is
    /// never lost.
    /// </summary>
    public sealed class NetworkCommandChannel : ICommandChannel
    {
        private readonly ClientTransportEndpoint _endpoint;
        private readonly byte[] _encodeBuffer =
            new byte[CommandWireCodec.HeaderSizeBytes +
                1 + SimulationConstantsMaxEntities() * 8 + 32];

        /// <summary>
        /// Authoritative command result delivered asynchronously by the
        /// server. A pending (not yet acked) command must never be
        /// interpreted as accepted.
        /// </summary>
        public event Action<CommandAckPayload> CommandResultReceived;

        public NetworkCommandChannel(ClientTransportEndpoint endpoint)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _endpoint.CommandAckReceived += OnCommandAck;
        }

        public ClientTransportEndpoint Endpoint => _endpoint;

        public MatchCommandRejection TrySubmitMove(
            CommandHeader header,
            EntityId[] entities,
            WorldPointMm destination,
            FormationSpec formation)
        {
            if (_endpoint.State != ClientTransportState.Established)
            {
                return MatchCommandRejection.SessionRejected;
            }

            if (entities == null || entities.Length == 0 ||
                entities.Length > SimulationConstantsMaxEntities())
            {
                return MatchCommandRejection.InvalidPayload;
            }

            var length = CommandWireCodec.EncodeMove(
                _encodeBuffer, header, entities, destination, formation);
            return _endpoint.TrySendCommand(_encodeBuffer, length)
                ? MatchCommandRejection.None
                : MatchCommandRejection.SessionRejected;
        }

        public MatchCommandRejection TrySubmitAttack(
            CommandHeader header,
            EntityId[] attackers,
            EntityId target)
        {
            if (_endpoint.State != ClientTransportState.Established)
            {
                return MatchCommandRejection.SessionRejected;
            }

            if (attackers == null || attackers.Length == 0 ||
                attackers.Length > SimulationConstantsMaxEntities())
            {
                return MatchCommandRejection.InvalidPayload;
            }

            var length = CommandWireCodec.EncodeAttack(_encodeBuffer, header, attackers, target);
            return _endpoint.TrySendCommand(_encodeBuffer, length)
                ? MatchCommandRejection.None
                : MatchCommandRejection.SessionRejected;
        }

        public MatchCommandRejection TrySubmitStop(CommandHeader header, EntityId[] entities)
        {
            if (_endpoint.State != ClientTransportState.Established)
            {
                return MatchCommandRejection.SessionRejected;
            }

            if (entities == null || entities.Length == 0 ||
                entities.Length > SimulationConstantsMaxEntities())
            {
                return MatchCommandRejection.InvalidPayload;
            }

            var length = CommandWireCodec.EncodeStop(_encodeBuffer, header, entities);
            return _endpoint.TrySendCommand(_encodeBuffer, length)
                ? MatchCommandRejection.None
                : MatchCommandRejection.SessionRejected;
        }

        private void OnCommandAck(CommandAckPayload ack) => CommandResultReceived?.Invoke(ack);

        private static int SimulationConstantsMaxEntities() =>
            GlobalFront.Core.Simulation.SimulationConstants.MaxSelectedEntities;
    }
}
