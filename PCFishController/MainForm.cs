using System.Text;
using System.Text.Json;

namespace PCFishController;

/// <summary>
/// 控制器主窗口。**完全在游戏之外**——游戏窗口里一个像素都不画。
/// 这就是照抄宝玉助手的地方：无头桥接负责干活，界面、统计、配置全在外面的独立 exe 里。
///
/// 两个核心功能：
///   1. 繁育：每 15~30 分钟检查繁育计数器，并逐次繁育到零，优先消耗高稀有度鱼。
///      繁育消耗的是鱼缸上方那排心（体力）。一颗心 6 分钟恢复、上限 5 颗，
///      所以 20~30 分钟的节奏既不会溢出浪费，也不会空转。
///   2. 升级：每轮繁育结束后检查一次鱼缸等级，够条件就点 LV↑。
/// </summary>
internal sealed partial class MainForm : Form
{
private const string ExpectedBridgeVersion = "0.39.0";
    private readonly AppSettings _settings;
    private readonly Logger _log = new();
    private readonly IGameBridge _client;
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private bool _allowClose;
    private IntPtr _gameWindowHandle;
    private int _gameWindowProcessId = -1;
    private DateTime _nextConsoleCheckAt;
    /// <summary>开机自检状态。pending 时不允许连接游戏。</summary>
    private bool _startupCheckPending = true;
    private bool _startupCheckOk;
    private string _startupCheckDetail = "";
    private DateTime _nextUpdateCheckAt = DateTime.MinValue;
    private bool _updateCheckRunning;
    private string _notifiedUpdateTag = "";

    private readonly Label _lblProcess = new();
    private readonly Label _lblBridge = new();
    private readonly Label _lblFish = new();
    private readonly Label _lblGrades = new();
    private readonly Label _lblTank = new();
    private readonly Label _lblCharge = new();
    private readonly Label _lblLevel = new();
    private readonly Label _lblCountdown = new();

    private readonly CheckBox _chkAuto = new();
    private readonly CheckBox _chkAutoUpgrade = new();
    private readonly NumericUpDown _numIvMin = new();
    private readonly NumericUpDown _numIvMax = new();
    private readonly NumericUpDown _numMinGrade = new();
    private readonly NumericUpDown _numMergeThreshold = new();
    private readonly NumericUpDown _numMergeSafety = new();
    private readonly NumericUpDown _numMergeProtectionPrice = new();
    private readonly CheckBox _chkMerge = new();
    private readonly CheckBox _chkProtectMarketPrice = new();
    private readonly CheckBox _chkSeasonalIgnoreThreshold = new();
    private readonly ToolTip _mergeProtectionTip = new();
    private readonly CheckBox _chkAutoUpdate = new();
    private readonly Button _btnMergeNow = new();
    private readonly Label _lblMergeStatus = new();
    private readonly Label _lblMergePlan = new();
    private readonly Button _btnOnce = new();
    private readonly Button _btnUpgrade = new();
    private readonly Button _btnState = new();
    private readonly Button _btnHud = new();
    private readonly Button _btnArm = new();
    private readonly TextBox _txtLog = new();

    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Random _rng = new();

    private volatile bool _armed;
    private volatile bool _bridgeAllowsActions;
    private bool _readOnlyMode = true;
    private bool _bridgeVersionOk;
    private int _bridgeMinInterval = 30;
    private int _actionCount;
    private DateTime _lastActionAt = DateTime.MinValue;
    private bool _statePending;
    private DateTime _lastStateAt = DateTime.MinValue;
    private List<FishDto> _lastFish;
    private List<string> _collectionTypes = new();
    private GoalPlan _goalPlan = new();

    /// <summary>本次间隔的随机目标（秒）。每轮执行完重新掷一次。</summary>
    private int _nextIntervalSeconds = 900;
    private DateTime _nextCheckAt = DateTime.MinValue;
    private DateTime _nextActionAt = DateTime.MinValue;
    private DateTime _breedSettleAt = DateTime.MinValue;
    private bool _roundActive;
    private bool _awaitingBreed;
    private bool _awaitingMerge;
    private DateTime _mergeRequestedAt = DateTime.MinValue;
    private DateTime _nextMergeCheckAt = DateTime.MinValue;
    private DateTime _nextMarketRetryAt = DateTime.MinValue;
    private List<FishDto> _preMergeSnapshot;
    private readonly BreedRequestTracker _breedRequest = new();
    private DateTime _nextBreedStatusAt = DateTime.MinValue;
    private bool _recoverAfterBreedVerification;
    private DateTime _breedRequestedAt = DateTime.MinValue;
    private bool _upgradePending;
    private bool _awaitingUpgrade;
    private bool _awaitingTradeExport;
    private DateTime _tradeExportRequestedAt = DateTime.MinValue;
    private int _roundAttempts;
    private int _roundSuccesses;
    private readonly HashSet<string> _roundAvoidedFish = new();
    private int _verificationRetries;
    private int _preBreedCharge = -1;

    /// <summary>窗口自检状态，来自桥接的 tick / window 报文。</summary>
    private bool _windowLocked;
    private string _windowLockReason = "";
    private int _windowChecksOk;

    /// <summary>亲缘图谱：服务端拒绝直系亲子繁育（实测），这里记账避免重蹈覆辙。</summary>
    private readonly RelationStore _relations;
    private readonly FailedPairStore _failedPairs;
    /// <summary>正在等待对账的那笔繁育的双亲 id。对账后把新生儿记进图谱。</summary>
    private string _pendingParentA;
    private string _pendingParentB;
    private string _pendingNewFishId;

    /// <summary>当前鱼缸状态，来自 tick。</summary>
    private int _tankLevel = -1;
    private int _tankExp = -1;
    private bool _canLevelUp;
    private int _charge = -1;
    private int _secToHeart = -1;
    private bool _gameBusy = true;
    private string _gameBusyReason = "";
    private string _lastNetworkReadyReason = "";
    private bool _waitingForGameIdle;
    private string _actionPauseReason = "";
    /// <summary>
    /// 暂停后自动恢复的时刻。
    ///
    /// 【为什么需要它】
    ///   一次瞬时网络错误（例如 SSL/连接失败）会把动作暂停并要求点击 ARM。
    ///   但暂停只把 _armed 置回 false，而 BeginRound 的前置检查又要求 _armed，
    ///   于是没人点按钮时助手会永远停在那里 —— 日志却还写着「X 分钟后再次检查」。
    ///   对挂机场景这是致命的：用户不看界面就不会知道已经停了。
    ///   这里改成退避后自动恢复；真正结果未知的那种暂停仍保留较长的等待时间。
    /// </summary>
    private DateTime _autoResumeAt = DateTime.MinValue;
    private bool _serverInventoryReady;
    private bool _serverAuditPending;
    private bool _armAfterServerAudit;
    private bool _beginRoundAfterServerAudit;
    private DateTime _lastServerAuditAt = DateTime.MinValue;
    private DateTime _serverAuditRequestedAt;
    private DateTime _serverAuditRetryAt;
    private DateTime _serverAuditLogAt = DateTime.MinValue;
    private string _serverAuditWaitReason = "";
    private int _serverAuditBusyCount;
    /// <summary>连续核对失败的次数，用来做退避，避免失败时每几秒压着游戏问一次。</summary>
    private int _auditFailCount;
    private int _actionWaitSeconds;

    /// <summary>发起繁殖前的鱼群快照。繁殖成功后拿它做对照，把"到底消耗了什么"打出来。</summary>
    private List<FishDto> _preBreedSnapshot;

    private static readonly JsonSerializerOptions JsonOpt = new() { PropertyNameCaseInsensitive = true };

    internal MainForm()
    {
        _settings = AppSettings.Load();
        SteamMarket.ConfigureCache(_settings.MarketRefreshIntervalMinutes,
            Path.Combine(AppContext.BaseDirectory, "PCFish市场报价缓存.json"));
        var marketFetchedAt = SteamMarket.LastCached?.FetchedAt ?? DateTime.MinValue;
        _nextAutoMarketRefreshAt = marketFetchedAt == DateTime.MinValue
            ? DateTime.Now.AddSeconds(8)
            : marketFetchedAt.AddMinutes(_settings.MarketRefreshIntervalMinutes);
        _nextAutoExportAt = _settings.AutoSteamExport ? DateTime.Now.AddSeconds(8) : DateTime.MaxValue;
        UiLanguage.Set(_settings.Language);
        WikiDatabase.Load();
        // 从配置读自动繁育开关（默认关）。启动时不会自己动，但不再强制只读：
        // 连接核查已完成，结论是「只加载桥接不做动作时窗口完全正常」，
        // 出问题的是绕过游戏窗口生命周期去改 UI 的旧路径。现在动作只走数据/网络入口。
        _relations = new RelationStore(Path.Combine(AppContext.BaseDirectory, "PCFish亲缘关系.json"));
        _failedPairs = new FailedPairStore(Path.Combine(AppContext.BaseDirectory, "PCFish失败配对.json"));
        _client = CreateBridge(_settings);

        RollNextInterval();

        BuildUi();
        InitializeTrayIcon();
        WireEvents();

        _log.LineWritten += OnLogLine;
        _goalPlan = GoalPlanner.Build(_settings.GoalType, _settings.GoalStar, _collectionTypes, _lastFish);
        _log.Write(UiLanguage.T($"=== PCFish助手 v{UpdateChecker.CurrentVersion} 启动 ===（桥接端口 {_settings.Port}）"));
        _log.Write(UiLanguage.T($"配置文件：{AppSettings.FilePath}"));
        _log.Write(UiLanguage.T($"繁育节奏：每 {_settings.BreedIntervalMinSeconds / 60}~{_settings.BreedIntervalMaxSeconds / 60} 分钟检查，现有繁育计数器逐次用完"));
        _log.Write(UiLanguage.T("本程序不操作鼠标键盘、不向游戏窗口画任何东西。"));

        // 启动后约 3 秒做一次自动更新检查，先让界面和桥接连接稳定下来。
        if (_settings.AutoCheckUpdate) _nextUpdateCheckAt = DateTime.Now.AddSeconds(3);

        // 开机自检没通过之前不允许连接游戏：界面先显示「初始化中」，
        // 检测通过后才放行连接。
        // 只有自检通过才允许连接：检测中和检测失败都保持断开。
        _client.Start(() => _startupCheckOk);
        HideGameConsoleIfNeeded();

        _timer.Interval = 1000;
        _timer.Start();

        // 自检涉及磁盘和 BepInEx 目录，放到界面显示之后再跑，避免启动时卡一下。
        // 必须等窗口句柄建好（Load）才能弹对话框，所以在构造函数里挂 Load。
        Load += (_, _) => _ = RunStartupCheckAsync();
    }

    /// <summary>
    /// 按设置选连接方式。auto 优先 Frida 内存连接；worker 缺失或启动失败时，
    /// MainForm 会提示并允许一键切回插件桥接。
    /// </summary>
    private static IGameBridge CreateBridge(AppSettings settings)
    {
        var baseDir = AppContext.BaseDirectory;
        var worker = Path.Combine(baseDir, "PCFishFrida.exe");
        if (settings.ConnectionMode == "plugin" || !File.Exists(worker))
            return new BridgeClient("127.0.0.1", settings.Port);

        var frida = new FridaGameClient("127.0.0.1", settings.FridaPort, worker,
            Path.Combine(baseDir, "PCFishFridaClient.log"), settings.SelectedInstancePid);
        if (settings.ConnectionMode == "frida") return frida;
        return new AutoGameBridge(frida, new BridgeClient("127.0.0.1", settings.Port));
    }

