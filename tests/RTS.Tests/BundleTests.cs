using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Models;
using Xunit;

namespace RTS.Tests;

public sealed class BundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestPaths.Root, "rts-bundle-" + Guid.NewGuid().ToString("N"));
    public BundleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    internal static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3, 4 };
    internal static readonly byte[] Ico = { 0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0, 4, 0 };
    private static readonly byte[] Exe = { (byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0 };

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>A zip with a manifest built from the given files (listed with real hashes unless told otherwise).</summary>
    internal static MemoryStream Zip(string manifestJson, params (string path, byte[] data)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false))) w.Write(manifestJson);
            foreach (var (path, data) in entries)
                using (var s = zip.CreateEntry(path).Open()) s.Write(data);
        }
        ms.Position = 0;
        return ms;
    }

    internal static string Manifest(string assets, params (string path, byte[] data)[] listed) =>
        "{\"schema\":\"rts.theme/v2\",\"id\":\"test-theme\",\"name\":\"Test\",\"author\":\"t\",\"files\":[" +
        string.Join(",", listed.Select(f => $"{{\"path\":\"{f.path}\",\"sha256\":\"{Hash(f.data)}\",\"bytes\":{f.data.Length}}}")) +
        "],\"assets\":" + assets + "}";

    private LoadedBundle Read(Stream zip) => BundleReader.Read(zip, Path.Combine(_dir, "assets"));

    private static void Refused(Func<object> act, string containing)
    {
        var ex = Assert.Throws<InvalidDataException>(act);
        Assert.Contains(containing, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidBundleStagesFilesUnderTheirHash()
    {
        var f = ("icons/documents.ico", Ico);
        var b = Read(Zip(Manifest("{\"icons\":{\"targets\":[{\"kind\":\"shell\",\"match\":\"Documents\",\"file\":\"icons/documents.ico\"}]}}", f), f));
        var staged = b.Files["icons/documents.ico"];
        Assert.StartsWith(Hash(Ico)[..16], Path.GetFileName(staged));
        Assert.Equal(Ico, File.ReadAllBytes(staged));
        Assert.Equal("Documents", b.Manifest.Assets.Icons!.Targets[0].Match);
    }

    [Theory]
    [InlineData("../evil.ico")]
    [InlineData("icons/../../evil.ico")]
    [InlineData("C:/Windows/evil.ico")]
    [InlineData("icons/a.ico:stream")]
    [InlineData("icons/CON.ico")]
    [InlineData("icons/trailing.ico.")]
    [InlineData("icons\\back.ico")]
    public void PathTricksAreRefused(string path)
    {
        var f = (path, Ico);
        Assert.Throws<InvalidDataException>(() => Read(Zip(Manifest("{}", f), f)));
    }

    [Theory]
    [InlineData("tools/setup.exe")]
    [InlineData("wallpapers/page.html")]
    [InlineData("icons/logo.svg")]
    [InlineData("screensavers/x.scr")]
    [InlineData("script.ps1")]
    public void ExecutableAndScriptTypesAreRefused(string path)
    {
        var f = (path, Exe);
        Refused(() => Read(Zip(Manifest("{}", f), f)), "not allowed");
    }

    [Fact]
    public void AnExecutableRenamedToAnImageIsRefused()
    {
        var f = ("wallpapers/a.png", Exe);
        Refused(() => Read(Zip(Manifest("{}", f), f)), "not a real PNG");
    }

    [Fact]
    public void UnlistedFilesAreRefused()
    {
        var listed = ("icons/a.ico", Ico);
        Refused(() => Read(Zip(Manifest("{}", listed), listed, ("icons/extra.ico", Ico))), "not listed");
    }

    [Fact]
    public void HashMismatchIsRefused()
    {
        var other = Ico.ToArray(); other[^1] ^= 0xFF;
        Refused(() => Read(Zip(Manifest("{}", ("icons/a.ico", Ico)), ("icons/a.ico", other))), "SHA-256");
    }

    [Fact]
    public void ListedButMissingIsRefused() =>
        Refused(() => Read(Zip(Manifest("{}", ("icons/a.ico", Ico)))), "missing");

    [Fact]
    public void UrlsAreRefused() =>
        Refused(() => Read(Zip(Manifest("{\"wallpaper\":{\"static\":{\"file\":\"https://example.com/a.png\"}}}"))), "web or network");

    [Fact]
    public void PersonalPathsAreRefused() =>
        Refused(() => Read(Zip(Manifest("{\"wallpaper\":{\"static\":{\"file\":\"%SystemRoot%\\\\..\\\\Users\\\\dave\\\\a.png\"}}}"))), "allowed Windows folders");

    [Theory]
    [InlineData(@"%SystemRoot%\Web\Wallpaper\Windows\img0.jpg", true)]
    [InlineData(@"%SystemRoot%\System32\imageres.dll,-109", true)]
    [InlineData(@"%SystemRoot%\System32\shell32.dll", true)]
    [InlineData(@"%SystemRoot%\System32\kernel32.dll", false)]
    [InlineData(@"%SystemRoot%\Web\Wallpaper\..\..\x.png", false)]
    [InlineData(@"%SystemRoot%\Web\Wallpaper\a.exe", false)]
    [InlineData(@"C:\Users\x\a.png", false)]
    public void SystemReferences(string reference, bool allowed) =>
        Assert.Equal(allowed, AssetRules.IsAllowedSystemRef(reference));

    [Fact]
    public void UnknownRequiredSectionIsRefused() =>
        Refused(() => Read(Zip(Manifest("{}").Replace("\"files\"", "\"requires\":[\"cursors\"],\"files\""))), "can't apply yet");

    [Fact]
    public void NewerEngineRequirementIsRefused() =>
        Refused(() => Read(Zip(Manifest("{}").Replace("\"files\"", "\"minRtsVersion\":\"9.0.0\",\"files\""))), "or newer");

    [Fact]
    public void UnknownSchemaIsRefused() =>
        Refused(() => Read(Zip(Manifest("{}").Replace("rts.theme/v2", "rts.theme/v9"))), "unknown schema");

    [Fact]
    public void V1BundlesMapIntoIcons()
    {
        const string v1 = "{\"schema\":\"rts.theme/v1\",\"id\":\"old\",\"name\":\"Old\",\"author\":\"a\",\"minRtsVersion\":\"1.0.0\",\"assets\":{" +
            "\"systemIcons\":{\"munSwaps\":[{\"iconGroup\":109,\"file\":\"icons/pc.ico\"}]}," +
            "\"shellOverrides\":[{\"target\":\"Recycle Bin\",\"scope\":\"system\",\"file\":\"icons/bin.ico\"},{\"target\":\"C\",\"scope\":\"drive\",\"file\":\"icons/c.ico\"}]}}";
        var b = Read(Zip(v1, ("icons/pc.ico", Ico), ("icons/bin.ico", Ico), ("icons/c.ico", Ico)));
        Assert.Equal(ThemeBundle.SchemaV2, b.Manifest.Schema);
        var t = b.Manifest.Assets.Icons!.Targets;
        Assert.Contains(t, x => x.Kind == "system" && x.Group == 109);
        Assert.Contains(t, x => x.Kind == "shell" && x.Match == "Recycle Bin");
        Assert.Contains(t, x => x.Kind == "drive" && x.Match == "C");
        Assert.Null(b.Manifest.Assets.SystemIcons);
    }

    [Fact]
    public void PhaseTwoSectionsSurviveARoundTrip()
    {
        var b = Read(Zip(Manifest("{\"cursors\":{\"baseSize\":48}}")));
        Assert.True(b.Manifest.Assets.Other!.ContainsKey("cursors"));
        string outPath = Path.Combine(_dir, "re.rtstheme");
        BundleWriter.Write(b.Manifest, new Dictionary<string, string>(), outPath);
        using var fs = File.OpenRead(outPath);
        var again = BundleReader.ReadManifest(fs);
        Assert.Equal(48, again.Assets.Other!["cursors"].GetProperty("baseSize").GetInt32());
    }

    [Fact]
    public void WriterOutputIsAcceptedByTheReader()
    {
        string png = Path.Combine(_dir, "w.png"); File.WriteAllBytes(png, Png);
        string ico = Path.Combine(_dir, "i.ico"); File.WriteAllBytes(ico, Ico);
        var m = new ThemeBundle
        {
            Id = "round-trip", Name = "Round trip", Author = "me",
            Assets = new ThemeAssets
            {
                Wallpaper = new WallpaperSettings { Kind = "static", Fit = "fill", Static = new StaticWallpaper { File = "wallpapers/w.png" } },
                Icons = new IconSettings { Targets = { new IconTarget { Kind = "shell", Match = "This PC", File = "icons/i.ico" } } },
            },
        };
        string outPath = Path.Combine(_dir, "rt.rtstheme");
        BundleWriter.Write(m, new Dictionary<string, string> { ["wallpapers/w.png"] = png, ["icons/i.ico"] = ico }, outPath);
        using var fs = File.OpenRead(outPath);
        var b = Read(fs);
        Assert.Equal(2, b.Files.Count);
        Assert.Equal(Png, File.ReadAllBytes(b.Resolve("wallpapers/w.png")));
    }

    [Fact]
    public void WriterRefusesWhatTheReaderWouldRefuse()
    {
        string exe = Path.Combine(_dir, "x.png"); File.WriteAllBytes(exe, Exe);
        var m = new ThemeBundle { Id = "bad", Name = "Bad", Author = "me" };
        Assert.Throws<InvalidDataException>(() =>
            BundleWriter.Write(m, new Dictionary<string, string> { ["wallpapers/x.png"] = exe }, Path.Combine(_dir, "bad.rtstheme")));
    }

    [Fact]
    public void LyingEntrySizeIsCaught()
    {
        // A zip bomb that claims a small size cannot unpack past the cap: the reader counts real bytes.
        var big = new byte[AssetRules.MaxFileBytes + 1];
        big[0] = 0; big[2] = 1;
        var f = ("icons/big.ico", big);
        Refused(() => Read(Zip(Manifest("{}", f), f)), "256 MiB");
    }
}
