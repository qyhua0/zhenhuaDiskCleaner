using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Services
{
    public static class FileClassifier
    {
        private static readonly System.Collections.Generic.Dictionary<string, FileType> _map =
            new System.Collections.Generic.Dictionary<string, FileType>(System.StringComparer.OrdinalIgnoreCase)
        {
            {".jpg",FileType.Image},{".jpeg",FileType.Image},{".png",FileType.Image},{".gif",FileType.Image},
            {".bmp",FileType.Image},{".webp",FileType.Image},{".svg",FileType.Image},{".ico",FileType.Image},
            {".tiff",FileType.Image},{".raw",FileType.Image},{".heic",FileType.Image},{".tif",FileType.Image},
            {".mp4",FileType.Video},{".avi",FileType.Video},{".mkv",FileType.Video},{".mov",FileType.Video},
            {".wmv",FileType.Video},{".flv",FileType.Video},{".webm",FileType.Video},{".m4v",FileType.Video},
            {".rmvb",FileType.Video},{".mpeg",FileType.Video},
            {".mp3",FileType.Audio},{".wav",FileType.Audio},{".flac",FileType.Audio},{".aac",FileType.Audio},
            {".ogg",FileType.Audio},{".wma",FileType.Audio},{".m4a",FileType.Audio},{".opus",FileType.Audio},
            {".txt",FileType.Document},{".pdf",FileType.Document},{".doc",FileType.Document},
            {".docx",FileType.Document},{".xls",FileType.Document},{".xlsx",FileType.Document},
            {".ppt",FileType.Document},{".pptx",FileType.Document},{".md",FileType.Document},
            {".rtf",FileType.Document},{".csv",FileType.Document},{".epub",FileType.Document},
            {".zip",FileType.Archive},{".rar",FileType.Archive},{".7z",FileType.Archive},
            {".tar",FileType.Archive},{".gz",FileType.Archive},{".bz2",FileType.Archive},
            {".xz",FileType.Archive},{".iso",FileType.Archive},{".cab",FileType.Archive},
            {".exe",FileType.Executable},{".msi",FileType.Executable},{".dll",FileType.Executable},
            {".bat",FileType.Executable},{".cmd",FileType.Executable},{".ps1",FileType.Executable},
            {".cs",FileType.Code},{".js",FileType.Code},{".ts",FileType.Code},{".py",FileType.Code},
            {".java",FileType.Code},{".cpp",FileType.Code},{".c",FileType.Code},{".h",FileType.Code},
            {".html",FileType.Code},{".css",FileType.Code},{".json",FileType.Code},{".xml",FileType.Code},
            {".sql",FileType.Code},{".go",FileType.Code},{".rs",FileType.Code},{".php",FileType.Code},
        };

        public static FileType Classify(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return FileType.Unknown;
            return _map.TryGetValue(extension, out var t) ? t : FileType.Unknown;
        }
    }
}
