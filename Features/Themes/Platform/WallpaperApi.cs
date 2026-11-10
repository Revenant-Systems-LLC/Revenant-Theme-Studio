using System.Collections.Generic;

namespace Revenant_Theme_Studio.Features.Themes.Platform
{
    /// <summary>A monitor as IDesktopWallpaper lists it. Ids are device paths: machine-specific, never stored in bundles.</summary>
    public sealed record MonitorInfo(string Id, int Left, int Top, int Right, int Bottom)
    {
        public bool IsPrimary => Left <= 0 && Top <= 0 && Right > 0 && Bottom > 0;
    }

    /// <summary>
    /// Per-monitor wallpaper access (DOC: IDesktopWallpaper). Implemented by
    /// WallpaperEngineService; faked in tests.
    /// </summary>
    public interface IWallpaperApi
    {
        /// <summary>Attached monitors, in the order Windows lists them.</summary>
        IReadOnlyList<MonitorInfo> Monitors();
        /// <summary>The image on a monitor, or "" when it shows a solid color.</summary>
        string GetWallpaper(string monitorId);
        /// <summary>Sets one monitor, or every monitor when <paramref name="monitorId"/> is null.</summary>
        void SetWallpaper(string? monitorId, string path);
        /// <summary>DESKTOP_WALLPAPER_POSITION: 0 center, 1 tile, 2 stretch, 3 fit, 4 fill, 5 span.</summary>
        int GetPosition();
        void SetPosition(int position);
        /// <summary>COLORREF (0x00BBGGRR).</summary>
        uint GetBackgroundColor();
        void SetBackgroundColor(uint colorRef);
    }
}
