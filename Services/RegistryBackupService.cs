using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Revenant_Theme_Studio.Services
{
    /// <summary>
    /// Provides a simple backup of drive and shell icon registry settings. When called, it reads
    /// the current per-user drive and shell icon overrides from the registry and writes them
    /// to a JSON file under the application data directory. This is not a full system backup,
    /// but it allows RTS to restore the previous icon values if needed.
    /// </summary>
    public class RegistryBackupService
    {
        public void BackupRegistry()
        {
            var data = new Dictionary<string, string?>();

            // Backup drive icons under HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons
            const string driveRoot = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\DriveIcons";
            using (var root = Registry.CurrentUser.OpenSubKey(driveRoot))
            {
                if (root != null)
                {
                    foreach (var drive in root.GetSubKeyNames())
                    {
                        using var defaultKey = Registry.CurrentUser.OpenSubKey($"{driveRoot}\\{drive}\\DefaultIcon");
                        var value = defaultKey?.GetValue(null) as string;
                        data[$"Drive:{drive}"] = value;
                    }
                }
            }

            // Backup shell icons under HKCU\Software\Classes\CLSID
            const string clsidRoot = "Software\\Classes\\CLSID";
            var shellService = new ShellIconService();
            foreach (var kvp in shellService.Targets)
            {
                var name = kvp.Key;
                var clsid = kvp.Value.Clsid;
                using var defaultKey = Registry.CurrentUser.OpenSubKey($"{clsidRoot}\\{clsid}\\DefaultIcon");
                var value = defaultKey?.GetValue(null) as string;
                data[$"Shell:{name}"] = value;
            }

            // Write backup to file
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "Revenant Theme Studio");
            Directory.CreateDirectory(dir);
            var backupPath = Path.Combine(dir, "registry_backup.json");
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(backupPath, json);
        }
    }
}