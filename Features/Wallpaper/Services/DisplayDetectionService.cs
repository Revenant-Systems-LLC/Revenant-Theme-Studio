using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    public class DisplayDetectionService : IDisplayDetectionService
    {
        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
            MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor,
            ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private const uint MONITORINFOF_PRIMARY = 1;

        public List<MonitorProfile> GetMonitorConfiguration()
        {
            var monitors = new List<MonitorProfile>();
            int index = 0;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                {
                    var info = new MONITORINFOEX();
                    info.cbSize = Marshal.SizeOf<MONITORINFOEX>();

                    if (GetMonitorInfo(hMonitor, ref info))
                    {
                        monitors.Add(new MonitorProfile
                        {
                            MonitorId = index,
                            DeviceName = info.szDevice,
                            Width = info.rcMonitor.Right - info.rcMonitor.Left,
                            Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                            PositionX = info.rcMonitor.Left,
                            PositionY = info.rcMonitor.Top
                        });
                        index++;
                    }
                    return true;
                }, IntPtr.Zero);

            return monitors;
        }

        public MonitorProfile GetPrimaryMonitor()
        {
            MonitorProfile? primary = null;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                {
                    var info = new MONITORINFOEX();
                    info.cbSize = Marshal.SizeOf<MONITORINFOEX>();

                    if (GetMonitorInfo(hMonitor, ref info) && (info.dwFlags & MONITORINFOF_PRIMARY) != 0)
                    {
                        primary = new MonitorProfile
                        {
                            MonitorId = 0,
                            DeviceName = info.szDevice,
                            Width = info.rcMonitor.Right - info.rcMonitor.Left,
                            Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                            PositionX = info.rcMonitor.Left,
                            PositionY = info.rcMonitor.Top
                        };
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

            return primary ?? new MonitorProfile
            {
                MonitorId = 0,
                DeviceName = @"\\.\DISPLAY1",
                Width = 1920,
                Height = 1080,
                PositionX = 0,
                PositionY = 0
            };
        }
    }
}
