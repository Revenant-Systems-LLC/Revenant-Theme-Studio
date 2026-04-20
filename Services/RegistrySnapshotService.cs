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

        // Non-shell watched roots. The shell-target keys are pulled
        // dynamically from ShellIconService so the two stay in sync.
        private static readonly string[] StaticWatchedKeys = new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons",
        };

        private static IEnumerable<string> WatchedKeys
        {
            get
            {
                foreach (var k in StaticWatchedKeys) yield return k;

                // Mirror every shell target the ShellIconService knows about,
                // including the secondary CLSIDs (e.g. Control Panel category view).
                var svc = new ShellIconService();
                foreach (var spec in svc.Targets.Values)
                {
                    yield return BuildShellTargetPath(spec.Scope, spec.Clsid);
                    if (!string.IsNullOrEmpty(spec.SecondaryClsid))
                        yield return BuildShellTargetPath(spec.Scope, spec.SecondaryClsid!);
                }
            }
        }

        private static string BuildShellTargetPath(ShellIconService.ShellRegistryScope scope, string clsid) => scope switch
        {
            ShellIconService.ShellRegistryScope.LibraryShort => $@"CLSID\{clsid}\DefaultIcon",
            ShellIconService.ShellRegistryScope.HkcrOverride => $@"Software\Classes\CLSID\{clsid}\DefaultIcon",
            _ => throw new InvalidOperationException($"Unknown scope: {scope}")
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
