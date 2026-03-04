using System.Windows;

namespace ZhenhuaDiskCleaner.Models
{
    public class TreemapNode
    {
        public FileNode FileNode { get; set; } = null!;
        public Rect Bounds { get; set; }
        public int Level { get; set; }
        public List<TreemapNode> Children { get; set; } = new();
    }
}
