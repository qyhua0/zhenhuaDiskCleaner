using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZhenhuaDiskCleaner.CleanerModule.Models
{
    /// <summary>
    /// 一条扫描结果，对应 TreeView 中的一个一级节点（垃圾分类）
    /// </summary>
    public class ScanResult : INotifyPropertyChanged
    {
        private bool _isChecked;
        private bool _isExpanded;

        // ── 基本信息 ──────────────────────────────────────────

        /// <summary>分类名称，来自规则 Name 字段</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>规则图标</summary>
        public string Icon { get; set; } = "📁";

        /// <summary>规则描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>规则类型（recommend / professional）</summary>
        public string RuleType { get; set; } = "recommend";

        // ── 统计数据 ──────────────────────────────────────────

        /// <summary>该分类下所有文件的总大小（字节）</summary>
        public long TotalSize { get; set; }

        /// <summary>格式化后的大小字符串（如 "12.3 MB"）</summary>
        public string SizeText => FormatSize(TotalSize);

        /// <summary>该分类下收集到的文件路径列表</summary>
        public List<string> Files { get; set; } = new();

        /// <summary>文件条目列表，用于 TreeView 子节点绑定</summary>
        public List<FileEntry> FileEntries { get; set; } = new();

        // ── UI 状态 ──────────────────────────────────────────

        /// <summary>是否勾选（用于清理选择）</summary>
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(); }
        }

        /// <summary>TreeView 展开状态</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        /// <summary>显示在一级节点的标题：图标 + 名称 + 大小</summary>
        public string DisplayTitle => $"{Icon}  {Name}  ({SizeText})";

        /// <summary>文件数量</summary>
        public int FileCount => Files.Count;

        // ── 工具方法 ──────────────────────────────────────────

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0)   return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F2} MB";
            return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 文件条目，对应 TreeView 中的二级节点（具体文件路径）
    /// </summary>
    public class FileEntry
    {
        /// <summary>完整文件路径</summary>
        public string FullPath { get; set; } = string.Empty;

        /// <summary>文件名（仅名称部分）</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>文件大小（字节）</summary>
        public long Size { get; set; }

        /// <summary>格式化的文件大小</summary>
        public string SizeText => ScanResult.FormatSize(Size);

        /// <summary>显示文本：文件名 + 大小</summary>
        public string DisplayText => $"{FileName}  ({SizeText})";

        /// <summary>父分类名称（用于 Tooltip）</summary>
        public string Category { get; set; } = string.Empty;
    }
}
