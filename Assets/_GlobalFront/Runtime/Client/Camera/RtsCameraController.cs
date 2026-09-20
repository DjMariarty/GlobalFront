using System;
using UnityEngine;

namespace GlobalFront.Client
{
    /// <summary>
    /// Tactical RTS Camera Controller (Phase 3, Step 3.1).
    /// Operates strictly within the client/presentation layer.
    /// Features:
    /// - Smooth pan / movement (WASD, arrows, screen edge panning).
    /// - Zoom with strict height limits [15f, 80f] and dynamic pitch (40° near ground -> 70° high above).
    /// - Yaw rotation around focus point (MMB or Alt + mouse drag) with quick reset to North (Home or ~).
    /// - Map bounds enforcement (default Rect(-200, -200, 400, 400)).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class RtsCameraController : MonoBehaviour
    {
        [Header("Input Service")]
        [SerializeField] private RtsInputManager inputManager;

        [Header("Movement")]
        [SerializeField] private float moveSpeed = 35f;
        [SerializeField] private float panSmoothing = 12f;
        [SerializeField] private bool enableEdgePan = false;
        [SerializeField] private float edgePanThickness = 12f;

        [Header("Zoom & Dynamic Pitch")]
        [SerializeField] private float minHeight = 15f;
        [SerializeField] private float maxHeight = 80f;
        [SerializeField] private float startHeight = 45f;
        [SerializeField] private float zoomStep = 8f;
        [SerializeField] private float zoomSmoothing = 12f;
        [SerializeField, Range(30f, 60f)] private float minPitch = 40f;
        [SerializeField, Range(60f, 85f)] private float maxPitch = 70f;

        [Header("Rotation")]
        [SerializeField] private float rotationSensitivity = 0.5f;
        [SerializeField] private float rotationSmoothing = 15f;

        [Header("Map Bounds")]
        [SerializeField] private Rect mapBounds = new Rect(-200f, -200f, 400f, 400f);

        // Runtime state
        private Vector3 _focusPoint;
        private Vector3 _targetFocusPoint;
        private float _currentHeight;
        private float _targetHeight;
        private float _currentYaw;
        private float _targetYaw;
        private float _currentPitch;

        public Vector3 FocusPoint => _focusPoint;
        public Vector3 TargetFocusPoint => _targetFocusPoint;
        public float CurrentHeight => _currentHeight;
        public float TargetHeight => _targetHeight;
        public float CurrentYaw => _currentYaw;
        public float TargetYaw => _targetYaw;
        public float CurrentPitch => _currentPitch;

        public float MoveSpeed { get => moveSpeed; set => moveSpeed = value; }
        public float PanSmoothing { get => panSmoothing; set => panSmoothing = value; }
        public bool EnableEdgePan { get => enableEdgePan; set => enableEdgePan = value; }
        public float EdgePanThickness { get => edgePanThickness; set => edgePanThickness = value; }

        public float MinHeight { get => minHeight; set => minHeight = value; }
        public float MaxHeight { get => maxHeight; set => maxHeight = value; }
        public float ZoomStep { get => zoomStep; set => zoomStep = value; }
        public float ZoomSmoothing { get => zoomSmoothing; set => zoomSmoothing = value; }
        public float MinPitch { get => minPitch; set => minPitch = value; }
        public float MaxPitch { get => maxPitch; set => maxPitch = value; }

        public float RotationSensitivity { get => rotationSensitivity; set => rotationSensitivity = value; }
        public float RotationSmoothing { get => rotationSmoothing; set => rotationSmoothing = value; }

        public Rect MapBounds { get => mapBounds; set => mapBounds = value; }

        private void Awake()
        {
            InitializeState();
        }

        public void InitializeState()
        {
            if (inputManager == null)
            {
                inputManager = RtsInputManager.Instance ?? FindAnyObjectByType<RtsInputManager>();
                if (inputManager == null)
                {
                    inputManager = gameObject.AddComponent<RtsInputManager>();
                }
            }

            _currentHeight = Mathf.Clamp(startHeight, minHeight, maxHeight);
            _targetHeight = _currentHeight;
            _currentYaw = 0f;
            _targetYaw = 0f;
            _focusPoint = Vector3.zero;
            _targetFocusPoint = Vector3.zero;

            ClampToBounds(ref _targetFocusPoint);
            ClampToBounds(ref _focusPoint);
            ApplyTransform(true);
        }

        private void Update()
        {
#if !UNITY_SERVER
            ManualUpdate(Time.unscaledDeltaTime);
#endif
        }

        /// <summary>
        /// Deterministically steps the camera controller. Callable from Update() and EditMode tests.
        /// </summary>
        public void ManualUpdate(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                ApplyTransform(false);
                return;
            }

            ProcessInput(deltaTime);
            InterpolateTowardsTarget(deltaTime);
            ApplyTransform(false);
        }

