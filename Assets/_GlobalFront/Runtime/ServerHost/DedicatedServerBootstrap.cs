using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;
using UnityEngine;

namespace GlobalFront.DedicatedServer
{
    /// <summary>
    /// Process entry point of the dedicated server (OD-35). A build whose
    /// scripting define includes <c>UNITY_SERVER</c> always starts one; any
    /// other build starts one only when <c>-dedicatedServer</c> is passed on
    /// the command line, which keeps local listen-server experiments and
    /// headless test runs possible without a second player build.
    ///
    /// Supported arguments: <c>-port &lt;int&gt;</c> (default 7777),
    /// <c>-tickrate &lt;int&gt;</c> (default 20) and <c>-topology 1v1|2v2</c>
    /// (M2 remediation P1-01, default 1v1). Invalid values fall back to
    /// the default instead of aborting startup.
    /// </summary>
    public static class DedicatedServerBootstrap
    {
        public const int DefaultPort = 7777;
        public const string DedicatedServerFlag = "-dedicatedServer";
        public const string PortFlag = "-port";
        public const string TickRateFlag = "-tickrate";
        public const string TopologyFlag = "-topology";
        public const string Topology1v1Value = "1v1";
        public const string Topology2v2Value = "2v2";

#if UNITY_SERVER
        private const bool IsServerBuild = true;
#else
        private const bool IsServerBuild = false;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Launch()
        {
            var args = Environment.GetCommandLineArgs();
            if (!IsServerBuild && !IsDedicatedServerRequested(args))
            {
                return;
            }

            var runnerObject = new GameObject("[GlobalFront Dedicated Server]");
            UnityEngine.Object.DontDestroyOnLoad(runnerObject);
            var runner = runnerObject.AddComponent<DedicatedServerRunner>();

            // M2 remediation (P1-01): the topology must be known before
            // Initialize — the combat relation is fixed at host construction
            // and the lobby waits for the topology's player count.
            runner.TopologyMode = ParseTopology(args);
            runner.Initialize(ParsePort(args), ParseTickRate(args));
        }

        /// <summary>True when the command line carries <c>-dedicatedServer</c>.</summary>
        public static bool IsDedicatedServerRequested(string[] args) =>
            HasFlag(args, DedicatedServerFlag);

        /// <summary>Parses <c>-port</c>; a missing or invalid value yields the fallback.</summary>
        public static int ParsePort(string[] args, int fallback = DefaultPort) =>
            ParseBoundedInt(args, PortFlag, fallback, 1, 65535);

        /// <summary>Parses <c>-tickrate</c>; a missing or invalid value yields the fallback.</summary>
        public static int ParseTickRate(
            string[] args,
            int fallback = SimulationConstants.ServerTickRate) =>
            ParseBoundedInt(args, TickRateFlag, fallback, 1, 1000);

        /// <summary>
        /// M2 remediation (P1-01): parses <c>-topology</c> — accepts
        /// <c>-topology 2v2</c> and <c>-topology=2v2</c>, case-insensitively.
        /// A missing, malformed or unknown value yields the 1v1 skirmish
        /// default; the first occurrence wins.
        /// </summary>
        public static MatchTopologyMode ParseTopology(
            string[] args,
            MatchTopologyMode fallback = MatchTopologyMode.Skirmish1v1)
        {
            if (args == null)
            {
                return fallback;
            }

            var prefix = TopologyFlag + "=";
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                string value = null;

                if (string.Equals(argument, TopologyFlag, StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 < args.Length)
                    {
                        value = args[index + 1];
                    }
                }
                else if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = argument.Substring(prefix.Length);
                }

                if (value == null)
                {
                    continue;
                }

                if (string.Equals(value, Topology2v2Value, StringComparison.OrdinalIgnoreCase))
                {
                    return MatchTopologyMode.Team2v2;
                }

                if (string.Equals(value, Topology1v1Value, StringComparison.OrdinalIgnoreCase))
                {
                    return MatchTopologyMode.Skirmish1v1;
                }

                return fallback;
            }

            return fallback;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null)
            {
                return false;
            }

            for (var index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int ParseBoundedInt(
            string[] args,
            string flag,
            int fallback,
            int minValue,
            int maxValue)
        {
            if (args == null)
            {
                return fallback;
            }

            var prefix = flag + "=";
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                string value = null;

                if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 < args.Length)
                    {
                        value = args[index + 1];
                    }
                }
                else if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = argument.Substring(prefix.Length);
                }

                if (value == null)
                {
                    continue;
                }

                if (int.TryParse(value, out var parsed) &&
                    parsed >= minValue &&
                    parsed <= maxValue)
                {
                    return parsed;
                }

                return fallback;
            }

            return fallback;
        }
    }
}
