using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZhenhuaDiskCleaner.Helpers;
using ZhenhuaDiskCleaner.Models;
using WpfColor = System.Windows.Media.Color;

namespace ZhenhuaDiskCleaner.Controls
{
    public class TreemapControl : Canvas
    {
        public static readonly DependencyProperty RootNodeProperty =
            DependencyProperty.Register(nameof(RootNode), typeof(FileNode), typeof(TreemapControl),
                new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Rebuild()));
        public static readonly DependencyProperty HighlightedNodeProperty =
            DependencyProperty.Register(nameof(HighlightedNode), typeof(FileNode), typeof(TreemapControl),
                new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Render()));
        public static readonly DependencyProperty SelectedNodeProperty =
            DependencyProperty.Register(nameof(SelectedNode), typeof(FileNode), typeof(TreemapControl),
                new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Render()));

        public FileNode? RootNode { get => (FileNode?)GetValue(RootNodeProperty); set => SetValue(RootNodeProperty, value); }
        public FileNode? HighlightedNode { get => (FileNode?)GetValue(HighlightedNodeProperty); set => SetValue(HighlightedNodeProperty, value); }
        public FileNode? SelectedNode { get => (FileNode?)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }

        public event Action<FileNode>? NodeClicked;
        public event Action<FileNode>? NodeHovered;

        private List<TreemapNode> _nodes = new();
        private TreemapNode? _hovered;
        private readonly DrawingVisual _visual = new();
        private readonly VisualCollection _visuals;
        private readonly ToolTip _tip = new();
        private static readonly Typeface _tf = new("Segoe UI");

        public TreemapControl()
        {
            _visuals = new VisualCollection(this);
            _visuals.Add(_visual);
            MouseMove += OnMove;
            MouseLeave += OnLeave;
            MouseLeftButtonDown += OnClick;
            SizeChanged += (_, _) => Rebuild();
            Background = new SolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x2E));
            ToolTip = _tip;
        }

        protected override int VisualChildrenCount => _visuals.Count;
        protected override Visual GetVisualChild(int index) => _visuals[index];

        private void Rebuild()
        {
            if (RootNode == null || ActualWidth < 4 || ActualHeight < 4)
            { _nodes.Clear(); Render(); return; }
            _nodes = TreemapAlgorithm.Build(RootNode, new Rect(2, 2, ActualWidth - 4, ActualHeight - 4));
            Render();
        }

        private void Render()
        {
            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(new SolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x2E)), null,
                new Rect(0, 0, ActualWidth, ActualHeight));
            DrawList(dc, _nodes);
        }

        private void DrawList(DrawingContext dc, List<TreemapNode> list)
        {
            foreach (var n in list)
            {
                DrawNode(dc, n);
                if (n.Children.Count > 0) DrawList(dc, n.Children);
            }
        }

        private void DrawNode(DrawingContext dc, TreemapNode node)
        {
            var b = node.Bounds;
            if (b.Width < 1 || b.Height < 1) return;
            var fn = node.FileNode;

            // 颜色：目录用深色背景，文件用类型颜色
            WpfColor col = fn.IsDirectory
                ? WpfColor.FromRgb(0x2A, 0x2A, 0x42)
                : fn.TypeColor;

            // 深层目录降低亮度
            if (fn.IsDirectory && node.Level > 0)
                col = WpfColor.FromRgb(
                    (byte)Math.Min(255, 0x2A + node.Level * 8),
                    (byte)Math.Min(255, 0x2A + node.Level * 8),
                    (byte)Math.Min(255, 0x42 + node.Level * 6));

            // 选中/悬停高亮
            bool isSelected = IsAncestorOrSelf(fn, SelectedNode);
            bool isHighlighted = IsAncestorOrSelf(fn, HighlightedNode);

            if (isSelected)
                col = WpfColor.FromRgb(
                    (byte)Math.Min(255, col.R + 80),
                    (byte)Math.Min(255, col.G + 60),
                    (byte)Math.Min(255, col.B + 20));
            else if (isHighlighted)
                col = WpfColor.FromRgb(
                    (byte)Math.Min(255, col.R + 50),
                    (byte)Math.Min(255, col.G + 40),
                    (byte)Math.Min(255, col.B + 15));
            else if (node == _hovered)
                col = WpfColor.FromRgb(
                    (byte)Math.Min(255, col.R + 30),
                    (byte)Math.Min(255, col.G + 30),
                    (byte)Math.Min(255, col.B + 30));

            var borderCol = isSelected
                ? WpfColor.FromRgb(0xFF, 0xC1, 0x07)  // 金色边框标记选中
                : WpfColor.FromRgb(0x1E, 0x1E, 0x2E);

            dc.DrawRectangle(
                new SolidColorBrush(col),
                new Pen(new SolidColorBrush(borderCol), isSelected ? 2 : 1),
                new Rect(b.X + .5, b.Y + .5, Math.Max(1, b.Width - 1), Math.Max(1, b.Height - 1)));

            if (b.Width > 24 && b.Height > 12)
            {
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var ft = new FormattedText(fn.Name, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, _tf, Math.Min(11, b.Width / 9),
                    Brushes.White, dpi)
                { MaxTextWidth = b.Width - 4, MaxTextHeight = b.Height - 2, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(ft, new Point(b.X + 2, b.Y + 2));

                if (b.Height > 26)
                {
                    var ft2 = new FormattedText(fn.SizeText, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, _tf, 9,
                        new SolidColorBrush(WpfColor.FromArgb(200, 255, 255, 255)), dpi)
                    { MaxTextWidth = b.Width - 4 };
                    dc.DrawText(ft2, new Point(b.X + 2, b.Y + 14));
                }
            }
        }

        // 判断节点是否是目标节点的祖先或自身，用于高亮整个路径
        private static bool IsAncestorOrSelf(FileNode node, FileNode? target)
        {
            if (target == null) return false;
            var cur = target;
            while (cur != null)
            {
                if (cur == node) return true;
                cur = cur.Parent;
            }
            return false;
        }

        private TreemapNode? Hit(Point p) => HitList(_nodes, p);

        private static TreemapNode? HitList(List<TreemapNode> list, Point p)
        {
            // 从后往前（最小/最深的节点优先）
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].Bounds.Contains(p)) continue;
                if (list[i].Children.Count > 0)
                {
                    var c = HitList(list[i].Children, p);
                    if (c != null) return c;
                }
                return list[i];
            }
            return null;
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            var h = Hit(e.GetPosition(this));
            if (h == _hovered) return;
            _hovered = h;
            if (h != null)
            {
                NodeHovered?.Invoke(h.FileNode);
                _tip.Content = $"{h.FileNode.Name}\n{h.FileNode.SizeText}\n{h.FileNode.FullPath}";
                _tip.IsOpen = false; _tip.IsOpen = true;
            }
            else _tip.IsOpen = false;
            Render();
        }

        private void OnLeave(object s, MouseEventArgs e)
        { _hovered = null; _tip.IsOpen = false; Render(); }

        private void OnClick(object s, MouseButtonEventArgs e)
        {
            var h = Hit(e.GetPosition(this));
            if (h == null) return;
            SelectedNode = h.FileNode;
            NodeClicked?.Invoke(h.FileNode);
        }
    }
}