    /// <summary>
    /// 启动自检：确认游戏环境和插件与助手一致。检测期间界面显示「初始化中」，
    /// 并且不允许连接；检测通过后才放行连接。
    /// </summary>
    private async Task RunStartupCheckAsync()
    {
        StartupCheckResult result;
        try
        {
            result = await Task.Run(() => StartupCheck.Run(_settings.GameDir, _client.RequiresPlugin));
        }
        catch (Exception ex)
        {
            result = new StartupCheckResult
            {
                Ok = false,
                Summary = "运行环境检测异常",
                BlockingReason = "检测运行环境时出错：" + ex.Message,
                FixableByDeploy = false
            };
        }

        _startupCheckOk = result.Ok;
        _startupCheckDetail = result.Summary;
        // 检测已经结束：无论通过与否都退出「初始化中」。
        // 通过就放行连接；没通过则停在「环境未就绪」，连接保持关闭。
        _startupCheckPending = false;
        foreach (var detail in result.Details) _log.Write("启动自检：" + detail);
        _log.Write("启动自检：" + (result.Ok ? "通过 —— " + result.Summary : "未通过 —— " + result.Summary));

        if (result.Ok)
        {
            // 通过：放行连接，让桥接立刻连上。
            UpdateStatusLabels();
            return;
        }

        _log.Write("启动自检未通过，已暂停连接：" + result.BlockingReason);
        UpdateStatusLabels();

        var buttons = result.FixableByDeploy
            ? MessageBoxButtons.YesNo
            : MessageBoxButtons.OK;
        var text = UiLanguage.T("运行环境检测未通过：") + result.Summary + Environment.NewLine + Environment.NewLine +
                   UiLanguage.T(result.BlockingReason) + Environment.NewLine;
        if (result.FixableByDeploy)
            text += Environment.NewLine + UiLanguage.T("是否现在执行一键部署来修好它？");

        var answer = MessageBox.Show(this, text,
            UiLanguage.T("PCFish 助手 · 初始化"),
            buttons, result.FixableByDeploy ? MessageBoxIcon.Question : MessageBoxIcon.Warning);

        if (result.FixableByDeploy && answer == DialogResult.Yes)
        {
            ShowPage("settings");
            await RunDeployAsync();
            // 部署完再自检一次，通过就自动放行连接。
            await RecheckStartupAsync();
            return;
        }

        VerifyFixPrompt();

    }

