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

        // 两张 DrawingVisual：色块层只在布局/选中变化时重画，悬停层只画一个框。
        // 以前鼠标每移动一格、只要跨过一个块，就要把几千个块连同 FormattedText
        // 重新画一遍（DrawNode 里每个块 new 1-2 个 FormattedText）—— 鼠标抖动很明。
        private readonly DrawingVisual _cellsVisual = new();
        private readonly DrawingVisual _overlayVisual = new();
        private readonly VisualCollection _visuals;
        private readonly ToolTip _tip = new();
        private static readonly Typeface _tf = new("Segoe UI");

        // 颜色缓存：同一文件每次渲染颜色一致
        private readonly Dictionary<FileNode, Color> _colorCache = new();
        private FileNode? _cachedColorRoot;

        // 画刷/画笔缓存：以前 DrawNode 给每个块 new SolidColorBrush + new Pen（ Pen 里再 new 一个），
        // 几千个块就是每帧上万次分配；冻结后缓存，颜色集合最多 Palette.Length × 2 种。
        private readonly Dictionary<Color, Brush> _brushCache = new();
        private static readonly Brush BgBrush = Frozen(Color.FromRgb(0x1E, 0x1E, 0x2E));
        private static readonly Brush SizeFgBrush = Frozen(Color.FromArgb(200, 255, 255, 255));
        private static readonly Pen CellPen = FrozenPen(Color.FromRgb(0x1E, 0x1E, 0x2E), 1);
        private static readonly Pen SelectedPen = FrozenPen(Color.FromRgb(0xFF, 0xD7, 0x00), 2);
        private static readonly Pen HoverPen = FrozenPen(Color.FromArgb(220, 255, 255, 255), 2);
        private static readonly Brush HoverFill = Frozen(Color.FromArgb(45, 255, 255, 255));

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static Pen FrozenPen(Color c, double w) { var p = new Pen(Frozen(c), w); p.Freeze(); return p; }

        private Brush GetBrush(Color c)
        {
            if (!_brushCache.TryGetValue(c, out var b)) { b = Frozen(c); _brushCache[c] = b; }
            return b;
        }

        // 选中节点的深度，用于 IsDescendant 判断时剪枝（以前每个块都要走到根）
        private int _selectedDepth = int.MaxValue;

        public TreemapControl()
        {
            _visuals = new VisualCollection(this);
            _visuals.Add(_cellsVisual);
            _visuals.Add(_overlayVisual);
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
            // 用 & int.MaxValue 而不是 Math.Abs：GetHashCode 可以返回 int.MinValue，
            // 对它取绝对值仍然是负数 → 取模得到负下标 → IndexOutOfRangeException。
            // 概率极低（每个路径 1/2^32），但整盘百万文件下属于“迟早会碰”的崩溃。
            int idx = (fn.FullPath.GetHashCode() & int.MaxValue) % Palette.Length;
            c = Palette[idx];
            _colorCache[fn] = c;
            return c;
        }

        private void Rebuild()
        {
            // 颜色缓存只在“换了根节点（重新扫描）”时清空。
            // 以前无条件 Clear，而 SizeChanged 也走 Rebuild → 拖动窗口时
            // 不仅全量重算哈希，同文件的颜色还会跳变（缓存本就是为了颜色稳定）。
            if (!ReferenceEquals(_cachedColorRoot, RootNode))
            {
                _colorCache.Clear();
                _cachedColorRoot = RootNode;
            }

            if (RootNode == null || ActualWidth < 4 || ActualHeight < 4)
            { _nodes.Clear(); Render(); return; }

            // 铺满整个容器，无边距
            _nodes = TreemapAlgorithm.Build(RootNode,
                new Rect(0, 0, ActualWidth, ActualHeight));
            Render();
        }

        /// <summary>全量重画：色块层 + 悬停层。数据、尺寸、选中变化时调用。</summary>
        private void Render()
        {
            RenderCells();
            RenderOverlay();
        }

        private void RenderCells()
        {
            using var dc = _cellsVisual.RenderOpen();
            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

            if (_nodes.Count == 0) return;

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _selectedDepth = SelectedNode?.Depth ?? int.MaxValue;

            foreach (var node in _nodes)
                DrawNode(dc, node, dpi);
        }

        /// <summary>只重画悬停高亮。鼠标移动的热路径走这里，代价恒定。</summary>
        private void RenderOverlay()
        {
            using var dc = _overlayVisual.RenderOpen();
            if (_hovered == null) return;

            var b = _hovered.Bounds;
            if (b.Width < 1 || b.Height < 1) return;

            dc.DrawRectangle(HoverFill, HoverPen, new Rect(
                b.X + 1, b.Y + 1,
                Math.Max(1, b.Width - 2),
                Math.Max(1, b.Height - 2)));
        }

        private void DrawNode(DrawingContext dc, TreemapNode node, double dpi)
        {
            var b = node.Bounds;
            if (b.Width < 1 || b.Height < 1) return;
            var fn = node.FileNode;

            Color col = GetNodeColor(fn);

            // 选中（包括选中目录下的所有子块）：亮金边 + 提亮。
            // 悬停不再改填充色，改由 _overlayVisual 画一个白框：
            // 这样鼠标移动时不需要重画整个色块层。
            // 祖先判断用 Depth 剪枝：只有深度≥选中节点的才可能在其子树里，
            // 否则每块都要沿 Parent 走到根（3000 块×平均 8 层）。
            bool isSelected = false;
            for (var cur = fn; cur != null && cur.Depth >= _selectedDepth; cur = cur.Parent)
                if (cur == SelectedNode) { isSelected = true; break; }

            if (isSelected)
                col = Lighten(col, 60);

            // 1px 深色间隔线，视觉分隔
            var rect = new Rect(
                b.X + 0.5, b.Y + 0.5,
                Math.Max(1, b.Width - 1),
                Math.Max(1, b.Height - 1));

            dc.DrawRectangle(GetBrush(col), isSelected ? SelectedPen : CellPen, rect);

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
                        SizeFgBrush, dpi)
                    { MaxTextWidth = b.Width - 4 };
                    dc.DrawText(ft2, new Point(b.X + 2, b.Y + ft.Height + 2));
                }
            }
        }

        private static Color Lighten(Color c, int amount) =>
            Color.FromRgb(
                (byte)Math.Min(255, c.R + amount),
                (byte)Math.Min(255, c.G + amount),
                (byte)Math.Min(255, c.B + amount));

        // ── 命中测试 ──────────────────────────────────────────────
        private TreemapNode? Hit(Point p)
        {
            // 鼠标移动是局部的：先测上一块，命中就直接返回（大多数移动只做一次比较）
            if (_hovered != null && _hovered.Bounds.Contains(p)) return _hovered;

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
            RenderOverlay();   // 不是 Render()：悬停只影响高亮层
        }

        private void OnLeave(object s, MouseEventArgs e)
        { _hovered = null; _tip.IsOpen = false; RenderOverlay(); }

        private void OnClick(object s, MouseButtonEventArgs e)
        {
            var h = Hit(e.GetPosition(this));
            if (h == null) return;
            SelectedNode = h.FileNode;
            NodeClicked?.Invoke(h.FileNode);
        }
    }
}