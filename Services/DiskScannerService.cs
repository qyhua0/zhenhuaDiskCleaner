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
            _cts?.Cancel();
            _cts?.Dispose();
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

                await ScanParallelAsync(rootNode, progress, token);

                if (!token.IsCancellationRequested)
                {
                    // 汇总子目录大小。以前 AccumulateSizes 定义了却从未调用，
                    // 所以“递归深扫”模式下目录的 Size 只是本层直属文件之和：
                    // 左侧树里的目录大小偏小、Treemap 的目录排序也不对，
                    // 而“MFT 快速扫描”模式（NtfsQuickScanner）是会汇总的——
                    // 同一个文件夹两种扫法给出两个数字。
                    AccumulateSizes(rootNode);

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
        private async Task ScanParallelAsync(FileNode rootNode, ScanProgress progress,
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

            // 等所有目录处理完后关闭 Channel。
            // 旧写法是 Task.Run(...).Wait(ct) + Task.WaitAll(workers, ct)：
            // 一个专门的线程什么都不干只在阻塞等，而且这两行在 Task.Run 里跑，
            // 等于长时间占住两个线程池线程（扫描越久占得越久）。
            var watchdog = Task.Run(async () =>
            {
                while (System.Threading.Interlocked.Read(ref pending) > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    try { await Task.Delay(20, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                dirChannel.Writer.TryComplete();
            }, CancellationToken.None);

            await watchdog.ConfigureAwait(false);

            // 工人循环（ReadAllAsync）看到 Complete 后自然退出；
            // 取消时 ReadAllAsync(ct) 会抛 OperationCanceledException → 这里吞掉
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { }
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

                // 更新目录自身大小（直属文件之和；子目录部分由扫描结束后的
                // AccumulateSizes 统一汇总）。同一个 dirNode 只会被一个工人处理，
                // 所以这里不需要原子操作。
                // 原先上一行写的是 Interlocked.Add(ref _scannedSize, 0); // memory fence：
                // .NET 内存模型下 Interlocked 本身已含 full fence，而 _scannedSize 的
                // 读取走的就是 Interlocked.Read，这一句加 0 什幺也不做，只留个误导性注释。
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
        /// <summary>
        /// 扫描完成后自底向上汇总目录大小（目录 Size = 所有子节点之和）。
        /// 用显栈后序而不是递归：真实磁盘上 node_modules、备份目录动辄上百层，
        /// 递归会爆栈（StackOverflow 不可捕获，直接闪退）。
        /// </summary>
        private static void AccumulateSizes(FileNode root)
        {
            var stack = new Stack<(FileNode Dir, bool Expanded)>();
            stack.Push((root, false));

            while (stack.Count > 0)
            {
                var (dir, expanded) = stack.Pop();

                if (expanded)
                {
                    long sum = 0;
                    lock (dir.Children)
                        foreach (var c in dir.Children) sum += c.Size;
                    dir.Size = sum;
                    continue;
                }

                stack.Push((dir, true));
                lock (dir.Children)
                {
                    foreach (var c in dir.Children)
                        if (c.IsDirectory) stack.Push((c, false));
                }
            }
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