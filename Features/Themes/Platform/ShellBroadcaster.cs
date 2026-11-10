using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Revenant_Theme_Studio.Features.Themes.Platform
{
    /// <summary>What an apply needs refreshed. Sections add to it; the engine sends it once.</summary>
    [Flags]
    public enum Refresh
    {
        None = 0,
        /// <summary>WM_SETTINGCHANGE "ImmersiveColorSet": accent, light/dark, transparency.</summary>
        ImmersiveColorSet = 1,
        /// <summary>WM_SETTINGCHANGE with a null area: catch-all for registry-only settings.</summary>
        Generic = 2,
        /// <summary>SHChangeNotify(SHCNE_ASSOCCHANGED): Explorer re-reads icons.</summary>
        Icons = 4,
    }

    public interface IShellBroadcaster
    {
        /// <summary>Sends every requested refresh once, in a fixed order. Blocks; call off the UI thread.</summary>
        void Send(Refresh what);
    }

    /// <summary>
    /// The real broadcaster (spec F6). SendMessageTimeout to HWND_BROADCAST with SMTO_ABORTIFHUNG,
    /// 2 s per window, so one hung window cannot stall the apply for long.
    /// </summary>
    public sealed class ShellBroadcaster : IShellBroadcaster
    {
        private const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;
        private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
        private const uint SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0;

        public void Send(Refresh what)
        {
            if (what.HasFlag(Refresh.ImmersiveColorSet)) SettingChange("ImmersiveColorSet");
            if (what.HasFlag(Refresh.Generic)) SettingChange(null);
            if (what.HasFlag(Refresh.Icons)) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        private static void SettingChange(string? area) =>
            SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 2000, out _);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string? lParam,
            uint flags, uint timeout, out IntPtr result);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    }

    /// <summary>Records what would have been sent. For tests.</summary>
    public sealed class RecordingBroadcaster : IShellBroadcaster
    {
        public List<Refresh> Sent { get; } = new();
        public void Send(Refresh what) => Sent.Add(what);
    }
}
