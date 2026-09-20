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

        [Header("Mock / Testing Mode")]
        [SerializeField] private bool isMockMode;

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

                // Rotation: Middle mouse button or Alt + horizontal mouse delta
                var altPressed = keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
                var mmbPressed = mouse.middleButton.isPressed;
                _isRotating = mmbPressed || altPressed;
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
            var groundPlane = new Plane(Vector3.up, Vector3.zero);

            if (groundPlane.Raycast(ray, out var enterDistance))
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

        private Camera GetActiveCamera()
        {
            if (targetCamera != null) return targetCamera;
            if (Camera.main != null) return Camera.main;
            return FindAnyObjectByType<Camera>();
        }

        public void SetTargetCamera(Camera cam)
        {
            targetCamera = cam;
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
