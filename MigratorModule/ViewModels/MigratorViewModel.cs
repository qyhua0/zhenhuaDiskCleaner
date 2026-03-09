using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using ZhenhuaDiskCleaner.MigratorModule.Models;
using ZhenhuaDiskCleaner.MigratorModule.Services;

namespace ZhenhuaDiskCleaner.MigratorModule.ViewModels
{
    public partial class MigratorViewModel : ObservableObject
    {
        // ── 服务 ──────────────────────────────────────────────────────────────

        private readonly MigrationService _svc = new();
        private CancellationTokenSource? _scanCts;

        // ── 集合 ──────────────────────────────────────────────────────────────

        public ObservableCollection<FolderItem> Folders { get; } = new();
        public ObservableCollection<MigrationLogEntry> Logs { get; } = new();

        // ── 状态属性 ──────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanMigrate))]
        private bool _isMigrating;

        [ObservableProperty] private bool _isScanning = true;
        [ObservableProperty] private int _overallProgress;
        [ObservableProperty] private int _folderProgress;
        [ObservableProperty] private string _currentFolder = "";
        [ObservableProperty] private string _statusMessage = "正在扫描用户文件夹…";
        [ObservableProperty] private string _targetDrive = "D:";
        [ObservableProperty] private string _driveFreeText = "";

        public bool CanMigrate =>
            !IsMigrating && Folders.Any(f => f.IsChecked && !f.AlreadyMigrated);

        // ── 可用驱动器列表（供 ComboBox 绑定，已排除源盘）───────────────────

        public ObservableCollection<string> AvailableDrives { get; } = new();

        /// <summary>用户文件夹所在的源盘盘符（通常是 C:），不允许选作目标</summary>
        private string _sourceDrive = "C:";

        // ── 构造 ──────────────────────────────────────────────────────────────

        public MigratorViewModel()
        {
            // 挂载服务事件
            _svc.OverallProgressChanged += p => Dispatch(() => OverallProgress = p);
            _svc.FolderProgressChanged += p => Dispatch(() => FolderProgress = p);
            _svc.CurrentFolderChanged += n => Dispatch(() =>
            {
                CurrentFolder = n;
                StatusMessage = $"正在迁移：{n}…";
            });
            _svc.LogAdded += e => Dispatch(() => Logs.Add(e));
            _svc.Completed += rs => Dispatch(() => OnMigrationCompleted(rs));

            // 检测源盘（%USERPROFILE% 所在盘）
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _sourceDrive = (System.IO.Path.GetPathRoot(profile) ?? "C:\\").TrimEnd('\\');

            // 构建可用驱动器列表（排除源盘，防止用户误选）
            RebuildDriveList();

            _ = LoadAsync();
        }

        /// <summary>重建驱动器下拉列表，排除源盘，自动选中剩余中空间最大的盘</summary>
        private void RebuildDriveList()
        {
            AvailableDrives.Clear();

            var candidates = DriveInfo.GetDrives()
                .Where(d => d.IsReady &&
                            d.DriveType == DriveType.Fixed &&
                            !d.Name.TrimEnd('\\').Equals(
                                _sourceDrive, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name)
                .ToList();

            foreach (var d in candidates)
                AvailableDrives.Add(d.Name.TrimEnd('\\'));

            // 默认选可用空间最大的盘
            string best = candidates
                .OrderByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault()?.Name.TrimEnd('\\') ?? "";

            // 必须在集合填充完成后再设 TargetDrive，ComboBox SelectedItem 才能命中
            TargetDrive = AvailableDrives.Contains(best) ? best
                        : AvailableDrives.FirstOrDefault() ?? "";
        }

        // ── 命令 ──────────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task RefreshAsync()
        {
            _scanCts?.Cancel();
            await LoadAsync();
        }

        /// <summary>目标盘选择改变时刷新推荐路径和可用空间</summary>
        [RelayCommand]
        private void TargetDriveChanged()
        {
            ApplyDefaultTargetPaths();
            RefreshDriveFree();
        }

        /// <summary>全选 / 取消全选</summary>
        [RelayCommand]
        private void ToggleSelectAll()
        {
            bool anyOff = Folders.Any(f => !f.IsChecked && !f.AlreadyMigrated);
            foreach (var f in Folders.Where(f => !f.AlreadyMigrated))
                f.IsChecked = anyOff;
            NotifyCanMigrateChanged();
        }

        /// <summary>浏览选择单个文件夹的目标路径（纯 Win32，不依赖 WinForms）</summary>
        [RelayCommand]
        private void Browse(FolderItem? item)
        {
            if (item == null || item.AlreadyMigrated) return;

            string? selected = ShellBrowseFolder(
                $"选择「{item.DisplayName}」的目标文件夹",
                item.TargetPath);

            if (selected != null)
            {
                item.TargetPath = selected;
                ValidatePath(item);
                NotifyCanMigrateChanged();
            }
        }

        // ── Win32 Shell 文件夹选择对话框 ──────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public string pszDisplayName;
            public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO bi);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDList(IntPtr pidl, System.Text.StringBuilder pszPath);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        private const uint BIF_RETURNONLYFSDIRS = 0x0001;
        private const uint BIF_NEWDIALOGSTYLE = 0x0040; // 新式对话框（可新建文件夹）
        private const uint BIF_EDITBOX = 0x0010; // 显示路径编辑框

