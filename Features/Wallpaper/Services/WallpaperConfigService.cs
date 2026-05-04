using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    public class WallpaperConfigService
    {
        private readonly string _configPath;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public WallpaperConfigService()
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevenantThemeStudio");
            Directory.CreateDirectory(appData);
            _configPath = Path.Combine(appData, "wallpaper-config.json");
        }

        public WallpaperConfig Load()
        {
            if (!File.Exists(_configPath))
                return new WallpaperConfig();

            try
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<WallpaperConfig>(json, _jsonOptions)
                       ?? new WallpaperConfig();
            }
            catch
            {
                return new WallpaperConfig();
            }
        }

        public void Save(WallpaperConfig config)
        {
            var json = JsonSerializer.Serialize(config, _jsonOptions);
            File.WriteAllText(_configPath, json);
        }
    }

    public class WallpaperConfig
    {
        public List<SavedSource> Sources { get; set; } = new();
        public bool AutoApply { get; set; } = false;
        public bool UpscaleEnabled { get; set; } = true;
        public int RefreshIntervalMinutes { get; set; } = 0;
    }

    public class SavedSource
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public SourceType Type { get; set; }
        public SelectionMode SelectionMode { get; set; } = SelectionMode.First;
        public int SelectionPosition { get; set; } = 0;
        public bool IsActive { get; set; } = true;
    }
}
