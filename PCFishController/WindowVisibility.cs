using System.Runtime.InteropServices;

namespace PCFishController;

internal static class WindowVisibility
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool NativeIsWindowVisible(IntPtr window);

    // STARTUPINFO 的 SW_HIDE 可使 WinForms Visible=true、原生窗口仍不可见。
    // 显式恢复用户打开的主窗口，同时保留最小化/托盘还原入口。
    internal static void Show(IntPtr window) => ShowWindow(window, 9); // SW_RESTORE

    internal static void Hide(IntPtr window) => ShowWindow(window, 0); // SW_HIDE

    internal static bool IsVisible(IntPtr window)
        => window != IntPtr.Zero && IsWindow(window) && NativeIsWindowVisible(window);
}
