using System.Drawing.Drawing2D;

namespace PCFishController;

internal sealed partial class MainForm
{
    private static readonly Color Bg = Color.FromArgb(244, 247, 251);
    private static readonly Color Side = Color.White;
    private static readonly Color Card = Color.White;
    private static readonly Color Line = Color.FromArgb(222, 230, 239);
    private static readonly Color White = Color.FromArgb(26, 38, 56);
    private static readonly Color Muted = Color.FromArgb(98, 115, 137);
    private static readonly Color Blue = Color.FromArgb(49, 91, 174);
    private static readonly Color Green = Color.FromArgb(28, 126, 86);
    private static readonly Color Gold = Color.FromArgb(190, 119, 20);
    private static readonly Color Pink = Color.FromArgb(184, 71, 100);
    private static readonly Color InputBg = Color.FromArgb(241, 245, 249);
    private static readonly Color MissingText = Color.FromArgb(169, 107, 0);
    private static readonly Color MissingFill = Color.FromArgb(255, 240, 190);
    private static readonly Color SufficientText = Color.FromArgb(0, 82, 174);
    private static readonly Color SufficientFill = Color.FromArgb(224, 237, 255);
    private static readonly Color CraftableText = Color.FromArgb(8, 123, 58);
    private static readonly Color CraftableFill = Color.FromArgb(211, 245, 224);

    private readonly Dictionary<string, NavButton> _navButtons = new();
    private readonly Dictionary<string, Panel> _pages = new();
    private string _activePage = "overview";
    private readonly Label _lblPageTitle = new();
    private readonly Label _lblPageSubtitle = new();
    private readonly Label _lblHeaderConnection = new();
    private readonly Label _lblRunState = new();
    private Button _btnRecheck;
    private Button _btnStartAll, _btnStopAll;
    private readonly Label _lblOverviewFish = new();
    private readonly Label _lblOverviewReady = new();
    private readonly Label _lblOverviewHeart = new();
    private readonly Label _lblOverviewLevel = new();
    private readonly Label _lblWarehouseCount = new();
    /// <summary>仓库改成卡片网格：每张卡照着游戏里的样式画图和品质。</summary>
    private readonly WarehouseGridCanvas _warehouseGrid = new();
    private readonly TextBox _warehouseSearch = new();
    private readonly ComboBox _warehouseFilter = new();
    private readonly ComboBox _warehouseSort = new();
    private DateTime _warehouseCdUpdateAt = DateTime.MinValue;
    private readonly Dictionary<string, Image> _fishIconCache = new(StringComparer.OrdinalIgnoreCase);
    private Button _btnMarket;
    private readonly Label _lblMarketStatus = new();
    private bool _marketBusy;
    private readonly ComboBox _goalTypeCombo = new();
    private readonly ComboBox _goalUnlockFilter = new();
    private readonly ComboBox _goalStarCombo = new();
    private readonly CheckBox _chkGoal = new();
    private readonly Label _lblCollectionSummary = new();
    private readonly Label _lblTargetStatus = new();
    private readonly TextBox _txtGoalRoute = new();
    private readonly RichTextBox _txtCollectionList = new();
    private bool _updatingGoalOptions;
    private readonly ComboBox _synthesisTargetCombo = new();
    private readonly HashSet<string> _craftableSeasonalTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox _synthesisStarCombo = new();
    private readonly Label _lblSynthesisTarget = new();
    private readonly Label _lblSynthesisStatus = new();
    private readonly Label _lblSynthesisSource = new();
    private readonly Label _lblSynthesisUpdated = new();
    private readonly DataGridView _synthesisGrid = new();
    private readonly ComboBox _languageCombo = new();
    private readonly ComboBox _closeBehaviorCombo = new();
    private readonly TextBox _commandBox = new();
    private readonly Label _commandStatus = new();

    /// <summary>界面右下角显示的版本号，取自程序集，改版本只需改 csproj。</summary>
    private static string AppVersion
    {
        get
        {
            try
            {
                var version = typeof(MainForm).Assembly.GetName().Version;
                return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                return "0.0.0";
            }
        }
    }

    private readonly TextBox _gameDirBox = new();
    private readonly Label _deployStatus = new();
    private Button _deployButton;
    private Button _gameDirBrowse;
    private readonly Label _updateStatus = new();
    private Button _updateCheckButton;
    private Button _updateApplyButton;
    private Button _updatePageButton;
    private UpdateInfo _pendingUpdate;
    private bool _changingLanguage;
    private SynthesisRoute _synthesisRoute = new();
    private string _synthesisType = "";
    private int _synthesisStar = 1;

    private static Label Label(string value, int size = 9, bool bold = false, Color? color = null)
        => new()
        {
            Text = UiLanguage.T(value), AutoSize = false, ForeColor = color ?? White,
            Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft
        };

    private static Panel Surface() => new() { BackColor = Card, Padding = new Padding(18) };

