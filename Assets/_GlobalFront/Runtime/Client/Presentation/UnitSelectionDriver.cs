using GlobalFront.Client.UI;
using UnityEngine;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// Runs one frame of selection for the local player: reads the pointer, keeps the
    /// selection honest, resolves clicks and drags, and draws the marquee (Phase 3,
    /// step 3.5).
    ///
    /// This is the seam that keeps <see cref="UnitSelectionController"/> free of
    /// Unity's frame loop. The controller takes a position, a projection and an
    /// explicit clock so a test can drive a double-click at 0.29 seconds and a
    /// 400-unit drag without waiting for either; this component is what supplies those
    /// arguments in a running game, and it deliberately decides nothing of its own.
    ///
    /// Commands go through <see cref="IUnitCommandSink"/> rather than straight to an
    /// <see cref="ICommandChannel"/> because this layer has no business knowing the
    /// tick window, the session attribution or where the match is hosted. The loop
    /// owner sets <see cref="RequestedTick"/> on the frame it wants commands to land
    /// on, which is the same contract
    /// <see cref="PrototypeRtsController"/> already keeps with
    /// <c>CurrentTick + 1</c> (ADR-007).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitSelectionDriver : MonoBehaviour
    {
        [Header("Wiring (optional; assign from the scene or in code)")]
        [SerializeField] private RtsInputManager inputManager;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private SelectionMarqueePresenter marqueePresenter;
        [SerializeField] private MinimapInteractionController minimapInteraction;

        private UnitSelectionController _controller;
        private IUnitCommandSink _sink;
        private IUnitPointerProjector _projector;
        private TacticalHudPointerBlocker _hudBlocker;

        /// <summary>
        /// Tick the next issued command asks for. Assigned by the loop owner, which is
        /// the only place that knows the authoritative tick.
        /// </summary>
        public ulong RequestedTick { get; set; }

        /// <summary>The selection state this driver advances.</summary>
        public UnitSelectionController Controller => _controller;

        /// <summary>True once a controller has been supplied.</summary>
        public bool IsConfigured => _controller != null;

        /// <summary>
        /// Projection the pointer queries go through. Assignable so a test can drive a
        /// marquee from an exact mapping instead of reverse-engineering one out of a
        /// camera; left null in a real scene, where <see cref="SetCamera"/> or the
        /// serialized camera supplies the camera-backed one on the first frame.
        /// </summary>
        public IUnitPointerProjector PointerProjector
        {
            get => _projector;
            set => _projector = value;
        }

        public void SetInputManager(RtsInputManager manager) => inputManager = manager;

        /// <summary>
        /// Re-points the projection at a different camera. Selection is deliberately
        /// untouched: moving the viewpoint does not change which units the player has
        /// asked to move.
        ///
        /// An existing <see cref="CameraUnitPointerProjector"/> is retargeted rather
        /// than replaced. A camera swap is not a rare event — a reconnect, a scene
        /// reload, the driver being handed the rig after the bootstrap has already run
        /// — and the projector the frame loop holds is the same object every pointer
        /// query goes through, so allocating a new one per swap puts a heap object in the
        /// path the drag walks down.
        /// </summary>
        public void SetCamera(Camera camera)
        {
            targetCamera = camera;

            if (camera != null && _projector is CameraUnitPointerProjector cameraProjector)
            {
                cameraProjector.SetCamera(camera);
                return;
            }

            _projector = camera != null ? new CameraUnitPointerProjector(camera) : null;
        }

        /// <summary>
        /// Supplies the selection state and where its commands go. Either may be null,
        /// in which case the corresponding half of the frame is skipped — a scene that
        /// is presenting but not yet playable has a binder and no session, and the
        /// driver must stay quiet rather than throw on the first frame.
        /// </summary>
        public void Configure(UnitSelectionController controller, IUnitCommandSink sink)
        {
            _controller = controller;
            _sink = sink;
        }

        /// <summary>
        /// Supplies the marquee renderer. Optional by the same rule as the rest of the
        /// wiring: a build with no HUD scene still selects and still commands, it just
        /// draws no box.
        /// </summary>
        public void SetMarqueePresenter(SelectionMarqueePresenter presenter) =>
            marqueePresenter = presenter;

        /// <summary>
        /// Supplies the rectangles the tactical HUD owns, so a pointer over the
        /// minimap or a HUD panel drives the panel and nothing else (step 3.6).
        ///
        /// Null by default, and null means no gate: a build with no HUD still selects
        /// and still commands on every pixel, exactly as it did before the minimap
        /// existed. The gate has to be opt-in rather than assumed, because a stale or
        /// mis-sized blocking rectangle is worse than no gate at all — it makes the
        /// part of the screen it covers unclickable, and the player has no way to find
        /// out which panel is lying.
        /// </summary>
        public void SetHudBlocker(TacticalHudPointerBlocker blocker) => _hudBlocker = blocker;

        /// <summary>Where the driver refuses battlefield pointer input, if it refuses anywhere.</summary>
        public TacticalHudPointerBlocker HudBlocker => _hudBlocker;

        /// <summary>
        /// Supplies the minimap the radar bindings belong to (step 3.6). Optional by the
        /// same rule as the rest of the wiring: with no minimap the driver is exactly the
        /// step 3.5 driver, and the left button stays the marquee everywhere.
        ///
        /// The driver, not the radar component, owns the pointer sample because there is
        /// only one mouse. Two components each reading <c>Input.mousePosition</c> in
        /// their own <c>Update</c> would disagree on any frame the pointer moved, and a
        /// press that the battlefield armed but only the radar released leaves a drag
        /// that never ends.
        /// </summary>
        public void SetMinimapInteraction(MinimapInteractionController interaction) =>
            minimapInteraction = interaction;

        /// <summary>The minimap this driver routes panel clicks to, if it has one.</summary>
        public MinimapInteractionController MinimapInteraction => minimapInteraction;

        private void Update()
        {
            ManualUpdate(Time.unscaledTimeAsDouble);
        }

        /// <summary>
        /// Advances one presentation frame. Deterministic entry point shared with
        /// <see cref="Update"/>, mirroring <c>RtsCameraController.ManualUpdate</c>.
        /// </summary>
        public void ManualUpdate(double nowSeconds)
        {
            if (_controller == null || inputManager == null)
            {
                return;
            }

            if (_projector == null && targetCamera != null)
            {
                _projector = new CameraUnitPointerProjector(targetCamera);
            }

            var pointer = inputManager.MousePosition;
            var additive = inputManager.IsShiftPressed;

            // Prune before the pointer query, not after: a unit that died last frame
            // must not be the one this frame's hover reports, and the overlay batch is
            // built by a pass that runs after this one. Also deliberately before the
            // HUD gate below — a squad does not stop dying because the player's mouse
            // is on the radar, and a selection the controller stops maintaining over a
            // panel is one that commands dead units the moment the pointer leaves it.
            _controller.PruneStaleSelection();

            // Re-read the panels before asking whether the pointer is on one: a HUD
            // that has been resized, scrolled or switched since the last frame would
            // otherwise block the pixel it occupied then. With nothing registered this
            // walks an empty array, so the cost is not paid by builds that have no HUD.
            _hudBlocker?.Refresh();

            // Which half of the screen the pointer is over is decided once, here, and
            // every battlefield input below reads the same answer. Deciding it per call
            // site would let the press and the release disagree about a pixel on a
            // panel border, which is a drag that never ends.
            var overHud = _hudBlocker != null && _hudBlocker.IsPointerOverHud(pointer);

            if (minimapInteraction != null)
            {
                minimapInteraction.RequestedTick = RequestedTick;

                // The radar refuses a pixel outside its own widget by itself, so a press
                // here is a question the minimap answers rather than a binding that
                // steals the marquee: battlefield pixels fall straight through to the
                // branch below.
                if (inputManager.IsLeftMouseButtonDown)
                {
                    minimapInteraction.HandleLeftPointerDown(pointer);
                }
                else if (inputManager.IsLeftMouseButtonPressed &&
                         minimapInteraction.IsDraggingCameraOnMinimap)
                {
                    minimapInteraction.HandleLeftPointerDrag(pointer);
                }

                if (inputManager.IsLeftMouseButtonUp &&
                    minimapInteraction.IsDraggingCameraOnMinimap)
                {
                    minimapInteraction.HandleLeftPointerUp(pointer);
                }

                // The right button is the one that has to be gated on the panel: outside
                // the HUD it already means "move the squad to that ground point", and a
                // radar that answered the same click would issue a second move to the
                // map coordinate behind the widget.
                if (inputManager.IsRightMouseButtonUp && overHud)
                {
                    minimapInteraction.HandleRightPointerUp(pointer);
                }
            }

            if (overHud)
            {
                // A held press that travelled into a panel is abandoned rather than
                // resolved, and abandoning leaves the selection exactly as the battlefield
                // clicks left it. The release edge is tested alongside IsDragging because a
                // pointer can teleport from the terrain onto the radar inside one frame: without
                // an in-flight drag to notice, that press would stay armed for the rest of the
                // match and resolve later as a marquee whose origin is a pixel the player pressed
                // on top of the minimap.
                if (_controller.IsDragging || inputManager.IsLeftMouseButtonUp)
                {
                    _controller.CancelDrag();
                }

                if (_controller.HasHoveredUnit)
                {
                    _controller.ClearHover();
                }
            }
            else
            {
                // Press, travel, release, in that order, so all three can be observed inside
                // one frame: a player who clicks without the frame rate noticing has pressed
                // and released between two Updates.
                if (inputManager.IsLeftMouseButtonDown)
                {
                    _controller.BeginDrag(pointer);
                }

                _controller.UpdatePointer(pointer, _projector);

                if (inputManager.IsLeftMouseButtonUp)
                {
                    _controller.ResolveRelease(pointer, additive, nowSeconds, _projector);
                }

                if (inputManager.IsRightMouseButtonUp)
                {
                    _controller.TryIssueCommandAtPointer(pointer, _projector, RequestedTick, _sink);
                }
            }

            // The stop hotkey is not a pointer action, so the HUD gate does not apply
            // to it: a player whose selection is on screen and whose mouse happens to
            // be resting on the radar still expects X to halt the squad.
            if (inputManager.StopRequested)
            {
                _controller.IssueStop(RequestedTick, _sink);
            }

            if (marqueePresenter != null)
            {
                marqueePresenter.Present(_controller.IsDragging, _controller.ScreenRectPx);
            }
        }
    }
}
