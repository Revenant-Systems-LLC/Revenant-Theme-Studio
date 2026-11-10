// TEST 5: a whole theme through the real engine on real Windows: build a bundle, import it into
// the library, apply it (wallpaper, colors, shell and drive icons, a system icon), check what
// Windows reports, undo, and check everything is exactly as before.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Revenant_Theme_Studio.Features.Themes;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.Formats;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Features.Themes.Sections;
using Revenant_Theme_Studio.Features.Wallpaper.Services;
using Revenant_Theme_Studio.Models;

static class ThemeTest
{
    private const string DocsThisPc = "{D3162B92-9365-467A-956B-92703ACA08AF}";
    private const string DocsNav = "{A8CDFF1C-4878-43BE-B5FD-F8091C1C60D0}";
    private const string Desktop = "{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}";

    public static void Run(Report r, string iconsDir, string work)
    {
        r.H("TEST 5: a whole theme through the engine (apply, verify, undo)");
        int checks = 0, passed = 0;
        void Check(bool ok, string what) { checks++; if (ok) passed++; r.L($"  {(ok ? "PASS" : "FAIL")}  {what}"); }

        string bsg = Path.Combine(iconsDir, "black-suede-gold"), jade = Path.Combine(iconsDir, "jade-gold");
        string wall = Path.Combine(work, "theme-night.png");
        RenderWallpaper(wall);

        // ── 1. Build the theme and add it to a library ──
        var result = new ImportResult
        {
            Manifest = new ThemeBundle
            {
                Id = "sandbox-test-5", Name = "Sandbox Test 5", Author = "RTS tests",
                Assets = new ThemeAssets
                {
                    Wallpaper = new WallpaperSettings { Kind = "static", Fit = "fill", Static = new StaticWallpaper { File = "wallpapers/night.png" } },
                    Colors = new ColorSettings
                    {
                        AppMode = "dark", SystemMode = "dark", Transparency = true,
                        Accent = new AccentColors { Color = "#8B0000", OnTitleBars = true, OnStartTaskbar = true },
                    },
                    Icons = new IconSettings
                    {
                        Targets =
                        {
                            new IconTarget { Kind = "shell", Match = "Documents", File = "icons/documents.ico" },
                            new IconTarget { Kind = "shell", Match = "Desktop", File = "icons/desktop.ico" },
                            new IconTarget { Kind = "drive", Match = "C", File = "icons/drive.ico" },
                            new IconTarget { Kind = "system", Group = 2, File = "icons/system-documents.ico" },
                        },
                    },
                },
            },
        };
        result.Files["wallpapers/night.png"] = wall;
        result.Files["icons/documents.ico"] = Path.Combine(bsg, "documents.ico");
        result.Files["icons/desktop.ico"] = Path.Combine(bsg, "desktop-computer.ico");
        result.Files["icons/drive.ico"] = Path.Combine(bsg, "folder.ico");
        result.Files["icons/system-documents.ico"] = Path.Combine(jade, "documents.ico");

        var lib = new ThemeLibrary(@"C:\rts-local\lib");
        var entry = lib.Add(result);
        var bundle = lib.Load(entry);
        Check(true, $"theme built and imported: {entry.Summary}");

        // ── 2. Before ──
        var reg = new RegistryStore();
        var wp = new WallpaperEngineService();
        string mun = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemResources", "imageres.dll.mun");
        string regBefore = Dump(reg), wallBefore = Wallpaper(wp), munBefore = Tests.Sha256(mun);
        byte[] munSdBefore = Tests.Sd(mun), dirSdBefore = Tests.Sd(Path.GetDirectoryName(mun)!);
        uint linksBefore = Tests.LinkCount(mun);
        string docsBefore = Tests.Probe(Tests.DocumentsItems[0].ParsingName);
        r.L($"  before: wallpaper {wallBefore}");
        r.L($"  before: Documents icon {docsBefore}; imageres links {linksBefore}");

        // ── 3. Apply ──
        var store = new TransactionStore(@"C:\rts-local\tx");
        var engine = ThemeEngineFactory.Create(store);
        foreach (var p in engine.Plan(bundle).Plans) r.L($"  plan {p.Key}: {p.Decision}{(p.Reason is null ? "" : $" ({p.Reason})")}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tx = engine.Apply(bundle, entry.BundlePath);
        r.L($"  apply took {sw.ElapsedMilliseconds} ms, state {tx.State}");
        foreach (var s in tx.Sections) r.L($"    {s.Key}: {s.Decision} {s.Status} {s.Detail}");
        foreach (var n in tx.Notes) r.L($"    note: {n}");
        Check(tx.State == TxState.Applied, "transaction applied");
        Check(tx.Sections.All(s => s.Status is VerifyResult.Verified or VerifyResult.AppliedUnverified), "every section verified");

        string staged(string p) => bundle.Resolve(p);
        string docsNow = Tests.Probe(Tests.DocumentsItems[0].ParsingName);
        string navNow = Tests.Probe(Tests.DocumentsItems[1].ParsingName);
        Check(docsNow.StartsWith(staged("icons/documents.ico"), StringComparison.OrdinalIgnoreCase), $"Windows shows the theme's Documents icon (This PC): {docsNow}");
        Check(navNow.StartsWith(staged("icons/documents.ico"), StringComparison.OrdinalIgnoreCase), "Windows shows it in the nav pane too");
        Check(wp.Monitors().All(m => string.Equals(wp.GetWallpaper(m.Id), staged("wallpapers/night.png"), StringComparison.OrdinalIgnoreCase)), "every monitor shows the theme's wallpaper");
        Check(wp.GetPosition() == 4, "wallpaper fit is fill");
        Check(reg.Get(RegHive.CurrentUser, ColorsSection.Personalize, "AppsUseLightTheme")?.Number == 0, "apps are in dark mode");
        uint dwm = DwmColor();
        r.L($"  DWM colorization now #{dwm & 0xFFFFFF:X6}");
        Check((dwm & 0xFFFFFF) == 0x8B0000, "DWM draws the theme's accent color");
        Check(Tests.Sha256(mun) != munBefore, "imageres.dll.mun was patched");
        Check(Tests.Sd(mun).SequenceEqual(munSdBefore), "patched imageres.dll.mun has the original's exact permissions");

        Thread.Sleep(10000);
        var again = engine.Reverify(bundle, tx);
        r.L("  10 s later: " + string.Join(", ", again.Sections.Select(s => $"{s.Key} {s.Status}")));
        Check(again.Sections.All(s => s.Status != VerifyResult.Drifted), "nothing drifted after 10 s");

        // ── 4. Undo ──
        sw.Restart();
        var undo = engine.Undo(bundle, store.LatestApplied()!);
        r.L($"  undo took {sw.ElapsedMilliseconds} ms");
        foreach (var i in undo.Issues) r.L($"    issue: {i.Section}: {i.What}");
        Check(undo.Complete, "undo completed with no issues");
        string regAfter = Dump(reg);
        Check(regAfter == regBefore, "every touched registry value is byte-identical to before");
        if (regAfter != regBefore)
            foreach (var line in regAfter.Split('\n').Except(regBefore.Split('\n'))) r.L($"    now: {line}");
        string wallAfter = Wallpaper(wp);
        Check(wallAfter == wallBefore, $"wallpaper is back: {wallAfter}");
        Check(Tests.Probe(Tests.DocumentsItems[0].ParsingName) == docsBefore, "Documents icon is back to stock");
        Check(Tests.Sha256(mun) == munBefore, "imageres.dll.mun is the Windows original again");
        Check(Tests.Sd(mun).SequenceEqual(munSdBefore), "its owner, group and permissions are the original's");
        Check(Tests.LinkCount(mun) == linksBefore, $"its hard-link count is back to {linksBefore}");
        Check(Tests.Sd(Path.GetDirectoryName(mun)!).SequenceEqual(dirSdBefore), "SystemResources permissions unchanged");
        Check(store.LatestApplied() is null, "no theme is recorded as applied");

        r.L($"TEST 5: {passed}/{checks} checks passed");
    }

    /// <summary>Every value the theme touches, plus the key's existence, one line each.</summary>
    private static string Dump(RegistryStore reg)
    {
        var keys = new List<(string Key, string[]? Only)>
        {
            (ColorsSection.Personalize, null), (ColorsSection.Dwm, null), (ColorsSection.Accent, null),
            (ColorsSection.Desktop, new[] { "AutoColorization", "WallPaper", "WallpaperStyle", "TileWallpaper" }),
            ($@"Software\Classes\CLSID\{DocsThisPc}", null), ($@"Software\Classes\CLSID\{DocsThisPc}\DefaultIcon", null),
            ($@"Software\Classes\CLSID\{DocsNav}", null), ($@"Software\Classes\CLSID\{DocsNav}\DefaultIcon", null),
            ($@"Software\Classes\CLSID\{Desktop}", null), ($@"Software\Classes\CLSID\{Desktop}\DefaultIcon", null),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons", null),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\C", null),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\C\DefaultIcon", null),
        };
        var lines = new List<string>();
        foreach (var (key, only) in keys)
        {
            if (!reg.KeyExists(RegHive.CurrentUser, key)) { lines.Add($"{key}: absent"); continue; }
            foreach (var name in reg.ValueNames(RegHive.CurrentUser, key).Where(n => only is null || only.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n))
                lines.Add($"{key}\\{name} = {reg.Get(RegHive.CurrentUser, key, name)}");
        }
        return string.Join("\n", lines);
    }

    private static string Wallpaper(WallpaperEngineService wp) =>
        string.Join(" | ", wp.Monitors().Select(m => wp.GetWallpaper(m.Id))) + $" (fit {wp.GetPosition()}, background {wp.GetBackgroundColor():X6})";

    private static void RenderWallpaper(string path)
    {
        Shell.RunSta(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new LinearGradientBrush(Color.FromRgb(0x00, 0x24, 0x14), Color.FromRgb(0x5A, 0x0A, 0x0A), 45);
                dc.DrawRectangle(brush, null, new Rect(0, 0, 1920, 1080));
                dc.DrawEllipse(Brushes.Goldenrod, null, new Point(960, 540), 200, 200);
            }
            var bmp = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        });
    }

    private static uint DwmColor() => DwmGetColorizationColor(out uint c, out _) == 0 ? c : 0;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint color, out bool opaque);
}
