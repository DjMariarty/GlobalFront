using System.Collections.Generic;
using GlobalFront.Client;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Client
{
    [TestFixture]
    public sealed class RtsCameraTests
    {
        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private Camera _camera;
        private RtsCameraController _cameraController;
        private RtsInputManager _inputManager;

        [SetUp]
        public void SetUp()
        {
            var cameraObject = new GameObject("RtsCameraTest_Camera");
            _createdObjects.Add(cameraObject);

            _camera = cameraObject.AddComponent<Camera>();
            _camera.pixelRect = new Rect(0, 0, 800, 600);
            _camera.fieldOfView = 60f;
            _camera.nearClipPlane = 0.3f;
            _camera.farClipPlane = 500f;

            _inputManager = cameraObject.AddComponent<RtsInputManager>();
            _inputManager.SetMockMode(true);
            _inputManager.SetTargetCamera(_camera);

            _cameraController = cameraObject.AddComponent<RtsCameraController>();
            _cameraController.SetInputManager(_inputManager);
            _cameraController.InitializeState();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _createdObjects)
            {
                if (obj != null)
                {
                    Object.DestroyImmediate(obj);
                }
            }
            _createdObjects.Clear();
            _camera = null;
            _cameraController = null;
            _inputManager = null;
        }

        [Test]
        public void Camera_MoveInput_CalculatesCorrectDirection()
        {
            // Case 1: Yaw = 0 (Facing North along +Z)
            _cameraController.SetYaw(0f, immediate: true);
            _cameraController.SetFocusPoint(Vector3.zero, immediate: true);

            // Move forward (input Y = +1)
            _inputManager.SetMockMoveInput(new Vector2(0f, 1f));
            _cameraController.ManualUpdate(1.0f);

            Assert.Greater(_cameraController.TargetFocusPoint.z, 0f, "Yaw 0 + forward input must shift focus along +Z");
            Assert.AreEqual(0f, _cameraController.TargetFocusPoint.x, 0.001f, "Yaw 0 + forward input must not shift focus along X");

            // Move right (input X = +1)
            _cameraController.SetFocusPoint(Vector3.zero, immediate: true);
            _inputManager.SetMockMoveInput(new Vector2(1f, 0f));
            _cameraController.ManualUpdate(1.0f);

            Assert.Greater(_cameraController.TargetFocusPoint.x, 0f, "Yaw 0 + right input must shift focus along +X");
            Assert.AreEqual(0f, _cameraController.TargetFocusPoint.z, 0.001f, "Yaw 0 + right input must not shift focus along Z");

            // Case 2: Yaw = 90 degrees (Facing East along +X)
            _cameraController.SetYaw(90f, immediate: true);
            _cameraController.SetFocusPoint(Vector3.zero, immediate: true);

            // Move forward (input Y = +1) should now shift along world +X
            _inputManager.SetMockMoveInput(new Vector2(0f, 1f));
            _cameraController.ManualUpdate(1.0f);

            Assert.Greater(_cameraController.TargetFocusPoint.x, 0f, "Yaw 90 + forward input must shift focus along +X");
            Assert.AreEqual(0f, _cameraController.TargetFocusPoint.z, 0.001f, "Yaw 90 + forward input must not shift focus along Z");

            // Move left (input X = -1) should now shift along world +Z
            _cameraController.SetFocusPoint(Vector3.zero, immediate: true);
            _inputManager.SetMockMoveInput(new Vector2(-1f, 0f));
            _cameraController.ManualUpdate(1.0f);

            Assert.Greater(_cameraController.TargetFocusPoint.z, 0f, "Yaw 90 + left input must shift focus along +Z");
            Assert.AreEqual(0f, _cameraController.TargetFocusPoint.x, 0.001f, "Yaw 90 + left input must not shift focus along X");
        }

        [Test]
        public void Camera_ZoomInput_ClampsWithinMinMaxLimits()
        {
            var minH = _cameraController.MinHeight;
            var maxH = _cameraController.MaxHeight;
            Assert.AreEqual(15f, minH, 0.001f, "Default minHeight must be 15f");
            Assert.AreEqual(80f, maxH, 0.001f, "Default maxHeight must be 80f");

            // Continuous zoom in (scroll up, positive zoom input)
            _inputManager.SetMockZoomInput(1f);
            for (var i = 0; i < 50; i++)
            {
                _cameraController.ManualUpdate(0.2f);
            }
            _cameraController.SnapToTarget();

            Assert.AreEqual(minH, _cameraController.CurrentHeight, 0.01f, "Height must clamp at minHeight");
            Assert.AreEqual(minH, _camera.transform.position.y, 0.01f, "Camera Y position must match minHeight");
            Assert.AreEqual(_cameraController.MinPitch, _cameraController.CurrentPitch, 0.5f, "Pitch must match minPitch at minHeight");
            Assert.AreEqual(40f, _cameraController.CurrentPitch, 0.5f, "minPitch must be 40 degrees");
            Assert.GreaterOrEqual(_camera.transform.position.y, minH, "Camera height must never fall below minHeight");

            // Continuous zoom out (scroll down, negative zoom input)
            _inputManager.SetMockZoomInput(-1f);
            for (var i = 0; i < 50; i++)
            {
                _cameraController.ManualUpdate(0.2f);
            }
            _cameraController.SnapToTarget();

            Assert.AreEqual(maxH, _cameraController.CurrentHeight, 0.01f, "Height must clamp at maxHeight");
            Assert.AreEqual(maxH, _camera.transform.position.y, 0.01f, "Camera Y position must match maxHeight");
            Assert.AreEqual(_cameraController.MaxPitch, _cameraController.CurrentPitch, 0.5f, "Pitch must match maxPitch at maxHeight");
            Assert.AreEqual(70f, _cameraController.CurrentPitch, 0.5f, "maxPitch must be 70 degrees");
            Assert.LessOrEqual(_camera.transform.position.y, maxH, "Camera height must never exceed maxHeight");
        }

        [Test]
        public void Camera_MapBounds_ConstrainsTargetPosition()
        {
            var bounds = _cameraController.MapBounds;
            Assert.AreEqual(-200f, bounds.xMin, 0.001f);
            Assert.AreEqual(200f, bounds.xMax, 0.001f);
            Assert.AreEqual(-200f, bounds.yMin, 0.001f);
            Assert.AreEqual(200f, bounds.yMax, 0.001f);

            // Attempt to move far past positive boundaries
            _inputManager.SetMockMoveInput(new Vector2(1f, 1f));
            for (var i = 0; i < 50; i++)
            {
                _cameraController.ManualUpdate(1.0f);
            }

            Assert.AreEqual(bounds.xMax, _cameraController.TargetFocusPoint.x, 0.01f, "TargetFocusPoint.x must clamp at xMax");
            Assert.AreEqual(bounds.yMax, _cameraController.TargetFocusPoint.z, 0.01f, "TargetFocusPoint.z must clamp at yMax");
            Assert.AreEqual(bounds.xMax, _cameraController.FocusPoint.x, 0.01f, "FocusPoint.x must clamp at xMax");
            Assert.AreEqual(bounds.yMax, _cameraController.FocusPoint.z, 0.01f, "FocusPoint.z must clamp at yMax");

            // Attempt to move far past negative boundaries
            _inputManager.SetMockMoveInput(new Vector2(-1f, -1f));
            for (var i = 0; i < 50; i++)
            {
                _cameraController.ManualUpdate(1.0f);
            }

            Assert.AreEqual(bounds.xMin, _cameraController.TargetFocusPoint.x, 0.01f, "TargetFocusPoint.x must clamp at xMin");
            Assert.AreEqual(bounds.yMin, _cameraController.TargetFocusPoint.z, 0.01f, "TargetFocusPoint.z must clamp at yMin");
            Assert.AreEqual(bounds.xMin, _cameraController.FocusPoint.x, 0.01f, "FocusPoint.x must clamp at xMin");
            Assert.AreEqual(bounds.yMin, _cameraController.FocusPoint.z, 0.01f, "FocusPoint.z must clamp at yMin");

            // Attempt direct out-of-bounds SetFocusPoint
            _cameraController.SetFocusPoint(new Vector3(9999f, 0f, -9999f), immediate: true);
            Assert.AreEqual(bounds.xMax, _cameraController.TargetFocusPoint.x, 0.01f);
            Assert.AreEqual(bounds.yMin, _cameraController.TargetFocusPoint.z, 0.01f);
            Assert.AreEqual(bounds.xMax, _cameraController.FocusPoint.x, 0.01f);
            Assert.AreEqual(bounds.yMin, _cameraController.FocusPoint.z, 0.01f);
        }

        [Test]
        public void InputManager_RaycastsToGroundPlaneAccurately()
        {
            // Create a dedicated test camera positioned at (0, 10, -10), pitched 45 degrees down looking towards (0, 0, 0)
            var rayTestObj = new GameObject("RaycastTest_Camera");
            _createdObjects.Add(rayTestObj);

            var rayCamera = rayTestObj.AddComponent<Camera>();
            var rt = new RenderTexture(800, 600, 24);
            rayCamera.targetTexture = rt;
            rayCamera.fieldOfView = 60f;
            rayCamera.nearClipPlane = 0.3f;
            rayCamera.farClipPlane = 500f;
            rayCamera.transform.position = new Vector3(0f, 10f, -10f);
            rayCamera.transform.rotation = Quaternion.Euler(45f, 0f, 0f);

            try
            {
                // Screen center is (pixelWidth * 0.5, pixelHeight * 0.5) = (400, 300)
                var screenCenter = new Vector2(rayCamera.pixelWidth * 0.5f, rayCamera.pixelHeight * 0.5f);
                _inputManager.SetMockMousePosition(screenCenter);

                var success = _inputManager.TryGetMouseWorldPosition(rayCamera, out var hitPoint);

                Assert.IsTrue(success, "Ray from camera must intersect ground plane Y = 0");
                Assert.AreEqual(0f, hitPoint.x, 0.01f, "Center ray X must be 0");
                Assert.AreEqual(0f, hitPoint.y, 0.01f, "Center ray Y must be 0");
                Assert.AreEqual(0f, hitPoint.z, 0.01f, "Center ray Z must be 0");

                // Second case: Camera directly overhead at (30, 50, 40), looking straight down (pitch 90)
                rayCamera.transform.position = new Vector3(30f, 50f, 40f);
                rayCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

                success = _inputManager.TryGetMouseWorldPosition(rayCamera, out var overheadHit);

                Assert.IsTrue(success, "Overhead ray must intersect ground plane Y = 0");
                Assert.AreEqual(30f, overheadHit.x, 0.01f, "Overhead ray X must match camera X");
                Assert.AreEqual(0f, overheadHit.y, 0.01f, "Overhead ray Y must be ground Y = 0");
                Assert.AreEqual(40f, overheadHit.z, 0.01f, "Overhead ray Z must match camera Z");
            }
            finally
            {
                // A render texture is native memory and is not covered by the GameObject
                // teardown, so it goes on a finally path: an assertion failing above
                // must not leak one per run.
                rayCamera.targetTexture = null;
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        // ---------------------------------------------------------------- audit remediation (P1-1, P2-1..P2-5)

        [Test]
        public void Camera_ZeroPitch_DoesNotProduceNaN()
        {
            // The [Range] attributes on these fields constrain the inspector slider
            // only, so assign the range the slider forbids: at pitch 0 the ground
            // distance is height / tan(0) = Infinity, which reaches the transform as
            // NaN and takes the camera out of the world.
            _cameraController.MinPitch = 0f;
            _cameraController.MaxPitch = 0f;
            _cameraController.SetHeight(45f, immediate: true);

            for (var frame = 0; frame < 10; frame++)
            {
                _cameraController.ManualUpdate(0.016f);
            }

            Assert.That(_cameraController.MinPitch, Is.GreaterThanOrEqualTo(10f), "pitch range refuses 0");
            Assert.That(_cameraController.MaxPitch, Is.GreaterThanOrEqualTo(10f));
            Assert.That(_cameraController.CurrentPitch, Is.InRange(10f, 85f));

            var position = _camera.transform.position;
            Assert.That(float.IsNaN(position.x) || float.IsInfinity(position.x), Is.False, "camera X");
            Assert.That(float.IsNaN(position.y) || float.IsInfinity(position.y), Is.False, "camera Y");
            Assert.That(float.IsNaN(position.z) || float.IsInfinity(position.z), Is.False, "camera Z");
            Assert.That(position.y, Is.GreaterThan(0f), "the camera must stay above the map");

            // Same family of division: an inverted or zero-width height range would put
            // a 0/0 straight into the pitch.
            _cameraController.MinHeight = 0f;
            _cameraController.MaxHeight = 0f;
            _cameraController.ManualUpdate(0.016f);
            Assert.That(_cameraController.CurrentHeight, Is.GreaterThan(0f));

            var afterHeightAbuse = _camera.transform.position;
            Assert.That(
                float.IsNaN(afterHeightAbuse.x) || float.IsNaN(afterHeightAbuse.y) || float.IsNaN(afterHeightAbuse.z),
                Is.False,
                "a collapsed height range must not poison the transform either");
        }

        [Test]
        public void Camera_NaNInput_IsRejected()
        {
            _cameraController.SetFocusPoint(new Vector3(12f, 0f, -8f), immediate: true);
            _cameraController.SetYaw(25f, immediate: true);
            var focusX = _cameraController.FocusPoint.x;
            var height = _cameraController.CurrentHeight;
            var yaw = _cameraController.CurrentYaw;

            // One NaN in the target is permanent: every later Lerp towards it returns
            // NaN, so the guard has to be on the way in.
            _cameraController.SetFocusPoint(new Vector3(float.NaN, 0f, float.NaN), immediate: true);
            Assert.That(_cameraController.TargetFocusPoint.x, Is.EqualTo(12f).Within(0.01f), "NaN focus refused");
            Assert.That(_cameraController.FocusPoint.x, Is.EqualTo(focusX).Within(0.01f));

            _cameraController.SetHeight(float.NaN, immediate: true);
            Assert.That(_cameraController.TargetHeight, Is.EqualTo(height).Within(1e-3f), "NaN height refused");

            _cameraController.SetYaw(float.NaN, immediate: true);
            Assert.That(_cameraController.TargetYaw, Is.EqualTo(yaw).Within(1e-3f), "NaN yaw refused");

            // Infinity is refused for the same reason: it survives one Lerp as NaN.
            _cameraController.SetHeight(float.PositiveInfinity);
            Assert.That(_cameraController.TargetHeight, Is.EqualTo(height).Within(1e-3f), "infinite height refused");

            _cameraController.SetYaw(float.NegativeInfinity);
            Assert.That(_cameraController.TargetYaw, Is.EqualTo(yaw).Within(1e-3f), "infinite yaw refused");

            _cameraController.ManualUpdate(0.016f);
            var position = _camera.transform.position;
            Assert.That(
                float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z),
                Is.False,
                "the transform must stay finite after rejected input");
        }

        [Test]
        public void Camera_ManualUpdate_DoesNotAllocate()
        {
            // Control first: the project measures allocations with Unity's GC.Alloc
            // recorder, because the editor's Mono runtime reports 0 from
            // GC.GetAllocatedBytesForCurrentThread().
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[1024]; },
                UnityEngine.TestTools.Constraints.Is.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            void Drive(int frames)
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    _cameraController.ManualUpdate(0.016f);
                }
            }

            Drive(20);
            Assert.That(
                () => Drive(200),
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "the camera step runs every frame and must not touch the heap");
        }

        [Test]
        public void InputManager_AltWithoutMouseButton_DoesNotRotate()
        {
            // Alt alone used to grab the camera, so the modifier a player presses when
            // tabbing out spun the whole battlefield while the cursor crossed the screen.
            //
            // The rule is asserted as a pure function: mock mode short-circuits the
            // hardware path by design, and the EditMode assembly does not reference the
            // Input System, so no test can press a virtual Alt. The gesture table is
            // what the defect was about, and it is tested where it is written.
            Assert.That(RtsInputManager.IsRotationCombination(false, false, true), Is.False,
                "Alt alone must never rotate");
            Assert.That(RtsInputManager.IsRotationCombination(false, false, false), Is.False,
                "nothing held must never rotate");
            Assert.That(RtsInputManager.IsRotationCombination(true, false, false), Is.True,
                "the middle button still rotates on its own");
            Assert.That(RtsInputManager.IsRotationCombination(false, true, true), Is.True,
                "Alt plus right button is the documented fallback gesture");
            Assert.That(RtsInputManager.IsRotationCombination(false, true, false), Is.False,
                "the right button alone is a command input, not a camera grab");

            // The mock seam is the test surface for the rest of the suite and stays intact.
            _inputManager.SetMockIsRotating(false);
            Assert.That(_inputManager.IsRotating, Is.False);
            _inputManager.SetMockIsRotating(true);
            Assert.That(_inputManager.IsRotating, Is.True);
        }
    }
}
