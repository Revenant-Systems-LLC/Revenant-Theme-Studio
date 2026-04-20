using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Revenant_Theme_Studio.Services
{
    [SupportedOSPlatform("windows")]
    public class DriveIconService
    {
        private const string DriveIconRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";

        private const uint SHCNE_UPDATEITEM    = 0x00002000;
        private const uint SHCNE_UPDATEDIR     = 0x00001000;
        private const uint SHCNE_ASSOCCHANGED  = 0x08000000;
        private const uint SHCNF_PATHW         = 0x0005;
        private const uint SHCNF_IDLIST        = 0x0000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public string? GetCurrentIcon(string driveLetter)
        {
            var cleaned = CleanDriveLetter(driveLetter);
            using var key = Registry.CurrentUser.OpenSubKey($@"{DriveIconRoot}\{cleaned}\DefaultIcon");
            return key?.GetValue(null) as string;
        }

        public void ApplyIcon(string driveLetter, string iconReference)
        {
            var cleaned = CleanDriveLetter(driveLetter);

            // Registry DefaultIcon entries must NOT have quoted paths.
            // Strip surrounding quotes so "C:\path\icon.ico",0 → C:\path\icon.ico,0
            var regValue = iconReference.Trim().TrimStart('"');
            var commaIdx = regValue.IndexOf("\",", StringComparison.Ordinal);
            if (commaIdx >= 0)
                regValue = regValue[..commaIdx] + regValue[(commaIdx + 1)..]; // remove closing quote only

            using var key = Registry.CurrentUser.CreateSubKey($@"{DriveIconRoot}\{cleaned}\DefaultIcon", true);
            key?.SetValue(null, regValue);

            RefreshShell(cleaned);
        }

        public void RestoreIcon(string driveLetter)
        {
            var cleaned = CleanDriveLetter(driveLetter);
            Registry.CurrentUser.DeleteSubKeyTree($@"{DriveIconRoot}\{cleaned}", throwOnMissingSubKey: false);
            RefreshShell(cleaned);
        }

        private static void RefreshShell(string driveLetter)
        {
            // Notify Explorer about the drive root path e.g. "C:\"
            var drivePath = $@"{driveLetter}:\";
            var p = Marshal.StringToHGlobalUni(drivePath);
            try
            {
                SHChangeNotify(SHCNE_UPDATEITEM,   SHCNF_PATHW,  p, IntPtr.Zero);
                SHChangeNotify(SHCNE_UPDATEDIR,    SHCNF_PATHW,  p, IntPtr.Zero);
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static string CleanDriveLetter(string driveLetter)
        {
            if (string.IsNullOrWhiteSpace(driveLetter)) throw new ArgumentException("Drive letter is required.");
            return driveLetter.Trim().TrimEnd(':').ToUpperInvariant();
        }
    }
}
