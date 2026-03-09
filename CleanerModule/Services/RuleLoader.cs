using System.Reflection;
using System.Text.Json;
using ZhenhuaDiskCleaner.CleanerModule.Models;

namespace ZhenhuaDiskCleaner.CleanerModule.Services
{
    /// <summary>
    /// 规则加载器：从嵌入资源 rules.json 加载扫描规则
    /// </summary>
    public static class RuleLoader
    {
        private const string ResourceName = "ZhenhuaDiskCleaner.CleanerModule.Resources.rules.json";

        /// <summary>加载所有扫描规则</summary>
        public static List<ScanRule> LoadRules()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(ResourceName);
                if (stream == null)
                    return GetFallbackRules();

                using var reader = new System.IO.StreamReader(stream);
                var json = reader.ReadToEnd();
                var rules = JsonSerializer.Deserialize<List<ScanRule>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return rules ?? GetFallbackRules();
            }
            catch
            {
                return GetFallbackRules();
            }
        }

        /// <summary>
        /// 内置兜底规则：当 JSON 文件无法读取时使用，保证最基本功能可用
        /// </summary>
        private static List<ScanRule> GetFallbackRules() => new()
        {
            new ScanRule
            {
                Name        = "用户临时文件",
                Icon        = "🗂",
                Path        = "%TEMP%",
                Recursive   = true,
                Type        = "recommend",
                Description = "Windows 用户临时目录"
            },
            new ScanRule
            {
                Name        = "系统临时文件",
                Icon        = "🗂",
                Path        = "%SystemRoot%\\Temp",
                Recursive   = true,
                Type        = "recommend",
                Description = "Windows 系统临时目录"
            },
            new ScanRule
            {
                Name        = "Windows 更新缓存",
                Icon        = "🔄",
                Path        = "%SystemRoot%\\SoftwareDistribution\\Download",
                Recursive   = true,
                Type        = "recommend",
                Description = "Windows 更新下载缓存"
            },
            new ScanRule
            {
                Name           = "预取文件",
                Icon           = "⚡",
                Path           = "%SystemRoot%\\Prefetch",
                Pattern        = "*.pf",
                Recursive      = false,
                Type           = "recommend",
                Description    = "Windows 程序预取文件"
            },
            new ScanRule
            {
                Name        = "系统日志文件",
                Icon        = "📋",
                Path        = "%SystemRoot%\\Logs",
                Pattern     = "*.log",
                Recursive   = true,
                Type        = "professional",
                Description = "Windows 系统日志"
            },
            new ScanRule
            {
                Name        = "崩溃转储文件",
                Icon        = "💥",
                Path        = "%SystemRoot%\\Minidump",
                Recursive   = false,
                Type        = "professional",
                Description = "程序崩溃时生成的内存转储文件"
            },
        };
    }
}
