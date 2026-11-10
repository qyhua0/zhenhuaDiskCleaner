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

        /// <summary>
        /// 目录枚举选项：忽略无权限/被占用的项，每条规则复用同一个实例。
        /// AttributesToSkip 用默认值（Hidden | System），和以前
        /// Directory.EnumerateFiles(path) 的行为一致。
        /// </summary>
        private static readonly EnumerationOptions EnumOptions = new() { IgnoreInaccessible = true };

        // ── 公开方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 异步启动扫描。
        /// 加载规则 → 解析路径 → 并行扫描 → 汇总结果
        /// </summary>
        public async Task ScanAsync()
        {
            // 先 Cancel 再 Dispose：只 Cancel 会让旧 CTS 的注册和内核句柄一直留着
            _cts?.Cancel();
            _cts?.Dispose();
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
        public void Cancel() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }

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

            // 匹配器按规则编译一次，不要在每个文件上重新解析 Pattern
            var matcher = FileMatcher.Build(rule.Pattern);

            var files = CollectFiles(rule, expandedPath, matcher);
            if (files.Count == 0)
                return null;

            // 大小直接取枚举时已经拿到的值，不再 new FileInfo() 二次 stat
            long totalSize = 0;
            var entries = new List<FileEntry>(files.Count);
            var paths = new List<string>(files.Count);

            foreach (var (fullPath, length, name) in files)
            {
                totalSize += length;
                entries.Add(new FileEntry
                {
                    FullPath = fullPath,
                    FileName = name,
                    Size     = length,
                    Category = rule.Name,
                });
                paths.Add(fullPath);
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
                Files       = paths,
                FileEntries = entries,
                // 推荐类型默认勾选，专业类型默认不勾选，与文档 5.2/5.3 保持一致
                IsChecked   = rule.Type == "recommend",
            };
        }

        /// <summary>
        /// 收集目录下的文件列表（路径 + 大小 + 文件名）。
        /// 支持按文件名 Pattern、子目录名 SubDirPattern 和最小文件大小过滤。
        /// 用显式栈而不是递归：日志/缓存目录的层级是可以上百的，递归会爆栈，
        /// 而 StackOverflow 不能被 catch，会直接带走进程。
        /// </summary>
        private static List<(string FullPath, long Length, string Name)> CollectFiles(
            ScanRule rule, string expandedPath, FileMatcher matcher)
        {
            var result = new List<(string, long, string)>();
            var dirs = new Stack<string>();
            dirs.Push(expandedPath);

            while (dirs.Count > 0)
            {
                DirectoryInfo di;
                try { di = new DirectoryInfo(dirs.Pop()); }
                catch { continue; }

                // ── 当前目录的文件 ──────────────────────────────────────────────
                // 用 DirectoryInfo.EnumerateFiles 拿 FileInfo：Length/Name 已经在
                // FindFirstFile/FindNextFile 的返回结构里，不需要再为每个文件
                // 调一次 GetFileAttributesEx（原来 new FileInfo(file).Length 就是这个）。
                try
                {
                    foreach (var fi in di.EnumerateFiles("*", EnumOptions))
                    {
                        if (!matcher.IsMatch(fi.Name, fi.Extension)) continue;
                        if (rule.MinFileSizeBytes > 0 && fi.Length < rule.MinFileSizeBytes) continue;
                        result.Add((fi.FullName, fi.Length, fi.Name));
                    }
                }
                catch { /* 目录权限不足或读取错误，跳过这一层 */ }

                if (!rule.Recursive) continue;

                // ── 子目录 ─────────────────────────────────────────────────────
                try
                {
                    foreach (var sub in di.EnumerateDirectories("*", EnumOptions))
                    {
                        // 若设置了子目录过滤，只进入名称匹配的子目录
                        if (!string.IsNullOrEmpty(rule.SubDirPattern) &&
                            !MatchesWildcard(sub.Name, rule.SubDirPattern)) continue;
                        dirs.Push(sub.FullName);
                    }
                }
                catch { /* 子目录权限不足，忽略 */ }
            }

            return result;
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

        /// <summary>文件名匹配策略（按 Pattern 预编译）</summary>
        private enum MatchKind { All, Extension, Contains, Prefix, Suffix, PrefixSuffix, Glob }

        /// <summary>
        /// 把规则的 Pattern 预编译成一次判断。
        /// 原来每个文件都走 MatchesWildcard：两次 ToLowerInvariant（两次字符串分配）
        /// 加一次回溯递归；%TEMP% 这类目录动辄几万文件 × 十几条规则。
        /// 内置规则全部是 "*.ext" / "prefix*.ext" / ".ext" 三种形态，
        /// 都能退化成 StartsWith/EndsWith，只有真正复杂的模式才回退到递归匹配。
        /// </summary>
        private readonly struct FileMatcher
        {
            private readonly MatchKind _kind;
            private readonly string _text;    // 扩展名 / 包含串 / 前缀 / "前缀*后缀"
            private readonly string _suffix;  // 仅 PrefixSuffix 用

            private FileMatcher(MatchKind kind, string text, string suffix = "")
            {
                _kind = kind; _text = text; _suffix = suffix;
            }

            public static FileMatcher Build(string pattern)
            {
                if (string.IsNullOrEmpty(pattern))
                    return new FileMatcher(MatchKind.All, "");

                // 扩展名精确匹配，如 ".log"
                if (pattern.StartsWith('.') && !pattern.Contains('*') && !pattern.Contains('?'))
                    return new FileMatcher(MatchKind.Extension, pattern);

                // 文件名包含匹配，如 "thumbcache_"
                if (!pattern.Contains('*') && !pattern.Contains('?'))
                    return new FileMatcher(MatchKind.Contains, pattern);

                // 只有一个 * 且没有 ?：退化成前缀/后缀判断
                if (pattern.IndexOf('?') < 0 && pattern.IndexOf('*') == pattern.LastIndexOf('*'))
                {
                    int star = pattern.IndexOf('*');
                    var prefix = pattern.Substring(0, star);
                    var suffix = pattern.Substring(star + 1);

                    if (prefix.Length == 0) return new FileMatcher(MatchKind.Suffix, suffix);
                    if (suffix.Length == 0) return new FileMatcher(MatchKind.Prefix, prefix);
                    return new FileMatcher(MatchKind.PrefixSuffix, prefix, suffix);
                }

                return new FileMatcher(MatchKind.Glob, pattern);
            }

            /// <summary>name = 文件名，extension = 带点的扩展名（可为空）</summary>
            public bool IsMatch(string name, string extension) => _kind switch
            {
                MatchKind.All         => true,
                MatchKind.Extension   => string.Equals(extension, _text, StringComparison.OrdinalIgnoreCase),
                MatchKind.Contains    => name.Contains(_text, StringComparison.OrdinalIgnoreCase),
                MatchKind.Prefix      => name.StartsWith(_text, StringComparison.OrdinalIgnoreCase),
                MatchKind.Suffix      => name.EndsWith(_text, StringComparison.OrdinalIgnoreCase),
                MatchKind.PrefixSuffix=> name.StartsWith(_text, StringComparison.OrdinalIgnoreCase)
                                        && name.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase)
                                        && name.Length >= _text.Length + _suffix.Length,
                _                     => MatchesWildcard(name, _text),
            };
        }
    }
}
