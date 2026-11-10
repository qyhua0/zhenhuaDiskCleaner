using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Services
{
    public class SystemCleanerService
    {
        public event Action<string>? ProgressChanged;
        public event Action<long>? Completed;

        private long _cleanedBytes;
        private CancellationTokenSource? _cts;

        /// <summary>目录枚举统一忽略无权限/被占用项，并复用同一个实例（以前每次调用都 new）。</summary>
        private static readonly EnumerationOptions IgnoreInaccessible = new() { IgnoreInaccessible = true };

        public void Cancel() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }

        public async Task CleanAsync()
        {
            // 旧的 CTS 先 Cancel 再 Dispose，不然每清理一次漏一个内核句柄
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
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

        /// <summary>
        /// 当前进程是否已提权。powercfg /h off 需要管理员，
        /// 没提权时应该明明白白告诉用户，而不是静默失败。
        /// </summary>
        private static bool IsElevated()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
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
                foreach (var fi in dir.EnumerateFiles(pattern, IgnoreInaccessible))
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
                    foreach (var sub in dir.EnumerateDirectories("*", IgnoreInaccessible))
                    {
                        if (ct.IsCancellationRequested) return;
                        // 以前这里先 DirSize(sub) 再 sub.Delete(true)：同一棵子树被完整遍历两遍
                        // （一遍只为算大小）。Windows 更新缓存动辄几万文件，时间直接翻倍。
                        // 现在边删边累加。
                        Interlocked.Add(ref _cleanedBytes, DeleteTree(sub, ct));
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

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        private const uint SHERB_NOCONFIRMATION = 0x1;
        private const uint SHERB_NOPROGRESSUI   = 0x2;
        private const uint SHERB_NOSOUND        = 0x4;

        /// <summary>
        /// 清空回收站——必须走 Shell API。
        /// 以前是直接遍历各盘的 $Recycle.Bin 删文件，会把回收站的元数据
        /// （$I* 记录与原文件 $R* 的配对）搞成一半有一半没，
        /// 资源管理器里会残留点不开的幽灵条目，右键“还原”也会报错。
        /// </summary>
        private void CleanRecycleBin(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            Report("正在清空回收站...");
            try
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                long size = 0, items = 0;
                if (SHQueryRecycleBin(null, ref info) == 0) { size = info.i64Size; items = info.i64NumItems; }

                int hr = SHEmptyRecycleBin(IntPtr.Zero, null,
                    SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

                if (hr == 0)
                {
                    Interlocked.Add(ref _cleanedBytes, size);
                    Report($"回收站已清空：{items} 项 / {FileNode.FormatSize(size)}");
                }
                else if (hr == unchecked((int)0x8000FFFF))
                {
                    // E_UNEXPECTED：回收站本来就是空的，不算失败
                }
                else
                {
                    Report($"回收站清空失败（0x{hr:X8}），已跳过");
                }
            }
            catch (Exception ex) { Report("回收站清空异常：" + ex.Message); }
        }

        private void DisableHibernation(System.Threading.CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            // 先量大小：powercfg /h off 成功的话系统会直接把 hiberfil.sys 删掉，
            // 之后再 File.Exists 永远是 false——原来就是这个顺序，所以这一项
            // 从来没被计入“释放空间”，用户看不到这块收益。
            long hibSize = 0;
            try
            {
                var hib = new FileInfo(@"C:\hiberfil.sys");
                if (hib.Exists) hibSize = hib.Length;
            }
            catch { }

            if (hibSize == 0)
            {
                Report("休眠已关闭或不存在 hiberfil.sys，跳过");
                return;
            }

            // 没提权时别再跑 powercfg：Verb = "runas" 在 UseShellExecute = false 下会被系统
            // 直接忽略，不会弹 UAC，只会得到一个失败的退出码——完全静默。
            if (!IsElevated())
            {
                Report($"需要管理员权限才能力休眠（可释放 {FileNode.FormatSize(hibSize)}），已跳过");
                return;
            }

            Report($"正在关闭休眠模式（释放 {FileNode.FormatSize(hibSize)}）...");
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/h off")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) { Report("powercfg 启动失败，休眠未关闭"); return; }

                if (!proc.WaitForExit(15000))
                {
                    try { proc.Kill(); } catch { }
                    Report("powercfg 超时，休眠未关闭");
                    return;
                }

                if (proc.ExitCode == 0)
                    Interlocked.Add(ref _cleanedBytes, hibSize);
                else
                    Report($"powercfg /h off 退出码 {proc.ExitCode}，休眠未关闭");
            }
            catch (Exception ex) { Report("关闭休眠失败：" + ex.Message); }
        }

        /// <summary>
        /// 后序递归删除一棵子树，只返回实际删掉的字节数（显式栈，不用递归）。
        /// 删不掉的（被占用/无权限）保留，与原来 Delete(true) 的部分失败行为一致。
        /// </summary>
        private static long DeleteTree(DirectoryInfo root, CancellationToken ct)
        {
            long bytes = 0;
            var stack = new Stack<(DirectoryInfo Dir, bool Expanded)>();
            stack.Push((root, false));

            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                var (dir, expanded) = stack.Pop();

                if (!expanded)
                {
                    stack.Push((dir, true));   // 子项处理完再回来删目录本身

                    try
                    {
                        foreach (var sub in dir.EnumerateDirectories("*", IgnoreInaccessible))
                            stack.Push((sub, false));
                    }
                    catch { }

                    try
                    {
                        foreach (var fi in dir.EnumerateFiles("*", IgnoreInaccessible))
                        {
                            try
                            {
                                fi.Attributes = FileAttributes.Normal;
                                fi.Delete();
                                bytes += fi.Length;
                            }
                            catch { }
                        }
                    }
                    catch { }
                    continue;
                }

                try { dir.Delete(); } catch { }
            }

            return bytes;
        }

        private void Report(string msg) => ProgressChanged?.Invoke(msg);
    }
}