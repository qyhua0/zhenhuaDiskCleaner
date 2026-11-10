using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WpfColor = System.Windows.Media.Color;

namespace ZhenhuaDiskCleaner.Models
{
    public class FileNode : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected;

        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;

        private long _size;

        /// <summary>
        /// 字节数。改这个值必须通知 UI：MainViewModel 的 Remove / ClearDirectory
        /// 会回写父链和自己的 Size，不通知的话左侧树的目录大小会一直显示旧值。
        /// 扫描阶段没有任何订阅者，事件为 null，开销可忽略。
        /// </summary>
        public long Size
        {
            get => _size;
            set
            {
                if (_size == value) return;
                _size = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SizeText));
            }
        }

        public bool IsDirectory { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime ModifiedTime { get; set; }
        public string Extension { get; set; } = string.Empty;
        public FileType FileType { get; set; }
        public ObservableCollection<FileNode> Children { get; set; } = new();
        public FileNode? Parent { get; set; }
        public int Depth { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string SizeText => FormatSize(Size);
        public string TypeText => IsDirectory ? "文件夹" : GetFileTypeText();
        public WpfColor TypeColor => GetTypeColor();

        private string GetFileTypeText() => GetTypeName(FileType);

        private WpfColor GetTypeColor() => GetColor(FileType);

        /// <summary>类型 → 显示名。供不需要构造整个节点的调用方使用。</summary>
        public static string GetTypeName(FileType t) => t switch
        {
            FileType.Image      => "图片",
            FileType.Video      => "视频",
            FileType.Audio      => "音频",
            FileType.Document   => "文档",
            FileType.Archive    => "压缩包",
            FileType.Executable => "程序",
            FileType.Code       => "代码",
            _                   => "其他"
        };

        /// <summary>
        /// 类型 → 主题色。饼图统计以前为了拿一个颜色而 new 一整个 FileNode
        /// （见 MainViewModel 的 BuildTypeStats），那个节点带一个 ObservableCollection，
        /// 白付一次分配；改成静态查表。
        /// </summary>
        public static WpfColor GetColor(FileType t) => t switch
        {
            FileType.Image      => WpfColor.FromRgb(76,  175, 80),
            FileType.Video      => WpfColor.FromRgb(244, 67,  54),
            FileType.Audio      => WpfColor.FromRgb(33,  150, 243),
            FileType.Document   => WpfColor.FromRgb(255, 193, 7),
            FileType.Archive    => WpfColor.FromRgb(156, 39,  176),
            FileType.Executable => WpfColor.FromRgb(255, 87,  34),
            FileType.Code       => WpfColor.FromRgb(0,   188, 212),
            _                   => WpfColor.FromRgb(158, 158, 158),
        };

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
            return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum FileType { Unknown, Image, Video, Audio, Document, Archive, Executable, Code }

    public class FileTypeStats
    {
        public FileType Type { get; set; }
        public string TypeName { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public int Count { get; set; }
        public double Percentage { get; set; }
        public WpfColor Color { get; set; }
        public string SizeText => FileNode.FormatSize(TotalSize);
    }

    public class DriveItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public long FreeSize { get; set; }
        public long UsedSize => TotalSize - FreeSize;
        public double UsedPercent => TotalSize > 0 ? (double)UsedSize / TotalSize * 100 : 0;
        public string TotalSizeText => FileNode.FormatSize(TotalSize);
        public string FreeSizeText => FileNode.FormatSize(FreeSize);
        public string UsedSizeText => FileNode.FormatSize(UsedSize);
        public DriveType DriveType { get; set; }
        public string Label { get; set; } = string.Empty;
        public string DisplayName =>
            $"{Name}{(string.IsNullOrEmpty(Label) ? "" : $" [{Label}]")} ({UsedSizeText}/{TotalSizeText})";
    }
}
