using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    public class WallpaperEngineService : IWallpaperEngineService
    {
        [ComImport]
        [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                              [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetMonitorDevicePathAt(uint monitorIndex);
            [return: MarshalAs(UnmanagedType.U4)]
            uint GetMonitorDevicePathCount();
            void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT displayRect);
            void SetBackgroundColor([MarshalAs(UnmanagedType.U4)] uint color);
            [return: MarshalAs(UnmanagedType.U4)]
            uint GetBackgroundColor();
            void SetPosition(DesktopWallpaperPosition position);
            [return: MarshalAs(UnmanagedType.I4)]
            DesktopWallpaperPosition GetPosition();
            void SetSlideshow(IntPtr items);
            IntPtr GetSlideshow();
            void SetSlideshowOptions(uint options, uint slideshowTick);
            void GetSlideshowOptions(out uint options, out uint slideshowTick);
            void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, uint direction);
            void GetStatus(out uint status);
            void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
        }

        [ComImport]
        [Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
        private class DesktopWallpaperClass { }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private enum DesktopWallpaperPosition
        {
            Center = 0,
            Tile = 1,
            Stretch = 2,
            Fit = 3,
            Fill = 4,
            Span = 5
        }

        public void ApplyWallpaper(WallpaperImage image, MonitorProfile target)
        {
            if (!File.Exists(image.Path))
                throw new FileNotFoundException("Wallpaper image not found.", image.Path);

            var desktop = (IDesktopWallpaper)new DesktopWallpaperClass();
            var monitorPath = GetMonitorDevicePath(desktop, target);

            if (monitorPath != null)
            {
                desktop.SetPosition(DesktopWallpaperPosition.Fill);
                desktop.SetWallpaper(monitorPath, image.Path);
            }
        }

        public void ApplyToAllMonitors(WallpaperImage image)
        {
            if (!File.Exists(image.Path))
                throw new FileNotFoundException("Wallpaper image not found.", image.Path);

            var desktop = (IDesktopWallpaper)new DesktopWallpaperClass();
            desktop.SetPosition(DesktopWallpaperPosition.Fill);

            var count = desktop.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                var path = desktop.GetMonitorDevicePathAt(i);
                desktop.SetWallpaper(path, image.Path);
            }
        }

        public List<WallpaperImage> GetAvailableWallpapers(WallpaperSource source)
        {
            return source.GetAvailableImages().GetAwaiter().GetResult();
        }

        private static string? GetMonitorDevicePath(IDesktopWallpaper desktop, MonitorProfile target)
        {
            var count = desktop.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                var path = desktop.GetMonitorDevicePathAt(i);
                desktop.GetMonitorRECT(path, out var rect);

                if (rect.Left == target.PositionX && rect.Top == target.PositionY)
                    return path;
            }
            return count > 0 ? desktop.GetMonitorDevicePathAt(0) : null;
        }
    }
}
