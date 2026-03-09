using System.Diagnostics;
using System.Runtime.InteropServices;
using ZhenhuaDiskCleaner.CleanerModule.Models;

namespace ZhenhuaDiskCleaner.CleanerModule.Services
{
    /// <summary>
    /// 垃圾清理服务。
    /// 负责删除已扫描的垃圾文件，支持移入回收站（可撤销）和永久删除两种模式。
    /// 本服务只负责删除，不执行任何扫描操作。
    /// </summary>
    public class CleanerService
    {
        // ── Win32 Shell 回收站支持 ────────────────────────────────────────────

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT fo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.U4)] public int wFunc;
            public string pFrom;
            public string? pTo;
            public short fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        private const int FO_DELETE = 0x0003;
        private const short FOF_ALLOWUNDO = 0x0040;
        private const short FOF_NOCONFIRMATION = 0x0010;
        private const short FOF_SILENT = 0x0004;
        private const short FOF_NOERRORUI = 0x0400;

        // ── Win32：延迟删除（重启后执行）────────────────────────────────────
        // lpNewFileName=null + MOVEFILE_DELAY_UNTIL_REBOOT 将路径写入注册表
        // HKLM\...\Session Manager\PendingFileRenameOperations
        // 系统重启时由 smss.exe 执行实际删除，需要管理员权限。

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(
            string lpExistingFileName,
            string? lpNewFileName,
            uint dwFlags);

        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

        // ── 进度 / 完成回调 ───────────────────────────────────────────────────

        /// <summary>每处理一个文件后触发（已处理数, 总数）</summary>
        public event Action<int, int>? ProgressChanged;

        /// <summary>
        /// 清理全部完成后触发。
        /// 参数：
        ///   freed         — 已释放字节数
        ///   success       — 直接删除成功数
        ///   pendingReboot — 已登记「重启后删除」的路径（被进程占用类）
        ///   skippedFiles  — 跳过的文件（需要 SYSTEM 权限 / 其他无法处理的原因）
        /// </summary>
        public event Action<long, int,
            List<string>,
            List<(string Path, string Reason)>>? CleanCompleted;

        private CancellationTokenSource? _cts;

        // ── 公开方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 异步执行清理。
        /// 遍历所有已勾选的 ScanResult，逐一删除其文件列表中的文件。
        /// </summary>
        /// <param name="results">扫描结果（只清理 IsChecked == true 的项）</param>
        /// <param name="toRecycleBin">true=移入回收站，false=永久删除</param>
        public async Task CleanAsync(
            IEnumerable<ScanResult> results,
            bool toRecycleBin = true)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // 收集所有待删除文件
            var toDelete = results
                .Where(r => r.IsChecked)
                .SelectMany(r => r.Files)
                .Where(File.Exists)
                .ToList();

            int total = toDelete.Count;
            int done = 0;
            int success = 0;
            long freed = 0;
            var pendingReboot = new List<string>();
            var skippedFiles = new List<(string Path, string Reason)>();

            await Task.Run(() =>
            {
                foreach (var filePath in toDelete)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        long size = new FileInfo(filePath).Length;

                        if (toRecycleBin)
                            DeleteToRecycleBin(filePath);
                        else
                        {
                            File.Delete(filePath);
                            // File.Delete 对受保护文件有时静默失败（不抛异常）
                            if (File.Exists(filePath))
                                throw new UnauthorizedAccessException(
                                    "文件删除后仍然存在（系统保护文件）");
                        }

                        freed += size;
                        success++;
                    }
                    catch (Exception ex) when (
                        ex is UnauthorizedAccessException ||
                        ex is IOException)
                    {
                        // ── 直接删除失败，判断失败原因再决定处理方式 ──────────
                        //
                        // 两种截然不同的情况：
                        //   A. 文件被其他进程占用（IOException）
                        //      → MoveFileEx 登记重启后删除，smss.exe 在进程启动前执行，有效
                        //
                        //   B. 文件需要 SYSTEM 权限（UnauthorizedAccessException）
                        //      → 即使 MoveFileEx + 注册表写入成功，重启时 smss.exe 同样
                        //        没有该文件的删除权限（Defender 等服务会在 smss 之后保护），
                        //        实际不会被删除，给用户"已登记"是虚假承诺
                        //      → 直接归入跳过列表，如实告知用户

                        bool isPermissionDenied = IsPermissionDenied(filePath);

                        if (isPermissionDenied)
                        {
                            // 需要 SYSTEM 权限，任何方式都无法删除，直接跳过
                            skippedFiles.Add((filePath, "需要 SYSTEM 权限，系统保护文件"));
                        }
                        else
                        {
                            // 文件被占用（非权限问题）→ 尝试登记重启后删除
                            try
                            {
                                bool ok = MoveFileEx(filePath, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                                if (ok && IsPendingRebootRegistered(filePath))
                                    pendingReboot.Add(filePath);
                                else
                                    skippedFiles.Add((filePath, "文件被占用，无法登记延迟删除"));
                            }
                            catch
                            {
                                skippedFiles.Add((filePath, "文件正在使用中"));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var reason = ex.Message.Length > 40
                            ? ex.Message[..40] + "…"
                            : ex.Message;
                        skippedFiles.Add((filePath, reason));
                    }

                    done++;
                    ProgressChanged?.Invoke(done, total);
                }
            }, token);

            CleanCompleted?.Invoke(freed, success, pendingReboot, skippedFiles);
        }

        /// <summary>取消正在进行的清理</summary>
        public void Cancel() => _cts?.Cancel();

        /// <summary>
        /// 验证 MoveFileEx 是否真正将路径写入了注册表
        /// HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations。
        ///
        /// 背景：对受安全软件（如 Windows Defender）保护的路径，MoveFileEx 会返回 true，
        /// 但注册表实际未写入，重启后文件不会被删除。此方法通过读取注册表确认写入结果。
        /// 路径在注册表中以 "\??\" 前缀的 NT 格式存储，需做前缀匹配。
        /// </summary>
        private static bool IsPendingRebootRegistered(string filePath)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager");
                if (key == null) return false;

                var value = key.GetValue("PendingFileRenameOperations");
                if (value is not string[] entries) return false;

                // 注册表中每两行为一对：[0]=源路径（\??\C:\...），[1]=目标（空=删除）
                // 只需找源路径包含 filePath 即可（大小写不敏感）
                string needle = filePath.Replace('/', '\\');
                return entries.Any(e =>
                    e.EndsWith(needle, StringComparison.OrdinalIgnoreCase) ||
                    e.Equals(@"\??\" + needle, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // 读注册表失败（非管理员等），保守返回 false
                return false;
            }
        }

        /// <summary>
        /// 判断文件是否因权限不足（需要 SYSTEM 级别）而无法操作。
        ///
        /// 方法：尝试以最低限度（只读 + 完全共享）打开文件。
        ///   - 抛出 UnauthorizedAccessException → 是权限问题（SYSTEM 保护），返回 true
        ///   - 抛出 IOException（sharing violation）→ 是占用问题，不是权限，返回 false
        ///   - 成功打开 → 说明可访问，删除失败属于其他原因，返回 false
        /// </summary>
        private static bool IsPermissionDenied(string filePath)
        {
            try
            {
                using var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return false; // 能打开，不是权限问题
            }
            catch (UnauthorizedAccessException)
            {
                return true;  // 明确权限拒绝
            }
            catch (IOException)
            {
                return false; // 被占用，不是权限问题，可以尝试 MoveFileEx
            }
            catch
            {
                return false; // 其他异常保守处理
            }
        }
        /// - 文件：打开其所在目录并高亮选中该文件
        /// - 目录：直接打开该目录
        /// </summary>
        public static void OpenInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Directory.Exists(path))
                    Process.Start("explorer.exe", $"\"{path}\"");
                // 路径不存在时尝试打开父目录
                else
                {
                    var parent = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                        Process.Start("explorer.exe", $"\"{parent}\"");
                }
            }
            catch { /* 打开资源管理器失败静默处理 */ }
        }

        // ── 私有方法 ──────────────────────────────────────────────────────────

        /// <summary>将单个文件移入回收站（不弹出确认对话框）。失败时抛出 IOException。</summary>
        private static void DeleteToRecycleBin(string filePath)
        {
            var fo = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = filePath + "\0\0",
                fFlags = (short)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
            };
            int ret = SHFileOperation(ref fo);

            // SHFileOperation 不抛异常，必须手动检查返回值和中止标志
            // 0x78 = ERROR_ACCESS_DENIED, 0x7C = 路径错误, 非零均为失败
            if (ret != 0 || fo.fAnyOperationsAborted)
                throw new UnauthorizedAccessException(
                    $"SHFileOperation 失败，错误码 0x{ret:X}");

            // 二次确认：有时返回 0 但文件仍然存在（受保护目录的假成功）
            if (File.Exists(filePath))
                throw new UnauthorizedAccessException(
                    "文件删除后仍然存在（系统保护文件）");
        }
    }
}