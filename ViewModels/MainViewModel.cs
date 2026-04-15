using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private const string FallbackFolderIconCacheName = "fallback_folder.ico";

        private readonly FolderIconService _folderIconService = new();
        private readonly IconMatchingService _matchingService = new();
        private readonly SystemIconResourceService _systemIconService = new();
        private readonly ManagedStorageService _storageService = new();
        private readonly ChangeHistoryService _historyService;
        private readonly DriveIconService _driveIconService = new();
        private readonly ShellIconService _shellIconService = new();
        private readonly AppSettingsService _appSettings;

        private string _iconFolder = string.Empty;
        private string _selectedFolder = string.Empty;
        private IconChoice? _selectedIcon;
        private string _statusMessage = "Ready.";
        private string _systemResourcePath = string.Empty;
        private bool _includeSystemIcons;
        private string _scanRoot = string.Empty;
        private bool _isAutoRunning;
        private string _autoProgressText = string.Empty;
        private MatchStrictness _autoStrictness = MatchStrictness.Strict;
        private string _selectedDrive = "C:";
        private string _selectedShellTarget = "Desktop";
        private string _advancedNote = "Direct system resource patching is advanced-only and intentionally not automated.";
        private CancellationTokenSource? _autoCts;

        public ObservableCollection<IconChoice> IconList { get; } = new();
        public ObservableCollection<AutoMatchResult> AppliedAutomatically { get; } = new();
        public ObservableCollection<AutoMatchResult> OurBestGuess { get; } = new();
        public ObservableCollection<AutoMatchResult> UsedDefaultIcon { get; } = new();
        public ObservableCollection<AutoMatchResult> Skipped { get; } = new();
        public ObservableCollection<string> ShellTargets { get; }

        public MainViewModel()
        {
            _historyService = new ChangeHistoryService(_storageService);
            _appSettings = new AppSettingsService(_storageService.RootPath);
            _matchingService.SetIconFolders();
            SystemResourcePath = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\SystemResources\imageres.dll.mun");
            ShellTargets = new ObservableCollection<string>(_shellIconService.Targets.Keys.OrderBy(x => x));
            ReloadIcons();
        }

        public string IconFolder { get => _iconFolder; set { _iconFolder = value; OnPropertyChanged(); _matchingService.SetIconFolders(_iconFolder); ReloadIcons(); } }
        public string SystemResourcePath { get => _systemResourcePath; set { _systemResourcePath = value; OnPropertyChanged(); } }
        public bool IncludeSystemIcons { get => _includeSystemIcons; set { _includeSystemIcons = value; OnPropertyChanged(); ReloadIcons(); } }
        public string SelectedFolder { get => _selectedFolder; set { _selectedFolder = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentFolderIconReference)); } }
        public IconChoice? SelectedIcon { get => _selectedIcon; set { _selectedIcon = value; OnPropertyChanged(); } }
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }
        public string ScanRoot { get => _scanRoot; set { _scanRoot = value; OnPropertyChanged(); } }
        public bool IsAutoRunning { get => _isAutoRunning; set { _isAutoRunning = value; OnPropertyChanged(); } }
        public string AutoProgressText { get => _autoProgressText; set { _autoProgressText = value; OnPropertyChanged(); } }
        public MatchStrictness AutoStrictness { get => _autoStrictness; set { _autoStrictness = value; OnPropertyChanged(); } }
        public string SelectedDrive { get => _selectedDrive; set { _selectedDrive = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentDriveIconReference)); } }
        public string SelectedShellTarget { get => _selectedShellTarget; set { _selectedShellTarget = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentShellIconReference)); } }
        public string AdvancedNote { get => _advancedNote; set { _advancedNote = value; OnPropertyChanged(); } }

        public string CurrentFolderIconReference => string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder)
            ? "No icon assigned"
            : _folderIconService.GetCurrentIconReference(SelectedFolder) ?? "Default system icon";

        public string CurrentDriveIconReference => _driveIconService.GetCurrentIcon(SelectedDrive) ?? "Default system icon";
        public string CurrentShellIconReference => _shellIconService.GetCurrentOverride(SelectedShellTarget) ?? "Default system icon";

        public void ReloadIcons()
        {
            IconList.Clear();
            var loadedFromFolders = 0;
            var loadedFromSystemResource = 0;

            if (!string.IsNullOrWhiteSpace(_iconFolder) && Directory.Exists(_iconFolder))
            {
                foreach (var iconPath in _matchingService.GetAllIcons())
                {
                    if (!TryCreateFileIconChoice(iconPath, out var iconChoice)) continue;
                    IconList.Add(iconChoice);
                    loadedFromFolders++;
                }
            }

            if (IncludeSystemIcons && !string.IsNullOrWhiteSpace(SystemResourcePath) && File.Exists(SystemResourcePath))
            {
                foreach (var iconChoice in _systemIconService.LoadIcons(SystemResourcePath))
                {
                    IconList.Add(iconChoice);
                    loadedFromSystemResource++;
                }
            }

            StatusMessage = loadedFromFolders == 0 && loadedFromSystemResource == 0
                ? "No icons were loaded. Select an icon folder and/or system resource path."
                : $"Loaded {loadedFromFolders} file icons and {loadedFromSystemResource} system icons.";
        }

        public void ApplyFolderIcon()
        {
            if (string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder) || SelectedIcon == null)
            {
                StatusMessage = "Select a valid folder and icon.";
                return;
            }

            try
            {
                var source = SelectedIcon.ResourcePath;
                var managed = File.Exists(source) && source.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                    ? _storageService.ImportIcon(source)
                    : source;

                var previous = _folderIconService.GetCurrentIconReference(SelectedFolder);
                var newReference = $"\"{managed}\",{SelectedIcon.ResourceIndex}";
                _folderIconService.ApplyIconReference(SelectedFolder, newReference);

                _historyService.Record(new ChangeRecord
                {
                    BackupId = Guid.NewGuid().ToString("N"),
                    TargetType = IconTargetType.Folder,
                    TargetPath = SelectedFolder,
                    PreviousValue = previous,
                    NewValue = newReference,
                    Timestamp = DateTimeOffset.UtcNow
                });

                StatusMessage = "Folder icon applied safely.";
                OnPropertyChanged(nameof(CurrentFolderIconReference));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Apply failed: {ex.Message}";
            }
        }

        public void ApplyDriveIcon()
        {
            if (SelectedIcon == null) { StatusMessage = "Select an icon first."; return; }
            var source = SelectedIcon.ResourcePath;
            var managed = File.Exists(source) && source.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? _storageService.ImportIcon(source)
                : source;
            var iconReference = $"\"{managed}\",{SelectedIcon.ResourceIndex}";
            var previous = _driveIconService.GetCurrentIcon(SelectedDrive);
            _driveIconService.ApplyIcon(SelectedDrive, iconReference);
            _historyService.Record(new ChangeRecord { BackupId = Guid.NewGuid().ToString("N"), TargetType = IconTargetType.Drive, TargetPath = SelectedDrive, PreviousValue = previous, NewValue = iconReference, Timestamp = DateTimeOffset.UtcNow });
            StatusMessage = $"Drive {SelectedDrive} icon updated.";
            OnPropertyChanged(nameof(CurrentDriveIconReference));
        }

        public void ApplyShellIcon()
        {
            if (SelectedIcon == null) { StatusMessage = "Select an icon first."; return; }
            var source = SelectedIcon.ResourcePath;
            var managed = File.Exists(source) && source.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? _storageService.ImportIcon(source)
                : source;
            var iconReference = $"\"{managed}\",{SelectedIcon.ResourceIndex}";
            var previous = _shellIconService.GetCurrentOverride(SelectedShellTarget);
            _shellIconService.ApplyOverride(SelectedShellTarget, iconReference);
            _historyService.Record(new ChangeRecord { BackupId = Guid.NewGuid().ToString("N"), TargetType = IconTargetType.Shell, TargetPath = SelectedShellTarget, PreviousValue = previous, NewValue = iconReference, Timestamp = DateTimeOffset.UtcNow });
            StatusMessage = $"{SelectedShellTarget} icon override set (HKCU).";
            OnPropertyChanged(nameof(CurrentShellIconReference));
        }

        public async Task RunAutoMatchAsync()
        {
            if (IsAutoRunning) return;
            if (string.IsNullOrWhiteSpace(ScanRoot) || !Directory.Exists(ScanRoot)) { StatusMessage = "Scan root does not exist."; return; }

            AppliedAutomatically.Clear();
            OurBestGuess.Clear();
            UsedDefaultIcon.Clear();
            Skipped.Clear();

            // Resolve fallback icon on the UI thread before the background loop.
            // Priority: (1) user-configured default, (2) extracted imageres folder icon, (3) null = skip.
            var sessionFallbackPath = ResolveSessionFallbackIcon();

            IsAutoRunning = true;
            _autoCts = new CancellationTokenSource();
            var token = _autoCts.Token;

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
                            if (decision.Strength == MatchStrength.Strong && decision.IconPath != null)
                            {
                                var managed = _storageService.ImportIcon(decision.IconPath);
                                var previous = _folderIconService.GetCurrentIconReference(dir);
                                var reference = $"\"{managed}\",0";
                                _folderIconService.ApplyIconReference(dir, reference);
                                _historyService.Record(new ChangeRecord { BackupId = Guid.NewGuid().ToString("N"), TargetType = IconTargetType.Folder, TargetPath = dir, PreviousValue = previous, NewValue = reference, Timestamp = DateTimeOffset.UtcNow });
                                App.Current.Dispatcher.Invoke(() => AppliedAutomatically.Add(new AutoMatchResult { FolderName = folderName, TargetPath = dir, SuggestedIcon = Path.GetFileName(decision.IconPath), Reason = decision.Reason ?? "Applied." }));
                            }
                            else
                            {
                                if (sessionFallbackPath != null)
                                {
                                    // Apply the user's fallback (or the extracted imageres folder icon).
                                    var previous = _folderIconService.GetCurrentIconReference(dir);
                                    var reference = $"\"{sessionFallbackPath}\",0";
                                    _folderIconService.ApplyIconReference(dir, reference);
                                    _historyService.Record(new ChangeRecord { BackupId = Guid.NewGuid().ToString("N"), TargetType = IconTargetType.Folder, TargetPath = dir, PreviousValue = previous, NewValue = reference, Timestamp = DateTimeOffset.UtcNow });
                                    App.Current.Dispatcher.Invoke(() => UsedDefaultIcon.Add(new AutoMatchResult { FolderName = folderName, TargetPath = dir, SuggestedIcon = null, Reason = decision.Reason ?? "No safe semantic match; applied fallback icon." }));
                                }
                                else
                                {
                                    // No fallback available — leave the folder untouched.
                                    App.Current.Dispatcher.Invoke(() => Skipped.Add(new AutoMatchResult { FolderName = folderName, TargetPath = dir, Reason = decision.Reason ?? "No match and no fallback icon configured." }));
                                }

                                if (!string.IsNullOrWhiteSpace(decision.IconPath))
                                {
                                    App.Current.Dispatcher.Invoke(() => OurBestGuess.Add(new AutoMatchResult { FolderName = folderName, TargetPath = dir, SuggestedIcon = Path.GetFileName(decision.IconPath), Reason = decision.Reason ?? "Rejected as unsafe." }));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            App.Current.Dispatcher.Invoke(() => Skipped.Add(new AutoMatchResult { FolderName = folderName, TargetPath = dir, Reason = ex.Message }));
                        }

                        if (scanned % 25 == 0)
                        {
                            App.Current.Dispatcher.Invoke(() => AutoProgressText = $"Scanned {scanned} folders.");
                        }
                    }
                }, token);

                StatusMessage = $"Auto Match finished. Applied {AppliedAutomatically.Count}, defaulted {UsedDefaultIcon.Count}, best guesses {OurBestGuess.Count}, skipped {Skipped.Count}.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Auto Match canceled.";
            }
            finally
            {
                IsAutoRunning = false;
                _autoCts?.Dispose();
                _autoCts = null;
            }
        }

        public void UndoLastChange()
        {
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
            OnPropertyChanged(nameof(CurrentShellIconReference));
        }

        public void RestoreSelectedShellDefault()
        {
            _shellIconService.RestoreDefault(SelectedShellTarget);
            StatusMessage = $"Removed per-user override for {SelectedShellTarget}.";
            OnPropertyChanged(nameof(CurrentShellIconReference));
        }

        public void CancelAutoMatch() => _autoCts?.Cancel();

        /// <summary>
        /// Resolves the icon path to use when Auto Match finds no strong match for a folder.
        /// Called once on the UI thread before the background scan loop.
        /// Priority:
        ///   1. User-configured default (AppSettingsService.DefaultFallbackIconPath) — if set and file exists.
        ///   2. Bundled gm_default.ico from the gunmetal-theme directory deployed alongside the app.
        ///   3. Folder icon extracted from imageres.dll.mun and cached — only if the bundled icon is missing.
        ///   4. null — caller must skip the folder without writing anything.
        /// </summary>
        private string? ResolveSessionFallbackIcon()
        {
            // 1. User-configured default
            var userDefault = _appSettings.DefaultFallbackIconPath;
            if (!string.IsNullOrWhiteSpace(userDefault) && File.Exists(userDefault))
                return userDefault;

            // 2. Bundled gunmetal-theme default folder icon (deployed via CopyToOutputDirectory)
            var bundledPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "IconEngine", "gunmetal-theme", "gm_default.ico");
            if (File.Exists(bundledPath))
                return bundledPath;

            // 3. Cached extraction from imageres.dll.mun (safety net if bundled icon is absent)
            var cachedPath = Path.Combine(_storageService.CachePath, FallbackFolderIconCacheName);
            if (File.Exists(cachedPath))
                return cachedPath;

            var munPath = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\SystemResources\imageres.dll.mun");
            foreach (var index in new[] { 3, 4 })
            {
                if (_systemIconService.TryExtractIconToFile(munPath, index, cachedPath))
                    return cachedPath;
            }

            // Nothing worked — caller should skip unmatched folders.
            return null;
        }

        private static bool TryCreateFileIconChoice(string iconPath, out IconChoice iconChoice)
        {
            iconChoice = null!;
            try
            {
                var preview = new BitmapImage();
                preview.BeginInit();
                preview.UriSource = new Uri(iconPath, UriKind.Absolute);
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.EndInit();
                preview.Freeze();

                iconChoice = new IconChoice { ResourcePath = iconPath, ResourceIndex = 0, DisplayName = Path.GetFileNameWithoutExtension(iconPath), PreviewImage = preview };
                return true;
            }
            catch
            {
                return false;
            }
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
                    }
                    catch { continue; }

                    yield return dir;
                    stack.Push(dir);
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
