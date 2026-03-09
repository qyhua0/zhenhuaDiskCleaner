using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ZhenhuaDiskCleaner.CleanerModule.Views;
using ZhenhuaDiskCleaner.MigratorModule.Views;
using ZhenhuaDiskCleaner.Models;
using ZhenhuaDiskCleaner.ViewModels;
using IoPath = System.IO.Path;
using WpfColor = System.Windows.Media.Color;

namespace ZhenhuaDiskCleaner.Views
{
    public partial class MainWindow : Window
    {
        private MainViewModel VM => (MainViewModel)DataContext;


        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            TreemapView.NodeClicked += node => {
                VM.SelectedNode = node;
                VM.HighlightedNode = node;
                VM.OnTreemapNodeClicked(node);
                SyncTree(node);
            };
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
            System.Diagnostics.Debug.WriteLine($"SyncTree called: {target?.FullPath}");
            if (target == null) return;

            // 1. 先清除所有已选中和展开状态（只清路径上的，性能更好）
            // 2. 展开从根到目标的所有祖先
            // 3. 选中目标节点
            // 全部操作在数据模型上完成，UI 自动跟随绑定更新

            // 构建祖先链
            var chain = new List<FileNode>();
            for (var cur = target; cur != null; cur = cur.Parent)
                chain.Add(cur);
            chain.Reverse();

            // 清除之前选中的节点
            ClearSelection(VM.RootNode);

            // 展开路径上所有祖先目录
            foreach (var node in chain)
            {
                if (node.IsDirectory)
                    node.IsExpanded = true;
            }

            // 选中目标节点
            target.IsSelected = true;

            // 等UI渲染后再滚动到目标位置
            Dispatcher.InvokeAsync(() =>
            {
                ScrollToSelected(DirectoryTree);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // 递归清除选中状态
        private static void ClearSelection(FileNode? node)
        {
            if (node == null) return;
            if (node.IsSelected) node.IsSelected = false;
            foreach (var child in node.Children)
                ClearSelection(child);
        }

        // 递归找到选中的 TreeViewItem 并滚动到视图
        private static bool ScrollToSelected(ItemsControl container)
        {
            if (container == null) return false;

            foreach (var item in container.Items)
            {
                if (container.ItemContainerGenerator
                        .ContainerFromItem(item) is not TreeViewItem tvi) continue;

                if (item is FileNode fn && fn.IsSelected)
                {
                    tvi.BringIntoView();
                    return true;
                }

                if (item is FileNode dir && dir.IsExpanded)
                {
                    if (ScrollToSelected(tvi)) return true;
                }
            }
            return false;
        }
        private async Task ExpandToNodeAsync(ItemsControl container,
            List<FileNode> chain, int depth, FileNode target)
        {
            if (depth >= chain.Count) return;
            var current = chain[depth];

            // 等待容器生成
            await WaitForContainerAsync(container, current);

            var tvi = container.ItemContainerGenerator
                          .ContainerFromItem(current) as TreeViewItem;
            if (tvi == null) return;

            // 展开当前节点
            tvi.IsExpanded = true;

            // 等待子项容器生成
            await Task.Yield();
            tvi.UpdateLayout();

            if (depth == chain.Count - 1)
            {
                // 到达目标，选中并滚动
                tvi.IsSelected = true;
                tvi.BringIntoView();

                // 如果目标是文件（不是目录），选中父目录下对应的文件子项
                if (!target.IsDirectory && tvi.Items.Count > 0)
                {
                    await WaitForContainerAsync(tvi, target);
                    var fileTvi = tvi.ItemContainerGenerator
                                      .ContainerFromItem(target) as TreeViewItem;
                    if (fileTvi != null)
                    {
                        fileTvi.IsSelected = true;
                        fileTvi.BringIntoView();
                    }
                }
                return;
            }

            await ExpandToNodeAsync(tvi, chain, depth + 1, target);
        }

        private async Task WaitForContainerAsync(ItemsControl container, object item)
        {
            // 最多等待 300ms，每 20ms 检查一次
            for (int i = 0; i < 15; i++)
            {
                var tvi = container.ItemContainerGenerator
                              .ContainerFromItem(item);
                if (tvi != null) return;

                // 强制布局更新
                container.UpdateLayout();
                await Task.Delay(20);

                // 尝试滚动到该项使其虚拟化容器生成
                if (container is TreeViewItem parentTvi)
                {
                    parentTvi.BringIntoView();
                    parentTvi.UpdateLayout();
                }
            }
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

        private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
                VM.CustomPath = string.Empty;
        }

        private void OpenCleaner_Click(object sender, RoutedEventArgs e)
        {
            var win = new CleanerWindow { Owner = this };
            win.Show();  // 非模态，不阻塞主窗口
        }

        /// <summary>打开个人资料迁移窗口</summary>
        private void OpenMigrator_Click(object sender, RoutedEventArgs e)
        {
            var win = new MigratorWindow { Owner = this };
            win.Show();
        }

    }
}
