using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Revenant_Theme_Studio.Services
{
    public class FolderIconService
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public void ApplyIcon(string targetFolder, string iconPath)
        {
            string iniPath = Path.Combine(targetFolder, "desktop.ini");

            if (File.Exists(iniPath))
                File.SetAttributes(iniPath, FileAttributes.Normal);

            File.SetAttributes(targetFolder, File.GetAttributes(targetFolder) & ~FileAttributes.ReadOnly);

            string content = "[.ShellClassInfo]\r\nIconResource=" + iconPath + ",0\r\n";
            File.WriteAllText(iniPath, content, Encoding.Unicode);

            File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(targetFolder, File.GetAttributes(targetFolder) | FileAttributes.ReadOnly);

            IntPtr folderPtr = Marshal.StringToHGlobalAuto(targetFolder);
            SHChangeNotify(0x00002000, 0x0001, folderPtr, IntPtr.Zero);
            Marshal.FreeHGlobal(folderPtr);
        }

        public void RemoveIcon(string targetFolder)
        {
            string iniPath = Path.Combine(targetFolder, "desktop.ini");

            if (File.Exists(iniPath))
            {
                File.SetAttributes(iniPath, FileAttributes.Normal);
                File.Delete(iniPath);
            }

            File.SetAttributes(targetFolder, File.GetAttributes(targetFolder) & ~FileAttributes.ReadOnly);

            IntPtr folderPtr = Marshal.StringToHGlobalAuto(targetFolder);
            SHChangeNotify(0x00002000, 0x0001, folderPtr, IntPtr.Zero);
            Marshal.FreeHGlobal(folderPtr);
        }
    }
}