        private void ProcessInput(float deltaTime)
        {
            if (inputManager == null)
            {
                return;
            }

            // Pan input
            var move = inputManager.MoveInput;
            if (enableEdgePan && Application.isFocused)
            {
                var mousePos = inputManager.MousePosition;
                if (mousePos.x >= 0f && mousePos.x <= edgePanThickness) move.x -= 1f;
                else if (mousePos.x <= Screen.width && mousePos.x >= Screen.width - edgePanThickness) move.x += 1f;

                if (mousePos.y >= 0f && mousePos.y <= edgePanThickness) move.y -= 1f;
                else if (mousePos.y <= Screen.height && mousePos.y >= Screen.height - edgePanThickness) move.y += 1f;

                if (move.sqrMagnitude > 1f) move.Normalize();
            }

            if (move.sqrMagnitude > 0.0001f)
            {
                var yawRot = Quaternion.Euler(0f, _targetYaw, 0f);
                var moveDir = yawRot * new Vector3(move.x, 0f, move.y);
                _targetFocusPoint += moveDir * (moveSpeed * deltaTime);
                ClampToBounds(ref _targetFocusPoint);
            }

            // Zoom input
            var zoom = inputManager.ZoomInput;
            if (Mathf.Abs(zoom) > 0.0001f)
            {
                _targetHeight = Mathf.Clamp(_targetHeight - zoom * zoomStep, minHeight, maxHeight);
            }

            // Rotation input
            if (inputManager.IsRotating)
            {
                _targetYaw += inputManager.RotationInput * rotationSensitivity;
            }

            // Reset rotation
            if (inputManager.ResetRotationRequested)
            {
                _targetYaw = 0f;
            }
        }

        private void InterpolateTowardsTarget(float deltaTime)
        {
            if (panSmoothing > 0f)
            {
                var panFactor = 1f - Mathf.Exp(-panSmoothing * deltaTime);
                _focusPoint = Vector3.Lerp(_focusPoint, _targetFocusPoint, panFactor);
            }
            else
            {
                _focusPoint = _targetFocusPoint;
            }
            ClampToBounds(ref _focusPoint);

            if (zoomSmoothing > 0f)
            {
                var zoomFactor = 1f - Mathf.Exp(-zoomSmoothing * deltaTime);
                _currentHeight = Mathf.Lerp(_currentHeight, _targetHeight, zoomFactor);
            }
            else
            {
                _currentHeight = _targetHeight;
            }
            _currentHeight = Mathf.Clamp(_currentHeight, minHeight, maxHeight);

            if (rotationSmoothing > 0f)
            {
                var rotFactor = 1f - Mathf.Exp(-rotationSmoothing * deltaTime);
                _currentYaw = Mathf.LerpAngle(_currentYaw, _targetYaw, rotFactor);
            }
            else
            {
                _currentYaw = _targetYaw;
            }
        }

        public void ApplyTransform(bool immediate = false)
        {
            if (immediate)
            {
                _focusPoint = _targetFocusPoint;
                _currentHeight = _targetHeight;
                _currentYaw = _targetYaw;
            }

            _currentHeight = Mathf.Clamp(_currentHeight, minHeight, maxHeight);
            var heightFraction = Mathf.InverseLerp(minHeight, maxHeight, _currentHeight);
            _currentPitch = Mathf.Lerp(minPitch, maxPitch, heightFraction);

            var pitchRad = _currentPitch * Mathf.Deg2Rad;
            var groundDist = _currentHeight / Mathf.Tan(pitchRad);
            var yawRotation = Quaternion.Euler(0f, _currentYaw, 0f);
            var offset = yawRotation * new Vector3(0f, _currentHeight, -groundDist);

            transform.position = _focusPoint + offset;
            transform.rotation = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
        }

        public void SnapToTarget()
        {
            ApplyTransform(true);
        }

        public void SetFocusPoint(Vector3 point, bool immediate = false)
        {
            _targetFocusPoint = point;
            ClampToBounds(ref _targetFocusPoint);
            if (immediate)
            {
                _focusPoint = _targetFocusPoint;
                ApplyTransform(false);
            }
        }

        public void SetHeight(float height, bool immediate = false)
        {
            _targetHeight = Mathf.Clamp(height, minHeight, maxHeight);
            if (immediate)
            {
                _currentHeight = _targetHeight;
                ApplyTransform(false);
            }
        }

        public void SetYaw(float yaw, bool immediate = false)
        {
            _targetYaw = yaw;
            if (immediate)
            {
                _currentYaw = _targetYaw;
                ApplyTransform(false);
            }
        }

        public void SetInputManager(RtsInputManager manager)
        {
            inputManager = manager;
        }

        private void ClampToBounds(ref Vector3 point)
        {
            point.x = Mathf.Clamp(point.x, mapBounds.xMin, mapBounds.xMax);
            point.z = Mathf.Clamp(point.z, mapBounds.yMin, mapBounds.yMax);
            point.y = 0f;
        }
    }
}
