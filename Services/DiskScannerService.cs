using System.IO;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Services
{
    public class DiskScannerService
    {
        private System.Threading.CancellationTokenSource? _cts;
        private readonly System.Diagnostics.Stopwatch _sw = new();
        private long _scannedFiles;
        private long _scannedSize;

        public event Action<ScanProgress>? ProgressChanged;
        public event Action<FileNode>? ScanCompleted;
        public event Action<string>? ErrorOccurred;

        public bool IsScanning { get; private set; }

        public async Task ScanAsync(string path)
        {
            if (IsScanning) return;
            IsScanning = true;
            _cts = new System.Threading.CancellationTokenSource();
            _sw.Restart();
            _scannedFiles = 0;
            _scannedSize = 0;

            try
            {
                long totalSize = 0;
                try
                {
                    var root = Path.GetPathRoot(path);
                    if (root != null)
                    {
                        var di = new DriveInfo(root);
                        totalSize = di.TotalSize - di.TotalFreeSpace;
                    }
                }
                catch { }

                var progress = new ScanProgress { TotalSize = totalSize };
                var rootNode = new FileNode
                {
                    Name = GetDisplayName(path),
                    FullPath = path,
                    IsDirectory = true,
                    CreatedTime = Directory.GetCreationTime(path),
                    ModifiedTime = Directory.GetLastWriteTime(path)
                };

                var token = _cts.Token;
                await Task.Run(() =>
                {
                    System.Threading.Thread.CurrentThread.Priority =
                        System.Threading.ThreadPriority.AboveNormal;
                    ScanDirectory(rootNode, progress, token);
                }, token);

                if (!token.IsCancellationRequested)
                {
                    progress.ScannedFiles = System.Threading.Interlocked.Read(ref _scannedFiles);
                    progress.ScannedSize = System.Threading.Interlocked.Read(ref _scannedSize);
                    progress.Elapsed = _sw.Elapsed;
                    progress.IsCompleted = true;
                    ProgressChanged?.Invoke(progress);
                    ScanCompleted?.Invoke(rootNode);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ErrorOccurred?.Invoke(ex.Message); }
            finally { IsScanning = false; _sw.Stop(); }
        }

        private static string GetDisplayName(string path)
        {
            var trimmed = path.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(trimmed)) return path;
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? path : name;
        }

        // 纯串行递归，无锁，结果完整可靠
        private void ScanDirectory(FileNode dirNode, ScanProgress progress,
            System.Threading.CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            var info = new DirectoryInfo(dirNode.FullPath);

            // ---- 扫描文件 ----
            try
            {
                foreach (var fi in info.EnumerateFiles())
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var node = new FileNode
                        {
                            Name = fi.Name,
                            FullPath = fi.FullName,
                            Size = fi.Length,
                            IsDirectory = false,
                            Extension = fi.Extension,
                            FileType = FileClassifier.Classify(fi.Extension),
                            CreatedTime = fi.CreationTime,
                            ModifiedTime = fi.LastWriteTime,
                            Parent = dirNode,
                            Depth = dirNode.Depth + 1
                        };
                        dirNode.Children.Add(node);
                        dirNode.Size += fi.Length;

                        System.Threading.Interlocked.Increment(ref _scannedFiles);
                        System.Threading.Interlocked.Add(ref _scannedSize, fi.Length);

                        // 每100个文件上报一次进度，保证进度条流畅
                        if (_scannedFiles % 100 == 0)
                        {
                            progress.CurrentPath = fi.FullName;
                            progress.ScannedFiles = _scannedFiles;
                            progress.ScannedSize = _scannedSize;
                            progress.Elapsed = _sw.Elapsed;
                            ProgressChanged?.Invoke(progress);
                        }
                    }
                    catch { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch { }

            // ---- 递归子目录 ----
            try
            {
                foreach (var di in info.EnumerateDirectories())
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        // 跳过 junction/symlink 防死循环
                        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                        var subNode = new FileNode
                        {
                            Name = di.Name,
                            FullPath = di.FullName,
                            IsDirectory = true,
                            CreatedTime = di.CreationTime,
                            ModifiedTime = di.LastWriteTime,
                            Parent = dirNode,
                            Depth = dirNode.Depth + 1
                        };
                        dirNode.Children.Add(subNode);

                        // 上报当前正在进入的目录
                        progress.CurrentPath = di.FullName;
                        progress.ScannedFiles = _scannedFiles;
                        progress.ScannedSize = _scannedSize;
                        progress.Elapsed = _sw.Elapsed;
                        ProgressChanged?.Invoke(progress);

                        ScanDirectory(subNode, progress, ct);

                        // 子目录扫完后把大小向上汇总
                        dirNode.Size += subNode.Size;
                    }
                    catch (UnauthorizedAccessException) { }
                    catch { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch { }
        }

        public void Cancel() => _cts?.Cancel();

        public static List<DriveItem> GetDrives()
        {
            var result = new List<DriveItem>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady)
                        result.Add(new DriveItem
                        {
                            Name = d.Name,
                            Path = d.RootDirectory.FullName,
                            TotalSize = d.TotalSize,
                            FreeSize = d.TotalFreeSpace,
                            DriveType = d.DriveType,
                            Label = d.VolumeLabel
                        });
                }
                catch { }
            }
            return result;
        }
    }
}