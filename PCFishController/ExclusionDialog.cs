using System.Drawing.Drawing2D;

namespace PCFishController;

/// <summary>
/// 「自动合成排除鱼」弹窗。卡片样式与仓库页一致：价格、数量、鱼图、品质星级、鱼名，
/// 左上角是勾选框。勾上的鱼不会进入自动合成的候选池。
/// </summary>
internal sealed class ExclusionDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(244, 247, 251);
    private static readonly Color Card = Color.White;
    private static readonly Color Line = Color.FromArgb(222, 230, 239);
    private static readonly Color Ink = Color.FromArgb(26, 38, 56);
    private static readonly Color Muted = Color.FromArgb(98, 115, 137);
    private static readonly Color Blue = Color.FromArgb(49, 91, 174);

    private readonly List<ExclusionItem> _items;
    private readonly HashSet<string> _selected;
    private readonly ExclusionGrid _grid = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _rarity = new();
    private readonly Label _status = new();

    internal ExclusionDialog(List<ExclusionItem> items, IReadOnlySet<string> selected,
        string formTitle = "自动合成排除鱼",
        string hintText = "按鱼种排除，勾选后该鱼种的所有星级都不会被选为合成材料。赛季排除在自动合成页单独设置。")
    {
        _items = (items ?? new List<ExclusionItem>())
            .OrderByDescending(item => item.Rarity)
            .ThenByDescending(item => item.Stars)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .ToList();
        _selected = new HashSet<string>(selected ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);

        Text = UiLanguage.T(formTitle);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(920, 700);
        MinimumSize = new Size(780, 560);
        BackColor = Bg;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9F);
        try { Icon = Icon; } catch { }

        var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Bg };
        var title = new Label
        {
            Text = UiLanguage.T("勾选要排除的鱼种"),
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            ForeColor = Ink, AutoSize = false, Bounds = new Rectangle(20, 14, 520, 32)
        };
        var hint = new Label
        {
            Text = UiLanguage.T(hintText),
            ForeColor = Muted, AutoSize = false, Bounds = new Rectangle(22, 48, 820, 24)
        };
        _search.Bounds = new Rectangle(22, 78, 240, 30);
        _search.PlaceholderText = UiLanguage.T("搜索鱼名或编号");
        _search.BackColor = Color.FromArgb(241, 245, 249);
        _search.ForeColor = Ink;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.TextChanged += (_, _) => Reload();
        _rarity.Bounds = new Rectangle(276, 78, 160, 30);
        _rarity.DropDownStyle = ComboBoxStyle.DropDownList;
        _rarity.FlatStyle = FlatStyle.Flat;
        _rarity.BackColor = Color.FromArgb(241, 245, 249);
        _rarity.ForeColor = Ink;
        _rarity.Items.AddRange(new object[] { "全部品质", "基础", "普通", "高级", "稀有", "传说", "神话" });
        _rarity.SelectedIndex = 0;
        _rarity.SelectedIndexChanged += (_, _) => Reload();
        _status.Bounds = new Rectangle(450, 80, 440, 26);
        _status.ForeColor = Muted;
        _status.TextAlign = ContentAlignment.MiddleRight;
        header.Controls.AddRange(new Control[] { title, hint, _search, _rarity, _status });

        _grid.Dock = DockStyle.Fill;
        _grid.BackColor = Bg;
        _grid.Toggle += Toggle;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Bg, Padding = new Padding(20, 12, 20, 10) };
        var clear = Button("清空勾选");
        clear.SetBounds(20, 12, 104, 36);
        clear.Click += (_, _) =>
        {
            _selected.Clear();
            Reload();
        };
        var selectAll = Button("勾选当前筛选");
        selectAll.SetBounds(132, 12, 132, 36);
        selectAll.Click += (_, _) =>
        {
            foreach (var item in Filtered()) _selected.Add(item.Type);
            Reload();
        };
        var cancel = Button("取消");
        cancel.SetBounds(640, 12, 104, 36);
        cancel.DialogResult = DialogResult.Cancel;
        var apply = Button("保存", Blue);
        apply.ForeColor = Color.White;
        apply.SetBounds(756, 12, 124, 36);
        apply.DialogResult = DialogResult.OK;
        footer.Controls.AddRange(new Control[] { clear, selectAll, cancel, apply });
        CancelButton = cancel;
        AcceptButton = apply;

        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(header);
        Reload();
    }

    internal IReadOnlyCollection<string> SelectedTypes => _selected;

    private static Button Button(string text, Color? back = null)
    {
        var button = new Button
        {
            Text = UiLanguage.T(text), FlatStyle = FlatStyle.Flat, Height = 36,
            BackColor = back ?? Color.FromArgb(232, 238, 247),
            ForeColor = back == Blue ? Color.White : Ink,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void Toggle(string type)
    {
        if (!_selected.Remove(type)) _selected.Add(type);
        Reload();
    }

    private IEnumerable<ExclusionItem> Filtered()
    {
        var search = _search.Text.Trim();
        var rarity = _rarity.SelectedIndex - 1;
        return _items.Where(item =>
            (search.Length == 0 ||
             (item.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
             item.Type.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (rarity < 0 || item.Rarity == rarity));
    }

    private void Reload()
    {
        var list = Filtered().ToList();
        foreach (var item in list) item.Checked = _selected.Contains(item.Type);
        _grid.SetItems(list);
        _status.Text = UiLanguage.T($"已勾选 {_selected.Count} 种　当前显示 {list.Count} 种");
    }

    internal sealed class ExclusionItem
    {
        internal string Type = "";
        internal string Name = "";
        internal int Rarity;
        internal int Stars;
        internal int Count;
        internal Image Icon;
        internal string Price = "";
        internal bool Checked;
    }

    /// <summary>与仓库页同一套卡片布局，只多了左上角勾选框。</summary>
    private sealed class ExclusionGrid : ScrollableControl
    {
        private const int CardHeight = 168;
        private const int CardGap = 10;
        private const int MinCardWidth = 132;
        private List<ExclusionItem> _items = new();
        private int _columns = 1, _cardWidth = 150;

        internal event Action<string> Toggle;

        internal ExclusionGrid()
        {
            DoubleBuffered = true;
            AutoScroll = true;
            Padding = new Padding(12, 8, 10, 8);
            Resize += (_, _) => Relayout();
            MouseClick += (_, e) => ClickAt(e.Location);
        }

        internal void SetItems(List<ExclusionItem> items)
        {
            _items = items ?? new List<ExclusionItem>();
            Relayout();
            Invalidate();
        }

        private void Relayout()
        {
            var usable = Math.Max(MinCardWidth, ClientSize.Width - Padding.Horizontal - 20);
            _columns = Math.Max(1, (usable + CardGap) / (MinCardWidth + CardGap));
            _cardWidth = Math.Max(MinCardWidth, (usable - (_columns - 1) * CardGap) / _columns);
            var rows = (_items.Count + _columns - 1) / _columns;
            AutoScrollMinSize = new Size(0, Padding.Vertical + rows * (CardHeight + CardGap));
        }

        private Rectangle CardRect(int index)
        {
            var column = index % _columns;
            var row = index / _columns;
            return new Rectangle(Padding.Left + column * (_cardWidth + CardGap) - AutoScrollPosition.X,
                Padding.Top + row * (CardHeight + CardGap) - AutoScrollPosition.Y, _cardWidth, CardHeight);
        }

        private void ClickAt(Point location)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var rect = CardRect(i);
                if (!rect.Contains(location)) continue;
                Toggle?.Invoke(_items[i].Type);
                return;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            for (var i = 0; i < _items.Count; i++) DrawCard(e.Graphics, _items[i], CardRect(i));
        }

        private void DrawCard(Graphics g, ExclusionItem item, Rectangle rect)
        {
            if (!rect.IntersectsWith(ClientRectangle)) return;
            using var fill = new SolidBrush(item.Checked ? Color.FromArgb(255, 246, 246) : Card);
            using var border = new Pen(item.Checked ? Color.FromArgb(184, 71, 100) : Line, item.Checked ? 2 : 1);
            g.FillRectangle(fill, rect);
            g.DrawRectangle(border, rect);

            var box = new Rectangle(rect.Left + 10, rect.Top + 10, 18, 18);
            g.FillRectangle(Brushes.White, box);
            g.DrawRectangle(border, box);
            if (item.Checked)
            {
                using var pen = new Pen(Color.FromArgb(184, 71, 100), 2);
                g.DrawLine(pen, box.Left + 3, box.Top + 9, box.Left + 7, box.Top + 14);
                g.DrawLine(pen, box.Left + 7, box.Top + 14, box.Left + 15, box.Top + 4);
            }

            using var priceFont = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold);
            var price = item.Price.Length == 0 ? "—" : item.Price;
            TextRenderer.DrawText(g, price, priceFont, new Rectangle(rect.Left + 34, rect.Top + 8, rect.Width - 44, 20),
                Color.FromArgb(28, 126, 86), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var icon = item.Icon;
            if (icon != null)
            {
                var size = 52;
                var x = rect.Left + (rect.Width - size) / 2;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(icon, new Rectangle(x, rect.Top + 36, size, size));
            }

            using var nameFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            var nameRect = new Rectangle(rect.Left + 8, rect.Top + 94, rect.Width - 16, 22);
            TextRenderer.DrawText(g, item.Name, nameFont, nameRect, Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using var small = new Font("Microsoft YaHei UI", 8F);
            TextRenderer.DrawText(g, FishCatalog.RarityName(item.Rarity) + " · " + new string('★', Math.Clamp(item.Stars, 0, 5)),
                small, new Rectangle(rect.Left + 8, rect.Top + 118, rect.Width - 16, 20), Color.FromArgb(190, 119, 20),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, UiLanguage.T($"仓库 {item.Count} 条"), small,
                new Rectangle(rect.Left + 8, rect.Top + 140, rect.Width - 16, 20), Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
