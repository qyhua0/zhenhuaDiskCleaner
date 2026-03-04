using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using ZhenhuaDiskCleaner.Models;

namespace ZhenhuaDiskCleaner.Services
{
    public class FileOperationService
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT fo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEOPSTRUCT
        {
            public System.IntPtr hwnd;
            [MarshalAs(UnmanagedType.U4)] public int wFunc;
            public string pFrom;
            public string? pTo;
            public short fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public System.IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        private const int FO_DELETE = 0x0003;
        private const short FOF_ALLOWUNDO = 0x0040;
        private const short FOF_NOCONFIRMATION = 0x0010;
        private const short FOF_SILENT = 0x0004;

        public bool DeleteToRecycleBin(string path)
        {
            try
            {
                var fo = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = path + "\0\0",
                    fFlags = (short)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT)
                };
                return SHFileOperation(ref fo) == 0;
            }
            catch { return false; }
        }

        public bool DeletePermanently(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, true);
                return true;
            }
            catch (System.Exception ex)
            {
                System.Windows.MessageBox.Show($"删除失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        public bool ClearDirectory(string path)
        {
            try
            {
                var di = new DirectoryInfo(path);
                foreach (var f in di.GetFiles()) f.Delete();
                foreach (var d in di.GetDirectories()) d.Delete(true);
                return true;
            }
            catch (System.Exception ex)
            {
                System.Windows.MessageBox.Show($"清空失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        public bool MoveFile(string source, string destination)
        {
            try
            {
                if (File.Exists(source)) File.Move(source, destination);
                else if (Directory.Exists(source)) Directory.Move(source, destination);
                return true;
            }
            catch (System.Exception ex)
            {
                System.Windows.MessageBox.Show($"移动失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        public void CopyPathToClipboard(string path)
        {
            try { System.Windows.Clipboard.SetText(path); } catch { }
        }

        public void OpenInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else
                    Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch { }
        }

        public void OpenInCmd(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                Process.Start(new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/k cd /d \"{dir}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public async System.Threading.Tasks.Task<HashResult> ComputeHashAsync(string filePath)
        {
            var result = new HashResult();
            try
            {
                var t1 = ComputeHash(MD5.Create(), filePath);
                var t2 = ComputeHash(SHA1.Create(), filePath);
                var t3 = ComputeHash(SHA256.Create(), filePath);
                var t4 = ComputeHash(SHA512.Create(), filePath);
                var results = await System.Threading.Tasks.Task.WhenAll(t1, t2, t3, t4);
                result.MD5 = results[0]; result.SHA1 = results[1];
                result.SHA256 = results[2]; result.SHA512 = results[3];
            }
            catch (System.Exception ex) { result.MD5 = "Error: " + ex.Message; }
            return result;
        }

        private static async System.Threading.Tasks.Task<string> ComputeHash(HashAlgorithm ha, string path)
        {
            using (ha)
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                var hash = await System.Threading.Tasks.Task.Run(() => ha.ComputeHash(fs));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static async System.Threading.Tasks.Task<string> ComputeHash<T>(string path) where T : HashAlgorithm, new()
        {
            using var ha = new T();
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var hash = await System.Threading.Tasks.Task.Run(() => ha.ComputeHash(fs));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
