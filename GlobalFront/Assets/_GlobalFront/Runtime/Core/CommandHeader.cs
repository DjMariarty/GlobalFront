using GlobalFront.Core.Model;

namespace GlobalFront.Core.Commands
{
    public enum GameCommandType : byte
    {
        None = 0,
        Move = 1,
        Attack = 2,
        AttackMove = 3,
        Stop = 4,
        Guard = 5,
        Build = 6,
        Produce = 7,
        UseAbility = 8
    }

    public enum CommandValidationResult : byte
    {
        Accepted = 0,
        InvalidPlayer = 1,
        MissingSequence = 2,
        UnknownCommand = 3,
        TickTooOld = 4,
        TickTooFarAhead = 5
    }

    /// <summary>
    /// Common header for every client request. Payloads are command-specific
    /// and are validated only by the authoritative server.
    /// </summary>
    public readonly struct CommandHeader
    {
        public CommandHeader(
            PlayerId player,
            uint sequence,
            ulong requestedTick,
            GameCommandType type)
        {
            Player = player;
            Sequence = sequence;
            RequestedTick = requestedTick;
            Type = type;
        }

        public PlayerId Player { get; }

        public uint Sequence { get; }

        public ulong RequestedTick { get; }

        public GameCommandType Type { get; }

        public CommandValidationResult Validate(
            ulong currentServerTick,
            ulong acceptedPastTicks,
            ulong acceptedFutureTicks)
        {
            if (!Player.IsValid)
            {
                return CommandValidationResult.InvalidPlayer;
            }

            if (Sequence == 0)
            {
                return CommandValidationResult.MissingSequence;
            }

            if (Type == GameCommandType.None ||
                (byte)Type > (byte)GameCommandType.UseAbility)
            {
                return CommandValidationResult.UnknownCommand;
            }

            if (RequestedTick < currentServerTick &&
                currentServerTick - RequestedTick > acceptedPastTicks)
            {
                return CommandValidationResult.TickTooOld;
            }

            if (RequestedTick > currentServerTick &&
                RequestedTick - currentServerTick > acceptedFutureTicks)
            {
                return CommandValidationResult.TickTooFarAhead;
            }

            return CommandValidationResult.Accepted;
        }
    }
}
