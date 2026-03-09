namespace ZhenhuaDiskCleaner.MigratorModule.Models
{
    public enum MigLogLevel { Info, Success, Warning, Error }

    /// <summary>迁移过程日志条目</summary>
    public class MigrationLogEntry
    {
        public DateTime    Time    { get; } = DateTime.Now;
        public MigLogLevel Level   { get; init; }
        public string      Message { get; init; } = "";

        public string TimeText  => Time.ToString("HH:mm:ss");
        public string LevelIcon => Level switch
        {
            MigLogLevel.Success => "✔",
            MigLogLevel.Warning => "⚠",
            MigLogLevel.Error   => "✖",
            _                   => "·",
        };
    }

    /// <summary>单个文件夹迁移结果</summary>
    public class MigrationResult
    {
        public string FolderName    { get; init; } = "";
        public string SourcePath    { get; init; } = "";
        public string TargetPath    { get; init; } = "";
        public bool   Success       { get; init; }
        public string ErrorMessage  { get; init; } = "";
        public long   BytesMigrated { get; init; }
    }
}
