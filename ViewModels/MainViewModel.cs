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


        private readonly SystemCleanerService _cleaner = new();
        [ObservableProperty] private bool _isCleaning;
        [ObservableProperty] private string _cleanStatus = string.Empty;
        [ObservableProperty] private bool _hasCleanResult;
        [ObservableProperty] private string _cleanResultText = string.Empty;

        public MainViewModel()
        {
            LoadDrives();
            _scanner.ProgressChanged += OnScanProgress;
            _scanner.ScanCompleted   += OnScanCompleted;
            _scanner.ErrorOccurred   += OnScanError;
            _watcher.Changed         += OnFsChanged;

            // 快速扫描器复用同一组处理器：构造函数里一次性订阅。
            // 以前在 QuickScanAsync 里每点一次做一轮 -= / +=，能工作但没必要。
            _quickScanner.ProgressChanged += OnScanProgress;
            _quickScanner.ScanCompleted   += OnScanCompleted;
            _quickScanner.ErrorOccurred   += OnScanError;

            // 清理服务的回调只能在这里订阅一次。原先写在 OneKeyCleanAsync 里，
            // 每点一次「一键清理」就多挂一对 lambda 且从不摘除：
            // 第 N 次清理时同一条状态会被推 N 次，lambda 还把本 VM 永久钉在服务上。
            _cleaner.ProgressChanged += msg =>
                Application.Current?.Dispatcher.InvokeAsync(() => CleanStatus = msg);

            _cleaner.Completed += bytes =>
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    IsCleaning = false;
                    HasCleanResult = true;
                    CleanResultText = $"清理完成！共释放 {FileNode.FormatSize(bytes)}";
                    CleanStatus = CleanResultText;
                    StatusMessage = CleanResultText;
                });
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

            // try/finally 不可省：ScanAsync 招任何异常都会把 IsScanning 卡在 true，
            // 而 CanScan 依赖它 → 扫描/快速扫描按钮全部变灰，只能重启程序
            try
            {
                await _scanner.ScanAsync(path);
            }
            catch (Exception ex)
            {
                IsScanning = false;
                StatusMessage = "扫描失败: " + ex.Message;
            }
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

            // 订阅已移到构造函数（同一组处理器只需挂一次）

            try
            {
                await _quickScanner.ScanAsync(root);
            }
            catch (Exception ex)
            {
                IsScanning = false;
                StatusMessage = "快速扫描失败: " + ex.Message;
            }
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
            // 磁盘忙时（Windows Update、解压、拷贝大文件）每秒能推上千条事件，
            // 每条一个 Dispatcher 投递会把 UI 线程淡没，而它换来的只是状态栏一行字。
            // 按 300ms 合流；最后一条偶发性地不上屏，对状态栏文案无害。
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastFsStatusTicks) < 300) return;
            Interlocked.Exchange(ref _lastFsStatusTicks, now);

            var name = Path.GetFileName(path);
            Application.Current?.Dispatcher.InvokeAsync(() =>
                StatusMessage = "文件变更: " + name);
        }

        private long _lastFsStatusTicks;

        private void BuildFileList(FileNode root)
        {
            // 排序取前 500 后整体替换集合：逐条 Add 会触发 500 次
            // CollectionChanged，DataGrid 跟着重绘 500 次；赋值只触发一次 PropertyChanged。
            FileListItems = new System.Collections.ObjectModel.ObservableCollection<FileNode>(
                Flatten(root).OrderByDescending(f => f.Size).Take(500));
        }

        /// <summary>
        /// 迭代式深度优先展开整棵树。
        /// 旧的写法是「yield return + 递归」，在深层目录（node_modules、虚拟机镜像
        /// 目录动辄上百层）上有爆栈风险，而 StackOverflow 不受 try/catch 管，直接闪退。
        /// </summary>
        private static IEnumerable<FileNode> Flatten(FileNode root)
        {
            var stack = new Stack<FileNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--)
                    stack.Push(n.Children[i]);
            }
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
                    Color = FileNode.GetColor(kv.Key)
                });
            }
        }

        /// <summary>迭代式统计各类型（同 Flatten，不把目录深度压在调用栈上）。</summary>
        private static void Collect(FileNode root, Dictionary<FileType, (long, int)> d)
        {
            var stack = new Stack<FileNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!n.IsDirectory)
                {
                    d.TryGetValue(n.FileType, out var c);
                    d[n.FileType] = (c.Item1 + n.Size, c.Item2 + 1);
                }
                for (int i = n.Children.Count - 1; i >= 0; i--)
                    stack.Push(n.Children[i]);
            }
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
            node.Parent?.Children.Remove(node);
            FileListItems.Remove(node);
            if (SelectedNode == node) SelectedNode = null;

            // 更新父目录及祖先的大小
            var parent = node.Parent;
            while (parent != null)
            {
                parent.Size -= node.Size;
                parent = parent.Parent;
            }

            // 通知 Treemap 重建
            var root = RootNode;
            RootNode = null;
            RootNode = root;
        }

        public void OnTreemapNodeClicked(FileNode node) => SelectedNode = node;
        public void OnTreemapNodeHovered(FileNode node) => HighlightedNode = node;



        [RelayCommand]
        private async Task OneKeyCleanAsync()
        {
            if (IsCleaning) return;
            var result = System.Windows.MessageBox.Show(
                "一键清理将执行以下操作：\n\n" +
                "✓ 清理临时文件、系统缓存\n" +
                "✓ 清理Windows更新残留\n" +
                "✓ 清理错误报告、崩溃转储\n" +
                "✓ 清理回收站\n" +
                "✓ 关闭休眠（删除hiberfil.sys）\n\n" +
                "不会删除任何用户数据和已安装软件。\n继续？",
                "一键清理确认", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes) return;

            IsCleaning = true;
            HasCleanResult = false;
            CleanStatus = "准备清理...";

            // 进度的订阅已在构造函数完成，这里只负责跑和兼管状态。
            // 没有 try/finally 时，CleanAsync 一抛异常 IsCleaning 就永久为 true，
            // 一键清理按钮再也不会响应。
            try
            {
                await _cleaner.CleanAsync();
            }
            catch (Exception ex)
            {
                CleanStatus = "清理失败: " + ex.Message;
                StatusMessage = CleanStatus;
            }
            finally
            {
                IsCleaning = false;
            }
        }
    }
}
