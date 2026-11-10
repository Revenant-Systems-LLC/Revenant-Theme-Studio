using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Models;
using RTS.Tests.Fakes;
using Xunit;

namespace RTS.Tests;

/// <summary>A registry section that writes fixed values, optionally failing partway.</summary>
internal sealed class TestSection : RegistrySection
{
    private readonly (string Key, string Name, RegData? Data)[] _values;
    private readonly int _failAfter;
    public List<string> Log { get; }
    public Decision Decision { get; set; } = Decision.Apply;

    public TestSection(string key, int order, List<string> log, int failAfter = -1, params (string, string, RegData?)[] values)
    {
        Key = key; Order = order; Log = log; _failAfter = failAfter; _values = values;
    }

    public override string Key { get; }
    public override int Order { get; }
    protected override Refresh RefreshNeeded => Refresh.ImmersiveColorSet;
    public override SectionPlan? Plan(ApplyContext ctx) => new(Key, Decision, Decision == Decision.Apply ? null : "test");
    protected override IReadOnlyList<(string Key, string Name, RegData? Data)> Intended(ApplyContext ctx) => _values;

    public override void Apply(ApplyContext ctx)
    {
        Log.Add("apply " + Key);
        int i = 0;
        foreach (var (k, n, d) in _values)
        {
            if (i++ == _failAfter) throw new InvalidOperationException("boom");
            if (d is null) ctx.Registry.DeleteValue(RegHive.CurrentUser, k, n); else ctx.Registry.Set(RegHive.CurrentUser, k, n, d);
        }
        ctx.Refresh |= RefreshNeeded;
    }

    public override IReadOnlyList<UndoIssue> Rollback(ApplyContext ctx, SectionState before, SectionState? written, bool force)
    {
        Log.Add("rollback " + Key);
        return base.Rollback(ctx, before, written, force);
    }
}

