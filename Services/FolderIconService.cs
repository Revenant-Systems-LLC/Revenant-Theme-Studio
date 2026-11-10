using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Revenant_Theme_Studio.Services
{
    /// <summary>
    /// Folder icons through desktop.ini.
    ///
    /// desktop.ini often holds more than the icon: special folders such as Downloads and
    /// Documents keep LocalizedResourceName, InfoTip, [ViewState] and CLSID entries in it.
    /// This service therefore edits only the IconResource key, through the Win32 profile
    /// API, and never rewrites or deletes the rest of the file.
    /// </summary>
    public class FolderIconService
    {
        private const string Section = ".ShellClassInfo";

        private const uint SHCNE_UPDATEITEM = 0x00002000;
        private const uint SHCNE_UPDATEDIR = 0x00001000;
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;
        private const uint SHCNF_PATHW = 0x0005;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetPrivateProfileString(
            string lpAppName, string lpKeyName, string lpDefault,
            StringBuilder lpReturnedString, uint nSize, string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WritePrivateProfileString(
            string? lpAppName, string? lpKeyName, string? lpString, string lpFileName);

        /// <summary>
        /// The folder's current icon as an IconResource-style string ("path",index), or null.
        /// Reads legacy IconFile/IconIndex when IconResource is absent. Handles ANSI and
        /// UTF-16 files, since the profile API reads both.
        /// </summary>
        public string? GetCurrentIconReference(string targetFolder)
        {
            var iniPath = Path.Combine(targetFolder, "desktop.ini");
            if (!File.Exists(iniPath)) return null;

            var resource = ReadKey(iniPath, "IconResource");
            if (!string.IsNullOrWhiteSpace(resource)) return resource.Trim();

            var iconFile = ReadKey(iniPath, "IconFile");
            if (string.IsNullOrWhiteSpace(iconFile)) return null;
            var iconIndex = ReadKey(iniPath, "IconIndex");
            var index = int.TryParse(iconIndex, out var i) ? i : 0;
            return $"\"{iconFile.Trim().Trim('"')}\",{index}";
        }

        public void ApplyIcon(string targetFolder, string iconPath, int iconIndex = 0)
        {
            ApplyIconReference(targetFolder, $"\"{iconPath}\",{iconIndex}");
        }

        /// <summary>
        /// Sets IconResource and leaves every other line of desktop.ini as it was.
        /// </summary>
        public void ApplyIconReference(string targetFolder, string iconReference)
        {
            if (string.IsNullOrWhiteSpace(targetFolder)) throw new ArgumentException("Target folder is empty.");
            if (!Directory.Exists(targetFolder)) throw new DirectoryNotFoundException(targetFolder);
            if (string.IsNullOrWhiteSpace(iconReference)) throw new ArgumentException("Icon reference is empty.");

            var iniPath = Path.Combine(targetFolder, "desktop.ini");
            var folderAttrs = File.GetAttributes(targetFolder);
            var iniAttrs = File.Exists(iniPath) ? File.GetAttributes(iniPath) : (FileAttributes?)null;

            try
            {
                if (iniAttrs is null)
                {
                    // New file: start it as UTF-16 so the profile API keeps writing Unicode
                    // and icon paths with non-ASCII characters survive.
                    File.WriteAllText(iniPath, $"[{Section}]\r\nConfirmFileOp=0\r\n", Encoding.Unicode);
                }
                else
                {
                    File.SetAttributes(iniPath, FileAttributes.Normal);
                    EnsureUnicode(iniPath);
                }

                WriteKey(iniPath, "IconResource", iconReference);
            }
            finally
            {
                if (File.Exists(iniPath))
                    File.SetAttributes(iniPath, (iniAttrs ?? 0) | FileAttributes.Hidden | FileAttributes.System);
                // Explorer only reads desktop.ini in folders marked ReadOnly or System.
                File.SetAttributes(targetFolder, folderAttrs | FileAttributes.ReadOnly);
            }

            RefreshShell(targetFolder);
        }

        /// <summary>
        /// Removes only the IconResource key. desktop.ini is deleted only when nothing of
        /// substance is left in it (just our own ConfirmFileOp line, or nothing).
        /// </summary>
        public void RemoveIcon(string targetFolder)
        {
            if (string.IsNullOrWhiteSpace(targetFolder)) throw new ArgumentException("Target folder is empty.");
            if (!Directory.Exists(targetFolder)) throw new DirectoryNotFoundException(targetFolder);

            var iniPath = Path.Combine(targetFolder, "desktop.ini");
            if (!File.Exists(iniPath)) { RefreshShell(targetFolder); return; }

            var folderAttrs = File.GetAttributes(targetFolder);
            var iniAttrs = File.GetAttributes(iniPath);
            bool deleted = false;

            try
            {
                File.SetAttributes(iniPath, FileAttributes.Normal);
                WriteKey(iniPath, "IconResource", null);

                if (HasNothingLeft(iniPath))
                {
                    File.Delete(iniPath);
                    deleted = true;
                }
            }
            finally
            {
                if (deleted)
                    File.SetAttributes(targetFolder, folderAttrs & ~FileAttributes.ReadOnly);
                else if (File.Exists(iniPath))
                    File.SetAttributes(iniPath, iniAttrs);
            }

            RefreshShell(targetFolder);
        }

        private static string ReadKey(string iniPath, string key)
        {
            var sb = new StringBuilder(2048);
            GetPrivateProfileString(Section, key, "", sb, (uint)sb.Capacity, iniPath);
            return sb.ToString();
        }

        private static void WriteKey(string iniPath, string key, string? value)
        {
            if (!WritePrivateProfileString(Section, key, value, iniPath))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not update {iniPath}");
            // Flush the profile API's cache so Explorer sees the change at once.
            WritePrivateProfileString(null, null, null, iniPath);
        }

        /// <summary>
        /// Converts an ANSI or UTF-8 desktop.ini to UTF-16 with the same content. The profile
        /// API writes in the file's existing encoding, and ANSI would mangle non-ASCII paths.
        /// </summary>
        private static void EnsureUnicode(string iniPath)
        {
            var bytes = File.ReadAllBytes(iniPath);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return;

            string text;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            else
            {
                // The profile API treats BOM-less files as the system ANSI code page.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding((int)GetACP()).GetString(bytes);
            }
            File.WriteAllText(iniPath, text, Encoding.Unicode);
        }

        private static bool HasNothingLeft(string iniPath)
        {
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';')) continue;
                if (line.Equals($"[{Section}]", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("ConfirmFileOp=", StringComparison.OrdinalIgnoreCase)) continue;
                return false;
            }
            return true;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        private static void RefreshShell(string folderPath)
        {
            var pFolder = Marshal.StringToHGlobalUni(folderPath);
            var parentPath = Path.GetDirectoryName(folderPath);
            var pParent = parentPath != null ? Marshal.StringToHGlobalUni(parentPath) : IntPtr.Zero;
            try
            {
                // Notify the folder itself
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, pFolder, IntPtr.Zero);
                SHChangeNotify(SHCNE_UPDATEDIR,  SHCNF_PATHW, pFolder, IntPtr.Zero);

                // Notify the parent — Explorer watches the parent directory and
                // re-reads children (including icon state) when the parent is updated.
                // Without this, Explorer's icon cache ignores the desktop.ini change
                // until something else triggers a refresh.
                if (pParent != IntPtr.Zero)
                    SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, pParent, IntPtr.Zero);

                // Broadcast global association change to flush icon cache
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pFolder);
                if (pParent != IntPtr.Zero) Marshal.FreeHGlobal(pParent);
            }
        }
    }
}
