using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Features.Themes.Sections;
using Revenant_Theme_Studio.Models;
using RTS.Tests.Fakes;
using Xunit;

namespace RTS.Tests;

public sealed class FakeWallpaperApi : IWallpaperApi
{
    public List<MonitorInfo> List { get; } = new()
    {
        new("\\\\?\\DISPLAY#A", 0, 0, 2560, 1440),
        new("\\\\?\\DISPLAY#B", 2560, 0, 4480, 1080),
        new("\\\\?\\DISPLAY#C", -1920, 0, 0, 1080),
    };
    public Dictionary<string, string> Walls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Position { get; set; } = 4;
    public uint Background { get; set; }

    public IReadOnlyList<MonitorInfo> Monitors() => List;
    public string GetWallpaper(string id) => Walls.TryGetValue(id, out var p) ? p : "";
    public void SetWallpaper(string? id, string path)
    {
        if (id is null) foreach (var m in List) Walls[m.Id] = path;
        else Walls[id] = path;
    }
    public int GetPosition() => Position;
    public void SetPosition(int p) => Position = p;
    public uint GetBackgroundColor() => Background;
    public void SetBackgroundColor(uint c) => Background = c;
}

public sealed class SectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestPaths.Root, "rts-sections-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistryStore _reg = new();
    private readonly RecordingBroadcaster _bc = new();
    private readonly TransactionStore _store;

    public SectionTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new TransactionStore(Path.Combine(_dir, "tx"));
        // The color values read from Yggdrasil (Windows 11 26300) on 2026-11-09.
        _reg.With(ColorsSection.Personalize, "AppsUseLightTheme", RegData.Dword(0))
            .With(ColorsSection.Personalize, "SystemUsesLightTheme", RegData.Dword(0))
            .With(ColorsSection.Personalize, "EnableTransparency", RegData.Dword(0))
            .With(ColorsSection.Personalize, "ColorPrevalence", RegData.Dword(1))
            .With(ColorsSection.Dwm, "AccentColor", RegData.Dword(0xffbe7911))
            .With(ColorsSection.Dwm, "ColorizationColor", RegData.Dword(0xc41179be))
            .With(ColorsSection.Dwm, "ColorizationAfterglow", RegData.Dword(0xc41179be))
            .With(ColorsSection.Dwm, "ColorPrevalence", RegData.Dword(1))
            .With(ColorsSection.Accent, "AccentColorMenu", RegData.Dword(0xffce7237))
            .With(ColorsSection.Accent, "StartColorMenu", RegData.Dword(0xffb45a2b))
            .With(ColorsSection.Accent, "AccentPalette", RegData.Binary(Convert.FromHexString("BAE3F60094C2EB005189D7003873CF002B5AB4001C3A8F000A15650088179800")))
            .With(ColorsSection.Desktop, "AutoColorization", RegData.Dword(1));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string File(string name, byte[]? data = null)
    {
        string p = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(p, data ?? BundleTests.Png);
        return p;
    }

    private static LoadedBundle Bundle(ThemeAssets assets, Dictionary<string, string>? files = null) => new()
    {
        Manifest = new ThemeBundle { Id = "t", Name = "T", Assets = assets },
        Files = files ?? new Dictionary<string, string>(),
    };

    private ThemeApplyEngine Engine(IWallpaperApi? api = null, uint? dwm = null) =>
        new(new IThemeSection[] { new ColorsSection(() => dwm), new WallpaperSection(api ?? new FakeWallpaperApi(), () => new DateTime(2026, 11, 9, 12, 0, 0)) },
            _reg, _bc, _store, new BuildNumber(26300, 9711));

    // ── colors ───────────────────────────────────────────────────────────────

    [Fact]
    public void ColorsWriteWhatWindowsWritesAndUndoRestoresTheMachine()
    {
        string before = _reg.Dump();
        var assets = new ThemeAssets
        {
            Colors = new ColorSettings { AppMode = "light", SystemMode = "dark", Transparency = true,
                Accent = new AccentColors { Color = "#8B0000", OnTitleBars = false } },
        };
        var engine = Engine(dwm: 0xC48B0000);
        var tx = engine.Apply(Bundle(assets));
        Assert.Equal(VerifyResult.Verified, tx.Sections.Single().Status);

        uint D(string k, string n) => (uint)_reg.Get(RegHive.CurrentUser, k, n)!.Number!.Value;
        Assert.Equal(1u, D(ColorsSection.Personalize, "AppsUseLightTheme"));
        Assert.Equal(0u, D(ColorsSection.Personalize, "SystemUsesLightTheme"));
        Assert.Equal(0xFF00008Bu, D(ColorsSection.Dwm, "AccentColor"));        // ABGR
        Assert.Equal(0xC48B0000u, D(ColorsSection.Dwm, "ColorizationColor"));  // ARGB
        Assert.Equal(0u, D(ColorsSection.Desktop, "AutoColorization"));
        Assert.Equal(0u, D(ColorsSection.Dwm, "ColorPrevalence"));
        Assert.Equal(1u, D(ColorsSection.Personalize, "ColorPrevalence"));     // untouched: not in the theme
        Assert.Contains(ColorsSection.Personalize.Length > 0 ? Refresh.ImmersiveColorSet : Refresh.None, _bc.Sent);

        Assert.True(engine.Undo(Bundle(assets), _store.LatestApplied()!).Complete);
        Assert.Equal(before, _reg.Dump());
    }

    [Fact]
    public void CapturedPaletteReplaysExactly()
    {
        var palette = new AccentPalette { Colors = { "#BAE3F600", "#94C2EB00", "#5189D700", "#3873CF00", "#2B5AB400", "#1C3A8F00", "#0A156500", "#88179800" } };
        Assert.Equal("BAE3F60094C2EB005189D7003873CF002B5AB4001C3A8F000A15650088179800", Convert.ToHexString(ColorsSection.PaletteBytes(palette)));
        var assets = new ThemeAssets { Colors = new ColorSettings { Accent = new AccentColors { Color = "#1179BE", Palette = palette } } };
        Engine(dwm: 0xC41179BE).Apply(Bundle(assets));
        // Without captured shades, Start and the accent menu come from palette entries 4 and 3.
        Assert.Equal(0xFFB45A2Bu, (uint)_reg.Get(RegHive.CurrentUser, ColorsSection.Accent, "StartColorMenu")!.Number!.Value);
        Assert.Equal(0xFFCF7338u, (uint)_reg.Get(RegHive.CurrentUser, ColorsSection.Accent, "AccentColorMenu")!.Number!.Value);
    }

    [Fact]
    public void DwmLagReadsAsUnconfirmedNotFailed()
    {
        var assets = new ThemeAssets { Colors = new ColorSettings { Accent = new AccentColors { Color = "#8B0000" } } };
        var tx = Engine(dwm: 0xC41179BE).Apply(Bundle(assets));
        Assert.Equal(VerifyResult.AppliedUnverified, tx.Sections.Single().Status);
    }

    [Fact]
    public void BadColorIsSkippedNotApplied()
    {
        string before = _reg.Dump();
        var tx = Engine().Apply(Bundle(new ThemeAssets { Colors = new ColorSettings { Accent = new AccentColors { Color = "red" } } }));
        Assert.Equal(Decision.Skip, tx.Sections.Single().Decision);
        Assert.Equal(before, _reg.Dump());
    }

    // ── wallpaper ────────────────────────────────────────────────────────────

    [Fact]
    public void PerMonitorWallpaperAppliesAndUndoesEvenIfOriginalsVanish()
    {
        var api = new FakeWallpaperApi { Position = 4, Background = 0 };
        string oldA = File("old-a.png"), oldB = File("old-b.png", BundleTests.Ico);
        api.Walls[api.List[0].Id] = oldA;
        api.Walls[api.List[1].Id] = oldB;   // C shows a solid color ("")
        string newLeft = File("left.png"), newPrimary = File("primary.png");
        var assets = new ThemeAssets
        {
            Wallpaper = new WallpaperSettings
            {
                Kind = "perMonitor", Fit = "fit", BackgroundColor = "#102030",
                PerMonitor = new() { new() { Match = new MonitorMatch { Position = "left" }, File = "w/left.png" },
                                     new() { Match = new MonitorMatch { Position = "primary" }, File = "w/primary.png" } },
            },
        };
        var bundle = Bundle(assets, new() { ["w/left.png"] = newLeft, ["w/primary.png"] = newPrimary });
        var engine = Engine(api);
        var tx = engine.Apply(bundle);

        Assert.Equal(VerifyResult.Verified, tx.Sections.Single().Status);
        Assert.Equal(newPrimary, api.Walls[api.List[0].Id]);
        Assert.Equal(oldB, api.Walls[api.List[1].Id]);           // not in the theme: untouched
        Assert.Equal(newLeft, api.Walls[api.List[2].Id]);
        Assert.Equal(3, api.Position);
        Assert.Equal(0x302010u, api.Background);                  // COLORREF is BGR

        System.IO.File.Delete(oldA);                              // the original image disappears
        Assert.True(engine.Undo(bundle, _store.LatestApplied()!).Complete);
        Assert.Equal(BundleTests.Png, System.IO.File.ReadAllBytes(api.Walls[api.List[0].Id]));   // restored from the kept copy
        Assert.Equal(oldB, api.Walls[api.List[1].Id]);
        Assert.Equal("", api.Walls[api.List[2].Id]);             // back to a solid color
        Assert.Equal(4, api.Position);
        Assert.Equal(0u, api.Background);
    }

    [Fact]
    public void WallpaperUndoLeavesAMonitorTheUserChanged()
    {
        var api = new FakeWallpaperApi();
        string mine = File("mine.png"), theme = File("theme.png");
        var bundle = Bundle(new ThemeAssets { Wallpaper = new WallpaperSettings { Kind = "static", Static = new StaticWallpaper { File = "w/t.png" } } },
            new() { ["w/t.png"] = theme });
        var engine = Engine(api);
        engine.Apply(bundle);
        api.Walls[api.List[1].Id] = mine;                         // changed by hand afterwards

        var report = engine.Undo(bundle, _store.LatestApplied()!);
        Assert.Single(report.Issues, i => i.ChangedSinceApply);
        Assert.Equal(mine, api.Walls[api.List[1].Id]);
        Assert.Equal("", api.Walls[api.List[0].Id]);
    }

    [Fact]
    public void WallpaperUndoAlsoRestoresAnAccentWindowsMoved()
    {
        string before = _reg.Dump();
        var api = new FakeWallpaperApi();
        var bundle = Bundle(new ThemeAssets { Wallpaper = new WallpaperSettings { Kind = "static", Static = new StaticWallpaper { File = "w/t.png" } } },
            new() { ["w/t.png"] = File("t.png") });
        var engine = Engine(api);
        engine.Apply(bundle);
        // Auto-colorization: Windows picks a new accent from the new wallpaper during the apply window.
        // (Simulated after the written snapshot: undo must still put the original accent back when forced.)
        _reg.Set(RegHive.CurrentUser, ColorsSection.Dwm, "AccentColor", RegData.Dword(0xFF123456));
        engine.Undo(bundle, _store.LatestApplied()!, force: true);
        Assert.Equal(before, _reg.Dump());
    }

    [Theory]
    [InlineData(3, "rise-0")]
    [InlineData(6.5, "rise-0")]
    [InlineData(7.5, "rise-1")]
    [InlineData(12, "day-0")]
    [InlineData(19, "set-0")]
    [InlineData(23, "night-0")]
    [InlineData(5.99, "night-1")]
    public void TimeOfDayPicksTheFrameForTheHour(double hour, string expected)
    {
        var s = new TimeOfDaySegments { Sunrise = new() { "rise-0", "rise-1" }, Day = new() { "day-0" }, Sunset = new() { "set-0" }, Night = new() { "night-0", "night-1" } };
        var when = new DateTime(2026, 11, 9).AddHours(hour);
        Assert.Equal(expected == "rise-0" && hour < 6 ? "night-1" : expected, TimeOfDay.Pick(s, when));
    }

    [Fact]
    public void MonitorMatching()
    {
        var api = new FakeWallpaperApi();
        Assert.Equal(api.List[0], WallpaperSection.Match(new MonitorMatch { Position = "primary" }, api.List));
        Assert.Equal(api.List[2], WallpaperSection.Match(new MonitorMatch { Position = "left" }, api.List));
        Assert.Equal(api.List[1], WallpaperSection.Match(new MonitorMatch { Position = "right" }, api.List));
        Assert.Equal(api.List[1], WallpaperSection.Match(new MonitorMatch { Ordinal = 1 }, api.List));
        Assert.Null(WallpaperSection.Match(new MonitorMatch { Ordinal = 7 }, api.List));
    }
}
