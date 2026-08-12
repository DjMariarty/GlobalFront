using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;

namespace GlobalFront.Client
{
    /// <summary>
    /// Abstraction that decouples the client-side command queue from a
    /// specific authoritative backend. Implementations forward validated
    /// commands to either a local <see cref="LocalMatchHost"/> or a future
    /// network transport without changing gameplay logic.
    ///
    /// This is the Command Channel Abstraction described in Phase 2:
    /// <code>
    /// PrototypeCommandQueue
    ///         ↓
    /// ICommandChannel
    ///         ├── LocalCommandChannel   (wraps LocalMatchHost)
    ///         └── NetworkCommandChannel (future)
    /// </code>
    ///
    /// All methods return <see cref="MatchCommandRejection"/> so the caller
    /// can surface server-side validation failures to the player HUD.
    /// </summary>
    public interface ICommandChannel
    {
        /// <summary>
        /// Submits a move command to the authoritative backend. The backend
        /// validates ownership, entity liveness and formation bounds before
        /// scheduling the command for execution.
        /// </summary>
        MatchCommandRejection TrySubmitMove(
            CommandHeader header,
            EntityId[] entities,
            WorldPointMm destination,
            FormationSpec formation);

        /// <summary>
        /// Submits an attack command to the authoritative backend. The
        /// backend validates attacker ownership, target existence and target
        /// liveness before scheduling the command.
        /// </summary>
        MatchCommandRejection TrySubmitAttack(
            CommandHeader header,
            EntityId[] attackers,
            EntityId target);

        /// <summary>
        /// Submits a stop command to the authoritative backend. The backend
        /// validates entity ownership and liveness before clearing movement
        /// and attack targets for the specified entities.
        /// </summary>
        MatchCommandRejection TrySubmitStop(
            CommandHeader header,
            EntityId[] entities);
    }
}