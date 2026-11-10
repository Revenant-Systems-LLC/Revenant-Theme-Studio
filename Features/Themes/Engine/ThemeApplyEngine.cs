using System;
using System.Collections.Generic;
using System.Linq;
using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Platform;

namespace Revenant_Theme_Studio.Features.Themes.Engine
{
    /// <summary>What a preflight found, before anything changes.</summary>
    public sealed class Preflight
    {
        public List<SectionPlan> Plans { get; } = new();
        public bool CanApply => Plans.All(p => p.Decision != Decision.Blocked) && Plans.Any(p => p.Decision == Decision.Apply);
        public bool NeedsAdmin => Plans.Any(p => p.Decision == Decision.Apply && p.NeedsAdmin);
    }

    public sealed class UndoReport
    {
        public required TransactionRecord Transaction { get; init; }
        public List<UndoIssue> Issues { get; } = new();
        public bool Complete => Issues.Count == 0;
    }

    /// <summary>
    /// Applies a whole theme as one transaction (spec F2): preflight, snapshot, apply in order,
    /// one broadcast, verify, record. A hard failure rolls back everything already applied.
    /// Undo restores the saved snapshot, skipping values someone changed since (spec D4).
    /// Blocking: call off the UI thread.
    /// </summary>
    public sealed class ThemeApplyEngine
    {
        /// <summary>Phase 2 sections the format carries but this version can't apply yet.</summary>
        private static readonly string[] LaterSections =
            { "cursors", "sounds", "metrics", "screensaver", "lockScreen", "tweaks", "console", "visualStyle", "schedule" };

        private readonly IReadOnlyList<IThemeSection> _sections;
        private readonly IRegistryStore _registry;
        private readonly IShellBroadcaster _broadcaster;
        private readonly TransactionStore _store;
        private readonly BuildNumber? _build;

        public ThemeApplyEngine(IEnumerable<IThemeSection> sections, IRegistryStore registry,
            IShellBroadcaster broadcaster, TransactionStore store, BuildNumber? build)
        {
            _sections = sections.OrderBy(s => s.Order).ToList();
            _registry = registry;
            _broadcaster = broadcaster;
            _store = store;
            _build = build;
        }

        private ApplyContext Context(LoadedBundle bundle, string? txId = null) => new()
            { Bundle = bundle, Registry = _registry, Build = _build, TxDir = txId is null ? null : System.IO.Path.Combine(_store.Root, txId) };

        public Preflight Plan(LoadedBundle bundle)
        {
            var result = new Preflight();
            var ctx = Context(bundle);
            var bundleGate = Gates.Check("theme", bundle.Manifest.Gate, _build);

            foreach (var s in _sections)
            {
                var plan = s.Plan(ctx);
                if (plan is null) continue;
                if (bundleGate is not null && plan.Decision == Decision.Apply) plan = bundleGate with { Key = s.Key };
                result.Plans.Add(plan);
            }
            foreach (var later in LaterSections)
                if (bundle.Manifest.Assets.Other?.ContainsKey(later) == true)
                    result.Plans.Add(new SectionPlan(later, Decision.Skip, "coming in a later version of Revenant Theme Studio"));
            return result;
        }

