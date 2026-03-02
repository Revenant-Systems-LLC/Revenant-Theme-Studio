using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Revenant_Theme_Studio.Services
{
    public class FolderIconService
    {
        private const uint SHCNE_UPDATEITEM   = 0x00002000;
        private const uint SHCNE_UPDATEDIR    = 0x00001000;
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;

        private const uint SHCNF_IDLIST = 0x0000;
        private const uint SHCNF_PATHW  = 0x0005; // Unicode path

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public void ApplyIcon(string targetFolder, string iconPath, int iconIndex = 0)
        {
            if (string.IsNullOrWhiteSpace(targetFolder))
                throw new ArgumentException("Target folder is empty.");
            if (!Directory.Exists(targetFolder))
                throw new DirectoryNotFoundException(targetFolder);

            if (string.IsNullOrWhiteSpace(iconPath))
                throw new ArgumentException("Icon path is empty.");
            if (!File.Exists(iconPath))
                throw new FileNotFoundException("Icon not found.", iconPath);

            string iniPath = Path.Combine(targetFolder, "desktop.ini");
            var originalFolderAttrs = File.GetAttributes(targetFolder);

            // Allow writes
            File.SetAttributes(targetFolder, originalFolderAttrs & ~FileAttributes.ReadOnly);

            if (File.Exists(iniPath))
                File.SetAttributes(iniPath, FileAttributes.Normal);

            string content =
                "[.ShellClassInfo]\r\n" +
                $"IconResource=\"{iconPath}\",{iconIndex}\r\n" +
                "ConfirmFileOp=0\r\n";

            File.WriteAllText(iniPath, content, Encoding.Unicode);

            File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);

            // Ensure the customization marker stays set
            File.SetAttributes(targetFolder, originalFolderAttrs | FileAttributes.ReadOnly);

            RefreshShell(targetFolder);
        }

        public void RemoveIcon(string targetFolder)
        {
            if (string.IsNullOrWhiteSpace(targetFolder))
                throw new ArgumentException("Target folder is empty.");
            if (!Directory.Exists(targetFolder))
                throw new DirectoryNotFoundException(targetFolder);

            string iniPath = Path.Combine(targetFolder, "desktop.ini");
            var originalFolderAttrs = File.GetAttributes(targetFolder);

            // Allow delete
            File.SetAttributes(targetFolder, originalFolderAttrs & ~FileAttributes.ReadOnly);

            if (File.Exists(iniPath))
            {
                File.SetAttributes(iniPath, FileAttributes.Normal);
                File.Delete(iniPath);
            }

            // Restore original folder attributes (don’t randomly change user state)
            File.SetAttributes(targetFolder, originalFolderAttrs);

            RefreshShell(targetFolder);
        }

        private static void RefreshShell(string folderPath)
        {
            IntPtr p = Marshal.StringToHGlobalUni(folderPath);
            try
            {
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, p, IntPtr.Zero);
                SHChangeNotify(SHCNE_UPDATEDIR,  SHCNF_PATHW, p, IntPtr.Zero);
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }
    }
}