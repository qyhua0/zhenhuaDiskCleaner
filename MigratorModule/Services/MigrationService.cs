using System.Diagnostics;
using System.Runtime.InteropServices;
using ZhenhuaDiskCleaner.MigratorModule.Models;

namespace ZhenhuaDiskCleaner.MigratorModule.Services
{
    /// <summary>
    /// 执行文件夹迁移核心逻辑：
    ///   1. Preflight 检查（空间、路径合法性）
    ///   2. robocopy 复制（实时进度）
    ///   3. 注册表 Shell Folders 重定向
    ///   4. 验证重定向生效
    ///   5. 失败自动回滚注册表
    /// </summary>
    internal class MigrationService
    {
        // ── Win32 Shell 重定向 ────────────────────────────────────────────────

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHSetKnownFolderPath(
            ref Guid rfid, uint dwFlags, IntPtr hToken, string pszPath);

        private static readonly Dictionary<string, Guid> KnownFolderGuids = new()
        {
            ["Desktop"] = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
            ["Personal"] = new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
            ["Downloads"] = new Guid("374DE290-123F-4565-9164-39C4925E467B"),
            ["My Pictures"] = new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"),
            ["My Music"] = new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"),
            ["My Videos"] = new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
            ["Favorites"] = new Guid("1777F761-68AD-4D8A-87BD-30B759FA33DD"),
        };

        // ── 事件 ──────────────────────────────────────────────────────────────

        /// <summary>总进度 0-100</summary>
        public event Action<int>? OverallProgressChanged;
        /// <summary>当前文件夹复制进度 0-100</summary>
        public event Action<int>? FolderProgressChanged;
        /// <summary>当前正在处理的文件夹名</summary>
        public event Action<string>? CurrentFolderChanged;
        /// <summary>日志条目产生</summary>
        public event Action<MigrationLogEntry>? LogAdded;
        /// <summary>全部结束</summary>
        public event Action<List<MigrationResult>>? Completed;

        private CancellationTokenSource? _cts;

        // ── 公开方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 迁移前检查。
        /// 返回 (通过, 错误信息列表)。
        /// </summary>
        public static (bool ok, List<string> errors, List<string> warnings) PreflightCheck(
            IEnumerable<FolderItem> items)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            foreach (var item in items)
            {
                if (!item.IsChecked) continue;

                // 路径不得为空
                if (string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    errors.Add($"[{item.DisplayName}] 目标路径不能为空"); continue;
                }

                // 源目标不得相同
                if (string.Equals(item.SourcePath, item.TargetPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"[{item.DisplayName}] 目标路径与源路径相同"); continue;
                }

                // 目标不得是源的子目录
                if (item.TargetPath.StartsWith(item.SourcePath + "\\",
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"[{item.DisplayName}] 目标路径不能是源路径的子目录"); continue;
                }

                // 目标磁盘可用空间 ≥ 源大小 × 1.2
                try
                {
                    string root = System.IO.Path.GetPathRoot(item.TargetPath) ?? "";
                    var di = new DriveInfo(root);
                    long need = (long)(item.SizeBytes * 1.2);

                    if (di.AvailableFreeSpace < need)
                        errors.Add($"[{item.DisplayName}] 目标磁盘空间不足" +
                            $"（需要 {FolderItem.FormatSize(need)}，" +
                            $"可用 {FolderItem.FormatSize(di.AvailableFreeSpace)}）");
                }
                catch
                {
                    errors.Add($"[{item.DisplayName}] 无法读取目标磁盘，请确认路径有效");
                }

                // 目标目录已存在且非空 → 单独收集为警告，不放入 errors
                if (Directory.Exists(item.TargetPath) &&
                    Directory.EnumerateFileSystemEntries(item.TargetPath).Any())
                    warnings.Add($"[{item.DisplayName}] 目标目录已存在且不为空，迁移将合并内容");
            }

            return (errors.Count == 0, errors, warnings);
        }

        /// <summary>异步开始迁移，逐个处理已勾选的文件夹。</summary>
        public async Task StartAsync(List<FolderItem> selected)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var results = new List<MigrationResult>();
            int total = selected.Count;
            int done = 0;

            Log(MigLogLevel.Info, $"开始迁移，共 {total} 个文件夹");

