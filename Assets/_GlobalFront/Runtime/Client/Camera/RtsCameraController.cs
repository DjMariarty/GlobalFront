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
        [SerializeField] private float minHeight = DefaultMinHeight;
        [SerializeField] private float maxHeight = DefaultMaxHeight;
        [SerializeField] private float startHeight = 45f;
        [SerializeField] private float zoomStep = 8f;
        [SerializeField] private float zoomSmoothing = 12f;
        [SerializeField, Range(30f, 60f)] private float minPitch = DefaultMinPitch;
        [SerializeField, Range(60f, 85f)] private float maxPitch = DefaultMaxPitch;

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

        /// <summary>
        /// Lowest usable zoom height. Off zero because the ground distance is
        /// <c>height / tan(pitch)</c>, and a zero-width range would also make the
        /// <c>InverseLerp</c> in <see cref="ApplyTransform"/> divide by zero.
        /// </summary>
        private const float HeightFloor = 1f;

        /// <summary>
        /// Pitch floor. <see cref="RangeAttribute"/> on the serialized field only
        /// constrains the inspector slider, so a script or a hand-edited scene can
        /// still assign 0 — and at pitch 0 the ground distance is
        /// <c>height / tan(0) = Infinity</c>, which lands on the transform as NaN and
        /// takes the camera out of the world. 10 degrees keeps tan() far from zero
        /// while still reading as an almost horizontal view.
        /// </summary>
        private const float PitchFloorDegrees = 10f;

        /// <summary>Pitch ceiling. At 90 degrees tan() diverges, so the range stops short.</summary>
        private const float PitchCeilingDegrees = 85f;

        /// <summary>
        /// Smallest tan() the ground distance may divide by. A pure belt-and-braces
        /// floor: the pitch clamp above already keeps the real value near 0.18.
        /// </summary>
        private const float MinTanPitch = 0.01f;

        // Tuning defaults, named so the Awake sanitizer can fall back to the same value the
        // field initializer uses instead of inventing a second one.
        private const float DefaultMinHeight = 15f;
        private const float DefaultMaxHeight = 80f;
        private const float DefaultMinPitch = 40f;
        private const float DefaultMaxPitch = 70f;

        /// <summary>
        /// Lowest zoom height. Clamped through the setter because the pitch is derived
        /// from the height fraction, so an inverted or zero-width range is a NaN
        /// generator, not just a cosmetic mistake. A non-finite write is dropped rather
        /// than clamped: <c>Mathf.Clamp</c> returns NaN for NaN, so clamping would store
        /// the very value this clamp exists to keep out.
        /// </summary>
        public float MinHeight
        {
            get => minHeight;
            set => minHeight = Mathf.Min(Mathf.Max(HeightFloor, FiniteOr(value, minHeight)), maxHeight);
        }

        /// <summary>Highest zoom height; see <see cref="MinHeight"/> for the ordering rule.</summary>
        public float MaxHeight
        {
            get => maxHeight;
            set => maxHeight = Mathf.Max(FiniteOr(value, maxHeight), minHeight);
        }

        public float ZoomStep { get => zoomStep; set => zoomStep = value; }
        public float ZoomSmoothing { get => zoomSmoothing; set => zoomSmoothing = value; }

        /// <summary>Pitch at the lowest zoom; the clamp is here rather than in the inspector because <see cref="RangeAttribute"/> does not bind code. See <see cref="MinHeight"/> for why a non-finite write is dropped.</summary>
        public float MinPitch
        {
            get => minPitch;
            set => minPitch = Mathf.Clamp(FiniteOr(value, minPitch), PitchFloorDegrees, maxPitch);
        }

        /// <summary>Pitch at the highest zoom; see <see cref="MinPitch"/>.</summary>
        public float MaxPitch
        {
            get => maxPitch;
            set => maxPitch = Mathf.Clamp(FiniteOr(value, maxPitch), minPitch, PitchCeilingDegrees);
        }

        public float RotationSensitivity { get => rotationSensitivity; set => rotationSensitivity = value; }
        public float RotationSmoothing { get => rotationSmoothing; set => rotationSmoothing = value; }

        public Rect MapBounds { get => mapBounds; set => mapBounds = value; }

        private void Awake()
        {
            SanitizeSerializedRanges();
            InitializeState();
        }

        /// <summary>
        /// Forces the serialized pitch/height fields into a finite, ordered range before the
        /// first <see cref="ApplyTransform"/>. Every one of them feeds a division, and a scene
        /// asset reaches them without passing a setter: Unity round-trips a hand-edited
        /// <c>NaN</c> in the YAML back into the field, and <see cref="RangeAttribute"/> only
        /// constrains the inspector slider. Doing it here rather than only inside
        /// <see cref="ApplyTransform"/> is what makes the whole object trustworthy from the
        /// first frame — properties, <see cref="InitializeState"/> and the height clamp in
        /// <see cref="SetHeight"/> all read these fields as already valid.
        /// </summary>
        private void SanitizeSerializedRanges()
        {
            minPitch = Mathf.Clamp(FiniteOr(minPitch, DefaultMinPitch), PitchFloorDegrees, PitchCeilingDegrees);
            maxPitch = Mathf.Clamp(FiniteOr(maxPitch, DefaultMaxPitch), minPitch, PitchCeilingDegrees);
            minHeight = Mathf.Max(HeightFloor, FiniteOr(minHeight, DefaultMinHeight));
            maxHeight = Mathf.Max(minHeight, FiniteOr(maxHeight, DefaultMaxHeight));
            startHeight = Mathf.Clamp(FiniteOr(startHeight, minHeight), minHeight, maxHeight);
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
                var advanced = _targetFocusPoint + moveDir * (moveSpeed * deltaTime);

                // Commit only a finite result, silently: moveSpeed is a serialized
                // tuning field, and one NaN through it would otherwise stick in the
                // focus forever. The setter paths log; this does not, because it is
                // reached every frame of a held key.
                if (IsFinite(advanced))
                {
                    _targetFocusPoint = advanced;
                    ClampToBounds(ref _targetFocusPoint);
                }
            }

            // Zoom input
            var zoom = inputManager.ZoomInput;
            if (Mathf.Abs(zoom) > 0.0001f)
            {
                var height = _targetHeight - zoom * zoomStep;
                if (IsFinite(height))
                {
                    _targetHeight = Mathf.Clamp(height, minHeight, maxHeight);
                }
            }

            // Rotation input
            if (inputManager.IsRotating)
            {
                var yaw = _targetYaw + inputManager.RotationInput * rotationSensitivity;
                if (IsFinite(yaw))
                {
                    _targetYaw = yaw;
                }
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

            // The last line of defence, deliberately independent of the Awake sanitizer and of
            // the setters: the two ranges can still be collapsed to a zero width by a legitimate
            // call (MinHeight(60) then MaxHeight(20) lands on 60..60), and both feed a division.
            // Everything below is bounded before it divides, and the serialized fields are read
            // through locals rather than rewritten in place.
            var heightFloor = Mathf.Max(HeightFloor, minHeight);
            var heightCeiling = Mathf.Max(maxHeight, heightFloor);
            _currentHeight = Mathf.Clamp(_currentHeight, heightFloor, heightCeiling);

            // A bounded InverseLerp: heightCeiling == heightFloor would otherwise return
            // NaN and hand it straight to the pitch.
            var heightFraction = heightCeiling > heightFloor
                ? (_currentHeight - heightFloor) / (heightCeiling - heightFloor)
                : 0f;

            var pitchFloor = Mathf.Clamp(minPitch, PitchFloorDegrees, maxPitch);
            var pitchCeiling = Mathf.Max(maxPitch, pitchFloor);
            _currentPitch = Mathf.Clamp(
                Mathf.Lerp(pitchFloor, pitchCeiling, heightFraction),
                PitchFloorDegrees,
                PitchCeilingDegrees);

            var pitchRad = _currentPitch * Mathf.Deg2Rad;
            var tanPitch = Mathf.Max(MinTanPitch, Mathf.Tan(pitchRad));
            var groundDist = _currentHeight / tanPitch;
            var yawRotation = Quaternion.Euler(0f, _currentYaw, 0f);
            var offset = yawRotation * new Vector3(0f, _currentHeight, -groundDist);

            transform.position = _focusPoint + offset;
            transform.rotation = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
        }

        public void SnapToTarget()
        {
            ApplyTransform(true);
        }

        /// <summary>
        /// Moves the focus. A NaN or infinite point is refused rather than clamped:
        /// <c>Mathf.Clamp(NaN, min, max)</c> is NaN, so a clamp would not repair it, and
        /// one poisoned <c>_targetFocusPoint</c> is permanent — every later
        /// <c>Lerp</c> towards it returns NaN and the camera freezes for the rest of the
        /// session. The door is the only place that can still refuse it.
        /// </summary>
        public void SetFocusPoint(Vector3 point, bool immediate = false)
        {
            if (!IsFinite(point))
            {
                Debug.LogWarning($"{nameof(RtsCameraController)}: ignored a non-finite focus point.");
                return;
            }

            _targetFocusPoint = point;
            ClampToBounds(ref _targetFocusPoint);
            if (immediate)
            {
                _focusPoint = _targetFocusPoint;
                ApplyTransform(false);
            }
        }

        /// <summary>Sets the zoom target. See <see cref="SetFocusPoint"/> for why a non-finite value is refused instead of clamped.</summary>
        public void SetHeight(float height, bool immediate = false)
        {
            if (!IsFinite(height))
            {
                Debug.LogWarning($"{nameof(RtsCameraController)}: ignored a non-finite height.");
                return;
            }

            _targetHeight = Mathf.Clamp(height, minHeight, maxHeight);
            if (immediate)
            {
                _currentHeight = _targetHeight;
                ApplyTransform(false);
            }
        }

        /// <summary>Sets the yaw target. See <see cref="SetFocusPoint"/> for why a non-finite value is refused instead of clamped.</summary>
        public void SetYaw(float yaw, bool immediate = false)
        {
            if (!IsFinite(yaw))
            {
                Debug.LogWarning($"{nameof(RtsCameraController)}: ignored a non-finite yaw.");
                return;
            }

            _targetYaw = yaw;
            if (immediate)
            {
                _currentYaw = _targetYaw;
                ApplyTransform(false);
            }
        }

        /// <summary>
        /// Unity's numeric guards spelled once. <c>float.IsNaN</c> alone would let an
        /// infinity through, and an infinity survives <c>Lerp</c> as NaN one frame later.
        /// </summary>
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// The number, or <paramref name="fallback"/> when it is not one. Needed because
        /// <c>Mathf.Clamp</c> and <c>Mathf.Max</c> propagate a non-finite value instead of
        /// repairing it — clamping NaN still yields NaN — so a range that must stay usable
        /// needs a fallback, not a clamp.
        /// </summary>
        private static float FiniteOr(float value, float fallback) => IsFinite(value) ? value : fallback;

        private static bool IsFinite(in Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        public void SetInputManager(RtsInputManager manager)
        {
            inputManager = manager;
        }

        private void ClampToBounds(ref Vector3 point)
        {
            // Commit only finite results. mapBounds is serialized, so a hand-edited
            // scene can carry NaN bounds, and Mathf.Clamp(NaN, a, b) is NaN — writing
            // that back would poison the focus through the one method every path calls.
            var x = Mathf.Clamp(point.x, mapBounds.xMin, mapBounds.xMax);
            var z = Mathf.Clamp(point.z, mapBounds.yMin, mapBounds.yMax);
            if (IsFinite(x))
            {
                point.x = x;
            }

            if (IsFinite(z))
            {
                point.z = z;
            }

            point.y = 0f;
        }
    }
}