    /// <summary>
    /// 再自检一次。一键部署之后调用：修好了就放行连接，仍不行就提示用户手动处理。
    /// </summary>
    private async Task RecheckStartupAsync()
    {
        StartupCheckResult result;
        try
        {
            result = await Task.Run(() => StartupCheck.Run(_settings.GameDir, _client.RequiresPlugin));
        }
        catch (Exception ex)
        {
            result = new StartupCheckResult
            {
                Ok = false,
                Summary = "运行环境检测异常",
                BlockingReason = "检测运行环境时出错：" + ex.Message,
                FixableByDeploy = false
            };
        }

        foreach (var detail in result.Details) _log.Write("启动自检：" + detail);
        if (result.Ok)
        {
            _startupCheckOk = true;
            _startupCheckDetail = result.Summary;
            _startupCheckPending = false;
            _log.Write("启动自检复检通过：" + result.Summary + "；放行连接。");
            UpdateStatusLabels();
            return;
        }

        _startupCheckOk = false;
        _startupCheckDetail = result.Summary;
        _log.Write("启动自检复检仍未通过：" + result.BlockingReason);
        UpdateStatusLabels();
        MessageBox.Show(this,
            UiLanguage.T("运行环境仍未就绪：") + result.Summary + Environment.NewLine + Environment.NewLine +
            UiLanguage.T(result.BlockingReason) + Environment.NewLine + Environment.NewLine +
            UiLanguage.T("修好后点「重新检测」，助手才会连接游戏。"),
            UiLanguage.T("PCFish 助手 · 初始化"),
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        VerifyFixPrompt();
    }

    /// <summary>自检未通过时，问用户要不要再检测一次；通过才放行连接。</summary>
    private void VerifyFixPrompt()
    {
        var answer = MessageBox.Show(this,
            UiLanguage.T("修好之后点「是」重新检测；点「否」则暂不连接游戏。") + Environment.NewLine +
            UiLanguage.T("之后也可以随时点「重新检测」。"),
            UiLanguage.T("PCFish 助手 · 初始化"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) _ = RecheckStartupAsync();
    }

    /// <summary>用户手动点「重新检测」。</summary>
    private void RecheckStartupNow() => _ = RecheckStartupAsync();

    /// <summary>掷一次下一轮间隔。用户要的是"每个 20-30 分钟"，也就是在这区间里随机。</summary>
    private void RollNextInterval()
    {
        var lo = Math.Max(30, _settings.BreedIntervalMinSeconds);
        var hi = Math.Max(lo, _settings.BreedIntervalMaxSeconds);
        _nextIntervalSeconds = _rng.Next(lo, hi + 1);
    }

    // ------------------------------------------------------------------ 界面

    private void BuildUi()
    {
        BuildDashboardUi();
    }

    private void ExitAssistant()
    {
        if (IsDisposed || Disposing) return;
        _allowClose = true;
        SaveSettings();
        Close();
    }

    private IntPtr ResolveGameWindow()
    {
        var process = GameProcess.Find();
        if (process == null)
        {
            _gameWindowHandle = IntPtr.Zero;
            _gameWindowProcessId = -1;
            return IntPtr.Zero;
        }

        try
        {
            if (_gameWindowProcessId != process.Id)
            {
                _gameWindowHandle = IntPtr.Zero;
                _gameWindowProcessId = process.Id;
            }
            if (_gameWindowHandle != IntPtr.Zero && WindowVisibility.IsWindow(_gameWindowHandle))
                return _gameWindowHandle;

            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero && WindowVisibility.IsWindow(handle))
                _gameWindowHandle = handle;
            return _gameWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ToggleGameWindow()
    {
        var window = ResolveGameWindow();
        if (window == IntPtr.Zero)
        {
            _log.Write("未找到 PCFish 游戏窗口；请先启动游戏并显示其主窗口。");
            UpdateGameWindowButton();
            return;
        }

        if (WindowVisibility.IsVisible(window))
        {
            WindowVisibility.Hide(window);
            _log.Write("已隐藏 PCFish 游戏窗口。");
        }
        else
        {
            WindowVisibility.Show(window);
            _log.Write("已显示 PCFish 游戏窗口。");
        }
        UpdateGameWindowButton();
    }

    private void UpdateGameWindowButton()
    {
        if (_btnGameWindow == null || _btnGameWindow.IsDisposed) return;
        var window = ResolveGameWindow();
        _btnGameWindow.Enabled = window != IntPtr.Zero;
        _btnGameWindow.Text = UiLanguage.T(window != IntPtr.Zero && !WindowVisibility.IsVisible(window)
            ? "显示游戏窗口" : "隐藏游戏窗口");
    }

    private void InitializeTrayIcon()
    {
        _trayMenu = new ContextMenuStrip();
        var openItem = _trayMenu.Items.Add(UiLanguage.T("打开助手"));
        openItem.Click += (_, _) => RestoreFromTray();
        var updateItem = _trayMenu.Items.Add(UiLanguage.T("检查更新"));
        updateItem.Click += (_, _) =>
        {
            RestoreFromTray();
            _ = CheckForUpdateAsync();
        };

        var exitItem = _trayMenu.Items.Add(UiLanguage.T("退出助手"));
        exitItem.Click += (_, _) => ExitAssistant();

        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            Text = UiLanguage.T("PCFish 助手"),
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
        => RestoreMainWindow();

    internal void RestoreMainWindow()
    {
        if (IsDisposed || Disposing) return;
        _trayIcon.Visible = false;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        WindowVisibility.Show(Handle);
        Activate();
    }

    private enum CloseChoice { Cancel, Exit, MinimizeToTray }

    private CloseChoice AskCloseChoice()
    {
        using var dialog = new Form
        {
            Text = UiLanguage.T("关闭 PCFish 助手"),
            ClientSize = new Size(470, 190),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Bg,
            ForeColor = White,
            Font = Font
        };
        try { dialog.Icon = Icon; } catch { }
        var title = Label("确定要关闭 PCFish 助手吗？", 12, true);
        title.SetBounds(24, 24, 420, 34);
        var detail = Label("请选择退出助手、缩小到系统托盘，或取消返回。", 9, false, Muted);
        detail.SetBounds(24, 68, 425, 46);
        detail.TextAlign = ContentAlignment.MiddleLeft;

        var exit = ActionButton("退出助手", Color.FromArgb(250, 226, 230));
        exit.SetBounds(24, 130, 122, 38);
        exit.DialogResult = DialogResult.Yes;
        var minimize = ActionButton("缩小到托盘", Blue);
        minimize.SetBounds(164, 130, 155, 38);
        minimize.DialogResult = DialogResult.No;
        var cancel = ActionButton("取消");
        cancel.SetBounds(337, 130, 105, 38);
        cancel.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = cancel;
        dialog.Controls.AddRange(new Control[] { title, detail, exit, minimize, cancel });

        return dialog.ShowDialog(this) switch
        {
            DialogResult.Yes => CloseChoice.Exit,
            DialogResult.No => CloseChoice.MinimizeToTray,
            _ => CloseChoice.Cancel
        };
    }

    private void OnMainFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason != CloseReason.UserClosing)
        {
            SaveSettings();
            _trayIcon.Visible = false;
            _client.Dispose();
            return;
        }

        e.Cancel = true;
        var choice = _settings.CloseBehavior switch
        {
            "exit" => CloseChoice.Exit,
            "tray" => CloseChoice.MinimizeToTray,
            _ => AskCloseChoice()
        };
        switch (choice)
        {
            case CloseChoice.Exit:
                _allowClose = true;
                SaveSettings();
                _trayIcon.Visible = false;
                _client.Dispose();
                e.Cancel = false;
                break;
            case CloseChoice.MinimizeToTray:
                SaveSettings();
                ShowInTaskbar = false;
                Hide();
                _trayIcon.Visible = true;
                _trayIcon.ShowBalloonTip(1800, UiLanguage.T("PCFish 助手"), UiLanguage.T("助手已缩小到系统托盘。"), ToolTipIcon.Info);
                break;
        }
    }

    private void WireEvents()
    {
        _client.ConnectionChanged += OnConnectionChanged;
        _client.LineReceived += OnLine;

        _timer.Tick += OnTick;

        _chkAuto.CheckedChanged += (_, _) =>
        {
            _settings.AutoBreed = _chkAuto.Checked;
            if (_chkAuto.Checked)
            {
                _nextCheckAt = DateTime.Now;
                    _log.Write("已开启自动繁育，连接就绪后立即检查繁育计数器。");
                SendArm(true);
            }
            else
            {
                _autoResumeAt = DateTime.MinValue;
                SendArm(false);
                _roundActive = false;
                _upgradePending = false;
                _log.Write("已取消自动繁育，同时解除 ARM。");
            }
        };

        _chkAutoUpgrade.CheckedChanged += (_, _) =>
        {
            _settings.AutoUpgrade = _chkAutoUpgrade.Checked;
            _log.Write(_chkAutoUpgrade.Checked
                ? "已开启：每轮繁育结束后自动检查鱼缸升级。"
                : "已关闭自动升级。");
        };

        _numIvMin.ValueChanged += (_, _) =>
        {
            _settings.BreedIntervalMinSeconds = (int)_numIvMin.Value * 60;
            if (_numIvMax.Value < _numIvMin.Value) _numIvMax.Value = _numIvMin.Value;
            RollNextInterval();
        };

        _numIvMax.ValueChanged += (_, _) =>
        {
            _settings.BreedIntervalMaxSeconds = (int)_numIvMax.Value * 60;
            if (_numIvMin.Value > _numIvMax.Value) _numIvMin.Value = _numIvMax.Value;
            RollNextInterval();
        };

        _numMinGrade.ValueChanged += (_, _) => _settings.MinGrade = (int)_numMinGrade.Value;
        _cmbBreedRarityPriority.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbBreedRarityPriority.SelectedIndex < 0) return;
            _settings.AutoBreedPriorityMode = PriorityModeFromIndex(_cmbBreedRarityPriority.SelectedIndex);
            _settings.AutoBreedPreferHighRarity = _settings.AutoBreedPriorityMode == "rarity_high";
            _settings.Save();
        };
        _numMergeThreshold.ValueChanged += (_, _) =>
        {
            _settings.AutoMergeThreshold = (int)_numMergeThreshold.Value;
            _settings.Save();
        };
        _numMergeSafety.ValueChanged += (_, _) =>
        {
            _settings.MergeSafetySeconds = (int)_numMergeSafety.Value;
            _settings.Save();
        };

        _chkMerge.CheckedChanged += (_, _) =>
        {
            _settings.AutoMerge = _chkMerge.Checked;
            _settings.Save();
            if (_chkMerge.Checked)
            {
                _nextMergeCheckAt = DateTime.Now;
                _log.Write("已开启自动合成。");
            }
            else
            {
                _log.Write("已关闭自动合成。");
            }
            UpdateMergeStatus();
        };

        _chkProtectMarketPrice.CheckedChanged += (_, _) =>
        {
            _settings.AutoMergeProtectMarketPriceAboveOneYuan = _chkProtectMarketPrice.Checked;
            _settings.Save();
            if (_chkProtectMarketPrice.Checked) _nextMergeCheckAt = DateTime.Now;
            UpdateMergeStatus();
        };

        _chkSeasonalIgnoreThreshold.CheckedChanged += (_, _) =>
        {
            _settings.AutoMergeSeasonalIgnoreThreshold = _chkSeasonalIgnoreThreshold.Checked;
            _settings.Save();
            if (_chkSeasonalIgnoreThreshold.Checked) _nextMergeCheckAt = DateTime.Now;
            _log.Write(_chkSeasonalIgnoreThreshold.Checked
                ? "已开启：赛季鱼材料凑齐即合成，无视仓库阈值。"
                : "已关闭：赛季鱼无视阈值合成。");
            UpdateMergeStatus();
        };

        _numMergeProtectionPrice.ValueChanged += (_, _) =>
        {
            _settings.AutoMergeProtectionPriceYuan = _numMergeProtectionPrice.Value;
            _settings.Save();
            UpdateMergeStatus();
        };

        _cmbMergeRarityPriority.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbMergeRarityPriority.SelectedIndex < 0) return;
            _settings.AutoMergePriorityMode = PriorityModeFromIndex(_cmbMergeRarityPriority.SelectedIndex);
            _settings.AutoMergePreferHighRarity = _settings.AutoMergePriorityMode == "rarity_high";
            _settings.Save();
            UpdateMergeStatus();
        };

        _btnMergeNow.Click += (_, _) => MergeNow();

        _btnOnce.Click += (_, _) =>
        {
            if (_gameBusy)
            {
                _log.Write("游戏仍在启动、登录或处理请求；连接稳定后再开始繁育。");
                return;
            }
            if (!EnsureReadyForAction()) return;
            _log.Write("手动触发：开始一轮繁育。");
            BeginRound();
        };

        _btnUpgrade.Click += (_, _) =>
        {
            if (_gameBusy)
            {
                _log.Write("游戏仍在启动、登录或处理请求；连接稳定后再升级。");
                return;
            }
            if (!EnsureReadyForAction()) return;
            _log.Write("手动请求鱼缸升级检查。");
            SendUpgrade();
        };

        _btnState.Click += (_, _) =>
        {
            _log.Write("请求完整鱼群清单。");
            RequestState();
        };

        _btnArm.Click += (_, _) => { _autoResumeAt = DateTime.MinValue; SendArm(!_armed); };

        _btnHud.Click += (_, _) =>
        {
            if (!_client.Connected)
            {
                _log.Write("桥接未连接，跳过。");
                return;
            }
                _log.Write("请求读取游戏繁育计数器状态。");
            _client.Send("HUD");
        };

        FormClosing += OnMainFormClosing;
        FormClosed += (_, _) => _trayIcon?.Dispose();
    }

    private void HideGameConsoleIfNeeded()
    {
        if (!_settings.HideGameConsole) return;
        if (DateTime.UtcNow < _nextConsoleCheckAt) return;
        _nextConsoleCheckAt = DateTime.UtcNow.AddSeconds(5);
        var message = GameConsole.EnsureHidden(_settings.GameDir);
        if (message != null) _log.Write(message);
    }

    private void StartAllAutomation()
    {
        _chkAutoUpgrade.Checked = true;
        _chkMerge.Checked = true;
        _nextMergeCheckAt = DateTime.Now;
        if (!_chkAuto.Checked) _chkAuto.Checked = true;
        else { _nextCheckAt = DateTime.Now; SendArm(true); }
        SaveSettings();
        UpdateStatusLabels();
        _log.Write("已一键开启自动繁育、合成和升级检查。");
    }

    private void StopAllAutomation()
    {
        _autoResumeAt = DateTime.MinValue;
        _chkAuto.Checked = false;
        _chkAutoUpgrade.Checked = false;
        _chkMerge.Checked = false;
        _upgradePending = false;
        _nextActionAt = DateTime.MinValue;
        _armAfterServerAudit = _beginRoundAfterServerAudit = false;
        _roundActive = false;
        if (!_breedRequest.Active) _actionPauseReason = "";
        SendArm(false);
        SaveSettings();
        UpdateStatusLabels();
        _log.Write(_breedRequest.Active ? "已关闭自动运行，当前繁育继续核对结果。" : "已关闭全部自动运行。");
    }

    private void SaveSettings()
    {
        _settings.WinW = Width;
        _settings.WinH = Height;
        _settings.WinX = Location.X;
        _settings.WinY = Location.Y;
        _settings.AutoBreed = _chkAuto.Checked;
        _settings.AutoUpgrade = _chkAutoUpgrade.Checked;
        _settings.AutoBreedPriorityMode = PriorityModeFromIndex(_cmbBreedRarityPriority.SelectedIndex);
        _settings.AutoBreedPreferHighRarity = _settings.AutoBreedPriorityMode == "rarity_high";
        _settings.BreedIntervalMinSeconds = (int)_numIvMin.Value * 60;
        _settings.BreedIntervalMaxSeconds = (int)_numIvMax.Value * 60;
        _settings.MinGrade = (int)_numMinGrade.Value;
        _settings.AutoMergeThreshold = (int)_numMergeThreshold.Value;
        _settings.MergeSafetySeconds = (int)_numMergeSafety.Value;
        _settings.AutoMerge = _chkMerge.Checked;
        _settings.AutoMergePriorityMode = PriorityModeFromIndex(_cmbMergeRarityPriority.SelectedIndex);
        _settings.AutoMergePreferHighRarity = _settings.AutoMergePriorityMode == "rarity_high";
        _settings.AutoMergeProtectMarketPriceAboveOneYuan = _chkProtectMarketPrice.Checked;
        _settings.AutoMergeProtectionPriceYuan = _numMergeProtectionPrice.Value;
        _settings.AutoCheckUpdate = _chkAutoUpdate.Checked;
        _settings.Language = UiLanguage.Current;
        _settings.GoalType = _goalTypeCombo?.SelectedValue?.ToString() ?? _settings.GoalType;
        _settings.GoalStar = _goalStarCombo == null ? _settings.GoalStar : Math.Clamp(_goalStarCombo.SelectedIndex + 1, 1, 5);
        _settings.GoalEnabled = _chkGoal?.Checked ?? _settings.GoalEnabled;
        _settings.Save();
    }

    private void Ui(Action action)
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch
        {
        }
    }

    private void OnLogLine(string line)
    {
        Ui(() =>
        {
            _txtLog.AppendText(UiLanguage.T(line) + Environment.NewLine);
            if (_txtLog.Lines.Length > 600)
            {
                var keep = _txtLog.Lines.Skip(_txtLog.Lines.Length - 400).ToArray();
                _txtLog.Lines = keep;
            }
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        });
    }

    // ------------------------------------------------------------------ 桥接

    private void OnConnectionChanged(bool connected)
    {
        Ui(() =>
        {
            if (connected)
            {
                // Wait for the first readiness tick before any manual or automatic game action.
                _gameBusy = true;
                _gameBusyReason = "等待游戏登录或网络初始化";
                _log.Write($"已连上{_client.ModeName}");
                // 连上就先拉一次完整鱼群，界面上马上有东西看，不用等用户点
                if (_chkAuto.Checked)
                {
                    _nextCheckAt = DateTime.Now;
                }
                ApplyFrameLimit();
                RequestState();
            }
            else
            {
                _gameBusy = true;
                _gameBusyReason = "等待游戏连接";
                _armed = false;
                _bridgeVersionOk = false;
                _serverInventoryReady = false;
                _serverAuditPending = false;
                _armAfterServerAudit = false;
                _beginRoundAfterServerAudit = false;
                _serverAuditWaitReason = "";
                _serverAuditLogAt = DateTime.MinValue;
                _serverAuditBusyCount = 0;
                _autoResumeAt = DateTime.MinValue;
                _statePending = false;
                if (_breedRequest.Active)
                {
                    _breedRequest.MarkTimeout();
                    _actionPauseReason = "连接中断，正在自动核对本笔繁育结果";
                }
                else
                {
                    _awaitingBreed = false;
                    _preBreedSnapshot = null;
                    _pendingParentA = _pendingParentB = _pendingNewFishId = null;
                }
                EndRound();
                _log.Write(_client.ModeName + "断开（游戏没开或连接不可用）。2 秒后自动重试。");
            }
            UpdateStatusLabels();
        });
    }

    private void SendArm(bool on)
    {
        if (!_client.RequiresArm)
        {
            // Frida 自动连接没有单独 ARM 闸；安全熔断由暂停原因和恢复计时管理。
            _armed = on || string.IsNullOrWhiteSpace(_actionPauseReason);
            _client.Send(on ? "ARM 1" : "ARM 0");
            UpdateStatusLabels();
            return;
        }
        if (on && _startupCheckPending)
        {
            _log.Write("初始化中：正在检测运行环境，检测通过后才能放行动作。");
            return;
        }
        if (on && !_startupCheckOk)
        {
            _log.Write("运行环境检测未通过，暂不放行动作。请点「重新检测」或到「设置 → 一键部署」修复。");
            return;
        }
        if (on && _breedRequest.Active)
        {
            RequestBreedStatus();
            _log.Write("本笔繁育仍在对账，核实完成后恢复。");
            return;
        }
        if (on && !_bridgeVersionOk)
        {
            _armed = false;
            _log.Write("桥接版本尚未确认，暂不 ARM。");
            UpdateStatusLabels();
            return;
        }
        if (on && !_serverInventoryReady)
        {
            _armAfterServerAudit = true;
            // 上一次失败后可能还压着退避时间，这里要清掉，才能立刻重新核对。
            _serverAuditRetryAt = DateTime.MinValue;
            RequestServerAudit();
            return;
        }
        if (!on) { _armAfterServerAudit = false; _beginRoundAfterServerAudit = false; }
        // 用户手动点了 ARM，就不再需要自动恢复。
        if (on) _autoResumeAt = DateTime.MinValue;
        _client.Send(on ? "ARM 1" : "ARM 0");
        _armed = on;
        if (on) _actionPauseReason = "";
        UpdateStatusLabels();
    }

    private void RequestServerAudit()
    {
        if (!_client.SupportsServerAudit)
        {
            _serverInventoryReady = true;
            _lastServerAuditAt = DateTime.Now;
            _serverAuditPending = false;
            if (_armAfterServerAudit) { _armAfterServerAudit = false; SendArm(true); }
            if (_beginRoundAfterServerAudit) { _beginRoundAfterServerAudit = false; BeginRound(); }
            return;
        }
        if (_serverAuditPending || !_client.Connected || DateTime.Now < _serverAuditRetryAt) return;
        _serverAuditPending = true;
        _serverAuditRetryAt = DateTime.MinValue;
        _serverAuditRequestedAt = DateTime.Now;
        _client.Send("SERVERAUDIT");

        // 游戏刚启动时登录和首个网络请求还没走完，桥接会连续回 busy，控制器每 3 秒重试。
        // 那种情况下这条日志会被刷成几百行，把真正有用的信息淹掉。改成节流：首次一定打，
        // 之后每 30 秒最多一条，并把桥接给出的等待原因带上。
        if (_serverAuditWaitReason.Length > 0)
        {
            if ((DateTime.Now - _serverAuditLogAt).TotalSeconds < 30) return;
            _log.Write("仍在只读核对服务器繁育次数：" + _serverAuditWaitReason);
        }
        else
        {
            if (_serverAuditLogAt != DateTime.MinValue &&
                (DateTime.Now - _serverAuditLogAt).TotalSeconds < 30) return;
            _log.Write("正在只读核对服务器繁育次数，核对完成后再选鱼。");
        }
        _serverAuditLogAt = DateTime.Now;
    }

    private void RequestState()
    {
        if (!_client.Connected)
        {
            _log.Write("桥接未连接，跳过。");
            return;
        }
        _statePending = true;
        _lastStateAt = DateTime.Now;
        _client.Send("STATE");
    }

    private void ApplyFrameLimit()
    {
        if (!_client.Connected || _client.RequiresPlugin) return;
        var requested = _settings.FrameLimit is 10 or 30 ? _settings.FrameLimit : 0;
        _client.Send("FRAMELIMIT " + requested);
        _log.Write(requested == 0 ? "已请求恢复游戏默认帧率。" : $"已请求将游戏锁帧为 {requested} FPS。");
    }

    private void RequestBreedStatus()
    {
        if (!_client.Connected || !_breedRequest.Waiting || DateTime.Now < _nextBreedStatusAt) return;
        _nextBreedStatusAt = DateTime.Now.AddSeconds(15);
        _client.Send("BREEDSTATUS " + _breedRequest.Id);
    }

    private void WaitForBreedResult()
    {
        _breedRequest.MarkTimeout();
        _actionPauseReason = "回包较慢，正在自动核对本笔繁育结果";
        _autoResumeAt = DateTime.MinValue;
        SendArm(false);
        EndRound();
        RequestBreedStatus();
    }

    private void SendUpgrade()
    {
        if (!_client.Connected)
        {
            _log.Write("桥接未连接，跳过。");
            return;
        }
        _log.Write("→ 请求鱼缸升级");
        _client.Send("UPGRADE");
        _lastActionAt = DateTime.Now;
    }

    /// <summary>调用游戏原生交易入口；自动发送只能由用户在市场设置中主动开启。</summary>
    private bool SendTradeExport(IReadOnlyList<string> ids, bool automated = false)
    {
        if (ids == null || ids.Count == 0) return false;
        if (_client.RequiresPlugin)
        {
            _log.Write("Steam 导出需要 Frida 内存连接。");
            return false;
        }
        if (_awaitingTradeExport)
        {
            _log.Write("上一笔 Steam 导出仍在等待游戏回报。");
            return false;
        }
        if (!EnsureReadyForAction())
        {
            _log.Write("Steam 导出未发送：连接或动作安全条件未就绪。");
            return false;
        }
        // 注意不要再把 _roundActive 放进这个条件：自动轮次开启后它几乎一直是 true，
        // 导出会被永久挡掉（日志表现为「游戏或助手正在处理其他动作」）。
        // 真正代表动作占用的 _gameBusy / _awaiting* 已经覆盖，轮次间隙允许导出。
        if (_gameBusy || _statePending || _serverAuditPending ||
            _awaitingBreed || _awaitingMerge || _awaitingUpgrade)
        {
            _log.Write("Steam 导出未发送：游戏或助手正在处理其他动作。");
            return false;
        }
        if (_actionWaitSeconds > 0)
        {
            _log.Write($"Steam 导出未发送：需再等待 {_actionWaitSeconds} 秒以满足动作间隔。");
            return false;
        }

        _client.Send("TRADEEXPORT " + string.Join(" ", ids));
        _lastActionAt = DateTime.Now;
        _awaitingTradeExport = true;
        _tradeExportRequestedAt = DateTime.Now;
        _log.Write(automated
            ? $"自动导出：已请求把 {ids.Count} 条鱼送入 Steam Inventory，等待游戏回报。"
            : $"已请求游戏导出 {ids.Count} 条鱼至 Steam Inventory，等待游戏回包核对。");
        return true;
    }

    /// <summary>自动合成的触发入口：仓库超过阈值就发起一次。</summary>
    private void MaybeAutoMerge()
    {
        if (!_settings.AutoMerge) return;
        if (_lastFish == null || _lastFish.Count <= _settings.AutoMergeThreshold) return;
        // 繁育或升级进行中时不要插队，等它们结束再合。
        if (_roundActive || _awaitingBreed || _breedRequest.Active) return;
        if (TrySendMerge(force: false, out var reason)) return;
        // 没发出去就把下一次检查推后，避免每个心跳都重算一遍。
        if (_nextMergeCheckAt <= DateTime.Now) _nextMergeCheckAt = DateTime.Now.AddSeconds(15);
        if (reason.Length > 0) _log.Write(reason);
    }

    /// <summary>
    /// 赛季鱼达标即合成：材料凑齐 10 条就发一笔，无视仓库阈值。
    /// 赛季配方固定为「核心鱼×1 + 辅助鱼×3×3 = 10 条」，与游戏合成槽格数一致，
    /// 所以不需要额外填充材料，也不会因为凑不够格而浪费鱼。
    /// </summary>
    private void MaybeSeasonalMergeIgnoreThreshold()
    {
        if (!_settings.AutoMergeSeasonalIgnoreThreshold) return;
        if (_lastFish == null || _lastFish.Count < 10) return;
        if (_roundActive || _awaitingBreed || _breedRequest.Active) return;
        if (_awaitingMerge || _awaitingTradeExport) return;

        var season = _settings.SynthesisSeasonFilter;
        // 按目标鱼品质降序：神话(tier5) 优先。赛季配方是唯一能稳定产出神话鱼的途径，
        // 而普通合成要求同品质同星凑满 10 条，神话鱼数量永远不够，只能靠配方产。
        // 两个赛季都尝试（S1 祭典章鱼 / S2 鬼火灯笼水母都是神话），用户选的赛季排前面。
        var candidates = WikiDatabase.Data.Recipes
            .OrderByDescending(recipe => FishCatalog.Find(recipe.Target)?.Tier ?? 0)
            .ThenBy(recipe => recipe.Season == season ? 0 : 1)
            .ToList();

        foreach (var recipe in candidates)
        {
            var picks = PickSeasonalMaterials(recipe);
            if (picks == null) continue;

            var target = FishCatalog.Find(recipe.Target);
            var label = string.IsNullOrWhiteSpace(target?.Name) ? recipe.Target : target.Name;
            _awaitingMerge = true;
            _mergeRequestedAt = DateTime.Now;
            _preMergeSnapshot = _lastFish;
            _log.Write($"赛季鱼材料已齐（S{recipe.Season} → {label}），无视阈值发起合成：{string.Join(",", picks)}");
            _client.Send("MERGE " + string.Join(" ", picks));
            _lastActionAt = DateTime.Now;
            return;
        }
    }

    /// <summary>按赛季配方凑 10 条材料；凑不齐、或配方不是标准 10 格时返回 null。</summary>
    private List<string> PickSeasonalMaterials(WikiRecipe recipe)
    {
        var ingredients = recipe.Ingredients ?? new List<WikiIngredient>();
        if (ingredients.Count == 0) return null;
        if (ingredients.Sum(i => i.Count) != 10) return null;

        // 按 (鱼型, 星级) 分桶，只取可合成的鱼（无繁育次数、未锁定、未展示）
        var pools = new Dictionary<(string Type, int Star), List<FishDto>>();
        foreach (var fish in _lastFish ?? new List<FishDto>())
        {
            if (!Selection.CanMerge(fish)) continue;
            var key = (fish.ty ?? "", fish.stars);
            if (!pools.TryGetValue(key, out var list)) pools[key] = list = new List<FishDto>();
            list.Add(fish);
        }

        var picks = new List<string>();
        foreach (var ingredient in ingredients)
        {
            var key = (ingredient.Type ?? "", ingredient.Star);
            if (!pools.TryGetValue(key, out var pool) || pool.Count < ingredient.Count) return null;
            for (var i = 0; i < ingredient.Count; i++) picks.Add(pool[i].id);
            pool.RemoveRange(0, ingredient.Count);
        }
        return picks.Count == 10 ? picks : null;
    }

    /// <summary>
    /// 发起一次合成。force=true 时忽略库存阈值（用户在页面上点「立即合成一次」）。
    /// 返回 false 表示没有发出去，reason 说明原因（空字符串表示只是安静地等待）。
    /// </summary>
    private bool TrySendMerge(bool force, out string reason)
    {
        reason = "";
        if (_awaitingTradeExport) { reason = "Steam 鱼类导出正在等待游戏回报。"; return false; }
        if (_awaitingMerge) { reason = "上一笔合成还在进行中，稍候。"; return false; }
        if (!_chkMerge.Checked && !force) return false;
        if (_startupCheckPending || !_startupCheckOk)
        {
            reason = "运行环境尚未就绪，暂不合成。";
            return false;
        }
        if ((_client.RequiresArm && !_armed) || !_bridgeVersionOk || !_bridgeAllowsActions)
        {
            reason = "自动合成未执行：需要先放行动作（ARM）并保持桥接连接。";
            return false;
        }
        if (!_client.Connected) { reason = "自动合成未执行：桥接未连接。"; return false; }
        if (_gameBusy)
        {
            reason = "游戏当前繁忙" + (string.IsNullOrWhiteSpace(_gameBusyReason) ? "" : "（" + _gameBusyReason + "）") + "，稍后再合成。";
            _nextMergeCheckAt = DateTime.Now.AddSeconds(60);
            return false;
        }
        if (_windowLocked) { reason = "窗口自检已锁停动作：" + _windowLockReason; return false; }
        if (_lastFish == null) { reason = "还没读到鱼群数据，稍候。"; return false; }
        if (_upgradePending || _awaitingUpgrade || _statePending || _serverAuditPending)
        {
            reason = "正在处理其它动作，稍后再合成。";
            return false;
        }
        if (!force && _actionWaitSeconds > 0)
        {
            _nextMergeCheckAt = DateTime.Now.AddSeconds(_actionWaitSeconds);
            return false;
        }

        var market = SteamMarket.Cached;
        if (_settings.AutoMergeProtectMarketPriceAboveOneYuan && market == null)
        {
            if (DateTime.Now < _nextMarketRetryAt)
            {
                reason = "Steam 市场价格暂不可用，保护模式下暂停合成并等待重试。";
                _nextMergeCheckAt = _nextMarketRetryAt;
                return false;
            }
            if (!_marketBusy) _ = RefreshMarketAsync();
            reason = "正在扫描 Steam 市场近期成交价；扫描完成后再自动合成。";
            _nextMergeCheckAt = DateTime.Now.AddSeconds(_marketBusy ? 60 : 15);
            return false;
        }

        var picks = Selection.PickMergeCandidates(_lastFish, 10, market,
            _settings.AutoMergeProtectMarketPriceAboveOneYuan, _settings.AutoMergeProtectionPriceYuan,
            _settings.AutoMergePriorityMode,
            new HashSet<int>(_settings.AutoMergeExcludedSeasons ?? new List<int>()),
            new HashSet<string>(_settings.AutoMergeExcludedTypes ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
            WikiDatabase.RecipeFishTypes(_settings.AutoMergeExcludedRecipeSeasons));
        if (picks.Count < 10)
        {
            reason = "没有凑齐 10 条同稀有度、同星级的可合成鱼；已排除赛季鱼" +
                     (_settings.AutoMergeProtectMarketPriceAboveOneYuan
                         ? $"及市场保护价高于 ¥{_settings.AutoMergeProtectionPriceYuan:0.00} 的鱼"
                         : "") +
                     (_settings.AutoMergeExcludedRecipeSeasons.Count > 0 ? "及已勾选赛季的配方鱼" : "") +
                     "，暂不合成。";
            _nextMergeCheckAt = DateTime.Now.AddSeconds(120);
            return false;
        }

        _awaitingMerge = true;
        _mergeRequestedAt = DateTime.Now;
        _preMergeSnapshot = _lastFish;
        _log.Write(force
            ? $"手动触发合成：{string.Join(",", picks.Select(p => p.id))}"
            : $"仓库 {_lastFish.Count} 条超过 {_settings.AutoMergeThreshold}，发起自动合成：{string.Join(",", picks.Select(p => p.id))}");
        _client.Send("MERGE " + string.Join(" ", picks.Select(p => p.id)));
        _lastActionAt = DateTime.Now;
        return true;
    }

    /// <summary>
    /// 自动检查更新：启动时一次，之后每小时一次。
    /// 检查在后台线程跑，结果回到界面线程；发现新版本时写日志、更新设置页状态，
    /// 并在托盘弹出提示。已经提示过的版本不会重复打扰。
    /// </summary>
    private async void MaybeAutoCheckUpdate()
    {
        if (!_settings.AutoCheckUpdate) return;
        if (_updateCheckRunning) return;
        if (DateTime.Now < _nextUpdateCheckAt) return;

        _updateCheckRunning = true;
        // 先排下一次，避免检查失败时每个心跳都重试。
        _nextUpdateCheckAt = DateTime.Now.AddHours(1);
        try
        {
            var info = await UpdateChecker.CheckAsync();
            Ui(() =>
            {
                // 把结果同步到设置页，用户在界面上能看到同一条状态。
                ApplyUpdateResult(info);
                if (!info.Ok)
                {
                    _log.Write("自动检查更新：" + info.Message);
                    return;
                }
                if (!info.HasUpdate)
                {
                    _log.Write($"自动检查更新：已是最新版本（{info.CurrentVersion}）。");
                    return;
                }

                _log.Write($"自动检查更新：" + info.Message);
                if (!string.IsNullOrWhiteSpace(info.ReleaseUrl))
                    _log.Write("发布页：" + info.ReleaseUrl);

                if (string.Equals(_notifiedUpdateTag, info.LatestTag, StringComparison.Ordinal)) return;
                _notifiedUpdateTag = info.LatestTag;
                NotifyUpdateAvailable(info);
            });
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                _log.Write("自动检查更新失败：" + ex.Message);
                _updateCheckRunning = false;
                _nextUpdateCheckAt = DateTime.Now.AddSeconds(60);
            });
            return;
        }
        _updateCheckRunning = false;
    }

    /// <summary>发现新版本时提示用户：托盘气泡 + 缩到托盘时也看得到。</summary>
    private void NotifyUpdateAvailable(UpdateInfo info)
    {
        var message = info.LatestTag.Length > 0
            ? $"发现新版本 {info.LatestTag}（当前 {info.CurrentVersion}）。到「设置 → 版本更新」一键更新。"
            : "发现新版本。到「设置 → 版本更新」一键更新。";
        try
        {
            _trayIcon.Visible = true;
            _trayIcon.ShowBalloonTip(5000, UiLanguage.T("PCFish 助手有新版本"),
                UiLanguage.T(message), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Write("弹出更新提示失败：" + ex.Message);
        }
    }

    /// <summary>手动触发一次合成，忽略库存阈值。结果通过日志说明。</summary>
    private void MergeNow()
    {
        if (TrySendMerge(force: true, out var reason) || reason.Length == 0) return;
        _log.Write(reason);
        UpdateMergeStatus();
    }

    private static string DescribeNetworkReadyReason(string reason) => reason switch
    {
        "steam_not_initialized" => "Steam 初始化尚未完成",
        "game_network_type_unavailable" => "游戏网络类型未找到",
        "game_network_manager_unavailable" => "游戏网络管理器尚未创建",
        "transport_getter_unavailable" => "游戏网络接口尚未就绪",
        "transport_unavailable" => "游戏网络实例尚未创建",
        "session_field_unavailable" => "游戏会话字段不可读取",
        "session_token_empty" => "游戏会话凭据尚未就绪（仅检查存在性，不读取或记录内容）",
        "session_check_error" => "游戏会话检查遇到异常",
        "network_request_busy" => "游戏正在处理网络请求",
        "game_input_blocked" => "游戏输入被提示窗口阻塞",
        "session_stabilizing" => "游戏会话已出现，正在等待稳定",
        "ready" => "游戏会话已稳定",
        _ => "未知状态（" + reason + ")"
    };

    private void OnLine(string line) => Ui(() => ProcessLine(line));

    private void ProcessLine(string line)
    {
        if (line.Length == 0) return;
        if (line[0] != '{')
        {
            // PONG / ARMED x cfg=y 这类简单回复
            return;
        }

        BridgeMessage msg;
        try
        {
            msg = JsonSerializer.Deserialize<BridgeMessage>(line, JsonOpt);
        }
        catch (Exception ex)
        {
            _log.Write($"桥接报文解析失败（长度 {line.Length}）：{ex.GetType().Name} {ex.Message}");
            return;
        }
        if (msg?.type == null) return;

        switch (msg.type)
        {
            case "hello":
                if (!string.IsNullOrWhiteSpace(msg.apiStatus)) _log.Write(msg.apiStatus);
                _bridgeVersionOk = msg.ver == ExpectedBridgeVersion;
                _readOnlyMode = false;
                _bridgeMinInterval = Math.Max(60, msg.minInterval);
                _bridgeAllowsActions = msg.cfg;
                if (!string.IsNullOrWhiteSpace(msg.mode))
                    _log.Write($"连接方式：{_client.ModeName}" + (_client.RequiresPlugin ? "（游戏目录内有插件）" : "（不向游戏目录部署插件）"));
                if (!_client.SupportsServerAudit)
                {
                    _serverInventoryReady = true;
                    _lastServerAuditAt = DateTime.Now;
                }
                _log.Write($"桥接版本 {msg.ver}，最小动作间隔 {_bridgeMinInterval} 秒，" +
                           (msg.cfg ? "游戏内已放行动作" : "游戏内 cfg 尚未放行动作"));
                if (!_bridgeVersionOk) _log.Write($"桥接版本 {msg.ver} 与控制器协议（{ExpectedBridgeVersion}）不匹配，已阻止动作。");
                _chkAuto.Enabled = _bridgeVersionOk;
                _chkAutoUpgrade.Enabled = _bridgeVersionOk;
                _btnOnce.Enabled = _bridgeVersionOk;
                _btnUpgrade.Enabled = _bridgeVersionOk;
                _btnArm.Enabled = _bridgeVersionOk;
                if (_breedRequest.Active)
                {
                    RequestBreedStatus();
                    if (!_breedRequest.Waiting) RequestState();
                    break;
                }
                if (_bridgeVersionOk && _bridgeAllowsActions && _chkAuto.Checked &&
                    string.IsNullOrEmpty(_actionPauseReason))
                {
                    SendArm(true);
                    _log.Write(_client.SupportsServerAudit
                        ? "桥接版本已核实，等待服务器库存核对后放行。"
                        : "连接已核实，放行动作。");
                }
                break;

            case "tick":
                Ui(() =>
                {
                    _bridgeAllowsActions = !_readOnlyMode && msg.cfg;
                    _armed = _client.RequiresArm ? msg.armed : string.IsNullOrWhiteSpace(_actionPauseReason);
                    _gameBusy = msg.gameBusy;
                    _gameBusyReason = msg.networkBusy ? "游戏网络请求中"
                        : msg.inputBlocked ? "游戏提示窗口阻塞输入"
                        : !_client.RequiresPlugin && !msg.networkReady ? "等待助手确认游戏会话"
                        : msg.gameBusy ? "助手动作处理中" : "";
                    if (!_client.RequiresPlugin && _client.Connected)
                    {
                        var reason = msg.networkReady ? "ready" : msg.networkReadyReason ?? "unknown";
                        if (reason != _lastNetworkReadyReason)
                        {
                            _lastNetworkReadyReason = reason;
                            if (reason == "ready")
                                _log.Write("助手已确认游戏会话稳定，允许继续游戏操作。");
                            else
                                _log.Write("助手连接已建立，游戏操作暂缓；会话检查状态：" + DescribeNetworkReadyReason(reason) + "。");
                        }
                    }
                    _serverInventoryReady = msg.serverInventoryReady;
                    _actionWaitSeconds = msg.actionWaitSeconds;
                    _actionCount = msg.actions;

                    if (msg.game && msg.fish == null)
                    {
                        _lblFish.Text = UiLanguage.T($"鱼群：总数 {msg.total}   可繁育 {msg.breedable}   " +
                                        $"次数耗尽 {msg.exhausted}   合成上限 {msg.maxMerge}");
                    }
                else if (!msg.game)
                    {
                        _lblFish.Text = UiLanguage.T("鱼群：游戏数据还没就绪（可能还在读档）");
                    }

                    // game=false 的 tick 不带 charge/tank 字段（JSON 缺字段会反序列化成 0），
                    // 直接赋值会把真实计数器清零、导致"明明有计数器却判没有"。未就绪时保留旧值。
                    if (msg.game)
                    {
                        _tankLevel = msg.tankLevel;
                        _tankExp = msg.tankExp;
                        _canLevelUp = msg.canLevelUp;
                        _charge = msg.charge;
                        _secToHeart = msg.secToHeart;
                        _windowChecksOk = msg.windowChecksOk;
                        if (msg.windowLocked && !_windowLocked)
                            _log.Write("⚠ 窗口自检已锁停动作：" + msg.windowLockReason);
                        _windowLocked = msg.windowLocked;
                        _windowLockReason = msg.windowLockReason ?? "";
                    }

                    _lblTank.Text = UiLanguage.T("鱼缸：" + (msg.summary ?? "—"));

                    var heartHint = _secToHeart > 0
                        ? $"　下一颗心约 {_secToHeart / 60.0:F1} 分钟后"
                        : (_charge >= 5 ? "　已满" : "");
                    var chargeUnit = UiLanguage.IsEnglish ? " slots" : " 格";
                    _lblCharge.Text = UiLanguage.T($"繁育计数器：{_charge} / 5") + chargeUnit + UiLanguage.T(heartHint);

                    _lblLevel.Text = UiLanguage.T(_tankLevel < 0
                        ? "鱼缸等级：—"
                        : $"鱼缸等级：{_tankLevel}　经验 {_tankExp}" +
                          (_canLevelUp ? "　★ 可升级！" : ""));

                    UpdateStatusLabels();
                });
                break;

            case "hud":
                _log.Write("游戏心位状态：" + (msg.detail ?? "(无细节)"));
                break;

            case "window":
                _windowLocked = msg.windowLocked;
                _windowLockReason = msg.windowLockReason ?? "";
                _windowChecksOk = msg.windowChecksOk;
                _log.Write((msg.windowLocked ? "⚠ 窗口自检：" : "窗口自检：") + (msg.detail ?? "(无细节)"));
                UpdateStatusLabels();
                break;


            case "state":
                _statePending = false;
                _lastStateAt = DateTime.Now;
                if (!msg.ok)
                {
                    _log.Write("鱼群请求失败：" + msg.msg);
                    if (!_breedRequest.Active)
                    {
                        _preBreedSnapshot = null;
                        _pendingParentA = _pendingParentB = _pendingNewFishId = null;
                    }
                    EndRound();
                    if (_breedRequest.Active && !_breedRequest.Waiting)
                        _breedSettleAt = DateTime.Now.AddSeconds(15);
                    break;
                }
                _lastFish = msg.fish ?? new List<FishDto>();
                _collectionTypes = msg.collectionTypes ?? _lastFish.Select(f => f.ty).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _goalPlan = GoalPlanner.Build(_settings.GoalType, _settings.GoalStar, _collectionTypes, _lastFish);
                RefreshWarehouse();
                RefreshCollectionPage();
                RefreshSynthesisPage();
                _charge = msg.charge;
                _tankLevel = msg.tankLevel;
                _tankExp = msg.tankExp;
                _canLevelUp = msg.canLevelUp;
                Ui(() =>
                {
                    _lblGrades.Text = UiLanguage.T("稀有度分布：" + Selection.DescribeGrades(_lastFish) +
                                      $"　可繁育最高稀有度：{FishCatalog.RarityName(Selection.HighestBreedableGrade(_lastFish))}");
                    _lblFish.Text = UiLanguage.T("鱼群：" + Selection.Summarize(_lastFish));
                });
                _log.Write($"收到鱼群：{Selection.Summarize(_lastFish)}");
                if (_preBreedSnapshot != null && !_awaitingBreed)
                {
                    var before = _preBreedSnapshot;
                    var bornIds = new List<string>();
                    var diffChanged = LogBreedDiff(before, _lastFish, bornIds);
                    var oldA = before.Find(f => f.id == _pendingParentA);
                    var oldB = before.Find(f => f.id == _pendingParentB);
                    var newA = _lastFish.Find(f => f.id == _pendingParentA);
                    var newB = _lastFish.Find(f => f.id == _pendingParentB);
                    var parentsConsumed = oldA != null && oldB != null && newA != null && newB != null &&
                                          newA.bc < oldA.bc && newB.bc < oldB.bc;
                    var returnedChildPresent = !string.IsNullOrWhiteSpace(_pendingNewFishId) &&
                                               bornIds.Contains(_pendingNewFishId);
                    // 计数器可能被恢复机制回填，也可能单独变化；只有本笔亲鱼或回包中的新鱼能证实繁育。
                    if (!parentsConsumed && !returnedChildPresent)
                    {
                        if (_verificationRetries++ == 0)
                        {
                            _log.Write($"繁育尚未核实：亲鱼次数和本笔新鱼均未变化（心 {_preBreedCharge}→{_charge}），再读取一次。");
                            RequestState();
                            break;
                        }
                        _log.Write($"繁育核实失败：亲鱼和本笔新鱼均无变化（心 {_preBreedCharge}→{_charge}），本轮停止；不计入成功次数。");
                        _preBreedSnapshot = null;
                        _pendingParentA = _pendingParentB = _pendingNewFishId = null;
                        _breedRequest.Clear();
                        _recoverAfterBreedVerification = false;
                        _actionPauseReason = "本笔鱼群变化未核实，请检查游戏状态";
                        SendArm(false);
                        EndRound();
                        break;
                    }
                    _preBreedSnapshot = null;
                    if (diffChanged && bornIds.Count > 0)
                    {
                        // 只记录本笔回包指定的新鱼，避免把玩家同时手动繁育出的鱼误记为这对双亲的孩子。
                        IEnumerable<string> ownChildren = returnedChildPresent
                            ? new[] { _pendingNewFishId }
                            : parentsConsumed && bornIds.Count == 1 ? bornIds : new List<string>();
                        foreach (var childId in ownChildren)
                        {
                            _relations.AddChild(_pendingParentA, childId);
                            _relations.AddChild(_pendingParentB, childId);
                        }
                        _relations.Prune(new HashSet<string>(_lastFish.Select(f => f.id)));
                        _relations.Save();
                    }
                    _pendingParentA = null;
                    _pendingParentB = null;
                    _pendingNewFishId = null;
                    _log.Write($"繁育核实成功：本笔亲鱼次数下降或新鱼已出现，本轮第 {_roundAttempts} 次（心 {_preBreedCharge}→{_charge}）。");
                    _roundSuccesses++;
                    _breedRequest.Clear();
                    if (_recoverAfterBreedVerification)
                    {
                        _recoverAfterBreedVerification = false;
                        _actionPauseReason = "";
                        _lastServerAuditAt = DateTime.MinValue;
                        _serverInventoryReady = false;
                        _nextCheckAt = DateTime.Now;
                        if (_chkAuto.Checked) _autoResumeAt = DateTime.Now;
                        _log.Write("繁育结果已核实，自动恢复繁育检查。");
                    }
                }
                if (_roundActive) MaybeBreed(_lastFish);
                break;

            case "result":
                OnResult(msg);
                break;
        }
    }

    private void OnResult(BridgeMessage msg)
    {
        if (msg.cmd == "serveraudit")
        {
            _serverAuditPending = false;
            _serverInventoryReady = msg.ok;
            if (!msg.ok)
            {
                if (msg.errorKind == "busy")
                {
                    _serverAuditWaitReason = msg.msg ?? "";
                    // 游戏还在登录时这会是连续 busy。固定 3 秒重试在启动阶段会连打几十次，
                    // 改成逐步退避，最大 30 秒，既不影响登录完成后的速度，也不再压着游戏问。
                    _serverAuditBusyCount++;
                    var backoff = Math.Min(30, 3 * _serverAuditBusyCount);
                    _serverAuditRetryAt = DateTime.Now.AddSeconds(backoff);
                    return;
                }
                // 失败不是致命错误：多数是网络抖动，或刚好撞上服务器维护。
                // 记录原因并安排退避重试，不要求用户手动点 ARM，挂机时也能自己恢复。
                _auditFailCount++;
                var retryDelay = Math.Min(300, 15 * _auditFailCount);
                _serverInventoryReady = false;
                _autoResumeAt = DateTime.Now.AddSeconds(retryDelay);
                _actionPauseReason = "服务器次数核对失败，稍后自动重试";
                SendArm(false);
                _log.Write($"服务器库存核对未完成：{msg.msg}" +
                    $"；{retryDelay} 秒后自动重试第 {_auditFailCount} 次。");
                EndRound();
                return;
            }
            _lastServerAuditAt = DateTime.Now;
            _serverAuditWaitReason = "";
            _serverAuditBusyCount = 0;
            _auditFailCount = 0;
            try
            {
                using var report = JsonDocument.Parse(msg.msg);
                var n = report.RootElement.GetProperty("countMismatches").GetInt32();
                _log.Write($"服务器库存已核对：本地与服务器次数不一致 {n} 条；选鱼按服务器资格筛选。");
            }
            catch { _log.Write("服务器库存已核对。"); }
            if (_armAfterServerAudit) { _armAfterServerAudit = false; SendArm(true); }
            if (_beginRoundAfterServerAudit)
            {
                _beginRoundAfterServerAudit = false;
                BeginRound();
            }
            else RequestState();
            return;
        }
        if (msg.cmd == "hidepopup")
        {
            // 「隐藏繁育弹窗」的回执。失败要出声，成功只在真正改变时记一行。
            if (!msg.ok) _log.Write("「隐藏繁育弹窗」未生效：" + (msg.msg ?? ""));
            else if (msg.msg != null && msg.msg.Contains("→"))
                _log.Write("游戏设置：" + msg.msg);
            return;
        }

        if (msg.cmd == "upgrade")
        {
            _upgradePending = false;
            _awaitingUpgrade = false;
            _log.Write(msg.ok
                ? $"✓ 鱼缸升级成功：{msg.msg}"
                : $"鱼缸升级未执行：{msg.msg}");
            return;
        }

        if (msg.cmd == "tradeexport")
        {
            _awaitingTradeExport = false;
            _tradeExportRequestedAt = DateTime.MinValue;
            _log.Write(msg.ok
                ? "✓ Steam 导出已由游戏确认：" + msg.msg
                : "Steam 导出未完成：" + msg.msg);
            if (!msg.ok && msg.errorKind == "timeout")
            {
                _actionPauseReason = "Steam 导出回包超时，请核对游戏仓库与 Steam Inventory 后再继续";
                _autoResumeAt = DateTime.MinValue;
                if (_client.RequiresArm) SendArm(false);
            }
            RequestState();
            return;
        }

        if (msg.cmd == "merge")
        {
            _awaitingMerge = false;
            _preMergeSnapshot = null;
            if (msg.ok)
            {
                _log.Write("✓ 自动合成成功：" + msg.msg + "；刷新鱼群。");
                // 合成结果动画和弹窗已在桥接侧收尾；这里再等用户设定的安全时间，
                // 让服务器回包和界面稳定下来，才发起下一次操作。
                var safety = Math.Clamp(_settings.MergeSafetySeconds, 1, 60);
                _nextMergeCheckAt = DateTime.Now.AddSeconds(3 + safety);
                RequestState();
            }
            else
            {
                _log.Write("自动合成未完成：" + msg.msg);
                if (msg.errorKind is "busy" or "not_sent")
                    _nextMergeCheckAt = DateTime.Now.AddSeconds(60);
                else
                {
                    _actionPauseReason = "自动合成异常，确认游戏状态后点击 ARM 恢复";
                    _autoResumeAt = DateTime.MinValue;
                    SendArm(false);
                }
            }
            return;
        }

        if (msg.cmd != "breed" || !_breedRequest.Accept(msg.requestId, msg.terminal && msg.errorKind != "uncertain")) return;
        if (!msg.terminal || msg.errorKind == "uncertain")
        {
            if (_actionPauseReason != "回包较慢，正在自动核对本笔繁育结果")
                _log.Write("繁育回包较慢，保留本笔记录，自动等待最终结果。");
            WaitForBreedResult();
            return;
        }
        _awaitingBreed = false;
        if (msg.ok)
        {
            _recoverAfterBreedVerification = _breedRequest.TimedOut;
            _pendingNewFishId = msg.id;
            _log.Write($"繁育回包报告成功：{msg.msg}；等待鱼群核实。");
            _breedSettleAt = DateTime.Now.AddSeconds(3);
        }
        else
        {
            var elapsed = (DateTime.Now - _breedRequestedAt).TotalSeconds;
            if (msg.errorKind == "game_state" && !string.IsNullOrWhiteSpace(msg.id) && _preBreedSnapshot != null)
            {
                _pendingNewFishId = msg.id;
                _recoverAfterBreedVerification = true;
                _verificationRetries = 0;
                _serverInventoryReady = false;
                _actionPauseReason = "繁育回包待核实，正在刷新鱼群";
                _autoResumeAt = DateTime.MinValue;
                _roundActive = false;
                _nextActionAt = DateTime.MinValue;
                _breedSettleAt = DateTime.Now.AddSeconds(3);
                SendArm(false);
                _log.Write($"繁育回包附带结果 ID，原生收尾尚待确认（{elapsed:F1}秒）；已保留本笔记录，3 秒后核对鱼群，核验成功后自动恢复。");
                return;
            }
            _log.Write($"繁育请求失败（{elapsed:F1}秒）：{msg.msg}");
            _preBreedSnapshot = null;
            _pendingParentA = _pendingParentB = _pendingNewFishId = null;
            _breedSettleAt = DateTime.MinValue;
            _breedRequest.Clear();
            if (msg.errorKind == "busy" && _roundActive)
            {
                _nextActionAt = DateTime.Now.AddSeconds(60);
                return;
            }
            if (!string.IsNullOrEmpty(msg.errorKind))
            {
                // 回包超时属于“本笔结果未知”，但连接本身还在；按瞬时失败处理后自动重试，
                // 否则挂机时会一直停在这里等人点 ARM。
                var retryable = msg.errorKind is "transport" or "not_sent" ||
                                (msg.msg?.Contains("回包超时", StringComparison.Ordinal) ?? false);
                _actionPauseReason = retryable ? "本笔已结束，等待网络恢复后重新核对" : "游戏接口或收尾异常：" + msg.msg;
                _autoResumeAt = retryable ? DateTime.Now.AddSeconds(180) : DateTime.MinValue;
                _serverInventoryReady = false;
                SendArm(false);
                _log.Write("自动动作已暂停；保留游戏提示，不换鱼重发，也不将网络失败计入亲鱼黑名单。");
                if (_autoResumeAt != DateTime.MinValue)
                    _log.Write("这是本笔的瞬时失败；3 分钟后自动重试，无需手动点 ARM。");
            }
            EndRound();
        }
    }

    private void BeginRound()
    {
        if (_roundActive || _awaitingBreed || _breedRequest.Active) return;
        if (!_serverInventoryReady || (DateTime.Now - _lastServerAuditAt).TotalSeconds > 60)
        {
            _beginRoundAfterServerAudit = true;
            RequestServerAudit();
            return;
        }
        _roundActive = true;
        _roundAttempts = 0;
        _roundSuccesses = 0;
        _roundAvoidedFish.Clear();
        _verificationRetries = 0;
        _preBreedSnapshot = null;
        _log.Write("开始检查繁育计数器与可繁育亲鱼。");
        RequestState();
    }

    private void EndRound()
    {
        if (!_roundActive) return;
        _roundActive = false;
        _nextActionAt = DateTime.MinValue;
        _breedSettleAt = DateTime.MinValue;
        RollNextInterval();
        _nextCheckAt = DateTime.Now.AddSeconds(_nextIntervalSeconds);
        _log.Write(_actionPauseReason.Length > 0 ? "本轮结束：" + _actionPauseReason
            : $"本轮结束，约 {_nextIntervalSeconds / 60.0:F1} 分钟后再次检查繁育计数器。");

        if (!_chkAutoUpgrade.Checked || _roundSuccesses == 0) return;
        if (!EnsureReadyForAction(quiet: true)) return;

        if (_canLevelUp)
        {
            _log.Write($"鱼缸等级 {_tankLevel} 满足升级条件；等待动作间隔后升级。");
            _upgradePending = true;
        }
        else
        {
            _log.Write($"鱼缸等级 {_tankLevel}（经验 {_tankExp}），游戏判定暂不可升级。");
        }
    }

    /// <summary>
    /// 打印这一轮繁育的实际效果：亲鱼次数掉了多少、多了哪条新鱼、总数怎么变。
    ///
    /// 一次繁育**同时**消耗两样东西：
    ///   1. 鱼缸上方那排心（体力），每次 1 颗——所以心跳数（charge）会掉，界面刷新由插件负责；
    ///   2. 每条亲鱼自己的剩余繁育次数（bc）——这个服务端扣。
    /// 两者都要看清，不然会误以为"没消耗"或"消耗了没生效"。
    /// </summary>
    /// <returns>鱼群是否有实质变化（亲鱼次数下降或有新鱼）——这是繁育成功的真判据。</returns>
    private bool LogBreedDiff(List<FishDto> before, List<FishDto> after, List<string> bornIds)
    {
        if (before == null || after == null) return false;
        var amap = new Dictionary<string, FishDto>();
        foreach (var f in after) amap[f.id] = f;

        var consumed = new List<string>();
        foreach (var f in before)
        {
            if (amap.TryGetValue(f.id, out var a) && a.bc != f.bc)
                consumed.Add($"{f.Name} {FishCatalog.RarityName(f.RarityTier)} {f.Stars}星 {f.bc}→{a.bc}");
        }

        var bmap = new HashSet<string>();
        foreach (var f in before) bmap.Add(f.id);
        var born = after.Where(f => !bmap.Contains(f.id)).ToList();

        _log.Write(consumed.Count > 0
            ? "  ├ 亲鱼剩余次数：" + string.Join("   ", consumed)
            : "  ├ 亲鱼剩余次数：无变化（可能亲鱼本轮次数未变）");

        foreach (var n in born)
        {
            _log.Write($"  ├ 新鱼：{n.id} {n.Name} {FishCatalog.RarityName(n.RarityTier)} {n.Stars}星 次数{n.bc}/{n.bm}");
            bornIds?.Add(n.id);
        }

        if (born.Count == 0) _log.Write("  ├ 新鱼：无（服务端未返回新个体）");
        _log.Write($"  └ 鱼群总数 {before.Count} → {after.Count}");
        return consumed.Count > 0 || born.Count > 0;
    }

    // ------------------------------------------------------------------ 主循环

    private void OnTick(object sender, EventArgs e)
    {
        HideGameConsoleIfNeeded();
        MaybeAutoCheckUpdate();
        MaybeAutoRefreshMarket();
        if (_settings.AutoSteamExport && !_marketBusy && DateTime.Now >= _nextAutoExportAt) TryAutoSteamExport();
        UpdateStatusLabels();
        UpdateWarehouseCountdowns();
        if (_breedRequest.Waiting && _breedRequest.TimedOut) RequestBreedStatus();
        if (_breedRequest.Active && !_breedRequest.Waiting && !_statePending &&
            _breedSettleAt != DateTime.MinValue && DateTime.Now >= _breedSettleAt)
        {
            _breedSettleAt = DateTime.MinValue;
            RequestState();
        }

        // 瞬时网络失败会自动恢复：暂停只影响这一笔，不该让挂机永久停摆。
        if (_autoResumeAt != DateTime.MinValue && DateTime.Now >= _autoResumeAt &&
            !_armed && !_roundActive && !_awaitingBreed && (_chkAuto.Checked || _settings.AutoMerge) &&
            _client.Connected && _bridgeVersionOk && !_windowLocked && !_gameBusy && !_breedRequest.Active)
        {
            _autoResumeAt = DateTime.MinValue;
            _actionPauseReason = "";
            // 核对没过关时先补核对再 ARM。SendArm 会走 _armAfterServerAudit 这条路，
            // 核对成功后自动放行；这样「核对失败」也能在没有用户干预的情况下自愈。
            _nextCheckAt = DateTime.Now;
            SendArm(true);
            _log.Write("瞬时失败已过等待期，自动恢复。");
            UpdateDashboardSummary();
        }
        if (!_serverAuditPending && (_armAfterServerAudit || _beginRoundAfterServerAudit) &&
            DateTime.Now >= _serverAuditRetryAt) RequestServerAudit();
        if (_serverAuditPending && (DateTime.Now - _serverAuditRequestedAt).TotalSeconds > 25)
        {
            // 核对超时同样按可恢复处理：退避后自动重试，不用用户手动点 ARM。
            _serverAuditPending = false;
            _serverInventoryReady = false;
            _auditFailCount++;
            var backoff = Math.Min(300, 15 * _auditFailCount);
            _autoResumeAt = DateTime.Now.AddSeconds(backoff);
            _actionPauseReason = "服务器次数核对超时，稍后自动重试";
            SendArm(false);
            _log.Write($"服务器次数核对超时；{backoff} 秒后自动重试第 {_auditFailCount} 次。");
            EndRound();
        }
        if ((_activePage == "warehouse" || _activePage == "synthesis" || _activePage == "collection") &&
            _client.Connected && !_statePending && !_roundActive &&
            (DateTime.Now - _lastStateAt).TotalSeconds >= 20) RequestState();

        if (_statePending && (DateTime.Now - _lastStateAt).TotalSeconds > 20)
        {
            _statePending = false;
            _log.Write("鱼群状态请求超时，本轮停止，等待下次检查。");
            EndRound();
            if (_breedRequest.Active && !_breedRequest.Waiting)
                _breedSettleAt = DateTime.Now.AddSeconds(15);
        }
        if (_awaitingBreed && !_breedRequest.TimedOut && (DateTime.Now - _breedRequestedAt).TotalSeconds > 35)
        {
            _log.Write("繁育回包较慢，保留本笔记录，自动等待最终结果。");
            WaitForBreedResult();
        }

        if (_upgradePending && !_awaitingUpgrade && !_roundActive && !_gameBusy && _actionWaitSeconds <= 0 &&
            EnsureReadyForAction(quiet: true))
        {
            if (_lastActionAt == DateTime.MinValue ||
                (DateTime.Now - _lastActionAt).TotalSeconds >= _bridgeMinInterval)
            {
                _upgradePending = false;
                _awaitingUpgrade = true;
                SendUpgrade();
            }
        }

        if (_awaitingMerge && (DateTime.Now - _mergeRequestedAt).TotalSeconds > 60)
        {
            _awaitingMerge = false;
            _preMergeSnapshot = null;
            _log.Write("合成回包超时，保留鱼群等待下一次检查。");
            _nextMergeCheckAt = DateTime.Now.AddSeconds(60);
            RequestState();
        }

        if (_awaitingTradeExport && _tradeExportRequestedAt != DateTime.MinValue &&
            (DateTime.Now - _tradeExportRequestedAt).TotalSeconds > 90)
        {
            _awaitingTradeExport = false;
            _tradeExportRequestedAt = DateTime.MinValue;
            _actionPauseReason = "Steam 导出回包超时，请核对游戏仓库和 Steam Inventory 后再继续";
            _autoResumeAt = DateTime.MinValue;
            _log.Write("Steam 导出 90 秒未收到回报；暂停后续动作，请先核对游戏仓库与 Steam Inventory。");
            RequestState();
        }

        if (!_awaitingMerge && DateTime.Now >= _nextMergeCheckAt)
        {
            // 赛季鱼达标优先：材料正好 10 条不会浪费，且无视仓库阈值。
            MaybeSeasonalMergeIgnoreThreshold();
            if (!_awaitingMerge && _settings.AutoMerge) MaybeAutoMerge();
        }

        if (_roundActive)
        {
            if (_awaitingBreed || _statePending) return;
            if (_breedSettleAt != DateTime.MinValue)
            {
                if (DateTime.Now < _breedSettleAt) return;
                _breedSettleAt = DateTime.MinValue;
                RequestState();
                return;
            }
            if (_nextActionAt != DateTime.MinValue && DateTime.Now >= _nextActionAt)
            {
                _nextActionAt = DateTime.MinValue;
                RequestState();
            }
            return;
        }

        if (!_chkAuto.Checked || _upgradePending || _awaitingUpgrade || _statePending || _serverAuditPending || _gameBusy) return;
        if (!EnsureReadyForAction(quiet: true)) return;
        if (DateTime.Now >= _nextCheckAt) BeginRound();
    }

    private bool EnsureReadyForAction(bool quiet = false)
    {
        if (_awaitingTradeExport)
        {
            if (!quiet) _log.Write("正在等待 Steam 导出的游戏回报。");
            return false;
        }
        if (_breedRequest.Active)
        {
            if (!quiet) _log.Write("正在核对本笔繁育结果。");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(_actionPauseReason) &&
            (_autoResumeAt == DateTime.MinValue || DateTime.Now < _autoResumeAt))
        {
            if (!quiet) _log.Write("自动操作暂停中：" + _actionPauseReason);
            return false;
        }
        if (!string.IsNullOrWhiteSpace(_actionPauseReason) &&
            (_autoResumeAt == DateTime.MinValue || DateTime.Now < _autoResumeAt))
        {
            if (!quiet) _log.Write("自动操作暂停中：" + _actionPauseReason);
            return false;
        }
        if (_readOnlyMode)
        {
            if (!quiet) _log.Write("连接核查版只提供状态读取，繁育和升级已暂停。");
            return false;
        }
        if (!_client.Connected)
        {
            if (!quiet) _log.Write("桥接未连接。");
            return false;
        }
        if (!_bridgeAllowsActions)
        {
            if (!quiet) _log.Write("游戏内 cfg 未放行动作。");
            return false;
        }
        if (!_bridgeVersionOk)
        {
            if (!quiet) _log.Write("桥接版本不匹配，已阻止动作。");
            return false;
        }
        if (_client.RequiresArm && !_armed)
        {
            if (!quiet) _log.Write("尚未 ARM。勾选自动繁育或点 ARM 按钮。");
            return false;
        }
        return true;
    }

    private static string PriorityModeFromIndex(int index) => index switch
    {
        1 => "rarity_low",
        2 => "stars_high",
        3 => "stars_low",
        _ => "rarity_high"
    };

    private static int PriorityModeIndex(string mode) => mode switch
    {
        "rarity_low" => 1,
        "stars_high" => 2,
        "stars_low" => 3,
        _ => 0
    };

    private static string PriorityModeText(string mode) => UiLanguage.T(mode switch
    {
        "rarity_low" => "低稀有度优先",
        "stars_high" => "高星级优先",
        "stars_low" => "低星级优先",
        _ => "高稀有度优先"
    });

    private void MaybeBreed(List<FishDto> fish)
    {
        if (!_roundActive || _awaitingBreed) return;
        if (_awaitingTradeExport) return;
        if (!EnsureReadyForAction(quiet: true))
        {
            EndRound();
            return;
        }

        if (_charge <= 0)
        {
            _log.Write($"繁育计数器 {_charge}，本轮无需继续繁育。");
            EndRound();
            return;
        }
        if (_gameBusy)
        {
            if (!_waitingForGameIdle)
                _log.Write("游戏当前繁忙" + (string.IsNullOrWhiteSpace(_gameBusyReason) ? "" : "（" + _gameBusyReason + "）") + "，等待空闲后继续本轮。");
            _waitingForGameIdle = true;
            _nextActionAt = DateTime.Now.AddSeconds(60);
            return;
        }
        _waitingForGameIdle = false;
        if (_actionWaitSeconds > 0)
        {
            _nextActionAt = DateTime.Now.AddSeconds(_actionWaitSeconds);
            return;
        }

        if (!Selection.TryPickBreedPair(fish, (int)_numMinGrade.Value, _relations, _failedPairs,
                                       _roundAvoidedFish, _settings.GoalEnabled ? _goalPlan : null,
                                       _settings.AutoBreedPriorityMode, out var a, out var b))
        {
            _log.Write("凑不出可繁殖的一对（次数耗尽、冷却中、亲缘或近期拒绝），等下一轮。");
            EndRound();
            return;
        }

        if (_roundAttempts >= 20)
        {
            _log.Write("本轮已尝试 20 次，停止以避免异常循环；下次检查会重新读取繁育计数器。");
            EndRound();
            return;
        }

        var earliest = _lastActionAt.AddSeconds(_bridgeMinInterval);
        if (_lastActionAt != DateTime.MinValue && DateTime.Now < earliest)
        {
            _nextActionAt = earliest;
            return;
        }

        var goalHint = _settings.GoalEnabled && _goalPlan.Enabled
            ? $"目标优先：{FishCatalog.Name(_goalPlan.TargetType)}"
            : PriorityModeText(_settings.AutoBreedPriorityMode);
        _log.Write($"发起繁育（{goalHint}）：A={a.id} ({a.Name} {FishCatalog.RarityName(a.RarityTier)} {a.Stars}星 次数{a.bc})  " +
                   $"B={b.id} ({b.Name} {FishCatalog.RarityName(b.RarityTier)} {b.Stars}星 次数{b.bc})");
        _preBreedSnapshot = fish;
        _preBreedCharge = _charge;
        _pendingParentA = a.id;
        _pendingParentB = b.id;
        _pendingNewFishId = null;
        _verificationRetries = 0;
        _awaitingBreed = true;
        _breedRequestedAt = DateTime.Now;
        _roundAttempts++;
        _breedRequest.Begin(Guid.NewGuid().ToString("N"));
        _client.Send($"BREED {_breedRequest.Id} {a.id} {b.id}");
        _lastActionAt = DateTime.Now;
    }

    /// <summary>自动合成页面的状态行：开关、阈值、候选数量和下一次检查时间。</summary>
    private void UpdateMergeStatus()
    {
        if (_lblMergeStatus.IsDisposed) return;

        var total = _lastFish?.Count ?? -1;
        _lblMergeStatus.Text = total < 0
            ? UiLanguage.T("仓库：等待鱼群数据…")
            : UiLanguage.T($"仓库：{total} 条　阈值：{_settings.AutoMergeThreshold} 条　" +
                           (total > _settings.AutoMergeThreshold ? "已超过阈值，可合成" : "未达阈值"));

        var picks = _lastFish == null ? new List<FishDto>() : Selection.PickMergeCandidates(_lastFish, 10,
            SteamMarket.Cached, _settings.AutoMergeProtectMarketPriceAboveOneYuan,
            _settings.AutoMergeProtectionPriceYuan, _settings.AutoMergePriorityMode,
            new HashSet<int>(_settings.AutoMergeExcludedSeasons ?? new List<int>()),
            new HashSet<string>(_settings.AutoMergeExcludedTypes ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
            WikiDatabase.RecipeFishTypes(_settings.AutoMergeExcludedRecipeSeasons));
        if (_lastFish == null)
        {
            _lblMergePlan.Text = UiLanguage.T("候选：等待鱼群数据…");
        }
        else if (_settings.AutoMergeProtectMarketPriceAboveOneYuan && SteamMarket.Cached == null)
        {
            _lblMergePlan.Text = UiLanguage.T("候选：等待 Steam 市场成交价扫描…");
        }
        else if (picks.Count < 10)
        {
            _lblMergePlan.Text = UiLanguage.T("候选：不足 10 条同稀有度、同星级鱼，或市场价/赛季/配方排除规则正在生效。");
        }
        else
        {
            var names = picks.Select(f => f.Name).Distinct().Take(5);
            _lblMergePlan.Text = UiLanguage.T($"候选：10/10 条可合成　") + string.Join("、", names) + " 等";
        }

        if (_awaitingMerge) _lblMergeStatus.Text = UiLanguage.T("正在合成中…");
        else if (_settings.AutoMerge && _chkMerge.Checked && _armed && _client.Connected &&
                 _nextMergeCheckAt != DateTime.MinValue && DateTime.Now < _nextMergeCheckAt)
        {
            var left = (_nextMergeCheckAt - DateTime.Now).TotalSeconds;
            _lblMergeStatus.Text += UiLanguage.T($"　下次检查 {left / 60.0:F1} 分后");
        }
    }

    private void UpdateStatusLabels()
    {
        _lblProcess.Text = UiLanguage.T("游戏进程：" + GameProcess.Describe());
        UpdateGameWindowButton();

        if (_client.Connected)
        {
            var yes = UiLanguage.T("是");
            var no = UiLanguage.T("否");
            _lblBridge.Text = UiLanguage.T($"{_client.ModeName}：已连接   " +
                               $"版本={(_bridgeVersionOk ? "匹配" : "待更新")}   放行={(_bridgeAllowsActions ? yes : no)}   " +
                              $"ARM={(_armed ? yes : no)}   累计动作={_actionCount}");
            if (_windowLocked)
                _lblBridge.Text += UiLanguage.T($"   ⚠ 窗口自检已锁停（{_windowLockReason}）");
            else if (_windowChecksOk > 0)
                _lblBridge.Text += UiLanguage.T($"   窗口自检通过 {_windowChecksOk} 次");
        }
        else
        {
            _lblBridge.Text = UiLanguage.T($"{_client.ModeName}：未连接（正在重试）");
        }

        if (_startupCheckPending)
        {
            _lblCountdown.Text = UiLanguage.T("初始化中：正在检测游戏运行环境和插件…");
        }
        else if (!_startupCheckOk)
        {
            _lblCountdown.Text = UiLanguage.T("初始化未通过：") + _startupCheckDetail +
                UiLanguage.T("。点「重新检测」，或到「设置 → 一键部署」修复。");
        }
        else if (_serverAuditPending || _armAfterServerAudit || _beginRoundAfterServerAudit)
        {
            _lblCountdown.Text = UiLanguage.T("正在核对服务器繁育次数…");
        }
        else if (!string.IsNullOrEmpty(_actionPauseReason))
        {
            if (_autoResumeAt != DateTime.MinValue && DateTime.Now < _autoResumeAt)
            {
                var wait = (_autoResumeAt - DateTime.Now).TotalSeconds;
                _lblCountdown.Text = UiLanguage.T("自动操作已暂停：") + _actionPauseReason +
                    UiLanguage.T($"；{wait / 60.0:F1} 分钟后自动恢复");
            }
            else
            {
                _lblCountdown.Text = UiLanguage.T("自动操作已暂停：" + _actionPauseReason);
            }
        }
        else if (_roundActive)
        {
            _lblCountdown.Text = UiLanguage.T(_awaitingBreed ? "等待繁育回包…"
                : _gameBusy ? "等待游戏空闲…" : "本轮繁育中…");
        }
        else if (_chkAuto.Checked && _armed && _client.Connected)
        {
            var left = (_nextCheckAt - DateTime.Now).TotalSeconds;
            _lblCountdown.Text = left > 0
                ? UiLanguage.IsEnglish
                    ? $"Next check in {left / 60.0:F1} min"
                    : $"距下次检查 {left / 60.0:F1} 分"
                : UiLanguage.T("即将检查…");
        }
        else
        {
            _lblCountdown.Text = "";
        }

        _btnArm.Text = UiLanguage.T(_armed ? "解除 ARM" : "ARM（放行动作）");
        UpdateDashboardSummary();
        UpdateMergeStatus();
    }
}
