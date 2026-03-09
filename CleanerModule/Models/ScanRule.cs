namespace ZhenhuaDiskCleaner.CleanerModule.Models
{
    /// <summary>
    /// 扫描规则定义，对应 rules.json 中的一条配置项
    /// </summary>
    public class ScanRule
    {
        /// <summary>垃圾分类名称，显示在结果树的一级节点</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>分类图标（Emoji），用于 UI 展示</summary>
        public string Icon { get; set; } = "📁";

        /// <summary>
        /// 扫描目录路径，支持环境变量（如 %TEMP%、%SystemRoot%）
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// 可选：文件名过滤模式，支持三种形式：
        /// - 留空           → 收集所有文件
        /// - ".log"         → 按扩展名过滤（以 "." 开头）
        /// - "thumbcache_"  → 文件名包含匹配
        /// - "*.tmp"        → 通配符匹配（含 * 或 ?）
        /// </summary>
        public string Pattern { get; set; } = string.Empty;

        /// <summary>
        /// 可选：子目录名过滤模式。
        /// 填写后只递归进入名称匹配的子目录，其余子目录跳过。
        /// 例如：填写 "Image" 时只扫描名为 "Image" 的子目录。
        /// 支持通配符（* ?），例如 "Cache*"。
        /// 留空则递归所有子目录（默认行为不变）。
        /// </summary>
        public string SubDirPattern { get; set; } = string.Empty;

        /// <summary>是否递归扫描子目录</summary>
        public bool Recursive { get; set; } = true;

        /// <summary>
        /// 可选：文件大小下限（字节）。
        /// 填写后只收集大于等于此值的文件，0 表示不限制。
        /// 例如设为 1048576 可只收集大于 1 MB 的文件。
        /// </summary>
        public long MinFileSizeBytes { get; set; } = 0;

        /// <summary>
        /// 规则类型：
        /// "recommend"    → 推荐清理，安全无副作用
        /// "professional" → 专业清理，谨慎操作
        /// </summary>
        public string Type { get; set; } = "recommend";

        /// <summary>在 UI 中显示的规则说明</summary>
        public string Description { get; set; } = string.Empty;
    }
}