        /// <summary>
        /// 调用系统文件夹选择对话框，返回用户选择的路径；取消则返回 null。
        /// 不依赖 WinForms，使用 shell32 SHBrowseForFolder。
        /// </summary>
        private static string? ShellBrowseFolder(string title, string initialPath)
        {
            var bi = new BROWSEINFO
            {
                hwndOwner = IntPtr.Zero,
                lpszTitle = title,
                ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
                pszDisplayName = new string('\0', 260),
            };

            IntPtr pidl = SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero) return null;

            try
            {
                var sb = new System.Text.StringBuilder(260);
                return SHGetPathFromIDList(pidl, sb) ? sb.ToString() : null;
            }
            finally
            {
                CoTaskMemFree(pidl);
            }
        }

        /// <summary>目标路径文本框编辑完毕时实时验证</summary>
        [RelayCommand]
        private void PathEdited(FolderItem? item)
        {
            if (item != null) ValidatePath(item);
            NotifyCanMigrateChanged();
        }

        // ── CanMigrate 通知辅助 ───────────────────────────────────────────────

        private void NotifyCanMigrateChanged()
        {
            OnPropertyChanged(nameof(CanMigrate));
            MigrateCommand.NotifyCanExecuteChanged();
        }

        /// <summary>开始迁移</summary>
        [RelayCommand]
        private async Task MigrateAsync()
        {
            var selected = Folders.Where(f => f.IsChecked && !f.AlreadyMigrated).ToList();
            if (selected.Count == 0) return;

            // ── Preflight 检查 ─────────────────────────────────────────────
            var (ok, errors, warnings) = MigrationService.PreflightCheck(selected);
            if (!ok)
            {
                MessageBox.Show(
                    "迁移前检查发现以下问题，请修正后重试：\n\n" +
                    string.Join("\n", errors.Select(e => "• " + e)),
                    "无法开始迁移", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ── 警告提示（不阻止，用户确认即可继续）─────────────────────────
            if (warnings.Count > 0)
            {
                var warnAnswer = MessageBox.Show(
                    "注意以下情况：\n\n" +
                    string.Join("\n", warnings.Select(w => "⚠ " + w)) +
                    "\n\n是否仍然继续迁移？",
                    "迁移提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (warnAnswer != MessageBoxResult.Yes) return;
            }

            // ── 确认对话框 ────────────────────────────────────────────────
            string names = string.Join("、", selected.Select(f => f.DisplayName));
            var answer = MessageBox.Show(
                $"即将迁移以下文件夹：\n{names}\n\n" +
                "⚠ 迁移前请确保已保存所有工作文件。\n" +
                "⚠ 迁移过程中请勿操作被迁移的文件夹。\n\n" +
                "确认开始迁移？",
                "确认迁移",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            // ── 开始 ──────────────────────────────────────────────────────
            IsMigrating = true;
            OverallProgress = 0;
            FolderProgress = 0;
            Logs.Clear();
            StatusMessage = "正在准备迁移…";

            await _svc.StartAsync(selected);
        }

        [RelayCommand]
        private void CancelMigration()
        {
            _svc.Cancel();
            StatusMessage = "正在取消…";
        }

        [RelayCommand]
        private void ExportLog()
        {
            if (Logs.Count == 0)
            {
                MessageBox.Show("暂无迁移日志。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出迁移日志",
                Filter = "文本文件|*.txt",
                FileName = $"迁移日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            };

            if (dlg.ShowDialog() != true) return;

            var lines = Logs.Select(e => $"[{e.TimeText}] {e.LevelIcon} {e.Message}");
            System.IO.File.WriteAllLines(
                dlg.FileName, lines, System.Text.Encoding.UTF8);
            StatusMessage = $"日志已导出：{dlg.FileName}";
        }

        // ── 私有 ──────────────────────────────────────────────────────────────

        private async Task LoadAsync()
        {
            IsScanning = true;
            StatusMessage = "正在扫描用户文件夹…";
            Folders.Clear();

            var items = FolderScanService.BuildFolderList(TargetDrive);
            foreach (var item in items)
            {
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(FolderItem.IsChecked))
                        NotifyCanMigrateChanged();
                };
                Folders.Add(item);
            }

            IsScanning = false;
            StatusMessage = "扫描完成，正在计算文件夹大小…";

            RefreshDriveFree();

            // 异步计算大小
            _scanCts = new CancellationTokenSource();
            await FolderScanService.FillSizesAsync(Folders, _scanCts.Token);

            StatusMessage = "就绪，请勾选要迁移的文件夹，然后点击「迁移」";
        }

        private void ApplyDefaultTargetPaths()
        {
            string drive = TargetDrive.TrimEnd('\\', '/');
            foreach (var f in Folders.Where(f => !f.AlreadyMigrated))
            {
                string suffix = System.IO.Path.GetFileName(f.SourcePath);
                f.TargetPath = $"{drive}\\{suffix}";
                ValidatePath(f);
            }
        }

        private static void ValidatePath(FolderItem item)
        {
            if (string.IsNullOrWhiteSpace(item.TargetPath))
            {
                item.PathValid = false;
                item.PathValidMsg = "路径不能为空";
                return;
            }
            if (string.Equals(item.SourcePath, item.TargetPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                item.PathValid = false;
                item.PathValidMsg = "与源路径相同";
                return;
            }
            item.PathValid = true;
            item.PathValidMsg = "";
        }

        private void RefreshDriveFree()
        {
            try
            {
                var di = new DriveInfo(TargetDrive.TrimEnd('\\') + "\\");
                DriveFreeText = $"可用空间：{FolderItem.FormatSize(di.AvailableFreeSpace)}";
            }
            catch { DriveFreeText = ""; }
        }

        private void OnMigrationCompleted(List<MigrationResult> results)
        {
            IsMigrating = false;
            OverallProgress = 100;

            int ok = results.Count(r => r.Success);
            int bad = results.Count(r => !r.Success);

            StatusMessage = bad == 0
                ? $"迁移完成！共迁移 {ok} 个文件夹"
                : $"迁移完成：{ok} 个成功，{bad} 个失败";

            var sb = new System.Text.StringBuilder();

            if (bad == 0)
                sb.AppendLine($"✔ 全部 {ok} 个文件夹迁移成功！");
            else
            {
                sb.AppendLine($"迁移完成：{ok} 个成功，{bad} 个失败");
                sb.AppendLine("\n失败项目：");
                foreach (var r in results.Where(r => !r.Success))
                    sb.AppendLine($"  · {r.FolderName}：{r.ErrorMessage}");
            }

            sb.AppendLine("\n⚠ 部分应用程序需要重启后才能识别新路径，建议重启计算机。");

            MessageBox.Show(sb.ToString(), "迁移完成",
                MessageBoxButton.OK, MessageBoxImage.Information);

            // ── 询问是否删除源文件夹 ──────────────────────────────────────────
            var succeeded = results.Where(r => r.Success).ToList();
            if (succeeded.Count > 0)
            {
                string names = string.Join("、", succeeded.Select(r => r.FolderName));
                var del = MessageBox.Show(
                    $"是否删除 C 盘的原始文件夹？\n\n{string.Join("\n", succeeded.Select(r => "  · " + r.SourcePath))}\n\n" +
                    "⚠ 删除前请确认新位置的文件完整无误。\n" +
                    "⚠ 被其他程序占用的文件将被跳过，不会强制删除。",
                    $"删除原始文件夹（{names}）",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (del == MessageBoxResult.Yes)
                    _ = DeleteSourcesAsync(succeeded);
            }

            // 刷新列表，显示"已迁移"状态
            _ = LoadAsync();
        }

        private async Task DeleteSourcesAsync(List<MigrationResult> succeeded)
        {
            StatusMessage = "正在删除源文件夹…";
            int totalDeleted = 0, totalSkipped = 0;

            foreach (var r in succeeded)
            {
                if (!Directory.Exists(r.SourcePath)) continue;

                var (d, s) = await _svc.DeleteSourceAsync(r.SourcePath);
                totalDeleted += d;
                totalSkipped += s;
            }

            StatusMessage = totalSkipped == 0
                ? $"源文件夹已清理，共删除 {totalDeleted} 个文件"
                : $"源文件夹清理完成，删除 {totalDeleted} 个，跳过 {totalSkipped} 个（被占用）";
        }

        private static void Dispatch(Action action) =>
            Application.Current.Dispatcher.Invoke(action);
    }
}