using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ZhenhuaDiskCleaner.MigratorModule.Models;
using ZhenhuaDiskCleaner.MigratorModule.ViewModels;

namespace ZhenhuaDiskCleaner.MigratorModule.Views
{
    public partial class MigratorWindow : Window
    {
        private MigratorViewModel VM => (MigratorViewModel)DataContext;

        public MigratorWindow()
        {
            InitializeComponent();
            DataContext = new MigratorViewModel();
            Loaded += MigratorWindow_Loaded;
        }

        private void MigratorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SyncDriveCombo();
        }

        /// <summary>
        /// 同步 ComboBox 选中项，并强制修正 ContentSite TextBlock 的显示。
        /// 全局 DarkTheme 的 ComboBox Template 中 ContentSite 绑定的是
        /// SelectedItem.DisplayName，对普通 string 列表会显示空白，
        /// 这里在选中后直接找到该 TextBlock 并赋值，完全绕开绑定问题。
        /// </summary>
        private void SyncDriveCombo()
        {
            if (!string.IsNullOrEmpty(VM.TargetDrive) &&
                VM.AvailableDrives.Contains(VM.TargetDrive))
            {
                DriveCombo.SelectedItem = VM.TargetDrive;
            }
            else if (DriveCombo.Items.Count > 0)
            {
                DriveCombo.SelectedIndex = 0;
            }

            FixComboBoxDisplay(DriveCombo);
        }

        /// <summary>
        /// 用 VisualTreeHelper 遍历 ComboBox 模板树，
        /// 找到第一个 TextBlock 直接把 Text 设成选中值。
        /// 不依赖模板内部命名，兼容任何自定义 Template。
        /// </summary>
        private static void FixComboBoxDisplay(ComboBox combo)
        {
            combo.ApplyTemplate();
            string value = combo.SelectedItem as string ?? "";
            SetFirstTextBlock(combo, value);
        }

        private static bool SetFirstTextBlock(DependencyObject parent, string text)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                // 跳过 Popup（下拉列表区域，不改那里）
                if (child is System.Windows.Controls.Primitives.Popup) continue;
                if (child is TextBlock tb)
                {
                    tb.Text = text;
                    return true;   // 找到第一个就停止
                }
                if (SetFirstTextBlock(child, text)) return true;
            }
            return false;
        }

        // ── 标题栏 ────────────────────────────────────────────────────────────

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
            else
                DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ── 目标盘下拉 ────────────────────────────────────────────────────────

        private void DriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (DriveCombo.SelectedItem is string selected)
            {
                VM.TargetDrive = selected;
                VM.TargetDriveChangedCommand.Execute(null);
                // 每次选中后同步修正显示
                FixComboBoxDisplay(DriveCombo);
            }
        }

        // ── 源路径点击打开资源管理器 ──────────────────────────────────────────

        private void SourcePath_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is string path && Directory.Exists(path))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", path);
                }
                catch { }
            }
        }

        // ── 目标路径文本框失焦验证 ────────────────────────────────────────────

        private void TargetPath_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is FolderItem item)
                VM.PathEditedCommand.Execute(item);
        }
    }
}