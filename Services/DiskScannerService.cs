using System.IO;
using System.Threading.Channels;
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

                var rootNode = new FileNode
                {
                    Name = GetDisplayName(path),
                    FullPath = path,
                    IsDirectory = true,
                    CreatedTime = Directory.GetCreationTime(path),
                    ModifiedTime = Directory.GetLastWriteTime(path)
                };

                var progress = new ScanProgress { TotalSize = totalSize };
                var token = _cts.Token;

                await Task.Run(() => ScanParallel(rootNode, progress, token), token);

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

        // ---------------------------------------------------------------
        // 并行扫描核心：BFS 广度优先 + 工作线程池
        // ---------------------------------------------------------------
        private void ScanParallel(FileNode rootNode, ScanProgress progress,
            System.Threading.CancellationToken ct)
        {
            // Channel：目录任务队列（无界，生产快于消费时缓冲）
            var dirChannel = Channel.CreateUnbounded<FileNode>(
                new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

            dirChannel.Writer.TryWrite(rootNode);

            // 用于追踪仍在飞行中的目录数
            long pending = 1;
            // 进度节流：上次上报时间
            long lastReport = 0;

            int workerCount = Math.Max(2, Environment.ProcessorCount);
            var workers = new Task[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                workers[i] = Task.Factory.StartNew(async () =>
                {
                    await foreach (var dirNode in dirChannel.Reader.ReadAllAsync(ct))
                    {
                        if (ct.IsCancellationRequested) break;
                        ProcessDirectory(dirNode, dirChannel, progress, ref pending, ref lastReport, ct);
                    }
                }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            }

            // 等待所有目录处理完毕后关闭 Channel
            Task.Run(async () =>
            {
                while (System.Threading.Interlocked.Read(ref pending) > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }
                dirChannel.Writer.Complete();
            }, ct).Wait(ct);

            Task.WaitAll(workers, ct);
        }

        private void ProcessDirectory(FileNode dirNode, Channel<FileNode> dirChannel,
            ScanProgress progress, ref long pending, ref long lastReport,
            System.Threading.CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                DecrementPending(ref pending);
                return;
            }

            try
            {
                var info = new DirectoryInfo(dirNode.FullPath);
                long dirSize = 0;

                // ---- 文件：批量读取，减少系统调用 ----
                try
                {
                    // 预分配列表减少 resize
                    var fileNodes = new List<FileNode>(64);
                    foreach (var fi in info.EnumerateFiles())
                    {
                        if (ct.IsCancellationRequested) break;
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
                            fileNodes.Add(node);
                            dirSize += fi.Length;
                            System.Threading.Interlocked.Increment(ref _scannedFiles);
                            System.Threading.Interlocked.Add(ref _scannedSize, fi.Length);
                        }
                        catch { }
                    }

                    // 批量加入子节点（单次锁）
                    if (fileNodes.Count > 0)
                    {
                        lock (dirNode.Children)
                            foreach (var n in fileNodes) dirNode.Children.Add(n);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch { }

                // ---- 子目录：入队并行处理 ----
                try
                {
                    foreach (var di in info.EnumerateDirectories())
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
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

                            lock (dirNode.Children) dirNode.Children.Add(subNode);

                            // 入队前先增加 pending，防止提前关闭
                            System.Threading.Interlocked.Increment(ref pending);
                            dirChannel.Writer.TryWrite(subNode);
                        }
                        catch { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch { }

                // 更新目录自身大小（原子）
                System.Threading.Interlocked.Add(ref _scannedSize, 0); // memory fence
                dirNode.Size += dirSize;

                // ---- 节流进度上报：最多每 80ms 一次 ----
                long now = _sw.ElapsedMilliseconds;
                long last = System.Threading.Interlocked.Read(ref lastReport);
                if (now - last > 80 &&
                    System.Threading.Interlocked.CompareExchange(ref lastReport, now, last) == last)
                {
                    progress.CurrentPath = dirNode.FullPath;
                    progress.ScannedFiles = System.Threading.Interlocked.Read(ref _scannedFiles);
                    progress.ScannedSize = System.Threading.Interlocked.Read(ref _scannedSize);
                    progress.Elapsed = _sw.Elapsed;
                    ProgressChanged?.Invoke(progress);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch { }
            finally
            {
                DecrementPending(ref pending);
            }
        }

        private static void DecrementPending(ref long pending)
            => System.Threading.Interlocked.Decrement(ref pending);

        // ---------------------------------------------------------------
        // 扫描完成后自底向上汇总目录大小
        // （并行下子目录大小在父目录处理完后才能统计）
        // ---------------------------------------------------------------
        private static void AccumulateSizes(FileNode node)
        {
            if (!node.IsDirectory) return;
            foreach (var child in node.Children)
                AccumulateSizes(child);
            node.Size = node.Children.Sum(c => c.Size);
        }

        public void Cancel() => _cts?.Cancel();

        private static string GetDisplayName(string path)
        {
            var trimmed = path.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(trimmed)) return path;
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? path : name;
        }

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