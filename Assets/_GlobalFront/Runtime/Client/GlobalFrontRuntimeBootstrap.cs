using GlobalFront.Client.Presentation;
using UnityEngine;

namespace GlobalFront.Client
{
    public static class GlobalFrontRuntimeBootstrap
    {
        private const string RuntimeRootName = "[GlobalFront Runtime]";
        private const string PrototypeWorldName = "[GlobalFront Prototype World]";

        /// <summary>Name of the GameObject that carries the Phase 3 vertical slice.</summary>
        public const string TacticalSliceRootName = "[GlobalFront Tactical Vertical Slice]";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            Application.runInBackground = true;

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
        /// </param>
        /// <param name="createVisuals">Build the battlefield and unit meshes.</param>
        /// <returns>The running slice.</returns>
        public static TacticalVerticalSliceRunner ActivateTacticalVerticalSlice(
            int unitCount = TacticalVerticalSliceRunner.DefaultUnitCount,
            bool issueInitialConvergenceMarch = true,
            bool createVisuals = true)
        {
            var previous = GameObject.Find(TacticalSliceRootName);
            if (previous != null)
            {
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
