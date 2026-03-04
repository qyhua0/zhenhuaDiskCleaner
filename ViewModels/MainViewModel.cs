using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using ZhenhuaDiskCleaner.Models;
using ZhenhuaDiskCleaner.Services;
using WpfColor = System.Windows.Media.Color;

namespace ZhenhuaDiskCleaner.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly DiskScannerService _scanner = new();

        private readonly NtfsQuickScanner _quickScanner = new();

        private readonly FileOperationService _fileOps = new();
        private readonly FileWatcherService _watcher = new();

        [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<DriveItem> _drives = new();
        [ObservableProperty] private DriveItem? _selectedDrive;
        [ObservableProperty] private string _customPath = string.Empty;
        [ObservableProperty] private FileNode? _rootNode;
        [ObservableProperty] private FileNode? _selectedNode;
        [ObservableProperty] private FileNode? _highlightedNode;
        [ObservableProperty] private ScanProgress _scanProgress = new();
        [ObservableProperty] private bool _isScanning;
        public bool CanScan => !IsScanning;
        partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(CanScan));

        [ObservableProperty] private bool _hasScanResult;
        [ObservableProperty] private string _statusMessage = "请选择驱动器或目录开始扫描";
        [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<FileTypeStats> _fileTypeStats = new();
        [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<FileNode> _fileListItems = new();
        [ObservableProperty] private int _selectedTabIndex;
        [ObservableProperty] private HashResult? _hashResult;
        [ObservableProperty] private bool _isComputingHash;

        public MainViewModel()
        {
            LoadDrives();
            _scanner.ProgressChanged += OnScanProgress;
            _scanner.ScanCompleted   += OnScanCompleted;
            _scanner.ErrorOccurred   += OnScanError;
            _watcher.Changed         += OnFsChanged;
        }

        private void LoadDrives()
        {
            Drives.Clear();
            foreach (var d in DiskScannerService.GetDrives()) Drives.Add(d);
            if (Drives.Count > 0) SelectedDrive = Drives[0];
        }

        [RelayCommand]
        private async Task StartScanAsync()
        {
            var path = !string.IsNullOrWhiteSpace(CustomPath) ? CustomPath : SelectedDrive?.Path;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!Directory.Exists(path)) { StatusMessage = "路径不存在，请重新输入"; return; }
            RootNode = null; HasScanResult = false;
            FileListItems.Clear(); FileTypeStats.Clear();
            ScanProgress = new ScanProgress(); IsScanning = true;
            StatusMessage = "正在扫描...";
            await _scanner.ScanAsync(path);
        }

        [RelayCommand]
        private void CancelScan()
        {
            _scanner.Cancel();
            _quickScanner.Cancel();
            IsScanning = false;
            StatusMessage = "扫描已取消";
        }

        [RelayCommand]
        private async Task QuickScanAsync()
        {
            var path = !string.IsNullOrWhiteSpace(CustomPath) ? CustomPath : SelectedDrive?.Path;
            if (string.IsNullOrWhiteSpace(path)) return;

            // 快速扫描只支持整盘 NTFS，需管理员权限
            var root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            { StatusMessage = "快速扫描需要选择完整的驱动器"; return; }

            RootNode = null; HasScanResult = false;
            FileListItems.Clear(); FileTypeStats.Clear();
            ScanProgress = new ScanProgress(); IsScanning = true;
            StatusMessage = "⚡ 快速扫描中（读取 MFT）...";

            _quickScanner.ProgressChanged -= OnScanProgress;
            _quickScanner.ScanCompleted -= OnScanCompleted;
            _quickScanner.ErrorOccurred -= OnScanError;
            _quickScanner.ProgressChanged += OnScanProgress;
            _quickScanner.ScanCompleted += OnScanCompleted;
            _quickScanner.ErrorOccurred += OnScanError;

            await _quickScanner.ScanAsync(root);
        }

        [RelayCommand]
        private void BrowseDirectory()
        {
            var path = FolderBrowserHelper.Browse("选择要扫描的目录");
            if (!string.IsNullOrEmpty(path)) CustomPath = path;
        }

        [RelayCommand]
        private void RefreshDrives() => LoadDrives();

        private void OnScanProgress(ScanProgress p)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ScanProgress.ScannedFiles = p.ScannedFiles;
                ScanProgress.ScannedSize  = p.ScannedSize;
                ScanProgress.TotalSize    = p.TotalSize;
                ScanProgress.CurrentPath  = p.CurrentPath;
                ScanProgress.Elapsed      = p.Elapsed;
                StatusMessage = $"已扫描 {p.ScannedFiles:N0} 个文件 · {p.ScannedSizeText} · {p.ElapsedText}";
            });
        }

        private void OnScanCompleted(FileNode root)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                IsScanning = false; RootNode = root; HasScanResult = true;
                BuildFileList(root); BuildTypeStats(root);
                StatusMessage = $"扫描完成 · 共 {ScanProgress.ScannedFiles:N0} 个文件 · {root.SizeText}";
                try { _watcher.Watch(root.FullPath); } catch { }
            });
        }

        private void OnScanError(string error)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            { IsScanning = false; StatusMessage = "扫描出错: " + error; });
        }

        private void OnFsChanged(string path, WatcherChangeTypes change)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
                StatusMessage = "文件变更: " + Path.GetFileName(path));
        }

        private void BuildFileList(FileNode root)
        {
            FileListItems.Clear();
            foreach (var item in Flatten(root).OrderByDescending(f => f.Size).Take(500))
                FileListItems.Add(item);
        }

        private static IEnumerable<FileNode> Flatten(FileNode n)
        {
            yield return n;
            foreach (var c in n.Children) foreach (var x in Flatten(c)) yield return x;
        }

        private void BuildTypeStats(FileNode root)
        {
            FileTypeStats.Clear();
            var dict = new Dictionary<FileType, (long size, int count)>();
            Collect(root, dict);
            long total = 0; foreach (var v in dict.Values) total += v.size;
            if (total == 0) return;
            var names = new Dictionary<FileType, string>
            {
                {FileType.Image,"图片"},{FileType.Video,"视频"},{FileType.Audio,"音频"},
                {FileType.Document,"文档"},{FileType.Archive,"压缩包"},
                {FileType.Executable,"程序"},{FileType.Code,"代码"},{FileType.Unknown,"其他"}
            };
            foreach (var kv in dict.OrderByDescending(k => k.Value.size))
            {
                names.TryGetValue(kv.Key, out var tn);
                FileTypeStats.Add(new FileTypeStats
                {
                    Type = kv.Key, TypeName = tn ?? "其他",
                    TotalSize = kv.Value.size, Count = kv.Value.count,
                    Percentage = (double)kv.Value.size / total * 100,
                    Color = new FileNode { FileType = kv.Key }.TypeColor
                });
            }
        }

        private static void Collect(FileNode n, Dictionary<FileType, (long, int)> d)
        {
            if (!n.IsDirectory) { d.TryGetValue(n.FileType, out var c); d[n.FileType] = (c.Item1 + n.Size, c.Item2 + 1); }
            foreach (var child in n.Children) Collect(child, d);
        }

        partial void OnSelectedNodeChanged(FileNode? value)
        { HashResult = null; if (value != null) HighlightedNode = value; }

        [RelayCommand]
        private void DeleteToRecycleBin(FileNode? node)
        {
            var n = node ?? SelectedNode; if (n == null) return;
            if (MessageBox.Show($"将 '{n.Name}' 移到回收站？", "确认删除", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                if (_fileOps.DeleteToRecycleBin(n.FullPath)) { Remove(n); StatusMessage = "已移至回收站: " + n.Name; }
        }

        [RelayCommand]
        private void DeletePermanently(FileNode? node)
        {
            var n = node ?? SelectedNode; if (n == null) return;
            if (MessageBox.Show($"永久删除 '{n.Name}'?\n不可撤销！", "警告",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                if (_fileOps.DeletePermanently(n.FullPath)) { Remove(n); StatusMessage = "已永久删除: " + n.Name; }
        }

        [RelayCommand]
        private void ClearDirectory(FileNode? node)
        {
            var n = node ?? SelectedNode; if (n == null || !n.IsDirectory) return;
            if (MessageBox.Show($"清空 '{n.Name}' 全部内容?\n不可撤销！", "危险操作",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                if (_fileOps.ClearDirectory(n.FullPath)) { n.Children.Clear(); n.Size = 0; StatusMessage = "已清空: " + n.Name; }
        }

        [RelayCommand]
        private void CopyPath(FileNode? node)
        {
            var n = node ?? SelectedNode; if (n == null) return;
            _fileOps.CopyPathToClipboard(n.FullPath);
            StatusMessage = "路径已复制";
        }

        [RelayCommand]
        private void OpenInExplorer(FileNode? node)
            => _fileOps.OpenInExplorer((node ?? SelectedNode)?.FullPath ?? string.Empty);

        [RelayCommand]
        private void OpenInCmd(FileNode? node)
            => _fileOps.OpenInCmd((node ?? SelectedNode)?.FullPath ?? string.Empty);

        [RelayCommand]
        private async Task ComputeHashAsync(FileNode? node)
        {
            var n = node ?? SelectedNode; if (n == null || n.IsDirectory) return;
            IsComputingHash = true; HashResult = null; StatusMessage = "正在计算哈希值...";
            try   { HashResult = await _fileOps.ComputeHashAsync(n.FullPath); StatusMessage = "哈希计算完成"; }
            catch (Exception ex) { StatusMessage = "哈希计算失败: " + ex.Message; }
            finally { IsComputingHash = false; }
        }

        private void Remove(FileNode node)
        {
            node.Parent?.Children.Remove(node); FileListItems.Remove(node);
            if (SelectedNode == node) SelectedNode = null;
        }

        public void OnTreemapNodeClicked(FileNode node) => SelectedNode = node;
        public void OnTreemapNodeHovered(FileNode node) => HighlightedNode = node;
    }
}
