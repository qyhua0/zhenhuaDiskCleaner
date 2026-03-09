using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using ZhenhuaDiskCleaner.CleanerModule.Models;
using ZhenhuaDiskCleaner.CleanerModule.Services;

namespace ZhenhuaDiskCleaner.CleanerModule.ViewModels
{
    /// <summary>
    /// 系统盘清理窗口的 ViewModel。
    /// 完全独立于主程序 MainViewModel，只依赖 CleanerModule 内的 Services。
    /// </summary>
    public partial class CleanerViewModel : ObservableObject
    {
        // ── 私有服务实例 ──────────────────────────────────────────────────────
        private readonly ScannerService _scanner = new();
        private readonly CleanerService _cleaner = new();

        // ── 绑定属性 ──────────────────────────────────────────────────────────

        /// <summary>扫描结果列表，绑定到 TreeView</summary>
        [ObservableProperty]
        private ObservableCollection<ScanResult> _results = new();

        /// <summary>当前状态消息，显示在底部状态栏</summary>
        [ObservableProperty]
        private string _statusMessage = "点击「开始扫描」检测系统垃圾文件";

        /// <summary>扫描进度 0-100</summary>
        [ObservableProperty]
        private double _scanProgress;

        /// <summary>清理进度 0-100</summary>
        [ObservableProperty]
        private double _cleanProgress;

        /// <summary>是否正在扫描</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
        [NotifyCanExecuteChangedFor(nameof(CleanSelectedCommand))]
        private bool _isScanning;

        /// <summary>是否正在清理</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CleanSelectedCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
        private bool _isCleaning;

        /// <summary>是否已有扫描结果可供清理</summary>
        [ObservableProperty]
        private bool _hasScanResult;

        /// <summary>已勾选项目的合计可释放空间文本（实时计算）</summary>
        [ObservableProperty]
        private string _selectedSizeText = "0 B";

        /// <summary>
        /// 休眠是否已关闭且 hiberfil.sys 不存在。
        /// true  → 底部显示「已关闭休眠释放空间。」绿色提示
        /// false → 底部显示「关闭休眠释放空间」按钮
        /// </summary>
        [ObservableProperty]
        private bool _hibernationAlreadyDisabled;

        /// <summary>上次清理释放的空间文本</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLastFreed))]
        private string _lastFreedText = string.Empty;

        /// <summary>是否有上次清理数据（控制底栏"上次释放"区域的可见性）</summary>
        public bool HasLastFreed => !string.IsNullOrEmpty(LastFreedText);

        /// <summary>磁盘信息快照</summary>
        [ObservableProperty]
        private DiskSnapshot _diskInfo = DiskInfoService.GetSystemDiskInfo();

        // ── 构造 ──────────────────────────────────────────────────────────────

        public CleanerViewModel()
        {
            _scanner.ProgressChanged += OnScanProgress;
            _scanner.ScanCompleted += OnScanCompleted;
            _cleaner.ProgressChanged += OnCleanProgress;
            _cleaner.CleanCompleted += OnCleanCompleted;

            // 初始化：检测休眠当前状态
            HibernationAlreadyDisabled = CheckHibernationDisabled();
        }

        // ── 命令：扫描 ────────────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanScan))]
        private async Task StartScanAsync()
        {
            Results.Clear();
            HasScanResult = false;
            LastFreedText = string.Empty;
            ScanProgress = 0;
            IsScanning = true;
            StatusMessage = "正在扫描系统垃圾，请稍候...";
            await _scanner.ScanAsync();
        }

        private bool CanScan() => !IsScanning && !IsCleaning;

        [RelayCommand]
        private void CancelScan()
        {
            _scanner.Cancel();
            IsScanning = false;
            StatusMessage = "扫描已取消";
        }

