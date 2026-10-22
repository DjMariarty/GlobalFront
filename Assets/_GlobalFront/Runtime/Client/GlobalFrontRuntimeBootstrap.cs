using System;
using GlobalFront.Client.Presentation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GlobalFront.Client
{
    public static class GlobalFrontRuntimeBootstrap
    {
        private const string RuntimeRootName = "[GlobalFront Runtime]";
        private const string PrototypeWorldName = "[GlobalFront Prototype World]";

        /// <summary>Name of the GameObject that carries the Phase 3 vertical slice.</summary>
        public const string TacticalSliceRootName = "[GlobalFront Tactical Vertical Slice]";

        /// <summary>Command-line switch that boots a client against a remote server.</summary>
        public const string ConnectFlag = "-connect";

        /// <summary>Port assumed when <c>-connect</c> names a host without one.</summary>
        public const int DefaultConnectPort = 7777;

        private static RemoteSliceSession _dedicatedSession;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            Application.runInBackground = true;

            if (TryParseConnectEndpoint(
                    Environment.GetCommandLineArgs(),
                    out var host,
                    out var port))
            {
                ActivateDedicatedClient(host, port);
                return;
            }

            if (GameObject.Find(RuntimeRootName) != null)
            {
                return;
            }

            var root = new GameObject(RuntimeRootName);
            // Phase 2.3 (ADR-007): the authoritative tick loop no longer
            // lives on a client runner component. The TickDriver inside
            // LocalMatchHost owns the server tick schedule; the controller
            // pumps it from Update.
            root.AddComponent<PrototypeWorldBootstrap>();
            root.AddComponent<PrototypeRtsController>();
            root.AddComponent<PrototypeHud>();
            root.AddComponent<RtsInputManager>();

            var mainCamera = Camera.main != null
                ? Camera.main
                : Object.FindAnyObjectByType<Camera>();

            if (mainCamera == null)
            {
                var cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                mainCamera = cameraObject.AddComponent<Camera>();
            }

            mainCamera.nearClipPlane = 0.2f;
            mainCamera.farClipPlane = 600f;

            if (mainCamera.GetComponent<RtsCameraController>() == null)
            {
                mainCamera.gameObject.AddComponent<RtsCameraController>();
            }
        }

        /// <summary>
        /// Switches the running scene from the 40-unit legacy prototype to the
        /// Phase 3 400-unit tactical vertical slice (step 3.7, ADR-012 / OD-28).
        ///
        /// Scene load still boots the legacy prototype, and the PlayMode suite
        /// still asserts it is there: this is a request, not a replacement, so
        /// the prototype's world and its two controller components are taken
        /// down here rather than never built. <c>[GlobalFront Runtime]</c> itself
        /// stays, because the <see cref="RtsInputManager"/> it carries is the one
        /// the slice's camera rig and selection driver read.
        /// </summary>
        /// <param name="unitCount">Roster size for both armies together.</param>
        /// <param name="issueInitialConvergenceMarch">
        /// Order both armies toward the centre and then into each other, so the
        /// slice opens on a battle. Both halves are needed: a marched army has its
        /// automatic acquisition cleared by the move order, so converging without
        /// engaging parks two silent lines facing each other.
        ///
        /// False by default, because that is the state a player needs to be dropped
        /// into: the march destinations are 14 m apart and the server's automatic
        /// acquisition range is 18 m, so opening on a battle means the two armies
        /// annihilate each other in about half a minute, before the first order can be
        /// given. Standing still is genuinely quiet — the armies start 80 m apart in
        /// their own quadrants and only the enemy is autonomous — so the roster waits
        /// for orders untouched. Tests and the benchmark harness that want a fight ask
        /// for the march explicitly.
        /// </param>
        /// <param name="createVisuals">Build the battlefield and unit meshes.</param>
        /// <returns>The running slice.</returns>
        public static TacticalVerticalSliceRunner ActivateTacticalVerticalSlice(
            int unitCount = TacticalVerticalSliceRunner.DefaultUnitCount,
            bool issueInitialConvergenceMarch = false,
            bool createVisuals = true)
        {
            DestroyExistingSlice();
            DisposeDedicatedSession();
            DestroyLegacyPrototype();

            var root = new GameObject(TacticalSliceRootName);
            var runner = root.AddComponent<TacticalVerticalSliceRunner>();
            runner.Initialize(
                unitCount,
                createVisuals,
                autoStartRealTime: true,
                enemyAutoAcquire: true);

            if (issueInitialConvergenceMarch)
            {
                runner.IssueConvergenceMarch();
                runner.IssueEngagementOrders();
            }

            return runner;
        }

        /// <summary>
        /// Boots the tactical slice as a client of a remote dedicated server
        /// (Phase 4, step 4.2.1, OD-38). The session owns the connection: the
        /// server's accept, the replication stream and the command channel all
        /// arrive through it, and the runner presents what the server decided.
        ///
        /// No local host is created, so the legacy prototype is taken down
        /// first: it would otherwise put a second, locally simulated battle in
        /// front of the same camera.
        /// </summary>
        /// <param name="host">Server host name or address.</param>
        /// <param name="port">Server UDP port.</param>
        /// <param name="createVisuals">Build the battlefield and unit meshes.</param>
        /// <returns>The running slice.</returns>
        public static TacticalVerticalSliceRunner ActivateDedicatedClient(
            string host,
            int port,
            bool createVisuals = true)
        {
            DestroyExistingSlice();
            DisposeDedicatedSession();
            DestroyLegacyPrototype();

            var root = new GameObject(TacticalSliceRootName);
            var runner = root.AddComponent<TacticalVerticalSliceRunner>();

            var session = new RemoteSliceSession();
            _dedicatedSession = session;
            session.Connect(host, port);

            runner.InitializeDedicatedClient(session, createVisuals, autoStartRealTime: true);
            return runner;
        }

        /// <summary>
        /// Parses <c>-connect &lt;host&gt;[:&lt;port&gt;]</c> or
        /// <c>-connect=&lt;host&gt;[:&lt;port&gt;]</c>. A host without a port
        /// connects to <see cref="DefaultConnectPort"/>; a missing flag, an
        /// empty host or a port outside 1..65535 fails the parse.
        /// </summary>
        public static bool TryParseConnectEndpoint(string[] args, out string host, out int port)
        {
            host = null;
            port = 0;

            if (args == null)
            {
                return false;
            }

            var prefix = ConnectFlag + "=";
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                string endpoint = null;

                if (string.Equals(argument, ConnectFlag, StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 < args.Length)
                    {
                        endpoint = args[index + 1];
                    }
                }
                else if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    endpoint = argument.Substring(prefix.Length);
                }

                if (endpoint == null)
                {
                    continue;
                }

                return TryParseEndpoint(endpoint, out host, out port);
            }

            return false;
        }

        private static bool TryParseEndpoint(string endpoint, out string host, out int port)
        {
            host = null;
            port = 0;

            // A following switch is a missing value, not a host name.
            if (string.IsNullOrWhiteSpace(endpoint) || endpoint[0] == '-')
            {
                return false;
            }

            port = DefaultConnectPort;

            // LastIndexOf keeps an IPv6 literal such as [::1]:7777 intact.
            var separator = endpoint.LastIndexOf(':');
            if (separator >= 0)
            {
                var portText = endpoint.Substring(separator + 1);
                if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
                {
                    port = 0;
                    return false;
                }

                endpoint = endpoint.Substring(0, separator);
            }

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                port = 0;
                return false;
            }

            host = endpoint;
            return true;
        }

        private static void DestroyExistingSlice()
        {
            var previous = GameObject.Find(TacticalSliceRootName);
            if (previous == null)
            {
                return;
            }

            // Re-arming the slice replaces the battle it was running: two
            // hosts pacing two matches into one camera is not a switch,
            // and the runner refuses to initialize twice anyway. Destroy is
            // deferred to the end of a Play Mode frame, so the outgoing
            // runner is stopped and renamed on the way out: its Update must
            // not present ticks the new slice owns, and a second call in
            // this same frame must not find a dying root by name.
            var outgoing = previous.GetComponent<TacticalVerticalSliceRunner>();
            if (outgoing != null)
            {
                outgoing.Shutdown();
                outgoing.enabled = false;
            }

            previous.name = TacticalSliceRootName + " [Released]";
            DestroyContent(previous);
        }

        /// <summary>
        /// The bootstrap owns the session it created for the dedicated client,
        /// while the runner only borrows it. Replacing the slice has to close
        /// the connection the outgoing runner can no longer release.
        /// </summary>
        private static void DisposeDedicatedSession()
        {
            _dedicatedSession?.Dispose();
            _dedicatedSession = null;
        }

        private static void DestroyLegacyPrototype()
        {
            var world = GameObject.Find(PrototypeWorldName);
            if (world != null)
            {
                DestroyContent(world);
            }

            // Components, not their GameObject: these three live on
            // [GlobalFront Runtime] alongside the input manager, and
            // destroying that root would take the camera rig's input with it.
            DestroyLegacyComponent(Object.FindAnyObjectByType<PrototypeWorldBootstrap>());
            DestroyLegacyComponent(Object.FindAnyObjectByType<PrototypeRtsController>());
            DestroyLegacyComponent(Object.FindAnyObjectByType<PrototypeHud>());
        }

        /// <summary>
        /// Switched off before it is destroyed. In Play Mode a destroy runs at the end of
        /// the frame, so a component left enabled would spend the rest of this one
        /// registering units and redrawing a HUD for a battlefield the slice just replaced.
        /// </summary>
        private static void DestroyLegacyComponent(Behaviour component)
        {
            if (component == null)
            {
                return;
            }

            component.enabled = false;
            DestroyContent(component);
        }

        /// <summary>
        /// <see cref="Object.Destroy"/> while a frame is running, and
        /// <see cref="Object.DestroyImmediate"/> in EditMode, where there is no
        /// frame end to wait for and a deferred destroy would leave the old
        /// battlefield next to the new one for the rest of the test.
        /// </summary>
        private static void DestroyContent(Object content)
        {
            if (content == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(content);
            }
            else
            {
                Object.DestroyImmediate(content);
            }
        }
    }
}
