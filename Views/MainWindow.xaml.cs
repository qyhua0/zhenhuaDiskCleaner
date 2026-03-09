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


        /// <summary>关于对话框</summary>
        private void About_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                ResizeMode = ResizeMode.NoResize,
                Width = 400,
                Height = 320,
                Background = System.Windows.Media.Brushes.Transparent,
            };

            // 整体卡片
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x35)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x52)),
                BorderThickness = new Thickness(1),
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            // ── 顶部色带 ──
            var header = new Border
            {
                CornerRadius = new CornerRadius(12, 12, 0, 0),
                Padding = new Thickness(24, 20, 24, 20),
                Background = new LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(0x6C, 0x63, 0xFF),
                    System.Windows.Media.Color.FromRgb(0x4A, 0x44, 0xBB),
                    new System.Windows.Point(0, 0), new System.Windows.Point(1, 1)),
            };
            var headerStack = new StackPanel { Orientation = Orientation.Horizontal };
            var iconBorder = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255)),
                Margin = new Thickness(0, 0, 14, 0),
            };
            iconBorder.Child = new TextBlock
            {
                Text = "⚡",
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var titleBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleBlock.Children.Add(new TextBlock
            {
                Text = "振华磁盘清理工具",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
            });
            titleBlock.Children.Add(new TextBlock
            {
                Text = "ZhenHua Disk Cleaner  v1.3",
                FontSize = 11,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 255, 255)),
                Margin = new Thickness(0, 3, 0, 0),
            });
            headerStack.Children.Add(iconBorder);
            headerStack.Children.Add(titleBlock);
            header.Child = headerStack;
            Grid.SetRow(header, 0);

            // ── 内容区 ──
            var accent = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0x63, 0xFF));
            var textSec = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA0, 0xA0, 0xB8));
            var textPri = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xE8, 0xF0));

            var body = new StackPanel { Margin = new Thickness(24, 18, 24, 8) };

            InfoRow(body, "🌐", "官方网站", "https://diskcleaner.bloghua.com", accent, textSec, textPri, dlg);
            InfoRow(body, "📧", "联系邮箱", "qyhua0@hotmail.com", accent, textSec, textPri, dlg);
            InfoRow(body, "📝", "开源协议", "GPL-3.0 license", accent, textSec, textPri, dlg);
            InfoRow(body, "🛡", "系统要求", "Windows 10/11  ·  .NET 8", accent, textSec, textPri, dlg);

            // 版权
            body.Children.Add(new TextBlock
            {
                Text = "© 2026 https://www.bloghua.com  保留所有权利",
                FontSize = 10,
                Foreground = textSec,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            Grid.SetRow(body, 1);

            // ── 底部关闭按钮 ──
            var footer = new Border
            {
                Padding = new Thickness(24, 10, 24, 16),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x52)),
                BorderThickness = new Thickness(0, 1, 0, 0),
            };
            var closeBtn = new Button
            {
                Content = "关闭",
                HorizontalAlignment = HorizontalAlignment.Right,
                Width = 80,
                Height = 30,
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.White,
                Background = accent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            closeBtn.Click += (_, _) => dlg.Close();
            footer.Child = closeBtn;
            Grid.SetRow(footer, 2);

            root.Children.Add(header);
            root.Children.Add(body);
            root.Children.Add(footer);
            card.Child = root;
            dlg.Content = card;

            // 点击卡片外区域关闭
            dlg.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.Source == dlg) dlg.Close();
                else dlg.DragMove();
            };

            dlg.ShowDialog();
        }



        private static void InfoRow(StackPanel parent,
            string icon, string label, string value,
            System.Windows.Media.Brush accent,
            System.Windows.Media.Brush textSec,
            System.Windows.Media.Brush textPri,
            Window owner)
        {
            bool isUrl = value.StartsWith("http");
            bool isEmail = value.Contains('@');

            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var ico = new TextBlock
            {
                Text = icon,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = textSec,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var val = new TextBlock
            {
                Text = value,
                FontSize = 12,
                Foreground = (isUrl || isEmail) ? accent : textPri,
                VerticalAlignment = VerticalAlignment.Center,
                TextDecorations = (isUrl || isEmail) ? TextDecorations.Underline : null,
                Cursor = (isUrl || isEmail)
                    ? System.Windows.Input.Cursors.Hand
                    : System.Windows.Input.Cursors.Arrow,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            if (isUrl)
                val.MouseLeftButtonUp += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(value) { UseShellExecute = true });
                    }
                    catch { }
                };
            else if (isEmail)
                val.MouseLeftButtonUp += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo("mailto:" + value) { UseShellExecute = true });
                    }
                    catch { }
                };

            Grid.SetColumn(ico, 0);
            Grid.SetColumn(lbl, 1);
            Grid.SetColumn(val, 2);
            row.Children.Add(ico);
            row.Children.Add(lbl);
            row.Children.Add(val);
            parent.Children.Add(row);
        }


    }
}
