#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace FullscreenGameViewTool
{
    public static class FullscreenGameView
    {
        private const string MenuPath = "Window/Toggle Fullscreen Game View";
        private const string WindowMarker = "FullscreenGameView.Instance";
        private const string TaskbarHiddenKey = "FullscreenGameView.TaskbarHidden";

        private static readonly Type GameViewType =
            typeof(Editor).Assembly.GetType("UnityEditor.GameView");

        private static readonly PropertyInfo ShowToolbarProperty =
            GameViewType?.GetProperty("showToolbar",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo ShowToolbarField =
            GameViewType?.GetField("showToolbar",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static double _lastToggleTime;
        private static bool _togglePending;
        private static IntPtr _editorWindowBefore = IntPtr.Zero;

        [MenuItem(MenuPath, false, 1)]
        public static void ToggleFromMenu() => RequestToggle();

        [Shortcut("Window/Toggle Fullscreen Game View", KeyCode.F11)]
        private static void ToggleFromShortcut() => RequestToggle();

        public static void RequestToggle()
        {
            var now = EditorApplication.timeSinceStartup;

            if (_togglePending || now - _lastToggleTime < 0.25)
                return;

            _lastToggleTime = now;
            _togglePending = true;

            EditorApplication.delayCall += () =>
            {
                _togglePending = false;
                Toggle();
            };
        }

        public static void Toggle()
        {
            if (TryGetOpenWindow(out var window))
                CloseFullscreen(window);
            else
                OpenFullscreen();
        }

        private static void HookGlobalKeyHandler()
        {
            var field = typeof(EditorApplication).GetField("globalEventHandler",
                BindingFlags.Static | BindingFlags.NonPublic);

            if (field == null)
            {
                Debug.LogWarning("[FullscreenGameView] Global key hook unavailable on this " +
                                 "Unity version. F11 may not respond while in Play Mode - " +
                                 "use the Window menu or bind a shortcut with modifiers.");
                return;
            }

            var handler = (EditorApplication.CallbackFunction)field.GetValue(null);
            handler -= OnGlobalKeyEvent;
            handler += OnGlobalKeyEvent;
            field.SetValue(null, handler);
        }

        private static void OnGlobalKeyEvent()
        {
            var e = Event.current;

            if (e == null || e.type != EventType.KeyDown || e.keyCode != KeyCode.F11)
                return;

            e.Use();
            RequestToggle();
        }

        private static void OpenFullscreen()
        {
            if (GameViewType == null)
            {
                Debug.LogError("[FullscreenGameView] UnityEditor.GameView not found. " +
                               "This Unity version is not supported.");
                return;
            }

            var bounds = Platform.GetFullscreenBounds();

            _editorWindowBefore = Platform.GetForegroundWindowHandle();

            var window = (EditorWindow)ScriptableObject.CreateInstance(GameViewType);

            if (ShowToolbarProperty != null && ShowToolbarProperty.CanWrite)
                ShowToolbarProperty.SetValue(window, false);
            else if (ShowToolbarField != null)
                ShowToolbarField.SetValue(window, false);
            else
                Debug.LogWarning("[FullscreenGameView] Could not hide the Game View toolbar " +
                                 "on this Unity version. Everything else still works.");

            window.ShowPopup();
            window.position = bounds;
            window.Focus();
            window.titleContent = new GUIContent(WindowMarker);

            Platform.SetTaskbarVisible(false);
            EditorPrefs.SetBool(TaskbarHiddenKey, true);

            BeginTopmostRetry(bounds, _editorWindowBefore);
        }

        private static void BeginTopmostRetry(Rect bounds, IntPtr foregroundBefore)
        {
            var attempts = 0;
            EditorApplication.CallbackFunction step = null;

            step = () =>
            {
                attempts++;

                if (Platform.TryMakeTopmost(bounds, foregroundBefore) || attempts >= 60)
                    EditorApplication.update -= step;
            };

            EditorApplication.update += step;
        }

        private static void CloseFullscreen(EditorWindow window)
        {
            Platform.ClearTopmost();

            if (window != null)
                window.Close();

            Platform.SetTaskbarVisible(true);
            EditorPrefs.SetBool(TaskbarHiddenKey, false);

            Platform.RestoreEditorFocus(_editorWindowBefore);
            _editorWindowBefore = IntPtr.Zero;
        }

        public static void ForceRestore()
        {
            TryGetOpenWindow(out var window);
            CloseFullscreen(window);
        }

        private static bool TryGetOpenWindow(out EditorWindow window)
        {
            window = null;

            if (GameViewType == null)
                return false;

            foreach (var candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (candidate == null || candidate.GetType() != GameViewType)
                    continue;

                if (candidate.titleContent == null || candidate.titleContent.text != WindowMarker)
                    continue;

                window = candidate;
                return true;
            }

            return false;
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.quitting -= ForceRestore;
            EditorApplication.quitting += ForceRestore;

            HookGlobalKeyHandler();

            if (EditorPrefs.GetBool(TaskbarHiddenKey, false) && !TryGetOpenWindow(out _))
            {
                Platform.SetTaskbarVisible(true);
                EditorPrefs.SetBool(TaskbarHiddenKey, false);
            }
        }

        private static class Platform
        {
#if UNITY_EDITOR_WIN

            [StructLayout(LayoutKind.Sequential)]
            private struct POINT
            {
                public int x;
                public int y;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct RECT
            {
                public int left;
                public int top;
                public int right;
                public int bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct MONITORINFO
            {
                public int cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
            }

            [DllImport("user32.dll")]
            private static extern IntPtr FindWindow(string className, string windowName);

            [DllImport("user32.dll")]
            private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter,
                string className, string windowName);

            [DllImport("user32.dll")]
            private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                int x, int y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll")]
            private static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            private static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern bool GetCursorPos(out POINT point);

            [DllImport("user32.dll")]
            private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

            private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

            private const uint SWP_SHOWWINDOW = 0x0040;
            private const uint MONITOR_DEFAULTTONEAREST = 2;
            private const int SW_HIDE = 0;
            private const int SW_SHOW = 5;

            private static IntPtr _fullscreenHwnd = IntPtr.Zero;

            public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

            public static Rect GetFullscreenBounds()
            {
                if (GetCursorPos(out var cursor))
                {
                    var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };

                    if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                    {
                        var r = info.rcMonitor;
                        return new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
                    }
                }

                var res = Screen.currentResolution;
                return new Rect(0, 0, res.width, res.height);
            }

            public static bool TryMakeTopmost(Rect bounds, IntPtr foregroundBefore)
            {
                var hwnd = GetForegroundWindow();

                if (hwnd == IntPtr.Zero || hwnd == foregroundBefore)
                    return false;

                _fullscreenHwnd = hwnd;

                SetWindowPos(hwnd, HWND_TOPMOST,
                    (int)bounds.x, (int)bounds.y,
                    (int)bounds.width, (int)bounds.height,
                    SWP_SHOWWINDOW);

                return true;
            }

            public static void ClearTopmost()
            {
                if (_fullscreenHwnd == IntPtr.Zero)
                    return;

                SetWindowPos(_fullscreenHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_SHOWWINDOW);
                _fullscreenHwnd = IntPtr.Zero;
            }

            public static void RestoreEditorFocus(IntPtr preferred)
            {
                var hwnd = preferred;

                if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
                    hwnd = GetMainWindowOfThisProcess();

                if (hwnd != IntPtr.Zero)
                    SetForegroundWindow(hwnd);
            }

            private static IntPtr GetMainWindowOfThisProcess()
            {
                try
                {
                    using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        process.Refresh();
                        return process.MainWindowHandle;
                    }
                }
                catch
                {
                    return IntPtr.Zero;
                }
            }

            public static void SetTaskbarVisible(bool visible)
            {
                var command = visible ? SW_SHOW : SW_HIDE;

                var primary = FindWindow("Shell_TrayWnd", null);
                if (primary != IntPtr.Zero)
                    ShowWindow(primary, command);

                var secondary = IntPtr.Zero;
                while ((secondary = FindWindowEx(IntPtr.Zero, secondary,
                           "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                {
                    ShowWindow(secondary, command);
                }
            }

#elif UNITY_EDITOR_OSX

            public static IntPtr GetForegroundWindowHandle() => IntPtr.Zero;

            public static Rect GetFullscreenBounds()
            {
                var res = Screen.currentResolution;
                return new Rect(0, 0, res.width, res.height);
            }

            public static bool TryMakeTopmost(Rect bounds, IntPtr foregroundBefore) => true;

            public static void ClearTopmost() { }

            public static void RestoreEditorFocus(IntPtr preferred)
            {
                RunAppleScript("tell application id \\\"com.unity3d.UnityEditor5.x\\\" to activate");
            }

            public static void SetTaskbarVisible(bool visible)
            {
                var autohide = visible ? "false" : "true";
                RunAppleScript(
                    "tell application \\\"System Events\\\" to tell dock preferences " +
                    "to set autohide to " + autohide);
            }

            private static void RunAppleScript(string script)
            {
                try
                {
                    var info = new System.Diagnostics.ProcessStartInfo(
                        "osascript", "-e \"" + script + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    System.Diagnostics.Process.Start(info);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[FullscreenGameView] AppleScript call failed: " + e.Message);
                }
            }

#else

            public static IntPtr GetForegroundWindowHandle() => IntPtr.Zero;

            public static Rect GetFullscreenBounds()
            {
                var res = Screen.currentResolution;
                return new Rect(0, 0, res.width, res.height);
            }

            public static bool TryMakeTopmost(Rect bounds, IntPtr foregroundBefore)
            {
                TryWmctrl("-r :ACTIVE: -b add,above,fullscreen");
                return true;
            }

            public static void ClearTopmost()
            {
                TryWmctrl("-r :ACTIVE: -b remove,above,fullscreen");
            }

            public static void RestoreEditorFocus(IntPtr preferred)
            {
                TryWmctrl("-a Unity");
            }

            public static void SetTaskbarVisible(bool visible) { }

            private static void TryWmctrl(string arguments)
            {
                try
                {
                    var info = new System.Diagnostics.ProcessStartInfo("wmctrl", arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    System.Diagnostics.Process.Start(info);
                }
                catch
                {
                }
            }

#endif
        }
    }
}
#endif
