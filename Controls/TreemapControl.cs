using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZhenhuaDiskCleaner.Helpers;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Controls
{
    public class TreemapControl : Canvas
    {
        // ── 调色板：30种高饱和深色主题色 ──────────────────────────
        private static readonly Color[] Palette =
        {
            Color.FromRgb(229, 57,  53),  Color.FromRgb(156, 39, 176),
            Color.FromRgb(63,  81, 181),  Color.FromRgb(3,  169, 244),
            Color.FromRgb(0,  150, 136),  Color.FromRgb(76, 175,  80),
            Color.FromRgb(255,193,   7),  Color.FromRgb(255, 87,  34),
            Color.FromRgb(233, 30,  99),  Color.FromRgb(103, 58, 183),
            Color.FromRgb(33, 150, 243),  Color.FromRgb(0,  188, 212),
            Color.FromRgb(139,195,  74),  Color.FromRgb(255,152,   0),
            Color.FromRgb(121, 85,  72),  Color.FromRgb(96, 125, 139),
            Color.FromRgb(244, 67,  54),  Color.FromRgb(171, 71, 188),
            Color.FromRgb(92, 107, 192),  Color.FromRgb(38, 198, 218),
            Color.FromRgb(102,187, 106),  Color.FromRgb(255,238,  88),
            Color.FromRgb(255,112,  67),  Color.FromRgb(240, 98, 146),
            Color.FromRgb(126, 87, 194),  Color.FromRgb(66, 165, 245),
            Color.FromRgb(38, 166, 154),  Color.FromRgb(174,213,  79),
            Color.FromRgb(255,202,  40),  Color.FromRgb(255,138, 101),
        };

        // ── Dependency Properties ──────────────────────────────────
        public static readonly DependencyProperty RootNodeProperty =
            DependencyProperty.Register(nameof(RootNode), typeof(FileNode),
                typeof(TreemapControl),
                new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Rebuild()));

        public static readonly DependencyProperty HighlightedNodeProperty =
            DependencyProperty.Register(nameof(HighlightedNode), typeof(FileNode),
                typeof(TreemapControl),
                new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Render()));

        public static readonly DependencyProperty SelectedNodeProperty =
            DependencyProperty.Register(nameof(SelectedNode), typeof(FileNode),
                typeof(TreemapControl),
                new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Render()));

        public FileNode? RootNode
        {
            get => (FileNode?)GetValue(RootNodeProperty);
            set => SetValue(RootNodeProperty, value);
        }
        public FileNode? HighlightedNode
        {
            get => (FileNode?)GetValue(HighlightedNodeProperty);
            set => SetValue(HighlightedNodeProperty, value);
        }
        public FileNode? SelectedNode
        {
            get => (FileNode?)GetValue(SelectedNodeProperty);
            set => SetValue(SelectedNodeProperty, value);
        }

        public event Action<FileNode>? NodeClicked;
        public event Action<FileNode>? NodeHovered;

        private List<TreemapNode> _nodes = new();
        private TreemapNode? _hovered;
        private readonly DrawingVisual _visual = new();
        private readonly VisualCollection _visuals;
        private readonly ToolTip _tip = new();
        private static readonly Typeface _tf = new("Segoe UI");

        // 颜色缓存：同一文件每次渲染颜色一致
        private readonly Dictionary<FileNode, Color> _colorCache = new();

        public TreemapControl()
        {
            _visuals = new VisualCollection(this);
            _visuals.Add(_visual);
            MouseMove += OnMove;
            MouseLeave += OnLeave;
            MouseLeftButtonDown += OnClick;
            SizeChanged += (_, _) => Rebuild();
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
            ToolTip = _tip;
        }

        protected override int VisualChildrenCount => _visuals.Count;
        protected override Visual GetVisualChild(int index) => _visuals[index];

        // ── 颜色分配：按文件路径哈希映射到调色板 ─────────────────
        private Color GetNodeColor(FileNode fn)
        {
            if (_colorCache.TryGetValue(fn, out var c)) return c;
            int idx = Math.Abs(fn.FullPath.GetHashCode()) % Palette.Length;
            c = Palette[idx];
            _colorCache[fn] = c;
            return c;
        }

        private void Rebuild()
        {
            _colorCache.Clear();
            if (RootNode == null || ActualWidth < 4 || ActualHeight < 4)
            { _nodes.Clear(); Render(); return; }

            // 铺满整个容器，无边距
            _nodes = TreemapAlgorithm.Build(RootNode,
                new Rect(0, 0, ActualWidth, ActualHeight));
            Render();
        }

        private void Render()
        {
            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)), null,
                new Rect(0, 0, ActualWidth, ActualHeight));

            if (_nodes.Count == 0) return;

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            foreach (var node in _nodes)
                DrawNode(dc, node, dpi);
        }

        private void DrawNode(DrawingContext dc, TreemapNode node, double dpi)
        {
            var b = node.Bounds;
            if (b.Width < 1 || b.Height < 1) return;
            var fn = node.FileNode;

            Color col = GetNodeColor(fn);

            // 选中：亮金边 + 提亮
            bool isSelected = fn == SelectedNode ||
                              IsDescendantOf(SelectedNode, fn);
            // 悬停：略微提亮
            bool isHovered = node == _hovered;

            if (isSelected)
                col = Lighten(col, 60);
            else if (isHovered)
                col = Lighten(col, 30);

            // 1px 深色间隔线，视觉分隔
            var rect = new Rect(
                b.X + 0.5, b.Y + 0.5,
                Math.Max(1, b.Width - 1),
                Math.Max(1, b.Height - 1));

            var borderPen = new Pen(
                new SolidColorBrush(isSelected
                    ? Color.FromRgb(0xFF, 0xD7, 0x00)   // 金色
                    : Color.FromRgb(0x1E, 0x1E, 0x2E)),
                isSelected ? 2 : 1);

            dc.DrawRectangle(new SolidColorBrush(col), borderPen, rect);

            // 文字：块足够大时才显示
            if (b.Width > 32 && b.Height > 16)
            {
                // 文件名
                var ft = new FormattedText(
                    fn.Name,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _tf,
                    Math.Clamp(b.Height / 4.0, 9, 12),
                    Brushes.White, dpi)
                {
                    MaxTextWidth = b.Width - 4,
                    MaxTextHeight = b.Height - 2,
                    Trimming = TextTrimming.CharacterEllipsis
                };
                dc.DrawText(ft, new Point(b.X + 2, b.Y + 2));

                // 大小
                if (b.Height > 30)
                {
                    var ft2 = new FormattedText(
                        fn.SizeText,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        _tf, 9,
                        new SolidColorBrush(
                            Color.FromArgb(200, 255, 255, 255)), dpi)
                    { MaxTextWidth = b.Width - 4 };
                    dc.DrawText(ft2, new Point(b.X + 2, b.Y + ft.Height + 2));
                }
            }
        }

        // 判断 target 是否是 ancestor 的后代（用于目录选中时高亮其下文件）
        private static bool IsDescendantOf(FileNode? ancestor, FileNode node)
        {
            if (ancestor == null) return false;
            var cur = node.Parent;
            while (cur != null)
            {
                if (cur == ancestor) return true;
                cur = cur.Parent;
            }
            return false;
        }

        private static Color Lighten(Color c, int amount) =>
            Color.FromRgb(
                (byte)Math.Min(255, c.R + amount),
                (byte)Math.Min(255, c.G + amount),
                (byte)Math.Min(255, c.B + amount));

        // ── 命中测试 ──────────────────────────────────────────────
        private TreemapNode? Hit(Point p)
        {
            // 从后往前找最小覆盖块
            for (int i = _nodes.Count - 1; i >= 0; i--)
                if (_nodes[i].Bounds.Contains(p)) return _nodes[i];
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
                _tip.Content =
                    $"{h.FileNode.Name}\n" +
                    $"{h.FileNode.SizeText}\n" +
                    $"{h.FileNode.FullPath}";
                _tip.IsOpen = false;
                _tip.IsOpen = true;
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