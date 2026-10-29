namespace PCFishController;

/// <summary>按当前 Steam 市场价筛选鱼，用户明确勾选并二次确认后才发送导出请求。</summary>
internal sealed class SteamExportDialog : Form
{
    private readonly List<Row> _all;
    private readonly DataGridView _grid = new();
    private readonly NumericUpDown _minimum = new();
    private readonly NumericUpDown _maximum = new();
    private readonly Label _summary = new();
    private bool _refreshing;

    internal IReadOnlyList<string> SelectedIds => _grid.Rows.Cast<DataGridViewRow>()
        .Where(r => r.Cells["pick"].Value is true)
        .Select(r => (r.Tag as Row)?.Fish.id)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    internal SteamExportDialog(IEnumerable<(FishDto Fish, MarketQuote Quote, Image Icon)> rows)
    {
        _all = (rows ?? Enumerable.Empty<(FishDto, MarketQuote, Image)>())
            .Where(x => x.Fish != null && x.Quote != null)
            .Select(x => new Row(x.Fish, x.Quote, x.Icon))
            .ToList();

        Text = UiLanguage.T("导出鱼到 Steam Inventory");
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(980, 720);
        MinimumSize = new Size(820, 560);
        BackColor = Color.FromArgb(244, 247, 251);
        ForeColor = Color.FromArgb(26, 38, 56);
        Font = new Font("Microsoft YaHei UI", 9F);

        var header = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = BackColor };
        var title = new Label
        {
            Text = UiLanguage.T("选择要导出至 Steam Inventory 的鱼"),
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            Bounds = new Rectangle(20, 12, 680, 30)
        };
        var note = new Label
        {
            Text = UiLanguage.T("价格使用 Steam 近期成交中位价；无成交记录时回退到最低在售价。导出会把鱼从游戏仓库移出。"),
            ForeColor = Color.FromArgb(98, 115, 137),
            Bounds = new Rectangle(22, 44, 900, 22)
        };
        var minLabel = new Label { Text = UiLanguage.T("最低 ¥"), Bounds = new Rectangle(22, 76, 52, 24) };
        _minimum.Bounds = new Rectangle(76, 73, 110, 28);
        _minimum.Minimum = 0;
        _minimum.Maximum = 100000;
        _minimum.DecimalPlaces = 2;
        _minimum.Increment = 0.01m;
        _minimum.Value = 0;
        var maxLabel = new Label { Text = UiLanguage.T("最高 ¥"), Bounds = new Rectangle(200, 76, 52, 24) };
        _maximum.Bounds = new Rectangle(254, 73, 110, 28);
        _maximum.Minimum = 0;
        _maximum.Maximum = 100000;
        _maximum.DecimalPlaces = 2;
        _maximum.Increment = 0.01m;
        _maximum.Value = 100000;
        _summary.Bounds = new Rectangle(390, 73, 560, 26);
        _summary.ForeColor = Color.FromArgb(98, 115, 137);
        _summary.TextAlign = ContentAlignment.MiddleRight;
        _minimum.ValueChanged += (_, _) => Reload();
        _maximum.ValueChanged += (_, _) => Reload();
        header.Controls.AddRange(new Control[] { title, note, minLabel, _minimum, maxLabel, _maximum, _summary });

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AutoGenerateColumns = false;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 238, 247);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(26, 38, 56);
        _grid.DefaultCellStyle.BackColor = Color.White;
        _grid.DefaultCellStyle.ForeColor = Color.FromArgb(26, 38, 56);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(218, 232, 252);
        _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(26, 38, 56);
        _grid.RowTemplate.Height = 44;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "pick", HeaderText = UiLanguage.T("导出"), Width = 56, ReadOnly = false });
        _grid.Columns.Add(new DataGridViewImageColumn { Name = "icon", HeaderText = "", ImageLayout = DataGridViewImageCellLayout.Zoom, Width = 54, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "fish", HeaderText = UiLanguage.T("鱼种"), Width = 250, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rarity", HeaderText = UiLanguage.T("品质"), Width = 90, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "stars", HeaderText = UiLanguage.T("星级"), Width = 100, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "price", HeaderText = UiLanguage.T("Steam 参考价"), Width = 120, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "id", HeaderText = "ID", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = BackColor };
        var selectAll = new Button { Text = UiLanguage.T("全选当前结果"), Bounds = new Rectangle(20, 12, 132, 36) };
        selectAll.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in _grid.Rows) row.Cells["pick"].Value = true;
            UpdateSummary();
        };
        var clear = new Button { Text = UiLanguage.T("清空"), Bounds = new Rectangle(160, 12, 88, 36) };
        clear.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in _grid.Rows) row.Cells["pick"].Value = false;
            UpdateSummary();
        };
        var cancel = new Button { Text = UiLanguage.T("取消"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(746, 12, 94, 36) };
        var export = new Button { Text = UiLanguage.T("导出选中鱼"), DialogResult = DialogResult.OK, Bounds = new Rectangle(852, 12, 110, 36) };
        export.BackColor = Color.FromArgb(49, 91, 174);
        export.ForeColor = Color.White;
        export.FlatStyle = FlatStyle.Flat;
        export.FlatAppearance.BorderSize = 0;
        AcceptButton = export;
        CancelButton = cancel;
        footer.Controls.AddRange(new Control[] { selectAll, clear, cancel, export });
        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(header);
        Reload();
    }

    private void Reload()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _grid.Rows.Clear();
            var filtered = _all.Where(x => x.Price >= _minimum.Value && x.Price <= _maximum.Value).ToList();
            foreach (var item in filtered)
            {
                var index = _grid.Rows.Add(false, item.Icon, item.Fish.Name,
                    FishCatalog.RarityName(item.Fish.RarityTier), new string('★', Math.Clamp(item.Fish.Stars, 0, 5)),
                    "¥" + item.Price.ToString("0.00"), item.Fish.id);
                _grid.Rows[index].Tag = item;
            }
            UpdateSummary();
        }
        finally { _refreshing = false; }
    }

    private void UpdateSummary()
    {
        var checkedCount = _grid.Rows.Cast<DataGridViewRow>().Count(r => r.Cells["pick"].Value is true);
        _summary.Text = UiLanguage.T($"价格区间匹配 {_grid.Rows.Count} 条，已选 {checkedCount} 条。");
    }

    private sealed record Row(FishDto Fish, MarketQuote Quote, Image Icon)
    {
        internal decimal Price => Quote.MergeProtectionPrice;
    }
}
