using UnityEngine;
using UnityEngine.InputSystem;

namespace GlobalFront.Client
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class RtsCameraController : MonoBehaviour
    {
        [Header("View")]
        [SerializeField, Range(30f, 80f)] private float pitchDegrees = 55f;
        [SerializeField] private float yawDegrees = 0f;
        [SerializeField] private float minimumDistance = 18f;
        [SerializeField] private float maximumDistance = 85f;
        [SerializeField] private float startingDistance = 48f;

        [Header("Movement")]
        [SerializeField] private float minimumPanSpeed = 18f;
        [SerializeField] private float maximumPanSpeed = 55f;
        [SerializeField] private bool edgeScrollEnabled = true;
        [SerializeField] private float edgeScrollPixels = 12f;
        [SerializeField] private float zoomStep = 7f;
        [SerializeField] private float zoomSharpness = 12f;

        [Header("Prototype map bounds")]
        [SerializeField] private Vector2 minimumMapPoint = new Vector2(-58f, -58f);
        [SerializeField] private Vector2 maximumMapPoint = new Vector2(58f, 58f);

        private Vector3 _focusPoint;
        private float _currentDistance;
        private float _targetDistance;
        private Quaternion _viewRotation;

        private void Awake()
        {
            _viewRotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);
            _currentDistance = Mathf.Clamp(startingDistance, minimumDistance, maximumDistance);
            _targetDistance = _currentDistance;

            _focusPoint = Vector3.zero;
            ClampFocusPoint();
            ApplyTransform(true);
        }

        private void Update()
        {
#if !UNITY_SERVER
            ReadPanInput();
            ReadZoomInput();
            ApplyTransform(false);
#endif
        }

        private void ReadPanInput()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            var input = Vector2.zero;

            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                {
                    input.y += 1f;
                }

                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                {
                    input.y -= 1f;
                }

                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                {
                    input.x += 1f;
                }

                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                {
                    input.x -= 1f;
                }
            }

            if (edgeScrollEnabled && Application.isFocused && mouse != null)
            {
                var pointer = mouse.position.ReadValue();

                if (pointer.x >= 0f && pointer.x <= edgeScrollPixels)
                {
                    input.x -= 1f;
                }
                else if (pointer.x <= Screen.width && pointer.x >= Screen.width - edgeScrollPixels)
                {
                    input.x += 1f;
                }

                if (pointer.y >= 0f && pointer.y <= edgeScrollPixels)
                {
                    input.y -= 1f;
                }
                else if (pointer.y <= Screen.height && pointer.y >= Screen.height - edgeScrollPixels)
                {
                    input.y += 1f;
                }
            }

            if (input.sqrMagnitude > 1f)
            {
                input.Normalize();
            }

            var heightRatio = Mathf.InverseLerp(minimumDistance, maximumDistance, _currentDistance);
            var panSpeed = Mathf.Lerp(minimumPanSpeed, maximumPanSpeed, heightRatio);
            var yawRotation = Quaternion.Euler(0f, yawDegrees, 0f);
            var movement = yawRotation * new Vector3(input.x, 0f, input.y);
            _focusPoint += movement * (panSpeed * Time.unscaledDeltaTime);
            ClampFocusPoint();
        }

        private void ReadZoomInput()
        {
            var mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f)
            {
                return;
            }

            _targetDistance = Mathf.Clamp(
                _targetDistance - Mathf.Sign(scroll) * zoomStep,
                minimumDistance,
                maximumDistance);
        }

        private void ApplyTransform(bool immediate)
        {
            _currentDistance = immediate
                ? _targetDistance
                : Mathf.Lerp(
                    _currentDistance,
                    _targetDistance,
                    1f - Mathf.Exp(-zoomSharpness * Time.unscaledDeltaTime));

            transform.SetPositionAndRotation(
                _focusPoint - _viewRotation * Vector3.forward * _currentDistance,
                _viewRotation);
        }

        private void ClampFocusPoint()
        {
            _focusPoint.x = Mathf.Clamp(_focusPoint.x, minimumMapPoint.x, maximumMapPoint.x);
            _focusPoint.y = 0f;
            _focusPoint.z = Mathf.Clamp(_focusPoint.z, minimumMapPoint.y, maximumMapPoint.y);
        }
    }
}
