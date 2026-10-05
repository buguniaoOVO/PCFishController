using System.Drawing.Imaging;
using System.Text.Json;

namespace PCFishController;

/// <summary>
/// 鱼图。图来自游戏自带的 AtlasFish 图集，构建时把整张图集和每个精灵的矩形
/// 一起放到助手目录：fish-icons.png 与 fish-icons.json。
/// 运行时按鱼的 sp（形如 FS00024_03_02_03）裁出那一块，不缩放、不补边。
///
/// 找不到文件时整个功能降级为「不显示图」，不影响其它功能。
/// </summary>
internal static class FishIconPack
{
    private const string SheetFile = "fish-icons.png";
    private const string IndexFile = "fish-icons.json";
    /// <summary>图标在表格里的最大边长。行高 40 留了上下各 3 像素。</summary>
    private const int MaxBox = 34;

    private static bool _loaded;
    private static Bitmap _sheet;
    private static Dictionary<string, RectangleF> _rects;
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>图集是否可用。第一次访问时读盘。</summary>
    internal static bool Available
    {
        get
        {
            Load();
            return _sheet != null && _rects != null && _rects.Count > 0;
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var dir = AppContext.BaseDirectory;
            var sheetPath = Path.Combine(dir, SheetFile);
            var indexPath = Path.Combine(dir, IndexFile);
            if (!File.Exists(sheetPath) || !File.Exists(indexPath)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("rects", out var rects) || rects.ValueKind != JsonValueKind.Object) return;
            _rects = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in rects.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Array) continue;
                var values = entry.Value.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                if (values.Length < 4) continue;
                _rects[entry.Name] = new RectangleF(values[0], values[1], values[2], values[3]);
            }
            if (_rects.Count == 0) { _rects = null; return; }

            // 用 MemoryStream 读，避免长期占用文件句柄。
            _sheet = new Bitmap(new MemoryStream(File.ReadAllBytes(sheetPath)));
        }
        catch (Exception ex)
        {
            Program.Mark("鱼图集加载失败", ex);
            _sheet = null;
            _rects = null;
        }
    }

    /// <summary>按鱼的 sp 取那一块图。取不到返回 null。</summary>
    internal static Bitmap Get(string sp)
    {
        if (string.IsNullOrWhiteSpace(sp)) return null;
        Load();
        if (_sheet == null || _rects == null) return null;
        if (Cache.TryGetValue(sp, out var cached)) return cached;
        if (!_rects.TryGetValue(sp, out var rect)) return null;

        try
        {
            // 矩形来自浮点，取整到整像素。
            var x = (int)Math.Round(rect.X);
            var y = (int)Math.Round(rect.Y);
            var srcW = Math.Max(1, (int)Math.Round(rect.Width));
            var srcH = Math.Max(1, (int)Math.Round(rect.Height));
            x = Math.Max(0, Math.Min(x, _sheet.Width - 1));
            y = Math.Max(0, Math.Min(y, _sheet.Height - 1));
            srcW = Math.Min(srcW, _sheet.Width - x);
            srcH = Math.Min(srcH, _sheet.Height - y);
            if (srcW <= 0 || srcH <= 0) return null;

            // 大型鱼（神话/传说里的 64×64）放不进表格行，按 1/2、1/4… 整数倍缩到框内。
            // 用整数倍是为了保住像素画的方块边缘，不做任意比例插值。
            var outW = srcW;
            var outH = srcH;
            while (outW > MaxBox || outH > MaxBox)
            {
                if (outW / 2 < 1 || outH / 2 < 1) break;
                outW = Math.Max(1, outW / 2);
                outH = Math.Max(1, outH / 2);
            }

            var cell = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cell))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(_sheet, new Rectangle(0, 0, outW, outH),
                    new Rectangle(x, y, srcW, srcH), GraphicsUnit.Pixel);
            }
            Cache[sp] = cell;
            return cell;
        }
        catch
        {
            return null;
        }
    }
}
