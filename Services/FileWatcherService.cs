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
                    EnableRaisingEvents = true
                };
                _watcher.Created += (s, e) => Changed?.Invoke(e.FullPath, WatcherChangeTypes.Created);
                _watcher.Deleted += (s, e) => Changed?.Invoke(e.FullPath, WatcherChangeTypes.Deleted);
                _watcher.Renamed += (s, e) => Changed?.Invoke(e.FullPath, WatcherChangeTypes.Renamed);
                _watcher.Changed += (s, e) => Changed?.Invoke(e.FullPath, WatcherChangeTypes.Changed);
            }
            catch { }
        }

        public void Stop() { _watcher?.Dispose(); _watcher = null; }
        public void Dispose() => Stop();
    }
}
