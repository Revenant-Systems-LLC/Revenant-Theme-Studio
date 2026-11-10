using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Features.Themes.Sections
{
    /// <summary>
    /// Desktop wallpaper through IDesktopWallpaper: one image everywhere, one per monitor, or the
    /// current frame of a time-of-day set. Fit and background color are global in Windows.
    ///
    /// Undo keeps a copy of every replaced image in the transaction folder, because the original
    /// (a Spotlight image, a cached transcode) may be gone by the time someone clicks undo.
    /// The accent values are snapshotted too: with auto-colorization on, a new wallpaper moves the accent.
    /// </summary>
    public sealed class WallpaperSection : IThemeSection
    {
        private static readonly string[] Fits = { "center", "tile", "stretch", "fit", "fill", "span" };

        private readonly IWallpaperApi _api;
        private readonly Func<DateTime> _now;

        public WallpaperSection(IWallpaperApi api, Func<DateTime>? now = null)
        {
            _api = api;
            _now = now ?? (() => DateTime.Now);
        }

        public string Key => "wallpaper";
        public int Order => 20;

        private sealed record Shot(string Id, string Path, string? Copy);

        public SectionPlan? Plan(ApplyContext ctx)
        {
            var w = ctx.Assets.Wallpaper;
            if (w is null) return null;
            if (Gates.Check(Key, w.Gate, ctx.Build) is { } gated) return gated;
            string? problem = w.Kind switch
            {
                "static" => w.Static?.File is { Length: > 0 } ? null : "no image given",
                "perMonitor" => w.PerMonitor is { Count: > 0 } ? null : "no monitors listed",
                "timeOfDay" => w.TimeOfDay?.Segments is { Day.Count: > 0, Night.Count: > 0 } ? null : "needs day and night images",
                "slideshow" => "slideshows come in a later version",
                _ => $"unknown wallpaper kind \"{w.Kind}\"",
            };
            if (problem is null && w.Fit is not null && FitIndex(w.Fit) < 0) problem = $"unknown fit \"{w.Fit}\"";
            if (problem is null && w.BackgroundColor is not null && !TryColorRef(w.BackgroundColor, out _)) problem = "background color is not #RRGGBB";
            if (problem is null && _api.Monitors().Count == 0) problem = "no monitors found";
            return problem is null ? new SectionPlan(Key, Decision.Apply) : new SectionPlan(Key, Decision.Skip, problem);
        }

        public SectionState Snapshot(ApplyContext ctx)
        {
            var state = new SectionState { Key = Key };
            var shots = new List<Shot>();
            foreach (var m in _api.Monitors())
            {
                string path = _api.GetWallpaper(m.Id);
                shots.Add(new Shot(m.Id, path, KeepCopy(ctx, path)));
            }
            state.Put("monitors", shots);
            state.Put("position", _api.GetPosition());
            state.Put("background", _api.GetBackgroundColor());
            foreach (var (k, n) in ColorsSection.AccentValues)
                state.Registry.Add(RegSnapshot.Capture(ctx.Registry, RegHive.CurrentUser, k, n));
            return state;
        }

        public void Apply(ApplyContext ctx)
        {
            var w = ctx.Assets.Wallpaper!;
            if (w.Fit is not null) _api.SetPosition(FitIndex(w.Fit));
            if (w.BackgroundColor is not null && TryColorRef(w.BackgroundColor, out uint bg)) _api.SetBackgroundColor(bg);
            foreach (var (monitor, path) in Targets(ctx))
            {
                if (!File.Exists(path)) throw new FileNotFoundException($"Wallpaper image is missing: {path}");
                _api.SetWallpaper(monitor, path);
            }
        }

        public VerifyResult Verify(ApplyContext ctx, out string? detail)
        {
            var monitors = _api.Monitors();
            foreach (var (monitor, path) in Targets(ctx))
            {
                foreach (var m in monitor is null ? monitors : monitors.Where(x => x.Id == monitor))
                {
                    var live = _api.GetWallpaper(m.Id);
                    if (!SamePath(live, path)) { detail = $"a monitor shows {live}"; return VerifyResult.Drifted; }
                }
            }
            detail = null;
            return VerifyResult.Verified;
        }

        public IReadOnlyList<UndoIssue> Rollback(ApplyContext ctx, SectionState before, SectionState? written, bool force)
        {
            var issues = new List<UndoIssue>();
            var wrote = written?.Get<List<Shot>>("monitors") ?? new();
            var attached = _api.Monitors().Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var b in before.Get<List<Shot>>("monitors") ?? new())
            {
                if (!attached.Contains(b.Id)) { issues.Add(new UndoIssue(Key, "a monitor is no longer connected", false)); continue; }
                var w = wrote.FirstOrDefault(x => x.Id == b.Id);
                if (!force && w is not null && !SamePath(_api.GetWallpaper(b.Id), w.Path))
                {
                    issues.Add(new UndoIssue(Key, "a monitor's wallpaper was changed after the theme was applied", true));
                    continue;
                }
                string? source = b.Path.Length == 0 ? "" : File.Exists(b.Path) ? b.Path : b.Copy is not null && File.Exists(b.Copy) ? b.Copy : null;
                if (source is null) { issues.Add(new UndoIssue(Key, $"the original image is gone: {b.Path}", false)); continue; }
                try { _api.SetWallpaper(b.Id, source); }
                catch (Exception ex) { issues.Add(new UndoIssue(Key, ex.Message, false)); }
            }

            RestoreGlobal(before, written, force, issues, "position", "fit", _api.GetPosition, v => _api.SetPosition(v));
            RestoreGlobal(before, written, force, issues, "background", "background color", () => _api.GetBackgroundColor(), v => _api.SetBackgroundColor(v));
            issues.AddRange(RegistrySection.RestoreRegistry(ctx, Key, before.Registry, written?.Registry, force));
            ctx.Refresh |= Refresh.ImmersiveColorSet;
            return issues;
        }

        private void RestoreGlobal<T>(SectionState before, SectionState? written, bool force, List<UndoIssue> issues,
            string name, string label, Func<T> get, Action<T> set) where T : struct
        {
            if (before.Extra?.ContainsKey(name) != true) return;
            T old = before.Get<T>(name);
            if (!force && written?.Extra?.ContainsKey(name) == true && !EqualityComparer<T>.Default.Equals(get(), written.Get<T>(name)))
            {
                issues.Add(new UndoIssue(Key, $"the wallpaper {label} was changed after the theme was applied", true));
                return;
            }
            try { set(old); }
            catch (Exception ex) { issues.Add(new UndoIssue(Key, $"{label}: {ex.Message}", false)); }
        }

        /// <summary>(monitor id or null for all, absolute image path) for the bundle's wallpaper.</summary>
        private List<(string? Monitor, string Path)> Targets(ApplyContext ctx)
        {
            var w = ctx.Assets.Wallpaper!;
            var list = new List<(string?, string)>();
            switch (w.Kind)
            {
                case "static":
                    list.Add((null, ctx.Bundle.Resolve(w.Static!.File)));
                    break;
                case "perMonitor":
                    var monitors = _api.Monitors();
                    foreach (var entry in w.PerMonitor!)
                        if (Match(entry.Match, monitors) is { } m) list.Add((m.Id, ctx.Bundle.Resolve(entry.File)));
                    break;
                case "timeOfDay":
                    list.Add((null, ctx.Bundle.Resolve(TimeOfDay.Pick(w.TimeOfDay!.Segments, _now()))));
                    break;
            }
            return list.Select(t => (t.Item1, ExpandSystem(t.Item2))).ToList();
        }

        public static MonitorInfo? Match(MonitorMatch match, IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors.Count == 0) return null;
            if (match.Ordinal is { } i) return i >= 0 && i < monitors.Count ? monitors[i] : null;
            return match.Position?.ToLowerInvariant() switch
            {
                "primary" => monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0],
                "left" => monitors.MinBy(m => m.Left),
                "right" => monitors.MaxBy(m => m.Right),
                "top" => monitors.MinBy(m => m.Top),
                "bottom" => monitors.MaxBy(m => m.Bottom),
                _ => null,
            };
        }

        private static string? KeepCopy(ApplyContext ctx, string path)
        {
            if (ctx.TxDir is null || path.Length == 0 || !File.Exists(path)) return null;
            try
            {
                string dir = Path.Combine(ctx.TxDir, "wallpaper");
                Directory.CreateDirectory(dir);
                string hash;
                using (var fs = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                string copy = Path.Combine(dir, hash[..16] + Path.GetExtension(path));
                if (!File.Exists(copy)) File.Copy(path, copy);
                return copy;
            }
            catch { return null; }   // undo will fall back to the original path
        }

        private static int FitIndex(string fit) => Array.FindIndex(Fits, f => f.Equals(fit, StringComparison.OrdinalIgnoreCase));

        private static bool TryColorRef(string hex, out uint colorRef)
        {
            colorRef = 0;
            var s = hex.Trim().TrimStart('#');
            if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) return false;
            colorRef = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | (rgb >> 16);   // #RRGGBB to 0x00BBGGRR
            return true;
        }

        private static string ExpandSystem(string path) =>
            path.StartsWith("%SystemRoot%", StringComparison.OrdinalIgnoreCase) ? Environment.ExpandEnvironmentVariables(path) : path;

        private static bool SamePath(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return a.Length == b.Length;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// <summary>
    /// Picks the time-of-day frame for a moment. Phase 1 uses fixed times (spec D8); sun-position
    /// times come in Phase 3. Each segment's images split it into equal parts.
    /// </summary>
    public static class TimeOfDay
    {
        public static string Pick(TimeOfDaySegments s, DateTime local)
        {
            double h = local.TimeOfDay.TotalHours;
            bool four = s.Sunrise is { Count: > 0 } && s.Sunset is { Count: > 0 };
            // (list, start hour, length in hours). Night wraps past midnight.
            var segments = four
                ? new[] { (s.Sunrise!, 6.0, 2.0), (s.Day, 8.0, 10.0), (s.Sunset!, 18.0, 2.0), (s.Night, 20.0, 10.0) }
                : new[] { (s.Day, 7.0, 12.0), (s.Night, 19.0, 12.0) };
            foreach (var (list, start, length) in segments)
            {
                double into = (h - start + 24) % 24;
                if (into < length)
                {
                    int i = Math.Min((int)(into / length * list.Count), list.Count - 1);
                    return list[i];
                }
            }
            return s.Night[0];
        }
    }
}
