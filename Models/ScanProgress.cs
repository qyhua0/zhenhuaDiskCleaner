using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZhenhuaDiskCleaner.Models
{
    public class ScanProgress : INotifyPropertyChanged
    {
        private long _scannedFiles;
        private long _scannedSize;
        private long _totalSize;
        private string _currentPath = string.Empty;
        private TimeSpan _elapsed;
        private bool _isCompleted;

        public long ScannedFiles
        {
            get => _scannedFiles;
            set { _scannedFiles = value; OnPropertyChanged(); OnPropertyChanged(nameof(Percentage)); }
        }
        public long ScannedSize
        {
            get => _scannedSize;
            set { _scannedSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScannedSizeText)); OnPropertyChanged(nameof(Percentage)); }
        }
        public long TotalSize
        {
            get => _totalSize;
            set { _totalSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalSizeText)); OnPropertyChanged(nameof(Percentage)); }
        }
        public string CurrentPath
        {
            get => _currentPath;
            set { _currentPath = value; OnPropertyChanged(); }
        }
        public TimeSpan Elapsed
        {
            get => _elapsed;
            set { _elapsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedText)); }
        }
        public bool IsCompleted
        {
            get => _isCompleted;
            set { _isCompleted = value; OnPropertyChanged(); }
        }

        public double Percentage => TotalSize > 0 ? Math.Min(100, (double)ScannedSize / TotalSize * 100) : 0;
        public string ScannedSizeText => FileNode.FormatSize(ScannedSize);
        public string TotalSizeText   => FileNode.FormatSize(TotalSize);
        public string ElapsedText     => $"{(int)Elapsed.TotalMinutes:D2}:{Elapsed.Seconds:D2}";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class HashResult
    {
        public string MD5    { get; set; } = string.Empty;
        public string SHA1   { get; set; } = string.Empty;
        public string SHA256 { get; set; } = string.Empty;
        public string SHA512 { get; set; } = string.Empty;
    }
}