            // 订阅 FolderProgress，实时折算到 OverallProgress
            // 总进度 = (已完成文件夹数 + 当前文件夹进度/100) / 总数 × 100
            FolderProgressChanged += folderPct =>
            {
                int overall = (int)((done + folderPct / 100.0) / total * 100);
                OverallProgressChanged?.Invoke(Math.Clamp(overall, 0, 100));
            };

            foreach (var item in selected)
            {
                if (token.IsCancellationRequested) break;

                CurrentFolderChanged?.Invoke(item.DisplayName);
                FolderProgressChanged?.Invoke(0);
                Log(MigLogLevel.Info,
                    $"▶ [{item.DisplayName}] {item.SourcePath} → {item.TargetPath}");

                var r = await MigrateOneAsync(item, token);
                results.Add(r);
                done++;
                // 文件夹完成，整体进度精确推到该段末尾
                OverallProgressChanged?.Invoke(done * 100 / total);

                if (r.Success)
                    Log(MigLogLevel.Success,
                        $"  ✔ [{item.DisplayName}] 完成，迁移 {FolderItem.FormatSize(r.BytesMigrated)}");
                else
                    Log(MigLogLevel.Error,
                        $"  ✖ [{item.DisplayName}] 失败：{r.ErrorMessage}");
            }

