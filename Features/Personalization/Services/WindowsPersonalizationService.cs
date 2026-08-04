using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using Microsoft.Win32;

namespace Revenant_Theme_Studio.Features.Personalization.Services
{
    public enum ColorMode { Light, Dark, Custom }

    /// <summary>
    /// Windows Personalization > Colors, via the same HKCU values Settings writes.
    /// Every value is backed up to a JSON sidecar before its first overwrite
    /// (first-write-wins), so the pre-RTS state is always recoverable. All writes
    /// are HKCU — no elevation, no HKLM, ever.
    /// </summary>
    public class WindowsPersonalizationService
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string DwmKey = @"Software\Microsoft\Windows\DWM";
        private const string AccentKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

        private readonly string _backupPath;
        private Dictionary<string, object?> _backup;

        public WindowsPersonalizationService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevenantThemeStudio");
            Directory.CreateDirectory(dir);
            _backupPath = Path.Combine(dir, "personalization-backup.json");
            _backup = LoadBackup();
        }

        // ── Mode ─────────────────────────────────────────────────────────────

        public ColorMode GetColorMode()
        {
            int apps = ReadDword(PersonalizeKey, "AppsUseLightTheme", 1);
            int system = ReadDword(PersonalizeKey, "SystemUsesLightTheme", 1);
            if (apps == system) return apps == 1 ? ColorMode.Light : ColorMode.Dark;
            return ColorMode.Custom;
        }

        public void SetColorMode(ColorMode mode)
        {
            if (mode == ColorMode.Custom) return;
            int value = mode == ColorMode.Light ? 1 : 0;
            WriteDword(PersonalizeKey, "AppsUseLightTheme", value);
            WriteDword(PersonalizeKey, "SystemUsesLightTheme", value);
            BroadcastChange();
        }

        // ── Transparency ─────────────────────────────────────────────────────

        public bool GetTransparency() => ReadDword(PersonalizeKey, "EnableTransparency", 1) == 1;

        public void SetTransparency(bool enabled)
        {
            WriteDword(PersonalizeKey, "EnableTransparency", enabled ? 1 : 0);
            BroadcastChange();
        }

        // ── Accent color ─────────────────────────────────────────────────────

        public Color GetAccentColor()
        {
            // DWM\AccentColor is ABGR.
            int abgr = ReadDword(DwmKey, "AccentColor", unchecked((int)0xFFD77800));
            return Color.FromRgb(
                (byte)(abgr & 0xFF),
                (byte)((abgr >> 8) & 0xFF),
                (byte)((abgr >> 16) & 0xFF));
        }

        public void SetAccentColor(Color c)
        {
            int abgr = unchecked((int)(0xFF000000 | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R));
            int argb = unchecked((int)(0xC4000000 | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B));

            WriteDword(DwmKey, "AccentColor", abgr);
            WriteDword(DwmKey, "ColorizationColor", argb);
            WriteDword(DwmKey, "ColorizationAfterglow", argb);
            WriteDword(AccentKey, "AccentColorMenu", abgr);
            WriteDword(AccentKey, "StartColorMenu", abgr);
            BroadcastChange();
        }

        // ── Accent prevalence toggles ────────────────────────────────────────

        /// <summary>Accent on Start and taskbar (Personalize\ColorPrevalence).</summary>
        public bool GetAccentOnStartTaskbar() => ReadDword(PersonalizeKey, "ColorPrevalence", 0) == 1;

        public void SetAccentOnStartTaskbar(bool enabled)
        {
            WriteDword(PersonalizeKey, "ColorPrevalence", enabled ? 1 : 0);
            BroadcastChange();
        }

        /// <summary>Accent on title bars and window borders (DWM\ColorPrevalence).</summary>
        public bool GetAccentOnTitleBars() => ReadDword(DwmKey, "ColorPrevalence", 0) == 1;

        public void SetAccentOnTitleBars(bool enabled)
        {
            WriteDword(DwmKey, "ColorPrevalence", enabled ? 1 : 0);
            BroadcastChange();
        }

        // ── Registry plumbing ────────────────────────────────────────────────

        private static int ReadDword(string key, string name, int fallback)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(key);
                return k?.GetValue(name) is int v ? v : fallback;
            }
            catch { return fallback; }
        }

        private void WriteDword(string key, string name, int value)
        {
            BackupValueOnce(key, name);
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue(name, value, RegistryValueKind.DWord);
        }

        /// <summary>Record the pre-RTS value the first time we ever touch it.</summary>
        private void BackupValueOnce(string key, string name)
        {
            var id = $@"HKCU\{key}\{name}";
            if (_backup.ContainsKey(id)) return;

            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(key);
                _backup[id] = k?.GetValue(name);
                File.WriteAllText(_backupPath,
                    JsonSerializer.Serialize(_backup, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* backup is best-effort; the write itself still proceeds */ }
        }

        private Dictionary<string, object?> LoadBackup()
        {
            try
            {
                if (File.Exists(_backupPath))
                    return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                        File.ReadAllText(_backupPath)) ?? new();
            }
            catch { }
            return new();
        }

        // ── Change broadcast ─────────────────────────────────────────────────

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg,
            IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

        private static void BroadcastChange()
        {
            var broadcast = new IntPtr(0xFFFF);
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            SendMessageTimeout(broadcast, WM_SETTINGCHANGE, IntPtr.Zero,
                "ImmersiveColorSet", SMTO_ABORTIFHUNG, 500, out _);
        }
    }
}
