using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using Revenant_Theme_Studio.Features.Themes.Platform;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    public class WallpaperEngineService : IWallpaperEngineService, IWallpaperApi
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

        /// <summary>Path of the wallpaper currently on the first monitor, for previews.</summary>
        public string? GetCurrentWallpaperPath()
        {
            try
            {
                var desktop = (IDesktopWallpaper)new DesktopWallpaperClass();
                if (desktop.GetMonitorDevicePathCount() == 0) return null;
                var path = desktop.GetWallpaper(desktop.GetMonitorDevicePathAt(0));
                return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
            }
            catch { return null; }
        }

        // ── IWallpaperApi: exact per-monitor read and write for the theme engine ──
        // Each call creates the COM object and releases it, so nothing stays bound
        // to a stale desktop after Explorer restarts.

        private static T WithDesktop<T>(Func<IDesktopWallpaper, T> work)
        {
            var desktop = (IDesktopWallpaper)new DesktopWallpaperClass();
            try { return work(desktop); }
            finally { Marshal.ReleaseComObject(desktop); }
        }

        private static void WithDesktop(Action<IDesktopWallpaper> work) =>
            WithDesktop<object?>(d => { work(d); return null; });

        public IReadOnlyList<MonitorInfo> Monitors() => WithDesktop(d =>
        {
            var list = new List<MonitorInfo>();
            uint count = d.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                var id = d.GetMonitorDevicePathAt(i);
                // Detached monitors are listed too; GetMonitorRECT fails for them.
                try { d.GetMonitorRECT(id, out var r); list.Add(new MonitorInfo(id, r.Left, r.Top, r.Right, r.Bottom)); }
                catch (COMException) { }
            }
            return (IReadOnlyList<MonitorInfo>)list;
        });

        public string GetWallpaper(string monitorId) => WithDesktop(d => d.GetWallpaper(monitorId) ?? "");

        public void SetWallpaper(string? monitorId, string path) => WithDesktop(d => d.SetWallpaper(monitorId!, path));

        public int GetPosition() => WithDesktop(d => (int)d.GetPosition());

        public void SetPosition(int position)
        {
            if (position is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(position));
            WithDesktop(d => d.SetPosition((DesktopWallpaperPosition)position));
        }

        public uint GetBackgroundColor() => WithDesktop(d => d.GetBackgroundColor());

        public void SetBackgroundColor(uint colorRef) => WithDesktop(d => d.SetBackgroundColor(colorRef));

        private static string? GetMonitorDevicePath(IDesktopWallpaper desktop, MonitorProfile target)
        {
            var count = desktop.GetMonitorDevicePathCount();
            if (count == 0) return null;

            for (uint i = 0; i < count; i++)
            {
                var path = desktop.GetMonitorDevicePathAt(i);
                desktop.GetMonitorRECT(path, out var rect);

                if (rect.Left == target.PositionX && rect.Top == target.PositionY)
                    return path;
            }

            // No positional match (DPI or virtual-origin mismatch between EnumDisplayMonitors
            // and the COM device paths). Fall back to this monitor's OWN index — falling back
            // to index 0 would silently land every per-monitor assignment on the primary.
            var index = (uint)Math.Clamp(target.MonitorId, 0, (int)count - 1);
            return desktop.GetMonitorDevicePathAt(index);
        }
    }
}
