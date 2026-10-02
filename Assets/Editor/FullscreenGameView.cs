#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace FullscreenGameViewTool
{
    public static class FullscreenGameView
    {
        private const string ToggleMenuPath = "Window/Toggle Fullscreen Game View";
        private const string RestoreMenuPath = "Window/Force Restore (Fullscreen Game View)";
        private const string DebugMenuPath = "Window/Fullscreen Game View Debug Log";

        private const string WindowMarker = "FullscreenGameView.Instance";
        private const string ActiveKey = "FullscreenGameView.Active";
        private const string RectKey = "FullscreenGameView.Rect";
        private const string DebugKey = "FullscreenGameView.DebugLog";
        private const string SessionOpenKey = "FullscreenGameView.SessionOpen";

        private const float RectTolerance = 6f;
        private const double WatchdogInterval = 0.3;
        private const double WatchdogGrace = 2.0;

        private static readonly Type GameViewType =
            typeof(Editor).Assembly.GetType("UnityEditor.GameView");

        private static readonly PropertyInfo ShowToolbarProperty = FindShowToolbarProperty();
        private static readonly FieldInfo ShowToolbarField = FindShowToolbarField();

        private static readonly FieldInfo HostViewField = typeof(EditorWindow).GetField(
            "m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);

        private const int ShowModePopupMenu = 1;

        private static double _lastToggleTime;
        private static bool _togglePending;
        private static IntPtr _editorWindowBefore = IntPtr.Zero;
        private static double _lastWatchdogRun;
        private static double _missingSince = -1;
        private static bool _seenAliveSinceReload;
        private static bool _reopenAttempted;

        [MenuItem(ToggleMenuPath, false, 1)]
        public static void ToggleFromMenu() => RequestToggle();

        [MenuItem(RestoreMenuPath, false, 2)]
        public static void ForceRestoreFromMenu() => ForceRestore();

        [MenuItem(DebugMenuPath, false, 3)]
        private static void ToggleDebugLogging()
        {
            var value = !EditorPrefs.GetBool(DebugKey, false);
            EditorPrefs.SetBool(DebugKey, value);
            Menu.SetChecked(DebugMenuPath, value);

            Debug.Log("[FullscreenGameView] Debug logging " + (value ? "enabled" : "disabled") +
                      "\n" + BuildDiagnostics());
        }

        private static string BuildDiagnostics()
        {
            var report = "Unity " + Application.unityVersion +
                         "\nGameView type: " + (GameViewType != null ? GameViewType.FullName : "NOT FOUND") +
                         "\nshowToolbar accessor: " + DescribeToolbarAccessor() +
                         "\nm_Parent accessor: " + (HostViewField != null ? "found" : "NOT FOUND") +
                         "\nEditor pixelsPerPoint: " + EditorGUIUtility.pixelsPerPoint +
                         "\nScreen.currentResolution: " + Screen.currentResolution.width +
                         "x" + Screen.currentResolution.height +
                         "\nActive flag: " + EditorPrefs.GetBool(ActiveKey, false) +
                         "\nStored rect: " + EditorPrefs.GetString(RectKey, "none") +
                         "\nPlaying: " + EditorApplication.isPlaying;

            foreach (var monitor in Platform.GetAllMonitorBounds())
                report += "\nMonitor bounds: " + monitor;

            if (GameViewType == null)
                return report;

            foreach (var candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (candidate == null || candidate.GetType() != GameViewType)
                    continue;

                report += "\nGameView: position " + candidate.position +
                          ", popup " + IsPopupWindow(candidate) +
                          ", title '" + (candidate.titleContent != null
                              ? candidate.titleContent.text
                              : "null") + "'";
            }

            return report;
        }

        [MenuItem(DebugMenuPath, true)]
        private static bool ToggleDebugLoggingValidate()
        {
            Menu.SetChecked(DebugMenuPath, EditorPrefs.GetBool(DebugKey, false));
            return true;
        }

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
            var windows = FindFullscreenWindows();

            Log("Toggle: found " + windows.Count + " window(s), active flag = " +
                EditorPrefs.GetBool(ActiveKey, false) + ", playing = " + EditorApplication.isPlaying);

            if (windows.Count > 0)
            {
                CloseFullscreen(windows);
                return;
            }

            if (EditorPrefs.GetBool(ActiveKey, false))
            {
                Log("Active flag was set but no window exists, treating this as an exit.");

                Platform.ClearTopmost();
                Cleanup();
                Platform.RestoreEditorFocus(_editorWindowBefore);
                _editorWindowBefore = IntPtr.Zero;
                return;
            }

            OpenFullscreen();
        }

        public static void ForceRestore()
        {
            Log("ForceRestore requested.");
            CloseFullscreen(FindFullscreenWindows());
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

            if (_editorWindowBefore == IntPtr.Zero)
                _editorWindowBefore = Platform.GetForegroundWindowHandle();

            Platform.SetTaskbarVisible(false);

            var window = (EditorWindow)ScriptableObject.CreateInstance(GameViewType);

            SetShowToolbar(window, false);

            window.ShowPopup();
            window.position = ToEditorPoints(bounds);
            window.Focus();

            ApplyIdentity(window);

            EditorPrefs.SetBool(ActiveKey, true);
            SessionState.SetBool(SessionOpenKey, true);
            SaveRect(window.position);

            _missingSince = -1;
            _seenAliveSinceReload = true;

            Log("Opened. Monitor " + bounds + ", pixelsPerPoint " + EditorGUIUtility.pixelsPerPoint +
                ", requested " + ToEditorPoints(bounds) + ", actual " + window.position);

            BeginTopmostRetry(bounds, _editorWindowBefore);
        }

        private static void CloseFullscreen(List<EditorWindow> windows)
        {
            Log("Closing " + windows.Count + " window(s).");

            Platform.ClearTopmost();

            foreach (var window in windows)
            {
                if (window != null)
                    window.Close();
            }

            Cleanup();

            Platform.RestoreEditorFocus(_editorWindowBefore);
            _editorWindowBefore = IntPtr.Zero;
        }

        private static void Cleanup()
        {
            Platform.SetTaskbarVisible(true);

            EditorPrefs.SetBool(ActiveKey, false);
            EditorPrefs.DeleteKey(RectKey);
            SessionState.EraseBool(SessionOpenKey);

            _missingSince = -1;
            _seenAliveSinceReload = false;
            _reopenAttempted = false;
        }

        private static List<EditorWindow> FindFullscreenWindows()
        {
            var found = new List<EditorWindow>();

            if (GameViewType == null)
                return found;

            var storedRect = default(Rect);
            var active = EditorPrefs.GetBool(ActiveKey, false);
            var hasStoredRect = active && TryLoadRect(out storedRect);
            var monitors = active ? Platform.GetAllMonitorBounds() : null;

            foreach (var candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (candidate == null || candidate.GetType() != GameViewType)
                    continue;

                var position = candidate.position;

                var marked = candidate.titleContent != null &&
                             candidate.titleContent.text == WindowMarker;

                var isPopup = IsPopupWindow(candidate);
                var matchesStored = hasStoredRect && RectsMatch(position, storedRect);
                var matchesMonitor = monitors != null && CoversWholeMonitor(position, monitors);

                if (!marked && !isPopup && !matchesStored && !matchesMonitor)
                    continue;

                if (!marked)
                {
                    Log("Re-identified window at " + position +
                        " (popup: " + isPopup +
                        ", stored match: " + matchesStored +
                        ", monitor match: " + matchesMonitor + ").");

                    ApplyIdentity(candidate);
                    SaveRect(position);
                }

                found.Add(candidate);
            }

            return found;
        }

        private static Rect ToEditorPoints(Rect physical)
        {
            var scale = EditorGUIUtility.pixelsPerPoint;

            if (scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale))
                scale = 1f;

            return new Rect(physical.x / scale, physical.y / scale,
                physical.width / scale, physical.height / scale);
        }

        private static bool IsPopupWindow(EditorWindow window)
        {
            try
            {
                var hostView = HostViewField?.GetValue(window);
                if (hostView == null)
                    return false;

                var containerProperty = hostView.GetType().GetProperty("window",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var container = containerProperty?.GetValue(hostView);
                if (container == null)
                    return false;

                var showModeProperty = container.GetType().GetProperty("showMode",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var showMode = showModeProperty?.GetValue(container);
                if (showMode == null)
                    return false;

                return Convert.ToInt32(showMode) == ShowModePopupMenu;
            }
            catch
            {
                return false;
            }
        }

        private static bool CoversWholeMonitor(Rect position, List<Rect> monitors)
        {
            foreach (var monitor in monitors)
            {
                if (RectsMatch(position, monitor))
                    return true;
            }

            return false;
        }

        private static void ApplyIdentity(EditorWindow window)
        {
            window.titleContent = new GUIContent(WindowMarker);
            SetShowToolbar(window, false);
            window.Repaint();
        }

        private static void SetShowToolbar(EditorWindow window, bool value)
        {
            if (ShowToolbarProperty != null)
            {
                ShowToolbarProperty.SetValue(window, value);
                return;
            }

            if (ShowToolbarField != null)
            {
                ShowToolbarField.SetValue(window, value);
                return;
            }

            Debug.LogWarning("[FullscreenGameView] Could not hide the Game View toolbar " +
                             "on this Unity version. Everything else still works.");
        }

        private static string DescribeToolbarAccessor()
        {
            if (ShowToolbarProperty != null)
                return "property on " + ShowToolbarProperty.DeclaringType.Name;

            if (ShowToolbarField != null)
                return "field " + ShowToolbarField.Name + " on " + ShowToolbarField.DeclaringType.Name;

            return "NOT FOUND";
        }

        private static PropertyInfo FindShowToolbarProperty()
        {
            for (var type = GameViewType; type != null; type = type.BaseType)
            {
                var property = type.GetProperty("showToolbar",
                    BindingFlags.Instance | BindingFlags.NonPublic |
                    BindingFlags.Public | BindingFlags.DeclaredOnly);

                if (property != null && property.CanWrite)
                    return property;
            }

            return null;
        }

        private static FieldInfo FindShowToolbarField()
        {
            string[] names = { "showToolbar", "m_ShowToolbar" };

            for (var type = GameViewType; type != null; type = type.BaseType)
            {
                foreach (var name in names)
                {
                    var field = type.GetField(name,
                        BindingFlags.Instance | BindingFlags.NonPublic |
                        BindingFlags.Public | BindingFlags.DeclaredOnly);

                    if (field != null && field.FieldType == typeof(bool))
                        return field;
                }
            }

            return null;
        }

        private static void SaveRect(Rect rect)
        {
            var value = string.Format(CultureInfo.InvariantCulture, "{0};{1};{2};{3}",
                rect.x, rect.y, rect.width, rect.height);

            EditorPrefs.SetString(RectKey, value);
        }

        private static bool TryLoadRect(out Rect rect)
        {
            rect = default;

            var raw = EditorPrefs.GetString(RectKey, string.Empty);
            if (string.IsNullOrEmpty(raw))
                return false;

            var parts = raw.Split(';');
            if (parts.Length != 4)
                return false;

            var values = new float[4];

            for (var i = 0; i < 4; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out values[i]))
                    return false;
            }

            rect = new Rect(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool RectsMatch(Rect a, Rect b)
        {
            return Mathf.Abs(a.x - b.x) <= RectTolerance &&
                   Mathf.Abs(a.y - b.y) <= RectTolerance &&
                   Mathf.Abs(a.width - b.width) <= RectTolerance &&
                   Mathf.Abs(a.height - b.height) <= RectTolerance;
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

            if (e == null || e.type != EventType.KeyDown)
                return;

            if (e.keyCode == KeyCode.F11)
            {
                e.Use();
                RequestToggle();
                return;
            }

            if (e.keyCode == KeyCode.Escape &&
                !EditorApplication.isPlaying &&
                EditorPrefs.GetBool(ActiveKey, false))
            {
                e.Use();
                RequestToggle();
            }
        }

        private static void Watchdog()
        {
            var now = EditorApplication.timeSinceStartup;

            if (now - _lastWatchdogRun < WatchdogInterval)
                return;

            _lastWatchdogRun = now;

            if (!EditorPrefs.GetBool(ActiveKey, false))
            {
                _missingSince = -1;
                return;
            }

            if (FindFullscreenWindows().Count > 0)
            {
                _missingSince = -1;
                _seenAliveSinceReload = true;
                return;
            }

            if (SessionState.GetBool(SessionOpenKey, false) &&
                !_seenAliveSinceReload &&
                !_reopenAttempted)
            {
                _reopenAttempted = true;

                Log("Watchdog: window did not survive the domain reload, reopening.");
                OpenFullscreen();
                return;
            }

            if (_missingSince < 0)
            {
                _missingSince = now;
                Log("Watchdog: window missing, starting grace period.");
                return;
            }

            if (now - _missingSince < WatchdogGrace)
                return;

            Log("Watchdog: window still missing after grace period, restoring taskbar.");

            Platform.ClearTopmost();
            Cleanup();

            Platform.RestoreEditorFocus(_editorWindowBefore);
            _editorWindowBefore = IntPtr.Zero;
        }

        private static void Log(string message)
        {
            if (EditorPrefs.GetBool(DebugKey, false))
                Debug.Log("[FullscreenGameView] " + message);
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.quitting -= ForceRestore;
            EditorApplication.quitting += ForceRestore;

            EditorApplication.update -= Watchdog;
            EditorApplication.update += Watchdog;

            HookGlobalKeyHandler();

            _lastWatchdogRun = 0;
            _missingSince = -1;
            _seenAliveSinceReload = false;
            _reopenAttempted = false;

            Log("Domain reload. Active flag = " + EditorPrefs.GetBool(ActiveKey, false) +
                ", session open = " + SessionState.GetBool(SessionOpenKey, false) +
                ", stored rect = " + EditorPrefs.GetString(RectKey, "none") +
                ", playing = " + EditorApplication.isPlaying);
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

            private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc,
                ref RECT rect, IntPtr data);

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
            private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

            [DllImport("user32.dll")]
            private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
                MonitorEnumProc callback, IntPtr data);

            private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

            private const uint SWP_SHOWWINDOW = 0x0040;
            private const uint MONITOR_DEFAULTTONEAREST = 2;
            private const int SW_HIDE = 0;
            private const int SW_SHOW = 5;

            private static IntPtr _fullscreenHwnd = IntPtr.Zero;

            public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

            public static List<Rect> GetAllMonitorBounds()
            {
                var result = new List<Rect>();

                MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };

                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var r = info.rcMonitor;
                        result.Add(new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top));
                    }

                    return true;
                };

                if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) ||
                    result.Count == 0)
                {
                    var res = Screen.currentResolution;
                    result.Add(new Rect(0, 0, res.width, res.height));
                }

                return result;
            }

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

                if (IsWindow(_fullscreenHwnd))
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

            public static List<Rect> GetAllMonitorBounds()
            {
                var res = Screen.currentResolution;
                return new List<Rect> { new Rect(0, 0, res.width, res.height) };
            }

            public static Rect GetFullscreenBounds()
            {
                var res = Screen.currentResolution;
                return new Rect(0, 0, res.width, res.height);
            }

            public static bool TryMakeTopmost(Rect bounds, IntPtr foregroundBefore) => true;

            public static void ClearTopmost() { }

            public static void RestoreEditorFocus(IntPtr preferred) { }

            public static void SetTaskbarVisible(bool visible)
            {
                var autohide = visible ? "false" : "true";

                try
                {
                    var arguments = "-e \"tell application \\\"System Events\\\" to tell " +
                                    "dock preferences to set autohide to " + autohide + "\"";

                    var info = new System.Diagnostics.ProcessStartInfo("osascript", arguments)
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

            public static IntPtr GetForegroundWindowHandle() => IntPtr.Zero;

            public static List<Rect> GetAllMonitorBounds()
            {
                var res = Screen.currentResolution;
                return new List<Rect> { new Rect(0, 0, res.width, res.height) };
            }

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