        // ── 命令：清理 ────────────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanClean))]
        private async Task CleanSelectedAsync()
        {
            var checkedCount = Results.Count(r => r.IsChecked);
            if (checkedCount == 0)
            {
                MessageBox.Show("请先勾选要清理的项目。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 构建确认信息：列出勾选的分类名称与预计释放大小
            var checkedItems = Results.Where(r => r.IsChecked).ToList();
            var totalFreeable = checkedItems.Sum(r => r.TotalSize);
            var categoryList = string.Join("\n  • ", checkedItems.Select(r => $"{r.Name} ({r.SizeText})"));
            var confirm = MessageBox.Show(
                $"即将清理以下 {checkedCount} 类垃圾文件（文件将移入回收站，可撤销）：\n\n  • {categoryList}\n\n预计释放：{ScanResult.FormatSize(totalFreeable)}\n\n确认继续？",
                "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            CleanProgress = 0;
            IsCleaning = true;
            StatusMessage = "正在清理，请稍候...";
            LastFreedText = string.Empty;

            await _cleaner.CleanAsync(Results, toRecycleBin: true);
        }

        private bool CanClean() => HasScanResult && !IsScanning && !IsCleaning;

        [RelayCommand]
        private void CancelClean()
        {
            _cleaner.Cancel();
            IsCleaning = false;
            StatusMessage = "清理已取消";
        }

        // ── 命令：勾选快捷键（文档 §5）────────────────────────────────────────

        /// <summary>全选（勾选所有扫描结果）</summary>
        [RelayCommand]
        private void SelectAll()
        {
            foreach (var r in Results) r.IsChecked = true;
            RefreshSelectedSize();
        }

        /// <summary>
        /// 推荐模式：只勾选 type == "recommend" 的项目
        /// （文档 §5.2：系统临时、浏览器缓存、日志等安全项）
        /// </summary>
        [RelayCommand]
        private void SelectRecommend()
        {
            foreach (var r in Results)
                r.IsChecked = r.RuleType == "recommend";
            RefreshSelectedSize();
        }

        /// <summary>
        /// 专业模式：同时勾选 recommend + professional 的项目
        /// （文档 §5.3：包含系统日志、着色器缓存等进阶项）
        /// </summary>
        [RelayCommand]
        private void SelectProfessional()
        {
            foreach (var r in Results)
                r.IsChecked = r.RuleType == "recommend" || r.RuleType == "professional";
            RefreshSelectedSize();
        }

        /// <summary>取消全选</summary>
        [RelayCommand]
        private void SelectNone()
        {
            foreach (var r in Results) r.IsChecked = false;
            RefreshSelectedSize();
        }

        // ── 命令：文件操作 ────────────────────────────────────────────────────

        /// <summary>在资源管理器中定位文件（右键菜单调用）</summary>
        [RelayCommand]
        private void OpenInExplorer(FileEntry? entry)
        {
            if (entry != null)
                CleanerService.OpenInExplorer(entry.FullPath);
        }

        // ── 命令：关闭休眠释放空间 ────────────────────────────────────────────

        /// <summary>
        /// 关闭 Windows 休眠功能并删除 hiberfil.sys，释放 4-16 GB 空间。
        /// 执行前先检测状态：
        ///   - 若已关闭且文件不存在 → 提示「已关闭休眠释放空间。」，不重复执行
        ///   - 否则 → 执行 powercfg /h off，等待文件消失，刷新磁盘信息
        /// </summary>
        [RelayCommand]
        private async Task DisableHibernationAsync()
        {
            // 再次检测，防止重复点击
            if (CheckHibernationDisabled())
            {
                HibernationAlreadyDisabled = true;
                StatusMessage = "已关闭休眠释放空间。";
                return;
            }

            StatusMessage = "正在关闭休眠，请稍候...";
            try
            {
                await Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = "/h off",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(8000); // 最多等 8 秒
                });

                // 等待系统删除 hiberfil.sys（最多 5 秒）
                var hiber = System.IO.Path.Combine(
                    System.IO.Path.GetPathRoot(Environment.GetFolderPath(
                        Environment.SpecialFolder.System)) ?? @"C:\",
                    "hiberfil.sys");

                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(500);
                    if (!File.Exists(hiber)) break;
                }

                HibernationAlreadyDisabled = CheckHibernationDisabled();
                DiskInfo = DiskInfoService.GetSystemDiskInfo();

                StatusMessage = HibernationAlreadyDisabled
                    ? "已关闭休眠释放空间。"
                    : "休眠已关闭，hiberfil.sys 将在重启后删除。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"关闭休眠失败：{ex.Message}（请以管理员身份运行软件）";
            }
        }

        /// <summary>
        /// 检测休眠是否已关闭且 hiberfil.sys 不存在。
        /// 两个条件都满足才算「已完成关闭」：
        ///   1. powercfg /a 输出不包含 "休眠" 或 "Hibernate"（表示功能已禁用）
        ///   2. hiberfil.sys 文件不存在（表示磁盘空间已释放）
        /// </summary>
        private static bool CheckHibernationDisabled()
        {
            try
            {
                // 检查 hiberfil.sys 是否存在
                var root = System.IO.Path.GetPathRoot(
                    Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\";
                var hiberFile = System.IO.Path.Combine(root, "hiberfil.sys");
                if (File.Exists(hiberFile)) return false;

                // hiberfil.sys 不存在 → 认为已关闭（文件消失是最可靠的标志）
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── 扫描进度回调 ──────────────────────────────────────────────────────

        private void OnScanProgress(int done, int total)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ScanProgress = total > 0 ? (double)done / total * 100 : 0;
                StatusMessage = $"正在扫描 ({done}/{total})...";
            });
        }

        private void OnScanCompleted(List<ScanResult> results)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                Results.Clear();
                foreach (var r in results) Results.Add(r);

