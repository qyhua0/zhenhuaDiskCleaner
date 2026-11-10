using ZhenhuaDiskCleaner.MigratorModule.Models;

namespace ZhenhuaDiskCleaner.MigratorModule.Services
{
    /// <summary>
    /// 扫描当前用户的可迁移文件夹，从注册表读取真实路径，
    /// 异步计算文件夹大小。
    /// </summary>
    internal static class FolderScanService
    {
        // ── 文件夹定义表 ──────────────────────────────────────────────────────

        private static readonly (string Name, string Icon, string RegKey, string DefaultSuffix)[] Definitions =
        {
            ("桌面",   "🖥",  "Desktop",     "Desktop"),
            ("文档",   "📄",  "Personal",    "Documents"),
            ("下载",   "⬇",   "Downloads",   "Downloads"),
            ("图片",   "🖼",  "My Pictures", "Pictures"),
            ("音乐",   "🎵",  "My Music",    "Music"),
            ("视频",   "🎬",  "My Videos",   "Videos"),
            ("收藏夹", "⭐",  "Favorites",   "Favorites"),
        };

        // ── 公开接口 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 构建文件夹列表（同步，仅读注册表/Shell，不计算大小）。
        /// 大小计算通过 <see cref="FillSizesAsync"/> 异步完成。
        /// </summary>
        public static List<FolderItem> BuildFolderList(string targetDrive)
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string drive = targetDrive.TrimEnd('\\', '/');
            var result = new List<FolderItem>();

            foreach (var (name, icon, regKey, suffix) in Definitions)
            {
                string src = ReadShellPath(regKey, profile, suffix);
                if (!Directory.Exists(src)) continue;

                // 判断是否已迁移（源路径不在 %USERPROFILE% 所在分区）
                bool migrated = !src.StartsWith(
                    System.IO.Path.GetPathRoot(profile) ?? "C:\\",
                    StringComparison.OrdinalIgnoreCase);

                result.Add(new FolderItem
                {
                    DisplayName = name,
                    Icon = icon,
                    ShellFolderKey = regKey,
                    SourcePath = src,
                    TargetPath = migrated ? src : $"{drive}\\{suffix}",
                    AlreadyMigrated = migrated,
                    IsChecked = false,
                    IsSizeReady = false,
                    SizeText = "计算中…",
                });
            }

            return result;
        }

        /// <summary>
        /// 异步计算每个文件夹大小，完成后更新 FolderItem 属性。
        /// </summary>
        public static async Task FillSizesAsync(
            IEnumerable<FolderItem> items,
            CancellationToken ct = default)
        {
            var tasks = items.Select(item => Task.Run(async () =>
            {
                try
                {
                    long sz = await ComputeSizeAsync(item.SourcePath, ct);
                    item.SizeBytes = sz;
                    item.SizeText = FolderItem.FormatSize(sz);
                    item.IsSizeReady = true;
                }
                catch (OperationCanceledException)
                {
                    item.SizeText = "已取消";
                }
                catch
                {
                    item.SizeText = "无法读取";
                    item.IsSizeReady = true;
                }
            }, ct));

            await Task.WhenAll(tasks);
        }

        // ── 私有方法 ──────────────────────────────────────────────────────────

        private static Task<long> ComputeSizeAsync(string path, CancellationToken ct) =>
            Task.Run(() => SafeGetSize(new DirectoryInfo(path), ct), ct);

        /// <summary>遇到无权限/被占用项直接跳过，不抛（所有目录枚举共用）。</summary>
        private static readonly EnumerationOptions IgnoreInaccessible = new() { IgnoreInaccessible = true };

        /// <summary>
        /// 累计目录大小。用显式栈而不是递归：下载/文档/OneDrive 这类用户目录
        /// 的真实层级可以上百，递归在这里有爆栈风险，而 StackOverflow 不可 catch，
        /// 会直接带走整个进程。无权限目录依旧逐个跳过；
        /// 取消则继续向上抛 OperationCanceledException（由 FillSizesAsync 接手）。
        /// </summary>
        private static long SafeGetSize(DirectoryInfo root, CancellationToken ct)
        {
            long total = 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();

                // 累计当前层文件（Length 已在枚举结果里，不再二次 stat）
                try
                {
                    foreach (var fi in dir.EnumerateFiles("*", IgnoreInaccessible))
                    {
                        ct.ThrowIfCancellationRequested();
                        try { total += fi.Length; } catch { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }

                try
                {
                    foreach (var sub in dir.EnumerateDirectories("*", IgnoreInaccessible))
                        stack.Push(sub);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }

            return total;
        }

        /// <summary>优先从注册表读真实路径，fallback 到拼接默认路径</summary>
        private static string ReadShellPath(string regKey, string profile, string suffix)
        {
            // 下载文件夹用 GUID 键
            string lookupKey = regKey == "Downloads"
                ? "{374DE290-123F-4565-9164-39C4925E467B}"
                : regKey;

            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                if (k?.GetValue(lookupKey) is string val && !string.IsNullOrEmpty(val))
                    return val;
            }
            catch { }

            return System.IO.Path.Combine(profile, suffix);
        }
    }
}