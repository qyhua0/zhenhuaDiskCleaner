using System.Globalization;
using System.Windows;
using System.Windows.Data;
using WpfColor  = System.Windows.Media.Color;
using WpfBrush  = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => value is Visibility v && v == Visibility.Visible;
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is long size ? FileNode.FormatSize(size) : "0 B";
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    public class ColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            if (value is WpfColor col)
                return new System.Windows.Media.SolidColorBrush(col);
            return WpfBrushes.Gray;
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            bool isNull = value == null;
            bool invert = p?.ToString() == "invert";
            return (isNull ^ invert) ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    public class FileTypeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            if (value is FileNode node)
            {
                if (node.IsDirectory) return "📁";
                return node.FileType switch
                {
                    FileType.Image      => "🖼",
                    FileType.Video      => "🎬",
                    FileType.Audio      => "🎵",
                    FileType.Document   => "📄",
                    FileType.Archive    => "📦",
                    FileType.Executable => "⚙",
                    FileType.Code       => "💻",
                    _                   => "📎"
                };
            }
            return "📎";
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    public class BoolToNegateConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is bool b && !b;
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => value is bool b && !b;
    }
}
