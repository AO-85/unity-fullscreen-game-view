#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

public static class FullscreenGameView
{
    private static readonly Type GameViewType =
        typeof(Editor).Assembly.GetType("UnityEditor.GameView");

    private static readonly PropertyInfo ShowToolbarProperty =
        GameViewType?.GetProperty(
            "showToolbar",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

    private static EditorWindow instance;

    [DllImport("user32.dll")] 
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private static IntPtr gameViewHwnd = IntPtr.Zero;

    // Ctrl + Shift + Alt + X 
    [MenuItem("Window/Toggle Fullscreen Game View %#&x")]
    public static void ToggleFullscreen()
    {
        if (instance != null)
        {
            if (gameViewHwnd != IntPtr.Zero)
                SetWindowPos(gameViewHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_SHOWWINDOW);

            instance.Close();
            instance = null;
            gameViewHwnd = IntPtr.Zero;

            ShowWindow(FindWindow("Shell_TrayWnd", null), SW_SHOW);
            return;
        }

        ShowWindow(FindWindow("Shell_TrayWnd", null), SW_HIDE);

        instance = (EditorWindow)ScriptableObject.CreateInstance(GameViewType);
        ShowToolbarProperty?.SetValue(instance, false);

        var res = Screen.currentResolution;
        instance.ShowPopup();
        instance.position = new Rect(0, 0, res.width, res.height);
        instance.Focus();

        EditorApplication.delayCall += () =>
        {
            gameViewHwnd = GetForegroundWindow();

            if (gameViewHwnd != IntPtr.Zero)
            {
                SetWindowPos(
                    gameViewHwnd,
                    HWND_TOPMOST,
                    0, 0,
                    res.width, res.height,
                    SWP_SHOWWINDOW
                );
            }
        };
    }

    public static void ForceRestore()
    {
        if (instance != null) { instance.Close(); instance = null; }
        ShowWindow(FindWindow("Shell_TrayWnd", null), SW_SHOW);
    }
}

[InitializeOnLoad]
public static class FullscreenCleanup
{
    static FullscreenCleanup()
    {
        EditorApplication.quitting += FullscreenGameView.ForceRestore;
    }
}
#endif
