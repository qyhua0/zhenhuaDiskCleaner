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

        /// <summary>
        /// 安全取当前根节点。DrawPieChart 里直接写 VM.RootNode 会报 CS8602：
        /// DataContext 是 object?，编译期无法证明它已被构造函数赋值。
        /// </summary>
        private FileNode? Root => (DataContext as MainViewModel)?.RootNode;

        /// <summary>
        /// 上一次被选中的节点。SyncTree 靠它只清一个节点，
        /// 而不是递归整棵树（见 SyncTree 里的注释）。
        /// </summary>
        private FileNode? _lastSelected;


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

            // 清除上一次选中的节点——只清那一个。
            // 以前这里是 ClearSelection(VM.RootNode)：递归走完树上每一个节点。
            // 扫一整个盘后是几百万个节点，而 SyncTree 是“点一下 Treemap 色块”
            // 和“点一下文件列表”都会走的路径，在 UI 线程上跑 → 大目录明卡。
            // 选中状态的所有变更都会经过本方法（树的 SelectedItemChanged → VM.SelectedNode
            // → SyncTree，Treemap 点击 → SyncTree），所以记住上一个就足够。
            if (_lastSelected != null && _lastSelected != target)
                _lastSelected.IsSelected = false;

            // 展开路径上所有祖先目录
            foreach (var node in chain)
            {
                if (node.IsDirectory)
                    node.IsExpanded = true;
            }

            // 选中目标节点
            target.IsSelected = true;
            _lastSelected = target;

            // 等UI渲染后再滚动到目标位置
            Dispatcher.InvokeAsync(() =>
            {
                ScrollToSelected(DirectoryTree);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
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
                Text = "总计\n" + FileNode.FormatSize(Root?.Size ?? 0),
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
            // Freeze 必须在 GeometryContext Dispose 之后：Open() 返回的 ctx 没释放就冻结，
            // 图形的内部数据还没最终写完（以前 geo.Freeze() 写在 using var 作用域内）。
            using (var ctx = geo.Open())
            {
                double end = start + sweep;
                var sp = new Point(cx + r * Math.Cos(start), cy + r * Math.Sin(start));
                var ep = new Point(cx + r * Math.Cos(end),   cy + r * Math.Sin(end));
                ctx.BeginFigure(new Point(cx, cy), true, true);
                ctx.LineTo(sp, false, false);
                ctx.ArcTo(ep, new Size(r, r), 0, sweep > Math.PI, SweepDirection.Clockwise, true, false);
            }
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


        /// <summary>
        /// 关于对话框。界面全部在 Views/AboutWindow.xaml 里声明，
        /// 这里只负责把它模态地开出来（版本号等文案见 Helpers/AppInfo）。
        /// </summary>
        private void About_Click(object sender, RoutedEventArgs e)
            => new AboutWindow { Owner = this }.ShowDialog();


    }
}
