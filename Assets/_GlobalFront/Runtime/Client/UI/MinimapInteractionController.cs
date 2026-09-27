using GlobalFront.Client.Presentation;
using GlobalFront.Core.Movement;
using UnityEngine;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// Turns a pointer on the minimap into a camera move or an order
    /// (Phase 3, step 3.6, ADR-012/OD-24).
    ///
    /// Generals and Zero Hour gave the radar two bindings and this keeps them: the
    /// left button looks, the right button sends. A third would be a third thing to
    /// explain — the left button also has to keep working as the marquee drag, so the
    /// two are separated by the panel gate the HUD registers with
    /// <see cref="TacticalHudPointerBlocker"/> rather than by a modifier key.
    ///
    /// <b>Nothing here decides a rule it can borrow.</b> Bounds, clamping and the
    /// refusal of a NaN pixel come from <see cref="MinimapProjection"/>; whether a
    /// move order is legal and who may issue it comes from
    /// <see cref="UnitSelectionController.IssueMove"/>, which already refuses an empty
    /// selection, an invalid local player and a destination outside the simulation's
    /// coordinate range. What is specific to the minimap is only the mapping and the
    /// order in which the two buttons are tried.
    ///
    /// <b>Refusal is the whole design.</b> Every handler answers with a bool and
    /// changes nothing on <see langword="false"/>, because the caller is a frame loop
    /// that cannot tell a refused click from an accepted one by looking at the world:
    /// a right-click beside the radar that still moved the squad, or a click through a
    /// HUD panel that repositioned the camera, are both invisible until the player
    /// finds out the hard way.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MinimapInteractionController : MonoBehaviour
    {
        [Header("Wiring (optional; assign from the scene or in code)")]
        [SerializeField] private RtsCameraController cameraController;

        // The radar model and the selection controller are plain classes, so Unity cannot
        // serialise a reference to either: an inspector slot here would always read "None"
        // and invite someone to conclude the wiring was broken. Both arrive through
        // Configure, the same way UnitSelectionDriver receives its controller.
        private MinimapRadarModel _radar;
        private UnitSelectionController _selection;

        private IUnitCommandSink _sink;
        private Rect _minimapRectPx;
        private bool _hasRect;
        private bool _leftPointerDown;

        /// <summary>
        /// Tick the next order asks for, assigned by the loop owner on the frame it
        /// wants it to land on — the same contract
        /// <see cref="UnitSelectionDriver.RequestedTick"/> keeps, restated here because
        /// a radar order and a battlefield order are issued by different components and
        /// neither should have to know the other exists.
        /// </summary>
        public ulong RequestedTick { get; set; }

        /// <summary>
        /// Whether a left press jumps the camera to the clicked point immediately or
        /// pans it there over the following frames. True by default: on a 400 m map a
        /// pan to the far corner reads as lag, and Generals teleported the view for
        /// precisely that reason.
        /// </summary>
        public bool LeftClickJumpsCamera { get; set; } = true;

        /// <summary>True while a left press that began on the minimap is still held.</summary>
        public bool IsDraggingCameraOnMinimap => _leftPointerDown;

        /// <summary>World position the last accepted left-click pointed at, or zero.</summary>
        public Vector3 LastCameraTargetMetres { get; private set; }

        /// <summary>Destination of the last accepted right-click, in millimetres.</summary>
        public WorldPointMm LastMoveDestinationMm { get; private set; }

        /// <summary>Whether a right-click on the radar is turned into a move order at all.</summary>
        public bool RightClickIssuesMove { get; set; } = true;

        /// <summary>The radar whose bounds the mappings are taken from.</summary>
        public MinimapRadarModel Radar => _radar;

        public void Configure(
            MinimapRadarModel radarModel,
            RtsCameraController camera,
            UnitSelectionController selectionController,
            IUnitCommandSink sink)
        {
            _radar = radarModel;
            cameraController = camera;
            _selection = selectionController;
            _sink = sink;
        }

        /// <summary>
        /// Declares the minimap's rectangle on screen, in the same bottom-left screen
        /// pixels <see cref="TacticalHudPointerBlocker"/> registers. Until this is set
        /// every handler refuses: guessing a rectangle from a canvas that has not been
        /// laid out yet would put clicks somewhere the player did not click.
        ///
        /// A corrupt rectangle is refused rather than stored. An infinite or overflowed
        /// extent makes <see cref="MinimapProjection.TryScreenToUv"/> answer for pixels
        /// across half the screen, which is a radar that pans the camera no matter where
        /// the player clicks, and <see cref="MinimapScreenRect"/> would hand the same
        /// corrupt rectangle to whoever asked.
        /// </summary>
        public void SetMinimapScreenRect(Rect minimapRectPx)
        {
            var isUsable =
                UnitPickMath.IsFinite(minimapRectPx.x) && UnitPickMath.IsFinite(minimapRectPx.y) &&
                UnitPickMath.IsFinite(minimapRectPx.width) && UnitPickMath.IsFinite(minimapRectPx.height) &&
                UnitPickMath.IsFinite(minimapRectPx.xMax) && UnitPickMath.IsFinite(minimapRectPx.yMax) &&
                minimapRectPx.width > 0f && minimapRectPx.height > 0f;

            if (!isUsable)
            {
                _minimapRectPx = default;
                _hasRect = false;
                return;
            }

            _minimapRectPx = minimapRectPx;
            _hasRect = true;
        }

        /// <summary>The rectangle the radar answers clicks inside, if one has been declared.</summary>
        public Rect MinimapScreenRect => _hasRect ? _minimapRectPx : default;

        /// <summary>
        /// Screen pixel to radar UV. <see langword="false"/> for a non-finite pixel or
        /// one outside the widget, so a caller can fall back to whatever owns that
        /// pixel.
        /// </summary>
        public bool TryMapScreenToUv(Vector2 screenPixels, out Vector2 uv)
        {
            uv = default;
            if (!_hasRect || _radar == null)
            {
                return false;
            }

            return MinimapProjection.TryScreenToUv(_minimapRectPx, screenPixels, out uv) ==
                   MinimapMappingResult.Ok;
        }

        /// <summary>Screen pixel to a world point on the ground plane, in metres.</summary>
        public bool TryMapScreenToWorldMetres(Vector2 screenPixels, out Vector3 worldMetres)
        {
            worldMetres = Vector3.zero;
            if (!TryMapScreenToUv(screenPixels, out var uv))
            {
                return false;
            }

            return MinimapProjection.TryUvToWorldMetres(_radar.Bounds, uv, out worldMetres) ==
                   MinimapMappingResult.Ok;
        }

        /// <summary>Screen pixel to the millimetre point a command would carry.</summary>
        public bool TryMapScreenToMillimetres(Vector2 screenPixels, out WorldPointMm point)
        {
            point = default;
            if (!TryMapScreenToUv(screenPixels, out var uv))
            {
                return false;
            }

            return MinimapProjection.TryUvToMillimetres(_radar.Bounds, uv, out point) ==
                   MinimapMappingResult.Ok;
        }

        /// <summary>
        /// The left button went down on the radar: look there.
        /// </summary>
        /// <returns>True when the click was on the radar and the camera was retargeted.</returns>
        public bool HandleLeftPointerDown(Vector2 screenPixels)
        {
            if (!TryMapScreenToWorldMetres(screenPixels, out var world))
            {
                return false;
            }

            _leftPointerDown = true;
            LastCameraTargetMetres = world;
            PanCamera(world, LeftClickJumpsCamera);
            return true;
        }

        /// <summary>
        /// The left button travelled with the radar held down: keep looking, panning
        /// rather than jumping so a sweep across the map reads as a sweep.
        /// </summary>
        public bool HandleLeftPointerDrag(Vector2 screenPixels)
        {
            if (!_leftPointerDown || !TryMapScreenToWorldMetres(screenPixels, out var world))
            {
                return false;
            }

            LastCameraTargetMetres = world;
            PanCamera(world, false);
            return true;
        }

        /// <summary>
        /// The left button came up. Ends the drag; changes nothing else, because the
        /// camera is already where the last drag sample asked it to be.
        /// </summary>
        public bool HandleLeftPointerUp(Vector2 screenPixels)
        {
            if (!_leftPointerDown)
            {
                return false;
            }

            _leftPointerDown = false;

            // A release with no travel is the click, and the click already acted on
            // press. Honouring it again here would double the move, which with a
            // smoothed pan is not visible but with a jump re-snaps the camera to a
            // pixel the player had already left.
            return TryMapScreenToWorldMetres(screenPixels, out _);
        }

        /// <summary>
        /// The right button came up on the radar: send the selection there.
        /// </summary>
        /// <returns>True when an order was handed to the sink.</returns>
        public bool HandleRightPointerUp(Vector2 screenPixels)
        {
            if (!RightClickIssuesMove || !_hasRect || _radar == null || _selection == null || _sink == null)
            {
                return false;
            }

            if (!TryMapScreenToMillimetres(screenPixels, out var destination))
            {
                return false;
            }

            // IssueMove is the only gate that matters. It refuses a zero-count
            // selection, an invalid local player and a destination outside the wire's
            // coordinate range, all without touching the sink, so a right-click on the
            // radar with nothing selected emits nothing rather than emitting an order
            // the server would answer with NotEntityOwner.
            if (!_selection.IssueMove(destination, RequestedTick, _sink))
            {
                return false;
            }

            LastMoveDestinationMm = destination;
            return true;
        }

        /// <summary>Abandons an in-flight radar drag without acting on it, for a HUD teardown or a focus loss.</summary>
        public void CancelDrag() => _leftPointerDown = false;

        /// <summary>
        /// Unity's own teardown and focus hooks both end the drag. Neither can be left to
        /// the caller: a HUD that is switched off, or a client that loses focus mid-sweep,
        /// never sees another pointer sample, so the press stays armed. The next left
        /// button the player releases then pans the camera from wherever the pointer
        /// happens to be, for a click that was never on the radar.
        /// </summary>
        private void OnDisable() => CancelDrag();

        /// <inheritdoc cref="OnDisable"/>
        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                CancelDrag();
            }
        }

        private void PanCamera(Vector3 worldMetres, bool immediate)
        {
            if (cameraController == null)
            {
                return;
            }

            // SetFocusPoint is the camera's own door: it refuses a non-finite point,
            // clamps to the camera's map bounds and re-applies the transform. A minimap
            // that wrote the camera's transform directly would skip all three, and the
            // first thing it would lose is the clamp that keeps the view on the map.
            cameraController.SetFocusPoint(
                new Vector3(worldMetres.x, worldMetres.y, worldMetres.z),
                immediate);
        }
    }
}
