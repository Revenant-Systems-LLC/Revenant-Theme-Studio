using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Features.Themes.Engine
{
    public enum Decision { Apply, Skip, Blocked }

    public enum VerifyResult { Verified, AppliedUnverified, Drifted, Failed }

    public sealed record SectionPlan(string Key, Decision Decision, string? Reason = null, bool NeedsAdmin = false);

    /// <summary>Everything a section needs while one bundle is applied.</summary>
    public sealed class ApplyContext
    {
        public required LoadedBundle Bundle { get; init; }
        public required IRegistryStore Registry { get; init; }
        public BuildNumber? Build { get; init; }
        /// <summary>This transaction's folder, for files a snapshot must keep (e.g. the old wallpaper). Null during planning.</summary>
        public string? TxDir { get; init; }
        /// <summary>Refreshes the sections ask for; the engine sends them once at the end.</summary>
        public Refresh Refresh { get; set; }
        public ThemeAssets Assets => Bundle.Manifest.Assets;
    }

    /// <summary>
    /// What a section looked like at one moment: the registry values it touches (typed, exact)
    /// plus anything else it needs, as JSON. Saved to disk, so undo survives a restart.
    /// </summary>
    public sealed class SectionState
    {
        public string Key { get; set; } = "";
        public List<RegSnapshotItem> Registry { get; set; } = new();
        public Dictionary<string, JsonElement>? Extra { get; set; }

        public T? Get<T>(string name) =>
            Extra is not null && Extra.TryGetValue(name, out var e) ? e.Deserialize<T>(Json.Options) : default;

        public void Put<T>(string name, T value) =>
            (Extra ??= new())[name] = JsonSerializer.SerializeToElement(value, Json.Options);
    }

    /// <summary>One problem met while undoing: a value someone else changed, or a step that failed.</summary>
    public sealed record UndoIssue(string Section, string What, bool ChangedSinceApply);

    /// <summary>
    /// One part of a theme (spec F2): Plan, Snapshot, Apply, Verify, Rollback. Sections never
    /// broadcast themselves; they add to <see cref="ApplyContext.Refresh"/>.
    /// </summary>
    public interface IThemeSection
    {
        string Key { get; }
        /// <summary>Apply order. Lower runs first; rollback runs in reverse.</summary>
        int Order { get; }
        /// <summary>Null when the bundle has no such section (leave Windows untouched).</summary>
        SectionPlan? Plan(ApplyContext ctx);
        SectionState Snapshot(ApplyContext ctx);
        void Apply(ApplyContext ctx);
        VerifyResult Verify(ApplyContext ctx, out string? detail);
        /// <summary>
        /// Puts <paramref name="before"/> back. A value that no longer matches <paramref name="written"/>
        /// was changed by someone else since the apply: it is reported and left alone unless
        /// <paramref name="force"/>.
        /// </summary>
        IReadOnlyList<UndoIssue> Rollback(ApplyContext ctx, SectionState before, SectionState? written, bool force);
    }

    /// <summary>
    /// Base for sections that only write registry values. Subclasses list the values they intend
    /// to write; snapshot, verify and conflict-checked rollback come for free.
    /// </summary>
    public abstract class RegistrySection : IThemeSection
    {
        public abstract string Key { get; }
        public abstract int Order { get; }
        public abstract SectionPlan? Plan(ApplyContext ctx);

        /// <summary>The exact values this section will write. Null data deletes the value.</summary>
        protected abstract IReadOnlyList<(string Key, string Name, RegData? Data)> Intended(ApplyContext ctx);

        /// <summary>Extra values to snapshot and restore without writing them (cross-effects).</summary>
        protected virtual IEnumerable<(string Key, string Name)> AlsoWatch(ApplyContext ctx) => Array.Empty<(string, string)>();

        protected abstract Refresh RefreshNeeded { get; }

        public virtual SectionState Snapshot(ApplyContext ctx)
        {
            var state = new SectionState { Key = Key };
            var keys = Intended(ctx).Select(v => (v.Key, v.Name)).Concat(AlsoWatch(ctx))
                .Distinct().ToList();
            foreach (var (k, n) in keys)
                state.Registry.Add(RegSnapshot.Capture(ctx.Registry, RegHive.CurrentUser, k, n));
            return state;
        }

        public virtual void Apply(ApplyContext ctx)
        {
            foreach (var (k, n, d) in Intended(ctx))
            {
                var current = ctx.Registry.Get(RegHive.CurrentUser, k, n);
                if (current?.Unreadable == true) throw new InvalidOperationException($"HKCU\\{k}\\{n} holds a value type RTS cannot restore, so it was not changed.");
                if (d is null) ctx.Registry.DeleteValue(RegHive.CurrentUser, k, n);
                else ctx.Registry.Set(RegHive.CurrentUser, k, n, d);
            }
            ctx.Refresh |= RefreshNeeded;
        }

        public virtual VerifyResult Verify(ApplyContext ctx, out string? detail)
        {
            foreach (var (k, n, d) in Intended(ctx))
            {
                var live = ctx.Registry.Get(RegHive.CurrentUser, k, n);
                bool ok = d is null ? live is null : d.SameAs(live);
                if (!ok) { detail = $"{n} is {live?.ToString() ?? "missing"}, expected {d?.ToString() ?? "none"}"; return VerifyResult.Drifted; }
            }
            detail = null;
            return VerifyResult.Verified;
        }

        public virtual IReadOnlyList<UndoIssue> Rollback(ApplyContext ctx, SectionState before, SectionState? written, bool force)
        {
            var issues = RestoreRegistry(ctx, Key, before.Registry, written?.Registry, force);
            ctx.Refresh |= RefreshNeeded;
            return issues;
        }

        /// <summary>
        /// Restores saved values, skipping (unless forced) any value that no longer equals what the
        /// apply wrote. Shared by every section that snapshots registry values.
        /// </summary>
        public static List<UndoIssue> RestoreRegistry(ApplyContext ctx, string section,
            IEnumerable<RegSnapshotItem> before, IReadOnlyList<RegSnapshotItem>? written, bool force)
        {
            var issues = new List<UndoIssue>();
            foreach (var item in before)
            {
                var wrote = written?.FirstOrDefault(w =>
                    w.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) && w.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (!force && wrote is not null && !RegSnapshot.StillEquals(ctx.Registry, wrote))
                {
                    issues.Add(new UndoIssue(section, $"{item.Name} was changed after the theme was applied", true));
                    continue;
                }
                try { RegSnapshot.Restore(ctx.Registry, item); }
                catch (Exception ex) { issues.Add(new UndoIssue(section, $"{item.Name}: {ex.Message}", false)); }
            }
            return issues;
        }
    }

    public static class Gates
    {
        /// <summary>Checks a gate against the running build. Null when the gate passes.</summary>
        public static SectionPlan? Check(string key, Gate? gate, BuildNumber? build)
        {
            if (gate is null || (gate.MinBuild is null && gate.MaxBuild is null)) return null;
            bool fail = string.Equals(gate.OnUnsupported, "fail", StringComparison.OrdinalIgnoreCase);
            if (build is null)
                return new SectionPlan(key, fail ? Decision.Blocked : Decision.Skip, "this Windows build could not be read");
            if (new BuildGate { MinBuild = gate.MinBuild, MaxBuild = gate.MaxBuild }.Allows(build.Value)) return null;
            return new SectionPlan(key, fail ? Decision.Blocked : Decision.Skip,
                $"made for Windows builds {gate.MinBuild ?? "any"} to {gate.MaxBuild ?? "any"}; this is {build}");
        }
    }
}
