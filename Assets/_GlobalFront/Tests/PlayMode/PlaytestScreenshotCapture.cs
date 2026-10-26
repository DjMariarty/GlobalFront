using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace GlobalFront.Tests.PlayMode
{
    /// <summary>
    /// Captures the Game view at a true 1920x1080 for the synthetic playtest.
    /// The editor Game view is resized through its selected-size index; the
    /// ScreenSpaceOverlay HUD and the IMGUI pause overlay are included because
    /// ScreenCapture reads the composed Game view, not a camera RenderTexture.
    /// </summary>
    public static class PlaytestScreenshotCapture
    {
        public const int Width = 1920;
        public const int Height = 1080;

        public static string OutputDirectory => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Artifacts", "Playtest"));

        private static bool _started;

#if UNITY_EDITOR
        private static EditorWindow _gameView;
        private static PropertyInfo _selectedSizeIndex;
        private static int _originalSizeIndex = -1;
#endif

        public static IEnumerator Begin()
        {
            Directory.CreateDirectory(OutputDirectory);

#if UNITY_EDITOR
            var assembly = typeof(EditorWindow).Assembly;
            var gameViewType = assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                throw new InvalidOperationException("UnityEditor.GameView was not found");
            }

            var windows = Resources.FindObjectsOfTypeAll(gameViewType);
            _gameView = windows.Length > 0
                ? (EditorWindow)windows[0]
                : EditorWindow.GetWindow(gameViewType);
            _gameView.Show();
            _selectedSizeIndex = gameViewType.GetProperty(
                "selectedSizeIndex",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_selectedSizeIndex == null)
            {
                throw new InvalidOperationException("GameView.selectedSizeIndex was not found");
            }

            _originalSizeIndex = (int)_selectedSizeIndex.GetValue(_gameView);

            var sizesType = assembly.GetType("UnityEditor.GameViewSizes");
            var singletonType = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            var instance = singletonType.GetProperty(
                "instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
            var group = sizesType.GetProperty(
                "currentGroup",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(instance);
            var getSize = group.GetType().GetMethod(
                "GetGameViewSize",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fullHdIndex = -1;
            for (var index = 0; index < 40; index++)
            {
                try
                {
                    var size = getSize.Invoke(group, new object[] { index });
                    var width = (int)size.GetType().GetProperty("width").GetValue(size);
                    var height = (int)size.GetType().GetProperty("height").GetValue(size);
                    if (width == Width && height == Height)
                    {
                        fullHdIndex = index;
                        break;
                    }
                }
                catch
                {
                    break;
                }
            }

            if (fullHdIndex < 0)
            {
                throw new InvalidOperationException("the Game view has no 1920x1080 size");
            }

            _selectedSizeIndex.SetValue(_gameView, fullHdIndex);
            _gameView.Repaint();
#else
            yield return null;
#endif

            _started = true;
            yield return null;
            yield return null;
        }

        public static IEnumerator Capture(string fileName)
        {
            if (!_started)
            {
                throw new InvalidOperationException("PlaytestScreenshotCapture.Begin was not called");
            }

            yield return new WaitForEndOfFrame();
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            if (texture == null)
            {
                throw new InvalidOperationException("ScreenCapture returned no texture");
            }

            try
            {
                if (texture.width != Width || texture.height != Height)
                {
                    throw new InvalidOperationException(
                        $"Game view capture is {texture.width}x{texture.height}, " +
                        $"expected {Width}x{Height}");
                }

                var png = texture.EncodeToPNG();
                var path = Path.Combine(OutputDirectory, fileName);
                File.WriteAllBytes(path, png);
                Debug.Log($"Playtest screenshot saved: {path} ({texture.width}x{texture.height})");
            }
            finally
            {
                Object.Destroy(texture);
            }
        }

        public static void End()
        {
            if (!_started)
            {
                return;
            }

            _started = false;
#if UNITY_EDITOR
            if (_gameView != null && _selectedSizeIndex != null && _originalSizeIndex >= 0)
            {
                _selectedSizeIndex.SetValue(_gameView, _originalSizeIndex);
                _gameView.Repaint();
            }

            _gameView = null;
            _selectedSizeIndex = null;
            _originalSizeIndex = -1;
#endif
        }
    }
}