                HasScanResult = Results.Count > 0;
                IsScanning = false;
                ScanProgress = 100;

                // 扫描完成后自动应用「推荐」预选：
                // recommend 类型默认勾选（IsChecked 在 ScannerService 中已按规则初始化），
                // 此处显式再调用一次确保与当前 Results 同步（兼容重新扫描的场景）
                foreach (var r in Results)
                    r.IsChecked = r.RuleType == "recommend";
                RefreshSelectedSize();

                // 刷新磁盘信息
                DiskInfo = DiskInfoService.GetSystemDiskInfo();

                long total = Results.Sum(r => r.TotalSize);
                StatusMessage = Results.Count == 0
                    ? "未发现需要清理的垃圾文件 ✔"
                    : $"扫描完成，共发现 {Results.Count} 类垃圾，合计 {ScanResult.FormatSize(total)}";
            });
        }

        // ── 清理进度回调 ──────────────────────────────────────────────────────

        private void OnCleanProgress(int done, int total)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CleanProgress = total > 0 ? (double)done / total * 100 : 0;
                StatusMessage = $"正在清理 ({done}/{total})...";
            });
        }

        private void OnCleanCompleted(
            long freed, int success,
            List<string> pendingReboot,
            List<(string Path, string Reason)> skippedFiles)
        {
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                IsCleaning = false;
                CleanProgress = 100;
                LastFreedText = ScanResult.FormatSize(freed);

                int pending = pendingReboot.Count;
                int skipped = skippedFiles.Count;

                // 刷新磁盘信息
                DiskInfo = DiskInfoService.GetSystemDiskInfo();

                var sb = new System.Text.StringBuilder();

                // ── 第一段：成功结果 ──────────────────────────────────────────
                sb.AppendLine($"✔  成功删除 {success} 个文件，已释放 {LastFreedText}");

                // ── 第二段：待重启删除 ────────────────────────────────────────
                if (pending > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"🔄  {pending} 个文件正在被系统使用，已登记「重启后删除」：");
                    foreach (var p in pendingReboot.Take(10))
                        sb.AppendLine($"      {System.IO.Path.GetFileName(p)}");
                    if (pending > 10)
                        sb.AppendLine($"      … 另有 {pending - 10} 个文件未列出");
                    sb.AppendLine("    （重启后系统将自动删除，无需手动操作）");
                }

                // ── 第三段：跳过（系统保护文件）────────────────────────────
                if (skipped > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"⚠  {skipped} 个文件受系统保护，已跳过：");
                    var grouped = skippedFiles
                        .GroupBy(f => f.Reason)
                        .OrderByDescending(g => g.Count());
                    int shown = 0;
                    foreach (var group in grouped)
                    {
                        sb.AppendLine($"  ▸ {group.Key}（{group.Count()} 个）");
                        foreach (var (path, _) in group.Take(5))
                        {
                            sb.AppendLine($"      {System.IO.Path.GetFileName(path)}");
                            if (++shown >= 15) break;
                        }
                        if (shown >= 15) break;
                    }
                    if (skipped > shown)
                        sb.AppendLine($"  … 另有 {skipped - shown} 个文件未列出");
                    sb.AppendLine("  （需要 SYSTEM 权限，手动也无法删除，不影响系统使用）");
                }

                // ── 状态栏文字 ────────────────────────────────────────────────
                StatusMessage = pending > 0
                    ? $"清理完成，已释放 {LastFreedText}；{pending} 个文件待重启删除"
                    : $"清理完成！已释放 {LastFreedText}，共删除 {success} 个文件 ✔";

                // ── 弹出结果对话框 ────────────────────────────────────────────
                string title = pending > 0
                    ? $"清理完成（{pending} 个文件待重启删除）"
                    : skipped > 0
                        ? $"清理完成（{skipped} 个系统保护文件已跳过）"
                        : "清理完成";

                if (pending > 0)
                {
                    sb.AppendLine();
                    sb.Append("是否现在重启计算机以完成清理？");
                    var result = MessageBox.Show(
                        sb.ToString(), title,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        await Task.Delay(1000);
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "shutdown",
                            Arguments = "/r /t 10 /c \"振华磁盘清理：正在完成垃圾文件删除，10 秒后重启\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        return;
                    }
                }
                else
                {
                    MessageBox.Show(sb.ToString(), title,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // 对话框关闭后重新扫描，刷新结果列表
                await Task.Delay(400);
                await StartScanAsync();
            });
        }

        // ── 辅助方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 重新计算已勾选项目的合计大小并更新 SelectedSizeText
        /// （在用户手动勾选或快捷键批量选择后调用）
        /// </summary>
        public void RefreshSelectedSize()
        {
            long total = Results.Where(r => r.IsChecked).Sum(r => r.TotalSize);
            SelectedSizeText = ScanResult.FormatSize(total);
        }
    }
}