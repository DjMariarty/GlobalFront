using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GlobalFront.Client
{
    /// <summary>
    /// Player input service and abstraction layer for RTS controls (Phase 3, Step 3.1).
    /// Provides unified access to pan, zoom, rotation, and ground-plane raycasting.
    /// Supports a mock mode for headless and EditMode testing without physical input devices.
    /// </summary>
    [DisallowMultipleComponent]
    public class RtsInputManager : MonoBehaviour
    {
        public static RtsInputManager Instance { get; private set; }

        /// <summary>
        /// Headless/EditMode input injection. Deliberately not serialized: a
        /// <c>[SerializeField]</c> here is a loaded gun — tick it while debugging, save
        /// the scene, and the release build stops reading keyboard and mouse entirely,
        /// with nothing in the code to explain why. Set it from a test or a debug build.
        /// </summary>
        [NonSerialized] private bool isMockMode;

        /// <summary>
        /// The one ground plane the client raycasts against: the playfield is flat at
        /// Y = 0 by contract (OD-27), so it never changes and is allocated once.
        /// Constructing it per pointer query would put a heap allocation in the code
        /// path every click and hover runs through.
        /// </summary>
        private static readonly Plane GroundPlane = new Plane(Vector3.up, Vector3.zero);

        // Mock state
        private Vector2 _mockMoveInput;
        private float _mockZoomInput;
        private float _mockRotationInput;
        private bool _mockIsRotating;
        private bool _mockResetRotation;
        private Vector2 _mockMousePosition;
        private bool _mockLeftButtonDown;
        private bool _mockLeftButtonPressed;
        private bool _mockLeftButtonUp;
        private bool _mockRightButtonDown;
        private bool _mockRightButtonPressed;
        private bool _mockRightButtonUp;

        // Hardware state
        private Vector2 _moveInput;
        private float _zoomInput;
        private float _rotationInput;
        private bool _isRotating;
        private bool _resetRotation;
        private Vector2 _mousePosition;
        private bool _leftButtonDown;
        private bool _leftButtonPressed;
        private bool _leftButtonUp;
        private bool _rightButtonDown;
        private bool _rightButtonPressed;
        private bool _rightButtonUp;

        [Header("Target Camera")]
        [SerializeField] private Camera targetCamera;

        private Camera _cachedActiveCamera;
        private bool _cameraSearchWarningLogged;

        public bool IsMockMode
        {
            get => isMockMode;
            set => isMockMode = value;
        }

        public Vector2 MoveInput => isMockMode ? _mockMoveInput : _moveInput;
        public float ZoomInput => isMockMode ? _mockZoomInput : _zoomInput;
        public float RotationInput => isMockMode ? _mockRotationInput : _rotationInput;
        public bool IsRotating => isMockMode ? _mockIsRotating : _isRotating;
        public bool ResetRotationRequested => isMockMode ? _mockResetRotation : _resetRotation;
        public Vector2 MousePosition => isMockMode ? _mockMousePosition : _mousePosition;

        public bool IsLeftMouseButtonDown => isMockMode ? _mockLeftButtonDown : _leftButtonDown;
        public bool IsLeftMouseButtonPressed => isMockMode ? _mockLeftButtonPressed : _leftButtonPressed;
        public bool IsLeftMouseButtonUp => isMockMode ? _mockLeftButtonUp : _leftButtonUp;

        public bool IsRightMouseButtonDown => isMockMode ? _mockRightButtonDown : _rightButtonDown;
        public bool IsRightMouseButtonPressed => isMockMode ? _mockRightButtonPressed : _rightButtonPressed;
        public bool IsRightMouseButtonUp => isMockMode ? _mockRightButtonUp : _rightButtonUp;

        /// <summary>
        /// Raycast world position of the mouse on the ground plane (Y = 0).
        /// </summary>
        public Vector3 MouseWorldPosition
        {
            get
            {
                TryGetMouseWorldPosition(GetActiveCamera(), out var worldPos);
                return worldPos;
            }
        }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
#if !UNITY_SERVER
            ManualUpdate(Time.unscaledDeltaTime);
#endif
        }

        /// <summary>
        /// Manually samples input. Callable deterministically from tests and Update().
        /// </summary>
        public void ManualUpdate(float deltaTime)
        {
            if (isMockMode)
            {
                return;
            }

            ReadHardwareInput();
        }

        private void ReadHardwareInput()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;

            // Pan / Movement (WASD + Arrow keys)
            var move = Vector2.zero;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move.y += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move.y -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move.x += 1f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move.x -= 1f;
            }

            if (move.sqrMagnitude > 1f)
            {
                move.Normalize();
            }
            _moveInput = move;

            if (mouse != null)
            {
                _mousePosition = mouse.position.ReadValue();

                // Zoom: scroll wheel Y
                var scroll = mouse.scroll.ReadValue().y;
                _zoomInput = Mathf.Abs(scroll) > 0.001f ? Mathf.Sign(scroll) : 0f;

                // Rotation: middle mouse button, or Alt held together with the right
                // button. Alt alone used to grab the camera, which turned any Alt-tab
                // away from the game (or a stray Alt keypress during a move) into a
                // spin of the whole view — see IsRotationCombination.
                var mmbPressed = mouse.middleButton.isPressed;
                var rmbPressed = mouse.rightButton.isPressed;
                var altPressed = keyboard != null &&
                                 (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
                _isRotating = IsRotationCombination(mmbPressed, rmbPressed, altPressed);
                _rotationInput = _isRotating ? mouse.delta.ReadValue().x : 0f;

                // Mouse buttons
                _leftButtonDown = mouse.leftButton.wasPressedThisFrame;
                _leftButtonPressed = mouse.leftButton.isPressed;
                _leftButtonUp = mouse.leftButton.wasReleasedThisFrame;

                _rightButtonDown = mouse.rightButton.wasPressedThisFrame;
                _rightButtonPressed = mouse.rightButton.isPressed;
                _rightButtonUp = mouse.rightButton.wasReleasedThisFrame;
            }
            else
            {
                _mousePosition = Vector2.zero;
                _zoomInput = 0f;
                _rotationInput = 0f;
                _isRotating = false;
                _leftButtonDown = false;
                _leftButtonPressed = false;
                _leftButtonUp = false;
                _rightButtonDown = false;
                _rightButtonPressed = false;
                _rightButtonUp = false;
            }

            // Quick reset orientation to North (Home or Backquote / ~)
            if (keyboard != null)
            {
                _resetRotation = keyboard.homeKey.wasPressedThisFrame || keyboard.backquoteKey.wasPressedThisFrame;
            }
            else
            {
                _resetRotation = false;
            }
        }

        /// <summary>
        /// The whole rotation gesture rule, as data rather than as hardware state.
        ///
        /// Alt used to be enough on its own, so an Alt press — the modifier a player
        /// hits when tabbing out — rotated the battlefield while the cursor travelled
        /// across the screen. Middle button alone still rotates; Alt is now only a
        /// fallback and needs the right button held with it.
        ///
        /// Exposed as a pure function because the hardware path cannot be exercised in
        /// EditMode (mock mode short-circuits it, and the test assembly does not
        /// reference the Input System): this is the rule the failing case is about, and
        /// it is testable exactly where it is written.
        /// </summary>
        public static bool IsRotationCombination(
            bool middleButtonPressed,
            bool rightButtonPressed,
            bool altPressed) =>
            middleButtonPressed || (altPressed && rightButtonPressed);

        /// <summary>
        /// Projects a ray from the given camera through the current mouse position onto the ground plane (Y = 0).
        /// </summary>
        public bool TryGetMouseWorldPosition(Camera cam, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            if (cam == null)
            {
                cam = GetActiveCamera();
                if (cam == null)
                {
                    return false;
                }
            }

            var screenPos = MousePosition;
            var ray = cam.ScreenPointToRay(screenPos);

            if (GroundPlane.Raycast(ray, out var enterDistance))
            {
                worldPosition = ray.GetPoint(enterDistance);
                return true;
            }

            return false;
        }

        public Vector3 GetMouseWorldPosition(Camera cam)
        {
            TryGetMouseWorldPosition(cam, out var worldPos);
            return worldPos;
        }

        /// <summary>
        /// Resolves the camera pointer queries fall back to, and remembers it. The old
        /// version asked <c>Camera.main</c> twice and then ran
        /// <c>FindAnyObjectByType</c> — a full scene scan on every hover and
        /// every click, whenever the tag lookup came up empty. A destroyed camera reads
        /// as null to Unity's comparison, so the cache refreshes itself on the next call.
        /// </summary>
        private Camera GetActiveCamera()
        {
            if (targetCamera != null)
            {
                return targetCamera;
            }

            if (_cachedActiveCamera != null)
            {
                return _cachedActiveCamera;
            }

            if (Camera.main != null)
            {
                _cachedActiveCamera = Camera.main;
                return _cachedActiveCamera;
            }

            _cachedActiveCamera = FindAnyObjectByType<Camera>();
            if (_cachedActiveCamera == null && !_cameraSearchWarningLogged)
            {
                _cameraSearchWarningLogged = true;
                Debug.LogWarning($"{nameof(RtsInputManager)}: no active camera found in scene.");
            }

            return _cachedActiveCamera;
        }

        /// <summary>
        /// Overrides the camera pointer queries project through. Clears the fallback
        /// cache with it: a stale camera kept past a target or scene change would go on
        /// raycasting from the view the caller just replaced.
        /// </summary>
        public void SetTargetCamera(Camera cam)
        {
            targetCamera = cam;
            _cachedActiveCamera = cam;
        }

        #region Mock Configuration Methods

        public void SetMockMode(bool enabled)
        {
            isMockMode = enabled;
        }

        public void SetMockMoveInput(Vector2 moveInput)
        {
            _mockMoveInput = moveInput.sqrMagnitude > 1f ? moveInput.normalized : moveInput;
        }

        public void SetMockZoomInput(float zoomInput)
        {
            _mockZoomInput = zoomInput;
        }

        public void SetMockRotationInput(float rotationInput)
        {
            _mockRotationInput = rotationInput;
        }

        public void SetMockIsRotating(bool isRotating)
        {
            _mockIsRotating = isRotating;
        }

        public void SetMockResetRotation(bool resetRequested)
        {
            _mockResetRotation = resetRequested;
        }

        public void SetMockMousePosition(Vector2 screenPosition)
        {
            _mockMousePosition = screenPosition;
        }

        public void SetMockMouseButton(int button, bool down, bool pressed, bool up)
        {
            if (button == 0)
            {
                _mockLeftButtonDown = down;
                _mockLeftButtonPressed = pressed;
                _mockLeftButtonUp = up;
            }
            else if (button == 1)
            {
                _mockRightButtonDown = down;
                _mockRightButtonPressed = pressed;
                _mockRightButtonUp = up;
            }
        }

        public void ResetMockInputs()
        {
            _mockMoveInput = Vector2.zero;
            _mockZoomInput = 0f;
            _mockRotationInput = 0f;
            _mockIsRotating = false;
            _mockResetRotation = false;
            _mockMousePosition = Vector2.zero;
            _mockLeftButtonDown = false;
            _mockLeftButtonPressed = false;
            _mockLeftButtonUp = false;
            _mockRightButtonDown = false;
            _mockRightButtonPressed = false;
            _mockRightButtonUp = false;
        }

        #endregion
    }
}
