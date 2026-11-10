using System.IO;

namespace ZhenhuaDiskCleaner.Services
{
    public class FileWatcherService : System.IDisposable
    {
        private FileSystemWatcher? _watcher;
        public event Action<string, WatcherChangeTypes>? Changed;

        public void Watch(string path)
        {
            Stop();
            try
            {
                _watcher = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    // 默认 8 KB，对「整盘 + 含子目录」完全不够：缓存一满，
                    // 后面所有事件都被静默丢弃（只多一个 InternalError），
                    // 而且不报错。上限推到 64 KB。
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Created += (s, e) => Changed?.Invoke(e.FullPath, WatcherChangeTypes.Created);
                _watcher.Deleted += (s, e) => Changed?.Invoke(e.FullPath, WatcherChangeTypes.Deleted);
                _watcher.Renamed += (s, e) => Changed?.Invoke(e.FullPath, WatcherChangeTypes.Renamed);
                _watcher.Changed += (s, e) => Changed?.Invoke(e.FullPath, WatcherChangeTypes.Changed);
            }
            catch
            {
                // 路径无效 / 无权限：这里不能抱，监视只是锦上添花的功能，
                // 失败不能让扫描结果不可用。
            }
        }

        public void Stop() { _watcher?.Dispose(); _watcher = null; }
        public void Dispose() => Stop();
    }
}
