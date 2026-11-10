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

        /// <summary>
        /// 算一个文件的四种摘要。
        /// 以前是 MD5/SHA1/SHA256/SHA512 各开一个 FileStream 并发跑，
        /// 同一个文件被从头到尾读四遍（机械盘上磁头还要来回甩），
        /// 现在改成单遍顺序读 + 四个 TransformBlock，IO 量降到 1/4。
        /// </summary>
        public async Task<HashResult> ComputeHashAsync(string filePath)
        {
            var result = new HashResult();
            try
            {
                var hashes = await Task.Run(() => ComputeAllHashes(filePath)).ConfigureAwait(false);
                result.MD5     = hashes[0];
                result.SHA1    = hashes[1];
                result.SHA256  = hashes[2];
                result.SHA512  = hashes[3];
            }
            catch (Exception ex) { result.MD5 = "Error: " + ex.Message; }
            return result;
        }

        /// <summary>单遍读文件同时更新四个哈希（在后台线程跑，故用同步 IO）。</summary>
        private static string[] ComputeAllHashes(string path)
        {
            using var md5    = MD5.Create();
            using var sha1   = SHA1.Create();
            using var sha256 = SHA256.Create();
            using var sha512 = SHA512.Create();

            const int Buffer = 1 << 20;   // 1 MB
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, Buffer, FileOptions.SequentialScan);
            var buffer = new byte[Buffer];

            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                md5.TransformBlock(buffer, 0, read, null, 0);
                sha1.TransformBlock(buffer, 0, read, null, 0);
                sha256.TransformBlock(buffer, 0, read, null, 0);
                sha512.TransformBlock(buffer, 0, read, null, 0);
            }

            // 空文件也必须 Final，否则 Hash 为 null
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha512.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            return new[]
            {
                Hex(md5.Hash!), Hex(sha1.Hash!), Hex(sha256.Hash!), Hex(sha512.Hash!)
            };
        }

        private static string Hex(byte[] hash) =>
            BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