            Log(MigLogLevel.Info, "全部任务处理完毕");
            Completed?.Invoke(results);
        }

        public void Cancel() => _cts?.Cancel();

        /// <summary>
        /// 删除源文件夹。仅在迁移+注册表重定向均成功后调用。
        /// 用 robocopy /MOVE 不适合（会移动而非先复制再删），
        /// 改用递归删除：先删文件，再删空目录，遇到被占用的文件跳过并记日志。
        /// </summary>
        public async Task<(int deleted, int skipped)> DeleteSourceAsync(
            string sourcePath, CancellationToken token = default)
        {
            int deleted = 0, skipped = 0;

            await Task.Run(() =>
            {
                DeleteDirectory(new DirectoryInfo(sourcePath),
                    ref deleted, ref skipped, token);
            }, token);

            return (deleted, skipped);
        }

        private void DeleteDirectory(DirectoryInfo dir,
            ref int deleted, ref int skipped, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            // 先递归删子目录
            try
            {
                foreach (var sub in dir.EnumerateDirectories())
                {
                    token.ThrowIfCancellationRequested();
                    DeleteDirectory(sub, ref deleted, ref skipped, token);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            // 再删当前目录的文件
            try
            {
                foreach (var fi in dir.EnumerateFiles())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        fi.Attributes = FileAttributes.Normal; // 去掉只读属性
                        fi.Delete();
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Log(MigLogLevel.Warning, $"  跳过（无法删除）：{fi.FullName} — {ex.Message}");
                        skipped++;
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            // 最后尝试删目录本身（为空才能删除）
            try
            {
                dir.Refresh();
                if (!dir.EnumerateFileSystemInfos().Any())
                    dir.Delete();
            }
            catch { /* 目录非空或被占用，保留 */ }
        }

        // ── 私有：迁移单个文件夹 ──────────────────────────────────────────────

        private async Task<MigrationResult> MigrateOneAsync(
            FolderItem item, CancellationToken token)
        {
            string originalPath = item.SourcePath;
            long bytes = 0;

            try
            {
                // Step 1 — 创建目标目录
                Directory.CreateDirectory(item.TargetPath);
                Log(MigLogLevel.Info, $"  目标目录已创建：{item.TargetPath}");

                // Step 2 — robocopy 复制
                bytes = await RobocopyAsync(item.SourcePath, item.TargetPath, token);

                // Step 3 — 写注册表重定向
                RedirectShellFolder(item.ShellFolderKey, item.TargetPath);

                // Step 4 — 验证
                string? actual = ReadShellFolder(item.ShellFolderKey);
                if (!string.Equals(actual, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"注册表写入后验证失败（actual={actual}）");

                Log(MigLogLevel.Success, $"  注册表重定向已验证：{item.TargetPath}");

                return new MigrationResult
                {
                    FolderName = item.DisplayName,
                    SourcePath = originalPath,
                    TargetPath = item.TargetPath,
                    Success = true,
                    BytesMigrated = bytes,
                };
            }
            catch (OperationCanceledException)
            {
                Log(MigLogLevel.Warning, "  用户取消，回滚注册表…");
                TryRollback(item.ShellFolderKey, originalPath);
                return Fail(item, originalPath, "用户取消");
            }
            catch (Exception ex)
            {
                Log(MigLogLevel.Error, $"  异常：{ex.Message}，回滚注册表…");
                TryRollback(item.ShellFolderKey, originalPath);
                return Fail(item, originalPath, ex.Message);
            }
        }

        // ── 私有：robocopy ────────────────────────────────────────────────────

        private async Task<long> RobocopyAsync(
            string src, string dst, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "robocopy",
                // /E=含空目录  /COPY:DAT=只复数据/属性/时间戳  /R:1/W:1=快速重试
                // /NP=不显示百分比  /NDL=不列目录  /NFL=不列文件名（减少输出噪音）
                Arguments = $"\"{src}\" \"{dst}\" /E /COPY:DAT /R:1 /W:1 /NP /NDL /NFL",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // 读取输出以防止缓冲区阻塞，同时解析进度
            int filesDone = 0;
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                token.ThrowIfCancellationRequested();

                // robocopy 每复制一个文件输出一行，统计行数估算进度
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("-") &&
                    !trimmed.StartsWith("\\") && trimmed.Contains("\\"))
                {
                    filesDone++;
                    // 进度不超过 95%，留空间给后续步骤
                    int pct = Math.Min(95, filesDone * 2);
                    FolderProgressChanged?.Invoke(pct);
                }
            }

            await proc.StandardError.ReadToEndAsync();
            await Task.Run(() => proc.WaitForExit(), token);

            if (token.IsCancellationRequested)
            {
                try { proc.Kill(); } catch { }
                throw new OperationCanceledException();
            }

            // robocopy 退出码 bit flags：
            //   bit0 = 有文件被复制  bit1 = 有额外文件  bit2 = 不匹配
            //   bit3 = 有文件跳过（警告，不终止）  bit5+(>=32) = 运行错误
            if (proc.ExitCode >= 32)
                throw new IOException($"robocopy 运行错误，退出码 {proc.ExitCode}");

            if ((proc.ExitCode & 8) != 0)
                Log(MigLogLevel.Warning, "  ⚠ 部分文件因权限不足被跳过");

            FolderProgressChanged?.Invoke(100);

            // 直接统计目标目录大小，不依赖 robocopy 输出解析
            long copied = await Task.Run(() => GetDirSize(new DirectoryInfo(dst)), token);
            return copied;
        }

        private static long GetDirSize(DirectoryInfo dir)
        {
            long total = 0;
            try
            {
                foreach (var fi in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    try { total += fi.Length; } catch { }
            }
            catch { }
            return total;
        }

        // ── 私有：注册表操作 ──────────────────────────────────────────────────

        private static void RedirectShellFolder(string key, string path)
        {
            // User Shell Folders（持久，ExpandString）
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                k.SetValue(key, path, Microsoft.Win32.RegistryValueKind.ExpandString);

            // Shell Folders（当前会话即时生效）
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"))
                k.SetValue(key, path, Microsoft.Win32.RegistryValueKind.String);

            // SHSetKnownFolderPath 通知 Shell（Win Vista+）
            if (KnownFolderGuids.TryGetValue(key, out Guid guid))
            {
                try { SHSetKnownFolderPath(ref guid, 0, IntPtr.Zero, path); }
                catch { /* 非致命，注册表已写入 */ }
            }
        }

        private static string? ReadShellFolder(string key)
        {
            try
            {
                string lookupKey = key == "Downloads"
                    ? "{374DE290-123F-4565-9164-39C4925E467B}" : key;
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                return k?.GetValue(lookupKey) as string;
            }
            catch { return null; }
        }

        private void TryRollback(string key, string originalPath)
        {
            try
            {
                RedirectShellFolder(key, originalPath);
                Log(MigLogLevel.Info, $"  ↩ 已回滚注册表 [{key}] → {originalPath}");
            }
            catch (Exception ex)
            {
                Log(MigLogLevel.Error, $"  回滚失败：{ex.Message}");
            }
        }

        // ── 私有：辅助 ────────────────────────────────────────────────────────

        private void Log(MigLogLevel level, string msg) =>
            LogAdded?.Invoke(new MigrationLogEntry { Level = level, Message = msg });

        private static MigrationResult Fail(FolderItem item, string src, string err) =>
            new()
            {
                FolderName = item.DisplayName,
                SourcePath = src,
                TargetPath = item.TargetPath,
                Success = false,
                ErrorMessage = err,
            };
    }
}