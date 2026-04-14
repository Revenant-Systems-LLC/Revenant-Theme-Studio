using System;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Revenant_Theme_Studio.Services
{
	[SupportedOSPlatform("windows")]
    public class DriveIconService
    {
        private const string DriveIconRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";

        public string? GetCurrentIcon(string driveLetter)
        {
            var cleaned = CleanDriveLetter(driveLetter);
            using var key = Registry.CurrentUser.OpenSubKey($"{DriveIconRoot}\\{cleaned}\\DefaultIcon");
            return key?.GetValue(null) as string;
        }

        public void ApplyIcon(string driveLetter, string iconReference)
        {
            var cleaned = CleanDriveLetter(driveLetter);
            using var key = Registry.CurrentUser.CreateSubKey($"{DriveIconRoot}\\{cleaned}\\DefaultIcon", true);
            key?.SetValue(null, iconReference);
        }

        public void RestoreIcon(string driveLetter)
        {
            var cleaned = CleanDriveLetter(driveLetter);
            Registry.CurrentUser.DeleteSubKeyTree($"{DriveIconRoot}\\{cleaned}", throwOnMissingSubKey: false);
        }

        private static string CleanDriveLetter(string driveLetter)
        {
            if (string.IsNullOrWhiteSpace(driveLetter)) throw new ArgumentException("Drive letter is required.");
            return driveLetter.Trim().TrimEnd(':').ToUpperInvariant();
        }
    }
}
