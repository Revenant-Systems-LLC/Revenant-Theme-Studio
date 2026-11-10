using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Features.Themes.Sections
{
    /// <summary>
    /// Light/dark mode, transparency and accent color. Writes the same per-user values Windows'
    /// Personalization page writes (observed on 26300), with no per-write broadcast; the engine
    /// sends ImmersiveColorSet once. AccentPalette is only ever replayed from a capture (spec D7).
    /// </summary>
    public sealed class ColorsSection : RegistrySection
    {
        public const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        public const string Dwm = @"Software\Microsoft\Windows\DWM";
        public const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
        public const string Desktop = @"Control Panel\Desktop";

        /// <summary>Every accent value Windows (or auto-colorization) can change. Wallpaper watches these too.</summary>
        public static readonly (string Key, string Name)[] AccentValues =
        {
            (Dwm, "AccentColor"), (Dwm, "ColorizationColor"), (Dwm, "ColorizationAfterglow"),
            (Accent, "AccentColorMenu"), (Accent, "StartColorMenu"), (Accent, "AccentPalette"),
            (Desktop, "AutoColorization"),
        };

        private readonly Func<uint?> _dwmColor;

        /// <param name="dwmColor">Reads DWM's live colorization color (0xAARRGGBB); swappable for tests.</param>
        public ColorsSection(Func<uint?>? dwmColor = null) => _dwmColor = dwmColor ?? ReadDwmColor;

        public override string Key => "colors";
        public override int Order => 60;
        protected override Refresh RefreshNeeded => Refresh.ImmersiveColorSet;

        public override SectionPlan? Plan(ApplyContext ctx)
        {
            var c = ctx.Assets.Colors;
            if (c is null) return null;
            if (Gates.Check(Key, c.Gate, ctx.Build) is { } gated) return gated;
            try { _ = Intended(ctx); }
            catch (FormatException ex) { return new SectionPlan(Key, Decision.Skip, ex.Message); }
            if (Intended(ctx).Count == 0) return new SectionPlan(Key, Decision.Skip, "nothing to change");
            return new SectionPlan(Key, Decision.Apply);
        }

        protected override IReadOnlyList<(string Key, string Name, RegData? Data)> Intended(ApplyContext ctx)
        {
            var c = ctx.Assets.Colors!;
            var list = new List<(string, string, RegData?)>();

            if (Mode(c.AppMode, "appMode") is { } app) list.Add((Personalize, "AppsUseLightTheme", RegData.Dword(app)));
            if (Mode(c.SystemMode, "systemMode") is { } sys) list.Add((Personalize, "SystemUsesLightTheme", RegData.Dword(sys)));
            if (c.Transparency is { } t) list.Add((Personalize, "EnableTransparency", RegData.Dword(t ? 1u : 0u)));

            var a = c.Accent;
            if (a is not null)
            {
                if (a.Mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add((Desktop, "AutoColorization", RegData.Dword(1)));
                }
                else if (a.Color is not null)
                {
                    var (r, g, b) = Hex(a.Color, "accent color");
                    uint abgr = 0xFF000000 | ((uint)b << 16) | ((uint)g << 8) | r;
                    uint argb = 0xC4000000 | ((uint)r << 16) | ((uint)g << 8) | b;
                    byte[]? palette = a.Palette is null ? null : PaletteBytes(a.Palette);

                    list.Add((Desktop, "AutoColorization", RegData.Dword(0)));
                    list.Add((Dwm, "AccentColor", RegData.Dword(abgr)));
                    list.Add((Dwm, "ColorizationColor", RegData.Dword(argb)));
                    list.Add((Dwm, "ColorizationAfterglow", RegData.Dword(argb)));
                    list.Add((Accent, "AccentColorMenu", RegData.Dword(Shade(a.AccentMenu, palette, 3, abgr))));
                    list.Add((Accent, "StartColorMenu", RegData.Dword(Shade(a.StartMenu, palette, 4, abgr))));
                    if (palette is not null) list.Add((Accent, "AccentPalette", RegData.Binary(palette)));
                }
                if (a.OnStartTaskbar is { } st) list.Add((Personalize, "ColorPrevalence", RegData.Dword(st ? 1u : 0u)));
                if (a.OnTitleBars is { } tb) list.Add((Dwm, "ColorPrevalence", RegData.Dword(tb ? 1u : 0u)));
            }
            return list;
        }

        public override VerifyResult Verify(ApplyContext ctx, out string? detail)
        {
            var result = base.Verify(ctx, out detail);
            if (result != VerifyResult.Verified) return result;

            // The registry is right; check DWM drew it too. A lag right after the broadcast is
            // possible, so a mismatch reads as "applied, not yet confirmed", never as a failure.
            var a = ctx.Assets.Colors!.Accent;
            if (a?.Color is not null && !a.Mode.Equals("auto", StringComparison.OrdinalIgnoreCase) && _dwmColor() is { } live)
            {
                var (r, g, b) = Hex(a.Color, "accent color");
                uint want = ((uint)r << 16) | ((uint)g << 8) | b;
                if ((live & 0xFFFFFF) != want)
                {
                    detail = $"Windows still draws #{live & 0xFFFFFF:X6}; it may need a moment or an Explorer restart";
                    return VerifyResult.AppliedUnverified;
                }
            }
            return VerifyResult.Verified;
        }

        private static uint? Mode(string? mode, string field) => mode?.ToLowerInvariant() switch
        {
            null or "unchanged" => null,
            "light" => 1u,
            "dark" => 0u,
            _ => throw new FormatException($"{field} must be light, dark or unchanged"),
        };

        /// <summary>#RRGGBB to its channels.</summary>
        public static (byte R, byte G, byte B) Hex(string hex, string what)
        {
            var s = hex.Trim().TrimStart('#');
            if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
                throw new FormatException($"{what} \"{hex}\" is not #RRGGBB");
            return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        /// <summary>8 × #RRGGBBAA to the 32-byte AccentPalette value (R,G,B,A per entry).</summary>
        public static byte[] PaletteBytes(AccentPalette p)
        {
            if (p.Colors.Count != 8) throw new FormatException("an accent palette needs exactly 8 colors");
            var bytes = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                var s = p.Colors[i].Trim().TrimStart('#');
                if (s.Length != 8 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
                    throw new FormatException($"palette color \"{p.Colors[i]}\" is not #RRGGBBAA");
                bytes[i * 4] = (byte)(v >> 24); bytes[i * 4 + 1] = (byte)(v >> 16);
                bytes[i * 4 + 2] = (byte)(v >> 8); bytes[i * 4 + 3] = (byte)v;
            }
            return bytes;
        }

        private static uint Shade(string? captured, byte[]? palette, int entry, uint fallbackAbgr)
        {
            if (captured is not null)
            {
                var (r, g, b) = Hex(captured, "accent shade");
                return 0xFF000000 | ((uint)b << 16) | ((uint)g << 8) | r;
            }
            if (palette is not null)
                return 0xFF000000 | ((uint)palette[entry * 4 + 2] << 16) | ((uint)palette[entry * 4 + 1] << 8) | palette[entry * 4];
            return fallbackAbgr;
        }

        private static uint? ReadDwmColor()
        {
            try { return DwmGetColorizationColor(out uint c, out _) == 0 ? c : null; }
            catch { return null; }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetColorizationColor(out uint color, out bool opaqueBlend);
    }
}
