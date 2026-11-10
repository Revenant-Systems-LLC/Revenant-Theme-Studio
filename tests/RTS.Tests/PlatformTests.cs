using Revenant_Theme_Studio.Features.Licensing;
using Revenant_Theme_Studio.Features.Themes.Platform;
using RTS.Tests.Fakes;
using Xunit;

namespace RTS.Tests;

public class BuildGateTests
{
    [Theory]
    [InlineData("26300", "26300", 0, true)]
    [InlineData("26300.9711", "26300", 9711, true)]
    [InlineData("26300.9711", "26300", 9710, false)]
    [InlineData("19045", "22000", 1, true)]
    [InlineData("22621", "19045", 6456, false)]
    public void MinBuild(string min, string build, int ubr, bool allowed) =>
        Assert.Equal(allowed, new BuildGate { MinBuild = min }.Allows(new BuildNumber(int.Parse(build), ubr)));

    [Fact]
    public void MaxWithoutRevisionCoversEveryRevision()
    {
        var gate = new BuildGate { MaxBuild = "26300" };
        Assert.True(gate.Allows(new BuildNumber(26300, 9711)));
        Assert.False(gate.Allows(new BuildNumber(28000, 1)));
    }

    [Fact]
    public void MaxWithRevisionIsExact() =>
        Assert.False(new BuildGate { MaxBuild = "26300.9000" }.Allows(new BuildNumber(26300, 9711)));

    [Theory]
    [InlineData("abc")]
    [InlineData("26300.x")]
    [InlineData("-1")]
    public void UnparseableGateFailsClosed(string bad) =>
        Assert.False(new BuildGate { MinBuild = bad }.Allows(new BuildNumber(26300, 0)));

    [Fact]
    public void CurrentBuildIsReadable()
    {
        Assert.NotNull(WindowsBuild.Current);
        Assert.True(WindowsBuild.Current!.Value.Build >= 19041);
    }
}

public class RegSnapshotTests
{
    private const string K = @"Software\Classes\CLSID\{GUID}\DefaultIcon";

    [Fact]
    public void RestoresAnExistingValueExactly()
    {
        var store = new FakeRegistryStore().With(K, "", RegData.Expand(@"%SystemRoot%\x.dll,-1"));
        var before = store.Dump();
        var snap = RegSnapshot.Capture(store, RegHive.CurrentUser, K, "");
        store.Set(RegHive.CurrentUser, K, "", RegData.Str(@"C:\new.ico,0"));
        RegSnapshot.Restore(store, snap);
        Assert.Equal(before, store.Dump());
        Assert.Equal(Microsoft.Win32.RegistryValueKind.ExpandString, store.Get(RegHive.CurrentUser, K, "")!.Kind);
    }

    [Fact]
    public void RemovesKeysItCreatedAndNothingElse()
    {
        var store = new FakeRegistryStore().With(@"Software\Classes\CLSID\{OTHER}", "x", RegData.Dword(1));
        var before = store.Dump();
        var snap = RegSnapshot.Capture(store, RegHive.CurrentUser, K, "");
        Assert.Equal(new[] { K, @"Software\Classes\CLSID\{GUID}" }, snap.CreatedKeys);
        store.Set(RegHive.CurrentUser, K, "", RegData.Str("C:\\new.ico,0"));
        RegSnapshot.Restore(store, snap);
        Assert.Equal(before, store.Dump());
    }

    [Fact]
    public void DeletesAValueThatDidNotExist()
    {
        var store = new FakeRegistryStore().With(K, "Other", RegData.Dword(5));
        var snap = RegSnapshot.Capture(store, RegHive.CurrentUser, K, "");
        Assert.Null(snap.Data);
        store.Set(RegHive.CurrentUser, K, "", RegData.Str("x"));
        RegSnapshot.Restore(store, snap);
        Assert.Null(store.Get(RegHive.CurrentUser, K, ""));
        Assert.NotNull(store.Get(RegHive.CurrentUser, K, "Other"));
    }

    [Fact]
    public void BinaryAndDwordSurviveJson()
    {
        var item = new RegSnapshotItem { Key = "k", Name = "AccentPalette", Data = RegData.Binary(new byte[] { 1, 2, 3, 255 }) };
        var json = System.Text.Json.JsonSerializer.Serialize(item, Json.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<RegSnapshotItem>(json, Json.Options)!;
        Assert.True(item.Data!.SameAs(back.Data));
        var dword = RegData.Dword(0xFFFFFFFF);
        var d2 = System.Text.Json.JsonSerializer.Deserialize<RegData>(System.Text.Json.JsonSerializer.Serialize(dword, Json.Options), Json.Options);
        Assert.True(dword.SameAs(d2));
    }
}

public class AtomicFileTests
{
    [Fact]
    public void ReplacesAndLeavesNoTempFiles()
    {
        string dir = Path.Combine(TestPaths.Root, "rts-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "a.json");
            AtomicFile.WriteAllText(path, "one");
            AtomicFile.WriteAllText(path, "two");
            Assert.Equal("two", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class FeatureGateTests
{
    [Fact]
    public void EveryFeatureHasARow() =>
        Assert.All(Enum.GetValues<Feature>(), f => Assert.True(Enum.IsDefined(FeatureGate.TierOf(f))));

    [Fact]
    public void ThemeFeaturesAreFreeWhileBuilding() =>
        Assert.True(FeatureGate.Allows(Feature.ThemeApply));
}
