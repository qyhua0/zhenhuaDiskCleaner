using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Services
{
    /// <summary>
    /// 使用 NTFS USN Change Journal + NtQueryDirectoryFile 实现快速扫描。
    /// 原理：直接读取 MFT（主文件表）索引，无需逐目录递归，速度比普通扫描快 5-10 倍。
    /// 需要管理员权限才能读取 Change Journal。
    /// </summary>
    public class NtfsQuickScanner
    {
        public event Action<ScanProgress>? ProgressChanged;
        public event Action<FileNode>? ScanCompleted;
        public event Action<string>? ErrorOccurred;

        private System.Threading.CancellationTokenSource? _cts;
        private readonly System.Diagnostics.Stopwatch _sw = new();

        public bool IsScanning { get; private set; }

        // ---------------------------------------------------------------
        // Win32 / NT Native API P/Invoke 定义
        // ---------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            ref USN_JOURNAL_DATA_V0 lpInBuffer, int nInBufferSize,
            IntPtr lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            ref MFT_ENUM_DATA_V0 lpInBuffer, int nInBufferSize,
            IntPtr lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, int nInBufferSize,
            IntPtr lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        [StructLayout(LayoutKind.Sequential)]
        private struct USN_JOURNAL_DATA_V0
        {
            public long UsnJournalID;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public long MaximumSize;
            public long AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MFT_ENUM_DATA_V0
        {
            public long StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct USN_RECORD_V2
        {
            public uint RecordLength;
            public ushort MajorVersion;
            public ushort MinorVersion;
            public long FileReferenceNumber;
            public long ParentFileReferenceNumber;
            public long Usn;
            public long TimeStamp;
            public uint Reason;
            public uint SourceInfo;
            public uint SecurityId;
            public uint FileAttributes;
            public ushort FileNameLength;
            public ushort FileNameOffset;
            // FileName follows (variable length)
        }

        // IOCTL 控制码
        private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
        private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
        private const uint FSCTL_CREATE_USN_JOURNAL = 0x000900E7;

        // CreateFile 参数
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        // ---------------------------------------------------------------
        // 公开扫描入口
        // ---------------------------------------------------------------

        public async Task ScanAsync(string drivePath,
            System.Threading.CancellationToken externalCt = default)
        {
            if (IsScanning) return;
            IsScanning = true;
            _cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            _sw.Restart();

            try
            {
                // drivePath 例如 "C:\\"，取盘符
                var driveLetter = Path.GetPathRoot(drivePath)?.TrimEnd('\\') ?? drivePath;
                var volumePath = "\\\\.\\" + driveLetter.TrimEnd('\\');

                var progress = new ScanProgress();
                var token = _cts.Token;

                FileNode? root = null;
                await Task.Run(() =>
                {
                    Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
                    root = ScanViaMft(volumePath, driveLetter + "\\", progress, token);
                }, token);

                if (root != null && !token.IsCancellationRequested)
                {
                    progress.IsCompleted = true;
                    progress.Elapsed = _sw.Elapsed;
                    ProgressChanged?.Invoke(progress);
                    ScanCompleted?.Invoke(root);
                }
            }
            catch (OperationCanceledException) { }
            catch (UnauthorizedAccessException)
            {
                ErrorOccurred?.Invoke("快速扫描需要管理员权限。请以管理员身份运行程序，或改用普通扫描。");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"快速扫描失败: {ex.Message}");
            }
            finally
            {
                IsScanning = false;
                _sw.Stop();
            }
        }

        public void Cancel() => _cts?.Cancel();

        // ---------------------------------------------------------------
        // 核心：通过枚举 MFT 构建文件树
        // ---------------------------------------------------------------

        private FileNode? ScanViaMft(string volumePath, string rootPath,
            ScanProgress progress, System.Threading.CancellationToken ct)
        {
            // 打开卷句柄
            using var hVol = CreateFile(volumePath,
                GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

            if (hVol.IsInvalid)
                throw new UnauthorizedAccessException(
                    $"无法打开卷 {volumePath}，错误码: {Marshal.GetLastWin32Error()}");

            // 查询或创建 USN Journal
            var journalData = new USN_JOURNAL_DATA_V0();
            if (!QueryOrCreateJournal(hVol, ref journalData))
            {
                int err = Marshal.GetLastWin32Error();
                throw new UnauthorizedAccessException(
                    err == 5
                        ? "权限不足，请以管理员身份运行程序后再使用快速扫描。"
                        : $"无法访问 USN Journal，错误码: {err}");
            }

            // 枚举所有 MFT 记录
            // key = FileReferenceNumber, value = (parentFRN, name, attrs)
            var allEntries = new Dictionary<long, MftEntry>(1 << 18); // 预分配 256K

            const int BUFFER_SIZE = 512 * 1024; // 512KB 缓冲区
            var buffer = Marshal.AllocHGlobal(BUFFER_SIZE);
            try
            {
                var enumData = new MFT_ENUM_DATA_V0
                {
                    StartFileReferenceNumber = 0,
                    LowUsn = 0,
                    HighUsn = journalData.NextUsn
                };

                while (!ct.IsCancellationRequested)
                {
                    if (!DeviceIoControl(hVol, FSCTL_ENUM_USN_DATA,
                        ref enumData, Marshal.SizeOf<MFT_ENUM_DATA_V0>(),
                        buffer, BUFFER_SIZE, out int bytesReturned, IntPtr.Zero))
                        break;

                    if (bytesReturned <= 8) break;

                    // 前 8 字节是下一个 StartFileReferenceNumber
                    enumData.StartFileReferenceNumber = Marshal.ReadInt64(buffer);

                    IntPtr ptr = buffer + 8;
                    IntPtr end = buffer + bytesReturned;

                    while (ptr.ToInt64() < end.ToInt64())
                    {
                        var rec = Marshal.PtrToStructure<USN_RECORD_V2>(ptr);
                        if (rec.RecordLength == 0) break;

                        int nameLen = rec.FileNameLength / 2; // UTF-16
                        string name = Marshal.PtrToStringUni(
                            ptr + rec.FileNameOffset, nameLen) ?? string.Empty;

                        bool isDir = (rec.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

                        // MFT 号只取低 48 位（高 16 位是序号）
                        long frn = rec.FileReferenceNumber & 0x0000FFFFFFFFFFFF;
                        long parentFrn = rec.ParentFileReferenceNumber & 0x0000FFFFFFFFFFFF;

                        allEntries[frn] = new MftEntry
                        {
                            Name = name,
                            ParentFrn = parentFrn,
                            IsDir = isDir,
                            Frn = frn
                        };

                        if (!isDir)
                            System.Threading.Interlocked.Increment(ref _totalFiles);

                        // 节流上报
                        long total = System.Threading.Interlocked.Read(ref _totalFiles);
                        if (total % 10000 == 0)
                        {
                            progress.ScannedFiles = total;
                            progress.CurrentPath = name;
                            progress.Elapsed = _sw.Elapsed;
                            ProgressChanged?.Invoke(progress);
                        }

                        ptr += (int)rec.RecordLength;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }

            if (ct.IsCancellationRequested) return null;

            progress.CurrentPath = "正在构建文件树...";
            ProgressChanged?.Invoke(progress);

            // 构建文件树
            return BuildTree(allEntries, rootPath, progress, ct);
        }

        private long _totalFiles;


        // 新增正确的 P/Invoke 重载：Query 时 InBuffer=零，OutBuffer=结构体
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, int nInBufferSize,
            out USN_JOURNAL_DATA_V0 lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        private bool QueryOrCreateJournal(SafeFileHandle hVol, ref USN_JOURNAL_DATA_V0 data)
        {
            // FSCTL_QUERY_USN_JOURNAL: InBuffer=NULL, OutBuffer=USN_JOURNAL_DATA
            if (DeviceIoControl(hVol, FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0,
                    out data, Marshal.SizeOf<USN_JOURNAL_DATA_V0>(),
                    out _, IntPtr.Zero))
                return true;

            int err = Marshal.GetLastWin32Error();

            // ERROR_JOURNAL_NOT_ACTIVE (1179) 或 ERROR_INVALID_FUNCTION -> 尝试创建
            if (err == 1179 || err == 1)
            {
                // CREATE_USN_JOURNAL: InBuffer=CREATE_USN_JOURNAL_DATA
                var createData = new CREATE_USN_JOURNAL_DATA
                {
                    MaximumSize = 0x800000, // 8MB
                    AllocationDelta = 0x100000  // 1MB
                };
                int createSize = Marshal.SizeOf<CREATE_USN_JOURNAL_DATA>();
                IntPtr pCreate = Marshal.AllocHGlobal(createSize);
                try
                {
                    Marshal.StructureToPtr(createData, pCreate, false);
                    DeviceIoControl(hVol, FSCTL_CREATE_USN_JOURNAL,
                        pCreate, createSize,
                        IntPtr.Zero, 0, out _, IntPtr.Zero);
                }
                finally { Marshal.FreeHGlobal(pCreate); }

                // 再次查询
                return DeviceIoControl(hVol, FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0,
                    out data, Marshal.SizeOf<USN_JOURNAL_DATA_V0>(),
                    out _, IntPtr.Zero);
            }

            // 其他错误（如权限不足）
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CREATE_USN_JOURNAL_DATA
        {
            public long MaximumSize;
            public long AllocationDelta;
        }

        // ---------------------------------------------------------------
        // 构建树形结构
        // ---------------------------------------------------------------

        private  FileNode BuildTree(Dictionary<long, MftEntry> entries,
            string rootPath, ScanProgress progress,
            System.Threading.CancellationToken ct)
        {
            var rootNode = new FileNode
            {
                Name = rootPath,
                FullPath = rootPath,
                IsDirectory = true
            };

            // FileNode 映射表
            var nodeMap = new Dictionary<long, FileNode>(entries.Count);

            // 找根目录 FRN（通常是卷根，ParentFrn == 自身 或 == 5）
            long rootFrn = 5; // NTFS 根目录 MFT 记录固定为 5
            nodeMap[rootFrn] = rootNode;

            // 先建立所有目录节点
            foreach (var kv in entries)
            {
                if (!kv.Value.IsDir) continue;
                if (kv.Key == rootFrn) continue;
                nodeMap[kv.Key] = new FileNode
                {
                    Name = kv.Value.Name,
                    FullPath = string.Empty, // 后面补全路径
                    IsDirectory = true
                };
            }

            // 连接父子关系（目录）
            foreach (var kv in entries)
            {
                if (!kv.Value.IsDir || kv.Key == rootFrn) continue;
                if (ct.IsCancellationRequested) return rootNode;

                if (nodeMap.TryGetValue(kv.Key, out var childNode) &&
                    nodeMap.TryGetValue(kv.Value.ParentFrn, out var parentNode))
                {
                    childNode.Parent = parentNode;
                    parentNode.Children.Add(childNode);
                }
                else
                {
                    // 找不到父节点，挂到根
                    var child = nodeMap[kv.Key];
                    child.Parent = rootNode;
                    rootNode.Children.Add(child);
                }
            }

            // 补全目录路径
            BuildPaths(rootNode, rootPath);

     
            // 先建立所有文件节点（不含元数据）
            var allFileNodes = new List<FileNode>(entries.Count);
            foreach (var kv in entries)
            {
                if (kv.Value.IsDir) continue;
                if (ct.IsCancellationRequested) return rootNode;

                if (!nodeMap.TryGetValue(kv.Value.ParentFrn, out var parentNode))
                    parentNode = rootNode;

                var ext = Path.GetExtension(kv.Value.Name);
                var file = new FileNode
                {
                    Name = kv.Value.Name,
                    FullPath = Path.Combine(parentNode.FullPath, kv.Value.Name),
                    IsDirectory = false,
                    Extension = ext,
                    FileType = FileClassifier.Classify(ext),
                    Parent = parentNode,
                    Depth = parentNode.Depth + 1
                };
                parentNode.Children.Add(file);
                allFileNodes.Add(file);
            }

            // 并行批量读取文件元数据（大小、时间）
            progress.CurrentPath = "正在读取文件元数据...";
            progress.ScannedFiles = 0;
            ProgressChanged?.Invoke(progress);

            long metaDone = 0;
            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
                CancellationToken = ct
            };

            Parallel.ForEach(allFileNodes, parallelOpts, file =>
            {
                try
                {
                    var fi = new FileInfo(file.FullPath);
                    if (fi.Exists)
                    {
                        file.Size = fi.Length;
                        file.CreatedTime = fi.CreationTime;
                        file.ModifiedTime = fi.LastWriteTime;
                    }
                }
                catch { }

                long done = Interlocked.Increment(ref metaDone);
                if (done % 20000 == 0)
                {
                    progress.ScannedFiles = done;
                    progress.CurrentPath = $"正在扫描文件... {done:N0}/{allFileNodes.Count:N0}";
                    progress.Elapsed = _sw.Elapsed;
                    ProgressChanged?.Invoke(progress);
                }
            });



            // 补充目录元数据
            Parallel.ForEach(nodeMap.Values.Where(n => n.IsDirectory && n != rootNode),
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                dir =>
                {
                    try
                    {
                        var di = new DirectoryInfo(dir.FullPath);
                        if (di.Exists)
                        {
                            dir.CreatedTime = di.CreationTime;
                            dir.ModifiedTime = di.LastWriteTime;
                        }
                    }
                    catch { }
                });

            // 自底向上汇总大小
            progress.CurrentPath = "正在计算目录大小...";
            ProgressChanged?.Invoke(progress);
            AccumulateSizes(rootNode);

            progress.ScannedFiles = entries.Count(e => !e.Value.IsDir);
            progress.ScannedSize = rootNode.Size;
            progress.Elapsed = _sw.Elapsed;
            ProgressChanged?.Invoke(progress);

            return rootNode;
        }

        private static void BuildPaths(FileNode node, string currentPath)
        {
            node.FullPath = currentPath;
            foreach (var child in node.Children)
            {
                if (child.IsDirectory)
                    BuildPaths(child, Path.Combine(currentPath, child.Name));
            }
        }

        private static void AccumulateSizes(FileNode node)
        {
            if (!node.IsDirectory) return;
            foreach (var c in node.Children) AccumulateSizes(c);
            node.Size = node.Children.Sum(c => c.Size);
        }

        private struct MftEntry
        {
            public long Frn;
            public long ParentFrn;
            public string Name;
            public bool IsDir;
        }
    }
}