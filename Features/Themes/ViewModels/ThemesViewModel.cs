using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Revenant_Theme_Studio.Features.Licensing;
using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.Formats;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Features.Wallpaper.Services;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Features.Themes.ViewModels
{
    public sealed class ThemeCardViewModel
    {
        public required LibraryEntry Entry { get; init; }
        public string Name => Entry.Manifest.Name;
        public string Byline => $"{Entry.Manifest.Author} · v{Entry.Manifest.Version}";
        public string Summary => Entry.Summary;
        public string? Description => Entry.Manifest.Description;
        public string Origin => Entry.Manifest.Origin?.Kind switch
        {
            "windowsTheme" => $"Imported from {Entry.Manifest.Origin.Name}",
            "ddw" => $"Imported from {Entry.Manifest.Origin.Name} (WinDynamicDesktop)",
            "capture" => $"Captured {Entry.Manifest.Origin.ImportedUtc}",
            _ => "Revenant Theme Studio theme",
        };
        public ImageSource? Preview { get; init; }
    }

    /// <summary>
    /// The Themes tab: a library of themes, and apply / undo / import / export / capture. All
    /// engine work runs off the UI thread; the view supplies dialogs and the consent gate.
    /// </summary>
    public sealed class ThemesViewModel : INotifyPropertyChanged
    {
        private readonly ThemeLibrary _library = ThemeLibrary.Default();
        private readonly TransactionStore _store = TransactionStore.Default();
        private readonly ThemeApplyEngine _engine;
        private ThemeCardViewModel? _selected;
        private string _status = "Import a theme, or save your current look as one.";
        private bool _busy;
        private string _captureName = $"My look {DateTime.Now:yyyy-MM-dd}";

        public ThemesViewModel()
        {
            _engine = ThemeEngineFactory.Create(_store);
            Refresh();
        }

        public ObservableCollection<ThemeCardViewModel> Themes { get; } = new();
        public ObservableCollection<string> Report { get; } = new();

        public ThemeCardViewModel? Selected
        {
            get => _selected;
            set { _selected = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelection)); }
        }

        public bool HasSelection => _selected is not null;
        public string StatusMessage { get => _status; set { _status = value; OnPropertyChanged(); } }
        public bool IsBusy { get => _busy; private set { _busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsIdle)); } }
        public bool IsIdle => !_busy;
        public string CaptureName { get => _captureName; set { _captureName = value; OnPropertyChanged(); } }

        public string AppliedText => _store.LatestApplied() is { } tx
            ? $"Applied: {tx.BundleName} ({tx.StartedUtc.ToLocalTime():g})" : "No theme applied by Revenant Theme Studio";
        public bool CanUndo => _store.LatestApplied() is not null;

        public void Refresh()
        {
            string? keep = _selected?.Entry.Folder;
            Themes.Clear();
            foreach (var e in _library.List())
                Themes.Add(new ThemeCardViewModel { Entry = e, Preview = LoadPreview(e.PreviewPath) });
            Selected = Themes.FirstOrDefault(t => t.Entry.Folder == keep) ?? Themes.FirstOrDefault();
            OnPropertyChanged(nameof(AppliedText));
            OnPropertyChanged(nameof(CanUndo));
        }

        // ── import / capture / export / remove ──────────────────────────────

        public async Task ImportAsync(IEnumerable<string> paths)
        {
            if (!Allowed(Feature.ThemeImport, "Importing themes")) return;
            await Run("Importing…", () =>
            {
                var lines = new List<string>();
                foreach (var path in paths)
                {
                    try
                    {
                        var (entry, notes) = _library.Import(path);
                        lines.Add($"Imported \"{entry.Manifest.Name}\": {entry.Summary}");
                        lines.AddRange(notes.Select(n => "  " + n));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        lines.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    }
                }
                return lines;
            });
            Refresh();
        }

        public async Task CaptureAsync()
        {
            if (!Allowed(Feature.ThemeCapture, "Saving your look as a theme")) return;
            string name = string.IsNullOrWhiteSpace(CaptureName) ? $"My look {DateTime.Now:yyyy-MM-dd}" : CaptureName.Trim();
            await Run("Capturing your current look…", () =>
            {
                var result = ThemeCapture.Capture(name, new RegistryStore(), new WallpaperEngineService());
                var entry = _library.Add(result);
                return new[] { $"Saved \"{entry.Manifest.Name}\": {entry.Summary}" }.Concat(result.Notes.Select(n => "  " + n)).ToList();
            });
            Refresh();
        }

        public async Task ExportBundleAsync(string outPath)
        {
            if (_selected is null || !Allowed(Feature.ThemeExport, "Exporting themes")) return;
            var entry = _selected.Entry;
            await Run("Exporting…", () =>
            {
                File.Copy(entry.BundlePath, outPath, overwrite: true);
                return new List<string> { $"Exported \"{entry.Manifest.Name}\" to {outPath}" };
            });
        }

        public async Task ExportWindowsThemeAsync(string outDir)
        {
            if (_selected is null || !Allowed(Feature.ThemeExport, "Exporting themes")) return;
            var entry = _selected.Entry;
            await Run("Exporting…", () =>
            {
                string path = ThemeFileExporter.Export(_library.Load(entry), outDir);
                return new List<string>
                {
                    $"Exported \"{entry.Manifest.Name}\" as a Windows theme: {path}",
                    "  Windows themes carry wallpaper, colors and desktop icons; system icons stay in the .rtstheme.",
                };
            });
        }

        public void RemoveSelected()
        {
            if (_selected is null) return;
            _library.Remove(_selected.Entry);
            Report.Clear();
            Report.Add($"Removed \"{_selected.Name}\" from the library. If it's applied, it stays applied until you undo it.");
            Refresh();
        }

        // ── apply / undo ────────────────────────────────────────────────────

        /// <summary>What applying the selection would do, for the confirmation dialog. Null when nothing is selected.</summary>
        public Preflight? PlanSelected()
        {
            if (_selected is null) return null;
            try { return _engine.Plan(_library.Load(_selected.Entry)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                StatusMessage = ex.Message;
                return null;
            }
        }

        public async Task ApplySelectedAsync()
        {
            if (_selected is null || !Allowed(Feature.ThemeApply, "Applying themes")) return;
            var entry = _selected.Entry;
            LoadedBundle? bundle = null;
            TransactionRecord? tx = null;
            await Run($"Applying \"{entry.Manifest.Name}\"…", () =>
            {
                bundle = _library.Load(entry);
                tx = _engine.Apply(bundle, entry.BundlePath);
                return Describe(tx);
            });
            OnPropertyChanged(nameof(AppliedText));
            OnPropertyChanged(nameof(CanUndo));

            // Windows can revert part of a theme a few seconds later; check once more.
            if (tx is { State: TxState.Applied } && bundle is not null)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                timer.Tick += async (_, _) =>
                {
                    timer.Stop();
                    var again = await Task.Run(() => _engine.Reverify(bundle, tx));
                    var drifted = again.Sections.Where(s => s.Status == VerifyResult.Drifted).ToList();
                    if (drifted.Count > 0)
                    {
                        Report.Add($"10 seconds later, Windows had changed: {string.Join(", ", drifted.Select(d => Label(d.Key)))}. Apply the theme again to restore it.");
                        StatusMessage = "Windows reverted part of the theme.";
                    }
                };
                timer.Start();
            }
        }

        /// <summary>Undoes the latest theme. Returns the record when some values were changed since, so the view can offer to force.</summary>
        public async Task<TransactionRecord?> UndoLastAsync(TransactionRecord? force = null)
        {
            if (!Allowed(Feature.ThemeUndo, "Undoing themes")) return null;
            var tx = force ?? _store.LatestApplied();
            if (tx is null) { StatusMessage = "There's no theme to undo."; return null; }
            UndoReport? report = null;
            await Run($"Undoing \"{tx.BundleName}\"…", () =>
            {
                report = _engine.Undo(BundleFor(tx), tx, force: force is not null);
                var lines = new List<string> { report.Complete ? $"Undid \"{tx.BundleName}\". Everything is back as it was." : $"Undid \"{tx.BundleName}\", except:" };
                lines.AddRange(report.Issues.Select(i => $"  {Label(i.Section)}: {i.What}"));
                return lines;
            });
            OnPropertyChanged(nameof(AppliedText));
            OnPropertyChanged(nameof(CanUndo));
            return report is { Complete: false } r && r.Issues.Any(i => i.ChangedSinceApply) && force is null ? tx : null;
        }

        /// <summary>A transaction a crash left half applied, if any.</summary>
        public TransactionRecord? Interrupted() => _store.Interrupted();

        public async Task RecoverAsync(TransactionRecord tx)
        {
            await Run("Rolling back the interrupted theme…", () =>
            {
                var report = _engine.Undo(BundleFor(tx), tx, force: true);
                return new List<string> { report.Complete ? $"Rolled back \"{tx.BundleName}\"." : $"Rolled back \"{tx.BundleName}\", except:" }
                    .Concat(report.Issues.Select(i => $"  {Label(i.Section)}: {i.What}")).ToList();
            });
            OnPropertyChanged(nameof(AppliedText));
            OnPropertyChanged(nameof(CanUndo));
        }

        // ── helpers ─────────────────────────────────────────────────────────

        /// <summary>The bundle a transaction applied, or an empty stand-in: undo works from the saved snapshot alone.</summary>
        private LoadedBundle BundleFor(TransactionRecord tx)
        {
            try
            {
                if (tx.BundlePath is not null && File.Exists(tx.BundlePath))
                {
                    var entry = _library.List().FirstOrDefault(e => e.BundlePath == tx.BundlePath);
                    if (entry is not null) return _library.Load(entry);
                }
            }
            catch (Exception) { }
            return new LoadedBundle { Manifest = new ThemeBundle { Id = tx.BundleId, Name = tx.BundleName }, Files = new Dictionary<string, string>() };
        }

        private static List<string> Describe(TransactionRecord tx)
        {
            var lines = new List<string>
            {
                tx.State switch
                {
                    TxState.Applied => $"Applied \"{tx.BundleName}\".",
                    TxState.RolledBack => $"\"{tx.BundleName}\" was not applied. Everything was put back.",
                    TxState.RollbackPartial => $"\"{tx.BundleName}\" was not applied, and some changes could not be put back:",
                    _ => $"\"{tx.BundleName}\": nothing was changed.",
                },
            };
            foreach (var s in tx.Sections)
            {
                string what = s.Decision switch
                {
                    Decision.Skip => $"skipped ({s.Reason})",
                    Decision.Blocked => $"can't apply ({s.Reason})",
                    _ => s.Status switch
                    {
                        VerifyResult.Verified => "done" + (s.Detail is null ? "" : $"; {s.Detail}"),
                        VerifyResult.AppliedUnverified => $"done, not yet confirmed ({s.Detail})",
                        VerifyResult.Drifted => $"Windows changed it back ({s.Detail})",
                        VerifyResult.Failed => $"failed: {s.Detail}",
                        _ => "not applied",
                    },
                };
                lines.Add($"  {Label(s.Key)}: {what}");
            }
            lines.AddRange(tx.Notes.Select(n => "  " + n));
            return lines;
        }

        public static string Label(string key) => key switch
        {
            "wallpaper" => "Wallpaper", "colors" => "Colors", "icons" => "Desktop and drive icons",
            "systemIcons" => "System icons", "lockScreen" => "Lock screen", "visualStyle" => "Visual style",
            _ => char.ToUpperInvariant(key[0]) + key[1..],
        };

        private bool Allowed(Feature feature, string what)
        {
            if (FeatureGate.Allows(feature)) return true;
            StatusMessage = FeatureGate.LockedMessage(what);
            return false;
        }

        private async Task Run(string busyText, Func<IEnumerable<string>> work)
        {
            IsBusy = true;
            StatusMessage = busyText;
            Report.Clear();
            try
            {
                var lines = await Task.Run(() => work().ToList());
                foreach (var l in lines) Report.Add(l);
                StatusMessage = lines.FirstOrDefault()?.Trim() ?? "Done.";
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                Report.Add(ex.Message);
            }
            finally { IsBusy = false; }
        }

        private static ImageSource? LoadPreview(string? path)
        {
            if (path is null || !File.Exists(path)) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(path);
                img.DecodePixelWidth = 320;
                img.CacheOption = BitmapCacheOption.OnLoad;   // no file lock
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
