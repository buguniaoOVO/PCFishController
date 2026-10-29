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

    /// <summary>
    /// 连接方式：frida 只用内存连接（默认）；auto 优先 Frida、失败回退插件；plugin 只用旧插件桥接。
    /// </summary>
    public string ConnectionMode { get; set; } = "frida";

    /// <summary>Frida worker 的本机端口。只监听 127.0.0.1。</summary>
    public int FridaPort { get; set; } = 27778;

    /// <summary>界面语言，zh 表示简体中文，en 表示 English。</summary>
    public string Language { get; set; } = "zh";

    /// <summary>关闭按钮：ask 每次询问，exit 退出，tray 缩小至托盘。</summary>
    public string CloseBehavior { get; set; } = "ask";
    public bool HideGameConsole { get; set; } = true;

    /// <summary>启动时以及之后每小时自动检查一次 GitHub 上的新版本。</summary>
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>仓库总鱼数超过该值时自动合成。</summary>
    public int AutoMergeThreshold { get; set; } = 900;

    /// <summary>自动合成总开关。单独控制，不再依赖自动繁育是否开启。</summary>
    public bool AutoMerge { get; set; } = true;
    /// <summary>自动合成优先选择高稀有度鱼组。</summary>
    public bool AutoMergePreferHighRarity { get; set; } = true;
    /// <summary>自动合成排序模式：rarity_high/low 或 stars_high/low。</summary>
    public string AutoMergePriorityMode { get; set; } = "";

    /// <summary>自动合成时，是否保护市场价高于设定金额的鱼。</summary>
    public bool AutoMergeProtectMarketPriceAboveOneYuan { get; set; } = true;

    /// <summary>市场价保护金额，单位为人民币元。</summary>
    public decimal AutoMergeProtectionPriceYuan { get; set; } = 1.00m;

    /// <summary>自动合成排除的赛季编号。当前选项为 S1、S2。</summary>
    public List<int> AutoMergeExcludedSeasons { get; set; } = new() { 1, 2 };

    /// <summary>自动合成时排除对应赛季配方涉及的鱼种。</summary>
    public List<int> AutoMergeExcludedRecipeSeasons { get; set; } = new();

    /// <summary>自动合成排除的鱼种代码，例如 FS00028。为空表示不额外排除。</summary>
    public List<string> AutoMergeExcludedTypes { get; set; } = new();

    /// <summary>
    /// 赛季鱼材料凑齐就自动合成，无视仓库阈值。
    /// 赛季配方固定为「核心鱼×1 + 辅助鱼×3×3 = 10 条」，正好填满游戏合成槽。
    /// 默认关闭：开启后会在鱼群数量不足阈值时也发起合成。
    /// </summary>
    public bool AutoMergeSeasonalIgnoreThreshold { get; set; }


    /// <summary>定期刷新 Steam 市场报价并写入本地缓存。</summary>
    public bool AutoMarketRefresh { get; set; } = true;
    /// <summary>自动刷新和自动导出的间隔，分钟。</summary>
    public int MarketRefreshIntervalMinutes { get; set; } = 60;
    /// <summary>允许按价格条件自动导出到 Steam；默认关闭。</summary>
    public bool AutoSteamExport { get; set; }
    public decimal AutoSteamExportMinPriceYuan { get; set; }
    public decimal AutoSteamExportMaxPriceYuan { get; set; } = 100000m;
    public int AutoSteamExportBatchSize { get; set; } = 10;
    public List<int> SteamExportExcludedSeasons { get; set; } = new() { 2 };
    public List<int> SteamExportExcludedRecipeSeasons { get; set; } = new();
    public List<string> SteamExportExcludedTypes { get; set; } = new();

    /// <summary>合成路线目标筛选赛季，当前只显示 S1、S2。</summary>
    public int SynthesisSeasonFilter { get; set; } = 1;

    /// <summary>游戏锁帧：0 表示不限制，可选 10 或 30。</summary>
    public int FrameLimit { get; set; }

    /// <summary>多开管理：当前管理的游戏实例 PID，0 表示自动选第一个。</summary>
    public int SelectedInstancePid { get; set; }

    /// <summary>实例别名，键是 PID，值是用户起的名字。</summary>
    public Dictionary<string, string> InstanceNames { get; set; } = new();

    /// <summary>
    /// 合成收尾后的安全等待（秒）。
    /// 合成会触发结果动画和弹窗，助手要等动画结束、关掉弹窗，再等这段时间
    /// 才发起下一次操作，避免连着灌命令把服务器回包和界面状态搅乱。
    /// </summary>
    public int MergeSafetySeconds { get; set; } = 5;

    /// <summary>自动繁育总开关。</summary>
    public bool AutoBreed { get; set; }
    /// <summary>自动繁育优先选择高稀有度鱼。</summary>
    public bool AutoBreedPreferHighRarity { get; set; } = true;
    /// <summary>自动繁育排序模式：rarity_high/low 或 stars_high/low。</summary>
    public string AutoBreedPriorityMode { get; set; } = "";

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
                if (s != null)
                {
                    s.AutoMergeProtectionPriceYuan = Math.Clamp(s.AutoMergeProtectionPriceYuan, 0m, 100000m);
                    s.FridaPort = Math.Clamp(s.FridaPort, 1024, 65535);
                    if (s.ConnectionMode is not ("auto" or "frida" or "plugin")) s.ConnectionMode = "auto";
                    static List<int> Seasons(IEnumerable<int> values)
                        => (values ?? Enumerable.Empty<int>()).Where(x => x is 1 or 2).Distinct().OrderBy(x => x).ToList();
                    static string Priority(string value, bool legacyHigh)
                        => value is "rarity_high" or "rarity_low" or "stars_high" or "stars_low"
                            ? value : legacyHigh ? "rarity_high" : "rarity_low";

                    s.AutoMergeExcludedSeasons = Seasons(s.AutoMergeExcludedSeasons);
                    s.AutoMergeExcludedRecipeSeasons = Seasons(s.AutoMergeExcludedRecipeSeasons);
                    s.AutoMergeExcludedTypes ??= new List<string>();
                    s.AutoMergePriorityMode = Priority(s.AutoMergePriorityMode, s.AutoMergePreferHighRarity);
                    s.AutoMergePreferHighRarity = s.AutoMergePriorityMode == "rarity_high";
                    s.AutoBreedPriorityMode = Priority(s.AutoBreedPriorityMode, s.AutoBreedPreferHighRarity);
                    s.AutoBreedPreferHighRarity = s.AutoBreedPriorityMode == "rarity_high";
                    s.MarketRefreshIntervalMinutes = Math.Clamp(s.MarketRefreshIntervalMinutes, 15, 1440);
                    s.AutoSteamExportMinPriceYuan = Math.Clamp(s.AutoSteamExportMinPriceYuan, 0m, 100000m);
                    s.AutoSteamExportMaxPriceYuan = Math.Clamp(s.AutoSteamExportMaxPriceYuan, 0m, 100000m);
                    if (s.AutoSteamExportMaxPriceYuan < s.AutoSteamExportMinPriceYuan)
                        (s.AutoSteamExportMinPriceYuan, s.AutoSteamExportMaxPriceYuan) =
                            (s.AutoSteamExportMaxPriceYuan, s.AutoSteamExportMinPriceYuan);
                    s.AutoSteamExportBatchSize = Math.Clamp(s.AutoSteamExportBatchSize, 1, 100);
                    if (s.AutoSteamExport) s.AutoMarketRefresh = true;
                    s.SteamExportExcludedSeasons = Seasons(s.SteamExportExcludedSeasons);
                    s.SteamExportExcludedRecipeSeasons = Seasons(s.SteamExportExcludedRecipeSeasons);
                    s.SteamExportExcludedTypes ??= new List<string>();
                    if (s.SynthesisSeasonFilter is not (1 or 2)) s.SynthesisSeasonFilter = 1;
                    s.InstanceNames ??= new Dictionary<string, string>();
                    if (s.FrameLimit is not (0 or 10 or 30)) s.FrameLimit = 0;
                    return s;
                }
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

    internal static Process Find(int preferredPid = 0)
    {
        try
        {
            if (preferredPid > 0)
            {
                var selected = Process.GetProcessById(preferredPid);
                if (string.Equals(selected.ProcessName, Name, StringComparison.OrdinalIgnoreCase))
                    return selected;
                selected.Dispose();
            }
            var list = Process.GetProcessesByName(Name);
            return list.Length > 0 ? list[0] : null;
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<GameInstanceInfo> ListInstances()
    {
        var rows = new List<GameInstanceInfo>();
        try
        {
            foreach (var process in Process.GetProcessesByName(Name))
            {
                try
                {
                    process.Refresh();
                    var title = process.MainWindowTitle;
                    var path = "";
                    try { path = process.MainModule?.FileName ?? ""; } catch { }
                    rows.Add(new GameInstanceInfo(process.Id, title, path));
                }
                finally { process.Dispose(); }
            }
        }
        catch { }
        return rows.OrderBy(x => x.Pid).ToList();
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

internal sealed record GameInstanceInfo(int Pid, string WindowTitle, string Path)
{
    public override string ToString()
        => $"PID {Pid}" + (string.IsNullOrWhiteSpace(WindowTitle) ? "" : " · " + WindowTitle);
}
