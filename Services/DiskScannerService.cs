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

        public async System.Threading.Tasks.Task ScanAsync(string path)
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
                    if (root != null) { var di = new DriveInfo(root); totalSize = di.TotalSize - di.TotalFreeSpace; }
                }
                catch { }

                var progress = new ScanProgress { TotalSize = totalSize };
                var rootNode = new FileNode
                {
                    Name = GetDisplayName(path), FullPath = path, IsDirectory = true,
                    CreatedTime = Directory.GetCreationTime(path),
                    ModifiedTime = Directory.GetLastWriteTime(path)
                };

                var token = _cts.Token;
               // await System.Threading.Tasks.Task.Run(() => ScanDirectory(rootNode, progress, token), token);

                await System.Threading.Tasks.Task.Run(() =>
                {
                    // 提升线程优先级加速 I/O 密集扫描
                    System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;
                    ScanDirectory(rootNode, progress, token);
                    System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
                }, token);

                if (!_cts.Token.IsCancellationRequested)
                {
                    progress.ScannedFiles = System.Threading.Interlocked.Read(ref _scannedFiles);
                    progress.ScannedSize = System.Threading.Interlocked.Read(ref _scannedSize);
                    progress.Elapsed = _sw.Elapsed;
                    progress.IsCompleted = true;
                    ProgressChanged?.Invoke(progress);
                    ScanCompleted?.Invoke(rootNode);
                }
            }
            catch (System.OperationCanceledException) { }
            catch (System.Exception ex) { ErrorOccurred?.Invoke(ex.Message); }
            finally { IsScanning = false; _sw.Stop(); }
        }

        private static string GetDisplayName(string path)
        {
            var trimmed = path.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(trimmed)) return path;
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? path : name;
        }

        private void ScanDirectory(FileNode dirNode, ScanProgress progress, System.Threading.CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var info = new DirectoryInfo(dirNode.FullPath);
                var subDirs = new System.Collections.Generic.List<FileNode>();

                // 一次性枚举所有条目，减少系统调用次数
                try
                {
                    foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        AttributesToSkip = FileAttributes.ReparsePoint // 跳过符号链接，防止死循环
                    }))
                    {
                        if (ct.IsCancellationRequested) return;

                        if (entry is FileInfo fi)
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
                            // 直接 Add，无需 lock —— 父目录未对外暴露，此时只有本线程访问
                            dirNode.Children.Add(node);
                            dirNode.Size += fi.Length;

                            var cnt = System.Threading.Interlocked.Increment(ref _scannedFiles);
                            System.Threading.Interlocked.Add(ref _scannedSize, fi.Length);

                            // 降低回调频率：每 500 个文件汇报一次
                            if (cnt % 500 == 0)
                            {
                                progress.CurrentPath = fi.FullName;
                                progress.ScannedFiles = cnt;
                                progress.ScannedSize = System.Threading.Interlocked.Read(ref _scannedSize);
                                progress.Elapsed = _sw.Elapsed;
                                ProgressChanged?.Invoke(progress);
                            }
                        }
                        else if (entry is DirectoryInfo di)
                        {
                            var node = new FileNode
                            {
                                Name = di.Name,
                                FullPath = di.FullName,
                                IsDirectory = true,
                                CreatedTime = di.CreationTime,
                                ModifiedTime = di.LastWriteTime,
                                Parent = dirNode,
                                Depth = dirNode.Depth + 1
                            };
                            dirNode.Children.Add(node);
                            subDirs.Add(node);
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (Exception) { }

                if (subDirs.Count == 0 || ct.IsCancellationRequested) return;

                // 浅层目录并行，深层串行 —— 避免线程爆炸
                if (dirNode.Depth < 3)
                {
                    var opts = new System.Threading.Tasks.ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = ct
                    };
                    try
                    {
                        System.Threading.Tasks.Parallel.ForEach(subDirs, opts, sub =>
                        {
                            ScanDirectory(sub, progress, ct);
                            // 子目录大小向上汇总
                            System.Threading.Interlocked.Add(ref _scannedSize, 0); // memory barrier
                            dirNode.Size += sub.Size;
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                }
                else
                {
                    foreach (var sub in subDirs)
                    {
                        if (ct.IsCancellationRequested) return;
                        ScanDirectory(sub, progress, ct);
                        dirNode.Size += sub.Size;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }


        //暂停使用：每个目录单独枚举文件和子目录，导致系统调用次数过多，性能较差
        private void ScanDirectory_old(FileNode dirNode, ScanProgress progress, System.Threading.CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var info = new DirectoryInfo(dirNode.FullPath);

                // Files
                try
                {
                    foreach (var file in info.EnumerateFiles())
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            var node = new FileNode
                            {
                                Name = file.Name, FullPath = file.FullName, Size = file.Length,
                                IsDirectory = false, Extension = file.Extension,
                                FileType = FileClassifier.Classify(file.Extension),
                                CreatedTime = file.CreationTime, ModifiedTime = file.LastWriteTime,
                                Parent = dirNode, Depth = dirNode.Depth + 1
                            };
                            lock (dirNode.Children) dirNode.Children.Add(node);

                            var cnt = System.Threading.Interlocked.Increment(ref _scannedFiles);
                            System.Threading.Interlocked.Add(ref _scannedSize, file.Length);

                            if (cnt % 200 == 0)
                            {
                                progress.CurrentPath = file.FullName;
                                progress.ScannedFiles = cnt;
                                progress.ScannedSize = System.Threading.Interlocked.Read(ref _scannedSize);
                                progress.Elapsed = _sw.Elapsed;
                                ProgressChanged?.Invoke(progress);
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Subdirectories
                var subNodes = new System.Collections.Generic.List<FileNode>();
                try
                {
                    foreach (var sub in info.EnumerateDirectories())
                    {
                        try
                        {
                            var node = new FileNode
                            {
                                Name = sub.Name, FullPath = sub.FullName, IsDirectory = true,
                                CreatedTime = sub.CreationTime, ModifiedTime = sub.LastWriteTime,
                                Parent = dirNode, Depth = dirNode.Depth + 1
                            };
                            lock (dirNode.Children) dirNode.Children.Add(node);
                            subNodes.Add(node);
                        }
                        catch { }
                    }
                }
                catch { }

                var opts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
                try { System.Threading.Tasks.Parallel.ForEach(subNodes, opts, sub => ScanDirectory(sub, progress, ct)); }
                catch (System.OperationCanceledException) { throw; }
                catch { }

                // Sum sizes
                foreach (var child in dirNode.Children)
                    dirNode.Size += child.Size;
            }
            catch (System.OperationCanceledException) { throw; }
            catch { }
        }

        public void Cancel() => _cts?.Cancel();

        public static System.Collections.Generic.List<DriveItem> GetDrives()
        {
            var result = new System.Collections.Generic.List<DriveItem>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady)
                        result.Add(new DriveItem
                        {
                            Name = d.Name, Path = d.RootDirectory.FullName,
                            TotalSize = d.TotalSize, FreeSize = d.TotalFreeSpace,
                            DriveType = d.DriveType, Label = d.VolumeLabel
                        });
                }
                catch { }
            }
            return result;
        }
    }
}
