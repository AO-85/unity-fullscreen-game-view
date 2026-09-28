// Unity Fullscreen Game View
// https://github.com/AO-85/unity-fullscreen-game-view
// MIT License

#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace FullscreenGameViewTool
{
    /// <summary>
    /// Opens Unity's Game View as a borderless fullscreen window on the monitor
    /// under the mouse cursor. Default shortcut: F11 (rebindable in Edit > Shortcuts).
    /// </summary>
    public static class FullscreenGameView
    {
        private const string MenuPath = "Window/Toggle Fullscreen Game View";

        // Identifies our window among any other Game Views. Never visible: the popup has no
        // title bar. Looking the window up this way avoids instance-id APIs, which were
        // renamed in Unity 6.4.
        private const string WindowMarker = "FullscreenGameView.Instance";

        // EditorPrefs survives an editor restart, so we can recover the taskbar after a crash.
        private const string TaskbarHiddenKey = "FullscreenGameView.TaskbarHidden";

        private static readonly Type GameViewType =
            typeof(Editor).Assembly.GetType("UnityEditor.GameView");

        // "showToolbar" is an internal property in most versions, a field in some.
        private static readonly PropertyInfo ShowToolbarProperty =
            GameViewType?.GetProperty("showToolbar",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo ShowToolbarField =
            GameViewType?.GetField("showToolbar",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // ------------------------------------------------------------------
        // Entry points
        // ------------------------------------------------------------------

        [MenuItem(MenuPath, false, 1)]
        public static void ToggleFromMenu() => RequestToggle();

        [Shortcut("Window/Toggle Fullscreen Game View", KeyCode.F11)]
        private static void ToggleFromShortcut() => RequestToggle();

        private static double _lastToggleTime;
        private static bool _togglePending;

        /// <summary>
        /// Debounces the request and runs it on the next editor tick. Opening or closing an
        /// EditorWindow from inside GUI event processing can throw and silently abort, which
        /// is why nothing is done inline here.
        /// </summary>
        public static void RequestToggle()
        {
            // Two paths can deliver the same keystroke (the Shortcut Manager and the global
            // event hook below). Without this guard they would toggle twice and cancel out.
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

        /// <summary>
        /// The Shortcut Manager only sees a keystroke once the focused window forwards it,
        /// and in Play Mode the Game View forwards unmodified keys straight to the running
        /// game instead. This hook sits on EditorApplication's internal global event handler,
        /// which receives the event first and from every editor window.
        /// </summary>
        private static void HookGlobalKeyHandler()
        {
            var field = typeof(EditorApplication).GetField("globalEventHandler",
                BindingFlags.Static | BindingFlags.NonPublic);

            if (field == null)
            {
                // Not fatal: the Shortcut Manager binding and the menu item still work.
                Debug.LogWarning("[FullscreenGameView] Global key hook unavailable on this " +
                                 "Unity version. F11 may not respond while in Play Mode — " +
                                 "use the Window menu or bind a shortcut with modifiers.");
                return;
            }

            var handler = (EditorApplication.CallbackFunction)field.GetValue(null);
            handler -= OnGlobalKeyEvent;   // domain reloads can leave a stale subscription
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

        // ------------------------------------------------------------------
        // Open / close
        // ------------------------------------------------------------------

        private static void OpenFullscreen()
        {
            if (GameViewType == null)
            {
                Debug.LogError("[FullscreenGameView] UnityEditor.GameView not found. " +
                               "This Unity version is not supported.");
                return;
            }

            var bounds = Platform.GetFullscreenBounds();
            var foregroundBefore = Platform.GetForegroundWindowHandle();

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

            // The popup has no title bar, so this text is never drawn — it is purely a marker
            // that lets us find this exact window again after a script recompile.
            window.titleContent = new GUIContent(WindowMarker);

            Platform.SetTaskbarVisible(false);
            EditorPrefs.SetBool(TaskbarHiddenKey, true);

            // The native window does not exist until Unity has pumped a frame, and how many
            // frames that takes varies — especially in Play Mode. Retry instead of guessing.
            BeginTopmostRetry(bounds, foregroundBefore);
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
        }

        /// <summary>
        /// Closes the fullscreen window (if any) and restores the taskbar.
        /// Safe to call at any time.
        /// </summary>
        public static void ForceRestore()
        {
            TryGetOpenWindow(out var window);
            CloseFullscreen(window);
        }

        /// <summary>
        /// Finds the window by its marker title rather than by instance id, so it still works
        /// after a script recompile (static fields are reset, but the window itself survives)
        /// and stays free of APIs that were renamed in newer Unity versions.
        /// </summary>
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

        // ------------------------------------------------------------------
        // Safety nets
        // ------------------------------------------------------------------

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.quitting -= ForceRestore;
            EditorApplication.quitting += ForceRestore;

            HookGlobalKeyHandler();

            // If the editor crashed or was killed while fullscreen was active, the taskbar
            // is still hidden and no window exists. Put it back.
            if (EditorPrefs.GetBool(TaskbarHiddenKey, false) && !TryGetOpenWindow(out _))
            {
                Platform.SetTaskbarVisible(true);
                EditorPrefs.SetBool(TaskbarHiddenKey, false);
            }
        }

        // ==================================================================
        // Platform layer
        // ==================================================================

        private static class Platform
        {
#if UNITY_EDITOR_WIN

            // ---------------- Windows ----------------

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

            /// <summary>Bounds of the monitor the mouse cursor is currently on.</summary>
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

            /// <summary>Returns true once the window has actually been pinned.</summary>
            public static bool TryMakeTopmost(Rect bounds, IntPtr foregroundBefore)
            {
                var hwnd = GetForegroundWindow();

                // If focus has not moved yet, this is still Unity's main window. Pinning that
                // would leave the whole editor above everything — wait for the next frame.
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

            public static void SetTaskbarVisible(bool visible)
            {
                var command = visible ? SW_SHOW : SW_HIDE;

                var primary = FindWindow("Shell_TrayWnd", null);
                if (primary != IntPtr.Zero)
                    ShowWindow(primary, command);

                // Windows creates one Shell_SecondaryTrayWnd per additional monitor.
                var secondary = IntPtr.Zero;
                while ((secondary = FindWindowEx(IntPtr.Zero, secondary,
                           "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                {
                    ShowWindow(secondary, command);
                }
            }

#elif UNITY_EDITOR_OSX

            // ---------------- macOS ----------------
            // The Dock can be auto-hidden through System Events, which is a normal,
            // reversible user preference. Pinning a window above all others and reading
            // per-monitor bounds both need a native plugin, so they are no-ops here.

            public static IntPtr GetForegroundWindowHandle() => IntPtr.Zero;

            public static Rect GetFullscreenBounds()
            {
                var res = Screen.currentResolution;
                return new Rect(0, 0, res.width, res.height);
            }

            public static bool TryMakeTopmost(Rect bounds, IntPtr foregroundBefore) => true;

            public static void ClearTopmost() { }

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
                    var info = new System.Diagnostics.ProcessStartInfo("osascript", "-e \"" + script + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    System.Diagnostics.Process.Start(info);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[FullscreenGameView] Could not toggle the Dock: " + e.Message);
                }
            }

#else

            // ---------------- Linux / other ----------------
            // Panel behaviour is desktop-environment specific and there is no portable API,
            // so we make a best-effort attempt through wmctrl when it happens to be installed.

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
                    // wmctrl is not installed or this is Wayland — the window still opens,
                    // it just will not be pinned above the panel.
                }
            }

#endif
        }
    }
}
#endif