    private static Button ActionButton(string caption, Color? color = null)
        => new()
        {
            Text = UiLanguage.T(caption), Width = 148, Height = 38, FlatStyle = FlatStyle.Flat,
            BackColor = color ?? Color.FromArgb(232, 238, 247),
            ForeColor = color.HasValue && color != Gold && color.Value.GetBrightness() < 0.5F ? Color.White : White,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 10)
        };

    private static Label LegendBadge(string caption, Color foreColor, Color backColor)
        => new()
        {
            Text = UiLanguage.T(caption), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = foreColor, BackColor = backColor,
            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold),
            BorderStyle = BorderStyle.FixedSingle
        };

    private void BuildDashboardUi()
    {
        SuspendLayout();
        Text = UiLanguage.T("PCFish助手");
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        ShowIcon = true;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Bg;
        ForeColor = White;
        MinimumSize = new Size(1000, 690);
        Size = new Size(Math.Max(1000, _settings.WinW), Math.Max(690, _settings.WinH));
        StartPosition = FormStartPosition.CenterScreen;
        if (_settings.WinX >= 0 && _settings.WinY >= 0)
        {
            var saved = new Rectangle(_settings.WinX, _settings.WinY, Width, Height);
            if (Screen.AllScreens.Any(s => Rectangle.Intersect(s.WorkingArea, saved).Width >= 300 &&
                                           Rectangle.Intersect(s.WorkingArea, saved).Height >= 250))
            {
                StartPosition = FormStartPosition.Manual;
                Location = saved.Location;
            }
        }

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 190, BackColor = Side };
        var brand = new Panel { Dock = DockStyle.Top, Height = 110, Padding = new Padding(20, 20, 8, 8) };
        var mark = new PictureBox
        {
            Image = AssistantMarkBitmap(), SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(225, 237, 255), Size = new Size(42, 42), Location = new Point(15, 20)
        };
        var title = Label("PCFish助手", 11, true);
        title.SetBounds(61, 24, 120, 29);
        var brandSub = Label("繁育 · 仓库 · 鱼缸", 8, false, Muted);
        brandSub.SetBounds(61, 52, 125, 22);
        brand.Controls.AddRange(new Control[] { mark, title, brandSub });
        sidebar.Controls.Add(brand);

        var menu = new Panel { Dock = DockStyle.Top, Height = 380, Padding = new Padding(12, 8, 12, 0) };
        AddNav(menu, "overview", "概览", 0);
        AddNav(menu, "breeding", "自动繁育", 50);
        AddNav(menu, "merge", "自动合成", 100);
        AddNav(menu, "warehouse", "仓库", 150);
        AddNav(menu, "synthesis", "合成路线", 200);
        AddNav(menu, "logs", "日志", 250);
        AddNav(menu, "settings", "设置", 300);
        sidebar.Controls.Add(menu);
        sidebar.Controls.SetChildIndex(menu, 0);
        var foot = Label("本地连接  ·  后台运行", 8, false, Muted);
        foot.Dock = DockStyle.Bottom;
        foot.Height = 46;
        foot.Padding = new Padding(19, 0, 0, 0);
        sidebar.Controls.Add(foot);

        var main = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(25, 0, 25, 0) };
        var header = new Panel { Dock = DockStyle.Top, Height = 102 };
        _lblPageTitle.Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold);
        _lblPageTitle.ForeColor = White;
        _lblPageTitle.SetBounds(0, 18, 360, 38);
        _lblPageSubtitle.ForeColor = Muted;
        _lblPageSubtitle.SetBounds(2, 55, 500, 24);
        _lblHeaderConnection.TextAlign = ContentAlignment.MiddleCenter;
        _lblHeaderConnection.ForeColor = Muted;
        _lblHeaderConnection.BackColor = Card;
        _lblHeaderConnection.Size = new Size(112, 34);
        _lblHeaderConnection.Margin = new Padding(0, 0, 8, 0);
        _lblRunState.Size = new Size(112, 34);
        _lblRunState.TextAlign = ContentAlignment.MiddleCenter;
        _lblRunState.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _lblRunState.Margin = new Padding(0, 0, 8, 0);
        _btnStartAll = ActionButton("一键开启", Color.FromArgb(255, 214, 108));
        _btnStartAll.Size = new Size(110, 34);
        _btnStartAll.Margin = new Padding(0, 0, 8, 0);
        _btnStartAll.Click += (_, _) => StartAllAutomation();
        _btnStopAll = ActionButton("全部关闭", Color.FromArgb(45, 63, 87));
        _btnStopAll.ForeColor = Color.White;
        _btnStopAll.Size = new Size(110, 34);
        _btnStopAll.Margin = Padding.Empty;
        _btnStopAll.Click += (_, _) => StopAllAutomation();
        var runControls = new FlowLayoutPanel
        {
            Size = new Size(478, 36), WrapContents = false, BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty
        };
        _btnRecheck = ActionButton("重新检测", Color.FromArgb(232, 238, 247));
        _btnRecheck.Size = new Size(96, 34);
        _btnRecheck.Margin = new Padding(0, 0, 8, 0);
        _btnRecheck.ForeColor = Color.FromArgb(71, 85, 105);
        _btnRecheck.Click += (_, _) => RecheckStartupNow();
        _btnRecheck.Visible = false;
        runControls.Controls.AddRange(new Control[]
        {
            _lblRunState, _btnRecheck, _lblHeaderConnection, _btnStartAll, _btnStopAll
        });
        void PlaceRunControls()
        {
            var compact = header.ClientSize.Width < 800;
            header.Height = compact ? 132 : 102;
            runControls.Location = new Point(Math.Max(0, header.ClientSize.Width - runControls.Width), compact ? 82 : 27);
            _lblPageTitle.Width = compact ? 360 : Math.Max(180, runControls.Left - 16);
            _lblPageSubtitle.Width = compact ? header.ClientSize.Width : Math.Max(180, runControls.Left - 16);
        }
        header.Resize += (_, _) => PlaceRunControls();
        header.Controls.AddRange(new Control[] { _lblPageTitle, _lblPageSubtitle, runControls });
        PlaceRunControls();

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Bg };

        // 版本与署名固定在每个页面右下角：加在 main 上、Dock.Bottom，所有页共用。
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Bg };
        var signature = Label($"v{AppVersion}   ·   by Awan", 8, false, Muted);
        signature.AutoSize = true;
        signature.TextAlign = ContentAlignment.MiddleRight;
        void PlaceSignature()
        {
            var width = TextRenderer.MeasureText(signature.Text, signature.Font).Width;
            signature.Location = new Point(Math.Max(0, footer.ClientSize.Width - width), 6);
        }
        footer.Resize += (_, _) => PlaceSignature();
        PlaceSignature();
        footer.Controls.Add(signature);

        main.Controls.Add(body);
        main.Controls.Add(footer);
        main.Controls.Add(header);
        Controls.Add(main);
        Controls.Add(sidebar);

        BuildOverview(body);
        BuildBreeding(body);
        BuildMerge(body);
        BuildWarehouse(body);
        BuildCollection(body);
        BuildSynthesis(body);
        BuildLogs(body);
        BuildSettings(body);
        ShowPage("overview");
        UiLanguage.Apply(this, "zh");
        ResumeLayout(true);
    }

    /// <summary>
    /// 侧边栏按钮：文字左边画一个像素小图标。
    /// 图标不参与文本布局，所以切换语言时文字变了图标也留在原地。
    /// </summary>
    private sealed class NavButton : Button
    {
        internal string Key { get; init; } = "";
        private Image _icon;

        internal void SetIcon(Image icon)
        {
            _icon?.Dispose();
            _icon = icon;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_icon == null) return;
            e.Graphics.SmoothingMode = SmoothingMode.None;
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.DrawImage(_icon, 14, (Height - _icon.Height) / 2, _icon.Width, _icon.Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _icon?.Dispose();
            base.Dispose(disposing);
        }
    }

    private void AddNav(Panel menu, string key, string caption, int y)
    {
        var button = new NavButton
        {
            Text = UiLanguage.T(caption), FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(44, 0, 0, 0),
            BackColor = Side, ForeColor = Muted, Cursor = Cursors.Hand,
            Height = 42, Width = 164, Location = new Point(12, y + 8),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Key = key
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => ShowPage(key);
        button.SetIcon(NavIcon(key, Muted));
        _navButtons[key] = button;
        menu.Controls.Add(button);
    }

    /// <summary>
    /// 侧边栏图标：按页面含义画一个像素风小图标，和游戏统一。
    /// 尺寸固定 20x20，坐标落在 2 像素的格子上，得到方块边缘的观感。
    /// </summary>
    private static Bitmap NavIcon(string key, Color color)
    {
        const int grid = 2;
        const int size = 20;
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, grid) { StartCap = LineCap.Square, EndCap = LineCap.Square };
        void Block(int x, int y, int w = grid, int h = grid) => g.FillRectangle(brush, x, y, w, h);
        void Dot(int x, int y) => g.FillRectangle(brush, x, y, grid, grid);

        switch (key)
        {
            case "overview":
                // 方框加中间一颗点：概览像一块仪表盘
                Block(2, 2, 16, 2); Block(2, 16, 16, 2);
                Block(2, 4, 2, 12); Block(16, 4, 2, 12);
                Block(8, 8, 4, 4);
                break;
            case "breeding":
                // 像素爱心
                Block(4, 4, 4, 4); Block(12, 4, 4, 4);
                Block(2, 6, 16, 4);
                Block(4, 10, 12, 2);
                Block(6, 12, 8, 2);
                Block(8, 14, 4, 2);
                break;
            case "merge":
                // 两格并成一格：合成
                Block(2, 4, 6, 6); Block(12, 4, 6, 6);
                Block(2, 14, 16, 2);
                Block(9, 6, 2, 6);
                break;
            case "warehouse":
                // 箱子：外框加中线
                Block(2, 2, 16, 2); Block(2, 16, 16, 2);
                Block(2, 4, 2, 12); Block(16, 4, 2, 12);
                Block(2, 9, 16, 2);
                break;
            case "synthesis":
                // 配方：三个材料点加结果点
                Dot(3, 3); Dot(3, 11); Dot(11, 11);
                Block(14, 3, 4, 4);
                break;
            case "logs":
                // 三条横线，长短不一
                Block(2, 4, 16, 2); Block(2, 9, 12, 2); Block(2, 14, 16, 2);
                break;
            case "settings":
            default:
                // 齿轮：方芯加四向齿
                Block(7, 7, 6, 6);
                Dot(9, 2); Dot(9, 16); Dot(2, 9); Dot(16, 9);
                break;
        }
        return bmp;
    }

    private void ShowPage(string key)
    {
        _activePage = key;
        foreach (var (name, page) in _pages) page.Visible = name == key;
        foreach (var (name, button) in _navButtons)
        {
            var active = name == key;
            button.BackColor = active ? Color.FromArgb(230, 238, 252) : Side;
            button.ForeColor = active ? Color.FromArgb(38, 78, 153) : Muted;
            button.SetIcon(NavIcon(name, button.ForeColor));
        }
        var heading = key switch
        {
            "breeding" => ("自动繁育", "控制检查周期和繁育条件"),
            "merge" => ("自动合成", "仓库超过阈值时自动合并，排除赛季鱼与配方鱼"),
            "warehouse" => ("仓库", "查看鱼群、繁育次数与冷却时间"),
            "synthesis" => ("合成路线", "根据 Wiki 配方查看材料状态并设置繁育目标"),
            "collection" => ("图鉴 / 目标", "查看已发现鱼种，设置最终目标和繁育路线"),
            "logs" => ("日志", "查看助手和游戏桥接的运行记录"),
            "settings" => ("设置", "管理助手语言、窗口行为和命令"),
            _ => ("概览", "游戏连接、鱼群和繁育状态")
        };
        _lblPageTitle.Text = UiLanguage.T(heading.Item1);
        _lblPageSubtitle.Text = UiLanguage.T(heading.Item2);
        if (key == "warehouse" && _client.Connected && !_statePending) RequestState();
        if (key == "warehouse") RefreshWarehouse();
        if (key == "warehouse") UpdateMarketStatusLine();
        if (key == "collection" && _client.Connected && !_statePending) RequestState();
        if (key == "collection") RefreshCollectionPage();
        if (key == "synthesis" && _client.Connected && !_statePending) RequestState();
        if (key == "synthesis") RefreshSynthesisPage();
        if (key == "merge")
        {
            if (_client.Connected && !_statePending) RequestState();
            UpdateMergeStatus();
        }
    }

    private void AddPage(Panel body, string key, Panel page)
    {
        page.Dock = DockStyle.Fill;
        page.BackColor = Bg;
        page.Visible = false;
        body.Controls.Add(page);
        _pages[key] = page;
    }

    private static Panel Metric(string caption, Label value, Color valueColor)
    {
        var card = Surface();
        card.Margin = new Padding(0, 0, 12, 0);
        var name = Label(caption, 9, false, Muted);
        name.Dock = DockStyle.Top;
        name.Height = 28;
        value.Dock = DockStyle.Top;
        value.Height = 48;
        value.Font = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold);
        value.ForeColor = valueColor;
        card.Controls.Add(value);
        card.Controls.Add(name);
        return card;
    }

    private void BuildOverview(Panel body)
    {
        var page = new Panel();
        AddPage(body, "overview", page);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Top, Height = 145, ColumnCount = 4, RowCount = 1 };
        for (var i = 0; i < 4; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        metrics.Controls.Add(Metric("鱼群总数", _lblOverviewFish, White), 0, 0);
        metrics.Controls.Add(Metric("可繁育", _lblOverviewReady, Green), 1, 0);
        metrics.Controls.Add(Metric("繁育计数器", _lblOverviewHeart, Pink), 2, 0);
        metrics.Controls.Add(Metric("鱼缸等级", _lblOverviewLevel, Gold), 3, 0);
        foreach (Control c in metrics.Controls) c.Dock = DockStyle.Fill;

        var cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 280, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 15, 0, 0) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        var status = Surface();
        status.Margin = new Padding(0, 0, 12, 0);
        var statusTitle = Label("▏ 当前状态", 11, true);
        statusTitle.Dock = DockStyle.Top;
        statusTitle.Height = 35;
        var lines = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var l in new[] { _lblProcess, _lblBridge, _lblFish, _lblGrades, _lblTank })
        {
            l.Width = 480;
            l.Height = l == _lblBridge ? 46 : 30;
            l.ForeColor = l == _lblBridge ? Green : Muted;
            l.Margin = new Padding(0, 4, 0, 0);
            lines.Controls.Add(l);
        }
        lines.Resize += (_, _) => { foreach (Control l in lines.Controls) l.Width = Math.Max(100, lines.ClientSize.Width - 4); };
        status.Controls.Add(lines);
        status.Controls.Add(statusTitle);

        var quick = Surface();
        quick.Margin = new Padding(0);
        var quickTitle = Label("▏ 快捷操作", 11, true);
        quickTitle.Dock = DockStyle.Top;
        quickTitle.Height = 40;
        var quickStack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        _btnOnce.Text = "立即繁育一轮";
        _btnUpgrade.Text = "检查鱼缸升级";
        _btnState.Text = "刷新鱼群数据";
        StyleAction(_btnOnce, Gold);
        StyleAction(_btnUpgrade, Blue);
        StyleAction(_btnState, Color.FromArgb(120, 88, 205));
        foreach (var b in new[] { _btnOnce, _btnUpgrade, _btnState })
        {
            b.Width = 225;
            quickStack.Controls.Add(b);
        }
        quick.Controls.Add(quickStack);
        quick.Controls.Add(quickTitle);
        cards.Controls.Add(status, 0, 0);
        cards.Controls.Add(quick, 1, 0);
        status.Dock = quick.Dock = DockStyle.Fill;

        var cycle = Surface();
        cycle.Dock = DockStyle.Top;
        cycle.Height = 110;
        cycle.Margin = new Padding(0, 15, 0, 0);
        var cycleTitle = Label("▏ 下一轮", 11, true);
        cycleTitle.Dock = DockStyle.Top;
        cycleTitle.Height = 35;
        _lblCountdown.Dock = DockStyle.Top;
        _lblCountdown.Height = 32;
        _lblCountdown.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        _lblCountdown.ForeColor = Blue;
        cycle.Controls.Add(_lblCountdown);
        cycle.Controls.Add(cycleTitle);
        page.Controls.Add(cycle);
        page.Controls.Add(cards);
        page.Controls.Add(metrics);
    }

    private void BuildBreeding(Panel body)
    {
        var page = new Panel();
        AddPage(body, "breeding", page);
        var settings = Surface();
        settings.Dock = DockStyle.Top;
        settings.Height = 250;
        var heading = Label("自动运行", 12, true);
        heading.SetBounds(20, 15, 300, 32);
        _chkAuto.SetBounds(22, 58, 160, 36);
        _chkAuto.Text = "自动繁育";
        _chkAuto.Checked = _settings.AutoBreed;
        StyleCheck(_chkAuto);
        _chkAutoUpgrade.SetBounds(195, 58, 190, 36);
        _chkAutoUpgrade.Text = "每轮后检查升级";
        _chkAutoUpgrade.Checked = _settings.AutoUpgrade;
        StyleCheck(_chkAutoUpgrade);
        var intervalTitle = Label("检查间隔（分钟，随机）", 9, false, Muted);
        intervalTitle.SetBounds(22, 110, 200, 25);
        _numIvMin.SetBounds(22, 143, 83, 30);
        _numIvMax.SetBounds(140, 143, 83, 30);
        _numIvMin.Minimum = _numIvMax.Minimum = 1;
        _numIvMin.Maximum = _numIvMax.Maximum = 720;
        _numIvMin.Value = Math.Clamp(_settings.BreedIntervalMinSeconds / 60, 1, 720);
        _numIvMax.Value = Math.Clamp(_settings.BreedIntervalMaxSeconds / 60, 1, 720);
        StyleNumber(_numIvMin);
        StyleNumber(_numIvMax);
        var separator = Label("至", 9, false, Muted);
        separator.SetBounds(110, 143, 30, 30);
        var gradeTitle = Label("最低稀有度（0 为不限）", 9, false, Muted);
        gradeTitle.SetBounds(275, 110, 180, 25);
        _numMinGrade.SetBounds(275, 143, 83, 30);
        _numMinGrade.Minimum = 0;
        _numMinGrade.Maximum = 6;
        _numMinGrade.Value = Math.Clamp(_settings.MinGrade, 0, 6);
        StyleNumber(_numMinGrade);
        var hint = Label("每次检查会使用现有繁育计数器，优先选择目标路线、高稀有度且已结束冷却的鱼。", 9, false, Muted);
        hint.SetBounds(22, 188, 700, 24);
        var hint2 = Label("合成相关的阈值和等待时间已移到「自动合成」页面。", 9, false, Muted);
        hint2.SetBounds(22, 212, 700, 24);
        settings.Controls.AddRange(new Control[] { heading, _chkAuto, _chkAutoUpgrade, intervalTitle, _numIvMin, separator, _numIvMax, gradeTitle, _numMinGrade, hint, hint2 });

        var manual = Surface();
        manual.Dock = DockStyle.Top;
        manual.Height = 170;
        manual.Margin = new Padding(0, 16, 0, 0);
        var manualTitle = Label("手动控制", 12, true);
        manualTitle.SetBounds(20, 15, 300, 32);
        _btnArm.Text = "ARM（放行动作）";
        _btnHud.Text = "查询计数器状态";
        StyleAction(_btnArm);
        StyleAction(_btnHud);
        _btnArm.SetBounds(22, 60, 175, 40);
        _btnHud.SetBounds(212, 60, 175, 40);
        var manualHint = Label("动作需游戏内放行，并与桥接保持连接。", 9, false, Muted);
        manualHint.SetBounds(22, 115, 550, 28);
        manual.Controls.AddRange(new Control[] { manualTitle, _btnArm, _btnHud, manualHint });

        var status = Surface();
        status.Dock = DockStyle.Top;
        status.Height = 120;
        status.Margin = new Padding(0, 16, 0, 0);
        _lblCharge.Dock = DockStyle.Top;
        _lblCharge.Height = 40;
        _lblCharge.ForeColor = Pink;
        _lblCharge.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _lblLevel.Dock = DockStyle.Top;
        _lblLevel.Height = 40;
        _lblLevel.ForeColor = Gold;
        _lblLevel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        status.Controls.Add(_lblLevel);
        status.Controls.Add(_lblCharge);
        page.Controls.Add(status);
        page.Controls.Add(manual);
        page.Controls.Add(settings);
    }

    private static void StyleAction(Button button, Color? back = null)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.BorderColor = Line;
        button.BackColor = back ?? Color.FromArgb(232, 238, 247);
        button.ForeColor = back == Gold ? Color.FromArgb(75, 49, 7)
            : back == Blue || back == Color.FromArgb(120, 88, 205) ? Color.White
            : White;
        button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.Height = 40;
        button.Margin = new Padding(0, 0, 0, 10);
    }

    private static void StyleCheck(CheckBox box)
    {
        var caption = box.Text;
        box.Appearance = Appearance.Button;
        box.FlatStyle = FlatStyle.Flat;
        box.FlatAppearance.BorderSize = 0;
        box.TextAlign = ContentAlignment.MiddleCenter;
        box.Cursor = Cursors.Hand;
        var isGoal = caption.Contains("目标", StringComparison.Ordinal);
        void Refresh()
        {
            box.BackColor = box.Checked
                ? Color.FromArgb(218, 242, 230)
                : isGoal ? Color.FromArgb(239, 233, 252) : Color.FromArgb(238, 242, 247);
            box.ForeColor = box.Checked
                ? Color.FromArgb(25, 106, 71)
                : isGoal ? Color.FromArgb(91, 63, 150) : Color.FromArgb(71, 85, 105);
            box.Text = (box.Checked ? "●  " : "○  ") + UiLanguage.T(caption) + (box.Checked ? "  " + UiLanguage.T("已开") : "  " + UiLanguage.T("已关"));
        }
        box.CheckedChanged += (_, _) => Refresh();
        Refresh();
    }

    private static void StyleNumber(NumericUpDown number)
    {
        number.BackColor = InputBg;
        number.ForeColor = White;
        number.BorderStyle = BorderStyle.FixedSingle;
    }

    private static Bitmap AssistantMarkBitmap()
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon != null) return icon.ToBitmap();
        }
        catch { }
        return SystemIcons.Application.ToBitmap();
    }

    private void BuildMerge(Panel body)
    {
        var page = new Panel();
        AddPage(body, "merge", page);

        var settings = Surface();
        settings.Dock = DockStyle.Top;
        settings.Height = 232;
        var heading = Label("自动运行", 12, true);
        heading.SetBounds(20, 15, 300, 32);
        _chkMerge.SetBounds(22, 58, 170, 36);
        _chkMerge.Text = "自动合成";
        _chkMerge.Checked = _settings.AutoMerge;
        StyleCheck(_chkMerge);
        _chkMergeExcludeSeasonal.SetBounds(205, 58, 250, 36);
        _chkMergeExcludeSeasonal.Text = "排除赛季鱼与配方鱼";
        _chkMergeExcludeSeasonal.Checked = _settings.AutoMergeExcludeSeasonal;
        StyleCheck(_chkMergeExcludeSeasonal);

        var thresholdTitle = Label("触发阈值（仓库鱼数）", 9, false, Muted);
        thresholdTitle.SetBounds(22, 110, 190, 25);
        _numMergeThreshold.SetBounds(22, 143, 90, 30);
        _numMergeThreshold.Minimum = 0;
        _numMergeThreshold.Maximum = 5000;
        _numMergeThreshold.Increment = 50;
        _numMergeThreshold.Value = Math.Clamp(_settings.AutoMergeThreshold, 0, 5000);
        StyleNumber(_numMergeThreshold);

        var safetyTitle = Label("合成后安全等待（秒）", 9, false, Muted);
        safetyTitle.SetBounds(240, 110, 210, 25);
        _numMergeSafety.SetBounds(240, 143, 90, 30);
        _numMergeSafety.Minimum = 1;
        _numMergeSafety.Maximum = 60;
        _numMergeSafety.Increment = 1;
        _numMergeSafety.Value = Math.Clamp(_settings.MergeSafetySeconds, 1, 60);
        StyleNumber(_numMergeSafety);

        var ruleHint = Label("每次合并 10 条；优先使用 0 繁育次数、稀有度最低的鱼。", 9, false, Muted);
        ruleHint.SetBounds(22, 192, 700, 24);

        settings.Controls.AddRange(new Control[]
        {
            heading, _chkMerge, _chkMergeExcludeSeasonal, thresholdTitle, _numMergeThreshold,
            safetyTitle, _numMergeSafety, ruleHint
        });

        var manual = Surface();
        manual.Dock = DockStyle.Top;
        manual.Height = 120;
        manual.Margin = new Padding(0, 16, 0, 0);
        var manualTitle = Label("手动控制", 12, true);
        manualTitle.SetBounds(20, 15, 300, 32);
        _btnMergeNow.Text = "立即合成一次";
        StyleAction(_btnMergeNow, Color.FromArgb(120, 88, 205));
        _btnMergeNow.SetBounds(22, 58, 175, 40);
        var manualHint = Label("忽略阈值，直接挑 10 条合成一次；需先 ARM 并与桥接保持连接。", 9, false, Muted);
        manualHint.SetBounds(212, 62, 640, 28);
        manual.Controls.AddRange(new Control[] { manualTitle, _btnMergeNow, manualHint });

        var status = Surface();
        status.Dock = DockStyle.Top;
        status.Height = 130;
        status.Margin = new Padding(0, 16, 0, 0);
        var statusTitle = Label("▏ 当前状态", 11, true);
        statusTitle.Dock = DockStyle.Top;
        statusTitle.Height = 35;
        _lblMergeStatus.Dock = DockStyle.Top;
        _lblMergeStatus.Height = 34;
        _lblMergeStatus.ForeColor = Blue;
        _lblMergeStatus.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _lblMergePlan.Dock = DockStyle.Top;
        _lblMergePlan.Height = 34;
        _lblMergePlan.ForeColor = Muted;
        status.Controls.Add(_lblMergePlan);
        status.Controls.Add(_lblMergeStatus);
        status.Controls.Add(statusTitle);

        page.Controls.Add(status);
        page.Controls.Add(manual);
        page.Controls.Add(settings);
    }

    private void BuildWarehouse(Panel body)
    {
        var page = new Panel();
        AddPage(body, "warehouse", page);
        var toolbar = Surface();
        toolbar.Dock = DockStyle.Top;
        toolbar.Height = 100;
        var header = Label("我的鱼", 12, true);
        header.SetBounds(18, 8, 180, 32);
        _lblWarehouseCount.SetBounds(175, 9, 210, 30);
        _lblWarehouseCount.ForeColor = Muted;
        // 市场报价的摘要放在标题行右侧，不占额外高度。
        _lblMarketStatus.SetBounds(400, 9, 560, 30);
        _lblMarketStatus.ForeColor = Muted;
        _lblMarketStatus.Font = new Font("Microsoft YaHei UI", 8F);
        _lblMarketStatus.TextAlign = ContentAlignment.MiddleLeft;
        _warehouseSearch.SetBounds(18, 51, 210, 32);
        _warehouseSearch.PlaceholderText = "搜索鱼种或 ID";
        _warehouseSearch.BackColor = InputBg;
        _warehouseSearch.ForeColor = White;
        _warehouseSearch.BorderStyle = BorderStyle.FixedSingle;
        _warehouseFilter.SetBounds(240, 51, 126, 32);
        _warehouseFilter.Items.AddRange(new object[] { "全部", "可繁育", "冷却中", "次数用尽", "锁定/展示" });
        _warehouseFilter.SelectedIndex = 0;
        _warehouseSort.SetBounds(379, 51, 150, 32);
        _warehouseSort.Items.AddRange(new object[] { "稀有度优先", "星级优先", "次数优先", "冷却最快", "价格降序", "价格升序" });
        _warehouseSort.SelectedIndex = 0;
        foreach (var combo in new[] { _warehouseFilter, _warehouseSort })
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = InputBg;
            combo.ForeColor = White;
        }
        var refresh = ActionButton("刷新仓库");
        refresh.SetBounds(542, 50, 102, 32);
        refresh.Font = new Font("Microsoft YaHei UI", 8F);
        refresh.FlatAppearance.BorderSize = 0;
        refresh.Click += (_, _) => RequestState();

        // Steam 市场报价：拉一次缓存起来，仓库表里就能看到每条鱼的价格。
        _btnMarket = ActionButton("查询市场价", Color.FromArgb(35, 122, 122));
        _btnMarket.SetBounds(652, 50, 118, 32);
        _btnMarket.Font = new Font("Microsoft YaHei UI", 8F);
        _btnMarket.FlatAppearance.BorderSize = 0;
        _btnMarket.Click += async (_, _) => await RefreshMarketAsync();
        _warehouseSearch.TextChanged += (_, _) => RefreshWarehouse();
        _warehouseFilter.SelectedIndexChanged += (_, _) => RefreshWarehouse();
        _warehouseSort.SelectedIndexChanged += (_, _) =>
        {
            RefreshWarehouse();
        };
        toolbar.Controls.AddRange(new Control[]
        {
            header, _lblWarehouseCount, _lblMarketStatus, _warehouseSearch, _warehouseFilter,
            _warehouseSort, refresh, _btnMarket
        });

        // 卡片网格：自动换行，滚动条在右边。
        _warehouseGrid.Dock = DockStyle.Fill;
        _warehouseGrid.BackColor = Bg;
        _warehouseGrid.Configure(FishIcon, MarketQuoteFor, MarketPriceText,
            GradeName, CooldownText, FishStatus);
        _warehouseGrid.Resize += (_, _) => _warehouseGrid.Relayout();
        page.Controls.Add(_warehouseGrid);
        page.Controls.Add(toolbar);
    }

    private void BuildSynthesis(Panel body)
    {
        var page = new Panel();
        AddPage(body, "synthesis", page);

        var toolbar = Surface();
        toolbar.Dock = DockStyle.Top;
        toolbar.Height = 142;
        var title = Label("Wiki 合成路线", 12, true);
        title.SetBounds(18, 10, 220, 30);
        var targetLabel = Label("路线目标", 9, false, Muted);
        targetLabel.SetBounds(18, 53, 70, 28);
        _synthesisTargetCombo.SetBounds(88, 52, 245, 32);
        _synthesisTargetCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _synthesisTargetCombo.FlatStyle = FlatStyle.Flat;
        _synthesisTargetCombo.BackColor = InputBg;
        _synthesisTargetCombo.ForeColor = White;
        _synthesisTargetCombo.DrawMode = DrawMode.OwnerDrawFixed;
        _synthesisTargetCombo.ItemHeight = 26;
        _synthesisTargetCombo.DropDownWidth = 380;
        _synthesisTargetCombo.DrawItem += DrawSynthesisTarget;
        _synthesisTargetCombo.DisplayMember = "DisplayName";
        _synthesisTargetCombo.ValueMember = "Code";
        var seasonalTargets = FishCatalog.Species.Where(x => x.Season > 0).ToList();
        _synthesisTargetCombo.DataSource = seasonalTargets;
        _synthesisType = seasonalTargets.Any(x => string.Equals(x.Code, _settings.GoalType, StringComparison.OrdinalIgnoreCase))
            ? _settings.GoalType
            : seasonalTargets.FirstOrDefault()?.Code ?? "";
        if (!string.IsNullOrWhiteSpace(_synthesisType))
            _synthesisTargetCombo.SelectedValue = _synthesisType;

        var starLabel = Label("星级", 9, false, Muted);
        starLabel.SetBounds(350, 53, 42, 28);
        _synthesisStarCombo.SetBounds(392, 52, 105, 32);
        _synthesisStarCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _synthesisStarCombo.FlatStyle = FlatStyle.Flat;
        _synthesisStarCombo.BackColor = InputBg;
        _synthesisStarCombo.ForeColor = White;
        _synthesisStarCombo.Items.AddRange(new object[] { "★☆☆☆☆", "★★☆☆☆", "★★★☆☆", "★★★★☆", "★★★★★" });
        _synthesisStarCombo.SelectedIndex = Math.Clamp(_settings.GoalStar - 1, 0, 4);

        var setGoal = ActionButton("设为繁育目标", Gold);
        setGoal.SetBounds(515, 50, 145, 36);
        setGoal.Click += (_, _) => SetSynthesisGoal(_synthesisType, _synthesisStar, true);
        var refreshRoute = ActionButton("刷新仓库状态", Blue);
        refreshRoute.SetBounds(665, 50, 125, 36);
        refreshRoute.Click += (_, _) => RequestState();
        var refreshWiki = ActionButton("刷新 Wiki 数据", Color.FromArgb(120, 88, 205));
        refreshWiki.SetBounds(800, 50, 135, 36);
        refreshWiki.Click += async (_, _) => await RefreshWikiDatabaseAsync();
        _lblSynthesisTarget.SetBounds(18, 90, 270, 24);
        _lblSynthesisTarget.AutoEllipsis = true;
        _lblSynthesisTarget.ForeColor = White;
        _lblSynthesisStatus.SetBounds(245, 12, 150, 28);
        _lblSynthesisStatus.TextAlign = ContentAlignment.MiddleCenter;
        _lblSynthesisStatus.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _lblSynthesisSource.SetBounds(300, 90, 610, 24);
        _lblSynthesisSource.ForeColor = Muted;
        _lblSynthesisUpdated.SetBounds(576, 113, 330, 20);
        _lblSynthesisUpdated.ForeColor = Muted;
        _lblSynthesisUpdated.Font = new Font("Microsoft YaHei UI", 8F);
        var missingLegend = LegendBadge("缺少数量", MissingText, MissingFill);
        missingLegend.SetBounds(210, 111, 104, 23);
        var sufficientLegend = LegendBadge("数量达标", SufficientText, SufficientFill);
        sufficientLegend.SetBounds(320, 111, 100, 23);
        var craftableLegend = LegendBadge("可合成", CraftableText, CraftableFill);
        craftableLegend.SetBounds(426, 111, 140, 23);
        toolbar.Controls.AddRange(new Control[] { title, _lblSynthesisStatus, targetLabel, _synthesisTargetCombo, starLabel, _synthesisStarCombo, setGoal, refreshRoute, refreshWiki, _lblSynthesisTarget, _lblSynthesisSource, _lblSynthesisUpdated, missingLegend, sufficientLegend, craftableLegend });
        void PlaceSynthesisControls()
        {
            var compact = toolbar.ClientSize.Width < 950;
            toolbar.Height = compact ? 207 : 160;
            setGoal.Location = new Point(compact ? 18 : 515, compact ? 92 : 50);
            refreshRoute.Location = new Point(compact ? 171 : 665, compact ? 92 : 50);
            refreshWiki.Location = new Point(compact ? 305 : 800, compact ? 92 : 50);
            var nameY = compact ? 138 : 93;
            _lblSynthesisTarget.Location = new Point(18, nameY);
            _lblSynthesisSource.SetBounds(300, nameY, Math.Max(200, toolbar.ClientSize.Width - 318), 24);
            var legendY = compact ? 173 : 126;
            missingLegend.Location = new Point(compact ? 18 : 210, legendY);
            sufficientLegend.Location = new Point(compact ? 130 : 320, legendY);
            craftableLegend.Location = new Point(compact ? 238 : 426, legendY);
            var updatedX = compact ? 396 : 576;
            _lblSynthesisUpdated.SetBounds(updatedX, legendY, Math.Max(160, toolbar.ClientSize.Width - updatedX - 18), 23);
        }
        toolbar.Resize += (_, _) => PlaceSynthesisControls();
        PlaceSynthesisControls();

        _synthesisTargetCombo.SelectedValueChanged += (_, _) => SynthesisSelectionChanged();
        _synthesisStarCombo.SelectedIndexChanged += (_, _) => SynthesisSelectionChanged();

        _synthesisGrid.Dock = DockStyle.Fill;
        _synthesisGrid.BackgroundColor = Card;
        _synthesisGrid.BorderStyle = BorderStyle.None;
        _synthesisGrid.GridColor = Line;
        _synthesisGrid.EnableHeadersVisualStyles = false;
        _synthesisGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 238, 247);
        _synthesisGrid.ColumnHeadersDefaultCellStyle.ForeColor = White;
        _synthesisGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _synthesisGrid.ColumnHeadersHeight = 40;
        _synthesisGrid.RowTemplate.Height = 38;
        _synthesisGrid.DefaultCellStyle.BackColor = Card;
        _synthesisGrid.DefaultCellStyle.ForeColor = White;
        _synthesisGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 231, 251);
        _synthesisGrid.DefaultCellStyle.SelectionForeColor = White;
        _synthesisGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
        _synthesisGrid.RowHeadersVisible = false;
        _synthesisGrid.ReadOnly = true;
        _synthesisGrid.AllowUserToAddRows = false;
        _synthesisGrid.AllowUserToDeleteRows = false;
        _synthesisGrid.AllowUserToResizeRows = false;
        _synthesisGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _synthesisGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _synthesisGrid.Columns.Add("fish", "路线鱼");
        _synthesisGrid.Columns.Add("star", "星级");
        _synthesisGrid.Columns.Add("count", "数量");
        _synthesisGrid.Columns.Add("role", "用途");
        _synthesisGrid.Columns.Add("unlock", "图鉴");
        _synthesisGrid.Columns.Add("owned", "可用 / 拥有");
        _synthesisGrid.Columns.Add("source", "来源");
        _synthesisGrid.Columns.Add(new DataGridViewButtonColumn { Name = "route", HeaderText = "合成路线", UseColumnTextForButtonValue = false });
        _synthesisGrid.Columns.Add(new DataGridViewButtonColumn { Name = "breed", HeaderText = "繁育路线", UseColumnTextForButtonValue = false });
        _synthesisGrid.Columns.Add(new DataGridViewButtonColumn { Name = "target", HeaderText = "目标", UseColumnTextForButtonValue = false });
        foreach (DataGridViewColumn col in _synthesisGrid.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;
        _synthesisGrid.Columns["fish"].FillWeight = 175;
        _synthesisGrid.Columns["fish"].MinimumWidth = 170;
        _synthesisGrid.Columns["star"].FillWeight = 58;
        _synthesisGrid.Columns["count"].FillWeight = 58;
        _synthesisGrid.Columns["role"].FillWeight = 115;
        _synthesisGrid.Columns["unlock"].FillWeight = 68;
        _synthesisGrid.Columns["owned"].FillWeight = 140;
        _synthesisGrid.Columns["source"].FillWeight = 90;
        _synthesisGrid.Columns["route"].FillWeight = 80;
        _synthesisGrid.Columns["breed"].FillWeight = 80;
        _synthesisGrid.Columns["target"].FillWeight = 68;
        _synthesisGrid.Columns["owned"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _synthesisGrid.CellContentClick += SynthesisGridCellContentClick;

        var card = Surface();
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(0);
        card.Controls.Add(_synthesisGrid);
        page.Controls.Add(card);
        page.Controls.Add(toolbar);

        _synthesisStar = Math.Clamp(_settings.GoalStar, 1, 5);
        RefreshSynthesisPage();
    }

    private void SynthesisSelectionChanged()
    {
        if (_synthesisTargetCombo.SelectedValue is string code) _synthesisType = code;
        _synthesisStar = Math.Clamp(_synthesisStarCombo.SelectedIndex + 1, 1, 5);
        RefreshSynthesisPage();
    }

    private void DrawSynthesisTarget(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _synthesisTargetCombo.Items.Count) return;
        if (_synthesisTargetCombo.Items[e.Index] is not FishSpecies species) return;
        var ready = _craftableSeasonalTypes.Contains(species.Code);
        var selected = (e.State & DrawItemState.Selected) != 0;
        var fill = ready ? CraftableFill : selected ? FishQualityPalette.SelectedBackground : FishQualityPalette.Background;
        var textColor = ready ? CraftableText : GradeColor(species.Tier);
        using var brush = new SolidBrush(fill);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var text = species.DisplayName + (ready ? " · " + UiLanguage.T("可合成") : "");
        var rect = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, e.Bounds.Width - 10, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, e.Font, rect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private void RefreshSynthesisPage()
    {
        if (_synthesisGrid == null) return;
        var fish = _lastFish ?? new List<FishDto>();
        _craftableSeasonalTypes.Clear();
        foreach (var species in FishCatalog.Species.Where(s => s.Season > 0))
            if (SynthesisPlanner.CanCraftNow(species.Code, _synthesisStar, fish)) _craftableSeasonalTypes.Add(species.Code);
        _synthesisTargetCombo.Invalidate();
        var stock = fish.GroupBy(f => (Type: f.ty ?? "", Star: f.Stars))
            .ToDictionary(g => g.Key, g => (Owned: g.Count(), Available: g.Count(Selection.CanMerge)));
        if (string.IsNullOrWhiteSpace(_synthesisType))
            _synthesisType = "FS00033";
        _synthesisRoute = SynthesisPlanner.Build(_synthesisType, _synthesisStar);
        var target = FishCatalog.Find(_synthesisRoute.TargetType);
        _lblSynthesisTarget.Text = UiLanguage.T(target == null
            ? "请选择路线目标"
            : $"当前路线：{target.DisplayName}　{StarText(_synthesisRoute.TargetStar)}");
        var targetReady = _craftableSeasonalTypes.Contains(_synthesisType);
        stock.TryGetValue((_synthesisType, _synthesisRoute.TargetStar), out var selectedTargetStock);
        var selectedTargetOwned = selectedTargetStock.Owned > 0;
        if (targetReady) _lblSynthesisTarget.Text += " · " + UiLanguage.T("可合成");
        _lblSynthesisTarget.BackColor = targetReady ? CraftableFill : FishQualityPalette.Background;
        _lblSynthesisTarget.ForeColor = targetReady ? CraftableText : GradeColor(target?.Tier ?? 0);
        _lblSynthesisTarget.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _lblSynthesisStatus.Text = UiLanguage.T(targetReady ? "可合成" : selectedTargetOwned ? "已拥有" : "材料未齐");
        _lblSynthesisStatus.ForeColor = targetReady ? CraftableText : selectedTargetOwned ? SufficientText : MissingText;
        _lblSynthesisStatus.BackColor = targetReady ? CraftableFill : selectedTargetOwned ? SufficientFill : MissingFill;
        _lblSynthesisSource.Text = UiLanguage.T(_synthesisRoute.SourceText);
        _lblSynthesisUpdated.Text = UiLanguage.T(_lastStateAt == DateTime.MinValue
            ? "仓库同步：等待游戏数据"
            : $"仓库同步：{_lastStateAt:HH:mm:ss}");

        _synthesisGrid.Rows.Clear();
        var known = new HashSet<string>(_collectionTypes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var routeRows = _synthesisRoute.Rows.ToList();
        var children = Enumerable.Range(0, routeRows.Count).Select(_ => new List<int>()).ToArray();
        for (var parent = 0; parent < routeRows.Count; parent++)
        {
            for (var child = parent + 1; child < routeRows.Count && routeRows[child].Depth > routeRows[parent].Depth; child++)
                if (routeRows[child].Depth == routeRows[parent].Depth + 1) children[parent].Add(child);
        }

        var readyCache = new bool?[routeRows.Count];
        bool CanSupply(int index)
        {
            if (readyCache[index].HasValue) return readyCache[index].Value;
            var material = routeRows[index];
            stock.TryGetValue((material.Type, material.Star), out var count);
            bool ready;
            if (material.Role == "目标鱼")
            {
                ready = count.Owned > 0 || children[index].Count > 0 && children[index].All(CanSupply);
            }
            else if (material.Role == "上一星级")
            {
                ready = count.Available >= material.Count ||
                        children[index].Count > 0 && children[index].All(CanSupply);
            }
            else
            {
                ready = count.Available >= material.Count;
            }
            readyCache[index] = ready;
            return ready;
        }
        bool CanCraftNow(int index)
        {
            if (!_synthesisRoute.Exact || children[index].Count == 0) return false;
            return SynthesisPlanner.CanCraftNow(routeRows[index].Type, routeRows[index].Star, fish);
        }

        for (var rowIndex = 0; rowIndex < routeRows.Count; rowIndex++)
        {
            var routeRow = routeRows[rowIndex];
            var species = FishCatalog.Find(routeRow.Type);
            stock.TryGetValue((routeRow.Type, routeRow.Star), out var count);
            var unlocked = known.Contains(routeRow.Type) || count.Owned > 0;
            var canSupply = CanSupply(rowIndex);
            var quantityMet = count.Available >= routeRow.Count;
            var craftableNow = CanCraftNow(rowIndex);
            var targetOwned = routeRow.Role == "目标鱼" && count.Owned > 0;
            var shortage = Math.Max(0, routeRow.Count - count.Available);
            var isReady = craftableNow;
            var color = !_synthesisRoute.Exact && !targetOwned ? Muted
                : isReady ? CraftableText
                : quantityMet || targetOwned ? SufficientText
                : MissingText;
            var rowFill = !_synthesisRoute.Exact && !targetOwned ? Card
                : isReady ? Color.FromArgb(249, 253, 250)
                : quantityMet || targetOwned ? Color.FromArgb(249, 251, 255)
                : Color.FromArgb(255, 252, 245);
            var badgeFill = !_synthesisRoute.Exact && !targetOwned ? InputBg
                : isReady ? CraftableFill
                : quantityMet || targetOwned ? SufficientFill
                : MissingFill;
            var roleState = craftableNow ? UiLanguage.T("可合成")
                : targetOwned ? UiLanguage.T("已拥有")
                : quantityMet ? UiLanguage.T("数量达标")
                : canSupply ? UiLanguage.T("可先合成")
                : UiLanguage.IsEnglish ? $"Missing {shortage}" : $"缺少 {shortage}";
            var roleText = $"{UiLanguage.T(routeRow.Role)} · {roleState}";
            var stockText = routeRow.Role == "目标鱼"
                ? craftableNow ? UiLanguage.T("可合成") : targetOwned
                    ? UiLanguage.IsEnglish ? $"Owned {count.Owned}" : $"已拥有 {count.Owned}"
                    : canSupply ? UiLanguage.T("可先合成") : UiLanguage.T("材料未齐")
                : quantityMet
                    ? UiLanguage.IsEnglish
                        ? $"Met · {count.Available}/{routeRow.Count} · {count.Owned} owned"
                        : $"达标 · {count.Available}/{routeRow.Count} · 库{count.Owned}"
                    : UiLanguage.IsEnglish
                        ? $"Need {shortage} · {count.Available}/{routeRow.Count} · {count.Owned} owned"
                        : $"缺{shortage} · {count.Available}/{routeRow.Count} · 库{count.Owned}";
            var sourceText = UiLanguage.T(routeRow.Role == "目标鱼" && _synthesisRoute.Exact
                ? craftableNow ? "可合成" : targetOwned ? "已拥有" : canSupply ? "可先合成" : "材料不足"
                : routeRow.Exact ? "Wiki 配方" : "待验证");
            var index = _synthesisGrid.Rows.Add(
                new string('　', routeRow.Depth) + (species?.DisplayName ?? routeRow.Type),
                StarText(routeRow.Star), routeRow.Count, roleText,
                UiLanguage.T(unlocked ? "已解锁" : "未解锁"), stockText,
                sourceText, UiLanguage.T("合成路线"),
                UiLanguage.T(species?.Season == 1 ? "不可繁育" : "图鉴繁育"), UiLanguage.T(species?.Season == 1 ? "设为目标" : "图鉴设置"));
            var row = _synthesisGrid.Rows[index];
            row.Tag = routeRow;
            row.DefaultCellStyle.BackColor = rowFill;
            row.DefaultCellStyle.ForeColor = White;
            foreach (var cellName in new[] { "count", "role", "owned", "source" })
            {
                row.Cells[cellName].Style.ForeColor = color;
                row.Cells[cellName].Style.BackColor = rowFill;
                row.Cells[cellName].Style.SelectionForeColor = color;
                row.Cells[cellName].Style.SelectionBackColor = badgeFill;
            }
            SetQualityCell(row.Cells["fish"], species?.Tier ?? 0, craftableNow);
            row.Cells["fish"].ToolTipText = FishCatalog.RarityName(species?.Tier ?? 0);
            row.Cells["star"].Style.ForeColor = Gold;
            row.Cells["star"].Style.SelectionForeColor = Gold;
            row.Cells["unlock"].Style.ForeColor = unlocked ? CraftableText : Muted;
            row.Cells["unlock"].Style.SelectionForeColor = unlocked ? CraftableText : Muted;
            row.Cells["role"].Style.BackColor = badgeFill;
            row.Cells["role"].Style.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold);
            row.Cells["owned"].Style.BackColor = badgeFill;
            row.Cells["owned"].Style.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold);
            row.Cells["owned"].ToolTipText = UiLanguage.IsEnglish
                ? routeRow.Role == "目标鱼"
                    ? $"Owned: {count.Owned}; ready to synthesize: {craftableNow}"
                    : $"Usable now: {count.Available}; required: {routeRow.Count}; warehouse total: {count.Owned}"
                : routeRow.Role == "目标鱼"
                    ? $"已拥有：{count.Owned}；当前可合成：{(craftableNow ? "是" : "否")}"
                    : $"当前可用：{count.Available}；需求数量：{routeRow.Count}；仓库总数：{count.Owned}";
            row.Cells["source"].Style.BackColor = isReady ? CraftableFill : rowFill;
            row.Cells["breed"].Style.ForeColor = species?.Season == 1 ? Muted : Green;
            row.Cells["target"].Style.ForeColor = craftableNow ? CraftableText : Gold;
        }
    }

    private static void SetQualityCell(DataGridViewCell cell, int tier, bool craftable = false)
    {
        cell.Style.ForeColor = craftable ? CraftableText : GradeColor(tier);
        cell.Style.SelectionForeColor = cell.Style.ForeColor;
        cell.Style.BackColor = craftable ? CraftableFill : FishQualityPalette.Background;
        cell.Style.SelectionBackColor = craftable ? CraftableFill : FishQualityPalette.SelectedBackground;
        cell.Style.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
    }

    private void SynthesisGridCellContentClick(object sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _synthesisGrid.Rows.Count) return;
        if (_synthesisGrid.Rows[e.RowIndex].Tag is not SynthesisRow row) return;
        var column = _synthesisGrid.Columns[e.ColumnIndex].Name;
        if (column == "route")
        {
            _synthesisType = row.Type;
            _synthesisStar = row.Star;
            _synthesisTargetCombo.SelectedValue = row.Type;
            _synthesisStarCombo.SelectedIndex = Math.Clamp(row.Star - 1, 0, 4);
            RefreshSynthesisPage();
        }
        else if (column == "breed")
        {
            var species = FishCatalog.Find(row.Type);
            if (species?.Season == 1)
            {
                _log.Write($"{species.Name} 属于赛季鱼，Wiki 路线标注为不可繁育。");
                return;
            }
            _log.Write($"{FishCatalog.Name(row.Type)} 属于普通鱼，请在“图鉴 / 目标”页面设置繁育目标。");
        }
        else if (column == "target")
        {
            if (FishCatalog.Find(row.Type)?.Season == 1)
                SetSynthesisGoal(row.Type, row.Star, false);
            else
                _log.Write($"{FishCatalog.Name(row.Type)} 属于普通鱼，请在“图鉴 / 目标”页面设置目标。");
        }
    }

    private async Task RefreshWikiDatabaseAsync()
    {
        _log.Write("正在刷新 Wiki 数据缓存……");
        var result = await WikiDatabase.RefreshAsync();
        FishCatalog.ApplyWikiRecords(WikiDatabase.Data.Fish);
        RefreshSynthesisTargetOptions();
        RefreshSynthesisPage();
        _log.Write(result.Message);
    }

    private void RefreshSynthesisTargetOptions()
    {
        var targets = FishCatalog.Species.Where(x => x.Season > 0).ToList();
        var current = _synthesisType;
        _synthesisTargetCombo.DataSource = null;
        _synthesisTargetCombo.DataSource = targets;
        _synthesisTargetCombo.DisplayMember = "DisplayName";
        _synthesisTargetCombo.ValueMember = "Code";
        if (targets.Any(x => string.Equals(x.Code, current, StringComparison.OrdinalIgnoreCase)))
            _synthesisTargetCombo.SelectedValue = current;
    }

    private void SetSynthesisGoal(string type, int star, bool preferBreed)
    {
        if (FishCatalog.Find(type) == null) return;
        _settings.GoalType = type;
        _settings.GoalStar = Math.Clamp(star, 1, 5);
        _settings.GoalEnabled = true;
        _goalPlan = GoalPlanner.Build(_settings.GoalType, _settings.GoalStar, _collectionTypes, _lastFish);
        _settings.Save();
        _log.Write($"已设置目标：{FishCatalog.Name(type)} {StarText(_settings.GoalStar)}" +
                   (preferBreed ? "，自动繁育将优先使用路线鱼。" : "。"));
        RefreshSynthesisPage();
        RefreshCollectionPage();
    }

    private void BuildCollection(Panel body)
    {
        var page = new Panel();
        AddPage(body, "collection", page);

        var target = Surface();
        target.Dock = DockStyle.Top;
        target.Height = 184;
        var title = Label("我的图鉴与最终目标", 12, true);
        title.SetBounds(18, 10, 280, 30);
        _lblCollectionSummary.SetBounds(305, 10, 370, 30);
        _lblCollectionSummary.ForeColor = Muted;

        var targetLabel = Label("目标鱼种", 9, false, Muted);
        targetLabel.SetBounds(18, 54, 70, 28);
        _goalTypeCombo.SetBounds(88, 53, 250, 32);
        _goalTypeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _goalTypeCombo.FlatStyle = FlatStyle.Flat;
        _goalTypeCombo.BackColor = InputBg;
        _goalTypeCombo.ForeColor = White;
        _goalTypeCombo.DisplayMember = "DisplayName";
        _goalTypeCombo.ValueMember = "Code";
        _goalTypeCombo.DataSource = FishCatalog.Species.Where(x => x.Tier > 0).ToList();
        if (FishCatalog.Find(_settings.GoalType) != null)
            _goalTypeCombo.SelectedValue = _settings.GoalType;

        var filterLabel = Label("显示分类", 9, false, Muted);
        filterLabel.SetBounds(355, 54, 70, 28);
        _goalUnlockFilter.SetBounds(425, 53, 150, 32);
        _goalUnlockFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _goalUnlockFilter.FlatStyle = FlatStyle.Flat;
        _goalUnlockFilter.BackColor = InputBg;
        _goalUnlockFilter.ForeColor = White;

        var starLabel = Label("目标星级", 9, false, Muted);
        starLabel.SetBounds(18, 101, 70, 28);
        _goalStarCombo.SetBounds(88, 100, 110, 32);
        _goalStarCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _goalStarCombo.FlatStyle = FlatStyle.Flat;
        _goalStarCombo.BackColor = InputBg;
        _goalStarCombo.ForeColor = White;
        _goalStarCombo.Items.AddRange(new object[] { "★☆☆☆☆", "★★☆☆☆", "★★★☆☆", "★★★★☆", "★★★★★" });
        if (_goalStarCombo.Items.Count > 0)
            _goalStarCombo.SelectedIndex = Math.Clamp(_settings.GoalStar - 1, 0, _goalStarCombo.Items.Count - 1);

        _chkGoal.SetBounds(215, 98, 180, 36);
        _chkGoal.Text = "按目标路线优先";
        _chkGoal.Checked = _settings.GoalEnabled;
        StyleCheck(_chkGoal);
        _lblTargetStatus.SetBounds(18, 98, 720, 32);
        _lblTargetStatus.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        target.Controls.AddRange(new Control[] { title, _lblCollectionSummary, targetLabel, _goalTypeCombo, filterLabel, _goalUnlockFilter, starLabel, _goalStarCombo, _chkGoal, _lblTargetStatus });

        _goalTypeCombo.SelectedValueChanged += (_, _) => GoalSelectionChanged();
        _goalUnlockFilter.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingGoalOptions) return;
            RefreshGoalTypeOptions();
            RefreshCollectionPage();
        };
        _goalStarCombo.SelectedIndexChanged += (_, _) => GoalSelectionChanged();
        _chkGoal.CheckedChanged += (_, _) => GoalSelectionChanged();

        RefreshGoalTypeOptions();

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 15, 0, 0) };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

        var routeCard = Surface();
        routeCard.Margin = new Padding(0, 0, 12, 0);
        routeCard.MinimumSize = new Size(0, 320);
        var routeTitle = Label("合成路线", 11, true);
        routeTitle.Dock = DockStyle.Top;
        routeTitle.Height = 34;
        _txtGoalRoute.Multiline = true;
        _txtGoalRoute.ReadOnly = true;
        _txtGoalRoute.ScrollBars = ScrollBars.Vertical;
        _txtGoalRoute.Dock = DockStyle.Fill;
        _txtGoalRoute.BackColor = Card;
        _txtGoalRoute.ForeColor = White;
        _txtGoalRoute.BorderStyle = BorderStyle.None;
        _txtGoalRoute.Font = new Font("Microsoft YaHei UI", 9F);
        routeCard.Controls.Add(_txtGoalRoute);
        routeCard.Controls.Add(routeTitle);

        var listCard = Surface();
        listCard.MinimumSize = new Size(0, 320);
        var listTitle = Label("目标鱼种图鉴", 11, true);
        listTitle.Dock = DockStyle.Top;
        listTitle.Height = 34;
        _txtCollectionList.Multiline = true;
        _txtCollectionList.ReadOnly = true;
        _txtCollectionList.ScrollBars = RichTextBoxScrollBars.Vertical;
        _txtCollectionList.Dock = DockStyle.Fill;
        _txtCollectionList.BackColor = Card;
        _txtCollectionList.ForeColor = Muted;
        _txtCollectionList.BorderStyle = BorderStyle.None;
        _txtCollectionList.Font = new Font("Microsoft YaHei UI", 8.5F);
        _txtCollectionList.DetectUrls = false;
        _txtCollectionList.HideSelection = false;
        listCard.Controls.Add(_txtCollectionList);
        listCard.Controls.Add(listTitle);
        split.Controls.Add(routeCard, 0, 0);
        split.Controls.Add(listCard, 1, 0);
        page.Controls.Add(split);
        page.Controls.Add(target);
        RefreshCollectionPage();
    }

    private void GoalSelectionChanged()
    {
        if (_updatingGoalOptions) return;
        if (_goalTypeCombo.SelectedValue is string code) _settings.GoalType = code;
        else if (_goalTypeCombo.SelectedItem is FishSpecies species) _settings.GoalType = species.Code;
        _settings.GoalStar = Math.Clamp(_goalStarCombo.SelectedIndex + 1, 1, 5);
        _settings.GoalEnabled = _chkGoal.Checked;
        _goalPlan = GoalPlanner.Build(_settings.GoalType, _settings.GoalStar, _collectionTypes, _lastFish);
        RefreshCollectionPage();
        _settings.Save();
    }

    private void RefreshGoalTypeOptions()
    {
        if (_goalTypeCombo == null || _goalUnlockFilter == null) return;
        var all = FishCatalog.Species.Where(x => x.Tier > 0).ToList();
        var known = new HashSet<string>(_collectionTypes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>((_lastFish ?? new List<FishDto>())
            .Select(f => f.ty).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        bool IsUnlocked(FishSpecies species) => known.Contains(species.Code) || owned.Contains(species.Code);
        var unlocked = all.Where(IsUnlocked).ToList();
        var locked = all.Where(x => !IsUnlocked(x)).ToList();
        var options = new[] { $"全部鱼种 ({all.Count})", $"已解锁鱼种 ({unlocked.Count})", $"未解锁鱼种 ({locked.Count})" }
            .Select(UiLanguage.T).ToArray();
        var selectedCode = _settings.GoalType;
        var selectedFilter = _goalUnlockFilter.SelectedIndex < 0
            ? 0
            : Math.Clamp(_goalUnlockFilter.SelectedIndex, 0, 2);
        var source = selectedFilter switch
        {
            1 => unlocked,
            2 => locked,
            _ => all
        };
        var selected = source.FirstOrDefault(x => string.Equals(x.Code, selectedCode, StringComparison.OrdinalIgnoreCase));
        _updatingGoalOptions = true;
        try
        {
            _goalUnlockFilter.Items.Clear();
            _goalUnlockFilter.Items.AddRange(options);
            _goalUnlockFilter.SelectedIndex = selectedFilter;
            _goalTypeCombo.DataSource = null;
            _goalTypeCombo.DataSource = source;
            _goalTypeCombo.DisplayMember = "DisplayName";
            _goalTypeCombo.ValueMember = "Code";
            _goalTypeCombo.SelectedValue = selected?.Code ?? source.FirstOrDefault()?.Code;
        }
        finally
        {
            _updatingGoalOptions = false;
        }
        if (selected == null && source.Count > 0)
        {
            _settings.GoalType = source[0].Code;
            _goalPlan = GoalPlanner.Build(_settings.GoalType, _settings.GoalStar, _collectionTypes, _lastFish);
            _settings.Save();
        }
    }

    private void RefreshCollectionPage()
    {
        if (_txtGoalRoute == null || _txtCollectionList == null) return;
        var known = new HashSet<string>(_collectionTypes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var fish = _lastFish ?? new List<FishDto>();
        var discovered = FishCatalog.Species.Count(x => known.Contains(x.Code));
        _lblCollectionSummary.Text = UiLanguage.T($"已发现 {discovered} / {FishCatalog.Species.Count} 种　当前仓库 {fish.Count} 条");
        RefreshGoalTypeOptions();
        _txtGoalRoute.Text = UiLanguage.T(string.Join(Environment.NewLine, (_goalPlan?.Lines ?? Array.Empty<string>()).Select(x => "• " + x)));
        var target = FishCatalog.Find(_settings.GoalType);
        _txtCollectionList.Clear();
        if (target == null)
        {
            _lblTargetStatus.Text = UiLanguage.T("目标图鉴：请选择目标鱼种");
            _lblTargetStatus.ForeColor = Muted;
            _txtCollectionList.AppendText(UiLanguage.T("请选择目标鱼种后查看解锁状态、仓库数量和星级进度。"));
        }
        else
        {
            var owned = fish.Where(f => string.Equals(f.ty, target.Code, StringComparison.OrdinalIgnoreCase)).ToList();
            var unlocked = known.Contains(target.Code) || owned.Count > 0;
            var status = UiLanguage.T(unlocked ? "已解锁" : "未解锁");
            var highestStar = owned.Count == 0 ? 0 : owned.Max(f => f.Stars);
            _lblTargetStatus.Text = UiLanguage.T($"目标图鉴：{status}　{target.Name}　仓库 {owned.Count} 条　最高 {StarText(highestStar)}");
            _lblTargetStatus.ForeColor = unlocked ? Green : Color.FromArgb(173, 94, 9);
            void AddTargetLine(string text, Color color)
            {
                _txtCollectionList.SelectionColor = color;
                _txtCollectionList.AppendText(text + Environment.NewLine);
            }
            AddTargetLine(UiLanguage.T($"目标鱼种：{target.Name}"), White);
            AddTargetLine(UiLanguage.T($"图鉴状态：{status}"), unlocked ? Green : Color.FromArgb(173, 94, 9));
            AddTargetLine(UiLanguage.T($"稀有度：{FishCatalog.RarityName(target.Tier)}"), Muted);
            AddTargetLine(UiLanguage.T($"目标星级：{StarText(_settings.GoalStar)}"), Gold);
            AddTargetLine(UiLanguage.T($"仓库数量：{owned.Count} 条"), White);
            AddTargetLine(UiLanguage.T($"仓库最高星级：{StarText(highestStar)}"), highestStar > 0 ? Gold : Muted);
            AddTargetLine(UiLanguage.T(unlocked ? "解锁来源：鱼种图鉴或仓库已有该鱼" : "解锁来源：发现该鱼种后自动更新"), Muted);
        }
        _txtCollectionList.SelectionStart = 0;
        _txtCollectionList.SelectionLength = 0;
    }

    private void BuildLogs(Panel body)
    {
        var page = new Panel();
        AddPage(body, "logs", page);
        var card = Surface();
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(12);
        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.Dock = DockStyle.Fill;
        _txtLog.BackColor = Card;
        _txtLog.ForeColor = Muted;
        _txtLog.BorderStyle = BorderStyle.None;
        _txtLog.Font = new Font("Consolas", 9F);
        card.Controls.Add(_txtLog);
        page.Controls.Add(card);
    }

    private void BuildSettings(Panel body)
    {
        var page = new Panel { AutoScroll = true };
        AddPage(body, "settings", page);
        var deploy = Surface();
        deploy.Dock = DockStyle.Top;
        deploy.Height = 376;
        var deployTitle = Label("一键部署", 13, true);
        deployTitle.SetBounds(20, 14, 240, 34);
        var deployNote = Label(
            "一次点击完成运行前的全部准备：安装 BepInEx 运行环境、放入插件、生成并打开游戏内的动作总闸。",
            9, false, Muted);
        deployNote.SetBounds(20, 46, 900, 24);
        var gameDirLabel = Label("游戏目录", 10, true, Muted);
        gameDirLabel.SetBounds(22, 82, 110, 32);
        _gameDirBox.SetBounds(138, 81, 470, 32);
        _gameDirBox.BackColor = InputBg;
        _gameDirBox.ForeColor = White;
        _gameDirBox.BorderStyle = BorderStyle.FixedSingle;
        _gameDirBox.PlaceholderText = "留空自动查找，例如 D:\\SteamLibrary\\steamapps\\common\\PC FISH";
        _gameDirBrowse = ActionButton("浏览…");
        _gameDirBrowse.SetBounds(616, 80, 96, 34);
        _gameDirBrowse.Click += (_, _) => BrowseGameDir();
        _deployButton = ActionButton("一键部署", Green);
        _deployButton.SetBounds(718, 80, 118, 34);
        _deployButton.Click += async (_, _) => await RunDeployAsync();
        _deployStatus.SetBounds(20, 120, 816, 76);
        _deployStatus.ForeColor = Muted;
        _deployStatus.Font = new Font("Microsoft YaHei UI", 9F);
        deploy.Controls.AddRange(new Control[]
        {
            deployTitle, deployNote, gameDirLabel, _gameDirBox, _gameDirBrowse, _deployButton, _deployStatus
        });

        // 版本更新：检查 GitHub 上的最新发布，由用户决定是否更新。
        var updateTitle = Label("版本更新", 10, true, Muted);
        updateTitle.SetBounds(22, 208, 110, 28);
        _updateStatus.SetBounds(138, 206, 560, 30);
        _updateStatus.ForeColor = Muted;
        _updateStatus.Font = new Font("Microsoft YaHei UI", 9F);
        _updateCheckButton = ActionButton("检查更新");
        _updateCheckButton.SetBounds(702, 203, 108, 34);
        _updateCheckButton.Click += async (_, _) => await CheckForUpdateAsync();
        _updateApplyButton = ActionButton("立即更新", Green);
        _updateApplyButton.SetBounds(816, 203, 108, 34);
        _updateApplyButton.Enabled = false;
        _updateApplyButton.Visible = false;
        _updateApplyButton.Click += async (_, _) => await ApplyUpdateAsync();
        _updatePageButton = ActionButton("打开发布页");
        _updatePageButton.SetBounds(22, 244, 148, 34);
        _updatePageButton.Click += (_, _) =>
        {
            UpdateChecker.OpenReleasesPage();
            _log.Write("已在浏览器中打开发布页：" + UpdateChecker.ReleasesPage);
        };
        var updateNote = Label("更新只替换程序文件，不会动你的配置和游戏数据。", 9, false, Muted);
        updateNote.SetBounds(22, 286, 780, 26);

        _chkAutoUpdate.SetBounds(22, 322, 300, 36);
        _chkAutoUpdate.Text = "启动时与每小时自动检查更新";
        _chkAutoUpdate.Checked = _settings.AutoCheckUpdate;
        StyleCheck(_chkAutoUpdate);
        _chkAutoUpdate.CheckedChanged += (_, _) =>
        {
            _settings.AutoCheckUpdate = _chkAutoUpdate.Checked;
            _settings.Save();
            if (_chkAutoUpdate.Checked)
            {
                _nextUpdateCheckAt = DateTime.Now;
                _log.Write("已开启自动检查更新。");
            }
            else
            {
                _log.Write("已关闭自动检查更新。");
            }
        };

        deploy.Controls.AddRange(new Control[]
        {
            updateTitle, _updateStatus, _updateCheckButton, _updateApplyButton, _updatePageButton,
            updateNote, _chkAutoUpdate
        });

        var card = Surface();
        card.Dock = DockStyle.Top;
        card.Height = 340;
        var title = Label("设置", 13, true);
        title.SetBounds(20, 16, 240, 34);
        var languageLabel = Label("界面语言", 10, true, Muted);
        languageLabel.SetBounds(22, 68, 110, 32);
        _languageCombo.SetBounds(138, 67, 210, 34);
        _languageCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _languageCombo.FlatStyle = FlatStyle.Flat;
        _languageCombo.BackColor = InputBg;
        _languageCombo.ForeColor = White;
        _languageCombo.Items.AddRange(new object[] { "简体中文", "English" });
        _languageCombo.SelectedIndex = UiLanguage.IsEnglish ? 1 : 0;
        var languageNote = Label("语言会立即应用到助手界面。", 9, false, Muted);
        languageNote.SetBounds(138, 105, 470, 26);
        var closeTitle = Label("关闭按钮", 10, true, Muted);
        closeTitle.SetBounds(22, 157, 110, 28);
        _closeBehaviorCombo.SetBounds(138, 153, 250, 34);
        _closeBehaviorCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _closeBehaviorCombo.FlatStyle = FlatStyle.Flat;
        _closeBehaviorCombo.BackColor = InputBg;
        _closeBehaviorCombo.ForeColor = White;
        _closeBehaviorCombo.Items.AddRange(new object[] { "每次询问", "直接退出助手", "缩小至托盘" });
        _closeBehaviorCombo.SelectedIndex = _settings.CloseBehavior switch { "exit" => 1, "tray" => 2, _ => 0 };
        _closeBehaviorCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_changingLanguage || _closeBehaviorCombo.SelectedIndex < 0) return;
            _settings.CloseBehavior = _closeBehaviorCombo.SelectedIndex switch { 1 => "exit", 2 => "tray", _ => "ask" };
            _settings.Save();
        };
        var closeNote = Label("点击右上角 X 时按此设置执行，选择后自动保存。托盘模式下助手继续运行。", 9, false, Muted);
        closeNote.SetBounds(138, 191, 640, 46);
        var commandTitle = Label("命令", 10, true, Muted);
        commandTitle.SetBounds(22, 246, 110, 28);
        _commandBox.SetBounds(138, 244, 340, 32);
        _commandBox.BackColor = InputBg;
        _commandBox.ForeColor = White;
        _commandBox.BorderStyle = BorderStyle.FixedSingle;
        _commandBox.PlaceholderText = "输入 /setting 打开设置命令";
        _commandBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            RunCommand(_commandBox.Text);
        };
        var commandRun = ActionButton("执行");
        commandRun.SetBounds(490, 243, 84, 34);
        commandRun.Click += (_, _) => RunCommand(_commandBox.Text);
        _commandStatus.SetBounds(586, 244, 300, 30);
        _commandStatus.ForeColor = Muted;
        _commandStatus.Font = new Font("Microsoft YaHei UI", 9F);
        var commandNote = Label("输入 /setting 或 /设置，直接跳到本页的设置项；输入 /help 查看可用命令。", 9, false, Muted);
        commandNote.SetBounds(138, 282, 700, 40);
        card.Controls.AddRange(new Control[]
        {
            title, languageLabel, _languageCombo, languageNote, closeTitle, _closeBehaviorCombo, closeNote,
            commandTitle, _commandBox, commandRun, _commandStatus, commandNote
        });
        // Dock.Top 的排列顺序是后添加的在上，所以先加语言卡、后加部署卡，一键部署才会在页面顶部。
        page.Controls.Add(card);
        page.Controls.Add(deploy);

        _chkAutoUpdate.Checked = _settings.AutoCheckUpdate;
        _gameDirBox.Text = _settings.GameDir ?? "";
        _gameDirBox.TextChanged += (_, _) =>
        {
            _settings.GameDir = _gameDirBox.Text.Trim();
            _settings.Save();
        };

        _languageCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_changingLanguage || _languageCombo.SelectedIndex < 0) return;
            ChangeLanguage(_languageCombo.SelectedIndex == 1 ? "en" : "zh");
        };
    }

    /// <summary>
    /// 设置页的命令行。目前支持 /setting（打开设置）和 /help。
    /// 命令识别大小写不敏感，也接受中文写法。
    /// </summary>
    private void RunCommand(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
        {
            _commandStatus.ForeColor = Muted;
            _commandStatus.Text = UiLanguage.T("请输入命令，例如 /setting");
            return;
        }

        var name = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        switch (name)
        {
            case "/setting":
            case "/settings":
            case "/设置":
                ShowPage("settings");
                _commandStatus.ForeColor = Green;
                _commandStatus.Text = UiLanguage.T("已打开设置");
                _log.Write("命令 /setting：已打开设置页面。");
                break;
            case "/help":
            case "/?":
            case "/帮助":
                _commandStatus.ForeColor = Muted;
                _commandStatus.Text = UiLanguage.T("/setting 打开设置　/help 查看命令");
                break;
            default:
                _commandStatus.ForeColor = Color.FromArgb(184, 71, 100);
                _commandStatus.Text = UiLanguage.T("未知命令：") + name;
                break;
        }
    }

    /// <summary>
    /// 把一次检查结果写进设置页的版本更新卡片。
    /// 手动检查和控制器的自动检查共用这一段，保证两边显示一致。
    /// </summary>
    private void ApplyUpdateResult(UpdateInfo info)
    {
        _pendingUpdate = info.Ok && info.HasUpdate && !string.IsNullOrWhiteSpace(info.AssetUrl)
            ? info : null;

        var lines = new List<string> { UiLanguage.T(info.Message) };
        if (_pendingUpdate != null)
        {
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.AssetName))
                details.Add($"{info.AssetName}（{info.DescribeSize()}）");
            if (!string.IsNullOrWhiteSpace(info.LatestTag)) details.Add(info.LatestTag);
            if (details.Count > 0) lines.Add(string.Join("  ·  ", details));
        }
        else if (info.Ok && info.HasUpdate)
        {
            lines.Add(UiLanguage.T("这个发布没有可下载的压缩包，请到发布页手动下载。"));
        }

        _updateStatus.Text = string.Join(Environment.NewLine, lines);
        _updateStatus.ForeColor = _pendingUpdate != null ? Green : Muted;
        _updateApplyButton.Visible = _pendingUpdate != null;
        _updateApplyButton.Enabled = _pendingUpdate != null;
    }

    /// <summary>检查 GitHub 上的最新发布。只查询，不下载。</summary>
    private async Task CheckForUpdateAsync()
    {
        _updateCheckButton.Enabled = false;
        _updateStatus.ForeColor = Muted;
        _updateStatus.Text = UiLanguage.T("正在检查更新…");
        try
        {
            var info = await UpdateChecker.CheckAsync();
            ApplyUpdateResult(info);
            var lines = _updateStatus.Text.Split(Environment.NewLine);
            foreach (var line in lines) _log.Write("检查更新：" + line);
            if (info.Ok && info.HasUpdate && !string.IsNullOrWhiteSpace(info.ReleaseUrl))
                _log.Write("发布页：" + info.ReleaseUrl);
            // 手动检查过就重置计时，避免紧接着又自动跑一次。
            _nextUpdateCheckAt = DateTime.Now.AddHours(1);
            if (info.Ok && info.HasUpdate) _notifiedUpdateTag = info.LatestTag;
        }
        catch (Exception ex)
        {
            _updateStatus.Text = "检查更新失败：" + ex.Message;
            _updateStatus.ForeColor = Pink;
            _log.Write("检查更新异常：" + ex.Message);
        }
        finally
        {
            _updateCheckButton.Enabled = true;
        }
    }

    /// <summary>下载并替换程序文件，然后重启。</summary>
    private async Task ApplyUpdateAsync()
    {
        var info = _pendingUpdate;
        if (info == null) return;

        var confirm = MessageBox.Show(this,
            UiLanguage.T($"将下载并安装 {info.LatestTag}，程序会自动重启。是否继续？") + Environment.NewLine +
            UiLanguage.T("配置文件和游戏数据不会被改动。"),
            UiLanguage.T("确认更新"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        _updateApplyButton.Enabled = false;
        _updateCheckButton.Enabled = false;
        var progress = new List<string>();
        void Report(string text)
        {
            progress.Add(text);
            _updateStatus.Text = string.Join(Environment.NewLine, progress);
            _updateStatus.ForeColor = Muted;
            _log.Write("更新：" + text);
        }

        try
        {
            var (ok, message, shouldExit) = await UpdateChecker.ApplyAsync(info, text => Ui(() => Report(text)));
            Report(message);
            if (!ok)
            {
                _updateStatus.ForeColor = Pink;
                _updateApplyButton.Enabled = true;
                _updateCheckButton.Enabled = true;
                return;
            }
            _updateStatus.ForeColor = Green;
            if (!shouldExit) return;

            SaveSettings();
            _allowClose = true;
            _trayIcon.Visible = false;
            _client.Dispose();
            Application.Exit();
        }
        catch (Exception ex)
        {
            Report("更新失败：" + ex.Message);
            _updateStatus.ForeColor = Pink;
            _updateApplyButton.Enabled = true;
            _updateCheckButton.Enabled = true;
        }
    }

    /// <summary>让用户手动指定游戏目录。留空则一键部署时自动查找。</summary>
    private void BrowseGameDir()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = UiLanguage.T("选择包含 PCFish.exe 的游戏目录"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        var current = (_gameDirBox.Text ?? "").Trim();
        var auto = GameDeploy.FindGameDir(current);
        if (auto != null) dialog.SelectedPath = auto;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _gameDirBox.Text = dialog.SelectedPath;
    }

    /// <summary>
    /// 一键部署：装齐运行环境（BepInEx）、放入插件、生成并打开游戏内的动作总闸。
    /// 全程异步，安装过程会实时把每一步写进状态区，避免界面像卡住。
    /// </summary>
    private async Task RunDeployAsync()
    {
        _deployButton.Enabled = false;
        _deployStatus.ForeColor = Muted;
        _deployStatus.Text = UiLanguage.T("正在准备…");
        var progress = new List<string>();
        void Report(string text)
        {
            progress.Add(text);
            _deployStatus.Text = string.Join(Environment.NewLine, progress.TakeLast(4));
            _log.Write("一键部署：" + text);
        }

        try
        {
            var result = await GameDeploy.RunAsync((_gameDirBox.Text ?? "").Trim(), _settings.Port,
                text => Ui(() => Report(text)));
            var lines = new List<string>(result.Steps);
            lines.Add(result.Message);
            _deployStatus.Text = string.Join(Environment.NewLine, lines.TakeLast(6));
            _deployStatus.ForeColor = result.Ok ? Green : Gold;
            foreach (var step in result.Steps) _log.Write("一键部署：" + step);
            _log.Write("一键部署：" + result.Message);
            if (!string.IsNullOrWhiteSpace(result.GameDir) && string.IsNullOrWhiteSpace(_settings.GameDir))
            {
                _gameDirBox.Text = result.GameDir;
            }
        }
        catch (Exception ex)
        {
            _deployStatus.Text = UiLanguage.T("一键部署失败：") + ex.Message;
            _deployStatus.ForeColor = Pink;
            _log.Write("一键部署异常：" + ex.Message);
        }
        finally
        {
            _deployButton.Enabled = true;
        }
    }

    private void ChangeLanguage(string language)
    {
        if (string.Equals(UiLanguage.Current, language, StringComparison.OrdinalIgnoreCase)) return;
        var sourceLanguage = UiLanguage.Current;
        _settings.Language = language;
        _settings.Save();
        UiLanguage.Set(language);
        _changingLanguage = true;
        try
        {
            UiLanguage.Apply(this, sourceLanguage);
            if (_trayMenu != null) UiLanguage.Apply(_trayMenu, sourceLanguage);
            if (_trayIcon != null) _trayIcon.Text = UiLanguage.T("PCFish 助手");
            RefreshGoalTypeOptions();
            RefreshSynthesisTargetOptions();
            RefreshWarehouse();
            RefreshCollectionPage();
            RefreshSynthesisPage();
            UpdateStatusLabels();
            ShowPage(_activePage);
        }
        finally
        {
            _changingLanguage = false;
        }
        _settings.Save();
    }

    private void UpdateDashboardSummary()
    {
        if (_lblHeaderConnection.IsDisposed) return;

        // 开机自检期间：显示「初始化中」，并且明确还没连接。
        // 这时候不允许连接游戏，状态要如实反映，不能让用户以为已经连上了。
        if (_startupCheckPending)
        {
            _lblRunState.Text = "● " + UiLanguage.T("初始化中");
            _lblRunState.ForeColor = Color.FromArgb(143, 87, 0);
            _lblRunState.BackColor = Color.FromArgb(255, 243, 212);
            _lblHeaderConnection.Text = UiLanguage.T("● 未连接");
            _lblHeaderConnection.ForeColor = Muted;
            if (_btnRecheck != null) _btnRecheck.Visible = false;
            _lblOverviewFish.Text = _lastFish?.Count.ToString() ?? "—";
            _lblOverviewReady.Text = _lastFish?.Count(f => Selection.CanBreed(f) && Selection.IsOffCooldown(f)).ToString() ?? "—";
            _lblOverviewHeart.Text = _charge < 0 ? "—" : $"{_charge} / 5";
            _lblOverviewLevel.Text = _tankLevel < 0 ? "—" : $"Lv {_tankLevel}";
            return;
        }

        var blocked = !_startupCheckOk;
        var paused = blocked || _windowLocked || _actionPauseReason.Length > 0 ||
            _client.Connected && (!_bridgeVersionOk || !_bridgeAllowsActions);
        var waiting = _chkAuto.Checked && !_armed;
        var state = blocked ? "环境未就绪"
            : !_chkAuto.Checked && !_armed ? "已停止"
            : paused ? "已暂停"
            : !_client.Connected ? "等待连接"
            : waiting ? "正在开启" : "正在运行";
        _lblRunState.Text = "● " + UiLanguage.T(state);
        _lblRunState.ForeColor = state == "正在运行" ? Color.FromArgb(17, 108, 62)
            : state is "已暂停" or "等待连接" or "正在开启" or "环境未就绪" ? Color.FromArgb(143, 87, 0)
            : Color.FromArgb(96, 108, 125);
        _lblRunState.BackColor = state == "正在运行" ? Color.FromArgb(221, 246, 232)
            : state is "已暂停" or "等待连接" or "正在开启" or "环境未就绪" ? Color.FromArgb(255, 243, 212)
            : Color.FromArgb(235, 239, 244);
        if (_btnRecheck != null) _btnRecheck.Visible = blocked;
        _lblHeaderConnection.Text = UiLanguage.T(_client.Connected ? "● 已连接" : "● 未连接");
        _lblHeaderConnection.ForeColor = _client.Connected ? Green : Muted;
        _lblOverviewFish.Text = _lastFish?.Count.ToString() ?? "—";
        _lblOverviewReady.Text = _lastFish?.Count(f => Selection.CanBreed(f) && Selection.IsOffCooldown(f)).ToString() ?? "—";
        _lblOverviewHeart.Text = _charge < 0 ? "—" : $"{_charge} / 5";
        _lblOverviewLevel.Text = _tankLevel < 0 ? "—" : $"Lv {_tankLevel}";
    }

    /// <summary>
    /// 仓库显示分组：同名且同星级的鱼合成一张卡。繁育次数用组内总和显示。
    /// </summary>
    private sealed class WarehouseGroup
    {
        internal readonly List<FishDto> Fish;
        internal FishDto Representative => Fish[0];
        internal string Name => Representative.Name;
        internal int Stars => Representative.Stars;
        internal int Quantity => Fish.Count;
        internal int BreedCount => Fish.Sum(f => Math.Max(0, f.bc));
        internal int BreedMax => Fish.Sum(f => Math.Max(0, f.bm));

        internal WarehouseGroup(IEnumerable<FishDto> fish) => Fish = fish.ToList();
    }

    /// <summary>
    /// 虚拟化仓库画布：只创建一个 WinForms 控件，不再为每条鱼创建六个子控件。
    /// OnPaint 根据裁剪区域只画当前可见的卡片，滚动时画下一屏。
    /// 这样仓库有 900+ 条鱼时，打开页面也不会创建几千个窗口控件卡死。
    /// </summary>
    private sealed class WarehouseGridCanvas : ScrollableControl
    {
        internal const int CardGap = 10;
        private const int CardHeight = 170;
        private const int MinCardWidth = 118;
        private const int HorizontalPadding = 6;
        private const int VerticalPadding = 6;

        private static readonly Font PriceFont = new("Microsoft YaHei UI", 8F, FontStyle.Bold);
        private static readonly Font BreedFont = new("Microsoft YaHei UI", 8F);
        private static readonly Font GradeFont = new("Microsoft YaHei UI", 8F, FontStyle.Bold);
        private static readonly Font StarFont = new("Microsoft YaHei UI", 8F);
        private static readonly Font NameFont = new("Microsoft YaHei UI", 7.5F);

        private List<WarehouseGroup> _items = new();
        private Func<FishDto, Image> _getIcon;
        private Func<FishDto, MarketQuote> _getQuote;
        private Func<FishDto, string> _getPrice;
        private Func<int, string> _getGrade;
        private Func<FishDto, string> _getCooldown;
        private Func<FishDto, string> _getStatus;
        private readonly ToolTip _tip = new() { InitialDelay = 450, ReshowDelay = 100 };

        private int _columns = 1;
        private int _cardWidth = MinCardWidth;
        private int _hoveredIndex = -1;

        internal WarehouseGridCanvas()
        {
            AutoScroll = true;
            BackColor = Bg;
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true;
        }

        internal void Configure(Func<FishDto, Image> getIcon, Func<FishDto, MarketQuote> getQuote,
            Func<FishDto, string> getPrice, Func<int, string> getGrade,
            Func<FishDto, string> getCooldown, Func<FishDto, string> getStatus)
        {
            _getIcon = getIcon;
            _getQuote = getQuote;
            _getPrice = getPrice;
            _getGrade = getGrade;
            _getCooldown = getCooldown;
            _getStatus = getStatus;
        }

        internal void SetItems(List<WarehouseGroup> items)
        {
            var oldScroll = Math.Max(0, -AutoScrollPosition.Y);
            _items = items ?? new List<WarehouseGroup>();
            Relayout();
            if (AutoScrollMinSize.Height > ClientSize.Height)
                AutoScrollPosition = new Point(0, Math.Min(oldScroll, AutoScrollMinSize.Height - ClientSize.Height));
            Invalidate();
        }

        internal void Relayout()
        {
            var usable = Math.Max(MinCardWidth, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
            _columns = Math.Max(1, (usable + CardGap) / (MinCardWidth + CardGap));
            _cardWidth = Math.Max(MinCardWidth, (usable - (_columns - 1) * CardGap) / _columns);
            var rows = (_items.Count + _columns - 1) / _columns;
            var height = VerticalPadding * 2 + rows * (CardHeight + CardGap);
            AutoScrollMinSize = new Size(0, height);
            Invalidate();
        }

        internal void RefreshCards() => Invalidate();

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_items.Count == 0) return;

            var offset = AutoScrollPosition;
            var viewportTop = Math.Max(0, -offset.Y);
            var pitchY = CardHeight + CardGap;
            var firstRow = Math.Max(0, (viewportTop - VerticalPadding) / pitchY);
            var lastRow = Math.Min((_items.Count - 1) / _columns,
                (viewportTop + ClientSize.Height) / pitchY + 1);

            var state = e.Graphics.Save();
            e.Graphics.TranslateTransform(offset.X, offset.Y);
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

            for (var row = firstRow; row <= lastRow; row++)
            {
                for (var col = 0; col < _columns; col++)
                {
                    var index = row * _columns + col;
                    if (index >= _items.Count) break;
                    var x = HorizontalPadding + col * (_cardWidth + CardGap);
                    var y = VerticalPadding + row * pitchY;
                    var bounds = new Rectangle(x, y, _cardWidth, CardHeight);
                    DrawCard(e.Graphics, bounds, _items[index], index == _hoveredIndex);
                }
            }
            e.Graphics.Restore(state);
        }

        private void DrawCard(Graphics g, Rectangle bounds, WarehouseGroup group, bool hovered)
        {
            var fish = group.Representative;
            var quality = FishQualityPalette.ForTier(fish.RarityTier);
            using var background = new SolidBrush(Color.White);
            using var border = new Pen(hovered ? Color.White : quality, hovered ? 2F : 1.5F);
            g.FillRectangle(background, bounds);
            g.DrawRectangle(border, bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);

            var quote = _getQuote?.Invoke(fish);
            var priceText = quote == null ? "" : _getPrice?.Invoke(fish) ?? "";
            var priceColor = quote == null ? Muted : CraftableText;
            DrawCentered(g, priceText, PriceFont, priceColor,
                new Rectangle(bounds.Left + 4, bounds.Top + 3, bounds.Width - 28, 18));
            if (group.Quantity > 1)
                DrawRight(g, $"×{group.Quantity}", BreedFont, Color.FromArgb(49, 91, 174),
                    new Rectangle(bounds.Right - 36, bounds.Top + 3, 30, 18));

            DrawCentered(g, $"{group.BreedCount}/{group.BreedMax}", BreedFont, Color.FromArgb(52, 68, 87),
                new Rectangle(bounds.Left + 3, bounds.Top + 23, bounds.Width - 6, 16));

            var icon = _getIcon?.Invoke(fish);
            if (icon != null)
            {
                var iconBox = new Rectangle(bounds.Left + 5, bounds.Top + 41, bounds.Width - 10, 64);
                var fit = Math.Min(iconBox.Width / (float)icon.Width, iconBox.Height / (float)icon.Height);
                // 放得下时用整数倍放大鱼图；大鱼则按最近邻等比缩小到图框内。
                var scale = fit >= 1F ? Math.Max(1, (int)Math.Floor(fit)) : fit;
                var drawW = Math.Max(1, (int)Math.Round(icon.Width * scale));
                var drawH = Math.Max(1, (int)Math.Round(icon.Height * scale));
                var imageRect = new Rectangle(iconBox.Left + (iconBox.Width - drawW) / 2,
                    iconBox.Top + (iconBox.Height - drawH) / 2, drawW, drawH);
                g.DrawImage(icon, imageRect, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel);
            }

            DrawCentered(g, _getGrade?.Invoke(fish.RarityTier) ?? "", GradeFont, ReadableQuality(quality),
                new Rectangle(bounds.Left + 3, bounds.Top + 103, bounds.Width - 6, 18));

            var stars = fish.Stars is >= 1 and <= 5 ? new string('★', fish.Stars) : "";
            DrawCentered(g, stars, StarFont, Gold,
                new Rectangle(bounds.Left + 3, bounds.Top + 121, bounds.Width - 6, 17));

            var name = fish.Name ?? "";
            DrawCentered(g, name, NameFont, Color.FromArgb(37, 51, 69),
                new Rectangle(bounds.Left + 2, bounds.Top + 140, bounds.Width - 4, 24));
        }

        private static readonly StringFormat CenterFormat = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        private static readonly StringFormat RightFormat = new()
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        private static void DrawRight(Graphics g, string text, Font font, Color color, Rectangle rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, rect, RightFormat);
        }

        private static Color ReadableQuality(Color color)
            => Color.FromArgb((int)(color.R * 0.72), (int)(color.G * 0.72), (int)(color.B * 0.72));

        private static void DrawCentered(Graphics g, string text, Font font, Color color, Rectangle rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            using var brush = new SolidBrush(color);
            // Graphics.DrawString follows the same scroll transform as card images/borders.
            // TextRenderer.DrawText ignores that transform, which made scrolled cards show only fish art.
            g.DrawString(text, font, brush, rect, CenterFormat);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var worldX = e.X - AutoScrollPosition.X;
            var worldY = e.Y - AutoScrollPosition.Y;
            var col = Math.Max(0, (worldX - HorizontalPadding) / (_cardWidth + CardGap));
            var row = Math.Max(0, (worldY - VerticalPadding) / (CardHeight + CardGap));
            var index = row * _columns + col;
            var cardX = HorizontalPadding + col * (_cardWidth + CardGap);
            var cardY = VerticalPadding + row * (CardHeight + CardGap);
            var insideCard = worldX >= cardX && worldX < cardX + _cardWidth &&
                             worldY >= cardY && worldY < cardY + CardHeight;
            var next = insideCard && index >= 0 && index < _items.Count ? index : -1;
            if (next != _hoveredIndex)
            {
                _hoveredIndex = next;
                if (next >= 0) _tip.SetToolTip(this, BuildTip(_items[next]));
                else _tip.SetToolTip(this, "");
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoveredIndex = -1;
            _tip.SetToolTip(this, "");
            Invalidate();
        }

        private string BuildTip(WarehouseGroup group)
        {
            var fish = group.Representative;
            var quote = _getQuote?.Invoke(fish);
            var tip = $"{fish.Name}  ×{group.Quantity}\n{fish.sp}\n" +
                      $"{UiLanguage.T("剩余繁育")}：{group.BreedCount} / {group.BreedMax}\n" +
                      $"{UiLanguage.T("冷却 CD")}：{UiLanguage.T(_getCooldown?.Invoke(fish) ?? "")}\n" +
                      $"{UiLanguage.T("状态")}：{UiLanguage.T(_getStatus?.Invoke(fish) ?? "")}";
            if (!string.IsNullOrWhiteSpace(fish.id))
            {
                tip += "\n" + string.Join("\n", group.Fish.Select(f =>
                    $"ID: {f.id} — {f.bc}/{f.bm}, {UiLanguage.T(CooldownText(f))}"));
            }
            if (quote != null)
                tip += $"\nSteam：{quote.HashName}\n最低在售 {quote.PriceText}　在售 {quote.Listed} 件";
            return tip;
        }
    }

    /// <summary>仓库某条鱼的图标，按 sp 缓存。取不到返回 null。</summary>
    private Image FishIcon(FishDto fish)
    {
        if (fish?.sp == null) return null;
        if (_fishIconCache.TryGetValue(fish.sp, out var cached)) return cached;
        var icon = FishIconPack.Get(fish.sp);
        if (icon == null) return null;
        _fishIconCache[fish.sp] = icon;
        return icon;
    }

    /// <summary>仓库某条鱼的 Steam 市场报价。取不到就返回 null。</summary>
    private MarketQuote MarketQuoteFor(FishDto fish)
    {
        if (fish == null) return null;
        var english = FishCatalog.EnglishName(fish.ty);
        if (string.IsNullOrWhiteSpace(english)) return null;
        return SteamMarket.Cached?.Find(english, fish.Stars);
    }

    private string MarketPriceText(FishDto fish)
    {
        var quote = MarketQuoteFor(fish);
        if (quote != null) return UiLanguage.T("¥") + quote.Lowest.ToString("0.00");
        return SteamMarket.Cached == null ? "—" : UiLanguage.T("无报价");
    }

    private Color MarketPriceColor(FishDto fish)
        => MarketQuoteFor(fish) != null ? CraftableText : Muted;

    /// <summary>拉取 Steam 市场报价，拉完刷新仓库表和状态行。</summary>
    private async Task RefreshMarketAsync()
    {
        if (_marketBusy) return;
        _marketBusy = true;
        _btnMarket.Enabled = false;
        _lblMarketStatus.ForeColor = Muted;
        _lblMarketStatus.Text = UiLanguage.T("正在查询 Steam 市场报价…");
        try
        {
            var result = await SteamMarket.FetchAsync(SteamMarket.CurrencyCny,
                text => Ui(() => _lblMarketStatus.Text = UiLanguage.T(text)));
            _lblMarketStatus.ForeColor = result.Ok ? Green : Pink;
            _lblMarketStatus.Text = UiLanguage.T(result.Message);
            _log.Write("Steam 市场：" + result.Message);
            RefreshWarehouse();
            if (result.Ok && _lastFish != null)
            {
                var matched = _lastFish.Count(f => MarketQuoteFor(f) != null);
                _log.Write($"Steam 市场：{matched}/{_lastFish.Count} 条鱼匹配到报价。");
            }
        }
        catch (Exception ex)
        {
            _lblMarketStatus.ForeColor = Pink;
            _lblMarketStatus.Text = UiLanguage.T("查询失败：") + ex.Message;
            _log.Write("Steam 市场查询异常：" + ex.Message);
        }
        finally
        {
            _marketBusy = false;
            _btnMarket.Enabled = true;
        }
    }

    /// <summary>启动时如果已有缓存，把状态行补上，仓库表也能立刻显示价格。</summary>
    private void UpdateMarketStatusLine()
    {
        if (_lblMarketStatus.IsDisposed || _marketBusy) return;
        var cache = SteamMarket.Cached;
        _lblMarketStatus.ForeColor = Muted;
        _lblMarketStatus.Text = cache == null
            ? UiLanguage.T("未查询市场价：点「查询市场价」获取 Steam 参考价。")
            : UiLanguage.T($"市场价：{cache.TotalItems} 条报价，更新于 {cache.FetchedAt:HH:mm}。");
    }

    private void RefreshWarehouse()
    {
        var source = _lastFish ?? new List<FishDto>();
        IEnumerable<FishDto> query = source;
        var search = _warehouseSearch.Text.Trim();
        if (search.Length > 0) query = query.Where(f =>
            (f.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (f.sp?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (f.ty?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (f.id?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        query = _warehouseFilter.SelectedIndex switch
        {
            1 => query.Where(f => Selection.CanBreed(f) && Selection.IsOffCooldown(f)),
            2 => query.Where(f => f.bc > 0 && Selection.CooldownRemaining(f) is TimeSpan cd && cd > TimeSpan.Zero),
            3 => query.Where(f => f.bc <= 0),
            4 => query.Where(f => f.lk || f.pl),
            _ => query
        };
        query = ApplyWarehouseSort(query);
        var shownFish = query.ToList();
        var shown = shownFish
            .GroupBy(f => $"{f.Name}|{f.Stars}", StringComparer.OrdinalIgnoreCase)
            .Select(g => new WarehouseGroup(g))
            .ToList();

        // 虚拟绘制：只保存鱼数据，不为每条鱼创建 WinForms 控件。
        // 仓库有近千条鱼时，页面依然只有一个绘图控件。
        _warehouseGrid.SetItems(shown);

        _lblWarehouseCount.Text = UiLanguage.T(_lastFish == null
            ? "等待游戏数据…"
            : $"显示 {shown.Count} 种 / {shownFish.Count} 条；仓库 {source.Count} 条");
        UpdateDashboardSummary();
    }

    private void UpdateWarehouseCountdowns()
    {
        if (_activePage != "warehouse" || (DateTime.Now - _warehouseCdUpdateAt).TotalSeconds < 1) return;
        _warehouseCdUpdateAt = DateTime.Now;
        // 卡片上的冷却和状态来自模型；需要刷新时只重画可见区域。
        _warehouseGrid.Invalidate();
    }

    /// <summary>排序。表格没了以后就以「排序」下拉为准，不再有列头点击。</summary>
    private IEnumerable<FishDto> ApplyWarehouseSort(IEnumerable<FishDto> source)
    {
        return _warehouseSort.SelectedIndex switch
        {
            1 => source.OrderByDescending(f => f.Stars).ThenByDescending(f => f.RarityTier).ThenBy(f => f.sp),
            2 => source.OrderByDescending(f => f.bc).ThenByDescending(f => f.RarityTier).ThenBy(f => f.sp),
            3 => source.OrderBy(f => Selection.CooldownRemaining(f) ?? TimeSpan.MaxValue).ThenBy(f => f.sp),
            4 => source.OrderByDescending(f => MarketQuoteFor(f)?.Lowest ?? 0m).ThenByDescending(f => f.RarityTier).ThenBy(f => f.sp),
            5 => source.OrderBy(f => MarketQuoteFor(f)?.Lowest ?? decimal.MaxValue).ThenBy(f => f.sp),
            _ => source.OrderByDescending(f => f.RarityTier).ThenByDescending(f => f.Stars).ThenBy(f => f.sp)
        };
    }

    private static string GradeName(int grade) => UiLanguage.T(grade switch
    {
        0 => "基础", 1 => "普通", 2 => "高级", 3 => "稀有", 4 => "传说", 5 => "神话", _ => $"T{grade}"
    });
    private static Color GradeColor(int grade) => FishQualityPalette.ForTier(grade);
    private static string StarText(int stars)
        => stars is >= 1 and <= 5 ? new string('★', stars) + new string('☆', 5 - stars) : "—";
    private static string CooldownText(FishDto f)
    {
        var left = Selection.CooldownRemaining(f);
        if (!left.HasValue) return "未知";
        if (left.Value <= TimeSpan.Zero) return "就绪";
        if (UiLanguage.IsEnglish)
            return left.Value.TotalHours >= 1
                ? $"{(int)left.Value.TotalHours}h {left.Value.Minutes:00}m"
                : $"{Math.Max(0, left.Value.Minutes):00}m {Math.Max(0, left.Value.Seconds):00}s";
        return left.Value.TotalHours >= 1
            ? $"{(int)left.Value.TotalHours}时{left.Value.Minutes:00}分"
            : $"{Math.Max(0, left.Value.Minutes):00}分{Math.Max(0, left.Value.Seconds):00}秒";
    }
    private static string FishStatus(FishDto f)
    {
        if (f.serverBc == -2) return "服务器未找到";
        if (f.serverBc == 0 && f.bc > 0) return "服务器次数用尽";
        if (f.lk) return "已锁定";
        if (f.pl) return "展示中";
        if (f.bc <= 0) return "次数用尽";
        return Selection.CooldownRemaining(f) switch
        {
            null => "冷却未知",
            TimeSpan cd when cd > TimeSpan.Zero => "冷却中",
            _ => "可繁育"
        };
    }
    private static Color StatusColor(FishDto f) => FishStatus(f) switch
    {
        "可繁育" => Green, "冷却中" => Gold, _ => Muted
    };
}
