using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.Features.Themes.Sections
{
    /// <summary>
    /// Shell-location and drive icons through per-user registry overrides: the same keys
    /// ShellIconService and DriveIconService write (HKCU\Software\Classes\CLSID, verified in
    /// Windows Sandbox 2026-11-09), written directly so the engine refreshes Explorer once.
    /// Undo restores the exact registry snapshot instead of deleting keys.
    /// </summary>
    public sealed class ShellIconsSection : RegistrySection
    {
        private const string DriveIcons = @"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";
        private static readonly IReadOnlyDictionary<string, ShellIconService.ShellTargetSpec> Shell = new ShellIconService().Targets;
        private static readonly string[] Supported = { "shell", "drive" };

        public override string Key => "icons";
        public override int Order => 70;
        protected override Refresh RefreshNeeded => Refresh.Icons;

        public override SectionPlan? Plan(ApplyContext ctx)
        {
            var icons = ctx.Assets.Icons;
            if (icons is null) return null;
            var (valid, problems) = Resolve(ctx);
            int other = icons.Targets.Count(t => !Supported.Contains(t.Kind) && t.Kind != "system");
            if (valid.Count == 0)
                return problems.Count + other == 0 ? null
                    : new SectionPlan(Key, Decision.Skip, Describe(problems, other));
            if (Gates.Check(Key, icons.Gate, ctx.Build) is { } gated) return gated;
            return new SectionPlan(Key, Decision.Apply, problems.Count + other == 0 ? null : Describe(problems, other));
        }

        protected override IReadOnlyList<(string Key, string Name, RegData? Data)> Intended(ApplyContext ctx)
        {
            var list = new List<(string, string, RegData?)>();
            foreach (var t in Resolve(ctx).Valid)
            {
                var icon = Ref(ctx, t.File!);
                if (t.Kind == "drive")
                {
                    list.Add(($@"{DriveIcons}\{char.ToUpperInvariant(t.Match![0])}\DefaultIcon", "", icon));
                    continue;
                }
                var spec = Find(t.Match!)!;
                foreach (var clsid in new[] { spec.Clsid, spec.SecondaryClsid }.Where(c => c is not null))
                {
                    string key = $@"Software\Classes\CLSID\{clsid}\DefaultIcon";
                    list.Add((key, "", icon));
                    if (spec.TwoState)
                    {
                        list.Add((key, "Empty", icon));
                        list.Add((key, "Full", t.FullFile is null ? icon : Ref(ctx, t.FullFile)));
                    }
                }
            }
            return list;
        }

        /// <summary>Targets this section can apply, and why the rest can't.</summary>
        private static (List<IconTarget> Valid, List<string> Problems) Resolve(ApplyContext ctx)
        {
            var valid = new List<IconTarget>();
            var problems = new List<string>();
            foreach (var t in ctx.Assets.Icons?.Targets ?? new())
            {
                if (!Supported.Contains(t.Kind)) continue;
                if (string.IsNullOrEmpty(t.File)) { problems.Add($"{t.Match}: no icon"); continue; }
                if (Gates.Check(t.Match ?? t.Kind, t.Gate, ctx.Build) is { } gated) { problems.Add($"{t.Match}: {gated.Reason}"); continue; }
                if (t.Kind == "drive" && (t.Match is not { Length: 1 } || !char.IsAsciiLetter(t.Match[0])))
                    { problems.Add($"\"{t.Match}\" is not a drive letter"); continue; }
                if (t.Kind == "shell" && Find(t.Match ?? "") is null)
                    { problems.Add($"\"{t.Match}\" is not a known shell location"); continue; }
                valid.Add(t);
            }
            return (valid, problems);
        }

        /// <summary>A shell target by display name ("Recycle Bin") or by either of its CLSIDs.</summary>
        public static ShellIconService.ShellTargetSpec? Find(string match)
        {
            var byName = Shell.FirstOrDefault(kv => kv.Key.Equals(match, StringComparison.OrdinalIgnoreCase)).Value;
            if (byName is not null) return byName;
            string clsid = match.Trim();
            if (!clsid.StartsWith('{')) clsid = "{" + clsid + "}";
            return Shell.Values.FirstOrDefault(s => s.Clsid.Equals(clsid, StringComparison.OrdinalIgnoreCase)
                                                 || (s.SecondaryClsid?.Equals(clsid, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        /// <summary>The DefaultIcon value: a system reference kept unexpanded, or "staged path,0".</summary>
        private static RegData Ref(ApplyContext ctx, string file)
        {
            string resolved = ctx.Bundle.Resolve(file);
            return Bundles.AssetRules.LooksLikeSystemRef(resolved) ? RegData.Expand(resolved) : RegData.Str($"{resolved},0");
        }

        private static string Describe(List<string> problems, int other) =>
            string.Join("; ", problems.Concat(other > 0 ? new[] { $"{other} icon(s) of kinds coming in a later version" } : Array.Empty<string>()));
    }

    /// <summary>
    /// System icons inside imageres.dll.mun, through the elevated helper: one UAC prompt for the
    /// whole theme. Undo asks the helper to put the preserved Windows original back.
    /// </summary>
    public sealed class SystemIconsSection : IThemeSection
    {
        private readonly IMunWriter _writer;
        private readonly string _target;

        public SystemIconsSection(IMunWriter writer, string? target = null)
        {
            _writer = writer;
            _target = target ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemResources", "imageres.dll.mun");
        }

        public string Key => "systemIcons";
        public int Order => 80;

        private static List<IconTarget> Targets(ApplyContext ctx) =>
            (ctx.Assets.Icons?.Targets ?? new()).Where(t => t.Kind == "system").ToList();

        public SectionPlan? Plan(ApplyContext ctx)
        {
            var targets = Targets(ctx);
            if (targets.Count == 0) return null;
            if (Gates.Check(Key, ctx.Assets.Icons!.Gate, ctx.Build) is { } gated) return gated;
            if (!File.Exists(_target)) return new SectionPlan(Key, Decision.Skip, "imageres.dll.mun was not found");
            string version = FileVersion(_target);
            foreach (var t in targets)
            {
                if (t.Group is not > 0) return new SectionPlan(Key, Decision.Skip, "a system icon has no group number");
                if (t.File is null || Bundles.AssetRules.LooksLikeSystemRef(t.File) || !t.File.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    return new SectionPlan(Key, Decision.Skip, $"group {t.Group}: system icons need an .ico from the theme");
                if (t.Gate?.FileVersion is { Count: > 0 } versions && !versions.Contains(version))
                    return new SectionPlan(Key, Decision.Skip, $"made for imageres.dll.mun {string.Join("/", versions)}; this PC has {version}");
            }
            return new SectionPlan(Key, Decision.Apply, "asks for administrator permission once", NeedsAdmin: true);
        }

        public SectionState Snapshot(ApplyContext ctx)
        {
            var state = new SectionState { Key = Key };
            state.Put("sha256", Sha(_target));
            state.Put("version", FileVersion(_target));
            return state;
        }

        public void Apply(ApplyContext ctx)
        {
            var replacements = Targets(ctx)
                .Select(t => (t.Group!.Value, File.ReadAllBytes(ctx.Bundle.Resolve(t.File!))))
                .ToList();
            var (ok, message) = _writer.Commit(new MunCommitRequest(_target, replacements));
            if (!ok) throw new InvalidOperationException(message);
            ctx.Refresh |= Refresh.Icons;
        }

        public VerifyResult Verify(ApplyContext ctx, out string? detail)
        {
            var groups = new MunResourceReader().ReadAllGroups(_target).Select(g => g.groupId).ToHashSet();
            var missing = Targets(ctx).Where(t => !groups.Contains(t.Group!.Value)).Select(t => t.Group).ToList();
            if (missing.Count > 0) { detail = $"icon group(s) {string.Join(", ", missing)} are missing"; return VerifyResult.Failed; }
            detail = "running programs show the new icons after Explorer restarts or you sign out";
            return VerifyResult.Verified;
        }

        public IReadOnlyList<UndoIssue> Rollback(ApplyContext ctx, SectionState before, SectionState? written, bool force)
        {
            string now = Sha(_target);
            if (now == before.Get<string>("sha256")) return Array.Empty<UndoIssue>();   // never changed
            if (!force && written is not null && now != written.Get<string>("sha256"))
                return new[] { new UndoIssue(Key, "imageres.dll.mun was changed again after the theme (another edit or a Windows update)", true) };
            var (ok, message) = _writer.Restore(_target);
            ctx.Refresh |= Refresh.Icons;
            return ok ? Array.Empty<UndoIssue>() : new[] { new UndoIssue(Key, message, false) };
        }

        private static string Sha(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(fs));
        }

        private static string FileVersion(string path)
        {
            string raw = FileVersionInfo.GetVersionInfo(path).FileVersion?.Split(' ')[0] ?? "";
            return new string(raw.Where(c => char.IsDigit(c) || c == '.').ToArray());
        }
    }
}
