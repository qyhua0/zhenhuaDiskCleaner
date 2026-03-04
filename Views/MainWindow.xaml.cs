using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ZhenhuaDiskCleaner.Models;
using ZhenhuaDiskCleaner.ViewModels;
using WpfColor = System.Windows.Media.Color;
using IoPath = System.IO.Path;

namespace ZhenhuaDiskCleaner.Views
{
    public partial class MainWindow : Window
    {
        private MainViewModel VM => (MainViewModel)DataContext;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            TreemapView.NodeClicked += node => { VM.OnTreemapNodeClicked(node); SyncTree(node); };
            TreemapView.NodeHovered += node => VM.OnTreemapNodeHovered(node);


            VM.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(VM.FileTypeStats)) DrawPieChart();
                // TreeMap 反向联动：ViewModel 选中变化时同步到 TreeMap 和左侧树
                if (e.PropertyName == nameof(VM.SelectedNode) && VM.SelectedNode != null)
                {
                    TreemapView.SelectedNode = VM.SelectedNode;
                    TreemapView.HighlightedNode = VM.SelectedNode;
                    SyncTree(VM.SelectedNode);
                }
            };
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        { if (e.ClickCount == 2) ToggleMax(); else DragMove(); }
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMax();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void ToggleMax() =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void DirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FileNode n) { VM.SelectedNode = n; VM.HighlightedNode = n; }
        }

        private void SyncTree(FileNode target)
        {
            var chain = new List<FileNode>();
            for (var cur = target; cur != null; cur = cur.Parent) chain.Add(cur);
            chain.Reverse();
            ExpandChain(DirectoryTree.Items, chain, 0);
        }

        private bool ExpandChain(System.Collections.IEnumerable items, List<FileNode> chain, int depth)
        {
            if (depth >= chain.Count) return false;
            foreach (var item in items)
            {
                if (item is not FileNode fn || fn != chain[depth]) continue;
                var tvi = DirectoryTree.ItemContainerGenerator.ContainerFromItem(fn) as TreeViewItem;
                if (tvi == null) continue;
                tvi.IsExpanded = true;
                if (depth == chain.Count - 1) { tvi.IsSelected = true; tvi.BringIntoView(); return true; }
                tvi.UpdateLayout();
                return ExpandChain(tvi.Items, chain, depth + 1);
            }
            return false;
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        { if (VM.SelectedNode != null) VM.HighlightedNode = VM.SelectedNode; }

        private void StatsCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPieChart();

        private void DrawPieChart()
        {
            StatsChartCanvas.Children.Clear();
            var stats = VM?.FileTypeStats;
            if (stats == null || stats.Count == 0) return;
            double w = StatsChartCanvas.ActualWidth, h = StatsChartCanvas.ActualHeight;
            if (w < 40 || h < 40) return;

            double cx = w / 2, cy = h / 2, r = Math.Min(cx, cy) - 40;
            if (r < 20) return;

            double angle = -Math.PI / 2, total = 0;
            foreach (var s in stats) total += s.TotalSize;
            if (total == 0) return;

            foreach (var stat in stats)
            {
                double sweep = stat.TotalSize / total * 2 * Math.PI;
                if (sweep < 0.005) { angle += sweep; continue; }

                StatsChartCanvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = MakeSlice(cx, cy, r, angle, sweep),
                    Fill = new SolidColorBrush(stat.Color),
                    Stroke = new SolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x2E)),
                    StrokeThickness = 1.5
                });

                if (sweep > 0.2)
                {
                    double mid = angle + sweep / 2;
                    var lbl = new TextBlock
                    {
                        Text = stat.TypeName + "\n" + stat.Percentage.ToString("F0") + "%",
                        Foreground = Brushes.White, FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        TextAlignment = TextAlignment.Center, Width = 56
                    };
                    Canvas.SetLeft(lbl, cx + Math.Cos(mid) * r * 0.62 - 28);
                    Canvas.SetTop (lbl, cy + Math.Sin(mid) * r * 0.62 - 14);
                    StatsChartCanvas.Children.Add(lbl);
                }
                angle += sweep;
            }

            var hole = new Ellipse
            {
                Width = r * 0.85, Height = r * 0.85,
                Fill = new SolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x2E))
            };
            Canvas.SetLeft(hole, cx - r * 0.425);
            Canvas.SetTop (hole, cy - r * 0.425);
            StatsChartCanvas.Children.Add(hole);

            var center = new TextBlock
            {
                Text = "总计\n" + FileNode.FormatSize(VM.RootNode?.Size ?? 0),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0xE8, 0xE8, 0xF0)),
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center, Width = 72
            };
            Canvas.SetLeft(center, cx - 36);
            Canvas.SetTop (center, cy - 16);
            StatsChartCanvas.Children.Add(center);
        }

        private static StreamGeometry MakeSlice(double cx, double cy, double r, double start, double sweep)
        {
            var geo = new StreamGeometry();
            using var ctx = geo.Open();
            double end = start + sweep;
            var sp = new Point(cx + r * Math.Cos(start), cy + r * Math.Sin(start));
            var ep = new Point(cx + r * Math.Cos(end),   cy + r * Math.Sin(end));
            ctx.BeginFigure(new Point(cx, cy), true, true);
            ctx.LineTo(sp, false, false);
            ctx.ArcTo(ep, new Size(r, r), 0, sweep > Math.PI, SweepDirection.Clockwise, true, false);
            geo.Freeze();
            return geo;
        }
    }
}
