using System.Runtime.InteropServices;

namespace PCFishController;

internal static class WindowVisibility
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    // STARTUPINFO 的 SW_HIDE 可使 WinForms Visible=true、原生窗口仍不可见。
    // 显式恢复用户打开的主窗口，同时保留最小化/托盘还原入口。
    internal static void Show(IntPtr window) => ShowWindow(window, 9); // SW_RESTORE
}
