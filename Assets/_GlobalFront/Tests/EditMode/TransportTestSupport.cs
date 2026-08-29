using GlobalFront.Client;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Transport;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Deterministic virtual transport clock for Phase 2.5 tests: RTO,
    /// keepalive, idle timeout and fragment lifetime all advance only
    /// through <see cref="Advance"/> — no wall-clock dependency.
    /// </summary>
    public sealed class VirtualTransportClock : ITransportClock
    {
        public long NowMs { get; private set; }

        public void Advance(long milliseconds) => NowMs += milliseconds;
    }

    /// <summary>
    /// Shared rig helpers for transport tests: a LocalMatchHost with a
    /// session-managed match plus server/client endpoints over one
    /// VirtualNetworkPipe.
    /// </summary>
    public static class TransportTestSupport
    {
        public static readonly CombatStats StandardStats = new CombatStats(
            maximumHealth: 100,
            damage: 25,
            rangeMm: 7000,
            cooldownTicks: 10);

        public sealed class Rig
        {
            public VirtualNetworkPipe Pipe;
            public VirtualTransportClock Clock;
            public LocalMatchHost Host;
            public MatchId Match;
            public ServerTransportHost ServerTransport;
            public OwnDatagramCarrier ServerCarrier;

            public void Pump(long milliseconds)
            {
                Pipe.Advance(milliseconds);
                Clock.Advance(milliseconds);
                ServerTransport.Pump(Clock.NowMs);
            }
        }

        public static Rig CreateRig(ImpairmentProfile profile, int capacity = 2, bool autoJoin = true)
        {
            var rig = new Rig
            {
                Pipe = new VirtualNetworkPipe(profile),
                Clock = new VirtualTransportClock(),
                Host = new LocalMatchHost()
            };

            var serverAddress = rig.Pipe.CreateEndpoint();
            rig.ServerCarrier = OwnDatagramCarrier.CreateServer(
                rig.Pipe, serverAddress, rig.Clock);
            rig.ServerTransport = new ServerTransportHost(
                rig.ServerCarrier, rig.Host.Sessions, rig.Host.Server);

            rig.Match = rig.Host.CreateSessionMatch(capacity);
            if (autoJoin)
            {
                rig.ServerTransport.AutoJoinMatch = rig.Match;
            }

            rig.ServerTransport.StartServer();
            return rig;
        }

        public static ClientTransportEndpoint CreateClient(Rig rig)
        {
            var clientAddress = rig.Pipe.CreateEndpoint();
            var carrier = OwnDatagramCarrier.CreateClient(
                rig.Pipe, clientAddress, ServerAddressOf(rig), rig.Clock);
            var endpoint = new ClientTransportEndpoint(carrier);
            endpoint.Connect("virtual", 0);
            return endpoint;
        }

        public static int ServerAddressOf(Rig rig)
        {
            // The server endpoint address is the first one created by the rig.
            return 1;
        }

        public static MatchConfig BuildMatchConfig(params PlayerId[] owners)
        {
            var specs = new UnitSpawnSpec[owners.Length];
            for (var index = 0; index < owners.Length; index++)
            {
                specs[index] = new UnitSpawnSpec(
                    owners[index],
                    new WorldPointMm(index * 20000, 0),
                    350,
                    false);
            }

            return new MatchConfig(StandardStats, specs);
        }

        /// <summary>Pumps both sides until the client reaches Established or the budget ends.</summary>
        public static bool PumpUntilEstablished(
            Rig rig,
            ClientTransportEndpoint client,
            long budgetMs = 5000,
            long stepMs = 5)
        {
            var elapsed = 0L;
            while (elapsed < budgetMs)
            {
                rig.Pump(stepMs);
                client.Pump(rig.Clock.NowMs);
                if (client.State == ClientTransportState.Established)
                {
                    return true;
                }

                elapsed += stepMs;
            }

            return client.State == ClientTransportState.Established;
        }
    }
}
