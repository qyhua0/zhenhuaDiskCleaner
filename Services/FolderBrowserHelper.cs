using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZhenhuaDiskCleaner.Services
{
    /// <summary>
    /// Pure Win32 COM folder picker - no System.Windows.Forms dependency.
    /// </summary>
    public static class FolderBrowserHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDList(IntPtr pidl, System.Text.StringBuilder pszPath);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public string? pszDisplayName;
            public string? lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        private const uint BIF_RETURNONLYFSDIRS   = 0x0001;
        private const uint BIF_NEWDIALOGSTYLE      = 0x0040;
        private const uint BIF_EDITBOX             = 0x0010;
        private const uint BIF_USENEWUI            = BIF_EDITBOX | BIF_NEWDIALOGSTYLE;

        public static string? Browse(string title = "选择文件夹")
        {
            var owner = Application.Current?.MainWindow;
            IntPtr hwnd = owner != null
                ? new WindowInteropHelper(owner).Handle
                : IntPtr.Zero;

            var info = new BROWSEINFO
            {
                hwndOwner = hwnd,
                lpszTitle = title,
                ulFlags   = BIF_RETURNONLYFSDIRS | BIF_USENEWUI,
                pszDisplayName = new string(' ', 260)
            };

            IntPtr pidl = SHBrowseForFolder(ref info);
            if (pidl == IntPtr.Zero) return null;

            try
            {
                var sb = new System.Text.StringBuilder(260);
                return SHGetPathFromIDList(pidl, sb) ? sb.ToString() : null;
            }
            finally { CoTaskMemFree(pidl); }
        }
    }
}
