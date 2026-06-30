using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly FolderIconService _folderIconService = new();
        private readonly IconMatchingService _matchingService = new();
        private readonly ManagedStorageService _storageService = new();
        private readonly ChangeHistoryService _historyService;
        private readonly DriveIconService _driveIconService = new();
        private readonly ShellIconService _shellIconService = new();

        private string _selectedFolder = string.Empty;
        private string _statusMessage = "Ready.";
        private string _scanRoot = string.Empty;
        private AutoMatchResult? _selectedAutoMatchResult;
        private bool _isAutoRunning;
        private string _autoProgressText = string.Empty;
        private MatchStrictness _autoStrictness = MatchStrictness.Strict;
        private string _selectedDrive = "C:";
        private string _selectedSystemTarget = "Desktop";
        private string _advancedNote = "Direct system resource patching is advanced-only and intentionally not automated.";
        private CancellationTokenSource? _autoCts;

        // ── Per-tab icon pickers ─────────────────────────────────────────────
        // Three independent picker view-models; each tab binds its rail to
        // exactly one. No shared SelectedIcon means switching tabs never
        // silently changes what's about to be applied.
        public TabIconPickerViewModel FolderIconPicker { get; }
        public TabIconPickerViewModel DriveIconPicker { get; }
        public TabIconPickerViewModel SystemIconPicker { get; }
        public TabIconPickerViewModel AutoMatchIconPicker { get; }

        // Auto Match results — these are tab-local so we keep them on MVM
        public ObservableCollection<AutoMatchResult> AppliedAutomatically { get; } = new();
        public ObservableCollection<AutoMatchResult> OurBestGuess { get; } = new();
        public ObservableCollection<AutoMatchResult> UsedDefaultIcon { get; } = new();
        public ObservableCollection<AutoMatchResult> Skipped { get; } = new();

        // System (formerly "Shell") icon target list — read off ShellIconService
        public ObservableCollection<string> SystemTargets { get; }

        // ── License ──────────────────────────────────────────────────────────
        public bool IsProUnlocked => LicenseService.Instance.IsPro;
        public void NotifyLicenseActivated()
        {
            OnPropertyChanged(nameof(IsProUnlocked));
            StatusMessage = "RTS Pro activated. All features unlocked.";
        }

        public MainViewModel()
        {
            _historyService = new ChangeHistoryService(_storageService);
            _matchingService.SetIconFolders();

            FolderIconPicker = new TabIconPickerViewModel("Folder", requiresProForGunmetal: true)
            {
                OnStatusMessage = msg => StatusMessage = msg
            };
            DriveIconPicker = new TabIconPickerViewModel("Drive", requiresProForGunmetal: true)
            {
                OnStatusMessage = msg => StatusMessage = msg
            };
            SystemIconPicker = new TabIconPickerViewModel("System", requiresProForGunmetal: true)
            {
                OnStatusMessage = msg => StatusMessage = msg
            };
            AutoMatchIconPicker = new TabIconPickerViewModel("AutoMatch", requiresProForGunmetal: true)
            {
                OnStatusMessage = msg => StatusMessage = msg
            };
            AutoMatchIconPicker.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TabIconPickerViewModel.IconFolder))
                    _matchingService.SetIconFolders(AutoMatchIconPicker.IconFolder);
            };

            SystemTargets = new ObservableCollection<string>(_shellIconService.Targets.Keys.OrderBy(x => x));

            // Auto-load the bundled icon library when it ships alongside the EXE.
            string bundledEngine = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IconEngine");
            if (Directory.Exists(bundledEngine))
            {
                FolderIconPicker.IconFolder    = bundledEngine;
                DriveIconPicker.IconFolder     = bundledEngine;
                SystemIconPicker.IconFolder    = bundledEngine;
                AutoMatchIconPicker.IconFolder = bundledEngine;
                _matchingService.SetIconFolders(bundledEngine);
            }
        }

        // ── Properties ───────────────────────────────────────────────────────
        public string SelectedFolder
        {
            get => _selectedFolder;
            set { _selectedFolder = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentFolderIconReference)); }
        }
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }
        public string ScanRoot { get => _scanRoot; set { _scanRoot = value; OnPropertyChanged(); } }
        public AutoMatchResult? SelectedAutoMatchResult
        {
            get => _selectedAutoMatchResult;
            set { _selectedAutoMatchResult = value; OnPropertyChanged(); }
        }
        public bool IsAutoRunning { get => _isAutoRunning; set { _isAutoRunning = value; OnPropertyChanged(); } }
        public string AutoProgressText { get => _autoProgressText; set { _autoProgressText = value; OnPropertyChanged(); } }
        public MatchStrictness AutoStrictness { get => _autoStrictness; set { _autoStrictness = value; OnPropertyChanged(); } }
        public string SelectedDrive
        {
            get => _selectedDrive;
            set { _selectedDrive = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentDriveIconReference)); }
        }
        public string SelectedSystemTarget
        {
            get => _selectedSystemTarget;
            set { _selectedSystemTarget = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentSystemIconReference)); }
        }
        public string AdvancedNote { get => _advancedNote; set { _advancedNote = value; OnPropertyChanged(); } }

        public string CurrentFolderIconReference => string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder)
            ? "No icon assigned"
            : _folderIconService.GetCurrentIconReference(SelectedFolder) ?? "Default system icon";

        public string CurrentDriveIconReference => _driveIconService.GetCurrentIcon(SelectedDrive) ?? "Default system icon";
        public string CurrentSystemIconReference => _shellIconService.GetCurrentOverride(SelectedSystemTarget) ?? "Default system icon";

        // ── Apply: Folder ────────────────────────────────────────────────────
        public void ApplyFolderIcon()
        {
            var picked = FolderIconPicker.SelectedIcon;
            if (string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder) || picked == null)
            {
                StatusMessage = "Select a valid folder and an icon from the Folder Icons rail.";
                return;
            }

            try
            {
                var managed = ImportIfFile(picked);
                var previous = _folderIconService.GetCurrentIconReference(SelectedFolder);
                var newReference = $"\"{managed}\",{picked.ResourceIndex}";
                _folderIconService.ApplyIconReference(SelectedFolder, newReference);

                _historyService.Record(new ChangeRecord
                {
                    BackupId      = Guid.NewGuid().ToString("N"),
                    TargetType    = IconTargetType.Folder,
                    TargetPath    = SelectedFolder,
                    PreviousValue = previous,
                    NewValue      = newReference,
                    Timestamp     = DateTimeOffset.UtcNow
                });

                StatusMessage = "Folder icon applied safely.";
                OnPropertyChanged(nameof(CurrentFolderIconReference));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Apply failed: {ex.Message}";
            }
        }

        // ── Apply: Drive ─────────────────────────────────────────────────────
        public void ApplyDriveIcon()
        {
            var picked = DriveIconPicker.SelectedIcon;
            if (picked == null) { StatusMessage = "Select an icon from the Drive Icons rail."; return; }

            var managed  = ImportIfFile(picked);
            var iconRef  = $"\"{managed}\",{picked.ResourceIndex}";
            var previous = _driveIconService.GetCurrentIcon(SelectedDrive);
            _driveIconService.ApplyIcon(SelectedDrive, iconRef);
            _historyService.Record(new ChangeRecord
            {
                BackupId      = Guid.NewGuid().ToString("N"),
                TargetType    = IconTargetType.Drive,
                TargetPath    = SelectedDrive,
                PreviousValue = previous,
                NewValue      = iconRef,
                Timestamp     = DateTimeOffset.UtcNow
            });
            StatusMessage = $"Drive {SelectedDrive} icon updated.";
            OnPropertyChanged(nameof(CurrentDriveIconReference));
        }

        // ── Apply: System (formerly Shell) ───────────────────────────────────
        public void ApplySystemIcon()
        {
            if (!LicenseService.Instance.IsPro) { StatusMessage = "System icon overrides require RTS Pro."; return; }
            var picked = SystemIconPicker.SelectedIcon;
            if (picked == null) { StatusMessage = "Select an icon from the System Icons rail."; return; }

            var managed  = ImportIfFile(picked);
            var iconRef  = $"\"{managed}\",{picked.ResourceIndex}";
            var previous = _shellIconService.GetCurrentOverride(SelectedSystemTarget);
            _shellIconService.ApplyOverride(SelectedSystemTarget, iconRef);
            _historyService.Record(new ChangeRecord
            {
                BackupId      = Guid.NewGuid().ToString("N"),
                TargetType    = IconTargetType.Shell, // history schema unchanged for back-compat
                TargetPath    = SelectedSystemTarget,
                PreviousValue = previous,
                NewValue      = iconRef,
                Timestamp     = DateTimeOffset.UtcNow
            });
            StatusMessage = $"{SelectedSystemTarget} icon override set (HKCU).";
            OnPropertyChanged(nameof(CurrentSystemIconReference));
        }

        // ── Auto Match ───────────────────────────────────────────────────────
        // "Arise" = dry-run scan. Produces a plan (four buckets of results).
        // Nothing is written to the registry until the user clicks a per-box
        // "Arise" button, which commits just that box.
        public async Task AriseAsync()
        {
            if (!LicenseService.Instance.IsPro) { StatusMessage = "Auto Match requires RTS Pro."; return; }
            if (IsAutoRunning) return;
            if (string.IsNullOrWhiteSpace(ScanRoot) || !Directory.Exists(ScanRoot)) { StatusMessage = "Scan root does not exist."; return; }

            AppliedAutomatically.Clear();
            OurBestGuess.Clear();
            UsedDefaultIcon.Clear();
            Skipped.Clear();
            SelectedAutoMatchResult = null;

            IsAutoRunning = true;
            _autoCts = new CancellationTokenSource();
            var token   = _autoCts.Token;
            var scanned = 0;

            try
            {
                await Task.Run(() =>
                {
                    foreach (var dir in SafeEnumerateDirectories(ScanRoot, token))
                    {
                        token.ThrowIfCancellationRequested();
                        scanned++;
                        var folderName = Path.GetFileName(dir);
                        try
                        {
                            var decision = _matchingService.FindBestMatch(folderName, AutoStrictness);
                            var (curPath, curIdx) = AutoMatchResult.ParseIconReference(_folderIconService.GetCurrentIconReference(dir));
                            var curPreview = AutoMatchResult.LoadIcoPreview(curPath);
                            App.Current.Dispatcher.Invoke(() =>
                            {
                                if (decision.Strength == MatchStrength.Strong && decision.IconPath != null)
                                {
                                    AppliedAutomatically.Add(new AutoMatchResult
                                    {
                                        FolderName         = folderName,
                                        TargetPath         = dir,
                                        Reason             = decision.Reason ?? "Strong match.",
                                        CurrentIconPath    = curPath,
                                        CurrentIconIndex   = curIdx,
                                        CurrentIconPreview = curPreview,
                                        AssignedIconPath   = decision.IconPath
                                    });
                                }
                                else if (decision.Strength == MatchStrength.Weak && !string.IsNullOrWhiteSpace(decision.IconPath))
                                {
                                    OurBestGuess.Add(new AutoMatchResult
                                    {
                                        FolderName         = folderName,
                                        TargetPath         = dir,
                                        Reason             = decision.Reason ?? "Weak match — review.",
                                        CurrentIconPath    = curPath,
                                        CurrentIconIndex   = curIdx,
                                        CurrentIconPreview = curPreview,
                                        AssignedIconPath   = decision.IconPath
                                    });
                                }
                                else
                                {
                                    UsedDefaultIcon.Add(new AutoMatchResult
                                    {
                                        FolderName         = folderName,
                                        TargetPath         = dir,
                                        Reason             = decision.Reason ?? "No safe match.",
                                        CurrentIconPath    = curPath,
                                        CurrentIconIndex   = curIdx,
                                        CurrentIconPreview = curPreview
                                    });
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            App.Current.Dispatcher.Invoke(() => Skipped.Add(new AutoMatchResult
                            {
                                FolderName = folderName,
                                TargetPath = dir,
                                Reason     = ex.Message
                            }));
                        }
                        if (scanned % 25 == 0)
                            App.Current.Dispatcher.Invoke(() => AutoProgressText = $"Scanned {scanned} folders.");
                    }
                }, token);

                StatusMessage = $"Arise complete. Ready to bind: {AppliedAutomatically.Count} | review: {OurBestGuess.Count} | unbound: {UsedDefaultIcon.Count} | errors: {Skipped.Count}.";
            }
            catch (OperationCanceledException) { StatusMessage = "Arise canceled."; }
            finally { IsAutoRunning = false; _autoCts?.Dispose(); _autoCts = null; }
        }

        // Assign the rail's currently-selected icon to the currently-selected
        // result row. Moves the row out of "Errors"/"No Match" into Best Guess.
        public void BindSelected()
        {
            var row  = SelectedAutoMatchResult;
            var icon = AutoMatchIconPicker.SelectedIcon;
            if (row == null) { StatusMessage = "Select a folder row first."; return; }
            if (icon == null || string.IsNullOrWhiteSpace(icon.ResourcePath) || !File.Exists(icon.ResourcePath))
            {
                StatusMessage = "Select an icon from the library rail first.";
                return;
            }
            row.AssignedIconPath = icon.ResourcePath;

            // Move unbound rows (errors / no-match) into Best Guess now that
            // they have an icon assigned.
            if (UsedDefaultIcon.Contains(row)) { UsedDefaultIcon.Remove(row); OurBestGuess.Add(row); }
            else if (Skipped.Contains(row))    { Skipped.Remove(row);        OurBestGuess.Add(row); }

            StatusMessage = $"Bound {row.FolderName} → {row.AssignedIconName}.";
        }

        public void UnbindSelected()
        {
            var row = SelectedAutoMatchResult;
            if (row == null) { StatusMessage = "Select a folder row to unbind."; return; }
            row.AssignedIconPath = null;
            if (AppliedAutomatically.Contains(row)) { AppliedAutomatically.Remove(row); UsedDefaultIcon.Add(row); }
            else if (OurBestGuess.Contains(row))    { OurBestGuess.Remove(row);        UsedDefaultIcon.Add(row); }
            StatusMessage = $"Unbound {row.FolderName}.";
        }

        // "Arise" a single box: commit all bound rows in that box to the
        // registry. Unbound rows are skipped. This is the only path that
        // writes — the scan itself is dry-run.
        public void AriseBox(ObservableCollection<AutoMatchResult> box, string boxName)
        {
            if (!LicenseService.Instance.IsPro) { StatusMessage = "Arise requires RTS Pro."; return; }
            var applied = 0;
            var skipped = 0;
            var toRemove = new List<AutoMatchResult>();
            foreach (var row in box.ToList())
            {
                if (string.IsNullOrEmpty(row.AssignedIconPath) || !File.Exists(row.AssignedIconPath))
                { skipped++; continue; }
                try
                {
                    var managed   = _storageService.ImportIcon(row.AssignedIconPath);
                    var previous  = _folderIconService.GetCurrentIconReference(row.TargetPath);
                    var reference = $"\"{managed}\",0";
                    _folderIconService.ApplyIconReference(row.TargetPath, reference);
                    _historyService.Record(new ChangeRecord
                    {
                        BackupId      = Guid.NewGuid().ToString("N"),
                        TargetType    = IconTargetType.Folder,
                        TargetPath    = row.TargetPath,
                        PreviousValue = previous,
                        NewValue      = reference,
                        Timestamp     = DateTimeOffset.UtcNow
                    });
                    applied++;
                    toRemove.Add(row);
                }
                catch (Exception ex)
                {
                    row.Reason = $"Arise failed: {ex.Message}";
                    skipped++;
                }
            }
            foreach (var r in toRemove) box.Remove(r);
            StatusMessage = $"{boxName}: arose {applied}, skipped {skipped}.";
        }

        public void UndoLastChange()
        {
            if (!LicenseService.Instance.IsPro) { StatusMessage = "Undo requires RTS Pro."; return; }
            var last = _historyService.GetLast();
            if (last == null) { StatusMessage = "No changes available to undo."; return; }

            switch (last.TargetType)
            {
                case IconTargetType.Folder:
                    if (string.IsNullOrWhiteSpace(last.PreviousValue)) _folderIconService.RemoveIcon(last.TargetPath);
                    else _folderIconService.ApplyIconReference(last.TargetPath, last.PreviousValue);
                    break;
                case IconTargetType.Drive:
                    if (string.IsNullOrWhiteSpace(last.PreviousValue)) _driveIconService.RestoreIcon(last.TargetPath);
                    else _driveIconService.ApplyIcon(last.TargetPath, last.PreviousValue);
                    break;
                case IconTargetType.Shell:
                    if (string.IsNullOrWhiteSpace(last.PreviousValue)) _shellIconService.RestoreDefault(last.TargetPath);
                    else _shellIconService.ApplyOverride(last.TargetPath, last.PreviousValue);
                    break;
            }

            _historyService.RemoveLast();
            StatusMessage = $"Undid last change: {last.TargetType} {last.TargetPath}";
            OnPropertyChanged(nameof(CurrentFolderIconReference));
            OnPropertyChanged(nameof(CurrentDriveIconReference));
            OnPropertyChanged(nameof(CurrentSystemIconReference));
        }

        public void RestoreSelectedSystemDefault()
        {
            _shellIconService.RestoreDefault(SelectedSystemTarget);
            StatusMessage = $"Removed per-user override for {SelectedSystemTarget}.";
            OnPropertyChanged(nameof(CurrentSystemIconReference));
        }

        public void CancelAutoMatch() => _autoCts?.Cancel();

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Copies the chosen .ico into managed storage so the registry path
        /// stays valid even if the user moves their library folder later.
        /// Non-.ico sources (e.g. system resources via index) pass through.
        /// </summary>
        private string ImportIfFile(IconChoice picked)
        {
            var source = picked.ResourcePath;
            return File.Exists(source) && source.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? _storageService.ImportIcon(source)
                : source;
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string root, CancellationToken token)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = stack.Pop();
                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(current); }
                catch { continue; }

                foreach (var dir in children)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        // Skip VCS internals and other hidden system dirs to avoid corrupting them.
                        if (info.Name.StartsWith('.')) continue;
                    }
                    catch { continue; }

                    yield return dir;
                    stack.Push(dir);
                }
            }
        }

        // ── App-close archive ─────────────────────────────────────────────────
        public void ArchiveIconsToProgramData()
        {
            try
            {
                var archiveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Revenant Theme Studio", "Icons");
                Directory.CreateDirectory(archiveDir);

                var records = _historyService.GetAll()
                    .Where(r => r.TargetType == IconTargetType.Drive ||
                                r.TargetType == IconTargetType.Shell)
                    .ToList();

                foreach (var record in records)
                {
                    var iconPath = ParseIconPath(record.NewValue);
                    if (iconPath == null || !File.Exists(iconPath)) continue;
                    var dest = Path.Combine(archiveDir, Path.GetFileName(iconPath));
                    if (!File.Exists(dest))
                        File.Copy(iconPath, dest, overwrite: false);
                }
            }
            catch { /* non-fatal on close */ }
        }

        private static string? ParseIconPath(string? iconRef)
        {
            if (string.IsNullOrWhiteSpace(iconRef)) return null;
            var s = iconRef.Trim();
            if (s.StartsWith('"'))
            {
                var closing = s.IndexOf('"', 1);
                return closing > 1 ? s[1..closing] : null;
            }
            var lastComma = s.LastIndexOf(',');
            return lastComma > 0 ? s[..lastComma] : s;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
