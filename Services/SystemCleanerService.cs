using System.Diagnostics;
using System.IO;

namespace ZhenhuaDiskCleaner.Services
{
    public class SystemCleanerService
    {
        public event Action<string>? ProgressChanged;
        public event Action<long>? Completed;

        private long _cleanedBytes;
        private System.Threading.CancellationTokenSource? _cts;

        public void Cancel() => _cts?.Cancel();

        public async Task CleanAsync()
        {
            _cts = new System.Threading.CancellationTokenSource();
            _cleanedBytes = 0;
            var ct = _cts.Token;

            await Task.Run(() =>
            {
                // 1. 用户临时文件
                CleanDirectory(Path.GetTempPath(), ct, "用户临时文件");

                // 2. Windows 临时文件
                CleanDirectory(@"C:\Windows\Temp", ct, "系统临时文件");

                // 3. 预取文件（Prefetch）
                CleanDirectory(@"C:\Windows\Prefetch", ct, "预取缓存");

                // 4. 缩略图缓存
                CleanDirectory(
                    Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\Windows\Explorer"),
                    ct, "缩略图缓存", "thumbcache_*.db");

                // 5. Windows 更新缓存
                CleanDirectory(@"C:\Windows\SoftwareDistribution\Download",
                    ct, "Windows更新缓存");

                // 6. 字体缓存
                CleanFiles(new[]
                {
                    @"C:\Windows\System32\FNTCACHE.DAT",
                }, ct, "字体缓存");

                // 7. 错误报告文件
                CleanDirectory(
                    Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\Windows\WER\ReportArchive"),
                    ct, "错误报告归档");
                CleanDirectory(
                    Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\Windows\WER\ReportQueue"),
                    ct, "错误报告队列");

                // 8. IE/Edge 缓存
                CleanDirectory(
                    Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\Windows\INetCache"),
                    ct, "浏览器缓存");

                // 9. 回收站
                CleanRecycleBin(ct);

                // 10. 最近使用文件记录（Recent）— 只删快捷方式，不删原文件
                CleanDirectory(
                    Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.Recent)),
                    ct, "最近使用记录", "*.lnk");

                // 11. 日志文件
                CleanDirectory(@"C:\Windows\Logs", ct, "系统日志", "*.log");
                CleanDirectory(@"C:\Windows\Logs\CBS", ct, "CBS日志");

                // 12. 崩溃转储
                CleanDirectory(@"C:\Windows\Minidump", ct, "崩溃转储");
                CleanFiles(new[] { @"C:\Windows\MEMORY.DMP" }, ct, "内存转储");

                // 13. DirectX Shader 缓存
                CleanDirectory(
                    Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                        @"D3DSCache"),
                    ct, "着色器缓存");

                // 14. 关闭休眠（释放 hiberfil.sys，通常 4-16GB）
                DisableHibernation(ct);

            }, ct);

            Completed?.Invoke(_cleanedBytes);
        }

        // ── 清理目录 ────────────────────────────────────────────────

        private void CleanDirectory(string path, System.Threading.CancellationToken ct,
            string label, string pattern = "*")
        {
            if (!Directory.Exists(path)) return;
            Report($"正在清理 {label}...");
            try
            {
                var dir = new DirectoryInfo(path);

                // 删文件
                foreach (var fi in dir.EnumerateFiles(pattern,
                    new EnumerationOptions { IgnoreInaccessible = true }))
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        long size = fi.Length;
                        fi.Attributes = FileAttributes.Normal;
                        fi.Delete();
                        System.Threading.Interlocked.Add(ref _cleanedBytes, size);
                    }
                    catch { }
                }

                // 删子目录（只在 pattern="*" 时才删目录）
                if (pattern == "*")
                {
                    foreach (var sub in dir.EnumerateDirectories("*",
                        new EnumerationOptions { IgnoreInaccessible = true }))
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            long size = DirSize(sub);
                            sub.Delete(true);
                            System.Threading.Interlocked.Add(ref _cleanedBytes, size);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void CleanFiles(IEnumerable<string> paths,
            System.Threading.CancellationToken ct, string label)
        {
            Report($"正在清理 {label}...");
            foreach (var p in paths)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    if (!File.Exists(p)) continue;
                    var fi = new FileInfo(p);
                    long size = fi.Length;
                    fi.Attributes = FileAttributes.Normal;
                    fi.Delete();
                    System.Threading.Interlocked.Add(ref _cleanedBytes, size);
                }
                catch { }
            }
        }

        private void CleanRecycleBin(System.Threading.CancellationToken ct)
        {
            Report("正在清空回收站...");
            try
            {
                // 枚举所有盘符的 $Recycle.Bin
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    var rb = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                    if (!Directory.Exists(rb)) continue;
                    foreach (var userDir in Directory.EnumerateDirectories(rb))
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            foreach (var f in Directory.EnumerateFiles(userDir, "*",
                                new EnumerationOptions { IgnoreInaccessible = true }))
                            {
                                try
                                {
                                    var fi = new FileInfo(f);
                                    long sz = fi.Length;
                                    fi.Attributes = FileAttributes.Normal;
                                    fi.Delete();
                                    System.Threading.Interlocked.Add(ref _cleanedBytes, sz);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void DisableHibernation(System.Threading.CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            Report("正在关闭休眠模式（释放 hiberfil.sys）...");
            try
            {
                // powercfg /h off 关闭休眠，系统自动删除 hiberfil.sys
                var psi = new ProcessStartInfo("powercfg", "/h off")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Verb = "runas"
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);

                // hiberfil.sys 大小计入清理量
                var hib = @"C:\hiberfil.sys";
                if (File.Exists(hib))
                {
                    var fi = new FileInfo(hib);
                    System.Threading.Interlocked.Add(ref _cleanedBytes, fi.Length);
                }
            }
            catch { }
        }

        private static long DirSize(DirectoryInfo dir)
        {
            long size = 0;
            try
            {
                foreach (var fi in dir.EnumerateFiles("*",
                    new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true }))
                {
                    try { size += fi.Length; } catch { }
                }
            }
            catch { }
            return size;
        }

        private void Report(string msg) => ProgressChanged?.Invoke(msg);
    }
}