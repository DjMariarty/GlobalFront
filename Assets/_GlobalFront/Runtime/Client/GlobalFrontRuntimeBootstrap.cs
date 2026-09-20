using UnityEngine;

namespace GlobalFront.Client
{
    public static class GlobalFrontRuntimeBootstrap
    {
        private const string RuntimeRootName = "[GlobalFront Runtime]";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            Application.runInBackground = true;

            if (GameObject.Find(RuntimeRootName) != null)
            {
                return;
            }

            var root = new GameObject(RuntimeRootName);
            // Phase 2.3 (ADR-007): the authoritative tick loop no longer
            // lives on a client runner component. The TickDriver inside
            // LocalMatchHost owns the server tick schedule; the controller
            // pumps it from Update.
            root.AddComponent<PrototypeWorldBootstrap>();
            root.AddComponent<PrototypeRtsController>();
            root.AddComponent<PrototypeHud>();
            root.AddComponent<RtsInputManager>();

            var mainCamera = Camera.main != null
                ? Camera.main
                : Object.FindAnyObjectByType<Camera>();

            if (mainCamera == null)
            {
                var cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                mainCamera = cameraObject.AddComponent<Camera>();
            }

            mainCamera.nearClipPlane = 0.2f;
            mainCamera.farClipPlane = 600f;

            if (mainCamera.GetComponent<RtsCameraController>() == null)
            {
                mainCamera.gameObject.AddComponent<RtsCameraController>();
            }
        }
    }
}
