using System.Collections.Concurrent;
using ZhenhuaDiskCleaner.CleanerModule.Models;

namespace ZhenhuaDiskCleaner.CleanerModule.Services
{
    /// <summary>
    /// 垃圾文件扫描服务。
    /// 采用并行策略对多条规则同时扫描，通过进度回调实时通知 UI。
    /// 本服务只负责扫描，不执行任何删除操作。
    /// </summary>
    public class ScannerService
    {
        // ── 进度回调 ──────────────────────────────────────────────────────────

        /// <summary>单条规则扫描完成后触发，参数为已完成数量和总数量</summary>
        public event Action<int, int>? ProgressChanged;

        /// <summary>全部扫描完成后触发，参数为最终结果列表</summary>
        public event Action<List<ScanResult>>? ScanCompleted;

        private CancellationTokenSource? _cts;

        // ── 公开方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 异步启动扫描。
        /// 加载规则 → 解析路径 → 并行扫描 → 汇总结果
        /// </summary>
        public async Task ScanAsync()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var rules = RuleLoader.LoadRules();
            int total = rules.Count;
            int done = 0;

            var bag = new ConcurrentBag<ScanResult>();

            await Task.Run(() =>
            {
                Parallel.ForEach(rules,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                    rule =>
                    {
                        if (token.IsCancellationRequested) return;
                        var result = ScanRule(rule);
                        if (result != null)
                            bag.Add(result);

                        var current = Interlocked.Increment(ref done);
                        ProgressChanged?.Invoke(current, total);
                    });
            }, token);

            if (token.IsCancellationRequested) return;

            // 按总大小降序排列，大块垃圾排前面更易被用户发现
            var sorted = bag.OrderByDescending(r => r.TotalSize).ToList();
            ScanCompleted?.Invoke(sorted);
        }

        /// <summary>取消正在进行的扫描</summary>
        public void Cancel() => _cts?.Cancel();

        // ── 私有方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 扫描单条规则，返回 ScanResult；
        /// 若目录不存在或扫描到的文件为 0，则返回 null（不显示空节点）
        /// </summary>
        private static ScanResult? ScanRule(ScanRule rule)
        {
            // 解析环境变量，如 %TEMP% → 实际路径
            var expandedPath = Environment.ExpandEnvironmentVariables(rule.Path);
            if (!Directory.Exists(expandedPath))
                return null;

            var files = CollectFiles(rule, expandedPath);
            if (files.Count == 0)
                return null;

            // 统计总大小（忽略无法访问的文件）
            long totalSize = 0;
            var entries = new List<FileEntry>(files.Count);
            foreach (var f in files)
            {
                try
                {
                    var info = new FileInfo(f);
                    totalSize += info.Length;
                    entries.Add(new FileEntry
                    {
                        FullPath  = f,
                        FileName  = Path.GetFileName(f),
                        Size      = info.Length,
                        Category  = rule.Name,
                    });
                }
                catch { /* 权限不足或文件已被删除，跳过 */ }
            }

            if (totalSize == 0) return null;

            // 文件条目按大小降序，方便用户优先关注大文件
            entries.Sort((a, b) => b.Size.CompareTo(a.Size));

            return new ScanResult
            {
                Name        = rule.Name,
                Icon        = rule.Icon,
                Description = rule.Description,
                RuleType    = rule.Type,
                TotalSize   = totalSize,
                Files       = files,
                FileEntries = entries,
                // 推荐类型默认勾选，专业类型默认不勾选，与文档 5.2/5.3 保持一致
                IsChecked   = rule.Type == "recommend",
            };
        }

        /// <summary>
        /// 收集目录下的文件列表。
        /// 支持按文件名 Pattern、子目录名 SubDirPattern 和最小文件大小过滤。
        /// </summary>
        private static List<string> CollectFiles(ScanRule rule, string expandedPath)
        {
            var result = new List<string>();
            CollectFilesCore(expandedPath, rule.Pattern, rule.SubDirPattern,
                             rule.Recursive, rule.MinFileSizeBytes, result);
            return result;
        }

        private static void CollectFilesCore(
            string dirPath,
            string filePattern,
            string subDirPattern,
            bool recursive,
            long minBytes,
            List<string> result)
        {
            try
            {
                // ── 收集当前目录的文件 ──────────────────────────────────────────
                foreach (var file in Directory.EnumerateFiles(dirPath))
                {
                    try
                    {
                        if (!MatchesFilePattern(file, filePattern)) continue;
                        if (minBytes > 0 && new FileInfo(file).Length < minBytes) continue;
                        result.Add(file);
                    }
                    catch { /* 单文件权限问题跳过 */ }
                }

                // ── 递归子目录 ──────────────────────────────────────────────────
                if (!recursive) return;

                foreach (var sub in Directory.EnumerateDirectories(dirPath))
                {
                    try
                    {
                        // 若设置了子目录过滤，只进入名称匹配的子目录
                        if (!string.IsNullOrEmpty(subDirPattern))
                        {
                            var dirName = Path.GetFileName(sub);
                            if (!MatchesWildcard(dirName, subDirPattern)) continue;
                        }
                        CollectFilesCore(sub, filePattern, subDirPattern,
                                         true, minBytes, result);
                    }
                    catch { /* 子目录权限不足，忽略 */ }
                }
            }
            catch { /* 目录权限不足或读取错误，整体忽略 */ }
        }

        /// <summary>
        /// 文件名过滤：
        ///   空字符串  → 全部通过
        ///   ".log"    → 扩展名精确匹配（大小写不敏感）
        ///   含 * 或 ? → 通配符匹配文件名
        ///   其他      → 文件名包含匹配（大小写不敏感）
        /// </summary>
        private static bool MatchesFilePattern(string filePath, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return true;

            var name = Path.GetFileName(filePath);

            // 扩展名精确匹配，如 ".log"
            if (pattern.StartsWith('.') && !pattern.Contains('*') && !pattern.Contains('?'))
                return string.Equals(
                    Path.GetExtension(filePath), pattern,
                    StringComparison.OrdinalIgnoreCase);

            // 通配符模式，如 "*.tmp" 或 "thumbcache_*.db"
            if (pattern.Contains('*') || pattern.Contains('?'))
                return MatchesWildcard(name, pattern);

            // 文件名包含匹配，如 "thumbcache_"
            return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>通配符匹配，支持 * 和 ?（大小写不敏感）</summary>
        private static bool MatchesWildcard(string name, string pattern)
        {
            return WildcardMatchCore(
                name.ToLowerInvariant(), 0,
                pattern.ToLowerInvariant(), 0);
        }

        private static bool WildcardMatchCore(
            string name, int ni, string pattern, int pi)
        {
            while (pi < pattern.Length)
            {
                char pc = pattern[pi];
                if (pc == '*')
                {
                    while (pi < pattern.Length && pattern[pi] == '*') pi++;
                    if (pi == pattern.Length) return true;
                    for (int i = ni; i <= name.Length; i++)
                        if (WildcardMatchCore(name, i, pattern, pi)) return true;
                    return false;
                }
                else if (pc == '?')
                {
                    if (ni >= name.Length) return false;
                    ni++; pi++;
                }
                else
                {
                    if (ni >= name.Length || name[ni] != pc) return false;
                    ni++; pi++;
                }
            }
            return ni == name.Length;
        }
    }
}
