using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Revenant_Theme_Studio.Features.Personalization.Services
{
    public class DisplayMode : IEquatable<DisplayMode>
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int RefreshHz { get; init; }
        public int BitsPerPixel { get; init; }

        public string Resolution => $"{Width} × {Height}";
        public override string ToString() => $"{Width} × {Height} @ {RefreshHz}Hz";

        public bool Equals(DisplayMode? other) =>
            other != null && Width == other.Width && Height == other.Height &&
            RefreshHz == other.RefreshHz;
        public override bool Equals(object? obj) => Equals(obj as DisplayMode);
        public override int GetHashCode() => HashCode.Combine(Width, Height, RefreshHz);
    }

    public enum DisplayOrientation
    {
        Landscape = 0,
        Portrait = 1,
        LandscapeFlipped = 2,
        PortraitFlipped = 3
    }

    public class DisplayApplyResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public static DisplayApplyResult Ok() => new() { Success = true };
        public static DisplayApplyResult Fail(string msg) => new() { Success = false, Message = msg };
    }

    /// <summary>
    /// Resolution / refresh / orientation switching via ChangeDisplaySettingsEx —
    /// the same API Windows Settings uses. Apply is dynamic-only (no registry
    /// persist) so a bad mode is fully recoverable: Confirm() persists it,
    /// Revert() restores the mode captured before the change. The ViewModel owns
    /// the countdown clock.
    /// </summary>
    public class DisplayModeService
    {
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int CDS_UPDATEREGISTRY = 0x1;
        private const int CDS_TEST = 0x2;
        private const int DISP_CHANGE_SUCCESSFUL = 0;

        private const int DM_PELSWIDTH = 0x80000;
        private const int DM_PELSHEIGHT = 0x100000;
        private const int DM_DISPLAYFREQUENCY = 0x400000;
        private const int DM_DISPLAYORIENTATION = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(string? lpszDeviceName,
            int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName,
            ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType,
            out uint dpiX, out uint dpiY);

        /// <summary>Effective scale (100/125/150...) for the monitor whose rect
        /// contains the given desktop-coordinate center point. Read-only for now —
        /// per-monitor scale override is a later, registry-touching feature.</summary>
        public static int GetScalePercent(int centerX, int centerY)
        {
            try
            {
                const uint MONITOR_DEFAULTTONEAREST = 2;
                const int MDT_EFFECTIVE_DPI = 0;
                var h = MonitorFromPoint(new POINT { X = centerX, Y = centerY },
                    MONITOR_DEFAULTTONEAREST);
                if (h != IntPtr.Zero &&
                    GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
                    return (int)Math.Round(dpiX * 100.0 / 96.0);
            }
            catch { }
            return 100;
        }

        private static DEVMODE NewDevMode() => new()
        {
            dmDeviceName = new string('\0', 32),
            dmFormName = new string('\0', 32),
            dmSize = (ushort)Marshal.SizeOf<DEVMODE>()
        };

        // Pre-change DEVMODE per device, captured at ApplyDynamic time.
        private readonly Dictionary<string, DEVMODE> _revertModes = new();

        /// <summary>All modes the driver enumerates for this device, best-first,
        /// filtered to the current color depth and sane sizes.</summary>
        public List<DisplayMode> GetModes(string deviceName)
        {
            var modes = new HashSet<DisplayMode>();
            var current = GetCurrentMode(deviceName);

            var dm = NewDevMode();
            for (int i = 0; EnumDisplaySettingsEx(deviceName, i, ref dm, 0); i++)
            {
                if (current != null && dm.dmBitsPerPel != current.BitsPerPixel) continue;
                if (dm.dmPelsWidth < 800 || dm.dmPelsHeight < 600) continue;

                modes.Add(new DisplayMode
                {
                    Width = (int)dm.dmPelsWidth,
                    Height = (int)dm.dmPelsHeight,
                    RefreshHz = (int)dm.dmDisplayFrequency,
                    BitsPerPixel = (int)dm.dmBitsPerPel
                });
            }

            return modes
                .OrderByDescending(m => m.Width * m.Height)
                .ThenByDescending(m => m.RefreshHz)
                .ToList();
        }

        public DisplayMode? GetCurrentMode(string deviceName)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
                return null;

            return new DisplayMode
            {
                Width = (int)dm.dmPelsWidth,
                Height = (int)dm.dmPelsHeight,
                RefreshHz = (int)dm.dmDisplayFrequency,
                BitsPerPixel = (int)dm.dmBitsPerPel
            };
        }

        public DisplayOrientation GetCurrentOrientation(string deviceName)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
                return DisplayOrientation.Landscape;
            return (DisplayOrientation)dm.dmDisplayOrientation;
        }

        /// <summary>
        /// Apply without persisting — the screen changes now, the registry doesn't.
        /// Captures the pre-change mode for Revert(). Runs CDS_TEST first.
        /// </summary>
        public DisplayApplyResult ApplyDynamic(string deviceName, DisplayMode mode,
            DisplayOrientation orientation)
        {
            var before = NewDevMode();
            if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref before, 0))
                return DisplayApplyResult.Fail("Could not read the current display mode.");

            var dm = before;
            var currentOrientation = (DisplayOrientation)before.dmDisplayOrientation;

            dm.dmPelsWidth = (uint)mode.Width;
            dm.dmPelsHeight = (uint)mode.Height;
            dm.dmDisplayFrequency = (uint)mode.RefreshHz;
            dm.dmDisplayOrientation = (uint)orientation;

            // Rotating between landscape and portrait swaps the axes.
            bool wasPortrait = currentOrientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped;
            bool isPortrait = orientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped;
            if (wasPortrait != isPortrait)
                (dm.dmPelsWidth, dm.dmPelsHeight) = (dm.dmPelsHeight, dm.dmPelsWidth);

            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_DISPLAYORIENTATION;

            int test = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (test != DISP_CHANGE_SUCCESSFUL)
                return DisplayApplyResult.Fail($"The driver rejected this mode (code {test}).");

            int result = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            if (result != DISP_CHANGE_SUCCESSFUL)
                return DisplayApplyResult.Fail($"Mode change failed (code {result}).");

            _revertModes[deviceName] = before;
            return DisplayApplyResult.Ok();
        }

        /// <summary>Persist the currently applied mode to the registry.</summary>
        public DisplayApplyResult Confirm(string deviceName)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
                return DisplayApplyResult.Fail("Could not read the applied mode.");

            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_DISPLAYORIENTATION;
            int result = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero,
                CDS_UPDATEREGISTRY, IntPtr.Zero);

            _revertModes.Remove(deviceName);
            return result == DISP_CHANGE_SUCCESSFUL
                ? DisplayApplyResult.Ok()
                : DisplayApplyResult.Fail($"Persist failed (code {result}).");
        }

        /// <summary>Restore the mode captured before ApplyDynamic.</summary>
        public DisplayApplyResult Revert(string deviceName)
        {
            if (!_revertModes.TryGetValue(deviceName, out var before))
                return DisplayApplyResult.Fail("Nothing to revert.");

            before.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_DISPLAYORIENTATION;
            int result = ChangeDisplaySettingsEx(deviceName, ref before, IntPtr.Zero, 0, IntPtr.Zero);

            _revertModes.Remove(deviceName);
            return result == DISP_CHANGE_SUCCESSFUL
                ? DisplayApplyResult.Ok()
                : DisplayApplyResult.Fail($"Revert failed (code {result}) — use Win+Ctrl+Shift+B or reboot.");
        }
    }
}
