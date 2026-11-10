using System.Reflection;

namespace ZhenhuaDiskCleaner.Helpers
{
    /// <summary>
    /// 标题栏和关于框共用的固定文案与版本号，集中一处。
    /// 以前版本号写死在两处（About 里一个、标题栏里一个），csproj 升版后界面还挂着旧号；
    /// 现在版本一律从程序集读，升版只改 csproj 里的 Version 一处。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>程序名（中文）</summary>
        public const string Name = "振华磁盘清理工具";

        /// <summary>官网</summary>
        public const string Website = "https://diskcleaner.bloghua.com";

        /// <summary>联系邮箱</summary>
        public const string Email = "qyhua0@hotmail.com";

        /// <summary>开源协议</summary>
        public const string License = "GPL-3.0 license";

        /// <summary>运行环境要求</summary>
        public const string Requirements = "Windows 10/11  ·  .NET 8";

        /// <summary>版权行</summary>
        public const string Copyright = "© 2026 https://www.bloghua.com  保留所有权利";

        /// <summary>
        /// 版本号，读程序集的 InformationalVersion（也就是 csproj 里的 Version）。
        /// SourceLink 开启时它会带 +commit 后缀，切掉只留版本号。
        /// </summary>
        public static string Version { get; } = ResolveVersion();

        /// <summary>关于框副标题：ZhenHua Disk Cleaner  v{Version}</summary>
        public static string Subtitle { get; } = "ZhenHua Disk Cleaner  v" + Version;

        /// <summary>标题栏尾巴：v{Version} www.bloghua.com</summary>
        public static string TitleSuffix { get; } = "v" + Version + " www.bloghua.com";

        private static string ResolveVersion()
        {
            var asm = typeof(App).Assembly;
            var info = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .FirstOrDefault() as AssemblyInformationalVersionAttribute;

            return info?.InformationalVersion?.Split('+')[0]
                ?? asm.GetName().Version?.ToString(3)
                ?? "0.0.0";
        }
    }
}
