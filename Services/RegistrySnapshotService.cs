using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Revenant_Theme_Studio.Services
{
    /// <summary>
    /// Snapshots relevant HKCU registry keys to a JSON file in ProgramData
    /// so the user can always restore their pre-RTS state.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class RegistrySnapshotService
    {
        public static RegistrySnapshotService Instance { get; } = new();

        private static readonly string SnapshotPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Revenant Theme Studio",
            "registry_snapshot.json");

        private static readonly string[] WatchedKeys = new[]
        {
            // Drive and shell icon overrides
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons",

            // Shell namespace CLSIDs (HKCU\Software\Classes)
            @"Software\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\DefaultIcon",
            @"Software\Classes\CLSID\{031E4825-7B94-4dc3-B131-E946B44C8DD5}\DefaultIcon",

            // Windows Library folder icons (HKCU\CLSID — per-user shell namespace)
            @"CLSID\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}\DefaultIcon",  // 3D Objects
            @"CLSID\{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}\DefaultIcon",  // Desktop
            @"CLSID\{d3162b92-9365-467a-956b-92703aca08af}\DefaultIcon",  // Documents
            @"CLSID\{088e3905-0323-4b02-9826-5d99428e115f}\DefaultIcon",  // Downloads
            @"CLSID\{3dfdf296-dbec-4fb4-81d1-6a3438bcf4de}\DefaultIcon",  // Music
            @"CLSID\{24ad3ad4-a569-4530-98e1-ab02f9417aa8}\DefaultIcon",  // Pictures
            @"CLSID\{f86fa3ab-70d2-4fc7-9c99-fcbf05467f3a}\DefaultIcon",  // Videos
        };

        private RegistrySnapshotService() { }

        public bool SnapshotExists => File.Exists(SnapshotPath);

        public void TakeSnapshot()
        {
            if (SnapshotExists) return; // Only snapshot once — the original state
            var snapshot = new Dictionary<string, Dictionary<string, string?>>();

            foreach (var keyPath in WatchedKeys)
            {
                var values = ReadKeyRecursive(keyPath);
                snapshot[keyPath] = values;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SnapshotPath, json);
            }
            catch { /* non-fatal */ }
        }

        private static Dictionary<string, string?> ReadKeyRecursive(string keyPath)
        {
            var result = new Dictionary<string, string?>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                if (key == null) return result;
                foreach (var name in key.GetValueNames())
                    result[name] = key.GetValue(name)?.ToString();

                foreach (var sub in key.GetSubKeyNames())
                {
                    var subValues = ReadKeyRecursive($@"{keyPath}\{sub}");
                    foreach (var kv in subValues)
                        result[$@"{sub}\{kv.Key}"] = kv.Value;
                }
            }
            catch { }
            return result;
        }

        public string? GetSnapshotPath() => SnapshotExists ? SnapshotPath : null;
    }
}
