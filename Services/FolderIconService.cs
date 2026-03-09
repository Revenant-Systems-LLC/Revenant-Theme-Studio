using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Revenant_Theme_Studio.Services
{
    public class FolderIconService
    {
        private const uint SHCNE_UPDATEITEM = 0x00002000;
        private const uint SHCNE_UPDATEDIR = 0x00001000;
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;
        private const uint SHCNF_PATHW = 0x0005;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public string? GetCurrentIconReference(string targetFolder)
        {
            var iniPath = Path.Combine(targetFolder, "desktop.ini");
            if (!File.Exists(iniPath)) return null;

            foreach (var line in File.ReadAllLines(iniPath, Encoding.Unicode))
            {
                if (line.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase))
                {
                    return line["IconResource=".Length..].Trim().Trim('"');
                }
            }

            return null;
        }

        public void ApplyIcon(string targetFolder, string iconPath, int iconIndex = 0)
        {
            ApplyIconReference(targetFolder, $"\"{iconPath}\",{iconIndex}");
        }

        public void ApplyIconReference(string targetFolder, string iconReference)
        {
            if (string.IsNullOrWhiteSpace(targetFolder)) throw new ArgumentException("Target folder is empty.");
            if (!Directory.Exists(targetFolder)) throw new DirectoryNotFoundException(targetFolder);
            if (string.IsNullOrWhiteSpace(iconReference)) throw new ArgumentException("Icon reference is empty.");

            var iniPath = Path.Combine(targetFolder, "desktop.ini");
            var originalAttrs = File.GetAttributes(targetFolder);

            File.SetAttributes(targetFolder, originalAttrs & ~FileAttributes.ReadOnly);
            if (File.Exists(iniPath)) File.SetAttributes(iniPath, FileAttributes.Normal);

            var content = "[.ShellClassInfo]\r\n" +
                          $"IconResource={iconReference}\r\n" +
                          "ConfirmFileOp=0\r\n";
            File.WriteAllText(iniPath, content, Encoding.Unicode);

            File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(targetFolder, originalAttrs | FileAttributes.ReadOnly);
            RefreshShell(targetFolder);
        }

        public void RemoveIcon(string targetFolder)
        {
            if (string.IsNullOrWhiteSpace(targetFolder)) throw new ArgumentException("Target folder is empty.");
            if (!Directory.Exists(targetFolder)) throw new DirectoryNotFoundException(targetFolder);

            var iniPath = Path.Combine(targetFolder, "desktop.ini");
            var originalAttrs = File.GetAttributes(targetFolder);
            File.SetAttributes(targetFolder, originalAttrs & ~FileAttributes.ReadOnly);

            if (File.Exists(iniPath))
            {
                File.SetAttributes(iniPath, FileAttributes.Normal);
                File.Delete(iniPath);
            }

            File.SetAttributes(targetFolder, originalAttrs);
            RefreshShell(targetFolder);
        }

        private static void RefreshShell(string folderPath)
        {
            var p = Marshal.StringToHGlobalUni(folderPath);
            try
            {
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, p, IntPtr.Zero);
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, p, IntPtr.Zero);
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }
    }
}
