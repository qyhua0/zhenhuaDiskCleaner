namespace ZhenhuaDiskCleaner.CleanerModule.Services
{
    /// <summary>
    /// 磁盘信息服务：读取系统盘（C:）的容量与空间数据，供界面顶部面板展示。
    /// 本服务为纯只读查询，不修改任何文件系统状态。
    /// </summary>
    public static class DiskInfoService
    {
        /// <summary>
        /// 获取系统盘（C:\\）的磁盘信息快照。
        /// </summary>
        public static DiskSnapshot GetSystemDiskInfo()
        {
            try
            {
                var drive = new DriveInfo("C");
                return new DiskSnapshot
                {
                    DriveName    = drive.Name,
                    VolumeLabel  = string.IsNullOrEmpty(drive.VolumeLabel) ? "系统盘" : drive.VolumeLabel,
                    TotalBytes   = drive.TotalSize,
                    FreeBytes    = drive.AvailableFreeSpace,
                    IsReady      = drive.IsReady,
                };
            }
            catch
            {
                return new DiskSnapshot { DriveName = "C:\\", IsReady = false };
            }
        }
    }

    /// <summary>磁盘信息快照（值对象，不含通知机制）</summary>
    public class DiskSnapshot
    {
        public string DriveName    { get; init; } = string.Empty;
        public string VolumeLabel  { get; init; } = string.Empty;
        public long   TotalBytes   { get; init; }
        public long   FreeBytes    { get; init; }
        public bool   IsReady      { get; init; }

        public long   UsedBytes    => TotalBytes - FreeBytes;
        public double UsedPercent  => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100 : 0;
        public double FreePercent  => TotalBytes > 0 ? (double)FreeBytes / TotalBytes * 100 : 0;

        public string TotalText    => Format(TotalBytes);
        public string FreeText     => Format(FreeBytes);
        public string UsedText     => Format(UsedBytes);

        private static string Format(long bytes)
        {
            if (bytes < 0)            return "—";
            if (bytes < 1024)         return $"{bytes} B";
            if (bytes < 1024 * 1024)  return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
            return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        }
    }
}
