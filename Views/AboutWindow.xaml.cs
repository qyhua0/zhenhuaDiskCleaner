using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Views
{
    /// <summary>
    /// 关于对话框。界面全部在 AboutWindow.xaml 里，这里只留三件事：
    /// 拖窗口、点链接、关窗口。（以前这 200 行 UI 是用 C# 一行行 new 出来的。）
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 点卡片外的留白关闭对话框。
        /// MouseLeftButtonDown 会冒泡，卡内的点击也会走到这里，
        /// 所以按坐标判断是不是真的落在卡片外。
        /// </summary>
        private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(Card);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X <= Card.ActualWidth && p.Y <= Card.ActualHeight;
            if (!inside) Close();
        }

        /// <summary>按住顶部色带拖动窗口。双击的第二次按下时已经没有按键捕获，DragMove 会抛异常，忽略。</summary>
        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 官网 / 邮箱点击：交给 shell。
        /// 机器上没装浏览器或邮件客户端时静默失败（与程序其它地方一致，不为这个弹错误框）。
        /// </summary>
        private void Value_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: AboutInfoItem item }) return;

            var target = item.IsUrl ? item.Value : "mailto:" + item.Value;
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // 没有关联程序 / 用户取消了选择
            }
        }
    }
}
