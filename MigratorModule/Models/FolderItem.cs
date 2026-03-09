// ╔══════════════════════════════════════════════════════════════╗
// ║  MigratorModule — 个人资料迁移模块                           ║
// ║  与主程序及 CleanerModule 完全隔离，禁止跨模块引用            ║
// ╚══════════════════════════════════════════════════════════════╝

using CommunityToolkit.Mvvm.ComponentModel;

namespace ZhenhuaDiskCleaner.MigratorModule.Models
{
    /// <summary>
    /// 可迁移的用户文件夹（桌面/文档/下载等）数据模型。
    /// </summary>
    public partial class FolderItem : ObservableObject
    {
        // ── 静态信息（初始化后不变）──────────────────────────────────────────

        public string DisplayName    { get; init; } = "";
        public string Icon           { get; init; } = "📁";
        public string SourcePath     { get; init; } = "";
        public string ShellFolderKey { get; init; } = "";  // 注册表键名

        // ── 可观察属性 ────────────────────────────────────────────────────────

        [ObservableProperty] private bool   _isChecked;
        [ObservableProperty] private string _targetPath   = "";
        [ObservableProperty] private long   _sizeBytes;
        [ObservableProperty] private string _sizeText     = "计算中…";
        [ObservableProperty] private bool   _isSizeReady  = false;

        /// <summary>
        /// 路径有效性：true=有效(绿✓)  false=无效(红✗)  null=未验证
        /// </summary>
        [ObservableProperty] private bool?  _pathValid    = null;
        [ObservableProperty] private string _pathValidMsg = "";

        /// <summary>已迁移到 C 盘以外（不可再勾选，显示"已迁移"角标）</summary>
        [ObservableProperty] private bool   _alreadyMigrated = false;

        // ── 工具方法 ──────────────────────────────────────────────────────────

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0)            return "0 B";
            if (bytes < 1024)          return $"{bytes} B";
            if (bytes < 1048576)       return $"{bytes / 1024.0:F2} KB";
            if (bytes < 1073741824)    return $"{bytes / 1048576.0:F2} MB";
            return                            $"{bytes / 1073741824.0:F2} GB";
        }
    }
}
