using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PCFishController;

/// <summary>隐藏 PCFish 的 BepInEx 日志控制台，并保存下一次启动的配置。</summary>
internal static class GameConsole
{
    private static readonly HashSet<string> Configured = new(StringComparer.OrdinalIgnoreCase);
    private static int _hiddenPid;

    internal static string DisableConsoleSetting(string text)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var found = false;
        var updated = Regex.Replace(text,
            @"(?ms)(^\[Logging\.Console\][^\r\n]*\r?\n)(.*?)(?=^\[|\z)", match =>
            {
                found = true;
                var body = match.Groups[2].Value;
                var key = @"(?m)^([ \t]*Enabled[ \t]*=[ \t]*)(?:true|false)[^\r\n]*";
                body = Regex.IsMatch(body, key, RegexOptions.IgnoreCase)
                    ? Regex.Replace(body, key, m => m.Groups[1].Value + "false", RegexOptions.IgnoreCase)
                    : "Enabled = false" + newline + body;
                return match.Groups[1].Value + body;
            });
        return found ? updated : text.TrimEnd() + newline + "[Logging.Console]" + newline + "Enabled = false" + newline;
    }

    internal static bool DisableInConfig(string gameDir)
    {
        if (!Directory.Exists(Path.Combine(gameDir, "BepInEx"))) return false;
        var configDir = Path.Combine(gameDir, "BepInEx", "config");
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "BepInEx.cfg");
        var before = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        var after = DisableConsoleSetting(before);
        if (before == after) return false;
        File.WriteAllText(path, after, new UTF8Encoding(false));
        return true;
    }

    internal static string EnsureHidden(string preferredDir)
    {
        var gameDir = GameDeploy.FindGameDir(preferredDir);
        string message = null;
        if (gameDir != null && Configured.Add(gameDir))
        {
            try { if (DisableInConfig(gameDir)) message = "已设置 BepInEx 控制台默认隐藏"; }
            catch (Exception ex) { message = "保存控制台设置失败：" + ex.Message; }
        }
        using var process = GameProcess.Find();
        if (process != null && process.Id != _hiddenPid && HideConsole(process.Id))
        {
            _hiddenPid = process.Id;
            message = "已隐藏游戏的 BepInEx 控制台";
        }
        return message;
    }

    private static bool HideConsole(int gamePid)
    {
        // 助手若已关联终端，保留其关联关系。
        if (GetConsoleWindow() != IntPtr.Zero || !AttachConsole((uint)gamePid)) return false;
        try
        {
            var window = GetConsoleWindow();
            if (window == IntPtr.Zero) return false;
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            if (!title.ToString().Contains("BepInEx", StringComparison.OrdinalIgnoreCase) ||
                !title.ToString().Contains("PCFish", StringComparison.OrdinalIgnoreCase)) return false;
            ShowWindow(window, 0); // SW_HIDE
            return true;
        }
        finally { FreeConsole(); }
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
}