        public TransactionRecord Apply(LoadedBundle bundle, string? bundlePath = null)
        {
            string txId = TransactionStore.NewId();
            var ctx = Context(bundle, txId);
            var preflight = Plan(bundle);
            var tx = new TransactionRecord
            {
                TxId = txId,
                BundleId = bundle.Manifest.Id, BundleName = bundle.Manifest.Name, BundleVersion = bundle.Manifest.Version,
                BundlePath = bundlePath, StartedUtc = DateTimeOffset.UtcNow, Build = _build?.ToString(), State = TxState.Planned,
                Sections = preflight.Plans.Select(p => new SectionResult { Key = p.Key, Decision = p.Decision, Reason = p.Reason }).ToList(),
            };
            if (!preflight.CanApply)
            {
                tx.Notes.Add(preflight.Plans.Any(p => p.Decision == Decision.Blocked)
                    ? "Nothing was changed: a section can't be applied on this Windows build."
                    : "Nothing was changed: this theme has nothing this version can apply.");
                tx.FinishedUtc = DateTimeOffset.UtcNow;
                _store.Save(tx);
                return tx;
            }

            var toApply = _sections.Where(s => preflight.Plans.Any(p => p.Key == s.Key && p.Decision == Decision.Apply)).ToList();

            // Snapshot everything first and save it, so even a crash mid-apply can be undone.
            var before = toApply.Select(s => s.Snapshot(ctx)).ToList();
            _store.SaveBefore(tx.TxId, before);
            tx.State = TxState.Snapshotted;
            _store.Save(tx);

            tx.State = TxState.Applying;
            _store.Save(tx);
            var applied = new List<IThemeSection>();
            foreach (var s in toApply)
            {
                try
                {
                    s.Apply(ctx);
                    applied.Add(s);
                }
                catch (Exception ex)
                {
                    Result(tx, s.Key).Status = VerifyResult.Failed;
                    Result(tx, s.Key).Detail = ex.Message;
                    tx.Notes.Add($"{s.Key} failed, so the whole theme was rolled back: {ex.Message}");
                    // The failed section may have written part of its values, so it rolls back too.
                    var issues = RollbackAll(ctx, applied.Append(s).ToList(), before, written: null, force: true);
                    tx.State = issues.Count == 0 ? TxState.RolledBack : TxState.RollbackPartial;
                    tx.Notes.AddRange(issues.Select(i => $"Not restored: {i.Section}: {i.What}"));
                    _broadcaster.Send(ctx.Refresh);
                    tx.FinishedUtc = DateTimeOffset.UtcNow;
                    _store.Save(tx);
                    return tx;
                }
            }

            _broadcaster.Send(ctx.Refresh);
            _store.SaveWritten(tx.TxId, applied.Select(s => s.Snapshot(ctx)).ToList());

            foreach (var s in applied)
            {
                VerifyResult v;
                string? detail;
                try { v = s.Verify(ctx, out detail); }
                catch (Exception ex) { v = VerifyResult.AppliedUnverified; detail = ex.Message; }
                Result(tx, s.Key).Status = v;
                Result(tx, s.Key).Detail = detail;
            }
            tx.State = TxState.Applied;
            tx.FinishedUtc = DateTimeOffset.UtcNow;
            _store.Save(tx);
            return tx;
        }

        /// <summary>Checks an applied theme again, e.g. 10 s later, in case Windows reverted part of it.</summary>
        public TransactionRecord Reverify(LoadedBundle bundle, TransactionRecord tx)
        {
            var ctx = Context(bundle);
            foreach (var r in tx.Sections.Where(r => r.Decision == Decision.Apply && r.Status is VerifyResult.Verified or VerifyResult.Drifted))
            {
                var s = _sections.FirstOrDefault(x => x.Key == r.Key);
                if (s is null) continue;
                try { r.Status = s.Verify(ctx, out var d); r.Detail = d; }
                catch (Exception ex) { r.Status = VerifyResult.AppliedUnverified; r.Detail = ex.Message; }
            }
            _store.Save(tx);
            return tx;
        }

        /// <summary>
        /// Reverts a transaction from its saved snapshot. Values changed since the apply are skipped
        /// and reported unless <paramref name="force"/>; an interrupted transaction is always forced.
        /// </summary>
        public UndoReport Undo(LoadedBundle bundle, TransactionRecord tx, bool force = false)
        {
            var ctx = Context(bundle, tx.TxId);
            var before = _store.LoadBefore(tx.TxId) ?? throw new InvalidOperationException("This theme's undo snapshot is missing.");
            var written = _store.LoadWritten(tx.TxId);
            bool interrupted = tx.State is TxState.Snapshotted or TxState.Applying;
            var sections = _sections.Where(s => before.Any(b => b.Key == s.Key)).ToList();

            var report = new UndoReport { Transaction = tx };
            report.Issues.AddRange(RollbackAll(ctx, sections, before, written, force || interrupted));
            _broadcaster.Send(ctx.Refresh);

            tx.State = report.Complete ? TxState.Undone : TxState.UndonePartial;
            tx.Notes.Add($"Undone {DateTimeOffset.UtcNow:u}" + (report.Complete ? "" : $", {report.Issues.Count} item(s) left as they were"));
            _store.Save(tx);
            return report;
        }

        private static List<UndoIssue> RollbackAll(ApplyContext ctx, List<IThemeSection> sections,
            List<SectionState> before, List<SectionState>? written, bool force)
        {
            var issues = new List<UndoIssue>();
            foreach (var s in sections.OrderByDescending(s => s.Order))
            {
                var b = before.FirstOrDefault(x => x.Key == s.Key);
                if (b is null) continue;
                try { issues.AddRange(s.Rollback(ctx, b, written?.FirstOrDefault(x => x.Key == s.Key), force)); }
                catch (Exception ex) { issues.Add(new UndoIssue(s.Key, ex.Message, false)); }
            }
            return issues;
        }

        private static SectionResult Result(TransactionRecord tx, string key) =>
            tx.Sections.First(r => r.Key == key);
    }
}
