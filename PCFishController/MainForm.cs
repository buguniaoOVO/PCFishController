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
    private const string ExpectedBridgeVersion = "0.23.0";
    private readonly AppSettings _settings;
    private readonly Logger _log = new();
    private readonly BridgeClient _client;
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private bool _allowClose;

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
    private readonly BreedRequestTracker _breedRequest = new();
    private DateTime _nextBreedStatusAt = DateTime.MinValue;
    private bool _recoverAfterBreedVerification;
    private DateTime _breedRequestedAt = DateTime.MinValue;
    private bool _upgradePending;
    private bool _awaitingUpgrade;
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
    private bool _gameBusy;
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
    private int _actionWaitSeconds;

    /// <summary>发起繁殖前的鱼群快照。繁殖成功后拿它做对照，把"到底消耗了什么"打出来。</summary>
    private List<FishDto> _preBreedSnapshot;

    private static readonly JsonSerializerOptions JsonOpt = new() { PropertyNameCaseInsensitive = true };

    internal MainForm()
    {
        _settings = AppSettings.Load();
        UiLanguage.Set(_settings.Language);
        WikiDatabase.Load();
        // 从配置读自动繁育开关（默认关）。启动时不会自己动，但不再强制只读：
        // 连接核查已完成，结论是「只加载桥接不做动作时窗口完全正常」，
        // 出问题的是绕过游戏窗口生命周期去改 UI 的旧路径。现在动作只走数据/网络入口。
        _relations = new RelationStore(Path.Combine(AppContext.BaseDirectory, "PCFish亲缘关系.json"));
        _failedPairs = new FailedPairStore(Path.Combine(AppContext.BaseDirectory, "PCFish失败配对.json"));
        _client = new BridgeClient("127.0.0.1", _settings.Port);

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

        _client.Start();

        _timer.Interval = 1000;
        _timer.Start();
    }

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

    private void InitializeTrayIcon()
    {
        _trayMenu = new ContextMenuStrip();
        var openItem = _trayMenu.Items.Add(UiLanguage.T("打开助手"));
        openItem.Click += (_, _) => RestoreFromTray();
        var exitItem = _trayMenu.Items.Add(UiLanguage.T("退出助手"));
        exitItem.Click += (_, _) =>
        {
            _allowClose = true;
            SaveSettings();
            Close();
        };

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

        _btnOnce.Click += (_, _) =>
        {
            if (!EnsureReadyForAction()) return;
            _log.Write("手动触发：开始一轮繁育。");
            BeginRound();
        };

        _btnUpgrade.Click += (_, _) =>
        {
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

    private void SaveSettings()
    {
        _settings.WinW = Width;
        _settings.WinH = Height;
        _settings.WinX = Location.X;
        _settings.WinY = Location.Y;
        _settings.AutoBreed = _chkAuto.Checked;
        _settings.AutoUpgrade = _chkAutoUpgrade.Checked;
        _settings.BreedIntervalMinSeconds = (int)_numIvMin.Value * 60;
        _settings.BreedIntervalMaxSeconds = (int)_numIvMax.Value * 60;
        _settings.MinGrade = (int)_numMinGrade.Value;
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
                _log.Write($"已连上桥接 127.0.0.1:{_settings.Port}");
                // 连上就先拉一次完整鱼群，界面上马上有东西看，不用等用户点
                if (_chkAuto.Checked)
                {
                    _nextCheckAt = DateTime.Now;
                }
                RequestState();
            }
            else
            {
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
                _log.Write("桥接断开（游戏没开、或插件没装）。2 秒后自动重试。");
            }
            UpdateStatusLabels();
        });
    }

    private void SendArm(bool on)
    {
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
                    _log.Write("桥接版本已核实，等待服务器库存核对后放行。");
                }
                break;

            case "tick":
                Ui(() =>
                {
                    _bridgeAllowsActions = !_readOnlyMode && msg.cfg;
                    _armed = msg.armed;
                    _gameBusy = msg.gameBusy;
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
                        _log.Write("迟到回包已核实，自动恢复繁育检查。");
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
                _actionPauseReason = "服务器次数核对失败，点击 ARM 重新核对";
                SendArm(false);
                _log.Write("服务器库存核对未完成：" + msg.msg);
                EndRound();
                return;
            }
            _lastServerAuditAt = DateTime.Now;
            _serverAuditWaitReason = "";
            _serverAuditBusyCount = 0;
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
            _log.Write($"繁育请求失败（{elapsed:F1}秒）：{msg.msg}");
            _preBreedSnapshot = null;
            _pendingParentA = _pendingParentB = _pendingNewFishId = null;
            _breedSettleAt = DateTime.MinValue;
            _breedRequest.Clear();
            if (msg.errorKind == "busy" && _roundActive)
            {
                _nextActionAt = DateTime.Now.AddSeconds(15);
                return;
            }
            if (!string.IsNullOrEmpty(msg.errorKind))
            {
                var retryable = msg.errorKind is "transport" or "not_sent";
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
            !_armed && !_roundActive && !_awaitingBreed && _chkAuto.Checked &&
            _client.Connected && _bridgeVersionOk && !_windowLocked && !_gameBusy && !_breedRequest.Active)
        {
            _autoResumeAt = DateTime.MinValue;
            _actionPauseReason = "";
            SendArm(true);
            _log.Write("瞬时失败已过等待期，自动恢复自动繁育。");
            UpdateDashboardSummary();
        }
        if (!_serverAuditPending && (_armAfterServerAudit || _beginRoundAfterServerAudit) &&
            DateTime.Now >= _serverAuditRetryAt) RequestServerAudit();
        if (_serverAuditPending && (DateTime.Now - _serverAuditRequestedAt).TotalSeconds > 25)
        {
            _serverAuditPending = false;
            _serverInventoryReady = false;
            _actionPauseReason = "服务器次数核对失败，点击 ARM 重新核对";
            SendArm(false);
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

        if (!_chkAuto.Checked || _upgradePending || _awaitingUpgrade || _statePending || _serverAuditPending) return;
        if (!EnsureReadyForAction(quiet: true)) return;
        if (DateTime.Now >= _nextCheckAt) BeginRound();
    }

    private bool EnsureReadyForAction(bool quiet = false)
    {
        if (_breedRequest.Active)
        {
            if (!quiet) _log.Write("正在核对本笔繁育结果。");
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
        if (!_armed)
        {
            if (!quiet) _log.Write("尚未 ARM。勾选自动繁育或点 ARM 按钮。");
            return false;
        }
        return true;
    }

    private void MaybeBreed(List<FishDto> fish)
    {
        if (!_roundActive || _awaitingBreed) return;
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
                _log.Write("游戏正在处理请求或功能窗口正在使用，等待空闲后继续本轮。");
            _waitingForGameIdle = true;
            _nextActionAt = DateTime.Now.AddSeconds(15);
            return;
        }
        _waitingForGameIdle = false;
        if (_actionWaitSeconds > 0)
        {
            _nextActionAt = DateTime.Now.AddSeconds(_actionWaitSeconds);
            return;
        }

        if (!Selection.TryPickBreedPair(fish, (int)_numMinGrade.Value, _relations, _failedPairs,
                                       _roundAvoidedFish, _settings.GoalEnabled ? _goalPlan : null, out var a, out var b))
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

        var goalHint = _settings.GoalEnabled && _goalPlan.Enabled ? $"目标优先：{FishCatalog.Name(_goalPlan.TargetType)}" : "稀有度优先";
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

    private void UpdateStatusLabels()
    {
        _lblProcess.Text = UiLanguage.T("游戏进程：" + GameProcess.Describe());

        if (_client.Connected)
        {
            var yes = UiLanguage.T("是");
            var no = UiLanguage.T("否");
            _lblBridge.Text = UiLanguage.T($"桥接连接：已连接 127.0.0.1:{_settings.Port}   " +
                               $"版本={(_bridgeVersionOk ? "匹配" : "待更新")}   放行={(_bridgeAllowsActions ? yes : no)}   " +
                              $"ARM={(_armed ? yes : no)}   累计动作={_actionCount}");
            if (_windowLocked)
                _lblBridge.Text += UiLanguage.T($"   ⚠ 窗口自检已锁停（{_windowLockReason}）");
            else if (_windowChecksOk > 0)
                _lblBridge.Text += UiLanguage.T($"   窗口自检通过 {_windowChecksOk} 次");
        }
        else
        {
            _lblBridge.Text = UiLanguage.T($"桥接连接：未连接（正在重试 127.0.0.1:{_settings.Port}）");
        }

        if (_serverAuditPending || _armAfterServerAudit || _beginRoundAfterServerAudit)
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
    }
}
