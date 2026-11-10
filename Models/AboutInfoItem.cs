namespace ZhenhuaDiskCleaner.Models
{
    /// <summary>
    /// 关于对话框里的一行信息（图标 / 名称 / 值）。
    /// 直接在 AboutWindow.xaml 里当 ItemsControl 的项写死，代码里不再手工搭 UI。
    /// 值以 http 开头或含 @ 时视为可点链接，点击由 AboutWindow 交给 shell 打开。
    /// </summary>
    public sealed class AboutInfoItem
    {
        public string Icon { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;

        /// <summary>是否为网址（否则含 @ 时按 mailto 处理）</summary>
        public bool IsUrl => Value.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        /// <summary>是否可点击（决定高亮、下划线和手型光标）</summary>
        public bool IsLink => IsUrl || Value.Contains('@');
    }
}