public sealed class EngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestPaths.Root, "rts-engine-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistryStore _reg = new();
    private readonly RecordingBroadcaster _bc = new();
    private readonly List<string> _log = new();
    private readonly TransactionStore _store;

    public EngineTests()
    {
        _store = new TransactionStore(Path.Combine(_dir, "tx"));
        _reg.With(@"Software\A", "Mode", RegData.Dword(1))
            .With(@"Software\B", "Color", RegData.Binary(new byte[] { 1, 2, 3 }));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static LoadedBundle Bundle(ThemeAssets? assets = null, Gate? gate = null) => new()
    {
        Manifest = new ThemeBundle { Id = "t", Name = "T", Gate = gate, Assets = assets ?? new ThemeAssets() },
        Files = new Dictionary<string, string>(),
    };

    private ThemeApplyEngine Engine(params IThemeSection[] s) => new(s, _reg, _bc, _store, new BuildNumber(26300, 9711));

    private TestSection A(int failAfter = -1) => new("a", 1, _log, failAfter,
        (@"Software\A", "Mode", RegData.Dword(0)), (@"Software\A\New", "X", RegData.Str("created")));
    private TestSection B(int failAfter = -1) => new("b", 2, _log, failAfter,
        (@"Software\B", "Color", RegData.Binary(new byte[] { 9, 9 })), (@"Software\B", "Extra", RegData.Dword(7)));

    [Fact]
    public void AppliesInOrderBroadcastsOnceAndVerifies()
    {
        var tx = Engine(B(), A()).Apply(Bundle());
        Assert.Equal(new[] { "apply a", "apply b" }, _log);
        Assert.Single(_bc.Sent);
        Assert.Equal(TxState.Applied, tx.State);
        Assert.All(tx.Sections, s => Assert.Equal(VerifyResult.Verified, s.Status));
    }

    [Fact]
    public void FailureRollsBackEverythingIncludingThePartialSection()
    {
        string before = _reg.Dump();
        var tx = Engine(A(), B(failAfter: 1)).Apply(Bundle());
        Assert.Equal(TxState.RolledBack, tx.State);
        Assert.Equal(before, _reg.Dump());          // byte-for-byte, including the key A created
        Assert.Equal(new[] { "apply a", "apply b", "rollback b", "rollback a" }, _log);
        Assert.Equal(VerifyResult.Failed, tx.Sections.Single(s => s.Key == "b").Status);
    }

    [Fact]
    public void UndoRestoresTheExactPriorState()
    {
        string before = _reg.Dump();
        var engine = Engine(A(), B());
        var tx = engine.Apply(Bundle());
        var report = engine.Undo(Bundle(), _store.LatestApplied()!);
        Assert.True(report.Complete);
        Assert.Equal(before, _reg.Dump());
        Assert.Equal(TxState.Undone, _store.All().Single().State);
        Assert.Null(_store.LatestApplied());
    }

    [Fact]
    public void UndoSkipsValuesChangedSinceApplyUnlessForced()
    {
        var engine = Engine(A());
        engine.Apply(Bundle());
        _reg.Set(RegHive.CurrentUser, @"Software\A", "Mode", RegData.Dword(42));   // the user changed it afterwards

        var report = engine.Undo(Bundle(), _store.LatestApplied()!);
        Assert.False(report.Complete);
        Assert.True(report.Issues.Single().ChangedSinceApply);
        Assert.Equal(42u, _reg.Get(RegHive.CurrentUser, @"Software\A", "Mode")!.Number);

        var forced = engine.Undo(Bundle(), _store.All().Single(), force: true);
        Assert.True(forced.Complete);
        Assert.Equal(1u, _reg.Get(RegHive.CurrentUser, @"Software\A", "Mode")!.Number);
    }

    [Fact]
    public void AnInterruptedApplyCanBeRolledBackAfterARestart()
    {
        string before = _reg.Dump();
        var engine = Engine(A(), B());
        var tx = engine.Apply(Bundle());
        // Simulate a crash mid-apply: the record says Applying and there is no written snapshot.
        tx.State = TxState.Applying;
        _store.Save(tx);
        File.Delete(Path.Combine(_store.Root, tx.TxId, "written.json"));

        var found = new TransactionStore(_store.Root).Interrupted();
        Assert.NotNull(found);
        Assert.True(engine.Undo(Bundle(), found!).Complete);
        Assert.Equal(before, _reg.Dump());
    }

    [Fact]
    public void BlockedSectionChangesNothing()
    {
        string before = _reg.Dump();
        var b = B(); b.Decision = Decision.Blocked;
        var tx = Engine(A(), b).Apply(Bundle());
        Assert.Equal(before, _reg.Dump());
        Assert.Empty(_log);
        Assert.Contains("Nothing was changed", tx.Notes.Single());
    }

    [Fact]
    public void BundleGateSkipsEverySectionOnOtherBuilds()
    {
        string before = _reg.Dump();
        var tx = Engine(A()).Apply(Bundle(gate: new Gate { MinBuild = "28000" }));
        Assert.Equal(before, _reg.Dump());
        Assert.Equal(Decision.Skip, tx.Sections.Single().Decision);
    }

    [Fact]
    public void LaterSectionsAreReportedNotDropped()
    {
        var assets = new ThemeAssets { Other = new() { ["cursors"] = System.Text.Json.JsonDocument.Parse("{}").RootElement } };
        var plan = Engine(A()).Plan(Bundle(assets));
        Assert.Contains(plan.Plans, p => p.Key == "cursors" && p.Decision == Decision.Skip);
    }

    [Fact]
    public void RecordSurvivesARestart()
    {
        Engine(A()).Apply(Bundle());
        var reloaded = new TransactionStore(_store.Root).LatestApplied();
        Assert.NotNull(reloaded);
        Assert.Equal("t", reloaded!.BundleId);
        Assert.NotNull(_store.LoadBefore(reloaded.TxId));
    }
}
