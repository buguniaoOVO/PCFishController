namespace PCFishController;

/// <summary>来自游戏 GameUtil.LevelToString 的 0–5 档品质颜色。</summary>
internal static class FishQualityPalette
{
    internal static readonly Color Background = Color.FromArgb(22, 46, 69);
    internal static readonly Color SelectedBackground = Color.FromArgb(36, 68, 96);
    private static readonly Color[] Colors =
    {
        ColorTranslator.FromHtml("#C3C3C3"), // 基础
        ColorTranslator.FromHtml("#AFD6FF"), // 普通
        ColorTranslator.FromHtml("#69D98A"), // 高级
        ColorTranslator.FromHtml("#E6C35A"), // 稀有
        ColorTranslator.FromHtml("#F08A3C"), // 传说
        ColorTranslator.FromHtml("#C76DFF")  // 神话
    };
    internal static Color ForTier(int tier) => Colors[Math.Clamp(tier, 0, 5)];
}
