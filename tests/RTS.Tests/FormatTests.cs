using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Revenant_Theme_Studio.Features.Themes;
using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Formats;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Features.Themes.Sections;
using RTS.Tests.Fakes;
using Xunit;

namespace RTS.Tests;

public sealed class FormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestPaths.Root, "rts-formats-" + Guid.NewGuid().ToString("N"));
    public FormatTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly byte[] Jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1 };

    private string Write(string rel, byte[] data)
    {
        string p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    private string Theme(string body, Encoding enc)
    {
        var bytes = enc.GetPreamble().Concat(enc.GetBytes(body.Replace("\n", "\r\n"))).ToArray();
        return Write("fixture.theme", bytes);
    }

    private const string Fixture = """
        ; scrubbed fixture in the shape Windows 11 writes
        [Theme]
        DisplayName=Bone Night
        [CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\DefaultIcon]
        DefaultValue=%SystemRoot%\System32\imageres.dll,-109
        [CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\DefaultIcon]
        Full=icons\bin-full.ico
        Empty=icons\bin-empty.ico
        [Control Panel\Cursors]
        Arrow=%SystemRoot%\cursors\aero_arrow.cur
        [Control Panel\Desktop]
        Wallpaper=DesktopBackground\night.jpg
        WallpaperStyle =10
        TileWallpaper=0
        [VisualStyles]
        Path=%SystemRoot%\resources\themes\Aero\Aero.msstyles
        ColorizationColor=0X3772CE
        AutoColorization=0
        Transparency=1
        AppMode=Dark
        [MasterThemeSelector]
        MTSM=RJSPBS
        """;

    private void FixtureAssets()
    {
        Write(@"DesktopBackground\night.jpg", Jpg);
        Write(@"icons\bin-full.ico", BundleTests.Ico);
        Write(@"icons\bin-empty.ico", BundleTests.Ico);
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-8")]
    [InlineData("ansi")]
    public void ParsesEveryEncodingWindowsUses(string which)
    {
        var enc = which switch { "utf-16" => Encoding.Unicode, "utf-8" => new UTF8Encoding(true), _ => Encoding.Latin1 };
        var t = ThemeFile.Parse(File.ReadAllBytes(Theme(Fixture, enc)));
        Assert.Equal("Bone Night", t.Get("theme", "displayname"));
        Assert.Equal("10", t.Get("Control Panel\\Desktop", "WallpaperStyle"));   // "Key =value" form
    }

    [Fact]
    public void ImportsWallpaperColorsAndIconsAndNamesWhatItLeftOut()
    {
        FixtureAssets();
        var r = ThemeFileImporter.Import(Theme(Fixture, Encoding.Unicode));
        var a = r.Manifest.Assets;
        Assert.Equal("Bone Night", r.Manifest.Name);
        Assert.Equal("fill", a.Wallpaper!.Fit);
        Assert.Equal("wallpapers/1.jpg", a.Wallpaper.Static!.File);
        Assert.Equal("#3772CE", a.Colors!.Accent!.Color);
        Assert.Equal("dark", a.Colors.AppMode);
        Assert.True(a.Colors.Transparency);
        Assert.Contains(a.Icons!.Targets, t => t.File == @"%SystemRoot%\System32\imageres.dll,-109");
        Assert.Contains(a.Icons.Targets, t => t.FullFile is not null && t.File!.StartsWith("icons/"));
        Assert.Contains(r.Notes, n => n.StartsWith("Cursors"));
    }

    [Fact]
    public void BuiltInDarkThemeImports()
    {
        var r = ThemeFileImporter.Import(@"C:\Windows\Resources\Themes\dark.theme");
        Assert.Equal("dark", r.Manifest.Name);                       // DisplayName is a resource string, so the file name is used
        Assert.NotNull(r.Manifest.Assets.Wallpaper);
        Assert.StartsWith("%SystemRoot%", r.Manifest.Assets.Wallpaper!.Static!.File, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(r.Files);                                        // system images stay references, nothing copied
    }

    [Fact]
    public void ExportHasEverySectionWindowsRequiresAndReimports()
    {
        FixtureAssets();
        var lib = new ThemeLibrary(Path.Combine(_dir, "lib"));
        var (entry, _) = lib.Import(Theme(Fixture, Encoding.Unicode));
        string themePath = ThemeFileExporter.Export(lib.Load(entry), Path.Combine(_dir, "out"));

        var bytes = File.ReadAllBytes(themePath);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);          // UTF-16 LE with BOM
        var t = ThemeFile.Parse(bytes);
        Assert.NotNull(t.Section("Control Panel\\Desktop"));
        Assert.NotNull(t.Section("VisualStyles"));
        Assert.NotNull(t.Section("MasterThemeSelector"));
        Assert.Equal("10", t.Get("Control Panel\\Desktop", "WallpaperStyle"));
        Assert.Equal("0XC43772CE", t.Get("VisualStyles", "ColorizationColor"));
        Assert.True(File.Exists(t.Get("Control Panel\\Desktop", "Wallpaper")));

        var again = ThemeFileImporter.Import(themePath);
        Assert.Equal("#3772CE", again.Manifest.Assets.Colors!.Accent!.Color);
    }

    [Fact]
    public void ThemePackExtractsOnlyThemeFilesAndNeverEscapes()
    {
        FixtureAssets();
        string src = Path.Combine(_dir, "packsrc");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "pack.theme"), Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Fixture.Replace("\n", "\r\n"))).ToArray());
        File.WriteAllBytes(Path.Combine(src, "night.jpg"), Jpg);
        File.WriteAllBytes(Path.Combine(src, "evil.exe"), new byte[] { (byte)'M', (byte)'Z', 0, 0 });
        string ddf = Path.Combine(src, "make.ddf");
        File.WriteAllText(ddf, string.Join("\r\n",
            ".Set CabinetNameTemplate=test.themepack", $".Set DiskDirectoryTemplate={_dir}", ".Set Cabinet=on", ".Set Compress=on",
            ".Set CompressionType=MSZIP",
            $"\"{Path.Combine(src, "pack.theme")}\" \"pack.theme\"",
            $"\"{Path.Combine(src, "night.jpg")}\" \"DesktopBackground\\night.jpg\"",
            $"\"{Path.Combine(src, "night.jpg")}\" \"..\\..\\escape.jpg\"",
            $"\"{Path.Combine(src, "evil.exe")}\" \"evil.exe\""));
        using (var p = Process.Start(new ProcessStartInfo(@"C:\Windows\System32\makecab.exe", $"/F \"{ddf}\"")
               { WorkingDirectory = src, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        { p.StandardOutput.ReadToEnd(); p.WaitForExit(); Assert.Equal(0, p.ExitCode); }

        string work = Path.Combine(_dir, "unpacked");
        var r = ThemePackImporter.Import(Path.Combine(_dir, "test.themepack"), work);
        Assert.Equal("wallpapers/1.jpg", r.Manifest.Assets.Wallpaper!.Static!.File);
        Assert.False(File.Exists(Path.Combine(work, "evil.exe")));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(work, "..", "..", "escape.jpg"))));
        Assert.All(Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories),
            f => Assert.StartsWith(Path.GetFullPath(work), Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Notes, n => n.Contains("left out"));
    }

    private string Ddw(bool missingImage = false, string folder = "a")
    {
        string path = Path.Combine(_dir, folder, "Mojave_Desert.ddw");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("theme.json").Open()))
            w.Write("{\"imageFilename\":\"mojave_*.jpg\",\"imageCredits\":\"Apple\",\"dayHighlight\":2,\"sunriseImageList\":[1,2],\"dayImageList\":[2,3],\"sunsetImageList\":[4],\"nightImageList\":[5,1]}");
        foreach (int i in new[] { 1, 2, 3, 4, 5 })
            if (!(missingImage && i == 4))
                using (var s = zip.CreateEntry($"mojave_{i}.jpg").Open()) s.Write(Jpg);
        return path;
    }

    [Fact]
    public void DdwBecomesATimeOfDayWallpaper()
    {
        var r = DdwImporter.Import(Ddw(), Path.Combine(_dir, "ddw"));
        Assert.Equal("Mojave Desert", r.Manifest.Name);
        var seg = r.Manifest.Assets.Wallpaper!.TimeOfDay!.Segments;
        Assert.Equal(new[] { "wallpapers/mojave_1.jpg", "wallpapers/mojave_2.jpg" }, seg.Sunrise);
        Assert.Equal(new[] { "wallpapers/mojave_5.jpg", "wallpapers/mojave_1.jpg" }, seg.Night);
        Assert.Equal(5, r.Files.Count);

        var lib = new ThemeLibrary(Path.Combine(_dir, "lib"));
        var (entry, _) = lib.Import(Ddw(folder: "b"));
        Assert.Equal("Time-of-day wallpaper", entry.Summary);
    }

    [Fact]
    public void DdwWithAMissingImageIsRefused() =>
        Assert.Throws<InvalidDataException>(() => DdwImporter.Import(Ddw(missingImage: true), Path.Combine(_dir, "ddw2")));

    [Fact]
    public void CaptureMakesABundleTheReaderAccepts()
    {
        var reg = new FakeRegistryStore()
            .With(ColorsSection.Personalize, "AppsUseLightTheme", RegData.Dword(0))
            .With(ColorsSection.Dwm, "AccentColor", RegData.Dword(0xffbe7911))
            .With(ColorsSection.Accent, "AccentPalette", RegData.Binary(Convert.FromHexString("BAE3F60094C2EB005189D7003873CF002B5AB4001C3A8F000A15650088179800")))
            .With(ColorsSection.Desktop, "AutoColorization", RegData.Dword(0))
            .With(@"Software\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\DefaultIcon", "", RegData.Str(Write("pc.ico", BundleTests.Ico) + ",0"));
        var api = new FakeWallpaperApi();
        string wall = Write("wall.jpg", Jpg);
        foreach (var m in api.List) api.Walls[m.Id] = wall;

        var r = ThemeCapture.Capture("My Look", reg, api);
        Assert.Equal("static", r.Manifest.Assets.Wallpaper!.Kind);
        Assert.Equal("#1179BE", r.Manifest.Assets.Colors!.Accent!.Color);
        Assert.Equal("#3873CF00", r.Manifest.Assets.Colors.Accent.Palette!.Colors[3]);
        Assert.Contains(r.Manifest.Assets.Icons!.Targets, t => t.Match == "This PC");

        var lib = new ThemeLibrary(Path.Combine(_dir, "lib"));
        var entry = lib.Add(r);
        var loaded = lib.Load(entry);
        Assert.Equal(Jpg, File.ReadAllBytes(loaded.Resolve(loaded.Manifest.Assets.Wallpaper!.Static!.File)));
    }

    [Fact]
    public void LibraryRemoveKeepsStagedFilesForAppliedThemes()
    {
        FixtureAssets();
        var lib = new ThemeLibrary(Path.Combine(_dir, "lib"));
        var (entry, _) = lib.Import(Theme(Fixture, Encoding.Unicode));
        string staged = lib.Load(entry).Resolve("wallpapers/1.jpg");
        lib.Remove(entry);
        Assert.Empty(lib.List());
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void ReimportingTheSameFileDoesNotDuplicate()
    {
        FixtureAssets();
        var lib = new ThemeLibrary(Path.Combine(_dir, "lib"));
        var (a, _) = lib.Import(Theme(Fixture, Encoding.Unicode));
        string bundle = Path.Combine(_dir, "copy.rtstheme");
        File.Copy(a.BundlePath, bundle);
        lib.Import(bundle);
        Assert.Single(lib.List());
    }
}
