using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PCFishController;

/// <summary>
/// 控制器自己的配置，存在 exe 同目录的「PCFish控制器配置.json」。
/// 和游戏内的 cfg 分开：这里放"想怎么跑"，游戏内只留"允不允许跑"。
/// </summary>
internal sealed class AppSettings
{
    public int Port { get; set; } = 27777;

    /// <summary>界面语言，zh 表示简体中文，en 表示 English。</summary>
    public string Language { get; set; } = "zh";

    /// <summary>关闭按钮：ask 每次询问，exit 退出，tray 缩小至托盘。</summary>
    public string CloseBehavior { get; set; } = "ask";
    public bool HideGameConsole { get; set; } = true;

    /// <summary>自动繁育总开关。</summary>
    public bool AutoBreed { get; set; }

    /// <summary>
    /// 两次繁育之间的间隔下限（秒）。默认 20 分钟 = 1200 秒。
    /// 一颗心 6 分钟恢复、上限 5 颗，所以最密也就该在 30 分钟量级跑一轮；
    /// 设 20 分钟是为了在心接近满时提前消耗掉，避免"溢出浪费"。
    /// </summary>
    public int BreedIntervalMinSeconds { get; set; } = 900;

    /// <summary>两次繁育之间的间隔上限（秒）。默认 30 分钟 = 1800 秒。</summary>
    public int BreedIntervalMaxSeconds { get; set; } = 1800;

    /// <summary>繁殖最低稀有度门槛。0 = 不限。</summary>
    public int MinGrade { get; set; }

    /// <summary>图鉴目标鱼种。空值表示未设置目标。</summary>
    public string GoalType { get; set; } = "";

    /// <summary>图鉴目标星级，范围 1-5。</summary>
    public int GoalStar { get; set; } = 1;

    /// <summary>自动繁育是否按图鉴目标路线优先选鱼。</summary>
    public bool GoalEnabled { get; set; }

    /// <summary>每轮繁育结束后是否自动检查并执行鱼缸升级。</summary>
    public bool AutoUpgrade { get; set; } = true;

    public int MaxActions { get; set; } = 200;

    /// <summary>游戏根目录，用于一键部署。空值表示自动查找。</summary>
    public string GameDir { get; set; } = "";

    public int WinX { get; set; } = -1;
    public int WinY { get; set; } = -1;
    public int WinW { get; set; } = 780;
    public int WinH { get; set; } = 660;

    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    internal static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "PCFish控制器配置.json");

    internal static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch
        {
            // 配置坏了就用默认值，不拦用户
        }
        return new AppSettings();
    }

    internal void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opt), Encoding.UTF8);
        }
        catch
        {
        }
    }
}

/// <summary>
/// 简易日志：同时进界面文本框和 exe 同目录的 PCFish控制器.log。
/// 文件只保留最近 2000 行，避免像宝玉助手那样越滚越大。
/// </summary>
internal sealed class Logger
{
    private const int MaxLines = 2000;
    private readonly object _sync = new();
    private readonly string _path;

    internal Logger()
    {
        _path = Path.Combine(AppContext.BaseDirectory, "PCFish控制器.log");
    }

    internal event Action<string> LineWritten;

    internal void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_sync)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                TrimIfNeeded();
            }
            catch
            {
            }
        }
        LineWritten?.Invoke(line);
    }

    private void TrimIfNeeded()
    {
        var lines = File.ReadAllLines(_path, Encoding.UTF8);
        if (lines.Length <= MaxLines) return;
        File.WriteAllLines(_path, lines.Skip(lines.Length - MaxLines), Encoding.UTF8);
    }
}

internal static class Program
{
    /// <summary>崩溃也要留下现场，否则 WinExe 出事什么都不显示。</summary>
    internal static string CrashPath =>
        Path.Combine(AppContext.BaseDirectory, "PCFish控制器.crash.log");

    internal static void Mark(string stage, object detail = null)
    {
        try
        {
            File.AppendAllText(CrashPath,
                $"[{DateTime.Now:HH:mm:ss}] {stage}" + (detail == null ? "" : "\n" + detail) +
                Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

    [STAThread]
    private static void Main()
    {
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PCFishController.Show");
        using var singleInstance = new Mutex(true, "Local\\PCFishController", out var firstInstance);
        if (!firstInstance)
        {
            showRequest.Set();
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Mark("!! AppDomain 未处理异常", e.ExceptionObject);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Mark("!! 界面线程异常", e.Exception);

        try
        {
            Mark("1 进程启动");
            ApplicationConfiguration.Initialize();
            Mark("2 WinForms 初始化完成");
            var form = new MainForm();
            Mark("3 主窗口构造完成");
            using var activationTimer = new System.Windows.Forms.Timer { Interval = 200 };
            var initialShow = true;
            activationTimer.Tick += (_, _) =>
            {
                // 主窗口已创建后显示它；再次启动任一 exe 时唤回同一窗口。
                var requested = showRequest.WaitOne(0);
                if (!initialShow && !requested) return;
                initialShow = false;
                form.RestoreMainWindow();
            };
            activationTimer.Start();
            Application.Run(form);
            Mark("4 Application.Run 正常返回（窗口被关掉了）");
        }
        catch (Exception ex)
        {
            Mark("!! 主函数异常", ex.ToString());
        }
    }
}

/// <summary>查游戏进程。这就是"像宝玉助手一样监测进程"的部分。</summary>
internal static class GameProcess
{
    internal const string Name = "PCFish";

    internal static Process Find()
    {
        try
        {
            var list = Process.GetProcessesByName(Name);
            return list.Length > 0 ? list[0] : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string Describe()
    {
        var p = Find();
        if (p == null) return "未运行";
        try
        {
            return $"运行中  PID {p.Id}  内存 {p.WorkingSet64 / 1024 / 1024} MB";
        }
        catch
        {
            return "运行中";
        }
    }
}
