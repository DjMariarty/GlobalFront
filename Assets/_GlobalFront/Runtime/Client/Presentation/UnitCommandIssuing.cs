using System;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using EntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// One selection-driven RTS request, already reduced to what the authoritative
    /// command layer needs (Phase 3, step 3.5).
    ///
    /// A struct over a span-of-entities rather than three command classes because the
    /// controller is the place that knows what the player asked for, and the
    /// <see cref="Core.Commands.CommandHeader"/> ordering contract
    /// (<c>RequestedTick → PlayerId → Sequence</c>) is not its concern. This is the
    /// client's intent; turning it into a validated
    /// <see cref="MoveCommand"/>/<see cref="AttackCommand"/>/<see cref="StopCommand"/>
    /// is the sink's job, and the authoritative server keeps being the only place that
    /// decides whether it is legal.
    ///
    /// <see cref="GetEntity"/> reads through a shared buffer the controller refills per
    /// request, so a sink that wants to keep a command beyond the call has to copy it.
    /// That is the same lifetime the existing
    /// <see cref="PrototypeCommandQueue"/> gives its callers, and it is what keeps the
    /// selection path allocation-free: a 400-unit drag would otherwise allocate an
    /// array per emitted command.
    /// </summary>
    public readonly struct IssuedCommand
    {
        private readonly EntityId[] _entities;
        private readonly int _entityCount;

        internal IssuedCommand(
            CommandHeader header,
            EntityId[] entities,
            int entityCount,
            WorldPointMm destination,
            EntityId target,
            FormationSpec formation)
        {
            _entities = entities;
            _entityCount = entityCount;
            Header = header;
            Destination = destination;
            Target = target;
            Formation = formation;
        }

        /// <summary>Player attribution, sequence and requested tick.</summary>
        public CommandHeader Header { get; }

        /// <summary>Move destination; meaningless for attack and stop.</summary>
        public WorldPointMm Destination { get; }

        /// <summary>Attack target; meaningless for move and stop.</summary>
        public EntityId Target { get; }

        /// <summary>Formation the move destination is spread into.</summary>
        public FormationSpec Formation { get; }

        /// <summary>The request kind, carried by the header.</summary>
        public GameCommandType Kind => Header.Type;

        /// <summary>How many entities this request addresses.</summary>
        public int EntityCount => _entityCount;

        /// <summary>
        /// Entity <paramref name="index"/> of the request, in the canonical ascending
        /// <see cref="EntityId.Value"/> order the controller keeps its selection in.
        /// Out-of-range reads come back invalid instead of throwing, so a sink walking
        /// to <see cref="EntityCount"/> cannot be tripped by a stale count.
        /// </summary>
        public EntityId GetEntity(int index) =>
            _entities != null && (uint)index < (uint)_entityCount
                ? _entities[index]
                : default;
    }

    /// <summary>
    /// Where the selection layer hands its requests to the client loop (step 3.5).
    ///
    /// An interface rather than a direct <see cref="ICommandChannel"/> call because the
    /// two halves of the job differ: selection decides synchronously, on the frame the
    /// player clicked, while submission is owned by the loop, which knows the current
    /// tick window, the session attribution and where the match is actually hosted
    /// (local host today, transport later, per ADR-007/ADR-009). The seam also lets an
    /// EditMode test read exactly what a click decided without standing up a server.
    /// </summary>
    public interface IUnitCommandSink
    {
        /// <summary>
        /// Hands over one request. Implementations must not retain
        /// <see cref="IssuedCommand.GetEntity"/>'s backing buffer past the call.
        /// </summary>
        void Submit(in IssuedCommand command);
    }

    /// <summary>
    /// Sink that forwards selection requests to an <see cref="ICommandChannel"/>
    /// (step 3.5) — the production path.
    ///
    /// It copies each request into an exactly-sized array rather than handing over the
    /// controller's shared buffer, because <see cref="ICommandChannel"/> takes an
    /// <see cref="EntityId"/> array whose length <em>is</em> the count. That is one
    /// small allocation per click, which is the cost the pre-existing
    /// <see cref="PrototypeCommandQueue"/> path already pays and is not on the
    /// per-frame budget: a player cannot generate more than a few commands a second,
    /// whereas the selection maintenance runs every frame for every unit.
    ///
    /// Nothing here re-validates legality. <c>MatchServer</c> already runs
    /// <see cref="MoveCommand.TryCreate"/> and <see cref="AttackCommand.TryCreate"/> on
    /// the authoritative side, and duplicating those rules client-side would give the
    /// two copies room to disagree.
    /// </summary>
    public sealed class UnitCommandChannelSink : IUnitCommandSink
    {
        private readonly ICommandChannel _channel;

        public UnitCommandChannelSink(ICommandChannel channel)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        /// <summary>Rejection from the last submission, for HUD surfacing.</summary>
        public MatchCommandRejection LastRejection { get; private set; }

        /// <summary>Kind of the last submission, so a HUD can name it.</summary>
        public GameCommandType LastKind { get; private set; }

        /// <summary>Requests the channel accepted since construction.</summary>
        public int SubmittedCount { get; private set; }

        /// <summary>
        /// Requests handed over since construction, accepted or refused.
        ///
        /// <see cref="SubmittedCount"/> cannot tell a HUD that an order happened at all:
        /// a refused request leaves it exactly where it was, so the orders a player most
        /// needs to see are the ones the accepted counter never mentions.
        /// </summary>
        public int AttemptedCount { get; private set; }

        public void Submit(in IssuedCommand command)
        {
            LastKind = command.Kind;
            AttemptedCount++;

            var entities = new EntityId[command.EntityCount];
            for (var index = 0; index < entities.Length; index++)
            {
                entities[index] = command.GetEntity(index);
            }

            LastRejection = command.Kind switch
            {
                GameCommandType.Move => _channel.TrySubmitMove(
                    command.Header,
                    entities,
                    command.Destination,
                    command.Formation),
                GameCommandType.Attack => _channel.TrySubmitAttack(
                    command.Header,
                    entities,
                    command.Target),
                GameCommandType.Stop => _channel.TrySubmitStop(command.Header, entities),
                _ => MatchCommandRejection.HeaderRejected,
            };

            if (LastRejection == MatchCommandRejection.None)
            {
                SubmittedCount++;
            }
        }
    }
}
