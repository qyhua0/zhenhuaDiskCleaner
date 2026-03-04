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
                new PropertyMetadata(null, (d, e) => ((TreemapControl)d).Rebuild()));
        public static readonly DependencyProperty HighlightedNodeProperty =
            DependencyProperty.Register(nameof(HighlightedNode), typeof(FileNode), typeof(TreemapControl),
                new PropertyMetadata(null, (d, e) => ((TreemapControl)d).Render()));
        public static readonly DependencyProperty SelectedNodeProperty =
            DependencyProperty.Register(nameof(SelectedNode), typeof(FileNode), typeof(TreemapControl),
                new PropertyMetadata(null));

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

        public TreemapControl()
        {
            _visuals = new VisualCollection(this);
            _visuals.Add(_visual);
            MouseMove           += OnMove;
            MouseLeave          += OnLeave;
            MouseLeftButtonDown += OnClick;
            SizeChanged         += (_, _) => Rebuild();
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
            foreach (var n in list) { DrawNode(dc, n); if (n.Children.Count > 0) DrawList(dc, n.Children); }
        }

        private void DrawNode(DrawingContext dc, TreemapNode node)
        {
            var b = node.Bounds;
            if (b.Width < 1 || b.Height < 1) return;
            var fn = node.FileNode;

            WpfColor col = fn.IsDirectory
                ? WpfColor.FromRgb(0x35, 0x35, 0x50)
                : fn.TypeColor;

            if (node.Level > 0)
                col = WpfColor.FromArgb((byte)Math.Max(40, 120 - node.Level * 20), col.R, col.G, col.B);
            if (fn == HighlightedNode)
                col = Colors.Gold;
            else if (node == _hovered)
                col = WpfColor.FromRgb(
                    (byte)Math.Min(255, col.R + 50),
                    (byte)Math.Min(255, col.G + 50),
                    (byte)Math.Min(255, col.B + 50));

            dc.DrawRectangle(
                new SolidColorBrush(col),
                new Pen(new SolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x2E)), 1.5),
                new Rect(b.X + .5, b.Y + .5, Math.Max(1, b.Width - 1), Math.Max(1, b.Height - 1)));

            if (b.Width > 28 && b.Height > 14)
            {
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var ft = new FormattedText(fn.Name, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, _tf, Math.Min(11, b.Width / 8),
                    Brushes.White, dpi)
                { MaxTextWidth = b.Width - 4, MaxTextHeight = b.Height, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(ft, new Point(b.X + 2, b.Y + 2));

                if (b.Height > 28)
                {
                    var ft2 = new FormattedText(fn.SizeText, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, _tf, 9,
                        new SolidColorBrush(WpfColor.FromArgb(180, 255, 255, 255)), dpi)
                    { MaxTextWidth = b.Width - 4 };
                    dc.DrawText(ft2, new Point(b.X + 2, b.Y + 14));
                }
            }
        }

        private TreemapNode? Hit(Point p) => HitList(_nodes, p);
        private static TreemapNode? HitList(List<TreemapNode> list, Point p)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].Bounds.Contains(p)) continue;
                if (list[i].Children.Count > 0) { var c = HitList(list[i].Children, p); if (c != null) return c; }
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
                _tip.Content = h.FileNode.FullPath + "\n" + h.FileNode.SizeText;
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
