using System.Collections.Generic;
using GlobalFront.Client;
using NUnit.Framework;
using UnityEngine;

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
            _createdObjects.Add(rayTestObj); // camera obj
            rayCamera.targetTexture = rt;
            rayCamera.fieldOfView = 60f;
            rayCamera.nearClipPlane = 0.3f;
            rayCamera.farClipPlane = 500f;
            rayCamera.transform.position = new Vector3(0f, 10f, -10f);
            rayCamera.transform.rotation = Quaternion.Euler(45f, 0f, 0f);

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

            rayCamera.targetTexture = null;
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
