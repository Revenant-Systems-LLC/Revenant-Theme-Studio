using System;
using System.Collections.Generic;

namespace Revenant_Theme_Studio.Features.Wallpaper
{
    // Monitor configuration classes
    public class MonitorProfile
    {
        public int MonitorId { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public int PositionX { get; set; }
        public int PositionY { get; set; }
    }

    public class MonitorLayout
    {
        public List<MonitorProfile> Monitors { get; set; } = new List<MonitorProfile>();
        public LayoutMode Mode { get; set; }
    }

    // Wallpaper assignment and management
    public class WallpaperAssignment
    {
        public string WallpaperPath { get; set; } = string.Empty;
        public List<int> TargetMonitors { get; set; } = new List<int>();
        public DateTime AssignedAt { get; set; }
        public string AssignedBy { get; set; } = string.Empty;
    }

    // Service interfaces
    public interface IWallpaperEngineService
    {
        void ApplyWallpaper(WallpaperImage image, MonitorProfile target);
        List<WallpaperImage> GetAvailableWallpapers(WallpaperSource source);
    }

    public interface IDisplayDetectionService
    {
        List<MonitorProfile> GetMonitorConfiguration();
        MonitorProfile GetPrimaryMonitor();
    }

    // Enums
    public enum LayoutMode
    {
        PerMonitor,
        Span,
        Grouped,
        UnifiedCanvas
    }
}