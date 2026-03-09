using System.Windows;
using System.Windows.Input;
using ZhenhuaDiskCleaner.CleanerModule.Models;
using ZhenhuaDiskCleaner.CleanerModule.ViewModels;

namespace ZhenhuaDiskCleaner.CleanerModule.Views
{
    /// <summary>
    /// 系统盘清理窗口的 Code-Behind。
    /// 只处理纯粹的 UI 交互（窗口拖动、右键菜单事件），
    /// 业务逻辑全部由 CleanerViewModel 处理。
    /// </summary>
    public partial class CleanerWindow : Window
    {
        private CleanerViewModel VM => (CleanerViewModel)DataContext;

        public CleanerWindow()
        {
            InitializeComponent();
            DataContext = new CleanerViewModel();
        }

        // ── 窗口操作 ──────────────────────────────────────────────────────────

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                ToggleMaximize();
            else
                DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
            => ToggleMaximize();

        private void Close_Click(object sender, RoutedEventArgs e)
            => Close();

        private void ToggleMaximize()
            => WindowState = WindowState == WindowState.Maximized
               ? WindowState.Normal
               : WindowState.Maximized;

        // ── CheckBox 变化 → 刷新已选大小 ─────────────────────────────────────

        /// <summary>
        /// 当用户手动勾选/取消某一分类时，刷新底部"已选 X 可释放"的统计。
        /// 由于 ScanResult.IsChecked 变化不会自动触发 VM 的派生属性，
        /// 需要在 CheckBox 的 Checked/Unchecked 事件中手动调用。
        /// </summary>
        private void OnResultCheckedChanged(object sender, RoutedEventArgs e)
            => VM.RefreshSelectedSize();

        // ── 右键菜单事件（Code-Behind 比 Command 更简洁，符合 WPF 惯例）────

        /// <summary>「打开文件所在目录」菜单项点击</summary>
        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (GetFileEntryFromMenuEvent(sender) is FileEntry entry)
                VM.OpenInExplorerCommand.Execute(entry);
        }

        /// <summary>「复制文件路径」菜单项点击</summary>
        private void CopyFilePath_Click(object sender, RoutedEventArgs e)
        {
            if (GetFileEntryFromMenuEvent(sender) is FileEntry entry)
            {
                try { Clipboard.SetText(entry.FullPath); }
                catch { /* 剪贴板访问偶发失败，静默处理 */ }
            }
        }

        /// <summary>从 MenuItem 的 Click 事件中提取 CommandParameter（FileEntry）</summary>
        private static FileEntry? GetFileEntryFromMenuEvent(object sender)
        {
            if (sender is System.Windows.Controls.MenuItem mi
                && mi.CommandParameter is FileEntry entry)
                return entry;
            return null;
        }
    }
}
