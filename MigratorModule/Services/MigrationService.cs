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

        // 进度折算用的实例状态（见 RaiseFolderProgress）
        private int _totalFolders;
        private int _doneFolders;

        /// <summary>
        /// 目录递归枚举的统一策略：<br/>
        /// IgnoreInaccessible —— 无权限/被删的目录静默跳过，不再让整个统计半途失败；<br/>
        /// AttributesToSkip 显式包含 ReparsePoint —— 不跟随 junction / 符号链接，
        /// 避免 C:\Users\All Users 这类链接造成重复计数甚至环状递归。
        /// </summary>
        private static readonly EnumerationOptions IgnoreInaccessible = new()
        {
            IgnoreInaccessible     = true,
            RecurseSubdirectories  = true,
            AttributesToSkip       = FileAttributes.Hidden  |
                                     FileAttributes.System   |
                                     FileAttributes.ReparsePoint,
        };

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

                // 目标不得是源的子目录（复制时会把源套自己）
                if (item.TargetPath.StartsWith(item.SourcePath + "\\",
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"[{item.DisplayName}] 目标路径不能是源路径的子目录"); continue;
                }

                // 反向也不行：源在目标里面时，robocopy 边写边读同一棵树，
                // 目标文件数永远追不上源，而且会无限套娃
                if (item.SourcePath.StartsWith(item.TargetPath + "\\",
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"[{item.DisplayName}] 源路径不能是目标路径的子目录"); continue;
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
            // 重入保护：上一次迁移的 CTS 必须先 Cancel + Dispose，
            // 否则旧的取消令牌会泄漏（旧的连 robocopy 子进程都可能还在跑）
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var results = new List<MigrationResult>();
            int total = Math.Max(1, selected.Count);
            int done = 0;

            _totalFolders = total;
            _doneFolders  = 0;

            Log(MigLogLevel.Info, $"开始迁移，共 {selected.Count} 个文件夹");

            // 整体进度由 RaiseFolderProgress 统一折算：
            //   总进度 = (已完成文件夹数 + 当前文件夹进度/100) / 总数 × 100
            // 旧写法是在这里 FolderProgressChanged += lambda，每次迁移都多挂一个
            // handler 且从不摘除，第 N 次迁移时同一条进度会被推送 N 次。

            foreach (var item in selected)
            {
                if (token.IsCancellationRequested) break;

                CurrentFolderChanged?.Invoke(item.DisplayName);
                RaiseFolderProgress(0);
                Log(MigLogLevel.Info,
                    $"▶ [{item.DisplayName}] {item.SourcePath} → {item.TargetPath}");

                var r = await MigrateOneAsync(item, token);
                results.Add(r);
                done++;
                _doneFolders = done;
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
        /// 上报当前文件夹进度，并顺带折算整体进度。
        /// 做成实例方法而不是在 StartAsync 里临时订阅 FolderProgressChanged：
        /// 后者每次调用都会叠加一个 handler，导致进度回调成倍触发。
        /// </summary>
        private void RaiseFolderProgress(int pct)
        {
            FolderProgressChanged?.Invoke(pct);

            int overall = (int)((_doneFolders + pct / 100.0) / _totalFolders * 100);
            OverallProgressChanged?.Invoke(Math.Clamp(overall, 0, 100));
        }

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

        /// <summary>
        /// 自底向上删除目录树。改成显栈是为了把递归深度从调用栈里拿掉：
        /// 用户文件夹（node_modules、虚拟机镜像目录等）动辄上百层，
        /// 旧的递归写法有真栈溢出风险（栈溢出不归 Exception 管，直接闪退）。
        /// </summary>
        private void DeleteDirectory(DirectoryInfo root,
            ref int deleted, ref int skipped, CancellationToken token)
        {
            // post-order：先压目录，弹出时若未展开则先展开子目录再压回自己
            var stack = new Stack<(DirectoryInfo dir, bool expanded)>();
            stack.Push((root, false));

            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (dir, expanded) = stack.Pop();

                if (!expanded)
                {
                    stack.Push((dir, true));
                    try
                    {
                        foreach (var sub in dir.EnumerateDirectories())
                            stack.Push((sub, false));
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                    continue;
                }

                // 子目录已处理完，现在删本层文件
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
        }

        // ── 私有：迁移单个文件夹 ──────────────────────────────────────────────

        private async Task<MigrationResult> MigrateOneAsync(
            FolderItem item, CancellationToken token)
        {
            string originalPath = item.SourcePath;
            long bytes = 0;

            try
            {
                // Step 0 — 先统计源目录（文件数 + 字节数）
                //   文件数 → robocopy 进度换算；两者 → 复制完成后的完整性校验
                var (srcFiles, srcBytes) = await Task.Run(
                    () => CountFilesAndBytes(item.SourcePath), token);
                Log(MigLogLevel.Info,
                    $"  源目录：{srcFiles:N0} 个文件，{FolderItem.FormatSize(srcBytes)}");

                // Step 1 — 创建目标目录
                Directory.CreateDirectory(item.TargetPath);
                Log(MigLogLevel.Info, $"  目标目录已创建：{item.TargetPath}");

                // Step 2 — robocopy 复制（complete=false 代有文件根本没复制成功）
                bool complete = await RobocopyAsync(
                    item.SourcePath, item.TargetPath, srcFiles, token);

                if (!complete)
                    throw new IOException(
                        "复制不完整：有文件因权限不足或正被占用而未复制成功，"
                        + "已保持原路径不变，也不会提供「删除源文件夹」选项");

                // Step 3 — 完整性校验：这一步是“能不能删源”的唯一依据
                var (dstFiles, dstBytes) = await Task.Run(
                    () => CountFilesAndBytes(item.TargetPath), token);
                bytes = dstBytes;

                if (dstFiles < srcFiles || dstBytes < srcBytes)
                {
                    Log(MigLogLevel.Error,
                        $"  ✖ 校验不通过：目标 {dstFiles:N0} 个 / " +
                        $"{FolderItem.FormatSize(dstBytes)}，"
                        + $"源 {srcFiles:N0} 个 / {FolderItem.FormatSize(srcBytes)}");
                    throw new IOException(
                        $"目标目录内容少于源目录（目标 {dstFiles:N0} 个文件/"
                        + $"{FolderItem.FormatSize(dstBytes)}，源 {srcFiles:N0} 个文件/"
                        + $"{FolderItem.FormatSize(srcBytes)}），为防丢数据已中止");
                }

                Log(MigLogLevel.Success,
                    $"  校验通过：目标 {dstFiles:N0} 个文件，{FolderItem.FormatSize(dstBytes)}");

                // Step 4 — 写注册表重定向（只有确认数据完整后才动注册表）
                RedirectShellFolder(item.ShellFolderKey, item.TargetPath);

                // Step 5 — 验证注册表生效
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

        /// <summary>
        /// 执行 robocopy 复制。
        /// 返回 false 表示“复制不完整”（有文件被跳过），调用方必须据此判定失败，
        /// 绝不能进入后续的注册表重定向和删源流程。
        /// </summary>
        private async Task<bool> RobocopyAsync(
            string src, string dst, int srcFiles, CancellationToken token)
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

            // stderr 必须并发排空：它在下面才被读取，一旦 stderr 管道写满（~4KB）
            // robocopy 会永久阻塞在写stderr 上，而我们又卡在等 stdout 关闭 → 死锁
            var errTask = proc.StandardError.ReadToEndAsync();

            // 读取输出以防止缓冲区阻塞，同时解析进度
            int filesDone = 0;
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(token)) != null)
            {
                if (token.IsCancellationRequested) break;

                // robocopy 每复制一个文件输出一行，统计行数估算进度
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("-") &&
                    !trimmed.StartsWith("\\") && trimmed.Contains("\\"))
                {
                    filesDone++;
                    // 复制阶段最多占 90%，剩下给校验和注册表
                    int pct = srcFiles > 0
                        ? filesDone * 90 / srcFiles
                        : filesDone * 2;              // 源文件数未知时保留旧估算
                    FolderProgressChanged?.Invoke(Math.Min(90, pct));
                }
            }

            // 取消时必须先杀掉子进程再报错。旧写法在循环里直接 throw，
            // 后面的 Kill 永远执行不到 → robocopy 变成孤儿进程继续往目标目录写，
            // 而上层已经开始回滚注册表，两边同时动同一批文件
            if (token.IsCancellationRequested && !proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Log(MigLogLevel.Warning, "  已终止 robocopy 子进程");
            }

            string stderr;
            try { stderr = await errTask; } catch { stderr = ""; }

            if (token.IsCancellationRequested)
                throw new OperationCanceledException(token);

            await proc.WaitForExitAsync();
            int code = proc.ExitCode;

            // robocopy 退出码 bit flags：
            //   1  = 有文件被复制            2  = 目标有多余文件
            //   4  = 有文件属性/时间戳不一致  8  = 有文件被跳过（权限/占用）
            //   16 = 有文件重试次数耗尽仍未成功
            //   >=32 = 致命错误
            // 旧代码只拦 >=32，bit3(8) 只记一条警告就当成功——而后续流程会删源目录。
            if (code >= 32)
                throw new IOException($"robocopy 致命错误，退出码 {code}"
                    + (string.IsNullOrWhiteSpace(stderr) ? "" : $"：{stderr.Trim()}"));

            if ((code & 16) != 0)
                throw new IOException(
                    $"robocopy 有文件多次重试仍未复制成功（退出码 {code}）");

            if ((code & 8) != 0)
                Log(MigLogLevel.Error,
                    "  ✖ 有文件因权限不足或正被占用而未复制（退出码 " + code + "）");
            if ((code & 4) != 0)
                Log(MigLogLevel.Warning, "  ⚠ 部分文件的属性/时间戳不一致");
            if ((code & 2) != 0)
                Log(MigLogLevel.Info, "  目标目录中存在源目录没有的文件（已保留）");

            RaiseFolderProgress(90);

            return (code & (8 | 16)) == 0;
        }

        /// <summary>
        /// 递归统计文件数与总字节数。无法访问的目录静默跳过，不跟随 junction。
        /// 用于两处：复制前估源规模（进度换算）、复制后校验（删源许可判定）。
        /// </summary>
        private static (int files, long bytes) CountFilesAndBytes(string path)
        {
            int files = 0;
            long bytes = 0;
            try
            {
                foreach (var fi in new DirectoryInfo(path)
                    .EnumerateFiles("*", IgnoreInaccessible))
                {
                    files++;
                    try { bytes += fi.Length; } catch { /* 统计期间文件被删 */ }
                }
            }
            catch { /* 目录不存在或无权限 */ }
            return (files, bytes);
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