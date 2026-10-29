namespace PCFishController;

/// <summary>计划市场报价刷新与自动导出；打开此窗口本身不会向 Steam 发送鱼。</summary>
internal sealed class MarketAutomationDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(244, 247, 251);
    private static readonly Color Ink = Color.FromArgb(26, 38, 56);
    private static readonly Color Muted = Color.FromArgb(98, 115, 137);
    private static readonly Color Blue = Color.FromArgb(49, 91, 174);

    private readonly CheckBox _autoRefresh = new();
    private readonly CheckBox _autoExport = new();
    private readonly NumericUpDown _interval = new();
    private readonly NumericUpDown _minimum = new();
    private readonly NumericUpDown _maximum = new();
    private readonly NumericUpDown _batch = new();
    private readonly CheckBox _excludeS1 = new();
    private readonly CheckBox _excludeS2 = new();
    private readonly CheckBox _excludeRecipeS1 = new();
    private readonly CheckBox _excludeRecipeS2 = new();
    private readonly Label _fishSummary = new();
    private readonly List<ExclusionDialog.ExclusionItem> _fishItems;
    private readonly HashSet<string> _excludedTypes;
    private readonly HashSet<int> _excludedSeasons;
    private readonly HashSet<int> _excludedRecipeSeasons;

    internal bool AutoRefresh => _autoRefresh.Checked;
    internal bool AutoExport => _autoExport.Checked;
    internal int RefreshIntervalMinutes => (int)_interval.Value;
    internal decimal MinimumPrice => _minimum.Value;
    internal decimal MaximumPrice => _maximum.Value;
    internal int BatchSize => (int)_batch.Value;
    internal IReadOnlyCollection<int> ExcludedSeasons => _excludedSeasons;
    internal IReadOnlyCollection<int> ExcludedRecipeSeasons => _excludedRecipeSeasons;
    internal IReadOnlyCollection<string> ExcludedTypes => _excludedTypes;

    internal MarketAutomationDialog(AppSettings settings, List<ExclusionDialog.ExclusionItem> fishItems)
    {
        settings ??= new AppSettings();
        _fishItems = fishItems ?? new List<ExclusionDialog.ExclusionItem>();
        _excludedTypes = new HashSet<string>(settings.SteamExportExcludedTypes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        _excludedSeasons = new HashSet<int>(settings.SteamExportExcludedSeasons ?? new List<int>());
        _excludedRecipeSeasons = new HashSet<int>(settings.SteamExportExcludedRecipeSeasons ?? new List<int>());

        Text = UiLanguage.T("自动报价与 Steam 导出设置");
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 550);
        MinimumSize = new Size(760, 520);
        BackColor = Bg;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = UiLanguage.T("市场报价刷新与自动导出"),
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            ForeColor = Ink,
            Bounds = new Rectangle(22, 16, 600, 30)
        };
        var note = new Label
        {
            Text = UiLanguage.T("报价仅在完整查询成功后替换本地缓存。自动导出关闭时，市场查询只更新报价。"),
            ForeColor = Muted,
            Bounds = new Rectangle(24, 48, 760, 24)
        };

        var refreshBox = new Panel { Bounds = new Rectangle(20, 82, 780, 82), BackColor = Color.White };
        _autoRefresh.Text = UiLanguage.T("定时自动查询 Steam 报价");
        _autoRefresh.Checked = settings.AutoMarketRefresh;
        _autoRefresh.SetBounds(16, 14, 245, 30);
        var intervalLabel = new Label { Text = UiLanguage.T("间隔（分钟）"), ForeColor = Muted, Bounds = new Rectangle(290, 18, 100, 24) };
        _interval.SetBounds(395, 14, 100, 30);
        _interval.Minimum = 15;
        _interval.Maximum = 1440;
        _interval.Increment = 15;
        _interval.Value = Math.Clamp(settings.MarketRefreshIntervalMinutes, 15, 1440);
        var refreshHint = new Label
        {
            Text = UiLanguage.T("每次查询成功后保存完整报价和成交中位价；失败时保留上次成功缓存。"),
            ForeColor = Muted,
            Bounds = new Rectangle(16, 48, 745, 22)
        };
        refreshBox.Controls.AddRange(new Control[] { _autoRefresh, intervalLabel, _interval, refreshHint });

        var exportBox = new Panel { Bounds = new Rectangle(20, 176, 780, 150), BackColor = Color.White };
        _autoExport.Text = UiLanguage.T("按计划自动导出符合条件的鱼");
        _autoExport.Checked = settings.AutoSteamExport;
        _autoExport.SetBounds(16, 12, 300, 30);
        _autoExport.CheckedChanged += (_, _) =>
        {
            if (_autoExport.Checked && !_autoRefresh.Checked) _autoRefresh.Checked = true;
        };
        _autoRefresh.CheckedChanged += (_, _) =>
        {
            if (!_autoRefresh.Checked && _autoExport.Checked) _autoExport.Checked = false;
        };
        var minLabel = new Label { Text = UiLanguage.T("价格下限 ¥"), ForeColor = Muted, Bounds = new Rectangle(20, 53, 85, 24) };
        _minimum.SetBounds(108, 50, 110, 30);
        _minimum.Minimum = 0;
        _minimum.Maximum = 100000;
        _minimum.DecimalPlaces = 2;
        _minimum.Increment = 0.01m;
        _minimum.Value = Math.Clamp(settings.AutoSteamExportMinPriceYuan, 0m, 100000m);
        var maxLabel = new Label { Text = UiLanguage.T("上限 ¥"), ForeColor = Muted, Bounds = new Rectangle(238, 53, 60, 24) };
        _maximum.SetBounds(300, 50, 110, 30);
        _maximum.Minimum = 0;
        _maximum.Maximum = 100000;
        _maximum.DecimalPlaces = 2;
        _maximum.Increment = 0.01m;
        _maximum.Value = Math.Clamp(settings.AutoSteamExportMaxPriceYuan, 0m, 100000m);
        _minimum.ValueChanged += (_, _) => { if (_maximum.Value < _minimum.Value) _maximum.Value = _minimum.Value; };
        _maximum.ValueChanged += (_, _) => { if (_minimum.Value > _maximum.Value) _minimum.Value = _maximum.Value; };
        var batchLabel = new Label { Text = UiLanguage.T("每批最多"), ForeColor = Muted, Bounds = new Rectangle(435, 53, 70, 24) };
        _batch.SetBounds(510, 50, 72, 30);
        _batch.Minimum = 1;
        _batch.Maximum = 100;
        _batch.Value = Math.Clamp(settings.AutoSteamExportBatchSize, 1, 100);
        var fishExclusion = Button("选择排除鱼种…");
        fishExclusion.SetBounds(600, 48, 150, 34);
        fishExclusion.Click += (_, _) => ConfigureExcludedFish();
        _fishSummary.SetBounds(18, 90, 730, 23);
        _fishSummary.ForeColor = Muted;
        var exportHint = new Label
        {
            Text = UiLanguage.T("自动导出使用仓库页选择的价格顺序；每批发送后等游戏确认，再继续下一轮。"),
            ForeColor = Muted,
            Bounds = new Rectangle(18, 118, 745, 22)
        };
        exportBox.Controls.AddRange(new Control[] { _autoExport, minLabel, _minimum, maxLabel, _maximum, batchLabel, _batch, fishExclusion, _fishSummary, exportHint });

        var seasonBox = new Panel { Bounds = new Rectangle(20, 338, 780, 114), BackColor = Color.White };
        var seasonTitle = new Label { Text = UiLanguage.T("导出排除规则"), Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), Bounds = new Rectangle(16, 10, 200, 26) };
        _excludeS1.Text = UiLanguage.T("排除 S1 赛季鱼");
        _excludeS1.Checked = _excludedSeasons.Contains(1);
        _excludeS1.SetBounds(18, 42, 150, 26);
        _excludeS2.Text = UiLanguage.T("排除 S2 赛季鱼");
        _excludeS2.Checked = _excludedSeasons.Contains(2);
        _excludeS2.SetBounds(185, 42, 150, 26);
        _excludeRecipeS1.Text = UiLanguage.T("排除 S1 配方鱼");
        _excludeRecipeS1.Checked = _excludedRecipeSeasons.Contains(1);
        _excludeRecipeS1.SetBounds(18, 76, 150, 26);
        _excludeRecipeS2.Text = UiLanguage.T("排除 S2 配方鱼");
        _excludeRecipeS2.Checked = _excludedRecipeSeasons.Contains(2);
        _excludeRecipeS2.SetBounds(185, 76, 150, 26);
        seasonBox.Controls.AddRange(new Control[] { seasonTitle, _excludeS1, _excludeS2, _excludeRecipeS1, _excludeRecipeS2 });

        var caution = new Label
        {
            Text = UiLanguage.T("启用自动导出后，助手会把符合价格范围且未排除的鱼移出游戏仓库，发往 Steam Inventory。S2 赛季期间默认排除 S2 赛季鱼。"),
            ForeColor = Color.FromArgb(159, 73, 47),
            Bounds = new Rectangle(24, 462, 765, 34)
        };
        var cancel = Button("取消");
        cancel.SetBounds(596, 504, 90, 34);
        cancel.DialogResult = DialogResult.Cancel;
        var save = Button("保存", Blue);
        save.ForeColor = Color.White;
        save.SetBounds(700, 504, 100, 34);
        save.DialogResult = DialogResult.OK;
        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { title, note, refreshBox, exportBox, seasonBox, caution, cancel, save });
        UpdateFishSummary();
    }

    private void ConfigureExcludedFish()
    {
        using var dialog = new ExclusionDialog(_fishItems, _excludedTypes,
            "自动导出排除鱼种",
            "勾选后，该鱼种的所有星级都不会自动导出。价格范围和赛季规则在上一页设置。");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _excludedTypes.Clear();
        foreach (var type in dialog.SelectedTypes) _excludedTypes.Add(type);
        UpdateFishSummary();
    }

    private void UpdateFishSummary()
    {
        _fishSummary.Text = UiLanguage.T($"已排除 {_excludedTypes.Count} 种鱼。价格排序沿用仓库页：价格降序选高价，价格升序选低价。");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            _excludedSeasons.Clear();
            _excludedRecipeSeasons.Clear();
            if (_excludeS1.Checked) _excludedSeasons.Add(1);
            if (_excludeS2.Checked) _excludedSeasons.Add(2);
            if (_excludeRecipeS1.Checked) _excludedRecipeSeasons.Add(1);
            if (_excludeRecipeS2.Checked) _excludedRecipeSeasons.Add(2);
        }
        base.OnFormClosing(e);
    }

    private static Button Button(string text, Color? back = null)
    {
        var button = new Button
        {
            Text = UiLanguage.T(text),
            FlatStyle = FlatStyle.Flat,
            BackColor = back ?? Color.FromArgb(232, 238, 247),
            ForeColor = back == Blue ? Color.White : Ink,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
