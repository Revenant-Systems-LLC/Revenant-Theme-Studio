using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Features.Themes.Sections;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;
using RTS.Tests.Fakes;
using Xunit;

namespace RTS.Tests;

/// <summary>Stands in for the elevated helper: "patches" by appending bytes, restores the saved original.</summary>
internal sealed class FakeMunWriter : IMunWriter
{
    private readonly byte[] _original;
    public List<MunCommitRequest> Commits { get; } = new();
    public int Restores { get; private set; }
    public bool Fail { get; set; }
    public FakeMunWriter(string target) => _original = File.ReadAllBytes(target);

    public (bool success, string message) Commit(MunCommitRequest r)
    {
        Commits.Add(r);
        if (Fail) return (false, "UAC prompt was cancelled.");
        using var fs = new FileStream(r.Target, FileMode.Append);
        fs.Write(new byte[] { 1, 2, 3, 4 });
        return (true, "ok");
    }

    public (bool success, string message) Restore(string target)
    {
        Restores++;
        File.WriteAllBytes(target, _original);
        return (true, "ok");
    }
}

public sealed class IconSectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestPaths.Root, "rts-icons-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistryStore _reg = new();
    private readonly TransactionStore _store;
    private const string RecycleBin = "{645FF040-5081-101B-9F08-00AA002F954E}";

    public IconSectionTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new TransactionStore(Path.Combine(_dir, "tx"));
        _reg.With(@"Software\Classes\CLSID\{OTHER-APP}", "", RegData.Str("keep me"));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string Ico(string name)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, BundleTests.Ico);
        return p;
    }

    private LoadedBundle Bundle(params IconTarget[] targets) => new()
    {
        Manifest = new ThemeBundle { Id = "t", Name = "T", Assets = new ThemeAssets { Icons = new IconSettings { Targets = targets.ToList() } } },
        Files = new Dictionary<string, string> { ["icons/a.ico"] = Ico("a.ico"), ["icons/full.ico"] = Ico("full.ico") },
    };

    private ThemeApplyEngine Engine(params IThemeSection[] sections) =>
        new(sections.Length > 0 ? sections : new IThemeSection[] { new ShellIconsSection() }, _reg, new RecordingBroadcaster(), _store, new BuildNumber(26300, 9711));

    private string? Value(string key, string name) => _reg.Get(RegHive.CurrentUser, key, name)?.Text;

    [Fact]
    public void DocumentsGetsBothGuidSetsAndUndoRemovesOnlyWhatWasCreated()
    {
        string before = _reg.Dump();
        var bundle = Bundle(new IconTarget { Kind = "shell", Match = "Documents", File = "icons/a.ico" });
        var engine = Engine();
        var tx = engine.Apply(bundle);
        Assert.Equal(VerifyResult.Verified, tx.Sections.Single().Status);
        string expected = bundle.Files["icons/a.ico"] + ",0";
        Assert.Equal(expected, Value(@"Software\Classes\CLSID\{D3162B92-9365-467A-956B-92703ACA08AF}\DefaultIcon", ""));
        Assert.Equal(expected, Value(@"Software\Classes\CLSID\{A8CDFF1C-4878-43BE-B5FD-F8091C1C60D0}\DefaultIcon", ""));

        Assert.True(engine.Undo(bundle, _store.LatestApplied()!).Complete);
        Assert.Equal(before, _reg.Dump());
    }

    [Fact]
    public void RecycleBinGetsEmptyAndFull()
    {
        var bundle = Bundle(new IconTarget { Kind = "shell", Match = RecycleBin, File = "icons/a.ico", FullFile = "icons/full.ico" });
        Engine().Apply(bundle);
        string key = $@"Software\Classes\CLSID\{RecycleBin}\DefaultIcon";
        Assert.EndsWith("a.ico,0", Value(key, "Empty"));
        Assert.EndsWith("full.ico,0", Value(key, "Full"));
        Assert.EndsWith("a.ico,0", Value(key, ""));
    }

    [Fact]
    public void SystemReferencesStayUnexpandedAndDrivesUseDriveIcons()
    {
        var bundle = Bundle(
            new IconTarget { Kind = "shell", Match = "This PC", File = @"%SystemRoot%\System32\imageres.dll,-109" },
            new IconTarget { Kind = "drive", Match = "d", File = "icons/a.ico" });
        Engine().Apply(bundle);
        var pc = _reg.Get(RegHive.CurrentUser, @"Software\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\DefaultIcon", "")!;
        Assert.Equal(Microsoft.Win32.RegistryValueKind.ExpandString, pc.Kind);
        Assert.Equal(@"%SystemRoot%\System32\imageres.dll,-109", pc.Text);
        Assert.EndsWith("a.ico,0", Value(@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\D\DefaultIcon", ""));
    }

    [Fact]
    public void BadTargetsAreSkippedAndNamedWithoutBlockingTheRest()
    {
        var bundle = Bundle(
            new IconTarget { Kind = "shell", Match = "Not A Place", File = "icons/a.ico" },
            new IconTarget { Kind = "drive", Match = "CD", File = "icons/a.ico" },
            new IconTarget { Kind = "folder", Match = @"C:\Users\x", File = "icons/a.ico" },
            new IconTarget { Kind = "shell", Match = "Music", File = "icons/a.ico" });
        var plan = Engine().Plan(bundle).Plans.Single();
        Assert.Equal(Decision.Apply, plan.Decision);
        Assert.Contains("Not A Place", plan.Reason);
        Assert.Contains("not a drive letter", plan.Reason);
        Assert.Contains("later version", plan.Reason);
    }

    [Fact]
    public void SystemIconsCommitOnceAndUndoRestoresTheOriginal()
    {
        string target = Path.Combine(_dir, "imageres.dll.mun");
        File.Copy(@"C:\Windows\SystemResources\imageres.dll.mun", target);
        string originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target)));
        var writer = new FakeMunWriter(target);
        var engine = Engine(new SystemIconsSection(writer, target));
        var bundle = Bundle(new IconTarget { Kind = "system", Group = 2, File = "icons/a.ico" },
                            new IconTarget { Kind = "system", Group = 3, File = "icons/full.ico" });

        var plan = engine.Plan(bundle);
        Assert.True(plan.NeedsAdmin);
        var tx = engine.Apply(bundle);
        Assert.Equal(VerifyResult.Verified, tx.Sections.Single().Status);
        Assert.Single(writer.Commits);                                   // one UAC prompt for the whole theme
        Assert.Equal(new[] { 2, 3 }, writer.Commits[0].Replacements.Select(r => r.GroupId));

        Assert.True(engine.Undo(bundle, _store.LatestApplied()!).Complete);
        Assert.Equal(1, writer.Restores);
        Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target))));
    }

    [Fact]
    public void CancelledUacRollsBackTheOtherSections()
    {
        string target = Path.Combine(_dir, "imageres.dll.mun");
        File.Copy(@"C:\Windows\SystemResources\imageres.dll.mun", target);
        string before = _reg.Dump();
        var writer = new FakeMunWriter(target) { Fail = true };
        var engine = Engine(new ShellIconsSection(), new SystemIconsSection(writer, target));
        var tx = engine.Apply(Bundle(new IconTarget { Kind = "shell", Match = "Videos", File = "icons/a.ico" },
                                     new IconTarget { Kind = "system", Group = 2, File = "icons/a.ico" }));
        Assert.Equal(TxState.RolledBack, tx.State);
        Assert.Equal(before, _reg.Dump());
        Assert.Equal(0, writer.Restores);                                // nothing was swapped, nothing to restore
    }
}
