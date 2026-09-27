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

        private UnitSelectionController _controller;
        private IUnitCommandSink _sink;
        private IUnitPointerProjector _projector;

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
            // built by a pass that runs after this one.
            _controller.PruneStaleSelection();

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
