using System;
using System.IO;
using System.Text.Json;

namespace Revenant_Theme_Studio.Services
{
    public class AppSettingsService
    {
        private readonly string _settingsPath;
        private SettingsData _data;

        public AppSettingsService(string rootPath)
        {
            _settingsPath = Path.Combine(rootPath, "settings.json");
            _data = Load();
        }

        /// <summary>
        /// User-configured fallback icon path (absolute path to an .ico file).
        /// Null means "no user default set."
        /// Wire to UI separately; set by editing settings.json directly for now.
        /// </summary>
        public string? DefaultFallbackIconPath
        {
            get => _data.DefaultFallbackIconPath;
            set { _data.DefaultFallbackIconPath = value; Save(); }
        }

        private SettingsData Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    return JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
                }
            }
            catch { }
            return new SettingsData();
        }

        private void Save()
        {
            try
            {
                File.WriteAllText(_settingsPath,
                    JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private sealed class SettingsData
        {
            public string? DefaultFallbackIconPath { get; set; }
        }
    }
}
